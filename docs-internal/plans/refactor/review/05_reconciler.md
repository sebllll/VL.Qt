# Review 05 – Reconciler

Claude Opus 5.5, 2026-11-19. Files read in full: all ten files in `src/VL.Qt/Reconciler/` (Reconciler.cs, PropertyDiff.cs, EffectDiff.cs, SceneRegistry.cs, Node.cs, ICommandSink.cs, IdAllocator.cs, ResyncController.cs, QmlPropertyWriter.cs, UniformWriter.cs), `docs/decisions/reconciler-identity.md`, `src/VL.Qt/Elements/SinkRegistry.cs`, `src/VL.Qt.Tests/ReconcilerPerfTests.cs`. Skimmed: `docs/architecture.md` (sections "Scene graph and data flow", "Reusing elements", "Performance"), `src/VL.Qt/Elements/ElementSink.cs` (SinkState/ElementSink/SinkSlot parts), `src/VL.Qt/Elements/Element.cs` (members only), `src/VL.Qt/SceneCore.cs:100-190`, `src/VL.Qt/CommandWriter.cs` (throws and `SetString`), `src/VL.Qt/Elements/QmlProperties.cs` (enumerator). Not read: the code-style skill files, the other test files, `StyleMerge`/`StyleWriter`, `Effect.Segment`, `Interaction.cs` beyond the `WriteProperties` signatures.

Tests that cover the area (names only): `src/VL.Qt.Tests/` ReconcilerBaseTests, ReconcilerPerfTests, ReconcilerPropertyTests, ReconcilerSceneTests, ReconcilerSpaceTests, ReconcilerStructureTests, ReconcilerStylableTests, ReconcilerStyleTests, EffectChainReconcilerTests, EffectTests, ShaderEffectTests, InteractionTests, QmlTests, ResyncTests, SceneRegistrySlotTests, BaseStyleTests; `src/VL.Qt.SmokeTest/` ReconcilerTest, MultiPlacementTest, MultiRendererTest, ResyncTest, VideoTest, StyleTest, StylableTest, EffectChainTest.

## 1 Summary

The reconciler is correct in its main path and fast: a reference-equal subtree costs one comparison, the common path allocates nothing and the perf test enforces it for three simple trees. What makes it hard for the reader is that one `partial class` of about 1,700 lines across three files holds seven jobs (tree walk, property diff, effect chain, input handlers, primary occurrence across renderers, video exclusivity, style and self-made widgets). It shares about a dozen mutable fields, and two class summaries of 60 and 45 lines carry the design.

The changes that matter most:

1. **Exception safety (REC-01, must).** A Text longer than about 64 KB of UTF-8, or one large QML string property, throws inside `Update`. From then on every frame leaks ids, orphan Qt objects and registry entries, and partial batches are applied.
2. **Messages in the sink slot (REC-02, must).** A changed fold warning is never shown, and the stale one stays forever. Video, interaction, effect and build messages overwrite or hide each other.
3. **Split the partial class and the registry (REC-03, REC-04, REC-06, REC-07).** The design goes to `docs/topics/reconciler.md`. The type split is PropertyDiff, HandlerReconciler, PrimaryOccurrences, EffectChainWriter, OccurrenceRegistry, InteractionRegistry and ItemMirror. The vocabulary becomes one word per concept: placeholder, primary, owner renderer.
4. **Allocations on animated content (REC-08, REC-09, REC-10).** These happen on ShaderEffect uniforms, on Effect chains and on elements shown in two renderers. The perf tests do not cover any of them.
5. **Question Q1:** the registry identifies an element by its instance, but a node outputs a new instance on every input change. In practice the "owner renderer = first that showed it" rule becomes "first to update after a change". This also forces per-frame churn and the slot fallback of `TryLocate`.

## 2 What works and must survive

- **Identity = position + type, reference-equality skip** (`Reconciler.cs:273-280`). The skip test has five conditions: same reference, not forced, not `Dirty`, no self-made widget inside, and inherited style reference-equal. Each condition is load-bearing. The equal-style-keeps-reference trick in `ResolveStyle` (`Reconciler.cs:345-354`) is what lets unchanged children stay skipped under a re-merged parent style.
- **Children only change at the tail** (`Reconciler.cs:366-403`), and nodes never change parent (`Node.cs:55`, `init`). Together these give the invariant that the traversal order of existing nodes is stable. `Register` relies on it: "the current primary was not visited yet in this pass, so it comes later" (`Reconciler.cs:451`). One exception exists: `ChangeEffectChain` rebuilds `EffectObjects` in the new chain order (`EffectDiff.cs:59-63`), so effect objects can change order. The only effect is which mask occurrence reports state, which is harmless today. Write the exception down when you write the invariant down.
- **The REPARENT index counts only real objects** (`live`, `Reconciler.cs:373`). DESTROY goes only to the topmost object, and the whole subtree's ids are freed (`Reconciler.cs:631-674`). QtNative relies on both.
- **A new sink handle on every slot change, never reused** (`Reconciler.cs:620-625`, `SinkRegistry.cs:55-66`). Late events of an old binding are dropped. A promotion to primary takes a new handle and sends BIND, so QtNative sends the state again.
- **Decisions at the end of the update** (`ResolvePrimaries`, `Reconciler.cs:487-511`). Moving an element inside the owner's tree does not hand it to another renderer.
- **`IdAllocator.Free` throws on a double free or an unknown id** (`IdAllocator.cs:54-61`). This is the only invariant of the area that the code enforces. Keep it.
- **Hot paths (must stay allocation-free):**
  - `Reconciler.Update` → `ReconcileElement` (skip test, `ResolveStyle` with equal references, `DiffProperties`, `BindSink` with an unchanged slot, `ReconcileHandlers`/`ReconcileEffect` skipped by reference, `ReconcileChildren`).
  - `SceneRegistry.AddOccurrence`/`RemoveOccurrence` for a new instance in one renderer (dictionary remove and add, pooled lists).
  - `ResolvePrimaries` with an empty list.
  - `DiffShape` (reused `_shapeBuffer`).
  - `QmlPropertyWriter.Write` (struct enumerator of `QmlProperties`).
  - `SinkRegistry.Dispatch` (neighbour).
- **The element model's immutability** (`docs/topics/element-immutability.md`) is the precondition of the whole design. Self-made widgets (`IStylable`, mutable) are the one exception, and they are handled by never skipping their ancestors.

## 3 Findings

