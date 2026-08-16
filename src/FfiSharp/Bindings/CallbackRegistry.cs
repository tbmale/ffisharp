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
        private readonly CallbackPendingFlag _pendingFlag = new CallbackPendingFlag();
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
                FfiCallback cb = _backend.CreateCallback(signature, callback, _policy, _pendingFlag);
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
            // Fast path: the overwhelmingly common case (no callback exception
            // pending) does NOT lock the registry or allocate a snapshot array.
            if (!_pendingFlag.IsSet)
                return;

            // Slow path: drain one pending exception ("one per call" semantics). The
            // retry loop guarantees a concurrently-recorded exception is never lost:
            // CaptureException sets the flag after recording the per-callback state,
            // so if the flag is observed set, the state is visible; clearing the flag
            // before scanning means any record during the scan re-sets it.
            while (_pendingFlag.IsSet)
            {
                _pendingFlag.Clear();

                FfiCallback[] snapshot;
                lock (_sync) snapshot = _callbacks.ToArray();

                foreach (FfiCallback cb in snapshot)
                {
                    if (cb.TryTakePending(out Exception ex))
                        throw new FfiException("A callback threw an exception", ex);
                }
                // No pending found this pass (stale flag or a concurrent drain won).
                // Loop re-checks the flag; exit if still clear.
            }
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
