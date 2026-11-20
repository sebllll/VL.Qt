using System;
using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Nodes.Paths;
using VL.Qt.Paths;

namespace VL.Qt.Tests.Paths
{
    public class BuilderNodeTests
    {
        private static readonly QtPath start = QtPath.Empty.MoveTo(1, 2).LineTo(10, 20);

        [Test]
        public void LineTo_AddsLine_RelativeAddsLastPoint()
        {
            var abs = new LineToNode().Update(start, new Vector2(3, 4));
            Assert.That(abs.Flatten().Verbs, Is.EqualTo(new[] { Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Line }));
            Assert.That(abs.Flatten().Points, Is.EqualTo(new[] { 1f, 2f, 10f, 20f, 3f, 4f }));

            var rel = new LineToRelativeNode().Update(start, new Vector2(3, 4));
            Assert.That(rel.Flatten().Points, Is.EqualTo(new[] { 1f, 2f, 10f, 20f, 13f, 24f }));
        }

        [Test]
        public void QuadTo_AddsQuad_RelativeAddsLastPoint()
        {
            var abs = new QuadToNode().Update(start, new Vector2(1, 2), new Vector2(3, 4));
            Assert.That(abs.Flatten().Verbs[^1], Is.EqualTo(Protocol.Verb.Quad));
            Assert.That(abs.Flatten().Points.ToArray()[4..], Is.EqualTo(new[] { 1f, 2f, 3f, 4f }));

            var rel = new QuadToRelativeNode().Update(start, new Vector2(1, 2), new Vector2(3, 4));
            Assert.That(rel.Flatten().Points.ToArray()[4..], Is.EqualTo(new[] { 11f, 22f, 13f, 24f }));
        }

        [Test]
        public void CubicTo_AddsCubic_RelativeAddsLastPoint()
        {
            var abs = new CubicToNode().Update(start, new Vector2(1, 2), new Vector2(3, 4), new Vector2(5, 6));
            Assert.That(abs.Flatten().Verbs[^1], Is.EqualTo(Protocol.Verb.Cubic));
            Assert.That(abs.Flatten().Points.ToArray()[4..], Is.EqualTo(new[] { 1f, 2f, 3f, 4f, 5f, 6f }));

            var rel = new CubicToRelativeNode().Update(start, new Vector2(1, 2), new Vector2(3, 4), new Vector2(5, 6));
            Assert.That(rel.Flatten().Points.ToArray()[4..], Is.EqualTo(new[] { 11f, 22f, 13f, 24f, 15f, 26f }));
        }

        [Test]
        public void Close_AddsClose_NullInputIsEmptyPath()
        {
            var closed = new CloseNode().Update(start);
            Assert.That(closed.Flatten().Verbs[^1], Is.EqualTo(Protocol.Verb.Close));
            Assert.That(closed.LastPoint, Is.EqualTo(new Vector2(1, 2)));
            Assert.That(new LineToNode().Update(null, new Vector2(1, 1)).ContentEquals(QtPath.Empty.LineTo(1, 1)), Is.True);
        }

        [Test]
        public void RelativeAfterClose_UsesContourStart()
        {
            var closed = new CloseNode().Update(start);
            var path = new LineToRelativeNode().Update(closed, new Vector2(1, 1));
            Assert.That(path.Flatten().Points.ToArray()[^2..], Is.EqualTo(new[] { 2f, 3f }));
        }

        [Test]
        public void ArcToOval_MatchesArcGeometry()
        {
            var oval = new RectangleF(0, 0, 4, 2);
            var path = new ArcToOvalNode().Update(start, oval, 0.25f, 0.5f, true);
            Assert.That(path.ContentEquals(ArcGeometry.ArcTo(start, oval, 0.25f, 0.5f, true)), Is.True);
            Assert.That(path.Flatten().Verbs[2], Is.EqualTo(Protocol.Verb.Move));
        }

        [Test]
        public void ArcToTangent_StartsAtLastPoint()
        {
            var path = new ArcToTangentNode().Update(start, new Vector2(20, 20), new Vector2(20, 40), 5);
            Assert.That(path.ContentEquals(ArcGeometry.TangentArcTo(start, new Vector2(10, 20), new Vector2(20, 20), new Vector2(20, 40), 5)), Is.True);
        }