### REC-01 Make `Reconciler.Update` exception-safe and stop oversized values from throwing
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no. A Text longer than the limit is cut and gets a warning instead of breaking the renderer.
- **Where:** `src/VL.Qt/Reconciler/PropertyDiff.cs:334`, `:336`, `:364`, `:437`, `:439`, `:470`, `:133`; `src/VL.Qt/Reconciler/QmlPropertyWriter.cs:28-39`; `src/VL.Qt/CommandWriter.cs:171-177`, `:602`; `src/VL.Qt/Reconciler/Reconciler.cs:160`, `:297`, `:303`, `:313-315`, `:442-456`; `src/VL.Qt/Reconciler/SceneRegistry.cs:166-175`; `src/VL.Qt/SceneCore.cs:102-108`
- **Finding:**
  - **Where it throws.** `CommandWriter.SetString` throws `ArgumentOutOfRangeException` when a SET payload exceeds 65,535 bytes (`CommandWriter.cs:602`). `DiffText` writes `Content` without any limit (`PropertyDiff.cs:334`). Button text, TextField value and placeholder, Video path and QML source do the same. `QmlPropertyWriter` splits a table into chunks, but one entry larger than the payload still ends in an over-long command, which throws in `EndQmlProperties`. Shape points and paths are already clamped (`PropertyDiff.cs:241`, `:265-287`), so the risk is known, but only for floats.
  - **Nothing catches it.** `SceneCore` does not catch (`SceneCore.cs:105`), and `Apply()` then sends the partial batch of that frame.
  - **The mirror is left half-updated.** `existing.Last = next` is skipped (`Reconciler.cs:303`), but `Register(next, existing)` already ran (`:293`). On the next frame `same` is false again, `Release(oldLast)` is a no-op, and `Register(next, existing)` adds the same node a second time. `SceneRegistry.AddOccurrence` finds the entry with `Primary == node`, so the node goes into `Others` (`SceneRegistry.cs:169-175`). The node is then demoted (`IsPrimary = false`) although it is the registry's primary, and its handle stops reporting state. `Others` grows by one entry every frame.
  - **A newly created node leaks every frame.** If the throw happens at a new node, the id is allocated, CREATE is written and the occurrence is registered (`Reconciler.cs:313-315`), but the node never reaches the parent's `Children`. Every frame then leaks one id, one QtNative object (CREATE without REPARENT) and one registry occurrence.
  - **Not verified:** whether QtNative shows such an orphan item. A test with a throwing sink would confirm the rest.
- **Proposal:**
  1. User data must not throw. Add `Protocol.MaxStringBytes = CommandWriter.MaxPayload - Protocol.SetFieldsSize - 4`. In `PropertyDiff`, cut string values at a UTF-8 character boundary to that length through one helper `WriteString(uint id, ushort prop, string value, SinkSlot slot)`, which also writes a warning into the slot (REC-02 mechanism). In `QmlPropertyWriter`, skip a single entry larger than a chunk and warn.
  2. Add a safety net. In `SceneCore.Update`, catch an exception of `_reconciler.Update`, call `_commands.Reset()`, then run the resync path: a new `ResyncController.OnReconcileFailed()` that does what `OnApplyResult(rc != 0)` does (RESET, `Discard`, give-up rule). Report the exception text at the renderer's `Error` output. This keeps mirror and QtNative consistent whatever throws.
  3. Write the invariant "`Node.Last` and the registry change only together, at the end of a visit" into the design page (REC-03).
- **Decided:** A4, P8, P4 step 4 → with the `u32` length the 64 KB limit goes, so no `Protocol.MaxStringBytes`, no cut at a UTF-8 boundary in `PropertyDiff` and no entry skipping in `QmlPropertyWriter`; part 1 of the proposal holds only as the fallback when the P8 comparison fails twice (`u32` for events only, so commands stay `u16`, then `u16` as today); B1 → part 2 as proposed (reset, resync), but the exception goes to the log and marks the Renderer node instead of an `Error` output (DECISIONS: no `Error` output pins); question 0.2 → wave 1 confirms the leak with the throwing-sink test before this issue is cut; A1 → `SinkSlot` is `FeedbackSlot`; B7 of [DECISION-ROUND.md](../DECISION-ROUND.md) → a value above `VLQT_MAX_COMMAND_PAYLOAD` is a failed reconcile as in B1 of QUESTIONS.md (log, mark the Renderer, resync); no truncation code
- **Wave 1 (W1-14):** confirmed by `Fault_AtANewElement_NextUpdate_LeavesNoOrphanObject` and `Fault_AtSetOfAChangedElement_NextUpdate_…` (W1-02: double registration, demoted node, new node never reaches its parent) and by `OversizedString_WithARealCommandWriter_DoesNotThrowOutOfUpdate` (a Text of 100,000 characters throws `ArgumentOutOfRangeException` from `CommandWriter.Begin` out of `Reconciler.Update`).
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/PropertyDiff.cs`, `src/VL.Qt/Reconciler/QmlPropertyWriter.cs`, `src/VL.Qt/Reconciler/ResyncController.cs`, `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/Protocol.cs`, new tests in `src/VL.Qt.Tests/ResyncTests.cs` and `ReconcilerPropertyTests.cs`
- **Depends on:** REC-02 (for the warning)
- **Verification:**
  - A new unit test: a Text with 100,000 characters is reconciled with a real `CommandWriter`. There is no exception, the content is cut, and the slot warning is set.
  - A new test with a sink that throws at the n-th command: after the safety net, `IdAllocator.Count == 0` and `SceneRegistry.Count == 0` before the rebuild, and the rebuild succeeds.
  - SmokeTest `ResyncTest`.

### REC-02 One message channel per sink slot that keeps every writer's message
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no. Warnings that were lost become visible.
- **Where:** `src/VL.Qt/Reconciler/EffectDiff.cs:293-305`; `src/VL.Qt/Reconciler/Reconciler.cs:237`, `:245-259`, `:642`; `src/VL.Qt/Reconciler/SceneRegistry.cs:302-324`, `:389-410`; `src/VL.Qt/Elements/ElementSink.cs:91`, `:106`
- **Finding:** `SinkState.Warning` is one string with three writers: the effect fold warning, the video duplicate and the interaction duplicate. `SinkState.BuildError` is one string for all siblings under a parent. Each writer sets the string "if empty" and clears it "if it is mine". That loses messages:
  - **Stale fold warning (a plain bug).** In `SetEffectWarning`, a change from warning A to warning B leaves `state.Warning == A`: neither branch applies (`EffectDiff.cs:299-302`). `node.EffectWarning` becomes B. When the chain is fixed later, A is not cleared either, because `A != node.EffectWarning`. The stale warning stays until the element goes away. `SetBuildError` handles the same case correctly (`Reconciler.cs:254`), so the two helpers already differ.
  - **Hidden fold warning.** A video duplicate writes over an effect warning unconditionally (`SceneRegistry.cs:307`). When the duplicate is cleared, the effect warning does not come back: `SetEffectWarning` returns early because the text did not change.
  - **Hidden build error.** The build error of a second sibling is never shown after the first sibling's error clears (early return at `Reconciler.cs:247`).
  - **Build error of the window root.** A self-made widget used as the window root has no parent slot (`parent?.Sink ?? default`, `Reconciler.cs:237`), so its build error goes nowhere.
- **Proposal:**
  - In `SinkState`, replace the writable `Warning` and `BuildError` setters with `void SetMessage(object source, MessageKind kind, string? text)`. `MessageKind` is `Warning` or `BuildError`, and the store is a small list of `(source, kind, text)`, allocated only when used. `Warning` and `BuildError` become getters that return the first entry of their kind, or all entries joined.
  - Writers pass a stable source: the `Node` for effect and build messages, and the constants `SceneRegistry.VideoDuplicate` and `InteractionDuplicate` (objects, not the strings) for the registry.
  - `SetEffectWarning` and `SetBuildError` then shrink to one call each.
  - Build errors of a root widget go to the renderer's `Error` output. `Reconciler.Update` returns them, or exposes `RootBuildError`.
- **Decided:** A1 → `SinkState` is `FeedbackState`, `SinkSlot` is `FeedbackSlot`; DECISIONS 'No `Error` output pins' → the build error of a root widget goes to the log and marks the Renderer node, not an `Error` output
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Elements/ElementSink.cs` (element-model area, coordinate), `src/VL.Qt/Reconciler/EffectDiff.cs`, `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/SceneRegistry.cs`, `src/VL.Qt/SceneCore.cs`, tests that set `Warning` directly (`src/VL.Qt.Tests/RectangleNodeTests.cs:149-159`, `SinkRegistryTests.cs:315-317`)
- **Depends on:** none
- **Verification:**
  - A new test in `EffectChainReconcilerTests`: a chain with fold warning A, then B, then none. The slot shows A, then B, then nothing.
  - A new test: a video duplicate plus an effect warning on the same slot, with the duplicate then removed. The effect warning is shown again.
  - Existing `ReconcilerStylableTests`.

