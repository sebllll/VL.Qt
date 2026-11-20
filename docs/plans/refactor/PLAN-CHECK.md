# Plan check – refactor (release 0.10.0)

Claude Fable 5, 2026-11-19, on `refactor` at 1edbe54. A check of the decisions before the findings are cut into issues: do they contradict each other, do they fit the code, and what is still missing.

Read in full: [DECISIONS.md](DECISIONS.md), [QUESTIONS.md](QUESTIONS.md), [review/README.md](review/README.md), [review/00_brief.md](review/00_brief.md), sections 4 to 7 of [review/23_api.md](review/23_api.md). Of the other 22 reports: one line per finding (id, severity, kind, effort, breaking, title, dependencies) for all 340, and the full text of about 20 findings that a decision touches. Checked in the code: the element types, the node signatures, the environment variables, the render thread pool, the file nodes, the workflows and the repository settings. Nothing was built or run.

## Result

The 76 questions and the catalog are decided and every question of the reports is in QUESTIONS.md (the counts match report by report). The decisions hold together with two exceptions (P5, P13). What is not ready is the step from the decisions to the issues: the findings were written before the decisions and several now say the opposite (P1, P2), and the cross-area picture that the review left for "when the plan is written" does not exist yet (P3, P4). An agent that implements a finding as written today would build the wrong thing in those places.

P1 to P22 are decided (2026-11-19); each carries its decision below, and the ones of principle are in DECISIONS.md. P7 to P15 were decisions that needed one more sentence, P16 to P22 are rules for the work.

What follows, in this order: [TARGET.md](TARGET.md) is drafted and its ten questions are decided (P3) → reconcile the findings with the decisions and append the missing catalog rows (P1, P2), and in parallel compare the shaders with VL.Stride (P12) → decide the open rows → cut the issues.

## Before the issues are cut

### P1 Findings that a later decision overturned

The brief promises "a proposal concrete enough to implement without this review". These proposals are no longer true:

| Finding | Says | Decided |
|---|---|---|
| NODX-02 | Click `Is Clicked` → `Clicked`, keep `Long Pressed` | `On Click`, `On Long Press` (catalog) |
| NODX-07 | AddRect, AddOval, AddArc default `-0.5, -0.5, 1, 1` | `0, 0, 1, 1` (catalog) |
| NODW-02 | Button channel pin named `Channel` | the same pin becomes `On Click` (catalog) |
| NODW-05, API-12 | `ToQtImage (VideoSource)` | name kept |
| API-09 | `Uniform (Float)` … | names kept, only the category moves |
| API-10 | Font Weight as the enum | `int` everywhere, the enum goes |
| NODW-04, RUN-01, B1 (a), B7 (a) | an `Error` output reads the message | no `Error` pins |
| TEST-11 | one scenario table for the custom runner | NUnit (F5) |
| API-01, section 4 of 23_api | `Element` is a public pin type | `IElement` on every pin, `Element` internal (C3) |
| ELM Q2 | concrete element classes public | internal (A6) |
| RUN-13 | hide `Geometry` | internal (catalog) |
| C2 in the catalog row | `Enabled` "before `Opacity`?" in the Proposed column | last input pin |

The table comes from titles and samples and is not complete. 45 more findings branch on a question in their text ("depends on Q1: if the key becomes the slot …, if it stays the instance …") and still hold both branches.

- (a) Resolve it when an issue is cut: the issue text is written from the decision, the finding is linked as evidence.
- (b) One pass through all 23 reports before the cut: every finding that a decision changes, or whose proposal branches on a question, gets a line `- **Decided:** <question or catalog row> → <what holds>` under its proposal.
- Decision: (b), as a session of its own (Sebastian, 2026-11-19). The issue texts still stand on their own; the pass makes the cut safe and collects the rows of P2.

### P2 Changes of the node API that are in no catalog

Section 7 of 23_api.md was decided row by row, and eight of about sixty rows were decided differently from the proposal. The other reports propose changes that a patch sees and that have no row:

