using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FfiSharp.Abi;

namespace FfiSharp.Bindings
{
    /// <summary>
    /// A prepared, reusable call description for a specific function signature.
    /// It owns the native <c>ffi_cif</c> buffer and the <c>ffi_type**</c> argument
    /// array, both of which must outlive every invocation.
    /// </summary>
    public sealed class FfiCallPlan : IDisposable
    {
        private IntPtr _cif;
        private IntPtr _argTypesArray;
        private bool _disposed;

        internal FfiCallPlan(IntPtr cif, IntPtr argTypesArray, FfiType returnType, IReadOnlyList<FfiType> argumentTypes)
        {
            _cif = cif;
            _argTypesArray = argTypesArray;
            ReturnType = returnType;
            ArgumentTypes = argumentTypes;
        }

        internal IntPtr Cif => _cif;
        internal IntPtr ArgTypesArray => _argTypesArray;

        public IReadOnlyList<FfiType> ArgumentTypes { get; }
        public FfiType ReturnType { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_cif != IntPtr.Zero) { Marshal.FreeHGlobal(_cif); _cif = IntPtr.Zero; }
            if (_argTypesArray != IntPtr.Zero) { Marshal.FreeHGlobal(_argTypesArray); _argTypesArray = IntPtr.Zero; }
        }
    }
}
