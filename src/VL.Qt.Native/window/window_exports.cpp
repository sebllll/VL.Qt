// The exports of the scene handle (vlqt.h, "scene" without the perf group, and "window"). Qt GUI thread.
#include "window/scene_handle.h"
#include "window/window.h"
#include "window/window_filters.h"
#include "app/app.h"
#include "app/exports.h"
#include "app/render_gate.h"
#include "app/scene_change.h"
#include "offscreen/offscreen.h"
#include "scene/scene.h"
#include "diagnostics/perf.h"
#include "protocol.h"
#include "vlqt.h"

#include <QtCore/QCoreApplication>
#include <QtGui/QIcon>
#include <QtGui/QImageReader>
#include <QtGui/QSurfaceFormat>
#include <QtQml/QQmlEngine>
#include <QtQuick/QQuickItem>
#include <QtQuick/QQuickWindow>
#include <rhi/qrhi.h> // vlqt_window_get_samples: QRhiSwapChain

#include <memory>
#include <string>
#include <utility>

using vlqt::applyBorderless;
using vlqt::applyFullscreen;
using vlqt::asHandle;
using vlqt::asSceneHandle;
using vlqt::clampWindowSamples;
using vlqt::defaultIcon;
using vlqt::fail;
using vlqt::g_app;
using vlqt::guarded;
using vlqt::guardedGui;
using vlqt::initShared;
using vlqt::installWindowFilters;
using vlqt::newEngine;
using vlqt::placeWindow;
using vlqt::replaceWindow;
using vlqt::SceneHandle;
using vlqt::withScene;
using vlqt::withWindow;

extern "C" {

VLQT_API vlqt_scene* vlqt_window_create(float x, float y, int width, int height, const char* title, int swapInterval)
{
    SceneHandle* result = nullptr;
    guardedGui("vlqt_window_create", [&]() -> int {
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "vlqt_window_create: app not initialized");
        auto w = std::make_unique<SceneHandle>();
        w->engine = newEngine();
        w->window = std::make_unique<QQuickWindow>();
        // Swap interval is read from the requested format when the swap chain is created (first show), so it cannot
        // change afterwards. 0 = Present never waits (default), >0 = VSync.
        // Start from the window's own format: QQuickWindow adds depth 24 + stencil 8 to the default format. Without
        // them the OpenGL default framebuffer has no depth buffer and Qt Quick's opaque pass (front to back, depth
        // test) inverts the stacking order (Linux/GL only, D3D11 always gets a depth-stencil).
        QSurfaceFormat winFmt = w->window->requestedFormat();
        winFmt.setSwapInterval(swapInterval > 0 ? 1 : 0);
        // MSAA: 4 by default, the look of every window before the count became a choice; also fixed per
        // QQuickWindow (vlqt_window_set_samples replaces the window).
        winFmt.setSamples(clampWindowSamples(4, w->window.get()));
        w->window->setFormat(winFmt);
        placeWindow(w->window.get(), x, y, width, height);
        w->window->setTitle(QString::fromUtf8(title ? title : ""));
        w->window->setIcon(defaultIcon());
        w->window->setColor(Qt::black);
        installWindowFilters(w.get(), w->window.get());
        initShared(w.get());
        result = w.release();
        return 0;
    });
    return asHandle(result);
}

VLQT_API void vlqt_scene_destroy(vlqt_scene* p)
{
    if (!p)
        return;
    withScene(p, "vlqt_scene_destroy", [&](SceneHandle& w) -> int {
        delete &w; // the order of its parts: window/scene_handle.h
        QCoreApplication::sendPostedEvents(nullptr, QEvent::DeferredDelete);
        return 0;
    });
}

VLQT_API void vlqt_window_show(vlqt_scene* p)
{
    if (!p)
        return;
    withWindow(p, "vlqt_window_show", VLQT_OK, [&](SceneHandle& w) -> int {
        if (w.mode & VLQT_WINDOW_MODE_FULLSCREEN)
            w.window->showFullScreen(); // show() = showNormal() would drop the state set before show
        else
            w.window->show();
        return 0;
    });
}

