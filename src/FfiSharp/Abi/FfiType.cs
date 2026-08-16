using System;

namespace FfiSharp.Abi
{
    public enum FfiTypeKind
    {
        Primitive,
        Pointer,
        Struct,
        Array,
        Function
    }

    /// <summary>
    /// C primitive identities. A C <c>long</c> stays <see cref="Long"/> rather than
    /// being collapsed to <see cref="long"/>, because its native size depends on the
    /// target ABI (Windows LLP64: 32-bit; Linux/macOS LP64: 64-bit).
    /// </summary>
    public enum FfiPrimitive
    {
        Void,
        Char,
        SChar,
        UChar,
        Short,
        UShort,
        Int,
        UInt,
        Long,
        ULong,
        LongLong,
        ULongLong,
        Float,
        Double,
        WChar
    }

    /// <summary>
    /// The independent native type model. C types are NOT mapped to CLR
    /// <see cref="Type"/>; the native representation is authoritative.
    /// </summary>
    public abstract class FfiType
    {
        public abstract FfiTypeKind Kind { get; }

        /// <summary>Native size in bytes, or 0 for void / unknown.</summary>
        public abstract int Size { get; }

        /// <summary>Native alignment in bytes.</summary>
        public abstract int Alignment { get; }
    }

    public sealed class FfiPrimitiveType : FfiType
    {
        private readonly int _size;
        private readonly int _alignment;

        public FfiPrimitiveType(FfiPrimitive primitive, FfiPrimitive storage, int size, int alignment)
        {
            Primitive = primitive;
            Storage = storage;
            _size = size;
            _alignment = alignment;
        }

        /// <summary>The logical C identity (e.g. <c>Long</c>), preserved from the declaration.</summary>
        public FfiPrimitive Primitive { get; }

        /// <summary>
        /// The fixed-width storage kind actually marshaled (e.g. <c>LongLong</c> for
        /// <c>Long</c> on LP64). Fixed primitives equal their own storage kind.
        /// </summary>
        public FfiPrimitive Storage { get; }

        public override FfiTypeKind Kind => FfiTypeKind.Primitive;
        public override int Size => _size;
        public override int Alignment => _alignment;

        public override string ToString() => Primitive.ToString();
    }

    /// <summary>
    /// A C pointer type. Native representation is always <c>ffi_type_pointer</c>;
    /// the <see cref="Pointee"/> is retained for metadata and marshaling decisions.
    /// </summary>
    public sealed class FfiPointerType : FfiType
    {
        private readonly int _size;
        private readonly int _alignment;

        public FfiPointerType(FfiType pointee, bool pointeeIsConst, int size, int alignment)
        {
            Pointee = pointee;
            IsConst = pointeeIsConst;
            _size = size;
            _alignment = alignment;
        }

        /// <summary>The pointed-to type, or <c>null</c> for <c>void*</c>.</summary>
        public FfiType Pointee { get; }

        /// <summary>Whether the pointee is <c>const</c> (e.g. <c>const char*</c>).</summary>
        public bool IsConst { get; }

        public override FfiTypeKind Kind => FfiTypeKind.Pointer;
        public override int Size => _size;
        public override int Alignment => _alignment;

        public override string ToString() => (IsConst ? "const " : "") + (Pointee?.ToString() ?? "void") + "*";
    }

    // FfiStructType, FfiArrayType, and FfiFunctionType are introduced in later
    // phases (Phase 4/5). The model above is the foundation.
}
