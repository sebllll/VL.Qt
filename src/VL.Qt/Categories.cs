namespace VL.Qt
{
    /// <summary>
    /// Node browser categories of VL.Qt. Patched nodes in VL.Qt.vl use the same paths.
    /// </summary>
    internal static class Categories
    {
        /// <summary>Renderer and other top-level nodes.</summary>
        internal const string Qt = "Qt";

        /// <summary>Visual building blocks of a designed scene: shapes, text, media, layout containers, effects.</summary>
        internal const string Elements = "Qt.Elements";

        /// <summary>Controls for status/debug panels (like ImGui).</summary>
        internal const string Widgets = "Qt.Widgets";

        /// <summary>Pointer, touch and wheel input attached to an element.</summary>
        internal const string Interaction = "Qt.Interaction";

        /// <summary>Blur, shadow, color, mask and shader effects attached to an element (Effect pin).</summary>
        internal const string Effects = "Qt.Effects";

        /// <summary>Builder nodes that create a QtPath step by step (MoveTo, LineTo, AddRect, ...), modelled on Skia.Paths.</summary>
        internal const string Paths = "Qt.Paths";

        /// <summary>Loading, converting, grabbing and writing images.</summary>
        internal const string Images = "Qt.Images";

        /// <summary>Shader element, Sampler and the nodes made from shader files (uniform nodes in the subcategory Uniform).</summary>
        internal const string Shaders = "Qt.Elements.Shaders";

        /// <summary>QmlElement and the nodes made from QML files.</summary>
        internal const string Qml = "Qt.Elements.Qml";
    }
}
