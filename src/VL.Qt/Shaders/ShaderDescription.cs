using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using Microsoft.Extensions.Logging;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Diagnostics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
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
        // Fixed pins like the Shader node (without Fragment/Vertex Code/Shader and Uniforms), help texts from there.
        private static readonly IVLPinDescription[] s_fixedInputs =
        {
            new FilePinDescription("Blending", typeof(bool), true, "Premultiplied alpha blending with what is behind. Off: the shader output replaces it."),
            new FilePinDescription("Mesh Resolution", typeof(Int2), new Int2(1, 1), "Grid subdivisions (x, y >= 1) for vertex shaders that deform the element.", isVisible: false),
            new FilePinDescription("Size", typeof(Vector2), new Vector2(1f, 1f), "Size of the element (width, height), only with Auto Size off or without Input. The position comes from Transform and Anchor."),
            new FilePinDescription("Auto Size", typeof(bool), true, "On: with an Input the implicit size (size of the input) applies. Without Input or off: Size counts.", isVisible: false),
            new FilePinDescription("Transform", typeof(Matrix), Matrix.Identity, "Position, rotation and scale, applied around the Anchor. Inside Row/Column the layout places the element and Transform is only visual."),
            new FilePinDescription("Anchor", typeof(RectangleAnchor), RectangleAnchor.TopLeft, "Point of the element that lies at the origin of its parent space (TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size. Transform rotates and scales around it. Inside Row/Column only the pivot of Transform.", isVisible: false),
            new FilePinDescription("Opacity", typeof(float), 1f, "Opacity of the effect output (qt_Opacity in the shader). 0 = not drawn, but still takes its space and receives input.", isVisible: false),
            new FilePinDescription("Clip", typeof(bool), false, "Clip to the element's size.", isVisible: false),
            new FilePinDescription("Debug", typeof(bool), false, "Draws the debug overlay of this element only, not of its children (the Debug pin of Renderer [Qt] shows all elements); it does not change layout or hit-testing. The colors are explained at the Debug pin of Renderer [Qt] and in the help patch HowTo Debug Overlay.", isVisible: false),
            new FilePinDescription("Interaction", typeof(Interaction), null, "Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input.", isVisible: false),
            new FilePinDescription("Effect", typeof(Effect), null, "Chain of effect nodes (Blur, DropShadow, Glow, ColorAdjust, Mask, …), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer.", isVisible: false),
            new FilePinDescription("Enabled", typeof(bool), true, "False takes the element out of the scene: not drawn, no input and no space in Row/Column (the siblings move up), while it keeps its state. Opacity 0 keeps the gap instead.", isVisible: false),
        };

        private static readonly IVLPinDescription[] s_outputs =
        {
            new FilePinDescription("Output", typeof(IElement), null, null),
            new FilePinDescription("Measured Size", typeof(Vector2), Vector2.Zero, "Size the element takes with Auto Size on, in units (last frame).", isVisible: false),
            new FilePinDescription("Scene Bounds", typeof(RectangleF), RectangleF.Empty, "Bounds in window coordinates after layout and transform, in units (last frame).", isVisible: false),
            new FilePinDescription("Error", typeof(string), string.Empty, "Shader bake errors at runtime (with line numbers), empty if none."),
        };

        private const string inputSummary = "Element sampled as sampler2D \"source\" (hidden itself, only the effect output is drawn).";

        /// <summary>Input pins: Input (if the shader samples source), image and uniform pins, fixed Shader pins.</summary>
        internal static List<IVLPinDescription> Inputs(ShaderLayout layout)
        {
            var inputs = new List<IVLPinDescription>();
            if (layout.HasInput)
            {
                inputs.Add(new FilePinDescription("Input", typeof(IElement), null, inputSummary));
                inputs.Add(new FilePinDescription("Input Sampler", typeof(Sampler), null, "How the shader reads the Input (filter, wrap, mipmaps), from the Sampler node. Unconnected: as the input's layer is (Nearest, Clamp).", isVisible: false));
            }
            foreach (var pin in layout.Pins)
                inputs.Add(new FilePinDescription(pin.Name, UniformPin.ClrType(pin.Type), pin.Default, pin.Summary, isVisible: pin.Type != ShaderPinType.Sampler));
            inputs.AddRange(s_fixedInputs);
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
