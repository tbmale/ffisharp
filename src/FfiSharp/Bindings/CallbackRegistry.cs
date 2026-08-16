using System;
using System.Collections.Generic;
using FfiSharp.Abi;
using FfiSharp.Backend;

namespace FfiSharp.Bindings
{
    /// <summary>
    /// Tracks all live callbacks created through a library so their closures (and
    /// the managed delegates they wrap) are retained for as long as native code may
    /// call them, and released on library disposal.
    /// </summary>
    internal sealed class CallbackRegistry : IDisposable
    {
        private readonly LibFfiBackend _backend;
        private readonly CallbackExceptionPolicy _policy;
        private readonly List<FfiCallback> _callbacks = new List<FfiCallback>();
        private readonly object _sync = new object();
        private bool _disposed;

        internal CallbackRegistry(LibFfiBackend backend, CallbackExceptionPolicy policy)
        {
            _backend = backend;
            _policy = policy;
        }

        internal FfiCallback Create(FfiFunctionType signature, Delegate callback)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CallbackRegistry));
                FfiCallback cb = _backend.CreateCallback(signature, callback, _policy);
                _callbacks.Add(cb);
                return cb;
            }
        }

        internal void Remove(FfiCallback callback)
        {
            lock (_sync)
            {
                _callbacks.Remove(callback);
            }
        }

        internal void ThrowPendingExceptions()
        {
            FfiCallback[] snapshot;
            lock (_sync) snapshot = _callbacks.ToArray();
            foreach (FfiCallback cb in snapshot)
                cb.ThrowPendingIfAny();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (FfiCallback cb in _callbacks)
                    cb.Dispose();
                _callbacks.Clear();
            }
        }
    }
}
