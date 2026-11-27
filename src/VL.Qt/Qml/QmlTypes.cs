using System;
using System.Linq;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Wire;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Qml
{
    /// <summary>
    /// Pin model mapping of the QML node factory: QML type name → <see cref="QmlValueKind"/> → CLR pin type → wire value
    /// (<see cref="QmlValue"/>). A <c>list&lt;T&gt;</c> of a type listed here gets a list kind (<see cref="ListKind"/>) and a Spread pin.
    /// Everything else (<c>alias</c>, <c>var</c>, lists of other types, <c>font</c>, enums, object types) gets no pin;
    /// the parser reports it as an info message.
    /// </summary>
    internal static class QmlTypes
    {
        private static readonly Path s_emptyPath = new(string.Empty);

        /// <summary>Returns true if <paramref name="qmlType"/> (as written in the declaration, e.g. "real") gets a pin.</summary>
        public static bool TryMap(string? qmlType, out QmlValueKind kind)
        {
            // list<T> of a type with a pin; a list of lists or of an unsupported type gets none.
            if (qmlType is { Length: > 6 } && qmlType.StartsWith("list<", StringComparison.Ordinal) && qmlType.EndsWith('>'))
            {
                if (TryMapElement(qmlType.AsSpan(5, qmlType.Length - 6).Trim().ToString(), out var element))
                {
                    kind = ListKind(element);
                    return true;
                }
                kind = default;
                return false;
            }
            return TryMapElement(qmlType, out kind);
        }

        private static bool TryMapElement(string? qmlType, out QmlValueKind kind)
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
                case "point": kind = QmlValueKind.Point; return true;
                case "size": kind = QmlValueKind.Size; return true;
                case "url": kind = QmlValueKind.Url; return true;
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
            QmlValueKind.Url => typeof(Path),
            QmlValueKind.RealList => typeof(Spread<float>),
            QmlValueKind.IntList => typeof(Spread<int>),
            QmlValueKind.BoolList => typeof(Spread<bool>),
            QmlValueKind.StringList => typeof(Spread<string>),
            QmlValueKind.ColorList => typeof(Spread<Color4>),
            QmlValueKind.Vector2DList or QmlValueKind.PointList or QmlValueKind.SizeList => typeof(Spread<Vector2>),
            QmlValueKind.Vector3DList => typeof(Spread<Vector3>),
            QmlValueKind.Vector4DList => typeof(Spread<Vector4>),
            QmlValueKind.RectList => typeof(Spread<RectangleF>),
            QmlValueKind.UrlList => typeof(Spread<Path>),
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
        /// 0, false, "", transparent (0, 0, 0, 0), zero vectors, point and size, empty rect, the empty path, the empty spread for a list.
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
            QmlValueKind.Url => s_emptyPath,
            QmlValueKind.RealList => Spread<float>.Empty,
            QmlValueKind.IntList => Spread<int>.Empty,
            QmlValueKind.BoolList => Spread<bool>.Empty,
            QmlValueKind.StringList => Spread<string>.Empty,
            QmlValueKind.ColorList => Spread<Color4>.Empty,
            QmlValueKind.Vector2DList or QmlValueKind.PointList or QmlValueKind.SizeList => Spread<Vector2>.Empty,
            QmlValueKind.Vector3DList => Spread<Vector3>.Empty,
            QmlValueKind.Vector4DList => Spread<Vector4>.Empty,
            QmlValueKind.RectList => Spread<RectangleF>.Empty,
            QmlValueKind.UrlList => Spread<Path>.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        /// <summary>
        /// A url as QML gives it (its text), or a url literal of the file, as the Path of a pin: a local path and a <c>file:</c> URL
        /// are the local path, a URL with another scheme (<c>http:</c>, <c>qrc:</c>, …) stays as written, and a relative one is
        /// resolved against <paramref name="baseFolder"/> (the folder of the .qml file; null keeps it relative), as QML resolves a
        /// url literal against its file.
        /// </summary>
        public static Path UrlToPath(string url, string? baseFolder)
        {
            if (url.Length == 0)
                return s_emptyPath;
            // Before the URI test: "C:/x" would read as a URL of the scheme "c".
            if (System.IO.Path.IsPathFullyQualified(url))
                return new Path(url);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return new Path(uri.IsFile ? uri.LocalPath : url);
            return new Path(baseFolder is null ? url : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseFolder, url)));
        }

        /// <summary>
        /// Returns true if <paramref name="value"/> (a value from QtNative: EVENT_QML_PROPERTY or a signal argument) fits a pin of
        /// <paramref name="kind"/>; <paramref name="result"/> is then of <see cref="ClrType"/>. Converts the cases QML mixes
        /// (int ↔ float for a number), the text of a url into a Path (<see cref="UrlToPath"/>, relative to <paramref name="baseFolder"/>),
        /// refuses everything else (e.g. a string for a color).
        /// </summary>
        public static bool TryCoerce(QmlValueKind kind, object? value, out object result, string? baseFolder = null)
        {
            if (IsList(kind))
                return TryCoerceList(kind, value, out result, baseFolder);

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
                case (QmlValueKind.Url, Path p): result = p; return true;
                case (QmlValueKind.Url, string s): result = UrlToPath(s, baseFolder); return true;
                default: result = TypeDefault(kind); return false;
            }
        }

        // A list kind takes the Spread<T> of the decoder (T = CLR element type of the wire): an empty spread fits every list kind,
        // Spread<int> and Spread<float> convert element by element like the scalars, the texts of urls into Paths, the rest has to be of
        // the pin type.
        private static bool TryCoerceList(QmlValueKind kind, object? value, out object result, string? baseFolder)
        {
            if (value is not null && value.GetType() == ClrType(kind))
            {
                result = value;
                return true;
            }
            switch (kind, value)
            {
                case (_, ISpread { Count: 0 }):
                    result = TypeDefault(kind);
                    return true;
                case (QmlValueKind.RealList, Spread<int> ints):
                    result = ints.Select(static i => (float)i).ToSpread();
                    return true;
                case (QmlValueKind.IntList, Spread<float> floats) when floats.All(static f => float.IsFinite(f)):
                    result = floats.Select(static f => (int)MathF.Round(f)).ToSpread();
                    return true;
                case (QmlValueKind.UrlList, Spread<string> urls):
                    result = urls.Select(u => UrlToPath(u, baseFolder)).ToSpread();
                    return true;
                default:
                    result = TypeDefault(kind);
                    return false;
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
