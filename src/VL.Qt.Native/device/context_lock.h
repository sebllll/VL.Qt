/* context_lock.h - the lock on the shared device's immediate context (device.h).
 *
 * The immediate context is multithread-protected and shared by the main thread (window frames, the upload QRhi, staging
 * copies) and the render threads of offscreen scenes (render_thread.h). Single calls need no lock; a sequence whose context
 * state must not interleave with another thread's takes a ContextLock. Windows only in effect: on other platforms every
 * lock is a no-op. Design: docs/decisions/render-texture.md.
 */
#pragma once

#ifdef _WIN32
#include <d3d11_4.h> // ID3D11Multithread
#include <wrl/client.h>
#endif

class QQuickWindow;

namespace vlqt::device {

// Holds the immediate context of a D3D11 device for a stateful sequence of calls (ID3D11Multithread::Enter/Leave,
// recursive per thread): a QRhi frame, Wait + beginFrame, endFrame + Signal + Flush, Map + Unmap. No-op for a null
// context and on other platforms. Main-thread holders: window frames (lockFrames), upload QRhi frames and staging copies
// (resources/image_upload_rhi.cpp, image_upload_d3d11.cpp). Lock order: never wait for another thread while holding it,
// above all never for a blocking render-thread job (RenderThread::call, drain): that job may take the lock too and both
// threads would wait forever.
class ContextLock
{
public:
    // The shared device's immediate context (device::sharedContext); no-op without one. Not while the main thread
    // rebuilds the device.
    ContextLock();
    // context: an ID3D11DeviceContext*, or null.
    explicit ContextLock(void* d3d11Context);
    ~ContextLock();
    ContextLock(const ContextLock&) = delete;
    ContextLock& operator=(const ContextLock&) = delete;

private:
#ifdef _WIN32
    Microsoft::WRL::ComPtr<ID3D11Multithread> m_multithread; // entered; empty without a context
#endif
};

// Holds the shared context (ContextLock) from beforeRendering to afterFrameEnd of every frame of this on-screen window,
// so the render threads of offscreen scenes cannot change the context state while QRhi D3D11 submits the frame (in
// endFrame). The vsync wait in beginFrame stays outside. Main thread, basic loop; not for offscreen scenes (their render
// thread locks itself).
void lockFrames(QQuickWindow* window);

// Ends every window frame lock still held; returns true if there was one. A frame normally ends it at afterFrameEnd;
// QQuickWindow::grabWindow renders without that signal. Called after the pump renders, before rebuild's listeners and
// before a blocking render-thread job: the main thread must not hold the lock there (lock order, above). Main thread.
bool releaseFrameLocks();

// releaseFrameLocks for a caller that is about to wait for a render-thread job (RenderThread::call). No frame is
// expected to be running there (the pump and a grab end their locks): a lock still held means the call came from inside
// a window frame, whose remaining commands then race with the render thread on the shared context. The lock is ended all
// the same (the wait would deadlock otherwise) and the first time the log (vlqt.device) names the window. Main thread.
void releaseFrameLocksBeforeWait();

} // namespace vlqt::device
