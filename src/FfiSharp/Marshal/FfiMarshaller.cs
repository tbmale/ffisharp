using System;
using System.Runtime.InteropServices;
using System.Text;
using FfiSharp.Abi;

namespace FfiSharp.Marshaling
{
    /// <summary>
    /// A marshalled native argument: the pointer libffi reads (<see cref="Pointer"/>,
    /// i.e. what goes in the <c>avalue</c> array) plus a release action that frees
    /// native storage — and, for struct-pointer arguments, copies mutated values back
    /// into the managed <see cref="FfiStruct"/> before freeing.
    /// </summary>
    internal sealed class MarshalledValue
    {
        private readonly Action _release;

        public MarshalledValue(IntPtr pointer, Action release)
        {
            Pointer = pointer;
            _release = release;
        }

        public IntPtr Pointer { get; }

        public void Release() => _release?.Invoke();
    }

    /// <summary>
    /// Converts between managed objects and explicit native storage. Every argument
    /// and return value gets its own native buffer whose lifetime spans
    /// <c>ffi_call</c>; boxed CLR objects are never passed directly to libffi.
    /// </summary>
    internal sealed class FfiMarshaller
    {
        private readonly FfiPlatform _platform;
        private readonly StringEncoding _encoding;

        public FfiMarshaller(FfiPlatform platform, StringEncoding encoding)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _encoding = encoding;
        }

        public MarshalledValue MarshalArgument(FfiType type, object value)
        {
            if (type is FfiPrimitiveType p)
            {
                int size = Math.Max(p.Size, 1);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                WritePrimitive(ptr, p, value);
                return new MarshalledValue(ptr, () => Marshal.FreeHGlobal(ptr));
            }

            if (type is FfiPointerType pointer)
                return MarshalPointer(pointer, value);

            if (type is FfiFunctionType)
            {
                // A function pointer is passed as a pointer-sized value. The binding
                // layer converts managed delegates to closure pointers before we get here.
                IntPtr slot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(slot, ToIntPtr(value));
                return new MarshalledValue(slot, () => Marshal.FreeHGlobal(slot));
            }

            if (type is FfiStructType s)
            {
                IntPtr ptr = Marshal.AllocHGlobal(s.Size);
                try
                {
                    Zero(ptr, s.Size);
                    WriteStruct(ptr, s, AsFfiStruct(value, s.Name));
                    return new MarshalledValue(ptr, () => Marshal.FreeHGlobal(ptr));
                }
                catch
                {
                    Marshal.FreeHGlobal(ptr);
                    throw;
                }
            }

            throw new NotSupportedException("Cannot marshal argument of type " + type.GetType().Name + " yet.");
        }

        public object MarshalReturn(FfiType type, IntPtr storage)
        {
            if (type is FfiPrimitiveType p)
                return ReadPrimitive(storage, p);

            if (type is FfiPointerType ptr)
                return MarshalPointerReturn(ptr, storage);

            if (type is FfiFunctionType)
                return Marshal.ReadIntPtr(storage);

            if (type is FfiStructType s)
                return ReadStruct(storage, s);

            throw new NotSupportedException("Cannot marshal return of type " + type.GetType().Name + " yet.");
        }

        /// <summary>
        /// Writes a managed value directly into existing native storage (no allocation).
        /// Used by callback trampolines to store a callback's return value.
        /// </summary>
        public void WriteToStorage(IntPtr dest, FfiType type, object value)
        {
            if (type is FfiPrimitiveType p)
            {
                if (p.Storage != FfiPrimitive.Void)
                    WritePrimitive(dest, p, value);
                return;
            }
            if (type is FfiPointerType) { Marshal.WriteIntPtr(dest, ToIntPtr(value)); return; }
            if (type is FfiFunctionType) { Marshal.WriteIntPtr(dest, ToIntPtr(value)); return; }
            if (type is FfiStructType s) { WriteStruct(dest, s, AsFfiStruct(value, s.Name)); return; }
            throw new NotSupportedException("Cannot write value of type " + type.GetType().Name);
        }

        // ---------------------------------------------------------------- pointers

