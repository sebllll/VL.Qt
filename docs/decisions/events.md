# Events and State Flowing Back to Element Nodes

How events and measured values travel from Qt back to the element nodes in a patch.

## Design
- **Return channel via a feedback.** Each element node (a process node) creates one stable feedback per instance and puts a reference to it into its immutable description. The reconciler binds the Qt object to the feedback, Qt signals write into it, and the element node reads it in `Update()`. The description stays pure data, the state lives in the node, and events arrive where the element was patched.
- **Outputs only on the element node.** There is no listener node and no element-related event output on the renderer. Distribution inside a patch is done with vvvv channels and bindings. One place, idiomatic for vvvv.
- **Pin semantics.** One-off events are a bang/pulse plus a counter, so nothing is lost when several events occur in one frame. States are values (for example `IsHovered`, `MeasuredSize`).
- **Latency.** Events and measured values arrive one frame late, because `processEvents` runs in the renderer after the element nodes have been evaluated. This is also accepted for measured sizes; there is no synchronous measuring.
- **Threading.** Everything runs on the vvvv main thread (which is the Qt thread), without locks. The feedback only buffers between `processEvents` and the next `Update()`.
- **Pointer handling is opt-in.** Only elements that ask for interaction get hover and tap handlers, so content elements pay for no unneeded handlers or hit testing.
- **Always available:** `MeasuredSize` (implicit or actual size, mainly for text) and `SceneBounds` (position in the scene). With interaction enabled: `IsHovered`, `IsPressed`, `OnClick` (pulse plus counter) and `PointerPosition` (local).

### Feedback binding
- Identity in the reconciler stays position plus type (see `reconciler-identity.md`). The description carries the feedback, and the reconciler binds it to the Qt object at that position every frame.
- A Qt signal writes into the feedback that was bound at that moment, that is, the element that was visible in the last frame. This matches the one-frame latency.
- In `ForEach` the node instance is positional anyway (state per iteration index), so feedback and Qt object move together. They only diverge if a patch reorders elements after creation, and rebinding every frame covers exactly that case.
- Inserting or reordering can make state jump (hover, pressed). This behaves like any stateful node in `ForEach` and is accepted; a key pin would remove it (see `reconciler-identity.md`).
- **Switching the feedback of a Qt object:** the reconciler cancels a running press (no `OnClick`). The old feedback receives hover/press = false, the new one the current state. Qt remembers hover/press in the object, so without cancelling there would be ghost clicks on the wrong sink.
- **A node emitting a spread of elements:** the description holds a slot consisting of feedback and index, and the node's outputs are spreads (`IsHovered`, …).
- **One feedback, several Qt objects:** if an element appears several times in the tree (see `reconciler-identity.md`), all occurrences bind the same feedback. Counters and events add up (a click on any occurrence counts), hover and pressed combine with OR, and state values (measured size, `SceneBounds`, error) come from the first occurrence in traversal order.

## Open questions
- **Two-way values** (for example the text of a text input): via a channel on the element? Relevant once input elements exist.
- **Animation events** (`OnAnimationFinished`, `IsAnimating`) belong to an animation node set built on Qt's animation system. The feedback would then carry them, and they depend on the timing decision (see `timing.md`).
- **Further interaction events:** drag (`IsDragging`, `DragDelta`), wheel, keyboard/focus, text input, multitouch, drop.
- **Global renderer output:** raw mouse and keyboard data, or "element under the cursor".
- **`IObservable` in addition to pins:** nice to have.

## Rejected alternatives
- **Callbacks/delegates on the element:** they do not fit immutable descriptions, create a new delegate identity every frame, and would run patch code in the middle of `processEvents`.
- **Channels as the default mechanism for events:** too heavy. Channels are only meant for true two-way values; distribution is done by the patch.
- **Feedback as identity in the reconciler:** identity stays position plus type, the feedback is only bound.
- **A separate listener node by ID/tag:** covered by channels and bindings in the patch.
- **Qt animations as part of the base element set:** a separate topic. Animation is driven per frame from vvvv.
