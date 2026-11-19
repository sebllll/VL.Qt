# Review 24 – Documentation: user and contributor pages

Reviewer: Claude Opus 5.5, 2026-11-19. Files read in full: `README.md`, `AGENTS.md`, `CONTRIBUTING.md`, `CLAUDE.md`, `LICENSES.md`, `CLA.md`, `docs/README.md`, `docs/architecture.md`, `docs/development.md`, `docs/diagnostics.md`, `docs/effects-and-shaders.md`, `docs/export.md`, `docs/help-patches.md`, `docs/images.md`, `docs/linux.md`, `docs/profiling.md`, `docs/shapes.md`, `docs/styles.md`, `docs/tooltips.md`, `docs/user-qml.md`, `docs/versioning.md`, `docs/video-audio.md`. Skimmed (targeted searches only): `tools/*.ps1|sh|py`, `src/VL.Qt*/**` (pin names, defaults, categories, env vars), `src/VL.Qt.Native/vlqt.h|vlqt.cpp|perf.cpp|render_thread.cpp`, `help/Help.xml`, `.github/workflows/public-check.yml`, `docs/plans/refactor/DECISIONS.md` (first 40 lines). Not read: `docs/decisions/`, `docs/topics/`, `docs/research/`, `docs/plans/` (other areas), `LICENSES/*`, `tests/profiling/README.md`.

## 1 Summary

The pages are precise and mostly correct at the level of single pins and numbers: of about 60 concrete claims checked (pin names, defaults, env vars, keys, script parameters, file paths), the user pages on elements, paths, images, styles, QML and effects matched the code. The problems are at the level of the whole set. The contributor overview pages (`architecture.md`, `development.md`, `AGENTS.md`) have fallen behind the code: the QtTexture render thread, the companion package VL.Qt.Audio and most of the newer source folders are missing or described wrongly. The same facts live in up to four places (README, `development.md`, `AGENTS.md`, `CONTRIBUTING.md`), and the README repeats every user page in short, so each change has to be made several times and already was not. User, contributor, maintainer and design-history content is mixed on the same pages: `linux.md`, `profiling.md`, `video-audio.md`, `diagnostics.md` and `help-patches.md` each carry one of the other audiences. Two rules that the refactoring itself breaks (Obsolete phase before a rename, "0.9.x until 1.0") are stated unconditionally in `AGENTS.md` and `CONTRIBUTING.md`. What a contributor from the vvvv community misses most: a walkthrough that follows one element through C#, the protocol and QtNative, and a glossary of the Qt terms the pages use without explanation.

The changes that matter most: (1) fix the stale contributor facts (DOC-01 to DOC-03); (2) one home per fact and a split into user / contributor / maintainer pages, with a short README (DOC-05, DOC-06, DOC-12); (3) a Stride page for QtTexture/QtRenderer instead of linking users to a design note (DOC-07); (4) the element walkthrough and the glossary (DOC-08, DOC-09).

## 2 What works and must survive

- **Pin-level precision of the user pages.** `shapes.md`, `user-qml.md`, `effects-and-shaders.md`, `images.md`, `styles.md` name the pins exactly as vvvv shows them, with defaults and units, and say what happens at the edges (limits, errors, what is not sent). Checked: `Stroke Width` 0.05 (`src/VL.Qt/Nodes/PathNode.cs:59`), `EndPoint` of `CubicTo` (`src/VL.Qt/Nodes/Paths/CubicToNode.cs:31`), 8190 points (`src/VL.Qt.Tests/ShapeNodeCoreTests.cs:146`), QtTexture defaults (`src/VL.Qt.Stride/QtTextureNode.cs:51-56`), 20000 characters (`src/VL.Qt/Elements/QmlProperties.cs:16`), F2/F5/Shift+F5/F6/F7 (`src/VL.Qt.Native/perf.cpp:746-770`), `Keep Previous`/`Is Loaded`/`On Loaded` (`src/VL.Qt/Nodes/LoadImageAsyncNode.cs:58-61`), all env vars named in `diagnostics.md`, `linux.md`, `profiling.md` exist in the code. Keep this level when pages are rewritten.
- **Known limits and "what you see" sections** (`linux.md` Known problems, `diagnostics.md` GPU device loss, `user-qml.md` Limitations): they answer the questions a user actually has. They stay, with the contributor rows moved out (DOC-12).
- **The style of the text:** short declarative sentences, one fact per bullet, English, no marketing. It matches the reader in the brief.
- **`help/Help.xml` is complete:** every `.vl` under `help/` is listed exactly once (checked by diff). The rule in `AGENTS.md:66` works.
- **`CLAUDE.md` = `@AGENTS.md`:** one source for all agents; keep.
- **`LICENSES.md`:** component table, export checklist and the trademark/name decision are what a user exporting an app needs; keep as is apart from DOC-15.

## 3 Findings

