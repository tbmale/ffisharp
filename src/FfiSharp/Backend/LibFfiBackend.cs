using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FfiSharp.Abi;
using FfiSharp.Bindings;
using FfiSharp.Interop;
using FfiSharp.Marshaling;

namespace FfiSharp.Backend
{
    /// <summary>
    /// <see cref="IFfiBackend"/> backed by libffi. All native ABI work — calling
    /// convention handling, register/stack argument placement, aggregate ABI rules,
    /// aggregate returns, trampolines — is delegated to libffi. This class never
    /// recomputes ABI details.
    /// </summary>
    public sealed class LibFfiBackend : IFfiBackend, IDisposable
    {
        // ffi_cif is an opaque, over-allocated native buffer. Its exact size varies
        // by target (FFI_EXTRA_CIF_FIELDS), so we never blit it as a managed struct.
        private const int CifBufferSize = 256;

        private readonly INativeLibrary _ffiLib;
        private readonly LibFfiNative _ffi;
        private readonly NativeTypeResolver _nativeResolver;
        private readonly FfiMarshaller _marshaller;
        private bool _disposed;

        public int DefaultAbi => _ffi.DefaultAbi;
        public ulong LibFfiVersion => _ffi.VersionNumber;

        /// <summary>The target ABI configuration this backend is operating against.</summary>
        public FfiPlatform Platform { get; }

        /// <summary>Canonical type cache and typedef resolution.</summary>
        public FfiTypeSystem Types { get; }

        public LibFfiBackend(string libFfiPath = null, FfiPlatform platform = null, StringEncoding stringEncoding = StringEncoding.Utf8)
        {
            _ffiLib = LoadLibFfi(libFfiPath);
            _ffi = new LibFfiNative(_ffiLib);
            _nativeResolver = new NativeTypeResolver(_ffi);
            Platform = platform ?? FfiPlatform.Detect();
            Types = new FfiTypeSystem(Platform, ResolvePrimitive, ResolvePointer);
            _marshaller = new FfiMarshaller(Platform, stringEncoding);
        }

        /// <summary>Returns the canonical primitive type, resolving platform-dependent kinds.</summary>
        public FfiPrimitiveType CreatePrimitiveType(FfiPrimitive primitive)
        {
            ThrowIfDisposed();
            return Types.GetPrimitive(primitive);
        }

        /// <summary>Returns the canonical pointer type for a pointee type.</summary>
        public FfiPointerType CreatePointerType(FfiType pointee)
        {
            ThrowIfDisposed();
            return Types.GetPointer(pointee);
        }

        /// <summary>
        /// Creates a native callable function pointer (a libffi closure) that
        /// dispatches to the given managed delegate using <paramref name="signature"/>.
        /// </summary>
        internal FfiCallback CreateCallback(FfiFunctionType signature, Delegate callback, CallbackExceptionPolicy policy)
        {
            ThrowIfDisposed();
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var cb = new FfiCallback(this, signature, callback, policy, _marshaller);
            try
            {
                FfiCallPlan plan = CreateCallPlan(signature.CallingConvention, signature.ReturnType, signature.ParameterTypes);
                IntPtr code;
                IntPtr writable = _ffi.ClosureAlloc(_ffi.ClosureSize, out code);
                cb.Attach(plan, writable, code);
                _ffi.PrepareClosure(writable, plan.Cif, FfiCallback.ThunkPointer, cb.UserData, code);
                return cb;
            }
            catch
            {
                cb.Dispose();
                throw;
            }
        }

        internal void FreeClosure(IntPtr writable)
        {
            if (writable != IntPtr.Zero)
                _ffi.FreeClosure(writable);
        }

        private FfiPrimitiveType ResolvePrimitive(FfiPrimitive logical)
        {
            FfiPrimitive storage = Platform.ResolveStorage(logical);
            FfiTypeRef r = _ffi.GetPrimitiveType(storage);
            return new FfiPrimitiveType(logical, storage, r.Size, r.Alignment);
        }

        private FfiPointerType ResolvePointer(FfiType pointee, bool pointeeIsConst)
        {
            FfiTypeRef r = _ffi.GetPointerType();
            return new FfiPointerType(pointee, pointeeIsConst, r.Size, r.Alignment);
        }

