using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    /// <summary>Adds the segments of another path (Skia AddPath).</summary>
    [ProcessNode(Name = "AddPath", Category = Categories.Paths)]
    public sealed class AddPathNode
    {
        QtPath? _input;
        QtPath? _other;
        PathAddMode _mode;
        QtPath? _output;

        /// <summary>Returns the path with the segments of the other path added.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <param name="other">The path to add. Nothing = the empty path, which adds nothing.</param>
        /// <param name="mode">Append adds the contours of the other path as new contours; Extend turns its first move into a line, so that the first contour continues the last contour of the input.</param>
        /// <returns>The path with the segments added; the same instance as long as the inputs do not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input, QtPath? other, PathAddMode mode = PathAddMode.Append)
        {
            input ??= QtPath.Empty;
            other ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input) || !ReferenceEquals(other, _other) || mode != _mode)
            {
                _input = input;
                _other = other;
                _mode = mode;
                _output = Build(input, other, mode);
            }
            return _output;
        }

        static QtPath Build(QtPath path, QtPath other, PathAddMode mode)
        {
            if (other.IsEmpty)
                return path;
            var flat = other.Flatten();
            if (flat.Verbs[0] == Protocol.Verb.Move)
            {
                if (mode == PathAddMode.Extend && !path.IsEmpty)
                    return ExtendWith(path, flat);
                return path.Append(other);
            }

            // The added path does not start with a Move. Extend continues the last contour as it is. Append starts a
            // contour at the last point of the input (the origin for an empty input), so the added segments have a start.
            return mode == PathAddMode.Extend ? path.Append(other) : path.MoveTo(path.LastPoint.X, path.LastPoint.Y).Append(other);
        }

        // Replays the segments of the added path one by one, its first Move as a Line (a join of the two paths cannot change a segment).
        static QtPath ExtendWith(QtPath path, FlatPath flat)
        {
            var verbs = flat.Verbs;
            var p = flat.Points;
            var i = 0;
            for (var v = 0; v < verbs.Length; v++)
            {
                switch (verbs[v])
                {
                    case Protocol.Verb.Move:
                        path = v == 0 ? path.LineTo(p[i], p[i + 1]) : path.MoveTo(p[i], p[i + 1]);
                        i += 2;
                        break;
                    case Protocol.Verb.Line:
                        path = path.LineTo(p[i], p[i + 1]);
                        i += 2;
                        break;
                    case Protocol.Verb.Quad:
                        path = path.QuadTo(p[i], p[i + 1], p[i + 2], p[i + 3]);
                        i += 4;
                        break;
                    case Protocol.Verb.Cubic:
                        path = path.CubicTo(p[i], p[i + 1], p[i + 2], p[i + 3], p[i + 4], p[i + 5]);
                        i += 6;
                        break;
                    default:
                        path = path.Close();
                        break;
                }
            }
            return path;
        }
    }
}