### DOC-01 Fix architecture.md: render thread, offscreen scenes and the missing subsystems
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/architecture.md:48`, `docs/architecture.md:61-63`, `docs/architecture.md:42`, `docs/architecture.md:89-96`
- **Finding:** `architecture.md` says "No `exec()` and no render thread" (:48) and "Rendering runs on the main thread for now; a Qt render thread is planned as a separate step" (:63). The render thread exists (`src/VL.Qt.Native/render_thread.cpp:23`, default one thread for all QtTexture scenes) and `diagnostics.md:77-86` and `profiling.md:76-100` describe it. A contributor reading the architecture page gets the threading model of QtNative wrong, which is the one thing they must not get wrong. The page also lacks the subsystems added since it was written: the resource manager and its workers (`vlqt-res-*`, `resources.cpp`, images, shaders, snapshots), the effect chain (`effectchain.cpp`), styles (`vlstyle.cpp`), paths (`pathelement.cpp`), the audio tap (`audio_tap.cpp`). ":42" says "per window a `QQuickWindow` and a `QQmlEngine`"; offscreen scenes have an engine too (`src/VL.Qt.Native/vlqt.cpp:799-806`, `newEngine()` shared by windows and offscreen scenes). The performance numbers (:91-96) have no date, machine or commit and duplicate `PerfLimits.cs`.
- **Proposal:** rewrite `docs/architecture.md` as the contributor's map, in this order: (1) layers and the three packages; (2) one frame, step by step (keep the existing `per vvvv frame` block, it is good); (3) threads: vvvv main thread = Qt GUI thread, `basic` render loop for windows, render thread(s) for offscreen scenes, resource workers, the audio thread of VL.Audio; what may be touched on which thread; (4) the protocol (command buffer, event buffer, `protocol.h`/`Protocol.cs`, `--drift`); (5) one short section per subsystem (elements and items, resources, effects, shaders, styles, paths, QML, offscreen/Stride, audio tap) with the source folder/file and a link to its design note; (6) events and measurements; (7) where to read on (`docs/decisions/`). Remove the numbers list or move it to `profiling.md` with date and machine. Remove `Feedback layer` detail (:55) down to two sentences and a link to `decisions/render-texture.md`; it is a design note.
- **Decided:** G5, O1, O2, E4, E7, S2 → the closing section points to `docs/design/` (not `docs/decisions/`); the 'Feedback layer' paragraph becomes two sentences on the `PreviousFrameLayer` and a link (O1: 'feedback' in the docs means only the way back from Qt, A1); 'scene' means only what one renderer owns on the Qt side (`WindowScene`, `OffscreenScene`), the tree of the patch is the 'element tree' (O2); the threads section says that QtNative runs Qt's `basic` render loop on purpose (E4) and that the pool of render threads is set by the pin `Render Threads` of QtTexture and QtRenderer, default 1, 0 = main-thread mode (E7, replaces `VLQT_OFFSCREEN_THREAD`); the subsystem sections name the folders of TARGET 2.2 and 2.3 (`resources/`, `effects/`, `style/`, `device/`, `offscreen/` …), so the page is written after wave 4
- **Effort:** M, mechanical: no
- **Touches:** `docs/architecture.md`
- **Depends on:** DOC-09 (glossary terms it links), the target structures of reviews 05–21 (file names after the refactoring)
- **Verification:** every file, function and env var the page names exists (`grep`); a reader can answer "which thread renders a QtTexture" from the page.

### DOC-02 Fix development.md: three packages, build.ps1, project layout
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/development.md:101`, `:157`, `:159`, `:216-243`, `:257-263`
- **Finding:** (a) `build.ps1` is described as building `src/VL.Qt` and `src/VL.Qt.Stride` with "unit tests" (:101); it also builds VL.Qt.Audio and runs `VL.Qt.Audio.Tests` (`tools/build.ps1:1`, `:4`, `:68-69`). (b) "**Both packages have the same version**" (:157) and "uploads both `.nupkg` files … pushes both" (:159), while three packages exist and `:171` and `:191` say so. (c) The project layout (:216-243) lists `src/VL.Qt/` with `Elements/`, `Nodes/`, `Reconciler/` and three files; the folder has `Images/`, `Paths/`, `Qml/`, `Shaders/`, `Styles/`, `Nodes/Paths/` and about twenty root files (`ls src/VL.Qt`). For QtNative it lists 8 of about 40 source files (no `offscreen`, `render_thread`, `effectchain`, `pathelement`, `qmlitem`, `shadereffectitem`, `audio_tap`, `vlstyle`, `feedback`, `image_writer`, `touch` …). `src/VL.Qt.Audio`, `src/VL.Qt.Audio.Tests`, `VL.Qt.Audio/` and `tests/profiling/` are missing. (d) "Add a new element type" (:257-263) is six lines, says "Implement the QML item in `elements.cpp`" although several items have their own file (`pathelement.cpp`, `qtimageitem.cpp`, `shadereffectitem.cpp`, `qmlitem.cpp`), and omits the category, the help patch, `Help.xml`, the pin catalog and the SmokeTest scenario.
- **Proposal:** (a)(b) correct the sentences; say "all three packages" and list them once in the Packages table, then refer to "the packages". (c) Replace the hand-written tree by one line per folder (not per file), generated from the target structures of the refactoring reviews; per folder one sentence of responsibility. Files are found by the folder; a per-file list goes stale. (d) Replace by a link to the walkthrough of DOC-08.
- **Decided:** S2, S3, TARGET 2.2 to 2.5, F5 → the folder list is one line per folder of TARGET 2.2 (`src/VL.Qt`: `Runtime/`, `Scene/`, `Window/`, `Interop/`, `Wire/`, `Reconciliation/`, `Elements/`, `Styles/`, `Images/`, `Paths/`, `Qml/`, `Shaders/`, `NodeFactories/`, `Nodes/` with one subfolder per category), 2.3 (QtNative: `app/`, `platform/`, `window/`, `scene/`, `elements/`, `effects/`, `user_qml/`, `style/`, `resources/`, `device/`, `offscreen/`, `diagnostics/`, `test/`, `testkit/`, `qml/`), 2.4 (`VL.Qt.Stride`, `VL.Qt.Audio`, flat) and 2.5 (`src/VL.Qt.TestSupport/`, `docs/design/`); the SmokeTest is an NUnit project (F5)
- **Effort:** S, mechanical: no
- **Touches:** `docs/development.md`
- **Depends on:** DOC-08 for (d); final folder layout of the refactoring for (c)
- **Verification:** each folder in the tree exists (`ls`); the build.ps1 row matches the header comment of `tools/build.ps1`.

### DOC-03 Fix AGENTS.md and CONTRIBUTING.md: stale rules and missing packages
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `AGENTS.md:7-13`, `AGENTS.md:52`, `AGENTS.md:96`, `AGENTS.md:100`, `AGENTS.md:131`, `CONTRIBUTING.md:19`, `CONTRIBUTING.md:23`
- **Finding:** (a) "What this repo is" (:7-13) names three layers and two test projects; the companion packages VL.Qt.Stride and VL.Qt.Audio (`src/VL.Qt.Stride`, `src/VL.Qt.Audio`, package folders `VL.Qt.Stride/`, `VL.Qt.Audio/`) and `VL.Qt.Audio.Tests` do not appear. An agent asked to fix QtTexture does not learn from AGENTS.md that it exists. (b) `vl-patch-check.py` without argument checks "all `help/Qt/*.vl`, `help/*.vl` and `VL.Qt.Stride/help/*.vl`" (:52); it also checks `VL.Qt.Audio/help/*.vl` (`tools/vl-patch-check.py:38`). (c) "A node or pin is renamed or removed only after `[Smell(SymbolSmell.Obsolete)]`" (:96) and `CONTRIBUTING.md:19` state the Obsolete phase unconditionally; `docs/versioning.md:3` limits the guarantees to 1.0.0 and later, and the refactoring decided "no Obsolete phase" before 1.0 (`docs/plans/refactor/DECISIONS.md`, "No extra effort for breaking changes"). (d) "Until 1.0 is released the package stays at 0.9.x and every issue that has to be done goes into `1.0`" (:131) and `CONTRIBUTING.md:23` ("0.9.x with fixes only") contradict the release 0.10.0 and the milestone `refactor` (DECISIONS, "Milestone and release"). (e) "Never call `GC.*`" (:100) is broader than meant: `src/VL.Qt/QtApp.cs:367-369` reads `GC.CollectionCount` and `GC.GetTotalPauseDuration` for the stats overlay, which is legitimate. "No `Console.*`" is broken by `src/VL.Qt/ExportedApp.cs:110-111` (`Console.Out.Flush()` at exit of an exported console app, also legitimate).
- **Proposal:** (a) add one bullet: "Companion packages: `src/VL.Qt.Stride` (QtTexture, QtRenderer; Windows) and `src/VL.Qt.Audio` (`Video (VL.Audio)`; Windows), package folders `VL.Qt.Stride/`, `VL.Qt.Audio/`; tests also in `src/VL.Qt.Audio.Tests`". (b) correct the list, or better say "no argument: all help patches of the three packages". (c) one rule in `versioning.md`: "Before 1.0 nodes and pins may be renamed or removed without an Obsolete phase; the help patches change with them. From 1.0 on: Obsolete first." AGENTS.md and CONTRIBUTING.md link to it instead of restating. (d) replace with "Milestones: see CONTRIBUTING.md"; in CONTRIBUTING.md state the milestones as they are after 0.10.0 (Question Q2). (e) "Never trigger a collection (`GC.Collect`, `GC.WaitForPendingFinalizers`)"; "No `Console.Write*` or `Debug.Write*` for messages".
- **Decided:** G2, 'Semantic versioning', P13 → (c) the rule in `versioning.md`: VL.Qt follows semantic versioning, before 1.0 a minor release may break, so there is no `Obsolete` phase and the help patches change with the break; (d) AGENTS.md and CONTRIBUTING.md keep only 'milestones are version numbers; `1.0` blocks the release' and drop '0.9.x until 1.0'; the work of this release is milestone `0.10.0`, not `refactor`; Question Q2 is answered with its alternative (b)
- **Effort:** S, mechanical: no
- **Touches:** `AGENTS.md`, `CONTRIBUTING.md`, `docs/versioning.md`
- **Depends on:** Q2
- **Verification:** each rule checked against the code with one search, as above.

