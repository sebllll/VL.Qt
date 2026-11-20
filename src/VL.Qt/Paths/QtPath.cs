using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using Stride.Core.Mathematics;

namespace VL.Qt.Paths
{
    /// <summary>
    /// Flat, ready-to-send form of a <see cref="QtPath"/>: verbs (<see cref="Protocol.Verb"/>) and x,y pairs in the
    /// units of the path. Immutable, shared between all readers of the path.
    /// </summary>
    internal sealed class FlatPath
    {
        internal static readonly FlatPath Empty = new FlatPath(ImmutableArray<byte>.Empty, ImmutableArray<float>.Empty, default);

        internal FlatPath(ImmutableArray<byte> verbs, ImmutableArray<float> points, RectangleF bounds)
        {
            Verbs = verbs;
            Points = points;
            Bounds = bounds;
        }

        /// <summary>One <see cref="Protocol.Verb"/> per segment.</summary>
        internal ImmutableArray<byte> Verbs { get; }

        /// <summary>x,y pairs of all segments in order (Move 1, Line 1, Quad 2, Cubic 3, Close 0 pairs).</summary>
        internal ImmutableArray<float> Points { get; }

        /// <summary>Bounding box of all points, control points included (zero rectangle for an empty path).</summary>
        internal RectangleF Bounds { get; }
    }

    /// <summary>
    /// An immutable vector path: a sequence of Move, Line, Quad, Cubic and Close segments. Building a path from another
    /// one costs O(1) because the new path only refers to its predecessor; the segments are put in order once, when the
    /// path is read (<see cref="Bounds"/>, drawing, <see cref="ContentEquals"/>).
    /// </summary>
    public sealed class QtPath
    {
        // A node is either one segment (_verb + points) with a predecessor, or a join (_appended != null) of
        // _previous and _appended. The Empty node has neither (_verb = byte.MaxValue).
        private readonly QtPath? _previous;
        private readonly QtPath? _appended;
        private readonly byte _verb;
        private readonly float _x0, _y0, _x1, _y1, _x2, _y2;
        private readonly int _verbCount;
        private readonly int _pointCount;
        // Current point (end of the last segment) and start of the current contour (target of Close), kept per node
        // so that relative nodes read them in O(1).
        private readonly float _lastX, _lastY, _startX, _startY;

        // Written by the first reader. Two threads may both compute it and the results are identical, so no lock is
        // needed: Interlocked.CompareExchange publishes exactly one instance and every reader gets that one.
        private FlatPath? _flat;

        /// <summary>The path without segments.</summary>
        public static QtPath Empty { get; } = new QtPath();

        private QtPath()
        {
            _verb = byte.MaxValue;
        }

        private QtPath(QtPath previous, byte verb, float x0, float y0, float x1, float y1, float x2, float y2)
        {
            _previous = previous;
            _verb = verb;
            _x0 = x0;
            _y0 = y0;
            _x1 = x1;
            _y1 = y1;
            _x2 = x2;
            _y2 = y2;
            _verbCount = previous._verbCount + 1;
            _pointCount = previous._pointCount + PointCountOf(verb);
            _startX = previous._startX;
            _startY = previous._startY;
            switch (verb)
            {
                case Protocol.Verb.Move:
                    _lastX = _startX = x0;
                    _lastY = _startY = y0;
                    break;
                case Protocol.Verb.Line:
                    _lastX = x0;
                    _lastY = y0;
                    break;
                case Protocol.Verb.Quad:
                    _lastX = x1;
                    _lastY = y1;
                    break;
                case Protocol.Verb.Cubic:
                    _lastX = x2;
                    _lastY = y2;
                    break;
                default:
                    _lastX = _startX;
                    _lastY = _startY;
                    break;
            }
        }

        private QtPath(QtPath previous, QtPath appended)
        {
            _previous = previous;
            _appended = appended;
            _verb = byte.MaxValue;
            _verbCount = previous._verbCount + appended._verbCount;
            _pointCount = previous._pointCount + appended._pointCount;
            _lastX = appended._lastX;
            _lastY = appended._lastY;
            _startX = appended._startX;
            _startY = appended._startY;
        }

        private static int PointCountOf(byte verb) => verb switch
        {
            Protocol.Verb.Move => 1,
            Protocol.Verb.Line => 1,
            Protocol.Verb.Quad => 2,
            Protocol.Verb.Cubic => 3,
            _ => 0,
        };

