using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VL.Core;

namespace VL.Qt.Qml
{
    /// <summary>A .qml file found in a qml/ tree: one component file = one node.</summary>
    /// <param name="FilePath">Full path of the .qml file.</param>
    /// <param name="Name">Node name from the file name, variant split off like the shader nodes ("Gauge_Round.qml" → "Gauge").</param>
    /// <param name="Version">Variant ("Round"), null if none.</param>
    /// <param name="Category">@category of the file head, else <see cref="QmlNodeFactory.DefaultCategory"/>.</param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="Tags">@tags of the file head, joined with commas for <c>NodeBuilding.NewNodeDescription(…, tags)</c>; empty if none.</param>
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
        public string FullName => Version is null ? Name : $"{Name} ({Version})";
    }

    /// <summary>
    /// Node factory for qml/**/*.qml next to a document or in a package (#107), the same mechanism as
    /// <see cref="Shaders.ShaderNodeFactory"/>: the scan reads only the head tags (category, summary, tags), the pins come
    /// lazily in the init of the node description (<see cref="QmlNodeDescription"/>) from <see cref="QmlDeclarations.Parse"/>.
    /// Only files whose name starts with an uppercase letter become nodes (QML's rule for component files); the others
    /// (helpers, singletons in lowercase, main.qml) are skipped silently. The node instance (<see cref="QmlNodeInstance"/>)
    /// wraps a <see cref="Nodes.QmlElementNode"/>.
    /// </summary>
    internal static class QmlNodeFactory
    {
        /// <summary>Identifier of the factory in the AppHost's NodeFactoryCache.</summary>
        public const string Identifier = "VL.Qt.Qml";

        /// <summary>
        /// Folder name searched next to documents and in packages: Qt projects keep their QML in qml/, and no other vvvv library
        /// claims that name (unlike shaders/, which belongs to Stride).
        /// </summary>
        // Qt's own deployed QML modules live under runtimes/<rid>/native/qml of the VL.Qt package, not at a package or document
        // root, so this factory never sees them.
        public const string FolderName = "qml";

        /// <summary>Category of a generated node without @category.</summary>
        public const string DefaultCategory = Categories.Elements + ".Qml";

        const string extension = ".qml";

        /// <summary>
        /// Registers the factory at the app host (to be called from <see cref="Initialization.Configure"/> by T3 of #107, not yet),
        /// over the app host's NodeFactoryCache like the shader factory.
        /// </summary>
        public static void Register(AppHost appHost)
        {
            var logger = appHost.LoggerFactory?.CreateLogger(Identifier) ?? NullLogger.Instance;
            appHost.NodeFactoryRegistry.RegisterNodeFactory(appHost.NodeFactoryCache.GetOrAdd(Identifier, f => Init(f, logger)));
        }

        /// <summary>Factory without nodes of its own; forPath creates one sub factory per document/package folder.</summary>
        public static NodeBuilding.FactoryImpl Init(IVLNodeDescriptionFactory factory, ILogger logger) =>
            NodeBuilding.NewFactoryImpl(forPath: path => f => ForPath(path, f, logger));

        /// <summary>
        /// Nodes of &lt;path&gt;/qml/ (recursive). invalidated: qml/ created/deleted, .qml files added/deleted/renamed. Content
        /// changes go to the node description (<see cref="WatchSignature"/>) and the running instance (QmlElementNode's watcher).
        /// Without the folder: empty factory that still watches path for it.
        /// </summary>
        public static NodeBuilding.FactoryImpl ForPath(string path, IVLNodeDescriptionFactory factory, ILogger logger)
            => throw new NotImplementedException("T3: QML node factory (#107)");

        /// <summary>One node description per file of <paramref name="files"/>.</summary>
        internal static ImmutableArray<IVLNodeDescription> CreateDescriptions(IEnumerable<QmlFile> files, IVLNodeDescriptionFactory factory, ILogger logger)
            => throw new NotImplementedException("T3: QML node factory (#107)");

        /// <summary>Scans a qml/ tree: component files only (<see cref="IsComponentFileName"/>), head tags, duplicate file names.</summary>
        public static ImmutableArray<QmlFile> Scan(string qmlPath)
            => throw new NotImplementedException("T3: QML node factory (#107)");

        /// <summary>Returns true if <paramref name="fileName"/> is a .qml file whose name starts with an uppercase letter.</summary>
        public static bool IsComponentFileName(string? fileName)
            => fileName is not null
                && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fileName) is { Length: > 4 } name
                && char.IsUpper(name[0]);

        /// <summary>Node name and variant from a file name like the shader nodes: "Gauge_Round.qml" → ("Gauge", "Round").</summary>
        public static (string Name, string? Version) GetNodeName(string fileName)
            => throw new NotImplementedException("T3: QML node factory (#107)");

        /// <summary>
        /// Fires when the parsed declaration signature (<see cref="QmlDeclarations.Signature"/>) of <paramref name="file"/> differs
        /// from <paramref name="signature"/>: a save of the file or of a sibling .qml it inherits from (folder of the file,
        /// NodeBuilding.WatchDir). The invalidated stream of the node description.
        /// </summary>
        public static IObservable<object> WatchSignature(QmlFile file, string signature)
            => throw new NotImplementedException("T3: QML node factory (#107)");
    }
}
