using System.Linq;
using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Nodes.Paths;
using VL.Qt.Paths;

namespace VL.Qt.Tests.Paths
{
    public class AddNodeTests
    {
        const byte M = Protocol.Verb.Move, L = Protocol.Verb.Line, C = Protocol.Verb.Cubic, Z = Protocol.Verb.Close;

        static byte[] Verbs(QtPath path) => path.Flatten().Verbs.ToArray();

        static void AssertBounds(QtPath path, float x, float y, float w, float h, float tolerance = 1e-4f)
        {
            var b = path.Bounds;
            Assert.That(b.X, Is.EqualTo(x).Within(tolerance));
            Assert.That(b.Y, Is.EqualTo(y).Within(tolerance));
            Assert.That(b.Width, Is.EqualTo(w).Within(tolerance));
            Assert.That(b.Height, Is.EqualTo(h).Within(tolerance));
        }

        [Test]
        public void AddRect_MoveThreeLinesClose()
        {
            var path = new AddRectNode().Update(QtPath.Empty, new RectangleF(1, 2, 3, 4));
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L, L, Z }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 1f, 2f, 4f, 2f, 4f, 6f, 1f, 6f }));
            AssertBounds(path, 1, 2, 3, 4);
        }

        [Test]
        public void AddRect_CounterClockwise_ReversesCorners()
        {
            var path = new AddRectNode().Update(QtPath.Empty, new RectangleF(1, 2, 3, 4), PathDirection.CounterClockwise);
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 1f, 2f, 1f, 6f, 4f, 6f, 4f, 2f }));
        }

        [Test]
        public void AddRect_ExtendsInput_AsNewContour()
        {
            var input = QtPath.Empty.MoveTo(0, 0).LineTo(1, 1);
            var path = new AddRectNode().Update(input, new RectangleF(0, 0, 1, 1));
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, M, L, L, L, Z }));
        }

        [Test]
        public void AddOval_MoveFourCubicsClose_BoundsAreTheRectangle()
        {
            var path = new AddOvalNode().Update(QtPath.Empty, new RectangleF(-1, -2, 4, 2));
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, C, C, C, C, Z }));
            AssertBounds(path, -1, -2, 4, 2);
            // Starts at the right middle.
            Assert.That(path.Flatten().Points.Take(2), Is.EqualTo(new[] { 3f, -1f }));
        }

        [Test]
        public void AddOval_CounterClockwise_SameBounds()
        {
            var path = new AddOvalNode().Update(QtPath.Empty, new RectangleF(-1, -2, 4, 2), PathDirection.CounterClockwise);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, C, C, C, C, Z }));
            AssertBounds(path, -1, -2, 4, 2);
        }

        [Test]
        public void AddCircle_MoveFourCubicsClose_BoundsAroundCenter()
        {
            var path = new AddCircleNode().Update(QtPath.Empty, new Vector2(3, 4), PathDirection.Clockwise, 2);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, C, C, C, C, Z }));
            AssertBounds(path, 1, 2, 4, 4);
        }

        [Test]
        public void AddCircle_DefaultRadiusIsHalf()
        {
            var path = new AddCircleNode().Update(QtPath.Empty);
            AssertBounds(path, -0.5f, -0.5f, 1, 1);
        }

        [Test]
        public void AddRoundRect_FourCornersWithLines_BoundsAreTheRectangle()
        {
            var path = new AddRoundRectNode().Update(QtPath.Empty, new RectangleF(0, 0, 10, 6), new Vector2(2, 1));
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, C, L, C, L, C, L, C, Z }));
            AssertBounds(path, 0, 0, 10, 6);
        }

        [Test]
        public void AddRoundRect_ZeroRadius_IsPlainRectangle()
        {
            var path = new AddRoundRectNode().Update(QtPath.Empty, new RectangleF(0, 0, 10, 6), Vector2.Zero);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L, L, Z }));
            AssertBounds(path, 0, 0, 10, 6);
        }

        [Test]
        public void AddArc_OpenContourWithinBounds()
        {
            // A quarter from the right middle to the bottom middle: Move + one Cubic, no Close.
            var path = new AddArcNode().Update(QtPath.Empty, new RectangleF(0, 0, 2, 2), 0, 0.25f);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, C }));
            Assert.That(path.Flatten().Points.Take(2), Is.EqualTo(new[] { 2f, 1f }));
            Assert.That(path.LastPoint.X, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(path.LastPoint.Y, Is.EqualTo(2f).Within(1e-4f));
        }

        [Test]
        public void AddArc_ZeroSweep_AddsNothing()
        {
            var input = QtPath.Empty.MoveTo(1, 1);
            Assert.That(new AddArcNode().Update(input, new RectangleF(0, 0, 2, 2), 0, 0), Is.SameAs(input));
        }

        [Test]
        public void AddArc_FullCycle_IsTheOval()
        {
            var path = new AddArcNode().Update(QtPath.Empty, new RectangleF(0, 0, 2, 2), 0, 1);
            Assert.That(path.ContentEquals(new AddOvalNode().Update(QtPath.Empty, new RectangleF(0, 0, 2, 2))), Is.True);
        }

        [Test]
        public void AddPoly_Open_MoveAndLines()
        {
            var points = Spread.Create(new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 3));
            var path = new AddPolyNode().Update(QtPath.Empty, points, false);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L }));
            AssertBounds(path, 0, 0, 2, 3);
        }

        [Test]
        public void AddPoly_Closed_EndsWithClose()
        {
            var points = Spread.Create(new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 3));
            var path = new AddPolyNode().Update(QtPath.Empty, points, true);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L, Z }));
        }

        [Test]
        public void AddPoly_EmptyOrNullPoints_AddNothing()
        {
            var input = QtPath.Empty.MoveTo(1, 1);
            var node = new AddPolyNode();
            Assert.That(node.Update(input, null, true), Is.SameAs(input));
            Assert.That(new AddPolyNode().Update(input, Spread<Vector2>.Empty, true), Is.SameAs(input));
        }

        [Test]
        public void AddPoly_SinglePoint_IsAMove()
        {
            var path = new AddPolyNode().Update(QtPath.Empty, Spread.Create(new Vector2(1, 2)), false);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M }));
        }

        [Test]
        public void AddPath_Append_NewContours()
        {
            var input = QtPath.Empty.MoveTo(0, 0).LineTo(1, 0);
            var other = QtPath.Empty.MoveTo(5, 5).LineTo(6, 5).Close();
            var path = new AddPathNode().Update(input, other, PathAddMode.Append);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, M, L, Z }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 0f, 0f, 1f, 0f, 5f, 5f, 6f, 5f }));
        }

        [Test]
        public void AddPath_Extend_FirstMoveBecomesLine()
        {
            var input = QtPath.Empty.MoveTo(0, 0).LineTo(1, 0);
            var other = QtPath.Empty.MoveTo(5, 5).LineTo(6, 5).MoveTo(8, 8).LineTo(9, 8).Close();
            var path = new AddPathNode().Update(input, other, PathAddMode.Extend);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L, L, M, L, Z }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 0f, 0f, 1f, 0f, 5f, 5f, 6f, 5f, 8f, 8f, 9f, 8f }));
        }

        [Test]
        public void AddPath_Extend_OnEmptyInput_IsAppend()
        {
            var other = QtPath.Empty.MoveTo(5, 5).LineTo(6, 5);
            var path = new AddPathNode().Update(QtPath.Empty, other, PathAddMode.Extend);
            Assert.That(path.ContentEquals(other), Is.True);
        }

        [Test]
        public void AddPath_WithoutLeadingMove_Append_StartsAtLastPoint()
        {
            var input = QtPath.Empty.MoveTo(2, 3).LineTo(4, 5);
            var other = QtPath.Empty.LineTo(6, 7);
            var path = new AddPathNode().Update(input, other, PathAddMode.Append);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, M, L }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 2f, 3f, 4f, 5f, 4f, 5f, 6f, 7f }));
        }

        [Test]
        public void AddPath_WithoutLeadingMove_Extend_ContinuesAsIs()
        {
            var input = QtPath.Empty.MoveTo(2, 3).LineTo(4, 5);
            var other = QtPath.Empty.LineTo(6, 7);
            var path = new AddPathNode().Update(input, other, PathAddMode.Extend);
            Assert.That(Verbs(path), Is.EqualTo(new[] { M, L, L }));
        }

        [Test]
        public void AddPath_EmptyOrNullOther_ReturnsInput()
        {
            var input = QtPath.Empty.MoveTo(1, 1);
            Assert.That(new AddPathNode().Update(input, null), Is.SameAs(input));
            Assert.That(new AddPathNode().Update(input, QtPath.Empty), Is.SameAs(input));
        }

        [Test]
        public void AddPath_ChainedLastPoint_FollowsOther()
        {
            var path = new AddPathNode().Update(QtPath.Empty.MoveTo(0, 0), QtPath.Empty.MoveTo(5, 5).LineTo(6, 7));
            Assert.That(path.LastPoint, Is.EqualTo(new Vector2(6, 7)));
        }

        // --- change detection: same inputs, same instance ---

        [Test]
        public void AddRect_ChangeDetection()
        {
            var node = new AddRectNode();
            var input = QtPath.Empty.MoveTo(1, 1);
            var a = node.Update(input, new RectangleF(0, 0, 1, 1), PathDirection.Clockwise);
            Assert.That(node.Update(input, new RectangleF(0, 0, 1, 1), PathDirection.Clockwise), Is.SameAs(a));
            Assert.That(node.Update(input, new RectangleF(0, 0, 2, 1), PathDirection.Clockwise), Is.Not.SameAs(a));
        }

        [Test]
        public void AddRoundRect_ChangeDetection()
        {
            var node = new AddRoundRectNode();
            var rect = new RectangleF(0, 0, 4, 4);
            var a = node.Update(QtPath.Empty, rect, new Vector2(1, 1), PathDirection.Clockwise);
            Assert.That(node.Update(QtPath.Empty, rect, new Vector2(1, 1), PathDirection.Clockwise), Is.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, rect, new Vector2(1, 2), PathDirection.Clockwise), Is.Not.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, rect, new Vector2(1, 2), PathDirection.CounterClockwise), Is.Not.SameAs(a));
        }

        [Test]
        public void AddOval_ChangeDetection()
        {
            var node = new AddOvalNode();
            var a = node.Update(QtPath.Empty, new RectangleF(0, 0, 4, 4), PathDirection.Clockwise);
            Assert.That(node.Update(QtPath.Empty, new RectangleF(0, 0, 4, 4), PathDirection.Clockwise), Is.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, new RectangleF(0, 0, 4, 4), PathDirection.CounterClockwise), Is.Not.SameAs(a));
        }

        [Test]
        public void AddCircle_ChangeDetection()
        {
            var node = new AddCircleNode();
            var a = node.Update(QtPath.Empty, new Vector2(1, 1), PathDirection.Clockwise, 2);
            Assert.That(node.Update(QtPath.Empty, new Vector2(1, 1), PathDirection.Clockwise, 2), Is.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, new Vector2(1, 1), PathDirection.Clockwise, 3), Is.Not.SameAs(a));
        }

        [Test]
        public void AddArc_ChangeDetection()
        {
            var node = new AddArcNode();
            var b = new RectangleF(0, 0, 2, 2);
            var a = node.Update(QtPath.Empty, b, 0, 0.25f);
            Assert.That(node.Update(QtPath.Empty, b, 0, 0.25f), Is.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, b, 0.1f, 0.25f), Is.Not.SameAs(a));
        }

        [Test]
        public void AddPoly_SameContentNewSpread_SameInstance()
        {
            var node = new AddPolyNode();
            var a = node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0), new Vector2(1, 1)), true);
            var again = node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0), new Vector2(1, 1)), true);
            Assert.That(again, Is.SameAs(a));
        }

        [Test]
        public void AddPoly_ChangedContentOrClose_NewInstance()
        {
            var node = new AddPolyNode();
            var a = node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0), new Vector2(1, 1)), true);
            var b = node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0), new Vector2(1, 2)), true);
            Assert.That(b, Is.Not.SameAs(a));
            Assert.That(node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0), new Vector2(1, 2)), false), Is.Not.SameAs(b));
            Assert.That(node.Update(QtPath.Empty, Spread.Create(new Vector2(0, 0)), false), Is.Not.SameAs(b));
        }

        [Test]
        public void AddPath_ChangeDetection()
        {
            var node = new AddPathNode();
            var input = QtPath.Empty.MoveTo(1, 1);
            var other = QtPath.Empty.MoveTo(2, 2).LineTo(3, 3);
            var a = node.Update(input, other, PathAddMode.Append);
            Assert.That(node.Update(input, other, PathAddMode.Append), Is.SameAs(a));
            Assert.That(node.Update(input, other, PathAddMode.Extend), Is.Not.SameAs(a));
            Assert.That(node.Update(input, QtPath.Empty.MoveTo(2, 2).LineTo(3, 3), PathAddMode.Extend), Is.Not.SameAs(a));
        }
    }
}
