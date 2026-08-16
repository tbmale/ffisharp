using System;

namespace FfiSharp.Interop
{
    /// <summary>
    /// <see cref="INativeLibrary"/> implemented directly on OS primitives
    /// (<c>LoadLibrary</c>/<c>GetProcAddress</c>/<c>FreeLibrary</c> on Windows,
    /// <c>dlopen</c>/<c>dlsym</c>/<c>dlclose</c> on Unix). This is the portable
    /// implementation used when the modern <c>NativeLibrary</c> API is not
    /// available (e.g. .NET Framework 4.7.2), and it works on every runtime.
    /// </summary>
    public sealed class PlatformNativeLibrary : INativeLibrary
    {
        private IntPtr _handle;
        private readonly object _sync = new object();
        private bool _disposed;

        private PlatformNativeLibrary(IntPtr handle) => _handle = handle;

        public static PlatformNativeLibrary Load(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            IntPtr h = NativeMethods.Load(path, out string error);
            if (h == IntPtr.Zero)
                throw new NativeLibraryLoadException(path, error);
            return new PlatformNativeLibrary(h);
        }

        public IntPtr GetSymbol(string name)
        {
            ThrowIfDisposed();
            if (name == null) throw new ArgumentNullException(nameof(name));
            return NativeMethods.GetSymbol(_handle, name);
        }

        public IntPtr GetSymbolOrThrow(string name)
        {
            IntPtr p = GetSymbol(name);
            if (p == IntPtr.Zero)
                throw new MissingSymbolException(name);
            return p;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                NativeMethods.Unload(_handle);
                _handle = IntPtr.Zero;
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PlatformNativeLibrary));
            }
        }
    }
}
