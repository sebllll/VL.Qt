using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static VL.Qt.Qml.QmlLexer;

namespace VL.Qt.Qml
{
    /// <summary>Reads the head tags (<c>@category</c>, <c>@summary</c>, <c>@tags</c>) of a .qml file and skips its imports and pragmas.</summary>
    internal static class QmlHeadReader
    {
        private static readonly Regex s_tagStart = new(@"^@(?<name>\w+)\s*(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Skips imports, pragmas and ".import"/".pragma" lines (everything on their line), returns the index of the root type.
        public static int SkipHeader(List<Token> tokens)
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

        public static int HeadEndLine(List<Token> tokens)
        {
            var i = SkipHeader(tokens);
            return i < tokens.Count ? tokens[i].Line : int.MaxValue;
        }

        public static QmlFileHead Read(List<Comment> comments, int endLine, List<QmlParseMessage> warnings)
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
    }
}
