# Stride output

How a Qt scene gets into Stride: the node `QtTexture` outputs a Stride `Texture`, the layer `QtRenderer [Stride]` draws the scene into a render target. Code: `src/VL.Qt.Stride/` (`QtTextureNode`, `QtRendererNode`, `SharedTextureScene`, `QtTextureRenderer`, `LayerComposition`, `TextureFormats`, `StrideInterop`) and `src/VL.Qt.Native/offscreen/` (`offscreen_binding.cpp`, `linear_pass.cpp`). The device model is in [gpu-device.md](gpu-device.md), threads and fences in [render-thread.md](render-thread.md), input in [offscreen-input.md](offscreen-input.md). Users find the nodes in the help patches `HowTo Draw Qt in a Stride Scene` and `HowTo Render Qt into a Stride Texture`, and the render-thread pin in [../diagnostics.md](../diagnostics.md#qttexture-render-thread).

## Context

`QtTexture` is used like `Renderer [Qt]` but opens no window. Several of them must not block each other or Stride's rendering, the data stays on the GPU (no readback), and ownership has to be clear. The reference implementation for the pattern is `SkiaTexture` in VL.Stride.

Qt and Stride sit on different devices ([gpu-device.md](gpu-device.md)): Qt renders into a texture that Stride's device created and shared, and fences order the two.

## Design

### Package layout

`VL.Qt` stays free of Stride, because it also runs on Linux. The nodes live in the companion package `VL.Qt.Stride` (the ImGui pattern: `VL.ImGui`, `VL.ImGui.Stride`), Windows only, built from this repository. The native part (shared texture, fences) is in `vlqt.dll`.

- The package folder is `VL.Qt.Stride/` at the repository root (`VL.Qt.Stride.vl`, `help/`, build output `lib/net10.0-windows7.0/`, not tracked). The C# source is in `src/VL.Qt.Stride/`. VL.Qt stays at the root.
- vvvv finds a source package through `--package-repositories <dir>`: every subfolder `<dir>\<PackageId>\` with `<PackageId>.vl` is a package. The development package repository holds one junction per package.
- The target framework is `net10.0-windows`; the output folder is `lib/net10.0-windows7.0/` in repository and package alike, because `dotnet pack` rejects a TFM folder without platform version and the `.vl` references the same relative path in both.
- The NuGet package contains the `.vl`, `VL.Qt.Stride.dll` and `help/`. Its only dependency is `VL.Qt` at the same version, so both are packed with the same version. VL.Stride and VL.Core are compile-time references only; they ship with vvvv. `VL.Qt.dll` is neither copied nor packed.

### Shared texture, fences and ownership

- The Stride side creates the shared texture (`Texture.New` with a shared NT handle) and the node owns it. QtNative opens it, closes the handle right after opening, and owns its wrapper, render target, offscreen scene and two shared `ID3D11Fence`s: Qt write complete and Stride read complete.
- The fences order Qt's write and Stride's read on the GPU, so the CPU never waits. Stride's context is never touched by Qt.
- The output is the same `Texture` instance until `Size` or `Format` change; consumers never dispose it. There is one texture and two fences, no double buffer, because a double buffer would change the output instance every frame.
- On a change of size or format, on device loss and on Dispose the node frees at once, in this order: Qt side, fences, Stride texture, on the main thread. D3D11 keeps resources alive that the GPU still uses, so there is no deferred disposal.
- The size is clamped to 1×1 … the largest Texture2D of the feature level (2048 below 9_3, 4096 below 10_0, 8192 below 11_0, else 16384). Invalid input gives an error at the node and a null output.
- `Enabled` false creates, recreates and rebinds nothing and renders nothing. `Output` keeps the last content (null if the node never rendered).
- No re-render when the scene did not change.

### Device loss on either side

- Stride resets its device in place (the same `GraphicsDevice`, its textures destroyed). The renderer forwards `IGraphicsDeviceService.DeviceResetting` and `DeviceDisposing` to the nodes, which unbind the Qt side, dispose their texture at once and create a new one in the next `Update`.
- When QtNative unbinds a scene after its own device loss ([gpu-device.md](gpu-device.md)), the Stride texture stays valid and the node binds again with a new NT handle in the next `Update`: every frame while QtNative is still rebuilding (`DEVICE_LOST`), after another failed bind (adapter, open, fences) only every 60th `Update`.

### Formats, colour space and alpha

The `Format` pin defaults to `B8G8R8A8_UNorm_SRgb`. Allowed are the 8-bit RGBA and BGRA formats (UNorm and SRgb) and `R16G16B16A16_Float`; anything else is an error at the node. 8-bit SRgb textures are created typeless, Stride gets the SRgb view.

- **UNorm** textures hold what Qt renders, gamma-space values as in a Qt window.
- **SRgb and Float** textures hold linear values. Qt renders premultiplied gamma-space values, and sRGB decoding does not commute with premultiplication. Read through an SRgb view, every pixel with 0 < alpha < 1 would be too dark: 50 % red over the Stride background gave a red of 132 instead of about 188, and a Float texture read gamma values as linear. For these formats Qt therefore renders into an `R16G16B16A16_Float` texture of QtNative, and after each frame a fullscreen pass (`linear_pass.cpp`) writes a × linear(c) into the shared texture: unpremultiply, sRGB decode, premultiply, written through the SRgb view (the GPU encodes) or as float. Opaque pixels read as in a Qt window. Half-transparent colours still differ, because Stride blends in linear light and a Qt window in gamma space.
- The bind format is the format Stride samples.
- Output is always **premultiplied alpha**, with no pin: Qt renders premultiplied, and Stride's premultiplied blend state (`AlphaBlendPremultiplied` in vvvv) and `SkiaTexture` expect it; vvvv's `AlphaBlend` is for straight alpha. Straight alpha comes from an unpremultiply TextureFX downstream (shown in the help patch).
- Elements with straight alpha come out premultiplied (50 % blue gives 0,0,128,128), but Qt does not premultiply a semi-transparent clear colour: (1, 0, 0, 0.5) would be written as (255, 0, 0, 128), which is no valid premultiplied value. The `Clear Color` pin therefore multiplies RGB by alpha before the colour reaches QtNative. Clear alpha 0 gives transparent black.

### Frame timing

In `Update` the node applies its scene through the reconciler and schedules itself for this frame. One renderer per app host (`QtTextureRenderer`) renders all scheduled nodes at the start of Stride's draw, after all updates and before any other game system draws, in one native call (`vlqt_offscreen_render_batch`: polish, sync, render into the texture, signal the write fence). Stride waits on that fence on the GPU, so the texture shows the same frame and the CPU never waits. Time advance and event handling for these scenes run before that point. A node whose output nobody draws costs nothing.

The hook is a `GameSystem` with `DrawOrder = int.MinValue`: `VLGame.Draw` raises the internal `DrawStarted` right before `GameSystems.Draw`, which draws by `DrawOrder`, so the lowest order is the same moment through public API only, without reflection. A direct `GameSystems.Draw` (the screenshot generator) finds nothing scheduled and does nothing.

### QtRenderer: the layer

`QtRenderer [Stride]` (category `Stride.Rendering`) is a layer inside a Stride render target and outputs no texture. A half-transparent Qt content drawn over a Stride scene should look (almost) the same as in a Qt window, which `QtTexture` cannot give.

- **Why not blend the texture through Stride's view:** Stride blends through an SRgb view in linear light, a Qt window blends in gamma space. No change of the texture alone can make up for it: Stride's result X + (1 − α)·linear(bg) is linear in the background, the Qt window's linear(c + (1 − a)·bg) is not. Over 20 000 random cases the best per-pixel approximation is off by 7/255 on average and 25/255 at p95. Example, 50 % red over (30, 148, 102): Qt window (142, 74, 51), Stride linear (189, 107, 73).
- **How Skia does it:** `SkiaRenderer [Stride]` looks like a Skia window because it draws straight into the back buffer, whose resource is not sRGB (only Stride's view is), so Skia blends on raw bytes in gamma space. `SkiaTexture` has the same difference as `QtTexture`.
- **Composition (Direct):** the layer renders into an internal core with a UNorm format (gamma values, premultiplied). In its draw it binds a UNorm render target view on the resource of the current target and draws a fullscreen quad into the viewport with premultiplied blending (`BlendStates.AlphaBlend`, One/InvSrcAlpha): exactly the arithmetic of a Qt window, one draw, no readback. The UNorm view comes from `Texture.ToTextureView`; if the target's own view is UNorm it is used as it is. The quad uses point sampling (texels map 1:1) and no depth buffer. Condition: the target's resource is 8-bit RGBA/BGRA and allows a UNorm view (UNorm or typeless: window back buffer, typeless render texture). The layer belongs after the 3D scene, not inside it with an HDR float target.
- **Fallback:** any other target (SRgb resource that is not typeless, float, MSAA without a UNorm view) gets the texture in the target's view format with `QtTexture`'s linear premultiplied values and premultiplied blending: correct for opaque content, half-transparent colours as with `QtTexture`. A view format `QtTexture` does not support gets `B8G8R8A8_UNorm_SRgb` if it is an SRgb format, else `R16G16B16A16_Float`. The layer logs this once per target change and shows a warning at the node (`LayerComposition`).
- **Shared core:** `QtTextureNode` and `QtRendererNode` use one internal class (`SharedTextureScene`) for the texture, bind and rebind, lifetime, device loss and scheduling.
- **Timing:** the layer schedules its core in `Update` with the viewport size of its last draw (a resize shows one frame later); the batch renders in `DrawStarted`, the draw composes the same frame's content. Before the first draw there is no size and nothing renders. After a target change to another format the layer draws nothing for one frame, until the next `Update` has recreated the texture (with `Enabled` false that stays empty until re-enabled). `Enabled` false freezes like `QtTexture` and the layer keeps drawing the last content; a layer that was not drawn since the last `Update` freezes the same way.
- **One target per frame:** a second, different target in the same frame (one layer connected to two windows) is not drawn and gives the warning "QtRenderer draws into one target per frame; use one QtRenderer per window" at the node.
- **Input:** from the render context's window input source, positions minus the viewport origin give texture pixels ([offscreen-input.md](offscreen-input.md)). A new input source re-subscribes.

### Pins

`QtTexture`: `Input`, `Size` (`Int2`, pixels, default 1920×1080), `Space` (default `DIPTopLeft`; 1 DIP = 1 pixel, the device pixel ratio is 1), `Clear Color` (default transparent), `Clear`, `Format`, `Enabled`, and the optional `Input Notifications`, `Debug`, `Multisample Count` and `Render Threads`. Output `Output` (Texture). `QtRenderer`: `Input`, `Space`, `Clear Color`, `Enabled`, optional `Debug` and `Render Threads`; output the layer. Its size comes from the viewport, its format from the target. Errors show at the node and in the log; there is no `Error` pin. `Clear` and `Multisample Count` are in [clear-and-msaa.md](clear-and-msaa.md).

## Consequences

- Every Stride-side rule here is protected by the SmokeTest scenario `qttexture` (formats, alpha, bind and unbind, adapter move, device loss, 1000× create and dispose with a second D3D11 device for Stride and no live D3D objects afterwards) and by the help patches.
- A consumer must never dispose or hold the texture across a `Size` or `Format` change.
- `QtRenderer` costs one render target view and one draw; it does not give the patch a texture to process further.

## Alternatives considered

- **Qt on Stride's device:** see [gpu-device.md](gpu-device.md).
- **Keyed mutex:** blocks on the CPU; shared fences wait on the GPU.
- **CPU readback as the main path:** the data stays on the GPU. A second node or pin may offer a readback.
- **Stride dependency in `VL.Qt.vl`:** breaks Linux exports and every Qt patch would load Stride. A second document in the `VL.Qt` package would have to be found and referenced by users.
- **Moving VL.Qt into a `VL.Qt/` folder** (the StandardLibs layout): large churn for no gain; **the `.vl` inside `src/VL.Qt.Stride/`:** mixes package and source, `bin` and `obj` would land in the package folder.
- **Deferred disposal by one frame:** unusual in vvvv, and D3D11 already covers GPU use.
- **Double buffering or a GPU copy into a stable texture:** needed only for a mode where Stride never waits; the copy would be preferred then, because consumers keep one instance.
- **Rendering in the Qt pump at the end of the frame:** may run after Stride's draw, one frame late.
- **A `Scale` pin:** not needed.

## Open questions

None.

## Evidence

The `QtRenderer` colour arithmetic was checked in vvvv on win-arm64 with the help patch `HowTo Draw Qt in a Stride Scene`: layer and Qt window look the same, half-transparent overlap included ([../images/qtrenderer-vs-qt-window.png](../images/qtrenderer-vs-qt-window.png)). The cost of the render-thread modes is in [../research/render-texture-measurements.md](../research/render-texture-measurements.md).
