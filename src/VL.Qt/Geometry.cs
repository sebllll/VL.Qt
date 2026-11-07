using System;
using Stride.Core.Mathematics;
using VL.Lib.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Central conversion from VL units (<see cref="Space"/>) to Qt item coordinates and from
    /// Stride transforms around an anchor to the matrix sent as <c>TRANSFORM</c> (mat4).
    /// </summary>
    /// <remarks>
    /// v1 implements <see cref="Space.DIPTopLeft"/> only: origin top left, y down, 1 unit = 100 DIP.
    /// Qt logical pixels are DIP (Qt's HiDPI scaling maps them to device pixels), so a Qt coordinate is
    /// value × 100. Lengths (size, translation, font size, radius, ...) are scaled; angles, opacity
    /// and scale factors are not. Other spaces fall back to DIPTopLeft (the renderer warns).
    /// Matrices use the Stride convention throughout: row vectors, row-major order M11..M44,
    /// translation in M41..M43 (QtNative transposes for Qt).
    /// </remarks>
    public static class Geometry
    {
        /// <summary>DIP (= Qt logical pixels) per VL unit.</summary>
        public const float DipPerUnit = 100f;

        /// <summary>The only space implemented in v1.</summary>
        public const Space DefaultSpace = Space.DIPTopLeft;

        public static bool IsSupported(Space space) => space == Space.DIPTopLeft;

        /// <summary>Space actually used for <paramref name="space"/> (fallback for unsupported values).</summary>
        public static Space Resolve(Space space) => IsSupported(space) ? space : DefaultSpace;

        /// <summary>Length in units → Qt logical pixels.</summary>
        public static float ToQt(float length) => length * DipPerUnit;

        /// <summary>Font size in units → whole Qt logical pixels (QFont pixel sizes are integers), at least 1.</summary>
        public static float ToFontPixels(float size) => MathF.Max(1f, MathF.Round(size * DipPerUnit));

        public static Vector2 ToQt(Vector2 v) => v * DipPerUnit;

        public static RectangleF ToQt(RectangleF r) =>
            new RectangleF(r.X * DipPerUnit, r.Y * DipPerUnit, r.Width * DipPerUnit, r.Height * DipPerUnit);

        /// <summary>Qt logical pixels → length in units (e.g. for measured sizes coming back).</summary>
        public static float FromQt(float length) => length / DipPerUnit;

        public static Vector2 FromQt(Vector2 v) => v / DipPerUnit;

        public static RectangleF FromQt(RectangleF r) =>
            new RectangleF(r.X / DipPerUnit, r.Y / DipPerUnit, r.Width / DipPerUnit, r.Height / DipPerUnit);

        /// <summary>
        /// Re-expresses a transform given in units in Qt logical pixels: S⁻¹·m·S with S = scale(100,100,100).
        /// Translation is multiplied by 100, the linear part (rotation, scale, shear) stays unchanged,
        /// the perspective column is divided by 100.
        /// </summary>
        public static Matrix ToQt(in Matrix m)
        {
            var r = m;
            r.M41 *= DipPerUnit; r.M42 *= DipPerUnit; r.M43 *= DipPerUnit;
            r.M14 /= DipPerUnit; r.M24 /= DipPerUnit; r.M34 /= DipPerUnit;
            return r;
        }

        /// <summary>
        /// Anchor as sent with <c>ANCHOR</c> (i32, Qt::Alignment: AlignLeft 0x1 / AlignRight 0x2 / AlignHCenter 0x4 |
        /// AlignTop 0x20 / AlignBottom 0x40 / AlignVCenter 0x80). QtNative resolves it against the item's actual
        /// size (also the implicit size with Auto Size on), so <c>TRANSFORM</c> is sent without the anchor
        /// (<see cref="ToQt(in Matrix)"/>). Default <see cref="RectangleAnchor.TopLeft"/> = 0x21 = QtNative default.
        /// </summary>
        public static int ToQtAlignment(RectangleAnchor anchor)
        {
            int h = anchor.Horizontal() switch
            {
                RectangleAnchorHorizontal.Left => 0x1,
                RectangleAnchorHorizontal.Right => 0x2,
                _ => 0x4,
            };
            int v = anchor.Vertical() switch
            {
                RectangleAnchorVertical.Top => 0x20,
                RectangleAnchorVertical.Bottom => 0x40,
                _ => 0x80,
            };
            return h | v;
        }
        /// <summary>Anchor position relative to the rectangle's top left corner (same units as <paramref name="size"/>).</summary>
        public static Vector2 AnchorOffset(Vector2 size, RectangleAnchor anchor)
        {
            float fx = anchor.Horizontal() switch
            {
                RectangleAnchorHorizontal.Left => 0f,
                RectangleAnchorHorizontal.Right => 1f,
                _ => 0.5f,
            };
            float fy = anchor.Vertical() switch
            {
                RectangleAnchorVertical.Top => 0f,
                RectangleAnchorVertical.Bottom => 1f,
                _ => 0.5f,
            };
            return new Vector2(size.X * fx, size.Y * fy);
        }

        /// <summary>
        /// Reference formula of the item → parent mapping QtNative computes from SIZE + ANCHOR + TRANSFORM (the
        /// reconciler sends them separately; used by tests), for an element outside a Row/Column: a point v in the
        /// item's local coordinates (origin = its top left, Qt logical pixels) lands at <c>v · T(-anchor) · m</c>
        /// in parent coordinates, like VL.Skia. In a Row/Column the layout position and <c>T(anchor)</c> follow on
        /// top (the anchor is only the pivot there). <paramref name="m"/> and <paramref name="size"/> are in units.
        /// Result is a Stride-convention matrix (row vectors, row-major M11..M44).
        /// </summary>
        public static Matrix ToQtTransform(in Matrix m, Vector2 size, RectangleAnchor anchor)
        {
            var p = AnchorOffset(size, anchor) * DipPerUnit;
            var mq = ToQt(m);
            Matrix.Translation(-p.X, -p.Y, 0f, out var toOrigin);
            Matrix.Multiply(ref toOrigin, ref mq, out var result);
            return result;
        }
    }
}
