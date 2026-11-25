using System;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Lib.Collections;
using VL.Model;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds a Bézier path through anchors, shaped by a pair of handles per anchor.</summary>
    [ProcessNode(Name = "Bezier (Handles)", Category = Categories.Paths)]
    public sealed class BezierHandlesNode
    {
        internal readonly record struct Props(Spread<Vector2> Anchors, Spread<Vector2> InHandles, Spread<Vector2> OutHandles, Spread<HandleMode> HandleModes, bool Closed)
        {
            public bool Equals(Props other)
                => Closed == other.Closed
                    && SpreadEquality.SameContent(Anchors, other.Anchors)
                    && SpreadEquality.SameContent(InHandles, other.InHandles)
                    && SpreadEquality.SameContent(OutHandles, other.OutHandles)
                    && SpreadEquality.SameContent(HandleModes, other.HandleModes);

            public override int GetHashCode() => HashCode.Combine(Anchors.Count, InHandles.Count, OutHandles.Count, HandleModes.Count, Closed);
        }

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => BezierGeometry.Build(input ?? QtPath.Empty, p.Anchors.AsSpan(), p.InHandles.AsSpan(), p.OutHandles.AsSpan(), p.HandleModes.AsSpan(), p.Closed);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with a curve through the anchors, bent by the handles.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="anchors">The points the curve passes through, in units. Fewer than two anchors add nothing.</param>
        /// <param name="inHandles">The handle on the incoming side of each anchor, relative to the anchor. Missing entries count as zero (a corner).</param>
        /// <param name="outHandles">The handle on the outgoing side of each anchor, relative to the anchor. Missing entries count as zero (a corner).</param>
        /// <param name="closed">If true, the curve returns from the last anchor to the first.</param>
        /// <param name="handleModes">How the in handle follows the out handle per anchor (applied cyclically). Empty = Free for all.</param>
        /// <returns>The path with the curve added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            Spread<Vector2>? anchors,
            Spread<Vector2>? inHandles,
            Spread<Vector2>? outHandles,
            bool closed,
            [Pin(Visibility = PinVisibility.Optional)] Spread<HandleMode>? handleModes)
            => _memo.Get(input, new Props(anchors ?? Spread<Vector2>.Empty, inHandles ?? Spread<Vector2>.Empty, outHandles ?? Spread<Vector2>.Empty, handleModes ?? Spread<HandleMode>.Empty, closed), s_create);
    }
}
