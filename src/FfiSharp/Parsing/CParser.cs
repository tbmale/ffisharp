using System;
using System.Collections.Generic;
using FfiSharp.Abi;

namespace FfiSharp.Parsing
{
    /// <summary>
    /// A restricted recursive-descent C parser for FFI declarations. It handles
    /// function declarations, typedefs, primitives, pointers, and qualifiers, and
    /// fails explicitly on anything outside that grammar (unions, structs, enums,
    /// variadics, function pointers, …). It is intentionally NOT a general parser.
    /// </summary>
    internal sealed class CParser
    {
        private static readonly HashSet<string> IgnorableSpecifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "const", "volatile", "extern", "static", "inline", "__inline", "restrict", "__restrict"
        };

        private readonly List<Token> _tokens;
        private int _pos;
        private bool _constPointee;
        private readonly List<StructDeclaration> _structs = new List<StructDeclaration>();
        private readonly Dictionary<string, StructDeclaration> _structByTag = new Dictionary<string, StructDeclaration>(StringComparer.Ordinal);

        private CParser(List<Token> tokens)
        {
            _tokens = tokens;
            _pos = 0;
        }

        public static HeaderModel Parse(string source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new CParser(CLexer.Tokenize(source)).ParseTranslationUnit();
        }

        /// <summary>Parses a single bare type expression (e.g. "const char *").</summary>
        public static CTypeNode ParseTypeExpression(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var parser = new CParser(CLexer.Tokenize(text));
            CTypeNode node = parser.ParseBareType();
            if (!parser.AtEnd)
                parser.Error("Unexpected trailing tokens in type expression");
            return node;
        }

        // ------------------------------------------------------------------ helpers

        private Token Cur => _tokens[_pos];

        private Token Peek(int k)
        {
            int idx = _pos + k;
            return idx < _tokens.Count ? _tokens[idx] : _tokens[_tokens.Count - 1];
        }

        private bool AtEnd => Cur.Kind == TokenKind.End;

        private void Advance() => _pos++;

        private bool IsIdentifier(string text)
            => Cur.Kind == TokenKind.Identifier && string.Equals(Cur.Text, text, StringComparison.Ordinal);

        private void Expect(string symbol)
        {
            if (Cur.Kind != TokenKind.Symbol || !string.Equals(Cur.Text, symbol, StringComparison.Ordinal))
                Error($"Expected '{symbol}' but found '{Describe(Cur)}'");
            Advance();
        }

        private string ExpectIdentifier()
        {
            if (Cur.Kind != TokenKind.Identifier)
                Error($"Expected an identifier but found '{Describe(Cur)}'");
            string text = Cur.Text;
            Advance();
            return text;
        }

        private void SkipIgnorableSpecifiers()
        {
            while (Cur.Kind == TokenKind.Identifier && IgnorableSpecifiers.Contains(Cur.Text))
                Advance();
        }

        /// <summary>
        /// Skips leading specifiers/qualifiers before a base type, recording whether
        /// <c>const</c> was present (it applies to the pointee of a following pointer).
        /// </summary>
        private bool SkipLeadingSpecifiers()
        {
            bool sawConst = false;
            while (Cur.Kind == TokenKind.Identifier && IgnorableSpecifiers.Contains(Cur.Text))
            {
                if (string.Equals(Cur.Text, "const", StringComparison.Ordinal))
                    sawConst = true;
                Advance();
            }
            return sawConst;
        }

        private static string Describe(Token t)
        {
            switch (t.Kind)
            {
                case TokenKind.End: return "end of input";
                case TokenKind.Number: return "number " + t.Text;
                case TokenKind.String: return "string literal";
                case TokenKind.Identifier: return "'" + t.Text + "'";
                default: return "'" + t.Text + "'";
            }
        }

        private void Error(string message)
            => throw new FfiParseException(message, Cur.Line, Cur.Column);

        // ------------------------------------------------------------------ grammar

