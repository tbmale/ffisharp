using System.Collections.Generic;
using FfiSharp.Abi;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// x86 stdcall name-decoration argument-byte computation. The @N suffix on a
    /// 32-bit Windows stdcall symbol is the total argument bytes, each rounded up to
    /// a 4-byte stack slot. This is a pure, unit-testable helper; the actual ABI
    /// invocation is validated end-to-end by the wine x86 smoke test.
    /// </summary>
    public class StdcallDecorationTests
    {
        private static FfiPrimitiveType Int => new FfiPrimitiveType(FfiPrimitive.Int, FfiPrimitive.Int, 4, 4);
        private static FfiPrimitiveType Double => new FfiPrimitiveType(FfiPrimitive.Double, FfiPrimitive.Double, 8, 8);
        private static FfiPrimitiveType Char => new FfiPrimitiveType(FfiPrimitive.Char, FfiPrimitive.SChar, 1, 1);
        private static FfiPointerType Ptr => new FfiPointerType(null, false, 4, 4);

        private static int Bytes(params FfiType[] args)
            => FfiLibrary.StdcallArgumentBytes(new List<FfiType>(args));

        [Fact]
        public void ZeroArgumentsDecorateToZero()
        {
            Assert.Equal(0, Bytes());
        }

        [Fact]
        public void TwoIntsDecorateToEight()
        {
            // Matches the real add_stdcall@8 export.
            Assert.Equal(8, Bytes(Int, Int));
        }

        [Fact]
        public void SinglePointerDecorateToFour()
        {
            Assert.Equal(4, Bytes(Ptr));
        }

        [Fact]
        public void DoubleDecorateToEight()
        {
            Assert.Equal(8, Bytes(Double));
        }

        [Fact]
        public void CharRoundsUpToFour()
        {
            Assert.Equal(4, Bytes(Char));
        }

        [Fact]
        public void MixedArgsSumStackSlots()
        {
            // int(4) + double(8) = 12.
            Assert.Equal(12, Bytes(Int, Double));
        }

        [Fact]
        public void StructRoundsToItsStackSize()
        {
            // A 16-byte struct (Point) occupies a 16-byte stack slot (already 4-aligned).
            var point = new FfiStructType("Point", new[]
            {
                new FfiStructField("x", Int, 1),
                new FfiStructField("y", Double, 1),
            });
            Assert.Equal(16, Bytes(point));
        }
    }
}
