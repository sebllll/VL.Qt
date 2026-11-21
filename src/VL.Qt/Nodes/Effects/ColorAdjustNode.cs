using System.ComponentModel;
using VL.Core.Import;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Adjusts brightness, contrast and saturation of the element (Qt MultiEffect). Connect Output to the Effect pin of an
    /// element, or chain it with other effect nodes; the order of the nodes
    /// does not matter within a run.
    /// Blur, DropShadow, ColorAdjust, Colorize and Mask next to each other in a chain are merged into one layer with one
    /// shader, Qt's fixed order applies inside it; a shader effect (e.g. Glow) ends the run, between runs the patch order applies.
    /// </summary>
    [ProcessNode(Name = "ColorAdjust", Category = Categories.Effects)]
    public sealed class ColorAdjustNode
    {
        internal readonly record struct Props(float Brightness, float Contrast, float Saturation);

        private readonly EffectNode<Props> _node = new(static (previous, p) => new ColorAdjustEffect(previous, p.Brightness, p.Contrast, p.Saturation));

        internal EffectNode<Props> Core => _node;

        /// <summary>Adds a color adjustment to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="brightness">-1..1, 0 = unchanged.</param>
        /// <param name="contrast">-1..1, 0 = unchanged.</param>
        /// <param name="saturation">-1..1, 0 = unchanged, -1 = grey (default, so the effect is visible).</param>
        [return: Pin(Name = "Output")]
        public Effect Update(
            Effect? input,
            float brightness,
            float contrast,
            [DefaultValue(-1f)] float saturation)
            => _node.Describe(input, new Props(brightness, contrast, saturation));
    }
}
