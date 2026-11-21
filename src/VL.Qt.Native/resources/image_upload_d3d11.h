/* image_upload_d3d11.h - the worker upload path of an image (image_resource.h), D3D11 only.
 *
 * The worker creates an empty texture and an empty staging texture of the image size (createInWorker). On the main
 * thread fill maps the staging texture, a copy task writes the pixels into it, then Unmap + CopyResource (+ GenerateMips
 * for mipmaps) put them into the texture, which QRhiTexture::createFrom wraps without owning it. The COM objects are
 * held by ComPtr; the destructor releases everything (release), so the owner (ImageContent::m_d3d11) needs no manual
 * cleanup. Off Windows the class is empty and never made: there is no worker path.
 */
#pragma once

#include <QtCore/qsystemdetection.h>

#ifdef Q_OS_WIN

#include <QtCore/QSize>
#include <QtGui/QImage>

#include <d3d11.h>
#include <wrl/client.h>

#include <atomic>
#include <future>
#include <string>

namespace vlqt::res::image {

class D3D11Upload final
{
public:
    D3D11Upload() = default;
    ~D3D11Upload() { release(); }
    D3D11Upload(const D3D11Upload&) = delete;
    D3D11Upload& operator=(const D3D11Upload&) = delete;

    // Worker: an empty texture (with its full mip chain for mips) and an empty staging texture (CPU write, level 0).
    // Returns false and keeps nothing on an error.
    bool createInWorker(ID3D11Device* device, QSize size, bool bgra, bool mips);

    // Main thread: maps the staging texture and starts the copy task (sync: runs it here), then, once it is done, Unmap
    // + CopyResource into the texture. True when the texture holds the pixels; false while the copy task runs or on an
    // error (error set). The first call takes image (keepImage: a shared copy, else moved out); cancel is read by the
    // copy task and must outlive this object.
    bool fill(QImage& image, bool keepImage, bool sync, const std::atomic<bool>& cancel, std::string& error);

    // fill has not finished yet (the staging texture is still there).
    bool filling() const { return m_staging != nullptr; }

    // The texture, complete once fill returned true; QRhiTexture::createFrom wraps it without owning it.
    ID3D11Texture2D* texture() const { return m_texture.Get(); }

    // Main thread: waits for a running copy task, unmaps and releases every object. Called by the destructor.
    void release();

    // The device (ID3D11Device*) was removed (device lost); thread safe, also in a worker.
    static bool deviceRemoved(void* device);

private:
    void releaseStaging();

    Microsoft::WRL::ComPtr<ID3D11Device> m_device;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> m_texture;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> m_staging;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> m_srv; // over m_texture for GenerateMips (mipmaps)
    bool m_mapped = false;   // m_staging is mapped; m_copy writes the pixels into it
    std::future<void> m_copy; // copy task image -> mapped staging texture
};

} // namespace vlqt::res::image

#else

namespace vlqt::res::image {

// No D3D11: the worker path does not exist, ImageContent::m_d3d11 stays empty.
class D3D11Upload final
{
};

} // namespace vlqt::res::image

#endif
