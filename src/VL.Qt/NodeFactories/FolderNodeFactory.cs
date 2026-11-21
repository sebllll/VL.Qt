using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using VL.Core;

namespace VL.Qt.NodeFactories
{
    /// <summary>
    /// The skeleton of the node factories for a folder of files next to a document or in a package (qtshaders/, qml/), after
    /// VL.Stride's EffectShaderNodes.Init: the scan of the folder and the events that build the node list again.
    /// </summary>
    internal static class FolderNodeFactory
    {
        /// <summary>
        /// Nodes of &lt;path&gt;/&lt;folderName&gt;/ (recursive). invalidated: the folder created or deleted, a file that
        /// <paramref name="isNewOrDeleted"/> counts added, deleted or renamed, or a change of a file whose head
        /// <paramref name="headChanged"/> reports. Without the folder: an empty factory that still watches path for it.
        /// </summary>
        /// <param name="path">Folder of the document or package.</param>
        /// <param name="folderName">Name of the folder searched in <paramref name="path"/>.</param>
        /// <param name="logger">Gets the duplicate errors of the scan and a failed scan.</param>
        /// <param name="scan">Scans the folder (full path) for its files.</param>
        /// <param name="duplicateError">Error of a file whose name exists more than once in the folder, else null.</param>
        /// <param name="createDescriptions">The node descriptions of the scanned files.</param>
        /// <param name="isNewOrDeleted">True for a watcher event in the folder that adds, deletes or renames a node.</param>
        /// <param name="watchContent">Content changes of one file.</param>
        /// <param name="headChanged">
        /// True if the file on disk now gives other nodes than the scan did (name and category of a description are fixed, so
        /// the node list again).
        /// </param>
        public static NodeBuilding.FactoryImpl ForPath<TFile>(
            string path,
            string folderName,
            ILogger logger,
            Func<string, ImmutableArray<TFile>> scan,
            Func<TFile, string?> duplicateError,
            Func<ImmutableArray<TFile>, ImmutableArray<IVLNodeDescription>> createDescriptions,
            Func<FileSystemEventArgs, bool> isNewOrDeleted,
            Func<TFile, IObservable<object>> watchContent,
            Func<TFile, bool> headChanged)
        {
            // The folder created or deleted: the whole factory again.
            var invalidated = FileSystemUtils.WatchDir(path)
                .Where(e => e.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed
                    && string.Equals(e.Name, folderName, StringComparison.OrdinalIgnoreCase))
                .Select(static e => (object)e);

            var folderPath = Path.Combine(path, folderName);
            if (Directory.Exists(folderPath))
            {
                try
                {
                    var files = scan(folderPath);
                    foreach (var file in files)
                    {
                        if (duplicateError(file) is { } error)
                            logger.LogError("{Error}", error);
                    }
                    var nodes = createDescriptions(files);
                    // Files added, deleted or renamed: the node list again (content changes go to the node itself).
                    invalidated = invalidated.Merge(FileSystemUtils.WatchDir(folderPath, includeSubdirectories: true)
                        .Where(isNewOrDeleted)
                        .Select(static e => (object)e));
                    foreach (var file in files)
                        invalidated = invalidated.Merge(watchContent(file).Where(_ => headChanged(file)));
                    return NodeBuilding.NewFactoryImpl(nodes, invalidated);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException)
                {
                    // Folder deleted while scanning; the watcher above brings the factory back.
                    logger.LogWarning(e, "Scanning {Path} failed: {Message}", folderPath, e.Message);
                }
            }

            return NodeBuilding.NewFactoryImpl(invalidated: invalidated);
        }

        /// <summary>
        /// Returns true if <paramref name="e"/> adds or deletes a name that <paramref name="counts"/>. Editors create lots of
        /// temporary files, so only those names count. Unlike VL.Stride a rename counts if either name counts, so
        /// "New Text Document.txt" → "Foo.frag" (Explorer) adds the node; an editor saving over a temp file then builds the
        /// node list once more, which is cheap.
        /// </summary>
        public static bool IsNewOrDeleted(FileSystemEventArgs e, Func<string?, bool> counts) => e.ChangeType switch
        {
            WatcherChangeTypes.Created or WatcherChangeTypes.Deleted => counts(e.Name),
            WatcherChangeTypes.Renamed => counts(e.Name) || (e is RenamedEventArgs r && counts(r.OldName)),
            _ => false,
        };

        /// <summary>Events of the files <paramref name="fileNames"/> in <paramref name="directory"/> (filtered NodeBuilding.WatchDir).</summary>
        public static IObservable<FileSystemEventArgs> WatchFiles(string directory, params string[] fileNames) =>
            NodeBuilding.WatchDir(directory)
                .Where(e => fileNames.Any(name => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)));
    }
}
