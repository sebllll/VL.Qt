using System;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Nodes;
using VL.Qt.Paths;

namespace VL.Qt.Tests
{
    /// <summary>The node Path (#154, package 04): pin defaults, change detection, path limit.</summary>
    public class PathNodeTests
    {
        private string? _warning;
        private int _warnings; // currently shown warnings

        private sealed class Removal(PathNodeTests t) : IDisposable { public void Dispose() => t._warnings--; }

        private IDisposable AddWarning(string text)
        {
            _warnings++;
            _warning = text;
            return new Removal(this);
        }

        // Pin defaults as declared on Update (what vvvv passes for unconnected pins).
        private static Element Path(PathNode n, QtPath? path, out Vector2 size)
        {
            var e = n.Update(path, default, new Color4(0, 0, 0, 0), Color4.White, 0.05f, ShapeCap.Flat, ShapeJoin.Miter, 4, Spread<float>.Empty,
                0, 0, 1, 0, ShapeFillRule.NonZero, 1, false, false, null, null, out size, out _);
            return e;
        }

        private static Element Path(PathNode n, QtPath? path) => Path(n, path, out _);

        private static QtPath Triangle(float x = 3) => QtPath.Empty.MoveTo(1, 1).LineTo(x, 1).LineTo(2, 4).Close();

        private static QtPath Long(int segments)
        {
            var p = QtPath.Empty.MoveTo(0, 0);
            for (int i = 0; i < segments; i++)
                p = p.LineTo(i % 100, i % 7);
            return p;
        }

        [Test]
        public void Defaults()
        {
            using var node = new PathNode(AddWarning);
            var path = Triangle();
            var s = (Shape)Path(node, path, out _);
            Assert.That(s.Path, Is.SameAs(path));
            Assert.That(s.Fill.A, Is.EqualTo(0f));
            Assert.That(s.StrokeColor, Is.EqualTo(Color4.White));
            Assert.That(s.StrokeWidth, Is.EqualTo(0.05f));
            Assert.That(s.Cap, Is.EqualTo(ShapeCap.Flat));
            Assert.That(s.Join, Is.EqualTo(ShapeJoin.Miter));
            Assert.That(s.MiterLimit, Is.EqualTo(4f));
            Assert.That(s.TrimStart, Is.EqualTo(0f));
            Assert.That(s.TrimEnd, Is.EqualTo(1f));
            Assert.That(s.TrimOffset, Is.EqualTo(0f));
            Assert.That(s.FillRule, Is.EqualTo(ShapeFillRule.NonZero));
            Assert.That(s.Anchor, Is.EqualTo(RectangleAnchor.TopLeft));
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void Size_IsExtentOfPath()
        {
            using var node = new PathNode(AddWarning);
            var s = (Shape)Path(node, Triangle(), out _);
            Assert.That(s.Size, Is.EqualTo(Shape.ExtentOf(Triangle())));
            Assert.That(s.Size, Is.EqualTo(new Vector2(3, 4)));
        }

        [Test]
        public void SamePath_SameElement()
        {
            using var node = new PathNode(AddWarning);
            var path = Triangle();
            var a = Path(node, path);
            Assert.That(Path(node, path), Is.SameAs(a));
        }

        [Test]
        public void NewPathSameContent_SameElement()
        {
            using var node = new PathNode(AddWarning);
            var a = Path(node, Triangle());
            Assert.That(Path(node, Triangle()), Is.SameAs(a));
        }

        [Test]
        public void ChangedPath_NewElement()
        {
            using var node = new PathNode(AddWarning);
            var a = Path(node, Triangle(3));
            var b = Path(node, Triangle(5));
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(((Shape)b).Size.X, Is.EqualTo(5f));
        }

        [Test]
        public void Null_IsEmptyAndDrawsNothing()
        {
            using var node = new PathNode(AddWarning);
            var a = (Shape)Path(node, null, out _);
            Assert.That(a.Path!.IsEmpty, Is.True);
            Assert.That(a.Size, Is.EqualTo(Vector2.Zero));
            Assert.That(Path(node, QtPath.Empty), Is.SameAs(a), "null and Empty are the same input");
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void PathAtTheLimit_IsDrawnWithoutWarning()
        {
            using var node = new PathNode(AddWarning);
            var path = Long(7000);
            var flat = path.Flatten();
            Assert.That(Protocol.PathFits(flat.Verbs.Length, flat.Points.Length / 2), Is.True);
            var s = (Shape)Path(node, path);
            Assert.That(s.Path, Is.SameAs(path));
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void PathOverTheLimit_WarnsAndDrawsNothing()
        {
            using var node = new PathNode(AddWarning);
            var path = Long(9000);
            var flat = path.Flatten();
            Assert.That(Protocol.PathFits(flat.Verbs.Length, flat.Points.Length / 2), Is.False);
            var s = (Shape)Path(node, path, out _);
            Assert.That(s.Path!.IsEmpty, Is.True, "nothing is drawn");
            Assert.That(_warnings, Is.EqualTo(1));
            Assert.That(_warning, Is.EqualTo("Path: 9001 verbs and 9001 points do not fit one command; nothing is drawn."));
            Assert.That(node.Core.PathLimitWarning, Is.EqualTo(_warning));

            // Unchanged: no second warning, same element.
            Assert.That(Path(node, path), Is.SameAs(s));
            Assert.That(_warnings, Is.EqualTo(1));

            // A path that fits removes the warning and is drawn.
            var small = Triangle();
            var t = (Shape)Path(node, small);
            Assert.That(t.Path, Is.SameAs(small));
            Assert.That(_warnings, Is.EqualTo(0));
            Assert.That(node.Core.PathLimitWarning, Is.Null);
        }

        [Test]
        public void PathOverTheLimit_ThenNull_RemovesWarning()
        {
            using var node = new PathNode(AddWarning);
            Path(node, Long(9000));
            Assert.That(_warnings, Is.EqualTo(1));
            Path(node, null);
            Assert.That(_warnings, Is.EqualTo(0));
        }

        [Test]
        public void Dispose_RemovesWarning()
        {
            var node = new PathNode(AddWarning);
            Path(node, Long(9000));
            node.Dispose();
            Assert.That(_warnings, Is.EqualTo(0));
        }
    }
}
