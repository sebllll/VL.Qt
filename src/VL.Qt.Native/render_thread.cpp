#include "render_thread.h"

#include "app/last_error.h"
#include "threadname.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>

#include <cstdlib>
#include <cstring>
#include <vector>

namespace vlqt::render_thread {

namespace {

Q_LOGGING_CATEGORY(lcRenderThread, "vlqt.offscreen")

int s_count = -1;      // the pool size for new scenes, -1 = not chosen yet (no setThreadCount, no assign)
int s_next = 0;        // round robin
std::vector<RenderThread*> s_pool; // created at the first use, never deleted (render_thread.h)

// VLQT_OFFSCREEN_THREAD: 0 = main thread, n = n threads; unset or empty = 1.
int fromEnvironment()
{
    const char* v = std::getenv("VLQT_OFFSCREEN_THREAD");
    if (!v || !*v)
        return 1;
    char* end = nullptr;
    const long n = std::strtol(v, &end, 10);
    if (*end || n < 0 || n > maxThreads) {
        qCWarning(lcRenderThread, "VLQT_OFFSCREEN_THREAD=%s is no thread count 0 ... %d, using 1 render thread", v, maxThreads);
        return 1;
    }
    return int(n);
}

} // namespace

RenderThread::RenderThread(int index)
{
    // The same name for Qt and for the OS (profilers, dumps): threadname.h, docs/profiling.md.
    const QByteArray name = "vlqt-render-" + QByteArray::number(index);
    m_thread.setObjectName(QString::fromLatin1(name));
    m_thread.start();
    m_context = new QObject();
    m_context->moveToThread(&m_thread);
    post([name] { setCurrentThreadName(name.constData()); });
}

void RenderThread::logError(std::exception_ptr error)
{
    try {
        std::rethrow_exception(error);
    } catch (const std::exception& e) {
        qCWarning(lcRenderThread, "render thread job failed: %s", e.what());
    } catch (...) {
        qCWarning(lcRenderThread, "render thread job failed: unknown exception");
    }
}

int setThreadCount(int count)
{
    if (count < 0 || count > maxThreads)
        return fail(VLQT_ERR_BAD_ARGUMENT, "offscreen set_render_threads: thread count out of range 0 ... 16");
    if (count != s_count) {
        if (count == 0)
            qCInfo(lcRenderThread, "offscreen scenes created from now on render on the main thread");
        else
            qCInfo(lcRenderThread, "offscreen scenes created from now on render on %d Qt render thread(s)", count);
    }
    s_count = count;
    return VLQT_OK;
}

RenderThread* assign()
{
    if (s_count < 0) {
        s_count = fromEnvironment();
        if (s_count == 0)
            qCInfo(lcRenderThread, "offscreen scenes render on the main thread");
        else
            qCInfo(lcRenderThread, "offscreen scenes render on %d Qt render thread(s)", s_count);
    }
    if (s_count == 0)
        return nullptr;
    const int index = s_next++ % s_count;
    while (int(s_pool.size()) <= index)
        s_pool.push_back(new RenderThread(int(s_pool.size())));
    return s_pool[index];
}

void drainAll()
{
    for (RenderThread* t : s_pool)
        t->drain();
}

} // namespace vlqt::render_thread
