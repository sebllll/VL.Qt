using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The lexer of <see cref="QmlDeclarationParser"/>: identifiers, numbers, strings, regular expression literals, punctuation and
    /// line comments; block comments, whitespace and the contents of strings and regular expressions are dropped.
    /// </summary>
    internal static class QmlLexer
    {
        internal enum TokenKind
        {
            Ident,
            Number,
            String,
            Template,
            Regex,
            Punct,
        }

        internal readonly record struct Token(TokenKind Kind, string Text, int Line, string? Value = null)
        {
            public bool IsPunct(char c) => Kind == TokenKind.Punct && Text[0] == c;
            public bool IsIdent(string name) => Kind == TokenKind.Ident && Text == name;
        }

        internal readonly record struct Comment(int Line, string Text, bool OwnLine);

        internal sealed record Lexed(List<Token> Tokens, List<Comment> Comments, string? Error, int ErrorLine);

        // Keywords after which a '/' starts a regular expression and is not a division.
        private static readonly HashSet<string> s_regexKeywords = new(StringComparer.Ordinal)
        {
            "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else",
        };

        private const string regexPrecedingPunct = "(,=:[!&|?{};+-*%<>~^";

        // A '/' starts a regular expression when no operand precedes it; after an operand it is a division.
        private static bool RegexAllowed(List<Token> tokens)
        {
            if (tokens.Count == 0)
                return true;
            var prev = tokens[^1];
            return prev.Kind switch
            {
                TokenKind.Punct => regexPrecedingPunct.Contains(prev.Text[0]),
                TokenKind.Ident => s_regexKeywords.Contains(prev.Text),
                _ => false,
            };
        }

        // Index after the closing '/' and the flags of the regular expression that starts at s[start], -1 if it does not end on its line.
        private static int SkipRegex(string s, int start)
        {
            var inClass = false;
            for (var j = start + 1; j < s.Length && s[j] != '\n' && s[j] != '\r'; j++)
            {
                var ch = s[j];
                if (ch == '\\')
                    j++;
                else if (ch == '[')
                    inClass = true;
                else if (ch == ']')
                    inClass = false;
                else if (ch == '/' && !inClass)
                {
                    j++;
                    while (j < s.Length && char.IsLetter(s[j]))
                        j++;
                    return j;
                }
            }
            return -1;
        }

        // Index after the closing backtick of the template literal that starts at s[start], -1 if there is none; counts the lines it spans.
        private static int SkipTemplate(string s, int start, ref int line)
        {
            var j = start + 1;
            while (j < s.Length)
            {
                var ch = s[j];
                if (ch == '`')
                    return j + 1;
                if (ch == '\\')
                {
                    j++;
                    if (j < s.Length && s[j] == '\n')
                        line++;
                }
                else if (ch == '\n')
                    line++;
                else if (ch == '$' && j + 1 < s.Length && s[j + 1] == '{')
                {
                    j = SkipSubstitution(s, j + 2, ref line);
                    if (j < 0)
                        return -1;
                    continue;
                }
                j++;
            }
            return -1;
        }

        // Index after the '}' that closes a "${" substitution, -1 if there is none. Nested braces, strings and templates are skipped.
        private static int SkipSubstitution(string s, int start, ref int line)
        {
            var depth = 0;
            var j = start;
            while (j < s.Length)
            {
                var ch = s[j];
                if (ch == '\n')
                    line++;
                else if (ch == '{')
                    depth++;
                else if (ch == '}')
                {
                    if (depth == 0)
                        return j + 1;
                    depth--;
                }
                else if (ch == '`')
                {
                    j = SkipTemplate(s, j, ref line);
                    if (j < 0)
                        return -1;
                    continue;
                }
                else if (ch == '"' || ch == '\'')
                {
                    j++;
                    while (j < s.Length && s[j] != ch && s[j] != '\n')
                        j += s[j] == '\\' ? 2 : 1;
                }
                j++;
            }
            return -1;
        }

        public static Lexed Lex(string s, bool headOnly)
        {
            var tokens = new List<Token>();
            var comments = new List<Comment>();
            string? error = null;
            var errorLine = 0;
            var line = 1;
            var lastTokenLine = 0;
            var directiveLine = -1;
            var i = 0;

            // Returns false when the head ends (the root type was read) and only the head is wanted.
            bool Add(Token t)
            {
                tokens.Add(t);
                var firstOfLine = lastTokenLine != t.Line;
                lastTokenLine = t.Line;
                if (t.IsIdent("import") || t.IsIdent("pragma") || (t.IsPunct('.') && firstOfLine))
                    directiveLine = t.Line;
                else if (headOnly && t.Kind == TokenKind.Ident && t.Line != directiveLine)
                    return false;
                return true;
            }

            while (i < s.Length)
            {
                var c = s[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                }
                else if (char.IsWhiteSpace(c) || c == '﻿')
                    i++;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    var end = s.IndexOf('\n', i);
                    if (end < 0)
                        end = s.Length;
                    var text = s.Substring(i + 2, end - i - 2).TrimEnd('\r');
                    comments.Add(new Comment(line, text, lastTokenLine != line));
                    i = end;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        error = "unterminated comment";
                        errorLine = line;
                        break;
                    }
                    for (var j = i; j < end; j++)
                        if (s[j] == '\n')
                            line++;
                    i = end + 2;
                }
                else if (c == '/' && RegexAllowed(tokens))
                {
                    var end = SkipRegex(s, i);
                    if (end < 0)
                    {
                        error = "unterminated regular expression";
                        errorLine = line;
                        break;
                    }
                    if (!Add(new Token(TokenKind.Regex, s.Substring(i, end - i), line)))
                        break;
                    i = end;
                }
                else if (c == '"' || c == '\'')
                {
                    var value = new StringBuilder();
                    var startLine = line;
                    var j = i + 1;
                    var closed = false;
                    while (j < s.Length && s[j] != '\n')
                    {
                        if (s[j] == c)
                        {
                            closed = true;
                            break;
                        }
                        if (s[j] == '\\' && j + 1 < s.Length)
                        {
                            j++;
                            // A line continuation (backslash and line break) adds nothing to the value.
                            if (s[j] == '\r' && j + 1 < s.Length && s[j + 1] == '\n')
                                j++;
                            if (s[j] == '\n')
                            {
                                line++;
                                j++;
                                continue;
                            }
                            switch (s[j])
                            {
                                case 'n': value.Append('\n'); break;
                                case 't': value.Append('\t'); break;
                                case 'r': value.Append('\r'); break;
                                case 'b': value.Append('\b'); break;
                                case 'f': value.Append('\f'); break;
                                case 'u' when j + 4 < s.Length
                                    && ushort.TryParse(s.AsSpan(j + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u):
                                    value.Append((char)u);
                                    j += 4;
                                    break;
                                default: value.Append(s[j]); break;
                            }
                        }
                        else
                            value.Append(s[j]);
                        j++;
                    }
                    if (!closed)
                    {
                        error = "unterminated string";
                        errorLine = startLine;
                        break;
                    }
                    if (!Add(new Token(TokenKind.String, s.Substring(i, j - i + 1), startLine, value.ToString())))
                        break;
                    i = j + 1;
                }
                else if (c == '`')
                {
                    var startLine = line;
                    var j = SkipTemplate(s, i, ref line);
                    if (j < 0)
                    {
                        error = "unterminated template string";
                        errorLine = startLine;
                        break;
                    }
                    if (!Add(new Token(TokenKind.Template, "`", startLine)))
                        break;
                    i = j;
                }
                else if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
                {
                    var j = i;
                    if (c == '0' && i + 1 < s.Length && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                    {
                        j += 2;
                        while (j < s.Length && char.IsAsciiHexDigit(s[j]))
                            j++;
                    }
                    else
                    {
                        while (j < s.Length && char.IsAsciiDigit(s[j]))
                            j++;
                        if (j < s.Length && s[j] == '.')
                        {
                            j++;
                            while (j < s.Length && char.IsAsciiDigit(s[j]))
                                j++;
                        }
                        if (j < s.Length && (s[j] == 'e' || s[j] == 'E'))
                        {
                            var x = j + 1;
                            if (x < s.Length && (s[x] == '+' || s[x] == '-'))
                                x++;
                            if (x < s.Length && char.IsAsciiDigit(s[x]))
                            {
                                while (x < s.Length && char.IsAsciiDigit(s[x]))
                                    x++;
                                j = x;
                            }
                        }
                    }
                    if (!Add(new Token(TokenKind.Number, s.Substring(i, j - i), line)))
                        break;
                    i = j;
                }
                else if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    var j = i + 1;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '$'))
                        j++;
                    if (!Add(new Token(TokenKind.Ident, s.Substring(i, j - i), line)))
                        break;
                    i = j;
                }
                else
                {
                    if (!Add(new Token(TokenKind.Punct, c.ToString(), line)))
                        break;
                    i++;
                }
            }

            return new Lexed(tokens, comments, error, errorLine);
        }
    }
}
