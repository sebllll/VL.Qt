using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>A shader file found in a qtshaders/ tree: one .frag = one node, a .vert with the same name belongs to it.</summary>
    /// <param name="FragmentPath">Full path of the .frag.</param>
    /// <param name="VertexPath">Full path of the .vert next to it (same folder, same base name), null if there is none.</param>
    /// <param name="Name">Node name from the file name (variant split off, see <see cref="FileNodeNames.Split"/>).</param>
    /// <param name="Version">Variant ("Glitch_RGB.frag" → "RGB"), null if none.</param>
    /// <param name="Category">@category of the file head, else <see cref="ShaderNodeFactory.DefaultCategory"/>.</param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="DuplicateError">
    /// Error if another .frag with the same file name exists in the same qtshaders/ tree (both paths), else null.
    /// </param>
    /// <param name="IsFilter">
    /// The .frag declares <c>sampler2D source</c>: besides the element node it gets a link node for the effect chain
    /// (<see cref="ShaderNodeFactory.LinkCategory"/>).
    /// </param>
    /// <param name="ChainOnly">The file head has <c>@chainonly</c>: only the link node of a filter is created, no element node.</param>
    internal sealed record ShaderFile(
        string FragmentPath,
        string? VertexPath,
        string Name,
        string? Version,
        string Category,
        string? Summary,
        string? DuplicateError,
        bool IsFilter = false,
        bool ChainOnly = false)
    {
        /// <summary>Name as vvvv shows it: "Glitch (RGB)", or just "Glitch" without variant.</summary>
        public string FullName => FileNodeNames.FullName(Name, Version);

        /// <summary>
        /// Category of the effect chain node: the @category of a @chainonly file (it has no element node), else always
        /// <see cref="ShaderNodeFactory.LinkCategory"/>.
        /// </summary>
        public string LinkCategory => ChainOnly ? Category : ShaderNodeFactory.LinkCategory;
    }
}
