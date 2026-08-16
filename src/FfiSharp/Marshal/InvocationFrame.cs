using System;
using System.Runtime.InteropServices;

namespace FfiSharp.Marshaling
{
    /// <summary>
    /// Reusable, thread-local scratch storage for a single native invocation.
    ///
    /// A frame owns:
    ///   - a reusable unmanaged <c>void*[]</c> (<c>avalues</c>) array;
    ///   - a reusable unmanaged return buffer;
    ///   - a reusable, 8-byte-aligned contiguous buffer for primitive arguments;
    ///   - a reusable managed <see cref="MarshalledArg"/> cleanup record array.
    ///
    /// <b>Concurrency invariant:</b> a frame is NEVER shared between threads or
    /// between two simultaneous invocations. It is obtained from the thread-local
    /// stack (<see cref="InvocationFrames"/>) for the duration of one call and
    /// returned afterwards. Because native invocation is synchronous and the frame
    /// never escapes the call, nested/reentrant calls (A → callback → B → callback →
    /// C) each acquire their own frame from the per-thread stack, so A's frame stays
    /// valid while B/C execute.
    /// </summary>
    internal sealed class InvocationFrame
    {
        // --- reusable unmanaged buffers (owned until the frame is dropped) ---
        private IntPtr _avalues;      // void*[capacity]
        private int _avaluesCount;    // number of pointer slots
        private IntPtr _avaluesBase;  // unaligned base of the avalues allocation (for free)

        private IntPtr _ret;          // return buffer
        private int _retCapacity;

        private IntPtr _args;         // 8-byte-aligned contiguous primitive arg buffer
        private int _argsCapacity;
        private IntPtr _argsBase;     // unaligned base of the args allocation (for free)

        // --- reusable managed cleanup records ---
        private MarshalledArg[] _cleanup;
        internal int CleanupCount;

        /// <summary>The previous (outer) frame on this thread's stack, for nesting.</summary>
        internal InvocationFrame Prev;

        internal IntPtr Avalues => _avalues;
        internal IntPtr ReturnBuffer => _ret;

        /// <summary>Ensures all reusable buffers are large enough for a call of <paramref name="argCount"/> arguments.</summary>
        internal void EnsureCapacity(int argCount, int returnSize)
        {
            // avalues: one pointer slot per argument.
            if (argCount > _avaluesCount)
            {
                if (_avaluesBase != IntPtr.Zero) Marshal.FreeHGlobal(_avaluesBase);
                int bytes = CheckedArithmetic.Multiply(argCount, IntPtr.Size);
                // Over-allocate by one pointer so we can manually align the base to 8.
                _avaluesBase = Marshal.AllocHGlobal(bytes + 8);
                _avalues = Align8(_avaluesBase);
                _avaluesCount = argCount;
            }

            // return buffer: max(returnSize, pointer size).
            int needRet = Math.Max(returnSize, IntPtr.Size);
            if (needRet > _retCapacity)
            {
                if (_ret != IntPtr.Zero) Marshal.FreeHGlobal(_ret);
                _ret = Marshal.AllocHGlobal(needRet);
                _retCapacity = needRet;
            }

            // primitive arg buffer: 8-byte slots.
            if (argCount > 0)
            {
                int needArgs = CheckedArithmetic.Multiply(argCount, 8);
                if (needArgs > _argsCapacity)
                {
                    if (_argsBase != IntPtr.Zero) Marshal.FreeHGlobal(_argsBase);
                    _argsBase = Marshal.AllocHGlobal(needArgs + 8);
                    _args = Align8(_argsBase);
                    _argsCapacity = needArgs;
                }
            }
        }

        /// <summary>Returns the 8-byte-aligned slot for primitive argument <paramref name="index"/>.</summary>
        internal IntPtr ArgSlot(int index)
            => new IntPtr(_args.ToInt64() + (long)index * 8L);

        /// <summary>Writes the pointer for argument <paramref name="index"/> into the avalues array.</summary>
        internal void SetAvalue(int index, IntPtr ptr)
            => Marshal.WriteIntPtr(_avalues, index * IntPtr.Size, ptr);

        /// <summary>Records one cleanup operation (append, no allocation after first use).</summary>
        internal void RecordCleanup(in MarshalledArg arg)
        {
            if (_cleanup == null || CleanupCount >= _cleanup.Length)
            {
                int newSize = _cleanup == null ? 8 : _cleanup.Length * 2;
                Array.Resize(ref _cleanup, newSize);
            }
            _cleanup[CleanupCount++] = arg;
        }

        /// <summary>Returns the i-th cleanup record by reference.</summary>
        internal ref MarshalledArg CleanupRecord(int i) => ref _cleanup[i];

        /// <summary>Resets per-call state so the frame can be reused.</summary>
        internal void Reset()
        {
            // Clear retained references (byte[]/FfiStruct copy-back targets) so a
            // long-lived thread-local frame does not pin the last call's arguments.
            if (_cleanup != null)
            {
                for (int i = 0; i < CleanupCount; i++)
                    _cleanup[i] = default;
            }
            CleanupCount = 0;
        }

        internal void ReleaseNative()
        {
            if (_avaluesBase != IntPtr.Zero) { Marshal.FreeHGlobal(_avaluesBase); _avaluesBase = IntPtr.Zero; _avalues = IntPtr.Zero; _avaluesCount = 0; }
            if (_ret != IntPtr.Zero) { Marshal.FreeHGlobal(_ret); _ret = IntPtr.Zero; _retCapacity = 0; }
            if (_argsBase != IntPtr.Zero) { Marshal.FreeHGlobal(_argsBase); _argsBase = IntPtr.Zero; _args = IntPtr.Zero; _argsCapacity = 0; }
        }

        private static IntPtr Align8(IntPtr p)
        {
            long v = p.ToInt64();
            return new IntPtr((v + 7L) & ~7L);
        }
    }

    /// <summary>
    /// Per-thread stack of reusable <see cref="InvocationFrame"/>s. Native invocation
    /// is synchronous, so a thread-static frame is safe provided nesting is handled;
    /// <see cref="Acquire"/>/<see cref="Release"/> implement a small stack so a nested
    /// call never reuses the outer call's frame.
    /// </summary>
    internal static class InvocationFrames
    {
        [ThreadStatic]
        private static InvocationFrame _free;

        [ThreadStatic]
        private static InvocationFrame _active;

        internal static InvocationFrame Acquire()
        {
            InvocationFrame f = _free;
            if (f != null)
            {
                _free = f.Prev;
                f.Reset();
            }
            else
            {
                f = new InvocationFrame();
            }
            f.Prev = _active;
            _active = f;
            return f;
        }

        internal static void Release(InvocationFrame f)
        {
            // f must be the current top of the stack.
            _active = f.Prev;
            f.Prev = _free;
            _free = f;
        }
    }
}
