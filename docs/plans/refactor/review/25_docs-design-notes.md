# Review 25 – Documentation: decisions, plans, research, topics

Claude Opus 5.5, 2026-11-19. Files read in full: `docs/decisions/` (device-sharing, element-catalog-v1, events, paths, qt-bridges, qtnative-deployment, reconciler-identity, render-texture, timing, user-qml), `docs/plans/paths/00_overview.md` … `10_gradients-brainstorm.md`, `docs/research/01_qt-bridge-csharp.md`, `02_shapes-spike.md`, `03_vl-node-versions.md`, `docs/topics/element-immutability.md`, `gradients.md`, `style-nodeset.md`, `user-qml.md` (28 pages, about 180 KB). From review 24 only section 4. Checked against the code with targeted searches (device, offscreen, protocol, element nodes, interaction nodes, style nodes, Stride package, QML item, CMake, packaging). Not read: `docs/plans/refactor/` (except the brief), the other reviews.

## 1 Summary

The ten decision pages hold the reasons a contributor needs, and most of their technical claims still match the code: the one route C# → reconciler → command buffer → QtNative, identity by position and type, the sink return channel, `IFrameClock` as the only clock, the shared D3D11 device with fences, the Clear feedback layer, the render-thread pool, MSAA by window swap. The code links to them about twenty times. But they are written as a log: dates on every bullet, "brainstorm", "Status: phases 1–3 implemented", task and phase numbers, "reversed the same day", task-cut tables for orchestrators, private and public issue numbers side by side. A newcomer has to read the history to find the present rule. Two pages are now wrong in what matters to a user: `element-catalog-v1.md` describes pins that no longer exist (`Bounds`, `Pivot` with default Center, `Visible`), and `events.md` describes hover and click outputs on the element node that the Interaction nodes replaced. `decisions/paths.md` contradicts its own plan and the code on the `Anchor` pin. `render-texture.md` alone is 60 KB, a third of the area, and covers six subjects. `docs/plans/paths/` is a finished work plan (01–05) plus issue bodies (06–10); `topics/` holds two handovers for brainstorms, one of them over.

The changes that matter most:
1. Rewrite `element-catalog-v1.md` and `events.md` to the present pin set and the Interaction nodes (DOCD-01, DOCD-02).
2. One folder `docs/design/` with one template for every note (DOCD-06); the long class summaries of the code (first: `Reconciler.cs:9-73`) move there (DOCD-07).
3. Split `render-texture.md` into four design notes and a research page (DOCD-05).
4. Delete `docs/plans/paths/`, `topics/user-qml.md`, `topics/gradients.md`, `research/03`; fold what is still true into the design notes (DOCD-03, DOCD-04, DOCD-11, DOCD-13, DOCD-14).

## 2 What works and must survive

- **The "Rejected alternatives" sections.** They are the most valuable part for a newcomer ("why not Qt on Stride's device", "why not a key pin", "why not a JSON diff", "why not `AddDllDirectory`"). Every rewrite keeps them, each in one or two sentences.
- **Reasons next to rules.** Most bullets say "because …" (e.g. `timing.md:11` why `QT_QPA_UPDATE_IDLE_TIME=0`, `qtnative-deployment.md:39` why not `qt.conf`). Keep that form.
- **The thread affinity table** (`render-texture.md:83-92`). It is the contract of the render thread; the native code relies on it (`render_thread.h:1`, `offscreen.h:3`).
- **Claims that are still true** (checked): device loss and listeners (`device.cpp:56`, `:412`, `moveTo` `:475`), `VLQT_SHARED_DEVICE` (`device.cpp:234`), `VLQT_OFFSCREEN_THREAD` (`offscreen.cpp:72`), `linear_pass.cpp` (`CMakeLists.txt:75`), `SET_CLEAR = 9` (`Protocol.cs:48`), `QT_QPA_UPDATE_IDLE_TIME` (`vlqt.cpp:1050`), `VlqtAnimationDriver` (`vlqt.cpp:131`), `component.create()` without initial properties (`qmlitem.cpp:482`), `ToTexturePixels` node (`ToTexturePixelsNode.cs:17`), the `qt/<rid>/` package layout (`VL.Qt.csproj:54`, `:87`), `buildTransitive/VL.Qt.targets` (`src/VL.Qt/buildTransitive/`), style categories `Qt.Style.Frame` … (`StyleNodes.cs:85`), `ResolveState` (`StyleNodes.cs:67`), the `Dark`/`Light` presets (`VL.Qt.vl`), `Column (Spectral)` naming (`ColumnSpectralNode.cs:21`), the point limit (`Protocol.cs:19`).
- **Measurements with their conditions** (machine, Qt version, scenario, how to rerun). `research/02_shapes-spike.md` is the model; tooltips and `docs/shapes.md` cite its numbers.

## 3 Findings

### DOCD-01 Rewrite the element model note to the present pin set
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/decisions/element-catalog-v1.md:8`, `:12-15`, `docs/topics/element-immutability.md`
- **Finding:** The page says the pins common to all elements are "Bounds, Transform, Pivot, Opacity, Visible, Clip", with `Pivot` (default Center) and `Bounds` as "the layout value". The code has `Size` (`RectangleNode.cs` `[DefaultValue("1, 1")] Vector2 size`), an optional `Anchor` with default `TopLeft`, no `Visible` pin, and the common inputs `size, transform, anchor, opacity, clip, debug, interaction, effect` (`ElementNode.cs:18`). A reader who trusts the page builds a wrong picture of every node. The coordinate-space table (`:18-30`) is still right (`Protocol.cs:493-496`) but carries "plan B, #143", "the spike of #143, T0". The title "Element Model" and the file name "catalog-v1" disagree. `topics/element-immutability.md` is the other half of the same subject (immutable descriptions, deliberate exceptions) filed as a topic.
- **Proposal:** New `docs/design/element-model.md` from both pages, in the template of DOCD-06: Context (vvvv describes, Qt works), Design (immutable descriptions and their exceptions; common pins `Size`, `Transform`, `Anchor`, `Opacity`, `Clip`, `Debug`, `Interaction`, `Effect` with the reason for each; `Transform` after layout around the anchor point, checked against `PropertyDiff`; typed pins instead of a `Properties` pin; image content from a handle, not a path), Consequences, Alternatives considered (transform wrapper nodes, generic pins, mirroring Qt state into descriptions). The `Space` table goes to the user page `docs/coordinates.md` of review 24; the design note keeps only why C# scales and QtNative moves the origin (blurry layers with a scaled root, `ToFontPixels`), without issue and spike numbers. Delete both old files.
- **Decided:** C2, catalog, A3, A6, C3, O3, P11 → the common pins in Design are `Size` (optional after `Auto Size` where that is on by default), `Transform`, `Anchor`, `Opacity`, `Clip`, `Debug`, `Interaction`, `Effect` and `Enabled` (optional, default true, the last input pin; it removes the element from the scene and stops input, the siblings in Row/Column move up); concrete element classes are internal and the nodes output `IElement` (A6, C3); the diff and the protocol knowledge live in `Reconciliation/`, not in the element records (A3); Verification reads `ElementNodeCore` and `CommonInputs`, not `ElementNode.cs`
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/element-catalog-v1.md` (delete), `docs/topics/element-immutability.md` (delete), `docs/design/element-model.md` (new), `docs/user-qml.md` (link), `docs/README.md` (index)
- **Depends on:** DOCD-06
- **Verification:** every pin named on the page exists in `src/VL.Qt/Nodes/ElementNode.cs` `CommonInputs` and `RectangleNode.Update`; `python tools/vl_pin_catalog.py`-style listing of `Rectangle` matches.

