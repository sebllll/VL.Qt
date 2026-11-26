using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Stride.Core.Mathematics;
using static VL.Qt.Qml.QmlLexer;

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
                default:
                    return false;
            }

            static bool Finite(double[] values) => values.All(static x => float.IsFinite((float)x));
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
