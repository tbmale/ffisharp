using System;

namespace FfiSharp
{
    /// <summary>Base type for all FfiSharp errors.</summary>
    public class FfiException : Exception
    {
        public FfiException(string message) : base(message) { }
        public FfiException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Thrown when a native library cannot be loaded.</summary>
    public class NativeLibraryLoadException : FfiException
    {
        public NativeLibraryLoadException(string message) : base(message) { }
        public NativeLibraryLoadException(string path, string error)
            : base($"Failed to load native library '{path}': {error}") { }
    }

    /// <summary>Thrown when a required native symbol cannot be resolved.</summary>
    public class MissingSymbolException : FfiException
    {
        public MissingSymbolException(string symbol)
            : base($"Native symbol not found: {symbol}") { }
    }

    /// <summary>Thrown when a native invocation fails (e.g. bad cif, bad ABI).</summary>
    public class FfiInvocationException : FfiException
    {
        public FfiInvocationException(string message) : base(message) { }
    }

    /// <summary>Thrown when a managed value cannot be converted to/from native memory.</summary>
    public class FfiMarshallingException : FfiException
    {
        public FfiMarshallingException(string message) : base(message) { }
        public FfiMarshallingException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Thrown when a C header fails to lex or parse.</summary>
    public class FfiParseException : FfiException
    {
        public int Line { get; }
        public int Column { get; }

        public FfiParseException(string message, int line, int column)
            : base(message + $" (line {line}, column {column})")
        {
            Line = line;
            Column = column;
        }
    }
}
