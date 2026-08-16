using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using FfiSharp.Abi;

namespace FfiSharp.Marshaling
{
    /// <summary>
    /// How a marshalled argument's native data buffer must be cleaned up after
    /// <c>ffi_call</c>. This discriminator replaces per-argument <c>Action</c>
    /// closures, so the hot path allocates no managed object or delegate per
    /// argument. The pointer <em>slot</em> lives in reusable frame storage and is
    /// never freed; only the data buffer (<see cref="MarshalledArg.Native"/>) is.
    /// </summary>
    internal enum CleanupKind : byte
    {
        /// <summary>No native resource to free (primitive/raw pointer written into frame storage).</summary>
        None = 0,
        /// <summary>Free the single native buffer at <see cref="MarshalledArg.Native"/>.</summary>
        Free = 1,
        /// <summary>Copy <see cref="MarshalledArg.Native"/> back into the <see cref="MarshalledArg.Retain"/> byte[] then free it.</summary>
        CopyBackBytesFree = 2,
        /// <summary>Copy the struct at <see cref="MarshalledArg.Native"/> back into the <see cref="MarshalledArg.Retain"/> FfiStruct then free it.</summary>
        CopyBackStructFree = 3,
    }

    /// <summary>
    /// A compact, allocation-light record describing one marshalled argument's native
    /// data buffer and how to clean it up. <see cref="Retain"/> keeps any managed
    /// object needed for copy-back strongly reachable until cleanup completes.
    /// Records live in a reusable <see cref="InvocationFrame"/> array — no per-call
    /// heap allocation and no closure.
    /// </summary>
    internal struct MarshalledArg
    {
        /// <summary>The native data buffer to free (or <see cref="IntPtr.Zero"/>).</summary>
        public IntPtr Native;
        public CleanupKind Kind;
        /// <summary>Managed object required for copy-back (byte[] or FfiStruct).</summary>
        public object Retain;
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

        // ---------------------------------------------------------------- frame marshalling

