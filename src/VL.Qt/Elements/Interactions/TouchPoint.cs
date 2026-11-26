using Stride.Core.Mathematics;

namespace VL.Qt.Elements
{
    /// <summary>
    /// One finger (or pen, or mouse) pressed on an element, as reported by the Touch Points node.
    /// </summary>
    /// <param name="Id">Id of the point, stable from the press until the release (usually the device's id; unique across
    /// devices, so a pen and a finger at the same time get different ids; may be reused for a later touch).</param>
    /// <param name="Position">Position local to the element in units.</param>
    /// <param name="Pressure">Pressure 0..1. Device dependent: many touchscreens always report 0.5.</param>
    /// <param name="Size">Contact area (ellipse diameters) in units. Device dependent: many touchscreens report 0 x 0.</param>
    public readonly record struct TouchPoint(int Id, Vector2 Position, float Pressure, Vector2 Size);
}
