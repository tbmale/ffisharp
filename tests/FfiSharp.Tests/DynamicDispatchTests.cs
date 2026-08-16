using System;
using System.IO;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 3 — dynamic and non-dynamic dispatch driven by a parsed header:
    /// <c>dynamic ffi = Ffi.Load(...); int r = ffi.add(10, 20);</c>
    /// </summary>
    public class DynamicDispatchTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        [Fact]
        public void DynamicAdd()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int result = ffi.add(10, 20);
                Assert.Equal(30, result);
            }
        }

        [Fact]
        public void DynamicMultiply()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                double result = ffi.multiply(2.5, 4.0);
                Assert.Equal(10.0, result, 10);
            }
        }

        [Fact]
        public void DynamicLongAndTypedef()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                long l = ffi.add_long(10L, 20L);
                Assert.Equal(30L, l);

                // uint64_t is a built-in typedef resolved by the type system.
                ulong u = ffi.add_u64(10UL, 20UL);
                Assert.Equal(30UL, u);
            }
        }

        [Fact]
        public void DynamicPointerIdentity()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                IntPtr sentinel = new IntPtr(0x12345678);
                IntPtr result = ffi.identity_ptr(sentinel);
                Assert.Equal(sentinel, result);
            }
        }

        [Fact]
        public void NonDynamicApi()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                NativeFunction add = lib.GetFunction("add");
                object result = add.Invoke(10, 20);
                Assert.Equal(30, Convert.ToInt32(result));
            }
        }

        [Fact]
        public void MissingFunctionThrows()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                Assert.ThrowsAny<FfiException>(() => ffi.does_not_exist(1, 2));
            }
        }

        [Fact]
        public void CustomTypeAlias()
        {
            // A header uses a made-up typedef resolved via FfiLoadOptions.TypeAliases;
            // the declared function symbol ('add_long') really exists in example.so.
            string aliasHeader = Path.Combine(Path.GetTempPath(), "ffisharp_alias_" + Guid.NewGuid().ToString("N") + ".h");
            File.WriteAllText(aliasHeader, "mylong_t add_long(mylong_t a, mylong_t b);\n");
            try
            {
                var options = new FfiLoadOptions
                {
                    TypeAliases = new System.Collections.Generic.Dictionary<string, string> { { "mylong_t", "long" } }
                };

                using (dynamic ffi = Ffi.Load(ExampleSo, aliasHeader, options))
                {
                    long result = ffi.add_long(10L, 20L);
                    Assert.Equal(30L, result);
                }
            }
            finally
            {
                File.Delete(aliasHeader);
            }
        }
    }
}
