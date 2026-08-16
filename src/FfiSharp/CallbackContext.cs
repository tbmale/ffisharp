using System;

namespace FfiSharp
{
    /// <summary>
    /// Tracks, per native thread, whether that thread is currently executing a
    /// managed callback entered via a libffi closure. Used to detect <em>reentrant
    /// disposal</em> — a callback disposing the library or its own handle from
    /// within itself — so resources still on the stack can be released later rather
    /// than deadlocking (waiting for the disposing thread's own in-flight operation)
    /// or freeing the executing trampoline.
    /// </summary>
    internal static class CallbackContext
    {
        [ThreadStatic]
        private static int _depth;

        /// <summary>Current thread's callback nesting depth (0 when not inside one).</summary>
        public static int Depth => _depth;

        public static void Enter() => _depth++;

        public static void Exit() => _depth--;
    }
}
