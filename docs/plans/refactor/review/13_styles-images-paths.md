# Review 13 – Styles, images and paths

Claude Opus 5.5, 2026-11-19.

**Files read in full:** `src/VL.Qt/Styles/` (BaseStyle.cs, IStylable.cs, Style.cs, StyleMerge.cs, StyleNodes.cs, StyleWriter.cs), `src/VL.Qt/Images/` (ImageFileWriter.cs, ImageNodeReport.cs, QtImage.cs, ResourceClient.cs, ShaderSlot.cs, VideoFrameStream.cs), `src/VL.Qt/Paths/` (ArcGeometry.cs, BezierGeometry.cs, HandleMode.cs, PathAddMode.cs, PathDirection.cs, QtPath.cs, SplineGeometry.cs), `docs/styles.md` and `docs/images.md`.
**Skimmed:** `docs/shapes.md` (the Skia sections and the spline notes); excerpts from the callers: `Reconciler/Reconciler.cs:330-362`, `Nodes/LoadImageNode.cs`, `Nodes/LoadImageAsyncNode.cs`, `Nodes/ElementTooltipPreview.cs`, `Nodes/Paths/AddPathNode.cs`, `Nodes/Paths/ArcToNode.cs`; `protocol.h:160-182`; `Native.cs:415-530` (return codes); the test names and counts in `src/VL.Qt.Tests` and `ArcGeometryTests.cs:230-285`.
**Not read:** the QtNative resource manager (`resources.h/.cpp`), the path and style handling in QtNative, the node classes in `Nodes/Paths/` beyond the lines cited.

## 1 Summary

The three folders are in good shape. The maths of the path helpers checks out: SVG arc (F.6.5), tangent arc, the arc-to-cubic constant, non-uniform Catmull-Rom (Barry–Goldman), periodic B-spline (Böhm) and clamped B-spline (blossoms). The style model is one concept: an immutable value with one merge rule, one default, one wire encoding and one node set. The image resources have a clear owner (the node) and one release path (the queue, flushed by the pump). What matters most:

1. **Style setters allocate a new `Style` every frame** (static operation nodes), and the reconciler then deep-compares every styled widget every frame (SIP-01).
2. **`AddRoundRect` and `AddArc` differ from Skia** although the docs promise that Skia patches carry over: radii are limited per axis instead of scaled together (a "pill" becomes an ellipse), and a full `AddArc` ignores its start angle (SIP-02).
3. **`ResourceClient` runs user code inside its status parse loop.** A `Settled` handler or the `On Loaded` observable can re-enter `Poll` and overwrite the buffer being parsed (SIP-03). Two error paths fail a resource without settling it (SIP-04).
4. **`ResourceClient.cs` holds four types and five copies of the same create and start pattern.** `ImageResource` is in fact the generic resource of shaders, snapshots and audio taps. Rename it and split the file (SIP-05, SIP-06).
5. **Adding a style field means editing six places.** Keep each part's fields, merge and writer together (SIP-08).

## 2 What works and must survive

- **`Style` is immutable and only holds what is set** (`Style.cs:37-149`). `StateValues<T>.Merge` is the one merge rule, and the reconciler, `Merge (Style)` and `IStylable` all use it. Unset means "inherit"; reading falls back to Normal, as QtNative does.
- **`StyleMerge.Merge` returns one of its inputs when the other adds nothing** (`StyleMerge.cs:15-32`), and the reconciler keeps the old resolved reference when the result is equal (`Reconciler.cs:349-351`). Unchanged children are skipped by reference. Keep both.
- **`StyleWriter` sends a reduced diff** (`StyleWriter.cs:7-18`): no allocation, a full send with `Clear` only when an entry disappears. `ReferenceEquals(resolved, lastSent)` returns early, so it is the per-frame hot path for every widget.
- **`BaseStyle` is the only place with default values.** QtNative has no defaults of its own. Keep it that way.
- **Releases go through a queue** (`ResourceClient.cs:484-546`). An element that switches from one image to another acquires the new id before the old one is released in the next pump. `Release` may be called from any thread. There are no finalizers and no native callbacks.
- **`vlqt_res_shutdown` runs only when nothing is referenced** (`ResourceClient.cs:552-570`), so a clean end logs no leak and the next load restarts the workers.
- **Resource ids carry a generation.** A stale id (an old `QtImage`, a released shader) is ignored by QtNative and never crashes. Device loss is handled in QtNative: the C# ids stay valid (docs/images.md, "Device loss").
- **Hot paths that allocate nothing per frame:** `ResourceClient.AfterEvents/Poll` (reused `_buffer`), `BeforeEvents/FlushReleases` (reused `_flushing`), `ShaderSlot.Update` and `ShaderSlot.Error` (cached text), `ImageNodeReport.Update`, `QtPath.Flatten` (computed once, published with `CompareExchange`), `QtPath.ContentEquals` (bitwise, NaN-safe), and the style getters (struct merges only). Keep these properties.
- **`QtPath` is a persistent list** (O(1) per builder node), flattened iteratively, so long chains cannot overflow the stack (`QtPath.cs:170-237`).
- **`VideoFrameStream` starts, grabs from and disposes the player on one dedicated thread** (player render contexts are thread affine). If the join times out, the thread disposes its own frames (`VideoFrameStream.cs:173-209`).
- **`ImageFileWriter`: at most one write runs and one waits, and the newest wins.** Volatile images are copied before `Start` returns.
- **`IResourceApi` is a real seam** (native vs. fake), not a test hook. The 41 tests in `ResourceClientTests.cs` depend on it.

## 3 Findings

