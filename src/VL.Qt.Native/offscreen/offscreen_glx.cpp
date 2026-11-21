#include "offscreen/offscreen_glx.h"

#include <dlfcn.h>
#include <X11/Xlib.h>

namespace {

// libX11 and libGL are loaded by Qt's offscreen platform plugin (RTLD_LOCAL, so not in the global scope): look them up
// in the loaded libraries without loading anything, and without a link dependency of QtNative on X11/GL.
void* loaded(const char* const* names)
{
    for (const char* const* n = names; *n; n++)
        if (void* h = dlopen(*n, RTLD_NOW | RTLD_NOLOAD))
            return h;
    return nullptr;
}

template <class F>
F symbol(void* lib, const char* name)
{
    return lib ? reinterpret_cast<F>(dlsym(lib, name)) : nullptr;
}

struct Api
{
    Api()
    {
        static const char* const x11Names[] = { "libX11.so.6", "libX11.so", nullptr };
        static const char* const glNames[] = { "libGLX.so.0", "libGL.so.1", "libGL.so", nullptr };
        void* const x11 = loaded(x11Names);
        void* const gl = loaded(glNames);
        getDisplay = symbol<Display* (*)()>(gl, "glXGetCurrentDisplay");
        getDrawable = symbol<XID (*)()>(gl, "glXGetCurrentDrawable");
        swapBuffers = symbol<void (*)(Display*, XID)>(gl, "glXSwapBuffers");
        getGeometry = symbol<decltype(&XGetGeometry)>(x11, "XGetGeometry");
    }
    Display* (*getDisplay)() = nullptr;
    XID (*getDrawable)() = nullptr;
    void (*swapBuffers)(Display*, XID) = nullptr;
    decltype(&XGetGeometry) getGeometry = nullptr;
};

const Api& api()
{
    static const Api s_api;
    return s_api;
}

} // namespace

namespace vlqt::glx {

bool invalidateCurrentDrawable()
{
    const Api& x = api();
    if (!x.getDisplay || !x.getDrawable || !x.swapBuffers)
        return false;
    Display* display = x.getDisplay();
    const XID drawable = x.getDrawable();
    if (!display || !drawable)
        return false;
    x.swapBuffers(display, drawable);
    return true;
}

void currentDrawableSize(int* width, int* height)
{
    *width = *height = -1;
    const Api& x = api();
    if (!x.getDisplay || !x.getDrawable || !x.getGeometry)
        return;
    Display* display = x.getDisplay();
    const XID drawable = x.getDrawable();
    if (!display || !drawable)
        return;
    Window root;
    int px, py;
    unsigned w, h, border, depth;
    if (x.getGeometry(display, drawable, &root, &px, &py, &w, &h, &border, &depth)) {
        *width = int(w);
        *height = int(h);
    }
}

} // namespace vlqt::glx
