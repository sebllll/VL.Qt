# Device Sharing: One QtNative Device per Process

How Qt gets onto the GPU next to Stride: QtNative owns one D3D11 device, Stride keeps its own, and a Qt scene reaches Stride as a shared texture. Implementation: `src/VL.Qt.Native/device/device.h` / `device.cpp`.

## Goals
- Qt windows, uploaded images and effects share one device, so a texture uploaded once can be drawn in every window.
- Qt never touches Stride's device or immediate context: no state swap, no risk for Stride's cached bindings.
- No added frame of latency for the Stride output, and a path to CPU parallelism for heavy scenes later.

## Design
### One QtNative device per process (Windows, D3D11)
- QtNative creates one `ID3D11Device` plus immediate context lazily at the first window (or the first upload), on the adapter Qt's D3D11 backend would pick.
- Every `QQuickWindow` gets it through `QQuickGraphicsDevice::fromDeviceAndContext` before its first expose (`device::attach`). Qt still creates one `QRhi` per window, all on this device.
- Next to them an upload `QRhi` on the same device (`device::uploadRhi`) serves the image packages.
- `device::generation()` changes with every new device; content made on another generation is never wrapped in a window's `QRhi`.
- Teardown order: texture wrappers, windows, upload `QRhi`, device. Device and upload `QRhi` are never released at exit; only a rebuild replaces them.
- Linux and every non-D3D11 backend: no-op, each window keeps its own device. `VLQT_SHARED_DEVICE=0` (set before process start) also keeps Qt's own device per window (diagnosis, A/B timing).

### Device loss
`device::checkLost` runs twice per pump and notices a removed device (`GetDeviceRemovedReason`). `device::rebuild` then calls the listeners registered with `device::addListener`: `lost` frees every object of the old device, `restored` runs once the new device and upload `QRhi` exist and uploads again. Every window moves to the new device with `setGraphicsDevice`; Qt notices the loss at that window's next `Present` and recreates its `QRhi`. Same `QQuickWindow`, same HWND, items keep their state.

If the device cannot be created (at the start or in a rebuild), `device::checkLost` tries again once per second. Meanwhile windows use devices of their own and keep them until their next loss, images are not uploaded, offscreen scenes report `DEVICE_LOST` (the caller binds again next frame), and the log says so once. Once a device exists, windows without a `QRhi` (offscreen scenes among them) get it and the listeners upload again.

### Texture output to Stride
Stride's device is not Qt's device, so a Qt scene reaches Stride as a texture shared between the two devices; this is `QtTexture` in the companion package `VL.Qt.Stride` (Windows only). Qt renders the scene offscreen on the QtNative device into a shared texture, and shared `ID3D11Fence`s order Qt's write and Stride's read on the GPU (no CPU wait). Details and decisions: [render-texture.md](render-texture.md).

### Stride layer
Three nodes, three names: `Renderer [Qt]` opens a Qt window, `QtRenderer [Stride]` (category `Stride.Rendering`, package `VL.Qt.Stride`) is a layer inside a Stride render target and outputs no texture, `QtTexture` outputs a texture. The layer uses the same shared texture and fences as `QtTexture`; it only adds the draw into the target. How it composes and why: [render-texture.md](render-texture.md#stride-layer-120-designed-2026-11-15).

## Open questions
- Adapter mismatch: can QtNative switch its device to Stride's adapter (reuse `device::rebuild`)? **Answered:** yes, `device::moveTo` rebuilds it on the foreign adapter while no window renders on it, else the bind fails with ADAPTER_MISMATCH; see [render-texture.md](render-texture.md).
- With a Qt render thread: which objects may live on which thread, and how does that fit the reconciler running on the main thread? **Answered:** the scene graph and `QRhi` of an offscreen scene live on a render thread of a pool, the `QQuickWindow`, items and reconciler stay on the main thread and the render thread touches items only while the main thread waits in `sync`; see [design/render-thread.md](../design/render-thread.md).
- Fences require `ID3D11Device5` / `ID3D11DeviceContext4` (Windows 10 1703+). Does Stride create its device accordingly? **Answered:** yes for Stride's device in vvvv; a device without them makes the bind fail with FENCE_FAILED.

## Rejected alternatives
- **Qt on Stride's device** (`fromDeviceAndContext` with Stride's device and `beginFrame … endFrame` inside a `SwapDeviceContextState`, like `DeviceContextScope` in VL.Skia): Stride would own the device, QtNative's device-loss handling would have to be rebuilt, Qt windows and image uploads would depend on Stride, and Qt rendering would load the main thread serially.
- **Keyed mutex for synchronisation:** blocks on the CPU. vvvv already uses shared fences (GPU-side wait).
