using System;
using System.Collections.Generic;
using FfiSharp.Abi;
using FfiSharp.Interop;

namespace FfiSharp.Backend
{
    /// <summary>
    /// Resolves independent <see cref="FfiType"/>s to native libffi type references,
    /// building and caching aggregate <c>ffi_type</c>s for structs (including nested
    /// structs). Owns all allocated native struct types and releases them on dispose.
    /// </summary>
    internal sealed class NativeTypeResolver : IDisposable
    {
        private readonly LibFfiNative _ffi;
        private readonly Dictionary<FfiStructType, StructTypeHandle> _structs =
            new Dictionary<FfiStructType, StructTypeHandle>();
        private readonly object _sync = new object();
        private bool _disposed;

        public NativeTypeResolver(LibFfiNative ffi) => _ffi = ffi;

        public FfiTypeRef Resolve(FfiType type)
        {
            if (type is FfiPrimitiveType p)
                return _ffi.GetPrimitiveType(p.Storage);
            if (type is FfiPointerType)
                return _ffi.GetPointerType();
            if (type is FfiFunctionType)
                return _ffi.GetPointerType(); // function pointers are pointers at the ABI level
            if (type is FfiStructType s)
                return ResolveStruct(s);

            throw new NotSupportedException("Cannot resolve FfiType " + type.GetType().Name);
        }

        private FfiTypeRef ResolveStruct(FfiStructType structType)
        {
            // Monitor is reentrant, so nested struct resolution (which re-enters
            // this method) is safe.
            lock (_sync)
            {
                if (_structs.TryGetValue(structType, out StructTypeHandle handle))
                    return handle.Ref;

                var elementRefs = new List<FfiTypeRef>();
                for (int i = 0; i < structType.Fields.Count; i++)
                {
                    FfiStructField field = structType.Fields[i];
                    FfiTypeRef memberRef = Resolve(field.Type);
                    for (int k = 0; k < field.ArrayLength; k++)
                        elementRefs.Add(memberRef);
                }

                handle = _ffi.CreateStructType(elementRefs);
                _structs[structType] = handle;
                return handle.Ref;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (StructTypeHandle handle in _structs.Values)
                    handle.Dispose();
                _structs.Clear();
            }
        }
    }
}
