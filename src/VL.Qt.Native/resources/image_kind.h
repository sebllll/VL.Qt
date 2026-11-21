/* image_kind.h - the image kind (image_resource.h), internal to its files: image_kind.cpp (decode, device loss, the C API
 * backends) and image_upload_rhi.cpp (the upload, stripes through the upload QRhi or the worker path of
 * image_upload_d3d11.h). The only class with access to the fields of ImageContent.
 */
#pragma once

#include "resources/image_resource.h"

namespace vlqt::res::image {

class ImageKind final : public Kind
{
public:
    const char* name() const override { return "image"; }
    std::string decode(Content& work) override;
    int64_t upload(Content& work, int64_t budget, UploadStatus& status, std::string& error) override;
    void destroy(Content& work) override { releaseGpu(static_cast<ImageContent&>(work)); }
    // Device lost: the textures, the staging texture and a running copy go, the source stays.
    void releaseDevice(Content& work) override { releaseGpu(static_cast<ImageContent&>(work)); }
    std::shared_ptr<Content> restore(const Content& work) override;
    bool stale(const Content& work) const override;
    Info info(const Content& work) const override;
    int64_t pendingBytes(const Content& work) const override;
    std::string takeWarning(Content& work) override;

    // The C API backends of image_resource.h that write the input fields.
    static int load(uint64_t id, const char* utf8Path, unsigned flags, int upload);
    static int setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len, int upload);

    // Gives the texture back to the pool (gpu_texture.h) and frees the worker path. Main thread, or the thread of the last
    // holder in ~ImageContent: the texture is only a reference there.
    static void releaseGpu(ImageContent& r);

private:
    // image_upload_rhi.cpp
    static int64_t uploadNow(ImageContent& r, int64_t budget, UploadStatus& status, std::string& error);
    // Files keep only the path (re-upload after a device loss reads the file again); pixels keep the converted copy.
    static void finishUpload(ImageContent& r);

    // image_kind.cpp
    static void makePreview(ImageContent& r);
    static std::string readFile(ImageContent& r);
    static std::string convertPixels(ImageContent& r);

    // Bytes of the texture (8 bit, 4 channels); m_image may be gone already (worker path, files).
    static int64_t bytes(const ImageContent& r) { return int64_t(r.m_size.width()) * 4 * r.m_size.height(); }
};

} // namespace vlqt::res::image
