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
    }
}
