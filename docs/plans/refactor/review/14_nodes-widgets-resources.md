# Review 14 – Widget, image and file nodes

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt/Nodes/` ButtonNode.cs, SliderNode.cs, ToggleNode.cs, TextInputNode.cs, InputIntNode.cs, LoadFontNode.cs, LoadImageNode.cs, LoadImageAsyncNode.cs, ToQtImageNode.cs, SnapshotNode.cs, VideoSourceToQtImageNode.cs, FileWriterNode.cs, FileWriterElementNode.cs; `Nodes/ValueChannel.cs` (outside the area, read because every value widget is built on it). Skimmed: `Nodes/ElementNode.cs` (TakeClicks, ReadOutputs), `Images/ImageNodeReport.cs`, `NodeErrors.cs`, `Images/ResourceClient.cs` (Snapshot, Wait), `Images/ImageFileWriter.cs` (Start, Poll), `src/VL.Qt.Native/resources.cpp:355-375`, `VL.Qt.Tests/SliderNodeTests.cs:151-195`, VL.ImGui `Core/ChannelWidget.cs`, `Widgets/Button/Button.cs`. Read of review 10: sections 1 and 4 only. Not read: the other tests of the area, the help patches.

## 1 Summary

The area is in good shape. The widgets are thin shells over `ElementNode` plus `ValueChannel<T>`, the resource nodes are thin shells over `ResourceClient`, `SnapshotGrab` and `ImageFileWriter`; change detection, disposal and the no-allocation rule hold in every node. The channel pattern is the same on the four value widgets and follows VL.ImGui closely, with two deliberate differences (author "Qt", attributes polled instead of subscribed) that are better than the original. What does not fit:

1. **NODW-01** The widget keeps its clamped copy as the truth: after Min/Max widen, the widget and its channel show different values for good. VL.ImGui reads the channel every frame; VL.Qt should too and clamp only what it shows.
2. **NODW-02** Button is the one widget whose channel pin is not called `Channel` and sits elsewhere.
3. **NODW-03** FileWriter (Element): a second Write before the first grab settled very likely writes the older grab to the newer path (suspicion, native code points that way).
4. **NODW-04** Errors are shown two ways: LoadFont puts a warning on the node, the image and file nodes only fill the Error output.
5. **NODW-05** `VideoSourceToQtImage` breaks the area's own naming (`LoadImage (Async)`, `FileWriter (Element)`) and its output pin is `Output` where ToQtImage says `Image`.

## 2 What works and must survive

- **Pull, not subscribe.** `ValueChannel<T>.Update` reads `channel.Value` once per frame (`ValueChannel.cs:60-104`); no subscription means nothing to dispose and no callback on a foreign thread. Keep it.
- **Author "Qt" on every write** (`ValueChannel.cs:97`, `ElementNode.TakeClicks`): other nodes can tell a user change from a patch change. VL.ImGui writes without author (`ChannelWidget.cs:51-60`); VL.Qt is better here.
- **The user wins over a patch change of the same frame** (`ValueChannel.cs:90-102`), and the user's value is not echoed back as a patch change (`_lastSeen = user`). This is the feedback-loop guard.
- **An unconnected channel pin is `null`** (`[DefaultValue(null)] IChannel<T>?`), the widget then owns its value; a plain value or IOBox is lifted by vvvv into a private channel. Same as VL.ImGui's nullable `Channel` (`ChannelWidget.cs:23`).
- **Min/Max fall back to the channel's attributes**, scanned only when the attribute spread instance changes (`ValueChannel.cs:145-165`): no subscription, no allocation per frame.
- **One Unit per click** on the Button channel with `SetValueAndAuthor`, not `EnsureValue` (`ElementNode.cs:143-150`): a Unit never differs, so `EnsureValue` would drop every click after the first.
- **Hot paths** (once per frame, no allocation): all five widget `Update`s (`CommonInputs` and `Props` are structs, `Describe` returns the cached element when nothing changed); `LoadImage`, `LoadImage (Async)`, `LoadFont` compare the path string and do nothing else; `VideoSourceToQtImage.Update` with `TryTake`; `ImageNodeReport.Update` compares strings by reference first. Exceptions are listed in NODW-06 and NODW-13.
- **Resource ownership:** each node owns at most one `ImageResource` (two for Async while a load is pending) and releases it on path change and on Dispose; a `QtImage` is a handle that needs no disposing (`QtImage.cs:17-22`). LoadFont adds the new font before it releases the old one (`LoadFontNode.cs:55-65`), so a path spelling change does not drop the family.
- **Async contracts:** Snapshot's Is Ready and FileWriter's Is Done never arrive in the frame of the trigger (`FileWriterNode.cs:55-60`); an error without work reports Is Done and Error in the same frame, so a patch waiting on Is Done does not hang (`FileWriterNode.cs:70-72`). A volatile image is copied before the worker writes it (`ImageFileWriter.cs:45-52`).

## 3 Findings

### NODW-01 The channel is the truth of a widget; clamp only what is shown
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: a Slider or Input (Int) whose channel holds a value outside Min/Max shows the channel's value again once the range is widened (today it stays at the old bound).
- **Where:** `src/VL.Qt/Nodes/ValueChannel.cs:79-88`, `:110-120`; `SliderNode.cs:102-105`; `InputIntNode.cs:102-105`; `src/VL.Qt.Tests/SliderNodeTests.cs:151-160`, `InputIntNodeTests.cs:167`
- **Finding:** `Clamp` writes the clamped value into `_value`, and `Update` only takes the channel value when it differs from `_lastSeen`. Channel 5, range 0…1: the widget shows 1. Range widened to 0…10: the channel did not change, so the widget keeps showing 1 while the channel, every other widget on it and the patch say 5. The test encodes this on purpose ("the clamped copy stays clamped like in Qt"), but the pin doc promises the opposite reading: "The value shown, clamped to Min…Max (the channel keeps the patch's value)" (`SliderNode.cs:75`). A view that disagrees with its model without any user action is a bug for the patcher. VL.ImGui reads the channel every frame (`VL.StandardLibs/VL.ImGui/src/Core/ChannelWidget.cs`, `Update()`: `if (Channel.IsValid()) value = Channel.Value;`), so its widget can never drift from the channel.
- **Proposal:** In `ValueChannel<T>`: with a valid channel, `_value = channel.Value` every Update (after the user change was written); `_lastSeen` then only serves the unconnected case and the "user wins" rule. `Clamp(min, max)` becomes `T Clamped(T min, T max)` that returns the clamped copy and does not write `_value`; the nodes pass the clamped copy to `Props` and to the Value output. Rewrite the two tests to: widen the range → the widget shows the channel value. The reconciler then sends Value again after a bound change (it does already, see `SliderNodeTests.cs:179-181`).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/ValueChannel.cs`, `SliderNode.cs`, `InputIntNode.cs`, `src/VL.Qt.Tests/SliderNodeTests.cs`, `InputIntNodeTests.cs`
- **Depends on:** NODE-07 of review 10 (also edits `ValueChannel.cs`); Q1
- **Verification:** the rewritten Clamp tests; `help/Qt/HowTo` patches of Slider and Input (Int) with a channel.

