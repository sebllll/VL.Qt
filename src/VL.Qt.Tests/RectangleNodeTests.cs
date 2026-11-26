using System;
using System.Collections.Immutable;
using System.Reactive;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Lib.Reactive;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Rectangle node and the shared element node core (07b): change detection, outputs, warning.</summary>
    public class RectangleNodeTests
    {
        private static readonly Vector2 DefaultSize = new(1, 1);

        private RectangleNode _node = null!;
        private RecordingMessages _m = null!;

        [SetUp]
        public void SetUp()
        {
            _m = new RecordingMessages();
            _node = new RectangleNode(_m.Errors);
        }

        [TearDown]
        public void TearDown() => _node.Dispose();

        // Pin defaults as declared on Update (what vvvv passes for unconnected pins).
        private Element Update(Vector2? size = null, Matrix transform = default, RectangleAnchor anchor = RectangleAnchor.Center,
            Color4? fill = null, float radius = 0, Color4? strokeColor = null, float strokeWidth = 0,
            float opacity = 1, bool clip = false, bool debug = false,
            Interaction? interaction = null, bool enabled = true)
            => (Element)_node.Update(size ?? DefaultSize, transform, anchor, fill ?? Color4.White, radius, strokeColor ?? Color4.Black,
                strokeWidth, opacity, clip, debug, interaction, null, enabled, out _, out _);

        [Test]
        public void Circle_SizeIsTwiceRadius_NoCenterPin_RadiusIsRadius()
        {
            using var circle = new CircleNode(_m.Errors);
            Rectangle Circle(float radius, RectangleAnchor anchor = RectangleAnchor.Center)
                => (Rectangle)circle.Update(radius, default, anchor, Color4.White, Color4.Black, 0, 1, false, false, null, null, true, out _, out _);

            var c = Circle(0.5f);
            Assert.That(c.Size, Is.EqualTo(new Vector2(1, 1)));
            Assert.That(c.Radius, Is.EqualTo(0.5f));
            Assert.That(c.Anchor, Is.EqualTo(RectangleAnchor.Center));
            Assert.That(c.Transform, Is.EqualTo(Matrix.Identity), "position comes from Transform only");

            var d = Circle(0.25f, RectangleAnchor.TopLeft);
            Assert.That(d.Size, Is.EqualTo(new Vector2(0.5f, 0.5f)));
            Assert.That(d.Radius, Is.EqualTo(0.25f));
            Assert.That(d.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));

            var neg = Circle(-1f);
            Assert.That(neg.Size, Is.EqualTo(Vector2.Zero), "negative radius is clamped to 0");
        }

        [Test]
        public void Defaults_WhiteUnitRectangle_IdentityTransform_WithFeedback()
        {
            var r = (Rectangle)Update();
            Assert.That(r.Size, Is.EqualTo(DefaultSize));
            Assert.That(r.Fill, Is.EqualTo(Color4.White));
            Assert.That(r.StrokeColor, Is.EqualTo(Color4.Black));
            Assert.That(r.StrokeWidth, Is.EqualTo(0f));
            Assert.That(r.Radius, Is.EqualTo(0f));
            Assert.That(r.Transform, Is.EqualTo(Matrix.Identity), "default(Matrix) → Identity");
            Assert.That(r.Anchor, Is.EqualTo(RectangleAnchor.Center));
            Assert.That(r.Opacity, Is.EqualTo(1f));
            Assert.That(r.Clip, Is.False);
            Assert.That(r.Interaction, Is.Null);
            Assert.That(r.Debug, Is.False);
            Assert.That(r.Visible, Is.True, "Enabled defaults to true");
            Assert.That(r.Feedback.Feedback, Is.SameAs(_node.Core.Feedback));
            Assert.That(r.Feedback.Index, Is.EqualTo(0));
        }

        [Test]
        public void UnchangedInputs_SameInstance_NoAllocation()
        {
            var t = Matrix.RotationZ(0.5f);
            var a = Update(transform: t, fill: new Color4(1, 0, 0, 1));
            var b = Update(transform: t, fill: new Color4(1, 0, 0, 1));
            Assert.That(b, Is.SameAs(a));

            for (int i = 0; i < 100; i++) Update(transform: t, fill: new Color4(1, 0, 0, 1)); // warm-up
            long allocated = Allocations.Fewest(() => Update(transform: t, fill: new Color4(1, 0, 0, 1)));
            Assert.That(allocated, Is.EqualTo(0), "Update with unchanged inputs must not allocate");
        }

        [Test]
        public void EachChangedInput_NewDescription()
        {
            var last = Update();
            void Changed(Element e, Func<Rectangle, bool> check, string what)
            {
                Assert.That(e, Is.Not.SameAs(last), what);
                Assert.That(check((Rectangle)e), Is.True, what);
                last = e;
            }

            Changed(Update(size: new Vector2(3, 4)), r => r.Size == new Vector2(3, 4), "size");
            Changed(Update(transform: Matrix.Scaling(2)), r => r.Transform == Matrix.Scaling(2), "transform");
            Changed(Update(anchor: RectangleAnchor.TopLeft), r => r.Anchor == RectangleAnchor.TopLeft, "anchor");
            Changed(Update(fill: Color4.Black), r => r.Fill == Color4.Black, "fill");
            Changed(Update(radius: 0.1f), r => r.Radius == 0.1f, "radius");
            Changed(Update(strokeColor: Color4.White), r => r.StrokeColor == Color4.White, "strokeColor");
            Changed(Update(strokeWidth: 0.02f), r => r.StrokeWidth == 0.02f, "strokeWidth");
            Changed(Update(opacity: 0.5f), r => r.Opacity == 0.5f, "opacity");
            Changed(Update(clip: true), r => r.Clip, "clip");
            var chain = new HoverNode(new RecordingMessages().Errors).Update(null, null);
            Changed(Update(interaction: chain), r => ReferenceEquals(r.Interaction, chain), "interaction");
            Changed(Update(debug: true), r => r.Debug, "debug");
            Changed(Update(enabled: false), r => !r.Visible, "enabled");
            Assert.That(Update(enabled: false), Is.SameAs(last), "enabled unchanged: same instance");
            // Back to defaults → new description again; the feedback stays the same.
            Changed(Update(), r => r.Feedback.Feedback == _node.Core.Feedback, "back to defaults");
            Assert.That(Update(), Is.SameAs(last));
        }

        [Test]
        public void Outputs_FromFeedback()
        {
            Update();
            var s = _node.Core.Feedback[0];
            s.MeasuredSize = new Vector2(0.3f, 0.4f);
            s.SceneBounds = new RectangleF(1, 2, 3, 4);
            _node.Update(DefaultSize, default, RectangleAnchor.Center, Color4.White, 0, Color4.Black, 0, 1, false, false,
                null, null, true, out var measured, out var scene);
            Assert.That(measured, Is.EqualTo(new Vector2(0.3f, 0.4f)));
            Assert.That(scene, Is.EqualTo(new RectangleF(1, 2, 3, 4)));
        }

        [Test]
        public void SeveralRenderers_NoWarning_FeedbackWarningShownAtNode()
        {
            // Several times in one tree and in two renderers is fine.
            var scene = new OccurrenceRegistry();
            var interactions = new InteractionRegistry();
            var rec = new Reconciler(new RecordingSink(), new IdAllocator(), scene, interactions);
            var rec2 = new Reconciler(new RecordingSink(), new IdAllocator(), scene, interactions);
            var e = Update();
            rec.Update(new Group { Children = ImmutableArray.Create(e, e) }, SpaceMapping.Default);
            rec2.Update(new Group { Children = ImmutableArray.Create(e) }, SpaceMapping.Default);
            Assert.That(Update(), Is.SameAs(e));
            Assert.That(_node.Core.Warning, Is.Null);
            Assert.That(_m.Shown, Is.Null);

            // A warning in the feedback slot (e.g. a video duplicate) is shown at the node once and removed with it.
            _node.Core.Feedback[0].SetMessage(OccurrenceRegistry.VideoPlaceholderSource, MessageKind.Warning, OccurrenceRegistry.VideoPlaceholderWarning);
            Update();
            Assert.That(_m.Shown, Is.Not.Null);
            Update();
            Assert.That(_m.Shows, Is.EqualTo(1), "added once");

            _node.Core.Feedback[0].SetMessage(OccurrenceRegistry.VideoPlaceholderSource, MessageKind.Warning, null);
            Update();
            Assert.That(_m.Shown, Is.Null, "removed");

            _node.Core.Feedback[0].SetMessage(OccurrenceRegistry.VideoPlaceholderSource, MessageKind.Warning, OccurrenceRegistry.VideoPlaceholderWarning);
            Update();
            Assert.That(_m.Shown, Is.Not.Null);
            _node.Dispose();
            Assert.That(_m.Shown, Is.Null, "Dispose removes the warning");
        }

        [Test]
        public void CommonInputs_ZeroMatrixIsIdentity_OthersKept()
        {
            Assert.That(CommonInputs.NormalizeTransform(default), Is.EqualTo(Matrix.Identity));
            var s = Matrix.Scaling(0); // M44 = 1, deliberate collapse stays
            Assert.That(CommonInputs.NormalizeTransform(s), Is.EqualTo(s));
        }
    }
}
