namespace VL.Qt.Qml
{
    /// <summary>How a property declaration is initialised; decides whether the pin default is known and when the value is sent.</summary>
    internal enum QmlInitializer
    {
        /// <summary>No initializer (<c>property real x</c>): pin default = <see cref="QmlTypes.TypeDefault"/>.</summary>
        None,
        /// <summary>
        /// A literal: number (int for real too), true/false, "…" or '…', a colour "#rgb"/"#rrggbb"/"#aarrggbb" or an SVG colour name,
        /// <c>Qt.rgba(r, g, b, a)</c>, <c>Qt.rgb(r, g, b)</c>, <c>Qt.vector2d/3d/4d(…)</c>, <c>Qt.point(x, y)</c>, <c>Qt.size(w, h)</c>, <c>Qt.rect(x, y, w, h)</c> with
        /// numeric literal arguments (unary minus allowed). Pin default = that value.
        /// </summary>
        Literal,
        /// <summary>
        /// Anything else (a binding, a JS expression, an object): pin default = <see cref="QmlTypes.TypeDefault"/>,
        /// QML keeps the binding until the patch changes the pin.
        /// </summary>
        Binding,
    }
}
