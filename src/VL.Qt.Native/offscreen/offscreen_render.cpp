// The frames of bound offscreen scenes: render_batch orders Qt's GPU against the foreign context with the two fences, on
// the main thread or per render thread. Windows only (CMakeLists.txt).
#include "offscreen/offscreen_scene.h"
#include "app/exports.h"
#include "device/device.h"
#include "vlqt.h"

#include <QtGui/QGuiApplication>
#include <QtQuick/QQuickRenderControl>
#include <QtQuick/QQuickWindow>
#include <QtQuick/QSGRendererInterface>

#include <array>
#include <exception>
#include <vector>

#include <d3d11_4.h>
#include <wrl/client.h>

namespace vlqt::offscreen {

namespace {

using Microsoft::WRL::ComPtr;

// One frame of a bound, dirty scene, ordered against the foreign context by the fences (vlqt.h).
void render(OffscreenScene* s, ID3D11DeviceContext4* foreign)
{
    // The commands of the previous frame that read the texture are already submitted on the foreign context.
    foreign->Signal(s->foreignRead, ++s->readValue);
    // Before beginFrame: QRhi D3D11 submits the frame's commands to the immediate context only in endFrame.
    s->qtContext->Wait(s->qtRead, s->readValue);

    QQuickRenderControl* rc = s->renderControl;
    rc->polishItems();
    // Polish's own changes are in this frame; a change made while syncing or rendering renders next frame.
    s->dirty = false;
    rc->beginFrame();
    rc->sync();
    rc->render();
    rc->endFrame();
    if (s->linearPass.source)
        vlqt::linear_pass::run(s->qtContext, s->linearPass);
    // TODO: GPU time of the frame: diagnostics/perf_monitor.cpp afterRendering reads it only from a swap chain; a scene has
    // renderControl->commandBuffer() (and its QRhi needs EnableTimestamps).

    s->qtContext->Signal(s->qtWrite, ++s->writeValue);
    // Submits the frame and the signal; without it the foreign GPU would wait forever.
    s->qtContext->Flush();
    foreign->Wait(s->foreignWrite, s->writeValue);
    s->renders++;
}

// The scenes of one render thread in one batch, with the write value each one signals. Two lists per thread: the
// main thread fills one while the render thread may still render the other (at most one batch is in flight per
// thread, see renderThreaded). Main thread; the render thread reads the list of its jobs, which captures its address:
// the batches live in a fixed array (s_threadBatches), so no later batch moves them.
struct ThreadBatch
{
    render_thread::RenderThread* thread = nullptr; // set the first time a scene of it renders
    std::vector<std::pair<OffscreenScene*, unsigned long long>> lists[2];
    int current = 0;
    bool used = false;     // in the batch being built
    bool software = false; // a scene of it is bound to a software device
    bool posted = false;   // its render job of the last batch may still run (finishPostedRenders)
};
// One per render thread, at the index of the thread in the pool (RenderThread::index). A fixed address: a posted render
// job may run after the next batch began, which never adds or moves a batch.
std::array<ThreadBatch, render_thread::maxThreads> s_threadBatches;

// Waits until every render job posted by the last batch ran. The scenes of all threads share Qt's immediate context,
// and the foreign context waits for the write values of the last batch before it signals the read values of the next
// one: a Wait for a read value of the next batch queued on Qt's context ahead of a frame of the last batch makes Qt's
// GPU wait for the foreign GPU, which waits for that frame, while its render thread waits in endFrame for Qt's GPU.
// So no scene of the next batch begins (Wait + beginFrame) before every frame of the last one is submitted. Main thread.
void finishPostedRenders()
{
    for (ThreadBatch& b : s_threadBatches) {
        if (!b.posted)
            continue;
        b.thread->drain();
        b.posted = false;
    }
}

// beginFrame (after Qt's GPU waited for the read value) and sync of one scene. Render thread, blocking job.
void beginAndSync(OffscreenScene* s)
{
    try {
        {
            vlqt::device::ContextLock lock(s->qtContext);
            // Before beginFrame: QRhi D3D11 submits the frame's commands to the immediate context only in endFrame.
            s->qtContext->Wait(s->qtRead, s->readValue);
            s->renderControl->beginFrame();
        }
        s->renderControl->sync();
    } catch (const std::exception& e) {
        qCWarning(lcOffscreen, "offscreen scene %p: sync failed: %s", static_cast<void*>(s), e.what());
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: sync failed", static_cast<void*>(s));
    }
}

// render, endFrame, then the write value: signalled on every path, the foreign context already waits for it (a value
// never signalled would hang the foreign GPU). Render thread.
void renderAndSignal(OffscreenScene* s, unsigned long long writeValue)
{
    try {
        s->renderControl->render();
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: render failed", static_cast<void*>(s));
    }
    vlqt::device::ContextLock lock(s->qtContext);
    try {
        s->renderControl->endFrame();
    } catch (...) {
        qCWarning(lcOffscreen, "offscreen scene %p: endFrame failed", static_cast<void*>(s));
    }
    if (s->linearPass.source)
        vlqt::linear_pass::run(s->qtContext, s->linearPass);
    // TODO: GPU time of the frame (as in render).
    s->qtContext->Signal(s->qtWrite, writeValue);
    // Submits the frame and the signal; without it the foreign GPU would wait forever.
    s->qtContext->Flush();
    s->renders++;
}

// The threaded batch: per scene the read signal on the foreign context and polish (main thread); one
// flush of the foreign context, so Qt's GPU need not wait for the host's next flush; per render thread one blocking job
// that waits for the read value, begins the frame and syncs its scenes (the main thread waits, items stay untouched);
// the foreign context's GPU wait for each write value (it waits for a value signalled later, its CPU never waits);
// then per render thread one posted job that renders, ends the frame, signals the write value and flushes. The next
// batch waits for these renders before anything else (finishPostedRenders): at most one frame in flight per scene,
// and the main thread runs ahead of the render threads by at most one batch. Software devices: the job is blocking
// and the wait is queued after it.
void renderThreaded(const std::vector<OffscreenScene*>& scenes, ID3D11DeviceContext4* foreign)
{
    for (OffscreenScene* s : scenes) {
        foreign->Signal(s->foreignRead, ++s->readValue);
        s->renderControl->polishItems();
        // Polish's own changes are in this frame; a change made while syncing or rendering renders next frame.
        s->dirty = false;
    }
    foreign->Flush();

    for (ThreadBatch& b : s_threadBatches)
        b.used = false;
    for (OffscreenScene* s : scenes) {
        ThreadBatch& b = s_threadBatches[s->thread->index()];
        b.thread = s->thread;
        if (!b.used) {
            b.used = true;
            b.software = false;
            b.current ^= 1;
            b.lists[b.current].clear();
        }
        b.lists[b.current].push_back({ s, ++s->writeValue });
        b.software = b.software || s->software;
    }

    for (ThreadBatch& b : s_threadBatches) {
        if (!b.used)
            continue;
        const auto& list = b.lists[b.current];
        b.thread->call([&list] {
            for (const auto& [s, writeValue] : list)
                beginAndSync(s);
        });
    }
    for (const ThreadBatch& b : s_threadBatches)
        if (b.used && !b.software)
            for (const auto& [s, writeValue] : b.lists[b.current])
                foreign->Wait(s->foreignWrite, writeValue);
    for (ThreadBatch& b : s_threadBatches) {
        if (!b.used)
            continue;
        const auto* list = &b.lists[b.current];
        const auto job = [list] {
            for (const auto& [s, writeValue] : *list)
                renderAndSignal(s, writeValue);
        };
        if (b.software) {
            b.thread->call(job);
        } else {
            b.thread->post(job);
            b.posted = true;
        }
    }
    for (const ThreadBatch& b : s_threadBatches)
        if (b.used && b.software)
            for (const auto& [s, writeValue] : b.lists[b.current])
                foreign->Wait(s->foreignWrite, writeValue);
}

} // namespace

int renderBatch(void* context, vlqt_scene* const* scenes, int count, double driverSeconds)
{
    if (!qGuiApp || QQuickWindow::graphicsApi() != QSGRendererInterface::Direct3D11)
        return fail(VLQT_ERR_UNSUPPORTED, "render_batch: needs Direct3D 11 and an initialized app");
    vlqt_app_advance_animations(driverSeconds);
    finishPostedRenders();
    ComPtr<ID3D11DeviceContext4> foreign;
    int rendered = 0;
    static std::vector<OffscreenScene*> threaded; // reused: no allocation per frame
    threaded.clear();
    for (int i = 0; i < count; i++) {
        OffscreenScene* s = stateOf(scenes[i]);
        if (!s || !s->bound || !s->dirty)
            continue;
        if (!foreign) {
            HRESULT hr = static_cast<ID3D11DeviceContext*>(context)->QueryInterface(IID_PPV_ARGS(foreign.GetAddressOf()));
            if (FAILED(hr))
                return fail(VLQT_ERR_BAD_ARGUMENT, "render_batch: context is no ID3D11DeviceContext4", hr);
        }
        // Lost but not rebuilt yet: the next pump (device::checkLost) rebuilds and unbinds every scene.
        ID3D11Device* qt = qtDevice(s);
        if (!qt || FAILED(qt->GetDeviceRemovedReason()))
            return fail(VLQT_ERR_DEVICE_LOST, "render_batch: QtNative's device is lost");
        if (s->thread)
            threaded.push_back(s);
        else
            render(s, foreign.Get());
        rendered++;
    }
    if (!threaded.empty())
        renderThreaded(threaded, foreign.Get());
    return rendered;
}

} // namespace vlqt::offscreen
