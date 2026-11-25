namespace VL.Qt.Elements
{
    /// <summary>How an image or a video fills its size. The values are the wire values of the protocol (vlqt_fill_mode).</summary>
    public enum FillMode
    {
        /// <summary>Fills the whole size, the aspect ratio is not kept.</summary>
        Stretch = 0,
        /// <summary>Keeps the aspect ratio and fits inside the size.</summary>
        Fit = 1,
        /// <summary>Keeps the aspect ratio and fills the size, the overflow is cut off.</summary>
        Crop = 2,
        /// <summary>Repeats the content at its natural size, centered (Image only; Video draws it as Stretch).</summary>
        Tile = 3,
    }
}
