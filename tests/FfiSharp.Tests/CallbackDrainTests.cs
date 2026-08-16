using System;
using FfiSharp.Abi;
using FfiSharp.Backend;
using FfiSharp.Bindings;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Unit tests for the callback pending-exception drain. These directly drive the
    /// internal registry so the "two callbacks both pending" scenario is set up
    /// deterministically, verifying the shared flag cannot strand an exception.
    /// </summary>
    public class CallbackDrainTests
    {
        private static FfiFunctionType VoidCallback(FfiTypeSystem types)
        {
            FfiType voidType = types.GetPrimitive(FfiPrimitive.Void);
            return new FfiFunctionType(voidType, new FfiType[0], FfiCallingConvention.Cdecl, 8, 8);
        }

        [Fact]
        public void TwoPendingExceptionsAreBothSurfaced()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            {
                var registry = new CallbackRegistry(backend, CallbackExceptionPolicy.RethrowOnManagedBoundary);
                FfiFunctionType sig = VoidCallback(backend.Types);

                FfiCallback first = registry.Create(sig, (Action)(() => { }));
                FfiCallback second = registry.Create(sig, (Action)(() => { }));

                // Both callbacks record an exception (no drain in between).
                first.CaptureException(new InvalidOperationException("first"));
                second.CaptureException(new InvalidOperationException("second"));

                // First drain surfaces one exception...
                var ex1 = Assert.Throws<FfiException>(() => registry.ThrowPendingExceptions());
                Assert.Contains("first", ex1.InnerException.Message);

                // ...and the second must STILL be surfaced (not stranded).
                var ex2 = Assert.Throws<FfiException>(() => registry.ThrowPendingExceptions());
                Assert.Contains("second", ex2.InnerException.Message);

                // Now drained: no exception remains, and the flag is clear.
                registry.ThrowPendingExceptions();
            }
        }

        [Fact]
        public void SinglePendingExceptionIsSurfacedThenCleared()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            {
                var registry = new CallbackRegistry(backend, CallbackExceptionPolicy.RethrowOnManagedBoundary);
                FfiFunctionType sig = VoidCallback(backend.Types);
                FfiCallback cb = registry.Create(sig, (Action)(() => { }));

                cb.CaptureException(new InvalidOperationException("boom"));

                Assert.Throws<FfiException>(() => registry.ThrowPendingExceptions());

                // A second drain is a no-op (flag cleared).
                registry.ThrowPendingExceptions();
            }
        }

        [Fact]
        public void NoPendingExceptionIsNoOp()
        {
            using (LibFfiBackend backend = new LibFfiBackend())
            {
                var registry = new CallbackRegistry(backend, CallbackExceptionPolicy.RethrowOnManagedBoundary);
                registry.ThrowPendingExceptions(); // must not throw or lock unnecessarily
            }
        }
    }
}
