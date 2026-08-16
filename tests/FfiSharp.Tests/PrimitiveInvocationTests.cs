using System;
using System.Runtime.InteropServices;
using FfiSharp.Abi;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 2 — native calls across the full primitive set and pointers, proving
    /// marshaling is correct end-to-end through libffi.
    /// </summary>
    public class PrimitiveInvocationTests
    {
        private static FfiTypeSystem Types => FfiTestHarness.Backend.Types;

        private static object Invoke(string name, FfiType ret, FfiType[] args, object[] values)
            => FfiTestHarness.Invoke(name, ret, args, values);

        private static FfiPrimitiveType P(FfiPrimitive p) => Types.GetPrimitive(p);

        [Fact]
        public void SignedAndUnsignedIntegers()
        {
            Assert.Equal(30, Convert.ToInt32(Invoke("add_int", P(FfiPrimitive.Int), new[] { P(FfiPrimitive.Int), P(FfiPrimitive.Int) }, new object[] { 10, 20 })));
            Assert.Equal(30u, Convert.ToUInt32(Invoke("add_uint", P(FfiPrimitive.UInt), new[] { P(FfiPrimitive.UInt), P(FfiPrimitive.UInt) }, new object[] { 10u, 20u })));

            Assert.Equal((short)30, Convert.ToInt16(Invoke("add_short", P(FfiPrimitive.Short), new[] { P(FfiPrimitive.Short), P(FfiPrimitive.Short) }, new object[] { (short)10, (short)20 })));
            Assert.Equal((ushort)30, Convert.ToUInt16(Invoke("add_ushort", P(FfiPrimitive.UShort), new[] { P(FfiPrimitive.UShort), P(FfiPrimitive.UShort) }, new object[] { (ushort)10, (ushort)20 })));

            Assert.Equal((sbyte)30, Convert.ToSByte(Invoke("add_schar", P(FfiPrimitive.SChar), new[] { P(FfiPrimitive.SChar), P(FfiPrimitive.SChar) }, new object[] { (sbyte)10, (sbyte)20 })));
            Assert.Equal((byte)30, Convert.ToByte(Invoke("add_uchar", P(FfiPrimitive.UChar), new[] { P(FfiPrimitive.UChar), P(FfiPrimitive.UChar) }, new object[] { (byte)10, (byte)20 })));
        }

        [Fact]
        public void CharSignExtension()
        {
            // negate_char(-5) == 5, exercising signed char widening through libffi.
            var c = P(FfiPrimitive.Char);
            Assert.Equal(5, Convert.ToInt32(Invoke("negate_char", c, new[] { c }, new object[] { (sbyte)-5 })));
        }

        [Fact]
        public void LongAndLongLong()
        {
            Assert.Equal(30L, Convert.ToInt64(Invoke("add_long", P(FfiPrimitive.Long), new[] { P(FfiPrimitive.Long), P(FfiPrimitive.Long) }, new object[] { 10L, 20L })));
            Assert.Equal(30UL, Convert.ToUInt64(Invoke("add_ulong", P(FfiPrimitive.ULong), new[] { P(FfiPrimitive.ULong), P(FfiPrimitive.ULong) }, new object[] { 10UL, 20UL })));

            Assert.Equal(30L, Convert.ToInt64(Invoke("add_ll", P(FfiPrimitive.LongLong), new[] { P(FfiPrimitive.LongLong), P(FfiPrimitive.LongLong) }, new object[] { 10L, 20L })));
            Assert.Equal(30UL, Convert.ToUInt64(Invoke("add_ull", P(FfiPrimitive.ULongLong), new[] { P(FfiPrimitive.ULongLong), P(FfiPrimitive.ULongLong) }, new object[] { 10UL, 20UL })));
        }

        [Fact]
        public void FloatingPoint()
        {
            float f = Convert.ToSingle(Invoke("add_float", P(FfiPrimitive.Float), new[] { P(FfiPrimitive.Float), P(FfiPrimitive.Float) }, new object[] { 1.25f, 2.5f }));
            Assert.Equal(3.75f, f, 4);

            double d = Convert.ToDouble(Invoke("add_double", P(FfiPrimitive.Double), new[] { P(FfiPrimitive.Double), P(FfiPrimitive.Double) }, new object[] { 2.5, 4.0 }));
            Assert.Equal(6.5, d, 10);
        }

        [Fact]
        public void PointerIdentity()
        {
            var ptr = Types.GetPointer(Types.GetPrimitive(FfiPrimitive.Int));
            IntPtr sentinel = new IntPtr(0x12345678);
            object r = Invoke("identity_ptr", ptr, new[] { ptr }, new object[] { sentinel });
            Assert.Equal(sentinel, (IntPtr)r);
        }

        [Fact]
        public void PointerToIntMutation()
        {
            var intPtr = Types.GetPointer(Types.GetPrimitive(FfiPrimitive.Int));
            IntPtr storage = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(storage, 41);
                object r = Invoke("increment", P(FfiPrimitive.Int), new[] { intPtr }, new object[] { storage });
                Assert.Equal(42, Convert.ToInt32(r));
                Assert.Equal(42, Marshal.ReadInt32(storage));
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }

        [Fact]
        public void NullPointerIsAccepted()
        {
            var ptr = Types.GetPointer(null); // void*
            object r = Invoke("identity_ptr", ptr, new[] { ptr }, new object[] { null });
            Assert.Equal(IntPtr.Zero, (IntPtr)r);
        }
    }
}