### DOC-04 Fix stale or missing user-facing facts
- **Kind:** docs
- **Severity:** must
- **Breaking:** no
- **Where:** `docs/help-patches.md:42-150`, `README.md:311-329`, `README.md:46`, `docs/linux.md:8`, `docs/linux.md:141`, `docs/README.md:32`, `README.md:321`
- **Finding:** (a) `help-patches.md`, the page that lists "the full list" (README.md:72), misses four patches that exist: `HowTo Antialias Edges`, `HowTo Avoid Tearing` (README.md:36 even refers to it), `HowTo Create QML Nodes from Files` (README.md:240 lists it) and, in the Stride section, `HowTo Draw Qt in a Stride Scene` (README.md:227 lists it; `VL.Qt.Stride/help/`). (b) The README's documentation list (:311-329) misses `docs/shapes.md`, the page for Line, Polyline, Polygon, Path and the generators; the README's own Paths section (:149) links to `help-patches.md` instead. (c) `linux.md:8` and `:141` say "What was tested for 1.0" / "For 1.0 only WSL2 … was tested"; there is no 1.0, the current release is 0.9.10. (d) The install screenshot (README.md:46) shows version 0.9.3. (e) Users are sent to a design note as the Stride user page (`README.md:321`, `docs/README.md:32` "Render texture: `QtTexture` for Stride, design notes"), see DOC-07.
- **Proposal:** (a) add the four patches to `help-patches.md` in their topic (Antialias Edges and Avoid Tearing under Window and Diagnostics or a new Rendering topic; QML Nodes under QML; Draw Qt in a Stride Scene under Stride) and add a check to `tools/vl-patch-check.py` or a unit test that every `<VLDocument link>` of the three `Help.xml` appears in `help-patches.md` (the list is otherwise maintained by hand and drifts). (b) after DOC-12 the README links every user page from its feature table; until then add `shapes.md`. (c) "tested with release 0.9.x" or, better, "tested on WSL2 … (x64 and arm64)" without a version. (d) take a new screenshot after 0.10.0 or crop the version out.
- **Effort:** S, mechanical: no
- **Touches:** `docs/help-patches.md`, `README.md`, `docs/linux.md`, `docs/images/install-browse-packs.png`, optionally `tools/vl-patch-check.py` or `src/VL.Qt.Tests`
- **Depends on:** none
- **Verification:** diff of the `Help.xml` links against `help-patches.md`; the new check fails when a patch is missing.

### DOC-05 One home per fact: remove the duplicates between README, development.md, AGENTS.md and CONTRIBUTING.md
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** see the table
- **Finding:** the same facts are written two to four times, and the copies already diverge (DOC-02, DOC-03 (b)):

| Fact | Copies | Home after the change |
|---|---|---|
| Setup and first build (`setup-qt`, `build.ps1`, `start-vvvv`) | `README.md:302-309`, `AGENTS.md:15-27`, `docs/development.md:10-18` | `development.md`; AGENTS.md keeps the three commands as a reminder with a link, README one sentence and a link |
| vvvv lookup (`-VvvvDir`, `%VVVV_DIR%`, newest `C:\vvvv\…`) | `AGENTS.md:24`, `development.md:18` | `development.md` |
| Test commands | `AGENTS.md:29-44`, `development.md:46-63` | `development.md`; AGENTS.md keeps the definition of done (:44) |
| Encoding, BOM, git hook | `AGENTS.md:69-91`, `development.md:197-214` (nearly verbatim) | `development.md`; AGENTS.md keeps the four rules as bullets |
| Branches and pull requests | `AGENTS.md:111-119`, `CONTRIBUTING.md:9-19` | `CONTRIBUTING.md`; AGENTS.md keeps "draft PR, `gh pr ready` only after `build.ps1 -Test`, never bare `git push`" |
| Issues, labels, milestones | `AGENTS.md:121-131`, `CONTRIBUTING.md:21-23` | `CONTRIBUTING.md` (labels for maintainers in a section of their own) |
| Help-patch text scheme | `AGENTS.md:60-66`, `docs/help-patches.md:12-40` | a contributor page `docs/contributing/help-patches.md` (DOC-12); AGENTS.md one line and link |
| `vl-patch-check.py` behaviour | `AGENTS.md:52`, `development.md:138` | `development.md` (Scripts) |
| Feature descriptions | README sections Styles, Paths, Images, Effects, Shaders, Stride, QML, Tooltips, Diagnostics, How It Works (`README.md:115-284`) and the first paragraphs of `styles.md`, `images.md`, `effects-and-shaders.md`, `tooltips.md`, `diagnostics.md`, `architecture.md` (nearly verbatim) | the docs pages; README keeps the feature table (DOC-06) |
| Linux quick path | `README.md:91-113`, `docs/linux.md:3-101` | `linux.md` |
| Export | `README.md:76-80`, `docs/export.md:3-9` | `export.md` |
| License summary | `README.md:335-345`, `LICENSES.md` | README keeps the "In short" paragraph and the trademark line (required by `LICENSES.md:53`); details only in `LICENSES.md` |
- **Proposal:** apply the "Home" column. Rule for the rewrite, to be added to `docs/README.md`: a fact is written on its home page; other pages link to it with one sentence at most.
- **Decided:** G2, F5, D4 → the home of 'Issues, labels, milestones' (CONTRIBUTING.md) states only 'milestones are version numbers; `1.0` blocks the release'; the home of 'Test commands' (`development.md`) describes the SmokeTest as NUnit tests (F5) and the drift check as a unit test of `VL.Qt.Tests` (D4); C1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the custom runner's options go: `--drift` and the check of `vlqt.h` against the `DllImport`s (CAPI-06 (c)) are unit tests, `--only`/`--list` are NUnit filters, `--replay`, `--tearing` and `--update-refs` are explicit NUnit tests or test parameters; the docs follow in wave 6
- **Effort:** M, mechanical: no
- **Touches:** `README.md`, `AGENTS.md`, `CONTRIBUTING.md`, `docs/development.md`, `docs/help-patches.md`, `docs/README.md`
- **Depends on:** DOC-06, DOC-12
- **Verification:** search for the commands (`setup-qt.ps1`, `fix-encoding.py`, `gh pr create`) across the pages: each appears in full on one page only.

