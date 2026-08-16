using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp.Parsing
{
    /// <summary>
    /// A parsed C type as an abstract syntax tree. Nodes are resolved to the
    /// independent <see cref="FfiType"/> model by <see cref="CTypeResolver"/>.
    /// </summary>
    internal abstract class CTypeNode
    {
        public abstract string Describe();
    }

    /// <summary>A C primitive type (int, char, unsigned long, …).</summary>
    internal sealed class CPrimitiveTypeNode : CTypeNode
    {
        public FfiPrimitive Primitive { get; }

        public CPrimitiveTypeNode(FfiPrimitive primitive) => Primitive = primitive;

        public override string Describe() => Primitive.ToString();
    }

    /// <summary>A reference to a typedef (or later, struct) name.</summary>
    internal sealed class CTypeNameNode : CTypeNode
    {
        public string Name { get; }

        public CTypeNameNode(string name) => Name = name;

        public override string Describe() => Name;
    }

    /// <summary>A pointer type; <see cref="Inner"/> is the pointee.</summary>
    internal sealed class CPointerTypeNode : CTypeNode
    {
        public CTypeNode Inner { get; }

        /// <summary>Whether the pointee is <c>const</c> (e.g. <c>const char*</c>).</summary>
        public bool PointeeIsConst { get; }

        public CPointerTypeNode(CTypeNode inner, bool pointeeIsConst = false)
        {
            Inner = inner;
            PointeeIsConst = pointeeIsConst;
        }

        public override string Describe() => (PointeeIsConst ? "const " : "") + Inner.Describe() + "*";
    }

    /// <summary>A reference to a struct type (by its parsed declaration).</summary>
    internal sealed class CStructTypeNode : CTypeNode
    {
        public StructDeclaration Declaration { get; }

        public CStructTypeNode(StructDeclaration declaration) => Declaration = declaration;

        public override string Describe() => "struct " + (Declaration.Tag ?? "(anonymous)");
    }

    /// <summary>A C function-pointer type (return type + parameters + convention).</summary>
    internal sealed class CFunctionPointerTypeNode : CTypeNode
    {
        public CFunctionPointerTypeNode(
            CTypeNode returnType,
            IReadOnlyList<ParameterDeclaration> parameters,
            FfiCallingConvention callingConvention)
        {
            ReturnType = returnType;
            Parameters = parameters;
            CallingConvention = callingConvention;
        }

        public CTypeNode ReturnType { get; }
        public IReadOnlyList<ParameterDeclaration> Parameters { get; }
        public FfiCallingConvention CallingConvention { get; }

        public override string Describe() => "function pointer";
    }
}
