using System;
using System.Runtime.InteropServices;

namespace FfiSharp.Interop
{
    /// <summary>
    /// Raw OS-level dynamic-library primitives. This is the ONLY place in the
    /// codebase that directly calls dlopen/dlsym/dlclose or
    /// LoadLibrary/GetProcAddress/FreeLibrary.
    /// </summary>
    internal static class NativeMethods
    {
        // RTLD_NOW == 2 on both glibc and macOS.
        private const int RtlNow = 2;

        // glibc >= 2.34 merged dlopen/dlsym/dlclose/dlerror into libc.so.6;
        // older glibc keeps them in libdl.so.2. Probe once at startup.
        private static readonly bool LinuxUsesLibc = ProbeLinuxDl();

        internal static IntPtr Load(string path, out string error)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WinLoad(path, out error);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacLoad(path, out error);
            return LinuxLoad(path, out error);
        }

        internal static IntPtr GetSymbol(IntPtr handle, string name)
        {
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetProcAddress(handle, name);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return dlsym_mac(handle, name);
            return LinuxUsesLibc ? dlsym_libc(handle, name) : dlsym_libdl(handle, name);
        }

        internal static void Unload(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { FreeLibrary(handle); return; }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { dlclose_mac(handle); return; }
            if (LinuxUsesLibc) dlclose_libc(handle); else dlclose_libdl(handle);
        }

        private static bool ProbeLinuxDl()
        {
            // Only meaningful on Linux. On Windows/macOS, do not touch libc.so.6 at
            // all (DllImport would throw DllNotFoundException).
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            try
            {
                // A harmless call; succeeds only if libc exports dlerror (glibc >= 2.34).
                dlerror_libc();
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                // glibc < 2.34: dlerror lives in libdl.so.2.
                return false;
            }
            catch (DllNotFoundException)
            {
                // Non-glibc Linux (e.g. musl/Alpine) has neither libc.so.6 nor
                // libdl.so.2 under those exact names. Fall back gracefully; a real
                // dlopen failure will surface as a clean error at Load() time.
                return false;
            }
        }

        // ---------------------------------------------------------------- Windows
        private static IntPtr WinLoad(string path, out string error)
        {
            IntPtr h = LoadLibrary(path);
            if (h == IntPtr.Zero)
            {
                error = "LoadLibrary failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                return IntPtr.Zero;
            }
            error = null;
            return h;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "LoadLibraryA")]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "GetProcAddress")]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        // ---------------------------------------------------------------- Linux (glibc >= 2.34: libc)
        private static IntPtr LinuxLoad(string path, out string error)
        {
            IntPtr h = LinuxUsesLibc ? dlopen_libc(path, RtlNow) : dlopen_libdl(path, RtlNow);
            if (h == IntPtr.Zero)
            {
                IntPtr e = LinuxUsesLibc ? dlerror_libc() : dlerror_libdl();
                error = e == IntPtr.Zero ? "dlopen failed" : Marshal.PtrToStringAnsi(e);
                return IntPtr.Zero;
            }
            error = null;
            return h;
        }

        [DllImport("libc.so.6", EntryPoint = "dlopen")]
        private static extern IntPtr dlopen_libc(string filename, int flags);

        [DllImport("libc.so.6", EntryPoint = "dlsym")]
        private static extern IntPtr dlsym_libc(IntPtr handle, string symbol);

        [DllImport("libc.so.6", EntryPoint = "dlclose")]
        private static extern int dlclose_libc(IntPtr handle);

        [DllImport("libc.so.6", EntryPoint = "dlerror")]
        private static extern IntPtr dlerror_libc();

        // ---------------------------------------------------------------- Linux (glibc < 2.34: libdl)
        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        private static extern IntPtr dlopen_libdl(string filename, int flags);

        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        private static extern IntPtr dlsym_libdl(IntPtr handle, string symbol);

        [DllImport("libdl.so.2", EntryPoint = "dlclose")]
        private static extern int dlclose_libdl(IntPtr handle);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        private static extern IntPtr dlerror_libdl();

        // ---------------------------------------------------------------- macOS
        private static IntPtr MacLoad(string path, out string error)
        {
            dlerror_mac(); // clear stale error
            IntPtr h = dlopen_mac(path, RtlNow);
            if (h == IntPtr.Zero)
            {
                IntPtr e = dlerror_mac();
                error = e == IntPtr.Zero ? "dlopen failed" : Marshal.PtrToStringAnsi(e);
                return IntPtr.Zero;
            }
            error = null;
            return h;
        }

        [DllImport("libdl.dylib", EntryPoint = "dlopen")]
        private static extern IntPtr dlopen_mac(string filename, int flags);

        [DllImport("libdl.dylib", EntryPoint = "dlsym")]
        private static extern IntPtr dlsym_mac(IntPtr handle, string symbol);

        [DllImport("libdl.dylib", EntryPoint = "dlclose")]
        private static extern int dlclose_mac(IntPtr handle);

        [DllImport("libdl.dylib", EntryPoint = "dlerror")]
        private static extern IntPtr dlerror_mac();
    }
}
