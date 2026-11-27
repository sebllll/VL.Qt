using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Pin model mapping of the QML node factory: QML type name → <see cref="QmlValueKind"/> → CLR pin type → wire value
    /// (<see cref="QmlValue"/>). A <c>list&lt;T&gt;</c> of a type listed here gets a list kind (<see cref="ListKind"/>) and a Spread pin.
    /// Everything else (<c>alias</c>, <c>var</c>, lists of other types, <c>url</c>, <c>font</c>, enums, object types) gets no pin;
    /// the parser reports it as an info message.
    /// </summary>
    internal static class QmlTypes
    {
        /// <summary>Returns true if <paramref name="qmlType"/> (as written in the declaration, e.g. "real") gets a pin.</summary>
        public static bool TryMap(string? qmlType, out QmlValueKind kind)
        {
            // TODO: map "list<T>" to ListKind of the kind of T (also "list<double>"); the parser has to hand over the whole type.
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
                case "point": kind = QmlValueKind.Point; return true;
                case "size": kind = QmlValueKind.Size; return true;
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
            QmlValueKind.Vector2D or QmlValueKind.Point or QmlValueKind.Size => typeof(Vector2),
            QmlValueKind.Vector3D => typeof(Vector3),
            QmlValueKind.Vector4D => typeof(Vector4),
            QmlValueKind.Rect => typeof(RectangleF),
            QmlValueKind.RealList => typeof(Spread<float>),
            QmlValueKind.IntList => typeof(Spread<int>),
            QmlValueKind.BoolList => typeof(Spread<bool>),
            QmlValueKind.StringList => typeof(Spread<string>),
            QmlValueKind.ColorList => typeof(Spread<Color4>),
            QmlValueKind.Vector2DList or QmlValueKind.PointList or QmlValueKind.SizeList => typeof(Spread<Vector2>),
            QmlValueKind.Vector3DList => typeof(Spread<Vector3>),
            QmlValueKind.Vector4DList => typeof(Spread<Vector4>),
            QmlValueKind.RectList => typeof(Spread<RectangleF>),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>Returns true if <paramref name="kind"/> is a <c>list&lt;T&gt;</c> kind.</summary>
        public static bool IsList(QmlValueKind kind) => kind >= QmlValueKind.RealList;

        /// <summary>The kind of the elements of a list kind (<see cref="QmlValueKind.PointList"/> → <see cref="QmlValueKind.Point"/>).</summary>
        public static QmlValueKind ElementKind(QmlValueKind list) => IsList(list)
            ? list - QmlValueKind.RealList + QmlValueKind.Real
            : throw new ArgumentOutOfRangeException(nameof(list));

        /// <summary>The list kind of an element kind (<see cref="QmlValueKind.Point"/> → <see cref="QmlValueKind.PointList"/>).</summary>
        public static QmlValueKind ListKind(QmlValueKind element) => !IsList(element)
            ? element - QmlValueKind.Real + QmlValueKind.RealList
            : throw new ArgumentOutOfRangeException(nameof(element));

        /// <summary>
        /// The value QML gives a property of this kind without initializer, used as pin default when the file has no literal:
        /// 0, false, "", transparent (0, 0, 0, 0), zero vectors, point and size, empty rect, the empty spread for a list.
        /// </summary>
        public static object TypeDefault(QmlValueKind kind) => kind switch
        {
            QmlValueKind.Real => 0f,
            QmlValueKind.Int => 0,
            QmlValueKind.Bool => false,
            QmlValueKind.String => string.Empty,
            QmlValueKind.Color => new Color4(0f, 0f, 0f, 0f),
            QmlValueKind.Vector2D or QmlValueKind.Point or QmlValueKind.Size => Vector2.Zero,
            QmlValueKind.Vector3D => Vector3.Zero,
            QmlValueKind.Vector4D => Vector4.Zero,
            QmlValueKind.Rect => RectangleF.Empty,
            QmlValueKind.RealList => Spread<float>.Empty,
            QmlValueKind.IntList => Spread<int>.Empty,
            QmlValueKind.BoolList => Spread<bool>.Empty,
            QmlValueKind.StringList => Spread<string>.Empty,
            QmlValueKind.ColorList => Spread<Color4>.Empty,
            QmlValueKind.Vector2DList or QmlValueKind.PointList or QmlValueKind.SizeList => Spread<Vector2>.Empty,
            QmlValueKind.Vector3DList => Spread<Vector3>.Empty,
            QmlValueKind.Vector4DList => Spread<Vector4>.Empty,
            QmlValueKind.RectList => Spread<RectangleF>.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>
        /// Returns true if <paramref name="value"/> (a value from QtNative: EVENT_QML_PROPERTY or a signal argument) fits a pin of
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
                case (QmlValueKind.Vector2D or QmlValueKind.Point or QmlValueKind.Size, Vector2 v): result = v; return true;
                case (QmlValueKind.Vector3D, Vector3 v): result = v; return true;
                case (QmlValueKind.Vector4D, Vector4 v): result = v; return true;
                case (QmlValueKind.Rect, RectangleF r): result = r; return true;
                // TODO: a list kind takes the Spread<T> of the decoder (T = CLR element type); an empty spread fits every list kind,
                // Spread<int> ↔ Spread<float> convert like the scalars.
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
}
