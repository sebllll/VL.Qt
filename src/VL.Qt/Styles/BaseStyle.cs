using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>
    /// The VL.Qt base style: neutral, dark, close to the vvvv/ImGui look. The only place with these values
    /// (QtNative has no defaults of its own). Only (field, state) pairs that differ from the Normal state are listed:
    /// QtNative falls back to (field, Normal) for an unset state, so all dimensions are stored once for all states.
    /// </summary>
    internal static class BaseStyle
    {
        // Colors as 0xRRGGBB (sRGB, opaque).
        private static Color4 C(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

        // Dimensions are set at Normal only.
        private static StateValues<float> N(float v) => default(StateValues<float>).With(StyleStates.Normal, v);

        private static readonly Style s_default = new()
        {
            Frame = new FrameStyle
            {
                Color = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0x383838)).With(StyleStates.Hovered, C(0x454545))
                    .With(StyleStates.Pressed, C(0x2A2A2A)).With(StyleStates.Disabled, C(0x2E2E2E)),
                BorderColor = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0x505050)).With(StyleStates.Hovered, C(0x6A6A6A))
                    .With(StyleStates.Disabled, C(0x3A3A3A)),
                Radius = N(2),
                BorderWidth = N(1),
                PaddingX = N(8),
                PaddingY = N(4),
                MinWidth = N(24),
                MinHeight = N(24),
            },
            Text = new TextStyle
            {
                Color = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0xE0E0E0)).With(StyleStates.Disabled, C(0x7A7A7A)),
                FontFamily = default(StateValues<string>).With(StyleStates.Normal, ""),
                FontSize = N(13),
                FontWeight = default(StateValues<int>).With(StyleStates.Normal, 400),
            },
            Indicator = new IndicatorStyle
            {
                Color = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0x9A9A9A)).With(StyleStates.Hovered, C(0xC8C8C8))
                    .With(StyleStates.Pressed, C(0xFFFFFF)).With(StyleStates.Disabled, C(0x5A5A5A))
                    .With(StyleStates.Checked, C(0xE0E0E0)),
                Size = N(14),
                Radius = N(2),
                BorderWidth = N(1),
            },
            Track = new TrackStyle
            {
                Color = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0x2A2A2A)).With(StyleStates.Disabled, C(0x262626)),
                FillColor = default(StateValues<Color4>)
                    .With(StyleStates.Normal, C(0x5A8FCB)).With(StyleStates.Hovered, C(0x6CA0DB))
                    .With(StyleStates.Disabled, C(0x44556A)),
                Thickness = N(4),
                Radius = N(2),
            },
        };

        /// <summary>The base style as a <see cref="Style"/> (all fields set at Normal), root of the resolution.</summary>
        public static Style Default => s_default;

        /// <summary>Writes the complete base style of element <paramref name="id"/> as one SET_STYLE (with CLEAR).</summary>
        public static void Write(ICommandSink sink, uint id) => StyleWriter.Write(sink, id, Default, null);
    }
}