- NODX-02: Drag `Delta` → `Translation`; optional `Devices` on Hover.
- NODX-07: `[DefaultValue]` values for every effect and interaction pin ("choose visible ones").
- NODW-02: where the Button channel pin sits (after `Anchor`).
- NODW-05: output pin of `VideoSourceToQtImage` → `Image`.
- C5: about fifty style nodes swap names (`Radius` → `SetRadius`, `GetRadius` → `Radius`, `State` → `States`). Section 4 of 23_api still says "Qt.Style unchanged", and the two style rows of the catalog use the old names.
- C6: the names of the six process nodes (`SetFrame` or `Frame`?).
- SIP-02: the AddArc part (C7 decides only the radii of AddRoundRect).
- SIP-14: `QtImage.State` after its node released it.
- COMP-06: the `Perf` pin of QtTexture goes.
- NDIAG-10, RUN-10: the field names of `PerfStats` (they are the pins of its Split).
- NFX-21: QML `point` and `size` values reach the patch as `Vector2`.
- NELM-01, COMP-04: behaviour a patch can see (frame of a nested Group, audio latency).

The list is not complete. Its points are of three kinds: consequences of a decided principle (C5: the table is generated from the code and only read against), changes that are open (a row and a decision each), and fixes a patch can see (NELM-01, COMP-04: no decision, a list for the release notes).

- (a) Decide the known points now; a second round follows after the pass of P1.
- (b) The pass of P1 collects every change without a row and appends it to section 7 of 23_api.md, with its source, a proposal and its kind; the open rows are then decided in one round.
- Decision: (b) (Sebastian, 2026-11-19).

### P3 The cross-area structure and one glossary are missing

The index says the architecture as a whole "is put together from sections 4 and 6 of the reports when the plan is written". That has not happened, and the reports claim the same words. "Scene" alone would be: the C handle `vlqt_scene` for a window or an offscreen scene (CAPI-03, A5), the native object table `vlqt::Scene` in `scene.h` (NCMD-05), the offscreen state `vlqt::offscreen::Scene` (NDEV-04), the C# class `Scene` (RUN-06), `SceneRegistry` (REC-06, with an open question in RUN-06 about its name), and the element tree in the docs. NODX-11 waits for "the namespace decision", which no question asks.

"Feedback" clashes too, and with a decision: A1 names the way back from Qt so in C# (`ElementFeedback`, `FeedbackSlot`, `FeedbackRouter`), while QtNative already has the `FeedbackLayer` (`src/VL.Qt.Native/feedback.h`), the layer that keeps the previous frame while `Clear` is off. The elements report wrote "no clash" without knowing the native layer, and to a vvvv user "feedback" means frame feedback first.

- (a) One page `docs/plans/refactor/TARGET.md` before wave 3, in three parts: a glossary with one word per concept across C, C++, C#, the docs and the node names, clashes as open rows; the folder and namespace tree of the four projects merged from the sections 4, with a namespace rule; which findings cut the same file, as the base for what may run in parallel in wave 4.
- (b) Only the glossary now, the tree per area in wave 4.
- (c) Nothing central.
- Decision: (a), before the pass of P1 (Sebastian, 2026-11-19). The glossary part becomes the page of DOC-09; DOC-09 and DOC-10 move from wave 6 to here. Order: draft `TARGET.md` → decide its open words (among them "feedback") → pass of P1 with the catalog rows of P2 → decide the open rows → cut the issues.

### P4 "The native layer breaks once" needs a list and an order

A5 names five renames. These decisions and findings change the C ABI or the protocol too: B3 (version check, PROTO-01), B4 (`vlqt_image_write`), B5, CAPI-02 and NAPP-03 (return codes of every export), E7 and E10 (pool size at runtime, upload mode per image), E13 and NDIAG-10 (perf structs), F1 and F2 (test exports move, some leave the library), C2 (`Enabled` is a new property), `FillMode`, PROTO-07/09/10, REC-13, NRES-12, NRES-15, NDEV-12. As written, "once" cannot hold.

The reports also disagree on the order: the C API report moves the test exports first and renames then; the native report cuts `vlqt.cpp` first (NAPP-01, effort L), then moves the test exports, then renames.

Decision (Sebastian, 2026-11-19): "once" means that every name and every field changes at most once. Later additions are allowed and each one raises the protocol version, which B3 makes safe. The break is a sequence of pull requests in wave 3, one after the other, none in parallel:

1. Checks: PROTO-01 (version), PROTO-08 (drift as a unit test), CAPI-06 (`vlqt.h` against `Native.cs`).
2. Test exports out: CAPI-04, NAPP-09, TEST-08 into `vlqt_test.h` and `test_*.cpp`; NDEV-09 into the testkit library.
3. The break, in three pull requests: return codes and the per-thread error text (CAPI-01/02, NAPP-02/03, B4; not mechanical); the names after the glossary of P3 (CAPI-03, NAPP-17, NELM-11, NDIAG-10, PROTO-10; mechanical); the wire format (PROTO-02, PROTO-09, B5, REC-13) with the before/after measurement of A4.
4. What `u32` makes possible: remove the caps and the chunking (REC-01, NCMD-08, QML records).

