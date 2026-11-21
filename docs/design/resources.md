# Resources

The resource manager of QtNative: one process-wide table of resources (images, snapshots, shaders, audio taps) that is independent of any window. Code: `src/VL.Qt.Native/resources/` (`resources.h` is the interface that the exports in `resource_exports.cpp` and the pump in `app/app.cpp` call, `resource_kind.h` the interface of a resource kind), the kinds in `image_resource.*`, `snapshot_resource.*`, `shader_resource.*` and `audio_tap.*`. User pages: [images.md](../images.md), [video-audio.md](../video-audio.md).

## Context

C# holds only an id; everything else happens in QtNative. A resource is made once (decoded on a worker, uploaded to the GPU within a byte budget per frame) and then used by any number of elements in any number of windows. Bugs in this layer are hard to debug from vvvv, so the manager is simple and logs everything under the category `VL.Qt.Resources`.

## Design

### Ids

An id is a u64: index in the low 32 bits, generation in the high 32 bits, assigned by `vlqt_res_create`. After a resource is released its index is reused with a higher generation, so an old id never reaches a new resource. An unknown or stale id logs a warning and returns -9. Log lines write an id as `#index.generation` (`idText`).

### States

`Pending -> Decoding -> Uploading -> Ready | Failed -> Released` (`vlqt_resource_state` in `protocol.h`). Every change goes through one function, `transition`, which rejects and logs invalid changes and logs valid ones on debug level. `Decoding` means "the content is being made" for every kind: a decode on a worker, a snapshot grab in flight.

### Threads

- Every entry point runs on the vvvv main thread (the thread of `vlqt_app_process_events_for`). One mutex still guards the table, so a stray call from another thread cannot corrupt it.
- A fixed worker pool (default `min(4, cores - 1)`, environment variable `VLQT_RES_WORKERS`, started at the first job) runs only `Kind::decode` on pure data (bytes, `QImage`). Workers never touch Qt Quick, the RHI or the table and never log. Results come back through one queue guarded by the same mutex.
- Upload (`Kind::upload`) and destroy (`Kind::destroy`) run on the main thread: uploads in `beginFrame` within the byte budget per frame, frees in `endFrame`.

### Reference counts

A resource has one host reference (C#, dropped by `vlqt_res_release`) and element references (`acquire` and `releaseElement`). When both counts are zero the resource is freed at the end of the frame, never in a worker; a running decode is cancelled or finishes first. After the host release no event is sent for the id any more.

### Event queue

`vlqt_res_poll` returns records with the header of the window poll: u16 event, u16 flags (0), u32 length, u64 id, payload (4-byte aligned, length = 8 + payload bytes).

| Event | Payload |
|---|---|
| STATE | u8 state |
| ERROR | UTF-8 text |
| INFO | u32 width, u32 height, u32 format, u64 bytes |
| WARNING | UTF-8 text (`Kind::takeWarning` after a decode, at most one per resource) |

### Replacement

`start` on a Ready resource decodes and uploads a second content next to the current one. The state stays Ready (no STATE record). When the new upload is complete the current content is swapped (the old one is destroyed) and a new INFO record is sent. A failed replacement makes the resource Failed. `start` on a Failed resource starts over (Failed -> Decoding). `start` while a decode or upload runs keeps only the newest content and starts it afterwards.

### External content

A snapshot (`snapshot_resource.h`) is made on the main thread (`beginExternal` / `finishExternal`) without a worker and without an upload, so Decoding -> Ready is allowed for that kind only. `vlqt_res_wait` does not block on it. An audio tap is Ready at its init.

### Device loss

The manager is a device listener (`device.h`).

- **Lost:** `Kind::releaseDevice` runs on every content that no worker holds: textures, staging buffers and running uploads of the old device go, the sources stay.
- **Restored:** every Ready resource with GPU content gets a replacement from `Kind::restore` (decode and upload within the budget). The state stays Ready, INFO is sent after the swap, so C# sees no change. A running replacement or queued content is renewed the same way. A first upload restarts its decode (Uploading -> Decoding, allowed only here).
- A decode that ran for the old device (`Kind::stale`) is restarted when its result comes back, also when it failed.
- Uploads that fail because the device is removed report no error (the `Kind::upload` contract) and wait for the rebuild instead of ending in Failed.
- Snapshots are CPU only and stay as they are.

## Consequences

- A new resource kind implements `Kind` and is registered in `resource_registry.cpp`, the only file that names every kind.
- The mutex is held across `Kind::upload` and `Kind::destroy`, so no worker can take a job or hand back a result meanwhile.
- Content a worker holds is never touched by the main thread until the worker hands it back; `destroy` is called only for content no worker holds.

## Alternatives considered

None recorded.

## Open questions

None.

## Evidence

`ImageResourceTest` (state machine, budget, wait, replacement) and `DeviceLostTest` in `src/VL.Qt.SmokeTest`; the F5 overlay, Resources page, shows the table live (`docs/diagnostics.md`).
