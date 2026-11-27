using System;
using Stride.Core.Mathematics;
using VL.Lib.Collections;
using VL.Qt.Elements;
using VL.Qt.Wire;

namespace VL.Qt.Qml
{
    /// <summary>
    /// QML value types that get a pin. The wire form is <see cref="QmlValue"/> of the QmlElement, so every
    /// kind maps to one CLR pin type and one <c>Protocol.ValueType</c> value type. A <c>list&lt;T&gt;</c> kind maps to
    /// <c>Spread</c> of the pin type of <c>T</c> and to <c>Protocol.ValueType.List</c> with the value type of <c>T</c> as element
    /// type; <see cref="QmlTypes.ElementKind"/> and <see cref="QmlTypes.ListKind"/> convert between the two.
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
        /// <summary>QML <c>url</c> → Path (wire string; a local file is a path, any other URL its text).</summary>
        Url,
        /// <summary>QML <c>date</c> → DateTime (local time; DateTime.MinValue = no date).</summary>
        Date,
        /// <summary>QML <c>font</c> → Font (size in units). The one kind without a list kind.</summary>
        Font,
        // The list kinds follow the element kinds in the same order (Font, the last element kind, has none): QmlTypes.ElementKind and
        // ListKind count on it.
        /// <summary>QML <c>list&lt;real&gt;</c> or <c>list&lt;double&gt;</c> → Spread&lt;float&gt;.</summary>
        RealList,
        /// <summary>QML <c>list&lt;int&gt;</c> → Spread&lt;int&gt;.</summary>
        IntList,
        /// <summary>QML <c>list&lt;bool&gt;</c> → Spread&lt;bool&gt;.</summary>
        BoolList,
        /// <summary>QML <c>list&lt;string&gt;</c> → Spread&lt;string&gt;.</summary>
        StringList,
        /// <summary>QML <c>list&lt;color&gt;</c> → Spread&lt;Color4&gt;.</summary>
        ColorList,
        /// <summary>QML <c>list&lt;vector2d&gt;</c> → Spread&lt;Vector2&gt;.</summary>
        Vector2DList,
        /// <summary>QML <c>list&lt;vector3d&gt;</c> → Spread&lt;Vector3&gt;.</summary>
        Vector3DList,
        /// <summary>QML <c>list&lt;vector4d&gt;</c> → Spread&lt;Vector4&gt;.</summary>
        Vector4DList,
        /// <summary>QML <c>list&lt;rect&gt;</c> → Spread&lt;RectangleF&gt;.</summary>
        RectList,
        /// <summary>QML <c>list&lt;point&gt;</c> → Spread&lt;Vector2&gt; (x, y).</summary>
        PointList,
        /// <summary>QML <c>list&lt;size&gt;</c> → Spread&lt;Vector2&gt; (width, height).</summary>
        SizeList,
        /// <summary>QML <c>list&lt;url&gt;</c> → Spread&lt;Path&gt;.</summary>
        UrlList,
        /// <summary>QML <c>list&lt;date&gt;</c> → Spread&lt;DateTime&gt;.</summary>
        DateList,
    }
}
