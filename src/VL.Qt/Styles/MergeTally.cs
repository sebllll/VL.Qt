using System;
using System.Collections.Generic;
using System.Text;
using VL.Core;
using VL.Core.Import;
using VL.Qt.Wire;

namespace VL.Qt.Styles
{
    /// <summary>
    /// Counts per field of a part merge whether the result equals the lower or the upper part, so that the merge can
    /// return one of them unchanged. A field that does not go through <see cref="Pick"/> is missing in the result
    /// and is noticed by the per-field merge test.
    /// </summary>
    internal struct MergeTally
    {
        private int _fields;
        private int _equalUnder;
        private int _equalOver;

        /// <summary><see cref="StateValues{T}.Merge"/> of one field, counted.</summary>
        public StateValues<T> Pick<T>(in StateValues<T> under, in StateValues<T> over)
        {
            var m = StateValues<T>.Merge(under, over);
            _fields++;
            if (m == under)
                _equalUnder++;
            if (m == over)
                _equalOver++;
            return m;
        }

        /// <summary>True if every field of the result equals the lower part.</summary>
        public bool AllUnder => _equalUnder == _fields;

        /// <summary>True if every field of the result equals the upper part.</summary>
        public bool AllOver => _equalOver == _fields;
    }
}
