namespace VL.Qt.Elements
{
    /// <summary>Axes along which a Drag node moves the element (values of VLQT_PROPERTY_DRAG_AXIS).</summary>
    public enum DragAxis
    {
        /// <summary>Free movement in both directions.</summary>
        Both = 0,
        /// <summary>Only along x; a mostly vertical drag is left to the elements below (e.g. a parent's Drag).</summary>
        Horizontal = 1,
        /// <summary>Only along y; a mostly horizontal drag is left to the elements below (e.g. a parent's Drag).</summary>
        Vertical = 2,
    }
}
