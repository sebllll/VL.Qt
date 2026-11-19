# Review 06 – Elements

Claude Opus 5.5, 2026-11-19. Files read in full: all 34 files in `src/VL.Qt/Elements/`; `docs/topics/element-immutability.md`, `docs/decisions/events.md`, `docs/decisions/element-catalog-v1.md`; `docs/plans/refactor/review/05_reconciler.md` lines 388–459. Looked up by search and short excerpts only: `src/VL.Qt/Reconciler/PropertyDiff.cs:34-75`, `UniformWriter.cs:13,100`, `EffectDiff.cs:41,300`, `Reconciler.cs:145,251-258,475,528`, `SceneRegistry.cs:306-407`, `SceneCore.cs:20,177,188,242`, `WindowRenderer.cs:228`, `Initialization.cs:1-30`, `Renderer.cs:1-20`, `VL.Qt.csproj` (InternalsVisibleTo), the `Update` signatures in `src/VL.Qt/Nodes`, and the test method names of `ElementTests`, `ElementTypeTests`, `EffectTests`, `InteractionTests`, `SinkRegistryTests`. Not read: any test file in full, QtNative.

Tests that cover the area: `ElementTests.cs`, `ElementTypeTests.cs`, `EffectTests.cs`, `EffectChainTests.cs`, `EffectPaddingTests.cs`, `EffectChainReconcilerTests.cs`, `InteractionTests.cs`, `SinkRegistryTests.cs`, `SinkRegistrySpaceTests.cs`, `SceneRegistrySlotTests.cs`, `QmlTests.cs`, `QmlNodeInstanceTests.cs`, `SamplerTests.cs`, `ShaderEffectTests.cs`, `Allocations.cs`; SmokeTest `InteractionTest.cs`, `HandlerTest.Latency.cs`, `MultiPlacementTest.cs`.

## 1 Summary

The element model is small, immutable and consistent where it matters: sealed classes with `init` properties, reference equality as the "unchanged" test, two immutable chains (Effect, Interaction) with a public abstract base and internal links, and a return path (sink → registry → node) that is allocation-free per frame and well tested. The problems are around the edges:

1. **The return path is public by accident and named for its plumbing.** `ElementSink`, `SinkState` (30 public setters), `SinkSlot`, `ElementType`, `Element.Sink` and several static helpers are public under `ImportAsIs`, although no pin uses them (ELM-04, ELM-05). "Sink" also means the command writer (`ICommandSink`) in the same files (ELM-06).
2. **Docs that describe an older state:** the `Effect` summary still describes one MultiEffect and an order-free chain (ELM-01); the decision records disagree with the code on Bounds/Pivot, sink identity, open questions (ELM-12).
3. **Hover/press of an element shown at several places is last-write-wins**, not the OR the decision promises (ELM-02).
4. **The element model knows the wire format and the reconciler** (handler SETs, QML event decoding, a name check from `UniformWriter`) (ELM-09).
5. **`Children` on `Element`** is a throwing `init` on leaves and means "hidden texture sources" on `ShaderEffect` (ELM-10); `Effect.cs` and `Interaction.cs` hold 11 and 14 types (ELM-11).

## 2 What works and must survive

- **Elements are immutable descriptions** (sealed, `init`-only, reference equality = unchanged subtree). Tests: `ElementTests.Immutable_OnlyInitSetters`, `ElementTypeTests.SealedAndInitOnly`. Elements are classes, so there is no `with` copy: a node builds a new instance with an object initializer only on change. `Uniform` and `Sampler` are records; their `with` is used only at creation (`Uniform.cs:45,48`).
- **The defaults of the element classes are the wire baseline.** `PropertyDiff.DefaultOf` diffs a new element against `new Rectangle()` and so on (`PropertyDiff.cs:41-55`). Changing a default in `Elements/` changes what is sent. The defaults must stay equal to QtNative's. Tests: `ElementTypeTests.*Defaults`.
- **Enum values are Qt or protocol values** and pass through unchanged (`Alignment.cs:3`, `Text.cs`, `Shape*.cs`, `ElementType.cs`). Tests: `ShapeEnums_AreProtocolValues`, `TextEnums_AreQtValues`, `TypeIds`.
- **Chains are immutable linked lists** (`Previous`), with a `private protected` base constructor so that only the library adds link kinds (`Effect.cs:27`, `Interaction.cs:17`). Unchanged node inputs give the same instance (`EffectTests.Nodes_UnchangedInputs_KeepInstance`).
- **Return path:** one stable sink per node instance; the description only references it; handles are counted up and never reused (`SinkRegistry.cs:59-63`), so late events of a released handle are dropped instead of landing in another slot; release resets pointer state; only the primary occurrence writes state values. All on the main thread, no locks.
- **Events are "take" counters, states are values** (`TakeClicks`, `TakeWheel`, `TakeValue<T>` reset what they return; `DragUpdates`, `TouchUpdates`, `PinchUpdates` count so that an equal value is still seen as new).
- **Hot paths (must not allocate per frame):**
  - `SinkRegistry.Dispatch` (`SinkRegistry.cs:145`), once per frame and window. It allocates only for strings (EV_VALUE, EV_ERROR), QML events, touch points and slot growth. Test: `SinkRegistryTests.Dispatch_1000Records_NoAllocations`.
  - The `ElementSink` readers (`IsHovered` … `MediaDuration`, `ElementSink.cs:151-319`), called by every element and interaction node every frame. Test: `Missing_Slot_ReturnsDefaults_WithoutAllocating`, `InteractionTests.SteadyState_*NoAllocation`.
  - `QmlProperties.Update` (`QmlProperties.cs:86`), every frame per QmlElement node, allocation-free while the dictionary is unchanged.
  - Not hot (only on change): `Effect.Segment`, `Interaction.WriteProperties`, `ShaderEffect.BuildChildren`.
- **Unit conversion at the boundary:** Dispatch converts Qt's logical pixels to units with the scene's `SpaceMapping` (`SinkRegistry.cs:114,257-317`). Elements and nodes never see pixels.

