using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>A shader file found in a qtshaders/ tree: one .frag = one node, a .vert with the same name belongs to it.</summary>
    /// <param name="FragmentPath">Full path of the .frag.</param>
    /// <param name="VertexPath">Full path of the .vert next to it (same folder, same base name), null if there is none.</param>
    /// <param name="Name">Node name from the file name (variant split off, see <see cref="FileNodeNames.Split"/>).</param>
    /// <param name="Version">Variant ("Glitch_RGB.frag" → "RGB"), null if none.</param>
    /// <param name="Category">
    /// Category of the element node from the @category of the file head (<see cref="ShaderNodeFactory.ResolveCategories"/>),
    /// else <see cref="ShaderNodeFactory.DefaultCategory"/>; with @chainonly the one of the chain node.
    /// </param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="DuplicateError">
    /// Error if a node of this file has the same name and category as the node of another .frag in the same qtshaders/ tree
    /// (all paths) or as a built-in effect node, else null.
    /// </param>
    /// <param name="IsFilter">
    /// The .frag declares <c>sampler2D source</c>: besides the element node it gets a link node for the effect chain
    /// (<see cref="ShaderNodeFactory.LinkCategory"/>).
    /// </param>
    /// <param name="ChainOnly">The file head has <c>@chainonly</c>: only the link node of a filter is created, no element node.</param>
    /// <param name="ChainCategory">Category of the effect chain node, null = <see cref="ShaderNodeFactory.LinkCategory"/>.</param>
    internal sealed record ShaderFile(
        string FragmentPath,
        string? VertexPath,
        string Name,
        string? Version,
        string Category,
        string? Summary,
        string? DuplicateError,
        bool IsFilter = false,
        bool ChainOnly = false,
        string? ChainCategory = null)
    {
        /// <summary>Name as vvvv shows it: "Glitch (RGB)", or just "Glitch" without variant.</summary>
        public string FullName => FileNodeNames.FullName(Name, Version);

        /// <summary>Category of the effect chain node (only a filter has one).</summary>
        public string LinkCategory => ChainCategory ?? ShaderNodeFactory.LinkCategory;
    }
}
