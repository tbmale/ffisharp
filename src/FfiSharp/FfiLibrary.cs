using System;
using System.Collections.Generic;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;
using FfiSharp.Parsing;

namespace FfiSharp
{
    /// <summary>
    /// A loaded native library + parsed header. Owns the header model, the native
    /// library handle, the libffi backend, the type resolver, and the function
    /// binding cache. Disposing it releases all native resources.
    /// </summary>
    public sealed class FfiLibrary : IDisposable
    {
        private readonly INativeLibrary _nativeLib;
        private readonly LibFfiBackend _backend;
        private readonly HeaderModel _header;
        private readonly CTypeResolver _resolver;
        private readonly CallbackRegistry _callbacks;
        private readonly Dictionary<string, NativeFunctionBinding> _functions =
            new Dictionary<string, NativeFunctionBinding>(StringComparer.Ordinal);
        private readonly object _sync = new object();
        private bool _disposed;
        private bool _resourcesReleased;

        private FfiLibrary(
            INativeLibrary nativeLib,
            LibFfiBackend backend,
            HeaderModel header,
            CTypeResolver resolver,
            CallbackExceptionPolicy callbackPolicy)
        {
            _nativeLib = nativeLib;
            _backend = backend;
            _header = header;
            _resolver = resolver;
            _callbacks = new CallbackRegistry(backend, callbackPolicy);
        }

        /// <summary>The target ABI configuration.</summary>
        public FfiPlatform Platform => _backend.Platform;

        /// <summary>The parsed header model (functions + typedefs).</summary>
        internal HeaderModel Header => _header;

        public static FfiLibrary Load(string libraryPath, string headerPath, FfiLoadOptions options = null)
        {
            if (string.IsNullOrEmpty(libraryPath)) throw new ArgumentNullException(nameof(libraryPath));
            if (string.IsNullOrEmpty(headerPath)) throw new ArgumentNullException(nameof(headerPath));
            options = options ?? new FfiLoadOptions();

            string source = File.ReadAllText(headerPath);
            HeaderModel header = CParser.Parse(source);

            INativeLibrary nativeLib = PlatformNativeLibrary.Load(libraryPath);
            LibFfiBackend backend;
            try
            {
                backend = new LibFfiBackend(options.LibFfiPath, options.Platform, options.StringEncoding);
            }
            catch
            {
                nativeLib.Dispose();
                throw;
            }

            var resolver = new CTypeResolver(backend.Types, header, options.TypeAliases);
            return new FfiLibrary(nativeLib, backend, header, resolver, options.CallbackExceptionPolicy);
        }

        /// <summary>Returns a bound native function by name (throws if unknown/missing).</summary>
        public NativeFunction GetFunction(string name)
        {
            ThrowIfDisposed();
            return new NativeFunction(GetBinding(name));
        }

        /// <summary>Returns the resolved struct type by typedef name or tag.</summary>
        public FfiStructType GetStructType(string name)
        {
            ThrowIfDisposed();
            return _resolver.ResolveStructByName(name);
        }

        /// <summary>Creates an empty boxed struct value for a struct type name.</summary>
        public FfiStruct CreateStruct(string name)
        {
            ThrowIfDisposed();
            return new FfiStruct(GetStructType(name));
        }

        /// <summary>
        /// Registers a managed callback for a native function whose single parameter
        /// is a function pointer, and invokes it with the new closure's pointer.
        /// Returns a handle that owns the closure until disposed.
        /// </summary>
        public CallbackHandle RegisterCallback(string functionName, Delegate callback)
        {
            ThrowIfDisposed();
            if (functionName == null) throw new ArgumentNullException(nameof(functionName));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            _callbacks.ThrowPendingExceptions();

            FunctionDeclaration fn = _header.FindFunction(functionName);
            if (fn == null)
                throw new MissingSymbolException(functionName + " (not declared in header)");
            if (fn.Parameters.Count != 1)
                throw new FfiException("RegisterCallback requires a function with exactly one parameter");

            FfiType paramType = _resolver.Resolve(fn.Parameters[0].Type);
            if (!(paramType is FfiFunctionType ft))
                throw new FfiException("RegisterCallback requires a function whose parameter is a function pointer");

            FfiCallback cb = _callbacks.Create(ft, callback);
            try
            {
                GetBinding(functionName).Invoke(new object[] { cb.FunctionPointer });
            }
            catch
            {
                cb.Dispose();
                _callbacks.Remove(cb);
                throw;
            }

            return new CallbackHandle(_callbacks, cb);
        }

        /// <summary>Invokes a function by name.</summary>
        internal object Invoke(string name, object[] arguments)
        {
            ThrowIfDisposed();
            return GetBinding(name).Invoke(arguments);
        }

