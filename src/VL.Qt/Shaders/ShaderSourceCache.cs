using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging;
using VL.Core.Diagnostics;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// One <see cref="ShaderSource"/> per file version, for the nodes of one factory folder: the element node and the effect
    /// chain node of a filter file read, annotate and reflect (bake) the file once, and the problems of a version are logged once.
    /// A version is the last-write times of the .frag and the .vert; a new version replaces the old one.
    /// </summary>
    internal sealed class ShaderSourceCache
    {
        private sealed class Entry
        {
            public Entry(DateTime fragmentTime, DateTime vertexTime, ShaderSource source)
            {
                FragmentTime = fragmentTime;
                VertexTime = vertexTime;
                Source = source;
            }

            public DateTime FragmentTime { get; }
            public DateTime VertexTime { get; }
            public ShaderSource Source { get; }
            public HashSet<string> Logged { get; } = new();
        }

        private readonly object _syncRoot = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LastGood<ShaderSource>> _lastGood = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger _logger;
        private readonly Func<string, ShaderStage, ShaderReflection> _reflect;

        /// <param name="logger">Receives the messages of <see cref="LogOnce"/>.</param>
        /// <param name="reflect">The reflection, normally <see cref="ShaderReflection.Reflect"/>; tests pass a fake.</param>
        public ShaderSourceCache(ILogger logger, Func<string, ShaderStage, ShaderReflection>? reflect = null)
        {
            _logger = logger;
            _reflect = reflect ?? ShaderReflection.Reflect;
        }

        /// <summary>
        /// The source of the current version of <paramref name="file"/>; reads and reflects only if the files changed since the
        /// last call. A file that could not be read is not remembered, so the next call tries again. A version that does not
        /// read or compile gives the last good one with <see cref="ShaderSource.KeptError"/> set (<see cref="LastGood{T}"/>).
        /// </summary>
        public ShaderSource Get(ShaderFile file)
        {
            var fragmentTime = File.GetLastWriteTimeUtc(file.FragmentPath);
            var vertexTime = file.VertexPath is null ? default : File.GetLastWriteTimeUtc(file.VertexPath);
            lock (_syncRoot)
            {
                if (_entries.TryGetValue(file.FragmentPath, out var entry) && entry.FragmentTime == fragmentTime && entry.VertexTime == vertexTime)
                    return entry.Source;

                if (!_lastGood.TryGetValue(file.FragmentPath, out var lastGood))
                    _lastGood[file.FragmentPath] = lastGood = new LastGood<ShaderSource>();
                var loaded = ShaderSource.Load(file, _reflect);
                var source = lastGood.Resolve(loaded, loaded.Failed, static (good, failed) => good with { KeptError = failed.ReadError ?? failed.PinModel.Error });
                if (loaded.ReadError is null)
                    _entries[file.FragmentPath] = new Entry(fragmentTime, vertexTime, source);
                else
                    _entries.Remove(file.FragmentPath);
                return source;
            }
        }

        /// <summary>
        /// Logs what a node shows (errors, warnings) unless the same text of this file version was logged before, e.g. by the other
        /// node of the file. The duplicate error is not logged: the factory logs it once per scan.
        /// </summary>
        public void LogOnce(ShaderFile file, ImmutableArray<Message> messages)
        {
            lock (_syncRoot)
            {
                _entries.TryGetValue(file.FragmentPath, out var entry);
                foreach (var m in messages)
                {
                    if (m.Text == file.DuplicateError || (entry != null && !entry.Logged.Add(m.Text)))
                        continue;
                    if (m.Type == MessageType.Error)
                        _logger.LogError("{Shader}: {Error}", file.FragmentPath, m.Text);
                    else
                        _logger.LogWarning("{Shader}: {Warning}", file.FragmentPath, m.Text);
                }
            }
        }
    }
}
