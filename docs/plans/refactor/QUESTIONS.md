# Questions for Sebastian – refactor (release 0.10.0)

Section 5 of the 23 review reports in [review/](review/README.md), merged into one list. Duplicates are merged into one question with all sources; where two reports recommend different answers, both are named. Blocks A and 0 come first because other questions and the cut of the issues depend on them. Each decision is recorded here in the line `Decision:`; decisions of principle also go into [DECISIONS.md](DECISIONS.md).

Sources: `REC Q1` means report prefix `REC`, question 1 of its section 5 (prefixes in [review/README.md](review/README.md)). Finding ids such as `REC-10` point to section 3 of the same report.

**Node renames:** the catalog of proposed renames of nodes, pins and categories is in [review/23_api.md](review/23_api.md), section 7, with its own decision column. It is decided there, after A7 below, and is not copied here.

Overview:

| Block | Topic | Questions |
|---|---|---|
| A | Foundations that block other decisions | 7 |
| 0 | Process (open in DECISIONS.md) | 2 |
| B | Errors and robustness | 9 |
| C | Node API and naming (besides the catalog) | 19 |
| D | Code structure and conventions | 9 |
| E | Native runtime behaviour | 16 |
| F | Tests | 6 |
| G | Documentation and repository | 8 |
| | **Total** | **76** |

