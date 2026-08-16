namespace FfiSharp
{
    /// <summary>
    /// Policy for handling a managed exception thrown from a callback that is being
    /// invoked by native code. A managed exception must never unwind through native
    /// frames, so every policy catches at the native callback boundary.
    /// </summary>
    public enum CallbackExceptionPolicy
    {
        /// <summary>Swallow the exception silently.</summary>
        Ignore = 0,

        /// <summary>Record the exception; it is inspectable via the callback handle.</summary>
        Store = 1,

        /// <summary>Record the exception and rethrow it on the next managed call.</summary>
        RethrowOnManagedBoundary = 2
    }
}
