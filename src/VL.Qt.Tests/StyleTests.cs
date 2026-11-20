using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Styles;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>Styles: StateValues, merge rule, base style, writer diff.</summary>
    public class StyleTests
    {
        private static readonly Color4 red = new(1, 0, 0, 1);
        private static readonly Color4 green = new(0, 1, 0, 1);

        private static Style WithRadius(StateValues<float> r) => new() { Frame = new FrameStyle { Radius = r } };

        private static StateValues<float> V(StyleStates s, float v) => default(StateValues<float>).With(s, v);

        [Test]
        public void With_SetsOnlyGivenStates_AndGetFallsBackToNormal()
        {
            var v = V(StyleStates.Normal | StyleStates.Hovered, 3);
            Assert.That(v[StyleState.Hovered].Value, Is.EqualTo(3));
            Assert.That(v[StyleState.Pressed].HasValue, Is.False);
            Assert.That(v.Get(StyleState.Pressed, -1), Is.EqualTo(3));
            Assert.That(V(StyleStates.Hovered, 5).Get(StyleState.Pressed, -1), Is.EqualTo(-1));
            Assert.That(V(StyleStates.Hovered, 5).Get(StyleState.Hovered, -1), Is.EqualTo(5));
            Assert.That(default(StateValues<float>).IsEmpty, Is.True);
            Assert.That(V(StyleStates.All, 1).IsEmpty, Is.False);
        }

        [Test]
        public void StateValuesMerge_PerState_OverWins()
        {
            var under = V(StyleStates.Normal, 1).With(StyleStates.Hovered, 2);
            var over = V(StyleStates.Hovered, 9).With(StyleStates.Pressed, 7);
            var m = StateValues<float>.Merge(under, over);
            Assert.That(m.Normal.Value, Is.EqualTo(1));
            Assert.That(m.Hovered.Value, Is.EqualTo(9));
            Assert.That(m.Pressed.Value, Is.EqualTo(7));
            Assert.That(m.Disabled.HasValue, Is.False);
        }

        [Test]
        public void Merge_NullAndEmpty_ReturnSameInstance()
        {
            var a = WithRadius(V(StyleStates.All, 4));
            Assert.That(StyleMerge.Merge((Style?)null, (Style?)null), Is.SameAs(Style.Empty));
            Assert.That(StyleMerge.Merge(a, null), Is.SameAs(a));
            Assert.That(StyleMerge.Merge(null, a), Is.SameAs(a));
            Assert.That(StyleMerge.Merge(a, Style.Empty), Is.SameAs(a));
            Assert.That(StyleMerge.Merge(Style.Empty, a), Is.SameAs(a));
        }

        [Test]
        public void Merge_OverAddsNothing_ReturnsUnder()
        {
            var under = WithRadius(V(StyleStates.All, 4));
            var over = WithRadius(V(StyleStates.Normal, 4));
            Assert.That(StyleMerge.Merge(under, over), Is.SameAs(under));
        }

        [Test]
        public void Merge_SharesPartsSetByOnlyOneSide()
        {
            var under = WithRadius(V(StyleStates.Normal, 4));
            var over = new Style { Text = new TextStyle { Color = default(StateValues<Color4>).With(StyleStates.Normal, red) } };
            var m = StyleMerge.Merge(under, over);
            Assert.That(m, Is.Not.SameAs(under).And.Not.SameAs(over));
            Assert.That(m.Frame, Is.SameAs(under.Frame));
            Assert.That(m.Text, Is.SameAs(over.Text));
        }

        [Test]
        public void Merge_PerStateRule_AcrossParts()
        {
            var under = new Style { Frame = new FrameStyle { Radius = V(StyleStates.Normal, 1).With(StyleStates.Hovered, 2) } };
            var over = new Style { Frame = new FrameStyle { Radius = V(StyleStates.Hovered, 5), PaddingX = V(StyleStates.Normal, 3) } };
            var m = StyleMerge.Merge(under, over);
            Assert.That(m.Frame!.Radius.Normal.Value, Is.EqualTo(1));
            Assert.That(m.Frame.Radius.Hovered.Value, Is.EqualTo(5));
            Assert.That(m.Frame.PaddingX.Normal.Value, Is.EqualTo(3));
        }

        [Test]
        public void Equality_IsDeep()
        {
            Assert.That(BaseStyleBuild(), Is.EqualTo(BaseStyleBuild()));
            Assert.That(BaseStyleBuild(), Is.Not.SameAs(BaseStyleBuild()));
            Assert.That(WithRadius(V(StyleStates.Normal, 1)), Is.Not.EqualTo(WithRadius(V(StyleStates.Normal, 2))));
        }

        private static Style BaseStyleBuild() => new()
        {
            Frame = new FrameStyle { Color = default(StateValues<Color4>).With(StyleStates.Normal, red), Radius = V(StyleStates.All, 2) },
            Text = new TextStyle { FontFamily = default(StateValues<string>).With(StyleStates.Normal, "x") },
        };

        [Test]
        public void BaseStyle_Default_SendsSameAsBefore()
        {
            var sink = new RecordingSink();
            BaseStyle.Write(sink, 7);
            var line = sink.Commands.Single();
            Assert.That(line, Does.StartWith("STYLE 7 " + Protocol.StyleSet.Clear + " "));
            var entries = line.Split(' ').Skip(3).ToArray();
            // 19 colors + 12 sizes + family + weight
            Assert.That(entries, Has.Length.EqualTo(33));
            Assert.That(entries, Does.Contain($"{Protocol.StyleField.TextFontWeight}/0=i32(400)"));
            Assert.That(entries, Does.Contain($"{Protocol.StyleField.TextFontFamily}/0=string()"));
            Assert.That(entries, Does.Contain($"{Protocol.StyleField.FramePaddingX}/0=f32(8)"));
            Assert.That(entries, Does.Contain($"{Protocol.StyleField.IndicatorColor}/4=color(0.8784314,0.8784314,0.8784314,1)"));
        }

        [Test]
        public void Writer_First_SendsClearAndAllEntries_SameStyleNothing()
        {
            var sink = new RecordingSink();
            var s = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 4));
            Assert.That(StyleWriter.Write(sink, 1, s, null), Is.True);
            Assert.That(sink.Commands.Single(), Is.EqualTo($"STYLE 1 {Protocol.StyleSet.Clear} {Protocol.StyleField.FrameRadius}/0=f32(3) {Protocol.StyleField.FrameRadius}/1=f32(4)"));

            sink.Clear();
            Assert.That(StyleWriter.Write(sink, 1, s, s), Is.False);
            var equal = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 4));
            Assert.That(StyleWriter.Write(sink, 1, equal, s), Is.False);
            Assert.That(sink.Commands, Is.Empty);
        }

        [Test]
        public void Writer_Diff_SendsOnlyChangedEntries_WithoutClear()
        {
            var sink = new RecordingSink();
            var last = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 4));
            var now = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 6));
            Assert.That(StyleWriter.Write(sink, 1, now, last), Is.True);
            Assert.That(sink.Commands.Single(), Is.EqualTo($"STYLE 1 0 {Protocol.StyleField.FrameRadius}/1=f32(6)"));
        }

        [Test]
        public void Writer_Unset_SendsFullWithClear()
        {
            var sink = new RecordingSink();
            var last = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 4));
            var now = WithRadius(V(StyleStates.Normal, 3));
            Assert.That(StyleWriter.Write(sink, 1, now, last), Is.True);
            Assert.That(sink.Commands.Single(), Is.EqualTo($"STYLE 1 {Protocol.StyleSet.Clear} {Protocol.StyleField.FrameRadius}/0=f32(3)"));
        }

        [Test]
        public void Writer_ReducedForm_AllStatesSendsOneEntry()
        {
            var sink = new RecordingSink();
            var s = new Style { Frame = new FrameStyle { Color = default(StateValues<Color4>).With(StyleStates.All, green) } };
            StyleWriter.Write(sink, 1, s, null);
            Assert.That(sink.Commands.Single().Split(' ').Skip(3).Count(), Is.EqualTo(1));
        }

        [Test]
        public void Writer_HoveredBecomesEqualToNormal_CountsAsRemoved()
        {
            var sink = new RecordingSink();
            var last = WithRadius(V(StyleStates.Normal, 3).With(StyleStates.Hovered, 4));
            var now = WithRadius(V(StyleStates.All, 3));
            Assert.That(StyleWriter.Write(sink, 1, now, last), Is.True);
            Assert.That(sink.Commands.Single(), Is.EqualTo($"STYLE 1 {Protocol.StyleSet.Clear} {Protocol.StyleField.FrameRadius}/0=f32(3)"));
        }

        [Test]
        public void Writer_DiffersOnlyInRedundantStates_WritesNothing()
        {
            var sink = new RecordingSink();
            var last = WithRadius(V(StyleStates.Normal, 3));
            var now = WithRadius(V(StyleStates.All, 3));
            Assert.That(StyleWriter.Write(sink, 1, now, last), Is.False);
            Assert.That(sink.Commands, Is.Empty);
        }
    }
}
