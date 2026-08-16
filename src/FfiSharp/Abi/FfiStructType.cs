using System;
using System.Collections.Generic;

namespace FfiSharp.Abi
{
    /// <summary>A single field of a C struct.</summary>
    public sealed class FfiStructField
    {
        public FfiStructField(string name, FfiType type, int arrayLength)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            Type = type ?? throw new ArgumentNullException(nameof(type));
            ArrayLength = arrayLength < 1 ? 1 : arrayLength;
        }

        public string Name { get; }

        /// <summary>The element type (for an array field, the type of one element).</summary>
        public FfiType Type { get; }

        /// <summary>Number of elements (1 for a scalar field, &gt;1 for a fixed-size array).</summary>
        public int ArrayLength { get; }

        /// <summary>Byte offset of this field within the struct (computed by layout).</summary>
        public int Offset { get; internal set; }
    }

    /// <summary>
    /// A blittable C struct type. The native ABI passing/returning rules are
    /// delegated to libffi (via the aggregate <c>ffi_type</c>); this type computes
    /// size/alignment/field-offsets for managed memory representation using the
    /// standard C layout rules (no bitfields, no packing, no unions).
    /// </summary>
    public sealed class FfiStructType : FfiType
    {
        private readonly int _size;
        private readonly int _alignment;

        public FfiStructType(string name, IReadOnlyList<FfiStructField> fields)
        {
            Name = name;
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            Fields = fields;

            if (fields.Count == 0)
                throw new FfiException("Empty structs are not supported");

            // Standard C layout: each field aligned to its natural alignment, struct
            // aligned to the maximum field alignment, size rounded up to alignment.
            int offset = 0;
            int maxAlign = 1;
            for (int i = 0; i < fields.Count; i++)
            {
                FfiStructField f = fields[i];
                if (f.Type.Size <= 0)
                    throw new FfiException($"Field '{f.Name}' of struct '{Name}' has invalid size");
                int align = f.Type.Alignment;
                if (align < 1) align = 1;
                offset = AlignUp(offset, align);
                f.Offset = offset;
                offset = CheckedArithmetic.Add(offset, CheckedArithmetic.Multiply(f.Type.Size, f.ArrayLength));
                if (align > maxAlign) maxAlign = align;
            }

            _alignment = maxAlign;
            _size = AlignUp(offset, maxAlign);
        }

        public string Name { get; }
        public IReadOnlyList<FfiStructField> Fields { get; }

        public override FfiTypeKind Kind => FfiTypeKind.Struct;
        public override int Size => _size;
        public override int Alignment => _alignment;

        public FfiStructField GetField(string name)
        {
            for (int i = 0; i < Fields.Count; i++)
                if (string.Equals(Fields[i].Name, name, StringComparison.Ordinal))
                    return Fields[i];
            throw new KeyNotFoundException($"Struct '{Name}' has no field '{name}'");
        }

        private static int AlignUp(int value, int alignment)
            => CheckedArithmetic.Add(value, alignment - 1) / alignment * alignment;

        public override string ToString() => "struct " + (Name ?? "(anonymous)");
    }
}
