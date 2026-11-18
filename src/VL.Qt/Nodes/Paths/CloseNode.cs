using System;
using System.ComponentModel;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes.Paths
{
    /// <summary>Closes the current contour with a line to its start (Skia Close; Skia has no relative variant).</summary>
    [ProcessNode(Name = "Close", Category = Categories.Paths)]
    public sealed class CloseNode
    {
        QtPath? _input;
        QtPath? _output;

        /// <summary>Returns the path with the current contour closed.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <returns>The path with the segment added; the same instance as long as the input does not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input)
        {
            input ??= QtPath.Empty;
            if (_output == null || !ReferenceEquals(input, _input))
            {
                _input = input;
                _output = input.Close();
            }
            return _output;
        }
    }
}
