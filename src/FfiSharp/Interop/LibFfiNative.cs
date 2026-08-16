using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FfiSharp.Abi;

namespace FfiSharp.Interop
{
    /// <summary>
    /// Blittable mirror of libffi's <c>ffi_type</c>:
    /// <c>size_t size; unsigned short alignment; unsigned short type; ffi_type** elements;</c>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FfiTypeNative
    {
        public IntPtr Size;
        public ushort Alignment;
        public ushort Type;
        public IntPtr Elements;
    }

    /// <summary>
    /// A resolved libffi type: the native address of an <c>ffi_type</c> plus a
    /// snapshot of its size/alignment (used by the marshaller).
    /// </summary>
    internal readonly struct FfiTypeRef
    {
        public readonly IntPtr Pointer;
        public readonly int Size;
        public readonly int Alignment;

        public FfiTypeRef(IntPtr pointer, FfiTypeNative info)
        {
            Pointer = pointer;
            Size = (int)info.Size.ToInt64();
            Alignment = info.Alignment;
        }
    }

    /// <summary>
    /// Low-level wrapper around a loaded libffi instance. ALL direct knowledge of
    /// libffi's C symbols is isolated here; the rest of FfiSharp talks to
    /// <see cref="Backend.IFfiBackend"/>. Function pointers are resolved via
    /// <c>GetSymbol</c> and wrapped in delegates rather than bound with DllImport,
    /// so libffi can be loaded from an explicit path at runtime.
    /// </summary>
    internal sealed class LibFfiNative
    {
        private const int FfiOk = 0;
        private const ushort FfiTypeStruct = 13;

