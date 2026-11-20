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
        private QtPath? _input;
        private RectangleF _bounds;
        private float _startAngle;
        private float _sweepAngle;
        private QtPath? _output;

        /// <summary>Returns the path with an open contour that follows part of the outline of an oval.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="bounds">The rectangle that encloses the oval in units.</param>
        /// <param name="startAngle">The start of the arc in cycles; 0 is at the right middle of the oval, positive angles turn clockwise.</param>
        /// <param name="sweepAngle">The length of the arc in cycles; one cycle or more gives the full oval, 0 adds nothing.</param>
        /// <returns>The path with the contour added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, RectangleF bounds = default, float startAngle = 0f, float sweepAngle = 0f)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || bounds != _bounds || startAngle != _startAngle || sweepAngle != _sweepAngle)
            {
                _input = input;
                _bounds = bounds;
                _startAngle = startAngle;
                _sweepAngle = sweepAngle;
                _output = ArcGeometry.AddArc(input, bounds, startAngle, sweepAngle);
            }
            return _output;
        }
    }
}
