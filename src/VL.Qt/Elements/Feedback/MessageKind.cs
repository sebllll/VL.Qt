using System.Collections.Generic;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>What a message of a feedback slot is (<see cref="FeedbackState.SetMessage"/>).</summary>
    internal enum MessageKind
    {
        /// <summary>A warning shown at the element node.</summary>
        Warning,

        /// <summary>The failed Build of an <see cref="Styles.IStylable"/> child, shown at the parent's node.</summary>
        BuildError,
    }
}
