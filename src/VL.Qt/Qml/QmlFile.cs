using VL.Qt.NodeFactories;

namespace VL.Qt.Qml
{
    /// <summary>A .qml file found in a qml/ tree: one component file = one node.</summary>
    /// <param name="FilePath">Full path of the .qml file.</param>
    /// <param name="Name">Node name from the file name, variant split off like the shader nodes ("Gauge_Round.qml" → "Gauge").</param>
    /// <param name="Version">Variant ("Round"), null if none.</param>
    /// <param name="Category">@category of the file head, else <see cref="QmlNodeFactory.DefaultCategory"/>.</param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="Tags">
    /// @tags of the file head, joined with commas for <c>NodeBuilding.NewNodeDescription(…, tags)</c>; empty if none.
    /// </param>
    /// <param name="DuplicateError">Error if another .qml with the same file name exists in the same qml/ tree, else null.</param>
    internal sealed record QmlFile(
        string FilePath,
        string Name,
        string? Version,
        string Category,
        string? Summary,
        string Tags,
        string? DuplicateError)
    {
        /// <summary>Name as vvvv shows it: "Gauge (Round)", or just "Gauge" without variant.</summary>
        public string FullName => FileNodeNames.FullName(Name, Version);
    }
}
