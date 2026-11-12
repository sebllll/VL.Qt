using System;
using Microsoft.Extensions.Logging;
using VL.Core;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Node description of one .qml file (#107). init (lazy, when vvvv needs the pins): <see cref="QmlNodeLayout.Load"/>, then
    /// <c>context.Node(layout.InputDescriptions(), layout.OutputDescriptions(), newNode: c =&gt; new QmlNodeInstance(c.NodeContext, layout).CreateNode(c),
    /// messages: layout.Messages, openEditorAction, summary: layout.Summary ?? file.Summary, filePath: file.FilePath)</c>; not
    /// fragmented (like the shader nodes, so vvvv calls Update). Name <see cref="QmlFile.FullName"/>, category
    /// <see cref="QmlFile.Category"/>, tags <see cref="QmlFile.Tags"/> (the tags argument of NewNodeDescription). invalidated:
    /// <see cref="QmlNodeFactory.WatchSignature"/> with the signature of the layout, so only a change of the declarations
    /// rebuilds the node; vvvv keeps links on pins with the same name. A file that does not parse still gets a node with the
    /// common pins and its errors as messages. Smell: experimental, like QmlElement.
    /// </summary>
    internal static class QmlNodeDescription
    {
        /// <summary>Creates the node description of <paramref name="file"/>.</summary>
        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, QmlFile file, ILogger logger)
            => throw new NotImplementedException("T3: QML node description (#107)");

        /// <summary>Middle click / Definition > Open: opens the .qml in the default editor, like the shader nodes.</summary>
        internal static bool OpenEditor(QmlFile file, ILogger logger)
            => throw new NotImplementedException("T3: QML node description (#107)");
    }
}