VLQT_API int vlqt_window_set_icon(vlqt_scene* p, const char* utf8Path)
{
    return withWindow(p, "vlqt_window_set_icon", VLQT_OK, [&](SceneHandle& w) -> int {
        const QString path = QString::fromUtf8(utf8Path ? utf8Path : "");
        if (path.isEmpty()) {
            w.window->setIcon(defaultIcon());
            return 0;
        }
        // QIcon(file) accepts missing/broken files silently (null icon later), so probe with the image readers first.
        QIcon icon;
        if (QImageReader(path).canRead())
            icon = QIcon(path);
        if (icon.isNull()) {
            // The default icon is shown instead (also replaces a custom icon set before).
            w.window->setIcon(defaultIcon());
            return fail(VLQT_ERR_IO, QStringLiteral("icon not loadable: %1").arg(path));
        }
        w.window->setIcon(icon);
        return 0;
    });
}

VLQT_API int vlqt_window_set_title(vlqt_scene* p, const char* title)
{
    return withWindow(p, "vlqt_window_set_title", VLQT_OK, [&](SceneHandle& w) -> int {
        w.window->setTitle(QString::fromUtf8(title ? title : ""));
        return 0;
    });
}

VLQT_API int vlqt_window_resize(vlqt_scene* p, int width, int height)
{
    // An offscreen scene takes its size from vlqt_offscreen_bind.
    return withWindow(p, "vlqt_window_resize", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& w) -> int {
        w.window->resize(width, height);
        return 0;
    });
}

VLQT_API void vlqt_window_hide(vlqt_scene* p)
{
    if (!p)
        return;
    withWindow(p, "vlqt_window_hide", VLQT_OK, [&](SceneHandle& w) -> int {
        w.window->hide();
        return 0;
    });
}

VLQT_API int vlqt_window_take_close_request(vlqt_scene* p)
{
    return withWindow(p, "vlqt_window_take_close_request", 0, [&](SceneHandle& w) -> int {
        if (!w.closeFilter || !w.closeFilter->requested)
            return 0;
        w.closeFilter->requested = false;
        return 1;
    });
}

VLQT_API int vlqt_window_is_visible(vlqt_scene* p)
{
    return withWindow(p, "vlqt_window_is_visible", 0, [&](SceneHandle& w) -> int {
        return w.window->isVisible() ? 1 : 0;
    });
}

VLQT_API int vlqt_scene_set_debug(vlqt_scene* p, int on)
{
    return withScene(p, "vlqt_scene_set_debug", [&](SceneHandle& w) -> int {
        w.scene->setDebugAll(on != 0);
        w.window->update();
        return 0;
    });
}

VLQT_API int vlqt_scene_get_debug(vlqt_scene* p)
{
    return withScene(p, "vlqt_scene_get_debug", [&](SceneHandle& w) -> int {
        return w.scene->debugAll ? 1 : 0;
    });
}

VLQT_API int vlqt_window_set_mode(vlqt_scene* p, unsigned int mask, unsigned int value)
{
    return withWindow(p, "vlqt_window_set_mode", VLQT_OK, [&](SceneHandle& w) -> int {
        mask &= VLQT_WINDOW_MODE_BORDERLESS | VLQT_WINDOW_MODE_FULLSCREEN;
        const unsigned changed = (w.mode ^ value) & mask;
        w.mode = (w.mode & ~mask) | (value & mask);
        if (changed & VLQT_WINDOW_MODE_BORDERLESS)
            applyBorderless(&w, w.mode & VLQT_WINDOW_MODE_BORDERLESS);
        if (changed & VLQT_WINDOW_MODE_FULLSCREEN)
            applyFullscreen(&w, w.mode & VLQT_WINDOW_MODE_FULLSCREEN);
        return 0;
    });
}

VLQT_API int vlqt_window_set_samples(vlqt_scene* p, int requested)
{
    // An offscreen scene takes its sample count from vlqt_offscreen_bind.
    return withWindow(p, "vlqt_window_set_samples", VLQT_ERR_UNSUPPORTED, [&](SceneHandle& w) -> int {
        const int samples = clampWindowSamples(requested, w.window.get());
        QSurfaceFormat f = w.window->requestedFormat();
        if (qMax(1, f.samples()) == samples)
            return samples;
        f.setSamples(samples);
        // No native window yet (never shown): no swap chain either, Qt reads the format at the first expose.
        if (!w.window->handle())
            w.window->setFormat(f);
        else
            replaceWindow(&w, f);
        return samples;
    });
}

