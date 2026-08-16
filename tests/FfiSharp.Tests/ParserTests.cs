using System;
using System.IO;
using FfiSharp.Parsing;
using Xunit;

namespace FfiSharp.Tests
{
    /// <summary>
    /// Phase 3 — the tiny C parser: function declarations, typedefs, primitives,
    /// pointers, const, and explicit failures for unsupported syntax.
    /// </summary>
    public class ParserTests
    {
        private static HeaderModel Parse(string source) => CParser.Parse(source);

        [Fact]
        public void ParsesFunctionDeclaration()
        {
            var model = Parse("int add(int a, int b);");
            Assert.Single(model.Functions);
            var f = model.Functions[0];
            Assert.Equal("add", f.Name);
            Assert.Equal(2, f.Parameters.Count);
            Assert.Equal("a", f.Parameters[0].Name);
            Assert.Equal("b", f.Parameters[1].Name);
        }

        [Fact]
        public void ParsesPointerReturnAndParameters()
        {
            var model = Parse("const char *get_name(void);");
            var f = model.Functions[0];
            Assert.Equal("get_name", f.Name);
            Assert.IsType<CPointerTypeNode>(f.ReturnType);
            Assert.Empty(f.Parameters); // (void) -> zero params
        }

        [Fact]
        public void ParsesTypedefsAndChainedResolution()
        {
            var model = Parse("typedef int MyInt;\ntypedef MyInt MyOther;\nMyInt triple(MyInt x);");
            Assert.Equal(2, model.Typedefs.Count);
            var f = model.Functions[0];
            Assert.IsType<CTypeNameNode>(f.ReturnType);
            Assert.IsType<CTypeNameNode>(f.Parameters[0].Type);
        }

        [Fact]
        public void ParsesFullPrimitiveSet()
        {
            var model = Parse(@"
                char c(char);
                signed char sc(signed char);
                unsigned char uc(unsigned char);
                short s(short);
                unsigned short us(unsigned short);
                int i(int);
                unsigned int ui(unsigned int);
                long l(long);
                unsigned long ul(unsigned long);
                long long ll(long long);
                unsigned long long ull(unsigned long long);
                float f(float);
                double d(double);
                void v(void);
            ");
            Assert.Equal(14, model.Functions.Count);
        }

        [Fact]
        public void SkipsCommentsAndPreprocessorLines()
        {
            var model = Parse(@"
                #ifndef X
                #define X
                #include <stdint.h>
                /* block comment */
                int add(int a, int b); // line comment
                #endif
            ");
            Assert.Single(model.Functions);
            Assert.Equal("add", model.Functions[0].Name);
        }

        [Fact]
        public void ParsesCallingConventionAndAttributes()
        {
            var model = Parse("int __cdecl add(int a, int b);");
            Assert.Equal(FfiSharp.Abi.FfiCallingConvention.Cdecl, model.Functions[0].CallingConvention);

            var model2 = Parse("int __stdcall sub(int a, int b);");
            Assert.Equal(FfiSharp.Abi.FfiCallingConvention.Stdcall, model2.Functions[0].CallingConvention);

            var model3 = Parse("int __attribute__((visibility(\"default\"))) foo(void);");
            Assert.Single(model3.Functions);
        }

        [Fact]
        public void RejectsFunctionDefinitionsWithBodies()
        {
            // Restricted subset: declarations only. A body with code the lexer
            // cannot tokenize must fail explicitly rather than be misinterpreted.
            Assert.Throws<FfiParseException>(() => Parse("int add(int a, int b) { return a + b; }"));
        }

        [Fact]
        public void RejectsUnionsAndEnums()
        {
            Assert.Throws<FfiParseException>(() => Parse("union U { int x; };"));
            Assert.Throws<FfiParseException>(() => Parse("enum E { A, B };"));
        }

        [Fact]
        public void RejectsVariadics()
        {
            Assert.Throws<FfiParseException>(() => Parse("int printf(const char *fmt, ...);"));
        }

        [Fact]
        public void RejectsGlobalVariables()
        {
            Assert.Throws<FfiParseException>(() => Parse("int global_counter;"));
        }

        [Fact]
        public void ParsesExampleHeader()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "example.h");
            var model = Parse(File.ReadAllText(path));
            Assert.Contains(model.Functions, f => f.Name == "add");
            Assert.Contains(model.Functions, f => f.Name == "add_long");
            Assert.Contains(model.Functions, f => f.Name == "add_u64");
            Assert.Contains(model.Functions, f => f.Name == "identity_ptr");
            Assert.Contains(model.Functions, f => f.Name == "increment");
        }
    }
}
