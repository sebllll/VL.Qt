// The exports of the scene handle (vlqt.h, "scene" without the perf group, and "window"). Qt GUI thread.
#include "window/scene_handle.h"
#include "window/window.h"
#include "window/window_filters.h"
#include "app/app.h"
#include "app/exports.h"
#include "app/render_gate.h"
#include "scene/scene.h"
#include "offscreen/offscreen.h"
#include "perf.h"
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

#include <string>
#include <utility>

using vlqt::applyBorderless;
using vlqt::applyFullscreen;
using vlqt::asHandle;
using vlqt::asWindow;
using vlqt::clampWindowSamples;
using vlqt::defaultIcon;
using vlqt::fail;
using vlqt::g_app;
using vlqt::guarded;
using vlqt::initShared;
using vlqt::installWindowFilters;
using vlqt::newEngine;
using vlqt::placeWindow;
using vlqt::replaceWindow;
using vlqt::SceneHandle;

extern "C" {

VLQT_API vlqt_scene* vlqt_window_create(float x, float y, int width, int height, const char* title, int swapInterval)
{
    SceneHandle* result = nullptr;
    guarded([&]() -> int {
        if (!g_app)
            return fail(VLQT_ERR_NOT_INITIALIZED, "window_create: app not initialized");
        auto* w = new SceneHandle();
        w->engine = newEngine();
        w->window = new QQuickWindow();
        // Swap interval is read from the requested format when the swap chain is created (first show), so it cannot
        // change afterwards. 0 = Present never waits (default), >0 = VSync.
        // Start from the window's own format: QQuickWindow adds depth 24 + stencil 8 to the default format. Without
        // them the OpenGL default framebuffer has no depth buffer and Qt Quick's opaque pass (front to back, depth
        // test) inverts the stacking order (Linux/GL only, D3D11 always gets a depth-stencil).
        QSurfaceFormat winFmt = w->window->requestedFormat();
        winFmt.setSwapInterval(swapInterval > 0 ? 1 : 0);
        // MSAA: 4 by default, the look of every window before the count became a choice; also fixed per
        // QQuickWindow (vlqt_window_set_samples replaces the window).
        winFmt.setSamples(clampWindowSamples(4, w->window));
        w->window->setFormat(winFmt);
        placeWindow(w->window, x, y, width, height);
        w->window->setTitle(QString::fromUtf8(title ? title : ""));
        w->window->setIcon(defaultIcon());
        w->window->setColor(Qt::black);
        installWindowFilters(w, w->window);
        initShared(w);
        result = w;
        return 0;
    });
    return asHandle(result);
}

VLQT_API void vlqt_scene_destroy(vlqt_scene* p)
{
    guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return 0;
        // A scene unbinds first: its render target and opened texture belong to the QRhi of its render control.
        if (w->offscreen)
            vlqt::offscreen::release(w->offscreen);
        delete w->perf;
        delete w->scene;
        delete w->window;
        delete w->engine;
        if (w->offscreen)
            vlqt::offscreen::destroy(w->offscreen); // after its QQuickWindow: the render control and its QRhi
        delete w;
        QCoreApplication::sendPostedEvents(nullptr, QEvent::DeferredDelete);
        return 0;
    });
}

VLQT_API void vlqt_window_show(vlqt_scene* p)
{
    guarded([&]() -> int {
        if (auto* w = asWindow(p); w && !w->offscreen) {
            if (w->mode & VLQT_WINDOW_MODE_FULLSCREEN)
                w->window->showFullScreen(); // show() = showNormal() would drop the state set before show
            else
                w->window->show();
        }
        return 0;
    });
}

VLQT_API int vlqt_window_set_icon(vlqt_scene* p, const char* utf8Path)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (w->offscreen)
            return 0;
        const QString path = QString::fromUtf8(utf8Path ? utf8Path : "");
        if (path.isEmpty()) {
            w->window->setIcon(defaultIcon());
            return 0;
        }
        // QIcon(file) accepts missing/broken files silently (null icon later), so probe with the image readers first.
        QIcon icon;
        if (QImageReader(path).canRead())
            icon = QIcon(path);
        if (icon.isNull()) {
            // The default icon is shown instead (also replaces a custom icon set before).
            w->window->setIcon(defaultIcon());
            return fail(VLQT_ERR_IO, QStringLiteral("icon not loadable: %1").arg(path));
        }
        w->window->setIcon(icon);
        return 0;
    });
}

VLQT_API int vlqt_window_set_title(vlqt_scene* p, const char* title)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (!w->offscreen)
            w->window->setTitle(QString::fromUtf8(title ? title : ""));
        return 0;
    });
}

VLQT_API int vlqt_window_resize(vlqt_scene* p, int width, int height)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (w->offscreen)
            return fail(VLQT_ERR_UNSUPPORTED, "resize: an offscreen scene takes its size from vlqt_offscreen_bind");
        w->window->resize(width, height);
        return 0;
    });
}

VLQT_API void vlqt_window_hide(vlqt_scene* p)
{
    guarded([&]() -> int {
        if (auto* w = asWindow(p); w && !w->offscreen)
            w->window->hide();
        return 0;
    });
}

VLQT_API int vlqt_window_take_close_request(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "take_close_request: null window");
        if (!w->closeFilter || !w->closeFilter->requested) // a scene has no close filter
            return 0;
        w->closeFilter->requested = false;
        return 1;
    });
}

