using System;
using System.Collections.Generic;
using Stride.Core.Mathematics;

namespace VL.Qt.Paths
{
    /// <summary>
    /// Bézier paths from anchors with handles for <see cref="QtPath"/>. Handles are relative to their anchor.
    /// Segment i is the cubic from anchor i with the controls anchor i + out i and anchor i+1 + in i+1 to anchor
    /// i+1; a closed path adds the segment back to anchor 0 and a Close verb.
    /// </summary>
    internal static class BezierGeometry
    {
        /// <summary>
        /// Appends a contour through <paramref name="anchors"/> to <paramref name="path"/>. Entries missing in
        /// <paramref name="inHandles"/> or <paramref name="outHandles"/> (shorter lists) count as zero, which makes a
        /// corner. <paramref name="modes"/> are applied cyclically (anchor i uses entry i mod count); an empty list
        /// means <see cref="HandleMode.Free"/> for all. With fewer than two anchors the input path is returned.
        /// </summary>
        internal static QtPath Build(
            QtPath path,
            IReadOnlyList<Vector2> anchors,
            IReadOnlyList<Vector2> inHandles,
            IReadOnlyList<Vector2> outHandles,
            IReadOnlyList<HandleMode> modes,
            bool closed)
        {
            var n = anchors.Count;
            if (n < 2)
                return path;

            var first = anchors[0];
            path = path.MoveTo(first.X, first.Y);
            var segments = closed ? n : n - 1;
            for (var i = 0; i < segments; i++)
            {
                var j = (i + 1) % n;
                var a = anchors[i];
                var b = anchors[j];
                var c1 = a + OutHandle(outHandles, i);
                var c2 = b + InHandle(inHandles, outHandles, modes, j);
                path = path.CubicTo(c1.X, c1.Y, c2.X, c2.Y, b.X, b.Y);
            }

            return closed ? path.Close() : path;
        }

        static Vector2 OutHandle(IReadOnlyList<Vector2> outHandles, int i) =>
            i < outHandles.Count ? outHandles[i] : Vector2.Zero;

        static Vector2 InHandle(IReadOnlyList<Vector2> inHandles, IReadOnlyList<Vector2> outHandles, IReadOnlyList<HandleMode> modes, int i)
        {
            var mode = modes.Count == 0 ? HandleMode.Free : modes[i % modes.Count];
            var given = i < inHandles.Count ? inHandles[i] : Vector2.Zero;
            switch (mode)
            {
                case HandleMode.Mirrored:
                    return -OutHandle(outHandles, i);
                case HandleMode.Aligned:
                    var o = OutHandle(outHandles, i);
                    var length = o.Length();
                    // Without a direction the handle stays as given (no division by zero).
                    return length > 0 ? o * (-given.Length() / length) : given;
                default:
                    return given;
            }
        }
    }
}
