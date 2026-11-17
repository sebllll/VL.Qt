using Stride.Core.Mathematics;
using VL.Lib.Mathematics;

namespace VL.Qt
{
    /// <summary>
    /// Conversions that do not depend on the scene: the anchor of an element as Qt alignment and as an offset in the
    /// element's own size. Lengths between units and Qt logical pixels depend on the <see cref="Space"/> and the scene
    /// and live in <see cref="SpaceMapping"/>.
    /// </summary>
    public static class Geometry
    {
        /// <summary>
        /// Anchor as sent with <c>ANCHOR</c> (i32, Qt::Alignment: AlignLeft 0x1 / AlignRight 0x2 / AlignHCenter 0x4 |
        /// AlignTop 0x20 / AlignBottom 0x40 / AlignVCenter 0x80). QtNative resolves it against the item's actual
        /// size (also the implicit size with Auto Size on), so <c>TRANSFORM</c> is sent without the anchor
        /// (<see cref="SpaceMapping.ToQt(in Matrix)"/>). Default <see cref="RectangleAnchor.TopLeft"/> = 0x21 = QtNative default.
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
    }
}
