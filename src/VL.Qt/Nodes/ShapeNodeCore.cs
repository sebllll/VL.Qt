using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shared core of the nodes Line, Polyline and Polygon (#154, package 03): the element specific inputs (<see cref="Props"/>), the
    /// description of the <see cref="Shape"/> (<see cref="Create"/>) and the warning for too many points. The public node classes are thin shells
    /// that pass their pins to <see cref="Describe"/>; Polygon passes Closed = true, Line the points of <see cref="LinePoints"/>.
    /// The points live in the parent space: the node has no Anchor and no Size pin, it reports
    /// (max(0, maxX), max(0, maxY)) of the points as Size and uses the Anchor TopLeft.
    /// </summary>
    internal sealed class ShapeNodeCore : IDisposable
    {
        /// <summary>Number of points that fit one command (two floats each); the reconciler cuts more.</summary>
        public const int MaxPoints = Protocol.MaxF32ArrayCount / 2;

        /// <summary>
        /// Element specific inputs. The spreads are compared by reference first, then by content (vvvv often creates a new spread with the
        /// same content every frame); the other fields by value.
        /// </summary>
        internal readonly record struct Props(
            Spread<Vector2> Points, bool Closed, Color4 Fill, Color4 StrokeColor, float StrokeWidth,
            ShapeCap Cap, ShapeJoin Join, float MiterLimit, Spread<float> Dash, float DashOffset,
            float TrimStart, float TrimEnd, float TrimOffset, ShapeFillRule FillRule)
        {
            public bool Equals(Props other)
                => Closed == other.Closed && Fill == other.Fill && StrokeColor == other.StrokeColor && StrokeWidth == other.StrokeWidth
                    && Cap == other.Cap && Join == other.Join && MiterLimit == other.MiterLimit && DashOffset == other.DashOffset
                    && TrimStart == other.TrimStart && TrimEnd == other.TrimEnd && TrimOffset == other.TrimOffset && FillRule == other.FillRule
                    && SameContent(Points, other.Points) && SameContent(Dash, other.Dash);

            public override int GetHashCode()
                => HashCode.Combine(Points.Count, Closed, Fill, StrokeColor, StrokeWidth, Dash.Count, TrimStart, TrimEnd);
        }

        /// <summary>
        /// Builds the element. The points are not cut here (the reconciler sends the first <see cref="MaxPoints"/>, <see cref="PointsWarning"/> says so);
        /// the Size covers only the points that are drawn.
        /// </summary>
        internal static readonly Func<CommonInputs, Props, SinkSlot, Shape> Create = static (c, p, slot) => new Shape
        {
            Size = Extent(p.Points),
            Transform = c.Transform,
            Anchor = RectangleAnchor.TopLeft,
            Opacity = c.Opacity,
            Clip = c.Clip,
            Debug = c.Debug,
            Interaction = c.Interaction,
            Effect = c.Effect,
            Sink = slot,
            Points = p.Points,
            Closed = p.Closed,
            Fill = p.Fill,
            StrokeColor = p.StrokeColor,
            StrokeWidth = p.StrokeWidth,
            Cap = p.Cap,
            Join = p.Join,
            MiterLimit = p.MiterLimit,
            Dash = p.Dash,
            DashOffset = p.DashOffset,
            TrimStart = p.TrimStart,
            TrimEnd = p.TrimEnd,
            TrimOffset = p.TrimOffset,
            FillRule = p.FillRule,
        };

        /// <summary>Returns the warning for <paramref name="count"/> points, null if all are drawn.</summary>
        internal static string? PointsWarning(int count)
            => count > MaxPoints ? $"Points: only the first {MaxPoints} of {count} points are drawn." : null;

        readonly ElementNode<Shape, Props> _node;
        readonly Func<string, IDisposable> _addWarning;
        string? _pointsWarning;
        IDisposable? _pointsWarningMessage;
        int _warnedCount;
        Spread<Vector2> _line = Spread<Vector2>.Empty;
        Vector2 _lineFrom, _lineTo;

        public ShapeNodeCore(NodeContext nodeContext)
        {
            _addWarning = w => nodeContext.AddPersistentMessage(VL.Lang.MessageSeverity.Warning, w);
            _node = new ElementNode<Shape, Props>(nodeContext, Create);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ShapeNodeCore(Func<string, IDisposable> addWarning)
        {
            _addWarning = addWarning;
            _node = new ElementNode<Shape, Props>(addWarning, Create);
        }

        internal ElementNode<Shape, Props> Core => _node;

        /// <summary>Warning shown for too many points, null if none.</summary>
        public string? Warning => _pointsWarning;

        /// <summary>
        /// Returns the points of a two-point line: the same instance as long as <paramref name="from"/> and <paramref name="to"/> stay the
        /// same, so an unchanged Line allocates nothing per frame.
        /// </summary>
        public Spread<Vector2> LinePoints(Vector2 from, Vector2 to)
        {
            if (_line.Count == 0 || _lineFrom != from || _lineTo != to)
            {
                _line = new[] { from, to }.ToSpread();
                _lineFrom = from;
                _lineTo = to;
            }
            return _line;
        }

        /// <summary>
        /// Describes the shape (a new description only when an input changes), updates the node warnings (too many points, sink)
        /// and reads the outputs. Call once per Update.
        /// </summary>
        public Shape Describe(in CommonInputs common, in Props props, out Vector2 measuredSize, out RectangleF sceneBounds)
        {
            var element = _node.Describe(common, props);
            UpdatePointsWarning(props.Points.Count);
            _node.ReadOutputs(out measuredSize, out sceneBounds);
            return element;
        }

        void UpdatePointsWarning(int count)
        {
            // The text is only formatted when the count changes.
            if (count == _warnedCount)
                return;
            _warnedCount = count;
            var warning = PointsWarning(count);
            if (warning == _pointsWarning)
                return;
            _pointsWarningMessage?.Dispose();
            _pointsWarningMessage = warning is null ? null : _addWarning(warning);
            _pointsWarning = warning;
        }

        public void Dispose()
        {
            _node.Dispose();
            _pointsWarningMessage?.Dispose();
            _pointsWarningMessage = null;
            _pointsWarning = null;
            _warnedCount = 0;
        }

        // Like Shape.ExtentOf, limited to the points that are drawn.
        static Vector2 Extent(Spread<Vector2> points)
        {
            if (points.Count <= MaxPoints)
                return Shape.ExtentOf(points);
            float x = 0, y = 0;
            for (int i = 0; i < MaxPoints; i++)
            {
                var p = points[i];
                if (p.X > x) x = p.X;
                if (p.Y > y) y = p.Y;
            }
            return new Vector2(x, y);
        }

        internal static bool SameContent(Spread<Vector2> a, Spread<Vector2> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a.Count != b.Count)
                return false;
            return MemoryMarshal.Cast<Vector2, float>(a.AsSpan()).SequenceEqual(MemoryMarshal.Cast<Vector2, float>(b.AsSpan()));
        }

        internal static bool SameContent(Spread<float> a, Spread<float> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a.Count != b.Count)
                return false;
            return a.AsSpan().SequenceEqual(b.AsSpan());
        }
    }
}
