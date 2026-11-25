using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Qt.Nodes;
using VL.Qt.Styles;

namespace VL.Qt.Tests
{
    /// <summary>Style nodeset: setters, getters, Merge, ResolveState and the cached output of the six process nodes.</summary>
    public class StyleNodesTests
    {
        private static readonly Color4 red = new(1, 0, 0, 1);
        private static readonly Color4 green = new(0, 1, 0, 1);
        private static readonly Color4 blue = new(0, 0, 1, 1);

        private static Optional<T> O<T>(T v) => new(v);

        private static Style SetColors(Style? input, Optional<Color4> frame = default, Optional<Color4> border = default,
            Optional<Color4> text = default, Optional<Color4> indicator = default, Optional<Color4> track = default,
            Optional<Color4> trackFill = default, StyleStates state = StyleStates.All)
            => new SetColorsNode().Update(input, frame, border, text, indicator, track, trackFill, state);

        private static Style SetFrame(Style? input, Optional<float> radius = default, Optional<float> borderWidth = default,
            Optional<float> paddingX = default, Optional<float> paddingY = default, Optional<float> minWidth = default,
            Optional<float> minHeight = default, StyleStates state = StyleStates.All)
            => new SetFrameNode().Update(input, radius, borderWidth, paddingX, paddingY, minWidth, minHeight, state);

        private static Style SetText(Style? input, Optional<string> fontFamily = default, Optional<float> fontSize = default,
            Optional<int> fontWeight = default, StyleStates state = StyleStates.All)
            => new SetTextNode().Update(input, fontFamily, fontSize, fontWeight, state);

        private static Style SetIndicator(Style? input, Optional<float> size = default, Optional<float> radius = default,
            Optional<float> borderWidth = default, StyleStates state = StyleStates.All)
            => new SetIndicatorNode().Update(input, size, radius, borderWidth, state);

        private static Style SetTrack(Style? input, Optional<float> thickness = default, Optional<float> radius = default,
            StyleStates state = StyleStates.All)
            => new SetTrackNode().Update(input, thickness, radius, state);

        private static Style Merge(Style? input, Style? input2) => new MergeNode().Update(input, input2);

        [Test]
        public void ProcessNodes_EqualInputs_ReturnTheSameReference()
        {
            var under = SetFrame(null, radius: O(4f));
            var over = SetFrame(null, radius: O(8f));

            var colors = new SetColorsNode();
            var a = colors.Update(under, O(red), default, default, default, default, O(blue), StyleStates.All);
            Assert.That(colors.Update(under, O(red), default, default, default, default, O(blue), StyleStates.All), Is.SameAs(a));
            Assert.That(colors.Update(under, O(green), default, default, default, default, O(blue), StyleStates.All), Is.Not.SameAs(a));
            Assert.That(colors.Update(under, O(red), default, default, default, default, O(blue), StyleStates.Hovered), Is.Not.SameAs(a));
            Assert.That(colors.Update(over, O(red), default, default, default, default, O(blue), StyleStates.All), Is.Not.SameAs(a));

            var frame = new SetFrameNode();
            var f = frame.Update(under, O(1f), default, default, default, default, default, StyleStates.All);
            Assert.That(frame.Update(under, O(1f), default, default, default, default, default, StyleStates.All), Is.SameAs(f));
            Assert.That(frame.Update(under, O(2f), default, default, default, default, default, StyleStates.All), Is.Not.SameAs(f));
            Assert.That(frame.Update(null, O(1f), default, default, default, default, default, StyleStates.All), Is.Not.SameAs(f));

            var text = new SetTextNode();
            var t = text.Update(under, O("Arial"), default, default, StyleStates.All);
            Assert.That(text.Update(under, O("Arial"), default, default, StyleStates.All), Is.SameAs(t));
            Assert.That(text.Update(under, O("Arial"), O(20f), default, StyleStates.All), Is.Not.SameAs(t));

            var indicator = new SetIndicatorNode();
            var i = indicator.Update(under, O(10f), default, default, StyleStates.All);
            Assert.That(indicator.Update(under, O(10f), default, default, StyleStates.All), Is.SameAs(i));
            Assert.That(indicator.Update(under, O(11f), default, default, StyleStates.All), Is.Not.SameAs(i));

            var track = new SetTrackNode();
            var k = track.Update(under, O(6f), default, StyleStates.All);
            Assert.That(track.Update(under, O(6f), default, StyleStates.All), Is.SameAs(k));
            Assert.That(track.Update(under, O(7f), default, StyleStates.All), Is.Not.SameAs(k));

            var merge = new MergeNode();
            var m = merge.Update(under, over);
            Assert.That(merge.Update(under, over), Is.SameAs(m));
            Assert.That(merge.Update(over, under), Is.Not.SameAs(m));
            Assert.That(FrameNodes.Radius(m), Is.EqualTo(8));
        }

