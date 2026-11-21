#include "snapshot_resource.h"

#include "app/exports.h"
#include "scene/scene.h"
#include "offscreen/offscreen.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QByteArray>
#include <QtCore/QLoggingCategory>
#include <QtCore/QPointer>
#include <QtCore/QSharedPointer>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickItemGrabResult>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>
#include <QtGui/private/qwindow_p.h> // grab of an offscreen scene
#include <QtQuick/private/qquickshadereffectsource_p.h> // frame grab of a Group

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace vlqt::res::snapshot {

namespace {

constexpr int maxSide = 16384;          // pixels per side; more is a wrong scale
constexpr int64_t timeoutMs = 10000;    // no ready within this time: Failed (the window does not render)
constexpr int64_t abandonMs = 60000;    // a given up grab Qt still holds is deleted after this time

int64_t steadyUs()
{
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// Deliveries of the last statsWindowUs for the overlay: a ring of fixed size, oldest overwritten, nothing allocated.
constexpr int64_t statsWindowUs = 10'000'000;
constexpr int statsCap = 256;
struct Delivery
{
    int64_t atUs = 0;
    int64_t durationUs = 0;
};
Delivery s_deliveries[statsCap];
int s_deliveryHead = 0;  // next slot to write
int s_deliveryCount = 0; // valid entries (<= statsCap)

void recordDelivery(int64_t startUs)
{
    const int64_t now = steadyUs();
    s_deliveries[s_deliveryHead] = {now, now - startUs};
    s_deliveryHead = (s_deliveryHead + 1) % statsCap;
    s_deliveryCount = std::min(s_deliveryCount + 1, statsCap);
}

class SnapshotKind final : public Kind
{
public:
    const char* name() const override { return "snapshot"; }
    // Never queued: snapshots come through beginExternal / finishExternal.
    std::string decode(Content&) override { return "snapshot: no decode"; }
    int64_t upload(Content&, int64_t, bool& done, std::string&) override
    {
        done = true;
        return 0;
    }
    void destroy(Content& work) override { static_cast<SnapshotContent&>(work).image = QImage(); }
    Info info(const Content& work) const override
    {
        const QImage& img = static_cast<const SnapshotContent&>(work).image;
        return {uint32_t(img.width()), uint32_t(img.height()), 9u};
    }
};

// One grabToImage in flight. Owned by s_grabs; deleted only in endFrame / shutdown, never inside ready.
struct Grab
{
    Grab() = default;
    Grab(const Grab&) = delete;
    Grab& operator=(const Grab&) = delete;
    // The ready lambda captures this Grab: cut it before the Grab goes, also if Qt still holds the result.
    ~Grab()
    {
        QObject::disconnect(connection);
        if (frameSource) // after ready or given up: the grab no longer renders it
            frameSource->deleteLater();
    }

    QMetaObject::Connection connection;
    uint64_t id = 0;
    QSharedPointer<QQuickItemGrabResult> result;
    QPointer<QQuickItem> item;
    QPointer<QQuickItem> frameSource; // hidden ShaderEffectSource over the frame of a Group (frameTarget), or null
    QPointer<QQuickWindow> window;
    bool offscreen = false; // window is an offscreen scene: never exposed, renders only while bound
    QSize size;
    int64_t startMs = 0;
    int64_t startUs = 0; // for the duration in the overlay
    bool current = true; // its result counts (false: superseded, released or given up)
    bool done = false;   // ready came; deleted at the next endFrame
};

std::vector<std::unique_ptr<Grab>> s_grabs;

// The grab of an id ends without content: Failed with RESOURCE_EVENT_ERROR (nothing if the id was released).
void failGrab(Grab& g, const std::string& error)
{
    g.current = false;
    finishExternal(g.id, nullptr, 0, error);
}

void onReady(Grab& g)
{
    g.done = true;
    if (!g.current)
        return;
    g.current = false;
    QImage img = g.result ? g.result->image() : QImage();
    if (img.isNull()) {
        finishExternal(g.id, nullptr, 0, "snapshot: the grab returned no image (element or window destroyed during the grab?)");
        return;
    }
    if (img.format() != QImage::Format_ARGB32_Premultiplied)
        img.convertTo(QImage::Format_ARGB32_Premultiplied);
    img.setDevicePixelRatio(1.0);
    if (img.size() != g.size) {
        // The request was divided by the window's device pixel ratio (start); where no integer size times the ratio
        // hits the wanted pixel size exactly (e.g. odd sizes at 1.5), Qt's result is off by about one pixel and is
        // scaled to the exact size of the contract (DIP * scale, independent of the DPR).
        qCDebug(lcRes, "%s: snapshot is %dx%d, scaled to the requested %dx%d", idText(g.id).constData(), img.width(),
            img.height(), g.size.width(), g.size.height());
        img = img.scaled(g.size, Qt::IgnoreAspectRatio, Qt::SmoothTransformation);
    }
    auto work = std::make_shared<SnapshotContent>();
    const int64_t bytes = int64_t(img.width()) * img.height() * 4;
    work->image = std::move(img);
    qCDebug(lcRes, "%s: snapshot %dx%d after %lld ms", idText(g.id).constData(), work->image.width(),
        work->image.height(), (long long)(steadyMs() - g.startMs));
    recordDelivery(g.startUs);
    finishExternal(g.id, std::move(work), bytes, {});
}

// Side for grabToImage so that Qt's side * dpr (rounded) is n: floor or ceil of n / dpr when one of them hits n
// exactly, else the nearest (onReady scales the one pixel miss).
int requestSide(int n, qreal dpr)
{
    const int lo = std::max(1, int(std::floor(n / dpr))), hi = std::max(1, int(std::ceil(n / dpr)));
    if (qRound(lo * dpr) == n)
        return lo;
    if (qRound(hi * dpr) == n)
        return hi;
    return std::max(1, qRound(n / dpr));
}

// The element of scene as a grab target and its frame (item coordinates), or the error text.
QQuickItem* target(Scene& scene, unsigned elementId, QRectF& frame, std::string& error)
{
    QQuickItem* item = nullptr;
    const TransformState* xf = nullptr;
    if (elementId == VLQT_ROOT_ID) {
        item = scene.root;
    } else {
        const auto it = scene.nodes.find(quint32(elementId));
        if (it == scene.nodes.end()) {
            error = "snapshot: element " + std::to_string(elementId) + " is unknown in the window";
            return nullptr;
        }
        item = it->second.item;
        xf = it->second.xf.get();
    }
    if (item)
        frame = xf ? itemFrame(*xf, item) : QRectF(0, 0, item->width(), item->height());
    if (!item) {
        error = "snapshot: element " + std::to_string(elementId) + " has no item";
        return nullptr;
    }
    if (item->window() != scene.window) {
        error = "snapshot: element " + std::to_string(elementId) + " is not shown in the window (no parent)";
        return nullptr;
    }
    return item;
}

// grabToImage always renders (0, 0, width, height) of the item. The frame of an auto-sized Group may start left of /
// above its origin ("Group frame" in protocol.h), so such a grab goes through a hidden ShaderEffectSource whose
// sourceRect is the frame: it renders the element without its own transform (like grabToImage), its texture has the
// requested pixel size, and grabToImage of the source draws exactly that texture. Hidden: Qt keeps a hidden
// item in the scene graph while a grab references it and renders it only into the grab. Parent = the window's content
// item, never the Group (the source would be part of its own texture) or another element (frame, layout).
QQuickItem* frameTarget(Scene& scene, QQuickItem* item, const QRectF& frame, QSize pixels)
{
    auto* source = new QQuickShaderEffectSource();
    source->setVisible(false);
    source->setLive(true);
    source->setHideSource(false);
    source->setSourceItem(item);
    source->setSourceRect(frame);
    source->setTextureSize(pixels);
    source->setSize(frame.size());
    source->setParentItem(scene.window->contentItem());
    return source;
}

// grabToImage of source. QQuickItemGrabResult refuses an item whose window is not visible, which the window of an
// offscreen scene never is (QQuickRenderControl, no native window; render-texture.md). The check is only in Qt's
// create: the grab itself runs in the scene's next render (render_batch). So the offscreen window counts as visible for
// this one call, set in QWindowPrivate without show() (show would create a native window). Private API: check on Qt
// updates; removed when Qt grabs items of a render control window without a visible render window.
QSharedPointer<QQuickItemGrabResult> grab(QQuickItem* source, QSize request, bool offscreenScene)
{
    if (!offscreenScene)
        return source->grabToImage(request);
    QWindowPrivate* wd = QWindowPrivate::get(source->window());
    const bool visible = std::exchange(wd->visible, true);
    auto result = source->grabToImage(request);
    wd->visible = visible;
    return result;
}

} // namespace

Kind* kind()
{
    static SnapshotKind s_kind;
    return &s_kind;
}

namespace {

// Shared by start and startFit: maxEdgePx <= 0 uses scale as given, else the scale is chosen from the element's frame
// (longest edge <= maxEdgePx, never above 1 pixel per DIP). The arguments are checked by the callers.
int startImpl(uint64_t id, Scene& scene, unsigned elementId, double scale, int maxEdgePx)
{
    const int rc = beginExternal(id, VLQT_RESOURCE_KIND_SNAPSHOT);
    if (rc != okRc)
        return rc;
    for (auto& g : s_grabs) // only the newest grab of the id counts
        if (g->id == id)
            g->current = false;

    std::string error;
    QRectF frame;
    QQuickItem* item = target(scene, elementId, frame, error);
    // An offscreen scene (QtTexture) has no native window and is never exposed; it renders in render_batch.
    const bool offscreenScene = offscreen::isScene(scene.window);
    QSize size;
    if (item) {
        if (maxEdgePx > 0) {
            const double longest = std::max(frame.width(), frame.height());
            scale = longest > double(maxEdgePx) ? double(maxEdgePx) / longest : 1.0;
        }
        double w = std::ceil(frame.width() * scale - 0.001), h = std::ceil(frame.height() * scale - 0.001);
        if (maxEdgePx > 0) { // rounding must not exceed the limit
            w = std::min(w, double(maxEdgePx));
            h = std::min(h, double(maxEdgePx));
        }
        if (!offscreenScene && !scene.window->isExposed())
            error = "snapshot: the window is not exposed (hidden or minimized)";
        else if (!(w >= 1 && h >= 1))
            error = "snapshot: element " + std::to_string(elementId) + " has no size";
        else if (w > maxSide || h > maxSide)
            error = "snapshot: " + std::to_string(int64_t(w)) + "x" + std::to_string(int64_t(h)) + " pixels is too large (max "
                + std::to_string(maxSide) + " per side)";
        else
            size = QSize(int(w), int(h));
    }
    QSharedPointer<QQuickItemGrabResult> result;
    QQuickItem* frameSource = nullptr;
    // grabToImage finishes in the next render of the window. render_batch renders only a dirty offscreen scene, so the
    // grab marks it dirty: the next render_batch renders it also when nothing changed.
    if (error.empty() && offscreenScene && !offscreen::requestFrame(scene.window))
        error = "snapshot: the offscreen scene is not bound to a texture (it does not render)";
    if (error.empty()) {
        // grabToImage multiplies the target size with the window's device pixel ratio (300x200 at 1.5 -> 450x300).
        // The contract is pixel size = DIP * scale independent of the DPR (deterministic for export), so ask for
        // size / DPR; onReady scales the rare rounding miss to the exact size.
        const qreal dpr = scene.window->effectiveDevicePixelRatio();
        const QSize request = dpr > 0 && dpr != 1.0 ? QSize(requestSide(size.width(), dpr), requestSide(size.height(), dpr))
                                                    : size;
        if (frame.topLeft() != QPointF(0, 0))
            frameSource = frameTarget(scene, item, frame, size);
        result = grab(frameSource ? frameSource : item, request, offscreenScene);
        if (!result) {
            error = "snapshot: Qt refused the grab of element " + std::to_string(elementId) + " (details in the log)";
            delete frameSource; // never rendered
        }
    }
    if (!error.empty()) {
        finishExternal(id, nullptr, 0, error);
        return okRc;
    }

    // Qt Quick software backend (Linux offscreen / CI, QT_QUICK_BACKEND=software): its layer renders into a QPixmap of
    // the screen format (RGB32, no alpha), so the snapshot is opaque and transparent areas are black. No public API
    // changes that target; documented in vlqt.h, logged once.
    static bool s_softwareWarned = false;
    if (!s_softwareWarned && scene.window->rendererInterface()
        && scene.window->rendererInterface()->graphicsApi() == QSGRendererInterface::Software) {
        s_softwareWarned = true;
        qCWarning(lcRes, "snapshot: Qt Quick software backend, snapshots have no alpha (transparent areas are opaque black)");
    }

    auto g = std::make_unique<Grab>();
    g->id = id;
    g->result = result;
    g->item = item;
    g->frameSource = frameSource;
    g->window = scene.window;
    g->offscreen = offscreenScene;
    g->size = size;
    g->startMs = steadyMs();
    g->startUs = steadyUs();
    Grab* raw = g.get();
    raw->connection = QObject::connect(result.data(), &QQuickItemGrabResult::ready, result.data(), [raw] { onReady(*raw); });
    s_grabs.push_back(std::move(g));
    qCDebug(lcRes, "%s: snapshot of element %u started (%dx%d)", idText(id).constData(), elementId, size.width(),
        size.height());
    return okRc;
}

} // namespace

int start(uint64_t id, Scene& scene, unsigned elementId, float scale)
{
    if (!(scale > 0.0f) || !std::isfinite(scale)) {
        qCWarning(lcRes, "snapshot: %s: scale %g rejected (must be > 0)", idText(id).constData(), double(scale));
        return badValueRc;
    }
    return startImpl(id, scene, elementId, double(scale), 0);
}

int startFit(uint64_t id, Scene& scene, unsigned elementId, int maxEdgePx)
{
    if (maxEdgePx <= 0) {
        qCWarning(lcRes, "snapshot_fit: %s: maxEdgePx %d rejected (must be > 0)", idText(id).constData(), maxEdgePx);
        return badValueRc;
    }
    return startImpl(id, scene, elementId, 1.0, maxEdgePx);
}

int readPixels(uint64_t id, void* dst, int cap, vlqt_pixel_desc* out, int* written, int* required)
{
    if (const int rc = beginBuffer(dst, cap, written, required); rc != okRc)
        return rc;
    const int k = kindOfId(id, "read_pixels");
    if (k < 0)
        return k;
    if (k != VLQT_RESOURCE_KIND_SNAPSHOT) {
        qCWarning(lcRes, "read_pixels: %s is not a snapshot resource", idText(id).constData());
        return badValueRc;
    }
    const auto work = std::static_pointer_cast<SnapshotContent>(readyContent(id, VLQT_RESOURCE_KIND_SNAPSHOT));
    if (!work || work->image.isNull())
        return noDataRc; // no finished snapshot yet (or Failed)
    const QImage& img = work->image;
    const int rowBytes = img.width() * 4;
    const int64_t size = int64_t(rowBytes) * img.height();
    if (out)
        *out = {img.width(), img.height(), rowBytes, 9, 1};
    if (const int rc = bufferFits(size, cap, written, required); rc != okRc)
        return rc;
    auto* d = static_cast<unsigned char*>(dst);
    if (img.bytesPerLine() == rowBytes) {
        std::memcpy(d, img.constBits(), size_t(size));
    } else {
        for (int y = 0; y < img.height(); y++)
            std::memcpy(d + size_t(y) * rowBytes, img.constScanLine(y), size_t(rowBytes));
    }
    return okRc;
}

void stats(Stats& out)
{
    out = {};
    const int64_t now = steadyUs();
    int64_t newest = -1;
    for (int i = 0; i < s_deliveryCount; i++) {
        const Delivery& d = s_deliveries[i];
        const int64_t age = now - d.atUs;
        if (age > statsWindowUs)
            continue;
        out.inWindow++;
        if (age <= 1'000'000)
            out.perSecond++;
        out.maxMs = std::max(out.maxMs, double(d.durationUs) / 1000.0);
        if (d.atUs > newest) {
            newest = d.atUs;
            out.lastMs = double(d.durationUs) / 1000.0;
        }
    }
}

void endFrame()
{
    if (s_grabs.empty())
        return;
    const int64_t now = steadyMs();
    for (auto& gp : s_grabs) {
        Grab& g = *gp;
        if (g.done)
            continue;
        if (g.current && kindOfId(g.id, nullptr) < 0) { // released during the grab: result dropped
            g.current = false;
            qCDebug(lcRes, "%s: snapshot dropped (released during the grab)", idText(g.id).constData());
        }
        if (!g.window || !g.item) { // Qt never finishes it: delete now
            if (g.current)
                failGrab(g, !g.window ? "snapshot: the window was destroyed during the grab"
                                      : "snapshot: the element was destroyed during the grab");
            g.done = true;
            continue;
        }
        if (g.current && !g.offscreen && !g.window->isExposed())
            failGrab(g, "snapshot: the window is no longer exposed (hidden or minimized during the grab)");
        // Still bound: keeps asking for the frame until the grab is done (a render clears dirty).
        else if (g.current && g.offscreen && !offscreen::requestFrame(g.window))
            failGrab(g, "snapshot: the offscreen scene was unbound during the grab");
        else if (g.current && now - g.startMs > timeoutMs)
            failGrab(g, "snapshot: no result after " + std::to_string(timeoutMs) + " ms ("
                + (g.offscreen ? "the offscreen scene does not render: QtTexture disabled or not drawn)" : "the window does not render)"));
        if (!g.current && now - g.startMs > abandonMs) // given up and Qt still holds it: let it go
            g.done = true;
    }
    s_grabs.erase(std::remove_if(s_grabs.begin(), s_grabs.end(), [](const auto& g) { return g->done; }), s_grabs.end());
}

void shutdown()
{
    if (!s_grabs.empty())
        qCInfo(lcRes, "shutdown: %d snapshot grab(s) dropped", int(s_grabs.size()));
    s_grabs.clear();
}

} // namespace vlqt::res::snapshot