### SIP-01 Style setters: return the same `Style` while their inputs are unchanged
- **Kind:** performance
- **Severity:** should
- **Breaking:** no (node names and pins stay; the setters become process nodes)
- **Where:** `src/VL.Qt/Styles/StyleNodes.cs:25-39`, `:97-139`, `:238-268`, `:324-354`, `:409-438`, the single setters `:160-191`, `:278-293`, `:364-379`, `:447-462`, `StyleNodes.Merge :63-64`; `src/VL.Qt/Reconciler/Reconciler.cs:345-351`
- **Finding:** Every setter is a static method, so vvvv evaluates it every frame. Each call with a connected pin allocates a new `Style` and a new part record (`style with { Frame = f with {...} }`, `:125-138`). A chain of three setters on a widget therefore allocates six objects per frame. The output is a new reference every frame, so the reconciler always takes the merge branch (`!ReferenceEquals(next.Style, node.Last?.Style)`, `Reconciler.cs:348`) and runs a deep `merged.Equals(resolved)` for that widget every frame. Nothing reaches the wire (StyleWriter sees the old reference), but this breaks the AGENTS.md rule "change detection instead of rebuilding every frame; allocate nothing per frame in hot paths". `Merge (Style)` with two fresh inputs allocates as well.
- **Proposal:** Make the five main setters and `Merge` process nodes that cache their output: `ColorsNode`, `FrameNode`, `TextNode`, `IndicatorNode`, `TrackNode`, `MergeNode` (`[ProcessNode(Name = "Frame")]` etc., same categories and pins). Each one remembers its last input reference, its `Optional` values (compared by value; `Optional<T>` is `IEquatable`) and `state`, and returns the cached `Style` while nothing changed. The internal `Apply` methods stay as the shared implementation. For the single Advanced setters, either the same treatment (generated or written out) or remove them, see Q1. Getters stay static: they allocate nothing.
- **Decided:** C6 → process nodes with a cached output for the six bundles only (Colors, Frame, Text, Indicator, Track, Merge); the single Advanced setters and getters stay static nodes and are not removed (C5 keeps them); C5 (b) → the bundle setters are named by the vvvv convention, so the process nodes are `SetColors`, `SetFrame`, `SetText`, `SetIndicator`, `SetTrack` (getters `Colors`, `Frame`, `Text`, `Indicator`, `Track`; the setter pin `State` becomes `States`); the help patches follow through the mapping file and the script of P19/P20, not by hand; C13 of [DECISION-ROUND.md](../DECISION-ROUND.md) → names as C5 (`SetColors` …, getters `Colors` …); the process nodes are `SetColorsNode`, `SetFrameNode`, `SetTextNode`, `SetIndicatorNode`, `SetTrackNode`, `MergeNode` in `Nodes/Style/`; the static single setters and getters stay in `Styles/`
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Styles/StyleNodes.cs` (and the split files of SIP-08/K3), `src/VL.Qt.Tests/StyleNodesTests.cs`, help patches `HowTo Style a Widget.vl`, `HowTo Style Control Parts.vl`, `HowTo Style Widgets with a Group.vl`, `HowTo Patch a Stylable Widget.vl` (node references change from operation to process node)
- **Depends on:** Q1 (naming), SIP-08
- **Verification:** a new unit test: two `Update`s with equal inputs return the same reference; `ReconcilerStyleTests` unchanged; the four style help patches compile (`tools/vvvvc.ps1 -File`).

### SIP-02 `AddRoundRect` and `AddArc`: follow Skia's rules
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: a round rect whose radii exceed half its size, a "pill" for example, draws circular ends instead of elliptical ones; a full `AddArc` starts at its start angle
- **Where:** `src/VL.Qt/Paths/ArcGeometry.cs:68-103` (line 76), `:38-45` (lines 42-43); `src/VL.Qt.Tests/Paths/ArcGeometryTests.cs:272-279`; `docs/shapes.md` ("Skia correspondence")
- **Finding:** docs/shapes.md says the `Qt.Paths` nodes have Skia's names and pins "so Skia patches carry over", and lists the differences. There are three more:
  1. **Radii.** Skia (`SkRRect::setRectXY`) scales *both* radii by one factor `min(w / 2rx, h / 2ry)` when they do not fit. `AddRoundRect` limits each one on its own (`erx = min(|rx|, w/2), ery = min(|ry|, h/2)`). A 100 × 20 rect with radius 30, 30 gives circular corners of 10 in Skia and elliptical corners of 30 × 10 here. The test `AddRoundRect_LimitsRadiiAndAllowsZero` fixes the deviation (its comment even says "stadium/ellipse"). Skia also draws a plain rect for a negative radius; here `Math.Abs` turns it positive.
  2. **Full `AddArc`.** For `|sweep| ≥ 1` the code always adds the oval starting at the right middle. Skia's `addArc` starts the oval at the start angle when that is a multiple of 90°; otherwise it draws the full arc from the start angle (`arcTo` with `forceMoveTo`). The start point matters for dashes, trim and caps.
  3. **Start point of `AddRoundRect` (suspicion).** As far as I remember, Skia's `addRRect(rrect, dir)` starts at index 6 (CW) or 7 (CCW) of its point iterator, on the left edge, not "after the top left corner" (`:64-66`). To confirm, compare the first point of `SKPath.AddRoundRect` in SkiaSharp with ours.
- **Proposal:** In `AddRoundRect`, scale both radii with Skia's factor and use `rx <= 0 || ry <= 0 → plain rect` without `Math.Abs`. In `AddArc`, pass the start angle to a new `AddOval(path, oval, clockwise, startAngle)` that emits from `t0` and snaps the end to the start. For other start angles use `ArcTo(..., forceMoveTo: true)` with the clamped sweep. Settle point 3 with the SkiaSharp check, then either match Skia or list the difference in docs/shapes.md. Adapt the test to Skia's numbers.
- **Decided:** C7 → (a) follow Skia: both radii of `AddRoundRect` are scaled by one factor, a pill gets semicircular ends, the existing test is rewritten to Skia's numbers; catalog: AddRoundRect `Radius` default `0.1, 0.1` and `Rectangle` default `0, 0, 1, 1`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Paths/ArcGeometry.cs`, `src/VL.Qt.Tests/Paths/ArcGeometryTests.cs`, `docs/shapes.md`
- **Depends on:** Q3
- **Verification:** `ArcGeometryTests` with Skia reference values (a 10 × 4 rect with radius 100 gives radii 2, 2); `HowTo Build a Path` and `HowTo Draw a Path` look unchanged except for the pill case.

