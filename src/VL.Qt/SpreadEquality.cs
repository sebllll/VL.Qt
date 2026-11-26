using System;
using System.Runtime.InteropServices;
using Stride.Core.Mathematics;
using VL.Lib.Collections;

namespace VL.Qt
{
    /// <summary>Content comparison of spreads: the one that change detection of nodes and the reconciler use.</summary>
    internal static class SpreadEquality
    {
        /// <summary>
        /// True if both spreads are the same instance or hold equal elements (<c>EqualityComparer&lt;T&gt;.Default</c>, so a
        /// <c>NaN</c> equals itself); a missing spread differs from every spread. No allocation.
        /// </summary>
        public static bool SameContent<T>(Spread<T>? a, Spread<T>? b)
            => ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b.AsSpan()));

        /// <summary>
        /// Same for points, compared as floats: Stride's <c>Vector2.Equals</c> has a tolerance and treats a <c>NaN</c> as different.
        /// </summary>
        public static bool SameContent(Spread<Vector2>? a, Spread<Vector2>? b)
            => ReferenceEquals(a, b)
                || (a is not null && b is not null && MemoryMarshal.Cast<Vector2, float>(a.AsSpan()).SequenceEqual(MemoryMarshal.Cast<Vector2, float>(b.AsSpan())));
    }
}
