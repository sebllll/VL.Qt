using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;

namespace VL.Qt.Tests
{
    /// <summary>The nodes Line, Polyline and Polygon (#154, package 03): pin defaults, change detection, point limit.</summary>
    public class ShapeNodesTests
    {
        private static readonly Color4 White = Color4.White;

        private string? _warning;
        private int _warnings; // currently shown warnings

        private sealed class Removal(ShapeNodesTests t) : IDisposable { public void Dispose() => t._warnings--; }

        private IDisposable AddWarning(string text)
        {
            _warnings++;
            _warning = text;
            return new Removal(this);
        }

        // Pin defaults as declared on Update (what vvvv passes for unconnected pins).
        private static Element Line(LineNode n, Vector2? from = null, Vector2? to = null, float strokeWidth = 0.05f)
            => n.Update(from ?? new Vector2(0.5f, 0.5f), to ?? new Vector2(2.5f, 1.5f), default, White, strokeWidth, ShapeCap.Flat,
                Spread<float>.Empty, 0, 0, 1, 0, 1, false, false, null, null, out _, out _);

        private static Element Polyline(PolylineNode n, Spread<Vector2> points, bool closed = false)
            => n.Update(points, closed, default, White, 0.05f, ShapeCap.Flat, ShapeJoin.Miter, 4, Spread<float>.Empty, 0, 0, 1, 0, 1,
                false, false, null, null, out _, out _);

        private static Element Polygon(PolygonNode n, Spread<Vector2> points)
            => n.Update(points, default, White, White, 0, ShapeJoin.Miter, 4, Spread<float>.Empty, 0, ShapeFillRule.NonZero, 1,
                false, false, null, null, out _, out _);

        private static Spread<Vector2> Points(params (float x, float y)[] p) => p.Select(v => new Vector2(v.x, v.y)).ToSpread();

        private static Spread<Vector2> Many(int count) => Enumerable.Range(0, count).Select(i => new Vector2(i, i % 7)).ToSpread();

        [Test]
        public void Line_Defaults()
        {
            using var node = new LineNode(AddWarning);
            var s = (Shape)Line(node);
            Assert.That(s.Points.ToArray(), Is.EqualTo(new[] { new Vector2(0.5f, 0.5f), new Vector2(2.5f, 1.5f) }));
            Assert.That(s.Closed, Is.False);
            Assert.That(s.StrokeColor, Is.EqualTo(White));
            Assert.That(s.StrokeWidth, Is.EqualTo(0.05f));
            Assert.That(s.Fill.A, Is.EqualTo(0f), "a line has no fill");
            Assert.That(s.Cap, Is.EqualTo(ShapeCap.Flat));
            Assert.That(s.Join, Is.EqualTo(ShapeJoin.Miter));
            Assert.That(s.MiterLimit, Is.EqualTo(4f));
            Assert.That(s.TrimStart, Is.EqualTo(0f));
            Assert.That(s.TrimEnd, Is.EqualTo(1f));
            Assert.That(s.TrimOffset, Is.EqualTo(0f));
            Assert.That(s.Dash.Count, Is.EqualTo(0));
            Assert.That(s.FillRule, Is.EqualTo(ShapeFillRule.NonZero));
            Assert.That(s.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));
            Assert.That(s.Transform, Is.EqualTo(Matrix.Identity));
            Assert.That(s.Size, Is.EqualTo(new Vector2(2.5f, 1.5f)));
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void Line_UnchangedInputs_SameElement_ChangedEnd_NewElement()
        {
            using var node = new LineNode(AddWarning);
            var a = Line(node);
            Assert.That(Line(node), Is.SameAs(a));
            Assert.That(Line(node, to: new Vector2(3, 3)), Is.Not.SameAs(a));
        }

        [Test]
        public void Polyline_SameContentInNewSpread_SameElement()
        {
            using var node = new PolylineNode(AddWarning);
            var a = Polyline(node, Points((0, 0), (1, 1), (2, 0)));
            var b = Polyline(node, Points((0, 0), (1, 1), (2, 0)));
            Assert.That(b, Is.SameAs(a));
            Assert.That(Polyline(node, Points((0, 0), (1, 1), (2, 1))), Is.Not.SameAs(a));
        }

        [Test]
        public void Polyline_Defaults_ClosedIsPassedOn()
        {
            using var node = new PolylineNode(AddWarning);
            var open = (Shape)Polyline(node, Points((0, 0), (1, 1)));
            Assert.That(open.Closed, Is.False);
            Assert.That(open.StrokeWidth, Is.EqualTo(0.05f));
            Assert.That(open.Fill.A, Is.EqualTo(0f));
            Assert.That(((Shape)Polyline(node, Points((0, 0), (1, 1)), closed: true)).Closed, Is.True);
        }

        [Test]
        public void Polygon_IsClosed_FilledWhite_NoStroke()
        {
            using var node = new PolygonNode(AddWarning);
            var s = (Shape)Polygon(node, Points((0, 0), (2, 0), (1, 2)));
            Assert.That(s.Closed, Is.True);
            Assert.That(s.Fill, Is.EqualTo(White));
            Assert.That(s.StrokeWidth, Is.EqualTo(0f));
            Assert.That(s.TrimStart, Is.EqualTo(0f));
            Assert.That(s.TrimEnd, Is.EqualTo(1f));
            Assert.That(s.Size, Is.EqualTo(new Vector2(2, 2)));
        }

        [Test]
        public void Polygon_SameContentInNewSpread_SameElement()
        {
            using var node = new PolygonNode(AddWarning);
            var a = Polygon(node, Points((0, 0), (2, 0), (1, 2)));
            Assert.That(Polygon(node, Points((0, 0), (2, 0), (1, 2))), Is.SameAs(a));
        }

        [Test]
        public void NullPoints_AreEmpty()
        {
            using var node = new PolylineNode(AddWarning);
            var s = (Shape)Polyline(node, null!);
            Assert.That(s.Points.Count, Is.EqualTo(0));
            Assert.That(s.Size, Is.EqualTo(Vector2.Zero));
        }

        [Test]
        public void TooManyPoints_WarnsAtTheNode_SizeCoversTheDrawnPoints_WarningGoesWhenFewer()
        {
            using var node = new PolylineNode(AddWarning);
            var s = (Shape)Polyline(node, Many(9000));
            Assert.That(_warnings, Is.EqualTo(1));
            Assert.That(_warning, Is.EqualTo("Points: only the first 8190 of 9000 points are drawn."));
            Assert.That(node.Core.Warning, Is.EqualTo(_warning));
            Assert.That(s.Size, Is.EqualTo(new Vector2(8189, 6)), "extent of the first 8190 points");

            Polyline(node, Many(100));
            Assert.That(_warnings, Is.EqualTo(0));
            Assert.That(node.Core.Warning, Is.Null);
        }

        [Test]
        public void TooManyPoints_Polygon_Warns()
        {
            using var node = new PolygonNode(AddWarning);
            Polygon(node, Many(9000));
            Assert.That(_warnings, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_RemovesWarning()
        {
            var node = new PolylineNode(AddWarning);
            Polyline(node, Many(9000));
            node.Dispose();
            Assert.That(_warnings, Is.EqualTo(0));
        }
    }
}