### NODW-02 Button: channel pin named `Channel`, at the same place as on the other widgets
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: the pin `Is Clicked` of Button is renamed to `Channel` and moves; links to it break.
- **Where:** `src/VL.Qt/Nodes/ButtonNode.cs:62`, `:81`; compare `SliderNode.cs:85`, `ToggleNode.cs:102`, `TextInputNode.cs:81`, `InputIntNode.cs:85`
- **Finding:** Four widgets have their channel pin `Channel` right after `Anchor`; Button calls it `Is Clicked` and puts it after `Auto Repeat`. A patcher who learned one widget does not find the pin on the other. VL.ImGui's Button is a `ChannelWidget<Unit>` with the same `Channel` pin as every other widget (`VL.ImGui/src/Widgets/Button/Button.cs:7`, `:15`). "Is Clicked" also reads like a bool output, not like an input channel of Unit.
- **Proposal:** `IChannel<Unit>? channel` directly after `anchor`, doc: "Channel that receives one value per click (author "Qt"). No Interaction node is needed." The click logic moves out of `ElementNode` as review 10 plans (`TakeClicks` removed there); put it next to `ValueChannel` as `internal sealed class ClickChannel` in `Nodes/ClickChannel.cs` with `int Update(IChannel<Unit>? channel, ElementSink sink)`. Update the Button help patch.
- **Decided:** catalog: Button / `Is Clicked` → `On Click` (the pin is not named `Channel`; the other widgets keep `Channel`); A1 → `ClickChannel.Update` takes the `ElementFeedback` (was `ElementSink`); the Button help patch is renamed through the mapping file and script of P19/P20
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/ButtonNode.cs`, `Nodes/ClickChannel.cs` (new), `Nodes/ElementNode.cs`, `src/VL.Qt.Tests/ButtonNodeTests.cs`, the Button help patch under `help/Qt/`
- **Depends on:** NODE-06/NODE-07 of review 10
- **Verification:** `ButtonNodeTests`; `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` on the Button help patch.

### NODW-03 FileWriter (Element): a Write before the previous grab settled probably writes the older grab
- **Kind:** correctness
- **Severity:** should (must if confirmed)
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/FileWriterElementNode.cs:66-89`; `SnapshotNode.cs:99-104`, `:113-119`; `src/VL.Qt/Images/ResourceClient.cs:266-279`; `src/VL.Qt.Native/resources.cpp:361-370`
- **Finding:** Write in frame 1 starts grab A of element X. Write in frame 2 (A not settled yet) sets `_path` to the new path and starts grab B on the same resource. `SnapshotGrab` has one `_settled` flag and no notion of which request settled. QtNative does not cancel A: a result that arrives while the resource is Ready replaces the content ("snapshot replaced", `resources.cpp:361-370`), so A's result settles first. `TakeSettled()` is then true while `_grabbing` is true, and `_writer.Start(image, _path, …)` writes A's pixels (element X of frame 1) to B's path; B's own result is ignored because `_grabbing` is already false. With two different elements written in consecutive frames, the second file gets the first element. Snapshot has the same shape but only shows A's image one frame early, which is harmless. Not confirmed by a test: whether `OnLoadStarted` plus the second `vlqt_res_snapshot` call makes the first result arrive as a separate settle depends on the state machine in `resources.cpp` around the Decoding state.
- **Proposal:** Give each grab a sequence number: `SnapshotGrab.Start` returns or stores `int _requested`, the `Settled` callback records the sequence of the request that settled (QtNative already reports per resource; if it cannot tell requests apart, `SnapshotGrab` counts outstanding requests and ignores settles until the count reaches zero). `FileWriterElementNode` writes only when the newest request settled. Add a test with the fake API that settles A after the second Start.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/SnapshotNode.cs` (or `Images/SnapshotGrab.cs` after the split), `FileWriterElementNode.cs`, `src/VL.Qt.Tests/FileWriterNodeTests.cs`
- **Depends on:** none
- **Verification:** new unit test "second Write before the first grab settled writes the second grab"; SmokeTest scenario of FileWriter (Element) if one exists.

### NODW-04 One rule for how resource and file nodes show errors
- **Kind:** api
- **Severity:** should
- **Breaking:** no (only what appears at the node)
- **Where:** `src/VL.Qt/Nodes/LoadFontNode.cs:66-69`; `src/VL.Qt/Images/ImageNodeReport.cs` (summary and `Update`); `LoadImageNode.cs:47-48`, `ToQtImageNode.cs:56-57`, `FileWriterNode.cs:76-77`
- **Finding:** LoadFont shows a missing or broken font file as a persistent warning on the node (`_errors.Warn`). LoadImage, LoadImage (Async), ToQtImage, VideoSourceToQtImage, Snapshot and both FileWriters only fill the Error output and log the text once (`ImageNodeReport`: "the node shows it at its output, not as a message"). Same mistake (wrong path), two behaviours: a patcher sees a yellow node for a font and nothing for an image. AGENTS.md allows either (Error output or `AddException`), not both side by side. `ImageNodeReport` is also misnamed for the file writers, which pass `null` as resource.
- **Proposal:** Rename `ImageNodeReport` → `ResourceNodeReport` (file `Images/ResourceNodeReport.cs`) and use it in LoadFont too, so all eight nodes follow one rule. Recommended rule: Error output plus log once; a persistent warning at the node only for a state that lasts and that the patcher must fix (load failed: LoadImage, LoadImage (Async), LoadFont, ToQtImage), not for one-shot actions (Snapshot, FileWriter). Alternative: Error output only everywhere (today's image rule), LoadFont loses its warning. Decision in Q2.
- **Decided:** B7 and DECISIONS "No `Error` output pins" → the `Error` outputs go from LoadImage, LoadImage (Async), LoadFont, ToQtImage, VideoSourceToQtImage, Snapshot, FileWriter and FileWriter (Element); every error goes to the log; a lasting load failure (LoadImage, LoadImage (Async), LoadFont, ToQtImage) also marks the node, one-shot failures (Snapshot, FileWriter) only log (Q2 option A without the output); Snapshot keeps `Is Ready`; P10 → FileWriter and FileWriter (Element) get an output `Success` next to `Is Done`; P22/TARGET 3.2 → one mechanism for messages at a node with NODE-05 and RUN-01, one issue; A1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `NodeErrors` is the one store and `ResourceNodeReport` uses it; VideoSourceToQtImage also marks its node on a lasting error, like ToQtImage
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Images/ImageNodeReport.cs` (renamed), `Nodes/LoadFontNode.cs`, `LoadImageNode.cs`, `LoadImageAsyncNode.cs`, `ToQtImageNode.cs`, `VideoSourceToQtImageNode.cs`, `SnapshotNode.cs`, `FileWriterNode.cs`, `FileWriterElementNode.cs`
- **Depends on:** Q2
- **Verification:** unit tests of the image nodes (they run with a null report); manual check in vvvv with a wrong path on LoadImage and LoadFont.

