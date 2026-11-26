using System;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// The fixed pins of <see cref="Nodes.ShaderNode"/> and of the generated shader nodes, once: names, types, defaults,
    /// visibility, order and help. <see cref="ShaderDescription"/> builds its pin descriptions from it, <see cref="ShaderInstance"/>
    /// its pins, <see cref="ShaderLayout"/> the reserved pin names; a test compares it with <c>ShaderNode.Update</c>.
    /// <para>The order is that of Shader, where a generated node has Input, Input Sampler (with an Input), the uniform pins,
    /// <see cref="FixedInputs"/> in place of the code, file and uniform inputs.</para>
    /// </summary>
    internal static class ShaderElementPins
    {
        /// <summary>The Input pin: the element the shader samples as sampler2D "source".</summary>
        public static readonly ShaderElementPin Input = new("Input", typeof(IElement), null, true,
            "Element sampled as sampler2D \"source\" (hidden itself, only the effect output is drawn). Unconnected = generative shader.");

        /// <summary>How the shader reads the Input.</summary>
        public static readonly ShaderElementPin InputSampler = new("Input Sampler", typeof(Sampler), null, false,
            "How the shader reads the Input (filter, wrap, mipmaps), from the Sampler node. Unconnected = as the input's layer is (Nearest, Clamp).");

        /// <summary>The inputs after the uniform pins.</summary>
        // Blending is visible on a generated node, optional on Shader: the one difference in visibility.
        public static readonly ImmutableArray<ShaderElementPin> FixedInputs = ImmutableArray.Create(
            new ShaderElementPin("Blending", typeof(bool), true, true, "Premultiplied alpha blending with what is behind. Off: the shader output replaces it."),
            new ShaderElementPin("Mesh Resolution", typeof(Int2), new Int2(1, 1), false, "Grid subdivisions (x, y >= 1) for vertex shaders that deform the element. Unconnected = 1, 1 (one quad)."),
            new ShaderElementPin("Auto Size", typeof(bool), true, false, "On: with an Input the implicit size (size of the input) applies. Without Input or off: Size counts."),
            new ShaderElementPin("Size", typeof(Vector2), new Vector2(1f, 1f), false, "Size of the element (width, height), only with Auto Size off or without Input. The position comes from Transform and Anchor."),
            new ShaderElementPin("Transform", typeof(Matrix), Matrix.Identity, true, "Position, rotation and scale, applied around the Anchor. Inside Row/Column the layout places the element and Transform is only visual."),
            new ShaderElementPin("Anchor", typeof(RectangleAnchor), RectangleAnchor.TopLeft, false, "Point of the element that lies at the origin of its parent space (TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size. Transform rotates and scales around it. Inside Row/Column only the pivot of Transform."),
            new ShaderElementPin("Opacity", typeof(float), 1f, false, "Opacity of the effect output (qt_Opacity in the shader). 0 = not drawn (costs no render time), but still takes its space in Row/Column and still receives input (invisible hit areas)."),
            new ShaderElementPin("Clip", typeof(bool), false, false, "Clip to the element's size."),
            new ShaderElementPin("Debug", typeof(bool), false, false, "Draws the debug overlay of this element only, not of its children (the Debug pin of Renderer [Qt] shows all elements); it does not change layout or hit-testing. The colors are explained at the Debug pin of Renderer [Qt] and in the help patch HowTo Debug Overlay."),
            new ShaderElementPin("Interaction", typeof(Interaction), null, false, "Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input."),
            new ShaderElementPin("Effect", typeof(Effect), null, false, "Chain of effect nodes (Blur, DropShadow, Glow, ColorAdjust, Mask, …), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer."),
            new ShaderElementPin("Enabled", typeof(bool), true, false, "False takes the element out of the scene: not drawn, no input and no space in Row/Column (the siblings move up), while it keeps its state. Opacity 0 keeps the gap instead."));

        /// <summary>The first output.</summary>
        public static readonly ShaderElementPin Output = new("Output", typeof(IElement), null, true, "");

        /// <summary>The outputs after Output.</summary>
        public static readonly ImmutableArray<ShaderElementPin> TrailingOutputs = ImmutableArray.Create(
            new ShaderElementPin("Measured Size", typeof(Vector2), Vector2.Zero, false, "Size the element takes with Auto Size on, in units (last frame)."),
            new ShaderElementPin("Scene Bounds", typeof(RectangleF), RectangleF.Empty, false, "Bounds in window coordinates after layout and transform, in units (last frame)."));

        /// <summary>Pin names a uniform pin of a generated shader node must not take: all pins of the node besides the uniform pins.</summary>
        public static readonly ImmutableHashSet<string> Names = BuildNames();

        /// <summary>The default of the fixed pin <paramref name="name"/> as <typeparamref name="T"/> (default(T) if it has none).</summary>
        public static T Default<T>(string name)
        {
            foreach (var pin in FixedInputs)
                if (pin.Name == name)
                    return pin.Default is T value ? value : default!;
            foreach (var pin in TrailingOutputs)
                if (pin.Name == name)
                    return pin.Default is T value ? value : default!;
            throw new ArgumentException($"No fixed pin '{name}'.", nameof(name));
        }

        private static ImmutableHashSet<string> BuildNames()
        {
            var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(Input.Name);
            names.Add(InputSampler.Name);
            names.Add(Output.Name);
            foreach (var pin in FixedInputs)
                names.Add(pin.Name);
            foreach (var pin in TrailingOutputs)
                names.Add(pin.Name);
            return names.ToImmutable();
        }
    }
}
