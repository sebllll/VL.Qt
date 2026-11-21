using System;
using System.Collections.Generic;
using System.Text;
using VL.Qt.Wire;

namespace VL.Qt.Elements
{
    /// <summary>
    /// The Properties of a QmlElement as the node sends them: a snapshot of the converted dictionary entries by name.
    /// Immutable; the node keeps the same instance while its input dictionary is unchanged (a patch may mutate the dictionary
    /// in place, so <see cref="Update"/> compares the content), which is what the reconciler's reference check relies on.
    /// Entries that cannot be sent (unsupported type, invalid name) are left out and listed in <see cref="Problems"/>.
    /// </summary>
    internal sealed class QmlProperties
    {
        /// <summary>No properties.</summary>
        public static readonly QmlProperties Empty = new(new Dictionary<string, QmlValue>(StringComparer.Ordinal), 0, string.Empty);

        private readonly Dictionary<string, QmlValue> _values;
        private readonly int _invalid;

        private QmlProperties(Dictionary<string, QmlValue> values, int invalid, string problems)
        {
            _values = values;
            _invalid = invalid;
            Problems = problems;
        }

        /// <summary>Number of entries that are sent.</summary>
        public int Count => _values.Count;

        /// <summary>One line per entry that is left out, empty if there is none.</summary>
        public string Problems { get; }

        public bool TryGetValue(string name, out QmlValue value) => _values.TryGetValue(name, out value);

        /// <summary>Allocation-free enumeration (insertion order).</summary>
        public Dictionary<string, QmlValue>.Enumerator GetEnumerator() => _values.GetEnumerator();

        /// <summary>
        /// Returns <paramref name="current"/> if it still describes <paramref name="source"/> (no allocation), else a new snapshot.
        /// Null or empty source = <see cref="Empty"/>.
        /// </summary>
        public static QmlProperties Update(QmlProperties current, Dictionary<string, object>? source)
        {
            if (source is null || source.Count == 0)
                return Empty;
            return current.Matches(source) ? current : From(source);
        }

        /// <summary>Converts <paramref name="source"/> (always a new snapshot).</summary>
        public static QmlProperties From(Dictionary<string, object>? source)
        {
            if (source is null || source.Count == 0)
                return Empty;
            var values = new Dictionary<string, QmlValue>(source.Count, StringComparer.Ordinal);
            StringBuilder? problems = null;
            int invalid = 0;
            foreach (var (name, value) in source)
            {
                string problem;
                if (!IsValidName(name))
                    problem = $"Property name '{name}' is empty or longer than {Protocol.UniformNameMax} bytes, ignored";
                else if (QmlValue.TryConvert(value, out var converted))
                {
                    values[name] = converted;
                    continue;
                }
                else
                    problem = QmlValue.Describe(name, value);
                invalid++;
                (problems ??= new StringBuilder()).AppendLine(problem);
            }
            return new QmlProperties(values, invalid, problems is null ? string.Empty : problems.ToString().TrimEnd());
        }

        /// <summary>Whether <paramref name="name"/> can be sent (1..<see cref="Protocol.UniformNameMax"/> UTF-8 bytes).</summary>
        public static bool IsValidName(string? name) => UniformWriter.IsValidName(name);

        private bool Matches(Dictionary<string, object> source)
        {
            int valid = 0, invalid = 0;
            foreach (var (name, value) in source)
            {
                if (IsValidName(name) && QmlValue.TryConvert(value, out var converted))
                {
                    if (!_values.TryGetValue(name, out var mine) || mine != converted)
                        return false;
                    valid++;
                }
                else
                    invalid++;
            }
            return valid == _values.Count && invalid == _invalid;
        }
    }
}
