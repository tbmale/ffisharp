using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Covers the "borrow an already-loaded libffi handle" escape hatch: the
    /// <see cref="LibFfiBackend(INativeLibrary, FfiPlatform, StringEncoding)"/>
    /// constructor.
    /// </summary>
    public class BackendEscapeHatchTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string LibFfiSo => Path.Combine(AppContext.BaseDirectory, "libffi.so.8");

        [Fact]
        public void BackendCanBorrowLoadedLibFfiHandle()
        {
            using (PlatformNativeLibrary ffiLib = PlatformNativeLibrary.Load(LibFfiSo))
            using (LibFfiBackend backend = new LibFfiBackend(ffiLib))
            using (PlatformNativeLibrary example = PlatformNativeLibrary.Load(ExampleSo))
            {
                IntPtr add = example.GetSymbolOrThrow("add");
                FfiType intType = backend.CreatePrimitiveType(FfiPrimitive.Int);
                using (FfiCallPlan plan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, intType, new[] { intType, intType }))
                {
                    int result = Convert.ToInt32(backend.Invoke(plan, add, new object[] { 10, 20 }));
                    Assert.Equal(30, result);
                }
            }
        }

        [Fact]
        public void BorrowedHandleIsNotDisposedByBackend()
        {
            PlatformNativeLibrary ffiLib = PlatformNativeLibrary.Load(LibFfiSo);
            var backend = new LibFfiBackend(ffiLib);

            backend.Dispose();

            // The borrowed handle must remain usable (the backend must NOT dispose it).
            IntPtr sym = ffiLib.GetSymbol("ffi_type_sint32");
            Assert.NotEqual(IntPtr.Zero, sym);

            ffiLib.Dispose();
        }
    }
}
