using System;
using System.IO;
using FfiSharp.Parsing;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 6 — strings and native buffers: explicit encoding policies, const
    /// char* ↔ string conversion, wchar_t handling (platform-sized), and opaque
    /// byte buffers.
    /// </summary>
    public class StringTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        // ---------------------------------------------------------------- parsing

        [Fact]
        public void ParsesConstCharPointer()
        {
            var model = CParser.Parse("const char *get_name(void);");
            var ret = (CPointerTypeNode)model.Functions[0].ReturnType;
            Assert.True(ret.PointeeIsConst);
        }

        [Fact]
        public void ParsesWideChar()
        {
            var model = CParser.Parse("const wchar_t *get_wide_name(void);");
            var ret = (CPointerTypeNode)model.Functions[0].ReturnType;
            Assert.True(ret.PointeeIsConst);
        }

        [Fact]
        public void NonConstCharPointerIsNotConst()
        {
            var model = CParser.Parse("char *strtok(char *s);");
            var ret = (CPointerTypeNode)model.Functions[0].ReturnType;
            Assert.False(ret.PointeeIsConst);
        }

        // ---------------------------------------------------------------- invocation

        [Fact]
        public void ConstCharReturnIsUtf8String()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                string name = ffi.get_name();
                Assert.Equal("Hello from C", name);
            }
        }

        [Fact]
        public void ConstCharArgumentIsEncoded()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int len = ffi.cstrlen("hello");
                Assert.Equal(5, len);
            }
        }

        [Fact]
        public void StringEchoRoundTrip()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                string echoed = ffi.echo("hello, world");
                Assert.Equal("hello, world", echoed);
            }
        }

        [Fact]
        public void OpaqueByteBufferArgument()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int sum = ffi.checksum(new byte[] { 1, 2, 3, 4 }, 4);
                Assert.Equal(10, sum);
            }
        }

        [Fact]
        public void WideCharReturnAndArgument()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                string wide = ffi.get_wide_name();
                Assert.Equal("Wide", wide);

                int len = ffi.wcslen_c("hi");
                Assert.Equal(2, len);
            }
        }

        [Fact]
        public void Utf16EncodingRoundTrip()
        {
            var options = new FfiLoadOptions { StringEncoding = StringEncoding.Utf16 };
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH, options))
            {
                string echoed = ffi.echo("hello");
                Assert.Equal("hello", echoed);
            }
        }

        [Fact]
        public void RawPointerPolicyDisablesStringConversion()
        {
            var options = new FfiLoadOptions { StringEncoding = StringEncoding.RawPointer };
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH, options))
            {
                // Passing a string to a char* must fail under RawPointer.
                Assert.Throws<ArgumentException>(() => lib.GetFunction("cstrlen").Invoke("hello"));

                // A const char* return becomes a raw IntPtr.
                IntPtr p = (IntPtr)lib.GetFunction("get_name").Invoke();
                Assert.NotEqual(IntPtr.Zero, p);
            }
        }

        [Fact]
        public void NullStringPointerIsAccepted()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                int len = ffi.cstrlen(null);
                Assert.Equal(-1, len);
            }
        }
    }
}
