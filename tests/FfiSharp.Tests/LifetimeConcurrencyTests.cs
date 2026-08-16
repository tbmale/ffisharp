using System;
using System.IO;
using System.Threading.Tasks;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Resource-lifetime races: an in-flight native invocation must remain safe while
    /// a binding/backend/library/callback is concurrently disposed. Disposal must
    /// reject new operations, drain active ones, and only then release resources.
    /// </summary>
    public class LifetimeConcurrencyTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        [Fact]
        public void ConcurrentInvocationOfSameFunctionIsSafe()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                NativeFunction add = lib.GetFunction("add");
                Parallel.For(0, 1000, i =>
                {
                    Assert.Equal(30, Convert.ToInt32(add.Invoke(10, 20)));
                });
            }
        }

        [Fact]
        public void ConcurrentFirstInvocationIsSafe()
        {
            // Many threads race to first-use the same function, forcing concurrent
            // lazy binding + call-plan creation.
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                Parallel.For(0, 200, _ =>
                {
                    Assert.Equal(30, (int)ffi.add(10, 20));
                });
            }
        }

        [Fact]
        public async Task InvokeRacesLibraryDispose()
        {
            // binding.Dispose is what FfiLibrary.Dispose performs; racing Invoke
            // against library disposal therefore exercises the binding lease too.
            for (int round = 0; round < 40; round++)
            {
                FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH);
                NativeFunction add = lib.GetFunction("add");

                Exception disposeError = null;
                var disposer = Task.Run(() =>
                {
                    try { lib.Dispose(); }
                    catch (Exception ex) { disposeError = ex; }
                });

                try
                {
                    for (int i = 0; i < 100; i++)
                        _ = add.Invoke(10, 20);
                }
                catch (ObjectDisposedException) { /* expected once disposal wins */ }

                await disposer;
                Assert.Null(disposeError);
            }
        }

        [Fact]
        public async Task InvokeRacesBackendDispose()
        {
            for (int round = 0; round < 40; round++)
            {
                LibFfiBackend backend = new LibFfiBackend();
                FfiType intType = backend.CreatePrimitiveType(FfiPrimitive.Int);
                FfiCallPlan plan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, intType, new[] { intType, intType });

                // The target library must stay loaded for the whole race; only the
                // backend is disposed concurrently.
                PlatformNativeLibrary lib = PlatformNativeLibrary.Load(ExampleSo);
                IntPtr add = lib.GetSymbolOrThrow("add");

                Exception disposeError = null;
                var disposer = Task.Run(() =>
                {
                    try { backend.Dispose(); }
                    catch (Exception ex) { disposeError = ex; }
                });

                try
                {
                    for (int i = 0; i < 100; i++)
                        _ = backend.Invoke(plan, add, new object[] { 10, 20 });
                }
                catch (ObjectDisposedException) { }

                await disposer;
                Assert.Null(disposeError);

                lib.Dispose();
            }
        }

        [Fact]
        public async Task CallbackRacesLibraryDispose()
        {
            for (int round = 0; round < 20; round++)
            {
                FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH);
                lib.RegisterCallback("set_callback", (Action<int>)(_ => { }));
                NativeFunction fire = lib.GetFunction("fire_callback");

                Exception disposeError = null;
                var disposer = Task.Run(() =>
                {
                    try { lib.Dispose(); }
                    catch (Exception ex) { disposeError = ex; }
                });

                try
                {
                    for (int i = 0; i < 100; i++)
                        _ = fire.Invoke(1);
                }
                catch (ObjectDisposedException) { }

                await disposer;
                Assert.Null(disposeError);
            }
        }

        [Fact]
        public void RepeatedDisposeIsIdempotent()
        {
            FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH);
            lib.Dispose();
            lib.Dispose(); // must not throw or double-free

            using (LibFfiBackend backend = new LibFfiBackend())
            {
                backend.Dispose();
                backend.Dispose();
            }
        }
    }
}
