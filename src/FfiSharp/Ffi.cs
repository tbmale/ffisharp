using FfiSharp.Dynamic;

namespace FfiSharp
{
    /// <summary>
    /// Entry point for loading a native library + C header and calling it
    /// dynamically:
    /// <code>
    /// dynamic ffi = Ffi.Load("foo.so", "foo.h");
    /// int result = ffi.add(10, 20);
    /// </code>
    /// </summary>
    public static class Ffi
    {
        /// <summary>Loads a library + header and returns a dynamic caller.</summary>
        public static dynamic Load(string libraryPath, string headerPath, FfiLoadOptions options = null)
            => new FfiDynamicObject(FfiLibrary.Load(libraryPath, headerPath, options));

        /// <summary>Loads a library + header and returns the explicit (non-dynamic) API.</summary>
        public static FfiLibrary LoadLibrary(string libraryPath, string headerPath, FfiLoadOptions options = null)
            => FfiLibrary.Load(libraryPath, headerPath, options);
    }
}
