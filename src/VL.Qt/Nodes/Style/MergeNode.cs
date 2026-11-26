using VL.Core.Import;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>Lays Input 2 over Input: field by field and state by state, a set value of Input 2 wins (the rule of Group and widget).</summary>
    [ProcessNode(Name = "Merge", Category = "Qt.Style")]
    public sealed class MergeNode
    {
        private Style? _input;
        private Style? _input2;
        private Style? _output;

        /// <param name="input">Lower style, e.g. a preset.</param>
        /// <param name="input2">Upper style, e.g. overrides from another branch.</param>
        /// <returns>The merged style. The same style as in the last frame while no input changed.</returns>
        [return: Pin(Name = "Output")]
        public Style Update(Style? input, [Pin(Name = "Input 2")] Style? input2)
        {
            if (_output is null || !ReferenceEquals(input, _input) || !ReferenceEquals(input2, _input2))
            {
                _input = input;
                _input2 = input2;
                _output = StyleMerge.Merge(input, input2);
            }
            return _output;
        }
    }
}