### REC-03 Move the design out of the class summaries into `docs/topics/reconciler.md`
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/Reconciler.cs:9-73`, `src/VL.Qt/Reconciler/SceneRegistry.cs:10-46`, `src/VL.Qt/Reconciler/PropertyDiff.cs:11-21`, `src/VL.Qt/Reconciler/EffectDiff.cs:11-17`, `src/VL.Qt/Reconciler/ResyncController.cs:6-18`
- **Finding:** The Reconciler summary has eight paragraphs and covers identity, occurrences, renderers, video, binding, handlers, command order, styles, self-made widgets and threading. A reader who opens the file to fix a diff reads all of it first, and none of it is close to the code it explains. The SceneRegistry summary repeats half of it. The design page `docs/decisions/reconciler-identity.md` covers only identity and occurrences. Comments also carry plan-package tags that a newcomer cannot resolve: "Hook for the property diff (04c)" `Reconciler.cs:810`, "see 03c" `PropertyDiff.cs:15`, "(04d)" `Node.cs:23`, "plan effects/08" `EffectDiff.cs:12`, "since #319 … since #320" `Reconciler.cs:595-596`, "fakes that predate #104" `ICommandSink.cs:107`. K1 removes the issue numbers; the plan tags go the same way.
- **Proposal:**
  - **New page `docs/topics/reconciler.md`**, linked from `docs/architecture.md` "Scene graph and data flow" and from `docs/README.md`. Sections:
    1. One frame: Update → commands → apply. Who calls it, the thread, and when the commands are applied.
    2. Identity and the skip test: link to `decisions/reconciler-identity.md`, plus the five skip conditions.
    3. Command order per object and the REPARENT index.
    4. Occurrences and the primary: traversal order, owner renderer, hand-over, as a mermaid sequence of two renderers.
    5. Placeholders: a Video shown once, and an interaction active once.
    6. Input handlers as a mirror next to the children.
    7. Effect chain and its attached objects.
    8. Styles: resolution and how it interacts with the skip.
    9. Self-made widgets: Build on every pass.
    10. Errors and resync.
    11. Invariants (§2 of this report, plus REC-01).
    12. Allocation contract and hot paths.
  - **What stays in the code:**
    - Per type: a summary of at most five lines saying what it is, who owns it, the thread, and a link to the page section.
    - Per method: one sentence of contract.
    - Short comments at the line that relies on an invariant:
      - the skip test, one line per condition;
      - `live` = REPARENT index;
      - `attached` = no REPARENT;
      - the end-of-update decision in `ResolvePrimaries`;
      - "Last and registry change together" (REC-01).
- **Decided:** G5 → the decision records are in `docs/design/`, so the link target is `docs/design/reconciler-identity.md`, rewritten for the slot key of A2; P14 → the bare `#NNN` go in wave 3, the comments that need a new sentence (the plan tags 04c, 03c, effects/08) are rewritten by the issue of REC-04 in wave 4; the page uses the glossary words (`ItemMirror`, `FeedbackRouter`, `OccurrenceRegistry`, 'element tree', never 'scene' for the tree of the patch, O2); C14 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the page is a design note: `docs/design/reconciler.md`, not `docs/topics/`
- **Effort:** M, mechanical: no
- **Touches:** `docs/topics/reconciler.md` (new), `docs/architecture.md`, `docs/README.md`, `docs/decisions/reconciler-identity.md` (link only), all files in `src/VL.Qt/Reconciler/`
- **Depends on:** best done together with REC-04 and REC-06, so that the comments are written once against the new types
- **Verification:** Review by a person who did not write the code. `dotnet build src/VL.Qt` (XML doc `cref`s still resolve).

### REC-04 Replace the partial class by types with one job each
- **Kind:** structure
- **Severity:** should
- **Breaking:** no (internal)
- **Where:** `src/VL.Qt/Reconciler/Reconciler.cs:74-88`, `:405-592` (occurrences), `:594-628` (binding), `:676-807` (handlers), `:809-821` (partial hooks); `src/VL.Qt/Reconciler/PropertyDiff.cs:22-556`; `src/VL.Qt/Reconciler/EffectDiff.cs:18-306`, `:309-313`
- **Finding:**
  - **Hidden coupling.** `Reconciler` is `partial` over three files. Every part sees every field (`_force`, `_rescale`, `_pass`, `_mapping`, `_sink`, `_ids`, `_scene`, `_sinks`, `_bindings`, `_pendingPrimary`, `_shapeBuffer`). A reader cannot tell what the property diff depends on: in fact only `_sink`, `_mapping` and `_rescale`.
  - **Leftovers of the plan packages.** `DiffProperties` and `ChildrenReconciled` are `partial void` "hooks" (`Reconciler.cs:815`, `:821`). They are not optional; `ChildrenReconciled` contains a stray brace block (`PropertyDiff.cs:535-547`).
  - **Two jobs in one class.** Occurrence bookkeeping (`CanOwn`, `Register`, `Release`, `Demote`, `ResolvePrimaries`, `SyncPrimary`, `IsBefore`, `Depth`, `Position`, `IsVisitedThisPass`, `Ignore`) is about 190 lines inside the tree walk.
  - **Folder and namespace disagree.** The folder is `Reconciler/` but the namespace is `VL.Qt`.
  - **Two types in one file.** `EffectPadding` lives in EffectDiff.cs (K3).
