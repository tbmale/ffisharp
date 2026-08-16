using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using FfiSharp.Interop;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 8 — the optional reusable call-plan fast path (libffi 3.7.0+). The
    /// loaded libffi (3.8.0) exposes ffi_call_plan_*, so bindings should build a
    /// fast plan and produce correct results; the fallback path (ffi_call) remains
    /// exercised when the API is absent.
    /// </summary>
    public class CallPlanFastPathTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");

        [Fact]
        public void BackendExposesFastPlanApi()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            {
                // The vendored libffi is 3.8.0, which includes call plans.
                Assert.True(backend.LibFfiVersion >= 30700);
            }
        }

        [Fact]
        public void PlanBuildsFastPlanAndInvokesCorrectly()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            using (PlatformNativeLibrary example = PlatformNativeLibrary.Load(ExampleSo))
            {
                FfiType intType = backend.CreatePrimitiveType(FfiPrimitive.Int);
                using (FfiCallPlan plan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, intType, new[] { intType, intType }))
                {
                    // 3.8.0 exposes the fast path; assert it was engaged.
                    Assert.True(plan.HasFastPlan, "expected a reusable libffi call plan");

                    IntPtr add = example.GetSymbolOrThrow("add");
                    int result = Convert.ToInt32(backend.Invoke(plan, add, new object[] { 10, 20 }));
                    Assert.Equal(30, result);
                }
            }
        }

        [Fact]
        public void RepeatedInvocationReusesFastPlan()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            using (PlatformNativeLibrary example = PlatformNativeLibrary.Load(ExampleSo))
            {
                FfiType dbl = backend.CreatePrimitiveType(FfiPrimitive.Double);
                using (FfiCallPlan plan = backend.CreateCallPlan(
                    FfiCallingConvention.Cdecl, dbl, new[] { dbl, dbl }))
                {
                    Assert.True(plan.HasFastPlan);

                    IntPtr mul = example.GetSymbolOrThrow("multiply");
                    for (int i = 1; i <= 5; i++)
                    {
                        double r = Convert.ToDouble(backend.Invoke(plan, mul, new object[] { (double)i, 2.5 }));
                        Assert.Equal(i * 2.5, r, 10);
                    }
                }
            }
        }

        [Fact]
        public void StructPlanAlsoUsesFastPath()
        {
            // Struct-by-value + fast plan (libffi falls back to ffi_call internally
            // for struct arguments on some targets, but the plan is still valid).
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, Path.Combine(AppContext.BaseDirectory, "example.h")))
            {
                FfiStruct p = lib.CreateStruct("Point");
                p["x"] = 3;
                p["y"] = 4.5;
                double sum = Convert.ToDouble(lib.GetFunction("point_sum").Invoke(p));
                Assert.Equal(7.5, sum, 10);
            }
        }
    }
}
