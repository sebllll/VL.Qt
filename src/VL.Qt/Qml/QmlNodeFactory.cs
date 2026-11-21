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

namespace VL.Qt.Qml
{
    /// <summary>
    /// Node factory for qml/**/*.qml next to a document or in a package, built on <see cref="FolderNodeFactory"/> like
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
        public const string DefaultCategory = Categories.Qml;

        private const string extension = ".qml";

        /// <summary>
        /// Registers the factory at the app host (called from <see cref="Initialization.Configure"/>), over the app host's
        /// NodeFactoryCache like the shader factory, so the factory (and its file watchers) lives across compilations; one sub
        /// factory per document/package folder.
        /// </summary>
        public static void Register(AppHost appHost)
        {
            var logger = appHost.LoggerFactory?.CreateLogger(Identifier) ?? NullLogger.Instance;
            appHost.RegisterNodeFactory(Identifier, (path, f) => ForPath(path, f, logger));
        }

        /// <summary>
        /// Nodes of &lt;path&gt;/qml/ (recursive). invalidated: qml/ created/deleted, .qml files added/deleted/renamed, a change
        /// of @category or @tags (name and category of a description are fixed). Content changes go to the node description
        /// (<see cref="WatchSignature(QmlFile, string)"/>) and the running instance (QmlElementNode's watcher). Without the
        /// folder: empty factory that still watches path for it.
        /// </summary>
        public static NodeBuilding.FactoryImpl ForPath(string path, IVLNodeDescriptionFactory factory, ILogger logger) =>
            FolderNodeFactory.ForPath(
                path,
                FolderName,
                logger,
                scan: Scan,
                duplicateError: static file => file.DuplicateError,
                createDescriptions: files => CreateDescriptions(files, factory, logger),
                isNewOrDeleted: IsNewOrDeletedQmlFile,
                watchContent: WatchContent,
                headChanged: HeadChanged);

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
                var (name, version) = FileNodeNames.Split(Path.GetFileNameWithoutExtension(path));
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

        /// <summary>
        /// True if @category or @tags of the file on disk differ from the scan (a description cannot change them itself); false
        /// if it cannot be read now.
        /// </summary>
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
            => FolderNodeFactory.WatchFiles(Path.GetDirectoryName(file.FilePath)!, Path.GetFileName(file.FilePath));

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
        /// <see cref="WatchSignature(QmlFile, string)"/>), and folders; a rename counts if either name counts (see
        /// <see cref="FolderNodeFactory.IsNewOrDeleted"/>), so "New Text Document.txt" → "Gauge.qml" (Explorer) adds the node.
        /// </summary>
        internal static bool IsNewOrDeletedQmlFile(FileSystemEventArgs e) => FolderNodeFactory.IsNewOrDeleted(e, IsComponentOrFolder);
    }
}