        private NativeFunctionBinding GetBinding(string name)
        {
            lock (_sync)
            {
                // Re-check disposal under the lock: closes the TOCTOU window where
                // ThrowIfDisposed() passed but Dispose() ran before this lock was
                // acquired (which would unload the native library under us).
                if (_disposed)
                    throw new ObjectDisposedException(nameof(FfiLibrary));

                if (_functions.TryGetValue(name, out NativeFunctionBinding binding))
                    return binding;

                FunctionDeclaration fn = _header.FindFunction(name);
                if (fn == null)
                    throw new MissingSymbolException(name + " (not declared in header)");

                FfiType returnType = _resolver.Resolve(fn.ReturnType);
                var argumentTypes = new List<FfiType>(fn.Parameters.Count);
                for (int i = 0; i < fn.Parameters.Count; i++)
                    argumentTypes.Add(_resolver.Resolve(fn.Parameters[i].Type));

                IntPtr address = ResolveSymbol(name, fn, argumentTypes);

                binding = new NativeFunctionBinding(
                    _backend, name, address, returnType, argumentTypes, fn.CallingConvention, CreateCallback,
                    _callbacks.ThrowPendingExceptions);
                _functions[name] = binding;
                return binding;
            }
        }

        /// <summary>
        /// Resolves a function's native symbol, handling 32-bit Windows stdcall name
        /// decoration (<c>name@N</c> / <c>_name@N</c>, where N is the argument byte
        /// count). Other conventions/ABIs export undecorated names.
        /// </summary>
        private IntPtr ResolveSymbol(string name, FunctionDeclaration fn, IReadOnlyList<FfiType> argumentTypes)
        {
            IntPtr address = _nativeLib.GetSymbol(name);
            if (address != IntPtr.Zero)
                return address;

            if (fn.CallingConvention == FfiCallingConvention.Stdcall
                && Platform.OS == FfiOS.Windows
                && Platform.Architecture == FfiArchitecture.X86)
            {
                int argBytes = StdcallArgumentBytes(argumentTypes);
                // mingw exports `name@N`; MSVC exports `_name@N`. Try both.
                foreach (string decorated in new[] { name + "@" + argBytes, "_" + name + "@" + argBytes })
                {
                    address = _nativeLib.GetSymbol(decorated);
                    if (address != IntPtr.Zero)
                        return address;
                }
            }

            throw new MissingSymbolException(name);
        }

        /// <summary>
        /// Total argument bytes for stdcall name decoration: each argument's size
        /// rounded up to a 4-byte stack slot, using checked arithmetic. (Hidden
        /// struct-return pointers are not included — such functions need an explicit
        /// undecorated export; see README.)
        /// </summary>
        internal static int StdcallArgumentBytes(IReadOnlyList<FfiType> argumentTypes)
        {
            int total = 0;
            foreach (FfiType t in argumentTypes)
            {
                int size = Math.Max(t.Size, 1);
                // Round each argument up to a 4-byte stack slot, using checked
                // arithmetic so an absurd argument size cannot wrap the decoration.
                int slot = (size + 3) & ~3;
                total = CheckedArithmetic.Add(total, slot);
            }
            return total;
        }

        private FfiCallback CreateCallback(FfiFunctionType signature, Delegate callback)
            => _callbacks.Create(signature, callback);

        public void Dispose()
        {
            NativeFunctionBinding[] bindings = null;
            lock (_sync)
            {
                if (_resourcesReleased) return;
                _disposed = true;

                // Reentrancy guard: disposing from within one of this library's own
                // callbacks (reached via ffi_call on this thread) would deadlock
                // draining the in-flight binding and could unload the DLL while its
                // code is still on the stack. Defer; a later non-reentrant Dispose()
                // completes the release.
                if (CallbackContext.Depth > 0)
                    return;

                bindings = new NativeFunctionBinding[_functions.Count];
                _functions.Values.CopyTo(bindings, 0);
                _functions.Clear();
                _resourcesReleased = true;
            }

            if (bindings == null)
                return; // deferred (reentrant)

            // Dispose bindings first: each drains its own in-flight invocations (via
            // its operation lease), which also ensures the target library's function
            // pointers are no longer being called through ffi_call. Then release
            // callbacks, the libffi backend, and finally unload the target library.
            foreach (NativeFunctionBinding binding in bindings)
                binding.Dispose();

            _callbacks.Dispose();
            _backend.Dispose();
            _nativeLib.Dispose();
        }

        private void ThrowIfDisposed()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(FfiLibrary));
            }
        }
    }
}
