using System;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private readonly GCHandle _gch;

        private FfiCallPlan _plan;
        private IntPtr _writable;
        private Exception _lastException;
        private bool _pendingRethrow;
        private bool _disposed;
        private readonly object _sync = new object();

        internal FfiCallback(LibFfiBackend backend, FfiFunctionType signature, Delegate callback, CallbackExceptionPolicy policy, FfiMarshaller marshaller)
        {
            _backend = backend;
            _signature = signature;
            _delegate = callback;
            _policy = policy;
            _marshaller = marshaller;
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
            GCHandle gch = GCHandle.FromIntPtr(userData);
            FfiCallback cb = (FfiCallback)gch.Target;
            try
            {
                cb.InvokeFromNative(resp, args);
            }
            catch (Exception ex)
            {
                cb.CaptureException(Unwrap(ex));
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
            lock (_sync)
            {
                _lastException = ex;
                if (_policy == CallbackExceptionPolicy.RethrowOnManagedBoundary)
                    _pendingRethrow = true;
            }
        }

        /// <summary>Rethrows a stored callback exception (RethrowOnManagedBoundary policy).</summary>
        internal void ThrowPendingIfAny()
        {
            Exception toThrow = null;
            lock (_sync)
            {
                if (_pendingRethrow)
                {
                    _pendingRethrow = false;
                    toThrow = _lastException;
                }
            }
            if (toThrow != null)
                throw new FfiException("A callback threw an exception", toThrow);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;

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
}
