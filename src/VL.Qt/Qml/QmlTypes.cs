using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;

namespace VL.Qt.Qml
{
    /// <summary>
    /// QML value types that get a pin. The wire form is <see cref="QmlValue"/> of the QmlElement, so every
    /// kind maps to one CLR pin type and one <c>Protocol.Vt</c> value type.
    /// </summary>
    internal enum QmlValueKind
    {
        /// <summary>QML <c>real</c> or <c>double</c> → float (wire f32).</summary>
        Real,
        /// <summary>QML <c>int</c> → int.</summary>
        Int,
        /// <summary>QML <c>bool</c> → bool.</summary>
        Bool,
        /// <summary>QML <c>string</c> → string.</summary>
        String,
        /// <summary>QML <c>color</c> → Stride Color4 (straight alpha).</summary>
        Color,
        /// <summary>QML <c>vector2d</c> → Stride Vector2.</summary>
        Vector2D,
        /// <summary>QML <c>vector3d</c> → Stride Vector3.</summary>
        Vector3D,
        /// <summary>QML <c>vector4d</c> → Stride Vector4.</summary>
        Vector4D,
        /// <summary>QML <c>rect</c> → Stride RectangleF (x, y, width, height).</summary>
        Rect,
    }

    /// <summary>
    /// Pin model mapping of the QML node factory: QML type name → <see cref="QmlValueKind"/> → CLR pin type → wire value
    /// (<see cref="QmlValue"/>). Everything not listed here (<c>alias</c>, <c>var</c>, <c>list&lt;…&gt;</c>, <c>url</c>, <c>point</c>,
    /// <c>size</c>, <c>font</c>, enums, object types) gets no pin; the parser reports it as an info message.
    /// </summary>
    internal static class QmlTypes
    {
        /// <summary>Returns true if <paramref name="qmlType"/> (as written in the declaration, e.g. "real") gets a pin.</summary>
        public static bool TryMap(string? qmlType, out QmlValueKind kind)
        {
            switch (qmlType)
            {
                case "real":
                case "double": kind = QmlValueKind.Real; return true;
                case "int": kind = QmlValueKind.Int; return true;
                case "bool": kind = QmlValueKind.Bool; return true;
                case "string": kind = QmlValueKind.String; return true;
                case "color": kind = QmlValueKind.Color; return true;
                case "vector2d": kind = QmlValueKind.Vector2D; return true;
                case "vector3d": kind = QmlValueKind.Vector3D; return true;
                case "vector4d": kind = QmlValueKind.Vector4D; return true;
                case "rect": kind = QmlValueKind.Rect; return true;
                default: kind = default; return false;
            }
        }

