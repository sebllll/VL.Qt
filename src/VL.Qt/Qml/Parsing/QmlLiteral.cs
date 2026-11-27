using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using static VL.Qt.Qml.QmlLexer;
using Font = VL.Qt.Elements.Font;
using Path = VL.Lib.IO.Path;

namespace VL.Qt.Qml
{
    /// <summary>Evaluates the literal initializer of a property to a pin default, including the SVG colour names.</summary>
    internal static class QmlLiteral
    {
        private static readonly Lazy<Dictionary<string, Color4>> s_namedColors = new(BuildNamedColors);

        public static (QmlInitializer, object) Evaluate(QmlValueKind kind, List<Token> t, int s, int e)
            => TryEvaluate(kind, t, s, e, out var value) ? (QmlInitializer.Literal, value) : (QmlInitializer.Binding, QmlTypes.TypeDefault(kind));

        private static bool TryEvaluate(QmlValueKind kind, List<Token> t, int s, int e, out object value)
        {
            value = QmlTypes.TypeDefault(kind);
            if (QmlTypes.IsList(kind))
                return TryEvaluateList(kind, t, s, e, out value);
            double d;
            double[] a;
            string fn;
            switch (kind)
            {
                case QmlValueKind.Real:
                    if (TryNumber(t, s, e, out d) && float.IsFinite((float)d))
                    {
                        value = (float)d;
                        return true;
                    }
                    return false;
                case QmlValueKind.Int:
                    if (TryNumber(t, s, e, out d) && d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue)
                    {
                        value = (int)d;
                        return true;
                    }
                    return false;
                case QmlValueKind.Bool:
                    if (e - s == 1 && (t[s].IsIdent("true") || t[s].IsIdent("false")))
                    {
                        value = t[s].Text == "true";
                        return true;
                    }
                    return false;
                case QmlValueKind.String:
                    if (e - s == 1 && t[s].Kind == TokenKind.String)
                    {
                        value = t[s].Value ?? string.Empty;
                        return true;
                    }
                    return false;
                case QmlValueKind.Color:
                    if (e - s == 1 && t[s].Kind == TokenKind.String)
                    {
                        if (!TryColor(t[s].Value ?? string.Empty, out var color))
                            return false;
                        value = color;
                        return true;
                    }
                    if (TryQtCall(t, s, e, out fn, out a) && a.All(static x => x is >= 0 and <= 1))
                    {
                        if (fn == "rgba" && a.Length == 4)
                        {
                            value = new Color4((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                            return true;
                        }
                        if (fn == "rgb" && a.Length == 3)
                        {
                            value = new Color4((float)a[0], (float)a[1], (float)a[2], 1f);
                            return true;
                        }
                    }
                    return false;
                case QmlValueKind.Vector2D:
                case QmlValueKind.Point:
                case QmlValueKind.Size:
                    var constructor = kind switch { QmlValueKind.Point => "point", QmlValueKind.Size => "size", _ => "vector2d" };
                    if (TryQtCall(t, s, e, out fn, out a) && fn == constructor && a.Length == 2 && Finite(a))
                    {
                        value = new Vector2((float)a[0], (float)a[1]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Vector3D:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "vector3d" && a.Length == 3 && Finite(a))
                    {
                        value = new Vector3((float)a[0], (float)a[1], (float)a[2]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Vector4D:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "vector4d" && a.Length == 4 && Finite(a))
                    {
                        value = new Vector4((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Rect:
                    if (TryQtCall(t, s, e, out fn, out a) && fn == "rect" && a.Length == 4 && Finite(a))
                    {
                        value = new RectangleF((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
                        return true;
                    }
                    return false;
                case QmlValueKind.Url:
                    // "images/a.png" or Qt.resolvedUrl("images/a.png") as written: QmlNodeLayout resolves it against the folder of the file.
                    if (e - s == 1 && t[s].Kind == TokenKind.String)
                    {
                        value = new Path(t[s].Value ?? string.Empty);
                        return true;
                    }
                    if (e - s == 6 && t[s].IsIdent("Qt") && t[s + 1].IsPunct('.') && t[s + 2].IsIdent("resolvedUrl") && t[s + 3].IsPunct('(')
                        && t[s + 4].Kind == TokenKind.String && t[s + 5].IsPunct(')'))
                    {
                        value = new Path(t[s + 4].Value ?? string.Empty);
                        return true;
                    }
                    return false;
                case QmlValueKind.Date:
                    // A date as text, as QML converts it ("2024-05-01", "2024-05-01T12:00:00"), in local time.
                    if (e - s == 1 && t[s].Kind == TokenKind.String
                        && DateTime.TryParse(t[s].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
                    {
                        value = date;
                        return true;
                    }
                    return false;
                case QmlValueKind.Font:
                    if (TryFont(t, s, e, out var font))
                    {
                        value = font;
                        return true;
                    }
                    return false;
                default:
                    return false;
            }

            static bool Finite(double[] values) => values.All(static x => float.IsFinite((float)x));
        }

        // [ element, element, ... ] over the whole range s..e (a trailing comma is allowed, [] is the empty list); every element
        // is a literal of the element kind. One element that is not readable makes the whole list unreadable.
        private static bool TryEvaluateList(QmlValueKind kind, List<Token> t, int s, int e, out object value)
        {
            value = QmlTypes.TypeDefault(kind);
            if (e - s < 2 || !t[s].IsPunct('[') || !t[e - 1].IsPunct(']'))
                return false;

            var element = QmlTypes.ElementKind(kind);
            var items = new List<object>();
            var depth = 0;
            var from = s + 1;
            for (var j = s; j < e; j++)
            {
                if (t[j].Kind == TokenKind.Punct)
                {
                    var c = t[j].Text[0];
                    if (c is '(' or '[' or '{')
                        depth++;
                    else if (c is ')' or ']' or '}')
                        depth--;
                    // The first bracket must close at the end: `[1] + [2]` is an expression.
                    if (depth == 0 && j < e - 1)
                        return false;
                }
                if (depth == 1 && t[j].IsPunct(',') || depth == 0 && j == e - 1)
                {
                    // `[]` and `[1, 2,]`: nothing between the brackets or after the last comma.
                    if (j == from && j == e - 1)
                    {
                        from = j + 1;
                        continue;
                    }
                    if (!TryEvaluate(element, t, from, j, out var item))
                        return false;
                    items.Add(item);
                    from = j + 1;
                }
            }
            if (depth != 0)
                return false;

            value = element switch
            {
                QmlValueKind.Real => Build<float>(items),
                QmlValueKind.Int => Build<int>(items),
                QmlValueKind.Bool => Build<bool>(items),
                QmlValueKind.String => Build<string>(items),
                QmlValueKind.Color => Build<Color4>(items),
                QmlValueKind.Vector2D or QmlValueKind.Point or QmlValueKind.Size => Build<Vector2>(items),
                QmlValueKind.Vector3D => Build<Vector3>(items),
                QmlValueKind.Vector4D => Build<Vector4>(items),
                QmlValueKind.Rect => Build<RectangleF>(items),
                QmlValueKind.Url => Build<Path>(items),
                QmlValueKind.Date => Build<DateTime>(items),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            return true;
        }

        private static Spread<T> Build<T>(List<object> items) => items.Count == 0 ? Spread<T>.Empty : items.Cast<T>().ToSpread();

        private static readonly Dictionary<string, int> s_weights = new(StringComparer.Ordinal)
        {
            ["Thin"] = 100, ["ExtraLight"] = 200, ["Light"] = 300, ["Normal"] = 400, ["Medium"] = 500,
            ["DemiBold"] = 600, ["Bold"] = 700, ["ExtraBold"] = 800, ["Black"] = 900,
        };

        // Qt.font({ family: "Arial", pixelSize: 24, weight: Font.Bold, italic: true }) over s..e; also pointSize (4/3 pixels per point
        // at the 96 DIP per inch of Qt) and bold. The size becomes units at SpaceMapping.DipPerUnit (DIPTopLeft, the default Space). Any
        // other key or a value that is not a literal makes it a binding.
        private static bool TryFont(List<Token> t, int s, int e, out Font font)
        {
            font = new Font();
            if (e - s < 6 || !t[s].IsIdent("Qt") || !t[s + 1].IsPunct('.') || !t[s + 2].IsIdent("font") || !t[s + 3].IsPunct('(')
                || !t[s + 4].IsPunct('{') || !t[e - 2].IsPunct('}') || !t[e - 1].IsPunct(')'))
                return false;
            int? weight = null;
            var bold = false;
            for (var j = s + 5; j < e - 2;)
            {
                if (t[j].Kind != TokenKind.Ident || j + 2 >= e - 1 || !t[j + 1].IsPunct(':'))
                    return false;
                var key = t[j].Text;
                var v = j + 2;
                var end = v;
                while (end < e - 2 && !t[end].IsPunct(','))
                    end++;
                switch (key)
                {
                    case "family" when end - v == 1 && t[v].Kind == TokenKind.String:
                        font = font with { Family = t[v].Value ?? string.Empty };
                        break;
                    case "pixelSize" when TryNumber(t, v, end, out var px) && px > 0:
                        font = font with { Size = (float)(px / SpaceMapping.DipPerUnit) };
                        break;
                    case "pointSize" when TryNumber(t, v, end, out var pt) && pt > 0:
                        font = font with { Size = (float)(pt * 4 / 3 / SpaceMapping.DipPerUnit) };
                        break;
                    case "weight" when TryNumber(t, v, end, out var w) && w is >= 1 and <= 1000:
                        weight = (int)w;
                        break;
                    case "weight" when end - v == 3 && t[v].IsIdent("Font") && t[v + 1].IsPunct('.') && s_weights.TryGetValue(t[v + 2].Text, out var named):
                        weight = named;
                        break;
                    case "bold" or "italic" when end - v == 1 && (t[v].IsIdent("true") || t[v].IsIdent("false")):
                        if (key == "bold")
                            bold = t[v].Text == "true";
                        else
                            font = font with { Italic = t[v].Text == "true" };
                        break;
                    default:
                        return false;
                }
                j = end < e - 2 ? end + 1 : end;
            }
            font = font with { Weight = weight ?? (bold ? 700 : 400) };
            return true;
        }

        // [+|-] number
        private static bool TryNumber(List<Token> t, int s, int e, out double value)
        {
            value = 0;
            var sign = 1.0;
            if (s < e && (t[s].IsPunct('-') || t[s].IsPunct('+')))
            {
                if (t[s].IsPunct('-'))
                    sign = -1;
                s++;
            }
            if (e - s != 1 || t[s].Kind != TokenKind.Number)
                return false;
            var text = t[s].Text;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                    return false;
                value = sign * hex;
                return true;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                return false;
            value = sign * d;
            return true;
        }

        // Qt.name(number, number, ...) over the whole range s..e
        private static bool TryQtCall(List<Token> t, int s, int e, out string name, out double[] args)
        {
            name = string.Empty;
            args = Array.Empty<double>();
            if (e - s < 5 || !t[s].IsIdent("Qt") || !t[s + 1].IsPunct('.') || t[s + 2].Kind != TokenKind.Ident
                || !t[s + 3].IsPunct('(') || !t[e - 1].IsPunct(')'))
                return false;
            name = t[s + 2].Text;
            var list = new List<double>();
            var from = s + 4;
            for (var j = s + 4; j < e; j++)
            {
                if (j == e - 1 || t[j].IsPunct(','))
                {
                    if (!TryNumber(t, from, j, out var d))
                        return false;
                    list.Add(d);
                    from = j + 1;
                }
            }
            args = list.ToArray();
            return true;
        }

        private static bool TryColor(string text, out Color4 color)
        {
            text = text.Trim();
            color = default;
            if (!text.StartsWith('#'))
                return s_namedColors.Value.TryGetValue(text.ToLowerInvariant(), out color);

            var hex = text.AsSpan(1);
            if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
                return false;
            switch (hex.Length)
            {
                case 3:
                    color = FromBytes(((v >> 8) & 0xF) * 17, ((v >> 4) & 0xF) * 17, (v & 0xF) * 17, 255);
                    return true;
                case 6:
                    color = FromBytes((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, 255);
                    return true;
                case 8:
                    color = FromBytes((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, v >> 24);
                    return true;
                default:
                    return false;
            }
        }

        private static Color4 FromBytes(uint r, uint g, uint b, uint a) => new(r / 255f, g / 255f, b / 255f, a / 255f);

        // SVG colour names (what QML accepts) from the named colours of Stride; "grey" spellings, transparent and rebeccapurple added.
        private static Dictionary<string, Color4> BuildNamedColors()
        {
            var map = new Dictionary<string, Color4>(StringComparer.Ordinal);

            void Add(string name, Color c)
            {
                var key = name.ToLowerInvariant();
                var color = new Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
                map[key] = color;
                if (key.Contains("gray", StringComparison.Ordinal))
                    map[key.Replace("gray", "grey", StringComparison.Ordinal)] = color;
            }

            foreach (var f in typeof(Color).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (f.FieldType == typeof(Color))
                    Add(f.Name, (Color)f.GetValue(null)!);
            foreach (var p in typeof(Color).GetProperties(BindingFlags.Public | BindingFlags.Static))
                if (p.PropertyType == typeof(Color) && p.GetIndexParameters().Length == 0)
                    Add(p.Name, (Color)p.GetValue(null)!);
            map.TryAdd("transparent", new Color4(0f, 0f, 0f, 0f));
            map.TryAdd("rebeccapurple", FromBytes(0x66, 0x33, 0x99, 255));
            return map;
        }
    }
}