### SIP-03 `ResourceClient.Poll`: no user code inside the parse loop
- **Kind:** threading
- **Severity:** should
- **Breaking:** no (`Settled` still fires in the same pump)
- **Where:** `src/VL.Qt/Images/ResourceClient.cs:573-600`, `:602-627`, `:629-640`, `:414-422`; `src/VL.Qt/Nodes/LoadImageAsyncNode.cs:96`, `:133-141`; `src/VL.Qt/Images/QtImage.cs:131-137`
- **Finding:** `Poll` parses the records in place in `_buffer` (`span` at line 583) and calls `Settle` for each finished resource while it parses. `Settle` runs the `Settled` delegate and completes the `TaskCompletionSource`, which has no `RunContinuationsAsynchronously`. For `LoadImage (Async)` the delegate calls `_onLoaded.OnNext(image)`, the `On Loaded` observable, so patch code subscribed to it runs inside the parse loop. If that code creates a `LoadImage` (synchronous), it calls `Wait` → `Poll` again. The inner call writes into the same `_buffer` while the outer loop still reads from it, and the remaining records of the outer batch are parsed from overwritten bytes. Their STATE records are lost, so those resources stay `Loading`. I did not reproduce this; a unit test with the fake `IResourceApi` can.
- **Proposal:** Two steps in `Poll`. (1) Apply all records (`OnState`, `OnInfo`, …) and collect the resources to settle into a reused `List<ImageResource>` field. (2) After the loop, call `Settle` for each one. Add a `_polling` guard: a re-entrant `Poll` (from `Wait`) only applies records, and the settle list is drained by the outermost call. Remove the `TaskCompletionSource` (test only, see SIP-07).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Images/ResourceClient.cs`, `src/VL.Qt/Images/QtImage.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`
- **Depends on:** none
- **Verification:** a new test in `ResourceClientTests`: a `Settled` handler that calls `Wait` on another resource while the fake queue holds three records; every resource ends Ready. SmokeTest `QtImageTest`.

### SIP-04 Every failed start settles the resource
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Images/ResourceClient.cs:192-196` (LoadShader, invalid path), `:393-397` (Load, invalid path); compare `:433-437` (SetPixels, unsupported format: settles)
- **Finding:** An invalid path calls `resource.Fail(...)` and returns false without `Settle`. All other refusals (`:203`, `:223`, `:275`, `:294`, `:404`, `:436`, `:453`) do settle. The effect: `Settled` is not called, `Version` is not incremented (used by `QtImageTooltipPreview.cs:46` to detect new content), and `WhenSettled` never completes (it checks `State != Loading`, so only callers that subscribed earlier hang). The nodes cope today because they read `State` themselves, but the rule "a load ends with Settle" is broken in exactly two places.
- **Proposal:** One private helper used by all starts (see SIP-05): `bool Refuse(ImageResource r, string error) { r.Fail(error); Settle(r); return false; }`, called on the invalid-path branch as well.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Images/ResourceClient.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`
- **Depends on:** none (fits SIP-05)
- **Verification:** a test: `Load` with an invalid path (`"a\0b"`) raises `Settled` once and increments `Version`.

### SIP-05 Split `ResourceClient.cs` and remove its five copies of one pattern
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Images/ResourceClient.cs:12-29` (IResourceApi), `:31-48` (NativeResourceApi), `:130-152`, `:234-259` (four `Create*`), `:179-298`, `:384-457` (five starts), `:304-359` (two pixel reads), `:310-315`, `:344-350`, `:362-378` (SnapshotImage), `:158-171` (TryGetBuiltinShader), `:463-478` (ToQtNativeFormat, also used by `ImageFileWriter.cs:137`)
- **Finding:** At 602 lines the file is hard to hold in your head, yet most of it is repetition:
  - `CreateImage`, `CreateShader` and `CreateSnapshot` differ only in the kind constant.
  - `Load`, `LoadShader`, `LoadShaderSource`, `Snapshot`, `SnapshotFit` and `SetPixels` repeat the same steps: released? → `OnLoadStarted` → native call → `Fail` + `Settle` on refusal. Two of them also repeat the `GetFullPath` try/catch.
  - `ReadSnapshot` and `ReadPreview` decode the same return convention differently. `ReadSnapshot` treats every negative code except −7 and −9 as "−required", so −1 (internal error) leads to `new byte[1]` and a second native call. `ReadPreview` uses `rc % 4 == 0`.
  - The codes −1, −7, −9 appear as literals; their meaning is only in comments in `Native.cs:415`.
  - Shader path parsing (`TryGetBuiltinShader`) and the pixel format table (`ToQtNativeFormat`) are not resource management. The format table is also used by `ImageFileWriter`.
