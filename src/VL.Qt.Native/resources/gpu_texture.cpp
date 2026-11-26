#include "resources/gpu_texture.h"

#include "device/device.h"
#include "resources/resource_kind.h"

#include <vector>

#ifdef Q_OS_WIN
#include <unknwn.h>
#endif

namespace vlqt::res::image {

GpuTexture::GpuTexture(QRhiTexture* rhiTexture) : m_rhiTexture(rhiTexture)
{
#ifdef Q_OS_WIN
    m_native = reinterpret_cast<void*>(rhiTexture->nativeTexture().object);
    if (m_native)
        static_cast<IUnknown*>(m_native)->AddRef();
#endif
}

GpuTexture::~GpuTexture()
{
    // Only the pool destroys a texture that still has its QRhiTexture, and only on the main thread.
    releaseRhiTexture();
#ifdef Q_OS_WIN
    if (m_native)
        static_cast<IUnknown*>(m_native)->Release();
#endif
}

void GpuTexture::releaseRhiTexture()
{
    if (!m_rhiTexture)
        return;
    m_rhiTexture->destroy();
    delete m_rhiTexture;
    m_rhiTexture = nullptr;
}

namespace texture_pool {

namespace {

constexpr int maxFreePerKey = 2; // free textures kept per format, size and flags
constexpr int64_t idleFrames = 60; // pumps a free texture is kept (~1 s at 60 Hz)

struct Entry
{
    std::shared_ptr<GpuTexture> texture;
    int64_t freeSince = -1; // pump in which it was seen free first, -1 = in use
};

// Main thread only, no lock (gpu_texture.h).
struct Pool
{
    std::vector<Entry> entries;
    int64_t frame = 0;

    void releaseDevice()
    {
        for (Entry& e : entries)
            e.texture->releaseRhiTexture();
        if (!entries.empty())
            qCDebug(lcRes, "texture pool: %d texture(s) of the lost device released", int(entries.size()));
        entries.clear();
    }
};

// Created at the first use and never deleted (like the manager): textures may still be held at process end.
Pool* s_pool = nullptr;

Pool& pool()
{
    if (!s_pool) {
        s_pool = new Pool();
        device::addListener({[] { s_pool->releaseDevice(); }, nullptr});
    }
    return *s_pool;
}

bool sameKey(const QRhiTexture* a, const QRhiTexture* b)
{
    return a->format() == b->format() && a->pixelSize() == b->pixelSize() && a->flags() == b->flags();
}

std::shared_ptr<GpuTexture> add(QRhiTexture* created)
{
    auto t = std::make_shared<GpuTexture>(created);
    pool().entries.push_back({t, -1});
    return t;
}

} // namespace

std::shared_ptr<GpuTexture> take(QRhi* rhi, QRhiTexture::Format format, QSize size, QRhiTexture::Flags flags, bool& reused)
{
    reused = false;
    for (Entry& e : pool().entries) {
        const QRhiTexture* t = e.texture->rhiTexture();
        // use_count 1: only the pool holds it. Only the main thread copies from the pool, so it cannot grow meanwhile.
        if (e.texture.use_count() == 1 && t && t->format() == format && t->pixelSize() == size && t->flags() == flags) {
            e.freeSince = -1;
            reused = true;
            return e.texture;
        }
    }
    QRhiTexture* created = rhi->newTexture(format, size, 1, flags);
    if (!created->create()) {
        delete created;
        return nullptr;
    }
    return add(created);
}

std::shared_ptr<GpuTexture> adopt(QRhiTexture* created)
{
    return add(created);
}

void endFrame()
{
    if (!s_pool)
        return;
    Pool& p = *s_pool;
    p.frame++;
    auto& entries = p.entries;
    for (size_t i = 0; i < entries.size();) {
        Entry& e = entries[i];
        if (e.texture.use_count() > 1) {
            e.freeSince = -1;
            ++i;
            continue;
        }
        if (e.freeSince < 0)
            e.freeSince = p.frame;
        // Free ones of the same key already kept in this pass (indices below i are processed).
        int sameFree = 0;
        for (size_t j = 0; j < i; j++)
            if (entries[j].freeSince >= 0 && entries[j].texture.use_count() == 1
                && sameKey(entries[j].texture->rhiTexture(), e.texture->rhiTexture()))
                sameFree++;
        if (p.frame - e.freeSince <= idleFrames && sameFree < maxFreePerKey) {
            ++i;
            continue;
        }
        e.texture->releaseRhiTexture();
        e = std::move(entries.back()); // order does not matter; the moved one is processed next
        entries.pop_back();
    }
}

} // namespace texture_pool

} // namespace vlqt::res::image
