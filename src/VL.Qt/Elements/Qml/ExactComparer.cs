using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt.Elements
{
    /// <summary>
    /// Exact equality of the values a QML property takes: Stride's Equals of Vector2/3/4 and RectangleF is a near-equal compare and
    /// would swallow tiny changes, so these and Color4 are compared as runs of floats with <see cref="float.Equals(float)"/>
    /// (NaN equals NaN, so a NaN value is not sent again every frame). A Spread is equal when its elements are, compared the same way.
    /// Every other type uses its default equality.
    /// </summary>
    internal static class ExactComparer
    {
        /// <summary>Returns the exact comparer of <typeparamref name="T"/>; one instance per type, no allocation per call.</summary>
        public static IEqualityComparer<T> For<T>() => Cache<T>.Instance;

        /// <summary>Returns true if both spans hold the same floats; the element types are plain runs of floats.</summary>
        public static bool FloatsEqual<T>(ReadOnlySpan<T> x, ReadOnlySpan<T> y) where T : unmanaged
        {
            if (x.Length != y.Length)
                return false;
            var a = MemoryMarshal.Cast<T, float>(x);
            var b = MemoryMarshal.Cast<T, float>(y);
            for (var i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }
            return true;
        }

        /// <summary>Adds the length and every float of <paramref name="items"/>, matching <see cref="FloatsEqual"/>.</summary>
        public static void HashFloats<T>(ref HashCode hash, ReadOnlySpan<T> items) where T : unmanaged
        {
            hash.Add(items.Length);
            foreach (var f in MemoryMarshal.Cast<T, float>(items))
                hash.Add(f);
        }

        private static IEqualityComparer<T> Create<T>()
        {
            var type = typeof(T);
            if (type == typeof(Color4) || type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(RectangleF))
                return (IEqualityComparer<T>)Activator.CreateInstance(typeof(Floats<>).MakeGenericType(type))!;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Spread<>))
                return (IEqualityComparer<T>)Activator.CreateInstance(typeof(Spreads<>).MakeGenericType(type.GetGenericArguments()[0]))!;
            return EqualityComparer<T>.Default;
        }

        private static class Cache<T>
        {
            public static readonly IEqualityComparer<T> Instance = Create<T>();
        }

        private sealed class Floats<T> : IEqualityComparer<T> where T : unmanaged
        {
            public bool Equals(T x, T y) => FloatsEqual(new ReadOnlySpan<T>(in x), new ReadOnlySpan<T>(in y));

            public int GetHashCode(T obj)
            {
                var hash = new HashCode();
                HashFloats(ref hash, new ReadOnlySpan<T>(in obj));
                return hash.ToHashCode();
            }
        }

        private sealed class Spreads<TItem> : IEqualityComparer<Spread<TItem>>
        {
            private readonly IEqualityComparer<TItem> _items = For<TItem>();

            // The same instance is equal without a walk over its elements.
            public bool Equals(Spread<TItem>? x, Spread<TItem>? y)
            {
                if (ReferenceEquals(x, y))
                    return true;
                if (x is null || y is null || x.Count != y.Count)
                    return false;
                var a = x.AsSpan();
                var b = y.AsSpan();
                for (var i = 0; i < a.Length; i++)
                {
                    if (!_items.Equals(a[i], b[i]))
                        return false;
                }
                return true;
            }

            public int GetHashCode(Spread<TItem> obj) => obj.Count;
        }
    }
}
