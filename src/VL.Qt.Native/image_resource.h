/* image_resource.h - resource kind VLQT_RESOURCE_KIND_IMAGE (images package 03).
 *
 * An image comes from a file (vlqt_image_load) or from pixels of C# (vlqt_image_set_pixels) and ends up as a
 * texture on the process device (device.h): an ID3D11Texture2D created by the upload QRhi, independent of windows.
 *
 *   load:   QImageReader (setAutoTransform for the EXIF rotation) + conversion to 8 bit premultiplied, in a worker.
 *   pixels: synchronous copy into an own buffer in the entry point, conversion (format table in image_resource.cpp,
 *           comment in protocol.h) in a worker.
 *   upload: files: the worker creates an empty texture and an empty staging texture; on the main thread the staging
 *           texture is mapped, a copy task writes the pixels into it (an image within the frame budget: the main
 *           thread), then Unmap + CopyResource (GPU copy) and the texture is wrapped (CreateTexture2D with
 *           initial data blocked the render for as long as it ran);
 *           pixels: main thread, in stripes of at most the frame budget through one offscreen frame of the upload
 *           QRhi per resource and pump. Ready only when every stripe is there. Mipmaps: on the worker path the
 *           texture is created with its mip chain, the main thread copies level 0 and records GenerateMips (no
 *           offscreen frame, no wait); on the stripes path generateMips follows the last stripe.
 *           The upload argument of vlqt_image_load / vlqt_image_set_pixels (vlqt_image_upload) forces one path;
 *           with AUTO the environment variable VLQT_IMG_UPLOAD=stripes|worker still does (measurements in #360) until
 *           the pin Upload replaces it (E10).
 *
 * Source kept for the re-upload after a device loss (package 10): the path for files (no CPU copy after the
 * upload, restore decodes the file again), the converted CPU copy for pixels (restore only uploads it again). A Work
 * remembers the device generation it was made for (generation()); QtImageItem draws only content of the current one. Without a shared device (Linux, VLQT_SHARED_DEVICE=0) there is no texture: the
 * resource keeps the converted QImage and is Ready after the decode (image()).
 *
 * A new load/set_pixels on the same id replaces the content: the old texture stays until the new one is complete
 * (resources.h, replacement), so elements never show an empty frame.
 */
#pragma once

#include "resources/resources.h"

#include <QtCore/QByteArray>
#include <QtCore/QSize>
#include <QtCore/QString>
#include <QtGui/QImage>

#include <cstdint>
#include <future>
#include <memory>

class QRhiTexture;
struct vlqt_pixel_desc;

namespace vlqt::res::image {

// One image (the Work of the kind). The fields are owned by the manager's threads as resources.h describes: input
// fields are set before start, the decode fields by the worker, the texture fields only on the main thread.
class ImageResource final : public Work
{
public:
    // --- for elements (package 04), main thread, on a Ready resource ---
    // Texture on the process device, nullptr without a shared device (then image() holds the pixels).
    QRhiTexture* texture() const { return m_texture; }
    // Native texture object (ID3D11Texture2D* for D3D11), nullptr like texture().
    void* nativeTexture() const;
    QSize size() const { return m_size; }
    // VL PixelFormat of the texture: 7 = R8G8B8A8, 9 = B8G8R8A8; always premultiplied, 8 bit.
    uint32_t format() const { return m_bgra ? 9u : 7u; }
    // Converted pixels without a shared device; empty after the upload of a file.
    const QImage& image() const { return m_image; }
    // device::generation() this content was made for. Content of another generation lost its texture with the
    // old device and must not be drawn; its replacement for the new device follows (resources.h, device lost).
    unsigned generation() const { return m_generation; }

private:
    friend class ImageKind;
    friend int load(uint64_t, const char*, unsigned, int);
    friend int setPixels(uint64_t, const vlqt_pixel_desc*, const void*, int, int);
    friend int readPreview(uint64_t, unsigned char*, int, vlqt_pixel_desc*, int*, int*);
    friend int testRead(uint64_t, unsigned char*, int, int*, int*, int*, int*);

    // input
    QString m_path;              // file source (empty for pixels)
    unsigned m_flags = 0;        // VLQT_IMAGE_LOAD_*
    int m_upload = 0;            // vlqt_image_upload, VLQT_IMAGE_UPLOAD_AUTO = the path of the source
    QByteArray m_raw;            // pixels: tightly packed copy (row = width * bpp)
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
    void* m_workerTexture = nullptr; // ID3D11Texture2D* created empty in the worker (worker upload path)
    void* m_staging = nullptr;       // ID3D11Texture2D* staging (CPU write), created empty in the worker
    void* m_workerSrv = nullptr;     // ID3D11ShaderResourceView* over m_workerTexture for GenerateMips (mipmaps)

    // upload (main thread)
    QRhiTexture* m_texture = nullptr;
    int m_uploadedRows = 0;
    bool m_mapped = false;           // m_staging is mapped; m_copy writes the pixels into it
    std::future<void> m_copy;        // copy task m_image -> mapped staging texture (worker upload path)
};

// The kind for the manager (resources.cpp kindOf).
Kind* kind();

// --- C API backends (vlqt.h), main thread ---
// upload: vlqt_image_upload (protocol.h); another value is VLQT_ERR_BAD_ARGUMENT.
int load(uint64_t id, const char* utf8Path, unsigned flags, int upload);
int setPixels(uint64_t id, const vlqt_pixel_desc* desc, const void* data, int len, int upload);
// vlqt_image_read_preview: the preview the worker made for VLQT_IMAGE_LOAD_PREVIEW, read only on a Ready resource.
int readPreview(uint64_t id, unsigned char* dst, int cap, vlqt_pixel_desc* desc, int* written, int* required);

// Current content of a Ready image resource, nullptr otherwise (also during the first load). Package 04 takes an
// element reference (res::acquire) for as long as it draws it.
std::shared_ptr<ImageResource> ready(uint64_t id);

// test hook (vlqt_test_res_read_pixels, test/test_probes.cpp): reads the content back as RGBA8 premultiplied (texture
// readback through the upload QRhi, else the CPU copy).
int testRead(uint64_t id, unsigned char* out, int cap, int* width, int* height, int* written, int* required);

} // namespace vlqt::res::image
