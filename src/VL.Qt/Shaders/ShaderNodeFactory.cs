using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VL.Core;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Node factory for qtshaders/**/*.frag next to a document or in a package, built on <see cref="FolderNodeFactory"/>
    /// (model: VL.Stride EffectShaderNodes.Init/TrackChanges). The scan reads only the annotations (category/summary); bake
    /// and reflection happen lazily in the init of the node description (<see cref="ShaderDescription"/>). The node
    /// instance wraps a <see cref="Nodes.ShaderNode"/> and feeds the file content over the source path.
    /// </summary>
    internal static class ShaderNodeFactory
    {
        /// <summary>Identifier of the factory in the AppHost's NodeFactoryCache.</summary>
        public const string Identifier = "VL.Qt.Shaders";

        /// <summary>Folder name searched next to documents and in packages (not "shaders", that one belongs to Stride).</summary>
        public const string FolderName = "qtshaders";

        /// <summary>Category of a generated node without @category.</summary>
        public const string DefaultCategory = Categories.Shaders;

        /// <summary>
        /// Category of the effect chain node of a filter shader (same name as the element node). This one, @category
        /// moves only the element node; a filter whose @category is this one too gets its element node in
        /// <see cref="DefaultCategory"/>, so the two nodes never share name and category. Exception: with @chainonly there is
        /// no element node and @category is the category of the chain node (default this one), see <see cref="ShaderFile.LinkCategory"/>.
        /// </summary>
        public const string LinkCategory = Categories.Effects;

        private const string fragmentExtension = ".frag";
        private const string vertexExtension = ".vert";

        /// <summary>
        /// Registers the factory at the app host (called from <see cref="Initialization.Configure"/>). Like VL.Stride over
        /// the app host's NodeFactoryCache, so the factory (and its file watchers) lives across compilations and only
        /// invalidates itself when files change; one sub factory per document/package folder.
        /// </summary>
        public static void Register(AppHost appHost)
        {
            var logger = appHost.LoggerFactory?.CreateLogger(Identifier) ?? NullLogger.Instance;
            appHost.RegisterNodeFactory(Identifier, (path, f) => ForPath(path, f, logger));
        }

        /// <summary>
        /// Nodes of &lt;path&gt;/qtshaders/ (recursive). invalidated: qtshaders/ created/deleted or .frag/.vert files
        /// added/deleted/renamed (NodeBuilding.WatchDir). Without the folder: empty factory that still watches path for it.
        /// </summary>
        public static NodeBuilding.FactoryImpl ForPath(string path, IVLNodeDescriptionFactory factory, ILogger logger)
        {
            // One cache for the folder, across scans: a rescan or a recompile does not bake unchanged files again.
            var cache = new ShaderSourceCache(logger);
            return FolderNodeFactory.ForPath(
                path,
                FolderName,
                logger,
                scan: Scan,
                duplicateError: static file => file.DuplicateError,
                createDescriptions: files => CreateDescriptions(files, factory, logger, cache),
                isNewOrDeleted: IsNewOrDeletedShaderFile,
                watchContent: WatchContent,
                headChanged: HeadChanged);
        }

        /// <summary>One element node per file (none for @chainonly), plus the link node of the effect chain for every filter.</summary>
        internal static ImmutableArray<IVLNodeDescription> CreateDescriptions(IEnumerable<ShaderFile> files, IVLNodeDescriptionFactory factory, ILogger logger, ShaderSourceCache? cache = null)
        {
            cache ??= new ShaderSourceCache(logger);
            var nodes = ImmutableArray.CreateBuilder<IVLNodeDescription>();
            foreach (var file in files)
            {
                if (file.ChainOnly && !file.IsFilter)
                    logger.LogWarning("{Shader}: @chainonly needs 'uniform sampler2D source', so there is no node for this file", file.FragmentPath);
                if (!file.ChainOnly)
                    nodes.Add(ShaderDescription.New(factory, file, logger, cache));
                if (file.IsFilter)
                    nodes.Add(ShaderEffectDescription.New(factory, file, logger, cache));
            }
            return nodes.ToImmutable();
        }

        /// <summary>Scans a qtshaders/ tree: pairs .frag/.vert, reads the head annotations, marks duplicate file names.</summary>
        public static ImmutableArray<ShaderFile> Scan(string shadersPath)
        {
            var fragments = Directory.EnumerateFiles(shadersPath, "*" + fragmentExtension, SearchOption.AllDirectories)
                .Where(f => HasExtension(f, fragmentExtension))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var duplicates = fragments
                .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToDictionary(
                    g => g.Key,
                    g => $"The shader file name '{g.Key}' exists more than once in {shadersPath}: {string.Join(", ", g)}. Rename one of them.",
                    StringComparer.OrdinalIgnoreCase);

            var result = ImmutableArray.CreateBuilder<ShaderFile>(fragments.Count);
            foreach (var fragment in fragments)
            {
                var vertex = Path.ChangeExtension(fragment, vertexExtension);
                var (name, version) = FileNodeNames.Split(Path.GetFileNameWithoutExtension(fragment));
                var head = ShaderFileHead.Read(fragment);
                var annotations = head?.Annotations ?? ShaderAnnotations.Empty;
                var isFilter = head?.IsFilter ?? false;
                var hasCategory = !string.IsNullOrWhiteSpace(annotations.Category);
                var category = hasCategory ? annotations.Category!.Trim() : DefaultCategory;
                if (annotations.ChainOnly)
                    category = hasCategory ? category : LinkCategory; // the only node is the chain node
                else if (isFilter && string.Equals(category, LinkCategory, StringComparison.OrdinalIgnoreCase))
                    category = DefaultCategory;
                duplicates.TryGetValue(Path.GetFileName(fragment), out var duplicateError);
                result.Add(new ShaderFile(
                    fragment,
                    File.Exists(vertex) ? vertex : null,
                    name,
                    version,
                    category,
                    annotations.Summary,
                    duplicateError,
                    isFilter,
                    annotations.ChainOnly));
            }
            return result.MoveToImmutable();
        }

        /// <summary>
        /// True if a content change turned a generator into a filter or back, or added or removed @chainonly: that adds or
        /// removes a node. False if the file cannot be read now.
        /// </summary>
        private static bool HeadChanged(ShaderFile file) =>
            ShaderFileHead.Read(file.FragmentPath) is { } head
                && (head.IsFilter != file.IsFilter || head.Annotations.ChainOnly != file.ChainOnly);

        /// <summary>Content change of <paramref name="file"/>'s own .frag/.vert (filtered NodeBuilding.WatchDir of its folder).</summary>
        public static IObservable<object> WatchContent(ShaderFile file)
        {
            var dir = Path.GetDirectoryName(file.FragmentPath)!;
            var fragmentName = Path.GetFileName(file.FragmentPath);
            var vertexName = Path.ChangeExtension(fragmentName, vertexExtension);
            return FolderNodeFactory.WatchFiles(dir, fragmentName, vertexName).Select(static e => (object)e);
        }

        private static bool HasExtension(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        private static bool IsShaderFile(string? name) =>
            name != null && (HasExtension(name, fragmentExtension) || HasExtension(name, vertexExtension));

        /// <summary>Like VL.Stride, only .frag/.vert files count (see <see cref="FolderNodeFactory.IsNewOrDeleted"/>).</summary>
        internal static bool IsNewOrDeletedShaderFile(FileSystemEventArgs e) => FolderNodeFactory.IsNewOrDeleted(e, IsShaderFile);
    }
}
