using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// One value of a QmlElement property as it goes over the wire: wire value type and the value in plain fields, so
    /// value equality is exact (Stride's vector equality is a near-equal compare and would swallow tiny changes) and no boxing
    /// is left after the conversion. Color and rect use <c>A..D</c> (r, g, b, a / x, y, width, height).
    /// </summary>
    internal readonly record struct QmlValue(ushort ValueType, float A, float B, float C, float D, int Integer, string? Text)
    {
        /// <summary>
        /// Converts a patch value: float and double → real (f32), int, bool, string, Color4 → color, Vector2/3/4 → vector2d/3d/4d,
        /// RectangleF → rect. Anything else (also null) is not supported.
        /// No allocation.
        /// </summary>
        public static bool TryConvert(object? value, out QmlValue result)
        {
            switch (value)
            {
                case float f: result = new QmlValue(Protocol.ValueType.F32, f, 0, 0, 0, 0, null); return true;
                case double d: result = new QmlValue(Protocol.ValueType.F32, (float)d, 0, 0, 0, 0, null); return true;
                case int i: result = new QmlValue(Protocol.ValueType.I32, 0, 0, 0, 0, i, null); return true;
                case bool b: result = new QmlValue(Protocol.ValueType.Bool, 0, 0, 0, 0, b ? 1 : 0, null); return true;
                case string s: result = new QmlValue(Protocol.ValueType.String, 0, 0, 0, 0, 0, s); return true;
                case Color4 c: result = new QmlValue(Protocol.ValueType.Color, c.R, c.G, c.B, c.A, 0, null); return true;
                case Vector2 v2: result = new QmlValue(Protocol.ValueType.Vec2, v2.X, v2.Y, 0, 0, 0, null); return true;
                case Vector3 v3: result = new QmlValue(Protocol.ValueType.Vec3, v3.X, v3.Y, v3.Z, 0, 0, null); return true;
                case Vector4 v4: result = new QmlValue(Protocol.ValueType.Vec4, v4.X, v4.Y, v4.Z, v4.W, 0, null); return true;
                case RectangleF r: result = new QmlValue(Protocol.ValueType.Rectf, r.X, r.Y, r.Width, r.Height, 0, null); return true;
                default: result = default; return false;
            }
        }

        /// <summary>The line the node shows for a value <see cref="TryConvert"/> refused (value ignored).</summary>
        public static string Describe(string name, object? value) => value switch
        {
            null => $"Property '{name}': the value is not set, ignored",
            _ => $"Property '{name}': type {value.GetType().Name} is not supported, ignored (use float, int, bool, string, Color4, Vector2/3/4 or RectangleF)",
        };
    }
}
