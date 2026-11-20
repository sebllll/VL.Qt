using System;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Lang;

namespace VL.Qt
{
    /// <summary>
    /// The one way a VL.Qt node reports runtime errors that do not surface through vvvv itself (pump path,
    /// QtNative errors): logged with the node's own logger (<see cref="NodeContext.GetLogger"/>, so the Log window links to
    /// the node and logging providers get it too) and shown at the node as a persistent error message until it is
    /// cleared or replaced. Repeated identical errors (e.g. every frame) are logged and shown once.
    /// Exceptions thrown in a node's Update are not caught: vvvv shows and logs those itself.
    /// Main thread only.
    /// </summary>
    internal sealed class NodeErrors : IDisposable
    {
        private readonly NodeContext _nodeContext;
        private IDisposable? _message;
        private string? _shown, _shownPhase;
        private string? _loggedException, _loggedError, _loggedReport;

        public NodeErrors(NodeContext nodeContext)
        {
            _nodeContext = nodeContext;
            Logger = nodeContext.GetLogger();
        }

        /// <summary>Logger of the node (category and node path of this node).</summary>
        public ILogger Logger { get; }

        /// <summary>Text currently shown at the node, null if none.</summary>
        public string? Current => _shown;

        /// <summary>Logs <paramref name="e"/> (once per distinct error) and shows it at the node.</summary>
        public void Report(Exception e, string phase)
        {
            var text = $"{phase}: {e.Message}";
            if (text != _loggedException)
            {
                _loggedException = text;
                Logger.LogError(e, "{Phase} failed: {Message}", phase, e.Message);
            }
            Show(text);
            _shownPhase = phase;
        }

        /// <summary>
        /// Logs a warning text (once per distinct text) and shows it at the node as a warning until <see cref="Clear"/>
        /// is called for the same <paramref name="phase"/> (e.g. an element error reported by Qt like a missing file,
        /// the app keeps running, the element stays empty).
        /// </summary>
        public void Warn(string warning, string phase)
        {
            if (warning != _loggedReport)
            {
                _loggedReport = warning;
                Logger.LogWarning("{Warning}", warning);
            }
            Show(warning, MessageSeverity.Warning);
            _shownPhase = phase;
        }

        /// <summary>Logs an error text from QtNative (once per distinct text) without showing it at the node (nodes with an Error output).</summary>
        public void LogOnly(string error)
        {
            if (error.Length == 0)
                _loggedError = null;
            if (error.Length == 0 || error == _loggedError)
                return;
            _loggedError = error;
            Logger.LogError("{Error}", error);
        }

        /// <summary>Removes the message shown at the node if it came from <paramref name="phase"/> (call after the phase succeeded).</summary>
        public void Clear(string phase)
        {
            if (_shown is null || _shownPhase != phase)
                return;
            _message?.Dispose();
            _message = null;
            _shown = null;
            _shownPhase = null;
            _loggedException = null;
            _loggedReport = null;
        }

        private void Show(string text, MessageSeverity severity = MessageSeverity.Error)
        {
            if (text == _shown)
                return;
            _message?.Dispose();
            // The pump runs outside the app's update: make this node's host current so the runtime (IVLRuntime) is found.
            using (_nodeContext.AppHost.MakeCurrent())
                _message = _nodeContext.AddPersistentMessage(severity, text);
            _shown = text;
        }

        public void Dispose()
        {
            _message?.Dispose();
            _message = null;
            _shown = null;
        }
    }
}
