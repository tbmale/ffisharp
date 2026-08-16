using System;
using System.Diagnostics;
using System.IO;

namespace FfiSharp.Benchmarks
{
    /// <summary>
    /// Minimal, dependency-free micro-benchmark harness. Measures mean ns/op and
    /// allocated bytes/op via GC.GetAllocatedBytesForCurrentThread across a fixed
    /// number of iterations. Not BenchmarkDotNet-grade, but sufficient to compare
    /// hot-path changes (A/B) without adding a runtime dependency.
    /// </summary>
    internal static class Program
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        private static int Main()
        {
            // Warm up libffi + JIT.
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                _ = ffi.add(1, 2);
            }

            Bench("int add(int,int)", () =>
            {
                using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
                {
                    int acc = 0;
                    for (int i = 0; i < 100000; i++) acc += ffi.add(i, i);
                    return acc;
                }
            });

            Bench("int add (non-dynamic)", () =>
            {
                using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
                {
                    NativeFunction add = lib.GetFunction("add");
                    int acc = 0;
                    for (int i = 0; i < 100000; i++) acc += Convert.ToInt32(add.Invoke(i, i));
                    return acc;
                }
            });

            Bench("int add (pre-boxed args)", () =>
            {
                using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
                {
                    NativeFunction add = lib.GetFunction("add");
                    object[] args = new object[2];
                    int acc = 0;
                    for (int i = 0; i < 100000; i++)
                    {
                        args[0] = i; args[1] = i;
                        acc += Convert.ToInt32(add.Invoke(args));
                    }
                    return acc;
                }
            });

            Bench("double add(double,double)", () =>
            {
                using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
                {
                    double acc = 0;
                    for (int i = 0; i < 100000; i++) acc += ffi.add_double(i, i + 0.5);
                    return (long)acc;
                }
            });

            Bench("pointer identity", () =>
            {
                using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
                {
                    IntPtr p = new IntPtr(0x1234);
                    for (int i = 0; i < 100000; i++) p = ffi.identity_ptr(p);
                    return (int)p;
                }
            });

            Bench("byte[] checksum", () =>
            {
                using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
                {
                    byte[] buf = new byte[16];
                    int acc = 0;
                    for (int i = 0; i < 100000; i++) acc += ffi.checksum(buf, buf.Length);
                    return acc;
                }
            });

            Bench("struct point_sum", () =>
            {
                using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
                {
                    FfiStruct p = lib.CreateStruct("Point");
                    p["x"] = 1; p["y"] = 2.5;
                    var fn = lib.GetFunction("point_sum");
                    double acc = 0;
                    for (int i = 0; i < 100000; i++) acc += Convert.ToDouble(fn.Invoke(p));
                    return (int)acc;
                }
            });

            Bench("callback (no throw)", () =>
            {
                using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
                {
                    lib.RegisterCallback("set_callback", (Action<int>)(_ => { }));
                    var fire = lib.GetFunction("fire_callback");
                    for (int i = 0; i < 100000; i++) fire.Invoke(i);
                    return 0;
                }
            });

            return 0;
        }

        private static void Bench(string name, Func<long> body)
        {
            body(); // warm-up once more
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            long sink = body();
            sw.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / 100000.0;
            double bytesPerOp = allocated / 100000.0;
            Console.WriteLine($"{name,-28} {nsPerOp,10:F1} ns/op   {bytesPerOp,8:F1} B/op   (sink={sink})");
        }
    }
}
