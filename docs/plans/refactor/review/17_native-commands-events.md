# Review 17 – QtNative: commands, events, input

Claude Opus 5.5, 2026-11-19. Files read in full: `src/VL.Qt.Native/commands.cpp`, `commands.h`, `events.cpp`, `events.h`, `touch.cpp`, `touch.h`, `feedback.cpp`, `feedback.h`. Looked up with targeted searches: `protocol.h` (opcodes, type ids, header constants), `elements.h` (`ApplyResult`, `Value`, `Node`), `vlqt.cpp:1795-1841` (apply/poll exports), `elements.cpp:1811` (`flushLayerOff`), `src/VL.Qt/SceneCore.cs:214-234` (poll retry), `src/VL.Qt.SmokeTest/ApplyTest.cs:146-233` (error cases). Not read: the rest of `protocol.h`, `elements.cpp`, `effectchain.cpp`, `vlstyle.cpp`, `qmlitem.cpp`.

## 1 Summary

The decoder is safe as far as I could check: every command is bounds-checked against the buffer before its payload is touched, every op checks its payload length exactly or entry by entry with 64-bit arithmetic for counts, unknown opcodes, ids, types and value types stop the apply with a structured error. I found no out-of-bounds read. The events side is sound in its main idea: almost every event is produced inside `poll` from "sent" state, so the queue cannot overflow, and a new sink always gets the current state. What hurts the reader is structure: `Scene` (one public `struct`) is decoder, object table, clear/feedback/space/debug state, event queue and measurement bookkeeping at once, and `events.cpp` reaches into its fields from free functions. Adding a command touches six places, adding a handler property three, and the named-entry wire format is parsed by three near-copies.

The changes that matter most:
1. Fix two real defects: a stale `lastApplyError` after an invalid buffer (NCMD-02) and a SET_EFFECT_CHAIN that changes the scene although it fails (NCMD-03); remove the false "stub" comment of `setUniforms` (NCMD-01).
2. Make `poll` collect once and drain, so a too-small buffer does not run the collectors twice (NCMD-04).
3. Split `Scene` and `events.cpp` along their real responsibilities (NCMD-05, section 4).
4. One bounds-checked wire reader and one op table for the decoder (NCMD-06, NCMD-07).
5. Move the 112-line behaviour spec in `events.h` to `docs/input.md` (NCMD-11) and add decoder robustness tests (NCMD-15).

## 2 What works and must survive