`vlqt.cpp` (NAPP-01) and `Native.cs` (CAPI-07) are cut after the break, in wave 4: a rename by search and replace does not care about the file layout, and every native issue of wave 4 then starts with the final names.

### P5 `Element` internal does not compile as decided

`IStylable.Build` is public, meant to be implemented in a patch, and returns `Element?` (`src/VL.Qt/Styles/IStylable.cs:21`); an internal return type on a public interface is a compile error. The other public places that name `Element` are the outputs of the 25 element nodes and twelve inputs: Renderer, Labeled, Snapshot, FileWriter (Element), ShaderEffect, Blend `Layer`, Mask `Mask`, Displace `Map`, the uniform node `Texture`, TooltipPreview, QtTexture and QtRenderer. `VL.Qt.HDE.vl` types its viewer `Element` (three references).

What `Build` returns:

- (a) `IElement?`, and a returned stylable is resolved in turn, so a widget may delegate to another; the reconciler guards against a widget that returns itself.
- (b) `IElement?`, but a returned stylable shows nothing and warns.
- (c) `Element` stays public without public members; the user sees two type names again, against C3.
- Decision: (a) (Sebastian, 2026-11-19).

For the C3 issue, with it: one internal conversion `IElement → Element` serves all twelve inputs (it wraps a stylable as `ChildrenInput` does today and allocates nothing in the normal case); a foreign implementation of `IElement` is ignored as today, but with a warning at the node; an exception from `Build` at the root (a stylable linked to the Renderer) shows at the Renderer. The help patch `HowTo Patch a Stylable Widget` implements `Build` and is compile-checked with the change.

### P6 The node-API snapshot would miss half of the API

TEST-01 reflects over `[ProcessNode]` classes (76) and lists the imported type names. vvvv also makes nodes from the public static methods of `StyleNodes`, `FrameNodes`, `TextNodes`, `IndicatorNodes`, `TrackNodes` and `UniformNodes` (63), from the public members of the imported types (`Style`, `QtImage`, `PerfStats`, `QtPath`, `TouchPoint` …, roughly 60), and from shader and QML files through the two node factories, which give every generated node a fixed pin set. C5 alone renames about fifty of the static nodes, `Enabled` also comes to the generated nodes, and nothing would notice a lost node or pin there.

- (a) The whole public C# surface: every public type and every public member of the three assemblies, with the attributes vvvv reads (node name, category, pin name, default, visibility, smell), plus the fixed pin set of the generated nodes from a run of the two factories over a test file. It does not model vvvv's import rules and need not: what vvvv shows can only change when the public surface or an attribute changes.
- (b) Ask vvvv itself for the node list: exact, but the import lives in vvvv, the test project references only `VL.Core`, so the test would need a vvvv installation.
- (c) As TEST-01, process nodes only.
- Decision: (a) (Sebastian, 2026-11-19). Pin names are written by the rule `tools/vl_pin_catalog.py` uses today (`fillColor` → `Fill Color`), so that the migration script of P19 can work from the diff of the snapshot. `Dark` and `Light` are patched in `VL.Qt.vl` and are covered by compiling that document with `tools/vvvvc.ps1`. The snapshot also shows API-01: the types that become internal leave the file.

## Decisions that need one more sentence

- **P7 E7 `Render Threads` at runtime.** The pool is fixed at its first use and its threads are never deleted (`render_thread.cpp:19-22`). "A change at runtime grows or shrinks the pool" and a switch to 0 mean moving live scenes between threads, in the file with the use-after-free of NDEV-01. Proposal: growing adds threads; a smaller value and 0 apply to scenes created afterwards. Say also that 0 only takes effect when every node asks for 0, since the maximum wins.
  - Decision: as proposed (Sebastian, 2026-11-19). A larger value adds threads at once and new scenes use them. A smaller value and 0 apply to scenes created afterwards; a live scene keeps its thread, and threads are never deleted. 0 takes effect only when every node asks for 0. The pin help says so. Not taken: moving live scenes between threads, and a value that is fixed at the first scene.