- **Proposal:**
  - `Images/IResourceApi.cs`: `IResourceApi` and `NativeResourceApi`.
  - Named return codes in `Native.cs` (next to the P/Invokes): `internal static class ResResult { public const int Ok = 0, InternalError = -1, BadState = -7, StaleId = -9; }`.
  - In `ResourceClient`: `ImageResource Create(int kind)` (private) behind the public `CreateImage/CreateShader/CreateSnapshot/CreateAudioTap`; `bool Start(ImageResource r, int rc, string what)` (refuse = Fail + Settle, see SIP-04); `bool TryGetFullPath(ImageResource r, string path, out string fullPath)`; and one `IImage? ReadPixels(ulong id, int estimatedBytes, ReadFn read, string what)` with one rule: `rc < 0 && -rc > buffer.Length` means "−required".
  - `Shaders/` (review 12): `TryGetBuiltinShader` and `builtinShaderPrefix` move to `ShaderSlot`.
  - `Images/PixelFormats.cs`: `internal static class PixelFormats { int ToQtNative(PixelFormat) }`.
  - `SnapshotImage` merges into `ByteArrayImage` (SIP-13).
- **Decided:** TARGET 2.2 → `IResourceApi.cs`, `PixelFormats.cs`, `QtResource.cs` and `ByteArrayImage.cs` sit in `Images/` (namespace `VL.Qt.Images`), `ShaderSlot.cs` with `TryGetBuiltinShader` goes to `Shaders/`; P4/CAPI-07 → `Native.cs` is cut into `Interop/Native.*.cs` after the native break, so the named codes (`ResResult`) are written against the return codes as the break leaves them (CAPI-02, B4: per-thread last error) and sit next to the resource P/Invokes in `Interop/Native.Resources.cs`; the calls are `vlqt_res_*` (A5)
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Images/ResourceClient.cs`, new `src/VL.Qt/Images/IResourceApi.cs`, `src/VL.Qt/Images/PixelFormats.cs`, `src/VL.Qt/Native.cs`, `src/VL.Qt/Images/ImageFileWriter.cs`, `src/VL.Qt/Images/ShaderSlot.cs` (or its new place), `src/VL.Qt.Tests/ResourceClientTests.cs`, `src/VL.Qt.Tests/ShaderEffectTests.cs` (if it calls `TryGetBuiltinShader`)
- **Depends on:** SIP-04, SIP-06, SIP-13
- **Verification:** `ResourceClientTests` (41), SmokeTests `QtImageTest`, `SnapshotTest`, `TooltipTest`; help `HowTo Take a Snapshot`.

### SIP-06 `ImageResource` → `QtResource`: name what it is
- **Kind:** naming
- **Severity:** should
- **Breaking:** no (internal)
- **Where:** `src/VL.Qt/Images/QtImage.cs:49-147`; `ResourceClient.cs:140-143` ("ImageResource is the generic resource view … also for shaders"); `ShaderSlot.cs:18`, `:35`; users in `Nodes/` (`LoadImageNode`, `LoadImageAsyncNode`, `ToQtImageNode`, `VideoSourceToQtImageNode`, `SnapshotNode`, `ElementTooltipPreview`, `QtImageTooltipPreview`) and the Video audio tap
- **Finding:** The type holds an image, a shader, a snapshot or an audio tap, and its doc has to explain that. It also lives in `QtImage.cs`. Its `Settled` is a settable delegate property, so a second subscriber silently replaces the first. Each of the four users sets it once, but nothing says that is the contract.
- **Proposal:** Rename it to `QtResource` (internal, file `Images/QtResource.cs`, or `Resources/QtResource.cs` per section 4). Keep `QtImageState` as its state type: it is public on `QtImage`, and a shader uses the same three states. Document `Settled` as "the one callback of the owner, set once after create", or make it a constructor argument of `Create(kind, Action<QtResource>? settled)`.
- **Decided:** A5/TARGET 1.2 → `ImageResource` becomes `QtResource` in `Images/QtResource.cs` (namespace `VL.Qt.Images`, not `Resources/`); `QtImageState` stays public beside `QtImage`; its users move with S3 (`Nodes/Images/…`, `Nodes/Editor/…TooltipPreviewNode.cs`)
- **Effort:** M, mechanical: yes (rename)
- **Touches:** `src/VL.Qt/Images/QtImage.cs`, `ResourceClient.cs`, `ShaderSlot.cs`, `src/VL.Qt/Nodes/*Image*.cs`, `Nodes/SnapshotNode.cs`, `Nodes/ElementTooltipPreview.cs`, `Nodes/QtImageTooltipPreview.cs`, the audio tap user in `Nodes/` (Video), `src/VL.Qt.SmokeTest/TooltipTest.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`, `QtImageTooltipPreviewTests.cs`
- **Depends on:** none
- **Verification:** build, unit tests, SmokeTest.

### SIP-07 Test hooks of the area (K6), and how the tests work without them
- **Kind:** dead-code
- **Severity:** should
- **Breaking:** no
- **Where:**
  - `QtImage.cs:56`, `:81-88`, `:134-136`, `:141-146` (`WhenSettled` and its `TaskCompletionSource`; only `ResourceClientTests.cs` calls it)
  - `ResourceClient.cs:116-127` (`LiveCount`, `PendingReleases`: SmokeTest and tests)
  - `ResourceClient.cs:86` (`previews` constructor parameter: tests and SmokeTest)
  - `ImageFileWriter.cs:17-18`, `:96-97` (`_idle`, `WaitIdle`)
  - `BaseStyle.cs:71-72` (`BaseStyle.Write`: only SmokeTest `ButtonTest`, `KeyRouteTest`, `MeasureTest`, `MsaaTest`)
  - `ShaderSlot.cs:34-35` (`Resource`, "tests, status")
- **Finding:** These members exist so that tests can observe internals. `WhenSettled` also makes `Settle` run inline continuations (SIP-03).
- **Proposal:**
  - Remove `WhenSettled`, `_settled` and `CancelSettled`; tests use `Settled`, as most of them already do.
  - Unit tests count create and release calls on the fake `IResourceApi`. The SmokeTest reads the native truth through `IResourceApi.List`. Then remove `LiveCount` and `PendingReleases`.
  - Tests set `Previews` (internal setter) instead of the constructor parameter.
  - `ImageFileWriterTests` poll `Poll(out _)` with a deadline, or pass a write delegate that signals a semaphore after the result is stored. Remove `_idle` and `WaitIdle`.
  - SmokeTests call `StyleWriter.Write(sink, id, BaseStyle.Default, null)`. Remove `BaseStyle.Write`.
  - Check `ShaderSlot.Resource` for non-test users (status); keep it only if one exists.
- **Decided:** P22 → SIP-07 is one of the K6 findings and is worked under TEST-07; F5 → the SmokeTest edits of the Where list are edits of the NUnit tests
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Images/QtImage.cs`, `ResourceClient.cs`, `ImageFileWriter.cs`, `ShaderSlot.cs`, `src/VL.Qt/Styles/BaseStyle.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`, `ImageFileWriterTests.cs`, `FileWriterNodeTests.cs`, `QtImageTooltipPreviewTests.cs`, `src/VL.Qt.SmokeTest/{QtImageTest,SnapshotTest,GroupFrameTest,ButtonTest,KeyRouteTest,MeasureTest,MsaaTest,Program}.cs`
- **Depends on:** SIP-03
- **Verification:** unit tests and SmokeTest `--all` pass unchanged in content.

### SIP-08 One style field, one place: part records carry their merge and their writer
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Styles/Style.cs:88-126` (part records), `StyleMerge.cs:35-133` (four hand-written merges), `StyleWriter.cs:49-82` (field list with protocol ids), `BaseStyle.cs:18-63`, `StyleNodes.cs` (`Apply` and getters per part)
- **Finding:** A new field (say `Frame.Shadow`) means editing six places in five files, plus `protocol.h`, `Protocol.cs` and QtNative. Nothing points from one place to the next. The merge methods compare all fields twice in one long line (`StyleMerge.cs:49`, `:51`) to avoid an allocation. That is correct but easy to break when a field is added: a forgotten field in the second comparison returns `o` and silently drops a value of `u`.
- **Proposal:** One file per part, each owning its fields, merge and wire mapping: `Styles/FrameStyle.cs` with `FrameStyle` plus `static FrameStyle? Merge(FrameStyle?, FrameStyle?)` and `static void Visit(ICommandSink?, FrameStyle?, FrameStyle?, StyleWriter.Pass)`; likewise `TextStyle.cs`, `IndicatorStyle.cs` and `TrackStyle.cs`. `StyleMerge` and `StyleWriter` then only call the four parts. In the merges, replace the two long comparisons with a helper that counts per field whether the result came from `u` or `o` (`Pick(ref fromU, ref fromO, …)`), so a missing field shows. Add a section "Adding a style field" to `docs/styles.md` (developer part): the list of places (part file, BaseStyle, StyleNodes setter and getter, protocol.h, Protocol.cs, QtNative style table).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Styles/Style.cs`, `StyleMerge.cs`, `StyleWriter.cs`, new `FrameStyle.cs`, `TextStyle.cs`, `IndicatorStyle.cs`, `TrackStyle.cs`, `docs/styles.md`
- **Depends on:** none (do it before SIP-01)
- **Verification:** `StyleTests`, `ReconcilerStyleTests`, `BaseStyleTests`; a new test that sets each field alone on `over` and checks that `Merge` keeps it (catches a forgotten field).

### SIP-09 `BaseStyle.AppliesTo`: an explicit list instead of a protocol range
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Styles/BaseStyle.cs:68-69`; `src/VL.Qt/Protocol.cs:135` (`Button = 10`), `:145` (`TextField = 15`); caller `Reconciler.cs:337`
- **Finding:** "Is this a widget that takes a style?" is answered by `type is >= Button and <= TextField`. That holds only as long as every number between 10 and 15 is a widget and every widget is in that range. A new widget type (a ComboBox gets the next free number) is silently unstyled. The function also has no reason to live with the default values.
- **Proposal:** Move it to `Protocol.cs` next to the type constants as `Protocol.Type.TakesStyle(ushort type) => type is Button or CheckBox or … or TextField`, with the members named explicitly, or make it a property of the element (`Element.TakesStyle`, decided by the element review). The reconciler calls it there. `BaseStyle` keeps only the values.
- **Decided:** TARGET 2.2 → `Protocol.cs` lives in `Wire/` (namespace `VL.Qt.Wire`), so the constant range and `TakesStyle` are edited there; C12 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the list of stylable element types lives in `Reconciliation/` next to the style writer, by element class; neither in `Protocol.cs` nor as `Element.TakesStyle` (A3)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Styles/BaseStyle.cs`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt.Tests/BaseStyleTests.cs`
- **Depends on:** none
- **Verification:** `BaseStyleTests` (each widget type → true, Rectangle/Text/Group → false), `ReconcilerStyleTests`.

### SIP-10 Style docs: help texts a user can read, the IStylable contract in the interface
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Styles/StyleNodes.cs:160-224`, `:278-310`, `:364-396`, `:447-479`; `StyleNodes.cs:41-58`, `:141-158`, `:270-276`, `:356-362`, `:440-445`; `src/VL.Qt/Styles/IStylable.cs:6-21`; `docs/styles.md` ("Style nodes" table, row `Colors`); `StyleMerge.cs:6-7`
- **Finding:**
  - The 40 single setters and getters say "Sets borderColor for the given states." and "Resolved paddingX in one state". These are C# parameter names, the node help in vvvv shows them as they are, and neither text says which part is meant.
  - No setter or getter documents `input` and `state` with `<param>`. The split getters (`GetColors`, `GetFrame`, `GetText`, `GetIndicator`, `GetTrack`) have no `<param>` for their `out` pins, which AGENTS.md requires.
  - The docs table says `Colors` sets "frame and text color". It sets six colors, including indicator, track and fill (`StyleNodes.cs:25-39`).
  - `IStylable`: the rule "the output pin of Build must be named Result" is only in `docs/styles.md`; the interface does not say it.
  - `StyleMerge`'s summary says "later by Merge (Style) and IStylable", but both exist.
- **Proposal:**
  - Write the summaries in words: "Sets the border color of the frame for the given states." and "Returns the border color of the frame in one state; unset fields come from the base style."
  - Add `<param name="input">`, `<param name="state">` and every `out` pin.
  - Correct the docs table.
  - Move the `Result` rule into the `IStylable` summary and keep one sentence in docs/styles.md that links to it.
  - Delete "later" in `StyleMerge`.
- **Decided:** C5 (b) → write the help texts once, for the final names: setters `SetRadius`, `SetFontSize` … and the bundles `SetColors`, `SetFrame` …, getters `Radius`, `Colors`, `Frame` …, setter pin `State` → `States`; the docs table row `Colors` becomes `SetColors`; P5 → the `IStylable` summary also says that `Build` returns `IElement?` and that a returned stylable is resolved in turn (the reconciler guards against a widget that returns itself); C13 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the six process nodes live in `Nodes/Style/` as `SetColorsNode` … `MergeNode`; the static single setters and getters stay in `Styles/`
- **Effort:** S, mechanical: partly
- **Touches:** `src/VL.Qt/Styles/StyleNodes.cs` (or its split files), `IStylable.cs`, `StyleMerge.cs`, `docs/styles.md`
- **Depends on:** Q1 (if nodes are renamed, write the docs once)
- **Verification:** read the node help in vvvv on `Radius [Qt.Style.Frame]` and `GetColors`; `tools/vl-patch-check.py` (unchanged pins).

### SIP-11 `SvgArcTo` and `TangentArcTo` read the current point from the path
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Paths/ArcGeometry.cs:11-12`, `:111`, `:163`; `src/VL.Qt/Paths/QtPath.cs:134`; `src/VL.Qt/Nodes/Paths/ArcToNode.cs:43`, `:127`, `:171-172`
- **Finding:** The class summary says "the current point of a path is not stored, so the methods that start at it take it as a parameter". That stopped being true when `QtPath.LastPoint` arrived: every caller now passes `input.LastPoint`. The parameter lets a caller pass a point that is not the current point, and the comment misleads.
- **Proposal:** Drop the `current` parameter, use `path.LastPoint` inside, and delete the sentence from the summary. `ArcToNode` (SVG relative) still computes `last + destinationPoint` itself.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Paths/ArcGeometry.cs`, `src/VL.Qt/Nodes/Paths/ArcToNode.cs`, `src/VL.Qt.Tests/Paths/ArcGeometryTests.cs`
- **Depends on:** none
- **Verification:** `ArcGeometryTests`, `BuilderNodeTests`.

### SIP-12 `QtPath` join node: contour start of an appended path without Move
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Paths/QtPath.cs:105-116`, `:76-102`; `src/VL.Qt/Nodes/Paths/AddPathNode.cs:47-50`
- **Finding:** A join takes `_startX/_startY` (and so the point after a later `Close`) from the appended path alone. If the appended path does not start with a Move (built from `Empty` with `LineTo`), its own start is (0, 0). `AddPath (Extend)` then continues the *previous* contour (`AddPathNode.cs:50`), so the contour start is the previous one's. After a `Close` in the appended part, `LastPoint` says (0, 0) while QtNative closes to the earlier start, and a following relative node (`LineTo (Relative)`) is placed wrongly. This is rare: it needs a Move-less path, `Extend` and a `Close`.
- **Proposal:** Store in each node whether its chain has seen a Move (`_hasMove`). In the join constructor, take `_startX/_startY` from `previous` when `!appended._hasMove`, and compute `_lastX/_lastY` for a trailing Close from that start. Alternatively, `LineTo/QuadTo/CubicTo` on an empty path insert `MoveTo(0, 0)` as Skia does (`injectMoveToIfNeeded`). That is simpler and gives Skia parity, but it changes `Flatten` for Move-less paths: say so in docs/shapes.md.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Paths/QtPath.cs`, `src/VL.Qt.Tests/QtPathTests.cs`, `src/VL.Qt.Tests/Paths/AddNodeTests.cs`
- **Depends on:** none
- **Verification:** a new test: `Empty.LineTo(5,0).LineTo(5,5).Close()` appended with Extend to a path that starts at (10,10): `LastPoint == (10,10)`.

### SIP-13 One byte-array `IImage` instead of two
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Images/ResourceClient.cs:362-378` (`SnapshotImage`), `src/VL.Qt/Images/ImageFileWriter.cs:163-186` (`CopiedImage`)
- **Finding:** Both are the same thing: an `IImage` and `IImageData` that owns a `byte[]`, is not volatile and has a no-op `Dispose`. They differ only in where the scan size comes from.
- **Proposal:** `Images/ByteArrayImage.cs`: `internal sealed class ByteArrayImage : IImage, IImageData` with `ByteArrayImage(ImageInfo info, int scanSize, byte[] bytes)` and `static ByteArrayImage CopyOf(IImage source)`. Both callers use it.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Images/ResourceClient.cs`, `ImageFileWriter.cs`, new `ByteArrayImage.cs`
- **Depends on:** none
- **Verification:** `ImageFileWriterTests`, SmokeTest `SnapshotTest`.

### SIP-14 `QtImage` says "Ready" after its node released it
- **Kind:** api
- **Severity:** could
- **Breaking:** yes: a `QtImage` held after its loader changed path reports `Failed` with an error instead of `Ready`
- **Where:** `src/VL.Qt/Images/QtImage.cs:17-47`, `:72-73`
- **Finding:** The summary says "pass the image around freely, it needs no disposing". When the node loads another file, though, the old `QtImage` (kept by an S+H, a spread, a channel) keeps `State = Ready`, `Width` and `Height`, and an Image element given it draws nothing, because QtNative ignores the stale id. The patch gets no hint why.
- **Proposal:** `QtImage.State` returns `Failed` and `Error` returns "Released by its node (it loaded another image or was deleted)" once `_resource.IsReleased`. Say in the summary that an image is valid while its node holds it.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Images/QtImage.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`
- **Depends on:** SIP-06
- **Verification:** a unit test: release → `State == Failed`; help `HowTo Preload Images` unchanged.

### SIP-15 Spline helpers: names and input ranges
- **Kind:** naming
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Paths/SplineGeometry.cs:108-110` (`Third`), `:31` (tension), `:28-30` (alpha NaN → 0.5), `:17-18` (summary line over 160 characters); `BezierGeometry.cs:22-25` vs `SplineGeometry.cs:22`, `:79`
- **Finding:**
  - `Third(p0, p1, p2)` returns `(p0 + 4 p1 + p2) / 6`, the point of the B-spline at the knot of `p1`; the name says nothing of that.
  - `tension` is not clamped. Above 1 the tangents flip (loops), and NaN gives NaN points, although the doc says "1 gives straight lines". A NaN alpha silently becomes 0.5.
  - `BezierGeometry` takes `IReadOnlyList<Vector2>` and `SplineGeometry` takes `ReadOnlySpan<Vector2>` for the same kind of input.
- **Proposal:** Rename `Third` → `PointAtKnot`. Clamp `tension` to 0..1 (doc: "clamped to 0..1") and document the NaN rule, or treat NaN like the default. Give `BezierGeometry.Build` `ReadOnlySpan<Vector2>` parameters (`HandleMode` as `ReadOnlySpan<HandleMode>`), as `SplineGeometry` has; the node passes `spread.AsSpan()`.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Paths/SplineGeometry.cs`, `BezierGeometry.cs`, `src/VL.Qt/Nodes/Paths/BezierHandlesNode.cs`, `src/VL.Qt.Tests/Paths/SplineGeometryTests.cs`, `BezierGeometryTests.cs`
- **Depends on:** none
- **Verification:** `SplineGeometryTests`, `BezierGeometryTests`, `HowTo Draw Splines`.

### SIP-16 `Style` shows its content in a tooltip
- **Kind:** api
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Styles/Style.cs:134-149`
- **Finding:** `Style` is a record whose parts are internal. The generated `ToString` prints only public members, so a link tooltip or IOBox in vvvv shows `Style { }` for every style. A user cannot see what a chain has set.
- **Proposal:** Override `PrintMembers` (or `ToString`) to list the set entries compactly, e.g. `Frame.Color[Normal,Hovered], Text.FontSize[All]`; with nothing set, write `Style (empty)`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Styles/Style.cs`, `src/VL.Qt.Tests/StyleTests.cs`
- **Depends on:** SIP-08
- **Verification:** a unit test on the text; tooltip in vvvv (manual-check).

## 4 Target structure

```
src/VL.Qt/Styles/                    namespace VL.Qt.Styles
  Style.cs              Style (public record, value of the Style pins)          [Style.cs split, K3]
  StyleState.cs         StyleState, StyleStates (public enums)                  [split from Style.cs]
  StateValues.cs        StateValues<T>: one field in five states, merge rule    [split from Style.cs]
  FrameStyle.cs         FrameStyle + its Merge and Visit (SIP-08)              [split from Style/StyleMerge/StyleWriter]
  TextStyle.cs          TextStyle + Merge + Visit                               [same]
  IndicatorStyle.cs     IndicatorStyle + Merge + Visit                          [same]
  TrackStyle.cs         TrackStyle + Merge + Visit                              [same]
  StyleMerge.cs         Merge(Style, Style): calls the four parts
  StyleWriter.cs        Write(sink, id, resolved, lastSent): diff + reduced form, calls the parts
  BaseStyle.cs          the default values only (AppliesTo → Protocol, Write → removed; SIP-09, SIP-07)
  IStylable.cs          interface for self-made widgets (contract incl. pin name Result)
  StyleNodes.cs         Colors, GetColors, Merge, ResolveState (+ shared Set/Resolve)   [split of StyleNodes.cs, K3]
  FrameNodes.cs         Frame + getters + Advanced single setters/getters           [split]
  TextNodes.cs          Text …                                                      [split]
  IndicatorNodes.cs     Indicator …                                                 [split]
  TrackNodes.cs         Track …                                                     [split]
  (if SIP-01 with process nodes: ColorsNode.cs, FrameNode.cs, TextNode.cs, IndicatorNode.cs,
   TrackNode.cs, MergeNode.cs as [ProcessNode]s; the static classes keep getters and Apply)

src/VL.Qt/Images/                    namespace VL.Qt (public QtImage, QtImageState stay where users find them)
  QtImage.cs            QtImage (public view), QtImageState (public)            [ImageResource moved out]
  QtResource.cs         QtResource (internal; was ImageResource, SIP-06)        [renamed, split]
  IResourceApi.cs       IResourceApi + NativeResourceApi                        [split from ResourceClient.cs]
  ResourceClient.cs     resource manager client: create, start, read, release queue, poll, shutdown
  ByteArrayImage.cs     IImage over an owned byte[] (was SnapshotImage + CopiedImage, SIP-13)
  PixelFormats.cs       VL PixelFormat → QtNative format number (was ResourceClient.ToQtNativeFormat)
  ImageFileWriter.cs    background writer of the FileWriter nodes
  ImageNodeReport.cs    error/warning reporting of the image nodes
  VideoFrameStream.cs   frames of one IVideoSource for one consumer
  ShaderSlot.cs         → moves to src/VL.Qt/Shaders/ShaderSlot.cs (owner of a shader resource, review 12),
                          together with TryGetBuiltinShader / builtinShaderPrefix

src/VL.Qt/Paths/                     namespace VL.Qt.Paths
  QtPath.cs             QtPath (public, immutable path)
  FlatPath.cs           FlatPath (internal, flat verbs/points/bounds)           [split from QtPath.cs, K3]
  ArcGeometry.cs        arcs, ovals, round rects → cubics (Skia rules, SIP-02, SIP-11)
  BezierGeometry.cs     anchors + handles → cubics
  SplineGeometry.cs     Catmull-Rom and B-spline → cubics
  HandleMode.cs, PathAddMode.cs, PathDirection.cs   public enums (unchanged)
```

Public in the area after the refactoring: `Style`, `StyleState`, `StyleStates`, `IStylable`, the node classes, `QtImage`, `QtImageState`, `QtPath`, `HandleMode`, `PathAddMode`, `PathDirection`. Everything else is internal.

## 5 Questions for Sebastian

**Q1 Naming of the style setters and getters.** Today a setter is called by its field (`Radius`, `FontSize`, `Color`) and a getter `GetRadius`, `GetColor`; the bundle nodes are `Frame` / `GetFrame`. VL imports a C# property as `Radius` (getter) and `SetRadius` (setter), the other way round, and vvvv users know that pattern from every record. There are also 40 Advanced single setters and getters, mostly boilerplate.
- (a) Keep the names. Nothing breaks.
- (b) VL convention: getters `Radius`, `Color`, …; setters `SetRadius`, `SetColor`, …; bundles `SetFrame` / `Frame`. The setter pin `State` (type `StyleStates`, a set) becomes `States`. This breaks every style patch; the help patches are updated with the change.
- (c) Like (b), and drop the single setters; the bundles with Optional pins already set one field.
- Recommendation: **(b)**. It matches what users expect in vvvv, and 0.10.0 is the moment to do it. Decide before SIP-01 and SIP-10, which touch the same lines.

**Q2 Style setters as process nodes (SIP-01).**
- (a) Process nodes with a cached output: no allocation per frame, the reconciler skips unchanged widgets.
- (b) Keep the static nodes, have the reconciler compare `next.Style` by value before merging, and accept the per-frame garbage.
- Recommendation: **(a)** for `Colors`, `Frame`, `Text`, `Indicator`, `Track` and `Merge`. Combined with Q1(c) only these six remain.

**Q3 Skia parity of `AddRoundRect` (SIP-02).** Scaling the radii together changes how existing patches draw round rects whose radii are too large. A pill gets semicircular ends, as in Skia and as with the `Rectangle` element.
- (a) Follow Skia.
- (b) Keep the per-axis limit and list it under "Differences" in docs/shapes.md.
- Recommendation: **(a)**. The docs promise that Skia patches carry over, and the pill is the common case.

## 6 For other areas

- **Shaders (12):** `ShaderSlot` lives in `Images/` but belongs to `Shaders/`. `_errorSource`/`_errorText` are declared in the middle of the class (`ShaderSlot.cs:61`). `TryGetBuiltinShader` should move with it.
- **Nodes / API (10, 01):** `ArcToNode` passes `input.LastPoint` to `ArcGeometry` (`ArcToNode.cs:43`, `:127`, `:172`); with SIP-11 the argument goes. Whether the SVG `Sweep` pin (bool) matches the meaning of VL.Skia's `SKPathDirection` sweep pin was not checked here.
- **Nodes (10):** `LoadImageAsyncNode` fires `On Loaded` from inside `ResourceClient.Poll` (`LoadImageAsyncNode.cs:133-141`), see SIP-03.
- **Runtime (07):** `ResourceClient.ForApp` sets `Previews` on the process-wide client from every node constructor (`ResourceClient.cs:104-110`). That is harmless because all apps of a process share `IsExported`, but the property belongs to `QtApp` setup.
- **Reconciler (05):** `ResolveStyle` deep-compares `merged.Equals(resolved)` for every widget whose `Style` input is a new reference (`Reconciler.cs:349-351`). That is cheap per widget, but every frame with static setters (SIP-01).
- **Native (09):** the return convention of `vlqt_res_read_pixels` / `vlqt_res_read_preview`, where "−required" shares its range with the error codes −1/−7/−9, should be one documented rule in `vlqt.h`, with named constants mirrored in C# (SIP-05).
