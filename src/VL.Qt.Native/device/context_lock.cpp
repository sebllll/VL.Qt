#include "device/context_lock.h"

#include "device/device.h"

#include <QtCore/QLoggingCategory>
#include <QtQuick/QQuickWindow>

#include <algorithm>
#include <optional>
#include <utility>
#include <vector>

namespace vlqt::device {

namespace {

Q_LOGGING_CATEGORY(lcContextLock, "vlqt.device")

// lockFrames: holds the shared context from beforeRendering to afterFrameEnd of an on-screen window's frame (basic loop,
// main thread); releaseFrameLocks ends every one still held. Child of its window.
class FrameLock final : public QObject
{
public:
    explicit FrameLock(QQuickWindow* window) : QObject(window)
    {
        s_all.push_back(this);
        connect(window, &QQuickWindow::beforeRendering, this, [this] {
            if (!m_lock)
                m_lock.emplace();
        }, Qt::DirectConnection);
        connect(window, &QQuickWindow::afterFrameEnd, this, &FrameLock::release, Qt::DirectConnection);
    }
    ~FrameLock() override { s_all.erase(std::remove(s_all.begin(), s_all.end(), this), s_all.end()); }
    // True if the lock was held.
    bool release()
    {
        const bool held = m_lock.has_value();
        m_lock.reset();
        return held;
    }
    // The window of the first lock that was held, nullptr if none was.
    static QQuickWindow* releaseAll()
    {
        QQuickWindow* first = nullptr;
        for (FrameLock* l : s_all)
            if (l->release() && !first)
                first = static_cast<QQuickWindow*>(l->parent());
        return first;
    }
private:
    std::optional<ContextLock> m_lock;
    static inline std::vector<FrameLock*> s_all; // main thread
};

} // namespace

ContextLock::ContextLock()
    : ContextLock(sharedContext())
{
}

ContextLock::ContextLock(void* d3d11Context)
{
#ifdef _WIN32
    auto* const context = static_cast<ID3D11DeviceContext*>(d3d11Context);
    if (context && SUCCEEDED(context->QueryInterface(IID_PPV_ARGS(m_multithread.GetAddressOf()))))
        m_multithread->Enter();
#else
    Q_UNUSED(d3d11Context);
#endif
}

ContextLock::~ContextLock()
{
#ifdef _WIN32
    if (m_multithread)
        m_multithread->Leave();
#endif
}

void lockFrames(QQuickWindow* window)
{
    new FrameLock(window);
}

bool releaseFrameLocks()
{
    return FrameLock::releaseAll() != nullptr;
}

void releaseFrameLocksBeforeWait()
{
    static bool warned = false;
    QQuickWindow* const window = FrameLock::releaseAll();
    if (window && !std::exchange(warned, true))
        qCWarning(lcContextLock, "a blocking render-thread job was called inside a frame of window %p: its frame lock was "
            "ended, the rest of that frame races with the render threads on the shared context (logged once)",
            static_cast<void*>(window));
}

} // namespace vlqt::device
