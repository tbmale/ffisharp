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

        // libffi ABI ids for 32-bit x86 (src/x86/ffitarget.h). These are the only
        // targets where cdecl and stdcall are distinct conventions:
        //   win32: FFI_STDCALL = 2, FFI_MS_CDECL = 5 (default = MS_CDECL)
        //   unix:  FFI_STDCALL = 5, FFI_SYSV     = 1 (default = SYSV)
        private const int FfiAbiX86Win32Stdcall = 2;
        private const int FfiAbiX86UnixStdcall = 5;

        private readonly INativeLibrary _ffiLib;
        private readonly LibFfiNative _ffi;
        private readonly NativeTypeResolver _nativeResolver;
        private readonly FfiMarshaller _marshaller;
        private readonly bool _ownsLibFfi;
        private readonly OperationLifetime _lifetime = new OperationLifetime();
        private readonly object _disposeSync = new object();
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
            _ownsLibFfi = true;
            _ffi = new LibFfiNative(_ffiLib);
            _nativeResolver = new NativeTypeResolver(_ffi);
            Platform = platform ?? FfiPlatform.Detect();
            Types = new FfiTypeSystem(Platform, ResolvePrimitive, ResolvePointer);
            _marshaller = new FfiMarshaller(Platform, stringEncoding);
        }

        /// <summary>
        /// Creates a backend over an already-loaded libffi instance. The caller
        /// retains ownership of <paramref name="ffiLibrary"/> and is responsible for
        /// disposing it; this backend will NOT dispose it. This is the escape hatch
        /// for supplying a pre-loaded libffi handle (e.g. a native handle you loaded
        /// yourself and wrapped in an <see cref="INativeLibrary"/>).
        /// </summary>
        public LibFfiBackend(INativeLibrary ffiLibrary, FfiPlatform platform = null, StringEncoding stringEncoding = StringEncoding.Utf8)
        {
            _ffiLib = ffiLibrary ?? throw new ArgumentNullException(nameof(ffiLibrary));
            _ownsLibFfi = false;
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
        internal FfiCallback CreateCallback(FfiFunctionType signature, Delegate callback, CallbackExceptionPolicy policy, CallbackPendingFlag pendingFlag = null)
        {
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (!_lifetime.TryEnter())
                throw new ObjectDisposedException(nameof(LibFfiBackend));

            try
            {
                var cb = new FfiCallback(this, signature, callback, policy, _marshaller, pendingFlag);
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
            finally
            {
                _lifetime.Exit();
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
            if (returnType == null) throw new ArgumentNullException(nameof(returnType));
            if (argumentTypes == null) throw new ArgumentNullException(nameof(argumentTypes));
            if (!_lifetime.TryEnter())
                throw new ObjectDisposedException(nameof(LibFfiBackend));

            try
            {
                int n = argumentTypes.Count;
                int argArrayBytes = CheckedArithmetic.Multiply(Math.Max(n, 1), IntPtr.Size);
                IntPtr cif = Marshal.AllocHGlobal(CifBufferSize);
                IntPtr argTypesArray = Marshal.AllocHGlobal(argArrayBytes);
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

                    // Optional reusable call-plan fast path (libffi 3.7.0+). Falls back
                    // to ffi_call transparently when the API is unavailable.
                    if (_ffi.HasCallPlanApi)
                    {
                        IntPtr fast = _ffi.CreateCallPlan(plan.Cif);
                        if (fast != IntPtr.Zero)
                            plan.AttachFastPlan(fast, _ffi.FreeCallPlan);
                    }

                    return plan;
                }
                finally
                {
                    if (cif != IntPtr.Zero) Marshal.FreeHGlobal(cif);
                    if (argTypesArray != IntPtr.Zero) Marshal.FreeHGlobal(argTypesArray);
                }
            }
            finally
            {
                _lifetime.Exit();
            }
        }

        public object Invoke(FfiCallPlan plan, IntPtr function, object[] arguments)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (function == IntPtr.Zero) throw new ArgumentNullException(nameof(function));
            if (arguments == null) throw new ArgumentNullException(nameof(arguments));
            if (!_lifetime.TryEnter())
                throw new ObjectDisposedException(nameof(LibFfiBackend));

            // Reusable, thread-local scratch storage. Nested/reentrant calls each
            // acquire their own frame, so this frame stays valid for the whole call
            // even if a callback re-enters FfiSharp on the same thread.
            InvocationFrame frame = InvocationFrames.Acquire();
            try
            {
                int n = plan.ArgumentTypes.Count;
                if (arguments.Length != n)
                    throw new ArgumentException($"Expected {n} arguments but got {arguments.Length}.");

                frame.EnsureCapacity(n, plan.ReturnType.Size);

                if (plan.IsPrimitiveOnly)
                    _marshaller.MarshalPrimitiveArguments(frame, plan.ArgumentTypes, arguments);
                else
                    _marshaller.MarshalArguments(frame, plan.ArgumentTypes, arguments);

                if (plan.HasFastPlan)
                    _ffi.InvokeCallPlan(plan.FastPlan, function, frame.ReturnBuffer, frame.Avalues);
                else
                    _ffi.CallFunction(plan.Cif, function, frame.ReturnBuffer, frame.Avalues);

                return _marshaller.MarshalReturn(plan.ReturnType, frame.ReturnBuffer);
            }
            finally
            {
                // Copy-back + free happen here, before the frame is released for reuse.
                _marshaller.Cleanup(frame);
                InvocationFrames.Release(frame);
                _lifetime.Exit();
            }
        }

        public void Dispose()
        {
            // Reject new operations and wait for in-flight native invocations /
            // plan creation to finish before unloading libffi.
            _lifetime.Close();

            lock (_disposeSync)
            {
                if (_disposed) return;
                _disposed = true;
            }

            _nativeResolver.Dispose();
            if (_ownsLibFfi)
                _ffiLib.Dispose();
        }

        private int ToNativeAbi(FfiCallingConvention cc)
        {
            return ResolveNativeAbi(cc, Platform, _ffi.DefaultAbi);
        }

        /// <summary>
        /// Maps a logical calling convention to a libffi ABI id. On 32-bit x86,
        /// <c>cdecl</c> and <c>stdcall</c> are distinct ABIs; everywhere else
        /// (x64, arm64) there is a single convention and both map to the default.
        /// </summary>
        internal static int ResolveNativeAbi(FfiCallingConvention cc, FfiPlatform platform, int defaultAbi)
        {
            if (platform.Architecture == FfiArchitecture.X86)
            {
                if (cc == FfiCallingConvention.Stdcall)
                {
                    // FFI_STDCALL differs between win32 (2) and Unix x86 (5).
                    return platform.OS == FfiOS.Windows
                        ? FfiAbiX86Win32Stdcall
                        : FfiAbiX86UnixStdcall;
                }
                // cdecl == default ABI (FFI_MS_CDECL on win32, FFI_SYSV on Unix x86).
                return defaultAbi;
            }

            return defaultAbi;
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
