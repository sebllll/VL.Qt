using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>
    /// One member of the uniform block, raw from QtNative. Type is the GLSL name as written by vlqt_shader_reflect
    /// ("float", "vec4", "mat4", ..., "other" for anything else); ArrayLength 0 = no array.
    /// </summary>
    internal sealed record ReflectedUniform(string Name, string Type, int ArrayLength);
}
