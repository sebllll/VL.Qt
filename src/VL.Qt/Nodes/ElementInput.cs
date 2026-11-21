using System;
using VL.Core;
using VL.Lang;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Element input of a node (Renderer, Labeled, Snapshot, the effect and shader inputs …): converts the pin value with
    /// <see cref="ElementConversion"/> and shows a persistent warning at the node while the value is a foreign
    /// implementation of <see cref="IElement"/>. Allocates nothing while the kind of input stays the same. One instance
    /// per pin, main thread only.
    /// </summary>
    internal sealed class ElementInput : IDisposable
    {
        private readonly Func<string, IDisposable>? _addWarning;
        private IDisposable? _message;
        private Type? _foreignType;

        /// <param name="addWarning">Shows a warning at the node, the result removes it; null shows nothing (tests read <see cref="Warning"/>).</param>
        public ElementInput(Func<string, IDisposable>? addWarning) => _addWarning = addWarning;

        /// <summary>Warning currently shown at the node, null if none.</summary>
        public string? Warning { get; private set; }

        /// <summary>Returns a function that shows a persistent warning at the node of <paramref name="nodeContext"/>.</summary>
        public static Func<string, IDisposable> WarningsOf(NodeContext nodeContext)
            => w => nodeContext.AddPersistentMessage(MessageSeverity.Warning, w);

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
            _message?.Dispose();
            _message = null;
            Warning = type is null ? null : ElementConversion.ForeignWarning(type);
            if (Warning is not null)
                _message = _addWarning?.Invoke(Warning);
        }

        public void Dispose()
        {
            _message?.Dispose();
            _message = null;
        }
    }
}
