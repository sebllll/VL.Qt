using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Styles;

namespace VL.Qt.Tests
{
    /// <summary>Style nodeset (#294): setters, getters, Merge, ResolveState.</summary>
    public class StyleNodesTests
    {
        private static readonly Color4 red = new(1, 0, 0, 1);
        private static readonly Color4 green = new(0, 1, 0, 1);
        private static readonly Color4 blue = new(0, 0, 1, 1);

        private static Optional<T> O<T>(T v) => new(v);

        [Test]
        public void NullInput_Getters_ReturnBaseStyle()
        {
            var b = BaseStyle.Default;
            Assert.That(FrameNodes.GetColor(null), Is.EqualTo(b.Frame!.Color.Normal.Value));
            Assert.That(FrameNodes.GetRadius(null, StyleState.Pressed), Is.EqualTo(2));
            Assert.That(TextNodes.GetFontSize(null), Is.EqualTo(13));
            Assert.That(TextNodes.GetFontFamily(null), Is.EqualTo(""));
            Assert.That(IndicatorNodes.GetSize(null), Is.EqualTo(14));
            Assert.That(TrackNodes.GetThickness(null), Is.EqualTo(4));
            StyleNodes.GetColors(null, StyleState.Hovered, out var frame, out _, out _, out _, out _, out var fill);
            Assert.That(frame, Is.EqualTo(b.Frame.Color.Hovered.Value));
            Assert.That(fill, Is.EqualTo(b.Track!.FillColor.Hovered.Value));
            FrameNodes.GetFrame(null, StyleState.Normal, out var r, out var bw, out var px, out var py, out var mw, out var mh);
            Assert.That((r, bw, px, py, mw, mh), Is.EqualTo((2f, 1f, 8f, 4f, 24f, 24f)));
            TextNodes.GetText(null, StyleState.Normal, out var fam, out var size, out var weight);
            Assert.That((fam, size, weight), Is.EqualTo(("", 13f, 400)));
            IndicatorNodes.GetIndicator(null, StyleState.Normal, out var isz, out var ir, out var ibw);
            Assert.That((isz, ir, ibw), Is.EqualTo((14f, 2f, 1f)));
            TrackNodes.GetTrack(null, StyleState.Normal, out var th, out var tr);
            Assert.That((th, tr), Is.EqualTo((4f, 2f)));
        }

        [Test]
        public void NullPart_Getters_DoNotThrow()
        {
            var style = FrameNodes.Radius(null, O(9f));
            Assert.That(TextNodes.GetFontWeight(style), Is.EqualTo(400));
            Assert.That(TrackNodes.GetFillColor(style), Is.EqualTo(BaseStyle.Default.Track!.FillColor.Normal.Value));
            Assert.That(FrameNodes.GetRadius(style), Is.EqualTo(9));
        }

