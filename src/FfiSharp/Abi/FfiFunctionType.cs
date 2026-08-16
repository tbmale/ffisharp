using System;
using System.Collections.Generic;

namespace FfiSharp.Abi
{
    /// <summary>
    /// A C function-pointer type: a return type, parameter types, and a calling
    /// convention. At the native ABI level a function pointer is passed as
    /// <c>ffi_type_pointer</c>; the signature is retained to build libffi closures
    /// when a managed delegate is registered as a callback.
    /// </summary>
    public sealed class FfiFunctionType : FfiType
    {
        public FfiFunctionType(
            FfiType returnType,
            IReadOnlyList<FfiType> parameterTypes,
            FfiCallingConvention callingConvention,
            int pointerSize,
            int pointerAlignment)
        {
            ReturnType = returnType ?? throw new ArgumentNullException(nameof(returnType));
            ParameterTypes = parameterTypes ?? throw new ArgumentNullException(nameof(parameterTypes));
            CallingConvention = callingConvention;
            _size = pointerSize;
            _alignment = pointerAlignment;
        }

        private readonly int _size;
        private readonly int _alignment;

        public FfiType ReturnType { get; }
        public IReadOnlyList<FfiType> ParameterTypes { get; }
        public FfiCallingConvention CallingConvention { get; }

        public override FfiTypeKind Kind => FfiTypeKind.Function;
        public override int Size => _size;
        public override int Alignment => _alignment;

        public override string ToString()
        {
            var names = new string[ParameterTypes.Count];
            for (int i = 0; i < ParameterTypes.Count; i++)
                names[i] = ParameterTypes[i].ToString();
            return ReturnType + "(*)" + "(" + string.Join(", ", names) + ")";
        }
    }
}