        private MarshalledValue MarshalPointer(FfiPointerType pointer, object value)
        {
            // Raw pointer (or null): pass directly, no copy-back.
            if (value == null || value is IntPtr || value is UIntPtr)
                return Passthrough(ToIntPtr(value));

            // Struct pointer passed as a boxed FfiStruct: allocate struct storage,
            // write it, and copy the (possibly mutated) result back on release.
            if (pointer.Pointee is FfiStructType st && value is FfiStruct fs)
            {
                IntPtr structBuf = Marshal.AllocHGlobal(st.Size);
                IntPtr slot = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Zero(structBuf, st.Size);
                    WriteStruct(structBuf, st, fs);
                    Marshal.WriteIntPtr(slot, structBuf);
                    return new MarshalledValue(slot, () =>
                    {
                        ReadStructInto(structBuf, st, fs);
                        Marshal.FreeHGlobal(structBuf);
                        Marshal.FreeHGlobal(slot);
                    });
                }
                catch
                {
                    Marshal.FreeHGlobal(structBuf);
                    Marshal.FreeHGlobal(slot);
                    throw;
                }
            }

            // String → narrow char* / wchar_t* (null-terminated).
            if (value is string s)
            {
                if (IsNarrowChar(pointer.Pointee))
                    return MarshalNarrowString(s);
                if (IsWChar(pointer.Pointee))
                    return MarshalWideString(s);
                throw new ArgumentException(
                    "Cannot pass a string to '" + pointer + "'; use an IntPtr for non-character pointers.");
            }

            // byte[] → opaque buffer (void* / unsigned char* / char*).
            if (value is byte[] bytes)
            {
                if (IsBytePointer(pointer))
                    return MarshalBytes(bytes);
                throw new ArgumentException("Cannot pass a byte[] to '" + pointer + "'.");
            }

