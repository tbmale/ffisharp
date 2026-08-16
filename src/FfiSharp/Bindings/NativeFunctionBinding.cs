using System;
using System.Collections.Generic;
using FfiSharp.Abi;
using FfiSharp.Backend;

namespace FfiSharp.Bindings
{
    /// <summary>
    /// A bound native function: name, resolved address, signature, and a lazily
    /// prepared <see cref="FfiCallPlan"/>. Binding is lazy — the symbol and call
    /// plan are resolved/prepared on first use and cached for subsequent calls.
    /// </summary>
    public sealed class NativeFunctionBinding : IDisposable
    {
        private readonly LibFfiBackend _backend;
        private readonly Func<FfiFunctionType, Delegate, FfiCallback> _callbackFactory;
        private readonly Action _throwPending;
        private readonly OperationLifetime _lifetime = new OperationLifetime();
        private readonly object _sync = new object();
        private FfiCallPlan _plan;
        private bool _disposed;

        internal NativeFunctionBinding(
            LibFfiBackend backend,
            string name,
            IntPtr address,
            FfiType returnType,
            IReadOnlyList<FfiType> argumentTypes,
            FfiCallingConvention callingConvention,
            Func<FfiFunctionType, Delegate, FfiCallback> callbackFactory,
            Action throwPending)
        {
            _backend = backend;
            _callbackFactory = callbackFactory;
            _throwPending = throwPending;
            Name = name;
            Address = address;
            ReturnType = returnType;
            ArgumentTypes = argumentTypes;
            CallingConvention = callingConvention;
        }

        public string Name { get; }
        public IntPtr Address { get; }
        public FfiType ReturnType { get; }
        public IReadOnlyList<FfiType> ArgumentTypes { get; }
        public FfiCallingConvention CallingConvention { get; }

        public object Invoke(object[] arguments)
        {
            // Acquire a lease so a concurrent Dispose() cannot free the cached call
            // plan (or otherwise tear the binding down) while this invocation is in
            // flight. Dispose() blocks until this lease is released.
            if (!_lifetime.TryEnter())
                throw new ObjectDisposedException(nameof(NativeFunctionBinding));

            try
            {
                _throwPending?.Invoke();
                FfiCallPlan plan = EnsurePlan();

                // Convert managed delegates passed for function-pointer parameters into
                // libffi closure pointers. The closures are retained by the library's
                // callback registry so they remain alive for native calls.
                object[] prepared = arguments;
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (ArgumentTypes[i] is FfiFunctionType ft && arguments[i] is Delegate del)
                    {
                        if (prepared == arguments)
                            prepared = (object[])arguments.Clone();
                        FfiCallback cb = _callbackFactory(ft, del);
                        prepared[i] = cb.FunctionPointer;
                    }
                }

                return _backend.Invoke(plan, Address, prepared);
            }
            finally
            {
                _lifetime.Exit();
            }
        }

        private FfiCallPlan EnsurePlan()
        {
            lock (_sync)
            {
                if (_plan == null)
                    _plan = _backend.CreateCallPlan(CallingConvention, ReturnType, ArgumentTypes);
                return _plan;
            }
        }

        public void Dispose()
        {
            // Reject new invocations and wait for any in-flight one to finish; only
            // then free the call plan (whose native cif/ffi_type** the in-flight
            // ffi_call may still be reading).
            _lifetime.Close();

            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_plan != null) { _plan.Dispose(); _plan = null; }
            }
        }
    }
}
