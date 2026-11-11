using System;
using System.Collections.Generic;
using System.Text;
using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// One value of a QmlElement property as it goes over the wire (#104): wire value type and the value in plain fields, so
    /// value equality is exact (Stride's vector equality is a near-equal compare and would swallow tiny changes) and no boxing
    /// is left after the conversion. Color and rect use <c>A..D</c> (r, g, b, a / x, y, width, height).
    /// </summary>
    internal readonly record struct QmlValue(ushort ValueType, float A, float B, float C, float D, int Integer, string? Text)
    {
        /// <summary>Longest string in chars that is sent (UTF-8 takes at most 3 bytes per char, a command carries 64 KB).</summary>
        public const int MaxStringChars = 20000;

        /// <summary>
        /// Converts a patch value: float and double → real (f32), int, bool, string, Color4 → color, Vector2/3/4 → vector2d/3d/4d,
        /// RectangleF → rect. Anything else (also null and a string longer than <see cref="MaxStringChars"/>) is not supported.
        /// No allocation.
        /// </summary>
        public static bool TryConvert(object? value, out QmlValue result)
        {
            switch (value)
            {
                case float f: result = new QmlValue(Protocol.Vt.F32, f, 0, 0, 0, 0, null); return true;
                case double d: result = new QmlValue(Protocol.Vt.F32, (float)d, 0, 0, 0, 0, null); return true;
                case int i: result = new QmlValue(Protocol.Vt.I32, 0, 0, 0, 0, i, null); return true;
                case bool b: result = new QmlValue(Protocol.Vt.Bool, 0, 0, 0, 0, b ? 1 : 0, null); return true;
                case string s when s.Length <= MaxStringChars: result = new QmlValue(Protocol.Vt.String, 0, 0, 0, 0, 0, s); return true;
                case Color4 c: result = new QmlValue(Protocol.Vt.Color, c.R, c.G, c.B, c.A, 0, null); return true;
                case Vector2 v2: result = new QmlValue(Protocol.Vt.Vec2, v2.X, v2.Y, 0, 0, 0, null); return true;
                case Vector3 v3: result = new QmlValue(Protocol.Vt.Vec3, v3.X, v3.Y, v3.Z, 0, 0, null); return true;
                case Vector4 v4: result = new QmlValue(Protocol.Vt.Vec4, v4.X, v4.Y, v4.Z, v4.W, 0, null); return true;
                case RectangleF r: result = new QmlValue(Protocol.Vt.Rectf, r.X, r.Y, r.Width, r.Height, 0, null); return true;
                default: result = default; return false;
            }
        }

        /// <summary>The line the node shows for a value <see cref="TryConvert"/> refused (value ignored).</summary>
        public static string Describe(string name, object? value) => value switch
        {
            null => $"Property '{name}': the value is not set, ignored",
            string => $"Property '{name}': the string is longer than {MaxStringChars} characters, ignored",
            _ => $"Property '{name}': type {value.GetType().Name} is not supported, ignored (use float, int, bool, string, Color4, Vector2/3/4 or RectangleF)",
        };
    }

    /// <summary>
    /// The Properties of a QmlElement as the node sends them: a snapshot of the converted dictionary entries by name (#104).
    /// Immutable; the node keeps the same instance while its input dictionary is unchanged (a patch may mutate the dictionary
    /// in place, so <see cref="Update"/> compares the content), which is what the reconciler's reference check relies on.
    /// Entries that cannot be sent (unsupported type, invalid name) are left out and listed in <see cref="Problems"/>.
    /// </summary>
    internal sealed class QmlProperties
    {
        /// <summary>No properties.</summary>
        public static readonly QmlProperties Empty = new(new Dictionary<string, QmlValue>(StringComparer.Ordinal), 0, string.Empty);

        readonly Dictionary<string, QmlValue> _values;
        readonly int _invalid;

        QmlProperties(Dictionary<string, QmlValue> values, int invalid, string problems)
        {
            _values = values;
            _invalid = invalid;
            Problems = problems;
        }

        /// <summary>Number of entries that are sent.</summary>
        public int Count => _values.Count;

        /// <summary>One line per entry that is left out, empty if there is none.</summary>
        public string Problems { get; }

        public bool TryGetValue(string name, out QmlValue value) => _values.TryGetValue(name, out value);

        /// <summary>Allocation-free enumeration (insertion order).</summary>
        public Dictionary<string, QmlValue>.Enumerator GetEnumerator() => _values.GetEnumerator();

        /// <summary>
        /// Returns <paramref name="current"/> if it still describes <paramref name="source"/> (no allocation), else a new snapshot.
        /// Null or empty source = <see cref="Empty"/>.
        /// </summary>
        public static QmlProperties Update(QmlProperties current, Dictionary<string, object>? source)
        {
            if (source is null || source.Count == 0)
                return Empty;
            return current.Matches(source) ? current : From(source);
        }

        /// <summary>Converts <paramref name="source"/> (always a new snapshot).</summary>
        public static QmlProperties From(Dictionary<string, object>? source)
        {
            if (source is null || source.Count == 0)
                return Empty;
            var values = new Dictionary<string, QmlValue>(source.Count, StringComparer.Ordinal);
            StringBuilder? problems = null;
            int invalid = 0;
            foreach (var (name, value) in source)
            {
                string problem;
                if (!IsValidName(name))
                    problem = $"Property name '{name}' is empty or longer than {Protocol.UniformNameMax} bytes, ignored";
                else if (QmlValue.TryConvert(value, out var converted))
                {
                    values[name] = converted;
                    continue;
                }
                else
                    problem = QmlValue.Describe(name, value);
                invalid++;
                (problems ??= new StringBuilder()).AppendLine(problem);
            }
            return new QmlProperties(values, invalid, problems is null ? string.Empty : problems.ToString().TrimEnd());
        }

        /// <summary>Whether <paramref name="name"/> can be sent (1..<see cref="Protocol.UniformNameMax"/> UTF-8 bytes).</summary>
        public static bool IsValidName(string? name) => UniformWriter.IsValidName(name);

        bool Matches(Dictionary<string, object> source)
        {
            int valid = 0, invalid = 0;
            foreach (var (name, value) in source)
            {
                if (IsValidName(name) && QmlValue.TryConvert(value, out var converted))
                {
                    if (!_values.TryGetValue(name, out var mine) || mine != converted)
                        return false;
                    valid++;
                }
                else
                    invalid++;
            }
            return valid == _values.Count && invalid == _invalid;
        }
    }
}
