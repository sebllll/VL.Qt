using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using VL.Qt.Paths;
using VL.Qt.Wire;

namespace VL.Qt.Tests
{
    public class QtPathTests
    {
        private static QtPath Triangle() => QtPath.Empty.MoveTo(1, 2).LineTo(3, 4).LineTo(5, 0).Close();

        [Test]
        public void Empty_HasNoSegments()
        {
            var flat = QtPath.Empty.Flatten();
            Assert.That(QtPath.Empty.IsEmpty, Is.True);
            Assert.That(flat.Verbs, Is.Empty);
            Assert.That(flat.Points, Is.Empty);
            Assert.That(QtPath.Empty.Bounds.Width, Is.EqualTo(0f));
            Assert.That(QtPath.Empty.Bounds.Height, Is.EqualTo(0f));
            Assert.That(QtPath.Empty.Flatten(), Is.SameAs(flat));
            Assert.That(QtPath.Empty.ContentEquals(QtPath.Empty), Is.True);
        }

        [Test]
        public void Flatten_KeepsSegmentOrderAndPointCounts()
        {
            var path = QtPath.Empty
                .MoveTo(1, 2)
                .LineTo(3, 4)
                .QuadTo(5, 6, 7, 8)
                .CubicTo(9, 10, 11, 12, 13, 14)
                .Close();
            var flat = path.Flatten();
            Assert.That(flat.Verbs, Is.EqualTo(new[]
            {
                Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Quad, Protocol.Verb.Cubic, Protocol.Verb.Close,
            }));
            Assert.That(flat.Points, Is.EqualTo(Enumerable.Range(1, 14).Select(i => (float)i)));
            Assert.That(path.IsEmpty, Is.False);
        }

        [Test]
        public void Builders_DoNotChangeTheirPredecessor()
        {
            var baseline = Triangle();
            var longer = baseline.LineTo(9, 9);
            Assert.That(baseline.Flatten().Verbs, Has.Length.EqualTo(4));
            Assert.That(longer.Flatten().Verbs, Has.Length.EqualTo(5));
        }

        [Test]
        public void Flatten_LongChainDoesNotOverflowTheStack()
        {
            const int n = 200_000;
            var path = QtPath.Empty.MoveTo(0, 0);
            for (int i = 1; i < n; i++)
                path = path.LineTo(i, -i);
            var flat = path.Flatten();
            Assert.That(flat.Verbs, Has.Length.EqualTo(n));
            Assert.That(flat.Points, Has.Length.EqualTo(2 * n));
            Assert.That(flat.Points[2 * (n - 1)], Is.EqualTo(n - 1));
            Assert.That(flat.Points[2 * (n - 1) + 1], Is.EqualTo(-(n - 1)));
            Assert.That(path.Bounds.Width, Is.EqualTo(n - 1));
        }

        [Test]
        public void Flatten_LongAppendChainsDoNotOverflowTheStack()
        {
            const int n = 20_000;
            var piece = QtPath.Empty.MoveTo(1, 1).LineTo(2, 2);

            // Left-leaning and right-leaning joins.
            var left = QtPath.Empty;
            var right = QtPath.Empty;
            for (int i = 0; i < n; i++)
            {
                left = left.Append(piece);
                right = piece.Append(right);
            }
            Assert.That(left.Flatten().Verbs, Has.Length.EqualTo(2 * n));
            Assert.That(right.Flatten().Verbs, Has.Length.EqualTo(2 * n));
            Assert.That(left.ContentEquals(right), Is.True);
        }

        [Test]
        public void Flatten_SecondCallReturnsTheSameInstance()
        {
            var path = Triangle();
            Assert.That(path.Flatten(), Is.SameAs(path.Flatten()));
        }

