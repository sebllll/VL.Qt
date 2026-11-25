using System;
using VL.Qt.Styles;

namespace VL.Qt.Nodes
{
    /// <summary>
    /// Remembers the last input style, the other inputs of a style node (as one value) and the style built from them,
    /// so the node returns the same reference while nothing changed (the reconciler skips unchanged widgets).
    /// </summary>
    internal struct StyleCache<TKey> where TKey : struct, IEquatable<TKey>
    {
        private Style? _input;
        private TKey _key;
        private Style? _output;

        /// <summary>The cached style if input and key equal the last call, else null.</summary>
        public readonly Style? Get(Style? input, in TKey key)
            => _output is not null && ReferenceEquals(input, _input) && _key.Equals(key) ? _output : null;

        public Style Store(Style? input, in TKey key, Style output)
        {
            _input = input;
            _key = key;
            return _output = output;
        }
    }
}
