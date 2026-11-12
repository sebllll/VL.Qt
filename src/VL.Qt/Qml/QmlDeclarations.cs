using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Stride.Core.Mathematics;

namespace VL.Qt.Qml
{
    /// <summary>Severity of a parser message; the node description turns them into VL messages (Info, Warning, Error).</summary>
    internal enum QmlMessageSeverity
    {
        /// <summary>A declaration that gets no pin (alias, var, list, object type, unsupported value type). All infos of a file are joined into one message on the node.</summary>
        Info,
        /// <summary>Something the parser skipped or guessed (e.g. an unknown head tag, a sibling file that cannot be read, a cycle of root types).</summary>
        Warning,
        /// <summary>The file does not parse (no root object, unbalanced braces): the node keeps only the common pins.</summary>
        Error,
    }

    /// <summary>A message of the parser.</summary>
    /// <param name="Line">1-based line in the file the message is about, 0 if not tied to a line.</param>
    /// <param name="Severity">Severity.</param>
    /// <param name="Text">Message without the line prefix.</param>
    /// <param name="File">File name of a sibling the message comes from (declarations of a sibling root type), null for the file itself.</param>
    internal sealed record QmlParseMessage(int Line, QmlMessageSeverity Severity, string Text, string? File = null)
    {
        public override string ToString()
        {
            var where = File is null ? (Line > 0 ? $"line {Line}" : null) : (Line > 0 ? $"{File}:{Line}" : File);
            return where is null ? Text : $"{where}: {Text}";
        }
    }

    /// <summary>
    /// Head tags of a .qml file: <c>//</c> comment lines before the root object (between and around the imports and pragmas).
    /// A tag takes the rest of its line; following comment lines without a tag are appended (like the shader annotations).
    /// </summary>
    /// <param name="Category">@category, null = <see cref="QmlNodeFactory.DefaultCategory"/>.</param>
    /// <param name="Summary">@summary (node help), null if not given.</param>
    /// <param name="Tags">@tags split at commas, trimmed, empty entries dropped (node browser search words).</param>
    internal sealed record QmlFileHead(string? Category, string? Summary, ImmutableArray<string> Tags)
    {
        /// <summary>No tags (a file that cannot be read).</summary>
        public static readonly QmlFileHead Empty = new(null, null, ImmutableArray<string>.Empty);
    }

    /// <summary>How a property declaration is initialised; decides whether the pin default is known and when the value is sent.</summary>
    internal enum QmlInitializer
    {
        /// <summary>No initializer (<c>property real x</c>): pin default = <see cref="QmlTypes.TypeDefault"/>.</summary>
        None,
        /// <summary>
        /// A literal: number (int for real too), true/false, "…" or '…', a colour "#rgb"/"#rrggbb"/"#aarrggbb" or an SVG colour name,
        /// <c>Qt.rgba(r, g, b, a)</c>, <c>Qt.rgb(r, g, b)</c>, <c>Qt.vector2d/3d/4d(…)</c>, <c>Qt.rect(x, y, w, h)</c> with
        /// numeric literal arguments (unary minus allowed). Pin default = that value.
        /// </summary>
        Literal,
        /// <summary>Anything else (a binding, a JS expression, an object): pin default = <see cref="QmlTypes.TypeDefault"/>, QML keeps the binding until the patch changes the pin.</summary>
        Binding,
    }

    /// <summary>Modifiers of a property declaration.</summary>
    [Flags]
    internal enum QmlPropertyFlags
    {
        /// <summary>A plain <c>property</c>.</summary>
        None = 0,
        /// <summary><c>readonly property</c>: output pin only.</summary>
        ReadOnly = 1,
        /// <summary><c>required property</c>: its input is always sent.</summary>
        Required = 2,
        /// <summary><c>default property</c>: no effect on the pins.</summary>
        Default = 4,
    }

