# Render loop

Which Qt Quick render loop QtNative runs, why, and which code relies on it. Code: `src/VL.Qt.Native/app/app.cpp` (where the loop is chosen), `app/render_gate.*`, `offscreen/offscreen_scene.h`, `effects/shader_item.*` and `effects/shader_material.*`. The behaviour as a user sees it is under Rendering in [architecture.md](../architecture.md#rendering).

## Context

Qt Quick makes a frame in three steps. The items change on the GUI thread (properties, polish). In the sync the GUI side is blocked and each item copies its state into its scene-graph node (`updatePaintNode`). Then the scene graph renders the nodes, their materials and the textures the materials point to. A render loop decides on which threads these steps run: `basic` runs all three one after another on the GUI thread, `threaded` syncs and renders on a render thread and lets the GUI thread go on while it renders.

QtNative has no `exec()`: vvvv pumps Qt once per vvvv frame (`vlqt_app_process_events_for`), and the GUI thread of Qt is the vvvv main thread.

## Design

### Windows: the basic loop

QtNative sets `QSG_RENDER_LOOP=basic` before Qt starts (`app.cpp`), on purpose: preparing and drawing a frame of a window happen on one thread, in step with vvvv. A window renders inside the pump, at most once per pump (the render gate, `render_gate.h`).

### Offscreen scenes: a render thread

An offscreen scene (`QtTexture`, `QtRenderer`) has no render loop; `QQuickRenderControl` drives it, and its scene graph and QRhi live on a render thread (`offscreen_scene.h`). The sync runs there while the main thread waits; render and end of frame are posted and run while the main thread goes on. Item code is shared by windows and offscreen scenes, so it meets the threaded situation anyway.

### Item code

- The state of an item changes on the GUI thread only. Its node, the node's material and the textures the material points to are touched only in `updatePaintNode`, in `preprocess` and by the material shader.
- A GPU object a material points to belongs to the node and is freed with it by the scene graph, never deleted from the GUI side. Example: the 1×1 transparent texture of the ShaderEffect element is owned by `EffectNode` (`shader_material.h`), not by `ShaderItem`.
- Code that is cheap to make correct under the `threaded` loop too is written that way; the rest relies on the basic loop and is listed below.

### Places that rely on the basic loop

- **Texture providers of `ShaderItem`** (`ShaderItem::watchProvider`): the `destroyed` signal of a provider is connected directly to `update()`. In a window the provider dies on the GUI thread between two frames, and the update makes the next sync drop its texture from the material before anything renders.
- **Render gate** (`render_gate.h`): with the basic loop Qt renders a window on `UpdateRequest`, which vvvv's message loop also delivers between two pumps; the gate keeps every render inside the pump.
- **Window sample count** (`window.cpp`, `vlqt_window_set_samples`): the basic loop fixes the MSAA count of a window when it creates the window's QRhi, so a new count needs a new `QQuickWindow`.
- **Frame lock of the shared device** (`device.h`, `lockFrames`): a window frame holds the device context on the main thread from `beforeRendering` to `afterFrameEnd`.
- **App fonts** (`fonts.cpp`): fonts are added on the GUI thread, which is also where every text layout of a window runs.

## Consequences

- A change of the render loop starts at the list above; each place needs a threaded design before the switch.
- New item code follows "Item code": a reviewer checks a GUI-side delete of anything a material can still point to.
- SmokeTest `group-shader-window-destroy` covers ShaderEffect elements deleted together with their window, `shader-effect` the 1×1 texture of a sampler without input.

## Alternatives considered

- **The `threaded` loop for windows:** a second thread per window that renders while vvvv goes on; frames would no longer be in step with vvvv, and the places above would need their own synchronisation.
- **Stating the dependency in each header:** the headers point here instead, so the list stays in one place.

## Open questions

None.

## Evidence

The choice is made in `app.cpp` (`QSG_RENDER_LOOP`). The measurements behind the window sample count are in [render-texture.md](../decisions/render-texture.md), "MSAA".
