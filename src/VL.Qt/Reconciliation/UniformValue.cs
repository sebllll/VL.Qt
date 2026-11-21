using Stride.Core.Mathematics;

namespace VL.Qt.Reconciliation
{
    /// <summary>
    /// One uniform as QtNative holds it: wire value type, the value and, for samplers, the resolved id (element
    /// child id for a texture, image resource id for an image). Value equality decides what is sent again.
    /// </summary>
    internal readonly record struct UniformValue(ushort ValueType, Matrix Numbers, int Integer, ulong Handle);
}