    /// <summary>A property declared in the root object (of the file or of a sibling root type) that gets pins.</summary>
    /// <param name="Name">Name in QML (key sent to QtNative).</param>
    /// <param name="QmlType">Type as written ("real", "double", "color", …).</param>
    /// <param name="Kind">Value kind (only supported types become a <see cref="QmlPropertyDecl"/>).</param>
    /// <param name="Flags">readonly, required, default.</param>
    /// <param name="Initializer">How the declaration is initialised; for an inherited property an assignment <c>name: …</c> in the root of the derived file replaces it.</param>
    /// <param name="Default">Pin default as a value of <see cref="QmlTypes.ClrType"/>: the literal, else <see cref="QmlTypes.TypeDefault"/>.</param>
    /// <param name="Summary">The <c>//</c> comment lines directly above the declaration, joined with spaces; null if none.</param>
    /// <param name="Line">1-based line of the declaration in <paramref name="DeclaredIn"/> (or in the file itself).</param>
    /// <param name="DeclaredIn">File name of the sibling root type that declares it ("Gauge.qml"), null for the file itself.</param>
    internal sealed record QmlPropertyDecl(
        string Name,
        string QmlType,
        QmlValueKind Kind,
        QmlPropertyFlags Flags,
        QmlInitializer Initializer,
        object Default,
        string? Summary,
        int Line,
        string? DeclaredIn = null)
    {
        /// <summary>CLR type of the pins of this property.</summary>
        public Type ClrType => QmlTypes.ClrType(Kind);

        /// <summary>True for <c>readonly property</c>.</summary>
        public bool IsReadOnly => (Flags & QmlPropertyFlags.ReadOnly) != 0;

        /// <summary>True for <c>required property</c>.</summary>
        public bool IsRequired => (Flags & QmlPropertyFlags.Required) != 0;
    }

    /// <summary>An argument of a declared signal.</summary>
    /// <param name="Name">Name of the argument.</param>
    /// <param name="QmlType">Type as written ("int", "var", …).</param>
    /// <param name="Kind">Value kind, null for a type without one (then the pin carries object).</param>
    internal sealed record QmlSignalArg(string Name, string QmlType, QmlValueKind? Kind);

    /// <summary>A signal declared in the root object: <c>signal name</c>, <c>signal name()</c> or <c>signal name(type a, …)</c> (also the <c>a: type</c> form).</summary>
    /// <param name="Name">Name in QML.</param>
    /// <param name="Args">Arguments in declaration order.</param>
    /// <param name="Summary">The <c>//</c> comment lines directly above the declaration, null if none.</param>
    /// <param name="Line">1-based line of the declaration.</param>
    /// <param name="DeclaredIn">File name of the sibling root type that declares it, null for the file itself.</param>
    internal sealed record QmlSignalDecl(string Name, ImmutableArray<QmlSignalArg> Args, string? Summary, int Line, string? DeclaredIn = null)
    {
        /// <summary>Shape of its output pin.</summary>
        public QmlSignalShape Shape => QmlTypes.SignalShape(Args.Length);
    }

    /// <summary>
    /// The root declarations of a .qml file (#107, docs/decisions/user-qml.md): a C# parser, no Qt, runs no user code. Reads only
    /// the members of the root object at depth 1 (nested objects, functions, strings and comments are skipped) and the head tags.
    /// If the root type is a sibling file of the same folder (<c>Gauge { }</c> + Gauge.qml), its declarations come first
    /// (recursive, a cycle gives a warning and stops); a redeclaration in the derived file replaces the inherited one in place.
    /// </summary>
    /// <param name="RootType">Type name of the root object as written ("Item", "Gauge", "Controls.Slider"), null if there is none.</param>
    /// <param name="Head">Head tags of the file itself (not of siblings).</param>
    /// <param name="Properties">Properties with a supported type, inherited first, each in declaration order.</param>
    /// <param name="Signals">Signals, inherited first, each in declaration order.</param>
    /// <param name="Messages">Infos (no pin), warnings and errors, in order of the file.</param>
    internal sealed record QmlDeclarations(
        string? RootType,
        QmlFileHead Head,
        ImmutableArray<QmlPropertyDecl> Properties,
        ImmutableArray<QmlSignalDecl> Signals,
        ImmutableArray<QmlParseMessage> Messages)
    {
        /// <summary>Nothing declared, no messages.</summary>
        public static readonly QmlDeclarations Empty = new(
            null, QmlFileHead.Empty, ImmutableArray<QmlPropertyDecl>.Empty, ImmutableArray<QmlSignalDecl>.Empty, ImmutableArray<QmlParseMessage>.Empty);

        /// <summary>True if a message is an error (the file does not parse).</summary>
        public bool HasErrors => Messages.Any(static m => m.Severity == QmlMessageSeverity.Error);

        /// <summary>
        /// Everything of the declarations that ends up in the node description, without line numbers: head, names, types, flags,
        /// initializer kinds, defaults (invariant culture), summaries, signal arguments, message texts. The factory invalidates the
        /// description only when this changes (hot reload of pins); any other edit only reloads the running instance.
        /// </summary>
        public string Signature => QmlDeclarationParser.Signature(this);

        /// <summary>
        /// Parses <paramref name="source"/> (the content of a .qml file).
        /// </summary>
        /// <param name="source">Content of the file.</param>
        /// <param name="readSibling">Content of "&lt;TypeName&gt;.qml" in the same folder for a root type name, null if there is none or it cannot be read.</param>
        public static QmlDeclarations Parse(string source, Func<string, string?> readSibling)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(readSibling);
            return QmlDeclarationParser.Parse(source, readSibling, ImmutableList<string>.Empty, false);
        }

