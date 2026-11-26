#include "device/render_thread.h"

#include "app/last_error.h"
#include "app/thread_name.h"
#include "offscreen/offscreen.h"
#include "vlqt.h"

#include <QtCore/QLoggingCategory>

#include <string>
#include <vector>

namespace vlqt::render_thread {

namespace {

Q_LOGGING_CATEGORY(lcRenderThread, "vlqt.renderthread")

int s_count = -1;      // the pool size for new scenes, -1 = not set yet (1 at the first scene)
int s_next = 0;        // round robin
std::vector<RenderThread*> s_pool; // never deleted (render_thread.h)

} // namespace

RenderThread::RenderThread(int index)
    : m_index(index)
{
    // The same name for Qt and for the OS (profilers, dumps): app/thread_name.h, docs/contributing/profiling.md.
    const QByteArray name = "vlqt-render-" + QByteArray::number(index);
    m_thread.setObjectName(QString::fromLatin1(name));
    m_thread.start();
    m_context = new QObject();
    m_context->moveToThread(&m_thread);
    post([name] { setCurrentThreadName(name.constData()); });
}

void logJobError(std::exception_ptr error)
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
        return fail(VLQT_ERR_BAD_ARGUMENT, ("offscreen set_render_threads: thread count out of range 0 ... " + std::to_string(maxThreads)).c_str());
    if (count != s_count) {
        if (count == 0)
            qCInfo(lcRenderThread, "offscreen scenes created from now on render on the main thread");
        else
            qCInfo(lcRenderThread, "offscreen scenes created from now on render on %d Qt render thread(s)", count);
    }
    s_count = count;
    while (int(s_pool.size()) < count)
        s_pool.push_back(new RenderThread(int(s_pool.size())));
    return VLQT_OK;
}

Executor assign()
{
    if (s_count < 0)
        setThreadCount(1);
    if (s_count == 0)
        return Executor();
    return Executor(s_pool[s_next++ % s_count]);
}

void drainAll()
{
    for (RenderThread* t : s_pool)
        t->drain();
}

void retire(QQuickWindow* window, std::shared_ptr<void> resource)
{
    if (!resource)
        return;
    if (RenderThread* t = offscreen::renderThreadOf(window))
        t->post([resource = std::move(resource)]() mutable { resource.reset(); });
}

} // namespace vlqt::render_thread
