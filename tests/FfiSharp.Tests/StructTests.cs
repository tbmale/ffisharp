using System;
using System.IO;
using FfiSharp.Abi;
using FfiSharp.Parsing;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 4 — structs: parsing, layout, struct-by-value arguments/returns,
    /// struct pointers with in-place mutation, nested structs, and fixed arrays.
    /// </summary>
    public class StructTests
    {
        private static string ExampleSo => Path.Combine(AppContext.BaseDirectory, "example.so");
        private static string ExampleH => Path.Combine(AppContext.BaseDirectory, "example.h");

        // ---------------------------------------------------------------- parsing

        [Fact]
        public void ParsesTypedefStruct()
        {
            var model = CParser.Parse("typedef struct { int x; double y; } Point;");
            Assert.Single(model.Structs);
            Assert.Equal("Point", model.Structs[0].TypedefName);
            Assert.Equal(2, model.Structs[0].Fields.Count);
        }

        [Fact]
        public void ParsesTaggedStructAndReference()
        {
            var model = CParser.Parse(
                "struct Foo { int x; void* data; };\n" +
                "void use(struct Foo* f);\n");
            Assert.Single(model.Structs);
            Assert.Equal("Foo", model.Structs[0].Tag);
            Assert.Equal("use", model.Functions[0].Name);
            Assert.IsType<CPointerTypeNode>(model.Functions[0].Parameters[0].Type);
        }

        [Fact]
        public void ParsesNestedStructAndArray()
        {
            var model = CParser.Parse(
                "typedef struct { int x; double y; } Point;\n" +
                "typedef struct { int x; Point inner; int values[4]; } Nested;\n");
            Assert.Equal(2, model.Structs.Count);
            var nested = model.Structs[1];
            Assert.Equal(3, nested.Fields.Count);
            Assert.Equal(4, nested.Fields[2].ArrayLength);
        }

        // ---------------------------------------------------------------- layout

        [Fact]
        public void PointLayout()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStructType point = lib.GetStructType("Point");
                Assert.Equal(16, point.Size);
                Assert.Equal(8, point.Alignment);
                Assert.Equal(0, point.GetField("x").Offset);
                Assert.Equal(8, point.GetField("y").Offset);
            }
        }

        [Fact]
        public void NestedLayout()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStructType nested = lib.GetStructType("NestedPoint");
                Assert.Equal(24, nested.Size);
                Assert.Equal(0, nested.GetField("x").Offset);
                Assert.Equal(8, nested.GetField("inner").Offset);
            }
        }

        // ---------------------------------------------------------------- invocation

        [Fact]
        public void StructByValueReturn()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                FfiStruct p = ffi.make_point(3, 4.5);
                Assert.Equal(3, (int)p["x"]);
                Assert.Equal(4.5, (double)p["y"], 12);
            }
        }

        [Fact]
        public void StructPointerInPlaceMutation()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                FfiStruct p = ffi.make_point(3, 4.5);
                ffi.mutate_point(p);
                Assert.Equal(4, (int)p["x"]);
                Assert.Equal(5.5, (double)p["y"], 12);
            }
        }

        [Fact]
        public void StructByValueArgument()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                FfiStruct p = ffi.make_point(3, 4.5);
                double sum = ffi.point_sum(p);
                Assert.Equal(7.5, sum, 12);
            }
        }

        [Fact]
        public void StructByValueArgumentAndReturn()
        {
            using (dynamic ffi = Ffi.Load(ExampleSo, ExampleH))
            {
                FfiStruct a = ffi.make_point(1, 1.5);
                FfiStruct b = ffi.make_point(2, 2.5);
                FfiStruct r = ffi.point_add(a, b);
                Assert.Equal(3, (int)r["x"]);
                Assert.Equal(4.0, (double)r["y"], 12);
            }
        }

        [Fact]
        public void NestedStructByValue()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct inner = lib.CreateStruct("Point");
                inner["x"] = 5;
                inner["y"] = 6.5;

                FfiStruct n = lib.CreateStruct("NestedPoint");
                n["x"] = 1;
                n["inner"] = inner;

                double result = Convert.ToDouble(lib.GetFunction("nested_sum").Invoke(n));
                Assert.Equal(12.5, result, 12);
            }
        }

        [Fact]
        public void FixedSizeArrayInStruct()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct b = lib.CreateStruct("Buffer");
                b["values"] = new int[] { 1, 2, 3, 4 };
                b["name"] = new sbyte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

                int result = Convert.ToInt32(lib.GetFunction("buffer_sum").Invoke(b));
                Assert.Equal(10, result);
            }
        }

        [Fact]
        public void ArrayFieldRoundTrip()
        {
            using (FfiLibrary lib = Ffi.LoadLibrary(ExampleSo, ExampleH))
            {
                FfiStruct b = lib.CreateStruct("Buffer");
                b["values"] = new int[] { 7, 8, 9, 10 };
                b["name"] = new sbyte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

                // buffer_sum leaves the struct mutated-free, but the FfiStruct path
                // copies back; verify the array field survives the round trip.
                lib.GetFunction("buffer_sum").Invoke(b);
                int[] values = (int[])b["values"];
                Assert.Equal(new[] { 7, 8, 9, 10 }, values);
            }
        }
    }
}
