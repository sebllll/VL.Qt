using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using Microsoft.Extensions.Logging;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Qt.NodeFactories;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// Node description of one shader file. init (lazy, when vvvv needs the pins): read
    /// the files, reflect, build the pins (Input if the shader samples source, image and uniform pins, fixed Shader
    /// pins), errors and warnings as messages of the description. invalidated: content change of the own .frag/.vert;
    /// vvvv then asks for a new description and keeps links on pins with the same name.
    /// </summary>
    internal static class ShaderDescription
    {
        private static readonly IReadOnlyList<IVLPinDescription> s_outputs =
            new[] { ShaderElementPins.Output }.Concat(ShaderElementPins.TrailingOutputs).Select(Describe).ToArray();

        private static IVLPinDescription Describe(ShaderElementPin pin) =>
            new FilePinDescription(pin.Name, pin.Type, pin.Default, pin.Summary.Length == 0 ? null : pin.Summary, pin.IsVisible);

        /// <summary>Input pins: Input (if the shader samples source), image and uniform pins, fixed Shader pins.</summary>
        internal static List<IVLPinDescription> Inputs(ShaderLayout layout)
        {
            var inputs = new List<IVLPinDescription>();
            if (layout.HasInput)
            {
                inputs.Add(Describe(ShaderElementPins.Input));
                inputs.Add(Describe(ShaderElementPins.InputSampler));
            }
            foreach (var pin in layout.Pins)
                inputs.Add(new FilePinDescription(pin.Name, UniformPin.ClrType(pin.Type), pin.Default, pin.Summary, isVisible: pin.Type != ShaderPinType.Sampler));
            foreach (var pin in ShaderElementPins.FixedInputs)
                inputs.Add(Describe(pin));
            return inputs;
        }

        /// <summary>Output pins like the Shader node.</summary>
        internal static IReadOnlyList<IVLPinDescription> Outputs => s_outputs;

        public static IVLNodeDescription New(IVLNodeDescriptionFactory factory, ShaderFile file, ILogger logger) =>
            factory.NewNodeDescription(
                file.FullName,
                file.Category,
                // Not fragmented: vvvv calls IVLNode.Update only for non-fragmented node descriptions (like VL.ImGui's
                // immediate-mode nodes); fragmented ones (VL.Stride shader nodes) compute in output getters instead.
                fragmented: false,
                invalidated: ShaderNodeFactory.WatchContent(file),
                init: context =>
                {
                    var layout = ShaderLayout.From(ShaderSource.Load(file, ShaderReflection.Reflect), file);
                    LogMessages(logger, file, layout.Messages);
                    return context.Node(
                        Inputs(layout),
                        Outputs,
                        newNode: c => new ShaderInstance(c.NodeContext, layout).CreateNode(c),
                        messages: layout.Messages,
                        openEditorAction: () => OpenEditor(file, logger),
                        summary: layout.Summary ?? file.Summary,
                        filePath: file.FragmentPath);
                });

        /// <summary>Logs what the node shows (errors, warnings), except the duplicate error that the factory logs once per scan.</summary>
        internal static void LogMessages(ILogger logger, ShaderFile file, ImmutableArray<Message> messages)
        {
            foreach (var m in messages)
            {
                if (m.Text == file.DuplicateError)
                    continue;
                if (m.Type == MessageType.Error)
                    logger.LogError("{Shader}: {Error}", file.FragmentPath, m.Text);
                else
                    logger.LogWarning("{Shader}: {Warning}", file.FragmentPath, m.Text);
            }
        }

        /// <summary>
        /// Middle click / Definition > Open: opens the .frag and, if there is one, the .vert next to it in the
        /// default editor, like VL.Stride's shader nodes.
        /// </summary>
        internal static bool OpenEditor(ShaderFile file, ILogger logger) =>
            file.VertexPath != null && File.Exists(file.VertexPath)
                ? FileEditor.Open(logger, "shader", file.FragmentPath, file.VertexPath)
                : FileEditor.Open(logger, "shader", file.FragmentPath);
    }
}