        [Test]
        public void ArcToSvg_AbsoluteEndsAtDestination_RelativeAddsLastPoint()
        {
            var abs = new ArcToSvgNode().Update(start, new Vector2(5, 5), 0, false, true, new Vector2(20, 20));
            Assert.That(abs.LastPoint.X, Is.EqualTo(20).Within(1e-4));
            Assert.That(abs.LastPoint.Y, Is.EqualTo(20).Within(1e-4));

            var rel = new ArcToRelativeNode().Update(start, new Vector2(5, 5), 0, false, true, new Vector2(10, 0));
            Assert.That(rel.LastPoint.X, Is.EqualTo(20).Within(1e-4));
            Assert.That(rel.LastPoint.Y, Is.EqualTo(20).Within(1e-4));
            var expected = ArcGeometry.SvgArcTo(start, new Vector2(10, 20), new Vector2(5, 5), 0, false, true, new Vector2(20, 20));
            Assert.That(rel.ContentEquals(expected), Is.True);
        }

        [Test]
        public void SameInputs_SameInstance_ChangedParameter_NewInstance()
        {
            var oval = new RectangleF(0, 0, 4, 2);
            Check(new LineToNode(), n => n.Update(start, new Vector2(1, 1)), n => n.Update(start, new Vector2(1, 2)));
            Check(new LineToRelativeNode(), n => n.Update(start, new Vector2(1, 1)), n => n.Update(start, new Vector2(1, 2)));
            Check(new QuadToNode(), n => n.Update(start, Vector2.One, Vector2.One), n => n.Update(start, Vector2.One, Vector2.Zero));
            Check(new QuadToRelativeNode(), n => n.Update(start, Vector2.One, Vector2.One), n => n.Update(start, Vector2.Zero, Vector2.One));
            Check(new CubicToNode(), n => n.Update(start, Vector2.One, Vector2.One, Vector2.One), n => n.Update(start, Vector2.One, Vector2.One, Vector2.Zero));
            Check(new CubicToRelativeNode(), n => n.Update(start, Vector2.One, Vector2.One, Vector2.One), n => n.Update(start, Vector2.Zero, Vector2.One, Vector2.One));
            Check(new CloseNode(), n => n.Update(start), n => n.Update(start.LineTo(0, 0)));
            Check(new ArcToTangentNode(), n => n.Update(start, Vector2.One, Vector2.One, 1), n => n.Update(start, Vector2.One, Vector2.One, 2));
            Check(new ArcToOvalNode(), n => n.Update(start, oval, 0, 0.5f, false), n => n.Update(start, oval, 0, 0.5f, true));
            Check(new ArcToSvgNode(), n => n.Update(start, Vector2.One, 0, false, true, Vector2.One), n => n.Update(start, Vector2.One, 0, true, true, Vector2.One));
            Check(new ArcToRelativeNode(), n => n.Update(start, Vector2.One, 0, false, true, Vector2.One), n => n.Update(start, Vector2.One, 0, false, false, Vector2.One));
        }

        private static void Check<T>(T node, Func<T, QtPath> same, Func<T, QtPath> changed)
        {
            var a = same(node);
            Assert.That(same(node), Is.SameAs(a), typeof(T).Name);
            Assert.That(changed(node), Is.Not.SameAs(a), typeof(T).Name);
        }

        [Test]
        public void Chain_OfTenThousandSteps_FlattensWithoutStackOverflow()
        {
            var lineTo = new LineToRelativeNode();
            var quadTo = new QuadToRelativeNode();
            var path = new MoveToNode().Update(null, Vector2.Zero);
            // Accumulator: the output of one Update is the input of the next, as in a patch with a state feedback.
            for (var i = 0; i < 5000; i++)
            {
                path = lineTo.Update(path, new Vector2(1, 0));
                path = quadTo.Update(path, new Vector2(0, 1), new Vector2(1, 1));
            }
            var flat = path.Flatten();
            Assert.That(flat.Verbs.Length, Is.EqualTo(10001));
            Assert.That(path.LastPoint, Is.EqualTo(new Vector2(10000, 5000)));
        }
    }
}
