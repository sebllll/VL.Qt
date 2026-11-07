# Device Sharing: Shared D3D11 Device vs. Own Device

How the Qt scene gets onto the GPU next to Stride, and why Qt renders on Stride's own D3D11 device.

## Goals
- Reuse the pattern VL.Skia already uses for ANGLE/Skia in vvvv instead of inventing a new one.
- No added frame of latency: Qt content has to arrive in the same Stride frame unless a trade-off explicitly justifies it.
- Heavy scenes (whole applications, a full Skia replacement) are a target, so there must be a path to CPU parallelism.

## Design
### Mode 1: shared Stride device on the main thread
- Qt receives Stride's D3D11 device and context through `QQuickGraphicsDevice::fromDeviceAndContext`.
- `beginFrame … endFrame` runs inside a dedicated `ID3DDeviceContextState` (`SwapDeviceContextState`), analogous to `DeviceContextScope` in VL.Skia.
- No copies, no synchronisation cost, and everything shows up in one RenderDoc/PIX capture.
- Downside: Qt rendering loads the main thread serially.
- Rationale: the pattern is proven in vvvv and carries the smallest risk.

### Stride layer
- The layer follows `SkiaRenderer` in VL.Stride (`RendererBase`/`IGraphicsRendererBase`).
- It renders into an intermediate texture that is blended onto the Stride scene with premultiplied alpha. This stays robust even if Qt clears the target during its render pass. The intermediate texture doubles as the texture output. The cost is one extra pass.
- Naming follows Skia: `Renderer [Qt]` for the window, `QtRenderer [Stride]` for the layer.
- A `QtRenderer` window works like the Skia window: form and swap chain in its own HWND on the shared device, rendered through `QQuickRenderControl`. It replaces the plain `QQuickWindow`.

### Mode 2 (direction for heavy scenes): own Qt device, Qt render thread, shared fences
- Mirrors Qt's own threaded render loop: the main thread only runs `polish` and `sync` (short, blocking); the Qt thread then renders in parallel to Stride, and Stride waits GPU-side on a write fence before sampling the texture.
- Result: content in the same frame while the CPU work runs in parallel. The pattern matches `SkiaTexture.DedicatedRenderer`. It also serves as a stability fallback.
- Both modes sit behind the same QtNative API: the scene renders into a texture, and device and thread are configuration details.
- Several windows or texture outputs: the scene renders once per frame into a texture and every consumer samples it, identically in both modes.

## Open questions
- Does Qt's D3D11 RHI backend tolerate the state swap, and does it cache bindings beyond `endFrame`?
- When Qt renders directly into a Stride render target that already holds content: is the content preserved or cleared? Variants to test are `setColor(Qt::transparent)` and `PreserveColorContents` via `fromRhiRenderTarget`. The result decides whether the layer may optionally render directly; the default stays the intermediate texture.
- Windows with different size or DPI: render at the maximum resolution and scale, or use several instances?
- With a Qt render thread: which objects may live on which thread (`QQuickWindow` on the main thread, RHI on the render thread), and how does that fit the reconciler running on the main thread?
- Fences require `ID3D11Device5`/`ID3D11DeviceContext4` (Windows 10 1703+). Does Stride create the device accordingly?

## Rejected alternatives
- **Keyed mutex for synchronisation:** blocks on the CPU. vvvv already uses shared fences (GPU-side wait).
- **Only restoring state, or invalidating Stride's cache after `endFrame`:** error-prone. `SwapDeviceContextState` isolates completely and is proven in vvvv.