        [Test]
        public void ProcessNodes_NothingSet_ReturnInputUnchanged()
        {
            var a = SetFrame(null, radius: O(4f));
            Assert.That(new SetFrameNode().Update(a, default, default, default, default, default, default, StyleStates.All), Is.SameAs(a));
            Assert.That(new SetFrameNode().Update(null, default, default, default, default, default, default, StyleStates.All), Is.SameAs(Style.Empty));
        }

        [Test]
        public void NullInput_Getters_ReturnBaseStyle()
        {
            var b = BaseStyle.Default;
            Assert.That(FrameNodes.Color(null), Is.EqualTo(b.Frame!.Color.Normal.Value));
            Assert.That(FrameNodes.Radius(null, StyleState.Pressed), Is.EqualTo(2));
            Assert.That(TextNodes.FontSize(null), Is.EqualTo(13));
            Assert.That(TextNodes.FontFamily(null), Is.EqualTo(""));
            Assert.That(IndicatorNodes.Size(null), Is.EqualTo(14));
            Assert.That(TrackNodes.Thickness(null), Is.EqualTo(4));
            StyleNodes.Colors(null, StyleState.Hovered, out var frame, out _, out _, out _, out _, out var fill);
            Assert.That(frame, Is.EqualTo(b.Frame.Color.Hovered.Value));
            Assert.That(fill, Is.EqualTo(b.Track!.FillColor.Hovered.Value));
            FrameNodes.Frame(null, StyleState.Normal, out var r, out var bw, out var px, out var py, out var mw, out var mh);
            Assert.That((r, bw, px, py, mw, mh), Is.EqualTo((2f, 1f, 8f, 4f, 24f, 24f)));
            TextNodes.Text(null, StyleState.Normal, out var fam, out var size, out var weight);
            Assert.That((fam, size, weight), Is.EqualTo(("", 13f, 400)));
            IndicatorNodes.Indicator(null, StyleState.Normal, out var isz, out var ir, out var ibw);
            Assert.That((isz, ir, ibw), Is.EqualTo((14f, 2f, 1f)));
            TrackNodes.Track(null, StyleState.Normal, out var th, out var tr);
            Assert.That((th, tr), Is.EqualTo((4f, 2f)));
        }

        [Test]
        public void NullPart_Getters_DoNotThrow()
        {
            var style = FrameNodes.SetRadius(null, O(9f));
            Assert.That(TextNodes.FontWeight(style), Is.EqualTo(400));
            Assert.That(TrackNodes.FillColor(style), Is.EqualTo(BaseStyle.Default.Track!.FillColor.Normal.Value));
            Assert.That(FrameNodes.Radius(style), Is.EqualTo(9));
        }