- **Proposal** (the target layout is in §4):
  - `PropertyDiff`, a `sealed class`, owns the default instances and the shape buffer. Constructor `(ICommandSink sink)`, method `Write(uint id, Element? previous, Element next, in SpaceMapping mapping, bool rescale)`, plus `WriteShaderEffectSource(...)` for today's `ChildrenReconciled`. The reconciler calls it directly. The `partial` keyword is removed.
  - `HandlerReconciler`, a `sealed class`, does the handler diff. Constructor `(ICommandSink, IdAllocator, SinkRegistry, InteractionRegistry, Dictionary<uint, SinkSlot> bindings)`, methods `Reconcile(ItemMirror item, Interaction? previous, Interaction? next, in PassInfo pass)` and `Forget(ItemMirror item)`.
  - `PrimaryOccurrences`, a `sealed class` with one instance per reconciler: `Register`, `Release`, `TryClaimExclusive` (today's `CanOwn`), `CreatePlaceholder`, `ResolveAtEndOfUpdate`. It holds `_pendingPrimary` and talks to `OccurrenceRegistry`.
  - `EffectChainWriter`, a `static class`: `Send(ICommandSink, EffectChainMirror, in SpaceMapping)` with `WriteMultiLink`/`WriteShaderLink`. Reconciling the effect objects stays in `Reconciler`, because it recurses into `Reconcile`.
  - Per-pass values go in one `readonly struct PassInfo(int Pass, bool Force, bool Rescale, SpaceMapping Mapping)`, passed down instead of read from fields.
  - The folder and namespace become `Reconciliation/` and `VL.Qt.Reconciliation`. A namespace named `Reconciler` would clash with the class.
- **Decided:** A3 → (a), central: all diffing of elements and of input handlers lives in `Reconciliation/`, `Interaction.WriteProperties` moves there as `HandlerPropertyDiff.cs` (ELM-09); O3 → `Node` and `HandlerNode` are `ItemMirror` and `HandlerMirror`, `EffectChainMirror` as proposed, and the mirror of the `ShaderEffect` element is `ShaderMirror` (O5: the element is `Shader`, so the method for today's `ChildrenReconciled` names the element `Shader`); A1 → `SinkRegistry` is `FeedbackRouter` (`Reconciliation/FeedbackRouter.cs`, with `DispatchResult.cs`) and `SinkSlot` is `FeedbackSlot`; S3 and TARGET 2.7 → `ICommandSink.cs` goes to `Wire/` (not next to the reconciler), `ResyncController.cs` to `Scene/`; S1 → folder `Reconciliation/`, namespace `VL.Qt.Reconciliation`; P14 → K3 (one type per file) is done by this issue in wave 4
- **Effort:** L, mechanical: no
- **Touches:** all files in `src/VL.Qt/Reconciler/`, `using` lines in `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/WindowRenderer.cs`, `src/VL.Qt/OffscreenRenderer.cs`, `src/VL.Qt/Nodes/SnapshotNode.cs`, `src/VL.Qt/Nodes/FileWriterElementNode.cs`, `src/VL.Qt/Nodes/ElementTooltipPreview.cs`, `src/VL.Qt/Styles/StyleWriter.cs`, `src/VL.Qt/Elements/Interaction.cs`, `src/VL.Qt/Elements/SinkRegistry.cs`, tests and SmokeTest files that construct `Reconciler` (about 60, listed by `rg "new Reconciler\("`)
- **Depends on:** REC-05, REC-06, REC-07 (do the renames in the same pass)
- **Verification:** `tools/build.ps1 -Test`. All Reconciler*, Interaction*, EffectChain*, ShaderEffect*, Qml* unit tests unchanged and green. `ReconcilerPerfTests` still at 0 bytes.

### REC-05 Remove the unused `parent` parameter of the property diff
- **Kind:** dead-code
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/PropertyDiff.cs:61`; `src/VL.Qt/Reconciler/Reconciler.cs:297`, `:317`, `:809-815`; `src/VL.Qt/Reconciler/Reconciler.cs:203`, `:220`, `:262`, `:378`, `:395`; `src/VL.Qt/Reconciler/EffectDiff.cs:61`, `:89`
- **Finding:** `DiffProperties(Node, Element?, Element next, Element? parent)` never reads `parent`. Its doc says the parameter exists "to treat the anchor only as pivot for children of Row/Column". `DiffCommon` says the opposite: everything is sent as it is and QtNative decides (`PropertyDiff.cs:141-142`). The only other use of the `Element? parent` threaded through `Reconcile`/`ReconcileStylable`/`ReconcileElement` is `parent?.Sink` for the build error (`Reconciler.cs:237`).
- **Proposal:** Drop the parameter from the diff. Replace `Element? parent` in the three `Reconcile*` signatures by `SinkSlot parentSink`, which is what is used. Bundle the remaining position arguments into `readonly struct Position(ItemMirror? Parent, SinkSlot ParentSink, int Index, Style Inherited, bool Attached)`.
- **Decided:** A1 → the slot type of `Position` and of the `Reconcile*` signatures is `FeedbackSlot`; TARGET 2.2 → `Position.cs` and `PassInfo.cs` live in `Reconciliation/`
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/PropertyDiff.cs`, `src/VL.Qt/Reconciler/EffectDiff.cs`
- **Depends on:** none
- **Verification:** The unit tests `ReconcilerPropertyTests`, `ReconcilerStylableTests`.

### REC-06 Split `SceneRegistry` and use one word per concept
- **Kind:** naming
- **Severity:** should
- **Breaking:** no (internal)
- **Where:** `src/VL.Qt/Reconciler/SceneRegistry.cs:47-411`; `src/VL.Qt/Reconciler/Reconciler.cs:412-432`, `:587-592`; `src/VL.Qt/Reconciler/Node.cs:28`, `:144`
- **Finding:**
  - **Three registries in one class.**
    1. Element occurrences: owner renderer, primary, hand-over, plus `TryLocate` for Snapshot and the slot fallback.
    2. Video placeholders (`Waiting`).
    3. Interaction handler ownership: a second dictionary with its own entry struct and warning (`SceneRegistry.cs:344-410`), whose rules differ ("the current owner always keeps it").
  - **Several words for one concept.** The same thing is called "ignored" (`Node.IsIgnored`, `Ignore()`), "duplicate" (`AddDuplicate`, `DuplicatesOf`), "waiting" (`Entry.Waiting`) and "placeholder" (all doc comments).
  - **"Owner" means two things.** `OwnerOf(element)` returns the primary node (`:93-105`), while "owner" everywhere else is the owner renderer (`OwnerReconcilerOf`).
  - **A predicate with side effects.** `CanOwn` reads like a check, but it marks nodes dirty and removes another node's occurrence (`Reconciler.cs:424-426`).
  - **Stray character.** `SceneRegistry.cs:10` starts with a U+FEFF in the middle of the file.
- **Proposal:**
  - **`OccurrenceRegistry`** (app service, `ForApp`) holds element entries, the hand-over, placeholder waiting lists for exclusive elements, and `TryLocate`. Renames:
    - `OwnerOf` → `PrimaryOf`
    - `OwnerReconcilerOf` → `OwnerOf`
    - `OthersOf` → `FurtherOccurrencesOf`
    - `AddDuplicate`/`RemoveDuplicate` → `AddPlaceholder`/`RemovePlaceholder`
    - `DuplicatesOf` → `PlaceholdersOf`
    - `IsExclusive` → `IsShownOnce`
  - **`InteractionRegistry`** (app service, `ForApp`) holds handler ownership and handler placeholders. Renames:
    - `HandlerOwnerOf` → `ActiveHandlerOf`
    - `SetHandlerOwner` → `Activate`
    - `ReleaseHandler` → `Deactivate`
  - `Node.IsIgnored`/`HandlerNode.IsIgnored` → `IsPlaceholder`, and `Ignore()` → `CreatePlaceholder()`.
  - `CanOwn` becomes `TryClaimShownOnce(element, self)`, documented as "may take the element over from a later occurrence".
  - Remove the stray U+FEFF.
- **Decided:** A2 → the key is the feedback slot when the element has one, the instance otherwise, done in this issue; the `_shownBySlot` fallback goes and 'owner = first renderer that showed it' becomes true; O2 → `OccurrenceRegistry` and `InteractionRegistry` as proposed (the registry loses the word 'scene'); O3 → `IsIgnored` becomes `IsPlaceholder` on `ItemMirror` and `HandlerMirror`; A1 → `SinkSlot` is `FeedbackSlot`; glossary 1.2 'occurrence' → `PrimaryOf`, `IsPlaceholder`
- **Effort:** M, mechanical: partly (renames yes, the split no)
- **Touches:** `src/VL.Qt/Reconciler/SceneRegistry.cs` → `OccurrenceRegistry.cs` + `InteractionRegistry.cs`, `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/Node.cs`, `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/WindowRenderer.cs`, `src/VL.Qt/OffscreenRenderer.cs`, `src/VL.Qt/Renderer.cs`, `src/VL.Qt/Nodes/SnapshotNode.cs`, `src/VL.Qt/Nodes/FileWriterElementNode.cs`, `src/VL.Qt/Nodes/ElementTooltipPreview.cs`, `src/VL.Qt/Nodes/SnapshotGrab*` (wherever `TryLocate` is called), `src/VL.Qt.Stride/SharedTextureScene.cs`, tests (`SceneRegistrySlotTests`, `ReconcilerSceneTests`, `InteractionTests`, `RectangleNodeTests`), SmokeTest `MultiRendererTest`
- **Depends on:** Q1 (if the key changes, do it in the same issue)
- **Verification:** `tools/build.ps1 -Test`. SmokeTest `MultiRendererTest`, `VideoTest`, `InteractionTest`.

### REC-07 Rename `Node` and group its state by concern
- **Kind:** naming
- **Severity:** should
- **Breaking:** no (internal)
- **Where:** `src/VL.Qt/Reconciler/Node.cs:13-124`, `:131-160`
- **Finding:**
  - **The name clashes.** For the vvvv reader a "node" is a process node. Here `Node` is the mirror of one position.
  - **Too much flat state.** It has 28 properties for seven concerns: identity, binding, traversal bookkeeping, Effect-pin chain (6), ShaderEffect element (2), handlers, styles (3) and self-made widget (3).
  - **The "Effect" prefix covers two different things.** `EffectSource` and `SentUniforms` belong to the *ShaderEffect element* (`Node.cs:90-94`). `EffectChain`, `EffectLinks`, `EffectElements`, `EffectObjects`, `EffectSentIds` and `EffectWarning` belong to the *Effect pin*. A reader takes the two for one feature.
  - **Unclear names.** `Dirty` really means "must be visited, a video below was taken over" (`:67`). `Last` is the element description.
  - **Two types in one file.** `HandlerNode` shares the file (K3).
- **Proposal:**
  - `Node` → `ItemMirror`, with these members:
    - identity: `Id`, `Type`, `Parent`, `Children`, `Element` (was `Last`)
    - binding: `BoundSlot`, `BoundHandle`, `IsPrimary` (see REC-11)
    - traversal: `VisitedPass`, `SkippedPass`, `MustVisit` (was `Dirty`)
    - `EffectChainMirror? Effect` (new class: `Chain`, `Links`, `Elements`, `Objects`, `SentIds`, `Warning`)
    - `ShaderEffectMirror? Shader` (new class: `Source`, `SentUniforms`)
    - `List<HandlerMirror>? Handlers`
    - style: `InheritedStyle`, `ResolvedStyle`, `SentStyle`
    - self-made widget: `Stylable`, `BuildError`, `ContainsStylable`
  - The sub-objects are allocated on first use, as `Handlers` already is, which also makes the common item smaller.
  - `HandlerNode` → `HandlerMirror` in `HandlerMirror.cs`.
- **Decided:** O3 → `ItemMirror` and `HandlerMirror` as proposed; O5 → the element is `Shader`, so `ShaderEffectMirror` is `ShaderMirror` (`ShaderMirror.cs`) and 'ShaderEffect element' reads 'Shader element' next to 'Effect pin'; O7 → `BoundHandle` holds the number that routes events, which is an id: `BoundFeedbackId`; A1 → `BoundSlot` is a `FeedbackSlot`
- **Effort:** M, mechanical: partly
- **Touches:** `src/VL.Qt/Reconciler/Node.cs` → `ItemMirror.cs`, `EffectChainMirror.cs`, `ShaderEffectMirror.cs`, `HandlerMirror.cs`; `src/VL.Qt/Reconciler/Reconciler.cs`, `EffectDiff.cs`, `PropertyDiff.cs`, `UniformWriter.cs`, `SceneRegistry.cs`; tests that read `Root`/`Node` (`rg "\.Root\b|Node\b" src/VL.Qt.Tests src/VL.Qt.SmokeTest`)
- **Depends on:** REC-04
- **Verification:** `tools/build.ps1 -Test`.

### REC-08 ShaderEffect uniforms: stop allocating a dictionary on every visit
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/UniformWriter.cs:39-53`, `:76-80`, `:145`; `src/VL.Qt/Reconciler/PropertyDiff.cs:546`
- **Finding:** `UniformWriter.Write` builds a new `Dictionary<string, UniformValue>` every time a ShaderEffect node is visited (not skipped). It also concatenates a new key string per sampler uniform (`u.Name + SamplerKey`, `"source" + SamplerKey`), and then usually throws the result away (`return sent` at `:77`). An animated uniform, or any change inside the effect's input child, makes the ShaderEffect a new instance every frame, so this path runs every frame. `WriteEntry` also trims the key with `TrimEnd` (no allocation for a span, but it shows the encoded key trick).
- **Proposal:**
  - Key the table by `readonly record struct UniformKey(string Name, bool Sampler)` instead of a suffixed string.
  - Keep two dictionaries per `ShaderEffectMirror` (`Sent`, `Scratch`). Fill `Scratch`, compare, and swap only when something was sent. Clear instead of allocating.
  - A fast path: when `effect.Uniforms` is reference-equal to the previous effect's uniforms and no child id changed, skip the build.
- **Decided:** O5 → the element is `Shader`: `ShaderEffectMirror` is `ShaderMirror` and the new perf case is for a `Shader`, not a `ShaderEffect`
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/UniformWriter.cs`, `src/VL.Qt/Reconciler/PropertyDiff.cs`, `src/VL.Qt/Reconciler/Node.cs` (or `ShaderEffectMirror.cs`), `src/VL.Qt.Tests/ReconcilerPerfTests.cs`
- **Depends on:** none (REC-07 if done after it)
- **Verification:** A new test in `ReconcilerPerfTests`: a ShaderEffect with three uniforms and one animated float, at 0 bytes per frame. `ShaderEffectTests` stays green.

### REC-09 Effect chain: reuse buffers when the chain instance changes
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/EffectDiff.cs:39-74`, `:116-133`
- **Finding:** An effect node outputs a new `Effect` on every input change, so an animated blur changes the chain instance every frame. `ChangeEffectChain` then allocates:
  - `Effect.Segment` (in the element area),
  - a `List<Element>` in `CollectEffectElements`,
  - `bool[]` and `Node?[]`, even `new Node?[0]` when the chain refers to no element,
  - and a new `List<Node>` that replaces `EffectObjects`.

  The class summary's "allocation-free in the steady state" does not hold for animated effects, and no test covers them.
- **Proposal:**
  - Keep `Elements` and `Objects` as lists of the `EffectChainMirror` and clear and refill them, with scratch `used`/`matches` buffers on the reconciler (grow-only, like `_shapeBuffer`).
  - Return early without element collection when the new links refer to no element.
  - Ask the element-area review whether `Effect.Segment` can reuse its builder (one line in §6).
- **Decided:** O3 → `Node?[]` and `List<Node>` are `ItemMirror?[]` and `List<ItemMirror>`; O5 → the shader entries of the chain are `ShaderEffect`s (was `ShaderLink`)
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/EffectDiff.cs`, `src/VL.Qt/Reconciler/Node.cs`, `src/VL.Qt.Tests/ReconcilerPerfTests.cs`
- **Depends on:** none
- **Verification:** A new `ReconcilerPerfTests` case: a Group with one Rectangle with an animated Blur effect, where only the reconciler's share is measured (the effect built outside the loop, alternating two instances). 0 bytes. `EffectChainReconcilerTests` green.

### REC-10 Elements shown in two renderers: per-frame allocation, hand-over and owner flip
- **Kind:** correctness
- **Severity:** should
- **Breaking:** yes, depending on Q1. Which renderer reports Measured, SceneBounds and Media, and where Snapshot finds the element, may change for elements shown in two renderers.
- **Where:** `src/VL.Qt/Reconciler/SceneRegistry.cs:161-191`, `:214-256`, `:284-299`, `:302-308`, `:135-149` (slot fallback); `src/VL.Qt/Reconciler/Reconciler.cs:284-294`, `:487-511`, `:262-271`
- **Finding:** The registry key is the element *instance*, and an element node outputs a new instance on every input change (as the `TryLocate` comment notes for #548).
  - **Order A then B** (A is the first owner), for an animated element shown in renderers A and B:
    - A releases the old instance. `RemoveOccurrence` returns true because B still shows it (`:241-242`).
    - A's `ResolvePrimaries` hands the old instance over to B (`HandOver`, `B.TakeOver`).
    - B then releases it and registers the new instance with a new `RendererOccurrences` and a new list (`:178`, `:189`).
    - Result: two allocations and a hand-over per element per frame.
  - **Order B then A:** B registers the new instance first and becomes its owner (`:166-172`). The documented rule "owner = first renderer that showed it" (`docs/decisions/reconciler-identity.md:29`) becomes "the renderer that updates first after a change".
  - **The same for a Video.** If the placeholder's renderer runs first, it creates a player. The real owner then turns its video into a placeholder (`Reconciler.cs:286-289`), so playback restarts in the other renderer.
  - **A duplicate video inside one tree.** An animated video used twice in one tree calls `Invalidate()` on its own reconciler every frame (`SceneRegistry.cs:250-254`). Every frame becomes a forced full visit, and `AddDuplicate` allocates a `List<Reconciler>` every frame (`:305`).

  These are suspicions only where marked. The traces above follow the code line by line, and no test runs two renderers with changing instances (`MultiRendererTest` uses fixed instances, as far as its asserts show).
- **Proposal:** Depends on Q1.
  - **If the key becomes the sink slot:** occurrence entries live as long as the slot is shown, and nothing is removed or added when only the instance changes. The `_shownBySlot` fallback (`SceneRegistry.cs:84`, `:137-138`, `:164-165`, `:326-333`) goes. Elements without a sink keep the instance key.
  - **If the key stays the instance:**
    - pool `RendererOccurrences` together with the lists;
    - when releasing and registering the same position in one visit (the `!same` branch, `Reconciler.cs:291-293`), move the occurrence from the old to the new instance in one registry call `Replace(old, next, node)`, which keeps owner, primary and `Elsewhere`;
    - document that the owner renderer may change after a change.
- **Decided:** A2 → first branch: the key is the feedback slot when the element has one, occurrence entries live as long as the slot is shown, `_shownBySlot` goes, elements without a slot keep the instance key; the second branch (`Replace(old, next, node)`, pooling, owner 'first renderer to show the current instance') is not taken; accepted: two instances of one node in one tree are two occurrences of one element and a Video node has one player; G5 → the record is `docs/design/reconciler-identity.md`; 'Depends on Q1' is answered
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/SceneRegistry.cs`, `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt.Tests/ReconcilerSceneTests.cs`, `SceneRegistrySlotTests.cs`, SmokeTest `MultiRendererTest.cs`, `docs/decisions/reconciler-identity.md`
- **Depends on:** Q1, REC-06
- **Verification:**
  - A new unit test: two reconcilers on one registry and an element whose instance changes every frame, in both update orders. The owner stays the first renderer, and there are 0 bytes per frame after warm-up.
  - A Video variant: no player is destroyed.

### REC-11 Keep "primary" in one place
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/Node.cs:49`, `:52`; `src/VL.Qt/Reconciler/Reconciler.cs:442-478`, `:505-508`, `:518-531`, `:603-607`, `:620-623`; `src/VL.Qt/Reconciler/SceneRegistry.cs:74`; `src/VL.Qt/Elements/SinkRegistry.cs:35-42`, `:75-79`
- **Finding:** One fact, "this occurrence reports state", is held four times:
  - `Entry.Primary` in the registry,
  - `Node.IsPrimary`,
  - `Node.BoundPrimary`,
  - the `Primary` flag of the handle in `SinkRegistry`.

  They are synchronised in five places, with a deliberate lag: a node promoted during `Register` is only re-bound later, in `BindSink`, through the `IsPrimary != BoundPrimary` check. Proving that the four agree at the end of an update needs the whole file. REC-01 shows how they drift apart after an exception.
- **Proposal:** Two methods in `PrimaryOccurrences`, `Promote(ItemMirror)` and `Demote(ItemMirror)`, are the only writers of `IsPrimary`, and they update the handle at once:
  - a promotion of a bound node takes a new primary handle and writes BIND (an existing object can be bound before its SETs);
  - a demotion flips the handle's flag.

  `BindSink` acquires with `IsPrimary` as today. `BoundPrimary` and the check at `Reconciler.cs:603-607` go. In DEBUG, `ResolveAtEndOfUpdate` asserts `PrimaryOf(e) == node ⇔ node.IsPrimary` for the elements it touched.
- **Decided:** A1 → `SinkRegistry` is `FeedbackRouter` (the `Primary` flag of the handle lives there); O3 → `Node` is `ItemMirror`; O7 → what BIND carries and the router maps is a feedback id, not a handle: `BoundHandle` is `BoundFeedbackId`, and 'takes a new primary handle' reads 'takes a new feedback id'
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/Node.cs`
- **Depends on:** REC-04
- **Verification:** `ReconcilerSceneTests` (the primary and hand-over tests), SmokeTest `MultiPlacementTest`, `MultiRendererTest`.

### REC-12 Self-made widgets used as effect masks are not rebuilt
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/Reconciler.cs:382`, `:399`, `:402`, `:276`; `src/VL.Qt/Reconciler/EffectDiff.cs:61`, `:89`
- **Finding:** `ContainsStylable` is computed from the children only. A `StylableElement` that is an effect mask source or a uniform texture is reconciled through `Reconcile` (so it is built), but the host's `ContainsStylable` does not see it. Once the host is skipped, the widget's `Build` stops running, although self-made widgets are meant to be built on every pass (`Reconciler.cs:59-63`).
- **Proposal:** In `ReconcileEffect` and `RetryEffectObjects`, OR the effect objects' `Stylable is not null || ContainsStylable` into the host, through a shared helper `bool HoldsStylable(ItemMirror)` used by both loops.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/EffectDiff.cs`, `src/VL.Qt.Tests/ReconcilerStylableTests.cs`
- **Depends on:** none
- **Verification:** A new test: a Rectangle with a mask effect whose source is a `StylableElement` that counts its Build calls. The count rises on every Update while the Rectangle is unchanged.

### REC-13 `ICommandSink`: remove the throwing default members and the always-0 BIND flags
- **Kind:** structure
- **Severity:** could
- **Breaking:** yes, for the protocol only (BIND loses a field). Patches notice nothing.
- **Where:** `src/VL.Qt/Reconciler/ICommandSink.cs:17-18`, `:107-117`; `src/VL.Qt/Reconciler/Reconciler.cs:524`, `:612`, `:624`, `:781`
- **Finding:**
  - The four QML members have default bodies that throw `NotSupportedException`, "so fakes that predate #104 keep compiling" (`ICommandSink.cs:107`). That is a test shim in the production interface (K6), and a sink that forgets them fails only at run time. The fakes are `RecordingSink`, two `CountingSink`s and `RecordingSinkNull` (`src/VL.Qt.Tests`).
  - `Bind(id, handle, flags)` is called with `flags = 0` everywhere ("no BIND flags" since input moved to handlers).
- **Proposal:**
  - Make the QML members abstract, and give the tests one `abstract class NullCommandSink : ICommandSink` with empty virtual members, which the fakes derive from.
  - Drop `flags` from `Bind` and from the BIND command (`protocol.h`, `Protocol.Bind`, `CommandWriter`, QtNative), or keep the wire field and drop only the C# parameter if the protocol review prefers.
  - Move `ICommandSink.cs` next to `CommandWriter.cs` (it is the wire contract).
- **Decided:** P4 step 3 (the wire-format pull request with PROTO-02, PROTO-09 and B5) → BIND loses `flags` on the wire too (PROTO-09 deletes the field in the same pull request: `protocol.h`, `Protocol.Bind`, `CommandWriter`, QtNative), so the 'keep the wire field' branch is not taken; O7 → BIND carries `feedbackId` (was `sinkHandle`); S3 → `ICommandSink.cs` moves to `Wire/` and `CommandSinkExtensions` gets its own file
- **Effort:** M, mechanical: partly
- **Touches:** `src/VL.Qt/Reconciler/ICommandSink.cs`, `src/VL.Qt/CommandWriter.cs`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt.Native/protocol.h`, `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt.Tests/RecordingSink.cs`, `ReconcilerPropertyTests.cs`, `ReconcilerStructureTests.cs`, `ReconcilerSceneTests.cs`
- **Depends on:** the wire-format review (protocol area)
- **Verification:** `dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift`, `tools/build.ps1 -Test`.

### REC-14 Remove test-only members and dead members (K6 for this area)
- **Kind:** dead-code
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/IdAllocator.cs:23`, `:26`; `src/VL.Qt/Reconciler/SceneRegistry.cs:87`, `:90`, `:112-123`, `:152`, `:360`; `src/VL.Qt/Reconciler/Reconciler.cs:104`, `:131`; `src/VL.Qt/Reconciler/ResyncController.cs:22`, `:29-30`
- **Finding:**
  - `IdAllocator.HighWater` has no caller (searched `src`).
  - These exist only for tests or the SmokeTest:
    - `IdAllocator.Count`;
    - `SceneRegistry.Count`, `SlotCount`, `HandlerCount`, `OccurrencesOf` and `DuplicatesOf`;
    - `Reconciler.Root`;
    - the overload `Reconciler.Update(Element?, Space)`. No caller in `src/VL.Qt` (searched `\.Update\([^;]*Space\.`); its doc says "for tests".
  - `ResyncController` takes a `Func<int>` whose result it ignores.
- **Proposal:**
  - Delete `HighWater`.
  - Replace the counters with one `internal Diagnostics GetDiagnostics()` per type, returning a record struct of counts. Tests keep their leak checks through one member, and the production surface says it is diagnostics.
  - Tests call `Update(root, new SpaceMapping(space, default, 1f))` through a test helper extension in `src/VL.Qt.Tests`.
  - `Func<int>` → `Action`.
- **Decided:** P22 → REC-14 is one of the K6 findings worked under TEST-07 (one issue), not an issue of its own; the REC-06 names apply to the members it lists (`OccurrencesOf`, `DuplicatesOf`)
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt/Reconciler/IdAllocator.cs`, `SceneRegistry.cs`, `Reconciler.cs`, `ResyncController.cs`, `src/VL.Qt/SceneCore.cs`, tests and SmokeTest files that use these members (`rg "OccurrencesOf|DuplicatesOf|SlotCount|HandlerCount|_ids\.Count|ids\.Count|\.Root\b|Space\.DIP" src/VL.Qt.Tests src/VL.Qt.SmokeTest`)
- **Depends on:** REC-06 if it is done first (names)
- **Verification:** `tools/build.ps1 -Test`.

### REC-15 Resync with animated input rebuilds every second frame
- **Kind:** performance
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt/Reconciler/ResyncController.cs:46-58`, `:65-86`
- **Finding:** After giving up, the controller retries as soon as the input reference changes (`:50-54`). With animated content the input is new every frame. A persistent error (an invalid value that QtNative rejects every time) therefore causes RESET + full rebuild + failure + give-up every second frame, with all the CREATEs and allocations of a first frame. "No endless loop" holds per input, not in time.
- **Proposal:** Keep the "new input" rule and add a minimum pause before the next attempt that doubles after each failed attempt (2, 4, … up to 64 frames) and resets after a successful apply. Count frames through `ShouldReconcile` calls.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt/Reconciler/ResyncController.cs`, `src/VL.Qt.Tests/ResyncTests.cs`
- **Depends on:** REC-01 (the same controller gains `OnReconcileFailed`)
- **Verification:** A new `ResyncTests` case: a fake QtNative that fails every apply and a new input every frame. The number of RESETs over 100 frames stays ≤ 8.

### REC-16 Tests: cover the paths that are not covered
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Tests/ReconcilerPerfTests.cs:24-68`, `:85-127`
- **Finding:** The allocation contract is tested for three trees (rectangles, a column, texts). Each alternates between the same two instances, so the changed position always swaps between two known elements. Nothing tests allocation-freedom or behaviour for:
  - two renderers sharing an element whose instance changes (REC-10),
  - a ShaderEffect with uniforms (REC-08),
  - an animated Effect chain (REC-09),
  - an animated style or self-made widget,
  - handlers on an animated element,
  - an exception inside `Update` (REC-01).

  A refactoring by REC-04, REC-06 or REC-07 would run without a net on exactly the parts it moves.
- **Proposal:**
  - Add `Measure` cases for: ShaderEffect, Effect chain, Group with `Style` changing every frame, element with Tap + Drag handlers changing every frame, and two reconcilers on one `SceneRegistry`.
  - Add a fault-injection sink (`ThrowingSink`, throws at command n) to the test project.
- **Decided:** PLAN-CHECK wave 1, item 3 → written in wave 1, before any cut of wave 4 (REC-04); the two-renderer case for REC-10 is written for the slot key of A2
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Tests/ReconcilerPerfTests.cs`, new `src/VL.Qt.Tests/ThrowingSink.cs`, `src/VL.Qt.Tests/ReconcilerSceneTests.cs`
- **Depends on:** none (should land before REC-04)
- **Verification:** The tests themselves; they fail today where REC-08 to REC-10 apply.

## 4 Target structure

```
src/VL.Qt/Reconciliation/                 namespace VL.Qt.Reconciliation   (was Reconciler/, namespace VL.Qt)
  Reconciler.cs            Reconciler: Update/Clear/Discard, position reconcile, create/destroy, skip test, children,
                           style resolve, self-made widget build, effect objects (no longer partial)
  PassInfo.cs              readonly struct PassInfo: pass number, force, rescale, mapping (new)
  Position.cs              readonly struct Position: parent mirror, parent sink, REPARENT index, inherited style, attached (new, REC-05)
  ItemMirror.cs            ItemMirror (was Node): per-position mirror (REC-07)
  EffectChainMirror.cs     EffectChainMirror: chain state of the Effect pin (split from Node)
  ShaderEffectMirror.cs    ShaderEffectMirror: Source + sent uniforms of a ShaderEffect element (split from Node)
  HandlerMirror.cs         HandlerMirror (was HandlerNode, split from Node.cs)
  PrimaryOccurrences.cs    PrimaryOccurrences: per-renderer primary/placeholder logic (split from Reconciler.cs, REC-04/11)
  HandlerReconciler.cs     HandlerReconciler: input handler diff (split from Reconciler.cs)
  PropertyDiff.cs          PropertyDiff: sealed class, per-type SET diff (was partial Reconciler)
  EffectChainWriter.cs     EffectChainWriter: static, writes SET_EFFECT_CHAIN (split from EffectDiff.cs)
  EffectPadding.cs         EffectPadding (split from EffectDiff.cs, K3)
  UniformWriter.cs         UniformWriter: SET_UNIFORMS diff (UniformKey added, REC-08)
  UniformValue.cs          UniformValue (split from UniformWriter.cs, K3)
  QmlPropertyWriter.cs     unchanged (plus the size guard of REC-01)
  OccurrenceRegistry.cs    OccurrenceRegistry: app-wide occurrences, owner renderer, hand-over, placeholders, TryLocate (from SceneRegistry)
  InteractionRegistry.cs   InteractionRegistry: app-wide active handler per interaction sink (from SceneRegistry)
  IdAllocator.cs           unchanged minus HighWater
moved out of the folder:
  ICommandSink.cs          → next to CommandWriter.cs (wire contract), CommandSinkExtensions into CommandSinkExtensions.cs (K3)
  ResyncController.cs      → next to SceneCore.cs (apply policy of a renderer, not part of the diff)
deleted:
  EffectDiff.cs            (content in Reconciler.cs and EffectChainWriter.cs)
  SceneRegistry.cs         (content in OccurrenceRegistry.cs and InteractionRegistry.cs)
  Node.cs                  (content in the *Mirror.cs files)
docs/topics/reconciler.md  new design page (REC-03)
```

Everything stays `internal`. `SinkSlot`, `ElementSink` and `SinkState` remain public in the element area.

## 5 Questions for Sebastian

**Q1 What identifies "the same element" for occurrences: the instance or the sink slot?**
Today it is the instance (`SceneRegistry._entries`, key = element reference). An element node outputs a new instance whenever an input changes, but always with the same sink slot. What the user sees as one Rectangle node is therefore a new key after every change. Consequences: per-frame registry churn, a hand-over and allocations across renderers, and an owner renderer that becomes "first to update after a change". The Snapshot slot fallback (#548) exists only to bridge the gap (REC-10).
- **A: key by `SinkSlot` when the element has one, by instance otherwise.**
  - Matches what users mean and makes the documented owner rule true.
  - Removes the fallback and the churn.
  - New behaviour: two different instances from the same node in one tree (for example last frame's instance kept by a FrameDelay next to the current one) become occurrences of one element. That also means one player for a Video node, which is arguably right.
  - Risk: elements built in C# without a sink keep today's behaviour, so there are two code paths in the key function only.
- **B: keep the instance key** and fix the cost by a `Replace(old, next, node)` registry call plus pooling (REC-10). The documented owner rule is changed to "first renderer to show the current instance".

Recommendation: **A**, because it removes code and makes the decision record true.

**Q2 What happens when the reconcile throws?**
- **A: treat it like a failed apply.** Reset the command buffer, resync (RESET + rebuild next frame), and show the exception at the renderer's Error output.
- **B: stop reconciling for this renderer until the input changes.**
- **C: let it propagate as today.** This leaks per frame (REC-01).

Recommendation: **A**, with REC-01's clamping, so that user data never reaches the exception path.

**Q3 One pattern for the per-type property diff.**
Items are diffed by a central switch in the reconciler (`PropertyDiff.cs`). Input handlers diff themselves through `Interaction.WriteProperties` in the element model (`src/VL.Qt/Elements/Interaction.cs:41`). There are two patterns for the same job.
- **A: central.** The reconciliation folder holds all wire knowledge; `Interaction.WriteProperties` moves into a `HandlerPropertyDiff` there. A contributor adding an element edits `ElementType`, the element record and `PropertyDiff`.
- **B: per element.** An `internal abstract void WriteProperties(ICommandSink, uint id, Element? previous, in SpaceMapping, bool rescale)` on `Element` (the element records already hold their defaults). A contributor edits one class, but the element records then carry protocol code.

Recommendation: **A**, because the element records are the public data model and should not depend on the wire format. Decide it before REC-04, since it fixes where `PropertyDiff` ends up.

## 6 For other areas

- **Element model:** `SinkSlot.State` (`src/VL.Qt/Elements/ElementSink.cs:355`) grows the slot list on read (`Sink?[Index]`). Callers that only want to write a message create slots as a side effect; a `TryGetState` would be clearer.
- **Element model:** `Effect.Segment` allocates on every chain change (an `ImmutableArray` plus warnings). This is the other half of REC-09.
- **Element model:** `SinkState.Warning`/`BuildError` need the per-source message store of REC-02 (`src/VL.Qt/Elements/ElementSink.cs:91`, `:106`).
- **Wire format:** `CommandWriter` throws on an oversized SET (`src/VL.Qt/CommandWriter.cs:602`) with no size helper for callers. Add `Protocol.MaxStringBytes`, or a `TrySetString` that reports truncation (REC-01).
- **Wire format:** the BIND `flags` field is always 0 from C# (REC-13). Check whether QtNative still reads it.
- **Renderer/SceneCore:** `SceneCore.Update` (`src/VL.Qt/SceneCore.cs:102-108`) has no try/catch around `_reconciler.Update`. `Apply()` sends whatever the failed frame wrote (REC-01, Q2).
- **Styles:** `StyleWriter.Write` writes string entries through the same size-limited command. Check that a long font family or style string cannot exceed `SET_STYLE`'s payload (`src/VL.Qt/CommandWriter.cs:291`).
- **Docs:** `docs/architecture.md` "Performance" quotes the reconciler at "~0.04 ms per 1000 steady-state updates", while `ReconcilerPerfTests.cs:20` quotes ~0.08 ms median. One number should go.
