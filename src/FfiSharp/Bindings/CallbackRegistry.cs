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

            // Slow path: surface one pending exception per call (matching the
            // historical per-call semantics), without stranding any others.
            //
            // Take ONLY the first pending exception, then re-Set() the shared flag so
            // any remaining pending exception stays visible for a subsequent call.
            // The flag is cleared only when a full scan found zero pending.
            while (_pendingFlag.IsSet)
            {
                FfiCallback[] snapshot;
                lock (_sync) snapshot = _callbacks.ToArray();

                Exception first = null;
                foreach (FfiCallback cb in snapshot)
                {
                    if (cb.TryTakePending(out first))
                        break; // take exactly one
                }

                if (first == null)
                {
                    // Stale flag (or a concurrent drain won). Safe to clear.
                    _pendingFlag.Clear();
                    return;
                }

                // Re-Set() before throwing: any OTHER callback's exception remains
                // visible on the next call. When this was the last one, the next call
                // scans, finds zero, and clears — a harmless extra lock+scan.
                _pendingFlag.Set();

                // Surface exactly one exception per call.
                throw new FfiException("A callback threw an exception", first);
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
