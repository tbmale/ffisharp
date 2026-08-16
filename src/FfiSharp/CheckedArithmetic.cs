using System;

namespace FfiSharp
{
    /// <summary>
    /// Checked arithmetic for native-boundary size/offset computation. Integer
    /// overflow must never silently produce an undersized native allocation or a
    /// wrong pointer offset, so every multiplication/addition that feeds an
    /// unmanaged allocation or offset goes through here and fails loudly.
    /// </summary>
    internal static class CheckedArithmetic
    {
        public static int Multiply(int a, int b)
        {
            try { return checked(a * b); }
            catch (OverflowException)
            {
                throw new FfiException($"Native size/offset overflow: {a} * {b}.");
            }
        }

        public static int Add(int a, int b)
        {
            try { return checked(a + b); }
            catch (OverflowException)
            {
                throw new FfiException($"Native size/offset overflow: {a} + {b}.");
            }
        }
    }
}