        public FfiCallPlan CreateCallPlan(
            FfiCallingConvention callingConvention,
            FfiType returnType,
            IReadOnlyList<FfiType> argumentTypes)
        {
            ThrowIfDisposed();
            if (returnType == null) throw new ArgumentNullException(nameof(returnType));
            if (argumentTypes == null) throw new ArgumentNullException(nameof(argumentTypes));

            int n = argumentTypes.Count;
            IntPtr cif = Marshal.AllocHGlobal(CifBufferSize);
            IntPtr argTypesArray = Marshal.AllocHGlobal(Math.Max(n, 1) * IntPtr.Size);
            try
            {
                // ffi_cif has platform-specific trailing fields; zero them so the
                // layout is deterministic before libffi writes its defined fields.
                Marshal.Copy(new byte[CifBufferSize], 0, cif, CifBufferSize);

                IntPtr rtypePtr = _nativeResolver.Resolve(returnType).Pointer;
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = _nativeResolver.Resolve(argumentTypes[i]).Pointer;
                    Marshal.WriteIntPtr(argTypesArray, i * IntPtr.Size, p);
                }

                int abi = ToNativeAbi(callingConvention);
                _ffi.PrepareCif(cif, abi, (uint)n, rtypePtr, argTypesArray);

                var plan = new FfiCallPlan(cif, argTypesArray, returnType, argumentTypes);
                cif = IntPtr.Zero;
                argTypesArray = IntPtr.Zero;
                return plan;
            }
            finally
            {
                if (cif != IntPtr.Zero) Marshal.FreeHGlobal(cif);
                if (argTypesArray != IntPtr.Zero) Marshal.FreeHGlobal(argTypesArray);
            }
        }

        public object Invoke(FfiCallPlan plan, IntPtr function, object[] arguments)
        {
            ThrowIfDisposed();
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (function == IntPtr.Zero) throw new ArgumentNullException(nameof(function));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));

            int n = plan.ArgumentTypes.Count;
            if (arguments.Length != n)
                throw new ArgumentException($"Expected {n} arguments but got {arguments.Length}.");

            MarshalledValue[] marshalled = new MarshalledValue[n];
            IntPtr avalues = Marshal.AllocHGlobal(Math.Max(n, 1) * IntPtr.Size);
            try
            {
                for (int i = 0; i < n; i++)
                {
                    marshalled[i] = _marshaller.MarshalArgument(plan.ArgumentTypes[i], arguments[i]);
                    Marshal.WriteIntPtr(avalues, i * IntPtr.Size, marshalled[i].Pointer);
                }

                int returnSize = Math.Max(plan.ReturnType.Size, IntPtr.Size);
                IntPtr rvalue = Marshal.AllocHGlobal(returnSize);
                try
                {
                    _ffi.CallFunction(plan.Cif, function, rvalue, avalues);
                    return _marshaller.MarshalReturn(plan.ReturnType, rvalue);
                }
                finally
                {
                    Marshal.FreeHGlobal(rvalue);
                }
            }
            finally
            {
                for (int i = 0; i < n; i++)
                    marshalled[i]?.Release();
                Marshal.FreeHGlobal(avalues);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativeResolver.Dispose();
            _ffiLib.Dispose();
        }

        private int ToNativeAbi(FfiCallingConvention cc)
        {
            // On 64-bit targets and most platforms there is a single calling
            // convention, so cdecl and stdcall are equivalent. Map to the platform
            // default ABI for now; 32-bit x86 stdcall support lands in Phase 7.
            return _ffi.DefaultAbi;
        }

        private static INativeLibrary LoadLibFfi(string path)
        {
            if (!string.IsNullOrEmpty(path))
                return PlatformNativeLibrary.Load(path);

            string last = null;

            // Prefer a vendored libffi sitting next to the loaded assembly. This is
            // essential on Linux/macOS, where dlopen does NOT search the application
            // directory (Windows' LoadLibrary does). The build copies
            // runtimes/<rid>/native/ files flat into the output directory.
            foreach (string candidate in LocalCandidatePaths())
            {
                try { return PlatformNativeLibrary.Load(candidate); }
                catch (NativeLibraryLoadException ex) { last = ex.Message; }
            }

            // Fall back to the system-installed libffi by SONAME.
            foreach (string candidate in CandidateNames())
            {
                try { return PlatformNativeLibrary.Load(candidate); }
                catch (NativeLibraryLoadException ex) { last = ex.Message; }
            }

            throw new NativeLibraryLoadException(
                "Failed to load libffi (tried local and system candidates): " + last);
        }

        /// <summary>
        /// Absolute paths of a vendored libffi under the application's base
        /// directory, using the platform-appropriate file names.
        /// </summary>
        private static string[] LocalCandidatePaths()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string[] names = CandidateNames();
            var paths = new List<string>(names.Length);
            foreach (string name in names)
            {
                // Skip entries that are already absolute (macOS Homebrew fallbacks).
                if (Path.IsPathRooted(name)) continue;
                paths.Add(Path.Combine(dir, name));
            }
            return paths.ToArray();
        }

        private static string[] CandidateNames()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new[] { "libffi.dll", "libffi-8.dll", "libffi-7.dll", "libffi-6.dll" };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new[]
                {
                    "libffi.dylib", "libffi.8.dylib",
                    "/opt/homebrew/opt/libffi/lib/libffi.dylib",
                    "/usr/local/opt/libffi/lib/libffi.dylib"
                };
            return new[] { "libffi.so.8", "libffi.so.7", "libffi.so.6", "libffi.so" };
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LibFfiBackend));
        }
    }
}
