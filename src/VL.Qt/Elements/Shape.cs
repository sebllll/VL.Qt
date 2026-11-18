using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Paths;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Polyline, polygon or path with fill and stroke (Qt Quick Shapes). The geometry is either <see cref="Points"/>
    /// (with <see cref="Closed"/>) or <see cref="Path"/>; a set <see cref="Path"/> wins. Points and lengths are in units
    /// in the parent space, <see cref="Element.Transform"/> acts around the origin.
    /// </summary>
    public sealed class Shape : Element
    {
        public Shape() : base(ElementType.Shape) { }

        Spread<Vector2> _points = Spread<Vector2>.Empty;
        Spread<float> _dash = Spread<float>.Empty;

        /// <summary>Vertices of the polyline or polygon in units; ignored when <see cref="Path"/> is set.</summary>
        public Spread<Vector2> Points { get => _points; init => _points = value ?? Spread<Vector2>.Empty; }

        /// <summary>Connects the last of the <see cref="Points"/> to the first.</summary>
        public bool Closed { get; init; }

        /// <summary>Path geometry; replaces <see cref="Points"/> when not null.</summary>
        public QtPath? Path { get; init; }

        /// <summary>Fill color, default transparent (no fill).</summary>
        public Color4 Fill { get; init; } = new Color4(0, 0, 0, 0);

        /// <summary>Stroke color, default white.</summary>
        public Color4 StrokeColor { get; init; } = Color4.White;

        /// <summary>Stroke width in units; 0 = no stroke.</summary>
        public float StrokeWidth { get; init; }

        /// <summary>Shape of the open ends of the stroke.</summary>
        public ShapeCap Cap { get; init; } = ShapeCap.Flat;

        /// <summary>Shape of the stroke at corners.</summary>
        public ShapeJoin Join { get; init; } = ShapeJoin.Miter;

        /// <summary>Limit for miter joins; QtNative rounds it to an integer.</summary>
        public float MiterLimit { get; init; } = 4;

        /// <summary>Dash pattern as alternating dash and gap lengths in units; empty = solid.</summary>
        public Spread<float> Dash { get => _dash; init => _dash = value ?? Spread<float>.Empty; }

        /// <summary>Start of the dash pattern along the stroke, in units.</summary>
        public float DashOffset { get; init; }

        /// <summary>Start of the visible part as fraction of the path length.</summary>
        public float TrimStart { get; init; }

        /// <summary>End of the visible part as fraction of the path length.</summary>
        public float TrimEnd { get; init; } = 1;

        /// <summary>Shifts the visible part along the path, as fraction of the path length.</summary>
        public float TrimOffset { get; init; }

        /// <summary>Which areas of a self-overlapping geometry are filled.</summary>
        public ShapeFillRule FillRule { get; init; } = ShapeFillRule.NonZero;

        /// <summary>Returns the extent of the points, (max(0, maxX), max(0, maxY)): the size a shape reports to Row/Column and the tooltip.</summary>
        public static Vector2 ExtentOf(Spread<Vector2> points)
        {
            float x = 0, y = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (p.X > x) x = p.X;
                if (p.Y > y) y = p.Y;
            }
            return new Vector2(x, y);
        }

        /// <summary>Returns the extent of the path, (max(0, maxX), max(0, maxY)), control points included.</summary>
        public static Vector2 ExtentOf(QtPath path)
        {
            var b = path.Bounds;
            return new Vector2(path.IsEmpty ? 0 : System.Math.Max(0, b.Right), path.IsEmpty ? 0 : System.Math.Max(0, b.Bottom));
        }
    }
}
