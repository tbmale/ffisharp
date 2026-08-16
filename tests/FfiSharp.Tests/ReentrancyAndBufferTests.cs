using System;
using System.IO;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Regression tests for: (1) the disposed-check TOCTOU hole, (2) reentrant
    /// disposal from within a callback, and (3) byte[] copy-back marshalling.
    /// </summary>
    [Collection("callback-global")]
    public class ReentrancyAndBufferTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        [Fact]
        public void ByteArrayBufferMutationsAreCopiedBack()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                byte[] data = new byte[8];
                ffi.fill(data, 8); // void fill(unsigned char *buf, int len)
                for (int i = 0; i < 8; i++)
                    Assert.Equal((byte)(i & 0xff), data[i]);
            }
        }

        [Fact]
        public void ByteArrayBufferChecksumStillWorks()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int sum = ffi.checksum(new byte[] { 1, 2, 3, 4 }, 4);
                Assert.Equal(10, sum);
            }
        }

        [Fact]
        public void CallbackDisposingItsOwnHandleDoesNotDeadlock()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                CallbackHandle handle = null;
                handle = lib.RegisterCallback("set_callback", (Action<int>)(_ => handle.Dispose()));

                // Firing the callback disposes its own handle reentrantly; must not
                // deadlock. The deferred free is completed when the library is
                // disposed at the end of the using block.
                lib.GetFunction("fire_callback").Invoke(42);
            }
        }

        [Fact]
        public void LibraryDisposingFromCallbackDoesNotDeadlock()
        {
            FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH);
            lib.RegisterCallback("set_callback", (Action<int>)(_ => lib.Dispose()));

            // Reentrant library disposal (deferred because we're inside a callback
            // on this thread).
            lib.GetFunction("fire_callback").Invoke(1);

            // A later non-reentrant dispose completes the deferred release.
            lib.Dispose();
        }

        [Fact]
        public void GetFunctionAfterDisposeThrows()
        {
            FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH);
            lib.Dispose();

            Assert.Throws<ObjectDisposedException>(() => lib.GetFunction("add"));
            Assert.Throws<ObjectDisposedException>(() => lib.GetStructType("Point"));
            Assert.Throws<ObjectDisposedException>(() => lib.CreateStruct("Point"));
        }

        [Fact]
        public void TwoPendingCallbackExceptionsAreBothSurfaced()
        {
            // Regression: the shared pending flag must not strand a second callback's
            // exception. A single native call fires both callbacks, each recording an
            // exception; then two separate invocations must each surface one.
            var options = new FfiLoadOptions { CallbackExceptionPolicy = CallbackExceptionPolicy.RethrowOnManagedBoundary };
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH, options))
            {
                lib.RegisterCallback("set_callback", (Action<int>)(_ => throw new InvalidOperationException("first")));
                lib.RegisterCallback("set_callback2", (Action<int>)(_ => throw new InvalidOperationException("second")));

                NativeFunction fireBoth = lib.GetFunction("fire_both");
                NativeFunction fire1 = lib.GetFunction("fire_callback");

                // Fire both callbacks in a single native call; both exceptions are
                // recorded, but only one shared flag exists.
                fireBoth.Invoke(1);

                var seen = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < 4; i++)
                {
                    try { fire1.Invoke(1); }
                    catch (FfiException ex)
                    {
                        var inner = ex.InnerException as InvalidOperationException;
                        if (inner != null) seen.Add(inner.Message);
                    }
                }

                Assert.Contains("first", seen);
                Assert.Contains("second", seen);
            }
        }
    }
}
