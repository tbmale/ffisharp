using System;
using FfiSharp.Bindings;

namespace FfiSharp
{
    /// <summary>
    /// A handle to a registered callback. Disposing it frees the libffi closure; the
    /// native library must not invoke the callback after disposal.
    /// </summary>
    public sealed class CallbackHandle : IDisposable
    {
        private readonly CallbackRegistry _registry;
        private readonly FfiCallback _callback;
        private bool _disposed;

        internal CallbackHandle(CallbackRegistry registry, FfiCallback callback)
        {
            _registry = registry;
            _callback = callback;
        }

        /// <summary>The native function pointer that can be passed to C code.</summary>
        public IntPtr FunctionPointer => _callback.FunctionPointer;

        /// <summary>The last exception thrown by the callback (Store/Rethrow policies).</summary>
        public Exception LastException => _callback.LastException;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _callback.Dispose();
            _registry.Remove(_callback);
        }
    }
}
