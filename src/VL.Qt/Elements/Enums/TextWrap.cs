namespace VL.Qt.Elements
{
    /// <summary>Values are Qt Text.WrapMode.</summary>
    public enum TextWrap
    {
        NoWrap = 0,
        WordWrap = 1,
        WrapAnywhere = 3,
        /// <summary>Word wrap, falls back to anywhere if a word does not fit.</summary>
        Wrap = 4,
    }
}
