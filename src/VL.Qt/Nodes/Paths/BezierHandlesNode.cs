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
        private QtPath? _input;
        private Spread<Vector2>? _anchors;
        private Spread<Vector2>? _inHandles;
        private Spread<Vector2>? _outHandles;
        private Spread<HandleMode>? _handleModes;
        private bool _closed;
        private QtPath? _output;

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
            bool closed = false,
            [Pin(Visibility = PinVisibility.Optional)] Spread<HandleMode>? handleModes = null)
        {
            input ??= QtPath.Empty;
            anchors ??= Spread<Vector2>.Empty;
            inHandles ??= Spread<Vector2>.Empty;
            outHandles ??= Spread<Vector2>.Empty;
            handleModes ??= Spread<HandleMode>.Empty;
            // A new spread with the same content (VL may rebuild it every frame) is not a change.
            if (_output == null || !ReferenceEquals(input, _input) || closed != _closed
                || !ShapeNodeCore.SameContent(anchors, _anchors!)
                || !ShapeNodeCore.SameContent(inHandles, _inHandles!)
                || !ShapeNodeCore.SameContent(outHandles, _outHandles!)
                || !SameModes(handleModes, _handleModes))
            {
                _input = input;
                _anchors = anchors;
                _inHandles = inHandles;
                _outHandles = outHandles;
                _handleModes = handleModes;
                _closed = closed;
                _output = BezierGeometry.Build(input, anchors, inHandles, outHandles, handleModes, closed);
            }
            return _output;
        }

        private static bool SameModes(Spread<HandleMode> a, Spread<HandleMode>? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (b == null || a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }
    }
}
