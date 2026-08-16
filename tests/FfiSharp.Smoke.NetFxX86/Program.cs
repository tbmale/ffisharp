using System;
using System.IO;
using FfiSharp;

namespace FfiSharp.Smoke.NetFxX86
{
    /// <summary>
    /// 32-bit x86 smoke test (Windows .NET Framework under Wine). Validates the
    /// distinct calling conventions on x86: cdecl and __stdcall map to different
    /// libffi ABIs, so this proves the calling-convention → ABI mapping end-to-end.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string lib = Path.Combine(dir, "example-x86.dll");
            string header = Path.Combine(dir, "example.h");

            Console.WriteLine("example library: " + lib);

            using (dynamic ffi = Ffi.Load(lib, header))
            {
                // cdecl
                int sum = ffi.add(10, 20);
                Console.WriteLine("cdecl add(10, 20) = " + sum);
                if (sum != 30) return Fail("cdecl add");

                // stdcall (distinct ABI on x86)
                int ssum = ffi.add_stdcall(4, 5);
                Console.WriteLine("stdcall add_stdcall(4, 5) = " + ssum);
                if (ssum != 9) return Fail("stdcall add_stdcall");

                // struct by value (x86 uses a hidden pointer for large structs)
                FfiStruct p = ffi.make_point(3, 4.5);
                if ((int)p["x"] != 3 || Math.Abs((double)p["y"] - 4.5) > 1e-9) return Fail("struct return");
                ffi.mutate_point(p);
                if ((int)p["x"] != 4 || Math.Abs((double)p["y"] - 5.5) > 1e-9) return Fail("struct mutate");
                Console.WriteLine("struct make_point/mutate_point = OK");

                // callback (stdcall/cdecl closure on x86)
                int captured = 0;
                ffi.invoke_callback((Action<int>)(v => captured = v), 42);
                if (captured != 42) return Fail("callback");
                Console.WriteLine("callback invoke_callback = OK");
            }

            Console.WriteLine("OK");
            return 0;
        }

        private static int Fail(string name)
        {
            Console.Error.WriteLine("FAIL: " + name);
            return 1;
        }
    }
}
