using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Subjects;
using VL.Lib.Collections;

namespace VL.Qt.Qml
{
    /// <summary>
    /// One type of the QML enum pool (<see cref="QmlEnumPool"/>): its entries are the values of the QML enum that claimed it, name →
    /// int value in declaration order (the int is the Tag of an entry). Converts between the values of the pin and the ints of QML.
    /// </summary>
    internal sealed class QmlEnumSlot
    {
        private static readonly IReadOnlyDictionary<string, object> s_noEntries = new OrderedDictionary<string, object>();

        private readonly object _syncRoot = new();
        private readonly Func<string, IDynamicEnum> _create;
        private readonly Subject<object> _changed = new();
        private IReadOnlyDictionary<string, object> _entries = s_noEntries;

        internal QmlEnumSlot(int number, QmlEnumSlotType type)
        {
            Number = number;
            Type = type.Type;
            _create = type.Create;
        }

        /// <summary>Number of the slot, 1 to <see cref="QmlEnumPool.SlotCount"/>: Type is QmlEnum01 for 1.</summary>
        public int Number { get; }

        /// <summary>The dynamic enum type of the pins of the enum.</summary>
        public Type Type { get; }

        /// <summary>The entries, name → boxed int; never changed in place, a change replaces the dictionary.</summary>
        internal IReadOnlyDictionary<string, object> Entries
        {
            get
            {
                lock (_syncRoot)
                    return _entries;
            }
        }

        /// <summary>Fires after the entries changed; the definition of the type then reads <see cref="Entries"/> again.</summary>
        internal IObservable<object> Changed => _changed;

        /// <summary>
        /// The pin value of the QML int <paramref name="value"/>: the first entry with this value; for a value without an entry a value
        /// named by the number, which <see cref="TryToInt"/> reads back.
        /// </summary>
        public IDynamicEnum FromInt(int value)
        {
            foreach (var (name, tag) in Entries)
            {
                if (tag is int i && i == value)
                    return _create(name);
            }
            return _create(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Returns true if <paramref name="value"/> is a value of <see cref="Type"/> whose name is an entry (or a number, see
        /// <see cref="FromInt"/>); false for null, another type and a name the enum no longer has.
        /// </summary>
        public bool TryToInt(object? value, out int result)
        {
            result = 0;
            if (value is not IDynamicEnum e || e.GetType() != Type || e.Value is not { } name)
                return false;
            if (Entries.TryGetValue(name, out var tag) && tag is int i)
            {
                result = i;
                return true;
            }
            // QML names are identifiers, so a number is never an entry name.
            return int.TryParse(name, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>Takes the values of <paramref name="decl"/> as entries; fires <see cref="Changed"/> if they differ.</summary>
        internal void SetEntries(QmlEnumDecl decl)
        {
            var entries = new OrderedDictionary<string, object>(decl.Values.Length);
            foreach (var v in decl.Values)
                entries.TryAdd(v.Name, v.Value);
            lock (_syncRoot)
            {
                if (Same(_entries, entries))
                    return;
                _entries = entries;
            }
            _changed.OnNext(this);
        }

        private static bool Same(IReadOnlyDictionary<string, object> a, OrderedDictionary<string, object> b)
        {
            if (a.Count != b.Count)
                return false;
            using var x = a.GetEnumerator();
            using var y = b.GetEnumerator();
            while (x.MoveNext() && y.MoveNext())
            {
                if (x.Current.Key != y.Current.Key || !Equals(x.Current.Value, y.Current.Value))
                    return false;
            }
            return true;
        }
    }
}
