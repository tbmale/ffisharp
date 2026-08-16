using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Lazily loads the native test library and libffi backend once per process.
    /// </summary>
    internal static class FfiTestHarness
    {
        private static readonly object Sync = new object();
        private static PlatformNativeLibrary _library;
        private static LibFfiBackend _backend;

        public static PlatformNativeLibrary Library
        {
            get { EnsureLoaded(); return _library; }
        }

        public static LibFfiBackend Backend
        {
            get { EnsureLoaded(); return _backend; }
        }

        private static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (_backend != null) return;
                _library = PlatformNativeLibrary.Load(FindExampleLibrary());
                _backend = new LibFfiBackend();
            }
        }

        public static object Invoke(string name, FfiType returnType, FfiType[] argTypes, object[] args)
        {
            IntPtr fn = Library.GetSymbolOrThrow(name);
            using (FfiCallPlan plan = Backend.CreateCallPlan(FfiCallingConvention.Cdecl, returnType, argTypes))
            {
                return Backend.Invoke(plan, fn, args);
            }
        }

        private static string FindExampleLibrary()
        {
            string exeDir = AppContext.BaseDirectory;
            string candidate = Path.Combine(exeDir, "example.so");
            if (File.Exists(candidate)) return candidate;
            if (File.Exists("example.so")) return Path.GetFullPath("example.so");
            throw new FileNotFoundException("example.so not found. Build it with tests/native/build.sh.");
        }
    }
}