- **Bounds discipline of `Scene::apply`** (`commands.cpp:342-455`): header length check, then `off + size > len` before any payload read, then per-op exact `plen` (`expect`) or entry-wise validation. Variable counts are widened to 64 bit before comparison (`commands.cpp:696-703`, `940-948`). Keep this order in any rewrite; the wire reader of NCMD-06 must make it structural, not weaker.
- **Validate, then apply** for SET_UNIFORMS, SET_QML_PROPERTIES and SET_EFFECT_CHAIN: a malformed command changes nothing (except NCMD-03). Content problems (unknown uniform name, failed QML conversion) are element errors (`EV_ERROR`), not apply failures. This split is right.
- **Stop at the first bad command, earlier ones stay applied**, with a structured error (`ApplyError`: index, id, op, prop, reason, `commands.h:119-126`). The C# side resyncs on it (`src/VL.Qt.Tests/ResyncTests.cs`). See question Q1.
- **Events are produced at poll time from "sent" state.** Only `resetSent` (BIND to a new handle, DESTROY of a handler) pushes during apply. So the queue size is bounded by the scene, no overflow policy is needed, and a new sink gets the current state with the next poll (`bindNode`, `resetSent`). Keep: do not move to signal-driven pushing.
- **Measure on change** (`events.cpp:891-927`, `931-962`): geometry signals mark ids, the poll walks only marked subtrees with a stamp, `pollIds` lists only nodes with media/error/clicks/value. A static scene reads no geometry. Ids of destroyed or reused nodes drop out by lookup and stamp, not by pointer: this is what makes reused ids safe.
- **Handlers look themselves up by id** (`DragState` via `handlerNodes.find(id_)`, `events.cpp:189-213`), not by stored `Node*`. Keeps them safe across map rehashes and destruction.
- **Former effect sources are deleted one frame late** (`commands.cpp:534-550`, `deleteRetired`). The reason (Qt's MultiEffect source proxy compares by pointer, a new item at the same address left the effect empty) must survive in the comment after K1 removes `#123`.
- **Point handlers are re-attached after other handlers** (`raisePointHandlers`, `events.cpp:137-154`): Qt delivers to the handlers of an item from the last attached; without this a Tap handler starves a Point handler.
- **Threading:** everything in this area runs on the GUI thread (apply, poll, polish, handler slots, `TouchPointsHandler` event delivery) except `FeedbackView::updatePaintNode` (render thread, during sync while the GUI thread is blocked), which talks back only through a queued connection (`feedback.cpp:44-52`). Keep that boundary.
- **Hot paths** (must not allocate per frame): the apply loop for SET/BIND/REPARENT (`SET` strings are `fromRawData` views), `Scene::polish` early-out (`commands.cpp:1249-1251`), `poll` → `collectGeometry` (stack and stamps reused), `collectMeasurements` over `pollIds`, `collectHandlers`, `pushEvent` (`events.resize(0)` keeps capacity). Broken today: NCMD-10.

## 3 Findings

### NCMD-01 Remove the false "interface stub" comment of SET_UNIFORMS
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:807-808`, `src/VL.Qt.Native/commands.h:1-3`
- **Finding:** `setUniforms` begins with "Interface stub (#437 task 1): validates the command completely …, applies nothing yet. Task 3 hands the entries to the ShaderEffect item". The same function applies the entries (`beginUniforms`, `setUniform`, `endUniforms`, `commands.cpp:871-889`). A reader trusting the comment would look for the apply elsewhere. `commands.h:2-3` ("plan 05 only adds entries, no new exports") is a plan step of the past.
- **Proposal:** Replace with: "Validates all entries first (wire format: protocol.h "SET_UNIFORMS"), then hands them to the ShaderEffectItem; unknown names or wrong types become the element's error text (`ShaderEffectItem::rebuild`)." Header of `commands.h`: "Decoder of the command buffer (`vlqt_window_apply`). Wire format: protocol.h."
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/commands.h`
- **Depends on:** none
- **Verification:** none needed (comment only).

### NCMD-02 Reset `lastApplyError` before the buffer check
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:336-340`, read by `src/VL.Qt.Native/vlqt.cpp:1823`
- **Finding:** `apply` returns `ApplyBadLength` for a null buffer or negative length before `lastApplyError = ApplyError{}`. `vlqt_window_get_apply_error` then reports the previous apply: `rc 0` (success) or an older, unrelated command. The C# error output and resync logic read that structure.
- **Proposal:** Assign `lastApplyError = ApplyError{}` first; on the invalid-buffer path set `lastApplyError = ApplyError{ApplyBadLength, 0, 0, 0, 0, err}`. Better: route every failure through one `fail(rc, index, id, op, prop, reason)` helper so the string `err` and `lastApplyError` cannot diverge.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`
- **Depends on:** none
- **Verification:** new case in `src/VL.Qt.SmokeTest/ApplyTest.cs`: a failing apply, then `vlqt_window_apply(w, null, 4)`; `vlqt_window_get_apply_error` must report `rc -2`, command 0.

### NCMD-03 A failing SET_EFFECT_CHAIN must not park masks and textures
- **Kind:** correctness
- **Severity:** must
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:1008` (claim), `1194-1239`; `src/VL.Qt.Native/elements.cpp:1653`
- **Finding:** The comment says "Validates the whole command first …, so a bad one changes nothing". After the wire validation, the function parks every new mask and texture element under the root, hidden, with `maskHidden = true` (`1197-1229`), and only then calls `elements.setEffectChain`, which can fail (`elements.cpp:1653`: `ec->apply(...)` returns `ok` and fills `err`). On failure the parked elements stay hidden and out of their place in the tree; masks are not in `after`, so their `maskHost` stays null and nothing un-parks them until the patch REPARENTs or destroys them. In the patch an element disappears because an unrelated chain was rejected. `listPolled` and the `maskHost` bookkeeping also run on the failure path. I did not trace which inputs make `EffectChain::apply` fail; confirm by reading `effectchain.cpp` for its `return false` paths.
- **Proposal:** Split into `validateEffectChain(host, chain, masks, err)` (wire + references + everything `EffectChain::apply` can reject that does not need the items changed) and `applyEffectChain(...)`. Park masks/textures only after validation passed. If `EffectChain::apply` can still fail after that (resource creation), undo the parking for items that were not parked before (`maskHidden` was false): restore parent (none) and visibility. Fix the comment to say exactly what is atomic.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/elements.cpp`, `src/VL.Qt.Native/effectchain.cpp`
- **Depends on:** none
- **Verification:** SmokeTest effect-chain scenario: a chain with a mask element and an invalid template parameter that `EffectChain::apply` rejects; the mask element must still be drawn where it was.

### NCMD-04 `poll`: collect once, then drain
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:1260-1274`, `src/VL.Qt.Native/vlqt.cpp:1835-1840`, `src/VL.Qt/SceneCore.cs:221-227`
- **Finding:** `poll` always runs `deleteRetired`, `collectWindowGeometry` and `collectMeasurements`, then returns `-n` if the buffer is too small and keeps the events. The C# side retries once with `max(-n, 2·len)`. The retry collects again and appends more events (a handler that changed in between, a new EV_TOUCH_POINTS), so `-n` from the first call is not a size guarantee; if the second call is too small again, C# reports a poll error for that frame. The retry also runs `polish` again (`vlqt.cpp:1839`). The contract "returns -required" is therefore not true. `-1` for a null window shares the value space with "-1 byte required" (C# works around it with `n < -1`).
- **Proposal:** Separate collecting from copying: `Scene::collectEvents()` runs the collectors once per poll cycle and sets `collected = true`; `Scene::drainEvents(out, cap)` copies and clears, and returns `-required` without collecting when `collected` is still true from a call whose drain failed. Export contract in `vlqt.h`: `>= 0` bytes, `-required` (always `>= VLQT_HEADER_SIZE + 8`) for a small buffer, a distinct `VLQT_POLL_ERROR` (for example `INT_MIN`) for a null window or exception.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/commands.h`, `src/VL.Qt.Native/vlqt.cpp`, `src/VL.Qt.Native/vlqt.h`, `src/VL.Qt/SceneCore.cs`, `src/VL.Qt/Native.cs`
- **Depends on:** none
- **Verification:** SmokeTest `events`: poll with `cap = 1` while a handler is active, then with `-n`; the second call must succeed and return exactly `n` bytes.

### NCMD-05 Split `Scene` along its responsibilities
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.h:30-154`, `src/VL.Qt.Native/commands.cpp:173-328`, `src/VL.Qt.Native/events.cpp` (free functions using `scene.nodes`, `scene.ids`, `scene.handlerNodes`, `scene.measureQueue`, `scene.pollIds`, `scene.events`, `scene.elements`, `scene.handlers`)
- **Finding:** `Scene` is a `struct` whose state is public and edited from `events.cpp`, `elements.cpp` and `vlqt.cpp`. It holds five things: the command decoder (`apply` and eight per-op methods), the object table (`nodes`, `ids`, `handlerNodes`, retire list, mask bookkeeping), the window-facing state (clear, clear colour, feedback layer, space/root origin, debug overlay, window swap), the event queue (`events`, `pushEvent`, `poll`), and measurement bookkeeping (`measureQueue`, `measureStack`, `measureStamp`, `pollIds`, `pollStamp`, `measuredLastPoll`, `geometryDirty`, `reportedFramePos`, `geometryWatch`). A newcomer who wants to change the clear colour opens a file called `commands.cpp` and reads 1,300 lines; one who wants to add an event cannot tell which fields of `Scene` they may touch. `events.cpp` mixes input handlers, element measurement, QML events and window geometry in one 1,058-line file.
- **Proposal:** Target in section 4. In short: `Scene` (object table + structural ops, `class`, private state) in `scene.h/.cpp`; `commands.cpp` keeps only the decoder; `ClearState` (clear, colour, feedback layer) moves next to `FeedbackLayer`; `EventQueue` gets its own type; `events.cpp` splits into `handlers.cpp` (input handlers) and `measure.cpp` (element and window state reporting). Free functions that take `Scene&` become members of the type whose state they edit. Do the split before NCMD-06/07 so those land in the new files.
- **Effort:** L, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `commands.h`, `events.cpp`, `events.h`, `feedback.cpp`, `feedback.h`, new `scene.cpp/.h`, `eventqueue.cpp/.h`, `clearstate.cpp/.h`, `handlers.cpp/.h`, `measure.cpp/.h`, `src/VL.Qt.Native/CMakeLists.txt`, includes in `vlqt.cpp`, `elements.cpp`, `perf.cpp`, `debug.cpp`, `effectchain.cpp`
- **Depends on:** none
- **Verification:** `tools/build.ps1 -Test` (all SmokeTest scenarios, especially `apply`, `events`, `measure`, `clear-pin`, the handler scenarios), `--drift`.

### NCMD-06 One bounds-checked wire reader and one parser for named entries
- **Kind:** structure
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:35-43` (`rd`, `align4`), named entries `830-885` (SET_UNIFORMS), `916-1002` (SET_QML_PROPERTIES), `1115-1150` (SHADER link uniforms); element lookup with the same four error branches `786-796`, `813-823`, `901-911`, `1013-1023`
- **Finding:** The safety of the decoder rests on offset arithmetic that is written out by hand in every op: `plen - off < 4`, `off + align4(4 + nameLen)`, `next > plen`. The entry format "u16 value type, u16 name length, name padded to 4, value padded to 4" is parsed three times with small differences (QML allows strings, uniforms do not), and SET_UNIFORMS and SET_QML_PROPERTIES repeat the offset walk in a second pass to apply. The element lookup with "window root / input handler / unknown id" errors is copied four times. A newcomer adding a value type must find and change all copies; one missed bounds check is an out-of-bounds read. `off + size > len` (`commands.cpp:363`) can overflow `int` for buffers near 2 GB (theoretical).
- **Proposal:** New `src/VL.Qt.Native/wire.h`: `class WireReader { WireReader(const unsigned char* p, int len); bool u16(quint16&); bool u32(quint32&); bool f32(float&); bool bytes(int n, const unsigned char*&); bool align(); int remaining() const; }` — every read checks `remaining()` first, comparisons as `n > remaining()` (no overflow). Plus `struct NamedEntry { quint16 type; QByteArrayView name; const unsigned char* value; int valueLen; }` and `bool readNamedEntry(WireReader&, NamedEntryRules, NamedEntry&, std::string& err)` used by all three commands; validation collects entries into a scratch `std::vector<NamedEntry>` (member, capacity reused), and the apply pass iterates that vector instead of re-walking offsets. `valueSize`/`fixedValueSize`/`verbPoints`/`samplerValueOk` move to `wire.h`. One `Node* Scene::findElement(quint32 id, const char* what, int& rc, std::string& err)` replaces the four lookups.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, new `src/VL.Qt.Native/wire.h`, possibly `src/VL.Qt.Native/vlstyle.cpp` (SET_STYLE payload, not read)
- **Depends on:** NCMD-05 (file layout), NCMD-15 (tests first)
- **Verification:** NCMD-15 cases; SmokeTest `apply`, shader/effect-chain and QML scenarios.

### NCMD-07 One op table instead of six places per command
- **Kind:** architecture
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:45-63` (`opName`), `369-371` (`hasId`), `376-442` (dispatch switch), `commands.h:129-137` (per-op methods); handler properties `src/VL.Qt.Native/events.cpp:474-484` (`known`, `expected`), `490-547` (apply switch)
- **Finding:** Adding a command means editing the opcode enum in `protocol.h`, `Protocol.cs`, `opName`, the `hasId` list, the switch with its `expect(n)`, and a new private method. Forgetting `hasId` silently drops the id from the structured error; forgetting `opName` prints `?`. Adding an input-handler property means extending a boolean expression of supported types, a nested ternary of value types and a switch. Neither is discoverable without reading the whole function.
- **Proposal:** In `commands.cpp`: `struct OpInfo { quint16 op; const char* name; bool hasId; int fixedLen; /* -1 = variable */ int (Scene::*handler)(WireReader&, std::string&); };` and `constexpr OpInfo kOps[]`, looked up by a small `findOp(op)`. `apply` checks `fixedLen`, reads the id for the error once, and dispatches. Document in `docs/architecture.md` "Adding a command": protocol.h, Protocol.cs, `kOps` entry + handler, `--drift`. For handlers: `struct HandlerProp { quint16 prop; quint16 valueType; quint32 typeMask; };` with `constexpr HandlerProp kHandlerProps[]` in `handlers.cpp`; `setHandler` checks support and value type from the table, then applies in one switch.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `commands.h`, `events.cpp` (or `handlers.cpp`), `docs/architecture.md`
- **Depends on:** NCMD-05, NCMD-06
- **Verification:** SmokeTest `apply` (error texts with op names and ids), handler scenarios (`tap`, `drag`, `pinch`, `touch-points`, `point`, `wheel`).

### NCMD-08 Event payload limits in one place, no silent drop
- **Kind:** correctness
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:1276-1288` (`pushEvent`), `src/VL.Qt.Native/events.cpp:42` (64 points), `748` (60,000), `801` (65,000), `833`, `851` (QML records skipped), `1004` (EV_ERROR 4,096)
- **Finding:** The record length is `u16`; `pushEvent` computes `quint16(8 + payloadLen)` without a check, so a caller that passes more than 65,527 bytes produces a corrupt stream (wrapped length, misaligned next record). Today every caller caps, but with four different constants spread over the file. Two caps are defects: EV_ERROR is cut at byte 4,096 without regard to UTF-8 (`events.cpp:1004`), so C# decodes a broken last character, while `pushValue` does it right (`784-785`); a QML property or signal record over 65,000 bytes (a long string property) is skipped silently (`833`, `851`): the patch's output never updates and nothing says why.
- **Proposal:** `protocol.h`: `VLQT_EVENT_PAYLOAD_MAX = 65535 - 8`. `EventQueue::push` asserts `payloadLen <= VLQT_EVENT_PAYLOAD_MAX` (`Q_ASSERT` plus a clamp that drops the record and logs in release). One helper `utf8Prefix(const QByteArray&, qsizetype max)` for EV_ERROR and EV_VALUE. An oversized QML record becomes an error line of the element (`QmlItem::rejectRootProperty`-style: "property x: value too large for the event (n bytes)").
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/protocol.h`, `src/VL.Qt/Protocol.cs`, `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/qmlitem.cpp`
- **Depends on:** none
- **Verification:** SmokeTest QML scenario with a 70,000-byte string property (error line appears); unit test in `src/VL.Qt.Tests` for the EventReader with an error text cut at a multi-byte character.

### NCMD-09 Give `HandlerNode` clear ownership
- **Kind:** lifetime
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/events.h:211-242`, `src/VL.Qt.Native/events.cpp:567-585`, `436-457`
- **Finding:** `HandlerNode` holds six raw pointers (`object`, `tap`, `drag`, `hover`, `points`, `pinch`). Who deletes what is spread over comments: `tap`/`drag`/`pinch` die with `object` (QObject children), `hover` is deleted explicitly, `points` is the handler itself for Touch Points but a separate object for Pinch, so `destroyHandler` deletes it only `if (handler.pinch)` (`events.cpp:579-580`). All are also QObject children of the owner item, so `~Scene` relies on the item deletion instead. A reader cannot tell from the type which pointer owns; a change of order (delete `object` before `points`) is not caught by anything. The struct also mixes three concerns: configuration (accepted buttons, axis, limits, point counts), Qt objects, and "sent" state (eleven `sent*` fields, reset field by field in `resetSent`, `events.cpp:77-81`).
- **Proposal:** Split `HandlerNode` into `HandlerConfig` (the stored properties), `HandlerObjects` (the Qt side) and `HandlerSent` (like `SentState`, reset with `sent = {}`). In `HandlerObjects` use `QPointer<QObject> object, hover` and a separate `QPointer<TouchPointsHandler> pinchTracker` for the Pinch helper (Touch Points uses `object` cast once at creation). Give it `void release()` that deletes in the one correct order and is called from `destroyHandler`. `resetSent` then becomes "push the 0 records for set flags, then `sent = {}`".
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.Native/events.h`, `src/VL.Qt.Native/events.cpp` (or `handlers.h/.cpp` after NCMD-05)
- **Depends on:** NCMD-05
- **Verification:** SmokeTest `pinch`, `pinch-node`, `point`, `point-tap`, `touch-points`, `drag` and a DESTROY/BIND churn case in `events`.

### NCMD-10 Remove per-frame allocations from the poll and apply paths
- **Kind:** performance
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/events.cpp:685-700` (`collectTouchPoints`), `events.cpp:814-815` (`thread_local` scratch), `commands.cpp:964` (`QString name` per QML property), `commands.cpp:1030`, `1116`, `1143` (strings and `QByteArray` per effect-chain link and uniform)
- **Finding:** `collectTouchPoints` builds a new `QByteArray` every poll for every bound Touch Points handler while a finger is down, only to compare it with the last payload: one heap allocation per handler per frame during touch. `setQmlProperties` creates a `QString` per entry per apply; an animated QmlElement property sends that every frame. `setEffectChain` builds the error prefix `ue` (> 15 characters, past the small-string buffer) for every uniform before any error, and copies each name into a `QByteArray`; an animated effect sends the chain every frame (to be confirmed on the C# side, see section 6). `collectQml` uses `thread_local` vectors as scratch, which works but hides that they are per-scene state.
- **Proposal:** Touch points: a member scratch `QByteArray` in the handler state, filled with `resize` + `memcpy` (capacity kept), compared with `sentTouch`, swapped on change. QML properties: pass `QByteArrayView` names to `QmlItem::setRootProperty` and let `QmlItem` look up a cached `QMetaProperty` by name (it already holds a table, see review of `qmlitem`). Effect chain: build error prefixes only on the error path (a lambda `at(l, i)` called in the failing branch). Scratch vectors of `collectQml`: members of the measurement state.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/qmlitem.h/.cpp`
- **Depends on:** none
- **Verification:** `tools/build.ps1 -Test` (`touch-points`, QML scenarios); allocation count with the profiler as in `docs/profiling.md` on a touch drag.

### NCMD-11 Move the input behaviour spec out of `events.h`
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/events.h:1-112`, `src/VL.Qt.Native/touch.h:26-30`, `70-78`, `src/VL.Qt.Native/feedback.h:3-29`
- **Finding:** The head of `events.h` is a 112-line specification: every event, its payload, when it is sent, Qt behaviour found in spikes (drag thresholds, `activeTranslation` quirks, axis behaviour), and the sink rules. It is the best description of input in the repository, but it sits in a header a vvvv user never opens, and `docs/` has no page about input (`docs/README.md` lists none). Payload formats are also in `protocol.h`, so they exist twice. Cross-ordering is not stated: all EV_QML_PROPERTY records of a poll come before all EV_QML_SIGNAL records (`events.cpp:825-853`), so the order "property changed, then signal" inside QML is not kept. `feedback.h:3-29` is a good design note, but it is in the right place (one file, one subject) and can stay shorter.
- **Proposal:** New `docs/input.md`: handler types and their events, grabs (exclusive vs passive, occlusion), sink change rules, Qt behaviour notes (drag, pinch), ordering guarantees (per handler in order; property records before signal records within one poll; reset records of an apply before the next poll's records). Payload layouts stay only in `protocol.h`. `events.h` head shrinks to five lines: what the file does and a link. `touch.h` keeps the two class summaries at about five lines each, the pinch maths goes to `docs/input.md`. Add the page to `docs/README.md`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/events.h`, `src/VL.Qt.Native/touch.h`, new `docs/input.md`, `docs/README.md`
- **Depends on:** NCMD-05 (if `events.h` is split, the head goes with it)
- **Verification:** none (docs).

### NCMD-12 Decide the invariant of `Node::xf`
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** null checks `src/VL.Qt.Native/events.cpp:195-196`, `878`; unchecked uses `events.cpp:364`, `904`, `941`, `commands.cpp:218`, `531`; creation `commands.cpp:480-484`
- **Finding:** `Node::xf` is created by `Elements::initGeometry` right after CREATE and dereferenced without a check in five places, but checked for null in two others. The reader cannot tell which is right. `Node{item, type, 0, 0, nullptr}` (`commands.cpp:480`) initializes by position against a struct with many fields; inserting a field before `xf` silently shifts the values.
- **Proposal:** Make it an invariant: `Node` is only created through one `Scene::makeNode(item, type)` that calls `initGeometry`; document "`xf` is never null for a node in `nodes`" at the field and remove the two checks. Replace the positional initializer with member assignments.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/events.cpp`, `src/VL.Qt.Native/elements.h`
- **Depends on:** none
- **Verification:** SmokeTest `drag`, `measure`.

### NCMD-13 Handler properties: store only what Qt accepted
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/events.cpp:525-529`, `537-541`, `441-444`
- **Finding:** `setHandler` stores `minimumPointCount`/`maximumPointCount` and `acceptedButtons`/`acceptedDevices` before calling `setProperty`; when Qt rejects the value the apply fails with `ApplyBadValue`, but the stored value stays and is applied again by a later REPARENT. `reparentHandler` ignores the result of `setProperty` for the point counts.
- **Proposal:** Assign the stored value only after `setProperty` succeeded (or when there is no object yet); in `reparentHandler` log a failed `setProperty` like `applyAccepted` does.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/events.cpp`
- **Depends on:** NCMD-07 (if the handler table lands first, fix it there)
- **Verification:** SmokeTest `pinch-node`.

### NCMD-14 SET_STYLE: ask the type table, not an id range
- **Kind:** structure
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:798`, `src/VL.Qt.Native/protocol.h:306-311`
- **Finding:** "Controls elements only" is checked as `type < VLQT_TYPE_BUTTON || type > VLQT_TYPE_TEXT_FIELD`, i.e. it relies on the Controls types being numbered 10..15 without gaps. A new Controls type with id 16 or a non-Controls type inserted at 12 breaks it silently.
- **Proposal:** Add `bool styled` to `TypeDef` (elements table) and check `findType(type)->styled`.
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/elements.h`, `src/VL.Qt.Native/elements.cpp`
- **Depends on:** none
- **Verification:** SmokeTest styles scenario.

### NCMD-15 Decoder robustness tests for the entry-based commands
- **Kind:** tests
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt.SmokeTest/ApplyTest.cs:146-233`; untested paths `src/VL.Qt.Native/commands.cpp:741-760` (path verbs), `828-869`, `916-958`, `1025-1165`
- **Finding:** `ApplyTest` covers the header level well (truncated buffer, wrong payload length, unknown op/id/type/property, duplicate id, subtree cycle). It has no case for malformed entries of SET_UNIFORMS, SET_QML_PROPERTIES and SET_EFFECT_CHAIN (name length 0 or > 255, entry past the payload, string length past the payload, misaligned `bodyLen`, unknown link kind, wrong shader count, non-zero reserved field, trailing bytes), nor for path values (unknown verb, point count mismatch). These are exactly the paths NCMD-06 rewrites. There is no randomized test. K6 in this area: `Scene::measuredLastPoll` exists only for `vlqt_test_measure_count` (`vlqt.cpp:2060`); the same check could count EV_MEASURED/EV_SCENE_BOUNDS records in the poll buffer, which the SmokeTest already reads.
- **Proposal:** Extend `ApplyTest.cs` with one `Err(...)` per rejection branch listed above, each asserting rc and a substring of the reason. Add a SmokeTest scenario `apply-fuzz`: take the recorded traces of `ReplayTest`, truncate each command buffer at every 4th byte and flip single bytes (fixed seed); every apply must return `0` or a negative `ApplyResult`, never crash, and `vlqt_window_get_apply_error` must agree with the return code. Replace `vlqt_test_measure_count` by counting records in the existing measure scenario, then delete `measuredLastPoll`.
- **Effort:** M, mechanical: no
- **Touches:** `src/VL.Qt.SmokeTest/ApplyTest.cs`, new `src/VL.Qt.SmokeTest/ApplyFuzzTest.cs`, `src/VL.Qt.SmokeTest/Program.cs`, `src/VL.Qt.Native/vlqt.cpp`, `src/VL.Qt.Native/commands.h`, `src/VL.Qt.Native/events.cpp`, the measure scenario file
- **Depends on:** none (do it before NCMD-06)
- **Verification:** the new scenarios themselves, under `tools/build.ps1 -Test`.

### NCMD-16 Small naming and duplication fixes
- **Kind:** naming
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:221-224` and `499-502` (video release twice), `commands.cpp:678` (`Scene::set`), `commands.cpp:36` (`rd`), `events.h:270` and `events.cpp:282-306` (`comps[6]` by magic index), `events.h:138` (`kInf` in the `vlqt` namespace of a header), `commands.h:30` (`struct Scene` with a private section)
- **Finding:** The video release sequence (stop player, release audio tap) is written twice, once in `~Scene` and once in `destroySubtree`; a third step would be added to one only. `Scene::set` reads like a generic setter, it is SET_PROPERTY. `rd` does not say "little-endian". `comps[0..5]` is indexed by numbers explained in a trailing comment. `kInf` is exported to every includer of `events.h` for one default value. `Scene` has invariants and a private part but is declared `struct`.
- **Proposal:** `void Scene::releaseVideo(Node&)` used by both. `set` → `setProperty`, `rd<T>` → `readLE<T>` (moves to `wire.h`, NCMD-06). `enum class HandlerComponent { Hover, Tap, Point, Wheel, Drag, Pinch, Count }` indexing `comps`. `kInf` → `std::numeric_limits<float>::infinity()` at the one use. `struct Scene` → `class Scene` (with NCMD-05).
- **Effort:** S, mechanical: yes
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `commands.h`, `events.cpp`, `events.h`
- **Depends on:** NCMD-05, NCMD-06
- **Verification:** `tools/build.ps1 -Test` (`video` scenario for the release helper).

### NCMD-17 REPARENT of a parked chain texture is not checked (suspicion)
- **Kind:** correctness
- **Severity:** could
- **Breaking:** no
- **Where:** `src/VL.Qt.Native/commands.cpp:636-651`, `1204-1229`
- **Finding:** A mask attached to a chain cannot be REPARENTed (`maskHost` check, `636-639`). A texture element of a SHADER link is parked the same way (`maskHidden = true`, `u.refItem = tex.item`) but has no such check: a REPARENT puts it into the tree and makes it visible while the chain still samples it through its layer. Whether that draws it twice or breaks the layer I could not confirm without `effectchain.cpp`; the comment "parked until REPARENT or DESTROY" suggests it is allowed on purpose. Confirm with a SmokeTest: chain with a texture element, then REPARENT that element under the root, grab a frame.
- **Proposal:** If the result is wrong: track `textureHost` like `maskHost` and reject the REPARENT with "element n is the texture of a chain link of m", or detach the texture from the chain (the link then reports "texture element has a parent" as an EV_ERROR line, as it does at SET time).
- **Effort:** S, mechanical: no
- **Touches:** `src/VL.Qt.Native/commands.cpp`, `src/VL.Qt.Native/elements.h`
- **Depends on:** none
- **Verification:** the SmokeTest case described above.

## 4 Target structure

`src/VL.Qt.Native/` (namespace `vlqt`, nothing here is exported; the C ABI stays in `vlqt.h/.cpp`):

| File | Types | Responsibility | Change |
|---|---|---|---|
| `wire.h` | `readLE<T>`, `align4`, `WireReader`, `NamedEntry`, `readNamedEntry`, `valueSize`, `fixedValueSize`, `verbPoints`, `samplerValueOk` | bounds-checked reading of the command wire format | new, from `commands.cpp` anonymous namespace |
| `commands.h/.cpp` | `CommandDecoder` (or free `applyCommands(Scene&, buf, len, ApplyError&)`), `OpInfo`, `kOps` | decode loop, per-op decoding, structured error | split: keeps only the decoder part of `commands.cpp` |
| `scene.h/.cpp` | `class Scene` | object table (`nodes`, `ids`, `handlerNodes`), create/destroy/reparent/reset, masks, retire list, `findElement`, `releaseVideo`, window swap orchestration, `apply`/`poll` entry points | new, from `commands.h/.cpp` (`struct Scene` → `class Scene`) |
| `clearstate.h/.cpp` | `ClearState` | Clear flag, clear colour, `FeedbackLayer` lifetime, software-backend warning, `release()/restore(window, root)` | new, from `Scene::setClear/setClearColor/applyClearColor/releaseFeedback/restoreFeedback` |
| `feedback.h/.cpp` | `FeedbackLayer`, `FeedbackView` (cpp only) | layer items of Clear off | stays |
| `eventqueue.h/.cpp` | `EventQueue` | `push`, `pushFlag`, `pushUtf8(capped)`, `collected` flag, `drain(out, cap)` | new, from `Scene::events/pushEvent/poll` and `events.cpp:pushFlag` |
| `handlers.h/.cpp` | `HandlerNode` (`HandlerConfig`, `HandlerObjects`, `HandlerSent`), `Handlers`, `TapState`, `DragState`, `TextMask`, `kHandlerProps`, `reparentHandler`, `setHandler`, `bindHandler`, `destroyHandler`, `collectHandlers` | input handlers | new, from `events.h/.cpp` (`TextMask` exists for handlers) |
| `measure.h/.cpp` | `SentState`, `MeasureState` (queue, stack, stamps, `pollIds`, window geometry fields, `geometryWatch`), `watchGeometry`, `markGeometry`, `listPolled`, `collectMeasurements`, `collectQml`, `pushValue`, `watchWindowGeometry`, `collectWindowGeometry` | element and window state reporting | new, from `events.h/.cpp` and the measurement fields of `Scene` |
| `touch.h/.cpp` | `TouchPointsHandler`, `PinchState` | Qt pointer handlers of our own | stays; summaries shortened (NCMD-11) |
| `events.h/.cpp` | — | — | deleted (split into `handlers.*`, `measure.*`) |

`docs/input.md` (new): behaviour and ordering of input and state events (NCMD-11).

## 5 Questions for Sebastian

**Q1 Apply on error: keep "stop at the first bad command, earlier ones stay applied"?**
Today a bad command stops the apply; what came before it is applied, what comes after is not. C# resyncs after an error (`ResyncTests`).
- (a) Keep it, and document it in `protocol.h` and `docs/architecture.md`. No cost, the resync is already built.
- (b) Validate the whole buffer first, apply only if all commands are valid. Needs a full decode pass (each op validated without side effects, including id existence across commands of the same buffer, e.g. CREATE then SET). About twice the decode cost per frame and a large change.
- Recommendation: (a). The partial state is recoverable and the resync exists; (b) adds a second validator that must stay in step with the first.

**Q2 The 64 KB limit of a command and an event record.**
The header length is `u16`: a SET string can carry at most 65,527 bytes, EV_VALUE strings are cut at 60,000 bytes, EV_ERROR at 4,096, QML records over 65,000 bytes are dropped (NCMD-08). Is a Text or TextField with more than about 64 KB a case for vvvv users?
- (a) Keep `u16`, put the limits into `protocol.h` as named constants, make C# reject a longer string at the node with a clear error, and make the native caps visible (NCMD-08).
- (b) Widen the length field to `u32` (header 8 bytes instead of 4, breaking for the protocol, allowed before 0.10.0). All commands grow by 4 bytes; every length check becomes 64-bit.
- Recommendation: (a), unless you know a patch that needs long texts; then (b) for events only (records are few per frame) and (a) for commands.

## 6 For other areas

- C# sinks (`SinkRegistry`): `resetSent` pushes EV_HOVER/PRESS/ACTIVE 0 to the old handle during apply (`events.cpp:59-82`); if C# reuses a released handle in the same frame, those records reach the new sink. Check that handles are reused only after the next poll.
- C# poll (`src/VL.Qt/SceneCore.cs:221-227`): one retry only, and `vlqt_window_poll` returns `-1` both for "null window" and as a size (`vlqt.cpp:1835`); see NCMD-04.
- Elements/effect chain: which inputs make `EffectChain::apply` fail after the wire validation (`elements.cpp:1653`), needed for NCMD-03.
- C# effect chain / QML nodes: confirm whether SET_EFFECT_CHAIN and SET_QML_PROPERTIES are sent every frame while a value is animated (NCMD-10).
- `vlstyle.cpp` `applySetStyle` receives the raw payload (`commands.cpp:802`); its bounds checks were not reviewed here.
- `perf.cpp:1095`, `1102` use `Scene::debugOverlay()` to skip the overlay in item counts; keep the accessor when `Scene` is split.
