# Input

What QtNative does with pointer input in a window: the input handlers (Hover, Tap, Point, Wheel, Drag, Touch Points, Pinch), which events each one sends and when, how handlers grab, what happens to the sent state when the feedback changes, and the Qt behaviour the rules are built on. Code: `src/VL.Qt.Native/scene/handlers.*` (the table, the collectors and the Qt objects), `scene/touch_handler.*` (Touch Points and Pinch), `elements/` (drag offset, `TextMask`), the handler QML in `src/VL.Qt.Native/qml/handlers/`. Record layouts are only in `protocol.h`; the way an event reaches a patch (feedback slot, Interaction nodes, channels) is in [events-and-interaction.md](events-and-interaction.md); input into offscreen scenes in [offscreen-input.md](offscreen-input.md).

## Context

An element has no pointer handling of its own. Without an input handler an item is transparent for input and costs no hit test. Qt Quick has input handlers (`QQuickPointerHandler`s) that attach to an item; QtNative makes one per Interaction node and reports their state as events at each poll. Two problems shape the rules: several handlers at one item and several overlapping items compete for the same press, and Qt reports some values in ways that need care (drag translation, pinch after a change of the finger count).

Words: a *handler* is Qt's object, an *interaction* its description in the patch ([glossary.md](../glossary.md)). The *feedback* of a handler is the id its events carry (`feedbackId`, bound by BIND).

## Design

### Lifecycle

A handler is a protocol object of its own (`HandlerEntry`), not an element. CREATE reserves the id. REPARENT creates the QML handler at the element's item (appended to its data, like `Item { HoverHandler {} }`) and applies the properties set before. BIND routes its events to a feedback id. DESTROY of the handler, or of its element, removes it. Several handlers per item are fine; each reports to its own feedback. Events are sent only while the handler is bound.

### Events per handler

