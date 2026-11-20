/* render_thread.h - the Qt render threads of offscreen scenes (phase 5; design: docs/decisions/render-texture.md,
 * "Qt render thread").
 *
 * A pool of threads with a Qt event loop, shared by every offscreen scene of the process (scenes of every app host).
 * Its size is a QtNative setting: vlqt_offscreen_set_threaded (before the first scene), else the environment variable
 * VLQT_OFFSCREEN_THREAD (0 = main thread, n = n threads), else 1. A scene gets its thread at create (round robin) and
 * keeps it: its scene graph and QRhi live there (QQuickRenderControl::prepareThread). Jobs of one thread run in
 * queue order: a blocking job (call) therefore runs after every job posted before it, which is how the main thread
 * drains a thread before it unbinds or destroys a scene.
 *
 * The threads live until process end, like the shared device (device.h): stopping them at exit would run after Qt.
 */
#pragma once

#include "device.h"

#include <QtCore/QObject>
#include <QtCore/QThread>

#include <exception>
#include <utility>

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

// Sets the pool size: 0 = offscreen scenes render on the main thread, n = on n render threads. Only before the first
// assign; afterwards only the size in use is accepted. Returns a vlqt_offscreen_result code (protocol.h): OK,
// BAD_ARGUMENT (count < 0 or > maxThreads), NOT_SUPPORTED (too late; message in error). Main thread.
int setThreadCount(int count, const char** error);
inline constexpr int maxThreads = 16;

// The render thread of a new scene: the next one of the pool, round robin; nullptr = main thread. The first call fixes
// the pool size (setThreadCount, else VLQT_OFFSCREEN_THREAD, else 1) and logs it. Main thread.
RenderThread* assign();

// Waits until every render thread of the pool ran the jobs queued before (none without a pool). Main thread.
void drainAll();

} // namespace vlqt::render_thread
