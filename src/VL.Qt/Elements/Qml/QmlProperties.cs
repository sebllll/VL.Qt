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
        public static readonly QmlProperties Empty = new(new Dictionary<string, QmlValue>(StringComparer.Ordinal), Array.Empty<Rejected>(), string.Empty);

        private readonly Dictionary<string, QmlValue> _values;
        private readonly Rejected[] _rejected;

        private QmlProperties(Dictionary<string, QmlValue> values, Rejected[] rejected, string problems)
        {
            _values = values;
            _rejected = rejected;
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
            List<Rejected>? rejected = null;
            foreach (var (name, value) in source)
            {
                string problem;
                if (!Protocol.IsValidName(name))
                    problem = $"Property name '{name}' is empty or longer than {Protocol.UniformNameMax} bytes, ignored";
                else if (QmlValue.TryConvert(value, out var converted))
                {
                    values[name] = converted;
                    continue;
                }
                else
                    problem = QmlValue.Describe(name, value);
                (rejected ??= new List<Rejected>()).Add(new Rejected(name, RejectedType(name, value)));
                (problems ??= new StringBuilder()).AppendLine(problem);
            }
            return new QmlProperties(values, rejected is null ? Array.Empty<Rejected>() : rejected.ToArray(), problems is null ? string.Empty : problems.ToString().TrimEnd());
        }

        private bool Matches(Dictionary<string, object> source)
        {
            int valid = 0, rejected = 0;
            foreach (var (name, value) in source)
            {
                if (Protocol.IsValidName(name) && QmlValue.TryConvert(value, out var converted))
                {
                    if (!_values.TryGetValue(name, out var mine) || mine != converted)
                        return false;
                    valid++;
                }
                else
                {
                    // The problem text depends on name and type only, so the same pair keeps the snapshot.
                    if (!IsRejected(name, RejectedType(name, value)))
                        return false;
                    rejected++;
                }
            }
            return valid == _values.Count && rejected == _rejected.Length;
        }

        /// <summary>The part of a refused value its problem text depends on: null for an invalid name (its text names the name only) and for a null value.</summary>
        private static Type? RejectedType(string name, object? value) => Protocol.IsValidName(name) ? value?.GetType() : null;

        // A linear search: the list is almost always empty, and nothing is allocated.
        private bool IsRejected(string name, Type? type)
        {
            foreach (var entry in _rejected)
            {
                if (entry.Type == type && string.Equals(entry.Name, name, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>An entry that is not sent: its name and the type of its value (see <see cref="RejectedType"/>).</summary>
        private readonly record struct Rejected(string Name, Type? Type);
    }
}