| Handler | Qt object | Events | Rule |
|---|---|---|---|
| Hover | `HoverHandler` | `HOVER` (u8) on change | Not blocking: hover reaches every handler under the cursor. No position; positions come from Point only. |
| Tap | `TapHandler`, policy `ReleaseWithinBounds` | `PRESS` (u8) on change, `CLICK` (u32) taps since the last poll, `TAP_COUNT` (u32) on change, `LONG_PRESS` (u32) long presses since the last poll | Exclusive grab on press: the topmost tap handler wins; a tap counts when released inside. `TAP_COUNT` is the count of the last tap (2 = double click), never 0, and stays until the next tap. `ACCEPTED_BUTTONS` / `ACCEPTED_DEVICES` filter. |
| Point | `PointHandler` | `ACTIVE` (u8) on change, `POINTER` (f32 x, y, item-local) while active, on change | Passive grab: presses pass through to the handlers below. A pressed point (touch, pen, accepted button) wins over the hovering mouse. With `ACCEPTED_BUTTONS` 0 the handler takes touch and pen only and an internal `HoverHandler` (mouse and touchpad of `ACCEPTED_DEVICES`) makes the mouse active while it is over the item, so the mouse position is reported without a button; the internal handler is made and removed when the property changes, so no `ACTIVE 1` stays hanging. |
| Wheel | `WheelHandler`, target null | `WHEEL` (f32 x, y) = sum of `angleDelta / 120` since the last poll, only when not 0; once (0, 0) after a poll with motion | Notches; trackpads give fractions. Moves nothing. |
| Drag | `DragHandler`, target null | `ACTIVE` (u8), `DRAG` (f32 x, y) = translation of the running drag since the press in the parent's space, after `DRAG_AXIS` and `DRAG_LIMITS`, on change and once per new drag | See [Drag](#drag). |
| Touch Points | `TouchPointsHandler` (own class) | `TOUCH_POINTS`: count, then per point id, x, y (item-local, logical pixels), pressure, width, height; sorted by id, on change (press, move or release of one point); count 0 once when the last point goes | See [Touch Points](#touch-points). |
| Pinch | `PinchHandler`, target null | `ACTIVE` (u8), `PINCH` (scale, rotation in degrees clockwise, translation x, y, pivot x, y; parent's space) while active, on change and once per new gesture | See [Pinch](#pinch). |

A handler sends only changes against what its feedback got last (`HandlerSent`); the exceptions are the counters (`CLICK`, `LONG_PRESS`, `WHEEL`), which report what happened since the last poll.

### Grabs and hit testing

- **Exclusive grab** (Tap, Drag): the topmost handler takes the press; those below get nothing. This is what makes the topmost element with a `Click` node take the click while `Hover` reaches all.
- **Passive grab** (Point, Touch Points, Pinch): the handler sees the press and leaves it to others, so Tap, Point, Drag and Pinch at one item keep working together.
- **Occlusion** (Touch Points, on by default): a press that an element on top already grabs exclusively (a Tap or Drag handler, a Button) is not taken. Exclusive grabs of handlers at the same item do not count.
- **Text** with at least one handler hit-tests against its painted text rectangle (`TextMask` as `containmentMask`), not the item rectangle. Line- or glyph-exact testing is not done.

### Drag

- `MOVES_ELEMENT` on (default): the item's drag offset is the offset at the drag start plus the translation, clamped to `DRAG_LIMITS` (minX, minY, maxX, maxY of the offset). Off: events only.
- The offset is part of the item's matrix (`Elements::setDragOffset`: translate(offset) × the TRANSFORM around the anchor), one object instead of a matrix and a translate pair, so the order is always right and the item moves in its parent's space whatever its own rotation or scale.
- Every SET TRANSFORM of the item sets the offset to 0 (a running drag goes on from there: `transformEpoch`); layouts (Row, Column) leave it alone: a Row or Column keeps the child's place. Several drag handlers at one item share the offset.
- A Group drags as a whole; its hit area is its size, with Auto Size the extent of the children. Nested drags follow Qt: the innermost item wins.
- After the release the handler keeps the last `DRAG` value until the next drag; a new feedback gets nothing until the next drag.
- **Qt behaviour** (Qt 6.12): `activeTranslation` is the movement of the centroid in scene (window) coordinates, also with a null target; QtNative maps it into the parent's space (equal to the window direction unless a parent is rotated or scaled; that case has no test). It starts with the move after the activation, past the drag threshold, so the first values arrive one move late. `xAxis` and `yAxis` `minimum` / `maximum` clamp only `persistentTranslation` and a target's position, not `activeTranslation`, so they are not used: QtNative clamps the offset itself. `DRAG_AXIS` sets `xAxis` / `yAxis` `enabled`; a drag more than 45 degrees off the enabled axis is not taken and reaches a drag handler of a parent (a child with x only and a vertical drag moves the parent).

### Touch Points

`TouchPointsHandler` has no QML counterpart: it is an own pointer device handler (Qt private API, listed with the other private API in `elements/elements.h`; check on every Qt update). It takes a passive grab on every point pressed inside the item and follows it until the release, also outside the item.

- At most 64 points are sent, because the record length is a u16.
- A point has its own id. `QEventPoint::id` is only unique per device (a pen and the first finger are both 0), so a pen and a finger would share one entry. A new point keeps Qt's id when no other point uses it, otherwise it gets the smallest free id, and keeps it until its release.
- Pressure and size are device dependent and often constant or 0×0. `ACCEPTED_DEVICES` filters (Qt default: all, so the mouse appears as point 0; the node sets touch screen and pen).

### Pinch

`PinchState` composes the active values of the Qt `PinchHandler` into one gesture that does not jump when the finger count changes. Qt ends a pinch (active false, values back to 1/0/0) as soon as the number of points differs from the one it started with and starts a new one later.

- The gesture runs while Qt's pinch is active or at least `MINIMUM_POINT_COUNT` points lie on the item (counted by an internal Touch Points handler without occlusion). A third finger (more than `MAXIMUM_POINT_COUNT`, defaults 2 and 2) pauses it; the values freeze and, when Qt activates again, the new transform is composed with the frozen one, so lifting the finger goes on from the paused value.
- The values describe `p' = pivot + translation + scale · R(rotation) · (p - pivot)` in the parent's space, with the centroid of the first activation as the pivot; applied like that, the content follows the fingers.
- Values count from Qt's activation, which is when the second finger touches. The translation is the movement of the pivot point (the centroid's movement without a change of the finger count); Qt's `activeTranslation` is not used, because it is wrong after a change of the finger count.
- The last `PINCH` comes before `ACTIVE 0`. The 1/0/0 that Qt resets to at the end is never sent. A new feedback gets nothing until the next gesture.

### Change of the feedback

A new feedback (BIND with another id) or DESTROY resets the sent state of the old feedback: it gets `HOVER 0`, `PRESS 0`, `ACTIVE 0` if 1 was sent, and `WHEEL (0, 0)` after motion, so the old slot does not keep a state its handler no longer reports. The new feedback gets the current state with the next poll.

- A running press of a Tap handler is cancelled for the new feedback: no `PRESS 1` and no tap until the next press. Taps and long presses not yet polled are dropped, as is wheel motion, and `TAP_COUNT` starts at the first tap of the new feedback.
- The last drag belonged to the old feedback and is cleared; a running drag goes on for the new one.

### Order of records

`Scene::poll` collects once per poll cycle, in this order: window geometry, measured values of the elements (including the changes and signals of a user QML element), then the handlers. A poll buffer that is too small leaves the records queued and the retry only drains, so a record is never collected twice. Within one handler the order is that of its collector: Tap `PRESS`, `CLICK`, `TAP_COUNT`, `LONG_PRESS`; Point `ACTIVE`, `POINTER`; Drag `ACTIVE`, `DRAG`; Pinch `ACTIVE 1`, then the values, at the end the last values, then `ACTIVE 0`. Across handlers there is no promised order (the handlers live in a hash table); each handler's records go to its own slot.

## Consequences

- A change of the grab rules (the gesture policy of Tap, passive grabs) changes which handler gets a press; `HandlerTest` and the multitouch tests protect them.
- A new handler type needs a QML component or class, a `collect…` function in `scene/handlers.cpp`, the record in `protocol.h` and its C# mirror; the drift tests check the C# side.
- Touch Points and Pinch use Qt private API; a Qt update can break them. The SmokeTest scenarios show it.
- The drag offset is state in QtNative that C# does not see; it resets with SET TRANSFORM.

## Alternatives considered

- **Pointer handling on every element:** every element would hit-test and take input; opt-in keeps an element without an Interaction node transparent for input.
- **A matrix and a translate object for the drag offset:** the order of the two had to be kept right; one matrix object holds translate × transform.
- **Qt's `activeTranslation` for Pinch:** wrong after a change of the finger count, see [Pinch](#pinch).

## Open questions

None.

## Evidence

Qt 6.12: the drag threshold, `activeTranslation` and axis behaviour were found with the SmokeTest scenarios `drag*`; the pinch behaviour with the multitouch scenarios. Rerun: `dotnet test src/VL.Qt.SmokeTest -c Release` (files `HandlerTest*.cs`, `InteractionTest.Multitouch.cs`).
