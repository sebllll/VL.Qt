#include "render_thread.h"

#include "protocol.h"

#include <QtCore/QLoggingCategory>

#include <cstdlib>
#include <cstring>
#include <vector>

namespace vlqt::render_thread {

namespace {

Q_LOGGING_CATEGORY(lcRenderThread, "vlqt.offscreen")

int s_requested = -1;  // setThreadCount, -1 = not set
int s_count = -1;      // the pool size in use, -1 = not fixed yet (no assign)
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
    m_thread.setObjectName(QStringLiteral("vlqt render %1").arg(index));
    m_thread.start();
    m_context = new QObject();
    m_context->moveToThread(&m_thread);
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

int setThreadCount(int count, const char** error)
{
    if (count < 0 || count > maxThreads) {
        *error = "offscreen set_threaded: thread count out of range 0 ... 16";
        return VLQT_OFFSCREEN_BAD_ARGUMENT;
    }
    if (s_count >= 0 && count != s_count) {
        *error = "offscreen set_threaded: only before the first offscreen scene (the thread count is fixed)";
        return VLQT_OFFSCREEN_NOT_SUPPORTED;
    }
    s_requested = count;
    return VLQT_OFFSCREEN_OK;
}

RenderThread* assign()
{
    if (s_count < 0) {
        s_count = s_requested >= 0 ? s_requested : fromEnvironment();
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

} // namespace vlqt::render_thread
