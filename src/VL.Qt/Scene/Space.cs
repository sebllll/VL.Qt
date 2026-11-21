namespace VL.Qt
{
    /// <summary>
    /// Coordinate space of an element's geometry: where the origin is and how many logical pixels (DIP) one unit
    /// covers. y points down in every space. Names, order and meaning mirror <c>VL.Skia.CommonSpace</c>
    /// (VL.StandardLibs, VL.Core.Skia/Enums.cs) so patches feel the same; declared here to avoid a dependency on Skia.
    /// A scene without a window (QtTexture) has a device pixel ratio of 1: 1 DIP is 1 pixel there. <see cref="SpaceMapping"/> does the conversion.
    /// </summary>
    public enum Space
    {
        /// <summary>Height goes from -1 top to 1 bottom, so one unit is half the scene height in DIP. The origin is located in the center.</summary>
        Normalized,

        /// <summary>Device independent pixels. One unit equals 100 DIP. The origin is located in the center.</summary>
        DIP,

        /// <summary>Device independent pixels. One unit equals 100 DIP. The origin is located at the top left.</summary>
        DIPTopLeft,

        /// <summary>Device pixels. One unit equals 100 device pixels (100 / dpr DIP, independent of the DPI scale). The
        /// origin is located at the top left.</summary>
        PixelTopLeft,
    }
}