            throw new ArgumentException(
                "A pointer argument must be an IntPtr, string, byte[], FfiStruct, or null. Got " + value.GetType().Name + ".");
        }

        private object MarshalPointerReturn(FfiPointerType pointer, IntPtr storage)
        {
            IntPtr addr = Marshal.ReadIntPtr(storage);

            // Only const narrow-char / wchar_t pointers are decoded as strings; a
            // non-const (or void/unsigned char) pointer is returned as a raw pointer.
            if (_encoding != StringEncoding.RawPointer && addr != IntPtr.Zero)
            {
                if (pointer.IsConst && IsNarrowChar(pointer.Pointee))
                    return DecodeNarrowString(addr);
                if (pointer.IsConst && IsWChar(pointer.Pointee))
                    return DecodeWideString(addr);
            }
            return addr;
        }

        private static MarshalledValue Passthrough(IntPtr value)
        {
            IntPtr slot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(slot, value);
            return new MarshalledValue(slot, () => Marshal.FreeHGlobal(slot));
        }

        // ---------------------------------------------------------------- strings

        private MarshalledValue MarshalNarrowString(string value)
        {
            byte[] data = EncodeNarrow(value);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buf, data.Length);
            return WrapPointer(buf);
        }

        private MarshalledValue MarshalWideString(string value)
        {
            byte[] data = EncodeWide(value);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buf, data.Length);
            return WrapPointer(buf);
        }

        private static MarshalledValue MarshalBytes(byte[] bytes)
        {
            IntPtr buf = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            return WrapPointer(buf);
        }

        /// <summary>
        /// Wraps a data pointer in a pointer-sized slot: libffi reads
        /// <c>avalue[i]</c> as a <c>void**</c>, so the slot must contain the data
        /// address. The release action frees both the data buffer and the slot.
        /// </summary>
        private static MarshalledValue WrapPointer(IntPtr dataPtr)
        {
            IntPtr slot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(slot, dataPtr);
            return new MarshalledValue(slot, () =>
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(slot);
            });
        }

        private byte[] EncodeNarrow(string s)
        {
            if (_encoding == StringEncoding.RawPointer)
                throw new ArgumentException("StringEncoding.RawPointer disables string conversion; pass an IntPtr.");

            Encoding enc = _encoding switch
            {
                StringEncoding.Utf16 => Encoding.Unicode,
                StringEncoding.Ansi => Encoding.Default,
                _ => Encoding.UTF8
            };

            byte[] body = enc.GetBytes(s);
            byte[] terminator = enc.GetBytes("\0");
            byte[] result = new byte[body.Length + terminator.Length];
            Array.Copy(body, 0, result, 0, body.Length);
            Array.Copy(terminator, 0, result, body.Length, terminator.Length);
            return result;
        }

        private byte[] EncodeWide(string s)
        {
            if (_encoding == StringEncoding.RawPointer)
                throw new ArgumentException("StringEncoding.RawPointer disables string conversion; pass an IntPtr.");

            // Windows wchar_t is 2-byte UTF-16; Linux/macOS wchar_t is 4-byte UTF-32.
            if (_platform.WCharSize == 2)
            {
                byte[] body = Encoding.Unicode.GetBytes(s);
                byte[] result = new byte[body.Length + 2];
                Array.Copy(body, 0, result, 0, body.Length);
                return result; // trailing 2 zero bytes are the terminator
            }

            // UTF-32 little-endian, no BOM.
            byte[] body32 = new UTF32Encoding(false, false).GetBytes(s);
            byte[] result32 = new byte[body32.Length + 4];
            Array.Copy(body32, 0, result32, 0, body32.Length);
            return result32; // trailing 4 zero bytes are the terminator
        }

        private string DecodeNarrowString(IntPtr addr)
        {
            Encoding enc = _encoding switch
            {
                StringEncoding.Utf16 => Encoding.Unicode,
                StringEncoding.Ansi => Encoding.Default,
                _ => Encoding.UTF8
            };

            int length = StrLen(addr, enc == Encoding.Unicode ? 2 : 1);
            if (length <= 0) return string.Empty;
            byte[] bytes = new byte[length];
            Marshal.Copy(addr, bytes, 0, length);
            return enc.GetString(bytes);
        }

        private string DecodeWideString(IntPtr addr)
        {
            int unitSize = _platform.WCharSize; // 2 or 4
            int unitCount = 0;
            while (true)
            {
                long v = unitSize == 2 ? Marshal.ReadInt16(addr, unitCount * 2) : Marshal.ReadInt32(addr, unitCount * 4);
                if (v == 0) break;
                unitCount++;
            }

            int byteLen = unitCount * unitSize;
            if (byteLen == 0) return string.Empty;
            byte[] bytes = new byte[byteLen];
            Marshal.Copy(addr, bytes, 0, byteLen);
            return unitSize == 2
                ? Encoding.Unicode.GetString(bytes)
                : new UTF32Encoding(false, false).GetString(bytes);
        }

        private static int StrLen(IntPtr addr, int unitSize)
        {
            int i = 0;
            while (true)
            {
                long v = unitSize == 2 ? Marshal.ReadInt16(addr, i * 2) : Marshal.ReadByte(addr, i);
                if (v == 0) return i * unitSize;
                i++;
            }
        }

        // ---------------------------------------------------------------- type tests

        private static bool IsNarrowChar(FfiType pointee)
            => pointee is FfiPrimitiveType p &&
               (p.Primitive == FfiPrimitive.Char || p.Primitive == FfiPrimitive.SChar);

        private static bool IsWChar(FfiType pointee)
            => pointee is FfiPrimitiveType p && p.Primitive == FfiPrimitive.WChar;

        private static bool IsBytePointer(FfiPointerType pointer)
        {
            if (pointer.Pointee == null) return true; // void*
            if (pointer.Pointee is FfiPrimitiveType p)
            {
                return p.Primitive == FfiPrimitive.UChar ||
                       p.Primitive == FfiPrimitive.Char ||
                       p.Primitive == FfiPrimitive.SChar;
            }
            return false;
        }

        private static IntPtr ToIntPtr(object value)
        {
            if (value == null) return IntPtr.Zero;
            if (value is IntPtr p) return p;
            if (value is UIntPtr up) return new IntPtr(unchecked((long)up.ToUInt64()));
            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------- primitives

        private static void WritePrimitive(IntPtr dest, FfiPrimitiveType p, object value)
        {
            switch (p.Storage)
            {
                case FfiPrimitive.Void:
                    break;
                case FfiPrimitive.SChar:
                    Marshal.WriteByte(dest, unchecked((byte)Convert.ToSByte(value))); break;
                case FfiPrimitive.UChar:
                    Marshal.WriteByte(dest, Convert.ToByte(value)); break;
                case FfiPrimitive.Short:
                    Marshal.WriteInt16(dest, Convert.ToInt16(value)); break;
                case FfiPrimitive.UShort:
                    Marshal.WriteInt16(dest, unchecked((short)Convert.ToUInt16(value))); break;
                case FfiPrimitive.Int:
                    Marshal.WriteInt32(dest, Convert.ToInt32(value)); break;
                case FfiPrimitive.UInt:
                    Marshal.WriteInt32(dest, unchecked((int)Convert.ToUInt32(value))); break;
                case FfiPrimitive.LongLong:
                    Marshal.WriteInt64(dest, Convert.ToInt64(value)); break;
                case FfiPrimitive.ULongLong:
                    Marshal.WriteInt64(dest, unchecked((long)Convert.ToUInt64(value))); break;
                case FfiPrimitive.Float:
                    WriteSingle(dest, Convert.ToSingle(value)); break;
                case FfiPrimitive.Double:
                    WriteDouble(dest, Convert.ToDouble(value)); break;
                default:
                    throw new NotSupportedException("Primitive " + p.Storage + " not supported.");
            }
        }

        private static object ReadPrimitive(IntPtr src, FfiPrimitiveType p)
        {
            switch (p.Storage)
            {
                case FfiPrimitive.Void: return null;
                case FfiPrimitive.SChar: return unchecked((sbyte)Marshal.ReadByte(src));
                case FfiPrimitive.UChar: return Marshal.ReadByte(src);
                case FfiPrimitive.Short: return Marshal.ReadInt16(src);
                case FfiPrimitive.UShort: return unchecked((ushort)Marshal.ReadInt16(src));
                case FfiPrimitive.Int: return Marshal.ReadInt32(src);
                case FfiPrimitive.UInt: return unchecked((uint)Marshal.ReadInt32(src));
                case FfiPrimitive.LongLong: return Marshal.ReadInt64(src);
                case FfiPrimitive.ULongLong: return unchecked((ulong)Marshal.ReadInt64(src));
                case FfiPrimitive.Float: return ReadSingle(src);
                case FfiPrimitive.Double: return ReadDouble(src);
                default:
                    throw new NotSupportedException("Primitive " + p.Storage + " not supported.");
            }
        }

        // ---------------------------------------------------------------- structs

        private static void WriteStruct(IntPtr dest, FfiStructType type, FfiStruct value)
        {
            for (int i = 0; i < type.Fields.Count; i++)
            {
                FfiStructField field = type.Fields[i];
                object fieldValue = value.TryGetField(field.Name, out object fv) ? fv : null;
                IntPtr fieldPtr = At(dest, field.Offset);

                if (field.ArrayLength > 1)
                {
                    // A null array field leaves the (already zeroed) storage untouched.
                    if (fieldValue != null)
                        WriteArray(fieldPtr, field, fieldValue);
                }
                else
                {
                    WriteValue(fieldPtr, field.Type, fieldValue, field.Name);
                }
            }
        }

        private static void WriteArray(IntPtr dest, FfiStructField field, object value)
        {
            var list = value as System.Collections.IList;
            if (list == null)
                throw new ArgumentException(
                    $"Field '{field.Name}' expects an array of {field.ArrayLength} elements.");
            if (list.Count != field.ArrayLength)
                throw new ArgumentException(
                    $"Field '{field.Name}' expects {field.ArrayLength} elements but got {list.Count}.");

            int elemSize = field.Type.Size;
            for (int k = 0; k < field.ArrayLength; k++)
                WriteValue(At(dest, k * elemSize), field.Type, list[k], field.Name + "[" + k + "]");
        }

        private static void WriteValue(IntPtr dest, FfiType type, object value, string fieldName)
        {
            if (type is FfiPrimitiveType p) { WritePrimitive(dest, p, value); return; }
            if (type is FfiPointerType) { Marshal.WriteIntPtr(dest, ToIntPtr(value)); return; }
            if (type is FfiStructType s) { WriteStruct(dest, s, AsFfiStruct(value, fieldName)); return; }
            throw new NotSupportedException("Cannot write field of type " + type.GetType().Name);
        }

        private static FfiStruct ReadStruct(IntPtr src, FfiStructType type)
        {
            var value = new FfiStruct(type);
            ReadStructInto(src, type, value);
            return value;
        }

        private static void ReadStructInto(IntPtr src, FfiStructType type, FfiStruct value)
        {
            for (int i = 0; i < type.Fields.Count; i++)
            {
                FfiStructField field = type.Fields[i];
                IntPtr fieldPtr = At(src, field.Offset);
                value.SetField(field.Name, field.ArrayLength > 1
                    ? (object)ReadArray(fieldPtr, field)
                    : ReadValue(fieldPtr, field.Type));
            }
        }

        private static Array ReadArray(IntPtr src, FfiStructField field)
        {
            Type elementType = GetClrType(field.Type);
            Array result = Array.CreateInstance(elementType, field.ArrayLength);
            int elemSize = field.Type.Size;
            for (int k = 0; k < field.ArrayLength; k++)
                result.SetValue(ReadValue(At(src, k * elemSize), field.Type), k);
            return result;
        }

        private static object ReadValue(IntPtr src, FfiType type)
        {
            if (type is FfiPrimitiveType p) return ReadPrimitive(src, p);
            if (type is FfiPointerType) return Marshal.ReadIntPtr(src);
            if (type is FfiStructType s) return ReadStruct(src, s);
            throw new NotSupportedException("Cannot read field of type " + type.GetType().Name);
        }

        private static FfiStruct AsFfiStruct(object value, string context)
        {
            if (value is FfiStruct fs) return fs;
            throw new ArgumentException(
                $"Expected an FfiStruct for '{context}' but got " + (value?.GetType().Name ?? "null") + ".");
        }

        private static Type GetClrType(FfiType type)
        {
            if (type is FfiPrimitiveType p)
            {
                switch (p.Storage)
                {
                    case FfiPrimitive.SChar: return typeof(sbyte);
                    case FfiPrimitive.UChar: return typeof(byte);
                    case FfiPrimitive.Short: return typeof(short);
                    case FfiPrimitive.UShort: return typeof(ushort);
                    case FfiPrimitive.Int: return typeof(int);
                    case FfiPrimitive.UInt: return typeof(uint);
                    case FfiPrimitive.LongLong: return typeof(long);
                    case FfiPrimitive.ULongLong: return typeof(ulong);
                    case FfiPrimitive.Float: return typeof(float);
                    case FfiPrimitive.Double: return typeof(double);
                    default: return typeof(object);
                }
            }
            if (type is FfiPointerType) return typeof(IntPtr);
            if (type is FfiStructType) return typeof(FfiStruct);
            return typeof(object);
        }

        // ---------------------------------------------------------------- helpers

        private static IntPtr At(IntPtr basePtr, int offset) => new IntPtr(basePtr.ToInt64() + offset);

        private static void Zero(IntPtr ptr, int size)
        {
            Marshal.Copy(new byte[size], 0, ptr, size);
        }

        private static void WriteDouble(IntPtr ptr, double d)
        {
            byte[] b = BitConverter.GetBytes(d);
            Marshal.Copy(b, 0, ptr, 8);
        }

        private static double ReadDouble(IntPtr ptr)
        {
            byte[] b = new byte[8];
            Marshal.Copy(ptr, b, 0, 8);
            return BitConverter.ToDouble(b, 0);
        }

        private static void WriteSingle(IntPtr ptr, float f)
        {
            byte[] b = BitConverter.GetBytes(f);
            Marshal.Copy(b, 0, ptr, 4);
        }

        private static float ReadSingle(IntPtr ptr)
        {
            byte[] b = new byte[4];
            Marshal.Copy(ptr, b, 0, 4);
            return BitConverter.ToSingle(b, 0);
        }
    }
}
