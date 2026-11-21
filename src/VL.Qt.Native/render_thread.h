/* render_thread.h - the Qt render threads of offscreen scenes (phase 5; design: docs/decisions/render-texture.md,
 * "Qt render thread").
 *
 * A pool of threads with a Qt event loop, shared by every offscreen scene of the process (scenes of every app host).
 * Its size is a QtNative setting: vlqt_offscreen_set_render_threads (any time, for the scenes created afterwards), else
 * the environment variable VLQT_OFFSCREEN_THREAD (0 = main thread, n = n threads), else 1. The pool only grows: threads
 * are created when a scene first needs them and live on when the size gets smaller. A scene gets its thread at create
 * (round robin) and keeps it: its scene graph and QRhi live there (QQuickRenderControl::prepareThread). Jobs of one thread run in
 * queue order: a blocking job (call) therefore runs after every job posted before it, which is how the main thread
 * drains a thread before it unbinds or destroys a scene.
 *
 * The threads live until process end, like the shared device (device.h): stopping them at exit would run after Qt.
 *
 * Scene graph objects of a scene (a QSGTexture and the QRhiTexture under it) belong to its render thread: an item that
 * holds one from the main thread lets it go through retire, never by dropping the last reference itself.
 */
#pragma once

#include "device/device.h"

#include <QtCore/QObject>
#include <QtCore/QThread>

#include <exception>
#include <memory>
#include <utility>

class QQuickWindow;

namespace vlqt::render_thread {

class RenderThread
{
public:
    explicit RenderThread(int index);
    RenderThread(const RenderThread&) = delete;
    RenderThread& operator=(const RenderThread&) = delete;

    QThread* thread() { return &m_thread; }
    bool isCurrent() const { return QThread::currentThread() == &m_thread; }

    // Runs job on this thread after every job queued before and returns at once. An exception of job is logged.
    template <class F> void post(F&& job)
    {
        QMetaObject::invokeMethod(m_context, [job = std::forward<F>(job)]() mutable {
            try {
                job();
            } catch (...) {
                logError(std::current_exception());
            }
        }, Qt::QueuedConnection);
    }

    // Runs job on this thread after every job queued before and waits for it; directly when called on this thread.
    // An exception of job is rethrown here.
    template <class F> void call(F&& job)
    {
        if (isCurrent()) {
            job();
            return;
        }
        // The job may take the context lock: the waiting main thread must not hold it (device.h, lock order).
        vlqt::device::releaseFrameLocks();
        std::exception_ptr error;
        QMetaObject::invokeMethod(m_context, [&job, &error] {
            try {
                job();
            } catch (...) {
                error = std::current_exception();
            }
        }, Qt::BlockingQueuedConnection);
        if (error)
            std::rethrow_exception(error);
    }

    // Waits until every job queued before ran.
    void drain() { call([] {}); }

private:
    static void logError(std::exception_ptr error);

    QThread m_thread;
    QObject* m_context = nullptr; // lives on m_thread: the receiver of the jobs
};

// Sets the pool size for the scenes created afterwards: 0 = they render on the main thread, n = on the first n render
// threads (round robin). Scenes created before keep theirs. Returns VLQT_OK, VLQT_ERR_BAD_ARGUMENT (count < 0 or >
// maxThreads); a failure sets vlqt_last_error. Main thread.
int setThreadCount(int count);
inline constexpr int maxThreads = 16;

// The render thread of a new scene: the next one of the pool, round robin; nullptr = main thread. Without
// setThreadCount the first call takes the pool size from VLQT_OFFSCREEN_THREAD, else 1, and logs it. Main thread.
RenderThread* assign();

// Waits until every render thread of the pool ran the jobs queued before (none without a pool). Main thread.
void drainAll();

// Drops resource where window renders: for the window of an offscreen scene with a render thread in a job posted there,
// so it goes after the render still running with it (the next blocking job of the thread, sync, unbind or destroy,
// waits for it); for any other window (on-screen, a scene on the main thread, nullptr) at once. Main thread.
void retire(QQuickWindow* window, std::shared_ptr<void> resource);

} // namespace vlqt::render_thread
