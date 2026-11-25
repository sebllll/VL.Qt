using System;
using System.ComponentModel;
using Stride.Core.Mathematics;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Adds an arc of an oval as a new contour (Skia AddArc).</summary>
    [ProcessNode(Name = "AddArc", Category = Categories.Paths)]
    public sealed class AddArcNode
    {
        internal readonly record struct Props(RectangleF Bounds, float StartAngle, float SweepAngle);

        private static readonly Func<QtPath?, Props, QtPath> s_create = static (input, p) => ArcGeometry.AddArc(input ?? QtPath.Empty, p.Bounds, p.StartAngle, p.SweepAngle);

        private ChainMemo<QtPath, Props> _memo;

        /// <summary>Returns the path with an open contour that follows part of the outline of an oval.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that encloses the oval in units.</param>
        /// <param name="startAngle">The start of the arc in cycles; 0 is at the right middle of the oval, positive angles turn clockwise.</param>
        /// <param name="sweepAngle">The length of the arc in cycles; one cycle or more gives the full oval, 0 adds nothing.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, [DefaultValue("0, 0, 1, 1")] RectangleF bounds, float startAngle, [DefaultValue(0.25f)] float sweepAngle)
            => _memo.Get(input, new Props(bounds, startAngle, sweepAngle), s_create);
    }
}
