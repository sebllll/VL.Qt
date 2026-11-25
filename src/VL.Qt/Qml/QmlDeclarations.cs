using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Stride.Core.Mathematics;

namespace VL.Qt.Qml
{
    /// <summary>
    /// The root declarations of a .qml file (docs/decisions/user-qml.md): found by a C# parser (<see cref="QmlDeclarationParser"/>)
    /// that needs no Qt and runs no user code. If the root type is a sibling file of the same folder (<c>Gauge { }</c> + Gauge.qml),
    /// its declarations come first (recursive, a cycle gives a warning and stops); a redeclaration in the derived file replaces
    /// the inherited one in place.
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
        /// initializer kinds, defaults (invariant culture), summaries, signal arguments, message texts; the line of an error or a warning
        /// (infos only list names, so they do not change when lines move). The factory invalidates the
        /// description only when this changes (hot reload of pins); any other edit only reloads the running instance.
        /// </summary>
        public string Signature
        {
            get
            {
                const char sep = '\u001f';
                var sb = new StringBuilder();
                sb.Append("head").Append(sep).Append(Head.Category).Append(sep).Append(Head.Summary).Append(sep).AppendJoin(',', Head.Tags).Append('\n');
                foreach (var p in Properties)
                    sb.Append('p').Append(sep).Append(p.Name).Append(sep).Append(p.QmlType).Append(sep).Append(p.Kind).Append(sep).Append((int)p.Flags)
                        .Append(sep).Append(p.Initializer).Append(sep).Append(Format(p.Default)).Append(sep).Append(p.Summary).Append('\n');
                foreach (var s in Signals)
                {
                    sb.Append('s').Append(sep).Append(s.Name).Append(sep).Append(s.Summary);
                    foreach (var a in s.Args)
                        sb.Append(sep).Append(a.Name).Append(':').Append(a.QmlType).Append(':').Append(a.Kind);
                    sb.Append('\n');
                }
                foreach (var m in Messages)
                    sb.Append('m').Append(sep).Append(m.Severity).Append(sep).Append(m.File).Append(sep).Append(m.Severity == QmlMessageSeverity.Info ? 0 : m.Line).Append(sep).Append(m.Text).Append('\n');
                return sb.ToString();
            }
        }

        /// <summary>Parses <paramref name="source"/> (the content of a .qml file).</summary>
        /// <param name="source">Content of the file.</param>
        /// <param name="ownTypeName">Type name of the file itself (its name without ".qml"); a root type of the same name is an error.</param>
        /// <param name="readSibling">
        /// Content of "&lt;TypeName&gt;.qml" in the same folder for a root type name, null if there is none or it cannot be read.
        /// </param>
        public static QmlDeclarations Parse(string source, string ownTypeName, Func<string, string?> readSibling)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(ownTypeName);
            ArgumentNullException.ThrowIfNull(readSibling);
            return QmlDeclarationParser.Parse(source, readSibling, ImmutableList.Create(ownTypeName), false);
        }

        /// <summary>Only the head tags: cheap, for the scan of the factory (node browser), reads no sibling.</summary>
        public static QmlFileHead ParseHead(string source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return QmlDeclarationParser.ParseHead(source);
        }

        private static string Format(object value)
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
