using System;
using System.ComponentModel;
using System.Reactive;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Paths;

namespace VL.Qt.Nodes
{
    /// <summary>Closes the current contour with a line to its start (Skia Close; Skia has no relative variant).</summary>
    [ProcessNode(Name = "Close", Category = Categories.Paths)]
    public sealed class CloseNode
    {
        private static readonly Func<QtPath?, Unit, QtPath> s_create = static (input, _) => (input ?? QtPath.Empty).Close();

        private ChainMemo<QtPath, Unit> _memo;

        /// <summary>Returns the path with the current contour closed.</summary>
        /// <param name="input">The path to extend. Nothing = the empty path.</param>
        /// <returns>The path with the segment added; the same instance as long as the input does not change.</returns>
        [return: Pin(Name = "Output")]
        public QtPath Update(QtPath? input)
            => _memo.Get(input, default, s_create);
    }
}
