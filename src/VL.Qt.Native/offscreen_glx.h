#pragma once

// Linux, Qt's offscreen platform with OpenGL (GLX) only. Separate from the Qt sources: X11 headers define macros
// (None, Bool, Status, ...) that clash with Qt's headers.
namespace vlqt::glx {

// W1-21: Qt's offscreen platform renders every surface of a GL context into one hidden X window of the context (created
// 100 x 100) and resizes that window in makeCurrent (QOffscreenX11GLXContext::makeCurrent); its swapBuffers does
// nothing. Mesa's software winsys (drisw: llvmpipe under WSL, no DRM device) reads the X window's size when the drawable
// is first validated and again only after it is invalidated, which a bind does not do (dri_bind_context invalidates for
// DRI3 and zink only) but a glXSwapBuffers does (drisw_copy_to_front). The default framebuffer can so keep an old size,
// e.g. 100 x 100, and a window grab reads one corner of the frame plus memory that was never written. This swaps the
// buffers of the current GLX drawable, so Mesa takes the window's present X size at the next validation (the next
// frame). The back buffer is undefined afterwards: render a complete frame next. Returns false if there is no current
// GLX drawable or libGL is not loaded.
bool invalidateCurrentDrawable();

// X geometry of the current GLX drawable (width, height), or -1, -1.
void currentDrawableSize(int* width, int* height);

} // namespace vlqt::glx
