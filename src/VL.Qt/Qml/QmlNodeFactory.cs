using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
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
    /// Node factory for qml/**/*.qml next to a document or in a package, the same mechanism as
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

        private const string extension = ".qml";

        /// <summary>
        /// Registers the factory at the app host (called from <see cref="Initialization.Configure"/>), over the app host's
        /// NodeFactoryCache like the shader factory, so the factory (and its file watchers) lives across compilations.
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
        /// changes go to the node description (<see cref="WatchSignature(QmlFile, string)"/>) and the running instance
        /// (QmlElementNode's watcher). Without the folder: empty factory that still watches path for it.
        /// </summary>
        public static NodeBuilding.FactoryImpl ForPath(string path, IVLNodeDescriptionFactory factory, ILogger logger)
        {
            // qml/ created or deleted: the whole factory again.
            var invalidated = FileSystemUtils.WatchDir(path)
                .Where(e => e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed
                    && string.Equals(e.Name, FolderName, StringComparison.OrdinalIgnoreCase))
                .Select(static e => (object)e);

            var qmlPath = Path.Combine(path, FolderName);
            if (Directory.Exists(qmlPath))
            {
                try
                {
                    var files = Scan(qmlPath);
                    foreach (var file in files)
                    {
                        if (file.DuplicateError != null)
                            logger.LogError("{Error}", file.DuplicateError);
                    }
                    var nodes = CreateDescriptions(files, factory, logger);
                    // Files added, deleted or renamed: the node list again (content changes go to the node itself).
                    invalidated = invalidated.Merge(FileSystemUtils.WatchDir(qmlPath, includeSubdirectories: true)
                        .Where(IsNewOrDeletedQmlFile)
                        .Select(static e => (object)e));
                    // A change of @category or @tags moves the node: name and category of a description are fixed, so the node list again.
                    foreach (var file in files)
                        invalidated = invalidated.Merge(WatchContent(file).Where(_ => HeadChanged(file)));
                    return NodeBuilding.NewFactoryImpl(nodes, invalidated);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    // Folder deleted while scanning; the watcher above brings the factory back.
                    logger.LogWarning(e, "Scanning {Path} failed: {Message}", qmlPath, e.Message);
                }
            }

            return NodeBuilding.NewFactoryImpl(invalidated: invalidated);
        }

        /// <summary>One node description per file of <paramref name="files"/>.</summary>
        internal static ImmutableArray<IVLNodeDescription> CreateDescriptions(IEnumerable<QmlFile> files, IVLNodeDescriptionFactory factory, ILogger logger)
        {
            var nodes = ImmutableArray.CreateBuilder<IVLNodeDescription>();
            foreach (var file in files)
                nodes.Add(QmlNodeDescription.New(factory, file, logger));
            return nodes.ToImmutable();
        }

        /// <summary>Scans a qml/ tree: component files only (<see cref="IsComponentFileName"/>), head tags, duplicate file names.</summary>
        public static ImmutableArray<QmlFile> Scan(string qmlPath)
            => Scan(qmlPath, static source => QmlDeclarations.ParseHead(source));

        /// <summary>Scan with the head parser of the caller (tests).</summary>
        internal static ImmutableArray<QmlFile> Scan(string qmlPath, Func<string, QmlFileHead> parseHead)
        {
            var paths = Directory.EnumerateFiles(qmlPath, "*" + extension, SearchOption.AllDirectories)
                .Where(f => IsComponentFileName(Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var duplicates = paths
                .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToDictionary(
                    g => g.Key,
                    g => $"The QML file name '{g.Key}' exists more than once in {qmlPath}: {string.Join(", ", g)}. Rename one of them.",
                    StringComparer.OrdinalIgnoreCase);

            var result = ImmutableArray.CreateBuilder<QmlFile>(paths.Count);
            foreach (var path in paths)
            {
                var fileName = Path.GetFileName(path);
                var (name, version) = GetNodeName(fileName);
                var head = ReadHead(path, parseHead);
                duplicates.TryGetValue(fileName, out var duplicateError);
                result.Add(new QmlFile(path, name, version, CategoryOf(head), head.Summary, TagsOf(head), duplicateError));
            }
            return result.MoveToImmutable();
        }

        private static string CategoryOf(QmlFileHead head) => string.IsNullOrWhiteSpace(head.Category) ? DefaultCategory : head.Category.Trim();

        private static string TagsOf(QmlFileHead head) => string.Join(",", head.Tags);

        /// <summary>Head tags for the node browser only; a file that cannot be read now gets the defaults (init reports it).</summary>
        private static QmlFileHead ReadHead(string path, Func<string, QmlFileHead> parseHead)
        {
            try
            {
                return parseHead(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return QmlFileHead.Empty;
            }
        }

        /// <summary>True if @category or @tags of the file on disk differ from the scan (a description cannot change them itself); false if it cannot be read now.</summary>
        private static bool HeadChanged(QmlFile file)
        {
            try
            {
                var head = QmlDeclarations.ParseHead(File.ReadAllText(file.FilePath));
                return CategoryOf(head) != file.Category || TagsOf(head) != file.Tags;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Returns true if <paramref name="fileName"/> is a .qml file whose name starts with an uppercase letter.</summary>
        public static bool IsComponentFileName(string? fileName)
            => fileName is not null
                && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fileName) is { Length: > 4 } name
                && char.IsUpper(name[0]);

        /// <summary>Node name and variant from a file name like the shader nodes: "Gauge_Round.qml" → ("Gauge", "Round").</summary>
        /// <remarks>Further parts are joined with spaces ("Levels_Clamp_Both" → ("Levels", "Clamp Both")); an empty name part keeps the whole name.</remarks>
        public static (string Name, string? Version) GetNodeName(string fileName)
        {
            var name = fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^extension.Length]
                : fileName;
            var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || name.StartsWith('_'))
                return (name, null);
            var version = string.Join(' ', parts.Skip(1));
            return (parts[0], version.Length > 0 ? version : null);
        }

        /// <summary>
        /// Fires when the parsed declaration signature (<see cref="QmlDeclarations.Signature"/>) of <paramref name="file"/> differs
        /// from <paramref name="signature"/>: a save of the file or of a sibling .qml it inherits from (folder of the file,
        /// NodeBuilding.WatchDir). The invalidated stream of the node description.
        /// </summary>
        public static IObservable<object> WatchSignature(QmlFile file, string signature)
            => WatchSignature(file, () => signature);

        /// <summary>
        /// <see cref="WatchSignature(QmlFile, string)"/> with the signature taken when an event arrives: a description knows it
        /// only once its init has run (null until then: nothing to compare, nothing fires).
        /// </summary>
        internal static IObservable<object> WatchSignature(QmlFile file, Func<string?> signature)
            => NodeBuilding.WatchDir(Path.GetDirectoryName(file.FilePath)!)
                .Where(IsQmlEvent)
                .Where(_ => signature() is { } known && CurrentSignature(file) is { } current && current != known)
                .Select(static e => (object)e);

        // Events of the own file only (head tags).
        private static IObservable<FileSystemEventArgs> WatchContent(QmlFile file)
        {
            var fileName = Path.GetFileName(file.FilePath);
            return NodeBuilding.WatchDir(Path.GetDirectoryName(file.FilePath)!)
                .Where(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));
        }

        // Null if the file cannot be read right now (editors save over a temp file): then nothing is compared, the next event decides.
        private static string? CurrentSignature(QmlFile file)
        {
            try
            {
                // Load reports an unreadable file as an error message and empty declarations; that must not look like a change.
                using (File.Open(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                }
                return QmlNodeLayout.Load(file, NullLogger.Instance).Declarations.Signature;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // Any .qml file of the folder: the file itself or a sibling root type it inherits from.
        private static bool IsQmlEvent(FileSystemEventArgs e)
            => HasQmlExtension(e.Name) || (e is RenamedEventArgs r && HasQmlExtension(r.OldName));

        private static bool HasQmlExtension(string? name) => name != null && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

        // Directories have no extension: a created, deleted or renamed folder may hold component files.
        private static bool IsComponentOrFolder(string? name) => name != null && (IsComponentFileName(Path.GetFileName(name)) || Path.GetExtension(name).Length == 0);

        /// <summary>
        /// Only component files count (a lowercase helper does not make a node; a sibling's content change goes through
        /// <see cref="WatchSignature(QmlFile, string)"/>), and folders. A rename counts if either name is a component file, so
        /// "New Text Document.txt" → "Gauge.qml" (Explorer) adds the node.
        /// </summary>
        internal static bool IsNewOrDeletedQmlFile(FileSystemEventArgs e) => e.ChangeType switch
        {
            WatcherChangeTypes.Created or WatcherChangeTypes.Deleted => IsComponentOrFolder(e.Name),
            WatcherChangeTypes.Renamed => IsComponentOrFolder(e.Name) || (e is RenamedEventArgs r && IsComponentOrFolder(r.OldName)),
            _ => false,
        };
    }
}
