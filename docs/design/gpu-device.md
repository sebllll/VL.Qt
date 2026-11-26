# GPU device

How Qt gets onto the GPU next to Stride: QtNative owns one graphics device per process, Stride keeps its own, and a Qt scene reaches Stride as a shared texture. Code: `src/VL.Qt.Native/device/device.h` and `device.cpp`. The Stride side of the shared texture is in [stride-output.md](stride-output.md); the threads that use the device are in [render-thread.md](render-thread.md).

## Context

Qt Quick draws through the Qt Rendering Hardware Interface (`QRhi`), Qt's graphics abstraction over Direct3D 11, OpenGL and others. Every `QQuickWindow` creates one `QRhi` on a graphics device. By default each window would create its own device, and a texture uploaded for one window could not be drawn in another. Stride has its own D3D11 device and immediate context, and its cached bindings must not be disturbed by Qt.

The design below is for Windows (Direct3D 11). Linux and every other backend keep one device per window; see "Other backends".

## Design

### One QtNative device per process

- QtNative creates one `ID3D11Device` and its immediate context lazily, at the first window or the first upload, on the adapter Qt's D3D11 backend would pick. Windows, uploaded images and effects share it, because a texture uploaded once can then be drawn in every window.
- Every `QQuickWindow` gets the device through `QQuickGraphicsDevice::fromDeviceAndContext` before its first expose (`device::attach`). Qt still creates one `QRhi` per window, all on this device.
- Next to them an upload `QRhi` on the same device (`device::uploadRhi`) serves the image packages.
- Qt never touches Stride's device or immediate context, because there is no state swap and so no risk for Stride's cached bindings.
- `device::generation()` changes with every new device. Content made on another generation is never wrapped in a window's `QRhi`.
- Teardown order: texture wrappers, windows, upload `QRhi`, device. The device and the upload `QRhi` are never released at exit; only a rebuild replaces them.

### All unsafe D3D11 code stays in C++

C# hands QtNative Stride's `ID3D11Device*`, its immediate context and the texture's shared handle. QtNative opens the texture, creates and opens the fences and issues `Signal` and `Wait` on both contexts. Because of this, no unsafe D3D11 code lives in C#, and the SmokeTest can drive the whole path with a second D3D11 device that stands in for Stride.

### Same adapter and adapter move

A shared handle does not cross adapters, so QtNative's device must sit on Stride's adapter. `vlqt_offscreen_bind` compares the adapter LUIDs. When they differ, bind moves the device with `device::moveTo`, which rebuilds it on the foreign adapter like a device loss (images upload again). It does that only while `device::canMove()` is true: no on-screen window has a `QRhi` on the device and no other scene is bound. Qt replaces a window's `QRhi` only after it noticed a device loss, so a window on a healthy device would keep the old device, and images of the new device would no longer draw in it. In every other case bind fails with `ADAPTER_MISMATCH` and a warning is logged once; nothing crashes.

A patch with only `QtTexture` nodes therefore follows Stride's adapter; a patch with a Qt window keeps the adapter of its device.

### Device loss

`device::checkLost` runs twice per pump and notices a removed device (`GetDeviceRemovedReason`). `device::rebuild` then calls the listeners registered with `device::addListener`:

- `lost` frees every object of the old device and keeps the sources.
- `restored` runs once the new device and upload `QRhi` exist and uploads again. A file image is read from disk again, because its CPU copy is gone after the upload; an image made from pixels uses its converted copy.

Every window moves to the new device with `setGraphicsDevice`. Qt notices the loss at that window's next `Present` and recreates its `QRhi`. The window, its HWND and its items keep their state. Offscreen scenes are unbound and bound again by the caller ([render-thread.md](render-thread.md), "Lifetime of a scene").

### No device available

If QtNative cannot create its device (at the start or in a rebuild), `device::checkLost` tries again once per second. Meanwhile:

- windows use devices of their own and keep them until their next loss,
- images are not uploaded (they keep CPU copies),
- offscreen scenes report `DEVICE_LOST` and the caller binds again next frame,
- the log says so once.

Once a device exists, windows without a `QRhi` (offscreen scenes among them) get it and the listeners upload again.

### Other backends

On Linux and every non-D3D11 backend `device::attach` is a no-op: each window keeps its own device, and `sharedContext` and `uploadRhi` return null. Offscreen scenes need D3D11 and report `UNSUPPORTED`.

`VLQT_SHARED_DEVICE=0` does the same on Windows: every window keeps Qt's own device. It is a diagnosis switch (A/B timing, telling a shared-device problem from another one) and stays an environment variable, because it acts before Qt starts and so cannot be a pin. Set it before the process starts.

## Consequences

- Qt windows, images and offscreen scenes share one device, so they share its immediate context. Threads that use it follow the lock rules in [render-thread.md](render-thread.md).
- A scene sees Stride's device only through the shared texture and two shared fences. Stride's device needs `ID3D11Device5` and `ID3D11DeviceContext4` (Windows 10 1703 and later), which Stride in vvvv has; without them bind fails with `FENCE_FAILED`.
- The shared device is a single point of failure for every Qt window: that is why its loss and its failed creation are handled as described above rather than by recreating windows.
- SmokeTest scenarios `device-lost` and `shared-device` cover loss and sharing.

## Alternatives considered

- **Qt on Stride's device** (`fromDeviceAndContext` with Stride's device and `beginFrame` … `endFrame` inside a swapped device context state, as `DeviceContextScope` does in VL.Skia): Stride would own the device, QtNative's device-loss handling would have to be rebuilt, Qt windows and image uploads would depend on Stride, and Qt rendering would load the main thread serially.
- **Keyed mutex for synchronisation:** it blocks on the CPU. Shared fences make the GPU wait instead (see [stride-output.md](stride-output.md)).
- **A device per render thread:** uploaded images would have to be opened as shared resources per scene, and device loss and adapter move would exist twice ([render-thread.md](render-thread.md)).

## Open questions

None.

## Evidence

SmokeTest scenarios `device-lost` (loss and rebuild, also through D3D11On12 on the test device) and `shared-device` (windows on one device refresh independently); `qttexture` covers adapter move and mismatch. Run with `tools/build.ps1 -Test -Only <scenario>`.
