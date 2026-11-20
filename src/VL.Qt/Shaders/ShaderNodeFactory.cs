using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VL.Core;

namespace VL.Qt.Shaders
{
    /// <summary>A shader file found in a qtshaders/ tree: one .frag = one node, a .vert with the same name belongs to it.</summary>
    /// <param name="FragmentPath">Full path of the .frag.</param>
    /// <param name="VertexPath">Full path of the .vert next to it (same folder, same base name), null if there is none.</param>
    /// <param name="Name">Node name from the file name (variant split off, see <see cref="ShaderNodeFactory.GetNodeName"/>).</param>
    /// <param name="Version">Variant ("Glitch_RGB.frag" → "RGB"), null if none.</param>
    /// <param name="Category">@category of the file head, else <see cref="ShaderNodeFactory.DefaultCategory"/>.</param>
    /// <param name="Summary">@summary of the file head (node help), null if not given.</param>
    /// <param name="DuplicateError">Error if another .frag with the same file name exists in the same qtshaders/ tree (both paths), else null.</param>
    /// <param name="IsFilter">The .frag declares <c>sampler2D source</c>: besides the element node it gets a link node for the effect chain (<see cref="ShaderNodeFactory.LinkCategory"/>).</param>
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
        public string FullName => Version is null ? Name : $"{Name} ({Version})";