        /// <summary>CLR type of the pin of a property of this kind.</summary>
        public static Type ClrType(QmlValueKind kind) => kind switch
        {
            QmlValueKind.Real => typeof(float),
            QmlValueKind.Int => typeof(int),
            QmlValueKind.Bool => typeof(bool),
            QmlValueKind.String => typeof(string),
            QmlValueKind.Color => typeof(Color4),
            QmlValueKind.Vector2D => typeof(Vector2),
            QmlValueKind.Vector3D => typeof(Vector3),
            QmlValueKind.Vector4D => typeof(Vector4),
            QmlValueKind.Rect => typeof(RectangleF),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>
        /// The value QML gives a property of this kind without initializer, used as pin default when the file has no literal:
        /// 0, false, "", transparent (0, 0, 0, 0), zero vectors, empty rect.
        /// </summary>
        public static object TypeDefault(QmlValueKind kind) => kind switch
        {
            QmlValueKind.Real => 0f,
            QmlValueKind.Int => 0,
            QmlValueKind.Bool => false,
            QmlValueKind.String => string.Empty,
            QmlValueKind.Color => new Color4(0f, 0f, 0f, 0f),
            QmlValueKind.Vector2D => Vector2.Zero,
            QmlValueKind.Vector3D => Vector3.Zero,
            QmlValueKind.Vector4D => Vector4.Zero,
            QmlValueKind.Rect => RectangleF.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>Wire value type (<c>Protocol.Vt</c>) of this kind, the one <see cref="QmlValue.TryConvert"/> produces for its CLR type.</summary>
        public static ushort WireType(QmlValueKind kind) => kind switch
        {
            QmlValueKind.Real => Protocol.Vt.F32,
            QmlValueKind.Int => Protocol.Vt.I32,
            QmlValueKind.Bool => Protocol.Vt.Bool,
            QmlValueKind.String => Protocol.Vt.String,
            QmlValueKind.Color => Protocol.Vt.Color,
            QmlValueKind.Vector2D => Protocol.Vt.Vec2,
            QmlValueKind.Vector3D => Protocol.Vt.Vec3,
            QmlValueKind.Vector4D => Protocol.Vt.Vec4,
            QmlValueKind.Rect => Protocol.Vt.Rectf,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>
        /// Returns true if <paramref name="value"/> (a value from QtNative: EV_QML_PROPERTY or a signal argument) fits a pin of
        /// <paramref name="kind"/>; <paramref name="result"/> is then of <see cref="ClrType"/>. Converts the cases QML mixes
        /// (int ↔ float for a number), refuses everything else (e.g. a string for a color).
        /// </summary>
        public static bool TryCoerce(QmlValueKind kind, object? value, out object result)
        {
            switch (kind, value)
            {
                case (QmlValueKind.Real, float f): result = f; return true;
                case (QmlValueKind.Real, double d): result = (float)d; return true;
                case (QmlValueKind.Real, int i): result = (float)i; return true;
                case (QmlValueKind.Int, int i): result = i; return true;
                case (QmlValueKind.Int, float f) when float.IsFinite(f): result = (int)MathF.Round(f); return true;
                case (QmlValueKind.Bool, bool b): result = b; return true;
                case (QmlValueKind.String, string s): result = s; return true;
                case (QmlValueKind.Color, Color4 c): result = c; return true;
                case (QmlValueKind.Vector2D, Vector2 v): result = v; return true;
                case (QmlValueKind.Vector3D, Vector3 v): result = v; return true;
                case (QmlValueKind.Vector4D, Vector4 v): result = v; return true;
                case (QmlValueKind.Rect, RectangleF r): result = r; return true;
                default: result = TypeDefault(kind); return false;
            }
        }

        /// <summary>
        /// CLR type of the output pin of a signal: no arguments → bool (true in the frame it fired); one argument →
        /// Spread of its values this frame (Spread&lt;object&gt; for an argument without <see cref="QmlValueKind"/>: QtNative sends
        /// it in its string form or as whatever supported type the value has); two or more → Spread&lt;QmlSignal&gt;.
        /// </summary>
        public static Type SignalPinType(QmlSignalShape shape, QmlValueKind? argument) => shape switch
        {
            QmlSignalShape.Bang => typeof(bool),
            QmlSignalShape.Values => typeof(Spread<>).MakeGenericType(argument is { } k ? ClrType(k) : typeof(object)),
            QmlSignalShape.Signals => typeof(Spread<QmlSignal>),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        /// <summary>Shape of a signal's output pin by its number of arguments.</summary>
        public static QmlSignalShape SignalShape(int argumentCount) => argumentCount switch
        {
            0 => QmlSignalShape.Bang,
            1 => QmlSignalShape.Values,
            _ => QmlSignalShape.Signals,
        };
    }

    /// <summary>What the output pin of a declared signal carries, see <see cref="QmlTypes.SignalPinType"/>.</summary>
    internal enum QmlSignalShape
    {
        /// <summary>No arguments: bool, true in the frame the signal fired.</summary>
        Bang,
        /// <summary>One argument: Spread of its values, one per emission this frame.</summary>
        Values,
        /// <summary>Two or more arguments: Spread&lt;QmlSignal&gt;, one per emission this frame.</summary>
        Signals,
    }
}
