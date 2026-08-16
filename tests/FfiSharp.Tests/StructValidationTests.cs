using System;
using System.Collections.Generic;
using System.IO;
using FfiSharp.Abi;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// FfiStruct field validation and struct-type compatibility: a typo in a field
    /// name must fail at assignment time, and an unrelated FfiStruct must never be
    /// silently reinterpreted according to another struct's native layout.
    /// </summary>
    public class StructValidationTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        [Fact]
        public void ValidFieldAssignmentWorks()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct p = lib.CreateStruct("Point");
                p["x"] = 10;
                p["y"] = 20.5;
                Assert.Equal(10, (int)p["x"]);
                Assert.Equal(20.5, (double)p["y"], 10);
            }
        }

        [Fact]
        public void InvalidFieldFailsImmediately()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct p = lib.CreateStruct("Point");
                var ex = Assert.Throws<KeyNotFoundException>(() => p["notAField"] = 1);
                Assert.Contains("Point", ex.Message);
                Assert.Contains("notAField", ex.Message);
            }
        }

        [Fact]
        public void MissingFieldReadFails()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct p = lib.CreateStruct("Point");
                Assert.Throws<KeyNotFoundException>(() => p.GetField("nope"));
            }
        }

        [Fact]
        public void WrongStructTypeIsRejected()
        {
            // Point { int x; double y; } vs NestedPoint { int x; Point inner; }.
            // Passing a NestedPoint where a Point is expected must throw.
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct nested = lib.CreateStruct("NestedPoint");
                nested["x"] = 1;
                FfiStruct inner = lib.CreateStruct("Point");
                inner["x"] = 2;
                inner["y"] = 3.0;
                nested["inner"] = inner;

                NativeFunction pointSum = lib.GetFunction("point_sum");
                Assert.Throws<FfiMarshallingException>(() => pointSum.Invoke(nested));
            }
        }

        [Fact]
        public void NestedStructTypeMismatchIsRejected()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                // NestedPoint.inner must be a Point; assigning an unrelated Buffer
                // value is caught at marshal time (field names are validated on
                // assignment, value types at the native boundary).
                FfiStruct nested = lib.CreateStruct("NestedPoint");
                nested["x"] = 1;
                FfiStruct wrong = lib.CreateStruct("Buffer");
                nested["inner"] = wrong; // name "inner" is valid; value type is wrong

                NativeFunction nestedSum = lib.GetFunction("nested_sum");
                Assert.Throws<FfiMarshallingException>(() => nestedSum.Invoke(nested));
            }
        }
    }
}
