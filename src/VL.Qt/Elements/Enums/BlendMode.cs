namespace VL.Qt.Elements
{
    /// <summary>How <see cref="Nodes.BlendNode"/> combines the layer with the element (W3C Compositing and Blending, separable modes).</summary>
    public enum BlendMode
    {
        Normal,
        Multiply,
        Screen,
        Overlay,
        Add,
        Subtract,
        Difference,
        Darken,
        Lighten,
        SoftLight,
        HardLight,
        ColorDodge,
        ColorBurn,
    }
}
