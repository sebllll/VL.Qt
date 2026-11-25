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

    /// <summary>
    /// GPU-backed image of VL.Qt, created by LoadImage, LoadImage (Async) or ToQtImage. Use it at the Image input of any
    /// number of Image elements in any window: it is uploaded once, switching between loaded images costs nothing.
    /// The node that created it owns it and frees it when it is deleted or loads another image; pass the image around
    /// freely, it needs no disposing. An image is valid while its node holds it, afterwards its State is Released.
    /// </summary>
    public sealed class QtImage
    {
        private readonly QtResource _resource;

        internal QtImage(QtResource resource) => _resource = resource;

        internal QtResource Resource => _resource;

        /// <summary>Resource id in QtNative (index | generation &lt;&lt; 32).</summary>
        internal ulong Id => _resource.Id;

        /// <summary>Width in pixels, 0 until loaded.</summary>
        public int Width => _resource.Width;

        /// <summary>Height in pixels, 0 until loaded.</summary>
        public int Height => _resource.Height;

        /// <summary>Loading, Ready, Failed or Released.</summary>
        public QtImageState State => _resource.IsReleased ? QtImageState.Released : _resource.State;

        /// <summary>Why loading failed, empty otherwise.</summary>
        public string Error => _resource.Error;

        public override string ToString() => $"QtImage {Width} x {Height} ({State})";
    }
}
