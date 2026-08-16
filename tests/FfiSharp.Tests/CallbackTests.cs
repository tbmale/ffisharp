using System;
using System.IO;
using System.Runtime.InteropServices;
using FfiSharp.Parsing;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 5 — callbacks: function-pointer typedefs/parameters, libffi closures,
    /// callback lifetime, and exception handling.
    /// </summary>
    [Collection("callback-global")]
    public class CallbackTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        // ---------------------------------------------------------------- parsing

        [Fact]
        public void ParsesFunctionPointerTypedef()
        {
            var model = CParser.Parse("typedef void (*Callback)(int value);");
            Assert.Single(model.Typedefs);
            Assert.True(model.Typedefs.ContainsKey("Callback"));
            Assert.IsType<CFunctionPointerTypeNode>(model.Typedefs["Callback"]);
        }

        [Fact]
        public void ParsesComparatorTypedef()
        {
            var model = CParser.Parse("typedef int (*Comparator)(const void* a, const void* b);");
            var fp = (CFunctionPointerTypeNode)model.Typedefs["Comparator"];
            Assert.Equal(2, fp.Parameters.Count);
        }

        [Fact]
        public void ParsesDirectFunctionPointerParameter()
        {
            var model = CParser.Parse("void invoke_callback_ex(void (*callback)(int), int value);");
            var f = model.Functions[0];
            Assert.IsType<CFunctionPointerTypeNode>(f.Parameters[0].Type);
            Assert.Equal(2, f.Parameters.Count);
        }

        // ---------------------------------------------------------------- invocation

        [Fact]
        public void CallbackInvokedViaTypedef()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int captured = 0;
                ffi.invoke_callback((Action<int>)(v => captured = v), 42);
                Assert.Equal(42, captured);
            }
        }

        [Fact]
        public void CallbackInvokedViaDirectFunctionPointer()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int captured = 0;
                ffi.invoke_callback_ex((Action<int>)(v => captured = v), 7);
                Assert.Equal(7, captured);
            }
        }

        [Fact]
        public void StoredCallbackFiresLater()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int captured = 0;
                ffi.set_callback((Action<int>)(v => captured = v));
                ffi.fire_callback(99);
                Assert.Equal(99, captured);
            }
        }

        [Fact]
        public void ComparatorCallbackWithPointers()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                // cmp returns (a - b); sort2 stores the smaller value.
                Func<IntPtr, IntPtr, int> cmp = (a, b) =>
                {
                    int av = Marshal.ReadInt32(a);
                    int bv = Marshal.ReadInt32(b);
                    return av - bv;
                };

                IntPtr result = Marshal.AllocHGlobal(4);
                try
                {
                    ffi.sort2(5, 3, cmp, result);
                    Assert.Equal(3, Marshal.ReadInt32(result));
                }
                finally
                {
                    Marshal.FreeHGlobal(result);
                }
            }
        }

        [Fact]
        public void CallbackHandleLifetime()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                int captured = 0;
                CallbackHandle handle = lib.RegisterCallback("set_callback", (Action<int>)(v => captured = v));
                Assert.NotEqual(IntPtr.Zero, handle.FunctionPointer);

                lib.GetFunction("fire_callback").Invoke(5);
                Assert.Equal(5, captured);

                handle.Dispose();
                // After disposal the closure is freed; the library is also disposed at
                // the end of the using block without double-freeing.
            }
        }

        [Fact]
        public void CallbackExceptionIsStored()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                CallbackHandle handle = lib.RegisterCallback(
                    "set_callback",
                    (Action<int>)(_ => throw new InvalidOperationException("boom")));

                // Default policy (Store): exception must NOT propagate through native.
                lib.GetFunction("fire_callback").Invoke(1);

                Assert.NotNull(handle.LastException);
                Assert.IsType<InvalidOperationException>(handle.LastException);
            }
        }

        [Fact]
        public void CallbackExceptionRethrowOnManagedBoundary()
        {
            var options = new FfiLoadOptions { CallbackExceptionPolicy = CallbackExceptionPolicy.RethrowOnManagedBoundary };
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH, options))
            {
                lib.RegisterCallback("set_callback", (Action<int>)(_ => throw new InvalidOperationException("boom")));

                // The callback fires and throws; it is captured, not propagated here.
                lib.GetFunction("fire_callback").Invoke(1);

                // Next managed call rethrows.
                Assert.Throws<FfiException>(() => lib.GetFunction("fire_callback").Invoke(1));
            }
        }

        [Fact]
        public void CallbackReturnValueIsMarshalled()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                // Comparator returns a value used by the native side.
                Func<IntPtr, IntPtr, int> cmp = (a, b) =>
                    Marshal.ReadInt32(a) - Marshal.ReadInt32(b);

                IntPtr result = Marshal.AllocHGlobal(4);
                try
                {
                    ffi.sort2(9, 4, cmp, result);
                    Assert.Equal(4, Marshal.ReadInt32(result));
                }
                finally
                {
                    Marshal.FreeHGlobal(result);
                }
            }
        }
    }
}