        /// <summary>Only the head tags: cheap, for the scan of the factory (node browser), reads no sibling.</summary>
        public static QmlFileHead ParseHead(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return QmlDeclarationParser.ParseHead(source);
        }
    }
}

namespace VL.Qt.Qml
{
    /// <summary>
    /// The parser behind <see cref="QmlDeclarations"/>: a small lexer (identifiers, numbers, strings, punctuation, line comments; block
    /// comments, whitespace and the contents of strings are dropped) and a scan of the members of the root object at depth 1.
    /// A member ends at a <c>;</c>, at the closing bracket that empties the nesting, or at a line break that does not continue the
    /// expression (a line that ends with or starts with an operator continues it).
    /// </summary>
    internal static class QmlDeclarationParser
    {
        enum TokenKind
        {
            Ident,
            Number,
            String,
            Template,
            Punct,
        }

        readonly record struct Token(TokenKind Kind, string Text, int Line, string? Value = null)
        {
            public bool IsPunct(char c) => Kind == TokenKind.Punct && Text[0] == c;
            public bool IsIdent(string name) => Kind == TokenKind.Ident && Text == name;
        }

        readonly record struct Comment(int Line, string Text, bool OwnLine);

        sealed record Lexed(List<Token> Tokens, List<Comment> Comments, string? Error, int ErrorLine);

