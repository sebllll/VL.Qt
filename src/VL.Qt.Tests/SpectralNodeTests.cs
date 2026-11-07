using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using Rectangle = VL.Qt.Elements.Rectangle;

namespace VL.Qt.Tests
{
    /// <summary>Group/Row/Column (Spectral) (#117): one Spread input, same behavior as the pin-group variants.</summary>
    public class SpectralNodeTests
    {
        static IDisposable NoWarning(string _) => throw new AssertionException("unexpected warning");

        static readonly Rectangle A = new() { Size = new Vector2(1, 0.5f) };
        static readonly Rectangle B = new() { Size = new Vector2(2, 1) };

        static Spread<IElement?> S(params IElement?[] e) => Spread.Create(e);

        [Test]
        public void Group_SpreadChildren_NullsSkipped_SameInstanceWhenUnchanged()
        {
            using var n = new GroupSpectralNode(NoWarning);
            Element Run(Spread<IElement?> c) => n.Update(c, default, true, default, RectangleAnchor.Center, 1, false, false,
                null, null, null, out _, out _);
            var first = (Group)Run(S(A, null, B));
            Assert.That(first.Children, Is.EqualTo(new Element[] { A, B }));
            Assert.That(Run(S(A, null, B)), Is.SameAs(first), "new spread, same children");
            Assert.That(((Group)Run(S(B, A))).Children, Is.EqualTo(new Element[] { B, A }), "order change");
            Assert.That(((Group)Run(Spread<IElement?>.Empty)).Children.IsEmpty, Is.True);
        }

        [Test]
        public void Row_And_Column_SpreadChildren()
        {
            using var r = new RowSpectralNode(NoWarning);
            var row = (Row)r.Update(S(A, B), default, true, default, RectangleAnchor.Center, 0.1f, 0, VerticalAlignment.Top, 1, false, false,
                null, null, out _, out _);
            Assert.That(row.Children, Is.EqualTo(new Element[] { A, B }));
            Assert.That(row.Spacing, Is.EqualTo(0.1f));

            using var c = new ColumnSpectralNode(NoWarning);
            var col = (Column)c.Update(S(B, A), default, true, default, RectangleAnchor.Center, 0, 0.2f, HorizontalAlignment.Left, 1, false, false,
                null, null, out _, out _);
            Assert.That(col.Children, Is.EqualTo(new Element[] { B, A }));
            Assert.That(col.Padding, Is.EqualTo(0.2f));
        }
    }
}
