using System;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Element input of a node (Renderer, Labeled, Snapshot, the effect and shader inputs …): converts the pin value with
    /// <see cref="ElementConversion"/> and shows a warning at the node (a phase of its <see cref="NodeErrors"/>) while the
    /// value is a foreign implementation of <see cref="IElement"/>. Allocates nothing while the kind of input stays the same.
    /// One instance per pin, main thread only.
    /// </summary>
    internal sealed class ElementInput : IDisposable
    {
        private readonly NodeMessage _message;
        private Type? _foreignType;

        /// <param name="messages">Store of the node; null shows nothing (<see cref="Warning"/> still holds the text).</param>
        /// <param name="phase">Phase of this input in the store, e.g. the pin name.</param>
        public ElementInput(NodeErrors? messages, string phase = "Input") => _message = new NodeMessage(messages, phase);

        /// <summary>Warning currently shown at the node, null if none.</summary>
        public string? Warning => _message.Text;

        /// <summary>Returns the element for the pin value (see <see cref="ElementConversion.ToElement"/>) and updates the warning.</summary>
        public Element? Update(IElement? input)
        {
            SetForeign(ElementConversion.IsForeign(input) ? input!.GetType() : null);
            return ElementConversion.ToElement(input);
        }

        /// <summary>Shows the warning for <paramref name="type"/>, or removes it (null).</summary>
        public void SetForeign(Type? type)
        {
            if (type == _foreignType)
                return;
            _foreignType = type;
            _message.Set(type is null ? null : ElementConversion.ForeignWarning(type));
        }

        public void Dispose() => _message.Dispose();
    }
}