        static readonly Regex s_tagStart = new(@"^@(?<name>\w+)\s*(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Lazy<Dictionary<string, Color4>> s_namedColors = new(BuildNamedColors);

        // ---- entry points --------------------------------------------------------------------------------------------------

        public static QmlFileHead ParseHead(string source)
        {
            var lexed = Lex(source, true);
            var warnings = new List<QmlParseMessage>();
            return ReadHead(lexed.Comments, HeadEndLine(lexed.Tokens), warnings);
        }

        public static QmlDeclarations Parse(string source, Func<string, string?> readSibling, ImmutableList<string> chain, bool isSibling)
        {
            var lexed = Lex(source, false);
            var tokens = lexed.Tokens;
            var headWarnings = new List<QmlParseMessage>();
            var head = ReadHead(lexed.Comments, HeadEndLine(tokens), headWarnings);
            if (isSibling)
                headWarnings.Clear();

            QmlDeclarations Fail(string? rootType, int line, string text)
                => new(rootType, head, ImmutableArray<QmlPropertyDecl>.Empty, ImmutableArray<QmlSignalDecl>.Empty,
                    ImmutableArray.Create(new QmlParseMessage(line, QmlMessageSeverity.Error, text)));

            if (lexed.Error != null)
                return Fail(null, lexed.ErrorLine, lexed.Error);

            // Root object: [imports and pragmas] Type { ... }
            var i = SkipHeader(tokens);
            if (i >= tokens.Count || tokens[i].Kind != TokenKind.Ident)
                return Fail(null, i < tokens.Count ? tokens[i].Line : 0, "no root object found (expected 'Type {' after the imports)");
            var rootLine = tokens[i].Line;
            var rootType = tokens[i].Text;
            var k = i + 1;
            while (k + 1 < tokens.Count && tokens[k].IsPunct('.') && tokens[k + 1].Kind == TokenKind.Ident)
            {
                rootType += "." + tokens[k + 1].Text;
                k += 2;
            }
            if (k >= tokens.Count || !tokens[k].IsPunct('{'))
                return Fail(rootType, rootLine, $"no root object found ('{rootType}' is not followed by '{{')");
            var rootOpen = tokens[k];
            k++;

            // Declarations of a sibling root type come first.
            var properties = new List<QmlPropertyDecl>();
            var signals = new List<QmlSignalDecl>();
            var messages = new List<QmlParseMessage>();
            if (!rootType.Contains('.') && char.IsUpper(rootType[0]))
                Inherit(rootType, rootLine, readSibling, chain, properties, signals, messages);
            messages.AddRange(headWarnings);

            var ownLine = OwnLineComments(lexed.Comments);
            var stack = new Stack<(char Open, int Line)>();
            var inherited = new HashSet<string>(properties.Select(static p => p.Name), StringComparer.Ordinal);
            var rootEnd = -1;
            while (true)
            {
                if (k >= tokens.Count)
                    return Fail(rootType, rootOpen.Line, "missing '}': the '{' of the root object is never closed");
                if (tokens[k].IsPunct('}'))
                {
                    rootEnd = k;
                    break;
                }

                var end = ScanMember(tokens, k, stack, out var error, out var errorLine);
                if (error != null)
                    return Fail(rootType, errorLine, error);
                ReadMember(tokens, k, end, ownLine, inherited, properties, signals, messages);
                k = end;
            }

            if (rootEnd + 1 < tokens.Count)
                return Fail(rootType, tokens[rootEnd + 1].Line, "unexpected content after the root object");

            return new QmlDeclarations(rootType, head, properties.ToImmutableArray(), signals.ToImmutableArray(), messages.ToImmutableArray());
        }

        static void Inherit(
            string rootType,
            int rootLine,
            Func<string, string?> readSibling,
            ImmutableList<string> chain,
            List<QmlPropertyDecl> properties,
            List<QmlSignalDecl> signals,
            List<QmlParseMessage> messages)
        {
            var file = rootType + ".qml";
            if (chain.Contains(rootType))
            {
                messages.Add(new QmlParseMessage(rootLine, QmlMessageSeverity.Warning,
                    $"root type '{rootType}' is part of a cycle of sibling root types ({string.Join(" -> ", chain.Append(rootType))}), its declarations are skipped"));
                return;
            }

            string? sibling;
            try
            {
                sibling = readSibling(rootType);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                messages.Add(new QmlParseMessage(rootLine, QmlMessageSeverity.Warning, $"cannot read {file}: {e.Message}"));
                return;
            }
            if (sibling == null)
                return;

            var parsed = Parse(sibling, readSibling, chain.Add(rootType), true);
            // A sibling that does not parse is not an error of this file: its declarations are missing, that is all.
            foreach (var m in parsed.Messages)
            {
                var severity = m.Severity == QmlMessageSeverity.Error ? QmlMessageSeverity.Warning : m.Severity;
                messages.Add(m with { Severity = severity, File = m.File ?? file });
            }
            foreach (var p in parsed.Properties)
                properties.Add(p with { DeclaredIn = p.DeclaredIn ?? file });
            foreach (var s in parsed.Signals)
                signals.Add(s with { DeclaredIn = s.DeclaredIn ?? file });
        }

        // ---- lexer ---------------------------------------------------------------------------------------------------------

        static Lexed Lex(string s, bool headOnly)
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
                                case 'u' when j + 4 < s.Length && ushort.TryParse(s.AsSpan(j + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var u):
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

        // ---- head ----------------------------------------------------------------------------------------------------------

        // Skips imports, pragmas and ".import"/".pragma" lines (everything on their line), returns the index of the root type.
        static int SkipHeader(List<Token> tokens)
        {
            var i = 0;
            while (i < tokens.Count)
            {
                var t = tokens[i];
                var directive = t.IsIdent("import") || t.IsIdent("pragma")
                    || (t.IsPunct('.') && i + 1 < tokens.Count && (tokens[i + 1].IsIdent("import") || tokens[i + 1].IsIdent("pragma")));
                if (!directive)
                    break;
                var line = t.Line;
                while (i < tokens.Count && tokens[i].Line == line)
                    i++;
            }
            return i;
        }

        static int HeadEndLine(List<Token> tokens)
        {
            var i = SkipHeader(tokens);
            return i < tokens.Count ? tokens[i].Line : int.MaxValue;
        }

        static QmlFileHead ReadHead(List<Comment> comments, int endLine, List<QmlParseMessage> warnings)
        {
            string? category = null;
            string? summary = null;
            var tags = new StringBuilder();
            string? open = null; // the tag that takes continuation lines
            var openLine = 0;
            foreach (var comment in comments)
            {
                if (comment.Line >= endLine)
                    break;
                var text = comment.Text.TrimStart('/').Trim();
                var m = s_tagStart.Match(text);
                if (m.Success)
                {
                    var name = m.Groups["name"].Value;
                    var value = m.Groups["rest"].Value.Trim();
                    open = null;
                    switch (name)
                    {
                        case "category": category = value; open = name; break;
                        case "summary": summary = value; open = name; break;
                        case "tags": tags.Clear().Append(value); open = name; break;
                        default: warnings.Add(new QmlParseMessage(comment.Line, QmlMessageSeverity.Warning, $"unknown head tag @{name}")); break;
                    }
                    openLine = comment.Line;
                }
                else if (open != null && text.Length > 0 && comment.OwnLine && comment.Line == openLine + 1)
                {
                    openLine = comment.Line;
                    switch (open)
                    {
                        case "category": category = Join(category, text); break;
                        case "summary": summary = Join(summary, text); break;
                        default: tags.Append(',').Append(text); break;
                    }
                }
                else
                    open = null;
            }

            var tagList = tags.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
            return new QmlFileHead(NullIfEmpty(category), NullIfEmpty(summary), tagList);

            static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;
            static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
        }

        // ---- members -------------------------------------------------------------------------------------------------------

        static Dictionary<int, string> OwnLineComments(List<Comment> comments)
        {
            var map = new Dictionary<int, string>();
            foreach (var c in comments)
                if (c.OwnLine)
                    map[c.Line] = c.Text.TrimStart('/').Trim();
            return map;
        }

        // The "//" lines directly above a declaration (no blank line, no code in between); an empty "//" ends the block.
        static string? SummaryAbove(Dictionary<int, string> ownLine, int line)
        {
            List<string>? lines = null;
            for (var l = line - 1; ownLine.TryGetValue(l, out var text) && text.Length > 0; l--)
                (lines ??= new List<string>()).Add(text);
            if (lines == null)
                return null;
            lines.Reverse();
            return string.Join(' ', lines);
        }

        // Returns the index after the member that starts at s (the first token of a statement inside the root object), -1 on error.
        static int ScanMember(List<Token> t, int s, Stack<(char Open, int Line)> stack, out string? error, out int errorLine)
        {
            error = null;
            errorLine = 0;
            stack.Clear();
            var prev = default(Token);
            var prevClosedBrace = false;
            for (var j = s; j < t.Count; j++)
            {
                var tok = t[j];
                if (stack.Count == 0 && j > s)
                {
                    if (tok.IsPunct('}') || prev.IsPunct(';'))
                        return j;
                    var newLine = tok.Line > prev.Line;
                    if (newLine && !Continues(prev, tok))
                        return j;
                    // "Item { } Item { }" on one line: a name after a closed block starts the next member.
                    if (!newLine && prevClosedBrace && tok.Kind == TokenKind.Ident && !Continues(prev, tok))
                        return j;
                }

                prevClosedBrace = false;
                if (tok.Kind == TokenKind.Punct)
                {
                    var c = tok.Text[0];
                    if (c is '(' or '[' or '{')
                        stack.Push((c, tok.Line));
                    else if (c is ')' or ']' or '}')
                    {
                        if (stack.Count == 0)
                        {
                            error = $"unexpected '{c}'";
                            errorLine = tok.Line;
                            return -1;
                        }
                        var (open, openLine) = stack.Pop();
                        if (!(open == '(' && c == ')' || open == '[' && c == ']' || open == '{' && c == '}'))
                        {
                            error = $"'{c}' does not match the '{open}' on line {openLine}";
                            errorLine = tok.Line;
                            return -1;
                        }
                        prevClosedBrace = c == '}' && stack.Count == 0;
                    }
                }
                prev = tok;
            }

            if (stack.Count > 0)
            {
                var (open, openLine) = stack.Peek();
                error = $"unexpected end of file: the '{open}' on line {openLine} is never closed";
                errorLine = openLine;
                return -1;
            }
            return t.Count;
        }

        // The expression goes on after the line break between prev and next (an operator ends or starts the line).
        static bool Continues(Token prev, Token next)
        {
            if (next.Kind == TokenKind.Punct && "{([.?:+-*/%&|^<>=,".Contains(next.Text[0]))
                return true;
            if (next.Kind == TokenKind.Ident && next.Text is "else" or "catch" or "finally" or "instanceof" or "in")
                return true;
            return prev.Kind == TokenKind.Punct && "+-*/%&|^<>=!?:,.([".Contains(prev.Text[0]);
        }

        static void ReadMember(
            List<Token> t,
            int s,
            int e,
            Dictionary<int, string> ownLine,
            HashSet<string> inherited,
            List<QmlPropertyDecl> properties,
            List<QmlSignalDecl> signals,
            List<QmlParseMessage> messages)
        {
            var line = t[s].Line;
            var flags = QmlPropertyFlags.None;
            var k = s;
            for (; k < e && t[k].Kind == TokenKind.Ident; k++)
            {
                if (t[k].Text == "default") flags |= QmlPropertyFlags.Default;
                else if (t[k].Text == "required") flags |= QmlPropertyFlags.Required;
                else if (t[k].Text == "readonly") flags |= QmlPropertyFlags.ReadOnly;
                else break;
            }

            if (k < e && t[k].IsIdent("property"))
                ReadProperty(t, s, k, e, flags, SummaryAbove(ownLine, line), properties, messages);
            else if (t[s].IsIdent("signal") && s + 1 < e && t[s + 1].Kind == TokenKind.Ident && !(s + 2 < e && t[s + 2].IsPunct(':')))
                ReadSignal(t, s, e, SummaryAbove(ownLine, line), signals);
            else if (t[s].Kind == TokenKind.Ident && s + 1 < e && t[s + 1].IsPunct(':') && inherited.Contains(t[s].Text))
                Reassign(t, s, e, properties);
        }

        static int TrimSemicolons(List<Token> t, int start, int end)
        {
            while (end > start && t[end - 1].IsPunct(';'))
                end--;
            return end;
        }

        static void ReadProperty(
            List<Token> t,
            int s,
            int propertyIndex,
            int e,
            QmlPropertyFlags flags,
            string? summary,
            List<QmlPropertyDecl> properties,
            List<QmlParseMessage> messages)
        {
            var line = t[s].Line;
            var m = propertyIndex + 1;
            if (m >= e || t[m].Kind != TokenKind.Ident)
            {
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Warning, "incomplete property declaration is skipped"));
                return;
            }

            string type;
            if (t[m].Text == "list" && m + 1 < e && t[m + 1].IsPunct('<'))
            {
                var sb = new StringBuilder("list");
                m++;
                while (m < e && !t[m].IsPunct('>'))
                    sb.Append(t[m++].Text);
                sb.Append('>');
                m++;
                type = sb.ToString();
            }
            else
            {
                type = t[m++].Text;
                while (m + 1 < e && t[m].IsPunct('.') && t[m + 1].Kind == TokenKind.Ident)
                {
                    type += "." + t[m + 1].Text;
                    m += 2;
                }
            }

            if (m >= e || t[m].Kind != TokenKind.Ident)
            {
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Warning, "incomplete property declaration is skipped"));
                return;
            }
            var name = t[m++].Text;

