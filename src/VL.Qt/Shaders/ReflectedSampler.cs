using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using VL.Qt.Interop;
using VL.Qt.Wire;

namespace VL.Qt.Shaders
{
    /// <summary>One combined image sampler, raw from QtNative.</summary>
    internal sealed record ReflectedSampler(string Name, int Binding);
}
