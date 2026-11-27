using System.Collections.Immutable;
using System.Linq;

namespace VL.Qt.Qml
{
    /// <summary>
    /// An <c>enum Name { A, B = 5 }</c> of a root object (of the file or of a sibling root type). A property of its type, or with one
    /// of its values as literal (<c>Gauge.Small</c>), gets an int pin whose help lists the values.
    /// </summary>
    /// <param name="Type">Type name of the file that declares it ("Gauge"); a value is written <c>Gauge.Small</c> or <c>Gauge.Mode.Small</c>.</param>
    /// <param name="Name">Name of the enum ("Mode").</param>
    /// <param name="Values">The values in declaration order.</param>
    internal sealed record QmlEnumDecl(string Type, string Name, ImmutableArray<QmlEnumValue> Values)
    {
        /// <summary>Returns true if the enum has a value of this name.</summary>
        public bool TryGet(string name, out int value)
        {
            foreach (var v in Values)
            {
                if (v.Name == name)
                {
                    value = v.Value;
                    return true;
                }
            }
            value = 0;
            return false;
        }

        /// <summary>"Mode: Small = 0, Large = 5", for the help of the pin.</summary>
        public string Describe() => $"{Name}: " + string.Join(", ", Values.Select(static v => $"{v.Name} = {v.Value}"));
    }
}
