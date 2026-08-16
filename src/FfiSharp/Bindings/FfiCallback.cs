using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Marshaling;

namespace FfiSharp.Bindings
{
    /// <summary>
    /// A live libffi closure: a native callable function pointer that dispatches to
    /// a managed <see cref="Delegate"/>. It retains a strong reference to the
    /// delegate and a <see cref="GCHandle"/> to itself for as long as the closure is
    /// alive, so the delegate can never become GC-eligible while native code may
    /// call it. Dispose releases the closure; the native library must not invoke the
    /// callback after that point.
    /// </summary>
    internal sealed class FfiCallback : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ClosureThunk(IntPtr cif, IntPtr resp, IntPtr args, IntPtr userData);

        // A single static thunk dispatches every closure; the FfiCallback instance
        // is recovered from user_data, so no per-closure delegate is required.
        private static readonly ClosureThunk SharedThunk = ThunkImpl;
        internal static readonly IntPtr ThunkPointer = Marshal.GetFunctionPointerForDelegate(SharedThunk);

        private readonly LibFfiBackend _backend;
        private readonly FfiFunctionType _signature;
        private readonly Delegate _delegate;
        private readonly CallbackExceptionPolicy _policy;
        private readonly FfiMarshaller _marshaller;
        private readonly CallbackPendingFlag _pendingFlag;
        private readonly GCHandle _gch;

        private FfiCallPlan _plan;
        private IntPtr _writable;
        private Exception _lastException;
        private bool _pendingRethrow;
        private int _activeCallbacks;
        private bool _closing;
        private bool _disposed;
        private readonly object _sync = new object();

        internal FfiCallback(LibFfiBackend backend, FfiFunctionType signature, Delegate callback, CallbackExceptionPolicy policy, FfiMarshaller marshaller, CallbackPendingFlag pendingFlag)
        {
            _backend = backend;
            _signature = signature;
            _delegate = callback;
            _policy = policy;
            _marshaller = marshaller;
            _pendingFlag = pendingFlag;
            _gch = GCHandle.Alloc(this);
        }

        /// <summary>The GCHandle value passed to libffi as user_data.</summary>
        internal IntPtr UserData => GCHandle.ToIntPtr(_gch);

        /// <summary>The native function pointer that can be passed to C code.</summary>
        public IntPtr FunctionPointer { get; private set; }

        /// <summary>The last exception thrown by the managed callback (policy Store/Rethrow).</summary>
        public Exception LastException
        {
            get { lock (_sync) return _lastException; }
        }

        internal void Attach(FfiCallPlan plan, IntPtr writable, IntPtr code)
        {
            _plan = plan;
            _writable = writable;
            FunctionPointer = code;
        }

        private static void ThunkImpl(IntPtr cif, IntPtr resp, IntPtr args, IntPtr userData)
        {
            FfiCallback cb;
            try
            {
                cb = GCHandle.FromIntPtr(userData).Target as FfiCallback;
            }
            catch
            {
                // GCHandle already freed (native caller violated the ownership rule:
                // it must not call the callback after Dispose). Drop the call safely.
                return;
            }

            if (cb == null)
                return;

            // Lease the callback so a concurrent Dispose() cannot free the closure
            // or GCHandle underneath us mid-invocation. Once closing has begun the
            // managed delegate is no longer invoked.
            if (!cb.TryEnterCallback())
                return;

            try
            {
                cb.InvokeFromNative(resp, args);
            }
            catch (Exception ex)
            {
                cb.CaptureException(Unwrap(ex));
            }
            finally
            {
                cb.ExitCallback();
            }
        }

        private bool TryEnterCallback()
        {
            lock (_sync)
            {
                if (_closing) return false;
                _activeCallbacks++;
                CallbackContext.Enter();
                return true;
            }
        }

        private void ExitCallback()
        {
            lock (_sync)
            {
                _activeCallbacks--;
                CallbackContext.Exit();
                if (_activeCallbacks == 0)
                    Monitor.PulseAll(_sync);
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            // DynamicInvoke wraps callback exceptions in TargetInvocationException.
            while (ex is TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;
            return ex;
        }

        internal void InvokeFromNative(IntPtr resp, IntPtr args)
        {
            int n = _signature.ParameterTypes.Count;
            object[] arguments = new object[n];
            for (int i = 0; i < n; i++)
            {
                IntPtr argPtr = Marshal.ReadIntPtr(args, i * IntPtr.Size);
                arguments[i] = _marshaller.MarshalReturn(_signature.ParameterTypes[i], argPtr);
            }

            object result = _delegate.DynamicInvoke(arguments);
            _marshaller.WriteToStorage(resp, _signature.ReturnType, result);
        }

        internal void CaptureException(Exception ex)
        {
            if (_policy == CallbackExceptionPolicy.Ignore) return;
            bool rethrow = false;
            lock (_sync)
            {
                _lastException = ex;
                if (_policy == CallbackExceptionPolicy.RethrowOnManagedBoundary)
                {
                    _pendingRethrow = true;
                    rethrow = true;
                }
            }
            // Set the shared "might be pending" flag AFTER recording the per-callback
            // state, so a drain that observes the flag is guaranteed to see the state.
            if (rethrow)
                _pendingFlag?.Set();
        }

        /// <summary>
        /// Atomically takes (and clears) a pending exception if one is recorded,
        /// without throwing. Used by the drain loop so it can preserve the existing
        /// "one pending exception per call" semantics while retrying on races.
        /// </summary>
        internal bool TryTakePending(out Exception exception)
        {
            lock (_sync)
            {
                if (_pendingRethrow)
                {
                    _pendingRethrow = false;
                    exception = _lastException;
                    return true;
                }
            }
            exception = null;
            return false;
        }

        /// <summary>Rethrows a stored callback exception (RethrowOnManagedBoundary policy).</summary>
        internal void ThrowPendingIfAny()
        {
            if (TryTakePending(out Exception toThrow))
                throw new FfiException("A callback threw an exception", toThrow);
        }

        public void Dispose() => DisposeNow();

        /// <summary>
        /// Releases the closure's native resources. Returns <c>true</c> if they were
        /// released immediately; returns <c>false</c> (deferring) if called
        /// reentrantly from within the callback itself, because the libffi trampoline
        /// cannot be freed while it is executing. A later non-reentrant
        /// <see cref="Dispose"/> completes the release.
        /// </summary>
        internal bool DisposeNow()
        {
            bool freeNow;
            lock (_sync)
            {
                if (_disposed) return true;
                _closing = true;

                if (CallbackContext.Depth > 0)
                {
                    // Reentrant: this thread is inside the callback. Mark closing so
                    // new callback entries are rejected, but defer the free.
                    freeNow = false;
                }
                else
                {
                    _disposed = true;
                    // Wait for any callback already entered into the thunk to finish.
                    while (_activeCallbacks > 0)
                        Monitor.Wait(_sync);
                    freeNow = true;
                }
            }

            if (freeNow)
                FreeResources();
            return freeNow;
        }

        private void FreeResources()
        {
            // Only after all active callbacks have drained do we release native
            // resources. NOTE: the native caller must have already stopped invoking
            // this callback; a call arriving after Dispose violates the ownership
            // contract and is dropped by the thunk (see ThunkImpl).
            if (_writable != IntPtr.Zero)
            {
                _backend.FreeClosure(_writable);
                _writable = IntPtr.Zero;
            }
            if (_plan != null) { _plan.Dispose(); _plan = null; }
            if (_gch.IsAllocated) _gch.Free();
        }
    }
}
