#include "resources/image_upload_d3d11.h"

#include "device/device.h"

#include <chrono>
#include <cstring>

namespace vlqt::res::image {

namespace {

// Levels of a full mip chain, as QRhi::mipLevelsForSize (QRhiTexture::MipMapped on the wrapper must match).
UINT mipLevels(QSize size)
{
    UINT levels = 1;
    for (int edge = (std::max)(size.width(), size.height()); edge > 1; edge >>= 1) // windows.h max macro
        levels++;
    return levels;
}

// Copy task (thread pool): the pixels into the mapped staging texture, row by row (the row pitches differ).
void copyToStaging(const QImage& img, D3D11_MAPPED_SUBRESOURCE map, const std::atomic<bool>& cancel)
{
    const auto* src = img.constBits();
    const qsizetype bpl = img.bytesPerLine();
    const size_t row = size_t(img.width()) * 4;
    for (int y = 0; y < img.height(); y++) {
        if ((y & 63) == 0 && cancel.load())
            return;
        std::memcpy(static_cast<uchar*>(map.pData) + size_t(y) * map.RowPitch, src + y * bpl, row);
    }
}

// Releases a D3D object in the thread pool: freeing a 4K staging texture takes 1-9 ms on arm64.
void releaseInPool(IUnknown* object)
{
    const auto release = [](PTP_CALLBACK_INSTANCE, void* p) { static_cast<IUnknown*>(p)->Release(); };
    if (!TrySubmitThreadpoolCallback(release, object, nullptr))
        object->Release();
}

} // namespace

// No initial data: CreateTexture2D with initial data blocks the main thread's context calls for as long as it runs
// (arm64 Adreno: 4K 5-45 ms, every pump that overlaps it is a hitch, 4 MB stripes as well); empty creates take < 1 ms
// and block nothing. Mipmaps: the texture has the full chain (render target + GENERATE_MIPS, like QRhi's
// UsedWithGenerateMips) and a shader resource view over it for GenerateMips; the staging texture holds level 0 only.
bool D3D11Upload::createInWorker(ID3D11Device* device, QSize size, bool bgra, bool mips)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = UINT(size.width());
    desc.Height = UINT(size.height());
    desc.MipLevels = mips ? mipLevels(size) : 1;
    desc.ArraySize = 1;
    desc.Format = bgra ? DXGI_FORMAT_B8G8R8A8_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (mips ? D3D11_BIND_RENDER_TARGET : 0);
    desc.MiscFlags = mips ? D3D11_RESOURCE_MISC_GENERATE_MIPS : 0;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture, staging;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> srv;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &texture)))
        return false;
    if (mips && FAILED(device->CreateShaderResourceView(texture.Get(), nullptr, &srv)))
        return false;
    desc.MipLevels = 1;
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.MiscFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging)))
        return false;
    m_device = device;
    m_texture = std::move(texture);
    m_staging = std::move(staging);
    m_srv = std::move(srv);
    return true;
}

bool D3D11Upload::fill(QImage& image, bool keepImage, bool sync, const std::atomic<bool>& cancel, std::string& error)
{
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> ctx;
    m_device->GetImmediateContext(&ctx);
    if (!m_mapped) {
        D3D11_MAPPED_SUBRESOURCE map{};
        const HRESULT hr = ctx->Map(m_staging.Get(), 0, D3D11_MAP_WRITE, 0, &map);
        if (FAILED(hr)) {
            error = "upload: Map of the staging texture failed (hr " + std::to_string(long(hr)) + ")";
            return false;
        }
        m_mapped = true;
        // Files drop the CPU copy after the upload: the task takes it and frees it there (4K: 2-9 ms on the main thread
        // on arm64). Pixels keep theirs (shared, read only).
        QImage img = keepImage ? image : std::move(image);
        m_copy = std::async(sync ? std::launch::deferred : std::launch::async, [&cancel, img = std::move(img), map]() mutable {
            copyToStaging(img, map, cancel);
            img = QImage();
        });
    }
    if (!sync && m_copy.wait_for(std::chrono::seconds(0)) != std::future_status::ready)
        return false;
    m_copy.get();
    // Unmap and the GPU copy (+ GenerateMips) as one sequence on the context shared with the render threads.
    device::ContextLock lock(ctx.Get());
    ctx->Unmap(m_staging.Get(), 0);
    m_mapped = false;
    if (m_srv) {
        // Mipmaps: level 0 from the staging texture, the rest on the GPU. Both are only recorded on the immediate
        // context (no wait, unlike the offscreen frame of the stripes); the windows draw on the same context
        // afterwards, so they see the finished chain.
        ctx->CopySubresourceRegion(m_texture.Get(), 0, 0, 0, 0, m_staging.Get(), 0, nullptr);
        ctx->GenerateMips(m_srv.Get());
        m_srv.Reset(); // the runtime keeps it alive for the recorded GenerateMips
    } else {
        ctx->CopyResource(m_texture.Get(), m_staging.Get());
    }
    releaseInPool(m_staging.Detach()); // the runtime keeps it alive until the GPU copy has run
    return true;
}

// Waits for a running copy task: it writes into the mapped staging texture.
void D3D11Upload::releaseStaging()
{
    if (m_copy.valid())
        m_copy.wait();
    if (!m_staging)
        return;
    if (m_mapped) {
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> ctx;
        m_device->GetImmediateContext(&ctx);
        ctx->Unmap(m_staging.Get(), 0);
        m_mapped = false;
    }
    m_staging.Reset();
}

void D3D11Upload::release()
{
    releaseStaging();
    m_srv.Reset();
    m_texture.Reset();
    m_device.Reset();
}

bool D3D11Upload::deviceRemoved(void* device)
{
    return device && static_cast<ID3D11Device*>(device)->GetDeviceRemovedReason() != S_OK;
}

} // namespace vlqt::res::image
