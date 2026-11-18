namespace VL.Qt.Paths
{
    /// <summary>How the in handle of an anchor follows its out handle (the out handle leads).</summary>
    public enum HandleMode
    {
        /// <summary>Both handles are used as given.</summary>
        Free,

        /// <summary>The in handle is the out handle mirrored through the anchor (in = −out).</summary>
        Mirrored,

        /// <summary>The in handle points opposite to the out handle and keeps its own length.</summary>
        Aligned,
    }
}
