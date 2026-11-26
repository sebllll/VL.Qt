using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VL.Core;
using VL.Lang;

namespace VL.Qt
{
    /// <summary>
    /// The one store of the messages at a VL.Qt node (runtime errors and warnings that do not surface through vvvv itself:
    /// pump path, QtNative errors, warnings reported by Qt for an element). Holds at most one entry per phase (the source of
    /// a message, e.g. "Window Icon" or "ApplyCommands"); a phase sets its entry and clears only its own. The node shows one
    /// persistent message that joins all entries, errors first, one line each (warning or error, the text as tooltip); it is
    /// rebuilt only when an entry changes. Each entry is logged with the node's own logger (<see cref="NodeContext.GetLogger"/>,
    /// so the Log window links to the node and logging providers get it too) when it appears or its text changes, so a
    /// message repeated every frame is logged once. A message source that holds a text holds a <see cref="Nodes.NodeMessage"/>.
    /// Exceptions thrown in a node's Update are not caught: vvvv shows and logs those itself. Main thread only.
    /// <para>
    /// This is the one seam for the messages of a node: next to its <c>NodeContext</c> constructor a node has an internal one that
    /// takes the store, so that a unit test passes a store on a recording logger and message (a <c>NodeContext</c> needs a running
    /// vvvv: without an app host it cannot be created, and without a runtime it shows nothing).
    /// </para>
    /// </summary>
    internal sealed class NodeErrors : IDisposable
    {
        private readonly Func<MessageSeverity, string, IDisposable> _show;
        // Few entries per node (one per phase): a list, searched linearly, allocates only when an entry appears.
        private readonly List<Entry> _entries = new();
        private IDisposable? _message;
        private string? _shown;
        private MessageSeverity _shownSeverity;
        private string? _loggedError;

        public NodeErrors(NodeContext nodeContext)
            : this(nodeContext.GetLogger(), (severity, text) =>
            {
                // The pump runs outside the app's update: make this node's host current so the runtime (IVLRuntime) is found.
                using (nodeContext.AppHost.MakeCurrent())
                    return nodeContext.AddPersistentMessage(severity, text);
            })
        {
        }

        /// <param name="logger">Logger of the node.</param>
        /// <param name="show">Shows the joined message at the node, the result removes it.</param>
        internal NodeErrors(ILogger logger, Func<MessageSeverity, string, IDisposable> show)
        {
            Logger = logger;
            _show = show;
        }

        /// <summary>Logger of the node (category and node path of this node).</summary>
        public ILogger Logger { get; }

        /// <summary>Logs <paramref name="e"/> and shows it at the node as an error until <paramref name="phase"/> is cleared; null clears it.</summary>
        public void Report(Exception? e, string phase)
        {
            if (e is null)
                Clear(phase);
            else
                Set(phase, MessageSeverity.Error, $"{phase}: {e.Message}", e);
        }

        /// <summary>
        /// Logs a warning text and shows it at the node as a warning until <see cref="Clear"/> is called for the same
        /// <paramref name="phase"/> (e.g. an element error reported by Qt like a missing file: the app keeps running, the
        /// element stays empty).
        /// </summary>
        public void Warn(string warning, string phase) => Set(phase, MessageSeverity.Warning, warning);

        /// <summary>
        /// Sets the entry of <paramref name="phase"/>: logged when it appears or its text changes, shown at the node until it
        /// is cleared. An empty text clears it.
        /// </summary>
        public void Set(string phase, MessageSeverity severity, string text) => Set(phase, severity, text, null);

        /// <summary>Logs an error text (once per distinct text) without showing it at the node (one-shot failures, nodes with an Error output).</summary>
        public void LogOnly(string error)
        {
            if (error.Length == 0)
                _loggedError = null;
            if (error.Length == 0 || error == _loggedError)
                return;
            _loggedError = error;
            Logger.LogError("{Error}", error);
        }

        /// <summary>Removes the entry of <paramref name="phase"/> (call after the phase succeeded); the other entries stay.</summary>
        public void Clear(string phase)
        {
            var i = IndexOf(phase);
            if (i < 0)
                return;
            _entries.RemoveAt(i);
            Rebuild();
        }

        /// <summary>
        /// Calls <paramref name="action"/> with <paramref name="state"/>: clears <paramref name="phase"/> when it returns,
        /// reports the exception when it throws (cancellation passes). Use a static lambda: no allocation.
        /// </summary>
        public void Run<TState>(string phase, TState state, Action<TState> action)
        {
            try
            {
                action(state);
                Clear(phase);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Report(e, phase);
            }
        }

        private void Set(string phase, MessageSeverity severity, string text, Exception? exception)
        {
            if (text.Length == 0)
            {
                Clear(phase);
                return;
            }
            var i = IndexOf(phase);
            if (i >= 0 && _entries[i].Severity == severity && _entries[i].Text == text)
                return;
            var entry = new Entry(phase, severity, text);
            if (i >= 0)
                _entries[i] = entry;
            else
                _entries.Add(entry);
            Log(entry, exception);
            Rebuild();
        }

        private void Log(in Entry entry, Exception? exception)
        {
            if (exception is not null)
                Logger.LogError(exception, "{Phase} failed: {Message}", entry.Phase, exception.Message);
            else if (entry.Severity == MessageSeverity.Error)
                Logger.LogError("{Error}", entry.Text);
            else
                Logger.LogWarning("{Warning}", entry.Text);
        }

        private int IndexOf(string phase)
        {
            for (var i = 0; i < _entries.Count; i++)
                if (_entries[i].Phase == phase)
                    return i;
            return -1;
        }

        // Joins the entries (errors first, one line each) and replaces the message at the node if the text or severity changed.
        private void Rebuild()
        {
            string? text = null;
            var severity = MessageSeverity.Warning;
            if (_entries.Count == 1)
            {
                text = _entries[0].Text;
                severity = _entries[0].Severity;
            }
            else if (_entries.Count > 1)
            {
                var sb = new StringBuilder();
                for (var errors = 1; errors >= 0; errors--)
                    foreach (var entry in _entries)
                        if ((entry.Severity == MessageSeverity.Error) == (errors == 1))
                        {
                            if (sb.Length > 0)
                                sb.Append('\n');
                            sb.Append(entry.Text);
                            if (errors == 1)
                                severity = MessageSeverity.Error;
                        }
                text = sb.ToString();
            }
            if (text == _shown && severity == _shownSeverity)
                return;
            _message?.Dispose();
            _message = text is null ? null : _show(severity, text);
            _shown = text;
            _shownSeverity = severity;
        }

        public void Dispose()
        {
            _entries.Clear();
            _message?.Dispose();
            _message = null;
            _shown = null;
        }

        private readonly record struct Entry(string Phase, MessageSeverity Severity, string Text);
    }
}
