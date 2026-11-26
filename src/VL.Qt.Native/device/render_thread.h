/* render_thread.h - the Qt render threads of offscreen scenes: a pool shared by every scene of the process, set by the pin
 * Render Threads (setThreadCount; 0 = the main thread). Threads: a job runs on its render thread, everything declared
 * here is called from the main thread. Owner: the pool, whose threads live until process end, like the shared device
 * (device.h). Design: docs/design/render-thread.md.
 */
#pragma once

#include "device/context_lock.h"

#include <QtCore/QObject>
#include <QtCore/QThread>

#include <exception>
#include <memory>
#include <utility>

class QQuickWindow;

namespace vlqt::render_thread {

inline constexpr int maxThreads = 16;

// Logs the exception of a posted job (it has no caller to rethrow to). Any thread.
void logJobError(std::exception_ptr error);

class RenderThread
{
public:
    explicit RenderThread(int index);
    RenderThread(const RenderThread&) = delete;
    RenderThread& operator=(const RenderThread&) = delete;

    // Position in the pool, the one the constructor got: 0 ... maxThreads - 1.
    int index() const { return m_index; }
    QThread* thread() { return &m_thread; }
    bool isCurrent() const { return QThread::currentThread() == &m_thread; }

    // Runs job on this thread after every job queued before and returns at once. An exception of job is logged.
    template <class F> void post(F&& job)
    {
        QMetaObject::invokeMethod(m_context, [job = std::forward<F>(job)]() mutable {
            try {
                job();
            } catch (...) {
                logJobError(std::current_exception());
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
        // The job may take the context lock: the waiting main thread must not hold it (context_lock.h, lock order).
        vlqt::device::releaseFrameLocksBeforeWait();
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
    int m_index;
    QThread m_thread;
    QObject* m_context = nullptr; // lives on m_thread: the receiver of the jobs
};

// Where the jobs of one offscreen scene run: on its render thread, or inline on the calling thread (the main thread)
// for a scene without one (Render Threads 0). call and post mean the same for both, so one frame sequence serves both
// modes (offscreen_render.cpp): inline, a posted job runs at once and its exception is logged like on a thread.
class Executor
{
public:
    Executor() = default; // inline
    explicit Executor(RenderThread* thread) : m_thread(thread) {}

    // The render thread; nullptr when inline.
    RenderThread* thread() const { return m_thread; }
    // 0 ... maxThreads - 1: the pool index of the thread; maxThreads when inline. One slot per executor.
    int index() const { return m_thread ? m_thread->index() : maxThreads; }

    // RenderThread::call, or job here.
    template <class F> void call(F&& job)
    {
        if (m_thread)
            m_thread->call(std::forward<F>(job));
        else
            job();
    }

    // RenderThread::post, or job here at once.
    template <class F> void post(F&& job)
    {
        if (m_thread) {
            m_thread->post(std::forward<F>(job));
            return;
        }
        try {
            job();
        } catch (...) {
            logJobError(std::current_exception());
        }
    }

    // RenderThread::drain; nothing to wait for inline.
    void drain()
    {
        if (m_thread)
            m_thread->drain();
    }

private:
    RenderThread* m_thread = nullptr;
};

// Sets the pool size (pin Render Threads): 0 = the scenes created afterwards render on the main thread, n = on the
// first n render threads (round robin), created now if the pool is smaller; the pool never shrinks. Scenes created
// before keep their executor. Logs a change. Returns VLQT_OK, VLQT_ERR_BAD_ARGUMENT (count < 0 or > maxThreads); a failure sets vlqt_last_error.
// Main thread.
int setThreadCount(int count);

// The executor of a new scene: the next thread of the pool, round robin, or inline with a pool size of 0. Without
// setThreadCount the size is 1 (logged at the first scene). Main thread.
Executor assign();

// Waits until every render thread of the pool ran the jobs queued before (none without a pool). Main thread.
void drainAll();

// Drops resource where window renders: for the window of an offscreen scene with a render thread in a job posted there,
// so it goes after the render still running with it (the next blocking job of the thread, sync, unbind or destroy,
// waits for it); for any other window (on-screen, a scene on the main thread, nullptr) at once. Main thread.
void retire(QQuickWindow* window, std::shared_ptr<void> resource);

} // namespace vlqt::render_thread
