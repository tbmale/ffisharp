namespace FfiSharp
{
    /// <summary>
    /// How <c>const char*</c> / <c>const wchar_t*</c> strings are converted between
    /// managed <c>string</c> and native memory.
    /// </summary>
    public enum StringEncoding
    {
        /// <summary>Encode/decode as UTF-8 (default).</summary>
        Utf8 = 0,

        /// <summary>Encode/decode using the platform default 8-bit code page.</summary>
        Ansi = 1,

        /// <summary>Encode/decode as UTF-16 (little-endian).</summary>
        Utf16 = 2,

        /// <summary>Never convert automatically; strings must be passed as raw <c>IntPtr</c>.</summary>
        RawPointer = 3
    }
}
