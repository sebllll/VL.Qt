using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Qml
{
    /// <summary>
    /// QML value types that get a pin. The wire form is <see cref="QmlValue"/> of the QmlElement, so every
    /// kind maps to one CLR pin type and one <c>Protocol.ValueType</c> value type.
    /// </summary>
    internal enum QmlValueKind
    {
        /// <summary>QML <c>real</c> or <c>double</c> → float (wire f32).</summary>
        Real,
        /// <summary>QML <c>int</c> → int.</summary>
        Int,
        /// <summary>QML <c>bool</c> → bool.</summary>
        Bool,
        /// <summary>QML <c>string</c> → string.</summary>
        String,
        /// <summary>QML <c>color</c> → Stride Color4 (straight alpha).</summary>
        Color,
        /// <summary>QML <c>vector2d</c> → Stride Vector2.</summary>
        Vector2D,
        /// <summary>QML <c>vector3d</c> → Stride Vector3.</summary>
        Vector3D,
        /// <summary>QML <c>vector4d</c> → Stride Vector4.</summary>
        Vector4D,
        /// <summary>QML <c>rect</c> → Stride RectangleF (x, y, width, height).</summary>
        Rect,
        /// <summary>QML <c>point</c> → Stride Vector2 (x, y).</summary>
        Point,
        /// <summary>QML <c>size</c> → Stride Vector2 (width, height).</summary>
        Size,
    }
}
