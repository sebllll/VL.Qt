using NUnit.Framework;
using Stride.Core.Mathematics;
using VL.Qt.Nodes;
using VL.Qt.Paths;
using VL.Qt.Wire;

namespace VL.Qt.Tests.Paths
{
    public class MoveToNodeTests
    {
        [Test]
        public void MoveTo_AddsMoveSegment()
        {
            var path = new MoveToNode().Update(QtPath.Empty, new Vector2(3, 4));
            Assert.That(path.Flatten().Verbs, Is.EqualTo(new[] { Protocol.Verb.Move }));
            Assert.That(path.Flatten().Points, Is.EqualTo(new[] { 3f, 4f }));
        }

        [Test]
        public void MoveTo_NullInputIsEmptyPath()
        {
            var path = new MoveToNode().Update(null, new Vector2(1, 2));
            Assert.That(path.ContentEquals(QtPath.Empty.MoveTo(1, 2)), Is.True);
        }

        [Test]
        public void MoveTo_SameInputs_SameInstance()
        {
            var node = new MoveToNode();
            var input = QtPath.Empty.LineTo(1, 1);
            var a = node.Update(input, new Vector2(3, 4));
            Assert.That(node.Update(input, new Vector2(3, 4)), Is.SameAs(a));
            Assert.That(node.Update(input, new Vector2(3, 5)), Is.Not.SameAs(a));
            Assert.That(node.Update(QtPath.Empty.LineTo(1, 1), new Vector2(3, 5)), Is.Not.SameAs(a));
        }

        [Test]
        public void MoveToRelative_AddsLastPoint()
        {
            var input = QtPath.Empty.MoveTo(1, 2).LineTo(10, 20);
            var path = new MoveToRelativeNode().Update(input, new Vector2(3, 4));
            Assert.That(path.LastPoint, Is.EqualTo(new Vector2(13, 24)));
            Assert.That(path.ContentEquals(input.MoveTo(13, 24)), Is.True);
        }

        [Test]
        public void MoveToRelative_EmptyPath_IsRelativeToOrigin()
        {
            var path = new MoveToRelativeNode().Update(QtPath.Empty, new Vector2(3, 4));
            Assert.That(path.ContentEquals(QtPath.Empty.MoveTo(3, 4)), Is.True);
        }

        [Test]
        public void MoveToRelative_SameInputs_SameInstance()
        {
            var node = new MoveToRelativeNode();
            var input = QtPath.Empty.MoveTo(1, 1);
            var a = node.Update(input, new Vector2(2, 2));
            Assert.That(node.Update(input, new Vector2(2, 2)), Is.SameAs(a));
            Assert.That(node.Update(QtPath.Empty.MoveTo(1, 1), new Vector2(2, 2)), Is.Not.SameAs(a));
        }

        [Test]
        public void LastPoint_FollowsSegmentsCloseAndAppend()
        {
            Assert.That(QtPath.Empty.LastPoint, Is.EqualTo(Vector2.Zero));
            var p = QtPath.Empty.MoveTo(1, 2).LineTo(3, 4);
            Assert.That(p.LastPoint, Is.EqualTo(new Vector2(3, 4)));
            Assert.That(p.QuadTo(5, 5, 6, 7).LastPoint, Is.EqualTo(new Vector2(6, 7)));
            Assert.That(p.CubicTo(5, 5, 6, 6, 8, 9).LastPoint, Is.EqualTo(new Vector2(8, 9)));
            Assert.That(p.Close().LastPoint, Is.EqualTo(new Vector2(1, 2)));
            Assert.That(QtPath.Empty.MoveTo(9, 9).Append(p).LastPoint, Is.EqualTo(new Vector2(3, 4)));
        }
    }
}
