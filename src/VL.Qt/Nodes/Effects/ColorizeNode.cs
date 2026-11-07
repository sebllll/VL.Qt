using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Tints the element with a color (Qt MultiEffect colorization). Connect Output to the Effect pin of an element, or
    /// chain it with other effect nodes; the order of the nodes
    /// does not matter within a run.
    /// Neighboring MultiEffect nodes are merged into one layer with one shader, Qt's fixed order applies inside it; a
    /// shader effect (e.g. Glow) ends the run, between runs the patch order applies.
    /// </summary>
    [ProcessNode(Name = "Colorize", Category = Categories.MultiEffect)]
    public sealed class ColorizeNode
    {
        internal readonly record struct Props(Color4 Color, float Amount);

        readonly EffectNode<Props> _node = new(static (previous, p) => new ColorizeEffect(previous, p.Color, p.Amount));

        internal EffectNode<Props> Core => _node;

        /// <summary>Adds a colorization to the effect chain.</summary>
        /// <param name="input">Effect chain to extend (other effect nodes).</param>
        /// <param name="color">Tint color.</param>
        /// <param name="amount">Strength of the tint, 0..1 (0 = off).</param>
        // Pin defaults: see RectangleNode (string-valued [DefaultValue] for Color4).
        [return: Pin(Name = "Output")]
        public Effect Update(
            Effect? input,
            [DefaultValue("1, 0, 0, 1")] Color4 color,
            [DefaultValue(1f)] float amount)
            => _node.Describe(input, new Props(color, amount));
    }
}
