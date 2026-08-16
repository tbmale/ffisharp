using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;

namespace FfiSharp.Smoke
{
    /// <summary>
    /// Phase 1 smoke test: proves that we can load libffi, build an ffi_cif,
    /// resolve native test functions, and call int add(int,int) plus
    /// double multiply(double,double) entirely through libffi.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            string examplePath = FindExampleLibrary();
            Console.WriteLine("example library: " + examplePath);

            using (PlatformNativeLibrary example = PlatformNativeLibrary.Load(examplePath))
            using (LibFfiBackend backend = new LibFfiBackend())
            {
                Console.WriteLine("libffi version: " + backend.LibFfiVersion);

                IntPtr add = example.GetSymbolOrThrow("add");
                IntPtr multiply = example.GetSymbolOrThrow("multiply");

                FfiType intType = backend.CreatePrimitiveType(FfiPrimitive.Int);
                FfiType doubleType = backend.CreatePrimitiveType(FfiPrimitive.Double);

                using (FfiCallPlan addPlan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, intType, new[] { intType, intType }))
                {
                    object r = backend.Invoke(addPlan, add, new object[] { 10, 20 });
                    Console.WriteLine("add(10, 20) = " + r);
                    if (Convert.ToInt32(r) != 30) return Fail("add");
                }

                using (FfiCallPlan mulPlan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, doubleType, new[] { doubleType, doubleType }))
                {
                    object r = backend.Invoke(mulPlan, multiply, new object[] { 2.5, 4.0 });
                    Console.WriteLine("multiply(2.5, 4.0) = " + r);
                    if (Math.Abs(Convert.ToDouble(r) - 10.0) > 1e-9) return Fail("multiply");
                }
            }

            Console.WriteLine("OK");
            return 0;
        }

        private static int Fail(string name)
        {
            Console.Error.WriteLine("FAIL: " + name);
            return 1;
        }

        private static string FindExampleLibrary()
        {
            string exeDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(exeDir, "example.so");
            if (File.Exists(candidate)) return candidate;

            // Fallback: relative to the project directory (e.g. `dotnet run` cwd).
            if (File.Exists("example.so")) return Path.GetFullPath("example.so");

            throw new FileNotFoundException(
                "example.so not found. Build it first with tests/native/build.sh.");
        }
    }
}