### DOC-06 Shorten the README to an entry page
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `README.md` (31 KB, 20 sections)
- **Finding:** the README is the first page a vvvv user sees on GitHub and nuget.org. It repeats every user page in short (DOC-05) and then lists the same pages again (`README.md:311-329`), so a reader meets each topic twice and the README has to change with every feature. The `Contents` line (:16) has 20 entries. The pictures are its strength.
- **Proposal:** target length about 8 KB: title and claim (:10), the note on version and vvvv 8 (:12-14), Install (:42-52), First patch (:54-72, with the `Space` paragraph moved to a "Coordinates" section of a user page and one sentence left), the feature table (:20-38) with one picture row and each row linking its user page (the table already does this; add `shapes.md` for Paths, the new `stride.md` for Stride, `export.md`), Platforms table (:84-89) with a link to `linux.md`, "Compared to Qt Bridge" (:286-298, it answers a question users ask), Contributing (two lines), License ("In short" + trademark). Everything else moves to the docs pages, pictures included where the page does not have them yet (`paths-*.png` to `shapes.md`, `qrenderer-vs-qt-window.png` to `stride.md`, `qml-node-factory.png` to `user-qml.md`).
- **Effort:** M, mechanical: no
- **Touches:** `README.md`, `docs/shapes.md`, `docs/stride.md` (new), `docs/user-qml.md`, `docs/README.md`
- **Depends on:** DOC-07
- **Verification:** every link in the README resolves (a link check, e.g. `lychee --offline README.md docs/*.md`); nothing that was in the README is lost: each removed paragraph has its home in the table of DOC-05.

### DOC-07 A user page for QtTexture and QtRenderer (docs/stride.md)
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `README.md:211-229`, `docs/README.md:32`, `README.md:321`, `docs/diagnostics.md:77-86`, `docs/architecture.md:57-67`
- **Finding:** the only user documentation of the companion package VL.Qt.Stride is a README section; for more, users are sent to `decisions/render-texture.md`, a design note with spikes and rejected alternatives. The render-thread switch for QtTexture sits in `diagnostics.md`. VL.Qt.Audio has its page (`video-audio.md`), VL.Qt.Stride does not.
- **Proposal:** new `docs/stride.md`, audience vvvv user with Stride: QtTexture (pins with defaults, GPU-only, alpha and premultiplied, input with `Input Notifications` and `ToTexturePixels`, adapter), QtRenderer (when to use which, the table from README.md:221), formats and colour (UNorm vs SRgb vs Float, the hint at `Error`), performance (render thread, `VLQT_OFFSCREEN_THREAD`, moved from `diagnostics.md:77-86`), the five help patches, limits. Links to `decisions/render-texture.md` for "why". Pin names and defaults from `src/VL.Qt.Stride/QtTextureNode.cs:41-56` and `QtRendererNode.cs`.
- **Decided:** 'No `Error` output pins', E6, E7, catalog, G4, G5 → the formats section describes the UNorm hint of QtRenderer as a warning at the node plus a log line, and QtTexture and QtRenderer have no `Error` output; the performance section describes the hidden pin `Render Threads` of QtTexture and QtRenderer (default 1; the maximum over all nodes wins; a smaller value and 0 apply to scenes created afterwards, 0 = main-thread mode; P7) and no `VLQT_OFFSCREEN_THREAD`; categories as in the catalog (`QtTexture` in `Stride.Textures.Source`, `ToTexturePixels` in `Stride.Input`, `QtRenderer` stays in `Stride.Rendering`); one page per package (G4); the 'why' links go to `docs/design/` (G5)
- **Effort:** S, mechanical: no
- **Touches:** `docs/stride.md` (new), `README.md`, `docs/README.md`, `docs/diagnostics.md`, `docs/help-patches.md` (link target of the Stride section)
- **Depends on:** the API review of the Stride nodes (22, 23) for final pin names
- **Verification:** pin names and defaults on the page match the `[DefaultValue]`s of the two nodes.

