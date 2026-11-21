namespace VL.Qt.Images
{
    /// <summary>
    /// How the pixels of a <see cref="QtImage"/> get onto the GPU. The values are those of vlqt_image_upload (protocol.h).
    /// </summary>
    public enum ImageUpload
    {
        /// <summary>Files take the worker path, pixels (ToQtImage, video frames) the main thread.</summary>
        Auto = 0,

        /// <summary>In stripes within the frame budget on the main thread; the image is ready after one or more frames.</summary>
        MainThread = 1,

        /// <summary>
        /// The texture is created in a worker and the pixels are copied by a task, the main thread only records the GPU
        /// copy (D3D11; elsewhere the same as Main Thread).
        /// </summary>
        Worker = 2,
    }
}
