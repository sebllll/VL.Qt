using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Qml
{
    /// <summary>One common pin of <c>QmlElement</c> and of every generated QML node.</summary>
    /// <param name="Name">Pin name.</param>
    /// <param name="Type">Pin type.</param>
    /// <param name="Default">Default value, null for none.</param>
    /// <param name="IsVisible">False for an optional pin.</param>
    /// <param name="Summary">Pin help.</param>
    internal sealed record QmlCommonPin(string Name, Type Type, object? Default, bool IsVisible, string Summary);

    /// <summary>
    /// The common pins of <see cref="Nodes.QmlElementNode"/> and of the generated QML nodes, once: names, types, defaults, visibility,
    /// order and help. <see cref="QmlNodeLayout"/> builds its descriptions from it, <see cref="QmlNodeInstance"/> its pins; a test
    /// compares it with <c>QmlElementNode.Update</c>.
    /// <para>The order is that of QmlElement. A generated node has the property pins where QmlElement has File:
    /// <see cref="LeadingInputs"/>, the property inputs, Properties, <see cref="TrailingInputs"/>.</para>
    /// </summary>
    internal static class QmlCommonPins
    {
        /// <summary>Name of the optional dictionary input.</summary>
        public const string PropertiesName = "Properties";

        /// <summary>Plan B for what has no pin: values by name, as at QmlElement.</summary>
        public static readonly QmlCommonPin Properties = new(PropertiesName, typeof(Dictionary<string, object>), null, false,
            "Plan B for properties of the QML root that get no pin (alias, var, list, object types): values by name, as at QmlElement. A pin of the node wins over an entry with the same name once the pin differs from its default.");

        /// <summary>Inputs before the property inputs.</summary>
        public static readonly ImmutableArray<QmlCommonPin> LeadingInputs = ImmutableArray.Create(
            new QmlCommonPin("Auto Size", typeof(bool), true, false, "On: the element takes the implicit size (implicitWidth, implicitHeight) of the QML root. Off: Size counts."),
            new QmlCommonPin("Size", typeof(Vector2), new Vector2(1f, 1f), false, "Size of the element (width, height), only with Auto Size off: it sets width and height of the QML root. The position comes from Transform and Anchor."),
            new QmlCommonPin("Transform", typeof(Matrix), Matrix.Identity, true, "Position, rotation and scale, applied around the Anchor. Inside Row/Column the layout places the element and Transform is only visual."),
            new QmlCommonPin("Anchor", typeof(RectangleAnchor), RectangleAnchor.TopLeft, false, "Point of the element that lies at the origin of its parent space (TopLeft: the top-left corner; Center: the element is centered on it), resolved against the actual size. Transform rotates and scales around it. Inside Row/Column only the pivot of Transform."));

        /// <summary>Inputs after Properties.</summary>
        public static readonly ImmutableArray<QmlCommonPin> TrailingInputs = ImmutableArray.Create(
            new QmlCommonPin("Opacity", typeof(float), 1f, false, "Opacity of the element, including its children. 0 = not drawn and costs no render time, but the element keeps its space in Row/Column and still receives input. Enabled off removes the element from the layout instead."),
            new QmlCommonPin("Clip", typeof(bool), false, false, "Clips the drawing of the element and of its children to the element's size: text that overflows, the overhang of a cropped Image and children outside the frame are cut."),
            new QmlCommonPin("Debug", typeof(bool), false, false, "Draws the debug overlay of this element only, not of its children (the Debug pin of Renderer [Qt] shows all elements); it does not change layout or hit-testing. The colors are explained at the Debug pin of Renderer [Qt] and in the help patch HowTo Debug Overlay."),
            new QmlCommonPin("Interaction", typeof(Interaction), null, false, "Chain of interaction nodes (Hover, Click, …). Without interaction the element is transparent for input."),
            new QmlCommonPin("Effect", typeof(Effect), null, false, "Chain of effect nodes (Blur, DropShadow, Glow, ColorAdjust, Mask, …), drawn as one effect layer on the element. Size, transform, layout and hit-testing stay the same. Without effect the element has no layer."),
            new QmlCommonPin("Enabled", typeof(bool), true, false, "False takes the element out of the scene: not drawn, no input and no space in Row/Column (the siblings move up), while it keeps its state. Opacity 0 keeps the gap instead."));

        /// <summary>The first output.</summary>
        public static readonly QmlCommonPin Output = new("Output", typeof(IElement), null, true, "");

        /// <summary>Outputs after the property and signal outputs.</summary>
        public static readonly ImmutableArray<QmlCommonPin> TrailingOutputs = ImmutableArray.Create(
            new QmlCommonPin("Measured Size", typeof(Vector2), Vector2.Zero, false, "Size the QML root takes with Auto Size on, in units (last frame)."),
            new QmlCommonPin("Scene Bounds", typeof(RectangleF), RectangleF.Empty, false, "Bounds in window coordinates after layout and transform, in units (last frame)."));

        /// <summary>Pin names a property or signal pin must not take: the common pins of both sides and Properties.</summary>
        public static readonly ImmutableHashSet<string> ReservedNames = BuildReservedNames();

        /// <summary>The default of the common pin <paramref name="name"/> as <typeparamref name="T"/> (default(T) if it has none).</summary>
        public static T Default<T>(string name)
        {
            foreach (var pins in new[] { LeadingInputs, TrailingInputs, TrailingOutputs })
                foreach (var pin in pins)
                    if (pin.Name == name)
                        return pin.Default is T value ? value : default!;
            throw new ArgumentException($"No common pin '{name}'.", nameof(name));
        }

        private static ImmutableHashSet<string> BuildReservedNames()
        {
            var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(PropertiesName);
            names.Add(Output.Name);
            foreach (var pin in LeadingInputs)
                names.Add(pin.Name);
            foreach (var pin in TrailingInputs)
                names.Add(pin.Name);
            foreach (var pin in TrailingOutputs)
                names.Add(pin.Name);
            return names.ToImmutable();
        }
    }
}