VLQT_API int vlqt_window_set_keys(vlqt_scene* p, int on)
{
    // An offscreen scene has no keyboard focus; its perf monitor keeps the filter, which never sees a key.
    return withWindow(p, "vlqt_window_set_keys", VLQT_OK, [&](SceneHandle& w) -> int {
        w.perf->setKeysEnabled(on != 0);
        return 0;
    });
}

VLQT_API int vlqt_window_get_samples(vlqt_scene* p)
{
    return withWindow(p, "vlqt_window_get_samples", 0, [&](SceneHandle& w) -> int {
        if (const QRhiSwapChain* sc = w.window->swapChain())
            return sc->sampleCount();
        return qMax(1, w.window->requestedFormat().samples());
    });
}

VLQT_API int vlqt_window_get_mode(vlqt_scene* p)
{
    return withWindow(p, "vlqt_window_get_mode", 0, [&](SceneHandle& w) -> int {
        return int(w.mode);
    });
}

VLQT_API int vlqt_scene_get_metrics(vlqt_scene* p, vlqt_scene_metrics* out)
{
    return withScene(p, "vlqt_scene_get_metrics", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_metrics: null out");
        // The root is sized to the window (Scene::attachRoot), also offscreen where the content item stays 0 x 0.
        const QQuickItem* root = w.scene->root;
        out->width = float(root->width());
        out->height = float(root->height());
        out->dpr = w.isOffscreen() ? 1.f : float(w.window->effectiveDevicePixelRatio());
        return VLQT_OK;
    });
}

VLQT_API int vlqt_scene_apply(vlqt_scene* p, const unsigned char* buf, int len)
{
    return withScene(p, "vlqt_scene_apply", [&](SceneHandle& w) -> int {
        std::string err;
        const int rc = w.scene->apply(buf, len, err);
        if (rc != 0)
            vlqt::setLastError(std::move(err));
        w.scene->updateDebug(); // show/hide/repaint the debug overlay, no-op while debug was never used
        // Commands changed the scene: request the render ourselves. Inside a MultiEffect source (layer)
        // Qt stops requesting updates for changed items after the first frames; the RenderGate keeps it at one per pump.
        if (len > 0) {
            w.window->update();
            w.changes->markChanged(); // also changes without a dirty item (clear color)
            if (w.offscreen)
                vlqt::offscreen::invalidatePolish(w.offscreen.get()); // the last batch's polish has not seen these commands
        }
        return rc;
    });
}

VLQT_API int vlqt_scene_get_apply_error(vlqt_scene* p, vlqt_apply_error* out)
{
    return withScene(p, "vlqt_scene_get_apply_error", [&](SceneHandle& w) -> int {
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "vlqt_scene_get_apply_error: null out");
        const vlqt::ApplyError& e = w.scene->lastApplyError;
        *out = vlqt_apply_error{e.rc, e.command, e.id, e.op, e.prop, e.reason.c_str()};
        return VLQT_OK;
    });
}

VLQT_API int vlqt_scene_poll(vlqt_scene* p, unsigned char* out, int cap, int* written, int* required)
{
    // A buffer export: both counts are set before the handle check.
    return guarded([&]() -> int {
        if (const int rc = vlqt::beginBuffer(out, cap, written, required); rc != VLQT_OK)
            return rc;
        if (const int rc = vlqt::checkScene(p, "vlqt_scene_poll"); rc != VLQT_OK)
            return rc;
        SceneHandle& w = *asSceneHandle(p);
        // No render in this pump (Wayland: UpdateRequest only on the compositor's frame callback; not exposed,
        // e.g. minimized): Qt has not polished, layouts would report stale geometry. An offscreen scene has no gate: the
        // render_batch that rendered it polished it, else the poll does (it costs a call per element when something changed).
        // Not for the retry after a too-small buffer: the scene only drains then.
        if (!w.scene->eventsCollected) {
            const bool polished = w.offscreen ? vlqt::offscreen::polishedSinceLastPoll(w.offscreen.get()) : w.gate && w.gate->rendered();
            if (!polished)
                w.scene->polish();
        }
        return w.scene->poll(out, cap, written, required);
    });
}

// Change count: the tooltip previews snapshot a scene again only when it changed.
VLQT_API long long vlqt_scene_get_change_count(vlqt_scene* p)
{
    long long count = 0;
    const int rc = withScene(p, "vlqt_scene_get_change_count", [&](SceneHandle& w) -> int {
        count = w.changes->count();
        return VLQT_OK;
    });
    return rc < 0 ? rc : count;
}

} // extern "C"
