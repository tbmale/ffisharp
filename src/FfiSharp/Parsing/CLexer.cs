using System;
using System.Collections.Generic;

namespace FfiSharp.Parsing
{
    internal enum TokenKind
    {
        Identifier,
        Number,
        String,
        Symbol,
        End
    }

    internal struct Token
    {
        public TokenKind Kind;
        public string Text;
        public int Line;
        public int Column;

        public Token(TokenKind kind, string text, int line, int column)
        {
            Kind = kind;
            Text = text;
            Line = line;
            Column = column;
        }
    }

    /// <summary>
    /// A deliberately tiny C lexer. It handles only the restricted FFI grammar:
    /// identifiers, decimal numbers (array sizes), punctuation, comments, and it
    /// skips preprocessor lines. It is NOT a general C lexer.
    /// </summary>
    internal static class CLexer
    {
        private const string Symbols = "*(){}[],;";

        public static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            if (source == null) throw new ArgumentNullException(nameof(source));

            int i = 0, line = 1, col = 1;
            int n = source.Length;

            while (i < n)
            {
                char c = source[i];

                // Whitespace.
                if (char.IsWhiteSpace(c)) { Advance(); continue; }

                // Line comment.
                if (c == '/' && i + 1 < n && source[i + 1] == '/')
                {
                    while (i < n && source[i] != '\n') Advance();
                    continue;
                }

                // Block comment.
                if (c == '/' && i + 1 < n && source[i + 1] == '*')
                {
                    Advance(); Advance();
                    while (i < n && !(source[i] == '*' && i + 1 < n && source[i + 1] == '/'))
                        Advance();
                    if (i < n) { Advance(); Advance(); }
                    continue;
                }

                // Preprocessor line: skip entirely (guards, includes, defines).
                if (c == '#')
                {
                    while (i < n && source[i] != '\n') Advance();
                    continue;
                }

                // Identifier / keyword.
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i, sl = line, sc = col;
                    while (i < n && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) Advance();
                    tokens.Add(new Token(TokenKind.Identifier, source.Substring(start, i - start), sl, sc));
                    continue;
                }

                // Decimal number (array sizes).
                if (char.IsDigit(c))
                {
                    int start = i, sl = line, sc = col;
                    while (i < n && char.IsDigit(source[i])) Advance();
                    tokens.Add(new Token(TokenKind.Number, source.Substring(start, i - start), sl, sc));
                    continue;
                }

                // String literal (e.g. inside __attribute__((visibility("default")))).
                if (c == '"')
                {
                    int sl = line, sc = col;
                    Advance(); // opening quote
                    while (i < n && source[i] != '"')
                    {
                        if (source[i] == '\\' && i + 1 < n) Advance(); // skip escaped char
                        Advance();
                    }
                    Advance(); // closing quote
                    tokens.Add(new Token(TokenKind.String, "string-literal", sl, sc));
                    continue;
                }

                // Ellipsis.
                if (c == '.' && i + 2 < n && source[i + 1] == '.' && source[i + 2] == '.')
                {
                    tokens.Add(new Token(TokenKind.Symbol, "...", line, col));
                    Advance(); Advance(); Advance();
                    continue;
                }

                // Single-character punctuation.
                if (Symbols.IndexOf(c) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line, col));
                    Advance();
                    continue;
                }

                throw new FfiParseException("Unexpected character '" + c + "'", line, col);
            }

            tokens.Add(new Token(TokenKind.End, "", line, col));
            return tokens;

            void Advance()
            {
                if (i < n && source[i] == '\n') { line++; col = 1; }
                else col++;
                i++;
            }
        }
    }
}
