using System;

namespace FfiSharp.Interop
{
    /// <summary>
    /// Minimal abstraction over a loaded native library. Implementations wrap
    /// <see cref="System.Runtime.InteropServices.NativeLibrary"/>, <c>dlopen</c>,
    /// or <c>LoadLibrary</c>. The rest of FfiSharp must not depend on the concrete
    /// platform API.
    /// </summary>
    public interface INativeLibrary : IDisposable
    {
        /// <summary>Resolves an exported symbol; returns <see cref="IntPtr.Zero"/> if not found.</summary>
        IntPtr GetSymbol(string name);
    }
}
