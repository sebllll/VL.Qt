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

        /// <summary>Controls for status/debug panels (like ImGui): Button, Toggle, Slider, Input, TextInput.</summary>
        internal const string Widgets = "Qt.Widgets";

        /// <summary>Pointer, touch and wheel input attached to an element.</summary>
        internal const string Interaction = "Qt.Interaction";

        /// <summary>Blur, shadow, color and mask effects attached to an element (Effect pin).</summary>
        internal const string Effects = "Qt.Effects";

        /// <summary>Builder nodes that create a QtPath step by step (MoveTo, LineTo, AddRect, ...), modelled on Skia.Paths.</summary>
        internal const string Paths = "Qt.Paths";

        /// <summary>Effects Qt merges into one MultiEffect layer (Blur, DropShadow, ColorAdjust, Colorize, Mask).</summary>
        internal const string MultiEffect = "Qt.Effects.MultiEffect";
    }
}
