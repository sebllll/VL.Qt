using Stride.Core.Mathematics;
using VL.Qt.Wire;
using VL.Qt;

// elements have no position/Bounds property any more, only SIZE + ANCHOR + TRANSFORM. Raw-protocol scenarios that used
// to place an item with BOUNDS (x, y, w, h) get the same picture with SetPlace: SIZE (w, h), ANCHOR top left and a
// TRANSFORM translation (x, y). Inside Row/Column the layout owns the position, so there only (0, 0) is meaningful
// (the translation would be a visual offset).
internal static class GeometryExtensions
{
    public const int AlignTopLeft = 0x1 | 0x20;
    public const int AlignCenter = 0x4 | 0x80;

    public static void SetPlace(this ICommandSink s, uint id, float x, float y, float w, float h)
    {
        s.SetVec2(id, Protocol.PropertyId.Size, w, h);
        s.SetI32(id, Protocol.PropertyId.Anchor, AlignTopLeft);
        s.SetMat4(id, Protocol.PropertyId.Transform, Matrix.Translation(x, y, 0));
    }

    /// <summary>Old "Bounds + Transform around the default pivot": ANCHOR Center, TRANSFORM = m · T(center of the rect), so m acts around the center of the rectangle (x, y, w, h).</summary>
    public static void SetPlaceXf(this ICommandSink s, uint id, float x, float y, float w, float h, in Matrix m)
    {
        s.SetVec2(id, Protocol.PropertyId.Size, w, h);
        s.SetI32(id, Protocol.PropertyId.Anchor, AlignCenter);
        s.SetMat4(id, Protocol.PropertyId.Transform, m * Matrix.Translation(x + w / 2, y + h / 2, 0));
    }

    /// <summary>Same as <see cref="SetPlace"/> without the position: only SIZE (Row/Column children, size changes of an unmoved item).</summary>
    public static void SetSize(this ICommandSink s, uint id, float w, float h) => s.SetVec2(id, Protocol.PropertyId.Size, w, h);

    /// <summary>Node-level placement in units: size of the rectangle (use with <see cref="Tr"/> and anchor TopLeft).</summary>
    public static Vector2 Sz(this RectangleF b) => new(b.Width, b.Height);

    public static Matrix Tr(this RectangleF b) => Matrix.Translation(b.X, b.Y, 0);
}