Then: [must findings that are unconfirmed suspicions](#unconfirmed-must-findings).

---

## A Foundations (decide first)

### A1 Rename "sink" (the way back from Qt) to "feedback"?
Today `ElementSink`, `SinkState`, `SinkSlot`, `SinkRegistry` carry events from Qt back to the nodes, while `ICommandSink` is the way to Qt, so "sink" means both directions.
- (a) "feedback": `ElementFeedback`, `FeedbackState`, `FeedbackSlot`, `FeedbackRouter`, `Element.Feedback`. Neutral, direction is clear, no clash with VL's notifications.
- (b) Keep "sink", rename `ICommandSink` → `ICommandTarget`. Smaller diff, but the term stays to be learned.
- Recommended: (a). Touches the reconciler and the nodes, so it fixes names for many issues.
- Source: ELM Q1
- Decision: (a) "feedback" (Sebastian, 2026-11-19).

### A2 What identifies "the same element" in the scene registry: the instance or the sink slot?
An element node outputs a new instance whenever an input changes, always with the same slot. Keyed by instance (today), one Rectangle node is a new key after every change: registry churn per frame, hand-over between renderers, the documented owner rule is false, and the Snapshot slot fallback exists only to bridge this (REC-10).
- (a) Key by slot when the element has one, by instance otherwise. Removes the fallback and the churn. New: two instances of one node in one tree (e.g. through a FrameDelay) become occurrences of one element; a Video node then has one player.
- (b) Keep the instance key, add `Replace(old, next, node)` plus pooling, change the owner rule to "first renderer to show the current instance".
- Recommended: (a).
- Source: REC Q1
- Decision: (a), key by slot (Sebastian, 2026-11-19). Two instances of one node in one tree are accepted as occurrences of one element.

### A3 One pattern for the per-type property diff?
The reconciler compares the previous and the next element and sends only changed properties. Items are diffed by a central switch (`PropertyDiff.cs`), input handlers by `Interaction.WriteProperties` in the element model: two patterns for one job.
- (a) Central: all wire knowledge in the reconciliation folder; a contributor edits `ElementType`, the element record and `PropertyDiff`.
- (b) Per element: an internal abstract `WriteProperties` on `Element`; one class per element, but the public data model carries protocol code.
- Recommended: (a). Decide before REC-04.
- Source: REC Q3
- Decision: (a), central (Sebastian, 2026-11-19).

### A4 Command and event length: 16 or 32 bit? (the reports disagree)
The length field in the protocol header is `u16`, so one command or event carries at most about 64 KB. Consequences today: a Text over 64 KB throws in the reconciler and leaks (REC-01), point caps on Shape/Path, chunking of QML records, EV_VALUE strings cut at 60,000 bytes.
- (a) `u32` length in command and event headers: removes the limit, the caps and the chunking; 4 bytes more per command; golden-hex tests rewritten.
- (b) Keep `u16`, name the limits in `protocol.h`, reject longer strings at the node with a clear error.
- (c) `u32` for events only, `u16` for commands.
- Recommended: PROTO says (a), together with PROTO-09 in one protocol-version bump. NCMD says (b), unless a patch needs long texts, then (c).
- Source: PROTO Q1, NCMD Q2
- Decision: (a), `u32` for commands and events (Sebastian, 2026-11-19), on the condition that it costs no measurable performance: the issue compares the SmokeTest perf scenarios and the apply time per frame before and after.

### A5 Break the native layer once: rename the C ABI and the protocol together?
QtNative (`vlqt.dll`) is loaded only by VL.Qt itself, so its C functions are internal. Proposed renames: about 35 exports and the handle type (CAPI-03); `vlqt_window_*` → `vlqt_scene_*` for what windows and offscreen scenes share (NAPP-03, NAPP-17); `vlqt_host_frame` → `vlqt_host_pump` (NDIAG); protocol type `ITEM` → `GROUP` (NELM-11).
- (a) Rename both layers now, one name per function across C and C#, in one change together with the protocol changes (A4, B5).
- (b) Keep the C names, only regroup and rename the C# wrappers.
- Recommended: (a), so the native layer breaks once.
- Source: CAPI Q2, NAPP Q2, NDIAG Q4, NELM Q4
- Decision: (a), one break of the native layer, together with A4 (Sebastian, 2026-11-19).

### A6 Concrete element classes (Rectangle, Text, Group …): public or internal? (the reports disagree)
No pin uses them; every node outputs `Element`.
- (a) Public, with `[Smell(SymbolSmell.Hidden)]`: C# authors of other packages can build and inspect elements; the tooltip of an `Element` pin shows the concrete type.
- (b) Internal: smallest public surface, free to change; C# users build elements only through the nodes. They carry `ElementSink`/`SinkSlot` plumbing that is not an API.
- Recommended: ELM says (a), API says (b) for 0.10.0 (API-01). Making them public later breaks nothing; the reverse does.
- Source: ELM Q2, API Q5
- Decision: (b), internal for 0.10.0 (Sebastian, 2026-11-19). Check in vvvv what the tooltip of an `Element` link shows afterwards (`manual-check`).

### A7 Naming principle of the node API: do VL.Skia and VL.ImGui win?
Many single questions in C and the catalog of 23_api.md are the same question: where VL.Skia (drawing, paths, renderer) or VL.ImGui (widgets) has a node or pin of the same meaning, VL.Qt takes its name, pin names and units, quirks included; otherwise the general vvvv conventions (cycles for angles, `Set…`/getter pattern of records). Qt names are used only where vvvv has no word.
- (a) Yes, as a rule; exceptions are written down with their reason.
- (b) Decide case by case.
- Recommended: (a). It decides C3, C8, C10, C12, C13 and most of the catalog at once.
- Source: DECISIONS.md "Open"; NODX Q3, SIP Q1, NODW Q3
- Decision: (a), with one exception (Sebastian, 2026-11-19): where a convention is inconsistent or makes no sense (e.g. Skia's `Dir` next to `Direction`), VL.Qt standardises instead of copying the quirk.

## 0 Process (open in DECISIONS.md)

### 0.1 Pull request per issue or direct commits on `refactor`?
- (a) One branch and one draft PR per issue against `refactor`, squash-merged; at the end one PR `refactor` → `develop` as merge commit. Every change is reviewable on its own; costs no CI (PRs against `refactor` start none).
- (b) Orchestrators commit directly on `refactor`. Less overhead, no review point per issue.
- Recommended: (a).
- Source: DECISIONS.md "Open"
- Decision: (a) (Sebastian, 2026-11-19).

### 0.2 Order of the work?
Proposed waves: 1 safety net and tooling (API snapshot TEST-01, tests that protect the areas), 2 API decisions on paper (this list, the catalog), 3 repository-wide mechanical changes, one after the other (K1–K5, A1, A5), 4 refactoring per area in parallel, 5 tests, 6 documentation, 7 release.
- Recommended: as proposed.
- Source: DECISIONS.md "Open"
- Decision: as proposed, and wave 1 also confirms or drops the unconfirmed must findings (REC-01, CAPI-01, NDIAG-01) (Sebastian, 2026-11-19).

## B Errors and robustness

### B1 What happens when the reconcile throws?
- (a) Like a failed apply: reset the command buffer, resync (RESET and rebuild next frame), show the exception at the renderer's Error output.
- (b) Stop reconciling for this renderer until the input changes.
- (c) Let it propagate as today: leaks every frame (REC-01).
- Recommended: (a), with REC-01's clamping so user data never reaches the exception path.
- Source: REC Q2
- Decision: (a) (Sebastian, 2026-11-19), but the exception goes to the log and marks the Renderer node instead of an `Error` output (see "No `Error` output pins" in DECISIONS.md).

### B2 A bad command in the middle of a buffer: keep "earlier commands stay applied"?
- (a) Keep it and document it in `protocol.h` and `architecture.md`; C# resyncs after an error already.
- (b) Validate the whole buffer first, apply only if all are valid: about twice the decode cost, a second validator.
- Recommended: (a).
- Source: NCMD Q1
- Decision: (a) (Sebastian, 2026-11-19).

### B3 `vlqt.dll` and `VL.Qt.dll` of different versions: refuse or warn?
- (a) Refuse: no windows, error at the Renderer.
- (b) Warn and continue; result undefined.
- Recommended: (a).
- Source: PROTO Q3
- Decision: (a), refuse; the Renderer is marked and the log names both versions (Sebastian, 2026-11-19).

### B4 Error text of native calls: per-thread last error or a buffer per call?
- (a) Per-thread last error, as `GetLastError`/`errno`; `vlqt_image_write` drops its own buffer (CAPI-02).
- (b) An `err`/`errCap` buffer on every function: thread-safe by construction, two more parameters everywhere.
- Recommended: (a).
- Source: CAPI Q3
- Decision: (a), per-thread last error; `vlqt_image_write` drops its buffer (Sebastian, 2026-11-19).

### B5 The index field of poll records: drop or keep?
C# adds it to the slot index; QtNative always sends 0 (ELM-08).
- (a) Drop its meaning: C# ignores records with index ≠ 0, `protocol.h` marks it reserved.
- (b) Keep it for a future item that serves several slots, but never grow the slot list from it.
- Recommended: (a).
- Source: ELM Q4
- Decision: (a), and the field is removed from the protocol with the native break of A5 (Sebastian, 2026-11-19).

### B6 Hover and press of an element shown at several places: OR or last event wins?
`events.md` says OR, the code does last-write-wins (ELM-02).
- (a) Implement OR (bits per handle, counts per slot).
- (b) Change the decision to "last event wins".
- Recommended: (a); the decision was taken for a Button shown twice.
- Source: ELM Q3
- Decision: (a), OR over all occurrences (Sebastian, 2026-11-19).

### B7 How do resource and file nodes show an error?
- (a) Error output and log for all; a warning at the node only for a lasting load failure (LoadImage, LoadImage (Async), LoadFont, ToQtImage).
- (b) Error output and log only; LoadFont loses its warning.
- (c) Warning at the node for every error, also one-shot Snapshot/FileWriter failures.
- Recommended: (a).
- Source: NODW Q2
- Decision: decided by the principle "No `Error` output pins" (Sebastian, 2026-11-19): every error goes to the log; a lasting failure (LoadImage, LoadImage (Async), LoadFont, ToQtImage) also marks the node; one-shot failures (Snapshot, FileWriter) only log.

### B8 QtNative cannot create its GPU device: retry or stay degraded?
- (a) Retry once per second; meanwhile windows use their own devices, images do not upload, offscreen scenes report `DEVICE_LOST`, the log says so once.
- (b) As today: degraded until restart; offscreen scenes fall back to private devices nobody watches for loss.
- Recommended: (a).
- Source: NDEV Q3
- Decision: (a), retry once per second, log once (Sebastian, 2026-11-19).

### B9 Which app receives Qt's log messages when several vvvv apps run?
Qt's message handler (the function Qt calls for every warning) does not know the window.
- (a) The newest live app, falling back to the previous one when it stops (RUN-03, small).
- (b) Every app gets every message: duplicates.
- (c) QtNative attributes messages to a window where it can and C# routes them: exact, but large and across layers.
- Recommended: (a) now, (c) as a 2.0 issue if users run several apps.
- Source: RUN Q2
- Decision: (a) now; (c) becomes an issue in milestone 2.0 (Sebastian, 2026-11-19).

## C Node API and naming (besides the catalog)

### C1 Renderer window pins and `WindowSettings`: one way or two?
Renderer has `Title`, `Borderless`, `Fullscreen` and a `Window: WindowSettings` pin whose same three fields win over the node's pins.
- (a) Keep both.
- (b) Renderer keeps the three pins (as Skia's Renderer has Title); WindowSettings keeps only what the Renderer has no pin for (`Icon`, `Bounds` on create).
- (c) Everything through WindowSettings.
- Recommended: (b).
- Source: API Q1
- Decision: (b) (Sebastian, 2026-11-19).

### C2 An `Enabled` pin on elements?
Every Skia layer has `Enabled`; VL.Qt elements have none (Opacity 0 still hit-tests and costs).
- (a) No pin; document Opacity and removing from the spread.
- (b) Optional `Enabled`, default true, before `Opacity`; it removes the element from the scene (Qt: the item's `visible` property, which also stops input).
- Recommended: (b).
- Source: API Q2
- Decision: (b), optional `Enabled`, default true, as the very last input pin so that it sits on the far right (as in VL.Skia), not before `Opacity` (Sebastian, 2026-11-19).

### C3 `IElement` or `Element` on element inputs?
Containers take `Spread<IElement?>` (so a self-made stylable widget fits), other inputs (Renderer, Labeled, Snapshot, Blend, Mask, Displace) take `Element`. Skia uses `ILayer` on both ends.
- (a) Keep, explain the difference in the summary of `IElement`.
- (b) `IElement` on every element input; the reconciler applies the base style there (a reconciler change).
- Recommended: (a) for 0.10.0.
- Source: API Q3
- Decision: (b), one type on every element input (Sebastian, 2026-11-19); a reconciler issue applies the base style there. Follow-up: outputs also show `IElement`; `Element` stays the internal base class. The name `IElement` is reviewed with the catalog.

### C4 Fill colour pin: `Fill Color` or `Color`?
- (a) `Fill Color`, pairs with `Stroke Color`.
- (b) `Color`, as on Text and Skia's `Fill`.
- Recommended: (a).
- Source: API Q4 (API-05)
- Decision: (a), `Fill Color` and `Stroke Color` on shapes; a node with one colour (Text) keeps `Color` (Sebastian, 2026-11-19).

### C5 Style setter and getter names
Today a setter is named by its field (`Radius`, `Color`), a getter `GetRadius`; bundles `Frame` / `GetFrame`. vvvv imports a record property the other way round (`Radius` reads, `SetRadius` writes). There are also 40 Advanced single setters and getters.
- (a) Keep.
- (b) vvvv convention: getters `Radius`, setters `SetRadius`, bundles `SetFrame` / `Frame`; pin `State` → `States`. Breaks every style patch.
- (c) Like (b) and drop the single setters; the bundles with Optional pins set one field too.
- Recommended: (b); with C6 only six process nodes remain if (c).
- Source: SIP Q1
- Decision: (b), vvvv convention, the single setters and getters stay (Sebastian, 2026-11-19).

### C6 Style setters as process nodes?
- (a) Process nodes with a cached output for `Colors`, `Frame`, `Text`, `Indicator`, `Track`, `Merge`: no allocation per frame, the reconciler skips unchanged widgets.
- (b) Keep static nodes, compare by value in the reconciler, accept per-frame garbage.
- Recommended: (a).
- Source: SIP Q2 (SIP-01)
- Decision: (a) for the six bundles; the single setters and getters stay static (Sebastian, 2026-11-19).

### C7 `AddRoundRect` with too large radii: follow Skia?
- (a) Scale the radii together as Skia does: a pill gets semicircular ends. Changes how existing patches draw.
- (b) Keep the per-axis limit, list it under "Differences" in `shapes.md`.
- Recommended: (a).
- Source: SIP Q3 (SIP-02)
- Decision: (a), follow Skia (Sebastian, 2026-11-19).

### C8 Rename `TextInput` to `Input (String)`?
Pairs with `Input (Int)` and VL.ImGui; tags keep "text, textfield" findable.
- Recommended: rename.
- Source: NODW Q3 (NODW-08)
- Decision: rename to `Input (String)`, tags `text, textfield` (Sebastian, 2026-11-19).

### C9 Slider and Input (Int) outside Min/Max: is the channel or the widget the truth?
- (a) Channel: the widget reads the channel every frame like VL.ImGui and clamps only what it shows; all widgets on one channel agree.
- (b) Widget (today), like a Qt control: widget and channel can disagree; the pin doc has to say so.
- Recommended: (a).
- Source: NODW Q1 (NODW-01)
- Decision: (a), the channel is the truth (Sebastian, 2026-11-19).

### C10 Angle unit of DirectionalBlur and RadialBlur
- (a) Cycles, as everywhere in vvvv, Pinch and the path nodes.
- (b) Degrees, as CSS and Qt; two units in one library.
- Recommended: (a).
- Source: NODX Q1 (NODX-01)
- Decision: (a), cycles (Sebastian, 2026-11-19).

### C11 One category for all effects?
Blur, DropShadow, ColorAdjust, Colorize and Mask sit in `Qt.Effects.MultiEffect` (MultiEffect is the Qt item that merges these five into one layer), the others in `Qt.Effects`.
- (a) Keep the split.
- (b) One category `Qt.Effects`; the merge is explained in the summaries and in `HowTo Chain Effects`.
- Recommended: (b).
- Source: NODX Q2
- Decision: (b), one category `Qt.Effects` (Sebastian, 2026-11-19).

### C12 Path pin names: copy Skia's quirks?
Skia uses `Dir` on AddRoundRect, `Direction` on AddRect/AddOval, `EndPoint` on CubicTo.
- (a) Copy Skia exactly where a Skia node of the same name exists.
- (b) Normalise (`Direction`, `End Point`).
- Recommended: (a).
- Source: NODX Q3 (NODX-13)
- Decision: (b) by A7: one name for one meaning (`Direction` everywhere); names Skia uses consistently are kept (Sebastian, 2026-11-19). The exact pin names go through the catalog.

### C13 Name of the running-gesture state
- (a) Specific: `Is Hovered`, `Is Pressed`, `Is Dragging`, `Is Pinching`, Point `Is Active`.
- (b) Uniform `Is Active` on Drag, Pinch and Point (Qt's term for its input handlers, the objects that turn pointer events into gestures).
- Recommended: (a).
- Source: NODX Q4 (NODX-02)
- Decision: (a), specific names (Sebastian, 2026-11-19).

### C14 Rename `ShaderEffect` to `Shader`?
In VL.Qt "effect" means the `Effect` chain; `ShaderEffect` is Qt's name for an item (an element in Qt's scene) that draws a shader, and has nothing to do with the chain.
- (a) Keep `ShaderEffect`, explain the difference in the docs.
- (b) `Shader`, in `Qt.Elements.Shaders`; "effect" then always means the chain.
- Recommended: (b).
- Source: SHD Q1
- Decision: (b), `Shader` in `Qt.Elements.Shaders` (Sebastian, 2026-11-19).

### C15 Where does the `Sampler` node live?
- (a) Keep in `Qt.Elements.Uniform`.
- (b) `Qt.Elements.Shaders`, uniform nodes in `Qt.Elements.Shaders.Uniform`.
- (c) Next to the image nodes.
- Recommended: (b), with the catalog.
- Source: SHD Q2
- Decision: (b) (Sebastian, 2026-11-19).

### C16 Clip pin on Rectangle and Circle
Qt's clip cuts an item's own painting and its children to its size; Rectangle and Circle have no children and draw inside their size, so the pin probably does nothing (not verified with DropShadow).
- (a) Keep Clip on every element for a uniform pin set, document it.
- (b) Remove it from Rectangle and Circle.
- Recommended: (a), unless a grab shows it never matters.
- Source: NODE Q4
- Decision: (a), keep Clip everywhere; wave 1 checks with a SmokeTest grab (Clip on, DropShadow) whether it ever matters on Rectangle and Circle, and the pin help says so honestly (Sebastian, 2026-11-19).

### C17 `PerfStats` shape and refresh rate
- (a) Grouped record structs with nested Split. (b) Flat record with documented parameters.
- Also: may the Perf output refresh at 4 Hz instead of every frame (the F5 overlay is unaffected)?
- Recommended: (a), and yes to 4 Hz.
- Source: RUN Q3 (RUN-10, RUN-04)
- Decision: (a), grouped record structs; the output keeps updating every frame, since the node serves debugging and must not lose values (Sebastian, 2026-11-19). RUN-04 then only removes the per-frame allocation (record structs), not the frame rate.

### C18 May a patch control the diagnostic overlays?
Today only F2/F5/F6/F7 do, swallowed in every window.
- (a) `Diagnostic Keys` (bool) on the Renderer.
- (b) Also an `Overlay` pin (None/Meter/Stats) for machines without a keyboard.
- (c) Leave as is; a patch with its own F5 shortcut cannot work.
- Recommended: (a); (b) only if wanted for installations.
- Source: NDIAG Q1 (NDIAG-07)
- Decision: (a), named as in VL.Stride's renderer: `Enable Keyboard Shortcuts`, default true; false disables every diagnostic key and passes the keys to the patch (Sebastian, 2026-11-19).

### C19 Mark `Video (VL.Audio)` experimental?
`QtTexture`, `QtRenderer` and `ToTexturePixels` are, `Video (VL.Audio)` is not, though measured only on win-arm64.
- Recommended: yes, until measured on x64.
- Source: COMP Q2
- Decision: yes, experimental until measured on x64 (Sebastian, 2026-11-19).

## D Code structure and conventions

### D1 Shared pin docs of the element nodes: `<include>`, generator or accept?
About 200 duplicated doc lines across 24 nodes.
- (a) `<include>` from one file plus shorter texts (NODE-02).
- (b) A source generator as VL.ImGui has: removes pin, `Props`, `Create` and doc duplication, but a pattern every contributor must learn.
- (c) Accept the duplication.
- Recommended: (a); a generator only if the node count grows well beyond 24.
- Source: NODE Q1 (NODE-03)
- Decision:

### D2 K5: drop the rule for `[Pin(Visibility = PinVisibility.Hidden)]` on `NodeContext`?
vvvv recognises the parameter by its type; VL.StandardLibs has 66 constructors without and 4 with the attribute.
- (a) Drop the rule from AGENTS.md, remove the six attributes.
- (b) Keep the rule, add 55 attributes that change nothing.
- Recommended: (a).
- Source: NODE Q2
- Decision:

### D3 Explicit defaults for 0, false and null?
- (a) Add `[DefaultValue(0f)]`/`[DefaultValue(false)]` everywhere (about 60).
- (b) Rule: "explicit unless `default(T)` is intended; never for `Matrix`"; remove the explicit zeros of the shape nodes; `vl_pin_catalog.py` treats a missing attribute as `default(T)`.
- Recommended: (b).
- Source: NODE Q3
- Decision:

### D4 `Protocol.cs`: hand mirror or generated?
- (a) Hand mirror plus a stricter drift check in the unit tests.
- (b) Generate the constants from `protocol.h` with a script, test fails when stale.
- (c) One description file that generates both.
- Recommended: (a) now, (b) if it becomes a chore, not (c).
- Source: PROTO Q2 (PROTO-08)
- Decision:

### D5 Keep the hand-written QML declaration parser?
It reads the properties a user's QML file declares, to make pins in the node browser. QML is Qt's declarative UI language.
- (a) Keep it, fix the lexer gaps (QML-01).
- (b) Qt's tools (`qmllint --json` or the private `QmlDom` library): exact grammar, but an extra shipped tool, a licence question and a slow first browse.
- (c) Ask QtNative at runtime: no pins before a renderer runs.
- Recommended: (a), later a warning when the runtime property list differs.
- Source: QML Q1
- Decision:

### D6 One shared folder-node-factory layer for shaders and QML?
The two node factories (nodes generated from files in a folder) are 90 % identical copies.
- (a) Extract it now, as one issue shared with the shader area.
- (b) Leave the copies, only move the pin types out of `Shaders/`.
- Recommended: (a).
- Source: QML Q2 (QML-03)
- Decision:

### D7 Companion packages (Stride, Audio): `InternalsVisibleTo` or a public API?
- (a) Keep `InternalsVisibleTo` with a documented seam (COMP-07): nothing frozen, third parties cannot build a companion.
- (b) A small public API in `VL.Qt.Hosting`: others can build companions, but types still moving get frozen.
- Recommended: (a) for 0.10.0, as VL.ImGui does.
- Source: COMP Q1
- Decision:

### D8 QML of the built-in element types: files or C++ strings?
- (a) `.qml` files in a resource folder (NELM-06): readable, lintable, windeployqt (Qt's tool that copies the needed Qt files next to an app) sees the imports, `deploy.qml` mostly goes.
- (b) Keep C++ strings; `deploy.qml` stays a hand-kept list.
- Recommended: (a).
- Source: NELM Q1
- Decision:

### D9 More Qt private API?
"Private" in Qt means classes without a compatibility promise between Qt versions. QtNative already uses several (`QQuickRectangle`, `QQuickText`, `QQuickShape` …); NELM-07/08 use more of the same classes, no new ones.
- Recommended: accept, with the rule "only classes listed in the head comment of `elements.h`, checked on each Qt update".
- Source: NELM Q3
- Decision:

## E Native runtime behaviour

### E1 `@padding` of shaders: DIP or renderer units?
Documented as DIP (device-independent pixels), applied in renderer units, 100 times too large (SHD-01).
- (a) DIP, fix the code. (b) Renderer units, fix the docs.
- Recommended: (a).
- Source: SHD Q3
- Decision:

### E2 Keep the last good pins while a shader file does not compile?
- (a) Yes: links and values survive a typo, the last good shader keeps drawing, the node shows the error. Same rule as QML-05.
- (b) No: all uniform pins disappear until it compiles (today).
- Recommended: (a).
- Source: SHD Q4 (SHD-05)
- Decision:

### E3 `required property` in user QML: fix or drop the promise?
A QML property marked `required` must be set when the item is created.
- (a) Fix natively with `createWithInitialProperties` (Qt's call that creates an item with properties already set): a few lines in `QmlItem::rebuild`.
- (b) Drop the "always sent" promise and `AlwaysSend`; such files keep failing.
- Recommended: (a).
- Source: QML Q3 (QML-08)
- Decision:

### E4 Rely on Qt's `basic` render loop explicitly?
In a render loop Qt alternates between preparing the scene (GUI thread) and drawing it; `basic` does both on one thread, `threaded` draws on a second one. QtNative forces `basic`.
- (a) Write the code correct under both loops (NFX-03 for the texture).
- (b) Declare `basic` a design decision and say so in each header that relies on it.
- Recommended: (a) for the texture, plus (b) as one decision record that other areas check against.
- Source: NFX Q1
- Decision:

### E5 GLSL rename check on Windows
Shaders are compiled ("baked") for each GPU backend; a uniform name can break only on Linux/OpenGL. The check warns about this on Windows.
- (a) Keep it as a second bake (two compiles per live edit).
- (b) One bake with an extra GLSL target: half the cost, but a shader SPIRV-Cross (the translator between shader languages) cannot turn into GLSL fails on Windows too.
- (c) Drop the check on non-GL backends.
- Recommended: (b).
- Source: NFX Q2 (NFX-12)
- Decision:

### E6 Main-thread mode of offscreen scenes (`VLQT_OFFSCREEN_THREAD=0`)
- (a) Keep, but through the same batch code (inline executor): one path, the switch stays.
- (b) Remove: less code and test time, no fallback in the field.
- Recommended: (a).
- Source: NDEV Q2 (NDEV-05)
- Decision:

### E7 Is a render-thread pool larger than 1 supported?
Reachable only through `VLQT_OFFSCREEN_THREAD`, untested, which is how NDEV-01 went unnoticed.
- (a) Supported: fix NDEV-01, add a pool-2 test run.
- (b) Diagnosis only: cap at 1, remove the round robin.
- Recommended: (a); measurements show a gain for several heavy scenes.
- Source: NDEV Q4 (NDEV-01)
- Decision:

### E8 Texture reuse for live image sources
Every new frame of a video or live ToQtImage creates and destroys a GPU texture.
- (a) Keep. (b) A pool of shared textures (about 150 lines). (c) Update one texture in place: a window could show a half-updated frame.
- Recommended: measure with the video help patch first; (b) if more than about 1 ms main thread per frame.
- Source: NRES Q1 (NRES-02)
- Decision:

### E9 Device loss for file images
After a GPU reset a file image is read again from disk.
- (a) Keep and document. (b) Keep decoded pixels in CPU memory: doubles image memory. (c) Keep the compressed file bytes.
- Recommended: (a).
- Source: NRES Q2 (NRES-14)
- Decision:

### E10 Upload path switch `VLQT_IMG_UPLOAD`
- (a) Keep as a documented diagnostic variable. (b) Remove.
- Recommended: (a) if the E8 measurement is planned, else (b).
- Source: NRES Q3 (NRES-05)
- Decision:

### E11 Animations and a clock that goes back
Qt animations advance by time deltas and do not rewind (NAPP-10, if confirmed).
- (a) Document: QML animations follow the clock forward.
- (b) Restart all animations on a backward jump: Qt has no API for it.
- Recommended: (a).
- Source: NAPP Q3
- Decision:

### E12 Row/Column alignment: JavaScript in QML or C++?
- Recommended: measure, move to C++ only if it shows.
- Source: NELM Q2 (NELM-13)
- Decision:

### E13 What does "over budget" mean?
- (a) Qt work (events + sync + render) above the refresh budget.
- (b) The whole pump including the VSync wait (today).
- (c) Both, as two counters.
- Recommended: (a) as `OverBudget`.
- Source: NDIAG Q2 (NDIAG-01)
- Decision:

### E14 Should the F6 freeze also freeze the F2 meter?
- Recommended: yes, so a screenshot of both is consistent.
- Source: NDIAG Q3
- Decision:

### E15 Rows of the stats page: remove "draw calls: n/a"?
- Recommended: remove the draw-calls row (always n/a), keep pipelines (shows shader-build hitches).
- Source: NDIAG Q5
- Decision:

### E16 Ending an exported app
vvvv has no API for it; `ExportedApp` sets a private field by reflection, with `Environment.Exit(0)` as fallback.
- (a) Keep it, contained in `ExportedApp`, tested at every vvvv update.
- (b) Ask the vvvv team for `AppHost.RequestExit()`, keep (a) until it exists.
- Recommended: (b) with (a) meanwhile.
- Source: RUN Q1
- Decision:

## F Tests

### F1 Test exports in the shipped `vlqt.dll`?
About 30 of 101 exports exist only for the SmokeTest and unit tests, among them a forced device loss.
- (a) Keep them in every build, separated in `vlqt_test.h` and `test_*.cpp`: the CI tests the binary that ships.
- (b) A CMake option, off for the package: smaller, cannot be misused, but the CI tests another binary or builds twice.
- (c) A second library `vlqt_test.dll`: large change, most hooks need internal state.
- Recommended: (a), by all three reports.
- Source: CAPI Q1, NAPP Q1, TEST Q1 (CAPI-04, NAPP-09, TEST-08)
- Decision:

### F2 Where do the stand-in "Stride" D3D11 helpers of the SmokeTest live?
The SmokeTest needs a second D3D11 device, shared textures and readback to test QtTexture without Stride.
- (a) A separate native `vlqt_testkit.dll`, built but not packed.
- (b) C# in the SmokeTest with Vortice.Direct3D11.
- (c) Leave them in `vlqt.dll`: every user ships exports that release arbitrary pointers.
- Recommended: (a).
- Source: NDEV Q1 (NDEV-09)
- Decision:

### F3 Reference images: which machine, which tolerance?
- (a) References only for Windows x64 D3D11 (CI, WARP software renderer), tolerant compare, skip elsewhere.
- (b) References per RID. (c) No compare, pixel probes only.
- Recommended: (a), scenes without text or with the bundled font.
- Source: TEST Q2 (TEST-03)
- Decision:

### F4 Wall-clock limits in the unit tests
- (a) Keep behind `VLQT_PERF_FACTOR` and a `Perf` category, warn-only on CI runners.
- (b) Drop from `dotnet test`, keep SmokeTest `PerfLimits` and the allocation tests.
- Recommended: (a); the allocation tests stay hard.
- Source: TEST Q3 (TEST-09)
- Decision:

### F5 SmokeTest as an NUnit project?
- (a) Keep the custom runner with a scenario table (TEST-11).
- (b) NUnit with one Qt start per run; several scenarios need their own process anyway.
- Recommended: (a) for 0.10.0.
- Source: TEST Q4
- Decision:

### F6 Where does the node-API snapshot live?
- (a) In the test project (`src/VL.Qt.Tests/Api/node-api.txt`), regenerated by the author of each issue.
- (b) Under `docs/` as a page users read too.
- Recommended: (a) now, a user reference generated from it later.
- Source: TEST Q5 (TEST-01)
- Decision:

## G Documentation and repository

### G1 CLA: draft status and contracting party
`CLA.md` says "Draft – pending legal review", while the CLA bot already asks for signatures. The licensee is Sebastian Huber as a person, the studio is the Huber-Pohle-Timpernagel GbR.
- (a) Keep the person. (b) The GbR as licensee, the person as its representative. (c) Leave until the legal review.
- Recommended: decide (a) or (b) with the legal review, before the repository is public and before the first external signature.
- Source: DOC Q1
- Decision:

### G2 Milestones and version rule after 0.10.0
- (a) Back to `1.0`/`2.0`/`3.0` with "0.10.x until 1.0".
- (b) Drop the version-number rule from the contributor pages; keep "milestones are version numbers; `1.0` blocks the release".
- Recommended: (b).
- Source: DOC Q2
- Decision:

### G3 Do `docs/plans/` and the review reports ship in the public repository?
`public-check.yml` blocks only top-level `plan/` and `docs-internal/`.
- (a) Move working plans and reviews to `docs-internal/` or out; what stays true goes to `docs/decisions/` or `docs/research/`.
- (b) Keep them public as a record.
- Recommended: (a).
- Source: DOC Q3
- Decision:

### G4 Companion docs: one page per package or per topic?
- Recommended: per package (`video-audio.md`, `stride.md`), as users install packages.
- Source: DOC Q4
- Decision:

### G5 Folder name `docs/design/` or keep `docs/decisions/`?
- Recommended: `docs/design/`; the links change anyway because most pages are split or merged.
- Source: DOCD Q1 (DOCD-15)
- Decision:

### G6 Finished plans and handovers: delete or archive?
- Recommended: delete (git keeps them); an archive in a public repository is read as current.
- Source: DOCD Q2
- Decision:

### G7 Open 2.0 work (paths 06–10, gradients): are the issues in the public repository?
- Recommended: create or confirm them there before DOCD-04 and DOCD-14 delete the pages.
- Source: DOCD Q3
- Decision:

### G8 Measurements from one laptop in public research pages?
- Recommended: keep, with machine, Qt version, scenario to rerun and "x64 not measured".
- Source: DOCD Q4
- Decision:

---

## Unconfirmed must findings

`must` findings whose report marks part of the evidence as a suspicion. Each is confirmed or dropped before its issue is cut.

| Finding | Unconfirmed part | What confirms it |
|---|---|---|
| [REC-01](review/05_reconciler.md) Reconcile not exception-safe | The leak per frame is shown by reading; whether QtNative then shows an orphan item is not. | A unit test with a sink that throws part-way, then a check of the native item count. |
| [CAPI-01](review/09_c-api.md) Last-error string shared between threads | The race through the three "any thread" exports is confirmed by reading; that `offscreen.cpp:767` also sets it on a render thread when the thread pool is > 0 is a suspicion. | `resource-stress` under TSan on Linux; a SmokeTest check that calls `vlqt_font_add` on a worker thread while the main thread fails an apply. |
| [NDIAG-01](review/26_native-diagnostics.md) "Pump" includes the VSync wait | Not run: that `over_budget` counts a static VSync-on window as over budget. | A Renderer with VSync on and a static, then a changing scene; `over budget` must stay 0 for the static one. |

Suspicions in `should` findings that also need a run: SIP-03 ([13](review/13_styles-images-paths.md)), COMP-03 and COMP-04 ([22](review/22_stride-audio.md)), NAPP-10 ([16](review/16_native-vlqt.md), see E11), the Clip question C16.
