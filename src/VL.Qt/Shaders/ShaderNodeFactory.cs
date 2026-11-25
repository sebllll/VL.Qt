using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VL.Core;
using VL.Core.Import;
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
        /// Category of the effect chain node of a filter shader; how @category moves it: docs/effects-and-shaders.md.
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

        /// <summary>One element node per file (none for @chainonly), plus the link node of the effect chain for every filter and every @chainonly file.</summary>
        internal static ImmutableArray<IVLNodeDescription> CreateDescriptions(IEnumerable<ShaderFile> files, IVLNodeDescriptionFactory factory, ILogger logger, ShaderSourceCache? cache = null)
        {
            cache ??= new ShaderSourceCache(logger);
            var nodes = ImmutableArray.CreateBuilder<IVLNodeDescription>();
            foreach (var file in files)
            {
                if (!file.ChainOnly)
                    nodes.Add(ShaderDescription.New(factory, file, logger, cache));
                if (file.HasLinkNode)
                    nodes.Add(ShaderEffectDescription.New(factory, file, logger, cache));
            }
            return nodes.ToImmutable();
        }

        /// <summary>
        /// Scans a qtshaders/ tree: pairs .frag/.vert, reads the head annotations, marks files whose nodes have the same
        /// identity (name and category) as the node of another file or, for the effect chain node, of a built-in effect node.
        /// </summary>
        public static ImmutableArray<ShaderFile> Scan(string shadersPath)
        {
            var fragments = Directory.EnumerateFiles(shadersPath, "*" + fragmentExtension, SearchOption.AllDirectories)
                .Where(f => HasExtension(f, fragmentExtension))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var files = new List<ShaderFile>(fragments.Count);
            foreach (var fragment in fragments)
            {
                var vertex = Path.ChangeExtension(fragment, vertexExtension);
                var (name, version) = FileNodeNames.Split(Path.GetFileNameWithoutExtension(fragment));
                var head = ShaderFileHead.Read(fragment);
                var annotations = head?.Annotations ?? ShaderAnnotations.Empty;
                var isFilter = head?.IsFilter ?? false;
                var (category, chainCategory) = ResolveCategories(annotations, isFilter);
                files.Add(new ShaderFile(
                    fragment,
                    File.Exists(vertex) ? vertex : null,
                    name,
                    version,
                    category,
                    annotations.Summary,
                    null,
                    isFilter,
                    annotations.ChainOnly,
                    chainCategory));
            }

            var errors = DuplicateErrors(files);
            return files.Select(f => errors.TryGetValue(f.FragmentPath, out var error) ? f with { DuplicateError = error } : f).ToImmutableArray();
        }

        /// <summary>
        /// Categories of the element node and of the effect chain node. An @category that starts with "Qt." is absolute and
        /// moves the element node (with @chainonly: the chain node, which is the only one). Any other path is relative and
        /// is appended to the default category of each node kind, so one file keeps its two nodes together
        /// (<c>@category Examples</c> gives <c>Qt.Elements.Shaders.Examples</c> and <c>Qt.Effects.Examples</c>).
        /// </summary>
        internal static (string Category, string ChainCategory) ResolveCategories(ShaderAnnotations annotations, bool isFilter)
        {
            var path = annotations.Category?.Trim();
            var relative = !string.IsNullOrEmpty(path)
                && !path.StartsWith("Qt.", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, "Qt", StringComparison.OrdinalIgnoreCase);
            if (relative)
                path = path!.Trim('.', ' ');
            if (string.IsNullOrEmpty(path))
                return (annotations.ChainOnly ? LinkCategory : DefaultCategory, LinkCategory);

            if (relative)
                return (annotations.ChainOnly ? LinkCategory + "." + path : DefaultCategory + "." + path, LinkCategory + "." + path);

            if (annotations.ChainOnly)
                return (path, path);
            // A filter's element node never shares name and category with its chain node.
            if (isFilter && string.Equals(path, LinkCategory, StringComparison.OrdinalIgnoreCase))
                return (DefaultCategory, LinkCategory);
            return (path, LinkCategory);
        }

        /// <summary>
        /// Error text per file path for the files whose node has the same name and category as the node of another file,
        /// and for effect chain nodes that have the name of a built-in effect node in the same category.
        /// </summary>
        private static Dictionary<string, string> DuplicateErrors(List<ShaderFile> files)
        {
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var groups = files
                .SelectMany(f => NodeIdentities(f).Select(identity => (identity, f.FragmentPath)))
                .GroupBy(x => x.identity, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var paths = group.Select(x => x.FragmentPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count < 2)
                    continue;
                var text = $"More than one shader file makes the node '{group.Key}': {string.Join(", ", paths)}. Rename one of them.";
                foreach (var path in paths)
                    errors[path] = text;
            }

            var builtIn = BuiltInEffectNames.Value;
            foreach (var file in files)
            {
                if (file.HasLinkNode && string.Equals(file.LinkCategory, LinkCategory, StringComparison.OrdinalIgnoreCase) && builtIn.Contains(file.FullName))
                    errors[file.FragmentPath] = $"The effect node '{file.FullName}' of {file.FragmentPath} has the name and category of the built-in node '{file.FullName}' ({LinkCategory}). Rename the file.";
            }
            return errors;
        }

        /// <summary>"Category/Name" of every node the file makes (the element node unless @chainonly, the chain node of a filter or @chainonly file).</summary>
        private static IEnumerable<string> NodeIdentities(ShaderFile file)
        {
            if (!file.ChainOnly)
                yield return file.Category + "/" + file.FullName;
            if (file.HasLinkNode)
                yield return file.LinkCategory + "/" + file.FullName;
        }

        /// <summary>Names of the built-in effect nodes (C# nodes in <see cref="LinkCategory"/>), read once from the assembly.</summary>
        private static readonly Lazy<HashSet<string>> BuiltInEffectNames = new(() => typeof(ShaderNodeFactory).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributes(typeof(ProcessNodeAttribute), inherit: false).Cast<ProcessNodeAttribute>())
            .Where(a => a.Name != null && string.Equals(a.Category, LinkCategory, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// True if a content change turned a generator into a filter or back, or changed @chainonly or @category: that
        /// adds, removes or moves a node. False if the file cannot be read now.
        /// </summary>
        internal static bool HeadChanged(ShaderFile file)
        {
            if (ShaderFileHead.Read(file.FragmentPath) is not { } head)
                return false;
            var (category, chainCategory) = ResolveCategories(head.Annotations, head.IsFilter);
            return head.IsFilter != file.IsFilter
                || head.Annotations.ChainOnly != file.ChainOnly
                || category != file.Category
                || chainCategory != file.LinkCategory;
        }

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
