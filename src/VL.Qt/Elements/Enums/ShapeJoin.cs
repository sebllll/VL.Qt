namespace VL.Qt.Elements
{
    /// <summary>How two stroke segments are joined at a corner. Values are the protocol values of <see cref="Wire.Protocol.PropertyId.ShapeJoin"/>.</summary>
    public enum ShapeJoin
    {
        /// <summary>Sharp corner, cut off to a bevel beyond the miter limit.</summary>
        Miter = 0,
        /// <summary>Corner cut off straight.</summary>
        Bevel = 1,
        /// <summary>Corner rounded by a circle around the join point.</summary>
        Round = 2,
    }
}