        // ---- libffi C function delegates (all use the C calling convention) ----
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PrepCif(IntPtr cif, int abi, uint nargs, IntPtr rtype, IntPtr argTypes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Call(IntPtr cif, IntPtr fn, IntPtr rvalue, IntPtr avalues);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetDefaultAbi();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr GetVersionNumber();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate UIntPtr GetClosureSize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ClosureAllocFn(UIntPtr size, out IntPtr code);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PrepClosureLoc(IntPtr closure, IntPtr cif, IntPtr fun, IntPtr userData, IntPtr codeloc);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ClosureFree(IntPtr writable);

        // Reusable call plans (libffi 3.7.0+). Optional fast path; may be null when
        // the loaded libffi predates the API. Detect via HasCallPlanApi and fall
        // back to ffi_call.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr CallPlanAllocFn(IntPtr cif);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CallPlanInvokeFn(IntPtr plan, IntPtr fn, IntPtr rvalue, IntPtr avalues);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CallPlanFreeFn(IntPtr plan);

        private readonly INativeLibrary _lib;
        private readonly PrepCif _prepCif;
        private readonly Call _call;
        private readonly GetDefaultAbi _getDefaultAbi;
        private readonly GetVersionNumber _getVersionNumber;
        private readonly GetClosureSize _getClosureSize;
        private readonly ClosureAllocFn _closureAlloc;
        private readonly PrepClosureLoc _prepClosureLoc;
        private readonly ClosureFree _closureFree;
        private readonly CallPlanAllocFn _callPlanAlloc;
        private readonly CallPlanInvokeFn _callPlanInvoke;
        private readonly CallPlanFreeFn _callPlanFree;
        private readonly Dictionary<FfiPrimitive, FfiTypeRef> _primitiveTypes = new Dictionary<FfiPrimitive, FfiTypeRef>();
        private readonly FfiTypeRef _pointerType;

        public int DefaultAbi => _getDefaultAbi();
        public ulong VersionNumber => _getVersionNumber().ToUInt64();
        public int ClosureSize => (int)_getClosureSize().ToUInt64();

        /// <summary>Whether the loaded libffi exposes reusable call plans (3.7.0+).</summary>
        public bool HasCallPlanApi => _callPlanAlloc != null;

        public LibFfiNative(INativeLibrary lib)
        {
            _lib = lib ?? throw new ArgumentNullException(nameof(lib));
            _prepCif = Resolve<PrepCif>("ffi_prep_cif");
            _call = Resolve<Call>("ffi_call");
            _getDefaultAbi = Resolve<GetDefaultAbi>("ffi_get_default_abi");
            _getVersionNumber = Resolve<GetVersionNumber>("ffi_get_version_number");
            _getClosureSize = Resolve<GetClosureSize>("ffi_get_closure_size");
            _closureAlloc = Resolve<ClosureAllocFn>("ffi_closure_alloc");
            _prepClosureLoc = Resolve<PrepClosureLoc>("ffi_prep_closure_loc");
            _closureFree = Resolve<ClosureFree>("ffi_closure_free");

            // Optional fast path (libffi >= 3.7.0). Missing symbols -> null -> fallback.
            _callPlanAlloc = TryResolve<CallPlanAllocFn>("ffi_call_plan_alloc");
            _callPlanInvoke = TryResolve<CallPlanInvokeFn>("ffi_call_plan_invoke");
            _callPlanFree = TryResolve<CallPlanFreeFn>("ffi_call_plan_free");

            // Resolve the fixed-width primitive ffi_type_* globals. Char/Long/ULong
            // are platform-dependent and map to one of these via FfiPlatform; they
            // have no direct exported symbol.
            foreach (FfiPrimitive p in Enum.GetValues(typeof(FfiPrimitive)))
            {
                string symbol = PrimitiveSymbol(p);
                if (symbol == null) continue;
                IntPtr addr = _lib.GetSymbol(symbol);
                if (addr == IntPtr.Zero) continue;
                FfiTypeNative info = Marshal.PtrToStructure<FfiTypeNative>(addr);
                _primitiveTypes[p] = new FfiTypeRef(addr, info);
            }

            IntPtr ptrAddr = _lib.GetSymbol("ffi_type_pointer");
            if (ptrAddr == IntPtr.Zero)
                throw new MissingSymbolException("ffi_type_pointer (in libffi)");
            _pointerType = new FfiTypeRef(ptrAddr, Marshal.PtrToStructure<FfiTypeNative>(ptrAddr));
        }

        private T Resolve<T>(string name) where T : class
        {
            IntPtr p = _lib.GetSymbol(name);
            if (p == IntPtr.Zero)
                throw new MissingSymbolException(name + " (in libffi)");
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        private T TryResolve<T>(string name) where T : class
        {
            IntPtr p = _lib.GetSymbol(name);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
        }

        /// <summary>Resolves a fixed-width primitive's libffi type (no Char/Long/ULong).</summary>
        public FfiTypeRef GetPrimitiveType(FfiPrimitive fixedPrimitive)
        {
            if (!_primitiveTypes.TryGetValue(fixedPrimitive, out FfiTypeRef t))
                throw new NotSupportedException(
                    "Primitive " + fixedPrimitive + " is not supported by this libffi/platform configuration yet.");
            return t;
        }

        /// <summary>Resolves the libffi pointer type (<c>ffi_type_pointer</c>).</summary>
        public FfiTypeRef GetPointerType() => _pointerType;

        public FfiTypeRef ResolveType(FfiType type)
        {
            if (type is FfiPrimitiveType p)
                return GetPrimitiveType(p.Storage);
            if (type is FfiPointerType)
                return _pointerType;

            // Structs are built via CreateStructType (the backend's NativeTypeResolver).
            throw new NotSupportedException("FfiType " + type.GetType().Name + " is not resolved directly; use CreateStructType.");
        }

        /// <summary>
        /// Builds a native aggregate <c>ffi_type</c> (FFI_TYPE_STRUCT) from a list of
        /// member <c>ffi_type</c> references. The returned handle owns the native
        /// <c>ffi_type</c> struct and its NULL-terminated elements array and must be
        /// kept alive for as long as any call plan references the struct.
        /// </summary>
        public StructTypeHandle CreateStructType(IReadOnlyList<FfiTypeRef> elementRefs)
        {
            int n = elementRefs.Count;

            // NULL-terminated elements array: n + 1 pointers.
            IntPtr elements = Marshal.AllocHGlobal((n + 1) * IntPtr.Size);
            try
            {
                for (int i = 0; i < n; i++)
                    Marshal.WriteIntPtr(elements, i * IntPtr.Size, elementRefs[i].Pointer);
                Marshal.WriteIntPtr(elements, n * IntPtr.Size, IntPtr.Zero);

                IntPtr type = Marshal.AllocHGlobal(Marshal.SizeOf<FfiTypeNative>());
                try
                {
                    // size = 0, alignment = 0, type = FFI_TYPE_STRUCT; libffi fills
                    // size/alignment during ffi_prep_cif.
                    var ffiType = new FfiTypeNative
                    {
                        Size = IntPtr.Zero,
                        Alignment = 0,
                        Type = FfiTypeStruct,
                        Elements = elements
                    };
                    Marshal.StructureToPtr(ffiType, type, false);

                    var handle = new StructTypeHandle(type, elements);
                    type = IntPtr.Zero;
                    elements = IntPtr.Zero;
                    return handle;
                }
                finally
                {
                    if (type != IntPtr.Zero) Marshal.FreeHGlobal(type);
                }
            }
            finally
            {
                if (elements != IntPtr.Zero) Marshal.FreeHGlobal(elements);
            }
        }

        public void PrepareCif(IntPtr cif, int abi, uint nargs, IntPtr rtype, IntPtr argTypes)
        {
            int status = _prepCif(cif, abi, nargs, rtype, argTypes);
            if (status != FfiOk)
                throw new FfiInvocationException("ffi_prep_cif failed with status " + status);
        }

        public void CallFunction(IntPtr cif, IntPtr fn, IntPtr rvalue, IntPtr avalues)
            => _call(cif, fn, rvalue, avalues);

        /// <summary>Allocates a closure; returns the writable address and the executable code address.</summary>
        public IntPtr ClosureAlloc(int size, out IntPtr code)
            => _closureAlloc(new UIntPtr((uint)size), out code);

        /// <summary>Prepares a closure at <paramref name="codeloc"/> to invoke <paramref name="fun"/>.</summary>
        public void PrepareClosure(IntPtr closure, IntPtr cif, IntPtr fun, IntPtr userData, IntPtr codeloc)
        {
            int status = _prepClosureLoc(closure, cif, fun, userData, codeloc);
            if (status != FfiOk)
                throw new FfiInvocationException("ffi_prep_closure_loc failed with status " + status);
        }

        public void FreeClosure(IntPtr writable) => _closureFree(writable);

        /// <summary>Builds a reusable call plan for a prepared cif (3.7.0+). Returns IntPtr.Zero on OOM.</summary>
        public IntPtr CreateCallPlan(IntPtr cif)
            => _callPlanAlloc != null ? _callPlanAlloc(cif) : IntPtr.Zero;

        /// <summary>Invokes a function through a reusable call plan (identical semantics to ffi_call).</summary>
        public void InvokeCallPlan(IntPtr plan, IntPtr fn, IntPtr rvalue, IntPtr avalues)
            => _callPlanInvoke(plan, fn, rvalue, avalues);

        /// <summary>Frees a reusable call plan. Passing IntPtr.Zero is harmless.</summary>
        public void FreeCallPlan(IntPtr plan)
        {
            if (plan != IntPtr.Zero && _callPlanFree != null)
                _callPlanFree(plan);
        }

        private static string PrimitiveSymbol(FfiPrimitive p)
        {
            switch (p)
            {
                case FfiPrimitive.Void: return "ffi_type_void";
                case FfiPrimitive.SChar: return "ffi_type_sint8";
                case FfiPrimitive.UChar: return "ffi_type_uint8";
                case FfiPrimitive.Short: return "ffi_type_sint16";
                case FfiPrimitive.UShort: return "ffi_type_uint16";
                case FfiPrimitive.Int: return "ffi_type_sint32";
                case FfiPrimitive.UInt: return "ffi_type_uint32";
                case FfiPrimitive.LongLong: return "ffi_type_sint64";
                case FfiPrimitive.ULongLong: return "ffi_type_uint64";
                case FfiPrimitive.Float: return "ffi_type_float";
                case FfiPrimitive.Double: return "ffi_type_double";
                // Char/Long/ULong are platform-dependent (resolved via FfiPlatform
                // in Phase 2); their fixed-width symbol depends on the ABI.
                case FfiPrimitive.Char:
                case FfiPrimitive.Long:
                case FfiPrimitive.ULong:
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Owns the native memory for a built aggregate <c>ffi_type</c>: the
    /// <c>ffi_type</c> struct itself and its NULL-terminated elements array. These
    /// must remain alive as long as any <c>ffi_cif</c>/call plan references the
    /// struct type.
    /// </summary>
    internal sealed class StructTypeHandle : IDisposable
    {
        private IntPtr _type;
        private IntPtr _elements;

        public StructTypeHandle(IntPtr type, IntPtr elements)
        {
            _type = type;
            _elements = elements;
            Ref = new FfiTypeRef(type, Marshal.PtrToStructure<FfiTypeNative>(type));
        }

        public FfiTypeRef Ref { get; }

        public void Dispose()
        {
            if (_elements != IntPtr.Zero) { Marshal.FreeHGlobal(_elements); _elements = IntPtr.Zero; }
            if (_type != IntPtr.Zero) { Marshal.FreeHGlobal(_type); _type = IntPtr.Zero; }
        }
    }
}
