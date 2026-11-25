using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Lib.Collections;
using VL.Lib.Mathematics;
using VL.Qt.Elements;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Shared core of the nodes Line, Polyline and Polygon (package 03): the element specific inputs (<see cref="Props"/>), the
    /// description of the <see cref="Shape"/> (<see cref="Create"/>). The public node classes are thin shells
    /// that pass their pins to <see cref="Describe"/>; Polygon passes Closed = true, Line its two points.
    /// The points live in the parent space: the node has no Anchor and no Size pin, it reports
    /// (max(0, maxX), max(0, maxY)) of the points as Size and uses the Anchor TopLeft.
    /// </summary>
    internal sealed class ShapeNodeCore : IDisposable
    {
        /// <summary>
        /// Element specific inputs. The spreads are compared by reference first, then by content (vvvv often creates a new spread with the
        /// same content every frame); the other fields by value.
        /// </summary>
        internal readonly record struct Props(
            Spread<Vector2> Points, bool Closed, Color4 Fill, Color4 StrokeColor, float StrokeWidth,
            ShapeCap Cap, ShapeJoin Join, float MiterLimit, Spread<float> Dash, float DashOffset,
            float TrimStart, float TrimEnd, float TrimOffset, ShapeFillRule FillRule)
        {
            /// <summary>Path geometry of the node Path; replaces <see cref="Points"/> when set. Compared by reference, then by content.</summary>
            public QtPath? Path { get; init; }

            public bool Equals(Props other)
                => Closed == other.Closed && Fill == other.Fill && StrokeColor == other.StrokeColor && StrokeWidth == other.StrokeWidth
                    && Cap == other.Cap && Join == other.Join && MiterLimit == other.MiterLimit && DashOffset == other.DashOffset
                    && TrimStart == other.TrimStart && TrimEnd == other.TrimEnd && TrimOffset == other.TrimOffset && FillRule == other.FillRule
                    && SpreadEquality.SameContent(Points, other.Points) && SpreadEquality.SameContent(Dash, other.Dash)
                    && (ReferenceEquals(Path, other.Path) || (Path is not null && Path.ContentEquals(other.Path)));

            public override int GetHashCode()
                => HashCode.Combine(Points.Count, Closed, Fill, StrokeColor, StrokeWidth, Dash.Count, TrimStart, TrimEnd);
        }

        /// <summary>Builds the element; the Size covers all points.</summary>
        internal static readonly Func<CommonInputs, Props, FeedbackSlot, Shape> Create = static (c, p, slot) => new Shape(
            c with { Size = p.Path is { } path ? Shape.ExtentOf(path) : Shape.ExtentOf(p.Points), Anchor = RectangleAnchor.TopLeft }, slot)
        {
            Points = p.Points,
            Path = p.Path,
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

        private readonly ElementNodeCore<Shape, Props> _node;

        public ShapeNodeCore(NodeContext nodeContext)
        {
            _node = new ElementNodeCore<Shape, Props>(nodeContext, Create);
        }

        /// <summary>For tests: warnings go to <paramref name="addWarning"/> instead of a NodeContext.</summary>
        internal ShapeNodeCore(Func<string, IDisposable> addWarning)
        {
            _node = new ElementNodeCore<Shape, Props>(addWarning, Create);
        }

        internal ElementNodeCore<Shape, Props> Core => _node;

        /// <summary>
        /// Describes the shape (a new description only when an input changes), updates the node warnings (feedback)
        /// and reads the outputs. Call once per Update.
        /// </summary>
        public Shape Describe(in CommonInputs common, in Props props, out Vector2 measuredSize, out RectangleF sceneBounds)
            => _node.Describe(common, props, out measuredSize, out sceneBounds);

        public void Dispose()
        {
            _node.Dispose();
        }
    }
}
