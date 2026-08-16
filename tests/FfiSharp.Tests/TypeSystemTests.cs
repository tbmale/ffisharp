using System;
using FfiSharp.Abi;
using FfiSharp.Backend;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 2 — the independent type system: platform-aware resolution of
    /// primitive C types, pointer types, and built-in typedefs.
    /// </summary>
    public class TypeSystemTests
    {
        private static FfiTypeSystem Types => FfiTestHarness.Backend.Types;
        private static FfiPlatform Platform => Types.Platform;

        [Fact]
        public void PlatformIsDetected()
        {
            // These tests run on Linux x64; assert the LP64 model.
            Assert.Equal(FfiOS.Linux, Platform.OS);
            Assert.Equal(FfiArchitecture.X64, Platform.Architecture);
            Assert.Equal(8, Platform.PointerSize);
            Assert.Equal(8, Platform.CLongSize);
            Assert.True(Platform.IsCharSigned);
        }

        [Theory]
        [InlineData(FfiPrimitive.Void, 1, 1)]
        [InlineData(FfiPrimitive.SChar, 1, 1)]
        [InlineData(FfiPrimitive.UChar, 1, 1)]
        [InlineData(FfiPrimitive.Short, 2, 2)]
        [InlineData(FfiPrimitive.UShort, 2, 2)]
        [InlineData(FfiPrimitive.Int, 4, 4)]
        [InlineData(FfiPrimitive.UInt, 4, 4)]
        [InlineData(FfiPrimitive.LongLong, 8, 8)]
        [InlineData(FfiPrimitive.ULongLong, 8, 8)]
        [InlineData(FfiPrimitive.Float, 4, 4)]
        [InlineData(FfiPrimitive.Double, 8, 8)]
        public void FixedWidthPrimitiveSizes(FfiPrimitive p, int size, int align)
        {
            var t = Types.GetPrimitive(p);
            Assert.Equal(size, t.Size);
            Assert.Equal(align, t.Alignment);
            Assert.Equal(p, t.Primitive);
            Assert.Equal(p, t.Storage); // fixed types map to themselves
        }

        [Fact]
        public void LongPreservesIdentityButResolvesTo64BitOnLP64()
        {
            var l = Types.GetPrimitive(FfiPrimitive.Long);
            Assert.Equal(FfiPrimitive.Long, l.Primitive);       // identity preserved
            Assert.Equal(FfiPrimitive.LongLong, l.Storage);     // LP64 -> 64-bit
            Assert.Equal(8, l.Size);

            var ul = Types.GetPrimitive(FfiPrimitive.ULong);
            Assert.Equal(FfiPrimitive.ULong, ul.Primitive);
            Assert.Equal(FfiPrimitive.ULongLong, ul.Storage);
            Assert.Equal(8, ul.Size);
        }

        [Fact]
        public void CharResolvesToSignedOnX64()
        {
            var c = Types.GetPrimitive(FfiPrimitive.Char);
            Assert.Equal(FfiPrimitive.Char, c.Primitive);
            Assert.Equal(FfiPrimitive.SChar, c.Storage);
            Assert.Equal(1, c.Size);
        }

        [Fact]
        public void PrimitiveTypesAreCachedAndShared()
        {
            Assert.Same(Types.GetPrimitive(FfiPrimitive.Int), Types.GetPrimitive(FfiPrimitive.Int));
        }

        [Fact]
        public void PointerTypesAreCached()
        {
            var p1 = Types.GetPointer(Types.GetPrimitive(FfiPrimitive.Int));
            var p2 = Types.GetPointer(Types.GetPrimitive(FfiPrimitive.Int));
            Assert.Same(p1, p2);
            Assert.Equal(8, p1.Size);
            Assert.Equal(FfiTypeKind.Pointer, p1.Kind);
        }

        [Fact]
        public void BuiltinTypedefsResolveCorrectly()
        {
            Assert.Equal(FfiPrimitive.ULongLong, ((FfiPrimitiveType)Types.ResolveTypedef("size_t")).Storage);
            Assert.Equal(FfiPrimitive.ULongLong, ((FfiPrimitiveType)Types.ResolveTypedef("uintptr_t")).Storage);
            Assert.Equal(FfiPrimitive.LongLong, ((FfiPrimitiveType)Types.ResolveTypedef("intptr_t")).Storage);
            Assert.Equal(FfiPrimitive.LongLong, ((FfiPrimitiveType)Types.ResolveTypedef("ptrdiff_t")).Storage);

            Assert.Equal(FfiPrimitive.Int, ((FfiPrimitiveType)Types.ResolveTypedef("int32_t")).Primitive);
            Assert.Equal(FfiPrimitive.LongLong, ((FfiPrimitiveType)Types.ResolveTypedef("int64_t")).Primitive);
            Assert.Equal(FfiPrimitive.UChar, ((FfiPrimitiveType)Types.ResolveTypedef("uint8_t")).Primitive);
        }

        [Fact]
        public void WindowsLlp64Model()
        {
            var win = new FfiPlatform(FfiOS.Windows, FfiArchitecture.X64);
            Assert.Equal(8, win.PointerSize);
            Assert.Equal(4, win.CLongSize);       // long is 32-bit on Windows x64
            Assert.True(win.IsCharSigned);
            Assert.Equal(FfiPrimitive.Int, win.ResolveStorage(FfiPrimitive.Long));
            Assert.Equal(FfiPrimitive.LongLong, win.PointerSizedSigned); // intptr_t is 64-bit
        }
    }
}