### DOCD-02 Rewrite the events note around the Interaction nodes
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/decisions/events.md:7`, `:11-12`, `:26-27`
- **Finding:** The page says hover and click come out of the element node ("With interaction enabled: `IsHovered`, `IsPressed`, `OnClick` … and `PointerPosition`") and that there is "no listener node". The code has Interaction nodes (`src/VL.Qt/Nodes/Interaction/`: `Hover`, `Click`, `Drag`, `Point`, `Pinch`, `TouchPoints`, `Wheel`) that are connected to the `Interaction` pin of an element and output `Is Hovered` etc. themselves (`HoverNode.cs` summary). The element node keeps only `Measured Size` and `Scene Bounds` (optional, `RectangleNode`). Most of the open questions (drag, wheel, multitouch, two-way text value) are answered by those nodes and the input elements. The sink mechanism, the one-frame latency, the multiple-occurrence rules and the rejected alternatives are still right and needed.
- **Proposal:** New `docs/design/events-and-interaction.md`: Context (Qt objects live in the reconciler, the patch reads values in `Update`), Design (sink per node instance, bound per frame by position; one-frame latency and why; Interaction nodes as chainable handler descriptions on the `Interaction` pin; pulse plus counter for events; values for states; multiple occurrences: events merge, states from the primary occurrence; what runs on which thread, including the locked notification queue of `QtTexture` input), Consequences (state jump on reorder), Alternatives (callbacks, channels for events, sink as identity, listener node by id), Open questions (key pin; interaction on two occurrences). The long summaries of `SinkRegistry.cs:20` ff. link here. Delete `events.md`.
- **Decided:** A1, A2, B6, O7, catalog → the 'sink' is feedback: `ElementFeedback`, `FeedbackSlot`, `FeedbackRouter` (in `Reconciliation/`), and the event header carries `feedbackId` (O7); the occurrence registry (not the diff) keys an element by its feedback slot, one node is one element (A2), which the note must not contradict under 'sink as identity'; for an element shown at several places hover and press are the OR over all occurrences (B6), not 'states from the primary occurrence', and 'interaction on two occurrences' leaves the open questions; the Interaction nodes use the pin names of the catalog (`On Click`, `On Long Press`, `Min`/`Max` on Drag, node `TouchPoints`); the summaries link from `FeedbackRouter.cs`
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/events.md` (delete), `docs/design/events-and-interaction.md` (new), `docs/README.md`, `src/VL.Qt/Elements/SinkRegistry.cs` (link)
- **Depends on:** DOCD-06
- **Verification:** each output named on the page exists as a pin of a node under `src/VL.Qt/Nodes/Interaction/` or `ElementNode`; `help/Qt/HowTo …` interaction patches show the same names.