        /// <summary>Category of the effect chain node: the @category of a @chainonly file (it has no element node), else always <see cref="ShaderNodeFactory.LinkCategory"/>.</summary>
        public string LinkCategory => ChainOnly ? Category : ShaderNodeFactory.LinkCategory;
    }

    /// <summary>
    /// Node factory for qtshaders/**/*.frag next to a document or in a package (#488). Model: VL.Stride EffectShaderNodes.Init/TrackChanges. The scan reads only the
    /// annotations (category/summary); bake + reflection happen lazily in the init of the node description
    /// (<see cref="ShaderNodeDescription"/>). The node instance wraps a <see cref="Nodes.ShaderEffectNode"/> and feeds the
    /// file content over the source path (#483).
    /// </summary>
    internal static class ShaderNodeFactory
    {
        /// <summary>Identifier of the factory in the AppHost's NodeFactoryCache.</summary>
        public const string Identifier = "VL.Qt.Shaders";

        /// <summary>Folder name searched next to documents and in packages (not "shaders", that one belongs to Stride).</summary>
        public const string FolderName = "qtshaders";

        /// <summary>Category of a generated node without @category.</summary>
        public const string DefaultCategory = Categories.Elements + ".Shaders";

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
        /// invalidates itself when files change.
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
        /// Nodes of &lt;path&gt;/qtshaders/ (recursive). invalidated: qtshaders/ created/deleted or .frag/.vert files
        /// added/deleted/renamed (NodeBuilding.WatchDir). Without the folder: empty factory that still watches path for it.
        /// </summary>
        public static NodeBuilding.FactoryImpl ForPath(string path, IVLNodeDescriptionFactory factory, ILogger logger)
        {
            // qtshaders/ created or deleted: the whole factory again.
            var invalidated = FileSystemUtils.WatchDir(path)
                .Where(e => e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed
                    && string.Equals(e.Name, FolderName, StringComparison.OrdinalIgnoreCase))
                .Select(static e => (object)e);

            var shadersPath = Path.Combine(path, FolderName);
            if (Directory.Exists(shadersPath))
            {
                try
                {
                    var files = Scan(shadersPath);
                    foreach (var file in files)
                    {
                        if (file.DuplicateError != null)
                            logger.LogError("{Error}", file.DuplicateError);
                    }
                    var nodes = CreateDescriptions(files, factory, logger);
                    // Files added, deleted or renamed: the node list again (content changes go to the node itself).
                    invalidated = invalidated.Merge(FileSystemUtils.WatchDir(shadersPath, includeSubdirectories: true)
                        .Where(IsNewOrDeletedShaderFile)
                        .Select(static e => (object)e));
                    // A content change that turns a generator into a filter or back, or adds or removes @chainonly, adds or removes a node: the node list again.
                    foreach (var file in files)
                        invalidated = invalidated.Merge(WatchContent(file)
                            .Where(_ => ReadIsFilter(file.FragmentPath) is { } isFilter
                                && (isFilter != file.IsFilter || ReadHead(file.FragmentPath).ChainOnly != file.ChainOnly)));
                    return NodeBuilding.NewFactoryImpl(nodes, invalidated);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    // Folder deleted while scanning; the watcher above brings the factory back.
                    logger.LogWarning(e, "Scanning {Path} failed: {Message}", shadersPath, e.Message);
                }
            }

            return NodeBuilding.NewFactoryImpl(invalidated: invalidated);
        }

        /// <summary>One element node per file (none for @chainonly), plus the link node of the effect chain for every filter.</summary>
        internal static ImmutableArray<IVLNodeDescription> CreateDescriptions(IEnumerable<ShaderFile> files, IVLNodeDescriptionFactory factory, ILogger logger)
        {
            var nodes = ImmutableArray.CreateBuilder<IVLNodeDescription>();
            foreach (var file in files)
            {
                if (file.ChainOnly && !file.IsFilter)
                    logger.LogWarning("{Shader}: @chainonly needs 'uniform sampler2D source', so there is no node for this file", file.FragmentPath);
                if (!file.ChainOnly)
                    nodes.Add(ShaderNodeDescription.New(factory, file, logger));
                if (file.IsFilter)
                    nodes.Add(ShaderLinkDescription.New(factory, file, logger));
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
                var (name, version) = GetNodeName(Path.GetFileNameWithoutExtension(fragment));
                var annotations = ReadHead(fragment);
                var isFilter = ReadIsFilter(fragment) ?? false;
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

        /// <summary>Annotations for the NodeBrowser only; a file that cannot be read now gets the defaults (init reports it).</summary>
        private static ShaderAnnotations ReadHead(string fragment)
        {
            try
            {
                return ShaderAnnotations.Parse(File.ReadAllText(fragment));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ShaderAnnotations.Empty;
            }
        }

        /// <summary>Whether the .frag declares <c>sampler2D source</c>; null if it cannot be read now.</summary>
        private static bool? ReadIsFilter(string fragment)
        {
            try
            {
                return ShaderNodeLayout.IsFilter(File.ReadAllText(fragment));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Node name and variant from a file name like VL.Stride: "Glitch_RGB.frag" → ("Glitch", "RGB").</summary>
        /// <remarks>Further parts are joined with spaces ("Levels_Clamp_Both" → ("Levels", "Clamp Both")); an empty name part keeps the whole name.</remarks>
        public static (string Name, string? Version) GetNodeName(string fileName)
        {
            var name = fileName.EndsWith(fragmentExtension, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^fragmentExtension.Length]
                : fileName;
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || name.StartsWith('_'))
                return (name, null);
            var version = string.Join(' ', parts.Skip(1));
            return (parts[0], version.Length > 0 ? version : null);
        }

        /// <summary>Content change of <paramref name="file"/>'s own .frag/.vert (filtered NodeBuilding.WatchDir of its folder).</summary>
        public static IObservable<object> WatchContent(ShaderFile file)
        {
            var dir = Path.GetDirectoryName(file.FragmentPath)!;
            var fragmentName = Path.GetFileName(file.FragmentPath);
            var vertexName = Path.ChangeExtension(fragmentName, vertexExtension);
            return NodeBuilding.WatchDir(dir)
                .Where(e => string.Equals(e.Name, fragmentName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(e.Name, vertexName, StringComparison.OrdinalIgnoreCase))
                .Select(static e => (object)e);
        }

        private static bool HasExtension(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        private static bool IsShaderFile(string? name) =>
            name != null && (HasExtension(name, fragmentExtension) || HasExtension(name, vertexExtension));

        /// <summary>
        /// Like VL.Stride, editors create lots of temporary files: only .frag/.vert count. Unlike VL.Stride a rename counts
        /// if either name is a shader file, so "New Text Document.txt" → "Foo.frag" (Explorer) adds the node; an editor
        /// saving over a temp file then rebuilds the node list once more, which is cheap.
        /// </summary>
        internal static bool IsNewOrDeletedShaderFile(FileSystemEventArgs e) => e.ChangeType switch
        {
            WatcherChangeTypes.Created or WatcherChangeTypes.Deleted => IsShaderFile(e.Name),
            WatcherChangeTypes.Renamed => IsShaderFile(e.Name) || (e is RenamedEventArgs r && IsShaderFile(r.OldName)),
            _ => false,
        };
    }
}
