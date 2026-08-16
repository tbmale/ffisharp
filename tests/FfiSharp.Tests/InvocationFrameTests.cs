using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Exercises the reusable InvocationFrame under nesting: a managed callback that
    /// re-enters FfiSharp (a nested native call on the same thread) must not corrupt
    /// the outer invocation's frame.
    /// </summary>
    [Collection("callback-global")]
    public class InvocationFrameTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        [Fact]
        public void CallbackReentersNativeFunction()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int result = 0;
                // invoke_callback calls the managed delegate; the delegate then makes
                // a nested FfiSharp call on the same thread (frame nesting).
                ffi.invoke_callback((Action<int>)(v =>
                {
                    int nested = ffi.add(v, 1);
                    result = nested;
                }), 41);

                Assert.Equal(42, result);
            }
        }

        [Fact]
        public void DeepCallbackReentry()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                // Two levels: callback -> nested native -> callback -> nested native.
                int final = 0;
                ffi.invoke_callback((Action<int>)(v1 =>
                {
                    ffi.invoke_callback((Action<int>)(v2 =>
                    {
                        final = ffi.add(v2, 1);
                    }), v1 + 1);
                }), 39);

                Assert.Equal(41, final);
            }
        }

        [Fact]
        public void ZeroAndManyArgumentCalls()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                // void(void) — get_name takes no args.
                string name = Convert.ToString(lib.GetFunction("get_name").Invoke());
                Assert.Equal("Hello from C", name);

                // add_ll(a,b,c,d,...) via a many-arg primitive call.
                NativeFunction add = lib.GetFunction("add");
                Assert.Equal(30, Convert.ToInt32(add.Invoke(10, 20)));
            }
        }
    }
}