### DOCD-03 One paths design note; fix the `Anchor` contradiction
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/decisions/paths.md:3`, `:9`, `:29`, `:39-40`; `docs/plans/paths/00_overview.md:39-46`; `src/VL.Qt/Nodes/LineNode.cs:74`
- **Finding:** `paths.md:29` lists "`Transform`, `Anchor`, `Opacity` as on all elements" as shape pins; the plan says "The shape nodes have no `Anchor` and no `Size` pin" (`00_overview.md:45`), and the code passes a fixed `RectangleAnchor.TopLeft` (`LineNode.cs:74`). The plan is right, the decision page is wrong. The page is titled "(brainstorm)", every bullet carries "(2026-11-18)", two rejections say "reversed the same day", and the decisions that a contributor needs most (protocol layout of `VT_F32_ARRAY`/`VT_PATH`, the 8190-point limit and its derivation, defaults that follow Skia, dash lengths divided by the stroke width, geometry in parent space, change detection by reference then content) are only in the plan, which is a work order.
- **Proposal:** New `docs/design/paths.md` from `decisions/paths.md` plus the "Fixed in the plan" section of `00_overview.md` and its architecture diagram: Context (motion graphics and data-heavy content; Qt Quick Shapes, `CurveRenderer` explained in one sentence), Design (one `SHAPE` type; `VlPathElement` as a subclass of Qt's private `QQuickCurve`; immutable `QtPath` chain, flattened once; arcs to cubics in C#, five verbs; Skia-named builder nodes; limits with the arithmetic; defaults; geometry space without `Anchor`/`Size`; miter limit rounded), Consequences (point limit, cost per point from the benchmark), Alternatives (native `QPainterPath` handle, `PathSvg`, one `PathCubic` per segment, `Line` as rotated `Rectangle`, `Canvas`/`QQuickPaintedItem`, `ConicTo`, `Relative` as pin), Open questions (renderer pin and `GeometryRenderer`, mass data, Skia adapter, more `Add…` nodes, gradients: one line each, linked to the public issue). No dates, no "reversed". Delete `decisions/paths.md`.
- **Decided:** A4, P8, C7, C12, catalog, G7, S2 → with the `u32` length the 64 KB limit and with it the point cap of Shape/Path (8190) go (A4, on the condition of P8), so Design and Consequences state a point limit only if a fallback of P8 (`u32` for events only, then `u16`) is taken; AddRoundRect scales too large radii together as Skia does (C7); pin names and defaults follow the Qt.Paths rows of the catalog (`Direction`, `End Point`, `Closed`, defaults `0, 0, 1, 1`); the open questions link the public issue sebllll/VL.Qt#155 (G7); `VlPathElement` lives in `elements/shape_path_element` (S2)
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/paths.md` (delete), `docs/design/paths.md` (new), `docs/shapes.md` (links), `src/VL.Qt.Native/elements.cpp:1043` (link), `docs/README.md`
- **Depends on:** DOCD-06, DOCD-04
- **Verification:** pins named on the page against `LineNode`, `PolylineNode`, `PolygonNode`, `PathNode`; protocol numbers against `protocol.h` (`--drift` covers the C# side).

### DOCD-04 Delete the finished paths plan
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/plans/paths/00_overview.md` … `10_gradients-brainstorm.md`
- **Finding:** Packages 01–05 are done (commits `3997e82`, `7b87f8c`, `c4e5358`; nodes exist under `src/VL.Qt/Nodes/Paths/`). Their pages are work orders: unchecked boxes, line numbers of files as they were (`02_foundation.md:10` cites `PropertyDiff.cs` `:22-36`, `commands.cpp` `:644-720`), model choices per task ("Sonnet", "Opus"), orchestrator rules, "Creative Cloud causes ~55 % background CPU" (`01_spike.md:13`). Packages 06–10 are issue bodies for milestone 2.0 ("the issue bodies hold the full package texts", `00_overview.md:3`). For the reader of the public repository this is history; the design content is folded into `design/paths.md` by DOCD-03.
- **Proposal:** Delete the folder `docs/plans/paths/`. Before that, make sure each of 06–10 exists as a public issue (or one issue with the five items) and that `design/paths.md` "Open questions" links them. Git history keeps the plan.
- **Decided:** G7, G6 → confirmed: sebllll/VL.Qt#155 (open, milestone 2.0) holds the package texts from 'Paths 06' on with goal, steps and acceptance criteria, so no issue is created; this finding only checks that nothing else is only in the pages; finished plans are deleted, not archived (G6)
- **Effort:** S, mechanical: yes
- **Touches:** `docs/plans/paths/*` (delete), `docs/README.md` (index line), `docs/research/02_shapes-spike.md` (links to the plan)
- **Depends on:** DOCD-03
- **Verification:** `grep -rn "plans/paths" docs src tools help README.md` finds nothing.

### DOCD-05 Split `render-texture.md` into four design notes and a research page
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/render-texture.md` (whole file, 60 KB); code links `vlqt.h:210`, `:496`, `:570`, `:578`, `vlqt.cpp:1684`, `offscreen.h:3`, `render_thread.h:1`, `StrideInterop.cs:13`, `TextureFormats.cs:6`, `ClearPinTest.cs:20`, `MsaaTest.cs:19`, `VL.Qt.Stride.csproj`, `README.md`, `docs/architecture.md`, `docs/diagnostics.md`, `docs/development.md`
- **Finding:** The page is a decision log of the `QtTexture` brainstorm that grew to six subjects: device and adapter, formats and colour space, ownership and timing, the Stride layer `QtRenderer`, input and keyboard focus, the Qt render thread, the Clear pin, MSAA, and the package layout. The status line (`:3`) lists nine issue numbers and phases; there are about 20 lines with issue numbers, task numbers ("task 1.5", "5.2a", "task 3.3"), spike branches (`spike/116-render-thread`), and a "Contract: input (#119, task 1)" section (`:109-139`) that describes stubs "laid down … later tasks fill them … the stubs are gone". Bullets of 300–600 words (`:28`, `:29`, `:146`) mix the rule, the measurement and the history. Measurements (render-thread table `:63-68`, MSAA table `:184-189`, Clear cost `:154`) sit between rules. A contributor who follows a code link "docs/decisions/render-texture.md, 'MSAA'" lands in the middle of this.
- **Proposal:** Four notes in `docs/design/`, each in the template of DOCD-06, present tense, no issue or task numbers:
  - `stride-output.md`: `QtTexture` and `QtRenderer [Stride]`. Why a companion package (ImGui pattern), shared texture and fences, same adapter and adapter move, formats and the linear pass (with the colour arithmetic of `:28` and `:49` as a short worked example), premultiplied output, ownership and disposal order, frame timing (`GameSystem` with lowest `DrawOrder`), layer composition through a UNorm view and its fallback, package layout (`:100-104`). Alternatives: Qt on Stride's device, keyed mutex, CPU readback, deferred disposal, double buffering.
  - `offscreen-input.md`: notifications pin, coordinates, `ToTexturePixels`, translation, native injection, keyboard focus with the private `QQuickRenderControlPrivate` (explain "render control" once), the focus rule, timing. The "Contract" section is deleted; signatures live in `vlqt.h` and the code.
  - `render-thread.md`: pool, default 1, environment switch, the device model under threads (multithread protection and locks), "same frame" sequence of signal/wait (as a mermaid sequence diagram), back pressure, WARP fallback, the thread affinity table. Measurements out.
  - `clear-and-msaa.md`: the feedback layer (structure, first frame, fade with the alpha of Clear Color, resize, attachment and `moveToWindow`, software backend, cost of one live recursive layer) and MSAA (why a window swap, clamp, defaults per node, offscreen resolve, what is not solved).
  - `docs/research/render-texture-measurements.md`: the three measurement blocks with machine, Qt version and the SmokeTest scenario to rerun (`qttextureperf`, `clear-pin`, `Msaa`).
  The device facts (`:22-24`, `:43`) go to `design/gpu-device.md` (DOCD-09). Update the code links in the same change (DOCD-15). Delete `render-texture.md`.
- **Decided:** G5, G8, E4, E6, E7, O1, O2 → the notes go to `docs/design/`; `render-thread.md` says that QtNative runs Qt's `basic` render loop on purpose (E4) and describes the pool through the pin `Render Threads` of QtTexture and QtRenderer (default 1, the maximum wins, 0 = main-thread mode; at runtime the pool only grows, a smaller value and 0 apply to scenes created afterwards; P7) instead of an environment switch; `clear-and-msaa.md` calls the layer the `PreviousFrameLayer` (it keeps the previous frame while `Clear` is off; 'feedback' only means the way back from Qt, O1) and uses 'scene' as in O2; the three measurement blocks keep machine, Qt version and the scenario to rerun and say 'x64 not measured' (G8)
- **Effort:** M, mechanical: no
- **Touches:** `docs/decisions/render-texture.md` (delete), `docs/design/stride-output.md`, `offscreen-input.md`, `render-thread.md`, `clear-and-msaa.md`, `docs/research/render-texture-measurements.md` (new), plus every file listed under Where (links)
- **Depends on:** DOCD-06, DOCD-09, DOCD-15
- **Verification:** `grep -rn "render-texture.md" .` finds nothing; every rule of the old page is in one new page (a reviewer compares the headings of both); `tools/build.ps1 -Test` unaffected (docs only, comments in code).

### DOCD-06 One folder and one template for design notes
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/*`, `docs/topics/*`, `docs/research/*`
- **Finding:** The same kind of page lives in three folders with three forms: "Design / Open questions / Rejected alternatives" (`timing.md`, `events.md`), "Decided / Open / Rejected" with dates (`paths.md`, `qt-bridges.md`, `user-qml.md`), "Requirements / Facts found / Decided / sections" (`render-texture.md`), "Principle / How it is realised / Deliberate exceptions" (`topics/element-immutability.md`), "Background / Design" (`topics/style-nodeset.md`), "Context / Idea / Open questions / Relevant files" (handovers). `topics/` mixes finished design notes with brainstorm handovers. The name `decisions/` suggests a dated log, which the pages partly are.
- **Proposal:** Folder `docs/design/` for every note that explains why something is built as it is (it replaces `decisions/` and `topics/`); `docs/research/` for dated measurements and external research that a note cites. No plans and no handovers in the repository: open work is an issue. One template:

  ```markdown
  # <Subject>                       (a noun: "Reconciler", "Timing", not "Decision: …")

  One paragraph: what this note covers, the code it is about (folders, files, types),
  the user page that describes the behaviour.

  ## Context
  The problem and its constraints, as a newcomer needs them. Qt terms explained once
  (or linked to docs/glossary.md).

  ## Design
  The present state as rules and invariants, each with its reason ("because …").
  Sub-headings per aspect; a mermaid diagram where a flow or sequence helps.

  ## Consequences
  What follows for users and contributors: limits, costs, what breaks if you change X,
  which tests protect it.

  ## Alternatives considered
  - **<alternative>:** why not, in one or two sentences.

  ## Open questions
  Only real open points, each with a link to a public issue.

  ## Evidence
  Measurements or experiments the design relies on: machine, Qt version, date,
  how to rerun (SmokeTest scenario), or a link to a page in docs/research/.
  ```

  Rules for every note: present tense; dates only under Evidence; no private issue numbers, no plan, package, task, phase or session names; no line numbers (name the file and the symbol); one subject per note, about 15 KB at most; a code comment that points to a note names the file and a heading of the template or of a sub-heading under Design. Add the template and the rules to `docs/README.md` (section "Design notes", review 24 DOC-16) or to `docs/contributing/` (review 24 tree).
- **Decided:** G8 → the Evidence section also says 'x64 not measured' where only the arm64 notebook was measured
- **Effort:** S (the template and rules), mechanical: no; the rewrites are the other findings
- **Touches:** `docs/README.md`, new folder `docs/design/`
- **Depends on:** none
- **Verification:** each page in `docs/design/` has the five headings; `grep -nE "#[0-9]+|task [0-9]|20[0-9]{2}-[0-9]{2}-[0-9]{2}" docs/design` finds nothing outside "Evidence".

### DOCD-07 A reconciler design note as the home for the long class summaries
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/reconciler-identity.md`; `src/VL.Qt/Reconciler/Reconciler.cs:9-73` (a summary of about 65 lines), `src/VL.Qt/Reconciler/SceneRegistry.cs:11`, `src/VL.Qt/Elements/SinkRegistry.cs:20`
- **Finding:** The code review proposes moving long class summaries into design pages. The identity note is already the right subject, but it is framed as a decision ("Context: how comparable systems handle identity") and lacks what the class summary explains (diff phases, the reference-equality skip, resync, rescale, styles resolved on the way down). Its rules on several renderers name `#126` and the primary occurrence rules are a dense paragraph (`:28-33`).
- **Proposal:** `docs/design/reconciler.md` replaces `reconciler-identity.md`. Structure (template of DOCD-06): Context (descriptions in, retained Qt objects out; comparison with Skia layers, Stride entities, ImGui, React in a short table); Design with sub-headings *Identity* (position plus type, type change, insert/delete, reparent), *Skipping unchanged subtrees* (reference equality, the resolved style in the skip test), *One frame* (the diff passes and what the command writer receives, as a mermaid flow), *Multiple occurrences and several renderers* (primary occurrence, owner renderer, `Video` exclusive), *Sink binding* (link to `events-and-interaction.md`), *Rescale and resync*; Consequences (what a contributor must keep: no allocation per frame in the diff, defaults of a new element equal QtNative defaults); Alternatives considered (the seven of `:43-49`); Open questions (key pin, interaction on several occurrences, `Mirror` element). The class summary of `Reconciler.cs` keeps 3–5 lines (what the class does, thread, "Design: docs/design/reconciler.md, Identity"). The same pattern holds for every subsystem: a long summary goes to the note of its subsystem (`timing.md`, `render-thread.md`, `stride-output.md`, `events-and-interaction.md`, `paths.md`, `styles.md`); a new note is created only for a subsystem that has none.
- **Decided:** A1, A2, B6, O3, TARGET 1.2 and 2.2 → the note names the final types, all in `Reconciliation/`: `OccurrenceRegistry` (was `SceneRegistry`), `FeedbackRouter` (was `SinkRegistry`), `ItemMirror` and `HandlerMirror` (was `Node`, `HandlerNode`); 'Sink binding' is 'Feedback binding'; the registry keys an element by its feedback slot, so two instances of one node in one tree are occurrences of one element and a Video has one player (A2); hover and press over several occurrences are the OR of all (B6), which removes 'interaction on several occurrences' from the open questions; the summaries of `Reconciler.cs`, `OccurrenceRegistry.cs` and `FeedbackRouter.cs` shrink to 3 to 5 lines
- **Effort:** M, mechanical: no
- **Touches:** `docs/decisions/reconciler-identity.md` (delete), `docs/design/reconciler.md` (new), `src/VL.Qt/Reconciler/Reconciler.cs`, `src/VL.Qt/Reconciler/SceneRegistry.cs`, `docs/topics/element-immutability.md` (link, deleted by DOCD-01), `docs/README.md`
- **Depends on:** DOCD-06; the reconciler review (05) for the content of the summary
- **Verification:** the rules on the page against `ReconcilerStructureTests`, `ReconcilerSceneTests` (multiple occurrences, several renderers).

### DOCD-08 Timing note: remove stale statements, settle the Animator question
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/timing.md:7`, `:13`, `:16`, `:21`, `:27`
- **Finding:** `:13` says "the form-based window that replaces the plain window behaves like them"; there is no form window, `Renderer.cs:74` says "The Qt window is no Form". `:27` rejects a substitute pump as "effort for a transitional window"; the window is not transitional. `:7` says "Scenes on their own threads are only worth adding for a real need"; offscreen scenes now render on a thread pool (`render_thread.h`) while the clock stays on the main thread: the page does not say so. `:16` ("frame-exact offline capture comes with the texture output") is a plan, not a design. `:21` (open): "`Animator` types run on the render thread in the `threaded` loop, bypassing our driver … harmless with the basic loop or render control on the main thread": since the render thread exists, render control is no longer only on the main thread. Suspicion, not confirmed: an `OpacityAnimator` in a user QML file inside a threaded `QtTexture` scene may run on the render thread with its own time and bypass `VlqtAnimationDriver`. Nothing in QtNative handles animators (`grep Animator src/VL.Qt.Native` finds nothing).
- **Proposal:** Rewrite as `docs/design/timing.md` (template): Design keeps the driver, the single clock, the order per frame, `QT_QPA_UPDATE_IDLE_TIME`, pause, VSync; one sub-heading "Render threads" (rendering of offscreen scenes moves to the pool, time and animation stay on the main thread; link `render-thread.md`). Remove the form-window sentences and the capture plan (an issue if wanted). Open question on animators: replace with the result of a check (see Verification) and state the rule.
- **Decided:** E4, E6, E7, E11 → the note says that QtNative runs Qt's `basic` render loop on purpose (E4); its 'Render threads' sub-heading names the pin `Render Threads` (default 1, 0 = main-thread mode, the maximum wins, at runtime it only grows; E7) and `VLQT_OFFSCREEN_THREAD` is gone (E6), so the Verification sets that pin instead; QML animations follow the animation clock forward and a backward jump does not rewind them (E11, documented)
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/timing.md` (delete), `docs/design/timing.md` (new), `docs/architecture.md`, `docs/diagnostics.md`, `docs/user-qml.md` (links), `src/VL.Qt.Native/vlqt.cpp:124` (link)
- **Depends on:** DOCD-06
- **Verification:** for the animator question: a SmokeTest run with a `QmlElement` whose file holds an `OpacityAnimator`, in a `QtTexture` scene with `VLQT_OFFSCREEN_THREAD=1`, paused clock: if the opacity still moves, the suspicion holds and becomes an issue for the QML area.

### DOCD-09 One GPU device note
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/device-sharing.md:22-27`, `:30`; `docs/decisions/render-texture.md:14-24`, `:43`
- **Finding:** The device model is split: `device-sharing.md` has the device, teardown and loss; `render-texture.md` has the same adapter, the adapter move and "native side owns the D3D work". `device-sharing.md` repeats the Stride layer naming (`:26`) that belongs to the Stride note, and its open question "With a Qt render thread (planned as a separate issue) …" (`:30`) is answered by the render-thread design. `render-texture.md:17` still explains that `device-sharing.md` once "named Mode 1 … which the code never followed": history.
- **Proposal:** `docs/design/gpu-device.md`: one QtNative device per process, attach before first expose, upload QRhi (explain QRhi once: Qt's graphics abstraction), generation counter, teardown order, device loss and listeners, adapter matching and `moveTo`, the rule that all unsafe D3D11 code stays in C++, `VLQT_SHARED_DEVICE=0` as a diagnosis switch, Linux/OpenGL behaviour. Alternatives: Qt on Stride's device, keyed mutex. Open: fence requirements (`ID3D11Device5`) only if still unverified. The Stride layer paragraph moves to `stride-output.md`. Delete `device-sharing.md`.
- **Decided:** B8, E9, S2, 'Pins instead of environment variables' → the note also states: if QtNative cannot create its GPU device it retries once per second, meanwhile windows use their own devices, images do not upload, offscreen scenes report `DEVICE_LOST` and the log says so once (B8); after a GPU reset a file image is read again from disk (E9); `VLQT_SHARED_DEVICE=0` stays an environment variable because it acts before Qt starts; the source link is `device/device.h` (S2)
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/device-sharing.md` (delete), `docs/design/gpu-device.md` (new), `src/VL.Qt.Native/device.h:8` (link), `docs/architecture.md` (link)
- **Depends on:** DOCD-05, DOCD-06
- **Verification:** symbols named on the page exist in `device.h`/`device.cpp`.

### DOCD-10 Split the deployment note into "native boundary" and "deployment"; bring it up to date
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/qtnative-deployment.md:13-15`, `:23`, `:41-44`, `:57`
- **Finding:** The page combines three subjects: the API between C# and QtNative (command buffer, one call per frame, logging), licensing and Qt version, and build/packaging/runtime lookup/export. `:23` says "CMake + MSVC for win-x64 and win-arm64"; QtNative builds for Linux too (`CMakeLists.txt:12` `linux-x64`, `libvlqt.so`). `:13` names the calls `apply(buf, len)` and `poll(outBuf)`; the exports are `vlqt_window_apply` and `vlqt_window_poll`. `:57` rejects embedding into a WinForms form because "the form plus render control makes it unnecessary"; neither exists. Build instructions (`:25-28`) duplicate `docs/development.md`; export mechanics (`:41-44`) duplicate `docs/export.md`.
- **Proposal:** Two notes. `docs/design/native-boundary.md`: why one command buffer per frame (one transition, small stable native API, live-editable logic, record and replay), the C ABI, events back through polling, logging through `qInstallMessageHandler` and the shutdown rule, STA main thread; Alternatives: one P/Invoke per property, JSON/QML diff. `docs/design/deployment.md`: LGPL model and module check, pinned Qt version and the minimum, one package with the runtime, the `qt/<rid>/` tree, runtime lookup (process-local `PATH`, `addLibraryPath`, import path) with the rejected `qt.conf`, `QT_PLUGIN_PATH`, `AddDllDirectory`; export (source package vs installed package, keep-alive) in short with a link to `export.md`; platforms win-x64, win-arm64, linux-x64. The form sentence goes. Build steps stay only in `development.md`. Delete `qtnative-deployment.md`.
- **Decided:** A5, B3, B4, A4, P4, F1, D9, S2 → the C ABI is named `vlqt_<group>_<verb>`, so the calls are `vlqt_scene_apply` and `vlqt_scene_poll` (A5); every export returns a code and the text of the last failure is kept per thread (B4); `VL.Qt.dll` and `vlqt.dll` of different versions are refused, the Renderer is marked and the log names both versions (B3); commands and events carry a `u32` length (A4, on the condition of P8) and the layer breaks once, later additions raise the protocol version (P4); the test exports are in `vlqt_test.h` and no part of the C ABI (F1); the deployment note says that Qt private classes are used and listed in the head comment of `elements.h` as the checklist of a Qt update (D9); the link in `Native.cs:118` moves with the split of `Native.cs` into `Interop/Native.*.cs`
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/qtnative-deployment.md` (delete), `docs/design/native-boundary.md`, `docs/design/deployment.md` (new), `src/VL.Qt/Native.cs:118` (link), `docs/user-qml.md`, `docs/topics/user-qml.md` (deleted by DOCD-11)
- **Depends on:** DOCD-06
- **Verification:** export names against `vlqt.h`; platforms against `.github/workflows/build.yml` RIDs.

### DOCD-11 User QML note: present state only; delete the handover
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/decisions/user-qml.md:3`, `:7-13`, `:15-25`, `:42-60`, `:66-68`; `docs/topics/user-qml.md`
- **Finding:** Every bullet carries a date and "*Done.*"; a section "Where the implementation differs from the first design" (`:15-25`) explains deviations from an issue text nobody can read; "Task cut (#107)" (`:48-60`) is an orchestrator table with file ownership per task; "Rejected: *(none yet)*". `:11` cites `vlqt.cpp:115`, the class is now at `vlqt.cpp:131` (line numbers rot). The parser decisions (`:31-40`) are good design content. `topics/user-qml.md` is the handover of the brainstorm: all seven open questions are answered in the decision page.
- **Proposal:** `docs/design/user-qml.md` (template): Context (escape hatch, not a second UI model), Design: *Element* (`QmlElement`, properties in and out, no echo of the value just set, auto size, hot reload scope), *Node factory* (C# parser, supported types, defaults, pins, discovery in `qml/` folders, hot reload by signature, errors), *Time* (animations follow the driver, `Timer` does not), *Licensing* (no import filter, why); Consequences (no `Experimental` smell possible for factory nodes; `required property` limitation with the symbol `QmlItem` creation, no line number); Alternatives (Qt Bridge wrappers, native probe for pins); Open (context properties, function calls). Deviations become plain statements of behaviour, the task cut goes. Delete `decisions/user-qml.md` and `topics/user-qml.md`.
- **Decided:** E3, E2, D5, D6, E12, catalog → `required property` is fixed in QtNative (`createWithInitialProperties`, E3), so Consequences lists it as supported and no longer as a limitation (until the fix is merged the page keeps it as a known limitation); while a QML file does not compile, the node keeps the last good pins, values and component and shows the error at the node (E2, through the shared folder node factory of D6, `NodeFactories/`); the pin parser is the hand-written C# one, kept and fixed (D5); the no-JavaScript rule applies to built-in elements only (E12); `QmlElement` is in the category `Qt.Elements.Qml`
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/user-qml.md`, `docs/topics/user-qml.md` (delete), `docs/design/user-qml.md` (new), `docs/user-qml.md`, `docs/architecture.md` (links), `src/VL.Qt/Qml/QmlDeclarations.cs:131`, `src/VL.Qt.Native/protocol.h:229`, `src/VL.Qt.Native/vlqt.cpp:124` (links)
- **Depends on:** DOCD-06
- **Verification:** behaviour statements against `docs/user-qml.md` (user page) and `QmlDeclarationsTests`, `QmlNodeFactoryTests`.

### DOCD-12 Fold the Qt Bridge decision and research into one page
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `docs/decisions/qt-bridges.md`, `docs/research/01_qt-bridge-csharp.md`
- **Finding:** The decision page is 1.7 KB of dated bullets pointing to the research page; the research page holds the comparison tables that answer the question a vvvv user will ask ("why not Qt's own C# bridge?"). Review 24 wants a Qt Bridge comparison in `README.md`.
- **Proposal:** `docs/design/qt-bridge.md`: Context (what the Bridge is, its status as of its date under Evidence), Design ("VL.Qt does not use the Bridge" with the three reasons: MVVM vs patch-built UI, build-time code generation with a C++ toolchain, Qt owns loop and clock), the comparison table and the redundancy table, Alternatives (Bridge as a building block: one `QGuiApplication` per process), Open (watch points), Evidence (sources, date). `README.md` gets three lines and a link. Delete both old pages.
- **Effort:** S, mechanical: no
- **Touches:** `docs/decisions/qt-bridges.md`, `docs/research/01_qt-bridge-csharp.md` (delete), `docs/design/qt-bridge.md` (new), `README.md`, `docs/user-qml.md`, `docs/README.md`
- **Depends on:** DOCD-06
- **Verification:** links resolve (`grep -rn "qt-bridges\|01_qt-bridge" .` empty).

### DOCD-13 Research pages: names by subject, no plan links, dead branch links
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `docs/research/02_shapes-spike.md:3`, `:7-12`, `:21`, `:123-127`; `docs/research/03_vl-node-versions.md`
- **Finding:** `02_shapes-spike.md` is good research (questions, method, numbers, consequences) but refers to "package 01/02/06/07/08", "issue #154/#155", and to a spike branch and its PNGs on GitHub (`spike/154-shapes`) that will disappear when the branch is deleted (not checked whether it exists on the public remote). The machine-load remark ("Creative Cloud keeps about 55 % of the CPU busy") is honest but private context; "a loaded machine" says the same. `03_vl-node-versions.md` answers a how-to question for contributors in five lines ("put the version in the node name") and is not research.
- **Proposal:** Rename `02_shapes-spike.md` → `docs/research/shapes-benchmark.md`; replace package references by the subject ("the renderer pin", "mass data"), keep the three PNGs that are in `docs/images/`, cite the other PNGs only if they move to `docs/images/`, drop the branch links or keep one link with "branch kept for reference" if the branch stays. Move the answer of `03` into the contributor docs (review 24's `docs/contributing/add-an-element.md` or the node rules in `AGENTS.md`: "a version such as `(Relative)` is part of `ProcessNode.Name`") and delete the page. No numeric prefixes in `research/`.
- **Decided:** G8 → the benchmark page keeps machine, Qt version and the scenario to rerun and says 'x64 not measured'
- **Effort:** S, mechanical: no
- **Touches:** `docs/research/02_shapes-spike.md` (rename), `docs/research/03_vl-node-versions.md` (delete), `docs/shapes.md`, `docs/README.md`, `AGENTS.md` or `docs/contributing/add-an-element.md`
- **Depends on:** DOCD-04
- **Verification:** `grep -rn "02_shapes-spike\|03_vl-node-versions" .` empty.

### DOCD-14 Gradients handover → issue
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `docs/topics/gradients.md`, `docs/plans/paths/10_gradients-brainstorm.md`
- **Finding:** A handover for a brainstorm that has not happened, with four open questions and file pointers (`C:\Qt\6.12.0\<arch>\…`). It is a feature request, not a design.
- **Proposal:** Put the context and the four questions into one public issue (milestone 2.0, `question`, `area:native`, `area:csharp`); `design/paths.md` "Open questions" links it. Delete both pages.
- **Decided:** G7 → no new issue: sebllll/VL.Qt#155 (open, milestone 2.0) already holds the package texts from 'Paths 06' on; the gradients questions are added to it only if they are not in it yet, and `design/paths.md` links #155
- **Effort:** S, mechanical: yes
- **Touches:** `docs/topics/gradients.md` (delete), `docs/decisions/paths.md` → `docs/design/paths.md` (link), `docs/README.md`
- **Depends on:** DOCD-03
- **Verification:** the issue exists; no link to `topics/gradients.md` remains.

### DOCD-15 Update the code's links to design notes in one mechanical step
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `src/VL.Qt/Elements/SinkRegistry.cs:20`, `src/VL.Qt/Native.cs:118`, `src/VL.Qt/Qml/QmlDeclarations.cs:131`, `src/VL.Qt/Reconciler/Reconciler.cs:13`, `src/VL.Qt/Reconciler/SceneRegistry.cs:11`, `src/VL.Qt.Native/device.h:8`, `elements.cpp:1043`, `offscreen.h:3`, `protocol.h:229`, `render_thread.h:1`, `vlqt.cpp:124`, `vlqt.cpp:1684`, `vlqt.h:210`, `:496`, `:570`, `:578`, `src/VL.Qt.SmokeTest/ClearPinTest.cs:20`, `MsaaTest.cs:19`, `src/VL.Qt.Stride/StrideInterop.cs:13`, `TextureFormats.cs:6`, `VL.Qt.Stride.csproj`; docs: `README.md`, `docs/architecture.md`, `docs/development.md`, `docs/diagnostics.md`, `docs/shapes.md`, `docs/user-qml.md`, `docs/README.md`
- **Finding:** These comments are the main way a reader of the code finds the reasons, and some of them name sections ("'MSAA'", "'Qt render thread'", "'Input'"). DOCD-01 … DOCD-14 move or rename every target. Some of the comments also carry issue numbers (K1).
- **Proposal:** After the notes exist, replace each path with the new one (`docs/design/<note>.md`) and each section name with the new heading; remove the issue number in the same line (K1). Do it in the same change as the restructure, or the links break for a release.
- **Decided:** G5, P14, S2, A1, TARGET 2.2 and 2.3 → the targets are `docs/design/<note>.md`; the code side uses the final paths, not the old ones: `Reconciler/Reconciler.cs` → `Reconciliation/Reconciler.cs`, `SceneRegistry.cs` → `Reconciliation/OccurrenceRegistry.cs`, `SinkRegistry.cs` → `Reconciliation/FeedbackRouter.cs`, `Native.cs` → `Interop/Native.*.cs`, native files in their folders (`device/device.h`, `offscreen/offscreen.h`, `device/render_thread.h`), and the links in `vlqt.cpp` and `elements.cpp` move with the code into the files those are cut into; the bare issue numbers are removed in wave 3 (P14), only comments that need a new sentence are rewritten here; this finding runs after the cuts of wave 4, in wave 6
- **Effort:** M, mechanical: yes
- **Touches:** the files listed under Where
- **Depends on:** DOCD-01 … DOCD-14
- **Verification:** `grep -rnE "docs/(decisions|topics|plans/paths)|research/0[0-9]_" src tools help .github README.md AGENTS.md docs` finds nothing; every `docs/design/*.md` path named in the code exists.

## 4 Target structure

```
docs/
  design/                              design notes, template of DOCD-06 (new folder; replaces decisions/ and topics/)
    element-model.md                   immutable descriptions, common pins, why typed pins, images by handle          new ← decisions/element-catalog-v1.md + topics/element-immutability.md (DOCD-01)
    reconciler.md                      identity, skipping, one frame, multiple occurrences, rescale/resync            new ← decisions/reconciler-identity.md + Reconciler.cs summary (DOCD-07)
    events-and-interaction.md          sinks, Interaction nodes, latency, threads                                     new ← decisions/events.md (DOCD-02)
    timing.md                          animation driver, single clock, frame order, VSync, render threads             rewrite of decisions/timing.md (DOCD-08)
    native-boundary.md                 command buffer, C ABI, polling, logging                                        new ← decisions/qtnative-deployment.md part (DOCD-10)
    deployment.md                      LGPL, Qt version, package, runtime lookup, export, platforms                   new ← decisions/qtnative-deployment.md part (DOCD-10)
    gpu-device.md                      one D3D11 device, loss, adapter, teardown                                      new ← decisions/device-sharing.md + render-texture.md device part (DOCD-09)
    stride-output.md                   QtTexture, QtRenderer [Stride], formats, lifetime, timing, package layout      new ← decisions/render-texture.md (DOCD-05)
    offscreen-input.md                 input and focus for offscreen scenes                                           new ← decisions/render-texture.md (DOCD-05)
    render-thread.md                   thread pool, fences, affinity table                                            new ← decisions/render-texture.md (DOCD-05)
    clear-and-msaa.md                  feedback layer, window swap for MSAA                                           new ← decisions/render-texture.md (DOCD-05)
    paths.md                           shapes and paths design                                                        new ← decisions/paths.md + plans/paths/00_overview.md facts (DOCD-03)
    styles.md                          style node set: chain, components, states, categories                         moved ← topics/style-nodeset.md (template headings; content still true)
    user-qml.md                        QmlElement and QML node factory design                                         rewrite ← decisions/user-qml.md (DOCD-11)
    qt-bridge.md                       why not Qt Bridge for C#, comparison                                           new ← decisions/qt-bridges.md + research/01 (DOCD-12)
  research/                            dated measurements and external research, cited by design notes
    shapes-benchmark.md                Qt Quick Shapes spike, CurveRenderer/GeometryRenderer numbers                  renamed ← research/02_shapes-spike.md (DOCD-13)
    render-texture-measurements.md     render thread, Clear layer and MSAA costs                                      new ← decisions/render-texture.md tables (DOCD-05)
```

Current page → action:

| Page | Action |
|---|---|
| `decisions/device-sharing.md` | merge into `design/gpu-device.md`, delete |
| `decisions/element-catalog-v1.md` | rewrite as `design/element-model.md`, delete; Space table → user page `coordinates.md` (review 24) |
| `decisions/events.md` | rewrite as `design/events-and-interaction.md`, delete |
| `decisions/paths.md` | rewrite as `design/paths.md`, delete |
| `decisions/qt-bridges.md` | merge into `design/qt-bridge.md`, delete |
| `decisions/qtnative-deployment.md` | split into `design/native-boundary.md` and `design/deployment.md`, delete |
| `decisions/reconciler-identity.md` | rewrite as `design/reconciler.md`, delete |
| `decisions/render-texture.md` | split into `design/stride-output.md`, `offscreen-input.md`, `render-thread.md`, `clear-and-msaa.md`, `gpu-device.md` (part), `research/render-texture-measurements.md`; delete |
| `decisions/timing.md` | rewrite as `design/timing.md` |
| `decisions/user-qml.md` | rewrite as `design/user-qml.md` |
| `plans/paths/00_overview.md` | facts merge into `design/paths.md`, delete |
| `plans/paths/01_spike.md` … `05_generators.md` | delete (done) |
| `plans/paths/06_geometry-renderer.md` … `09_more-add-nodes.md` | delete; content lives in public issues, linked from `design/paths.md` |
| `plans/paths/10_gradients-brainstorm.md` | delete; one public issue (DOCD-14) |
| `research/01_qt-bridge-csharp.md` | merge into `design/qt-bridge.md`, delete |
| `research/02_shapes-spike.md` | keep, rename to `research/shapes-benchmark.md`, strip plan references |
| `research/03_vl-node-versions.md` | one sentence into contributor docs, delete |
| `topics/element-immutability.md` | merge into `design/element-model.md`, delete |
| `topics/gradients.md` | public issue, delete |
| `topics/style-nodeset.md` | keep, move to `design/styles.md`, headings of the template |
| `topics/user-qml.md` | delete (handover of a finished brainstorm) |

Folders `docs/decisions/`, `docs/topics/` and `docs/plans/paths/` disappear. The index (`docs/README.md`, review 24 DOC-16) lists `design/` and `research/` in its "Design notes" and "Research" sections.

## 5 Questions for Sebastian

1. **Folder name: `docs/design/` or keep `docs/decisions/`?** `design/` says "how it is built and why, now"; `decisions/` reads as a dated log, which is what the rewrite removes. Keeping `decisions/` saves renaming about 20 code links (DOCD-15), but the links change anyway because most pages are split or merged. Recommendation: `docs/design/`.
2. **History: delete or archive?** Finished plans, handovers, the input contract and the task cuts can be deleted (git history keeps them) or moved to `docs/archive/`. An archive in a public repository is read as current by search engines and agents. Recommendation: delete, no archive.
3. **Open 2.0 work (paths 06–10, gradients): where are the issues?** The plan says the package texts are in issue bodies (#154, #155). If those issues are in the public repository, the pages can go at once; if not, they have to be created there first. Recommendation: create or confirm them in the public repository before DOCD-04 and DOCD-14.
4. **Measurements from one laptop in public research pages?** All numbers (render thread, MSAA, Clear, shapes) come from the Snapdragon laptop under background load; x64 is open everywhere. Keep them with the machine stated, or remove until x64 is measured? Recommendation: keep, with machine, Qt version and the scenario to rerun, and one sentence "x64 not measured".

## 6 For other areas

- C# reconciler (05): `Reconciler.cs:9-73` summary moves to `docs/design/reconciler.md` (DOCD-07); keep 3–5 lines.
- QML (11): `qmlitem.cpp:482` still creates the root without initial properties, so a `required property` without initializer fails; the decision page names it a known limitation, no issue is visible.
- QML / timing (11, 07): possible bypass of the animation driver by `Animator` types in threaded offscreen scenes (DOCD-08), unverified.
- Docs pages (24): `docs/user-qml.md` links five decision pages, `docs/shapes.md` two, `README.md` and `docs/diagnostics.md`, `docs/development.md`, `docs/architecture.md` link `render-texture.md`; all change with DOCD-15.
- Docs pages (24): `docs/versioning.md` states that unstable nodes get `[Smell(SymbolSmell.Experimental)]`; generated QML nodes cannot (`decisions/user-qml.md:46`): the rule should name the exception.
- Deployment/CI: open question "trimming windeployqt output" (`qtnative-deployment.md:47`: `Qt6OpenGL`, `Qt6Svg`, `qmltooling/`, `tls/`, `networkinformation/`) is not tracked anywhere visible.
- Repository: `docs/plans/refactor/` itself is internal planning; decide before the repository goes public whether it stays.
