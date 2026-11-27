using System;
using VL.Lib.Collections;

namespace VL.Qt.Qml
{
    /// <summary>One precompiled dynamic enum type of the pool (<see cref="QmlEnumSlotTypes"/>) and how to make a value of it.</summary>
    /// <param name="Type">The type, QmlEnum01 to QmlEnum32.</param>
    /// <param name="Create">Makes a value of <paramref name="Type"/> from the name of an entry.</param>
    internal readonly record struct QmlEnumSlotType(Type Type, Func<string, IDynamicEnum> Create);
}
