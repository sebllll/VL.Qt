using VL.Lib.Collections;

namespace VL.Qt.Elements
{
    /// <summary>
    /// A signal emitted by the root of a QML file (a <c>signal clicked(int n)</c> declared in the file), as the QmlElement node reports it.
    /// </summary>
    /// <param name="Name">Name of the signal, e.g. "clicked".</param>
    /// <param name="Arguments">Arguments of the signal in the order of its declaration: float, int, bool, string, Color4,
    /// Vector2/3/4 or RectangleF; arguments of other QML types arrive as their string form.</param>
    public sealed record QmlSignal(string Name, Spread<object> Arguments);
}
