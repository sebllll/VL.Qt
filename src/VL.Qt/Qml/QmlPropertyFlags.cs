using System;

namespace VL.Qt.Qml
{
    /// <summary>Modifiers of a property declaration.</summary>
    [Flags]
    internal enum QmlPropertyFlags
    {
        /// <summary>A plain <c>property</c>.</summary>
        None = 0,
        /// <summary><c>readonly property</c>: output pin only.</summary>
        ReadOnly = 1,
        /// <summary><c>required property</c>: its input is always sent.</summary>
        Required = 2,
        /// <summary><c>default property</c>: no effect on the pins.</summary>
        Default = 4,
    }
}
