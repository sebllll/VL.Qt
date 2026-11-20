using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;
using VL.Core;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Node description of one .qml file. init (lazy, when vvvv needs the pins): <see cref="QmlNodeLayout.Load"/>, then
    /// <c>context.Node(layout.InputDescriptions(), layout.OutputDescriptions(), newNode: c =&gt; new QmlNodeInstance(c.NodeContext, layout).CreateNode(c),
    /// messages: layout.Messages, openEditorAction, summary: layout.Summary ?? file.Summary, filePath: file.FilePath)</c>; not
    /// fragmented (like the shader nodes, so vvvv calls Update). Name <see cref="QmlFile.FullName"/>, category
    /// <see cref="QmlFile.Category"/>, tags <see cref="QmlFile.Tags"/> (the tags argument of NewNodeDescription). invalidated:
    /// <see cref="QmlNodeFactory.WatchSignature(QmlFile, string)"/> with the signature of the layout, so only a change of the declarations
    /// rebuilds the node; vvvv keeps links on pins with the same name. A file that does not parse still gets a node with the
    /// common pins and its errors as messages.
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
            return factory.NewNodeDescription(
                file.FullName,
                file.Category,
                // Not fragmented: vvvv calls IVLNode.Update only for non-fragmented node descriptions (like the shader nodes).
                fragmented: false,
                invalidated: QmlNodeFactory.WatchSignature(file, () => Volatile.Read(ref signature)),
                init: context =>
                {
                    var layout = QmlNodeLayout.Load(file, logger);
                    Volatile.Write(ref signature, layout.Declarations.Signature);
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
        internal static bool OpenEditor(QmlFile file, ILogger logger)
        {
            try
            {
                Process.Start(new ProcessStartInfo(file.FilePath) { UseShellExecute = true });
                return true;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not open QML file {Path}", file.FilePath);
                return false;
            }
        }
    }
}
