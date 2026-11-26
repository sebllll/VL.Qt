# Reconciler identity

How the reconciler decides, every frame, whether to update an existing Qt object or create a new one, and what counts as one element when it is shown at several places. The rest of the reconciler (one frame, the skip test, the order of the commands, resync) is in [reconciler.md](reconciler.md). The code is in `src/VL.Qt/Reconciliation/`: `Reconciler` (positions), `OccurrenceKey` and `OccurrenceRegistry` (elements and their occurrences), `PrimaryOccurrences` (the primary occurrence inside one tree), `InteractionRegistry` (input handlers). How the feedback carries events back to the nodes is in [events-and-interaction.md](events-and-interaction.md).

## Context

The input is an element tree of immutable C# descriptions (primitives, text, Group, layouts, …). Qt Quick is retained: real objects live on, like Stride entities. For each frame the reconciler has to decide between updating the same Qt object and creating a new one.

An element node outputs a new description instance whenever one of its inputs changes, always with the same feedback slot (the reference from the description to the state of its node, [events-and-interaction.md](events-and-interaction.md)). What the user sees as one Rectangle node is one feedback slot and a new instance after every change.

How comparable systems handle identity:
- **VL.Skia:** layer objects, fully re-rendered every frame, no native objects to match, so no identity problem.
- **VL.Stride:** the node instance owns its entity, so identity is the node instance (the iteration index inside `ForEach`).
- **ImGui:** identity through an ID stack (path/label).
- **React:** position under the parent plus type plus an optional key.
- **VL `ForEach`:** state per iteration index, that is, positional.

## Design

### Qt objects: position and type

- **Identity of a Qt object = position under the parent + element type.** This behaves like `ForEach` and Stride in VL, needs no pins, and suffices because every value comes from vvvv each frame (no state in Qt that could "jump").
- **Type change at a position:** destroy the old object, create a new one.
- **Insert/delete in the middle of a spread:** the following objects take over the data of their neighbours. This is invisible, because all values are set every frame.
- **Reparenting to another parent:** create anew.
- **Reference equality as a shortcut:** if a description is reference-equal to the one from the last frame, the whole subtree is skipped (possible because descriptions are immutable).
- **Feedback binding per frame:** the feedback slot comes from the description every frame and is bound to the Qt object at that position. A key pin would only affect the identity of Qt objects, not the binding.

### Element key

- **One element = one feedback slot.** The `OccurrenceRegistry` counts an element by its `OccurrenceKey`: the feedback slot of the element when it has one, the instance otherwise. Every element node has a slot; only elements built in C# without a node have none and are counted by instance.
- **A new instance changes nothing in the registry.** When the instance at a position changes but the key stays, the occurrence stays as it is: same owner renderer, same primary, no hand-over, no allocation. Only a different key at a position releases the old occurrence and registers a new one.
- **Two instances of one node are one element.** Last frame's instance (through a FrameDelay) next to the current one are two occurrences of one element; for a Video that is one player.
- **Locating an element** (Snapshot, TooltipPreview) goes through the key, so a node's new instance is found before the renderer of the frame ran.

### Occurrences

