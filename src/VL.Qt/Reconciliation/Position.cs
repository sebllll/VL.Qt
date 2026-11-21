using VL.Qt.Elements;
using VL.Qt.Styles;

namespace VL.Qt.Reconciliation
{
    /// <summary>Where an element is reconciled in the tree.</summary>
    /// <param name="Parent">Parent mirror; null under the window root.</param>
    /// <param name="ParentSlot">Feedback slot of the parent element; a failed Build of a self-made widget is shown there.</param>
    /// <param name="Index">REPARENT index: number of real objects before this position.</param>
    /// <param name="Inherited">Resolved style of the parent chain (the base style under the window root).</param>
    /// <param name="Attached">An object of an effect chain: no REPARENT, it is not a child.</param>
    internal readonly record struct Position(ItemMirror? Parent, FeedbackSlot ParentSlot, int Index, Style Inherited, bool Attached);
}
