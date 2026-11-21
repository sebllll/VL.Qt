using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    /// <summary>Shared core of Line, Polyline and Polygon (package 03): content equality, extent, closed.</summary>
    public class ShapeNodeCoreTests
    {
        private ShapeNodeCore _core = null!;
        private int _warnings; // currently shown warnings
        private string? _lastWarning;

        private sealed class Removal(ShapeNodeCoreTests t) : IDisposable { public void Dispose() => t._warnings--; }

        [SetUp]
        public void SetUp()
        {
            _warnings = 0;
            _lastWarning = null;
            _core = new ShapeNodeCore(w => { _warnings++; _lastWarning = w; return new Removal(this); });
        }

        [TearDown]
        public void TearDown() => _core.Dispose();

        private static readonly CommonInputs Common = new(default, default, RectangleAnchor.Center, 1, false, false, null, null);

        private static ShapeNodeCore.Props PropsOf(Spread<Vector2> points, bool closed = false, Spread<float>? dash = null, float strokeWidth = 0.05f)
            => new(points, closed, Color4.White, Color4.White, strokeWidth, ShapeCap.Flat, ShapeJoin.Miter, 4, dash ?? Spread<float>.Empty,
                0, 0, 1, 0, ShapeFillRule.NonZero);

        private Shape Describe(ShapeNodeCore.Props props) => _core.Describe(Common, props, out _, out _);

        private static Spread<Vector2> Points(params (float x, float y)[] p) => p.Select(v => new Vector2(v.x, v.y)).ToSpread();

        private static Spread<Vector2> Many(int count) => Enumerable.Range(0, count).Select(i => new Vector2(i, i % 7)).ToSpread();

        [Test]
        public void SameContentInNewSpread_SameElementInstance()
        {
            var a = Describe(PropsOf(Points((0, 0), (1, 2), (3, 1))));
            var b = Describe(PropsOf(Points((0, 0), (1, 2), (3, 1))));
            Assert.That(b, Is.SameAs(a));
            Assert.That(_core.Core.Created, Is.EqualTo(1));
        }

        [Test]
        public void SameSpreadInstance_SameElementInstance()
        {
            var points = Points((0, 0), (1, 2));
            var a = Describe(PropsOf(points));
            Assert.That(Describe(PropsOf(points)), Is.SameAs(a));
        }

        [Test]
        public void ChangedPoint_NewElement()
        {
            var a = Describe(PropsOf(Points((0, 0), (1, 2))));
            var b = Describe(PropsOf(Points((0, 0), (1, 3))));
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(b.Points[1], Is.EqualTo(new Vector2(1, 3)));
        }

        [Test]
        public void ChangedPointCount_NewElement()
        {
            var a = Describe(PropsOf(Points((0, 0), (1, 2))));
            Assert.That(Describe(PropsOf(Points((0, 0), (1, 2), (2, 2)))), Is.Not.SameAs(a));
        }

        [Test]
        public void DashContent_IsCompared()
        {
            var points = Points((0, 0), (1, 2));
            var a = Describe(PropsOf(points, dash: new[] { 0.1f, 0.2f }.ToSpread()));
            Assert.That(Describe(PropsOf(points, dash: new[] { 0.1f, 0.2f }.ToSpread())), Is.SameAs(a));
            Assert.That(Describe(PropsOf(points, dash: new[] { 0.1f, 0.3f }.ToSpread())), Is.Not.SameAs(a));
        }

        [Test]
        public void OtherField_NewElement()
        {
            var points = Points((0, 0), (1, 2));
            var a = Describe(PropsOf(points));
            Assert.That(Describe(PropsOf(points, strokeWidth: 0.1f)), Is.Not.SameAs(a));
            Assert.That(Describe(PropsOf(points, strokeWidth: 0.1f, closed: true)).Closed, Is.True);
        }

        [Test]
        public void Props_EqualsAndHashCode_ByContent()
        {
            var a = PropsOf(Points((0, 0), (1, 2)));
            var b = PropsOf(Points((0, 0), (1, 2)));
            Assert.That(a.Equals(b), Is.True);
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
            Assert.That(a.Equals(PropsOf(Points((0, 0), (1, 5)))), Is.False);
        }

        [Test]
        public void Size_IsExtentOfPoints_AnchorTopLeft()
        {
            var shape = Describe(PropsOf(Points((-1, 0.5f), (2, 1), (1, 3))));
            Assert.That(shape.Size, Is.EqualTo(new Vector2(2, 3)));
            Assert.That(shape.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));
            Assert.That(shape.Closed, Is.False);
            Assert.That(shape.Points.Count, Is.EqualTo(3));
        }

        [Test]
        public void NoPoints_SizeZero_NoWarning()
        {
            var shape = Describe(PropsOf(Spread<Vector2>.Empty));
            Assert.That(shape.Size, Is.EqualTo(Vector2.Zero));
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void Closed_IsPassedOn()
            => Assert.That(Describe(PropsOf(Points((0, 0), (1, 0), (1, 1)), closed: true)).Closed, Is.True);

        [Test]
        public void LinePoints_SameInstanceUntilChanged()
        {
            using var line = new LineNode(_ => new Removal(this));
            Shape Line(Vector2 to, float strokeWidth = 0.05f)
                => (Shape)line.Update(new Vector2(0.5f, 0.5f), to, Matrix.Identity, Color4.White, strokeWidth, ShapeCap.Flat, Spread<float>.Empty,
                    0, 0, 1, 0, 1, false, false, null, null, true, out _, out _);

            var a = Line(new Vector2(2.5f, 1.5f));
            Assert.That(a.Points.Count, Is.EqualTo(2));
            // A new element (other stroke width) with the same end points reuses the points spread.
            var a2 = Line(new Vector2(2.5f, 1.5f), 0.1f);
            Assert.That(a2, Is.Not.SameAs(a));
            Assert.That(a2.Points, Is.SameAs(a.Points));

            var b = Line(new Vector2(3, 1.5f));
            Assert.That(b.Points, Is.Not.SameAs(a.Points));
            Assert.That(b.Points[1], Is.EqualTo(new Vector2(3, 1.5f)));
            Assert.That(b.Size, Is.EqualTo(new Vector2(3, 1.5f)));
        }

        [Test]
        public void ManyPoints_NoWarning_ElementKeepsAllPoints_SizeCoversAllPoints()
        {
            var shape = Describe(PropsOf(Many(100_000)));
            Assert.That(_warnings, Is.EqualTo(0));
            Assert.That(shape.Points.Count, Is.EqualTo(100_000));
            Assert.That(shape.Size, Is.EqualTo(new Vector2(99_999, 6)), "extent of all points: x = 99999, y = max(i % 7) = 6");
        }
    }
}