        /// <summary>
        /// Marshals every argument into the reusable frame's storage, filling its
        /// <c>avalues</c> array and recording cleanup records (only for arguments
        /// that allocate their own native buffer). Allocation-light: primitives and
        /// raw pointers write directly into the frame and record nothing.
        /// </summary>
        public void MarshalArguments(InvocationFrame frame, IReadOnlyList<FfiType> argumentTypes, object[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
                MarshalArgument(frame, i, argumentTypes[i], arguments[i]);
        }

        /// <summary>
        /// Primitive-only fast path: writes each primitive directly into an aligned
        /// frame slot and reads nothing else — no cleanup records, no type dispatch
        /// beyond the primitive write switch. Falls back to <see cref="MarshalArgument"/>
        /// for any non-primitive type.
        /// </summary>
        public void MarshalPrimitiveArguments(InvocationFrame frame, IReadOnlyList<FfiType> argumentTypes, object[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                FfiType type = argumentTypes[i];
                if (type is FfiPrimitiveType p)
                {
                    IntPtr slot = frame.ArgSlot(i);
                    WritePrimitive(slot, p, arguments[i]);
                    frame.SetAvalue(i, slot);
                }
                else
                {
                    MarshalArgument(frame, i, type, arguments[i]);
                }
            }
        }

        private void MarshalArgument(InvocationFrame frame, int index, FfiType type, object value)
        {
            if (type is FfiPrimitiveType p)
            {
                IntPtr slot = frame.ArgSlot(index);
                WritePrimitive(slot, p, value);
                frame.SetAvalue(index, slot);
                return; // no cleanup
            }

            if (type is FfiPointerType pointer)
            {
                MarshalPointer(frame, index, pointer, value);
                return;
            }

            if (type is FfiFunctionType)
            {
                // A function pointer is passed as a pointer-sized value. The binding
                // layer converts managed delegates to closure pointers before we get here.
                IntPtr slot = frame.ArgSlot(index);
                Marshal.WriteIntPtr(slot, ToIntPtr(value));
                frame.SetAvalue(index, slot);
                return; // no cleanup
            }

            if (type is FfiStructType s)
            {
                // Struct by value: avalue[i] points directly at the struct storage.
                IntPtr ptr = Marshal.AllocHGlobal(s.Size);
                try
                {
                    Zero(ptr, s.Size);
                    WriteStruct(ptr, s, AsFfiStructOfType(s, value, s.Name));
                }
                catch
                {
                    Marshal.FreeHGlobal(ptr);
                    throw;
                }
                frame.SetAvalue(index, ptr);
                frame.RecordCleanup(new MarshalledArg { Native = ptr, Kind = CleanupKind.Free });
                return;
            }

            throw new NotSupportedException("Cannot marshal argument of type " + type.GetType().Name + " yet.");
        }

        /// <summary>Executes all recorded cleanup records (copy-back before free) and resets the frame.</summary>
        public void Cleanup(InvocationFrame frame)
        {
            for (int i = 0; i < frame.CleanupCount; i++)
            {
                MarshalledArg arg = frame.CleanupRecord(i);
                switch (arg.Kind)
                {
                    case CleanupKind.Free:
                        Marshal.FreeHGlobal(arg.Native);
                        break;
                    case CleanupKind.CopyBackBytesFree:
                        {
                            byte[] bytes = (byte[])arg.Retain;
                            Marshal.Copy(arg.Native, bytes, 0, bytes.Length);
                            Marshal.FreeHGlobal(arg.Native);
                            break;
                        }
                    case CleanupKind.CopyBackStructFree:
                        {
                            ReadStructInto(arg.Native, ((FfiStruct)arg.Retain).Type, (FfiStruct)arg.Retain);
                            Marshal.FreeHGlobal(arg.Native);
                            break;
                        }
                }
            }
            frame.Reset();
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
            if (type is FfiStructType s) { WriteStruct(dest, s, AsFfiStructOfType(s, value, s.Name)); return; }
            throw new NotSupportedException("Cannot write value of type " + type.GetType().Name);
        }

        // ---------------------------------------------------------------- pointers

        private void MarshalPointer(InvocationFrame frame, int index, FfiPointerType pointer, object value)
        {
            // Raw pointer (or null): write the pointer value into the frame slot.
            if (value == null || value is IntPtr || value is UIntPtr)
            {
                IntPtr slot = frame.ArgSlot(index);
                Marshal.WriteIntPtr(slot, ToIntPtr(value));
                frame.SetAvalue(index, slot);
                return;
            }

            // Struct pointer passed as a boxed FfiStruct: allocate struct storage,
            // write it, and copy the (possibly mutated) result back on cleanup.
            if (pointer.Pointee is FfiStructType st && value is FfiStruct fs)
            {
                IntPtr structBuf = Marshal.AllocHGlobal(st.Size);
                try
                {
                    Zero(structBuf, st.Size);
                    WriteStruct(structBuf, st, fs);
                }
                catch
                {
                    Marshal.FreeHGlobal(structBuf);
                    throw;
                }
                IntPtr slot = frame.ArgSlot(index);
                Marshal.WriteIntPtr(slot, structBuf);
                frame.SetAvalue(index, slot);
                frame.RecordCleanup(new MarshalledArg { Native = structBuf, Kind = CleanupKind.CopyBackStructFree, Retain = fs });
                return;
            }

            // String → narrow char* / wchar_t* (null-terminated).
            if (value is string s)
            {
                IntPtr buf;
                if (IsNarrowChar(pointer.Pointee))
                    buf = EncodeNarrowBuffer(s);
                else if (IsWChar(pointer.Pointee))
                    buf = EncodeWideBuffer(s);
                else
                    throw new FfiMarshallingException(
                        "Cannot pass a string to '" + pointer + "'; use an IntPtr for non-character pointers.");

                IntPtr slot = frame.ArgSlot(index);
                Marshal.WriteIntPtr(slot, buf);
                frame.SetAvalue(index, slot);
                frame.RecordCleanup(new MarshalledArg { Native = buf, Kind = CleanupKind.Free });
                return;
            }

            // byte[] → opaque buffer (void* / unsigned char* / char*).
            if (value is byte[] bytes)
            {
                if (!IsBytePointer(pointer))
                    throw new FfiMarshallingException("Cannot pass a byte[] to '" + pointer + "'.");

                IntPtr buf = MarshalBytesBuffer(bytes);
                IntPtr slot = frame.ArgSlot(index);
                Marshal.WriteIntPtr(slot, buf);
                frame.SetAvalue(index, slot);
                frame.RecordCleanup(new MarshalledArg { Native = buf, Kind = CleanupKind.CopyBackBytesFree, Retain = bytes });
                return;
            }

            throw new FfiMarshallingException(
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

        // ---------------------------------------------------------------- strings / buffers (allocate native data only; slot lives in the frame)

        private IntPtr EncodeNarrowBuffer(string value)
        {
            byte[] data = EncodeNarrow(value);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buf, data.Length);
            return buf;
        }

        private IntPtr EncodeWideBuffer(string value)
        {
            byte[] data = EncodeWide(value);
            IntPtr buf = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, buf, data.Length);
            return buf;
        }

        private static IntPtr MarshalBytesBuffer(byte[] bytes)
        {
            int length = Math.Max(bytes.Length, 1);
            IntPtr buf = Marshal.AllocHGlobal(length);
            Marshal.Copy(bytes, 0, buf, bytes.Length);
            return buf;
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

        private static bool IsNumericValue(object value)
        {
            return value is sbyte || value is byte || value is short || value is ushort
                || value is int || value is uint || value is long || value is ulong
                || value is float || value is double;
        }

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

            // Unsupported managed values must NEVER silently become NULL: a native
            // function would receive a null pointer and misbehave. Fail loudly.
            throw new FfiMarshallingException(
                "Expected a pointer representation (IntPtr, UIntPtr, or null) but got "
                + value.GetType().Name + ".");
        }

        // ---------------------------------------------------------------- primitives

        private static void WritePrimitive(IntPtr dest, FfiPrimitiveType p, object value)
        {
            if (value != null && !IsNumericValue(value))
                throw new FfiMarshallingException(
                    "Cannot marshal value of type " + value.GetType().Name
                    + " as C " + p.Primitive + ". Expected a numeric value.");

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
            if (type is FfiStructType s) { WriteStruct(dest, s, AsFfiStructOfType(s, value, fieldName)); return; }
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

        /// <summary>
        /// Requires <paramref name="value"/> to be an <see cref="FfiStruct"/> whose
        /// type is exactly <paramref name="expected"/> (reference identity — struct
        /// types are canonical/cached per declaration). Prevents silently
        /// reinterpreting an unrelated struct according to the expected native layout.
        /// </summary>
        private static FfiStruct AsFfiStructOfType(FfiStructType expected, object value, string context)
        {
            if (!(value is FfiStruct fs))
                throw new FfiMarshallingException(
                    $"Expected an FfiStruct of type '{expected.Name}' for '{context}' but got "
                    + (value?.GetType().Name ?? "null") + ".");
            if (!ReferenceEquals(fs.Type, expected))
                throw new FfiMarshallingException(
                    $"Struct type mismatch for '{context}': expected '{expected.Name}' but got '{fs.Type.Name}'.");
            return fs;
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
