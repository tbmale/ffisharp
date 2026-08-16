using System;
using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp.Parsing
{
    /// <summary>A single function parameter (name is null when anonymous).</summary>
    internal sealed class ParameterDeclaration
    {
        public ParameterDeclaration(string name, CTypeNode type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public CTypeNode Type { get; }
    }

    /// <summary>A parsed C typedef (name → type node).</summary>
    internal sealed class TypedefDeclaration
    {
        public TypedefDeclaration(string name, CTypeNode type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public CTypeNode Type { get; }
    }

    /// <summary>A parsed C function declaration.</summary>
    internal sealed class FunctionDeclaration
    {
        public FunctionDeclaration(
            string name,
            CTypeNode returnType,
            IReadOnlyList<ParameterDeclaration> parameters,
            FfiCallingConvention callingConvention)
        {
            Name = name;
            ReturnType = returnType;
            Parameters = parameters;
            CallingConvention = callingConvention;
        }

        public string Name { get; }
        public CTypeNode ReturnType { get; }
        public IReadOnlyList<ParameterDeclaration> Parameters { get; }
        public FfiCallingConvention CallingConvention { get; }
    }

    /// <summary>A parsed struct field (array suffix produces <see cref="ArrayLength"/> &gt; 1).</summary>
    internal sealed class StructFieldDeclaration
    {
        public StructFieldDeclaration(string name, CTypeNode type, int arrayLength)
        {
            Name = name;
            Type = type;
            ArrayLength = arrayLength < 1 ? 1 : arrayLength;
        }

        public string Name { get; }
        public CTypeNode Type { get; }
        public int ArrayLength { get; }
    }

    /// <summary>A parsed C struct definition.</summary>
    internal sealed class StructDeclaration
    {
        public StructDeclaration(string tag, string typedefName, IReadOnlyList<StructFieldDeclaration> fields)
        {
            Tag = tag;
            TypedefName = typedefName;
            Fields = fields;
        }

        /// <summary>The struct tag (e.g. <c>Point</c> in <c>struct Point</c>), or null.</summary>
        public string Tag { get; }

        /// <summary>The typedef name when declared via <c>typedef struct {...} Name;</c>, or null.</summary>
        public string TypedefName { get; }

        public IReadOnlyList<StructFieldDeclaration> Fields { get; }
    }

    /// <summary>
    /// The parsed header: function declarations, typedefs, and struct definitions
    /// (unresolved).
    /// </summary>
    internal sealed class HeaderModel
    {
        private readonly List<FunctionDeclaration> _functions;
        private readonly Dictionary<string, CTypeNode> _typedefs;
        private readonly List<StructDeclaration> _structs;
        private readonly Dictionary<string, StructDeclaration> _structByTag;

        public HeaderModel(
            List<FunctionDeclaration> functions,
            Dictionary<string, CTypeNode> typedefs,
            List<StructDeclaration> structs,
            Dictionary<string, StructDeclaration> structByTag)
        {
            _functions = functions;
            _typedefs = typedefs;
            _structs = structs;
            _structByTag = structByTag;
        }

        public IReadOnlyList<FunctionDeclaration> Functions => _functions;
        public IReadOnlyDictionary<string, CTypeNode> Typedefs => _typedefs;
        public IReadOnlyList<StructDeclaration> Structs => _structs;
        public IReadOnlyDictionary<string, StructDeclaration> StructByTag => _structByTag;

        public FunctionDeclaration FindFunction(string name)
        {
            if (name == null) return null;
            for (int i = 0; i < _functions.Count; i++)
                if (string.Equals(_functions[i].Name, name, StringComparison.Ordinal))
                    return _functions[i];
            return null;
        }

        /// <summary>
        /// Finds a struct definition by typedef name first, then by tag (used by
        /// <c>CreateStruct</c> and by name resolution).
        /// </summary>
        public StructDeclaration FindStruct(string name)
        {
            if (name == null) return null;
            if (_typedefs.TryGetValue(name, out CTypeNode node) && node is CStructTypeNode sn)
                return sn.Declaration;
            if (_structByTag.TryGetValue(name, out StructDeclaration decl))
                return decl;
            return null;
        }
    }
}
