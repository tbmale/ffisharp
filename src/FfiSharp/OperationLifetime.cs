using System;
using System.Threading;

namespace FfiSharp
{
    /// <summary>
    /// A lightweight, reentrant reference-counted lifetime gate. It separates
    /// "accepting operations" from "releasing resources" so a native resource can
    /// be disposed without racing an in-flight operation that uses it.
    ///
    /// Operations call <see cref="TryEnter"/>/<see cref="Exit"/> around their work.
    /// Disposal calls <see cref="Close"/>, which rejects new operations and blocks
    /// until every active operation has drained. No per-operation allocation occurs
    /// and no lock is held across the operation body — only a brief monitor
    /// critical section guards the counter — so independent operations stay
    /// concurrent.
    ///
    /// Reentrancy: <c>TryEnter</c>/<c>Exit</c> only mutate a counter under a
    /// reentrant monitor, so nested operations on the same thread are safe (e.g. a
    /// callback that itself invokes another bound function).
    ///
    /// Limitation: <see cref="Close"/> must not be called from within an active
    /// operation on the same thread, or it will deadlock waiting for itself.
    /// </summary>
    internal sealed class OperationLifetime
    {
        private readonly object _sync = new object();
        private int _active;
        private bool _closing;

        /// <summary>Attempts to begin an operation. Returns false once closing/closed.</summary>
        public bool TryEnter()
        {
            lock (_sync)
            {
                if (_closing) return false;
                _active++;
                return true;
            }
        }

        /// <summary>Ends an operation begun by <see cref="TryEnter"/>.</summary>
        public void Exit()
        {
            lock (_sync)
            {
                _active--;
                if (_active == 0)
                    Monitor.PulseAll(_sync);
            }
        }

        /// <summary>
        /// Marks the lifetime closing (rejecting new operations) and blocks until all
        /// active operations complete. Idempotent.
        /// </summary>
        public void Close()
        {
            lock (_sync)
            {
                _closing = true;
                while (_active > 0)
                    Monitor.Wait(_sync);
            }
        }
    }
}