        [Test]
        public void Flatten_ConcurrentReadersGetTheSameInstance()
        {
            var path = QtPath.Empty.MoveTo(0, 0);
            for (int i = 1; i < 5000; i++)
                path = path.LineTo(i, i);
            var results = new FlatPath[8];
            Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i => results[i] = path.Flatten());
            Assert.That(results.Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void ContentEquals_ComparesVerbsAndCoordinates()
        {
            Assert.That(Triangle().ContentEquals(Triangle()), Is.True);
            Assert.That(Triangle().ContentEquals(null), Is.False);
            Assert.That(Triangle().ContentEquals(QtPath.Empty), Is.False);
            Assert.That(Triangle().ContentEquals(QtPath.Empty.MoveTo(1, 2).LineTo(3, 4).LineTo(5, 0)), Is.False, "Close missing");
            Assert.That(Triangle().ContentEquals(QtPath.Empty.MoveTo(1, 2).LineTo(3, 4).LineTo(5, 1).Close()), Is.False, "one coordinate");
            // Same number of points, different verbs.
            Assert.That(
                QtPath.Empty.MoveTo(0, 0).LineTo(1, 1).LineTo(2, 2).ContentEquals(QtPath.Empty.MoveTo(0, 0).LineTo(1, 1).MoveTo(2, 2)),
                Is.False);
        }

        [Test]
        public void ContentEquals_NaNEqualsItself()
        {
            var a = QtPath.Empty.MoveTo(float.NaN, 0);
            var b = QtPath.Empty.MoveTo(float.NaN, 0);
            Assert.That(a.ContentEquals(b), Is.True);
        }

        [Test]
        public void Append_PutsTheOtherPathAfterThisOne()
        {
            var a = QtPath.Empty.MoveTo(0, 0).LineTo(1, 1);
            var b = QtPath.Empty.MoveTo(5, 5).QuadTo(6, 6, 7, 7).Close();
            var joined = a.Append(b).LineTo(9, 9);
            var flat = joined.Flatten();
            Assert.That(flat.Verbs, Is.EqualTo(new[]
            {
                Protocol.Verb.Move, Protocol.Verb.Line, Protocol.Verb.Move, Protocol.Verb.Quad, Protocol.Verb.Close, Protocol.Verb.Line,
            }));
            Assert.That(flat.Points, Is.EqualTo(new float[] { 0, 0, 1, 1, 5, 5, 6, 6, 7, 7, 9, 9 }));
            Assert.That(a.Flatten().Verbs, Has.Length.EqualTo(2), "operands stay unchanged");
            Assert.That(b.Flatten().Verbs, Has.Length.EqualTo(3));
        }

        [Test]
        public void Append_EmptyReturnsTheOtherInstance()
        {
            var a = Triangle();
            Assert.That(a.Append(QtPath.Empty), Is.SameAs(a));
            Assert.That(QtPath.Empty.Append(a), Is.SameAs(a));
        }

        [Test]
        public void Append_OfTheSamePathTwice()
        {
            var a = Triangle();
            var twice = a.Append(a);
            var flat = twice.Flatten();
            Assert.That(flat.Verbs, Has.Length.EqualTo(8));
            Assert.That(flat.Points, Has.Length.EqualTo(12));
            Assert.That(flat.Points.Take(6), Is.EqualTo(flat.Points.Skip(6)));
        }

        [Test]
        public void Bounds_IncludeControlPoints()
        {
            var path = QtPath.Empty.MoveTo(10, 20).CubicTo(-5, 40, 30, -10, 15, 25).Close();
            var b = path.Bounds;
            Assert.That(b.Left, Is.EqualTo(-5f));
            Assert.That(b.Top, Is.EqualTo(-10f));
            Assert.That(b.Right, Is.EqualTo(30f));
            Assert.That(b.Bottom, Is.EqualTo(40f));
        }

        [Test]
        public void Bounds_OfClosedOnlyPathAreZero()
        {
            var path = QtPath.Empty.Close();
            Assert.That(path.IsEmpty, Is.False);
            Assert.That(path.Flatten().Verbs, Is.EqualTo(new[] { Protocol.Verb.Close }));
            Assert.That(path.Bounds.Width, Is.EqualTo(0f));
        }
    }
}
