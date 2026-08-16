using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// The native test library stores callbacks in a single C <c>static</c> variable
    /// (<c>g_callback</c> behind <c>set_callback</c>/<c>fire_callback</c>). Tests
    /// that exercise this shared global must not run in parallel with each other, so
    /// they are grouped into one collection.
    /// </summary>
    [CollectionDefinition("callback-global", DisableParallelization = true)]
    public class CallbackGlobalCollection
    {
    }
}
