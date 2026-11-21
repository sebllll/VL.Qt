using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Labeled node: Row/Column of label Text and element, change detection.</summary>
    public class LabeledNodeTests
    {
        private static IDisposable NoWarning(string _) => throw new AssertionException("unexpected warning");

        private static readonly Rectangle A = new() { Size = new Vector2(1, 0.5f) };

        private static Element Labeled(LabeledNode n, Element? element, string text = "Volume", float labelWidth = 1.5f,
            LabelPosition position = LabelPosition.Left)
            => (Element)n.Update(element, text, labelWidth, position, 0.1f, default, RectangleAnchor.Center, 0.2f, 1, null,
                null, out _, out _);

        [Test]
        public void Left_IsRow_LabelThenElement()
        {
            using var n = new LabeledNode(NoWarning);
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
            using var n = new LabeledNode(NoWarning);
            var col = (Column)Labeled(n, A, position: LabelPosition.Top);
            Assert.That(col.Children[1], Is.SameAs(A));
        }

        [Test]
        public void UnchangedInputs_SameInstance_ChangedText_NewLabel()
        {
            using var n = new LabeledNode(NoWarning);
            var first = Labeled(n, A);
            Assert.That(Labeled(n, A), Is.SameAs(first));
            var second = (Row)Labeled(n, A, text: "Other");
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(((Text)second.Children[0]).Content, Is.EqualTo("Other"));
        }

        [Test]
        public void NoElement_OnlyLabel()
        {
            using var n = new LabeledNode(NoWarning);
            Assert.That(Labeled(n, null).Children, Has.Length.EqualTo(1));
        }
    }
}
