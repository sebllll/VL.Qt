using System;
using VL.Lang;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// The handle one message source of a node holds on its <see cref="NodeErrors"/>: one phase of the store, set to a text
    /// or cleared. Compares the text first, so a source can pass its state every frame without allocating or logging again.
    /// Without a store it only keeps <see cref="Text"/>. Main thread only.
    /// </summary>
    internal sealed class NodeMessage : IDisposable
    {
        private readonly NodeErrors? _errors;
        private readonly string _phase;
        private readonly MessageSeverity _severity;

        /// <param name="errors">Store of the node; null keeps the text only.</param>
        /// <param name="phase">Phase of this source, unique within the store.</param>
        /// <param name="severity">How the text is shown at the node and logged.</param>
        public NodeMessage(NodeErrors? errors, string phase, MessageSeverity severity = MessageSeverity.Warning)
        {
            _errors = errors;
            _phase = phase;
            _severity = severity;
        }

        /// <summary>Text this source shows at the node, null if none.</summary>
        public string? Text { get; private set; }

        /// <summary>Shows <paramref name="text"/> at the node, or removes it (null or empty).</summary>
        public void Set(string? text)
        {
            if (string.IsNullOrEmpty(text))
                text = null;
            if (ReferenceEquals(text, Text) || text == Text)
                return;
            Text = text;
            if (text is null)
                _errors?.Clear(_phase);
            else
                _errors?.Set(_phase, _severity, text);
        }

        /// <summary>Removes the text of this source from the node.</summary>
        public void Dispose() => Set(null);
    }
}
