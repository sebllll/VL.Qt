using System;
using System.Runtime.InteropServices;
using VL.Qt.Wire;

namespace VL.Qt.Interop
{
    internal static partial class Native
    {
        // Offscreen scenes (QtTexture): main thread only, codes <see cref="Result"/>. The scene handle also works
        // with vlqt_scene_destroy/_apply/_get_apply_error/_poll/_set_debug/_get_perf* (list in vlqt.h).
        /// <summary>Creates an unbound offscreen scene of width x height DIP; a null handle = error. Free with vlqt_scene_destroy.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern SceneHandle vlqt_offscreen_create(int width, int height);

        /// <summary>
        /// Binds the scene to the texture behind ntHandle (shared NT handle of an ID3D11Texture2D of device, an ID3D11Device*);
        /// format = DXGI_FORMAT the caller samples the texture with (SRGB and float: linear values, vlqt.h); samples = MSAA
        /// count (&lt; 2 = none), clamped, the count used in usedSamples. A change of the count is a new bind. The caller may
        /// close ntHandle after the call.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_bind(SceneHandle scene, IntPtr device, IntPtr ntHandle, int width, int height, int format, int samples, out int usedSamples);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_unbind(SceneHandle scene);

        /// <summary>1 = bound, 0 = unbound (bind again, e.g. after a device loss of QtNative), <see cref="Result.NullHandle"/> = null handle or no offscreen scene.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int vlqt_offscreen_is_bound(SceneHandle scene);

        /// <summary>
        /// Renders the changed, bound scenes of one host frame, ordered against context (the foreign immediate
        /// ID3D11DeviceContext*) by fences. Returns the number of scenes rendered, or a negative <see cref="Result"/>.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int vlqt_offscreen_render_batch(IntPtr context, in SceneHandle scenes, int count, double driverSeconds);

        public static int OffscreenRenderBatch(IntPtr context, ReadOnlySpan<SceneHandle> scenes, double driverSeconds)
            => vlqt_offscreen_render_batch(context, in MemoryMarshal.GetReference(scenes), scenes.Length, driverSeconds);

        /// <summary>
        /// Where offscreen scenes created afterwards render (pin Render Threads): 0 = main thread, n = 1 … 16 Qt render
        /// threads (without the call: 1). May be called any time; the pool only grows.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_set_render_threads(int count);

        // Input into an offscreen scene: positions in texture pixels, codes <see cref="Result"/> (vlqt.h).
        /// <summary>
        /// Pointer event (Protocol.Pointer) of device (Protocol.PointerDevice) at x/y; mouse: buttons = Qt::MouseButtons held
        /// after the event, id ignored; touch/pen: id of the point, buttons ignored. modifiers = Qt::KeyboardModifiers.
        /// </summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_pointer(SceneHandle scene, int device, int type, int id, float x, float y, int buttons, int modifiers);
        /// <summary>Wheel at x/y; angle delta in eighths of a degree (120 = one notch).</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_wheel(SceneHandle scene, float x, float y, int dx, int dy, int modifiers);
        /// <summary>Key press or release (Protocol.KeyEvent) of a Qt::Key without text; autoRepeat 1 for repeated presses.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_send_key(SceneHandle scene, int type, int key, int modifiers, int autoRepeat);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern Result vlqt_offscreen_send_text(SceneHandle scene, in byte utf8, int length);

        /// <summary>Typed text as UTF-8 (no NUL needed) to the focus item; a stack span keeps it allocation-free.</summary>
        public static Result OffscreenSendText(SceneHandle scene, ReadOnlySpan<byte> utf8)
            => vlqt_offscreen_send_text(scene, in MemoryMarshal.GetReference(utf8), utf8.Length);

        /// <summary>focused 1: FocusIn, 0: FocusOut; no-op if unchanged.</summary>
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern Result vlqt_offscreen_set_focus(SceneHandle scene, int focused);
    }
}
