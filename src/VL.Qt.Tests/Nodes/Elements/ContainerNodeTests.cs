using System;
using System.Collections.Immutable;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Reconciliation;
using VL.Qt.Tests.Support;
using P = VL.Qt.Wire.Protocol.PropertyId;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests.Nodes.Elements
{
    /// <summary>Group, Row and Column nodes: children pin group, change detection, layout pins.</summary>
    public class ContainerNodeTests
    {
        private static readonly Rectangle A = new() { Size = new Vector2(1, 0.5f) };
        private static readonly Rectangle B = new() { Size = new Vector2(0.5f, 0.25f) };
        private static readonly Rectangle C = new() { Size = new Vector2(2, 1) };

        private static Spread<IElement?> S(params IElement?[] e) => Spread.Create(e);

        [Test]
        public void RowAndColumn_TakeAStyle_AndNewStyleIsNewDescription()
        {
            var style = new VL.Qt.Styles.Style();
            using var row = new RowNode(UnexpectedMessages.Store());
            using var column = new ColumnNode(UnexpectedMessages.Store());
            var children = S(A);
            var r = (Row)row.Update(children, true, default, default, RectangleAnchor.Center, 0, 0, VerticalAlignment.Top, 1, false, false,
                null, null, style, true, out _, out _);
            var c = (Column)column.Update(children, true, default, default, RectangleAnchor.Center, 0, 0, HorizontalAlignment.Left, 1, false, false,
                null, null, style, true, out _, out _);
            Assert.That((r.Style, c.Style), Is.EqualTo((style, style)));

            var r2 = (Row)row.Update(children, true, default, default, RectangleAnchor.Center, 0, 0, VerticalAlignment.Top, 1, false, false,
                null, null, style, true, out _, out _);
            Assert.That(r2, Is.SameAs(r), "same inputs: same instance");
            var r3 = (Row)row.Update(children, true, default, default, RectangleAnchor.Center, 0, 0, VerticalAlignment.Top, 1, false, false,
                null, null, null, true, out _, out _);
            Assert.That(r3.Style, Is.Null);
        }

        private static Element Group(GroupNode n, Spread<IElement?> children, float opacity = 1, bool autoSize = true)
            => (Element)n.Update(children, autoSize, default, default, RectangleAnchor.Center, opacity, false, false,
                null, null, null, true, out _, out _);

        private static Element Column(ColumnNode n, Spread<IElement?> children, float spacing = 0, float padding = 0,
            HorizontalAlignment align = HorizontalAlignment.Left, bool autoSize = true)
            => (Element)n.Update(children, autoSize, default, default, RectangleAnchor.Center, spacing, padding, align, 1, false, false,
                null, null, null, true, out _, out _);

        private static Element Row(RowNode n, Spread<IElement?> children, float spacing = 0, float padding = 0,
            VerticalAlignment align = VerticalAlignment.Top, bool autoSize = true)
            => (Element)n.Update(children, autoSize, default, default, RectangleAnchor.Center, spacing, padding, align, 1, false, false,
                null, null, null, true, out _, out _);

        [Test]
        public void Group_UnconnectedInputsSkipped_DefaultsAndFeedback()
        {
            using var n = new GroupNode(UnexpectedMessages.Store());
            var g = (Group)Group(n, S(null, A, null, B));
            Assert.That(g.Children, Is.EqualTo(new Element[] { A, B }));
            Assert.That(g.Size, Is.EqualTo(default(Vector2)), "no own extent");
            Assert.That(g.Transform, Is.EqualTo(Matrix.Identity));
            Assert.That(g.Feedback.Feedback, Is.SameAs(n.Core.Feedback));

            var empty = (Group)Group(n, S(null, null));
            Assert.That(empty.Children.IsEmpty, Is.True);
            Assert.That(((Group)Group(n, Spread<IElement?>.Empty)), Is.SameAs(empty), "no children either way");
        }

        [Test]
        public void SameChildren_NewSpreadEachFrame_SameInstance_NoAllocation()
        {
            using var n = new ColumnNode(UnexpectedMessages.Store());
            var first = Column(n, S(A, null, B, C), spacing: 0.1f);
            // VL may rebuild the pin group spread every frame: equal elements → same description.
            Assert.That(Column(n, S(A, B, null, C), spacing: 0.1f), Is.SameAs(first));

            var spreads = Enumerable.Range(0, 2).Select(_ => S(A, B, C)).ToArray();
            for (int i = 0; i < 100; i++) Column(n, spreads[i & 1], spacing: 0.1f); // warm-up
            int frame = 0;
            long allocated = Allocations.Fewest(() => Column(n, spreads[frame++ & 1], spacing: 0.1f));
            Assert.That(allocated, Is.EqualTo(0), "Update with unchanged children must not allocate");
            Assert.That(Column(n, spreads[0], spacing: 0.1f), Is.SameAs(first));
        }

        [Test]
        public void ChangedChild_Order_Count_NewDescription()
        {
            using var n = new GroupNode(UnexpectedMessages.Store());
            var last = Group(n, S(A, B));
            void Changed(Element e, Element[] expected, string what)
            {
                Assert.That(e, Is.Not.SameAs(last), what);
                Assert.That(e.ChildItems, Is.EqualTo(expected), what);
                last = e;
            }

            Changed(Group(n, S(B, A)), new Element[] { B, A }, "order");
            Changed(Group(n, S(B, A, C)), new Element[] { B, A, C }, "appended");
            Changed(Group(n, S(B, C)), new Element[] { B, C }, "removed");
            var b2 = new Rectangle { Size = B.Size };
            Changed(Group(n, S(b2, C)), new Element[] { b2, C }, "new instance (child changed)");
            Changed(Group(n, S(b2, C), opacity: 0.5f), new Element[] { b2, C }, "common pin");
            Assert.That(last.Opacity, Is.EqualTo(0.5f));
        }

        [Test]
        public void Column_LayoutPins_Defaults_EachChange()
        {
            using var n = new ColumnNode(UnexpectedMessages.Store());
            var c = (Column)Column(n, S(A));
            Assert.That(c.Spacing, Is.EqualTo(0f));
            Assert.That(c.Padding, Is.EqualTo(0f));
            Assert.That(c.ChildAlignment, Is.EqualTo(HorizontalAlignment.Left));

            c = (Column)Column(n, S(A), spacing: 0.2f);
            Assert.That(c.Spacing, Is.EqualTo(0.2f));
            var prev = c;
            c = (Column)Column(n, S(A), spacing: 0.2f, padding: 0.1f);
            Assert.That(c, Is.Not.SameAs(prev));
            Assert.That(c.Padding, Is.EqualTo(0.1f));
            prev = c;
            c = (Column)Column(n, S(A), spacing: 0.2f, padding: 0.1f, align: HorizontalAlignment.Center);
            Assert.That(c, Is.Not.SameAs(prev));
            Assert.That(c.ChildAlignment, Is.EqualTo(HorizontalAlignment.Center));
            Assert.That(c.Children, Is.EqualTo(new Element[] { A }));
        }

        [Test]
        public void Row_LayoutPins_Defaults_EachChange()
        {
            using var n = new RowNode(UnexpectedMessages.Store());
            var r = (Row)Row(n, S(A, B));
            Assert.That(r.ChildAlignment, Is.EqualTo(VerticalAlignment.Top));
            Assert.That(r.Children, Is.EqualTo(new Element[] { A, B }));
            var prev = r;
            r = (Row)Row(n, S(A, B), spacing: 0.1f, padding: 0.05f, align: VerticalAlignment.Bottom);
            Assert.That(r, Is.Not.SameAs(prev));
            Assert.That((r.Spacing, r.Padding, r.ChildAlignment), Is.EqualTo((0.1f, 0.05f, VerticalAlignment.Bottom)));
            Assert.That(Row(n, S(A, B), spacing: 0.1f, padding: 0.05f, align: VerticalAlignment.Bottom), Is.SameAs(r));
        }

        [Test]
        public void ColumnOfThreeRectangleNodes_ThroughReconciler()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            var rects = Enumerable.Range(0, 3).Select(_ => new RectangleNode(UnexpectedMessages.Store())).ToArray();
            using var column = new ColumnNode(UnexpectedMessages.Store());

            Element Frame(float spacing, float y0)
            {
                var children = rects.Select((r, i) => (Element?)r.Update(new Vector2(1, 0.5f), default,
                    RectangleAnchor.Center, Color4.White, 0, Color4.Black, 0, 1, false, false,
                    null, null, true, out _, out _)).ToArray();
                var e = Column(column, S(children), spacing: spacing, align: HorizontalAlignment.Center);
                sink.Clear();
                rec.Update(e, SpaceMapping.Default);
                return e;
            }

            Frame(0.1f, 0);
            Assert.That(sink.OfOp("CREATE").Count(), Is.EqualTo(4));
            Assert.That(sink.OfOp("REPARENT"), Is.EqualTo(new[] { "REPARENT 1 0 0", "REPARENT 2 1 0", "REPARENT 3 1 1", "REPARENT 4 1 2" }));
            Assert.That(sink.Commands, Does.Contain($"SET 1 Spacing f32(10)"), "units → DIP in Geometry");
            Assert.That(sink.Commands, Does.Contain($"SET 1 ChildAlignment i32({(int)HorizontalAlignment.Center})"));
            // Layout children: only the size goes out.
            Assert.That(sink.OfOp("SET").Where(s => s.Split(' ')[2] == nameof(P.Size)),
                Is.EqualTo(new[] { $"SET 2 Size vec2(100,50)", $"SET 3 Size vec2(100,50)", $"SET 4 Size vec2(100,50)" }));

            Frame(0.1f, 0);
            Assert.That(sink.Count, Is.EqualTo(0), "unchanged → nothing");
            Frame(0.1f, 2);
            Assert.That(sink.Count, Is.EqualTo(0), "position of a layout child is ignored");
            Frame(0.2f, 2);
            Assert.That(sink.Commands, Is.EqualTo(new[] { $"SET 1 Spacing f32(20)" }));

            foreach (var r in rects) r.Dispose();
        }

        [Test]
        public void AutoSize_DefaultOn_AllContainers_ChangeDetection()
        {
            using var g = new GroupNode(UnexpectedMessages.Store());
            using var c = new ColumnNode(UnexpectedMessages.Store());
            using var r = new RowNode(UnexpectedMessages.Store());
            Assert.That(((Group)Group(g, S(A))).AutoSize, Is.True);
            Assert.That(((Column)Column(c, S(A))).AutoSize, Is.True);
            Assert.That(((Row)Row(r, S(A))).AutoSize, Is.True);

            var g1 = Group(g, S(A));
            Assert.That(Group(g, S(A)), Is.SameAs(g1), "unchanged → same instance");
            var g2 = (Group)Group(g, S(A), autoSize: false);
            Assert.That(g2, Is.Not.SameAs(g1));
            Assert.That(g2.AutoSize, Is.False);
            Assert.That(Group(g, S(A), autoSize: false), Is.SameAs(g2));
            Assert.That(((Column)Column(c, S(A), autoSize: false)).AutoSize, Is.False);
            Assert.That(((Row)Row(r, S(A), autoSize: false)).AutoSize, Is.False);
        }

        [Test]
        public void AutoSize_ThroughReconciler_OnlyFlagIsSent_ZeroBytesWhenUnchanged()
        {
            var sink = new RecordingSink();
            var rec = new Reconciler(sink, new IdAllocator(), new OccurrenceRegistry(), new InteractionRegistry());
            using var row = new RowNode(UnexpectedMessages.Store());
            using var group = new GroupNode(UnexpectedMessages.Store());

            // Default (on): nothing is sent for it, also not for Size 0, 0.
            rec.Update(Row(row, S(A)), SpaceMapping.Default);
            Assert.That(sink.Commands.Any(x => x.Contains($" {nameof(P.AutoSize)} ")), Is.False, "default on → no SET");
            sink.Clear();
            rec.Update(Row(row, S(A)), SpaceMapping.Default);
            Assert.That(sink.Count, Is.EqualTo(0), "unchanged → 0 commands");

            rec.Update(Row(row, S(A), autoSize: false), SpaceMapping.Default);
            Assert.That(sink.Commands, Is.EqualTo(new[] { $"SET 1 AutoSize bool(0)" }), "flip off: only the flag, Size is not resent");
            sink.Clear();
            rec.Update(Row(row, S(A), autoSize: false), SpaceMapping.Default);
            Assert.That(sink.Count, Is.EqualTo(0));
            rec.Update(Row(row, S(A)), SpaceMapping.Default);
            Assert.That(sink.Commands, Is.EqualTo(new[] { $"SET 1 AutoSize bool(1)" }));

            // Created with Auto Size off: flag goes out with the other initial values.
            sink.Clear();
            rec.Update(Group(group, S(A), autoSize: false), SpaceMapping.Default);
            Assert.That(sink.Commands, Does.Contain($"SET 1 AutoSize bool(0)"));
        }
    }
}
