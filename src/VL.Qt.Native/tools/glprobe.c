/* vlqt-glprobe: checks that OpenGL works on the X display, as Qt does first on xcb: open the display and create a GLX
 * context. QtNative starts it under WSLg before the QGuiApplication (platform/platform_linux.cpp, probeGlx) to find out
 * whether Mesa's driver in GALLIUM_DRIVER works on this system. A process of its own: loading libX11 and libGL in a fork
 * of the multithreaded host could deadlock. Linux only, no Qt; libX11 and libGL are looked up at run time, not linked.
 * Exit code: 0 a context was created; 2 libraries or functions missing; 3 no X display; 4 no frame buffer config;
 * 5 no context; an X error ends the process through Xlib's default error handler (exit code 1). */
#include <dlfcn.h>
#include <stddef.h>

typedef void* (*OpenDisplayFn)(const char*);
typedef int (*SyncFn)(void*, int);
typedef void** (*ChooseFn)(void*, int, const int*, int*);
typedef void* (*CreateFn)(void*, void*, int, void*, int);

int main(void)
{
    void* x11 = dlopen("libX11.so.6", RTLD_NOW);
    void* gl = dlopen("libGL.so.1", RTLD_NOW);
    if (!x11 || !gl)
        return 2;
    OpenDisplayFn openDisplay = (OpenDisplayFn)dlsym(x11, "XOpenDisplay");
    SyncFn sync = (SyncFn)dlsym(x11, "XSync");
    ChooseFn choose = (ChooseFn)dlsym(gl, "glXChooseFBConfig");
    CreateFn create = (CreateFn)dlsym(gl, "glXCreateNewContext");
    if (!openDisplay || !sync || !choose || !create)
        return 2;
    void* display = openDisplay(NULL);
    if (!display)
        return 3;
    const int attribs[] = {0x8011 /* GLX_RENDER_TYPE */, 0x1 /* GLX_RGBA_BIT */, 0};
    int count = 0;
    void** configs = choose(display, 0, attribs, &count);
    if (!configs || count < 1)
        return 4;
    void* context = create(display, configs[0], 0x8014 /* GLX_RGBA_TYPE */, NULL, 1);
    sync(display, 0); /* an X error from the context request ends the process here */
    return context ? 0 : 5;
}
