# Review 20 – QtNative: image and snapshot resources

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/resources.h`, `resources.cpp`, `image_resource.h`, `image_resource.cpp`, `snapshot_resource.h`, `snapshot_resource.cpp`, `image_writer.h`, `image_writer.cpp`, `qtimageitem.h`, `qtimageitem.cpp`, `docs/images.md`. Skimmed (targeted excerpts): `device.h` (threads, listener, generation), `offscreen.cpp:60-80, 565-640` (render threads), `vlqt.cpp:1115-1130, 2205-2235` (frame hooks, C entry points), `shadereffectitem.cpp` (users of `imageTextureFor`), `vlqt.h` (return codes), `src/VL.Qt/Native.cs` (test entry points), the file list of `src/VL.Qt.SmokeTest`. Not read: `shader_resource.*`, `audio_tap.*` (other kinds registered in the same manager), the C# side of the resources.

## 1 Summary

The resource layer is carefully built: one generic manager (ids with generations, a checked state machine, host and element references, a decode worker pool, a per-frame upload budget, replacement without an empty frame, device-loss restore) with three kinds (image, snapshot, plus shader and audio tap outside this area). Logging and the F5 page make it observable. Its weak points are where it meets Qt's threads and where it grew by packages:

1. **Must:** an Image element in a threaded offscreen scene (QtTexture) frees its window texture on the main thread while that scene's render thread may still render with it (`qtimageitem.cpp:271`, `:200`); the header still claims "everything runs on the main thread" (NRES-01).
2. A per-frame `set_pixels` (VideoSourceToQtImage, ToQtImage on a live source) allocates a CPU copy, a new GPU texture, and a new wrapper per window every frame, and uploads through an offscreen frame that waits for the GPU (NRES-02).
3. `resources.cpp` (1,127 lines) is one `Manager` doing slot table, state machine, refcounts, events, worker pool, uploads and device loss under one mutex that is held across GPU work; it should be cut in four parts with the table main-thread-only (NRES-03, NRES-04).
4. `ImageResource` is one class with input, decode result, D3D11 upload state and preview, raw `void*` COM pointers and friend free functions; split into content view, decode, and two upload paths with RAII (NRES-05).
5. Names that mislead the reader: `Work` (is the content), `res::snapshot` (is the F5 overview, not the snapshot kind), `snap`; duplicated helpers and pixel-format tables (NRES-06, NRES-08).

## 2 What works and must survive

- **Ids = index | generation** (`resources.cpp:41-43`, `:928`): an old id never reaches a new resource; stale ids give -9 with a warning. Keep.
- **One transition function with an explicit table** (`resources.cpp:59-71`, `:702-717`): every state change is checked and logged. Keep; it is what makes the layer debuggable.
- **Freeing only at the frame end, never in a worker** (`endFrame`, `resources.cpp:573-590`), and only when host *and* element references are zero (`maybeQueueFree`, `:899`). The element reference is what lets C# release an id while an Image element still shows it.
- **Replacement keeps the old content until the new upload is complete** (`uploadStep`, `:784-793`), and `contentEpoch` lets items poll once per pump instead of per resource (`qtimageitem.cpp:255-264`). No empty frame on reload or on device restore.
- **The window wrapper holds its own reference on the native texture** (`qtimageitem.cpp:96`, `:46`): a resource replaced or freed while a window still draws it never leaves a dangling D3D texture. Any refactoring of texture ownership must keep this guarantee.
- **Workers touch only pure data plus free-threaded D3D11 device calls**; they never log, never touch Qt Quick, the RHI or the table (`resources.cpp:989-1011`, `image_resource.cpp:327`).
- **Worker upload path for files** (`image_resource.cpp:97-108`, `:327-437`): empty textures created in the worker, the copy into a mapped staging texture in a task, then one GPU copy. Measured reason in the comment (CreateTexture2D with initial data blocks the main thread's context). Keep the path; the issue references go (K1), the measured reason stays in one sentence.
- **Frame budget semantics:** a worker-path image counts its full size against the budget (`uploadNow` returns `total`), so at most one large image lands per frame. Intended.
- **Hot paths (no allocation per frame when idle):** `res::beginFrame` / `endFrame` (early returns on empty queues), `QtImageItem::afterResourceFrame` (one epoch compare), `QtImageItem::updatePaintNode` (`thread_local` quad scratch, node reuse, `setTexture` only on change), `snap::endFrame` (early return), `snap::stats` (fixed ring), `pushEvent` (`QByteArray::resize(0)` keeps capacity).
- **Snapshot contract:** pixel size = DIP × scale independent of the device pixel ratio (`snapshot_resource.cpp:291-296`, `requestSide`, the one-pixel rescale in `onReady`). Deterministic export depends on it.
- **A grab never stays in Decoding** (`snap::endFrame`, `snapshot_resource.cpp:408-440`): window gone, item gone, not exposed, unbound offscreen scene, or a 10 s timeout all end in Failed.

## 3 Findings

### NRES-01 Free window textures of offscreen scenes on their render thread
- **Kind:** threading
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/qtimageitem.cpp:200-207`, `:266-274`, `:30-49`, `:64-115`; `src/VL.Qt.Native/qtimageitem.h:13-14`; `src/VL.Qt.Native/offscreen.cpp:66-72`, `:614-633`
- **Finding:** The scene graph rule of Qt Quick: an item (`QQuickItem`) lives on the GUI thread, but its scene graph nodes and textures (`QSGNode`, `QSGTexture`, the `QRhiTexture` under them) belong to the thread that renders the window. Qt calls `updatePaintNode` on the render thread while the GUI thread is blocked ("sync"); `releaseResources` is called on the GUI thread, so an item must not delete render resources there directly but hand them to the render thread (Qt's own `QQuickImage` does that with a cleanup job); `invalidateSceneGraph` is called on the render thread while its graphics objects still exist.
  On-screen windows use the basic render loop (everything on the main thread), so they are safe. Threaded offscreen scenes are not: `offscreen.cpp:66-72` says sync runs on the scene's render thread while the main thread waits, but render + endFrame are *posted* (`offscreen.cpp:624-633`) and run while the main thread goes on. Against that rule, per entry point:
  - `updatePaintNode` (`qtimageitem.cpp:283`): runs in sync on the render thread. Creating the wrapper there is correct. It also calls `device::uploadRhi()` and `window->setProperty(...)` (`:77-83`), which `device.h:26` declares main-thread-only; safe only because the main thread is blocked during sync. Acceptable, but the contract must say so.
  - `invalidateSceneGraph` (`:266`): render thread, QRhi alive. Correct.
  - `releaseResources` (`:271`) and the destructor (`:200`, via `m_texture`'s destructor): main thread. If this item held the last reference, `~ImageTexture` deletes the `QSGPlainTexture` and its `QRhiTexture` wrapper of the scene's QRhi on the main thread while the render thread may be executing a posted frame on that QRhi. A `vlqt_window_apply` that removes an Image element from a QtTexture scene between two batches hits exactly this. Confirmed by reading the threading model; no crash reproduced.
  - The header comment `qtimageitem.h:13` ("Everything runs on the main thread (render loop basic)") is wrong since threaded offscreen scenes exist, and `qtimageitem.cpp:299` ("render loop basic: main thread") describes a `thread_local` that is correct only *because* of the render threads.
- **Proposal:** Never let the last `ImageTexture` reference drop on the main thread for a window with a render thread. Add to the offscreen/render-thread layer one function `void render_thread::retire(QQuickWindow* window, std::shared_ptr<void> resource)`: for a window with a render thread it queues the pointer and the thread drops it at the start of its next `beginAndSync` job (or in `invalidate`/unbind, which already drain the thread); for any other window it drops it at once. `QtImageItem::releaseResources` and `~QtImageItem` call `render_thread::retire(window(), std::move(m_texture))` instead of `m_texture.reset()`. Alternative: `QQuickWindow::scheduleRenderJob` with a `QRunnable` holding the pointer; recommended only if it is verified that render-control windows run such jobs (Qt runs them from its own render loops, which a `QQuickRenderControl` scene does not use). Rewrite `qtimageitem.h:13-14` to state the rule: "Items: main thread. `updatePaintNode` and `invalidateSceneGraph`: the window's render thread (main thread for on-screen windows), the main thread blocked during `updatePaintNode`. Textures are released through `render_thread::retire`."
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/qtimageitem.cpp`, `qtimageitem.h`, `render_thread.h`, `render_thread.cpp`, `offscreen.cpp`; the same change in `shadereffectitem.cpp` (see section 6)
- **Depends on:** none
- **Verification:** new SmokeTest scenario: threaded offscreen scene with an Image element, remove the element by an apply right after `render_batch` posts the render, repeated a few hundred times under `QT_FATAL_WARNINGS=1` and the D3D11 debug layer; existing `ImageTest`, `ImageShareTest`, `DeviceLostTest`.

### NRES-02 Reuse textures for content that is replaced every frame
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/image_resource.cpp:651-685`, `:247-318`, `:264-279`; `src/VL.Qt.Native/resources.cpp:784-793`; `src/VL.Qt.Native/qtimageitem.cpp:64-115`, `:117-133`
- **Finding:** `docs/images.md:14` says a VL.Video source "replaces the content of the same `QtImage`". In QtNative every `set_pixels` makes a new `ImageResource`: a `width × height × bpp` `QByteArray` copy on the main thread (`:675`), a conversion `QImage` in a worker, a new `QRhiTexture` created on the upload QRhi (`:270-272`), and the stripes upload in an offscreen frame whose `endOffscreenFrame` waits for the GPU (the comment at `:102-103` measured 2.2 ms per frame for the same wait). On the swap the old texture is destroyed (`resources.cpp:785`), so in every window that shows it `textureFor` creates a new wrapper, a new `QSGPlainTexture`, an `AddRef` and a new cache entry (`qtimageitem.cpp:89-104`). For a 1080p video that is about 8 MB allocated and one GPU texture created and destroyed per frame, plus the GPU wait on the main thread. Not measured in this review.
- **Proposal:** Introduce a shared GPU texture object and a small pool in the image kind:
  - `struct GpuTexture { QRhiTexture* rhiTexture; /* upload QRhi */ ComPtr<ID3D11Texture2D> native; QSize size; QRhiTexture::Format format; bool mipmaps; }`, owned through `std::shared_ptr<GpuTexture>`. `ImageResource` holds one; `ImageTexture` holds the same `shared_ptr` instead of a raw `AddRef` (this keeps the guarantee of section 2 and removes the manual `AddRef`/`Release`).
  - The image kind keeps a pool of released `GpuTexture`s keyed by (size, format, mipmaps); `uploadNow` takes one whose `use_count() == 1` (no window wrapper left) before creating a new one. Pool limit: two per key, freed on device loss.
  - The window cache keys on the `GpuTexture*` (plus window and QRhi, NRES-07), so a recycled texture reuses its wrapper.
  - `setPixels` reuses the raw buffer of the previous Work of the same id when size and format match (move it in `Manager::start` through a `Kind::recycle(Work& old, Work& fresh)` hook), or converts on the main thread directly into the mapped staging texture for the direct formats (6–9).
  - Use the staging/worker path for pixels as well when `VLQT_IMG_MIPMAPS` is off and the size is above the budget, so no offscreen-frame GPU wait remains in the per-frame path. Decide with a measurement (profile `HowTo Show a VL.Video player in Qt` before and after, `docs/profiling.md`).
- **Effort:** L, mechanical: no
- **Touches:** `src/VL.Qt.Native/image_resource.h`, `image_resource.cpp` (or the split files of NRES-05), `qtimageitem.cpp`, `resources.h`, `resources.cpp`
- **Depends on:** NRES-05 (easier after the split), NRES-07
- **Verification:** `ImageResourceTest`, `ImageTest`, `ImageShareTest`, `DeviceLostTest`; help patch `HowTo Show a VL.Video player in Qt` with the F5 Resources page (no growing texture count) and a CPU profile.

### NRES-03 Cut resources.cpp into table, worker pool, events and test kind
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.cpp:28-1127`, `src/VL.Qt.Native/resources.h:1-190`
- **Finding:** One anonymous-namespace class `Manager` (`:164-1026`) holds the slot table and id scheme, the state machine, host/element refcounts, replacement and queued content, the external path, the binary event queue (`pushEvent`, `dropEvents`), the worker pool (`ensureWorkers`, `workerLoop`, job/result queues, two condition variables), the per-frame upload loop, the device listener and test hooks, plus the kind registry (`kindOf`, `:675-686`, a switch that names every kind of the library). A reader who wants to change the worker count or the event format has to understand the whole file. `resources.h` mixes the kind interface, the C API backends, the frame hooks, the F5 overview and the test hooks.
- **Proposal:** Same behaviour, these files (all `namespace vlqt::res`):
  - `resource_kind.h`: `State`, `stateName`, `Content` (ex `Work`, NRES-06), `Info`, `Kind`, the return codes.
  - `resource_events.h/.cpp`: `class EventQueue { void push(uint16_t ev, uint64_t id, std::span<const std::byte>); void drop(uint64_t id); int poll(unsigned char* out, int cap); }` (from `:941-974`, `:248-259`).
  - `resource_workers.h/.cpp`: `class WorkerPool { void submit(uint64_t id, Kind*, std::shared_ptr<Content>); bool takeQueued(uint64_t id, Job&); void drainResults(std::deque<Result>&); bool waitFor(uint64_t id, deadline, Result&); void stop(); }` with its own mutex and the `runDecode` wrapper (from `:139-162`, `:976-1011`, the job part of `wait`).
  - `resource_manager.h/.cpp`: `class Manager` with the slot table, transitions, refcounts, replacement, external content, uploads, device listener; uses `EventQueue` and `WorkerPool`.
  - `resource_registry.cpp`: `Kind* kindOf(int kind)`; the only file that includes every kind header.
  - `resources.h/.cpp`: the free C API backends and frame hooks as today (the façade `vlqt.cpp` calls), `overview()` (NRES-06).
  - `resource_test_kind.cpp`: `TestKind`, `testStart`, `testTransition`, `testElementRef` (see NRES-09).
  The 46-line header comment of `resources.h` moves to `docs/topics/resources.md` (NRES-14); each new header keeps a three-line summary.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/resources.h`, `resources.cpp`, new `resource_kind.h`, `resource_events.h/.cpp`, `resource_workers.h/.cpp`, `resource_manager.h/.cpp`, `resource_registry.cpp`, `resource_test_kind.cpp`, `src/VL.Qt.Native/CMakeLists.txt`, includes in `image_resource.h`, `snapshot_resource.h`, `shader_resource.h`, `audio_tap.h`
- **Depends on:** NRES-04 (do both in one go), NRES-06
- **Verification:** `ImageResourceTest` (state machine, budget, wait, replacement), `DeviceLostTest`, `--drift`, all SmokeTest scenarios.

### NRES-04 Make the slot table main-thread-only and stop holding a lock across GPU work
- **Kind:** threading
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.h:14-15`; `src/VL.Qt.Native/resources.cpp:521-543`, `:769-802`, `:595-607`, `:990-1011`; `src/VL.Qt.Native/image_resource.cpp:448-451`, `:292-310`
- **Finding:** `resources.h:14-15` says every entry point runs on the main thread and the one mutex exists only "so a stray call from another thread cannot corrupt it". In fact the mutex is also the worker queue lock, and the main thread holds it across `Kind::upload` (an offscreen frame that waits for the GPU, `image_resource.cpp:294-309`), across `Kind::destroy`/`releaseDevice` (which waits for a running copy task, `:450-451`) and through `deviceLost`. While it is held, no worker can take a job or hand back a result. It also does not deliver the promised safety: a stray thread calling `start` would run `Kind` code (D3D, QRhi) that is main-thread-only anyway. A reader cannot tell which data the lock protects.
- **Proposal:** The slot table, events, uploads and free list are main-thread-only, without a mutex; every API function begins with `assertMainThread("create")` (one helper in `resource_manager.cpp` that logs `qCCritical` once and returns `errorRc` on another thread). Only `WorkerPool` (NRES-03) has a mutex, covering its job and result queues. `wait` keeps its behaviour: take the job from the pool and decode inline, or block on the pool's result condition. Document in the header: "Main thread only. Workers see only the `Content` they were given and the pool's queues."
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/resource_manager.cpp`, `resource_workers.cpp` (NRES-03), `resources.h`
- **Depends on:** NRES-03
- **Verification:** `ImageResourceTest` (wait with queued and running decode, budget stripes), `HowTo Preload Images` with the F5 page (decode jobs drain while uploads run).

### NRES-05 Split ImageResource into content view, decode and two upload paths with RAII
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/image_resource.h:45-95`; `src/VL.Qt.Native/image_resource.cpp:125-629`, `:320-465`, `:97-108`
- **Finding:** `ImageResource` carries four roles in one object: input (path or raw pixels), decode result (`m_image`, `m_preview`, `m_bgra`, `m_warning`), D3D11 worker-path state (`m_workerTexture`, `m_staging`, `m_workerSrv`, `m_mapped`, `m_copy`, all `void*` to keep `d3d11.h` out of the header) and the RHI upload state (`m_texture`, `m_uploadedRows`). Access is granted to five friends (`:65-69`), four of them free functions. `ImageKind` is 500 lines with `#ifdef Q_OS_WIN` blocks inside its methods. The COM objects are released by hand in `releaseDevice`; the destructor releases nothing, so the correctness of every path depends on the manager calling `Kind::destroy` first (it does today, `resources.cpp:912-917`, but nothing enforces it). `ImageTexture::native` repeats the manual `AddRef`/`Release` (`qtimageitem.cpp:35`, `:46`, `:96`).
- **Proposal:**
  - `image_resource.h`: `class ImageContent final : public Content` with only the read view used by elements and shader effects: `texture()`, `nativeTexture()`, `size()`, `format()`, `image()`, `generation()`, `preview()`. Fields stay private; the kind's files get access through one `friend class ImageKind` and an internal header, no friend free functions (`load`, `setPixels`, `readPreview` become `ImageKind` statics called by thin free functions).
  - `image_kind.cpp`: decode (`readFile`, `convertPixels`, `makePreview`), `restore`, `stale`, `info`, the C API backends.
  - `image_upload_d3d11.h/.cpp` (Windows only): `class D3D11Upload { bool createInWorker(ID3D11Device*, QSize, bool bgra, bool mips); bool fill(const QImage&, bool sync, std::string& error); void release(); }` with `Microsoft::WRL::ComPtr` members, `releaseInPool`, `copyToStaging`; held by `ImageContent` as `std::unique_ptr<D3D11Upload>` (pimpl, so the header stays free of `d3d11.h`). Its destructor releases everything.
  - `image_upload_rhi.cpp`: the stripes path (`uploadNow` minus the D3D11 part).
  - `pixel_format.h/.cpp`: see NRES-08.
  - `ImageContent::~ImageContent` releases the GPU objects (calls the same code as `releaseDevice`), so a forgotten `destroy` cannot leak.
  - `VLQT_IMG_UPLOAD` (`:104-108`): keep only if it is listed in `docs/diagnostics.md`; otherwise remove (question Q3).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/image_resource.h`, `image_resource.cpp` → `image_kind.cpp`, new `image_upload_d3d11.h/.cpp`, `image_upload_rhi.cpp`, `pixel_format.h/.cpp`, `qtimageitem.cpp`, `shadereffectitem.cpp` (type name), `vlqt.cpp` (includes), `CMakeLists.txt`
- **Depends on:** NRES-06 (names), NRES-08
- **Verification:** `ImageResourceTest`, `ImageTest`, `ImagePreviewTest`, `ImageShareTest`, `DeviceLostTest`; help patches `HowTo Draw an Image`, `HowTo Preload Images`, `HowTo Use an IImage`.

### NRES-06 Rename Work, readyWork, res::snapshot and snap
- **Kind:** naming
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.h:69-75`, `:133-134`, `:165-182`; `src/VL.Qt.Native/snapshot_resource.h:32`; `src/VL.Qt.Native/resources.cpp:121-123`
- **Finding:** `Work` is not work: it is the content of a resource (pixels, texture, path). `readyWork` returns the visible content. `res::snapshot(rows, out)` fills the F5 overview, while "snapshot" in the same layer is a resource kind (`res::snap`); a reader searching for the snapshot code lands in the wrong place. `Slot::next` / `Slot::queued` are a replacement in progress and the newest request waiting behind it. `State::Decoding` also means "grab in flight" for snapshots and "Ready at init" for audio taps (`resources.cpp:395`).
- **Proposal:** `Work` → `Content`; `readyWork` → `readyContent`; `res::snapshot(std::vector<Row>&, Overview&)` → `res::overview(std::vector<OverviewRow>&, OverviewTotals&)`; namespace `snap` → `snapshot`; `SnapshotWork` → `SnapshotContent`; `Slot::next` → `replacement`, `Slot::queued` → `waiting`, `nextBytes`/`nextUploading` → `replacementBytes`/`replacementUploading`. Keep `State` names (they are protocol values seen by C#) and document in `resource_kind.h` that `Decoding` means "content is being made" for every kind.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/resources.h`, `resources.cpp`, `image_resource.h/.cpp`, `snapshot_resource.h/.cpp`, `shader_resource.*`, `audio_tap.*`, `qtimageitem.*`, `shadereffectitem.cpp`, the F5 overlay file that calls `res::snapshot` and `snap::stats`
- **Depends on:** none (do it first, before NRES-03/05)
- **Verification:** build; all SmokeTest scenarios.

### NRES-07 Key the window texture cache by QRhi and replace the dynamic-property flags
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/qtimageitem.cpp:117-133`, `:78-87`; `src/VL.Qt.Native/device.cpp:344`, `:532-536`; `src/VL.Qt.Native/offscreen.cpp:182`, `:394`
- **Finding:** The cache matches entries on `(window, content)`. A wrapper belongs to the window's QRhi, which Qt replaces after a device loss while the `QQuickWindow` stays. The entry is safe only as long as every holder resets its `shared_ptr` in `invalidateSceneGraph`; `QtImageItem` does, `ShaderEffectItem` does (`shadereffectitem.cpp:980-990`), but nothing in the cache enforces it. Suspicion, not confirmed: if Qt's basic loop does not call `invalidateSceneGraph` on items during its device-loss handling, an item with old-generation content would reuse a wrapper of the destroyed QRhi until the replacement arrives. What would confirm it: a breakpoint on `QtImageItem::invalidateSceneGraph` in `DeviceLostTest`. Separately, the shared-device state is a string-keyed dynamic property `_vlqt_shared_device` written in `device.cpp` and read in `qtimageitem.cpp` and `offscreen.cpp`, plus `_vlqt_device_warned` as a once-flag: hidden coupling a reader cannot find by following calls.
- **Proposal:** Cache key `(QQuickWindow*, QRhi*, GpuTexture*)` (or `content` until NRES-02); an entry whose QRhi differs from `window->rhi()` is never returned. Add to `device.h`: `bool isOnSharedDevice(const QQuickWindow*)` and `void markOnSharedDevice(QQuickWindow*, bool)`, backed by a main-thread set in `device.cpp`; the warn-once flag becomes a `static std::unordered_set<const QQuickWindow*>` in `qtimageitem.cpp` (entries removed on `QObject::destroyed`). Move the cache with `ImageTexture` and `imageTextureFor` into `image_texture_cache.h/.cpp`, since `ShaderEffectItem` uses it too.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/qtimageitem.h/.cpp`, new `image_texture_cache.h/.cpp`, `device.h`, `device.cpp`, `offscreen.cpp`, `shadereffectitem.cpp`
- **Depends on:** none; combine with NRES-02 if done together
- **Verification:** `DeviceLostTest` with an Image element and an image-sampling ShaderEffect visible; `ImageShareTest` (one image in several windows).

### NRES-08 One home for ids, time, pixel formats and the log category
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.cpp:46-49`, `:134-137`, `:30`; `src/VL.Qt.Native/snapshot_resource.cpp:38-46`, `:68-71`, `:32`; `src/VL.Qt.Native/qtimageitem.cpp:59-62`, `:25`; `src/VL.Qt.Native/image_resource.cpp:26`, `:34-58`; `src/VL.Qt.Native/image_writer.cpp:20-51`
- **Finding:** `idText` exists three times in two formats (`#index.generation` in resources and snapshot, `index/generation` in the item), so log lines of one resource cannot be grepped across files. `steadyMs` exists twice. The VL `PixelFormat` enum exists twice (`image_resource.cpp:34`, `image_writer.cpp:21`) with two different "bytes per pixel / QImage format" tables, plus a comment copy in `protocol.h:694`. Three files each define their own `Q_LOGGING_CATEGORY(..., "VL.Qt.Resources")`; the item logs as `VL.Qt.Image.Item`.
- **Proposal:** `resource_kind.h` exports `QByteArray idText(uint64_t id)` (format `#index.generation`) and `Q_DECLARE_LOGGING_CATEGORY(lcRes)`, defined once in `resources.cpp`; `steadyMs()` goes into the existing shared time helper if there is one, else into `resource_kind.h`. New `pixel_format.h/.cpp`: `enum class VlPixelFormat : int { R8 = 1, …, R16G16B16A16F = 12 }`, `int bytesPerPixel(VlPixelFormat)`, `QImage::Format qimageFormat(VlPixelFormat, bool premultiplied)` (invalid for R32F/R32G32F); `image_resource.cpp` and `image_writer.cpp` use it. The item logs to `VL.Qt.Resources` or to a documented `VL.Qt.Image` category; list the categories in `docs/diagnostics.md`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/resources.cpp`, `resource_kind.h` (or `resources.h`), `snapshot_resource.cpp`, `qtimageitem.cpp`, `image_resource.cpp`, `image_writer.cpp`, new `pixel_format.h/.cpp`, `CMakeLists.txt`, `docs/diagnostics.md`
- **Depends on:** none
- **Verification:** `ImageResourceTest` (set_pixels formats), `ImageWriteTest`.

### NRES-09 Replace the resource test hooks with production paths where possible
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.cpp:73-113`, `:655-672`, `:1102-1125`; `src/VL.Qt.Native/resources.h:184-187`; `src/VL.Qt.Native/image_resource.h:110-114`; `src/VL.Qt.Native/image_resource.cpp:726-785`; `src/VL.Qt/Native.cs:475-511`
- **Finding (K6 for this area):** Test hooks: `TestKind` / `VLQT_RES_KIND_TEST` and `vlqt_test_res_start` (a kind with controllable decode time, upload size and failures), `vlqt_test_res_transition` (drives the transition table directly), `vlqt_test_res_element_ref` (fake element references), `vlqt_test_res_read_pixels` (texture readback through an offscreen frame), `vlqt_test_write_image` (writes a striped PNG/JPG). They are exported from the library and imported in `Native.cs`.
- **Proposal:**
  - `testWrite` → delete; the SmokeTest writes its input file with `vlqt_image_write` (production writer) from pixels it builds itself.
  - `testElementRef` → delete; the tests use a real Image element (`PROP_IMAGE`) for element references.
  - `testTransition` → delete; the rejected transitions it checks are covered by behaviour through `TestKind` (start on a Decoding resource, release during decode, …). If a direct table test is wanted, make `allowed()` a pure function in `resource_kind.h` and test it in a native unit test, not through the C API.
  - `testRead` → replace by a snapshot of a 1:1 Image element with `smooth` off where exact pixels are compared; keep only if a test needs the texture without drawing (then rename `vlqt_debug_res_read_pixels` and say so).
  - `TestKind` + `testStart` stay (no production kind gives deterministic timings), in `resource_test_kind.cpp` (NRES-03), with a summary saying it exists for the SmokeTest.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/resources.h/.cpp`, `resource_test_kind.cpp`, `image_resource.h/.cpp`, `vlqt.h`, `vlqt.cpp`, `src/VL.Qt/Native.cs`, `src/VL.Qt.SmokeTest/ImageResourceTest.cs`, `ImageTest.cs`, other SmokeTest files that call these hooks (search `vlqt_test_res_`, `vlqt_test_write_image`)
- **Depends on:** NRES-03
- **Verification:** the rewritten SmokeTest scenarios pass; `--drift`.

### NRES-10 Do not stall the upload queue behind a running copy task
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.cpp:530-541`; `src/VL.Qt.Native/image_resource.cpp:261-262`, `:412-415`
- **Finding:** When a worker-path image waits for its copy task, `upload` returns 0 bytes without `done`; `beginFrame` sees the resource still uploading and `break`s, so no other upload in the queue runs that frame, even a small image that would fit the budget. With a preload of many images one 4K copy delays all others.
- **Proposal:** `Kind::upload` reports "waiting" separately (`enum class UploadStatus { Progress, Waiting, Done }` instead of `bool& done`); `beginFrame` moves a waiting resource to the back of the queue and continues; it stops only when the budget is used or a full pass made no progress.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/resources.cpp` (or `resource_manager.cpp`), `resource_kind.h`, `image_resource.cpp`, `shader_resource.cpp`, `snapshot_resource.cpp`, `audio_tap.cpp` (the `upload` overrides)
- **Depends on:** NRES-03
- **Verification:** `HowTo Preload Images` with the F5 Resources page (upload jobs drain without one-per-frame steps); `ImageResourceTest` budget scenarios.

### NRES-11 Advance only the items whose resource changed
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/qtimageitem.cpp:255-264`, `:242-253`; `src/VL.Qt.Native/resources.cpp:792`, `:797`
- **Finding:** Any content change anywhere (one video frame) increments the global epoch, and then every live Image item calls `res::image::ready`, a mutex lock and table lookup. With a video playing this runs for every Image item every frame. No allocation, but O(items) work for one change.
- **Proposal:** The manager records the ids that got new Ready content in this frame (`std::vector<uint64_t>`, cleared at `beginFrame`, capacity kept) and exposes `std::span<const uint64_t> res::changedThisFrame()`. `QtImageItem` keeps `std::unordered_map<uint64_t, std::vector<QtImageItem*>>` of wanted ids (maintained in `setResource` and the destructor) and advances only those. Same for `ShaderEffectItem::afterResourceFrame`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/qtimageitem.h/.cpp`, `resources.h/.cpp`, `shadereffectitem.cpp`
- **Depends on:** none
- **Verification:** `ImageTest`, `ImageShareTest`; `HowTo Show a VL.Video player in Qt` with many Image elements.

### NRES-12 Align image_writer with the rest of QtNative
- **Kind:** api
- **Severity:** could
- **Breaking:** yes: C# callers of `vlqt_image_write` see -7 instead of -1 for bad arguments
- **Where:** `src/VL.Qt.Native/image_writer.cpp:18`, `:94`, `:14-147`; `src/VL.Qt.Native/image_writer.h:6-9`; `src/VL.Qt.Native/vlqt.h:375`; `src/VL.Qt.Native/image_resource.cpp:670`
- **Finding:** `vlqt_image_write` uses its own return codes (`-1` bad value, `-2` unsupported format, `-3` write failed), while the rest of the C API uses `-7` for a bad value and `-1` for an internal error (`vlqt.h:258`, `:271`). Its size check requires `stride * height` bytes, `set_pixels` requires `stride * (height - 1) + row` (`image_resource.cpp:670`): the same buffer can be valid for one and rejected by the other. The file is written in a different style from the rest of the layer (indented namespace bodies, Allman braces on control flow). 16-bit and float formats are reduced to 8 bit for PNG/TIFF without a warning, while `set_pixels` warns for the same reduction.
- **Proposal:** Codes `0` ok, `-7` bad argument, `-10` unsupported file format, `-11` write failed (or whatever free numbers the API review settles on), documented in `vlqt.h`; one shared size check in `pixel_format.h` (`bool holdsPixels(int len, int width, int height, int stride, VlPixelFormat)`, the `set_pixels` rule); layer style; the 8-bit reduction reported in `err` as a warning text with return 0, or 16-bit PNG written through `Format_RGBA64` (decide in the API review).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/image_writer.h/.cpp`, `vlqt.h`, `pixel_format.h/.cpp`, the C# FileWriter that calls `vlqt_image_write` (search in `src/VL.Qt`)
- **Depends on:** NRES-08
- **Verification:** `ImageWriteTest`; help patch of FileWriter.

### NRES-13 Snapshot: release a given-up grab's frame source at once; keep private Qt API in one place
- **Kind:** lifetime
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/snapshot_resource.cpp:122-127`, `:436-437`, `:98-104`, `:207-219`, `:226-235`, `:16-17`
- **Finding:** `failGrab` marks a grab not current but keeps it until Qt delivers or 60 s pass (`abandonMs`). Its hidden `QQuickShaderEffectSource` (live, parented to the content item) keeps rendering the Group into an offscreen texture every frame for up to a minute after the timeout, for a result nobody reads. Separately, two private Qt APIs are used: `QWindowPrivate::visible` (to grab in an offscreen scene) and `QQuickShaderEffectSource` (private header) for Group frames; both are explained in place, but a Qt update review has to find them by grepping `private/`.
- **Proposal:** In `failGrab` and when a newer grab supersedes one, `deleteLater` the frame source immediately (the grab then never completes and is dropped at `abandonMs`, as today for a destroyed item). Move the two private-API uses into `qt_private.h/.cpp` (`QSharedPointer<QQuickItemGrabResult> grabInRenderControlWindow(QQuickItem*, QSize)`, `QQuickItem* createFrameSource(QQuickItem* source, QRectF frame, QSize pixels, QQuickItem* parent)`) with a `static_assert`/`#if QT_VERSION` guard on the tested Qt version, if the other native reviews collect private uses the same way.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/snapshot_resource.cpp`, new `qt_private.h/.cpp` (or the shared file the native-architecture review proposes)
- **Depends on:** none
- **Verification:** the snapshot SmokeTest scenarios (search `res_snapshot` in `src/VL.Qt.SmokeTest`, `GroupFrameTest`); help patch `HowTo Take a Snapshot`.

### NRES-14 Move the design notes to docs/topics/resources.md and document restore semantics
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/resources.h:1-46`, `image_resource.h:1-26`, `snapshot_resource.h:1-17`, `qtimageitem.h:1-22`; `docs/images.md:23`; `src/VL.Qt.Native/image_resource.cpp:197-218`
- **Finding:** The contract of the layer (id scheme, state diagram, threads, refcounts, replacement, device loss, event record format) exists only as long header comments (K4), and a developer document for it is missing from `docs/`. One user-visible consequence is documented nowhere for users: after a GPU reset a file image is read again from disk (`restore`), so an image whose file was changed or deleted meanwhile changes or ends in Failed; `docs/images.md:23` says "the output comes back by itself".
- **Proposal:** New `docs/topics/resources.md` (listed in `docs/README.md`): id scheme, state diagram as a mermaid `stateDiagram`, who runs where (main thread, workers, render threads per NRES-01), reference counting, replacement and queued content, external content, device loss, event records (pointing to `protocol.h` for the byte layout), upload paths with the measured reasons in one sentence each. Each header keeps three to five lines and a link. Add one sentence to `docs/images.md` under device loss: "Images loaded from a file are read again from disk; a file changed meanwhile shows its new content."
- **Effort:** S, mechanical: no
- **Touches:** new `docs/topics/resources.md`, `docs/README.md`, `docs/images.md`, the four headers
- **Depends on:** NRES-01, NRES-03 (describe the final state)
- **Verification:** review by reading.

### NRES-15 Fill mode as an enum
- **Kind:** api
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/qtimageitem.h:49`, `:55-56`, `:84`; `src/VL.Qt.Native/qtimageitem.cpp:141-182`
- **Finding:** `fillMode` is an `int` with the values 0..3 named only in comments (`case 1: // PreserveAspectFit`); `VLQT_PROP_FILL_MODE` (`protocol.h:380`) is shared with Video. The `Q_PROPERTY`/`NOTIFY` pair is used by nothing in QML as far as this review saw.
- **Proposal:** `enum class FillMode : int { Stretch = 0, PreserveAspectFit = 1, PreserveAspectCrop = 2, Tile = 3 }` in `protocol.h` (mirrored in `Protocol.cs`), used in `layout()` and by the Video item; drop the `Q_PROPERTY` and signal if a search confirms no QML use.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/qtimageitem.h/.cpp`, `protocol.h`, `src/VL.Qt/Protocol.cs`, the Video item file
- **Depends on:** none
- **Verification:** `--drift`; `ImageTest` (fill modes); help patch `HowTo Draw an Image`.

## 4 Target structure

All in `src/VL.Qt.Native/` (flat, as the layer is today), namespace `vlqt::res` unless noted.

| File | Content | Change |
|---|---|---|
| `resource_kind.h` | `State`, `Content` (ex `Work`), `Info`, `Kind`, return codes, `idText`, `lcRes` | new, from `resources.h` |
| `resource_events.h/.cpp` | `EventQueue`: push, drop by id, poll | new, split from `resources.cpp` |
| `resource_workers.h/.cpp` | `WorkerPool`: threads, job/result queues, its own mutex | new, split |
| `resource_manager.h/.cpp` | `Manager`: slots, transitions, refcounts, replacement, external content, uploads, device listener; main thread only | split |
| `resource_registry.cpp` | `kindOf(int)` | split |
| `resources.h/.cpp` | C API backends, frame hooks, `overview()` (ex `snapshot()`) | slimmed |
| `resource_test_kind.cpp` | `TestKind`, `testStart` (K6, kept) | split; `testTransition`/`testElementRef` deleted |
| `pixel_format.h/.cpp` | `VlPixelFormat`, `bytesPerPixel`, `qimageFormat`, `holdsPixels` | new, merges two tables |
| `image_resource.h` | `ImageContent` (ex `ImageResource`): read view for elements | renamed, slimmed |
| `image_kind.cpp` | `ImageKind`: decode, convert, preview, restore; `load`, `setPixels`, `readPreview` | renamed from `image_resource.cpp` |
| `image_upload_d3d11.h/.cpp` | `D3D11Upload`: worker textures, staging, copy task, `ComPtr` | new, split |
| `image_upload_rhi.cpp` | stripes upload through the upload QRhi | new, split |
| `gpu_texture.h` | `GpuTexture` shared by content, pool and window wrappers (NRES-02) | new |
| `snapshot_resource.h/.cpp` | namespace `vlqt::res::snapshot` (ex `snap`), `SnapshotContent` | renamed |
| `image_texture_cache.h/.cpp` | `ImageTexture`, cache keyed by (window, QRhi, texture), `imageTextureFor` | new, split from `qtimageitem.cpp` |
| `qtimageitem.h/.cpp` | `QtImageItem` (namespace `vlqt`), `FillMode` | slimmed |
| `image_writer.h/.cpp` | `vlqt::imagewriter::write`, layer style, shared codes | aligned |
| `docs/topics/resources.md` | developer contract of the layer | new |

Deleted: `testWrite`, `testElementRef`, `testTransition`, probably `testRead` (NRES-09); duplicated `idText`/`steadyMs`/`PixelFormat`. Public (exported) surface unchanged except the test entry points and the `vlqt_image_write` codes.

## 5 Questions for Sebastian

**Q1 Texture reuse for live sources (NRES-02).** Today every new frame of a VideoSourceToQtImage or a live ToQtImage creates and destroys a GPU texture and one wrapper per window. (a) Keep it: simple, correct, cost unmeasured. (b) A pool of shared GPU textures (`GpuTexture`), recycled when no window draws them any more: removes the per-frame creation, adds about 150 lines and one more ownership object. (c) Update one texture in place: cheapest, but a window could show a half-updated frame and the "old image until the new one is complete" guarantee breaks. Recommendation: measure first with the video help patch; if the per-frame cost shows (more than about 1 ms of main thread per frame), do (b).

**Q2 Device loss for file images (NRES-14).** After a GPU reset a file image is read again from disk. (a) Keep and document (no extra memory; a changed or deleted file changes the picture). (b) Keep the decoded pixels of every file image in CPU memory: exact restore, but doubles the memory of all loaded images. (c) Keep the compressed file bytes: exact restore, memory = file size. Recommendation: (a); device loss is rare and (b)/(c) cost memory all the time.

**Q3 Upload path switch `VLQT_IMG_UPLOAD` (NRES-05).** It forces the stripes or worker path for A/B measurements. (a) Keep as a documented diagnostic variable in `docs/diagnostics.md`. (b) Remove it and keep the path choice fixed in code. Recommendation: (a) if the NRES-02 measurement is planned, else (b).

## 6 For other areas

- Effects/ShaderEffect review: `shadereffectitem.cpp:594`, `:677`, `:983` reset `imageTexture` on the main thread too; same render-thread rule as NRES-01, same per-frame walk as NRES-11 (`ShaderEffectItem::afterResourceFrame`, `shadereffectitem.cpp:577`).
- Device/offscreen review: the window state `_vlqt_shared_device` is a string-keyed dynamic property used as an API across `device.cpp:344`, `:532-536`, `offscreen.cpp:182`, `:394` (NRES-07 proposes `device::isOnSharedDevice`).
- Native C API review: the order of `vlqt_res_shutdown` (`vlqt.cpp:2219`) against window and device teardown was not checked; Image items destroyed after it log "unknown or stale resource id" for their element references (`qtimageitem.cpp:203-206` → `resources.cpp:697`), and `ImageKind::destroy` needs the upload QRhi alive.
- Shader and audio-tap kinds (`resources.cpp:54`, `:680-683`): `externalKind` names the audio tap explicitly; a kind property (`Kind::external()`) would remove the list in the manager.
- C# API review: `Native.cs:475-511` imports the test entry points (`vlqt_test_res_*`, `vlqt_test_write_image`); they go with NRES-09.
