using System;
using System.Collections.Immutable;
using System.Linq;

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
        public string Signature => throw new NotImplementedException("T2: QML parser (#107)");

        /// <summary>
        /// Parses <paramref name="source"/> (the content of a .qml file).
        /// </summary>
        /// <param name="source">Content of the file.</param>
        /// <param name="readSibling">Content of "&lt;TypeName&gt;.qml" in the same folder for a root type name, null if there is none or it cannot be read.</param>
        public static QmlDeclarations Parse(string source, Func<string, string?> readSibling)
            => throw new NotImplementedException("T2: QML parser (#107)");

        /// <summary>Only the head tags: cheap, for the scan of the factory (node browser), reads no sibling.</summary>
        public static QmlFileHead ParseHead(string source)
            => throw new NotImplementedException("T2: QML parser (#107)");
    }
}
