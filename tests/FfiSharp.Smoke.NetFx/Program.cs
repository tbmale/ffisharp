using System;
using System.IO;
using System.Runtime.InteropServices;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;

namespace FfiSharp.Smoke.NetFx
{
    /// <summary>
    /// Phase 1 smoke test for .NET Framework 4.7.2: proves the netstandard2.0 core
    /// (using the dlopen/LoadLibrary platform loader, not NativeLibrary) drives
    /// libffi correctly. Run with: mono FfiSharp.Smoke.NetFx.exe
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

            // Phase 3: dynamic dispatch through the parsed header (dynamic + DynamicObject).
            string headerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "example.h");
            using (dynamic ffi = Ffi.Load(examplePath, headerPath))
            {
                int sum = ffi.add(10, 20);
                Console.WriteLine("dynamic add(10, 20) = " + sum);
                if (sum != 30) return Fail("dynamic add");

                long lsum = ffi.add_long(10L, 20L);
                Console.WriteLine("dynamic add_long(10, 20) = " + lsum);
                if (lsum != 30L) return Fail("dynamic add_long");

                // Phase 4: struct by value (Win64 vs SysV ABI differences).
                FfiStruct p = ffi.make_point(3, 4.5);
                if ((int)p["x"] != 3 || Math.Abs((double)p["y"] - 4.5) > 1e-9) return Fail("struct return");
                ffi.mutate_point(p);
                if ((int)p["x"] != 4 || Math.Abs((double)p["y"] - 5.5) > 1e-9) return Fail("struct mutate");
                Console.WriteLine("struct make_point/mutate_point = OK");

                // Phase 5: callback through a libffi closure.
                int captured = 0;
                ffi.invoke_callback((Action<int>)(v => captured = v), 42);
                if (captured != 42) return Fail("callback");
                Console.WriteLine("callback invoke_callback = OK");

                // Phase 6: const char* string round-trip.
                string name = ffi.get_name();
                if (name != "Hello from C") return Fail("string return");
                int len = ffi.cstrlen("hello");
                if (len != 5) return Fail("string argument");
                Console.WriteLine("string get_name/cstrlen = OK");
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
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string fileName = windows ? "example.dll" : "example.so";

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate = Path.Combine(exeDir, fileName);
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(fileName)) return Path.GetFullPath(fileName);
            throw new FileNotFoundException(
                fileName + " not found. Build it first with tests/native/build.sh (Linux) or build-win.sh (Windows).");
        }
    }
}
