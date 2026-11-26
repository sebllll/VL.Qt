using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds the arc of an oval (Skia ArcTo, oval form).</summary>
    [ProcessNode(Name = "ArcTo", Category = Categories.Paths)]
    public sealed class ArcToOvalNode
    {
        internal readonly record struct Props(RectangleF Bounds, float StartAngle, float SweepAngle, bool ForceMoveTo);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.ArcTo(input ?? QtPath.Empty, p.Bounds, p.StartAngle, p.SweepAngle, p.ForceMoveTo);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an arc of the oval added. A line connects the last point to the start of the arc, unless a new
        /// contour is forced.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that bounds the oval, in units.</param>
        /// <param name="startAngle">Start of the arc in cycles (1 = full turn); 0 is the right end of the oval, positive angles go clockwise.</param>
        /// <param name="sweepAngle">Length of the arc in cycles; a sweep of one cycle or more gives a full turn.</param>
        /// <param name="forceMoveTo">If true, the arc starts a new contour instead of connecting to the last point.</param>
        /// <returns>The path with the arc added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(
            QtPath? input,
            RectangleF bounds,
            [Pin(Name = "Start Angle")] float startAngle,
            [Pin(Name = "Sweep Angle")] float sweepAngle,
            [Pin(Name = "Force Move To")] bool forceMoveTo)
            => _memo.Get(input, new Props(bounds, startAngle, sweepAngle, forceMoveTo), s_create);
    }
}