**Who writes and reads `SinkState`** (all on the vvvv main thread; Dispatch runs in the renderer, `WindowRenderer.cs:228` → `SceneCore.cs:242`, so nodes see a value one frame after Qt produced it):

| Fields | Written by | Reset by | Read by |
|---|---|---|---|
| `MeasuredSize`, `SceneBounds`, `MediaPosition/Duration`, `Error` | `SinkRegistry.Dispatch`, primary handle only | `Release` (Error) | element nodes via `ElementSink` |
| `IsHovered`, `IsPressed`, `IsActive`, `PointerPosition`, `TapCount`, `DragDelta/Updates`, `TouchPoints/Updates`, `Pinch*` | Dispatch, every handle | `Release` (part of them) | Hover, Click, Point, Drag, Touch Points, Pinch nodes; Button |
| `PendingClicks`, `PendingLongPresses`, `PendingWheel`, `HasPendingValue…PendingString`, `PendingQmlSignals` | Dispatch (adds) | the node's `Take*` | nodes |
| `QmlProperties`, `QmlPropertyUpdates` | Dispatch | `ResetQml` (node) | QmlElement node |
| `Warning` | `SceneRegistry.cs:307,394` (duplicates), `EffectDiff.cs:300` (effect duplicate) | the same, compare-and-clear | `ElementSink.Warning` |
| `ApplyError` | `SceneCore.cs:177` | `SceneCore.cs:188` | `ElementSink.Error` (wins) |
| `BuildError` | `Reconciler.cs:256` | `Reconciler.cs:252` | `ElementSink.Error` |

An event for an element that is gone: the reconciler releases the handle when it destroys or unbinds the item (`Reconciler.cs:522,614,621,664,804`); later records for that handle find no binding and are counted as ignored. A node removed from the patch keeps receiving into its orphaned sink until the next reconcile releases the handle; this is harmless.

## 3 Findings

### ELM-01 Rewrite the Effect summary for shader links; delete `Effect.Fold`
- **Kind:** docs, dead-code
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/Effect.cs:9-24`, `:38-51`, `:173`, `:246`; tests `EffectTests.cs:40-59`, `EffectPaddingTests.cs:23-54`, `EffectChainTests.cs:117`
- **Finding:** The class summary says the whole chain "is folded into one EffectDesc, which becomes one Qt Quick MultiEffect", "the order of the chain has no meaning" and "each kind may appear once". Since shader links (`ShaderLink`, `Segment`) all three are false. `Segment` keeps the application order, a shader link splits the chain into several MultiEffect runs, order matters between runs, and a kind may appear once per run (Blur → shader → Blur gives no warning). A reader who adds an effect trusts the summary and gets it wrong. `Fold` is called only by tests; production uses `Segment` (`EffectDiff.cs:41`). `EffectKind` and `IManualPadding` still point to `Fold` in their docs.
- **Proposal:** Summary of `Effect` in four sentences: an immutable chain; `Segment` cuts it at every `ShaderLink` into runs of built-in links, in application order (start of the chain first); each run becomes one MultiEffect, in which order has no meaning and a kind counts once (the link nearest to the pin wins); the effect area rule moves to `docs/` (effects page) with one sentence left here. Delete `Fold`. `FoldRun` becomes `private static EffectDesc FoldRun(List<Effect> nearestFirst, out string? warning)`. Tests call `Effect.Segment(chain, out _)[0].Multi` (a helper `SingleRun(chain)` in the test file).
- **Decided:** O5 → `ShaderLink` is `ShaderEffect : Effect`, so the summary and `Segment` say 'cuts the chain at every `ShaderEffect`'; O4 → the five built-in effects are the 'merged effects' (each run of them becomes one MultiEffect layer) and the others the 'shader effects', in the summary and on the effects page
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Effect.cs`, `src/VL.Qt.Tests/EffectTests.cs`, `EffectPaddingTests.cs`, `EffectChainTests.cs`
- **Depends on:** none (do it together with ELM-11)
- **Verification:** `EffectTests`, `EffectPaddingTests`, `EffectChainTests`, `EffectChainReconcilerTests` green.

