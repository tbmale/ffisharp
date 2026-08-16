using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp
{
    /// <summary>
    /// Options for loading a native library + header.
    /// </summary>
    public sealed class FfiLoadOptions
    {
        /// <summary>
        /// Optional explicit path to the libffi shared library. When null, libffi is
        /// resolved from platform-appropriate candidate names.
        /// </summary>
        public string LibFfiPath { get; set; }

        /// <summary>
        /// Optional explicit target ABI configuration. When null, the running
        /// platform is detected automatically.
        /// </summary>
        public FfiPlatform Platform { get; set; }

        /// <summary>
        /// Optional user-supplied typedef aliases (name → C type text, e.g.
        /// <c>{"mylong": "long"}</c>). Useful when the header relies on macros or
        /// platform typedefs that are not part of the built-in set.
        /// </summary>
        public IDictionary<string, string> TypeAliases { get; set; }

        /// <summary>
        /// Policy for handling exceptions thrown by managed callbacks invoked from
        /// native code. Defaults to <see cref="CallbackExceptionPolicy.Store"/>.
        /// </summary>
        public CallbackExceptionPolicy CallbackExceptionPolicy { get; set; } = CallbackExceptionPolicy.Store;

        /// <summary>
        /// Encoding used for automatic <c>const char*</c> ↔ string conversion.
        /// Defaults to <see cref="StringEncoding.Utf8"/>. Set to
        /// <see cref="StringEncoding.RawPointer"/> to disable automatic conversion.
        /// </summary>
        public StringEncoding StringEncoding { get; set; } = StringEncoding.Utf8;
    }
}
