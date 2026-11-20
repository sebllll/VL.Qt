using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using VL.Core;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Hot reload source of a QmlElement, after <see cref="Shaders.ShaderNodeFactory.WatchContent"/>: watches the folder
    /// of the .qml file (<c>NodeBuilding.WatchDir</c>) and reports a change of the file itself, of any other *.qml next to it or of
    /// <c>qmldir</c> (components the file imports by relative path). The events come on a file system thread; the node asks
    /// with <see cref="TakeChanged"/> once per Update. Dispose it with the node.
    /// </summary>
    internal sealed class QmlFileWatcher : IDisposable
    {
        private const string qmlExtension = ".qml";
        private const string qmldirName = "qmldir";

        private readonly Func<string, IObservable<FileSystemEventArgs>> _watchDir;
        private IDisposable? _subscription;
        private string _path = string.Empty;
        private int _changed;

        public QmlFileWatcher()
            : this(static dir => NodeBuilding.WatchDir(dir))
        {
        }

        /// <param name="watchDir">Change events of a folder (tests: a subject).</param>
        public QmlFileWatcher(Func<string, IObservable<FileSystemEventArgs>> watchDir) => _watchDir = watchDir;

        /// <summary>Path of the watched file, empty = none.</summary>
        public string Path => _path;

        /// <summary>True while a folder is being watched.</summary>
        public bool IsWatching => _subscription is not null;

        /// <summary>
        /// Watches the folder of <paramref name="path"/> (absolute, empty = stop). Nothing happens while the path stays the same;
        /// a new path forgets a change that was reported for the old one. A folder that does not exist is not watched (set
        /// the path again after creating it).
        /// </summary>
        public void Update(string path)
        {
            if (path == _path)
                return;
            _subscription?.Dispose();
            _subscription = null;
            _path = path;
            Volatile.Write(ref _changed, 0);
            if (path.Length == 0)
                return;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            try
            {
                _subscription = _watchDir(dir)
                    .Where(static e => IsRelevant(e))
                    .Subscribe(_ => Volatile.Write(ref _changed, 1));
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                _subscription = null;
            }
        }

        /// <summary>True once after a relevant change since the last call.</summary>
        public bool TakeChanged() => Interlocked.Exchange(ref _changed, 0) != 0;

        /// <summary>A *.qml file or qmldir; for a rename either the new or the old name.</summary>
        internal static bool IsRelevant(FileSystemEventArgs e)
            => IsRelevantName(e.Name) || (e is RenamedEventArgs r && IsRelevantName(r.OldName));

        internal static bool IsRelevantName(string? name)
            => name is not null
                && (name.EndsWith(qmlExtension, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(System.IO.Path.GetFileName(name), qmldirName, StringComparison.OrdinalIgnoreCase));

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
            _path = string.Empty;
        }
    }
}
