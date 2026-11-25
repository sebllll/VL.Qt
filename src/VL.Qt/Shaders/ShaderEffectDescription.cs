using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Qt.Elements;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Node description of the effect chain link of one filter shader file: same name as the element node, category
    /// <see cref="ShaderFile.LinkCategory"/>. Same lifecycle as <see cref="ShaderDescription"/> (lazy init, content
    /// change invalidates the node and keeps links on pins with the same name, shader problems at the node).
    /// </summary>
    internal static class ShaderEffectDescription
    {
        private static readonly IVLPinDescription[] s_outputs =
        {
            new FilePinDescription("Output", typeof(Effect), null, null),
        };

        private const string inputSummary = "Effect chain to extend: what the other effect nodes before this one made of the element. This link works on that result.";

        private const string applySummary = "On: the effect is applied. Off: the effect chain passes on unchanged, as if this node was not there.";

        /// <summary>Input pins: Input (the chain before), image and uniform pins, Apply last (hidden).</summary>
        internal static List<IVLPinDescription> Inputs(ShaderEffectLayout layout)
        {
            var inputs = new List<IVLPinDescription> { new FilePinDescription("Input", typeof(Effect), null, inputSummary) };
            foreach (var pin in layout.Pins)
                inputs.Add(new FilePinDescription(pin.Name, UniformPin.ClrType(pin.Type), pin.Default, pin.Summary, isVisible: pin.Type != ShaderPinType.Sampler));
            inputs.Add(new FilePinDescription("Apply", typeof(bool), true, applySummary, isVisible: false));
            return inputs;
        }

        internal static IReadOnlyList<IVLPinDescription> Outputs => s_outputs;

        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, ShaderFile file, ILogger logger, ShaderSourceCache cache) =>
            factory.NewNodeDescription(
                file.FullName,
                file.LinkCategory,
                fragmented: false, // like the element node: vvvv calls Update only for non-fragmented descriptions
                invalidated: ShaderNodeFactory.WatchContent(file),
                init: context =>
                {
                    var layout = ShaderEffectLayout.From(cache.Get(file), file);
                    cache.LogOnce(file, layout.Messages);
                    return context.Node(
                        Inputs(layout),
                        Outputs,
                        newNode: c => new ShaderEffectInstance(c.NodeContext, layout).CreateNode(c),
                        messages: layout.Messages,
                        openEditorAction: () => ShaderDescription.OpenEditor(file, logger),
                        summary: layout.Summary ?? file.Summary,
                        filePath: file.FragmentPath);
                });
    }
}
