/* image_resource.h - resource kind VLQT_RESOURCE_KIND_IMAGE: the content of an image and the C API backends. The kind
 * itself is in image_kind.h/.cpp, its two upload paths in image_upload_rhi.cpp and image_upload_d3d11.*.
 *
 * An image comes from a file (vlqt_image_load) or from pixels of C# (vlqt_image_set_pixels) and ends up as a texture on
 * the process device (device/device.h), independent of windows. Without a shared device (Linux, VLQT_SHARED_DEVICE=0)
 * there is no texture: the resource keeps the converted QImage (image()). A new load or set_pixels on the same id
 * replaces the content; the old texture stays until the new one is complete. A Content remembers the device
 * generation it was made for (generation()); QtImageItem draws only content of the current one.
 * Decode, the upload paths and why, the source kept for the restore after a device loss, the texture pool and
 * replacement every frame: docs/design/resources.md, "Images", "Replacement" and "Device loss".
 */
#pragma once

#include "resources/resources.h"

#include <QtCore/QByteArray>
#include <QtCore/QSize>
#include <QtCore/QString>
#include <QtGui/QImage>

#include <cstdint>
#include <memory>
#include <string>

class QRhiTexture;
struct vlqt_pixel_desc;

namespace vlqt::res::image {

class D3D11Upload;
class GpuTexture;

// One image (the Content of the kind). The fields are owned by the manager's threads as docs/design/resources.md
// describes: input fields are set before start, the decode fields by the worker, the texture fields only on the main
// thread. Only ImageKind (image_kind.h) writes them.
class ImageContent final : public Content
{
public:
    ImageContent();
    // Frees the GPU objects like Kind::releaseDevice, so a Content the manager did not destroy cannot leak them.
    ~ImageContent() override;

    // --- for the Image element and shader items, main thread, on a Ready resource ---
    // Texture on the process device (upload QRhi), nullptr without a shared device (then image() holds the pixels).
    QRhiTexture* texture() const;
    // Native texture object (ID3D11Texture2D* for D3D11), nullptr like texture().
    void* nativeTexture() const;
    // The shared texture behind texture() (gpu_texture.h), held by the window wrappers; nullptr like texture().
    const std::shared_ptr<GpuTexture>& gpuTexture() const { return m_gpu; }
    QSize size() const { return m_size; }
    // VL PixelFormat of the texture: 7 = R8G8B8A8, 9 = B8G8R8A8; always premultiplied, 8 bit.
    uint32_t format() const { return m_bgra ? 9u : 7u; }
    // Converted pixels without a shared device; empty after the upload of a file.
    const QImage& image() const { return m_image; }
    // device::generation() this content was made for. Content of another generation lost its texture with the
    // old device and must not be drawn; its replacement for the new device follows (docs/design/resources.md, device lost).
    unsigned generation() const { return m_generation; }
    // Tooltip preview (VLQT_IMAGE_LOAD_PREVIEW): ARGB32_Premultiplied, longest edge <= 256, null without the flag.
    // Written by the worker before Ready, read only afterwards.
    const QImage& preview() const { return m_preview; }

private:
    friend class ImageKind;

    // input
    QString m_path;              // file source (empty for pixels)
    unsigned m_flags = 0;        // VLQT_IMAGE_LOAD_*
    int m_upload = 0;            // vlqt_image_upload, VLQT_IMAGE_UPLOAD_AUTO = the path of the source
    QByteArray m_raw;            // pixels: tightly packed copy (row = width * bpp), from and back to the buffer pool
    int m_rawWidth = 0, m_rawHeight = 0, m_rawFormat = 0;
    bool m_rawPremultiplied = false;
    void* m_device = nullptr;    // ID3D11Device* for the worker upload path, read on the main thread at start
    unsigned m_generation = 0;   // device::generation() of m_device

    // decode result (worker)
    QImage m_image;              // RGBA8888_Premultiplied or ARGB32_Premultiplied (BGRA in memory)
    QImage m_preview;            // ARGB32_Premultiplied, longest edge <= 256 (VLQT_IMAGE_LOAD_PREVIEW), survives m_image's drop
    bool m_bgra = false;
    QSize m_size;
    std::string m_warning;
    std::unique_ptr<D3D11Upload> m_d3d11; // worker upload path: textures made in the worker (Windows only)

    // upload (main thread)
    std::shared_ptr<GpuTexture> m_gpu; // from the texture pool (gpu_texture.h)
    int m_uploadedRows = 0;
};

// The kind for the manager (resources/resource_registry.cpp kindOf).
Kind* kind();

// --- C API backends (vlqt.h), main thread ---
// upload: vlqt_image_upload (protocol.h); another value is VLQT_ERR_BAD_ARGUMENT.
int load(uint64_t id, const char* utf8Path, unsigned flags, int upload);
int setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len, int upload);
// vlqt_image_read_preview: the preview the worker made for VLQT_IMAGE_LOAD_PREVIEW, read only on a Ready resource.
int readPreview(uint64_t id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written, int* required);

// Once per pump after the uploads (res::endFrame), main thread: frees pooled textures and buffers nobody took for a while.
void endFrame();

// Current content of a Ready image resource, nullptr otherwise (also during the first load). The Image element takes an
// element reference (res::acquire) for as long as it draws it.
std::shared_ptr<ImageContent> ready(uint64_t id);

// test hook (vlqt_test_res_read_pixels, test/test_probes.cpp): reads the content back as RGBA8 premultiplied (texture
// readback through the upload QRhi, else the CPU copy).
int testRead(uint64_t id, unsigned char* out, int cap, int* width, int* height, int* written, int* required);

} // namespace vlqt::res::image