### ELM-02 Combine hover and press of several occurrences with OR, as the decision says
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes: an element that appears twice in the tree reports `IsHovered`/`IsPressed` true while either occurrence is hovered or pressed; today the last event wins.
- **Where:** `src/VL.Qt/Elements/SinkRegistry.cs:151`, `:263-270`, `:85-89`, `:91-108`; `docs/decisions/events.md:21`
- **Finding:** `events.md` says that with one sink and several Qt objects "hover and pressed combine with OR". Dispatch writes `IsHovered = payload` for every handle, primary or not (`IsState` excludes only Measured, SceneBounds, Media, Error), so the last record of the poll wins. The pointer moving from occurrence A to occurrence B in one frame yields "B enter, A leave" or "A leave, B enter", depending on Qt's delivery order. In the first order the slot ends up not hovered while B is hovered. Also `Release` of the primary handle resets hover and press although another occurrence may still be hovered. Item-level hover and press come from the widgets (handler handles are always primary and unique per sink, `SinkRegistry.cs:28`), so the case is "the same Button at two places". I did not run it; the order Qt uses is not verified.
- **Proposal:** Track the bits per handle and derive the slot value. `Binding` gets `bool Hovered, Pressed` (written back into `_slots[handle]`, which is a struct update without allocation). `SinkState` gets `internal int HoverCount, PressCount`. On EV_HOVER/EV_PRESS a handle that changes its bit adjusts the count, and `IsHovered = HoverCount > 0`. `Release` subtracts the handle's bits instead of resetting the slot; the primary still resets the other pointer fields. If Sebastian prefers last-write-wins, change `events.md:21` instead (Q3).
- **Decided:** B6 → (a), OR over all occurrences as proposed (per-handle bits, counts per slot); the 'prefers last-write-wins' branch is not taken, `events.md` (in `docs/design/`, G5) stays the rule; A1 → `SinkRegistry` is `FeedbackRouter`, `SinkState` is `FeedbackState`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/SinkRegistry.cs`, `src/VL.Qt/Elements/ElementSink.cs`, `src/VL.Qt.Tests/SinkRegistryTests.cs`
- **Depends on:** Q3
- **Verification:** New `SinkRegistryTests` case: two handles on one slot; hover 1 on h1, then hover 0 on h2 → hovered; release h1 while h2 hovered → hovered. Existing `NotPrimary_DropsStateKeepsEvents_ReleaseLeavesSlot` still green.

### ELM-03 `SinkRegistry.Clear` is never called by the library: check the dispose path, delete test-only members
- **Kind:** lifetime, tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/SinkRegistry.cs:123-130`, `:48-49`, `:68-69`, `:116-121`; only caller of `Clear`: `src/VL.Qt.SmokeTest/HandlerTest.Latency.cs:134`
- **Finding:** `Clear` is documented as "Frees all handles (renderer dispose)", but nothing in `src/VL.Qt` calls it. Either the reconciler releases every handle one by one when a renderer is disposed (then `Clear` and its doc are dead), or a window closed while an element is hovered or pressed leaves `IsHovered`/`IsPressed` stuck at true in the element node. I could not confirm which from my area. `TryResolve` and `IsPrimary` are used only by tests (`ReconcilerSceneTests.cs:42,58,125`, `SinkRegistryTests.cs:251`). `Count` is used only by tests and the SmokeTest (K6).
- **Proposal:** Read `Reconciler.Clear`/`Discard` (REC area). If they release per handle, delete `Clear` and use the same path in the SmokeTest. If they do not, call `_sinks.Clear()` from the reconciler's dispose and add a test "dispose while hovered → not hovered". Replace `TryResolve`/`IsPrimary`/`Count` in tests with observable behaviour: send an event for the handle and check the slot; for `Count`, assert that a released handle's events are ignored (`DispatchResult.Ignored`). If one counter must stay, keep `Count` (a single `int`, cheap) and drop the other two.
- **Decided:** A1 → `SinkRegistry` is `FeedbackRouter` (`Clear`, `TryResolve`, `IsPrimary`, `Count` are its members)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/SinkRegistry.cs`, possibly `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt.Tests/ReconcilerSceneTests.cs`, `SinkRegistryTests.cs`, `src/VL.Qt.SmokeTest/HandlerTest.Latency.cs`
- **Depends on:** none
- **Verification:** New unit test "renderer disposed while hovered → `IsHovered` false"; SmokeTest `HandlerTest` latency scenario.

### ELM-04 Make the return path and helpers internal; keep public only what pins carry
- **Kind:** api
- **Severity:** should
- **Breaking:** yes: C# code outside VL.Qt can no longer read or write sinks, set `Element.Sink` or call `Video.ClampRate`/`Shape.ExtentOf`; patches lose the corresponding imported nodes, if vvvv shows them.
- **Where:** `src/VL.Qt/Elements/ElementSink.cs:10`, `:114`, `:350`; `Element.cs:17`, `:34`; `Interaction.cs:30`; `ElementType.cs:7`; `Video.cs:41-47`; `Shape.cs:65-82`; `Text.cs:51`; `src/VL.Qt/Renderer.cs:14`
- **Finding:** Every node outputs `Element`, `Effect` or `Interaction` (all `Update` signatures in `src/VL.Qt/Nodes`). No pin carries `ElementSink`, `SinkState`, `SinkSlot` or `ElementType`. They are public only because `Element.Sink` and `Element.Type` are. `SinkState` has 22 public setters, so any C# or VL code can fake a click or clear an error. Under `[assembly: ImportAsIs]` without `[Smell]`, the public members of these types (for example `ElementSink.TakeClicks`, `ElementSink.Resize`, `Video.ClampRate`, `Shape.ExtentOf`) are probably imported as nodes; this needs a look in the node browser to confirm. All neighbouring assemblies already have `InternalsVisibleTo` (`VL.Qt.csproj:35-40`), so nothing outside needs them. `Text.DefaultFontSize` and `Video.MinRate` are constants of the library, not API.
- **Proposal:**
  - Make `ElementSink`, `SinkState`, `SinkSlot` and `ElementType` internal (renamed per ELM-06); make `Element.Type`, `Element.Sink` and `Interaction.Sink` internal.
  - Make `Video.MinRate`, `ClampRate`, `ClampVolume`, `Shape.ExtentOf` and `Text.DefaultFontSize` internal.
  - Public stays: `Element`, `IElement`, `Effect`, `Interaction`, `Sampler`, `Uniform`, `TouchPoint`, `QmlSignal` and the enums used on pins (`PointerButtons`, `PointerDevices`, `DragAxis`, `FontWeight`, `TextWrap`, `TextElide`, `TextAutofit`, `TextHorizontalAlignment`, `HorizontalAlignment`, `VerticalAlignment`, `ImageFillMode`, `VideoFillMode`, `SliderOrientation`, `ShapeCap`, `ShapeJoin`, `ShapeFillRule`, `SamplerFilter`, `SamplerMipFilter`, `SamplerAddress`).
  - The concrete element classes: Q2.
- **Decided:** A6 → the concrete element classes (`Rectangle`, `Text`, `Group` …) are internal, so Q2 is answered 'internal' and `Video.ClampRate`, `Shape.ExtentOf` and the other statics of these classes leave the node browser with them; C3 and P5 → `Element` and `Container` are internal too and only `IElement` is public (the 'Public stays' list drops `Element`); catalog → `FontWeight` goes (Font Weight is `int` everywhere) and `ImageFillMode`, `VideoFillMode` become `FillMode`, so the public-enum list loses the first three and gains `FillMode`; A1 → `ElementSink`, `SinkState`, `SinkSlot` are `ElementFeedback`, `FeedbackState`, `FeedbackSlot`, and `Interaction.Sink` is the internal `Interaction.Feedback`; B9 of [DECISION-ROUND.md](../DECISION-ROUND.md) → `ElementType` is not an element type: only `Protocol.ElementType` in `Wire/`, the mapping lives in `Reconciliation/`, `Elements/ElementType.cs` goes
- **Effort:** M, mechanical: yes (except Q2)
- **Touches:** `src/VL.Qt/Elements/*.cs`; compile errors will point to any public node member that exposes these types (none found by search)
- **Depends on:** ELM-05, ELM-06 (do the rename in the same pass), Q2
- **Verification:** `dotnet build` of all projects; `tools/vvvvc.ps1` over the help patches (`python tools/vl-patch-check.py` with no argument, then vvvvc on a few element patches); node browser check "Qt.Elements" by a human (`manual-check`).

### ELM-05 Close `Element` to foreign subclasses; `ElementType` is a redundant second type tag
- **Kind:** api, correctness
- **Severity:** should
- **Breaking:** yes: C# code outside VL.Qt can no longer derive from `Element`.
- **Where:** `src/VL.Qt/Elements/Element.cs:15`; `src/VL.Qt/Reconciler/PropertyDiff.cs:69-74`; `src/VL.Qt/Elements/StylableElement.cs:15`
- **Finding:** `Element` has a `protected` constructor taking any `ElementType`, while `Effect` and `Interaction` use `private protected` (`Effect.cs:27`, `Interaction.cs:17`). A class outside the library can derive and pass, for example, `ElementType.Rectangle`. `PropertyDiff` then casts `(Rectangle)next` by `next.Type` (`PropertyDiff.cs:71`) and throws `InvalidCastException` in the middle of a reconcile, which REC Q2 shows is not handled. The summary's "Subclasses should be sealed" is a wish, not a rule. The CLR type already says what an element is; `Type` exists for the wire id and for `Group → Item`.
- **Proposal:** `private protected Element(ElementType type)`; delete the sentence about sealing (the test `SealedAndInitOnly` enforces it). Make `ElementType` and `Element.Type` internal (ELM-04).
- **Decided:** C3, A6 → `Element` is internal, so a foreign subclass cannot exist by accessibility; a foreign implementation of `IElement` is ignored, with a warning at the node (P5)
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Elements/Element.cs`, `ElementType.cs`
- **Depends on:** none
- **Verification:** `ElementTypeTests`, `ElementTests` green.

### ELM-06 Rename "sink" for the way back from Qt
- **Kind:** naming
- **Severity:** should
- **Breaking:** yes: C# names only (after ELM-04 they are internal); the protocol field `sinkHandle` of BIND is renamed in `protocol.h`/`Protocol.cs`.
- **Where:** `src/VL.Qt/Elements/ElementSink.cs`, `SinkRegistry.cs`, `Element.cs:34`, `Interaction.cs:29-30` vs `:41`; `ICommandSink` in `src/VL.Qt/Reconciler/`
- **Finding:** "Sink" names two opposite directions. `ICommandSink` receives commands going to Qt; `ElementSink` receives events coming back. In `Interaction` both appear in one class: the property `Sink` (`SinkSlot`, events back, `:30`) and the parameter `ICommandSink sink` of `WriteProperties` (commands out, `:41`). A vvvv user knows "sink" from neither VL nor Skia. The three types also do not say how they relate: `SinkSlot` is a reference to a slot, `SinkState` is the slot, `ElementSink` is the list of slots, and `SinkRegistry` routes poll records by handle.
- **Proposal (recommended, Q1 A):** name the whole path "feedback".

  | Old | New | Role |
  |---|---|---|
  | `ElementSink` | `ElementFeedback` | per node instance, one state per spread slot, read API for the node |
  | `SinkState` | `FeedbackState` | the values of one slot |
  | `SinkSlot` | `FeedbackSlot` | (feedback, index) reference held by a description |
  | `SinkRegistry` | `FeedbackRouter` | handle ↔ slot, writes the poll buffer into slots (moves to `Reconciliation/`, see section 4) |
  | `Element.Sink`, `Interaction.Sink` | `Feedback` | |
  | BIND `sinkHandle`, `Acquire/Release` | `feedbackHandle` | |

  Alternative B: keep "sink" for this path and rename `ICommandSink` to `ICommandTarget`.
- **Decided:** A1 → (a), the table as proposed (alternative B not taken); O1 → 'feedback' names only this way back (`FeedbackLayer` of QtNative is `PreviousFrameLayer`); O7 → the number in BIND and in the event header is `feedbackId`, not `feedbackHandle` (`EventReader.FeedbackId`); P14 → done in wave 3 as one mechanical change before the issues of wave 4, so 'right after the REC file moves, before ELM-07' no longer applies; TARGET 2.2 → `FeedbackRouter` lives in `Reconciliation/`, the three feedback types in `Elements/Feedback/`
- **Effort:** L, mechanical: yes (rename refactoring; about 25 test files reference the names)
- **Touches:** `src/VL.Qt/Elements/`, `src/VL.Qt/Reconciler/` (Reconciler, SceneRegistry, EffectDiff, PropertyDiff), `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/Nodes/` (ElementNode and the interaction nodes), `src/VL.Qt/Protocol.cs`, `src/VL.Qt.Native/protocol.h` (field name only), `src/VL.Qt.Tests/`, `src/VL.Qt.SmokeTest/`, `docs/decisions/events.md`, `docs/architecture.md`
- **Depends on:** Q1; best done right after the REC file moves, before ELM-07
- **Verification:** build; all unit tests; SmokeTest `--drift`.

### ELM-07 Give `SinkState` one owner per field and no public setters; replace `SinkSlot.State`
- **Kind:** structure
- **Severity:** should
- **Breaking:** no (after ELM-04)
- **Where:** `src/VL.Qt/Elements/ElementSink.cs:10-107`, `:122-130`, `:133-138`, `:355`; writers in section 2
- **Finding:** `SinkState` is one flat class with 30 settable properties for six different producers (Dispatch, the node's `Take*`, the reconciler, the scene registry, the effect diff, `SceneCore`), in no order: Video fields sit between pointer fields and messages. A reader cannot tell which field the node may reset and which only Dispatch writes. `SinkSlot.State` grows the slot list on read (`Sink?[Index]`); the reconciler uses it to write a build error (`Reconciler.cs:254`), and the scene registry to write a warning (`SceneRegistry.cs:306`), creating slots as a side effect (also noted by REC). `TryGet` returns `null!` through a non-nullable `out`. `Resize` only shrinks. One `Warning` field is shared by three writers that compare and clear each other's text (REC-02 covers the fix).
- **Proposal:**
  - Keep one class (one allocation per slot; Dispatch writes fields directly), make it internal and order it in sections, each with a one-line comment: item state (Measured, SceneBounds, Media), pointer (Hover, Press, Active, Position), handler events (Tap, LongPress, Wheel, Drag, Touch, Pinch), value input, QML, messages (Warning, Error, ApplyError, BuildError).
  - Setters `internal`.
  - `TryGet(int, [NotNullWhen(true)] out FeedbackState? state)`.
  - Replace `SinkSlot.State` with `bool TryGetState(out FeedbackState? state)` (no growth) plus `FeedbackState GetOrAddState()` for the one writer that must create it.
  - `Resize` → `TrimTo(int count)`.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/ElementSink.cs`, `src/VL.Qt/Reconciler/Reconciler.cs`, `SceneRegistry.cs`, `EffectDiff.cs`, `src/VL.Qt/Nodes/ElementNode.cs` (Resize caller), `src/VL.Qt.Tests/ElementTests.cs`
- **Depends on:** ELM-06; REC-02 for the message store
- **Verification:** `ElementTests.Sink_SlotsGrowAndShrink` (adapted), `SinkRegistryTests`, `ReconcilerStylableTests`.

### ELM-08 Drop the record index offset, or stop it from growing the slot list
- **Kind:** dead-code, robustness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/SinkRegistry.cs:132-169`, `:250-341` (every `sink[index]`); `ElementSink.cs:122-130`; test `SinkRegistryTests.cs:490-503`
- **Finding:** A poll record's index is added to the slot index of the binding, up to `MaxIndexOffset` = 65,535. The doc says "QtNative sends 0" and offsets above 0 are "for items that serve several slots", and no such item exists. Every write uses the growing indexer, so one record with index 65,535 allocates 65,536 `SinkState` objects in the node's sink (the test `RecordIndex_IsOffsetToSlotIndex` asserts exactly that). Those slots stay until the node trims. The feature is unused generality with a memory cost on a malformed record.
- **Proposal (recommended):** Treat index ≠ 0 as invalid (ignored, counted in `Ignored`) and delete `MaxIndexOffset` and `Target`. Remove the field's meaning from `protocol.h` in the wire area (Q4). Alternative: keep the offset but write only existing slots (`TryGet`) when the offset is above 0.
- **Decided:** B5 → (a), and the field is removed from the protocol with the native break (P4 step 3, with PROTO-09), not only marked reserved: `MaxIndexOffset`, `Target` and `EventReader.Index` go
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/SinkRegistry.cs`, `src/VL.Qt.Tests/SinkRegistryTests.cs`; wire area: `protocol.h`/`Protocol.cs` comment
- **Depends on:** Q4 (only for the protocol side)
- **Verification:** adapted `SinkRegistryTests.RecordIndex_*` and `UnknownHandle_Index_Event_ShortPayload_Ignored`; SmokeTest `--all`.

### ELM-09 Take wire and reconciler code out of the element model
- **Kind:** architecture
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/Interaction.cs:41`, `:53-55`, `:110-112`, `:128-130`, `:142-144`, `:185-196`, `:221-228`, `:251-260`, `:85-95`, `:264-274`; `QmlEvents.cs` (whole file); `QmlProperties.cs:120`; `Sampler.cs:56-62`
- **Finding:** The element model is the public data the nodes output. Parts of it encode the wire format or call the reconciler:
  - every `Interaction` link writes its own SETs and hard-codes QtNative's defaults (`MovesElement` true, `DragAxis.Both`, `Occlusion` true, point count 2, buttons Left, devices All);
  - `QmlEvents` decodes EV_QML_* payloads (the way in from the wire, used only by `SinkRegistry.cs:328,332`);
  - `QmlProperties.IsValidName` calls `UniformWriter.IsValidName` in the reconciler folder;
  - `Sampler.Pack` lays out wire bytes.

  A contributor who adds an element property looks in two places for the diff (`PropertyDiff` for items, the link class for handlers). That is REC Q3.
- **Proposal:**
  - With REC Q3 A: move `WriteProperties` of all seven links, `PointerMasks` and `PointerProps` into `Reconciliation/HandlerPropertyDiff.cs` (`static void Write(ICommandSink, uint id, Interaction next, Interaction? prev, in SpaceMapping)`, a switch on the link type, defaults as named constants next to `protocol.h` values). `Interaction` keeps `HandlerType` (the CREATE id) only.
  - Move `QmlEvents` next to `EventReader.cs` as `QmlEventDecoder`.
  - Move the name check to `Protocol` (`Protocol.IsValidName(string?)`), used by both `UniformWriter` and `QmlProperties`.
  - Move `Sampler.Pack` to `UniformWriter` as `static ulong PackSampler(Sampler)`.
  - The enum values stay equal to protocol values (section 2).
- **Decided:** A3 → (a), central: `WriteProperties` of the seven links, `PointerMasks` and `PointerProps` move to `Reconciliation/HandlerPropertyDiff.cs`; S3 → `QmlEvents` becomes `Wire/QmlEventDecoder.cs`; the other moves hold; 'Depends on REC Q3' is answered
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Interaction.cs`, `QmlEvents.cs`, `QmlProperties.cs`, `Sampler.cs`; `src/VL.Qt/Reconciler/` (new `HandlerPropertyDiff.cs`, `UniformWriter.cs`, the handler reconcile call site); `src/VL.Qt/EventReader.cs` folder; `src/VL.Qt/Protocol.cs`; `src/VL.Qt.Tests/InteractionTests.cs`, `QmlTests.cs`, `SamplerTests.cs`
- **Depends on:** REC Q3; ELM-11 (file cut)
- **Verification:** `InteractionTests.*Properties_*`, `DefaultProperties_NotSent`, `QmlTests`, `SamplerTests`; SmokeTest `--all`.

### ELM-10 Move `Children` off `Element`: a container base and an internal child-item list
- **Kind:** structure
- **Severity:** should
- **Breaking:** yes: `Children` is no longer a property of `Element` (the imported node "Children (Element)", if present, moves to the container types).
- **Where:** `src/VL.Qt/Elements/Element.cs:54-70`; `Group.cs:9-16`; `Row.cs:8-15`; `Column.cs:8-15`; `ShaderEffect.cs:14-71`
- **Finding:**
  - Every element has a public `Children` with an `init` accessor that throws on leaves (`Element.cs:61-65`): a setter that exists to fail.
  - `ShaderEffect` uses the same property for something else: its hidden texture sources (Input plus texture uniforms), rebuilt in both `init` accessors. An initializer that sets `Input` and `Uniforms` builds the array twice, with an O(n²) `Contains`.
  - Group, Row and Column repeat the backing field (named `children`, against the `_camelCase` rule), `Normalize` and `AutoSize` word for word.
- **Proposal:**
  - `public abstract class Container : Element` with `public ImmutableArray<Element> Children { get; init; }` (normalised) and `public bool AutoSize { get; init; } = true`; Group, Row and Column derive from it.
  - `Element` gets `internal virtual ImmutableArray<Element> ChildItems => ImmutableArray<Element>.Empty`, the list of items the reconciler creates under this one. `Container` returns `Children`; `ShaderEffect` returns a list computed on first read and cached in a field (like `StylableElement.Empty`), with a linear duplicate check.
  - The reconciler reads `ChildItems`.
  - Delete `Element.Normalize` and the throwing accessor.
- **Decided:** C3, A6 → `Container` is internal like `Element` (not public as proposed); O5 → the `ShaderEffect` element is `Shader` (`Elements/Shaders/Shader.cs`), its `ChildItems` as proposed
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/Element.cs`, `Group.cs`, `Row.cs`, `Column.cs`, `ShaderEffect.cs`, new `Container.cs`; `src/VL.Qt/Reconciler/` (every `.Children` read of an element); `src/VL.Qt.Tests/ElementTypeTests.cs` (`Leaves_RejectChildren` deleted, `Containers_KeepChildren` kept)
- **Depends on:** none
- **Verification:** `ElementTypeTests`, `ReconcilerStructureTests`, `ShaderEffectTests`; help patches `HowTo Group`/`Row`/`Column` compile (`tools/vvvvc.ps1`).

### ELM-11 Cut `Effect.cs`, `Interaction.cs` and the other multi-type files; build the two chains the same way
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/Effect.cs` (11 types), `Interaction.cs` (14 types), `ElementSink.cs` (3), `SinkRegistry.cs` (2), `QmlProperties.cs` (2), `Text.cs` (5), `Sampler.cs` (4), `Alignment.cs` (3), `Image.cs`, `Video.cs`, `Slider.cs` (2 each)
- **Finding:**
  - `Effect.cs` holds the chain base, the shader link, the segment record, the kind flags, the folded description, the padding interface and five links.
  - `Interaction.cs` holds the base, seven links, three public enums, `DragLimits` and two static helpers.
  - The two chains do the same job, but each has its own pattern:

    | | Effect | Interaction |
    |---|---|---|
    | per-link behaviour | `Apply(EffectDesc)`, a data transformation | `WriteProperties(ICommandSink …)`, wire code |
    | shared pins | interface `IManualPadding` | static `PointerProps.Write` with a `writeButtons` flag |
    | identity | none (folded) | the sink slot |
    | duplicates | per kind per run, warning | per sink, warning in `SceneRegistry` |
    | walking the chain | `List` plus two reversals in `Segment` | in the reconciler |

    With ELM-09 both become pure data, and their only difference is the one that matters: effects fold, handlers keep an identity.
  - `PointerMasks` and `PointerProps` lack an explicit `internal` and `sealed`. `PointerButtons.None` behaviour is documented in a 220-character line.
- **Proposal:** One type per file in subfolders, layout in section 4. Shared pins as interfaces on both sides: rename `IManualPadding` → `IEffectPadding`; add `IPointerFilter { PointerButtons Buttons; PointerDevices Devices }` on Click, Point and Drag (Wheel, Touch Points and Pinch implement `Devices` only through `IPointerDevices`) so that `HandlerPropertyDiff` (ELM-09) writes them with one helper instead of the `writeButtons` flag.
- **Decided:** O5 → `ShaderLink` is `ShaderEffect : Effect` in `Effects/ShaderEffect.cs`, the element `ShaderEffect` is `Shader` in `Shaders/Shader.cs`; catalog → `Enums/` has no `FontWeight.cs`, `ImageFillMode.cs` and `VideoFillMode.cs` become `FillMode.cs`, and `BlendMode.cs`, `LabelPosition.cs`, `ToggleLook.cs` join it; A1 → the types of `ElementSink.cs` and `SinkRegistry.cs` carry the names of ELM-06 (`FeedbackRouter` leaves the folder); P14 → K3 is done by this issue in wave 4
- **Effort:** M, mechanical: yes
- **Touches:** all files listed under Where, new files per section 4
- **Depends on:** ELM-01; ELM-09 for the helpers that leave the folder
- **Verification:** build; unit tests unchanged.

### ELM-12 Bring the decision records and summaries in line with the code
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:**
  - `docs/decisions/element-catalog-v1.md:8,12-14`
  - `docs/decisions/events.md:15,23-27,33`
  - `src/VL.Qt/Elements/Interaction.cs:158-162` vs `:179`
  - `SinkRegistry.cs:82-83` vs `:91-108`
  - `ElementSink.cs:111`, `:147-148`
  - `Video.cs:14`
- **Finding:**
  - `element-catalog-v1.md` describes pins `Bounds`, `Pivot` (default Center) and `Visible`; the model has `Size`, `Anchor` (default `TopLeft`, `Element.cs:28`) and no `Visible`.
  - `events.md` lists "Sink as identity in the reconciler" as rejected, yet input handlers are keyed by their sink (`Interaction.cs:10`, `SinkRegistry.cs:28`). Its open questions list two-way values, drag, wheel and multitouch, all implemented (`TakeValue`, Drag, Wheel, Touch Points, Pinch).
  - `DragInteraction`'s summary says the limits are in logical pixels; its property says units, converted in `WriteProperties`, which is what the code does (`:193`).
  - `Release`'s doc promises to clear "the pending taps" but leaves `PendingClicks` pending (only `TapCount` and `PendingLongPresses` are reset).
  - `ElementSink` cites "plan 06" (a private plan).
  - `Video` begins "v1: … (no seek, no sync to vvvv)", a history note.
- **Proposal:**
  - `element-catalog-v1.md` "Geometry": `Size` and `Transform`, `Anchor` (default TopLeft, pivot of `Transform`), no `Visible`; keep the reasons.
  - `events.md`: "Identity: items by position and type; input handlers by their sink slot (one handler per sink)". Move the implemented open questions to "Design" in one line each (two-way values: EV_VALUE plus "take"; drag, wheel, touch, pinch: interaction nodes).
  - Drag summary: "Limits are in units of the renderer's space, ±infinity = none."
  - `Release`: "clears hover, press, active, tap count, long presses, wheel, touch points and the error; pending clicks stay until taken."
  - `ElementSink` summary without the plan reference; the one-frame latency sentence stays.
  - `Video`: "Plays a file with Qt's own clock; position and duration come back through the feedback, 0 until the media is loaded."
- **Decided:** G5 → the records are in `docs/design/` (`element-catalog-v1.md`, `events.md`); C2 → the geometry section says there is no `Visible` pin but an optional `Enabled` (default true, last input pin); A1 → the wording of `events.md` and of the `ElementSink` and `Release` docs says 'feedback slot'
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/element-catalog-v1.md`, `docs/decisions/events.md`, `src/VL.Qt/Elements/Interaction.cs`, `SinkRegistry.cs`, `ElementSink.cs`, `Video.cs`
- **Depends on:** none (wording follows ELM-06 if it lands first)
- **Verification:** read-through.

### ELM-13 Make `TouchPoint` a record struct
- **Kind:** performance, api
- **Severity:** could
- **Breaking:** yes: `TouchPoint` becomes a value type (no null; in vvvv, same pins).
- **Where:** `src/VL.Qt/Elements/TouchPoint.cs:12`; `SinkRegistry.cs:177-207`
- **Finding:** EV_TOUCH_POINTS is sent on change, which during a moving touch is every frame. Each event allocates one `TouchPoint` object per finger plus the spread. "Sent on change only, so allocating is fine" (`:177`) does not hold for a running gesture. A sealed record class with a `Vector2` also gets Stride's near-equal compare in its `Equals`.
- **Proposal:** `public readonly record struct TouchPoint(int Id, Vector2 Position, float Pressure, Vector2 Size)`. The spread still allocates once per event; that is acceptable and documented.
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Elements/TouchPoint.cs`, `SinkRegistry.cs` (comment), `src/VL.Qt/Nodes/Interaction/TouchPointsNode.cs` if it null-checks
- **Depends on:** none
- **Verification:** `SinkRegistryTests.TouchPointsEvent_*`, `InteractionTests.TouchPointsNode_*`; help patch `HowTo … Touch Points` compiles.

### ELM-14 `QmlProperties` keeps a stale problem text
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/QmlProperties.cs:122-137`
- **Finding:** `Matches` counts the entries it cannot send, but does not compare them. If an unsupported value changes to another unsupported value (a `double[]` to a `Matrix`, or a null to a string that is too long), the snapshot is kept and the node keeps showing the old reason ("type Double[] is not supported").
- **Proposal:** Keep the names of the refused entries with their reason (`string[]` of `name + "\0" + typeName`, or a second small dictionary name → `Type?`). `Matches` compares the type of each refused value with it; a different type gives a new snapshot. Still no allocation in the steady state.
- **Decided:** P22 → ELM-14 = QML-07, one issue
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Elements/QmlProperties.cs`, `src/VL.Qt.Tests/QmlTests.cs`
- **Depends on:** none
- **Verification:** new `QmlTests` case: unsupported → other unsupported type changes `Problems`.

## 4 Target structure

Namespace stays `VL.Qt.Elements` for every file below, also in the subfolders. It is the category of the imported pin types, and splitting it would scatter them in the node browser. Say so in one line in `docs/architecture.md`.

```
src/VL.Qt/Elements/
  Element.cs              public abstract Element: common description; private protected ctor (ELM-05);
                          Type, Feedback internal; internal virtual ChildItems (ELM-10)
  IElement.cs             unchanged
  Container.cs            NEW public abstract Container: Children + AutoSize (ELM-10)
  StylableElement.cs      unchanged (internal)
  ElementType.cs          internal (was public)
  Group.cs Row.cs Column.cs                     derive from Container
  Rectangle.cs Text.cs Image.cs Video.cs Shape.cs
  Button.cs CheckBox.cs Slider.cs SpinBox.cs Switch.cs TextField.cs   (public or internal: Q2)
  Enums/                  one public enum per file (split from Text.cs, Image.cs, Video.cs, Slider.cs, Alignment.cs):
    FontWeight.cs TextWrap.cs TextElide.cs TextAutofit.cs TextHorizontalAlignment.cs
    HorizontalAlignment.cs VerticalAlignment.cs ImageFillMode.cs VideoFillMode.cs SliderOrientation.cs
    ShapeCap.cs ShapeJoin.cs ShapeFillRule.cs              (moved, unchanged)
  Effects/                (split from Effect.cs)
    Effect.cs             public abstract Effect: chain base, Segment (Fold deleted, ELM-01)
    ShaderLink.cs         internal abstract: filter shader link
    EffectChainLink.cs    internal record struct: one SET_EFFECT_CHAIN link
    EffectKind.cs         internal flags
    EffectDesc.cs         internal record struct: folded MultiEffect values
    IEffectPadding.cs     was IManualPadding
    BlurEffect.cs DropShadowEffect.cs ColorAdjustEffect.cs ColorizeEffect.cs MaskEffect.cs
  Interactions/           (split from Interaction.cs)
    Interaction.cs        public abstract Interaction: chain base, HandlerType, Feedback (WriteProperties gone, ELM-09)
    HoverInteraction.cs ClickInteraction.cs PointInteraction.cs WheelInteraction.cs
    DragInteraction.cs DragLimits.cs TouchPointsInteraction.cs PinchInteraction.cs
    PointerButtons.cs PointerDevices.cs DragAxis.cs IPointerFilter.cs (new, ELM-11)
    TouchPoint.cs         moved here; readonly record struct (ELM-13)
  Feedback/               (split from ElementSink.cs, renamed per ELM-06)
    ElementFeedback.cs    was ElementSink: per node instance, read API (internal)
    FeedbackState.cs      was SinkState: values of one slot, sections, internal setters (ELM-07)
    FeedbackSlot.cs       was SinkSlot: (feedback, index), TryGetState
  Shaders/
    ShaderEffect.cs Uniform.cs
    Sampler.cs SamplerFilter.cs SamplerMipFilter.cs SamplerAddress.cs   (Pack moved out, ELM-09)
  Qml/
    QmlElement.cs QmlSignal.cs
    QmlProperties.cs      QmlProperties only
    QmlValue.cs           split from QmlProperties.cs

moved out of the folder:
  SinkRegistry.cs   → src/VL.Qt/Reconciliation/FeedbackRouter.cs (owned by the reconciler, one per window)
                      + DispatchResult.cs next to it
  QmlEvents.cs      → next to src/VL.Qt/EventReader.cs as QmlEventDecoder.cs (ELM-09)
  Interaction.WriteProperties, PointerMasks, PointerProps
                    → src/VL.Qt/Reconciliation/HandlerPropertyDiff.cs (ELM-09, REC Q3 A)
deleted:
  Alignment.cs      (content in Enums/)
  Effect.Fold, SinkRegistry.Clear/TryResolve/IsPrimary (ELM-01, ELM-03)
```

Public after the refactoring: `Element`, `Container`, `IElement`, `Effect`, `Interaction`, `Sampler`, `Uniform`, `TouchPoint`, `QmlSignal`, the enums in `Enums/`, `Interactions/` and `Shaders/`, and, depending on Q2, the concrete element classes. Everything else is internal.

## 5 Questions for Sebastian

**Q1 Name of the way back from Qt.** Today: `ElementSink`, `SinkState`, `SinkSlot`, `SinkRegistry`, while `ICommandSink` is the way to Qt.
- **A: "feedback"** (`ElementFeedback`, `FeedbackState`, `FeedbackSlot`, `FeedbackRouter`, `Element.Feedback`). It is neutral English, does not collide with VL's notifications (input into Skia layers), and makes the direction clear.
- **B: keep "sink"**, rename `ICommandSink` → `ICommandTarget`. This is a smaller diff, but "sink" stays a term the reader has to learn.
- Recommendation: **A**. It touches the reconciler and the nodes, so decide it before their refactoring issues are cut.

**Q2 Should the concrete element classes (Rectangle, Text, …) stay public?** No pin uses them; every node outputs `Element`.
- **A: public, with `[Smell(SymbolSmell.Hidden)]` on their constructors** (if vvvv imports them as Create nodes). C# authors of other packages can build and inspect elements, and the tooltip of an `Element` pin shows the concrete type and its properties.
- **B: internal.** This gives the smallest public surface and the freedom to change them later. C# users of the library can then only build elements through the nodes.
- Recommendation: **A**, because the library will be public and building descriptions in C# is a natural use. Check in the node browser what `ImportAsIs` shows today (`manual-check`).

**Q3 Hover and press of an element shown at several places.** `events.md` says OR, the code does last-write-wins (ELM-02).
- **A: implement OR** (per-handle bits, counts per slot).
- **B: change the decision** to "the last event wins".
- Recommendation: **A**. It is small, and the decision was taken for a reason (a Button shown twice).

**Q4 The index field of poll records.** C# adds it to the slot index; QtNative sends 0 (ELM-08).
- **A: drop its meaning.** C# ignores records with index ≠ 0, and `protocol.h` marks the field as reserved.
- **B: keep it for a future item that serves several slots**, but never grow the slot list from it.
- Recommendation: **A**. Nothing uses it, and a protocol change is allowed in this release.

## 6 For other areas

- **Reconciler:** `UniformValue` equality compares Stride `Matrix` values (`src/VL.Qt/Reconciler/UniformWriter.cs:13`, `:100`). If Stride's `Matrix.Equals` is a near-equal compare, as `QmlProperties.cs:10` says it is for vectors, tiny uniform changes are never sent. Confirm in `Stride.Core.Mathematics.Matrix.Equals`.
- **Reconciler:** Check that renderer dispose releases every feedback handle (`SinkRegistry.Clear` is unused, ELM-03).
- **API (01):** Check what `[assembly: ImportAsIs]` (`src/VL.Qt/Renderer.cs:14`) imports from `VL.Qt.Elements` (public constructors, `ElementSink.TakeClicks`, `Video.ClampRate`, `Shape.ExtentOf`, `Element.Children`). Also check whether the type name `Effect` collides with Stride's `Effect` in patches that reference VL.Qt.Stride.
- **Wire format:** The index field of poll records is unused (ELM-08, Q4). BIND's `sinkHandle` is renamed with ELM-06.
- **Nodes:** `ElementNode.Resize` caller → `TrimTo` (ELM-07). `TouchPointsNode` null checks, if any, go with ELM-13.
- **Docs:** `docs/architecture.md` should state that the element subfolders keep the namespace `VL.Qt.Elements` (section 4).