### NODW-05 `VideoSourceToQtImage` → `ToQtImage (VideoSource)`, output pin `Image`
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: node renamed, its output pin `Output` renamed to `Image`.
- **Where:** `src/VL.Qt/Nodes/VideoSourceToQtImageNode.cs:14`, `:55`; `ToQtImageNode.cs:13`, `:40`
- **Finding:** The area names variants of a node with a version in brackets (`LoadImage (Async)`, `FileWriter (Element)`), but the video variant of ToQtImage is a separate word `VideoSourceToQtImage`. In the node browser the two do not appear together. Its return pin is `Output` while ToQtImage, LoadImage and LoadImage (Async) return `Image`; a patcher swapping one for the other loses the link.
- **Proposal:** `[ProcessNode(Name = "ToQtImage (VideoSource)")]`, class `ToQtImageVideoSourceNode` in `ToQtImageVideoSourceNode.cs`, `[return: Pin(Name = "Image")]`. Update the help patch and `help/Help.xml` if it names the node; rename `VideoSourceToQtImageTests.cs` to match.
- **Decided:** catalog: VideoSourceToQtImage → the name is kept (as VL.Video's `VideoSourceToSKImage`), only the category becomes `Qt.Images`; class, file, tests and `help/Help.xml` keep `VideoSourceToQtImage`, there is no `ToQtImageVideoSourceNode.cs`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Nodes/VideoSourceToQtImageNode.cs` (renamed), `src/VL.Qt.Tests/VideoSourceToQtImageTests.cs` (renamed), the help patch that uses the node under `help/Qt/`, `help/Help.xml`, README/docs that name it (search `VideoSourceToQtImage`)
- **Depends on:** none
- **Verification:** `VideoSourceToQtImageTests`; `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` on the video help patch.

### NODW-06 ToQtImage: one QtImage per node, like the video variant
- **Kind:** performance
- **Severity:** could
- **Breaking:** no (unless a patch relies on a new instance per input; see verification)
- **Where:** `src/VL.Qt/Nodes/ToQtImageNode.cs:39`, `:48-54`; `VideoSourceToQtImageNode.cs:109`, `:54`
- **Finding:** Both nodes own one GPU resource and replace its content in place. ToQtImage wraps it in a new `QtImage` for every new input instance; the video variant keeps one `QtImage` for all frames, and the Image element evidently draws the new content without a new instance (that is how video works). An upstream that yields a new `IImage` every frame (a Skia renderer's output, a camera) makes ToQtImage allocate a `QtImage` every frame in a hot path, and the two nodes have different output contracts without a word why.
- **Proposal:** Keep one `QtImage` per resource (`_image ??= new QtImage(_resource)`), set it to null only for a null input; the `<returns>` text says "the same QtImage for all inputs (its content is replaced)", as the video variant does. If a new instance is needed for vvvv change detection downstream, say so in one comment and in the video node too.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/ToQtImageNode.cs`, the ToQtImage tests in `src/VL.Qt.Tests/ResourceClientTests.cs`
- **Depends on:** none
- **Verification:** ToQtImage tests (assert same instance on a new input); the ToQtImage help patch shows a changing image.

### NODW-07 LoadImage (Async): a failed create keeps the old image even with Keep Previous off
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/LoadImageAsyncNode.cs:86-95`, `:49`
- **Finding:** When `_client.CreateImage()` throws (QtNative refuses), `Load` returns before `ReleaseCurrent()`. With Keep Previous off the doc promises "the output switches at once", but the previous image stays at the output while Error reports the new path's failure. LoadImage (the sync node) releases first and returns nothing in the same case (`LoadImageNode.cs:56-73`).
- **Proposal:** Decide the switch before creating: `if (!keepPrevious) ReleaseCurrent();` before the `try`. Add a test with a fake API whose create fails.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/LoadImageAsyncNode.cs`, `src/VL.Qt.Tests/ResourceClientTests.cs`
- **Depends on:** none
- **Verification:** new unit test "create fails with Keep Previous off → output null".

### NODW-08 `TextInput` → `Input (String)`
- **Kind:** naming
- **Severity:** could
- **Breaking:** yes: node renamed.
- **Where:** `src/VL.Qt/Nodes/TextInputNode.cs:20`; `InputIntNode.cs:20`
- **Finding:** The two input fields of the area are `Input (Int)` and `TextInput`. VL.ImGui names the pair `Input (Int)` and `Input (String)` (`VL.ImGui/src/Widgets/…`, `[GenerateNode(Name = "Input (String)", Category = "ImGui.Widgets")]`); a patcher coming from ImGui types "Input" and finds only one of them.
- **Proposal:** `[ProcessNode(Name = "Input (String)")]`, class `InputStringNode` in `InputStringNode.cs`; keep "text, textfield" findable through `Tags`. Decision in Q3.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Nodes/TextInputNode.cs` (renamed), `src/VL.Qt.Tests/TextInputNodeTests.cs` (renamed), the TextInput help patch, `help/Help.xml`, docs naming it
- **Depends on:** Q3
- **Verification:** `TextInputNodeTests`; patch check and vvvvc on the help patch.

### NODW-09 XML docs: write for the patcher, share the widget channel text
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `ButtonNode.cs:16-18`, `SliderNode.cs:15-18`, `ToggleNode.cs:22-25`, `TextInputNode.cs:15-18`, `InputIntNode.cs:15-18`; channel `<param>` at `SliderNode.cs:63`, `ToggleNode.cs:83`, `TextInputNode.cs:61`, `InputIntNode.cs:63`; `measuredSize` at `SliderNode.cs:77` and siblings; `FileWriterNode.cs:45`, `FileWriterElementNode.cs:49`; `LoadImageAsyncNode.cs:10-13`; `ToggleNode.cs:15-19`
- **Finding:** (a) Every widget summary opens with the Qt type ("QtQuick.Controls Button, style Basic", "Qt clicked"), which tells a patcher nothing; the line "Lengths in units of the renderer's space (DIPTopLeft: 1 unit = 100 DIP)" repeats on every element node. (b) The channel `<param>` is the same paragraph four times with one word changed; it is the most important help text of a widget and will drift. (c) `measuredSize` says "Implicit size reported by Qt", a Qt term; the patcher needs "the size the widget takes with Auto Size on". (d) "a write while one runs waits, only the newest one" is not a sentence a reader can parse. (e) LoadImage (Async)'s summary says the previous image stays, which is only true with Keep Previous on. (f) `ToggleLook.Checkbox`/`Switch` have no summaries, so the enum IOBox has no tooltip.
- **Proposal:** (a) Summaries start with what the widget does for the patcher ("Button that reports clicks into a channel and as Bang."); the Qt type goes into `<remarks>` or away. (b) Put the channel text into the shared include file that review 10 proposes (`Nodes/CommonPins.xml`, NODE-03) as `<param name="channel">` with "value" as the neutral word; Button gets its own. (c) "Size the widget takes with Auto Size on, in units." (d) "Starts writing the current Image. A Write while another write runs waits; of several waiting Writes only the newest is written." (e) "…stays at the output until the new one is ready (Keep Previous on)." (f) One summary line per enum member.
- **Decided:** catalog/TARGET 2.2 → `ToggleLook` lives in `Elements/Enums/ToggleLook.cs` (namespace `VL.Qt.Elements`, category `Qt.Elements`), so the member summaries of (f) are written there; Button keeps its own channel `<param>` for the pin `On Click` (catalog)
- **Effort:** S, mechanical: no
- **Touches:** the five widget node files, `ToggleNode.cs` (or `ToggleLook.cs` after the split), `FileWriterNode.cs`, `FileWriterElementNode.cs`, `LoadImageAsyncNode.cs`, `Nodes/CommonPins.xml`
- **Depends on:** NODE-03 of review 10
- **Verification:** build without XML-doc warnings; hover the pins in vvvv (manual-check).

### NODW-10 Test constructors of the resource nodes in one shape
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `LoadImageNode.cs:23-34`, `ToQtImageNode.cs:23-34`, `SnapshotNode.cs:25-36`, `VideoSourceToQtImageNode.cs:29-47`, `FileWriterNode.cs:26-37`, `FileWriterElementNode.cs:27-40`; the good shape: `LoadImageAsyncNode.cs:28-44`
- **Finding:** (K6 in this area.) The internal constructors of the resource nodes inject `ResourceClient`, `SceneRegistry`, `ImageFileWriter` or `VideoPlaybackContext`; that is dependency injection, not a test hook, and should stay. But six nodes duplicate the body in the public and the internal constructor; only LoadImage (Async) chains both to one private constructor. VideoSourceToQtImage's test constructor skips the `IFrameClock` lookup and the logger, so the public path is untested. The widget test constructors (`Func<string, IDisposable> addWarning`) and the `Core` accessors are covered by review 10.
- **Proposal:** Every resource node: `public XNode(NodeContext)` → `: this(<real dependencies>, new NodeErrors(nodeContext))`, one `internal XNode(<dependencies>, NodeErrors? errors = null)` that holds the body. Drop the "For tests:" summaries; the internal constructor documents its parameters instead.
- **Decided:** P22 → NODW-10 is one of the K6 findings and is worked under TEST-07; the `NodeErrors` argument of the constructor chain follows the one mechanism for messages at a node (P22, TARGET 3.2)
- **Effort:** S, mechanical: yes
- **Touches:** `LoadImageNode.cs`, `ToQtImageNode.cs`, `SnapshotNode.cs`, `VideoSourceToQtImageNode.cs`, `FileWriterNode.cs`, `FileWriterElementNode.cs`
- **Depends on:** none
- **Verification:** the existing unit tests compile and pass unchanged.

### NODW-11 Node-rule gaps (AGENTS.md "Nodes")
- **Kind:** api
- **Severity:** could
- **Breaking:** no
- **Where:** `LoadImageNode.cs:42` (`mipmaps`), `LoadImageAsyncNode.cs:57` (`mipmaps`), `VideoSourceToQtImageNode.cs:56` (`fallback`), `ButtonNode.cs:80` (`autoRepeat`); `LoadImageAsyncNode.cs:61` (`Model.PinVisibility`); `ToggleNode.cs:15-19`
- **Finding:** Checked every node against the rules: `[ProcessNode]`, one `Update`, `IDisposable`, outputs via `out`/`[return: Pin]`, no tuples, optional pins marked: all hold. Gaps: (a) pins without an explicit default although AGENTS.md asks for one (the four above; the common `clip`/`debug` are NODE-04 of review 10). (b) Two default styles side by side: `[DefaultValue]` on the widgets and LoadImage (Async), C# parameter defaults placed after the `out` parameters on Snapshot, FileWriter, FileWriter (Element) and VideoSourceToQtImage; both are allowed, but within one area one is easier to read. (c) `Model.PinVisibility` half-qualified where the other files import `VL.Model`. (d) `ToggleLook` is a public type in `ToggleNode.cs` (K3).
- **Proposal:** (a) `[DefaultValue(false)]` / `[DefaultValue(null)]` on the four pins. (b) Use `[DefaultValue]` on inputs and keep all `out` parameters last, as the widgets do; pin order in vvvv stays the same because vvvv orders inputs and outputs separately. (c) `using VL.Model;`. (d) `Nodes/ToggleLook.cs`.
- **Decided:** D3 (b) → (a) is dropped: `[DefaultValue(false)]` and `[DefaultValue(null)]` are not added, since `default(T)` is the intended value of `mipmaps`, `autoRepeat` and `fallback` (an explicit default only where it differs from `default(T)`, never for `Matrix`, whose vvvv default is the identity, A2 of [DECISION-ROUND.md](../DECISION-ROUND.md)); (d) → `ToggleLook` moves to `Elements/Enums/ToggleLook.cs` (namespace `VL.Qt.Elements`), not `Nodes/ToggleLook.cs` (catalog, TARGET 2.2)
- **Effort:** S, mechanical: yes
- **Touches:** `LoadImageNode.cs`, `LoadImageAsyncNode.cs`, `VideoSourceToQtImageNode.cs`, `ButtonNode.cs`, `SnapshotNode.cs`, `FileWriterNode.cs`, `FileWriterElementNode.cs`, `ToggleNode.cs`, `Nodes/ToggleLook.cs` (new)
- **Depends on:** NODE-04 of review 10 (the `[DefaultValue]` form)
- **Verification:** `python tools/vl-patch-check.py` on all help patches (pin catalog reads defaults); vvvvc on the affected help patches.

### NODW-12 Split the shared grab out of SnapshotNode.cs
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/SnapshotNode.cs:17-18`, `:73-129`; `FileWriterNode.cs:17-20`; `FileWriterElementNode.cs:72`
- **Finding:** `SnapshotGrab` serves two nodes but lives in `SnapshotNode.cs` (K3), and its error text `NotShownError` lives on `SnapshotNode` although only `SnapshotGrab` returns it. FileWriter (Element) reaches into `FileWriterNode.NoPathError`. A reader of `SnapshotGrab` has to open the node to find its own error text.
- **Proposal:** `src/VL.Qt/Images/SnapshotGrab.cs` (next to `ResourceClient`, which it wraps) with `internal const string NotShownError`; `NoPathError`/`NoImageError` move to `ImageFileWriter` as `internal const`, used by both FileWriter nodes.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Nodes/SnapshotNode.cs`, `src/VL.Qt/Images/SnapshotGrab.cs` (new), `Nodes/FileWriterNode.cs`, `Nodes/FileWriterElementNode.cs`, `src/VL.Qt/Images/ImageFileWriter.cs`, `src/VL.Qt.Tests/SnapshotNodeTests.cs`, `FileWriterNodeTests.cs` (constant references)
- **Depends on:** NODW-03 (same code), do it first or together
- **Verification:** `SnapshotNodeTests`, `FileWriterNodeTests`.

### NODW-13 VideoSourceToQtImage formats a new error string every frame for GPU frames
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/VideoSourceToQtImageNode.cs:102-106`
- **Finding:** A frame that is not in CPU memory sets `_error = $"The video frame ({w} x {h}) is not in CPU memory"` on every new frame: one string allocation per frame for as long as the source delivers such frames. Rare today (the context has no device), but it is the hot path, and the fix is free.
- **Proposal:** Build the text only when width or height changed (`if (_error.Length == 0 || size changed)`), or drop the size from the text.
- **Decided:** DECISIONS "No `Error` output pins" → the text is not an output but a message of the shared node-message mechanism (log, once; P22/TARGET 3.2); the proposal stands: no per-frame formatting
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/VideoSourceToQtImageNode.cs`
- **Depends on:** NODW-05 if done in the same edit
- **Verification:** `VideoSourceToQtImageTests` (a fake source with texture-backed frames).

### NODW-14 LoadFont has no test
- **Kind:** tests
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Nodes/LoadFontNode.cs`; `src/VL.Qt.Tests/` (no `LoadFontNode` anywhere)
- **Finding:** Every node of the area except LoadFont is constructed in a unit test. LoadFont calls `Native` directly (`LoadFontNode.cs:56`, `:74`) and creates the Qt app in its constructor, so it cannot be tested without QtNative; its reference counting across nodes and the "add before release" order (`:55`) are unprotected.
- **Proposal:** Put the two native calls behind the same kind of API seam `ResourceClient` uses (an `IFontApi` with `Add`/`Release`, the real one forwarding to `Native`), inject it through an internal constructor as in NODW-10, and test: path change keeps the family when add succeeds, release on Dispose, error text on failure. Alternatively a SmokeTest scenario with a font file.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Nodes/LoadFontNode.cs`, `src/VL.Qt.Tests/LoadFontNodeTests.cs` (new)
- **Depends on:** NODW-10
- **Verification:** the new tests.

## 4 Target structure

Namespace `VL.Qt.Nodes` for the nodes, `VL.Qt` for the helpers under `Images/`. Public: the node classes and `ToggleLook`.

```
src/VL.Qt/Nodes/
  ButtonNode.cs            public: Channel pin like the other widgets (NODW-02)
  ClickChannel.cs          NEW internal sealed: clicks of the sink → Unit per click into the channel (from ElementNode.TakeClicks)
  SliderNode.cs            public
  ToggleNode.cs            public
  ToggleLook.cs            NEW (split from ToggleNode.cs): public enum, members documented
  InputIntNode.cs          public
  InputStringNode.cs       was TextInputNode.cs, node "Input (String)" (NODW-08, if Q3 agrees)
  ValueChannel.cs          internal sealed: channel read every frame, Clamped() returns a copy (NODW-01)
  LoadFontNode.cs          public: ResourceNodeReport, injectable font API (NODW-04, NODW-14)
  LoadImageNode.cs         public
  LoadImageAsyncNode.cs    public
  ToQtImageNode.cs         public: one QtImage per node (NODW-06)
  ToQtImageVideoSourceNode.cs  was VideoSourceToQtImageNode.cs, node "ToQtImage (VideoSource)", output "Image" (NODW-05)
  SnapshotNode.cs          public: the node only
  FileWriterNode.cs        public
  FileWriterElementNode.cs public: writes only the newest grab (NODW-03)
src/VL.Qt/Images/
  SnapshotGrab.cs          NEW (split from SnapshotNode.cs): grab with request sequence, NotShownError (NODW-03, NODW-12)
  ResourceNodeReport.cs    was ImageNodeReport.cs: one error rule for all resource and file nodes (NODW-04)
  ImageFileWriter.cs       + NoPathError, NoImageError (from FileWriterNode)
```

Deleted: the duplicated constructor bodies of six resource nodes (NODW-10), `SnapshotNode.NotShownError`, `FileWriterNode.NoPathError`/`NoImageError` (moved).

## 5 Questions for Sebastian

**Q1 Is the channel or the widget the truth when the value is outside Min/Max?** (NODW-01)
- A, channel (recommended): the widget reads the channel every frame like VL.ImGui and clamps only what it shows. Widening the range shows the channel's value again; all widgets on one channel agree. Cost: a small change in `ValueChannel`, two tests rewritten.
- B, widget (today): the widget keeps its clamped copy like a Qt control does; widget and channel can disagree until the channel changes. Then the Value pin doc has to say so, because today it suggests A.

**Q2 How do resource and file nodes show an error?** (NODW-04)
- A (recommended): Error output and log for all; a warning at the node only for a lasting load failure (LoadImage, LoadImage (Async), LoadFont, ToQtImage).
- B: Error output and log only (today's image rule); LoadFont loses its warning. Quietest, but a wrong path is invisible unless Error is connected.
- C: warning at the node for every error, also one-shot Snapshot/FileWriter failures. Most visible; a failed write stays yellow until the next one.

**Q3 Rename `TextInput` to `Input (String)`?** (NODW-08) Pairs with `Input (Int)` and with VL.ImGui. Against: "TextInput" is what people type who never used ImGui; `Tags = "text, textfield"` keeps it findable. Recommendation: rename.

## 6 For other areas

- Interop (09): `LoadFontNode.cs:56` calls the wrapper `Native.FontAdd`, `:74` the raw `Native.vlqt_font_release`; one style for the font API.
- Nodes/elements (10): `ElementNode.TakeClicks` is Button-only (`ElementNode.cs:137-150`), supports NODE-06's removal; `ValueChannel.cs:11-29` summary is a 19-line design note with issue and PR numbers (K1/K4), the rules belong to `docs/` (channel section of architecture.md).
- Images (13): `ResourceClient.Wait` only logs when the 10 s timeout passes (`ResourceClient.cs:414-421`); LoadImage then returns a Loading image without anything at the node or its Error output.
- Native (QtNative resources): `resources.cpp:361-370` applies a snapshot result that arrives while the resource is Ready as a replacement; whether a superseded request should still settle is the question behind NODW-03.
