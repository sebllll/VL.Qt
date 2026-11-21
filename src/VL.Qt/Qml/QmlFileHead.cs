using System.Collections.Immutable;

namespace VL.Qt.Qml
{
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
}