        [Test]
        public void NullInput_Setters_DoNotThrow()
        {
            Assert.That(SetColors(null, frame: O(red)).Frame!.Color.Normal.Value, Is.EqualTo(red));
            Assert.That(SetFrame(null, radius: O(3f)).Frame!.Radius.Normal.Value, Is.EqualTo(3));
            Assert.That(SetText(null, fontFamily: O("Arial")).Text!.FontFamily.Normal.Value, Is.EqualTo("Arial"));
            Assert.That(SetIndicator(null, size: O(20f)).Indicator!.Size.Normal.Value, Is.EqualTo(20));
            Assert.That(SetTrack(null, thickness: O(8f)).Track!.Thickness.Normal.Value, Is.EqualTo(8));
            Assert.That(FrameNodes.SetColor(null, O(red)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetBorderColor(null, O(red)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetPaddingX(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetPaddingY(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetMinWidth(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetMinHeight(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(FrameNodes.SetBorderWidth(null, O(1f)).Frame, Is.Not.Null);
            Assert.That(TextNodes.SetColor(null, O(red)).Text, Is.Not.Null);
            Assert.That(TextNodes.SetFontSize(null, O(1f)).Text, Is.Not.Null);
            Assert.That(TextNodes.SetFontWeight(null, O(700)).Text, Is.Not.Null);
            Assert.That(TextNodes.SetFontFamily(null, O("x")).Text, Is.Not.Null);
            Assert.That(IndicatorNodes.SetColor(null, O(red)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.SetRadius(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.SetBorderWidth(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(IndicatorNodes.SetSize(null, O(1f)).Indicator, Is.Not.Null);
            Assert.That(TrackNodes.SetColor(null, O(red)).Track, Is.Not.Null);
            Assert.That(TrackNodes.SetFillColor(null, O(red)).Track, Is.Not.Null);
            Assert.That(TrackNodes.SetThickness(null, O(1f)).Track, Is.Not.Null);
            Assert.That(TrackNodes.SetRadius(null, O(1f)).Track, Is.Not.Null);
        }

        [Test]
        public void Setter_States_SetExactlyThoseStates()
        {
            var all = FrameNodes.SetRadius(null, O(5f), StyleStates.All).Frame!.Radius;
            Assert.That(all.Normal.HasValue && all.Hovered.HasValue && all.Pressed.HasValue && all.Disabled.HasValue && all.Checked.HasValue, Is.True);

            var hp = FrameNodes.SetRadius(null, O(5f), StyleStates.Hovered | StyleStates.Pressed).Frame!.Radius;
            Assert.That(hp.Hovered.HasValue, Is.True);
            Assert.That(hp.Pressed.HasValue, Is.True);
            Assert.That(hp.Normal.HasValue, Is.False);
            Assert.That(hp.Disabled.HasValue, Is.False);
            Assert.That(hp.Checked.HasValue, Is.False);
        }

        [Test]
        public void Setter_UnconnectedPins_StayUnset()
        {
            var style = SetFrame(null, radius: O(4f));
            Assert.That(style.Frame!.Radius.Normal.Value, Is.EqualTo(4));
            Assert.That(style.Frame.BorderWidth.IsEmpty, Is.True);
            Assert.That(style.Frame.Color.IsEmpty, Is.True);
            Assert.That(style.Text, Is.Null);
            Assert.That(style.Track, Is.Null);

            var colors = SetColors(null, text: O(green), state: StyleStates.Disabled);
            Assert.That(colors.Text!.Color.Disabled.Value, Is.EqualTo(green));
            Assert.That(colors.Text.Color.Normal.HasValue, Is.False);
            Assert.That(colors.Frame, Is.Null);
            Assert.That(colors.Indicator, Is.Null);
            Assert.That(colors.Track, Is.Null);
        }

        [Test]
        public void Setter_NothingSet_ReturnsInputUnchanged()
        {
            var a = FrameNodes.SetRadius(null, O(4f));
            Assert.That(SetColors(a), Is.SameAs(a));
            Assert.That(SetFrame(a), Is.SameAs(a));
            Assert.That(SetText(a), Is.SameAs(a));
            Assert.That(SetIndicator(a), Is.SameAs(a));
            Assert.That(SetTrack(a), Is.SameAs(a));
            Assert.That(FrameNodes.SetRadius(a), Is.SameAs(a));
            Assert.That(FrameNodes.SetRadius(a, O(9f), StyleStates.None), Is.SameAs(a));
            Assert.That(SetColors(null), Is.SameAs(Style.Empty));
            Assert.That(SetFrame(null), Is.SameAs(Style.Empty));
        }

        [Test]
        public void Chain_ColorsFrameGetter()
        {
            var style = SetColors(null, frame: O(red), trackFill: O(blue));
            style = SetFrame(style, radius: O(12f));
            style = FrameNodes.SetColor(style, O(green), StyleStates.Hovered);

            Assert.That(FrameNodes.Color(style), Is.EqualTo(red));
            Assert.That(FrameNodes.Color(style, StyleState.Hovered), Is.EqualTo(green));
            Assert.That(FrameNodes.Color(style, StyleState.Pressed), Is.EqualTo(red));
            Assert.That(FrameNodes.Radius(style), Is.EqualTo(12));
            Assert.That(TrackNodes.FillColor(style, StyleState.Hovered), Is.EqualTo(blue));
            // Not set in the style: from the base style.
            Assert.That(FrameNodes.BorderWidth(style), Is.EqualTo(1));
            StyleNodes.Colors(style, StyleState.Normal, out var frame, out var border, out _, out _, out _, out var fill);
            Assert.That((frame, fill), Is.EqualTo((red, blue)));
            Assert.That(border, Is.EqualTo(BaseStyle.Default.Frame!.BorderColor.Normal.Value));
        }

        [Test]
        public void Getter_StateSetOnlyForHovered_FallsBackToBaseNormal()
        {
            var style = FrameNodes.SetRadius(null, O(30f), StyleStates.Hovered);
            Assert.That(FrameNodes.Radius(style, StyleState.Hovered), Is.EqualTo(30));
            Assert.That(FrameNodes.Radius(style, StyleState.Pressed), Is.EqualTo(2));
            Assert.That(FrameNodes.Radius(style), Is.EqualTo(2));
        }

        [Test]
        public void Merge_TopWins_NullCases()
        {
            var lower = SetColors(null, frame: O(red), text: O(red));
            var upper = SetColors(null, frame: O(green));
            var m = Merge(lower, upper);
            Assert.That(FrameNodes.Color(m), Is.EqualTo(green));
            Assert.That(TextNodes.Color(m), Is.EqualTo(red));

            Assert.That(Merge(lower, null), Is.SameAs(lower));
            Assert.That(Merge(null, upper), Is.SameAs(upper));
            Assert.That(Merge(null, null), Is.SameAs(Style.Empty));
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
