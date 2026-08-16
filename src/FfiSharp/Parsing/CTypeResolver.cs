using System;
using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp.Parsing
{
    /// <summary>
    /// Resolves parsed <see cref="CTypeNode"/>s into the independent
    /// <see cref="FfiType"/> model, walking user typedefs, struct definitions, and
    /// built-in typedefs (<c>size_t</c>, <c>int32_t</c>, …).
    /// </summary>
    internal sealed class CTypeResolver
    {
        private readonly FfiTypeSystem _types;
        private readonly HeaderModel _header;
        private readonly IReadOnlyDictionary<string, CTypeNode> _userTypedefs;
        private readonly Dictionary<StructDeclaration, FfiStructType> _structs =
            new Dictionary<StructDeclaration, FfiStructType>();

        public CTypeResolver(
            FfiTypeSystem types,
            HeaderModel header,
            IDictionary<string, string> typeAliases)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _header = header ?? throw new ArgumentNullException(nameof(header));

            var merged = new Dictionary<string, CTypeNode>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, CTypeNode> kv in header.Typedefs)
                merged[kv.Key] = kv.Value;

            // User-supplied aliases (name -> C type text) take precedence and are
            // parsed with the same restricted grammar.
            if (typeAliases != null)
                foreach (KeyValuePair<string, string> kv in typeAliases)
                    merged[kv.Key] = CParser.ParseTypeExpression(kv.Value);

            _userTypedefs = merged;
        }

        public FfiType Resolve(CTypeNode node)
        {
            if (node is CPrimitiveTypeNode p)
                return _types.GetPrimitive(p.Primitive);

            if (node is CPointerTypeNode ptr)
                return _types.GetPointer(Resolve(ptr.Inner), ptr.PointeeIsConst);

            if (node is CStructTypeNode st)
                return ResolveStruct(st.Declaration);

            if (node is CFunctionPointerTypeNode fp)
            {
                FfiType returnType = Resolve(fp.ReturnType);
                var parameterTypes = new List<FfiType>(fp.Parameters.Count);
                for (int i = 0; i < fp.Parameters.Count; i++)
                    parameterTypes.Add(Resolve(fp.Parameters[i].Type));
                return new FfiFunctionType(returnType, parameterTypes, fp.CallingConvention,
                    _types.GetPointer(_types.GetPrimitive(FfiPrimitive.Void)).Size,
                    _types.GetPointer(_types.GetPrimitive(FfiPrimitive.Void)).Alignment);
            }

            if (node is CTypeNameNode name)
            {
                if (_userTypedefs.TryGetValue(name.Name, out CTypeNode def))
                    return Resolve(def);

                if (_types.TryResolveTypedef(name.Name, out FfiType builtin))
                    return builtin;

                throw new FfiException("Unknown C type name: " + name.Name);
            }

            throw new FfiException("Cannot resolve C type node " + node.GetType().Name);
        }

        /// <summary>Resolves a struct definition to its canonical <see cref="FfiStructType"/>.</summary>
        public FfiStructType ResolveStruct(StructDeclaration decl)
        {
            if (_structs.TryGetValue(decl, out FfiStructType existing))
                return existing;

            var fields = new List<FfiStructField>(decl.Fields.Count);
            for (int i = 0; i < decl.Fields.Count; i++)
            {
                StructFieldDeclaration fd = decl.Fields[i];
                FfiType fieldType = Resolve(fd.Type);
                fields.Add(new FfiStructField(fd.Name, fieldType, fd.ArrayLength));
            }

            var structType = new FfiStructType(decl.Tag ?? decl.TypedefName, fields);
            _structs[decl] = structType;
            return structType;
        }

        /// <summary>Resolves a struct type by typedef name or tag (used by CreateStruct).</summary>
        public FfiStructType ResolveStructByName(string name)
        {
            StructDeclaration decl = _header.FindStruct(name);
            if (decl == null)
                throw new FfiException("Unknown struct type: " + name);
            return ResolveStruct(decl);
        }
    }
}