        /// <summary>Returns true if the path has no segments.</summary>
        public bool IsEmpty => _verbCount == 0;

        /// <summary>Bounding box of all points, control points included (zero rectangle for an empty path).</summary>
        public RectangleF Bounds => Flatten().Bounds;

        /// <summary>The end point of the last segment, or the start of the contour after Close; (0, 0) for an empty path.</summary>
        internal Vector2 LastPoint => new Vector2(_lastX, _lastY);

        internal QtPath MoveTo(float x, float y) => new QtPath(this, Protocol.Verb.Move, x, y, 0, 0, 0, 0);

        internal QtPath LineTo(float x, float y) => new QtPath(this, Protocol.Verb.Line, x, y, 0, 0, 0, 0);

        internal QtPath QuadTo(float cx, float cy, float x, float y) => new QtPath(this, Protocol.Verb.Quad, cx, cy, x, y, 0, 0);

        internal QtPath CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) =>
            new QtPath(this, Protocol.Verb.Cubic, c1x, c1y, c2x, c2y, x, y);

        internal QtPath Close() => new QtPath(this, Protocol.Verb.Close, 0, 0, 0, 0, 0, 0);

        /// <summary>The segments of <paramref name="other"/> after the segments of this path.</summary>
        internal QtPath Append(QtPath other)
        {
            if (other.IsEmpty)
                return this;
            if (IsEmpty)
                return other;
            return new QtPath(this, other);
        }

        /// <summary>
        /// Returns the verbs, points and bounds of the path. Iterative, so chains of any length work. The result is
        /// computed on the first call and the same instance is returned afterwards.
        /// </summary>
        internal FlatPath Flatten()
        {
            var flat = Volatile.Read(ref _flat);
            if (flat != null)
                return flat;
            flat = Compute();
            return Interlocked.CompareExchange(ref _flat, flat, null) ?? flat;
        }

        private FlatPath Compute()
        {
            if (_verbCount == 0)
                return FlatPath.Empty;

            // Collect the segment nodes back to front: a segment node is followed by its predecessor, a join node
            // by its appended path (it comes later in drawing order, so it is visited first) and then by its
            // predecessor.
            var segments = new List<QtPath>(_verbCount);
            var stack = new Stack<QtPath>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node._verbCount == 0)
                    continue;
                if (node._appended == null)
                {
                    segments.Add(node);
                    stack.Push(node._previous!);
                }
                else
                {
                    stack.Push(node._previous!);
                    stack.Push(node._appended);
                }
            }

            var verbs = new byte[_verbCount];
            var points = new float[_pointCount * 2];
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            int v = 0, p = 0;
            for (int i = segments.Count - 1; i >= 0; i--)
            {
                var s = segments[i];
                verbs[v++] = s._verb;
                switch (s._verb)
                {
                    case Protocol.Verb.Move:
                    case Protocol.Verb.Line:
                        Put(s._x0, s._y0);
                        break;
                    case Protocol.Verb.Quad:
                        Put(s._x0, s._y0);
                        Put(s._x1, s._y1);
                        break;
                    case Protocol.Verb.Cubic:
                        Put(s._x0, s._y0);
                        Put(s._x1, s._y1);
                        Put(s._x2, s._y2);
                        break;
                }
            }

            var bounds = p == 0 ? default : new RectangleF(minX, minY, maxX - minX, maxY - minY);
            return new FlatPath(ImmutableCollectionsMarshal.AsImmutableArray(verbs), ImmutableCollectionsMarshal.AsImmutableArray(points), bounds);

            void Put(float x, float y)
            {
                points[p++] = x;
                points[p++] = y;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        /// <summary>
        /// Returns true if <paramref name="other"/> has the same segments and coordinates, whichever way the two paths
        /// were built.
        /// </summary>
        public bool ContentEquals(QtPath? other)
        {
            if (ReferenceEquals(this, other))
                return true;
            if (other == null || _verbCount != other._verbCount || _pointCount != other._pointCount)
                return false;
            var a = Flatten();
            var b = other.Flatten();
            // Compare the bits, so that NaN equals itself and an unchanged path is not sent again.
            return a.Verbs.AsSpan().SequenceEqual(b.Verbs.AsSpan())
                && MemoryMarshal.Cast<float, int>(a.Points.AsSpan()).SequenceEqual(MemoryMarshal.Cast<float, int>(b.Points.AsSpan()));
        }
    }
}
