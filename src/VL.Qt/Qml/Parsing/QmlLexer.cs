using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The lexer of <see cref="QmlDeclarationParser"/>: identifiers, numbers, strings, punctuation and line comments; block comments,
    /// whitespace and the contents of strings are dropped.
    /// </summary>
    internal static class QmlLexer
    {
        internal enum TokenKind
        {
            Ident,
            Number,
            String,
            Template,
            Punct,
        }

        internal readonly record struct Token(TokenKind Kind, string Text, int Line, string? Value = null)
        {
            public bool IsPunct(char c) => Kind == TokenKind.Punct && Text[0] == c;
            public bool IsIdent(string name) => Kind == TokenKind.Ident && Text == name;
        }

        internal readonly record struct Comment(int Line, string Text, bool OwnLine);

        internal sealed record Lexed(List<Token> Tokens, List<Comment> Comments, string? Error, int ErrorLine);

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
                else if (c == '"' || c == '\'')
                {
                    var value = new StringBuilder();
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
                        errorLine = line;
                        break;
                    }
                    if (!Add(new Token(TokenKind.String, s.Substring(i, j - i + 1), line, value.ToString())))
                        break;
                    i = j + 1;
                }
                else if (c == '`')
                {
                    var startLine = line;
                    var j = i + 1;
                    while (j < s.Length && s[j] != '`')
                    {
                        if (s[j] == '\\')
                            j++;
                        else if (j < s.Length && s[j] == '\n')
                            line++;
                        j++;
                    }
                    if (j >= s.Length)
                    {
                        error = "unterminated template string";
                        errorLine = startLine;
                        break;
                    }
                    if (!Add(new Token(TokenKind.Template, "`", startLine)))
                        break;
                    i = j + 1;
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