- **P8 A4 without a fallback.** "No measurable loss" has no threshold and no answer for a failed comparison, on a machine with about 55 % background load. Proposal: median of five runs, limit 3 % apply time; above it (c) or (b), raised again.
  - Decision: (Sebastian, 2026-11-19) median of five runs before and after, on one machine in one session. "Measurable" is a loss above 3 % of the apply time, and never below the spread of the five runs before, so that the noise of the machine cannot fail it. Above the limit the pair is measured once more; if it fails again the issue stops and shows the numbers. First fallback is `u32` for events only (way (c) of A4), second `u16` as today (way (b)).
- **P9 F3 reference images cannot run on the development machine.** References exist only for Windows x64 (WARP), the gate runs on win-arm64, the CI only at checkpoints. Wave 4 restructures the native rendering (NELM-07/08, E12, D8) without any picture comparison in between. Proposal: wave 1 checks whether WARP on arm64 matches the x64 references within the tolerance; if not, a second set for win-arm64. Also say who creates and updates a reference (a CI artifact that is committed).
  - Decision: as proposed (Sebastian, 2026-11-19). The compare always runs on WARP, so that the local gate and the CI see the same pixels. Wave 1 checks on the arm64 notebook and on the x64 workstation whether a local WARP run matches the x64 references within the tolerance (WARP differs between Windows versions and between x64 and arm64, so this is a check, not a given). If arm64 does not match, a second set for win-arm64 is added. The issue names the switch that forces WARP. A reference changes only through a run with `--update-refs`, made on the x64 workstation or taken from the CI artifact, and is committed on its own in the pull request with the diff image in the description.
- **P10 FileWriter loses its only failure signal.** `Is Done` is true "also when it failed, then Error says why" (`FileWriterNode.cs:42`). Without `Error` a patch that writes a sequence cannot tell. Proposal: `Is Done` only on success, or a second output `Success`.
  - Decision: a second output `Success` on FileWriter and FileWriter (Element) (Sebastian, 2026-11-19). `Is Done` stays true in the frame the write ended; `Success` says how it ended and holds until the next write. The message goes to the log and to the node. The pin is a catalog row for the pass of P2, already decided. Not taken: `Is Done` only on success, because a patch could then not tell "not done yet" from "failed".
- **P11 `Enabled` in Row and Column.** Qt's positioners skip an invisible item, so the siblings move up. Say in the pin help whether that is meant.
  - Decision: it is meant (Sebastian, 2026-11-19). `Enabled` off acts like taking the element out of the spread: it takes no space and the siblings move up, while the element keeps its identity and state. The pin help says so and names `Opacity` 0 for keeping the gap. When the alignment of Row and Column moves to C++ (E12, NELM-13), the new code skips invisible children as Qt's positioners do. Not taken: a placeholder that keeps the gap.
- **P12 "Shaders follow VL.Stride" has no list.** The principle was decided after report 12 was written, so nobody compared the shipped shaders, their uniform names and defaults with TextureFX. Proposal: one issue that writes this comparison as catalog rows, decided before the shader issues start. The open note on Blur `Max Radius` (pixels or DIP) belongs there.
  - Decision: a read-only session of its own before the catalog round (Sebastian, 2026-11-19). It compares the shaders in `src/VL.Qt.Native/shaders/` and the twelve effect nodes with their counterparts in `C:\vl\VL.StandardLibs\VL.Stride` and writes `review/28_shader-reference.md`: rows in the format of the catalog, with a column for the reason of a deviation. Scope: what a patch and a shader author see, that is pin and uniform names, units, the meaning of a parameter and file conventions. The shaders need not compute or look like their TextureFX counterparts. It may run in parallel to the pass of P1, since it writes its own file; its rows are decided in the same round as the open rows of P2. Order: `TARGET.md` → pass of P1/P2 and shader comparison → decide the open rows → cut the issues.
- **P13 Milestone name.** "All issues go into a new milestone `refactor`" (2026-11-18) contradicts G2, which keeps "milestones are version numbers". Proposal: milestone `0.10.0`.
  - Decision: milestone `0.10.0` (Sebastian, 2026-11-19). The entry "Milestone and release" in DECISIONS.md and the index of the review are corrected.
