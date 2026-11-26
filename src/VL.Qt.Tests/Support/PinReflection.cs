using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Stride.Core.Mathematics;
using VL.Core.Import;
using VL.Model;
using VL.Qt.NodeFactories;

namespace VL.Qt.Tests
{
    /// <summary>The pin of a parameter of a node's <c>Update</c> as vvvv reads it, for tests that compare a table of pins with the node.</summary>
    internal static class PinReflection
    {
        public static string PinName(ParameterInfo p) => p.GetCustomAttribute<PinAttribute>()?.Name ?? PinNames.FromIdentifier(p.Name!);

        public static bool IsVisible(ParameterInfo p) => p.GetCustomAttribute<PinAttribute>()?.Visibility != PinVisibility.Optional;

        // The default as vvvv reads it: DefaultValue(object), DefaultValue(Type, string) or the IOBox string; default(T) without one.
        public static object? DefaultOf(ParameterInfo p)
        {
            var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            if (type == typeof(Matrix))
                return Matrix.Identity;
            if (p.GetCustomAttribute<DefaultValueAttribute>() is { } attribute)
            {
                if (attribute.Value is string text && type == typeof(Vector2))
                {
                    var xy = text.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                    return new Vector2(xy[0], xy[1]);
                }
                return attribute.Value;
            }
            return type.IsValueType ? System.Activator.CreateInstance(type) : null;
        }
    }
}