        private HeaderModel ParseTranslationUnit()
        {
            var functions = new List<FunctionDeclaration>();
            var typedefs = new Dictionary<string, CTypeNode>(StringComparer.Ordinal);

            while (!AtEnd)
            {
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == ";") { Advance(); continue; }

                if (IsIdentifier("typedef"))
                {
                    ParseTypedef(typedefs);
                    continue;
                }

                if (IsIdentifier("struct"))
                {
                    ParseStructDeclaration();
                    continue;
                }

                if (IsIdentifier("union"))
                    Error("unions are not supported");
                if (IsIdentifier("enum"))
                    Error("enums are not supported");

                functions.Add(ParseFunctionDeclaration());
            }

            return new HeaderModel(functions, typedefs, _structs, _structByTag);
        }

        private void ParseStructDeclaration()
        {
            Advance(); // 'struct'
            string tag = null;
            if (Cur.Kind == TokenKind.Identifier)
                tag = ExpectIdentifier();

            if (Cur.Kind == TokenKind.Symbol && Cur.Text == "{")
            {
                List<StructFieldDeclaration> fields = ParseStructBody();

                // Optional declarators after the body: `struct Tag {...} name;` (a
                // variable) is unsupported; `typedef` handles the typedef case.
                if (!(Cur.Kind == TokenKind.Symbol && Cur.Text == ";"))
                    Error("Only 'struct Tag { ... };' declarations are supported (use typedef for named structs)");

                Advance(); // ';'

                if (tag == null)
                    Error("Anonymous struct declaration without a typedef is not supported");

                RegisterStruct(new StructDeclaration(tag, null, fields));
            }
            else
            {
                // `struct Tag;` forward declaration or reference — not supported here.
                Error("Expected '{' after struct name (forward declarations are not supported)");
            }
        }

        private void ParseTypedef(Dictionary<string, CTypeNode> typedefs)
        {
            Advance(); // 'typedef'

            // typedef struct [Tag] { ... } Name;
            if (IsIdentifier("struct"))
            {
                Advance(); // 'struct'

                string tag = null;
                if (Cur.Kind == TokenKind.Identifier)
                    tag = ExpectIdentifier();

                if (Cur.Kind == TokenKind.Symbol && Cur.Text == "{")
                {
                    List<StructFieldDeclaration> fields = ParseStructBody();
                    string name = ExpectIdentifier();
                    Expect(";");

                    StructDeclaration decl = new StructDeclaration(tag, name, fields);
                    RegisterStruct(decl);
                    AddTypedef(typedefs, name, new CStructTypeNode(decl));
                }
                else
                {
                    // typedef struct Tag Name;  (reference an already-defined tagged struct)
                    string name = ExpectIdentifier();
                    Expect(";");
                    if (!_structByTag.TryGetValue(tag, out StructDeclaration decl))
                        Error($"Unknown struct '{tag}'");
                    AddTypedef(typedefs, name, new CStructTypeNode(decl));
                }
                return;
            }

            // typedef <type> Name;  (primitives / pointers / typedefs / function pointers)
            CTypeNode baseType = ParseDeclSpecifiers();

            SkipIgnorableSpecifiers();

            // typedef <ret> [*] (*Name)(params);  — a function-pointer typedef.
            CTypeNode fpType = TryParseFunctionPointer(baseType, out string fpName);
            if (fpType != null)
            {
                if (fpName == null)
                    Error("Function-pointer typedef requires a name");
                Expect(";");
                AddTypedef(typedefs, fpName, fpType);
                return;
            }

            int pointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                pointers++;
                SkipIgnorableSpecifiers();
            }

            string typedefName = ExpectIdentifier();

            Expect(";");
            AddTypedef(typedefs, typedefName, ApplyPointers(baseType, pointers));
        }

        /// <summary>
        /// After a base type has been parsed, detects whether a function-pointer
        /// declarator follows, consuming any <c>*</c> that are part of the function
        /// pointer's <em>return</em> type (e.g. <c>const void* (*handler)(...)</c>).
        /// Returns the function-pointer type node, or <c>null</c> when no function
        /// pointer is present (restoring the token stream in that case).
        /// </summary>
        private CTypeNode TryParseFunctionPointer(CTypeNode baseType, out string declaratorName)
        {
            declaratorName = null;

            int savePos = _pos;
            bool saveConst = _constPointee;

            int returnPointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                returnPointers++;
                SkipIgnorableSpecifiers();
            }

            if (!(Cur.Kind == TokenKind.Symbol && Cur.Text == "("))
            {
                // Not a function pointer — it was a plain pointer declarator.
                _pos = savePos;
                _constPointee = saveConst;
                return null;
            }

            // Resolve the return type (which consumes the pending const-pointee flag)
            // BEFORE parsing the parameter list, because parameter parsing resets that
            // flag. Otherwise `const void* (*handler)(...)` would lose its const.
            CTypeNode fpReturnType = ApplyPointers(baseType, returnPointers);

            FfiCallingConvention cc = FfiCallingConvention.Cdecl;
            Expect("(");
            cc = ParseAttributesAndConvention(cc);
            if (!(Cur.Kind == TokenKind.Symbol && Cur.Text == "*"))
                Error("Expected '*' in function-pointer declarator");
            Advance(); // '*'
            SkipIgnorableSpecifiers();
            cc = ParseAttributesAndConvention(cc);
            if (Cur.Kind == TokenKind.Identifier)
                declaratorName = ExpectIdentifier();
            cc = ParseAttributesAndConvention(cc);
            Expect(")");
            Expect("(");
            List<ParameterDeclaration> parameters = ParseParameterList(); // consumes ')'

            return new CFunctionPointerTypeNode(fpReturnType, parameters, cc);
        }

        private List<StructFieldDeclaration> ParseStructBody()
        {
            Expect("{");
            var fields = new List<StructFieldDeclaration>();
            while (true)
            {
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == "}") break;

                fields.Add(ParseStructField());

                if (Cur.Kind == TokenKind.Symbol && Cur.Text == ";") { Advance(); continue; }
                Error("Expected ';' after struct field");
            }
            Expect("}");
            return fields;
        }

        private StructFieldDeclaration ParseStructField()
        {
            _constPointee = SkipLeadingSpecifiers();
            CTypeNode baseType = ParseBaseType();
            SkipIgnorableSpecifiers();

            int pointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                pointers++;
                SkipIgnorableSpecifiers();
            }

            string name = ExpectIdentifier();
            CTypeNode type = ApplyPointers(baseType, pointers);

            int arrayLength = 1;
            if (Cur.Kind == TokenKind.Symbol && Cur.Text == "[")
            {
                Advance();
                if (Cur.Kind == TokenKind.Number)
                {
                    arrayLength = int.Parse(Cur.Text, System.Globalization.CultureInfo.InvariantCulture);
                    Advance();
                    if (arrayLength < 1)
                        Error("Array size must be positive");
                }
                else
                {
                    Error("Only fixed-size arrays with literal bounds are supported");
                }
                Expect("]");
            }

            return new StructFieldDeclaration(name, type, arrayLength);
        }

        private void RegisterStruct(StructDeclaration decl)
        {
            _structs.Add(decl);
            if (decl.Tag != null)
            {
                if (_structByTag.ContainsKey(decl.Tag))
                    Error($"Duplicate struct tag '{decl.Tag}'");
                _structByTag[decl.Tag] = decl;
            }
        }

        private static void AddTypedef(Dictionary<string, CTypeNode> typedefs, string name, CTypeNode type)
        {
            if (typedefs.ContainsKey(name))
                throw new FfiParseException($"Duplicate typedef '{name}'", 0, 0);
            typedefs[name] = type;
        }

        private FunctionDeclaration ParseFunctionDeclaration()
        {
            CTypeNode baseType = ParseDeclSpecifiers();

            FfiCallingConvention cc = FfiCallingConvention.Cdecl;
            cc = ParseAttributesAndConvention(cc);

            SkipIgnorableSpecifiers();
            int pointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                pointers++;
                SkipIgnorableSpecifiers();
                cc = ParseAttributesAndConvention(cc);
            }

            string name = ExpectIdentifier();
            cc = ParseAttributesAndConvention(cc);

            if (!(Cur.Kind == TokenKind.Symbol && Cur.Text == "("))
                Error($"Expected '(' after function name but found '{Describe(Cur)}' (only function declarations are supported)");

            // Resolve the return type BEFORE parsing the parameter list: parameter
            // parsing consumes the pending const-pointee flag.
            CTypeNode returnType = ApplyPointers(baseType, pointers);

            Advance(); // '('
            List<ParameterDeclaration> parameters = ParseParameterList();

            // Trailing ';' or an inline body '{ ... }' (skipped; symbol comes from the library).
            if (Cur.Kind == TokenKind.Symbol && Cur.Text == ";") Advance();
            else if (Cur.Kind == TokenKind.Symbol && Cur.Text == "{") SkipBalancedBraces();
            else Error("Expected ';' after function declaration");

            return new FunctionDeclaration(name, returnType, parameters, cc);
        }

        private List<ParameterDeclaration> ParseParameterList()
        {
            var list = new List<ParameterDeclaration>();

            // '(void)' means zero parameters.
            if (IsIdentifier("void") && Peek(1).Kind == TokenKind.Symbol && Peek(1).Text == ")")
            {
                Advance(); // void
                Advance(); // )
                return list;
            }

            while (true)
            {
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == ")") break;
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == "...")
                    Error("Variadic functions are not supported");

                list.Add(ParseParameter());

                if (Cur.Kind == TokenKind.Symbol && Cur.Text == ",") { Advance(); continue; }
                break;
            }

            Expect(")");
            return list;
        }

        private ParameterDeclaration ParseParameter()
        {
            _constPointee = SkipLeadingSpecifiers();
            CTypeNode baseType = ParseBaseType();
            SkipIgnorableSpecifiers();

            // Function pointer parameter: [ret *] (*name)(params)
            CTypeNode fpType = TryParseFunctionPointer(baseType, out string fnName);
            if (fpType != null)
                return new ParameterDeclaration(fnName, fpType);

            int pointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                pointers++;
                SkipIgnorableSpecifiers();
            }

            string name = null;
            if (Cur.Kind == TokenKind.Identifier)
                name = ExpectIdentifier();

            CTypeNode type = ApplyPointers(baseType, pointers);

            // Array parameters decay to pointers (e.g. "int a[5]" -> int*).
            if (Cur.Kind == TokenKind.Symbol && Cur.Text == "[")
            {
                Advance();
                if (Cur.Kind == TokenKind.Number) Advance();
                Expect("]");
                type = new CPointerTypeNode(type);
            }

            return new ParameterDeclaration(name, type);
        }

        /// <summary>Parses decl-specifiers (qualifiers + base type), ignoring qualifiers.</summary>
        private CTypeNode ParseDeclSpecifiers()
        {
            _constPointee = SkipLeadingSpecifiers();
            CTypeNode baseType = ParseBaseType();
            SkipIgnorableSpecifiers();
            return baseType;
        }

        /// <summary>Parses a bare type (specifiers + trailing pointers) with no declarator.</summary>
        private CTypeNode ParseBareType()
        {
            CTypeNode baseType = ParseDeclSpecifiers();
            int pointers = 0;
            while (Cur.Kind == TokenKind.Symbol && Cur.Text == "*")
            {
                Advance();
                pointers++;
                SkipIgnorableSpecifiers();
            }
            return ApplyPointers(baseType, pointers);
        }

        private CTypeNode ParseBaseType()
        {
            if (Cur.Kind != TokenKind.Identifier)
                return Fail($"Expected a type but found '{Describe(Cur)}'");

            string text = Cur.Text;

            switch (text)
            {
                case "void": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Void);
                case "char": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Char);

                case "signed":
                    Advance();
                    if (IsIdentifier("char")) { Advance(); return new CPrimitiveTypeNode(FfiPrimitive.SChar); }
                    if (IsIdentifier("short")) { Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.Short); }
                    if (IsIdentifier("int")) { Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Int); }
                    if (IsIdentifier("long"))
                    {
                        Advance();
                        if (IsIdentifier("long")) { Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.LongLong); }
                        OptionalInt();
                        return new CPrimitiveTypeNode(FfiPrimitive.Long);
                    }
                    return Fail("Invalid 'signed' type specifier");

                case "unsigned":
                    Advance();
                    if (IsIdentifier("char")) { Advance(); return new CPrimitiveTypeNode(FfiPrimitive.UChar); }
                    if (IsIdentifier("short")) { Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.UShort); }
                    if (IsIdentifier("int")) { Advance(); return new CPrimitiveTypeNode(FfiPrimitive.UInt); }
                    if (IsIdentifier("long"))
                    {
                        Advance();
                        if (IsIdentifier("long")) { Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.ULongLong); }
                        OptionalInt();
                        return new CPrimitiveTypeNode(FfiPrimitive.ULong);
                    }
                    return Fail("Invalid 'unsigned' type specifier");

                case "short": Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.Short);
                case "int": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Int);

                case "long":
                    Advance();
                    if (IsIdentifier("long")) { Advance(); OptionalInt(); return new CPrimitiveTypeNode(FfiPrimitive.LongLong); }
                    OptionalInt();
                    return new CPrimitiveTypeNode(FfiPrimitive.Long);

                case "float": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Float);
                case "double": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.Double);
                case "wchar_t": Advance(); return new CPrimitiveTypeNode(FfiPrimitive.WChar);

                case "struct":
                    Advance();
                    if (Cur.Kind != TokenKind.Identifier)
                        return Fail("Expected struct tag name after 'struct'");
                    {
                        string tag = ExpectIdentifier();
                        if (!_structByTag.TryGetValue(tag, out StructDeclaration decl))
                            return Fail($"Unknown struct '{tag}'");
                        return new CStructTypeNode(decl);
                    }

                case "union": return Fail("unions are not supported");
                case "enum": return Fail("enums are not supported");

                default:
                    // A typedef name (resolved later by CTypeResolver).
                    Advance();
                    return new CTypeNameNode(text);
            }
        }

        private CTypeNode Fail(string message)
        {
            throw new FfiParseException(message, Cur.Line, Cur.Column);
        }

        private void OptionalInt()
        {
            if (IsIdentifier("int")) Advance();
        }

        private FfiCallingConvention ParseAttributesAndConvention(FfiCallingConvention cc)
        {
            while (Cur.Kind == TokenKind.Identifier)
            {
                switch (Cur.Text)
                {
                    case "__cdecl": Advance(); cc = FfiCallingConvention.Cdecl; break;
                    case "__stdcall": Advance(); cc = FfiCallingConvention.Stdcall; break;
                    case "__fastcall":
                    case "__thiscall":
                        Error("Calling convention '" + Cur.Text + "' is not supported");
                        break;
                    case "__attribute__": Advance(); SkipBalancedParens(); break;
                    case "__declspec": Advance(); SkipBalancedParens(); break;
                    default: return cc;
                }
            }
            return cc;
        }

        private void SkipBalancedParens()
        {
            Expect("(");
            int depth = 1;
            while (depth > 0)
            {
                if (AtEnd) Error("Unterminated '('");
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == "(") depth++;
                else if (Cur.Kind == TokenKind.Symbol && Cur.Text == ")") depth--;
                Advance();
            }
        }

        private void SkipBalancedBraces()
        {
            Expect("{");
            int depth = 1;
            while (depth > 0)
            {
                if (AtEnd) Error("Unterminated '{'");
                if (Cur.Kind == TokenKind.Symbol && Cur.Text == "{") depth++;
                else if (Cur.Kind == TokenKind.Symbol && Cur.Text == "}") depth--;
                Advance();
            }
        }

        private CTypeNode ApplyPointers(CTypeNode baseType, int count)
        {
            CTypeNode node = baseType;
            for (int i = 0; i < count; i++)
            {
                // The 'const' qualifier captured before the base type applies to the
                // pointee of the innermost (first) pointer, e.g. `const char*`.
                bool isConst = (i == 0) && _constPointee;
                node = new CPointerTypeNode(node, isConst);
            }
            _constPointee = false;
            return node;
        }
    }
}
