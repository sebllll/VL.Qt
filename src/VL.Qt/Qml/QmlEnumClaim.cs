namespace VL.Qt.Qml
{
    /// <summary>Answer of <see cref="QmlEnumPool.Claim"/>.</summary>
    /// <param name="Slot">The pool type of the enum, or null: then the pin stays a number (<c>int</c>).</param>
    /// <param name="Message">
    /// Text for the node, or null. Without a slot it says why (pool full, file not writable, collision); with a slot it reports a
    /// move to another pool type once (IOBoxes saved with the old type need to be recreated).
    /// </param>
    internal readonly record struct QmlEnumClaim(QmlEnumSlot? Slot, string? Message);
}
