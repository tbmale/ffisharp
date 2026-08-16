using System;
using System.Collections.Generic;

namespace FfiSharp.Abi
{
    /// <summary>
    /// Canonical, thread-safe cache of <see cref="FfiType"/> instances and C typedef
    /// aliases. Primitives are resolved by identity and cached; platform-dependent
    /// primitives (<c>long</c>, <c>char</c>) are mapped to fixed-width storage via
    /// the supplied <see cref="FfiPlatform"/>. This is the managed counterpart to
    /// the libffi <c>ffi_type</c> cache.
    /// </summary>
    public sealed class FfiTypeSystem
    {
        private readonly FfiPlatform _platform;
        private readonly Func<FfiPrimitive, FfiPrimitiveType> _primitiveResolver;
        private readonly Func<FfiType, bool, FfiPointerType> _pointerResolver;

        private readonly Dictionary<FfiPrimitive, FfiPrimitiveType> _primitives =
            new Dictionary<FfiPrimitive, FfiPrimitiveType>();
        private readonly Dictionary<string, FfiType> _typedefs =
            new Dictionary<string, FfiType>(StringComparer.Ordinal);
        private readonly Dictionary<FfiType, FfiPointerType> _pointers =
            new Dictionary<FfiType, FfiPointerType>();
        private readonly Dictionary<FfiType, FfiPointerType> _constPointers =
            new Dictionary<FfiType, FfiPointerType>();
        private FfiPointerType _voidPointer;
        private FfiPointerType _constVoidPointer;

        private readonly object _sync = new object();

        public FfiTypeSystem(
            FfiPlatform platform,
            Func<FfiPrimitive, FfiPrimitiveType> primitiveResolver,
            Func<FfiType, bool, FfiPointerType> pointerResolver)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _primitiveResolver = primitiveResolver ?? throw new ArgumentNullException(nameof(primitiveResolver));
            _pointerResolver = pointerResolver ?? throw new ArgumentNullException(nameof(pointerResolver));
            RegisterBuiltinTypedefs();
        }

        public FfiPlatform Platform => _platform;

        /// <summary>Returns the canonical (cached) primitive type for a logical C primitive.</summary>
        public FfiPrimitiveType GetPrimitive(FfiPrimitive primitive)
        {
            lock (_sync)
            {
                if (!_primitives.TryGetValue(primitive, out FfiPrimitiveType t))
                {
                    t = _primitiveResolver(primitive);
                    _primitives[primitive] = t;
                }
                return t;
            }
        }

        /// <summary>Returns the canonical (cached) pointer type for a pointee type.</summary>
        public FfiPointerType GetPointer(FfiType pointee, bool pointeeIsConst = false)
        {
            lock (_sync)
            {
                // A null pointee represents void*; Dictionary cannot key on null.
                if (pointee == null)
                {
                    if (pointeeIsConst)
                    {
                        if (_constVoidPointer == null)
                            _constVoidPointer = _pointerResolver(null, true);
                        return _constVoidPointer;
                    }
                    if (_voidPointer == null)
                        _voidPointer = _pointerResolver(null, false);
                    return _voidPointer;
                }

                Dictionary<FfiType, FfiPointerType> dict = pointeeIsConst ? _constPointers : _pointers;
                if (!dict.TryGetValue(pointee, out FfiPointerType p))
                {
                    p = _pointerResolver(pointee, pointeeIsConst);
                    dict[pointee] = p;
                }
                return p;
            }
        }

        public void AddTypedef(string name, FfiType type)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (type == null) throw new ArgumentNullException(nameof(type));
            lock (_sync)
            {
                _typedefs[name] = type;
            }
        }

        public bool TryResolveTypedef(string name, out FfiType type)
        {
            lock (_sync)
            {
                return _typedefs.TryGetValue(name, out type);
            }
        }

        /// <summary>Resolves a typedef by name, throwing if unknown.</summary>
        public FfiType ResolveTypedef(string name)
        {
            if (!TryResolveTypedef(name, out FfiType type))
                throw new KeyNotFoundException("Unknown C type name: " + name);
            return type;
        }

        private void RegisterBuiltinTypedefs()
        {
            // Fixed-width integer aliases.
            AddTypedef("int8_t", GetPrimitive(FfiPrimitive.SChar));
            AddTypedef("uint8_t", GetPrimitive(FfiPrimitive.UChar));
            AddTypedef("int16_t", GetPrimitive(FfiPrimitive.Short));
            AddTypedef("uint16_t", GetPrimitive(FfiPrimitive.UShort));
            AddTypedef("int32_t", GetPrimitive(FfiPrimitive.Int));
            AddTypedef("uint32_t", GetPrimitive(FfiPrimitive.UInt));
            AddTypedef("int64_t", GetPrimitive(FfiPrimitive.LongLong));
            AddTypedef("uint64_t", GetPrimitive(FfiPrimitive.ULongLong));

            // Pointer-sized integer aliases. These are independent of C `long`
            // because on Windows x64 `long` is 32-bit while these are 64-bit.
            AddTypedef("intptr_t", GetPrimitive(_platform.PointerSizedSigned));
            AddTypedef("uintptr_t", GetPrimitive(_platform.PointerSizedUnsigned));
            AddTypedef("size_t", GetPrimitive(_platform.PointerSizedUnsigned));
            AddTypedef("ssize_t", GetPrimitive(_platform.PointerSizedSigned));
            AddTypedef("ptrdiff_t", GetPrimitive(_platform.PointerSizedSigned));
            AddTypedef("wchar_t", GetPrimitive(FfiPrimitive.WChar));
        }
    }
}
