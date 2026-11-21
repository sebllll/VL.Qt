using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Blurs the element (Qt MultiEffect blur). Connect Output to the Effect pin of an element, or chain it with other
    /// effect nodes; the order of the nodes
    /// does not matter within a run.
    /// Blur, DropShadow, ColorAdjust, Colorize and Mask next to each other in a chain are merged into one layer with one
    /// shader, Qt's fixed order applies inside it; a shader effect (e.g. Glow) ends the run, between runs the patch order applies.
    /// </summary>
    [ProcessNode(Name = "Blur", Category = Categories.Effects)]
    public sealed class BlurNode
    {
        internal readonly record struct Props(float Amount, int Max, float Multiplier, bool AutoPadding = true, RectangleF Padding = default);

        private readonly EffectNode<Props> _node = new(static (previous, p) => new BlurEffect(previous, p.Amount, p.Max, p.Multiplier, p.AutoPadding, p.Padding));

        internal EffectNode<Props> Core => _node;

        /// <summary>Adds a blur to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="amount">Strength of the blur, 0..1 (0 = no blur).</param>
        /// <param name="max">Maximum blur radius in pixels at Amount 1; also limits the shadow blur. Changing it is expensive (switches shaders).</param>
        /// <param name="multiplier">Values above 0 enlarge the blur beyond Max with lower quality.</param>
        /// <param name="autoPadding">On: the effect area grows by what the blur needs. Off: Padding applies. Qt has one effect area per element: if any Blur or DropShadow in the chain has this off, the area is the side-wise maximum of the Padding of all those links.</param>
        /// <param name="padding">Extra effect area around the element in units (x = left, y = top, width = right, height = bottom); only used with Auto Padding off.</param>
        [return: Pin(Name = "Output")]
        public Effect Update(
            Effect? input,
            [DefaultValue(0.5f)] float amount,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(32)] int max,
            [Pin(Visibility = PinVisibility.Optional)] float multiplier,
            [Pin(Visibility = PinVisibility.Optional), DefaultValue(true)] bool autoPadding,
            [Pin(Visibility = PinVisibility.Optional)] RectangleF padding)
            => _node.Describe(input, new Props(amount, max, multiplier, autoPadding, padding));
    }
}
