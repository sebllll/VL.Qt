using System;
using System.Collections.Generic;
using VL.Core;
using VL.Qt.Nodes;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Instance of a generated QML node (#107): a <see cref="QmlElementNode"/> fed with the file of the layout and a
    /// Dictionary built from the typed pins, its outputs split into typed pins. Per frame:
    /// <list type="number">
    /// <item>Properties dictionary: the entries of the Properties input, then the typed property inputs on top (a pin wins on a
    /// name clash). A typed input is in it once it differed from its declared default (<see cref="QmlInputPin"/>), a required one
    /// always. The dictionary instance is reused and changed only when an entry changes (QmlProperties compares content, no
    /// allocation per frame).</item>
    /// <item>QmlElementNode.Update(file, dictionary, common pins…).</item>
    /// <item>Property outputs: from its Properties output, coerced with <see cref="QmlTypes.TryCoerce"/>; until the first change and
    /// after a reload (the dictionary starts empty again) the declared default.</item>
    /// <item>Signal outputs: its Signals output this frame, by name: bool (any emission), Spread&lt;T&gt; of the only argument, or
    /// Spread&lt;QmlSignal&gt;; empty/false in a frame without emission.</item>
    /// <item>Output, Measured Size, Scene Bounds, Error passed on.</item>
    /// </list>
    /// Pins come in the order of <see cref="QmlNodeLayout.InputDescriptions"/> / <see cref="QmlNodeLayout.OutputDescriptions"/>.
    /// Hot reload of the content is QmlElementNode's (its watcher of the folder); new pins come with a new description.
    /// </summary>
    internal sealed class QmlNodeInstance : IDisposable
    {
        public QmlNodeInstance(NodeContext nodeContext, QmlNodeLayout layout)
            => throw new NotImplementedException("T4: QML node instance (#107)");

        /// <summary>For tests: with a QmlElementNode built on its test constructor.</summary>
        internal QmlNodeInstance(QmlElementNode element, QmlNodeLayout layout)
            => throw new NotImplementedException("T4: QML node instance (#107)");

        /// <summary>Input pins in the order of the description's inputs.</summary>
        internal IEnumerable<IVLPin> Inputs => throw new NotImplementedException("T4: QML node instance (#107)");

        /// <summary>Output pins in the order of the description's outputs.</summary>
        internal IEnumerable<IVLPin> Outputs => throw new NotImplementedException("T4: QML node instance (#107)");

        public IVLNode CreateNode(NodeBuilding.NodeInstanceBuildContext context) =>
            context.Node(Inputs, Outputs, update: Update, dispose: Dispose);

        public void Update() => throw new NotImplementedException("T4: QML node instance (#107)");

        public void Dispose()
        {
        }
    }
}
