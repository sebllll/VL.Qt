using System;

namespace VL.Qt.Qml
{
    /// <summary>A property declared in the root object (of the file or of a sibling root type) that gets pins.</summary>
    /// <param name="Name">Name in QML (key sent to QtNative).</param>
    /// <param name="QmlType">Type as written ("real", "double", "color", …); for an alias the type of its target.</param>
    /// <param name="Kind">Value kind (only supported types become a <see cref="QmlPropertyDecl"/>).</param>
    /// <param name="Flags">readonly, required, default.</param>
    /// <param name="Initializer">
    /// How the declaration is initialised; for an inherited property an assignment <c>name: …</c> in the root of the derived file replaces it.
    /// </param>
    /// <param name="Default">Pin default as a value of <see cref="QmlTypes.ClrType"/>: the literal, else <see cref="QmlTypes.TypeDefault"/>.</param>
    /// <param name="Summary">The <c>//</c> comment lines directly above the declaration, joined with spaces; null if none.</param>
    /// <param name="Line">1-based line of the declaration in <paramref name="DeclaredIn"/> (or in the file itself).</param>
    /// <param name="DeclaredIn">File name of the sibling root type that declares it ("Gauge.qml"), null for the file itself.</param>
    /// <param name="AliasOf">For <c>property alias</c>: the target as written ("label.text"), null for any other declaration.</param>
    internal sealed record QmlPropertyDecl(
        string Name,
        string QmlType,
        QmlValueKind Kind,
        QmlPropertyFlags Flags,
        QmlInitializer Initializer,
        object Default,
        string? Summary,
        int Line,
        string? DeclaredIn = null,
        string? AliasOf = null)
    {
        /// <summary>CLR type of the pins of this property.</summary>
        public Type ClrType => QmlTypes.ClrType(Kind);

        /// <summary>True for a <c>list&lt;T&gt;</c> property: its pins are Spreads.</summary>
        public bool IsList => QmlTypes.IsList(Kind);

        /// <summary>True for <c>readonly property</c>.</summary>
        public bool IsReadOnly => (Flags & QmlPropertyFlags.ReadOnly) != 0;

        /// <summary>True for <c>required property</c>.</summary>
        public bool IsRequired => (Flags & QmlPropertyFlags.Required) != 0;
    }
}