        [Test]
        public void NullInput_Setters_DoNotThrow()
        {
            Assert.That(StyleNodes.Colors(null, frame: O(red)).Frame!.Color.Normal.Value, Is.EqualTo(red));
            Assert.That(FrameNodes.Frame(null, radius: O(3f)).Frame!.Radius.Normal.Value, Is.EqualTo(3));
            Assert.That(TextNodes.Text(null, fontFamily: O("Arial")).Text!.FontFamily.Normal.Value, Is.EqualTo("Arial"));
            Assert.That(IndicatorNodes.Indicator(null, size: O(20f)).Indicator!.Size.Normal.Value, Is.EqualTo(20));
            Assert.That(TrackNodes.Track(null, thickness: O(8f)).Track!.Thickness.Normal.Value, Is.EqualTo(8));
            Assert.That(FrameNodes.Color(null, O(red)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.BorderColor(null, O(red)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.PaddingX(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.PaddingY(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.MinWidth(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.MinHeight(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.BorderWidth(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(TextNodes.Color(null, O(red)).Text, Is.Not.Null);
            Assert.That(TextNodes.FontSize(null, O(1f)).Text, Is.Not.Null);
            Assert.That(TextNodes.FontWeight(null, O(700)).Text, Is.Not.Null);
            Assert.That(TextNodes.FontFamily(null, O("x")).Text, Is.Not.Null);
            Assert.That(IndicatorNodes.Color(null, O(red)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.Radius(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.BorderWidth(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.Size(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(TrackNodes.Color(null, O(red)).Track, Is.Not.Null);
            Assert.That(TrackNodes.FillColor(null, O(red)).Track, Is.Not.Null);
            Assert.That(TrackNodes.Thickness(null, O(1f)).Track, Is.Not.Null);
            Assert.That(TrackNodes.Radius(null, O(1f)).Track, Is.Not.Null);
        }

        [Test]
        public void Setter_States_SetExactlyThoseStates()
        {
            var all = FrameNodes.Radius(null, O(5f), StyleStates.All).Frame!.Radius;
            Assert.That(all.Normal.HasValue && all.Hovered.HasValue && all.Pressed.HasValue && all.Disabled.HasValue && all.Checked.HasValue, Is.True);

            var hp = FrameNodes.Radius(null, O(5f), StyleStates.Hovered | StyleStates.Pressed).Frame!.Radius;
            Assert.That(hp.Hovered.HasValue, Is.True);
            Assert.That(hp.Pressed.HasValue, Is.True);
            Assert.That(hp.Normal.HasValue, Is.False);
            Assert.That(hp.Disabled.HasValue, Is.False);
            Assert.That(hp.Checked.HasValue, Is.False);
        }

        [Test]
        public void Setter_UnconnectedPins_StayUnset()
        {
            var style = FrameNodes.Frame(null, radius: O(4f));
            Assert.That(style.Frame!.Radius.Normal.Value, Is.EqualTo(4));
            Assert.That(style.Frame.BorderWidth.IsEmpty, Is.True);
            Assert.That(style.Frame.Color.IsEmpty, Is.True);
            Assert.That(style.Text, Is.Null);
            Assert.That(style.Track, Is.Null);

            var colors = StyleNodes.Colors(null, text: O(green), state: StyleStates.Disabled);
            Assert.That(colors.Text!.Color.Disabled.Value, Is.EqualTo(green));
            Assert.That(colors.Text.Color.Normal.HasValue, Is.False);
            Assert.That(colors.Frame, Is.Null);
            Assert.That(colors.Indicator, Is.Null);
            Assert.That(colors.Track, Is.Null);
        }

        [Test]
        public void Setter_NothingSet_ReturnsInputUnchanged()
        {
            var a = FrameNodes.Radius(null, O(4f));
            Assert.That(StyleNodes.Colors(a), Is.SameAs(a));
            Assert.That(FrameNodes.Frame(a), Is.SameAs(a));
            Assert.That(TextNodes.Text(a), Is.SameAs(a));
            Assert.That(IndicatorNodes.Indicator(a), Is.SameAs(a));
            Assert.That(TrackNodes.Track(a), Is.SameAs(a));
            Assert.That(FrameNodes.Radius(a), Is.SameAs(a));
            Assert.That(FrameNodes.Radius(a, O(9f), StyleStates.None), Is.SameAs(a));
            Assert.That(StyleNodes.Colors(null), Is.SameAs(Style.Empty));
            Assert.That(FrameNodes.Frame(null), Is.SameAs(Style.Empty));
        }

        [Test]
        public void Chain_ColorsFrameGetter()
        {
            var style = StyleNodes.Colors(null, frame: O(red), trackFill: O(blue));
            style = FrameNodes.Frame(style, radius: O(12f));
            style = FrameNodes.Color(style, O(green), StyleStates.Hovered);

            Assert.That(FrameNodes.GetColor(style), Is.EqualTo(red));
            Assert.That(FrameNodes.GetColor(style, StyleState.Hovered), Is.EqualTo(green));
            Assert.That(FrameNodes.GetColor(style, StyleState.Pressed), Is.EqualTo(red));
            Assert.That(FrameNodes.GetRadius(style), Is.EqualTo(12));
            Assert.That(TrackNodes.GetFillColor(style, StyleState.Hovered), Is.EqualTo(blue));
            // Not set in the style: from the base style.
            Assert.That(FrameNodes.GetBorderWidth(style), Is.EqualTo(1));
            StyleNodes.GetColors(style, StyleState.Normal, out var frame, out var border, out _, out _, out _, out var fill);
            Assert.That((frame, fill), Is.EqualTo((red, blue)));
            Assert.That(border, Is.EqualTo(BaseStyle.Default.Frame!.BorderColor.Normal.Value));
        }

        [Test]
        public void Getter_StateSetOnlyForHovered_FallsBackToBaseNormal()
        {
            var style = FrameNodes.Radius(null, O(30f), StyleStates.Hovered);
            Assert.That(FrameNodes.GetRadius(style, StyleState.Hovered), Is.EqualTo(30));
            Assert.That(FrameNodes.GetRadius(style, StyleState.Pressed), Is.EqualTo(2));
            Assert.That(FrameNodes.GetRadius(style), Is.EqualTo(2));
        }

        [Test]
        public void Merge_TopWins_NullCases()
        {
            var lower = StyleNodes.Colors(null, frame: O(red), text: O(red));
            var upper = StyleNodes.Colors(null, frame: O(green));
            var m = StyleNodes.Merge(lower, upper);
            Assert.That(FrameNodes.GetColor(m), Is.EqualTo(green));
            Assert.That(TextNodes.GetColor(m), Is.EqualTo(red));

            Assert.That(StyleNodes.Merge(lower, null), Is.SameAs(lower));
            Assert.That(StyleNodes.Merge(null, upper), Is.SameAs(upper));
            Assert.That(StyleNodes.Merge(null, null), Is.SameAs(Style.Empty));
        }

        [Test]
        public void ResolveState_Precedence()
        {
            Assert.That(StyleNodes.ResolveState(false, false, false, false), Is.EqualTo(StyleState.Normal));
            Assert.That(StyleNodes.ResolveState(false, false, false, true), Is.EqualTo(StyleState.Checked));
            Assert.That(StyleNodes.ResolveState(true, false, false, true), Is.EqualTo(StyleState.Hovered));
            Assert.That(StyleNodes.ResolveState(true, true, false, true), Is.EqualTo(StyleState.Pressed));
            Assert.That(StyleNodes.ResolveState(true, true, true, true), Is.EqualTo(StyleState.Disabled));
            Assert.That(StyleNodes.ResolveState(false, true, false, false), Is.EqualTo(StyleState.Pressed));
        }
    }
}
