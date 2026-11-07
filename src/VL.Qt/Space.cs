namespace VL.Qt
{
    /// <summary>
    /// Coordinate space of an element's geometry. Names, order and meaning mirror
    /// <c>VL.Skia.CommonSpace</c> (VL.StandardLibs, VL.Core.Skia/Enums.cs) so patches feel the same;
    /// declared here to avoid a dependency on Skia.
    /// </summary>
    public enum Space
    {
        /// <summary>Height goes from -1 top to 1 bottom. The origin is located in the center.</summary>
        Normalized,

        /// <summary>Device independent pixels. One unit equals 100 DIP. The origin is located in the center.</summary>
        DIP,

        /// <summary>Device independent pixels. One unit equals 100 DIP. The origin is located at the top left.</summary>
        DIPTopLeft,

        /// <summary>Pixels. One unit equals 100 pixels. The origin is located at the top left.</summary>
        PixelTopLeft,
    }
}
