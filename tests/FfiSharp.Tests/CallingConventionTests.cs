using FfiSharp.Abi;
using FfiSharp.Backend;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 7 — calling-convention → libffi ABI mapping. cdecl and stdcall are
    /// distinct ABIs only on 32-bit x86; everywhere else they collapse to the
    /// platform default.
    /// </summary>
    public class CallingConventionTests
    {
        private const int SentinelDefaultAbi = 999;

        [Fact]
        public void Win32CdeclUsesDefaultAbi()
        {
            var p = new FfiPlatform(FfiOS.Windows, FfiArchitecture.X86);
            Assert.Equal(SentinelDefaultAbi,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Cdecl, p, SentinelDefaultAbi));
        }

        [Fact]
        public void Win32StdcallMapsToFfiStdcall()
        {
            var p = new FfiPlatform(FfiOS.Windows, FfiArchitecture.X86);
            // FFI_STDCALL == 2 on win32.
            Assert.Equal(2,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Stdcall, p, SentinelDefaultAbi));
        }

        [Fact]
        public void UnixX86CdeclUsesDefaultAbi()
        {
            var p = new FfiPlatform(FfiOS.Linux, FfiArchitecture.X86);
            Assert.Equal(SentinelDefaultAbi,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Cdecl, p, SentinelDefaultAbi));
        }

        [Fact]
        public void UnixX86StdcallMapsToFfiStdcall()
        {
            var p = new FfiPlatform(FfiOS.Linux, FfiArchitecture.X86);
            // FFI_STDCALL == 5 on Unix x86.
            Assert.Equal(5,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Stdcall, p, SentinelDefaultAbi));
        }

        [Theory]
        [InlineData(FfiOS.Windows, FfiArchitecture.X64)]
        [InlineData(FfiOS.Linux, FfiArchitecture.X64)]
        [InlineData(FfiOS.Linux, FfiArchitecture.Arm64)]
        [InlineData(FfiOS.OSX, FfiArchitecture.X64)]
        [InlineData(FfiOS.OSX, FfiArchitecture.Arm64)]
        public void NonX86CollapsesBothConventionsToDefault(FfiOS os, FfiArchitecture arch)
        {
            var p = new FfiPlatform(os, arch);
            Assert.Equal(SentinelDefaultAbi,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Cdecl, p, SentinelDefaultAbi));
            Assert.Equal(SentinelDefaultAbi,
                LibFfiBackend.ResolveNativeAbi(FfiCallingConvention.Stdcall, p, SentinelDefaultAbi));
        }
    }
}
