using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// One value of a QmlElement property as it goes over the wire: wire value type and the value in plain fields, so
    /// value equality is exact (Stride's vector equality is a near-equal compare and would swallow tiny changes) and no boxing
    /// is left after the conversion. Color and rect use <c>A..D</c> (r, g, b, a / x, y, width, height).
    /// A list (<see cref="Protocol.ValueType.List"/>) keeps the patch's spread in <see cref="Items"/> (Spread&lt;T&gt; of float, int,
    /// bool, string, Color4, Vector2, Vector3, Vector4 or RectangleF, not copied) and the wire type of its elements in
    /// <see cref="Integer"/>; two lists are equal when their elements are, compared exactly like the scalars.
    /// </summary>
    internal readonly record struct QmlValue(ushort ValueType, float A, float B, float C, float D, int Integer, string? Text, object? Items = null)
    {
        /// <summary>
        /// Converts a patch value: float and double → real (f32), int, bool, string, Color4 → color, Vector2/3/4 → vector2d/3d/4d,
        /// RectangleF → rect, a Spread of one of float, int, bool, string, Color4, Vector2/3/4 or RectangleF → list.
        /// Anything else (also null) is not supported. No allocation.
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
                default: return TryConvertList(value, out result);
            }
        }

        /// <summary>
        /// A Spread of a supported element type → <see cref="Protocol.ValueType.List"/> with the element wire type in
        /// <see cref="Integer"/> and the spread itself in <see cref="Items"/>. Refuses more than
        /// <see cref="Protocol.ListValueMaxCount"/> elements. No allocation.
        /// </summary>
        private static bool TryConvertList(object? value, out QmlValue result)
        {
            if (!TryGetListInfo(value, out int elementType, out int count) || count > Protocol.ListValueMaxCount)
            {
                result = default;
                return false;
            }
            result = new QmlValue(Protocol.ValueType.List, 0, 0, 0, 0, elementType, null, value);
            return true;
        }

        /// <summary>Wire type of the elements and the element count of a Spread of a supported element type.</summary>
        private static bool TryGetListInfo(object? value, out int elementType, out int count)
        {
            switch (value)
            {
                case Spread<float> s: elementType = Protocol.ValueType.F32; count = s.Count; return true;
                case Spread<int> s: elementType = Protocol.ValueType.I32; count = s.Count; return true;
                case Spread<bool> s: elementType = Protocol.ValueType.Bool; count = s.Count; return true;
                case Spread<string> s: elementType = Protocol.ValueType.String; count = s.Count; return true;
                case Spread<Color4> s: elementType = Protocol.ValueType.Color; count = s.Count; return true;
                case Spread<Vector2> s: elementType = Protocol.ValueType.Vec2; count = s.Count; return true;
                case Spread<Vector3> s: elementType = Protocol.ValueType.Vec3; count = s.Count; return true;
                case Spread<Vector4> s: elementType = Protocol.ValueType.Vec4; count = s.Count; return true;
                case Spread<RectangleF> s: elementType = Protocol.ValueType.Rectf; count = s.Count; return true;
                default: elementType = 0; count = 0; return false;
            }
        }

        /// <summary>
        /// The line the node shows for a value <see cref="TryConvert"/> refused (value ignored). It depends on the name and the
        /// type of the value only, so it is not repeated for every new value of the same type.
        /// </summary>
        public static string Describe(string name, object? value)
        {
            if (value is null)
                return $"Property '{name}': the value is not set, ignored";
            if (TryGetListInfo(value, out _, out _))
                return $"Property '{name}': a list can have at most {Protocol.ListValueMaxCount} elements, ignored";
            return $"Property '{name}': type {TypeName(value.GetType())} is not supported, ignored "
                + "(use float, int, bool, string, Color4, Vector2/3/4 or RectangleF, or a Spread of one of them)";
        }

        private static string TypeName(Type type)
        {
            if (!type.IsGenericType)
                return type.Name;
            var arguments = type.GetGenericArguments();
            var names = new string[arguments.Length];
            for (int i = 0; i < names.Length; i++)
                names[i] = TypeName(arguments[i]);
            return $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", names)}>";
        }

        /// <summary>Exact equality of every field; <see cref="Items"/> by content (the same spread instance is equal at once).</summary>
        public bool Equals(QmlValue other)
            => ValueType == other.ValueType && A.Equals(other.A) && B.Equals(other.B) && C.Equals(other.C) && D.Equals(other.D)
                && Integer == other.Integer && string.Equals(Text, other.Text, StringComparison.Ordinal) && ItemsEqual(Items, other.Items);

        public override int GetHashCode() => HashCode.Combine(ValueType, A, B, C, D, Integer, Text, Items is null ? 0 : ItemsHash(Items));

        private static bool ItemsEqual(object? x, object? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            return x switch
            {
                Spread<float> a => y is Spread<float> b && FloatsEqual<float>(a.AsSpan(), b.AsSpan()),
                Spread<int> a => y is Spread<int> b && a.AsSpan().SequenceEqual(b.AsSpan()),
                Spread<bool> a => y is Spread<bool> b && a.AsSpan().SequenceEqual(b.AsSpan()),
                Spread<string> a => y is Spread<string> b && StringsEqual(a.AsSpan(), b.AsSpan()),
                Spread<Color4> a => y is Spread<Color4> b && FloatsEqual<Color4>(a.AsSpan(), b.AsSpan()),
                Spread<Vector2> a => y is Spread<Vector2> b && FloatsEqual<Vector2>(a.AsSpan(), b.AsSpan()),
                Spread<Vector3> a => y is Spread<Vector3> b && FloatsEqual<Vector3>(a.AsSpan(), b.AsSpan()),
                Spread<Vector4> a => y is Spread<Vector4> b && FloatsEqual<Vector4>(a.AsSpan(), b.AsSpan()),
                Spread<RectangleF> a => y is Spread<RectangleF> b && FloatsEqual<RectangleF>(a.AsSpan(), b.AsSpan()),
                _ => false,
            };
        }

        // These element types are plain runs of floats; Stride's own Equals is a near-equal compare, so the floats are compared.
        private static bool FloatsEqual<T>(ReadOnlySpan<T> x, ReadOnlySpan<T> y) where T : unmanaged
        {
            if (x.Length != y.Length)
                return false;
            var a = MemoryMarshal.Cast<T, float>(x);
            var b = MemoryMarshal.Cast<T, float>(y);
            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        private static bool StringsEqual(ReadOnlySpan<string> x, ReadOnlySpan<string> y)
        {
            if (x.Length != y.Length)
                return false;
            for (int i = 0; i < x.Length; i++)
            {
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static int ItemsHash(object items)
        {
            var hash = new HashCode();
            switch (items)
            {
                case Spread<float> s: HashFloats<float>(ref hash, s.AsSpan()); break;
                case Spread<int> s: hash.Add(s.Count); foreach (var v in s.AsSpan()) hash.Add(v); break;
                case Spread<bool> s: hash.Add(s.Count); foreach (var v in s.AsSpan()) hash.Add(v); break;
                case Spread<string> s: hash.Add(s.Count); foreach (var v in s.AsSpan()) hash.Add(v, StringComparer.Ordinal); break;
                case Spread<Color4> s: HashFloats<Color4>(ref hash, s.AsSpan()); break;
                case Spread<Vector2> s: HashFloats<Vector2>(ref hash, s.AsSpan()); break;
                case Spread<Vector3> s: HashFloats<Vector3>(ref hash, s.AsSpan()); break;
                case Spread<Vector4> s: HashFloats<Vector4>(ref hash, s.AsSpan()); break;
                case Spread<RectangleF> s: HashFloats<RectangleF>(ref hash, s.AsSpan()); break;
            }
            return hash.ToHashCode();
        }

        private static void HashFloats<T>(ref HashCode hash, ReadOnlySpan<T> items) where T : unmanaged
        {
            hash.Add(items.Length);
            foreach (var f in MemoryMarshal.Cast<T, float>(items))
                hash.Add(f);
        }
    }
}