- **P14 Wave 3 is smaller than it reads.** K2, K5, A1 and the break are repository-wide and mechanical. K3 (one type per file) is not: NAPP-01, NELM-05, NDEV-04, NCMD-05, NRES-03, NDIAG-04, REC-04, RUN-05 and QML-02 cut the same files differently in wave 4, so a mechanical cut first moves every type twice. K1 and K4 are mechanical only where a comment needs no rewrite. Proposal: K3 and the rewriting part of K1 and K4 are done per area in wave 4; wave 3 removes only the bare issue references.
  - Decision: as proposed (Sebastian, 2026-11-19). Wave 3 holds what is mechanical: K2, the bare issue references of K1, the rename of A1, the native break (P4) and the two moves of S3. K3, K4 and the comments of K1 that need a new sentence are done in wave 4 by the issue that cuts or rewrites the file. Wave 4 ends with a check as a script, which then joins the gate: no `#NNN` in `src`, no comment line over 160 characters, no file with two types besides the named exceptions.
- **P15 K7 has no owner.** `Directory.Build.props` says 0.1.0; no finding and no question sets it. One line in the release issue.
  - Decision: as proposed (Sebastian, 2026-11-19): the release issue of 0.10.0 sets `VersionPrefix` in `Directory.Build.props` to `0.10.0`.

## Rules for the work

- **P16 One machine, many agents.** The gate is `tools/build.ps1 -Test`. Several SmokeTests at once on one GPU fail the timing checks. Proposal: one verifier at a time runs the gate; F4's `Perf` category is warn-only locally as well.
  - Decision: as proposed, per machine (Sebastian, 2026-11-19). There are two machines, the arm64 notebook and an x64 workstation. On each, one verifier runs the full gate, one run after the other; an agent runs in its worktree only what needs no GPU (`dotnet test`, `--drift`, the patch check). The limits of the category `Perf` warn in the local gate as on the CI; the allocation tests and everything functional stay hard. Hard timing belongs to the measurements that need it (the baseline of wave 1, the comparison of A4), which run alone on a quiet machine. Which machine verifies which lane is settled when the issues are cut.
- **P17 Linux between checkpoints.** The gate is Windows only. Issues that touch `#if` blocks or CMake (NAPP-08, NAPP-12, NDEV-08, E5, the break) add the WSL build (`tools/build-native.sh`) to their gate.
  - Decision: as proposed (Sebastian, 2026-11-19). Every native cut of wave 4 belongs to these issues too, since it edits `CMakeLists.txt` (S2 in [TARGET.md](TARGET.md)). The WSL build is not part of the general gate.
- **P18 When is a checkpoint.** Not defined. Proposal: after wave 1, after each step of P4, in the middle and at the end of wave 4, before the release.
  - Decision: five CI runs (Sebastian, 2026-11-19): after wave 1, after the native break as a whole, in the middle and at the end of wave 4, and before the release (that one through `workflow_dispatch` with all four RIDs). After each single step of P4 a local checkpoint runs instead: the full gate on both machines and `tools/test-linux.sh` under WSL. Reason: win-x64 and linux-x64 can be tested locally, so the CI adds mainly a clean machine. Not checked: the minutes of one run, and whether WSL is set up on the workstation.
- **P19 `manual-check` in one pass.** Renames reach all 62 help patches, several times. Proposal: the patches are migrated by one script from the snapshot diff (it must rename `Radius` → `SetRadius` before `GetRadius` → `Radius`, or setters silently become getters), and looked at in vvvv once, at the end of wave 4.
  - Decision: as proposed, together with P20 (Sebastian, 2026-11-19). An API issue changes C# and the node-API snapshot and adds its renames as rows to one mapping file (old node, pin or category → new); it does not edit a help patch by hand. A script applies the new rows of an issue to all help patches at once, not one after the other, which settles the swap of C5. The patches stay compilable after every issue, so `vl-patch-check.py` and `vvvvc.ps1` stay in the gate. All patches are looked at in vvvv once, at the end of wave 4 (`manual-check` on one issue). The script is an internal tool; a migration script for users stays rejected.
- **P20 Parallel issues and `.vl` files.** Two issues that edit the same help patch cannot be merged by git in a useful way. Proposal: API issues that touch help patches run one after the other, or the patch edits are collected in the script of P19.
  - Decision: the script of P19 (Sebastian, 2026-11-19). A `.vl` file is derived from the mapping file: on a conflict the mapping file is merged and the script runs again, nobody merges a patch by hand. The script only renames and moves categories. An issue that adds a pin or rebuilds a patch edits the `.vl` file directly; two such issues on one patch run one after the other.