### DOC-08 Walkthrough: how to add an element, followed through all layers
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/development.md:245-263` (the only guidance today)
- **Finding:** the reader in the brief "opens the repository to … add an element". The six-line recipe (DOC-02 (d)) names files but not what goes into them, not the order, not how the IDs are chosen, how the element reaches the Qt item, how a measured value or an event comes back, and how to test it. A contributor has to reverse-engineer it from an existing element across `Protocol.cs`, `protocol.h`, `Elements/`, `Reconciler/PropertyDiff.cs`, `Nodes/`, `Categories.cs`, `commands.cpp`, `elements.cpp` or an item file of its own, `events.cpp`, `vl_pin_catalog.py`, the help patch, `Help.xml`, a unit test and a SmokeTest scenario.
- **Proposal:** new page `docs/contributing/add-an-element.md` that follows one small, real element end to end. Recommended example: `Circle` (few pins, no resources) or `Line` (shows `F32Array`, a limit and a warning). Sections: 1 the protocol entry (type ID, property IDs in `protocol.h`, mirror in `Protocol.cs`, `--drift`); 2 the C# element (immutable record, defaults, `Element` base); 3 diffing (`PropertyDiff`), what the reconciler does with it and why identity matters (link `decisions/reconciler-identity.md`); 4 the node (`[ProcessNode]`, category from `Categories`, pin docs, `NodeContext`); 5 QtNative: where the command is decoded, the item class and its file, how a property is applied, error reporting back to the node; 6 measurements and events back (sinks, `events.cpp`, `EventReader`); 7 tests: unit test of the diff, SmokeTest scenario with a grab, help patch + `Help.xml` + `vl-patch-check.py` + `vvvvc.ps1`; 8 docs: which user page. Each step shows the real lines of the example (short excerpts with `path:line`), written after the refactoring so that the paths are final. Second, shorter page later: "add a property to an existing element".
- **Decided:** A1, A3, A6, C3, O3, O7, S3, TARGET 2.2 and 2.3, G5 → the page uses the final names: the element is an internal record under `Elements/` behind `IElement` (A6, C3); the diff is `PropertyDiff` in `Reconciliation/` and the element model knows no protocol (A3); the node is a class in `Nodes/Elements/` built on `ElementNodeCore` (O3, S3); 'sinks' are feedback: `ElementFeedback`, `FeedbackSlot`, `FeedbackRouter`, read in C# through `EventReader.FeedbackId` (A1, O7); the native parts are in the folders `scene/`, `elements/` and `effects/` of TARGET 2.3; the link to the identity note points to `docs/design/` (G5)
- **Effort:** M, mechanical: no
- **Touches:** `docs/contributing/add-an-element.md` (new), `docs/development.md`, `docs/README.md`, `AGENTS.md` (one link)
- **Depends on:** the refactoring of the element, reconciler, protocol and native element areas (05, 06, 08, 17, 18) being merged, so the walkthrough shows final names
- **Verification:** a person or agent who has not worked on the code adds a trivial test element by following only the page; every `path:line` in it resolves.

### DOC-09 Glossary of the Qt and VL.Qt terms
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** for example `docs/architecture.md:3`, `:42`, `:48`, `:55`; `docs/diagnostics.md:22`, `:32`; `docs/effects-and-shaders.md:24`, `:52`; `docs/help-patches.md:104`; `docs/profiling.md:50`, `:88`; `docs/shapes.md:36`; `docs/user-qml.md:87`
- **Finding:** the pages use Qt terms without explanation, also on user pages: Qt Quick, item, scene graph, `QQuickWindow`, `QQmlEngine`, QML, render loop (`basic`), polish / sync / render / present, pump, QRhi and `beginFrame` (`diagnostics.md:22`), `qsb` and shader baking, MultiEffect (`help-patches.md:104` "one layer per run of MultiEffect nodes", never defined; `effects-and-shaders.md:24` says "Qt applies them in its own fixed order"), `CurveRenderer`, `QQuickShaderEffectSource` (`architecture.md:55`), platform plugin (xcb, wayland, eglfs), windeployqt, `QVariant`, swap chain, flip model, TDR, DIP and device pixel ratio. Sebastian and the community reader know vvvv, not Qt (brief). VL.Qt's own terms (element, element node, renderer, scene, sink, unit, space, layer, effect chain, run) are not defined in one place either.
- **Proposal:** `docs/glossary.md`, two tables: "Qt terms" (term, one-sentence meaning in vvvv words, where VL.Qt uses it, link to the Qt docs) and "VL.Qt terms" (term, meaning, the name used in code). Every page links a term the first time it uses it. Start with the 25 terms above.
- **Decided:** P3, 'One target page before any rename' → the content is sections 1.2 and 1.3 of TARGET.md as they are (its columns 'Names after the refactoring' and 'Today' stay in TARGET.md); DOC-09 and DOC-10 move from wave 6 to the pass over TARGET.md, so DOC-09 no longer waits for DOC-10; the terms 'sink' and 'scene' follow it (feedback, A1/O1; scene, O2)
- **Effort:** S, mechanical: no
- **Touches:** `docs/glossary.md` (new), first uses in the pages of section 4
- **Depends on:** DOC-10 (the names chosen there)
- **Verification:** search the docs for the listed terms: the first occurrence on each page links the glossary.

### DOC-10 One name per concept across all pages
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** as listed
- **Finding:** variants collected while reading:

| Concept | Variants (examples) | Proposed name |
|---|---|---|
| The C++ layer | QtNative, "the native C++ layer" (`architecture.md:3`), "the C++ layer" (`README.md:273`), "native layer" (`profiling.md:39`), `vlqt.dll` | **QtNative** (first use: "QtNative, the C++ layer (`vlqt.dll`, `libvlqt.so`)") |
| The test program | SmokeTest, "smoke test" (`architecture.md:41`), "smoke tests" (`architecture.md:91`, `development.md:58`) | **SmokeTest** |
| Length units | units, DIP, "px" (`styles.md:28` "Sizes in a style are px"; `StyleNodes.cs:235` "Font size in px"), "QML units (device-independent pixels)" (`user-qml.md:22`), "pixels per DIP" | **unit** (scene space) and **DIP** (Qt's logical pixel); "px" only for device pixels. Note: `README.md:70` defines 1 unit = 100 DIP for `DIPTopLeft`, but `help-patches.md:35` and `user-qml.md:13` state it unconditionally |
| Merged MultiEffect nodes | "run", "merged layer", "offscreen layer", "MultiEffect nodes" (`help-patches.md:104`), category `Qt.Effects.MultiEffect` (`Categories.cs:27`) | **layer effects** (Blur, DropShadow, ColorAdjust, Colorize, Mask) vs **shader effects**; "run" defined once |
| Effect chain nodes from files | "filter shader", "shader node", "effect chain node", "chain node", "chain-only" | **shader node** (element) and **effect node** (in `Qt.Effects`) |
| QtTexture's output | "Stride texture", "Render texture", "texture output", "offscreen scene" | **QtTexture** for the node, **offscreen scene** for the QtNative concept |
| GPU reset | "device loss", "GPU reset", "TDR", "device removed", "driver reset" | **device loss** (TDR explained in the glossary) |
| vvvv aspects | "aspect `Obsolete`" (`versioning.md:25`), `[Smell]`, "SymbolSmell" | **aspect** for users, `[Smell(SymbolSmell.X)]` for C#, with the mapping once in `versioning.md` (it is there, :33) |
| The per-frame Qt work | "pump", "Qt pump", "native pump", `vlqt_app_process_events` | **pump**, defined in the glossary |
| Help patch | "help patch", "HowTo", "`help/Qt/` patch" | **help patch** |
- **Proposal:** adopt the right column in all pages of this area and in the glossary (DOC-09). The node-help texts follow in the XML-doc review.
- **Decided:** O4, O5, O2, E1 (TARGET 1.2) → 'layer effects' becomes 'merged effects' (Blur, DropShadow, ColorAdjust, Colorize, Mask) and the others stay 'shader effects' (O4); 'layer' has Qt's meaning only and a Skia layer is an element here; the nodes generated from a `.frag` file are 'shader nodes' and, for a filter, 'shader effect nodes' (O5), the element node is `Shader`; 'scene space' is 'the space of the renderer' (`Space` pin, O2), lengths are 'unit' and 'DIP', 'px' only for device pixels; the other rows (QtNative, SmokeTest, help patch, device loss, pump, aspect) stand; C15 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the word "run" goes; write "neighbouring merged effects"
- **Effort:** M, mechanical: no
- **Touches:** all pages of section 4
- **Depends on:** none
- **Verification:** search for the rejected variants returns nothing in `README.md`, `AGENTS.md`, `CONTRIBUTING.md`, `docs/*.md`.

### DOC-11 Remove issue numbers from the pages
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/README.md:37`, `:40`, `:41`; `docs/user-qml.md:95`, `:195`; `docs/video-audio.md:3`, `:27`; `docs/profiling.md:52`, `:54`, `:65`, `:67`, `:69`, `:76`, `:110`; `docs/linux.md:161`, `:162`
- **Finding:** K1 covers `src`; the pages carry the same problem. Some numbers point to the private predecessor repository, some to this one, and the reader cannot tell which (e.g. `profiling.md:52` "Measurement path (#20)", "Since #71", "(#215)"). Some sentences only make sense with the issue: `video-audio.md:27` "(spike in the issue)", `user-qml.md:195` "is not part of #107". `linux.md:162` quotes SmokeTest check names that start with issue numbers ("`233: created borderless at Bounds`", "`143/234 live: …`"), so the doc inherits the numbers from the test output.
- **Proposal:** delete every `#NNN` and say instead what the sentence needs: the date or version since when something holds ("since 0.9.5"), or the fact itself. `profiling.md:52-74`: replace the "Before/After #20/#71" history by the current behaviour ("a static scene measures nothing and polishes nothing") and move the tables to `docs/research/` if they are worth keeping (DOC-13). `video-audio.md:3`, `:27`: link the design note instead (`docs/decisions/` page for the audio tap, if review 22 creates one) and state the measured start offset in the sentence. `user-qml.md:195`: "Fixing it needs initial properties at creation in QtNative." `linux.md:162`: depends on the renamed SmokeTest checks (for other areas); describe the four checks by what they test.
- **Decided:** G5, G8 → the link to the design note of the audio tap points to `docs/design/` (not `docs/decisions/`); the profiling tables that move to `docs/research/` keep machine, Qt version and the scenario to rerun and say 'x64 not measured'
- **Effort:** S, mechanical: no
- **Touches:** `docs/README.md`, `docs/user-qml.md`, `docs/video-audio.md`, `docs/profiling.md`, `docs/linux.md`
- **Depends on:** K1 issue for the SmokeTest check names
- **Verification:** `grep -nE "#[0-9]{2,4}\b" README.md AGENTS.md CONTRIBUTING.md docs/*.md` returns nothing.

### DOC-12 Separate the audiences: user, contributor, maintainer
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/help-patches.md:5-40`; `docs/linux.md:107-137`, `:155`, `:160-163`; `docs/video-audio.md:5`, `:25-31`, `:62-66`; `docs/diagnostics.md:30-44`; `docs/export.md:3-6`, `README.md:76-78`; `docs/development.md:163-195`; `CONTRIBUTING.md:31-33`; `docs/README.md:3`, `:23`
- **Finding:** the pages for vvvv users carry contributor and maintainer content, and the user does not have what the contributor content replaces:
  - `help-patches.md` opens with the rules for writing help patches (Help Browser manifest, text scheme) before the list a user looks for.
  - `export.md:3-6` and `README.md:76-78` show `tools/vvvvc.ps1` as the way to export. A user who installed the NuGet package has no `tools/` folder; their way is the application exporter in vvvv (described only in `linux.md:28`).
  - `linux.md` mixes the user path (1–4, Fonts) with building the Linux runtime, testing window pins with the SmokeTest, timing calibration, and Known-problems rows about `tools/test-linux.sh` and the SmokeTest (:155, :160-163).
  - `video-audio.md:5` tells users to start the help patch with `tools/start-vvvv.ps1`; "How it works" and "Tests" are contributor content.
  - `diagnostics.md:30-44` is a measurement report (replay runs, tables) in the user's diagnostics page.
  - `development.md:163-195` (CI, release, rulesets, Trusted Publishing) and `CONTRIBUTING.md:31-33` (contributors workflow) are for the maintainer only.
  - `docs/README.md` calls everything "User and contributor documentation" in one table; the user does not see which pages are theirs.
- **Proposal:** three groups, shown as three sections of `docs/README.md` (section 4): **Using VL.Qt** (user pages; may mention a SmokeTest or script only in a "for contributors" line at the end), **Contributing** (`docs/contributing/`), **Maintaining** (`docs/maintaining/`: release, CI, repository settings, contributors list). Moves: help-patch writing rules → `docs/contributing/help-patches.md`; Linux runtime build, window-pin tests, timing checks and the SmokeTest rows → `docs/contributing/linux.md`; `export.md` leads with the exporter dialog, the script follows as "from the repository"; `video-audio.md` "How it works" stays (users need the sync model to calibrate), "Tests" moves to the contributor testing page; `diagnostics.md:30-44` → `docs/research/` (or `profiling.md`), the user page keeps "the warning lists the phases; with VSync off, present does not block; spikes under CPU load come from preemption".
- **Decided:** G8 → the measurement report of `diagnostics.md:30-44` that moves to `docs/research/` keeps machine, Qt version and the scenario to rerun and says 'x64 not measured'
- **Effort:** M, mechanical: no
- **Touches:** `docs/README.md`, `docs/help-patches.md`, `docs/linux.md`, `docs/video-audio.md`, `docs/diagnostics.md`, `docs/export.md`, `docs/development.md`, `CONTRIBUTING.md`, `README.md`, new pages in section 4; links from code to `docs/linux.md`, `docs/development.md` (3 each, `grep -rn "docs/linux.md\|docs/development.md" src tools .github`)
- **Depends on:** DOC-05
- **Verification:** each page in `docs/README.md` is in exactly one group; a user page contains no `tools/` command except in a closing "from the repository" note.

### DOC-13 Move measurement history out of the how-to pages
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `docs/profiling.md:52-100`, `docs/diagnostics.md:36-44`, `docs/architecture.md:89-96`, `docs/shapes.md:117-121`
- **Finding:** `profiling.md` is "how to profile" (:1-50, good) followed by three change reports with before/after tables, dated runs on one laptop and the internals of a SmokeTest scenario (:52-100). These numbers are history: they explain why the code is as it is (design-note content) and go stale with every change. The thread-name table (:102-112) is useful reference but belongs to diagnostics or architecture. `shapes.md:117-121` keeps measured costs a user needs (limits), which is right.
- **Proposal:** `profiling.md` keeps scenarios, run, evaluate, read with care, and a short "SmokeTest perf scenarios" list (`measure`, `qttextureperf`, `--replay`, `--tearing` from `development.md:67-89`) with their commands. The before/after tables move to `docs/research/` (one file per measurement, dated, with machine and commit). Thread names move to `architecture.md` (threads section, DOC-01). `development.md:67-89` (`--tearing`) moves here too: it is a measurement tool, not a build step.
- **Decided:** G8 → every file in `docs/research/` names machine, Qt version, date and the scenario to rerun, and says 'x64 not measured' where only the arm64 notebook was measured; C1 of [DECISION-ROUND.md](../DECISION-ROUND.md) → the custom runner's options go: `--drift` and the check of `vlqt.h` against the `DllImport`s (CAPI-06 (c)) are unit tests, `--only`/`--list` are NUnit filters, `--replay`, `--tearing` and `--update-refs` are explicit NUnit tests or test parameters; the docs follow in wave 6
- **Effort:** S, mechanical: no
- **Touches:** `docs/profiling.md`, `docs/diagnostics.md`, `docs/architecture.md`, `docs/development.md`, new files in `docs/research/`
- **Depends on:** DOC-01
- **Verification:** `profiling.md` has no table with "Before" / "After" columns.

### DOC-14 Restructure AGENTS.md for a newcomer: rules first, short, linked
- **Kind:** docs
- **Severity:** should
- **Breaking:** no
- **Where:** `AGENTS.md` (15 KB)
- **Finding:** AGENTS.md is read in full by every agent session and by contributors (`CONTRIBUTING.md:7`). Half of it repeats `development.md` and `CONTRIBUTING.md` (DOC-05). The order puts setup and tests before the code rules, and the help-patch rules (:46-68) are longer than the C# rules. Debugging (:93-109) is reference material, not a rule. The rules a newcomer breaks first are spread over the page: definition of done (:44), protocol mirror (:42), encoding (:76-79), no bare `git push` (:114), draft PRs (:117).
- **Proposal:** AGENTS.md at about 6 KB, in this order: (1) what this repo is (layers + companion packages, 6 lines, DOC-03 (a)); (2) **Before you finish** checklist: build, tests, help patch check, `--drift` after protocol changes, encoding, docs page updated; (3) code rules C# (the current list, corrected per DOC-03 (e)), QtNative (one short list: Qt conventions, ownership across the C API, no exceptions across the boundary; to be written with the native reviews 16–21, which today have no rules here at all); (4) help-patch rules (5 bullets + link); (5) git and PR rules (5 bullets + link to CONTRIBUTING.md); (6) where to read on (development, architecture, glossary, walkthrough, diagnostics for debugging). Debugging moves to `docs/contributing/debugging.md` (with `diagnostics.md` for the in-app tools).
- **Decided:** D2, D3, G2, D4, F6, P14, S2, D9, E4, E12, 'Pins instead of environment variables' → the C# rules lose the `NodeContext` `Hidden` rule (D2) and say 'set a default explicitly unless `default(T)` is the intended value; never for `Matrix`' (D3, A2 of [DECISION-ROUND.md](../DECISION-ROUND.md)); the milestone line is only 'milestones are version numbers; `1.0` blocks the release' (G2); the checklist names the node-API snapshot (`src/VL.Qt.Tests/Api/node-api.txt`, regenerated by the author of each issue, F6) and the drift check as a unit test (D4); from the end of wave 4 it also names the check script (no `#NNN` in `src`, no comment line over 160 characters, no file with two types besides the named exceptions, P14); the QtNative list takes: one folder per area and snake_case file names (S2), Qt private classes allowed and listed in the head comment of `elements.h` (D9), the `basic` render loop on purpose (E4), no JavaScript in the QML of built-in elements (E12), environment variables only for what must be set before vvvv starts and for test switches
- **Effort:** S, mechanical: no
- **Touches:** `AGENTS.md`, `docs/contributing/debugging.md` (new)
- **Depends on:** DOC-03, DOC-05
- **Verification:** AGENTS.md under 7 KB; every removed paragraph has its home (DOC-05 table).

### DOC-15 Small corrections: LICENSES.md wrapping, help-patches example, diagnostics repetition
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `LICENSES.md:36-38`, `docs/help-patches.md:35`, `docs/diagnostics.md:62`, `:66`, `docs/development.md:254`
- **Finding:** `LICENSES.md:36-38` is the only hard-wrapped paragraph in all pages (lines break mid-sentence). `help-patches.md:35` quotes "1 unit = 100 DIP" as a note of a help patch without the `Space` condition (DOC-10). `diagnostics.md:62` and `:66` say the same thing twice (orange node, Renderer `Error`). `development.md:254` runs `--drift` without `-c Release`, while `AGENTS.md:40` uses `-c Release`; with both configurations the second build costs time.
- **Proposal:** unwrap; add "(in the default space `DIPTopLeft`)"; keep the bullet list at :64-68 and shorten :62 to one sentence; use `-c Release` everywhere.
- **Decided:** 'No `Error` output pins', B1 → the shortened sentence at `diagnostics.md:62` and the bullet at `:66` say that the element node turns orange with the message and that QtNative errors and exceptions of the per-frame pump go to the log and mark the Renderer node; there is no Renderer `Error` output
- **Effort:** S, mechanical: yes
- **Touches:** `LICENSES.md`, `docs/help-patches.md`, `docs/diagnostics.md`, `docs/development.md`
- **Depends on:** none
- **Verification:** reading.

### DOC-16 docs/README.md: an index by audience, with the design notes separated from open topics and plans
- **Kind:** docs
- **Severity:** could
- **Breaking:** no
- **Where:** `docs/README.md:25-45`
- **Finding:** "Design notes" mixes decisions (`decisions/`), research (`research/`), background topics (`topics/`), an open brainstorm handover (`topics/gradients.md`: "handover for a brainstorm") and a plan (`plans/paths/00_overview.md` via :40). Plans and handovers are maintainer working material; a public reader expects the design notes to describe the present. `docs/plans/` is not excluded by `public-check.yml:30` (it checks top-level `plan/`), so `docs/plans/paths/` (11 files) and this review (`docs/plans/refactor/`) would ship publicly.
- **Proposal:** sections Using / Contributing / Maintaining / Design notes (decisions only) / Research (dated measurements and spikes); no links to plans or open topics. Whether `docs/plans/` stays tracked is Q3.
- **Decided:** G3, G5, G6 → the sections are Using / Contributing / Maintaining / Design notes (`docs/design/`) / Research; `docs/plans/` and the review reports leave `docs/` in wave 6 (G3, Question Q3 is answered with (a)) and finished plans and handovers are deleted, not archived (G6), so the index links none of them
- **Effort:** S, mechanical: no
- **Touches:** `docs/README.md`
- **Depends on:** Q3
- **Verification:** every link in `docs/README.md` resolves; no link into `plans/`.

## 4 Target structure

Audience: **U** vvvv user, **C** contributor (and coding agent), **M** maintainer. Pages named in the code (`grep -rn "docs/…\.md" src tools help .github`: `decisions/render-texture.md` 12×, `effects-and-shaders.md` 7×, `linux.md` 3×, `development.md` 3×, `styles.md` 2×, others 1×) keep their path or get their references updated in the same change.

```
README.md                      U   entry page: what, install, first patch, feature table → user pages, platforms, Qt Bridge comparison, license in short      rewrite (DOC-06)
CONTRIBUTING.md                C   how to contribute: fork/branch/PR, CLA, issue forms, milestones, labels (one home)                                       rewrite (DOC-03, DOC-05); maintainer section → docs/maintaining/
AGENTS.md                      C   rules and the before-you-finish checklist for people and agents, ≤ 7 KB, links                                             rewrite (DOC-14)
CLAUDE.md                      C   `@AGENTS.md`                                                                                                              keep
LICENSES.md                    U   licenses, export checklist, third-party table, name decision                                                            keep (DOC-15)
CLA.md                         C   contributor licence agreement                                                                                             keep (Q1)
docs/
  README.md                    all index in sections Using / Contributing / Maintaining / Design notes / Research                                           rewrite (DOC-16)
  glossary.md                  all Qt terms and VL.Qt terms, one sentence each                                                                            new (DOC-09)
  help-patches.md              U   the list of help patches by topic, complete                                                                              rewrite: rules out (DOC-12), missing patches in (DOC-04)
  coordinates.md (or section of help-patches/README) U  Space, units, DIP, transforms, anchors                                                              new, from README.md:70 and element-catalog
  shapes.md                    U   Line, Polyline, Polygon, Path, Qt.Paths, generators, limits                                                              keep (+ pictures from README)
  images.md                    U   QtImage, loaders, ToQtImage, Snapshot, FileWriter                                                                        keep (+ FileWriter from README.md:165-171)
  styles.md                    U   base style, Style pin, style nodes, IStylable                                                                            keep (units per DOC-10)
  effects-and-shaders.md       U   Effect pin, layer and shader effects, shader nodes, ShaderEffect, Sampler                                                keep (terms per DOC-10)
  user-qml.md                  U   QmlElement and QML node factory                                                                                          keep (DOC-11)
  video-audio.md               U   Video (VL.Audio)                                                                                                         keep, Tests section → contributing/testing.md (DOC-12)
  stride.md                    U   QtTexture and QtRenderer                                                                                                 new (DOC-07)
  export.md                    U   exporting with the vvvv exporter, fonts, qtshaders/qml folders                                                           rewrite: dialog first (DOC-12)
  linux.md                     U   export and run on Linux, WSL, fonts, known problems for users                                                            rewrite: contributor parts out (DOC-12)
  diagnostics.md               U   overlays, Debug pin, logging, device loss, env vars for diagnosis                                                        rewrite: measurement report out, QtTexture part → stride.md
  tooltips.md                  U   editor previews                                                                                                          merge into diagnostics.md (800 bytes; a section "In the editor")
  versioning.md                U/C version numbers, API, aspects, the pre-1.0 rule                                                                          rewrite (DOC-03 (c))
  architecture.md              C   layers, one frame, threads, protocol, subsystems with source paths, events                                              rewrite (DOC-01)
  development.md               C   prerequisites, first build, solution, packages, scripts, tests, encoding, folder layout                                  rewrite (DOC-02, DOC-05); CI/release → maintaining/
  profiling.md                 C   CPU profiling with Ultra, SmokeTest perf scenarios (measure, qttextureperf, --replay, --tearing)                         rewrite (DOC-13)
  contributing/
    add-an-element.md          C   walkthrough of one element through protocol, C#, reconciler, node, QtNative, tests, help patch                         new (DOC-08)
    help-patches.md            C   writing help patches: Help.xml, text scheme, layout, vl-patch-check, manual-check                                         new, from help-patches.md:5-40 and AGENTS.md:46-68
    testing.md                 C   unit tests, SmokeTest scenarios and options, Audio tests, what protects what                                             new, from development.md:46-91, video-audio.md:62-66
    linux.md                   C   building the Linux runtime, test-linux.sh, window pins under WSLg, timing calibration                                    new, from linux.md:107-137, 155, 160-163
    debugging.md               C   dumps, stacks, cdb, QT_LOGGING_RULES, QT_FATAL_WARNINGS                                                                   new, from AGENTS.md:93-109
  maintaining/
    release.md                 M   CI, repository settings, Trusted Publishing, release steps, contributors list                                           new, from development.md:163-195 and CONTRIBUTING.md:31-33
  decisions/, research/, topics/   design notes (other area)                                                                                                 index only (DOC-16)
```

Deleted: `docs/tooltips.md` (merged). No other page is deleted; most change their audience mix.

## 5 Questions for Sebastian

**Q1 CLA: draft status and the contracting party.** `CLA.md:1` says "Draft – pending legal review" while the CLA bot (`.github/workflows/cla.yml`) already asks contributors to sign it. The "Maintainer" is a natural person (`CLA.md:14`, "Sebastian Huber … and his successors and assigns"), while the studio is the Huber-Pohle-Timpernagel GbR (SCHNELLE BUNTE BILDER). Alternatives: (a) keep the person as licensee; (b) name the GbR as licensee, the person as its representative; (c) leave as is until the legal review. Consequences: signatures collected under one wording cannot be moved to another party without re-signing (or the transfer clause `CLA.md:75`). Recommendation: decide (a) or (b) with the legal review before the repository becomes public and before the first external signature; remove the draft comment then. This is a legal decision, not a documentation one.

**Q2 Milestones and version rule after 0.10.0.** AGENTS.md and CONTRIBUTING.md say "0.9.x until 1.0, everything into `1.0`". The refactoring releases 0.10.0 from milestone `refactor`, and DECISIONS says 0.10.x is the last release before the repository becomes public. Alternatives: (a) after 0.10.0 return to `1.0`/`2.0`/`3.0` with "0.10.x until 1.0"; (b) drop the version-number rule from the contributor pages and keep only "milestones are version numbers; `1.0` blocks the release". Recommendation: (b): it stays true without edits at every minor release.

**Q3 Do `docs/plans/` and the review reports ship in the public repository?** `public-check.yml:30` blocks only a top-level `plan/` and `docs-internal/`; `docs/plans/paths/` (11 files, tracked) and `docs/plans/refactor/` are public once pushed. Alternatives: (a) move them to `docs-internal/` (blocked) or out of the repository; (b) keep them public as a record of how the library was built. Recommendation: (a) for working plans and reviews; what is still true after the refactoring goes into `docs/decisions/` or `docs/research/`.

**Q4 One page per package or one per topic for the companions?** `video-audio.md` is per package (VL.Qt.Audio), the new `stride.md` would be too. The alternative is topic pages ("Video", "Rendering into Stride") that also cover the core `Video` node. Recommendation: per package, as proposed: the user installs packages, and the Help Browser groups by package too.

## 6 For other areas

- **API (23):** `QuadTo` has `End Point` (`src/VL.Qt/Nodes/Paths/QuadToNode.cs:25`) but `CubicTo` has `EndPoint` (`src/VL.Qt/Nodes/Paths/CubicToNode.cs:31`); `shapes.md:57` documents both faithfully. One spelling.
- **API / styles (13):** style sizes are documented as "px" (`src/VL.Qt/Styles/StyleNodes.cs:235`, `docs/styles.md:28`) while all other lengths are units or DIP; decide which unit the style fields use and name it in the pin help.
- **tools:** `tools/start-vvvv-with-nupkg.ps1` and `tools/test-nupkg-consumer.ps1` know VL.Qt.Stride but not VL.Qt.Audio (0 occurrences of "Audio" in both), so a release test never loads or checks the third package.
- **tools:** `tools/start-vvvv.ps1:1-50` has German comments; all other scripts and docs are English.
- **SmokeTest:** check names start with issue numbers (`233: created borderless at Bounds`, `143/234 live: …`, quoted in `docs/linux.md:162`); rename them by what they test as part of K1.
- **CI (`public-check.yml:30`):** the pattern does not cover `docs/plans/` (see Q3).
- **XML docs / node help:** "1 unit = 100 DIP" is stated without the `Space` condition in several places (`docs/help-patches.md:35` quotes a help patch note; check the node help of `Size` pins and `src/VL.Qt/Space.cs:20`).