- **Multiple occurrences are allowed.** The same element may hang in the tree several times (for example the same rectangle in two groups). Each occurrence gets its own Qt object (identity stays position plus type), and all occurrences bind the same feedback slot. How the return channel merges them (events add up, hover and press are the OR, state values come from the primary occurrence) is in [events-and-interaction.md](events-and-interaction.md#an-element-shown-at-several-places). Class instances with their own element node are unaffected; every instance has its own feedback. Rationale: a rule that forbids multiple occurrences is surprising for users (a warning instead of drawing), and Skia layers behave the same way.
- **The same rules hold across renderers.** An element may hang in several renderers at once (a `Renderer [Qt]` window and a `QtTexture`, two textures at different sizes); every renderer builds its own Qt objects, no placeholder, no warning.
  - The owner renderer is the first renderer that showed the element (registration order). The primary occurrence is the first occurrence in traversal order of the owner renderer. It alone reports state values; Snapshot and TooltipPreview locate it. Because the key is the slot, the owner stays the same while the node outputs new instances, whichever renderer updates first.
  - Events of all occurrences in all renderers are merged into the feedback, as inside one renderer.
  - When another occurrence becomes primary (an earlier position takes the element, or a reordered spread moves a slot to an earlier position), the slot drops the state values the former primary wrote; the new primary is bound with a new feedback id, so QtNative sends its own.
  - When the primary lets go, the first remaining occurrence in the owner's tree takes over. When the owner has no occurrence left at the end of its update, the next renderer in registration order becomes the owner and picks its first occurrence as primary in its next update; in between there is no primary for one update (no new state values). Deciding at the end of the update keeps a move inside the owner's tree from handing the element to another renderer.
- **Placeholders.** A `Video` is shown at one place only, app-wide (one player): the first occurrence of the first renderer plays, every other occurrence is a placeholder (a mirror without Qt object) with a warning in the feedback slot until the player lets go.

### Input handlers

An interaction node is active at one handler only, app-wide, keyed by its own feedback slot (`InteractionRegistry`). Unlike elements, the active handler keeps it as long as it exists; further occurrences are placeholders with a warning until it lets go.

## Consequences

- A node that changes every frame costs a property diff per renderer, nothing in the registry. `ReconcilerPerfTests` holds this at 0 bytes per frame for one element shown in two renderers, in both update orders.
- An element without a slot that is a new instance every frame is released and registered again every frame, which allocates when it is shown in two renderers. Element nodes do not produce such elements.
- A Video node whose last instance is kept next to the current one shows one player and a placeholder with the warning.
- What breaks if the key changes: the owner renderer, the Snapshot lookup and the Video placeholder all rely on it. `OccurrenceRegistrySlotTests` and the two-renderer cases of `ReconcilerSceneTests` and `ReconcilerPerfTests` protect it.

## Alternatives considered

- **Key by instance** (with a `Replace(old, next)` call and pooling against the cost): keeps per-frame registry work, makes the owner "the first renderer to show the current instance", lets a Video restart in another renderer after a change, and needs a slot fallback for Snapshot. The slot is what users mean by "the same element".
- **Stride pattern (node instance owns the Qt object directly):** contradicts the input model, where descriptions are data and the reconciler owns the Qt objects.
- **Automatic ID from the node instance (GUID per process node):** falls back to the index inside `ForEach` anyway and gains nothing.
- **Forbidding multiple occurrences ("one element in one place only"):** surprising for users; replaced by the multiple-occurrence rules above.
- **Return channel as a spread per occurrence:** unstable order, ugly pins for the normal case.
- **An explicit `Instance`/`Clone` node as the only way:** one would have to know beforehand that an element is hung in twice.
- **Global keys / carrying objects across parent boundaries:** complex, little benefit for 2D content.
- **Enter/exit animations and the Qt animation system** as part of the basic reconciler.

## Open questions

- **Optional `Key` pin** (among siblings only). It would also remove the state jump (hover/pressed) when inserting or reordering. It becomes necessary with Qt-native animations/transitions or enter/exit effects.
- **A `Mirror` element** (`ShaderEffectSource` with a `sourceItem`): a purely visual live copy without interaction, for reflections and repeats. A complement, not a replacement.
- Whether elements backing Qt objects that are expensive to build (large texts, images) should be reused despite moving (performance rather than correctness).

## Evidence

- Unit tests, no GPU: `ReconcilerPerfTests.TwoRenderers_SharedElementChangingInstance_NoAllocations` and `_OwnerStaysTheFirstRenderer` (both update orders), `ReconcilerSceneTests.VideoNewInstanceEveryFrame_*`, `OccurrenceRegistrySlotTests`. Rerun with `dotnet test src/VL.Qt.Tests --filter "FullyQualifiedName~TwoRenderers|FullyQualifiedName~OccurrenceRegistrySlotTests|FullyQualifiedName~VideoNewInstance"`.
- SmokeTest `MultiRendererTest`: one tree in a window and an offscreen scene of one registry.