VLQT_API int vlqt_window_is_visible(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "is_visible: null window");
        return !w->offscreen && w->window->isVisible() ? 1 : 0;
    });
}

VLQT_API int vlqt_scene_set_debug(vlqt_scene* p, int on)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        w->scene->setDebugAll(on != 0);
        w->window->update();
        return 0;
    });
}

VLQT_API int vlqt_scene_get_debug(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "get_debug: null window");
        return w->scene->debugAll ? 1 : 0;
    });
}

VLQT_API int vlqt_window_set_mode(vlqt_scene* p, unsigned int mask, unsigned int value)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (w->offscreen)
            return 0;
        mask &= VLQT_WINDOW_MODE_BORDERLESS | VLQT_WINDOW_MODE_FULLSCREEN;
        const unsigned changed = (w->mode ^ value) & mask;
        w->mode = (w->mode & ~mask) | (value & mask);
        if (changed & VLQT_WINDOW_MODE_BORDERLESS)
            applyBorderless(w, w->mode & VLQT_WINDOW_MODE_BORDERLESS);
        if (changed & VLQT_WINDOW_MODE_FULLSCREEN)
            applyFullscreen(w, w->mode & VLQT_WINDOW_MODE_FULLSCREEN);
        return 0;
    });
}

VLQT_API int vlqt_window_set_samples(vlqt_scene* p, int requested)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "null window");
        if (w->offscreen)
            return fail(VLQT_ERR_UNSUPPORTED, "set_samples: an offscreen scene takes its sample count from vlqt_offscreen_bind");
        const int samples = clampWindowSamples(requested, w->window);
        QSurfaceFormat f = w->window->requestedFormat();
        if (qMax(1, f.samples()) == samples)
            return samples;
        f.setSamples(samples);
        // No native window yet (never shown): no swap chain either, Qt reads the format at the first expose.
        if (!w->window->handle())
            w->window->setFormat(f);
        else
            replaceWindow(w, f);
        return samples;
    });
}

VLQT_API int vlqt_window_get_samples(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "samples: null window");
        if (w->offscreen)
            return 0;
        if (const QRhiSwapChain* sc = w->window->swapChain())
            return sc->sampleCount();
        return qMax(1, w->window->requestedFormat().samples());
    });
}

VLQT_API int vlqt_window_get_mode(vlqt_scene* p)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "get_mode: null window");
        return int(w->mode);
    });
}

VLQT_API int vlqt_scene_get_metrics(vlqt_scene* p, vlqt_scene_metrics* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "get_scene_metrics: null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "get_scene_metrics: null out");
        // The root is sized to the window (Scene::attachRoot), also offscreen where the content item stays 0 x 0.
        const QQuickItem* root = w->scene->root;
        out->width = float(root->width());
        out->height = float(root->height());
        out->dpr = w->offscreen ? 1.f : float(w->window->effectiveDevicePixelRatio());
        return VLQT_OK;
    });
}

VLQT_API int vlqt_scene_apply(vlqt_scene* p, const unsigned char* buf, int len)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "apply: null window");
        std::string err;
        const int rc = w->scene->apply(buf, len, err);
        if (rc != 0)
            vlqt::setLastError(std::move(err));
        w->scene->updateDebug(); // show/hide/repaint the debug overlay, no-op while debug was never used
        // Commands changed the scene: request the render ourselves. Inside a MultiEffect source (layer)
        // Qt stops requesting updates for changed items after the first frames; the UpdateGate keeps it at one per pump.
        if (len > 0) {
            w->window->update();
            w->gate->markChanged(); // also changes without a dirty item (clear color)
        }
        return rc;
    });
}

VLQT_API int vlqt_scene_get_apply_error(vlqt_scene* p, vlqt_apply_error* out)
{
    return guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "get_apply_error: null window");
        if (!out)
            return fail(VLQT_ERR_BAD_ARGUMENT, "get_apply_error: null out");
        const vlqt::ApplyError& e = w->scene->lastApplyError;
        *out = vlqt_apply_error{e.rc, e.command, e.id, e.op, e.prop, e.reason.c_str()};
        return VLQT_OK;
    });
}

VLQT_API int vlqt_scene_poll(vlqt_scene* p, unsigned char* out, int cap, int* written, int* required)
{
    return guarded([&]() -> int {
        if (const int rc = vlqt::beginBuffer(out, cap, written, required); rc != VLQT_OK)
            return rc;
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "poll: null window");
        // No render in this pump (Wayland: UpdateRequest only on the compositor's frame callback; not exposed,
        // e.g. minimized): Qt has not polished, layouts would report stale geometry.
        if (!w->gate->rendered())
            w->scene->polish();
        return w->scene->poll(out, cap, written, required);
    });
}

// Change count: the tooltip previews snapshot a scene again only when it changed.
VLQT_API long long vlqt_scene_get_change_count(vlqt_scene* p)
{
    long long count = 0;
    const int rc = guarded([&]() -> int {
        auto* w = asWindow(p);
        if (!w)
            return fail(VLQT_ERR_NULL_HANDLE, "change_count: null window");
        count = w->gate->changeCount();
        return VLQT_OK;
    });
    return rc < 0 ? rc : count;
}

} // extern "C"
