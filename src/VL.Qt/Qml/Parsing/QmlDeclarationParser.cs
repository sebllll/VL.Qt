using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static VL.Qt.Qml.QmlLexer;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The parser behind <see cref="QmlDeclarations"/>: scans the members of the root object at depth 1 of the tokens of
    /// <see cref="QmlLexer"/>. A member ends at a <c>;</c>, at the closing bracket that empties the nesting, or at a line break that
    /// does not continue the expression (a line that ends with or starts with an operator continues it). For the aliases of the root
    /// it also reads the nested objects: their ids, the properties they declare and their assignments (<see cref="AliasResolver"/>).
    /// </summary>
    internal static class QmlDeclarationParser
    {
        private static readonly Regex s_internalTag = new(@"^@internal(\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ---- entry points --------------------------------------------------------------------------------------------------

        public static QmlFileHead ParseHead(string source)
        {
            var lexed = Lex(source, true);
            var warnings = new List<QmlParseMessage>();
            return QmlHeadReader.Read(lexed.Comments, QmlHeadReader.HeadEndLine(lexed.Tokens), warnings);
        }

        public static QmlDeclarations Parse(string source, Func<string, string?> readSibling, ImmutableList<string> chain, bool isSibling)
        {
            var lexed = Lex(source, false);
            var tokens = lexed.Tokens;
            var headWarnings = new List<QmlParseMessage>();
            var head = QmlHeadReader.Read(lexed.Comments, QmlHeadReader.HeadEndLine(tokens), headWarnings);
            if (isSibling)
                headWarnings.Clear();

            QmlDeclarations Fail(string? rootType, int line, string text)
                => new(rootType, head, ImmutableArray<QmlPropertyDecl>.Empty, ImmutableArray<QmlSignalDecl>.Empty,
                    ImmutableArray.Create(new QmlParseMessage(line, QmlMessageSeverity.Error, text)));

            if (lexed.Error != null)
                return Fail(null, lexed.ErrorLine, lexed.Error);

            // Root object: [imports and pragmas] Type { ... }
            var i = QmlHeadReader.SkipHeader(tokens);
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
            // The first entry of the chain is the type of the file itself; QML rejects a file that instantiates its own type.
            if (!isSibling && chain.Count > 0 && rootType == chain[0])
                return Fail(rootType, rootLine, $"'{rootType}.qml' cannot have its own type '{rootType}' as root type");
            var rootOpen = tokens[k];
            k++;

            // The objects with an id anywhere in the file, for the aliases of the root; a body that does not scan is reported below.
            var ids = new Dictionary<string, ObjectInfo>(StringComparer.Ordinal);
            CollectObject(tokens, k, new ObjectInfo(rootType), ids);
            var aliases = new AliasResolver(tokens, ids, readSibling, chain);

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
                ReadMember(tokens, k, end, ownLine, inherited, aliases, properties, signals, messages);
                k = end;
            }

            if (rootEnd + 1 < tokens.Count)
                return Fail(rootType, tokens[rootEnd + 1].Line, "unexpected content after the root object");

            return new QmlDeclarations(rootType, head, properties.ToImmutableArray(), signals.ToImmutableArray(), messages.ToImmutableArray());
        }

        private static void Inherit(
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
                    $"root type '{rootType}' is part of a cycle of sibling root types ({string.Join(" -> ", chain.Append(rootType))}), "
                    + "its declarations are skipped"));
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

        // ---- members -------------------------------------------------------------------------------------------------------

        private static Dictionary<int, string> OwnLineComments(List<Comment> comments)
        {
            var map = new Dictionary<int, string>();
            foreach (var c in comments)
                if (c.OwnLine)
                    map[c.Line] = c.Text.TrimStart('/').Trim();
            return map;
        }

        // The "//" lines directly above a declaration (no blank line, no code in between); an empty "//" ends the block.
        // A line "@internal" in the block marks the declaration as internal (no pins); the rest of the block is then no summary.
        private static string? SummaryAbove(Dictionary<int, string> ownLine, int line, out bool tagged)
        {
            tagged = false;
            List<string>? lines = null;
            for (var l = line - 1; ownLine.TryGetValue(l, out var text) && text.Length > 0; l--)
            {
                tagged |= s_internalTag.IsMatch(text);
                (lines ??= new List<string>()).Add(text);
            }
            if (lines == null || tagged)
                return null;
            lines.Reverse();
            return string.Join(' ', lines);
        }

        // Internal declarations are dropped here: no pin, no message, and (since they never reach the lists) not in the signature.
        private static bool IsInternal(bool tagged, string name) => tagged || name.StartsWith('_');

        // Returns the index after the member that starts at s (the first token of a statement inside the root object), -1 on error.
        private static int ScanMember(List<Token> t, int s, Stack<(char Open, int Line)> stack, out string? error, out int errorLine)
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
        private static bool Continues(Token prev, Token next)
        {
            if (next.Kind == TokenKind.Punct && "{([.?:+-*/%&|^<>=,".Contains(next.Text[0]))
                return true;
            if (next.Kind == TokenKind.Ident && next.Text is "else" or "catch" or "finally" or "instanceof" or "in")
                return true;
            return prev.Kind == TokenKind.Punct && "+-*/%&|^<>=!?:,.([".Contains(prev.Text[0]);
        }

        private static void ReadMember(
            List<Token> t,
            int s,
            int e,
            Dictionary<int, string> ownLine,
            HashSet<string> inherited,
            AliasResolver aliases,
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
                ReadProperty(t, s, k, e, flags, SummaryAbove(ownLine, line, out var tagged), tagged, aliases, properties, messages);
            else if (t[s].IsIdent("signal") && s + 1 < e && t[s + 1].Kind == TokenKind.Ident && !(s + 2 < e && t[s + 2].IsPunct(':')))
                ReadSignal(t, s, e, SummaryAbove(ownLine, line, out var tagged), tagged, signals);
            else if (t[s].Kind == TokenKind.Ident && s + 1 < e && t[s + 1].IsPunct(':') && inherited.Contains(t[s].Text))
                Reassign(t, s, e, properties);
        }

        private static int TrimSemicolons(List<Token> t, int start, int end)
        {
            while (end > start && t[end - 1].IsPunct(';'))
                end--;
            return end;
        }

        private static void ReadProperty(
            List<Token> t,
            int s,
            int propertyIndex,
            int e,
            QmlPropertyFlags flags,
            string? summary,
            bool tagged,
            AliasResolver aliases,
            List<QmlPropertyDecl> properties,
            List<QmlParseMessage> messages)
        {
            var line = t[s].Line;
            if (!TryReadDeclaration(t, propertyIndex + 1, e, out var type, out var name, out var m))
            {
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Warning, "incomplete property declaration is skipped"));
                return;
            }

            if (IsInternal(tagged, name))
            {
                // Like a type without a pin: an inherited pin of the same name is gone.
                properties.RemoveAll(p => p.Name == name);
                return;
            }

            if (type == "alias")
            {
                ReadAlias(t, m, e, name, flags, summary, line, aliases, properties, messages);
                return;
            }

            if (!QmlTypes.TryMap(type, out var kind))
            {
                // The inherited pin of the same name is gone: the property is now of a type that gets no pin.
                properties.RemoveAll(p => p.Name == name);
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Info, $"property '{name}' ({type})"));
                return;
            }

            var initializer = QmlInitializer.None;
            var value = QmlTypes.TypeDefault(kind);
            if (m < e && t[m].IsPunct(':'))
            {
                var end = TrimSemicolons(t, m + 1, e);
                if (end > m + 1)
                    (initializer, value) = QmlLiteral.Evaluate(kind, t, m + 1, end);
            }

            Put(properties, new QmlPropertyDecl(name, type, kind, flags, initializer, value, summary, line));
        }

        // A redeclaration replaces the inherited declaration in place.
        private static void Put(List<QmlPropertyDecl> properties, QmlPropertyDecl decl)
        {
            var index = properties.FindIndex(p => p.Name == decl.Name);
            if (index >= 0)
                properties[index] = decl;
            else
                properties.Add(decl);
        }

        // "type name" from m (after "property"): next is the index after the name. The brackets of a list nest: list<list<int>> is
        // one type (without a pin, but not a broken declaration).
        private static bool TryReadDeclaration(List<Token> t, int m, int e, out string type, out string name, out int next)
        {
            type = string.Empty;
            name = string.Empty;
            next = m;
            if (m >= e || t[m].Kind != TokenKind.Ident)
                return false;
            if (t[m].Text == "list" && m + 1 < e && t[m + 1].IsPunct('<'))
            {
                var sb = new StringBuilder("list");
                var depth = 0;
                m++;
                do
                {
                    if (t[m].IsPunct('<'))
                        depth++;
                    else if (t[m].IsPunct('>'))
                        depth--;
                    sb.Append(t[m++].Text);
                }
                while (m < e && depth > 0);
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
                return false;
            name = t[m].Text;
            next = m + 1;
            return true;
        }

        // "property alias name: id.property": a pin of the type of the target, or an info with the reason why there is none.
        private static void ReadAlias(
            List<Token> t,
            int m,
            int e,
            string name,
            QmlPropertyFlags flags,
            string? summary,
            int line,
            AliasResolver aliases,
            List<QmlPropertyDecl> properties,
            List<QmlParseMessage> messages)
        {
            var from = m < e && t[m].IsPunct(':') ? m + 1 : e;
            var to = TrimSemicolons(t, from, e);
            if (!aliases.TryResolve(from, to, 0, out var target, out var reason))
            {
                // The inherited pin of the same name is gone, as for a type without a pin.
                properties.RemoveAll(p => p.Name == name);
                var of = to > from ? " of " + string.Concat(t.Skip(from).Take(to - from).Select(static x => x.Text)) : string.Empty;
                messages.Add(new QmlParseMessage(line, QmlMessageSeverity.Info, $"property '{name}' (alias{of}): {reason}"));
                return;
            }
            if (target.ReadOnly)
                flags |= QmlPropertyFlags.ReadOnly;
            Put(properties, new QmlPropertyDecl(name, target.QmlType, target.Kind, flags, target.Initializer, target.Default, summary, line,
                AliasOf: target.Path));
        }

        // "name: value" in the root of a derived file replaces the initializer of an inherited property.
        private static void Reassign(List<Token> t, int s, int e, List<QmlPropertyDecl> properties)
        {
            var name = t[s].Text;
            var index = properties.FindIndex(p => p.Name == name);
            if (index < 0)
                return;
            var end = TrimSemicolons(t, s + 2, e);
            if (end <= s + 2)
                return;
            var (initializer, value) = QmlLiteral.Evaluate(properties[index].Kind, t, s + 2, end);
            properties[index] = properties[index] with { Initializer = initializer, Default = value };
        }

        private static void ReadSignal(List<Token> t, int s, int e, string? summary, bool tagged, List<QmlSignalDecl> signals)
        {
            var name = t[s + 1].Text;
            if (IsInternal(tagged, name))
            {
                signals.RemoveAll(x => x.Name == name);
                return;
            }

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

        // ---- objects with an id, for the aliases of the root ---------------------------------------------------------------

        /// <summary>An object of the file: its type, the properties it declares and its assignments <c>name: value</c> (token ranges).</summary>
        private sealed class ObjectInfo
        {
            public ObjectInfo(string type) => Type = type;

            public string Type { get; }

            public Dictionary<string, (string Type, bool ReadOnly, int Start, int End)> Properties { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, (int Start, int End)> Assignments { get; } = new(StringComparer.Ordinal);
        }

        // Reads the members of an object body from the token after its '{', nested objects included, and lists every object that
        // has an id. Returns the index of the closing '}', -1 if the body does not scan (the main loop reports why).
        private static int CollectObject(List<Token> t, int start, ObjectInfo obj, Dictionary<string, ObjectInfo> ids)
        {
            var stack = new Stack<(char Open, int Line)>();
            var k = start;
            while (k < t.Count && !t[k].IsPunct('}'))
            {
                var end = ScanMember(t, k, stack, out var error, out _);
                if (error != null)
                    return -1;
                ReadObjectMember(t, k, end, obj, ids);
                k = end;
            }
            return k < t.Count ? k : -1;
        }

        private static void ReadObjectMember(List<Token> t, int s, int e, ObjectInfo obj, Dictionary<string, ObjectInfo> ids)
        {
            e = TrimSemicolons(t, s, e);
            if (e - s >= 3 && t[s].Kind == TokenKind.Ident && t[s + 1].IsPunct(':'))
            {
                var name = t[s].Text;
                if (name == "id")
                {
                    // The first object with an id wins, as the QML engine refuses a file that has one twice.
                    if (e - s == 3 && t[s + 2].Kind == TokenKind.Ident)
                        ids.TryAdd(t[s + 2].Text, obj);
                    return;
                }
                // "name: Type { … }" is an object as the value of a property; anything else an assignment.
                if (!TryNestedObject(t, s + 2, e, ids))
                    obj.Assignments[name] = (s + 2, e);
                return;
            }

            var k = s;
            var readOnly = false;
            for (; k < e && t[k].Kind == TokenKind.Ident && t[k].Text is "default" or "required" or "readonly"; k++)
                readOnly |= t[k].Text == "readonly";
            if (k < e && t[k].IsIdent("property"))
            {
                if (TryReadDeclaration(t, k + 1, e, out var type, out var name, out var m))
                {
                    var from = m < e && t[m].IsPunct(':') ? m + 1 : e;
                    obj.Properties[name] = (type, readOnly, from, e);
                }
                return;
            }
            TryNestedObject(t, s, e, ids);
        }

        // "Type { … }" or "Module.Type { … }" over s..e: reads the object, true if it is one.
        private static bool TryNestedObject(List<Token> t, int s, int e, Dictionary<string, ObjectInfo> ids)
        {
            if (s >= e || t[s].Kind != TokenKind.Ident || !char.IsUpper(t[s].Text[0]))
                return false;
            var type = t[s].Text;
            var k = s + 1;
            while (k + 1 < e && t[k].IsPunct('.') && t[k + 1].Kind == TokenKind.Ident)
            {
                type += "." + t[k + 1].Text;
                k += 2;
            }
            if (k >= e || !t[k].IsPunct('{'))
                return false;
            CollectObject(t, k + 1, new ObjectInfo(type), ids);
            return true;
        }

        /// <summary>What an alias of the root points to: the type and default of its pin.</summary>
        private readonly record struct AliasTarget(string QmlType, QmlValueKind Kind, QmlInitializer Initializer, object Default, bool ReadOnly, string Path);

        /// <summary>
        /// Resolves <c>property alias name: id.property</c> without a Qt engine: the property is one the object declares in the file
        /// (an alias of it is followed), one of the built-in types of <see cref="QmlBuiltinProperties"/>, or one the sibling file of
        /// the object's type declares (with that file's own base types). The default is the literal the object assigns to it, else
        /// the literal of the declaration, else the value Qt gives it.
        /// </summary>
        private sealed class AliasResolver
        {
            private const int maxDepth = 8;
            private readonly List<Token> _t;
            private readonly Dictionary<string, ObjectInfo> _ids;
            private readonly Func<string, string?> _readSibling;
            private readonly ImmutableList<string> _chain;
            private readonly Dictionary<string, QmlDeclarations?> _siblings = new(StringComparer.Ordinal);

            public AliasResolver(List<Token> t, Dictionary<string, ObjectInfo> ids, Func<string, string?> readSibling, ImmutableList<string> chain)
            {
                _t = t;
                _ids = ids;
                _readSibling = readSibling;
                _chain = chain;
            }

            /// <summary>
            /// Returns true if the tokens s..e are <c>id.property</c> of a property with a pin type; else <paramref name="reason"/> says why not.
            /// </summary>
            public bool TryResolve(int s, int e, int depth, out AliasTarget target, out string reason)
            {
                target = default;
                var parts = new List<string>();
                for (var j = s; j < e; j++)
                {
                    var dot = (j - s) % 2 == 1;
                    if (dot ? !_t[j].IsPunct('.') : _t[j].Kind != TokenKind.Ident)
                    {
                        reason = "the target is not 'id.property'";
                        return false;
                    }
                    if (!dot)
                        parts.Add(_t[j].Text);
                }
                if (parts.Count == 0 || (e - s) % 2 == 0)
                {
                    reason = "the target is not 'id.property'";
                    return false;
                }
                if (parts.Count == 1)
                {
                    reason = $"an alias of the object '{parts[0]}' has no pin";
                    return false;
                }
                if (parts.Count > 2)
                {
                    reason = $"an alias of a grouped property ({string.Join('.', parts.Skip(1))}) has no pin";
                    return false;
                }
                if (!_ids.TryGetValue(parts[0], out var obj))
                {
                    reason = $"'{parts[0]}' is no id in this file";
                    return false;
                }
                return TryProperty(obj, parts[1], parts[0] + "." + parts[1], depth, out target, out reason);
            }

            private bool TryProperty(ObjectInfo obj, string name, string path, int depth, out AliasTarget target, out string reason)
            {
                target = default;
                if (depth > maxDepth)
                {
                    reason = "the aliases point to each other too deep";
                    return false;
                }

                string type;
                QmlValueKind kind;
                QmlInitializer initializer;
                object value;
                bool readOnly;
                if (obj.Properties.TryGetValue(name, out var declared))
                {
                    if (declared.Type == "alias")
                    {
                        if (!TryResolve(declared.Start, TrimSemicolons(_t, declared.Start, declared.End), depth + 1, out target, out reason))
                            return false;
                        target = target with { ReadOnly = target.ReadOnly || declared.ReadOnly, Path = path };
                        return true;
                    }
                    if (!QmlTypes.TryMap(declared.Type, out kind))
                    {
                        reason = $"'{path}' is of the type {declared.Type}, which has no pin";
                        return false;
                    }
                    type = declared.Type;
                    readOnly = declared.ReadOnly;
                    (initializer, value) = declared.End > declared.Start
                        ? QmlLiteral.Evaluate(kind, _t, declared.Start, declared.End)
                        : (QmlInitializer.None, QmlTypes.TypeDefault(kind));
                }
                else if (!TryTypeProperty(obj.Type, name, depth, out type, out kind, out initializer, out value, out readOnly))
                {
                    reason = $"'{path}' is no property of '{obj.Type}' that the node knows (the built-in ones it knows are listed in the help)";
                    return false;
                }

                // An assignment on the object replaces the default.
                if (obj.Assignments.TryGetValue(name, out var assigned))
                    (initializer, value) = QmlLiteral.Evaluate(kind, _t, assigned.Start, assigned.End);
                target = new AliasTarget(type, kind, initializer, value, readOnly, path);
                reason = string.Empty;
                return true;
            }

            // A property of a type: the built-in table, else the root declarations of the sibling file of the type, with its base type.
            private bool TryTypeProperty(string typeName, string name, int depth, out string type, out QmlValueKind kind, out QmlInitializer initializer,
                out object value, out bool readOnly)
            {
                initializer = QmlInitializer.None;
                readOnly = false;
                kind = default;
                if (QmlBuiltinProperties.TryGet(typeName, name, out type, out value))
                    return QmlTypes.TryMap(type, out kind);
                if (Sibling(typeName) is not { } sibling)
                    return false;
                if (sibling.Properties.FirstOrDefault(p => p.Name == name) is { } p)
                {
                    (type, kind, initializer, value, readOnly) = (p.QmlType, p.Kind, p.Initializer, p.Default, p.IsReadOnly);
                    return true;
                }
                return sibling.RootType is { } baseType && depth < maxDepth
                    && TryTypeProperty(baseType, name, depth + 1, out type, out kind, out initializer, out value, out readOnly);
            }

            // The declarations of <type>.qml next to the file, null if there is none, it cannot be read or it is part of the chain.
            private QmlDeclarations? Sibling(string typeName)
            {
                if (_siblings.TryGetValue(typeName, out var known))
                    return known;
                QmlDeclarations? parsed = null;
                if (!typeName.Contains('.') && char.IsUpper(typeName[0]) && !_chain.Contains(typeName))
                {
                    try
                    {
                        if (_readSibling(typeName) is { } source)
                            parsed = Parse(source, _readSibling, _chain.Add(typeName), true);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Like an unknown type: the alias gets no pin.
                    }
                }
                _siblings[typeName] = parsed;
                return parsed;
            }
        }
    }
}
