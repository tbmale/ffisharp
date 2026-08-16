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
        private IntPtr _fastPlan;
        private Action<IntPtr> _fastPlanFree;
        private bool _disposed;

        internal FfiCallPlan(IntPtr cif, IntPtr argTypesArray, FfiType returnType, IReadOnlyList<FfiType> argumentTypes)
        {
            _cif = cif;
            _argTypesArray = argTypesArray;
            ReturnType = returnType;
            ArgumentTypes = argumentTypes;
            IsPrimitiveOnly = Classify(returnType, argumentTypes);
        }

        /// <summary>
        /// Whether this signature's return and every argument are primitive types,
        /// enabling the allocation-free primitive fast path. Computed once and never
        /// mutated, so the call plan remains immutable and shareable across threads.
        /// </summary>
        internal bool IsPrimitiveOnly { get; }

        private static bool Classify(FfiType returnType, IReadOnlyList<FfiType> argumentTypes)
        {
            if (!(returnType is FfiPrimitiveType))
                return false;
            for (int i = 0; i < argumentTypes.Count; i++)
                if (!(argumentTypes[i] is FfiPrimitiveType))
                    return false;
            return true;
        }

        internal IntPtr Cif => _cif;
        internal IntPtr ArgTypesArray => _argTypesArray;

        /// <summary>The reusable libffi call plan, or <see cref="IntPtr.Zero"/> if unavailable.</summary>
        internal IntPtr FastPlan => _fastPlan;
        internal bool HasFastPlan => _fastPlan != IntPtr.Zero;

        internal void AttachFastPlan(IntPtr fastPlan, Action<IntPtr> free)
        {
            _fastPlan = fastPlan;
            _fastPlanFree = free;
        }

        public IReadOnlyList<FfiType> ArgumentTypes { get; }
        public FfiType ReturnType { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_fastPlan != IntPtr.Zero)
            {
                _fastPlanFree?.Invoke(_fastPlan);
                _fastPlan = IntPtr.Zero;
            }
            if (_cif != IntPtr.Zero) { Marshal.FreeHGlobal(_cif); _cif = IntPtr.Zero; }
            if (_argTypesArray != IntPtr.Zero) { Marshal.FreeHGlobal(_argTypesArray); _argTypesArray = IntPtr.Zero; }
        }
    }
}
