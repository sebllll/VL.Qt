namespace VL.Qt.Reconciliation
{
    /// <summary>Values of one reconcile pass (<see cref="Reconciler.Update(Elements.Element?, in SpaceMapping)"/>) that the diff reads.</summary>
    /// <param name="Pass">Number of the pass; marks the mirrors that were visited, skipped or seen in it.</param>
    /// <param name="Force">The whole tree is visited: the reference-equality shortcut is off.</param>
    /// <param name="Rescale">The unit scale changed in this pass: every length is written again.</param>
    /// <param name="Mapping">Units ↔ logical pixels of the scene.</param>
    internal readonly record struct PassInfo(int Pass, bool Force, bool Rescale, SpaceMapping Mapping);
}
