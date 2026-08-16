using System.Threading;

namespace FfiSharp.Bindings
{
    /// <summary>
    /// A cheap "a callback exception might be pending" indicator shared between a
    /// <see cref="CallbackRegistry"/> and its <see cref="FfiCallback"/>s. The flag is
    /// only an optimization hint: the authoritative per-callback state is
    /// <see cref="FfiCallback"/>'s own locked <c>_pendingRethrow</c> flag. The normal
    /// native-call path reads this flag with a single <see cref="Volatile.Read"/> —
    /// no lock, no allocation — and only enters the slow drain path when it is set.
    /// </summary>
    internal sealed class CallbackPendingFlag
    {
        private int _hasPending;

        public bool IsSet => Volatile.Read(ref _hasPending) != 0;

        public void Set() => Volatile.Write(ref _hasPending, 1);

        public void Clear() => Volatile.Write(ref _hasPending, 0);
    }
}