            if (!QmlTypes.TryMap(type, out var kind))
            {
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Info, $"property '{name}' of type '{type}'"));
                return;
            }

            var initializer = QmlInitializer.None;
            var value = QmlTypes.TypeDefault(kind);
            if (m < e && t[m].IsPunct(':'))
            {
                var end = TrimSemicolons(t, m + 1, e);
                if (end > m + 1)
                    (initializer, value) = Evaluate(kind, t, m + 1, end);
            }

            var decl = new QmlPropertyDecl(name, type, kind, flags, initializer, value, summary, line);
            var index = properties.FindIndex(p => p.Name == name);
            if (index >= 0)
                properties[index] = decl;
            else
                properties.Add(decl);
        }

        // "name: value" in the root of a derived file replaces the initializer of an inherited property.
        static void Reassign(List<Token> t, int s, int e, List<QmlPropertyDecl> properties)
        {
            var name = t[s].Text;
            var index = properties.FindIndex(p => p.Name == name);
            if (index < 0)
                return;
            var end = TrimSemicolons(t, s + 2, e);
            if (end <= s + 2)
                return;
            var (initializer, value) = Evaluate(properties[index].Kind, t, s + 2, end);
            properties[index] = properties[index] with { Initializer = initializer, Default = value };
        }

        static void ReadSignal(List<Token> t, int s, int e, string? summary, List<QmlSignalDecl> signals)
        {
            var name = t[s + 1].Text;
            var args = ImmutableArray.CreateBuilder<QmlSignalArg>();
            var m = s + 2;
            if (m < e && t[m].IsPunct('('))
            {
                m++;
                var from = m;
                var depth = 0;
                for (; m < e; m++)
                {
                    if (t[m].IsPunct('('))
                        depth++;
                    else if (t[m].IsPunct(')'))
                    {
                        if (depth == 0)
                            break;
                        depth--;
                    }
                    else if (t[m].IsPunct('<'))
                        depth++;
                    else if (t[m].IsPunct('>'))
                        depth--;
                    else if (t[m].IsPunct(',') && depth == 0)
                    {
                        AddArg(from, m);
                        from = m + 1;
                    }
                }
                AddArg(from, m);
            }

            var decl = new QmlSignalDecl(name, args.ToImmutable(), summary, t[s].Line);
            var index = signals.FindIndex(x => x.Name == name);
            if (index >= 0)
                signals[index] = decl;
            else
                signals.Add(decl);

            void AddArg(int from, int to)
            {
                if (to <= from)
                    return;
                string argName;
                string argType;
                if (to - from >= 3 && t[from + 1].IsPunct(':'))
                {
                    argName = t[from].Text;
                    argType = Concat(from + 2, to);
                }
                else if (to - from >= 2)
                {
                    argName = t[to - 1].Text;
                    argType = Concat(from, to - 1);
                }
                else
                {
                    argName = $"arg{args.Count + 1}";
                    argType = t[from].Text;
                }
                args.Add(new QmlSignalArg(argName, argType, QmlTypes.TryMap(argType, out var kind) ? kind : null));
            }

            string Concat(int from, int to)
            {
                var sb = new StringBuilder();
                for (var j = from; j < to; j++)
                    sb.Append(t[j].Text);
                return sb.ToString();
            }
        }

        // ---- literals ------------------------------------------------------------------------------------------------------

        static (QmlInitializer, object) Evaluate(QmlValueKind kind, List<Token> t, int s, int e)
            => TryLiteral(kind, t, s, e, out var value) ? (QmlInitializer.Literal, value) : (QmlInitializer.Binding, QmlTypes.TypeDefault(kind));

        static bool TryLiteral(QmlValueKind kind, List<Token> t, int s, int e, out object value)
        {
            value = QmlTypes.TypeDefault(kind);
            double d;
            double[] a;
            string fn;
            switch (kind)
            {
                case QmlValueKind.Real:
                    if (TryNumber(t, s, e, out d) && float.IsFinite((float)d))
                    {
                        value = (float)d;
                        return true;
                    }
                    return false;
                case QmlValueKind.Int:
                    if (TryNumber(t, s, e, out d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue)
                    {
                        value = (int)d;
                        return true;
                    }
                    return false;
                case QmlValueKind.Bool:
                    if (e - s == 1 && (t[s].IsIdent("true") || t[s].IsIdent("false")))
                    {
                        value = t[s].Text == "true";
                        return true;
                    }
                    return false;
                case QmlValueKind.String:
                    if (e - s == 1 && t[s].Kind == TokenKind.String)
                    {
                        value = t[s].Value ?? string.Empty;
                        return true;
                    }
                    return false;
                case QmlValueKind.Color:
                    if (e - s == 1 && t[s].Kind == TokenKind.String)
                    {
                        if (!TryColor(t[s].Value ?? string.Empty, out var color))
                            return false;
                        value = color;
                        return true;
                    }
                    if (TryQtCall(t, s, e, out fn, out a) && a.All(static x => x is >= 0 and <= 1))
                    {
                        if (fn == "rgba" && a.Length == 4)
                        {
                            value = new Color4((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                            return true;
                        }
                        if (fn == "rgb" && a.Length == 3)
                        {
                            value = new Color4((float)a[0], (float)a[1], (float)a[2], 1f);
                            return true;
                        }
                    }
                    return false;
                case QmlValueKind.Vector2D:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "vector2d" && a.Length == 2 && Finite(a))
                    {
                        value = new Vector2((float)a[0], (float)a[1]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Vector3D:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "vector3d" && a.Length == 3 && Finite(a))
                    {
                        value = new Vector3((float)a[0], (float)a[1], (float)a[2]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Vector4D:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "vector4d" && a.Length == 4 && Finite(a))
                    {
                        value = new Vector4((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Rect:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "rect" && a.Length == 4 && Finite(a))
                    {
                        value = new RectangleF((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                        return true;
                    }
                    return false;
                default:
                    return false;
            }

            static bool Finite(double[] values) => values.All(static x => float.IsFinite((float)x));
        }

        // [+|-] number
        static bool TryNumber(List<Token> t, int s, int e, out double value)
        {
            value = 0;
            var sign = 1.0;
            if (s < e && (t[s].IsPunct('-') || t[s].IsPunct('+')))
            {
                if (t[s].IsPunct('-'))
                    sign = -1;
                s++;
            }
            if (e - s != 1 || t[s].Kind != TokenKind.Number)
                return false;
            var text = t[s].Text;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                    return false;
                value = sign * hex;
                return true;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                return false;
            value = sign * d;
            return true;
        }

        // Qt.name(number, number, ...) over the whole range s..e
        static bool TryQtCall(List<Token> t, int s, int e, out string name, out double[] args)
        {
            name = string.Empty;
            args = Array.Empty<double>();
            if (e - s < 5 || !t[s].IsIdent("Qt") || !t[s + 1].IsPunct('.') || t[s + 2].Kind != TokenKind.Ident || !t[s + 3].IsPunct('(') || !t[e - 1].IsPunct(')'))
                return false;
            name = t[s + 2].Text;
            var list = new List<double>();
            var from = s + 4;
            for (var j = s + 4; j < e; j++)
            {
                if (j == e - 1 || t[j].IsPunct(','))
                {
                    if (!TryNumber(t, from, j, out var d))
                        return false;
                    list.Add(d);
                    from = j + 1;
                }
            }
            args = list.ToArray();
            return true;
        }

        static bool TryColor(string text, out Color4 color)
        {
            text = text.Trim();
            color = default;
            if (!text.StartsWith('#'))
                return s_namedColors.Value.TryGetValue(text.ToLowerInvariant(), out color);

            var hex = text.AsSpan(1);
            if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
                return false;
            switch (hex.Length)
            {
                case 3:
                    color = FromBytes(((v >> 8) & 0xF) * 17, ((v >> 4) & 0xF) * 17, (v & 0xF) * 17, 255);
                    return true;
                case 6:
                    color = FromBytes((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, 255);
                    return true;
                case 8:
                    color = FromBytes((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, v >> 24);
                    return true;
                default:
                    return false;
            }
        }

        static Color4 FromBytes(uint r, uint g, uint b, uint a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        // SVG colour names (what QML accepts) from the named colours of Stride; "grey" spellings, transparent and rebeccapurple added.
        static Dictionary<string, Color4> BuildNamedColors()
        {
            var map = new Dictionary<string, Color4>(StringComparer.Ordinal);

            void Add(string name, Color c)
            {
                var key = name.ToLowerInvariant();
                var color = new Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
                map[key] = color;
                if (key.Contains("gray", StringComparison.Ordinal))
                    map[key.Replace("gray", "grey", StringComparison.Ordinal)] = color;
            }

            foreach (var f in typeof(Color).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(Color))
                    Add(f.Name, (Color)f.GetValue(null)!);
            foreach (var p in typeof(Color).GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (p.PropertyType == typeof(Color) && p.GetIndexParameters().Length == 0)
                    Add(p.Name, (Color)p.GetValue(null)!);
            map.TryAdd("transparent", new Color4(0f, 0f, 0f, 0f));
            map.TryAdd("rebeccapurple", FromBytes(0x66, 0x33, 0x99, 255));
            return map;
        }

        // ---- signature -----------------------------------------------------------------------------------------------------

        public static string Signature(QmlDeclarations d)
        {
            const char sep = '\u001f';
            var sb = new StringBuilder();
            sb.Append("head").Append(sep).Append(d.Head.Category).Append(sep).Append(d.Head.Summary).Append(sep).AppendJoin(',', d.Head.Tags).Append('\n');
            foreach (var p in d.Properties)
                sb.Append('p').Append(sep).Append(p.Name).Append(sep).Append(p.QmlType).Append(sep).Append(p.Kind).Append(sep).Append((int)p.Flags)
                    .Append(sep).Append(p.Initializer).Append(sep).Append(Format(p.Default)).Append(sep).Append(p.Summary).Append('\n');
            foreach (var s in d.Signals)
            {
                sb.Append('s').Append(sep).Append(s.Name).Append(sep).Append(s.Summary);
                foreach (var a in s.Args)
                    sb.Append(sep).Append(a.Name).Append(':').Append(a.QmlType).Append(':').Append(a.Kind);
                sb.Append('\n');
            }
            foreach (var m in d.Messages)
                sb.Append('m').Append(sep).Append(m.Severity).Append(sep).Append(m.File).Append(sep).Append(m.Text).Append('\n');
            return sb.ToString();
        }

        static string Format(object value)
        {
            static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            return value switch
            {
                float f => F(f),
                int i => i.ToString(CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                string s => s,
                Color4 c => $"{F(c.R)},{F(c.G)},{F(c.B)},{F(c.A)}",
                Vector2 v => $"{F(v.X)},{F(v.Y)}",
                Vector3 v => $"{F(v.X)},{F(v.Y)},{F(v.Z)}",
                Vector4 v => $"{F(v.X)},{F(v.Y)},{F(v.Z)},{F(v.W)}",
                RectangleF r => $"{F(r.X)},{F(r.Y)},{F(r.Width)},{F(r.Height)}",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
        }
    }
}
