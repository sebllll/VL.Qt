# Offscreen scenes and render threads

How an offscreen scene (the Qt side of `QtTexture` and `QtRenderer`) lives in QtNative and which thread runs what: `src/VL.Qt.Native/offscreen/` (`offscreen_scene.h`, `offscreen_binding.cpp`, `offscreen_render.cpp`), `src/VL.Qt.Native/device/render_thread.h` and `device/context_lock.h`. The pin `Render Threads` is described in [../diagnostics.md](../diagnostics.md#qttexture-render-thread); the frame sequence, the fences and the measurements are in [../decisions/render-texture.md](../decisions/render-texture.md).

## Context

A scene is a `QQuickWindow` driven by a `QQuickRenderControl`: no native window, rendering into a texture of another device (Stride's) that the host passes to `bind`. Qt's scene graph and its `QRhi` (Qt's graphics abstraction) are bound to the thread that uses them, while the elements, the reconciler and all input stay on the main thread. Every scene of the process shares QtNative's one D3D11 device and its immediate context ([../decisions/device-sharing.md](../decisions/device-sharing.md)).

## Design

### Lifetime of a scene

- A scene exists from `vlqt_offscreen_create` to `vlqt_scene_destroy` and goes through `bind`, `render_batch` … `unbind` any number of times in between. Destroy first releases the binding and the scene graph (`release`), then deletes the `QQuickWindow`, then the render control and the scene's `QRhi` (`destroy`).
- The `QRhi` belongs to the render control and lives on QtNative's shared device from create on, so a scene is never bound to another device than the one its `QRhi` uses. `render_batch` refuses a context that belongs to another device than the one the scene was bound to.
- A binding is the foreign texture opened on QtNative's device (Qt's render target), QtNative's immediate context and two shared fences, each as created on QtNative's device and as opened on the foreign one. `readFence` means the foreign device is done reading the texture (Qt may write), `writeFence` means Qt is done writing (the foreign device may read). Both count up from 0 per binding.
- QtNative holds no reference to the foreign device of its own: the two fences opened on it keep it alive until `unbind`. A bound scene therefore keeps the device alive after its owner released it, and `unbind` or destroy frees it.
- When QtNative's device is lost, or moves to the foreign adapter in `bind`, a `device::Listener` unbinds every scene and drops its scene graph and `QRhi` (`lost`). `device::rebuild` then gives the scene's window, which has no `QRhi` now, the new device like any window that is not yet exposed, and `restored` initializes the render control again. The caller sees `is_bound` 0 and binds again.

### Render threads

- A pool of threads with a Qt event loop, shared by every offscreen scene of the process (scenes of every app host). Its size is the pin `Render Threads`, 1 until it is set; 0 means the main thread. The pool only grows: a larger size adds threads at once, a smaller one (or 0) applies to the scenes created afterwards and leaves the threads to the scenes that have them. The threads live until the process ends, like the shared device: stopping them at exit would run after Qt.
- A scene gets its executor at create (a thread, round robin, or inline on the main thread) and keeps it. Its scene graph and `QRhi` live there (`QQuickRenderControl::prepareThread`): `initialize`, `beginFrame`, `sync`, `render`, `endFrame`, `invalidate` and `unbind` run through it. The main thread waits for each of them except `render` + `endFrame` + `Signal` + `Flush`, which `render_batch` posts (inline they run at once). Items, polish, input, `apply` and `bind` stay on the main thread; a render thread touches items only in `sync`, while the main thread waits.
- Jobs of one thread run in queue order, so a blocking job runs after every job posted before it. That is how the main thread drains a thread before it unbinds, destroys, handles a device loss or an adapter move.
- Scene graph objects of a scene (a `QSGTexture` and the `QRhiTexture` under it) belong to its render thread: an item that holds one from the main thread lets it go through `render_thread::retire`, never by dropping the last reference itself.

### The context lock

The shared device's immediate context is multithread-protected, so single calls need no lock. A sequence whose state must not interleave with another thread's takes a `ContextLock` (a QRhi frame, `Wait` + `beginFrame`, `endFrame` + `Signal` + `Flush`, `Map` + `Unmap`). Window frames hold it from `beforeRendering` to `afterFrameEnd`. The main thread never holds it while it waits for a render thread, because that job may take it too: `RenderThread::call` ends the window frame locks first and warns once if one was still held (the call came from inside a window frame).

## Consequences

- A change in what runs on a render thread starts at the thread table in the "Qt render thread" section of [../decisions/render-texture.md](../decisions/render-texture.md).
- New code that holds the context lock follows the lock order above; the SmokeTest scenarios `qttexture` (both thread modes), `image-retire` and `qttextureperf` protect it.

## Alternatives considered

- **One device per render thread, deferred contexts, one thread per scene:** rejected with their measurements in [../decisions/render-texture.md](../decisions/render-texture.md).

## Open questions

None.

## Evidence

Measurements of one render thread, a pool and the main-thread mode: [../decisions/render-texture.md](../decisions/render-texture.md) (one laptop; x64 desktop not measured). Rerun with SmokeTest `qttextureperf`.
