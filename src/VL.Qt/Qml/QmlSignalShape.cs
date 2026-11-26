using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Qml
{
    /// <summary>What the output pin of a declared signal carries, see <see cref="QmlTypes.SignalPinType"/>.</summary>
    internal enum QmlSignalShape
    {
        /// <summary>No arguments: bool, true in the frame the signal fired.</summary>
        Bang,
        /// <summary>One argument: Spread of its values, one per emission this frame.</summary>
        Values,
        /// <summary>Two or more arguments: Spread&lt;QmlSignal&gt;, one per emission this frame.</summary>
        Signals,
    }
}
