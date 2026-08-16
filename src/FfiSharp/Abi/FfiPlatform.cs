using System;
using System.Runtime.InteropServices;

namespace FfiSharp.Abi
{
    public enum FfiOS
    {
        Windows,
        Linux,
        OSX,
        Unknown
    }

    public enum FfiArchitecture
    {
        X86,
        X64,
        Arm64,
        Unknown
    }

    /// <summary>
    /// Explicit target ABI configuration. Determines how platform-dependent C types
    /// (<c>char</c>, <c>long</c>, <c>unsigned long</c>, pointer-sized integers) map
    /// to fixed-width libffi types. The actual register/stack placement is still
    /// decided by libffi; this only classifies platform metadata.
    /// </summary>
    public sealed class FfiPlatform
    {
        public FfiPlatform(FfiOS os, FfiArchitecture architecture)
        {
            OS = os;
            Architecture = architecture;

            PointerSize = Architecture switch
            {
                FfiArchitecture.X86 => 4,
                FfiArchitecture.X64 => 8,
                FfiArchitecture.Arm64 => 8,
                _ => IntPtr.Size
            };

            // Windows uses the LLP64 model on every architecture: long is 32-bit
            // even on x64. Linux/macOS use ILP32 (x86) or LP64 (x64/arm64), so
            // sizeof(long) == sizeof(void*).
            CLongSize = OS == FfiOS.Windows ? 4 : PointerSize;

            // char is unsigned only on Linux/ARM64 in the platforms we target
            // (gcc/clang default on aarch64-linux); everywhere else it is signed.
            IsCharSigned = !(OS == FfiOS.Linux && Architecture == FfiArchitecture.Arm64);

            WCharSize = OS == FfiOS.Windows ? 2 : 4;
            DefaultCallingConvention = FfiCallingConvention.Cdecl;
        }

        public FfiOS OS { get; }
        public FfiArchitecture Architecture { get; }

        /// <summary>Native pointer size in bytes (4 or 8).</summary>
        public int PointerSize { get; }

        /// <summary>Native size of C <c>long</c> in bytes (4 on LLP64, 8 on LP64).</summary>
        public int CLongSize { get; }

        /// <summary>Whether plain C <c>char</c> is signed on this platform.</summary>
        public bool IsCharSigned { get; }

        /// <summary>Native size of <c>wchar_t</c> in bytes.</summary>
        public int WCharSize { get; }

        public FfiCallingConvention DefaultCallingConvention { get; }

        public bool Is64Bit => PointerSize == 8;

        /// <summary>
        /// Maps a logical C primitive to the fixed-width storage kind that libffi
        /// will use for it. Fixed primitives map to themselves.
        /// </summary>
        public FfiPrimitive ResolveStorage(FfiPrimitive logical)
        {
            switch (logical)
            {
                case FfiPrimitive.Char:
                    return IsCharSigned ? FfiPrimitive.SChar : FfiPrimitive.UChar;
                case FfiPrimitive.Long:
                    return CLongSize == 8 ? FfiPrimitive.LongLong : FfiPrimitive.Int;
                case FfiPrimitive.ULong:
                    return CLongSize == 8 ? FfiPrimitive.ULongLong : FfiPrimitive.UInt;
                case FfiPrimitive.WChar:
                    // wchar_t is 2 bytes (UTF-16) on Windows, 4 bytes (UTF-32) elsewhere.
                    return WCharSize == 2 ? FfiPrimitive.UShort : FfiPrimitive.Int;
                default:
                    return logical;
            }
        }

        /// <summary>The signed integer that matches the pointer size.</summary>
        public FfiPrimitive PointerSizedSigned => PointerSize == 8 ? FfiPrimitive.LongLong : FfiPrimitive.Int;

        /// <summary>The unsigned integer that matches the pointer size.</summary>
        public FfiPrimitive PointerSizedUnsigned => PointerSize == 8 ? FfiPrimitive.ULongLong : FfiPrimitive.UInt;

        public static FfiPlatform Detect()
        {
            FfiOS os;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) os = FfiOS.Windows;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) os = FfiOS.Linux;
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = FfiOS.OSX;
            else os = FfiOS.Unknown;

            FfiArchitecture arch;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case System.Runtime.InteropServices.Architecture.X86: arch = FfiArchitecture.X86; break;
                case System.Runtime.InteropServices.Architecture.X64: arch = FfiArchitecture.X64; break;
                case System.Runtime.InteropServices.Architecture.Arm64: arch = FfiArchitecture.Arm64; break;
                default: arch = FfiArchitecture.Unknown; break;
            }

            return new FfiPlatform(os, arch);
        }

        public override string ToString() => $"{OS}-{Architecture} (ptr {PointerSize}, long {CLongSize})";
    }
}