- **P21 Hotfixes and open issues.** Say who merges `develop` into `refactor` after a hotfix, and whether the open issues of milestone 1.0 (#144 "Help flags: at least one per node" fits the patch pass) join the refactoring or wait.
  - Decision: (Sebastian, 2026-11-19) whoever merges a hotfix into `develop` opens a pull request `develop` → `refactor` right after it and merges it as a merge commit, not as a squash, before the next issue is merged; a squash would cut the common history and the closing pull request would see every hotfix as a conflict. The open issues stay in their milestones. The issue cut goes once through all open issues (24 on 2026-11-19, five of them in `1.0`) and moves into `0.10.0` only what the refactoring does anyway or what would touch the same files twice: #144 joins the look at the patches at the end of wave 4, #28 is checked against E1 and the shader comparison of P12. The overlaps are read from the titles only.
- **P22 Duplicates across reports.** One issue each for: CAPI-01 = NAPP-02; CAPI-02 = NAPP-03; CAPI-04 = NAPP-09 = TEST-08; API-06 = NODX-01; API-07 = NODW-08; API-11 = NODX-13; RUN-10 = NDIAG-11; QML-03 = SHD-10; ELM-14 = QML-07; NELM-06 with NFX-06; the K6 findings (REC-14, RUN-14, NODE-06, SHD-12, SIP-07, NODW-10, NODX-10, NRES-09, NDIAG-14) under TEST-07.
  - Decision: as listed (Sebastian, 2026-11-19), and from section 3.2 of [TARGET.md](TARGET.md): PROTO-12 = NCMD-06 (`wire.h`); PROTO-06 with CAPI-04 (`vlqt_test.h`); NODE-05, RUN-01 and NODW-04 as one mechanism for messages at a node. NAPP-07, NDEV-04 and CAPI-03 stay three issues and share the names of O2.

## Wave 1 in one list

The decisions name these in many places (0.2, C16, D1, A6, E8, F5, F6 and the table at the end of QUESTIONS.md):

1. F5: SmokeTest as NUnit. Acceptance: the same scenario names and checks pass before and after, the grabbed PNGs are equal.
2. TEST-01 with the scope of P6. The perf baseline for A4, taken on the NUnit runner.
3. Tests that findings name as a condition for their refactoring: REC-16, QML-12, NCMD-15, NELM-12, NFX-23, COMP-08, COMP-09; TEST-03 with P9.
4. Confirm or drop: REC-01, CAPI-01, NDIAG-01; SIP-03, COMP-03, COMP-04, NAPP-10; and five suspicions QUESTIONS.md does not list: QML-06, NCMD-17, NELM-14, NDIAG-06, NODW-03.
5. Measurements and checks: C16 (Clip grab), E8 (video texture), D1 (`<include>` as pin help; low risk, `VL.Qt.vl` references the compiled DLL, so the compiler expands the include), A6 (tooltip of an `IElement` link). Both checked in vvvv by Sebastian, 2026-11-20 (W1-18): D1 works (an `<include>` must replace the whole `<param>`, CS1589 inside it); A6 works today; `ElementViewer` (`IValueViewer<Element>`, `VL.Qt.HDE.vl`) must move to `IElement` in W4-16.

## Stale lines in DECISIONS.md

- "Milestone and release": corrected with P13.
- The counts: the reports hold 31 must, 211 should, 98 could. The total in the index said 209 and 100 and is corrected; the per-report columns of the index are not recounted.

Corrected with P1 to P6: the section "Open", the output type in "Concrete element classes are internal", and "one change" in "The native layer breaks once".

## Checked, nothing to do

- Pull requests against `refactor` start no workflow: `build.yml` and `public-check.yml` filter on `develop` and `main`, `cla.yml` skips while the repository is private, `sanitizers.yml` filters on its own paths.
- Merge commits are allowed in the repository, so `refactor` → `develop` can keep its history.
- A2 (key by slot) holds together with B6 and C3 as far as REC-10 describes the code: the registry counts occurrences, the property diff works per position in the tree, so two instances of one node in one tree keep their own properties. Not traced in `SceneRegistry.cs` myself.
- Every environment variable the decisions name exists and is read once per process (`VLQT_OFFSCREEN_THREAD`, `VLQT_IMG_UPLOAD`); `VLQT_GPU`, `VLQT_GPU_PROBE` and `VLQT_SHARED_DEVICE` act before Qt starts and stay variables under the rule.
- No help patch has a pad typed `Element`, so the type change of C3 breaks no patch.
