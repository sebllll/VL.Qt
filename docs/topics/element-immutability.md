# Element Descriptions and Immutability

Why elements are immutable descriptions, what that guarantees, and where Qt-side state deliberately lives outside of them.

## Principle
**vvvv describes, Qt works.** The C# objects in a patch are simple, immutable descriptions, as far as is possible and reasonable. Qt objects, GPU resources and runtime state belong to the reconciler or to the node that created them. Whoever patches does not deal with lifetime or resources: nothing needs disposing, descriptions can be passed around freely and hung into the tree several times.

## How it is realised
- `Element` is an immutable description (`init` properties, `ImmutableArray<Element> Children`).
- The reconciler uses reference equality as a shortcut: a reference-equal description means the subtree is unchanged (see `decisions/reconciler-identity.md`).

## Deliberate exceptions
Some state exists only in Qt and changes without a new description arriving:
- Video position.
- Interaction state: hover, pressed, focus, text field input. It flows back through feedback or channels (see `design/events.md`).
- `QtImage` as a handle: its loading state changes. It belongs to the node that created it and needs no dispose.

Consequence: the same instance means "the same description", not "the same picture". Code that needs to detect visual change, for example a tooltip that has to follow an interaction state, cannot rely on reference equality alone.

New features are measured against the principle. Exceptions need a justification and must not impose lifetime duties on the user.

## Rejected alternative
Mirroring Qt state into the description so that "same instance" would mean "same picture". It would require copying video position, hover state, text input and loading state into C# every frame, for no gain outside a few special cases, and it would blur the line between description and runtime state.
