using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using Stride.Core.Mathematics;
using VL.Qt.Wire;

namespace VL.Qt.Paths
{
    /// <summary>
    /// Flat, ready-to-send form of a <see cref="QtPath"/>: verbs (<see cref="Protocol.Verb"/>) and x,y pairs in the
    /// units of the path. Immutable, shared between all readers of the path.
    /// </summary>
    internal sealed class FlatPath
    {
        internal static readonly FlatPath Empty = new FlatPath(ImmutableArray<byte>.Empty, ImmutableArray<float>.Empty, default);

        internal FlatPath(ImmutableArray<byte> verbs, ImmutableArray<float> points, RectangleF bounds)
        {
            Verbs = verbs;
            Points = points;
            Bounds = bounds;
        }

        /// <summary>One <see cref="Protocol.Verb"/> per segment.</summary>
        internal ImmutableArray<byte> Verbs { get; }

        /// <summary>x,y pairs of all segments in order (Move 1, Line 1, Quad 2, Cubic 3, Close 0 pairs).</summary>
        internal ImmutableArray<float> Points { get; }

        /// <summary>Bounding box of all points, control points included (zero rectangle for an empty path).</summary>
        internal RectangleF Bounds { get; }
    }
}
