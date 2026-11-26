namespace VL.Qt.Images
{
    /// <summary>Loading state of a <see cref="QtImage"/>.</summary>
    public enum QtImageState
    {
        /// <summary>Decoding or uploading; the Image element keeps showing its previous image meanwhile.</summary>
        Loading,
        /// <summary>On the GPU, drawn by every Image element that uses it.</summary>
        Ready,
        /// <summary>Could not be loaded (see Error); draws nothing.</summary>
        Failed,
        /// <summary>The node that created the image freed it (it loaded another image or was deleted); draws nothing.</summary>
        Released,
    }
}
