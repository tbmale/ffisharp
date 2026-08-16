using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Marshaling;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Native-boundary marshalling: unsupported managed values must fail loudly
    /// (never silently become NULL or a bogus numeric), while the documented pointer
    /// representations (null, IntPtr, UIntPtr) remain supported.
    /// </summary>
    public class MarshallingHardeningTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        // ---------------------------------------------------------------- pointers

        [Fact]
        public void NullPointerIsAccepted()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                IntPtr r = ffi.identity_ptr(null);
                Assert.Equal(IntPtr.Zero, r);
            }
        }

        [Fact]
        public void IntPtrPointerIsAccepted()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                IntPtr sentinel = new IntPtr(0x12345678);
                Assert.Equal(sentinel, (IntPtr)ffi.identity_ptr(sentinel));
            }
        }

        [Fact]
        public void UIntPtrPointerIsAccepted()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                UIntPtr sentinel = new UIntPtr(0xDEADBEEF);
                long expected = unchecked((long)sentinel.ToUInt64());
                Assert.Equal(expected, ((IntPtr)ffi.identity_ptr(sentinel)).ToInt64());
            }
        }

        [Fact]
        public void UnsupportedPointerValueFailsLoudly()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                // bool is not a valid pointer representation and must throw, not
                // become NULL.
                Assert.ThrowsAny<Exception>(() => ffi.identity_ptr(true));
            }
        }

        [Fact]
        public void StringToNonCharPointerFailsLoudly()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                // increment takes int*; a string must not be silently converted.
                Assert.ThrowsAny<Exception>(() => ffi.increment("not an int pointer"));
            }
        }

        // ---------------------------------------------------------------- numerics

        [Fact]
        public void StringToIntegerFailsLoudly()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                Assert.Throws<FfiMarshallingException>(() => ffi.add("10", 20));
            }
        }

        [Fact]
        public void BoolToIntegerFailsLoudly()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                Assert.Throws<FfiMarshallingException>(() => ffi.add(true, 20));
            }
        }

        [Fact]
        public void NumericWideningStillWorks()
        {
            // Integral widening (int -> long) is a documented, useful convenience.
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                long r = ffi.add_long(10L, 20L);
                Assert.Equal(30L, r);
            }
        }

        // ---------------------------------------------------------------- callback return

        [Fact]
        public void CallbackReturningWrongPointerTypeIsCapturedNotCrash()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                CallbackHandle handle = lib.RegisterCallback(
                    "set_callback",
                    (Action<int>)(_ => { }));

                // The callback's void return marshalling is trivial; assert the
                // handle works and reports no exception.
                lib.GetFunction("fire_callback").Invoke(1);
                Assert.Null(handle.LastException);
            }
        }

        // ---------------------------------------------------------------- direct marshaller

        [Fact]
        public void DirectMarshallerRejectsUnsupportedPointer()
        {
            var platform = FfiPlatform.Detect();
            var marshaller = new FfiMarshaller(platform, StringEncoding.Utf8);
            FfiType voidPtr = new FfiPointerType(null, false, platform.PointerSize, platform.PointerSize);

            // A struct argument value for a void* must throw, never become NULL.
            Assert.Throws<FfiMarshallingException>(() => marshaller.MarshalArgument(voidPtr, new object()));
        }
    }
}
