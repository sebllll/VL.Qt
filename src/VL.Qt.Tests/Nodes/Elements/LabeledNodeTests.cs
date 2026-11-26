using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Tests.Support;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests.Nodes.Elements
{
    /// <summary>Labeled node: Row/Column of label Text and element, change detection.</summary>
    public class LabeledNodeTests
    {
        private static readonly Rectangle A = new() { Size = new Vector2(1, 0.5f) };

        private static Element Labeled(LabeledNode n, Element? element, string text = "Volume", float labelWidth = 1.5f,
            LabelPosition position = LabelPosition.Left)
            => (Element)n.Update(element, text, position, labelWidth, 0.2f, 0.1f, default, RectangleAnchor.Center, 1, false, false, null,
                null, true, out _, out _);

        [Test]
        public void ClipAndDebug_ReachTheLayout()
        {
            using var n = new LabeledNode(UnexpectedMessages.Store());
            foreach (var position in new[] { LabelPosition.Left, LabelPosition.Top })
            {
                var e = (Element)n.Update(A, "Volume", position, 1.5f, 0.2f, 0.1f, default, RectangleAnchor.Center, 1, true, true, null,
                    null, true, out _, out _);
                Assert.That((e.Clip, e.Debug), Is.EqualTo((true, true)), position.ToString());
                e = (Element)n.Update(A, "Volume", position, 1.5f, 0.2f, 0.1f, default, RectangleAnchor.Center, 1, false, false, null,
                    null, true, out _, out _);
                Assert.That((e.Clip, e.Debug), Is.EqualTo((false, false)), position.ToString());
            }
        }

        [Test]
        public void Left_IsRow_LabelThenElement()
        {
            using var n = new LabeledNode(UnexpectedMessages.Store());
            var row = (Row)Labeled(n, A, labelWidth: 1.2f);
            Assert.That(row.Children, Has.Length.EqualTo(2));
            var label = (Text)row.Children[0];
            Assert.That(label.Content, Is.EqualTo("Volume"));
            Assert.That(label.Size.X, Is.EqualTo(1.2f));
            Assert.That(row.Children[1], Is.SameAs(A));
            Assert.That(row.Spacing, Is.EqualTo(0.1f));
        }

        [Test]
        public void Top_IsColumn()
        {
            using var n = new LabeledNode(UnexpectedMessages.Store());
            var col = (Column)Labeled(n, A, position: LabelPosition.Top);
            Assert.That(col.Children[1], Is.SameAs(A));
        }

        [Test]
        public void UnchangedInputs_SameInstance_ChangedText_NewLabel()
        {
            using var n = new LabeledNode(UnexpectedMessages.Store());
            var first = Labeled(n, A);
            Assert.That(Labeled(n, A), Is.SameAs(first));
            var second = (Row)Labeled(n, A, text: "Other");
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(((Text)second.Children[0]).Content, Is.EqualTo("Other"));
        }

        [Test]
        public void Warning_OfTheRow_GoneAfterSwitchingToTop()
        {
            var m = new RecordingMessages();
            using var n = new LabeledNode(m.Errors);
            var row = Labeled(n, A);
            row.Feedback.Feedback![0].SetMessage(this, MessageKind.Warning, "row warning");
            Labeled(n, A);
            Assert.That(m.Shown, Is.EqualTo("row warning"));

            Labeled(n, A, position: LabelPosition.Top);
            Assert.That(m.Shown, Is.Null, "the inactive Row keeps no message at the node");

            Labeled(n, A);
            Assert.That(m.Shown, Is.EqualTo("row warning"), "back with the Row");
        }

        [Test]
        public void NoElement_OnlyLabel()
        {
            using var n = new LabeledNode(UnexpectedMessages.Store());
            Assert.That(Labeled(n, null).ChildItems, Has.Length.EqualTo(1));
        }
    }
}
