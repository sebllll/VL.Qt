# Identity in the Reconciler

How the reconciler decides, every frame, whether to update an existing Qt object or create a new one.

## Context
The input is a virtual tree of immutable C# element descriptions (primitives, text, group, layouts, …). Qt Quick is retained: real objects live on, like Stride entities. For each frame the reconciler has to decide between updating the same Qt object and creating a new one.

How comparable systems handle identity:
- **VL.Skia:** layer objects, fully re-rendered every frame, no native objects to match, so no identity problem.
- **VL.Stride:** the node instance owns its entity, so identity is the node instance (the iteration index inside `ForEach`).
- **ImGui:** identity through an ID stack (path/label).
- **React:** position under the parent plus type plus an optional key.
- **VL `ForEach`:** state per iteration index, that is, positional.

## Design
- **Identity = position under the parent + element type.** This behaves like `ForEach` and Stride in VL, needs no pins, and suffices because every value comes from vvvv each frame (no state in Qt that could "jump").
- **Type change at a position:** destroy the old object, create a new one.
- **Insert/delete in the middle of a spread:** the following objects take over the data of their neighbours. This is invisible, because all values are set every frame.
- **Reparenting to another parent:** create anew.
- **Reference equality as a shortcut:** if a description is reference-equal to the one from the last frame, the whole subtree is skipped (possible because descriptions are immutable).
- **Multiple occurrences are allowed.** The same element instance may hang in the tree several times (for example the same rectangle in two groups). Each occurrence gets its own Qt object (identity stays position plus type), and all occurrences bind the same sink. Return-channel rules:
  - State values (measured size, error) come from the first occurrence in traversal order (the "primary" one).
  - Events are merged: a click on any occurrence counts, hover/pressed combine with OR.
  - Channels are unchanged; two views of one value are intended.
  - To tell which occurrence was clicked, put an interaction on the surrounding group per placement.
  - Class instances with their own element node are unaffected; every instance has its own sink.
  - Rationale: a rule that forbids multiple occurrences is surprising for users (a warning instead of drawing), and Skia layers behave the same way.
- **The same rules hold across renderers** (#126). An element may hang in several renderers at once (a `Renderer [Qt]` window and a `QtTexture`, two textures at different sizes); every renderer builds its own Qt objects, no placeholder, no warning.
  - The primary occurrence is the first occurrence in traversal order of the owner renderer, the first renderer that showed the element (registration order). It alone reports state values; Snapshot and TooltipPreview locate it.
  - Events of all occurrences in all renderers are merged into the sink, as inside one renderer.
  - When the primary lets go, the first remaining occurrence in the owner's tree takes over. When the owner has no occurrence left at the end of its update, the next renderer in registration order becomes the owner and picks its first occurrence as primary in its next update; in between there is no primary for one update (no new state values). Deciding at the end of the update keeps a move inside the owner's tree from handing the element to another renderer.
  - `Video` stays exclusive app-wide (one player): the first occurrence of the first renderer plays, every other occurrence is a placeholder with a warning until it becomes free.
  - Interaction handlers are not covered: an interaction node is active at one handler only, app-wide (see the open question below).
- **Sink binding per frame** (see `events.md`): identity stays position plus type. The sink comes from the description every frame and is bound to the Qt object at that position. A key pin would only affect the identity of Qt objects, not the sink binding.

## Open questions
- **Optional `Key` pin** (among siblings only). It would also remove the state jump (hover/pressed) when inserting or reordering. It becomes necessary with Qt-native animations/transitions or enter/exit effects.
- **Interaction handlers with multiple occurrences:** merge simultaneous drags/touches on two occurrences of the same interaction, or only honour the first.
- **A `Mirror` element** (`ShaderEffectSource` with a `sourceItem`): a purely visual live copy without interaction, for reflections and repeats. A complement, not a replacement.
- Whether elements backing Qt objects that are expensive to build (large texts, images) should be reused despite moving (performance rather than correctness).

## Rejected alternatives
- **Stride pattern (node instance owns the Qt object directly):** contradicts the input model, where descriptions are data and the reconciler owns the Qt objects.
- **Automatic ID from the node instance (GUID per process node):** falls back to the index inside `ForEach` anyway and gains nothing.
- **Forbidding multiple occurrences ("one element in one place only"):** surprising for users; replaced by the multiple-occurrence rules above.
- **Return channel as a spread per occurrence:** unstable order, ugly pins for the normal case.
- **An explicit `Instance`/`Clone` node as the only way:** one would have to know beforehand that an element is hung in twice.
- **Global keys / carrying objects across parent boundaries:** complex, little benefit for 2D content.
- **Enter/exit animations and the Qt animation system** as part of the basic reconciler.
