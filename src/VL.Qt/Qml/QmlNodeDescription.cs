using System.Threading;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Qt.NodeFactories;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Node description of one .qml file. init (lazy, when vvvv needs the pins): <see cref="QmlNodeLayout.Load"/>, then
    /// <c>context.Node(layout.InputDescriptions(), layout.OutputDescriptions(), newNode: c =&gt; new QmlNodeInstance(c.NodeContext, layout).CreateNode(c),
    /// messages: layout.Messages, openEditorAction, summary: layout.Summary ?? file.Summary, filePath: file.FilePath)</c>; not
    /// fragmented (like the shader nodes, so vvvv calls Update). Name <see cref="QmlFile.FullName"/>, category
    /// <see cref="QmlFile.Category"/>, tags <see cref="QmlFile.Tags"/> (the tags argument of NewNodeDescription). invalidated:
    /// <see cref="QmlNodeFactory.WatchSignature"/> with the signature of the layout, so only a change of the declarations
    /// rebuilds the node; vvvv keeps links on pins with the same name. A file that does not read or parse keeps the pins of the
    /// last good version (<see cref="LastGood{T}"/>) and shows its errors as messages; without a good version the node has
    /// the common pins and the errors.
    /// </summary>
    // Smell experimental like QmlElement is not possible: NodeBuilding.NewNodeDescription and IVLNodeDescription (VL.Core
    // 2026.8) have no smell, SymbolSmell only exists as the [Smell] attribute on symbols. The experimental state of the
    // generated nodes is documented in docs/user-qml.md instead.
    internal static class QmlNodeDescription
    {
        /// <summary>Creates the node description of <paramref name="file"/>.</summary>
        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, QmlFile file, ILogger logger)
        {
            // The signature of the layout of the last init: the description is invalidated only when the declarations change.
            string? signature = null;
            var lastGood = new LastGood<QmlNodeLayout>();
            return factory.NewNodeDescription(
                file.FullName,
                file.Category,
                // Not fragmented: vvvv calls IVLNode.Update only for non-fragmented node descriptions (like the shader nodes).
                fragmented: false,
                invalidated: QmlNodeFactory.WatchSignature(file, () => Volatile.Read(ref signature)),
                init: context =>
                {
                    var loaded = QmlNodeLayout.Load(file, logger);
                    // The signature of the file as it is now, also while the node shows the last good pins: the broken file then does
                    // not invalidate again with every event, and a fix does (it differs from the broken one).
                    Volatile.Write(ref signature, loaded.Declarations.Signature);
                    var layout = lastGood.Resolve(loaded, loaded.HasErrors, QmlNodeLayout.KeepPins);
                    return context.Node(
                        layout.InputDescriptions(),
                        layout.OutputDescriptions(),
                        newNode: c => new QmlNodeInstance(c.NodeContext, layout).CreateNode(c),
                        messages: layout.Messages,
                        openEditorAction: () => OpenEditor(file, logger),
                        summary: layout.Summary ?? file.Summary,
                        filePath: file.FilePath);
                },
                tags: file.Tags);
        }

        /// <summary>Middle click / Definition > Open: opens the .qml in the default editor, like the shader nodes.</summary>
        internal static bool OpenEditor(QmlFile file, ILogger logger) => FileEditor.Open(logger, "QML", file.FilePath);
    }
}
