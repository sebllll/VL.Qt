# Issues – refactor (release 0.10.0)

Claude Opus 5.5, 2026-11-19, on `refactor` at 8779a2a; grouped into GitHub issues on 2026-11-20. The cut of the 340 review findings into issues of the milestone `0.10.0`, after [DECISIONS.md](DECISIONS.md), [PLAN-CHECK.md](PLAN-CHECK.md) (P1 to P22), [TARGET.md](TARGET.md) part 3 and [DECISION-ROUND.md](DECISION-ROUND.md). The `Decided:` line of every finding binds over its proposal. Nothing was built or run, and nothing was created on GitHub: this is the draft from which the issues are opened.

## 1 Summary

**30 GitHub issues with 192 tasks** (section 2a; a task is one branch and one PR, called "issue" in the tables below) for 340 findings (31 must, 211 should, 98 could), the decided catalog rows of [review/23_api.md](review/23_api.md) section 7 and the decided rows of [review/28_shader-reference.md](review/28_shader-reference.md). Every finding is in exactly one issue (section 6); none is unplaced.

| Wave | Issues | What |
|---|---|---|
| 1 | 19 | safety net: SmokeTest as NUnit, node-API snapshot, tests named as conditions, confirm or drop, measurements |
| 2 | 0 | API on paper: done (QUESTIONS.md, the catalog, DECISION-ROUND.md, TARGET.md), no issues |
| 3 | 15 | repository-wide mechanical steps one after the other, the native break as seven pull requests; mapping script, glossary and design folder in parallel |
| 4 | 131 | cuts as pure moves, the cross-cutting chain, fixes in 15 lanes, closing check and the look at all help patches |
| 5 | 7 | tests (the K6 hooks, test folders, CI patch check) |
| 6 | 19 | documentation |
| 7 | 1 | release 0.10.0 |
| total | 192 | |

Wave 4 per lane: cuts 15, chain 6, N-app 8, N-scene 7, N-elements 7, N-effects 8, N-resources 9, N-device 7, N-diagnostics 6, C-reconciler 10, C-runtime 6, C-elements 3, C-nodes 7, C-chains 10, C-files 11, C-values 6, companions 3, closing 2.

Per machine (P16): arm64 95, x64 92, Sebastian in vvvv 2, both 1. 35 issues add the WSL build to their gate (P17), 11 carry `manual-check`.

**Must findings:** all 31 are in 28 issues, each the main finding or named in the title: W1-04, W3-09, W3-12, W3-14, W4-13, W4-16, W4-20, W4-30, W4-31, W4-37, W4-52, W4-61, W4-68, W4-74, W4-75, W4-93, W4-100, W4-101, W4-110, W4-116, W6-02, W6-03, W6-04, W6-05, W6-09, W6-11, W6-12, W6-13. Twelve findings are suspicions that wave 1 confirms or drops before their issue starts: REC-01, CAPI-01, NDIAG-01, SIP-03, COMP-03, COMP-04, NAPP-10, QML-06, NCMD-17, NELM-14, NDIAG-06, NODW-03.

**Critical path:** W1-01 (F5) → W1-04 (snapshot) → the mechanical steps W3-01–W3-05 → the seven pull requests of the native break W3-09–W3-15 → W4-01 (CMake) → the native cuts W4-03 → W4-08 → W4-09 → the lanes N-effects and N-resources, which share the shader item and run one after the other → W4-130 (closing check) → W4-131 (patch look) → W5-01 (TEST-07) → wave 6 → W7-01; all native work runs on the x64 workstation, which makes it the bottleneck.

## 2 Rules of the cut

- **One task, one branch, one draft PR** against `refactor`, merged with a merge commit, no squash (0.1, changed by Sebastian on 2026-11-20); the tasks of one package are one GitHub issue with a task list (section 2a, Sebastian, 2026-11-20). Milestone `0.10.0` (P13). Created on 2026-11-20.
- **Ids** are `W<wave>-<NN>`, numbered in the order the issues run. Wave 4 is numbered cuts, chain, lanes, closing. The title is the GitHub title.
- **Findings:** the first one is the main finding. Merged into one issue are the duplicates of P22, the merges of TARGET 3.2, and S or M findings of one lane that edit the same files. Every `must` finding is the main finding of its issue or is named in its title.
- **Overturned findings** (P1, `Decided:` lines that overturn) are cut as the decided thing in the issue that does it: TEST-11 in W1-01, API-09 in W4-17, NODW-02 in W4-95, NODW-05 in W4-98. No finding is left without an issue.
- **Confirm or drop** (wave 1, item 4): the twelve suspected findings keep their issue in wave 4. If wave 1 drops one, its part of the issue goes, and an issue left empty is closed as "not planned".
- **Wave 4 order (TARGET 3.3, B12):** `CMakeLists.txt` first (W4-01), then the cuts as pure moves (native round 1 W4-02–W4-05, round 2 W4-06–W4-09, C# cuts W4-10–W4-15 in parallel to native), then the chain W4-16 → W4-21, then the lanes. A native lane starts when the cut of its files is merged; the C# lanes start after the chain issue they build on (Dep. column). Inside a lane the rows run top to bottom, lanes run in parallel. A cut puts its files into the folder and with the names of TARGET 2.3 at once and does K3, K4 and the comments of K1 that need a new sentence (P14).
- **Crossing findings** are worked in the lane of the file they edit (TARGET 3.3). Where a finding edits two lanes equally, it stays in the lane of its report and the other lane waits for it through the Dep. column (judgement call, one line in the note where it matters).
- **Help patches (P19, P20):** "mapping rows" means the issue adds old → new rows to the mapping file and runs the script over all patches; "direct .vl" means the issue edits patches itself (an added pin, a changed value, a removed link), one such issue per patch at a time, and carries `manual-check`. Every issue that changes the public surface regenerates `node-api.txt` (F6). `vl-patch-check.py` and `vvvvc.ps1` stay in the gate.
- **Gate (P16):** `tools/build.ps1 -Test`, run by the one verifier of the machine in the Verifies column, one run after the other. An agent runs in its worktree only what needs no GPU. The `Perf` category warns (W1-07). WSL column "yes": the gate adds `tools/build-native.sh` under WSL (P17).
- **Checkpoints (P18):** CI after wave 1; after each pull request of the break the full gate on both machines and `tools/test-linux.sh` under WSL, CI after W3-15; CI in the middle of wave 4 (cuts and chain merged) and after W4-131; `workflow_dispatch` with all four RIDs in W7-01.
- **Labels:** exactly one type (`bug` when the main finding is a defect, `question` for a check or measurement, `documentation` for docs and XML docs, `enhancement` otherwise), the areas of the files touched, at most one topic, `manual-check` where a person must look in vvvv. Repository-wide pages (README, AGENTS.md, CONTRIBUTING.md, development, index, glossary) carry no area. No new labels.

## 2a GitHub issues and their tasks

Sebastian, 2026-11-20: the 192 tasks of section 3 are grouped into 30 GitHub issues, one per package: a wave-1 or wave-3 package, the native and the C# cuts, the chain, one issue per lane of wave 4, the closing, two of tests, two of docs, the release. A task is what section 3 calls an issue: one branch and one draft PR against `refactor`. The GitHub issue holds its tasks as a task list; a PR says `Part of #<issue>, task <id>` and ticks its box when it is merged; the issue is closed when its last task is merged. Labels of an issue: the union of the areas of its tasks, one type (the type of most of its tasks, `bug` where the lane is mostly fixes), at most one topic, `manual-check` if any task carries it.
Opened on GitHub on 2026-11-20 in the milestone `0.10.0` (#169 to #198); #144 and #28 moved into it (section 5).


| Issue | GitHub | Wave | Title | Tasks | Labels |
|---|---|---|---|---|---|
| I-01 | [#169](https://github.com/sebllll/VL.Qt/issues/169) | 1 | Wave 1: SmokeTest on NUnit, perf baseline, reference images, Linux green | W1-01, W1-07, W1-08, W1-12, W1-13, W1-19 | `enhancement`, `area:csharp`, `area:native`, `area:ci` |
| I-02 | [#170](https://github.com/sebllll/VL.Qt/issues/170) | 1 | Wave 1: node-API snapshot and the tests the refactoring needs | W1-04, W1-02, W1-03, W1-05, W1-06, W1-09, W1-10, W1-11 | `enhancement`, `area:csharp`, `area:native` |
| I-03 | [#171](https://github.com/sebllll/VL.Qt/issues/171) | 1 | Wave 1: confirm or drop the suspected defects; measurements | W1-14, W1-15, W1-16, W1-17, W1-18 | `question`, `area:csharp`, `area:native`, `area:patch`, `manual-check` |
| I-04 | [#172](https://github.com/sebllll/VL.Qt/issues/172) | 3 | Wave 3: repository-wide mechanical steps (private, issue refs, feedback, node and wire folders) | W3-01, W3-02, W3-03, W3-04, W3-05 | `enhancement`, `area:csharp`, `area:native` |
| I-05 | [#173](https://github.com/sebllll/VL.Qt/issues/173) | 3 | Wave 3: help-patch mapping script, glossary, docs/design | W3-06, W3-07, W3-08 | `enhancement`, `area:patch` |
| I-06 | [#174](https://github.com/sebllll/VL.Qt/issues/174) | 3 | Wave 3: the native break in seven pull requests | W3-09, W3-10, W3-11, W3-12, W3-13, W3-14, W3-15 | `enhancement`, `area:native`, `area:csharp` |
| I-07 | [#175](https://github.com/sebllll/VL.Qt/issues/175) | 4 | Wave 4: cut QtNative into folders (pure moves) | W4-01, W4-02, W4-03, W4-04, W4-05, W4-06, W4-07, W4-08, W4-09 | `enhancement`, `area:native`, `area:ci` |
| I-08 | [#176](https://github.com/sebllll/VL.Qt/issues/176) | 4 | Wave 4: cut the C# runtime, reconciler, interop, QML and factories (pure moves) | W4-10, W4-11, W4-12, W4-13, W4-14, W4-15 | `enhancement`, `area:csharp` |
| I-09 | [#177](https://github.com/sebllll/VL.Qt/issues/177) | 4 | Wave 4: the cross-cutting chain (imports, categories, IElement, node core, messages, Enabled) | W4-16, W4-17, W4-18, W4-19, W4-20, W4-21 | `enhancement`, `area:csharp`, `area:native`, `area:patch`, `manual-check` |
| I-10 | [#178](https://github.com/sebllll/VL.Qt/issues/178) | 4 | Wave 4, lane N-app: app, window and platform of QtNative | W4-22, W4-23, W4-24, W4-25, W4-26, W4-27, W4-28, W4-29 | `enhancement`, `area:native`, `area:csharp` |
| I-11 | [#179](https://github.com/sebllll/VL.Qt/issues/179) | 4 | Wave 4, lane N-scene: commands, events and the scene of QtNative | W4-30, W4-31, W4-32, W4-33, W4-34, W4-35, W4-36 | `bug`, `area:native` |
| I-12 | [#180](https://github.com/sebllll/VL.Qt/issues/180) | 4 | Wave 4, lane N-elements: the native elements | W4-37, W4-38, W4-39, W4-40, W4-41, W4-42, W4-43 | `bug`, `area:native` |
| I-13 | [#181](https://github.com/sebllll/VL.Qt/issues/181) | 4 | Wave 4, lane N-effects: effect chain, shader item, user QML, style | W4-44, W4-45, W4-46, W4-47, W4-48, W4-49, W4-50, W4-51 | `enhancement`, `area:native`, `area:csharp`, `topic:effects` |
| I-14 | [#182](https://github.com/sebllll/VL.Qt/issues/182) | 4 | Wave 4, lane N-resources: images, snapshots, shaders, audio tap | W4-52, W4-53, W4-54, W4-55, W4-56, W4-57, W4-58, W4-59, W4-60 | `bug`, `area:native`, `area:csharp`, `topic:images` |
| I-15 | [#183](https://github.com/sebllll/VL.Qt/issues/183) | 4 | Wave 4, lane N-device: device, offscreen scenes, render threads | W4-61, W4-62, W4-63, W4-64, W4-65, W4-66, W4-67 | `bug`, `area:native`, `area:csharp` |
| I-16 | [#184](https://github.com/sebllll/VL.Qt/issues/184) | 4 | Wave 4, lane N-diagnostics: perf statistics and overlays | W4-68, W4-69, W4-70, W4-71, W4-72, W4-73 | `bug`, `area:native`, `area:csharp`, `topic:performance` |
| I-17 | [#185](https://github.com/sebllll/VL.Qt/issues/185) | 4 | Wave 4, lane C-reconciler: reconciliation and the scene registry | W4-74, W4-75, W4-76, W4-77, W4-78, W4-79, W4-80, W4-81, W4-82, W4-83 | `bug`, `area:csharp` |
| I-18 | [#186](https://github.com/sebllll/VL.Qt/issues/186) | 4 | Wave 4, lane C-runtime: app, pump, renderer node, PerfStats | W4-84, W4-85, W4-86, W4-87, W4-88, W4-89 | `enhancement`, `area:csharp`, `area:native`, `area:patch`, `manual-check` |
| I-19 | [#187](https://github.com/sebllll/VL.Qt/issues/187) | 4 | Wave 4, lane C-elements: the element model | W4-90, W4-91, W4-92 | `enhancement`, `area:csharp`, `area:native`, `area:patch` |
| I-20 | [#188](https://github.com/sebllll/VL.Qt/issues/188) | 4 | Wave 4, lane C-nodes: element, widget and image nodes | W4-93, W4-94, W4-95, W4-96, W4-97, W4-98, W4-99 | `enhancement`, `area:csharp`, `area:patch`, `manual-check` |
| I-21 | [#189](https://github.com/sebllll/VL.Qt/issues/189) | 4 | Wave 4, lane C-chains: effect, interaction and path nodes | W4-100, W4-101, W4-102, W4-103, W4-104, W4-105, W4-106, W4-107, W4-108, W4-109 | `enhancement`, `area:csharp`, `area:native`, `area:patch`, `manual-check` |
| I-22 | [#190](https://github.com/sebllll/VL.Qt/issues/190) | 4 | Wave 4, lane C-files: QML and shader files and their node factories | W4-110, W4-111, W4-112, W4-113, W4-114, W4-115, W4-116, W4-117, W4-118, W4-119, W4-120 | `enhancement`, `area:csharp`, `area:native`, `area:patch` |
| I-23 | [#191](https://github.com/sebllll/VL.Qt/issues/191) | 4 | Wave 4, lane C-values: styles, images and paths | W4-121, W4-122, W4-123, W4-124, W4-125, W4-126 | `enhancement`, `area:csharp`, `area:patch` |
| I-24 | [#192](https://github.com/sebllll/VL.Qt/issues/192) | 4 | Wave 4, lane companions: VL.Qt.Stride and VL.Qt.Audio | W4-127, W4-128, W4-129 | `enhancement`, `area:csharp`, `area:patch`, `manual-check` |
| I-25 | [#193](https://github.com/sebllll/VL.Qt/issues/193) | 4 | Wave 4: closing check and the look at every help patch | W4-130, W4-131 | `enhancement`, `area:patch`, `area:ci`, `manual-check` |
| I-26 | [#194](https://github.com/sebllll/VL.Qt/issues/194) | 5 | Wave 5: test hooks out of the library; test folders | W5-01a, W5-01b, W5-02, W5-06 | `enhancement`, `area:csharp`, `area:native` |
| I-27 | [#195](https://github.com/sebllll/VL.Qt/issues/195) | 5 | Wave 5: patch checker on node-api.txt, Stride tests, tests/ and the Probe | W5-03, W5-04, W5-05 | `enhancement`, `area:csharp`, `area:patch`, `area:ci` |
| I-28 | [#196](https://github.com/sebllll/VL.Qt/issues/196) | 6 | Wave 6: user and contributor pages | W6-01, W6-02, W6-03, W6-04, W6-05, W6-06, W6-07, W6-08, W6-09, W6-10 | `documentation` |
| I-29 | [#197](https://github.com/sebllll/VL.Qt/issues/197) | 6 | Wave 6: design notes | W6-11, W6-12, W6-13, W6-14, W6-15, W6-16, W6-17, W6-18, W6-19 | `documentation`, `area:native`, `area:csharp` |
| I-30 | [#198](https://github.com/sebllll/VL.Qt/issues/198) | 7 | Release 0.10.0 | W7-01 | `enhancement`, `area:ci` |

## 3 Waves

Columns: **Dep.** other issues that must be merged first (inside a wave-4 lane the row above is implied); **Eff.** S, M, L as in the brief; **Patches** see section 2; **WSL** the WSL build is part of the gate (P17); **Verifies** the machine whose verifier runs the gate (section 4).

### 3.1 Wave 1 – safety net

W1-01 runs first and alone (F5: new tests are written once, in the NUnit project). The C# tests W1-02, W1-03, W1-04, W1-05, W1-06 can start at once, since they live in `VL.Qt.Tests`. After W1-01 everything else runs in parallel. CI checkpoint at the end.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W1-01 | Convert the SmokeTest to NUnit | TEST-11, TEST-10, TEST-12 | F5; C1 (runner options become tests, filters, parameters); *TEST-11 is overturned by F5 and is cut as the decided thing. Acceptance: same scenario names and checks, equal PNGs* | — | L | `enhancement`, `area:csharp`, `area:ci` | — | yes | x64 |
| W1-02 | Cover the reconciler paths that have no test | REC-16 | *brings the throwing sink that the C# confirm issue uses* | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W1-03 | Add a robustness net for the QML parser | QML-12 | *parser net only; the hook part goes to TEST-07* | — | S | `enhancement`, `area:csharp` | — | — | arm64 |
| W1-04 | Add the node-API snapshot of the whole public surface | TEST-01 | P6 scope; F6 | — | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W1-05 | Print names instead of numeric ids in RecordingSink | TEST-05 | *judgement call: its Decided line assumes it lands before the break* | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W1-06 | Test the pin parity of the two Video nodes and TexturePixelMapping | COMP-08, COMP-09 | — | — | S | `enhancement`, `area:csharp` | — | — | arm64 |
| W1-07 | Put the timing limits into a warn-only Perf category | TEST-09 | F4; P16; *judgement call: in wave 1 because P16 makes the local gate warn-only for Perf* | W1-01 | M | `enhancement`, `area:csharp`, `topic:performance` | — | — | arm64 |
| W1-08 | Take the perf baseline on the NUnit runner | — | A4/P8 baseline | W1-01 | S | `question`, `area:native`, `area:csharp`, `topic:performance` | — | — | x64 |
| W1-09 | Add decoder robustness tests for the entry-based commands | NCMD-15 | — | W1-01 | M | `enhancement`, `area:native`, `area:csharp` | — | — | x64 |
| W1-10 | Test that every element table property resolves and takes its default | NELM-12 | — | W1-01 | M | `enhancement`, `area:native`, `area:csharp` | — | — | x64 |
| W1-11 | Test the native shader, style and sampler logic | NFX-23 | — | W1-01 | M | `enhancement`, `area:native`, `area:csharp`, `topic:effects` | — | — | x64 |
| W1-12 | Compare grabbed PNGs with WARP reference images | TEST-03 | P9: WARP check on both machines, references made on x64 | W1-01 | M | `enhancement`, `area:csharp`, `area:native` | — | — | both |
| W1-13 | Record command traces from the help patches and replay them | TEST-04 | C3 (VLQT_TRACE_COMMANDS stays); *re-recorded after the wire-format step of wave 3* | W1-12 | M | `enhancement`, `area:csharp`, `area:native` | — | — | x64 |
| W1-14 | Confirm or drop the suspected C# defects | — | confirms REC-01, SIP-03, NODW-03 | W1-02, W1-01 | S | `question`, `area:csharp` | — | — | arm64 |
| W1-15 | Confirm or drop the suspected native defects | — | confirms CAPI-01, NAPP-10, QML-06, NCMD-17, NELM-14, NDIAG-01, NDIAG-06 | W1-01 | M | `question`, `area:native` | — | — | x64 |
| W1-16 | Confirm or drop the audio suspicions; measure the latency on x64 | — | confirms COMP-03, COMP-04; C19 (x64 measurement) | W1-01 | S | `question`, `area:csharp`, `area:native`, `topic:video` | — | — | x64 |
| W1-17 | Measure the Clip grab and the video texture upload | — | C16 (Clip grab for NODE-02); E8 (for NRES-02) | W1-01 | S | `question`, `area:native`, `topic:performance` | — | — | x64 |
| W1-18 | Check include-based pin help and the IElement link tooltip in vvvv | — | D1; A6 | — | S | `question`, `area:csharp`, `area:patch`, `manual-check` | — | — | Sebastian in vvvv |
| W1-19 | Make tools/test-linux.sh green: clear-pin under OpenGL offscreen | — | local task tc6w: 7 `clear-pin` checks fail under WSL (offscreen, OpenGL), timing check 361 to be re-run alone; *must be merged before W3-09, since every pull request of the break has a local checkpoint with `tools/test-linux.sh`* | W1-01 | M | `bug`, `area:native`, `topic:linux` | — | yes | x64 |

### 3.2 Wave 2 – API on paper

Done: QUESTIONS.md, the catalog of 23_api.md section 7, the shader comparison, DECISION-ROUND.md and TARGET.md are decided. No issues.

### 3.3 Wave 3 – mechanical steps and the native break

One after the other, in table order. The parallel group W3-06, W3-07, W3-08 touches no file of the others and may run at any time in wave 3; W3-06 must be merged before the first issue that adds mapping rows (W4-16). The break W3-09–W3-15 follows P4: checks, test exports out, return codes, names, wire format with the P8 measurement, then the caps.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W3-01 | Spell out private; drop the Hidden attribute on NodeContext | — | K2; K5 (D2) | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W3-02 | Remove the bare issue references from the sources | — | K1 (bare #NNN only; rewrites are wave 4) | W3-01 | M | `enhancement`, `area:csharp`, `area:native` | — | — | arm64 |
| W3-03 | Rename sink to feedback in C# | ELM-06 | A1; O7 (C# side) | W3-02 | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W3-04 | Move the node files into one folder per category | NODX-11 | S3; S1 rule 3 | W3-03 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W3-05 | Move the wire files into Wire/ | — | S3 | W3-04 | S | `enhancement`, `area:csharp` | — | — | arm64 |
| W3-06 | Add the help-patch mapping file and the migration script | — | P19; P20 | W1-04 | M | `enhancement`, `area:patch` | — | — | arm64 |
| W3-07 | Write docs/glossary.md from TARGET.md | DOC-09 | P3 | — | S | `documentation` | — | — | arm64 |
| W3-08 | Create docs/design/ and its template | DOCD-06 | G5; G8; *judgement call: early, because wave-4 issues already write design notes* | — | S | `documentation` | — | — | arm64 |
| W3-09 | Refuse a vlqt.dll of another ABI version | PROTO-01 | B3; B4; principle row "version mismatch" | W3-05, W1-19 | S | `bug`, `area:native`, `area:csharp` | — | yes | x64 |
| W3-10 | Check protocol.h and vlqt.h against the C# mirrors in unit tests | PROTO-08, CAPI-06 | D4; C1 | W3-09 | M | `enhancement`, `area:native`, `area:csharp` | — | yes | x64 |
| W3-11 | Move the test exports into vlqt_test.h and the testkit | CAPI-04, NAPP-09, TEST-08, PROTO-06, NDEV-09, NDEV-03, CAPI-05 | F1; F2; *NDEV-03 joins because TEST-08 puts test/test_device.* here (judgement call)* | W3-10 | L | `enhancement`, `area:native`, `area:csharp` | — | yes | x64 |
| W3-12 | One result table and a per-thread last error for every export | CAPI-01, NAPP-02, CAPI-02, NAPP-03, PROTO-07, NDEV-12 | B1; B2; B4; *also carries the poll convention of NCMD-04 and the error buffer of NRES-12 (B4); those findings are cut in their lanes* | W3-11 | L | `bug`, `area:native`, `area:csharp` | — | yes | x64 |
| W3-13 | Rename the C ABI and the protocol after the glossary | CAPI-03, NAPP-17, NELM-11, NDIAG-10, PROTO-10, CAPI-09 | A5; glossary 1.6 (O1, O5, O7, B3, B5, B8, B9); export signatures of E7 and E10; *B10 is left to SHD-07, which creates those types (judgement call)* | W3-12 | L | `enhancement`, `area:native`, `area:csharp` | — | yes | x64 |
| W3-14 | u32 lengths and no dead fields in the wire format | PROTO-02, PROTO-09, ELM-08, REC-13 | A4 with the P8 measurement; B5; *re-records the traces of wave 1* | W3-13 | L | `bug`, `area:native`, `area:csharp`, `topic:performance` | — | yes | x64 |
| W3-15 | Remove the caps and the chunking that u32 makes unnecessary | NCMD-08, PROTO-11 | principle row "size limit"; C18; *as written only if P8 kept u32 for commands; otherwise the P8 fallback applies* | W3-14 | M | `enhancement`, `area:native`, `area:csharp` | — | yes | x64 |

### 3.4 Wave 4 – refactoring per area

#### Cuts (pure moves)

W4-01 first and alone; then native round 1 (W4-02, W4-03, W4-04, W4-05) and round 2 (W4-06, W4-07, W4-08, W4-09), each after the cut it names; the C# cuts in parallel to the native ones, W4-10 before W4-11.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-01 | Make CMakeLists.txt one source per line; fix the RID default | NDEV-08 | — | W3-15 | S | `enhancement`, `area:native`, `area:ci` | — | yes | x64 |
| W4-02 | Cut vlqt.cpp into app/, window/ and platform/ | NAPP-01 | — | W4-01 | L | `enhancement`, `area:native` | — | yes | x64 |
| W4-03 | Cut commands, events and feedback into scene/ | NCMD-05 | O1; O3 | W4-01 | L | `enhancement`, `area:native` | — | yes | x64 |
| W4-04 | Cut resources.cpp into resources/ and name its parts | NRES-03, NRES-06, NRES-08 | B12 (pure move) | W4-01 | M | `enhancement`, `area:native`, `topic:images` | — | yes | x64 |
| W4-05 | Cut shadereffectitem.cpp into shader_item, shader_material, layer_requests | NFX-05, NFX-15, NFX-17 | — | W4-01 | M | `enhancement`, `area:native`, `topic:effects` | — | yes | x64 |
| W4-06 | Cut perf.cpp into diagnostics/ | NDIAG-04, NDIAG-19 | — | W4-02 | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-07 | Cut offscreen.cpp into offscreen/ around OffscreenScene | NDEV-04 | O2 | W4-02 | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-08 | Cut elements.cpp into elements/ | NELM-05, NELM-10 | — | W4-03, W4-05 | L | `enhancement`, `area:native` | — | yes | x64 |
| W4-09 | One struct per effect chain link, cut into effects/ | NFX-07 | — | W4-08 | M | `enhancement`, `area:native`, `topic:effects` | — | yes | x64 |
| W4-10 | Cut QtApp.cs and name the scene types | RUN-06, RUN-05 | O2 | W3-15 | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-11 | Replace the reconciler partial class by types with one job each | REC-04, REC-07, REC-05 | A3; O3 | W4-10 | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-12 | Split Native.cs into Interop/ | CAPI-07 | — | W3-15 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-13 | Rewrite the Effect and element summaries (ELM-01, ELM-12); cut the multi-type element files | ELM-01, ELM-12, ELM-11 | O4; O5; C2 text | W3-15 | M | `documentation`, `area:csharp` | — | — | arm64 |
| W4-14 | Cut QmlDeclarations.cs into Qml/ and Qml/Parsing/ | QML-02 | — | W1-03, W3-15 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-15 | One folder-node-factory layer for QML and shader files | QML-03, SHD-10 | D6; *the keep-last-good rule follows in C-files (SHD-05, QML-05), judgement call: a cut stays a move* | W3-15 | M | `enhancement`, `area:csharp` | — | — | arm64 |

#### The cross-cutting chain

One after the other; they change signatures every node issue builds on, and run while the native cuts run (TARGET 3.3). API-02 and the message mechanism join the chain (judgement calls, see notes).

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-16 | Import only the intended types, under Qt.* | API-01, ELM-04, RUN-13, API-15 | A6; catalog "all imported types", element classes, ElementSink … Geometry, Initialization, TooltipPreview, BlendMode/LabelPosition/ToggleLook; principle rows Interaction / Sink, Geometry nodes; *checks in vvvv that node categories stay absolute (S1)* | W3-06, W1-04, W4-10, W4-13 | L | `enhancement`, `area:csharp`, `area:patch`, `manual-check` | mapping rows | — | arm64 |
| W4-17 | Sort the categories by what the user looks for | API-02, API-09 | C11; C14; C15; catalog Categories, Qt.Images, Stride rows; *API-09 is overturned (names kept), its category move is done here. Judgement call: API-02 joins the chain because later API issues name its categories* | W4-16 | M | `enhancement`, `area:csharp`, `area:patch` | mapping rows | — | arm64 |
| W4-18 | One element type IElement on every pin; Element becomes internal | ELM-05 | C3; P5; catalog "IElement / Element on inputs", "Element.Type, Element.Sink" | W4-17 | L | `enhancement`, `area:csharp` | compile-check HowTo Patch a Stylable Widget; VL.Qt.HDE.vl edited directly | — | arm64 |
| W4-19 | Cut the element node down to its specific part | NODE-01, NODE-08 | O3 (ElementNodeCore); CommonInputs | W4-18 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-20 | One mechanism for messages at a node (RUN-01); no Error pins on Renderer, image and file nodes | RUN-01, NODE-05, NODW-04, RUN-12 | A1 of DECISION-ROUND; P10 (FileWriter Success); principle rows Renderer / Error, Load… / Error, FileWriter / Success; *judgement call: joins the chain after NODE-01, since every node lane reports through it* | W4-19 | L | `bug`, `area:csharp`, `area:patch`, `manual-check` | direct .vl where an Error pin is linked | — | arm64 |
| W4-21 | Add Enabled to every element node and to the generated nodes | — | C2; P11; catalog "(all elements) / Enabled"; principle rows generated QML / Enabled, generated shader / Enabled; protocol addition raises the version | W4-20 | L | `enhancement`, `area:csharp`, `area:native` | — | — | x64 |

#### Lanes

Fifteen lanes as in TARGET 3.3; inside a lane top to bottom, lanes in parallel. N-effects and N-resources share the shader item, C-nodes and C-values share `Images/`: where the Dep. column names an issue of the other lane, that one goes first.

##### N-app

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-22 | Give the scene handle a name, an owner and no test state | NAPP-07 | O2 | W4-02, W4-07 | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-23 | Split UpdateGate into pump gate and change counter; the debug overlay leaves the change check | NAPP-06, NDIAG-09 | — | W4-06 | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-24 | One guarded helper per export that states and checks its thread | NAPP-04, NAPP-13, NAPP-11 | — | — | L | `enhancement`, `area:native` | — | yes | x64 |
| W4-25 | One platform interface instead of #if blocks; the GL probe as a process | NAPP-08, NAPP-12, NAPP-16 | — | — | M | `enhancement`, `area:native`, `topic:linux` | — | yes | x64 |
| W4-26 | Animation clock: guard the input, document that QML animations do not rewind | NAPP-10 | E11; principle row QmlElement / animations when the clock jumps back; *the scrub part only if wave 1 confirms it* | — | S | `documentation`, `area:native` | — | — | x64 |
| W4-27 | List the private Qt API in one place; correct untrue comments | NAPP-05, NAPP-14 | D9 | — | M | `documentation`, `area:native` | — | — | x64 |
| W4-28 | Renderer pin Enable Keyboard Shortcuts | NDIAG-07 | C18; C5; principle row Renderer / Enable Keyboard Shortcuts | W4-06, W4-20 | M | `enhancement`, `area:native`, `area:csharp` | — | — | x64 |
| W4-29 | poll: collect once, then drain | NCMD-04 | — | W4-03 | S | `bug`, `area:native` | — | — | x64 |

##### N-scene

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-30 | Reset lastApplyError before the buffer check (NCMD-02); remove the false SET_UNIFORMS comment (NCMD-01) | NCMD-02, NCMD-01 | — | W4-03 | S | `bug`, `area:native` | — | — | x64 |
| W4-31 | A rejected SET_EFFECT_CHAIN parks nothing | NCMD-03, NCMD-17 | *NCMD-17 only if wave 1 confirms it* | W4-09 | M | `bug`, `area:native`, `topic:effects` | — | — | x64 |
| W4-32 | One bounds-checked wire reader in wire.h | NCMD-06, PROTO-12, NCMD-16 | — | W1-09 | M | `enhancement`, `area:native` | — | — | x64 |
| W4-33 | One op table instead of six places per command | NCMD-07, NCMD-13 | — | — | M | `enhancement`, `area:native` | — | — | x64 |
| W4-34 | Give HandlerEntry clear ownership | NCMD-09 | — | — | M | `enhancement`, `area:native`, `topic:input` | — | — | x64 |
| W4-35 | No per-frame allocation in poll and apply | NCMD-10 | — | — | S | `enhancement`, `area:native`, `topic:performance` | — | — | x64 |
| W4-36 | One creation point for ElementEntry | NCMD-12, NELM-15 | — | W4-08 | S | `enhancement`, `area:native` | — | — | x64 |

##### N-elements

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-37 | Group bounds from the child Group's own bounds | NELM-01, NELM-14 | O6 (itemBounds); C10; *NELM-14 only if wave 1 confirms it* | W4-08 | M | `bug`, `area:native` | — | — | x64 |
| W4-38 | One non-finite check; inPositioner flag; Matrix4x4 built in C++ | NELM-03, NELM-02, NELM-08 | — | — | M | `bug`, `area:native` | — | — | x64 |
| W4-39 | All facts about an element type in its table entry | NELM-04, NCMD-14 | — | — | M | `enhancement`, `area:native` | — | — | x64 |
| W4-40 | Built-in QML as .qml resources under qml/ | NELM-06, NFX-06 | D8; C16 | W4-09, W4-06 | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-41 | Typed setters for Rectangle and Text | NELM-07 | D9 | — | M | `enhancement`, `area:native`, `topic:performance` | — | — | x64 |
| W4-42 | Explicit ownership of components, GroupItem state and the debug counter | NELM-09 | — | — | S | `enhancement`, `area:native` | — | — | x64 |
| W4-43 | Row and Column alignment in C++, skipping hidden children | NELM-13 | E12; P11; principle row Row, Column / alignment | W4-21 | S | `enhancement`, `area:native` | — | — | x64 |

##### N-effects

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-44 | No allocation and no repaint for same-structure chain updates | NFX-01 | — | W4-09, W1-11 | M | `enhancement`, `area:native`, `topic:effects` | — | — | x64 |
| W4-45 | Shader item: merged mip requests, GUI-side texture delete, one destroyed connection | NFX-02, NFX-03, NFX-18 | E4 | W4-05 | M | `bug`, `area:native`, `topic:effects` | — | — | x64 |
| W4-46 | VlStyle: field ids from a Q_ENUM, m_ members, no side effect in fontAt | NFX-08, NFX-10, NFX-22 | — | W4-33 | M | `enhancement`, `area:native`, `topic:styles` | — | — | x64 |
| W4-47 | One list of reserved uniform names and an error at the node | NFX-14 | open row D8; shader row E24 | W4-20 | S | `enhancement`, `area:native`, `area:csharp`, `topic:effects` | — | — | x64 |
| W4-48 | Move the test shaders out of the built-in set | NFX-16 | — | — | S | `enhancement`, `area:native`, `topic:effects` | — | yes | x64 |
| W4-49 | User QML item: required properties and a fresh component after a change | QML-08, QML-06 | E3; *QML-06 only if wave 1 confirms it* | — | M | `bug`, `area:native` | — | — | x64 |
| W4-50 | User QML: point and size reach the patch as Vector2; guarded watcher ids | NFX-21, NFX-20 | open row D7 | — | M | `enhancement`, `area:native`, `area:csharp` | — | yes | x64 |
| W4-51 | Advance only the items whose resource changed | NRES-11 | — | W4-45 | S | `enhancement`, `area:native`, `topic:performance` | — | — | x64 |

##### N-resources

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-52 | Free window textures of offscreen scenes on their render thread | NRES-01 | E6; E7 (pool-2 run) | W4-04, W4-45 | M | `bug`, `area:native`, `topic:images` | — | yes | x64 |
| W4-53 | Main-thread slot table, no lock across GPU work, no stalled upload queue | NRES-04, NRES-10 | B12 | — | M | `enhancement`, `area:native`, `topic:images` | — | — | x64 |
| W4-54 | Split ImageResource; the upload mode becomes the hidden pin Upload | NRES-05 | E10; principle row LoadImage … / Upload | — | L | `enhancement`, `area:native`, `area:csharp`, `topic:images` | — | yes | x64 |
| W4-55 | Key the window texture cache by QRhi | NRES-07 | — | W4-51 | M | `enhancement`, `area:native`, `topic:images` | — | yes | x64 |
| W4-56 | Reuse textures for content replaced every frame | NRES-02 | *only if the wave-1 measurement shows more than about 1 ms per frame* | W1-17 | L | `enhancement`, `area:native`, `topic:video` | — | yes | x64 |
| W4-57 | image_writer: 16-bit PNG where it fits, aligned with QtNative | NRES-12 | open row D6 | — | S | `enhancement`, `area:native`, `topic:images` | — | — | x64 |
| W4-58 | Snapshot: release a given-up grab at once | NRES-13 | D9; C8 | — | S | `bug`, `area:native`, `topic:images` | — | — | x64 |
| W4-59 | Shader resource: one default-shader lookup, one bake, reflect thread, binding 0 | NFX-09, NFX-12, NFX-13, NFX-19 | C9 | — | M | `enhancement`, `area:native`, `topic:effects` | — | — | x64 |
| W4-60 | Audio tap: no lock in the read, one reader, an owned tap stays muted | COMP-02, COMP-03, COMP-12 | *COMP-03 only if wave 1 confirms it* | — | M | `bug`, `area:native`, `topic:video` | — | — | x64 |

##### N-device

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-61 | Give the threaded batch lists a stable address | NDEV-01 | E7 (pool-2 run in build.ps1 -Test) | W4-07 | S | `bug`, `area:native` | — | yes | x64 |
| W4-62 | One frame sequence for both thread modes; the pin Render Threads | NDEV-05 | E6; E7; P7; principle row QtTexture, QtRenderer / Render Threads; "Pins instead of environment variables" (sort every VLQT_* variable) | — | L | `enhancement`, `area:native`, `area:csharp`, `topic:performance` | — | yes | x64 |
| W4-63 | Retry a failed device creation; a device function instead of a dynamic property | NDEV-02, NDEV-06 | B8; principle row device retry | W4-55 | M | `bug`, `area:native` | — | yes | x64 |
| W4-64 | One COM ownership style with ComPtr | NDEV-07, NFX-11 | — | — | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-65 | Assert the frame lock and the context of the bound device | NDEV-11, NDEV-13 | — | — | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-66 | Device and offscreen headers state contracts; small names | NDEV-10, NDEV-14 | — | — | S | `documentation`, `area:native` | — | — | x64 |
| W4-67 | Polish an offscreen scene in the poll only when it needs it | NAPP-15 | — | W4-23 | S | `enhancement`, `area:native`, `topic:performance` | — | — | x64 |

##### N-diagnostics

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-68 | Count only Qt work as over budget, from one definition | NDIAG-01 | E13; C4; C7; principle row PerfStats / OverBudget; *only if wave 1 confirms it* | W4-06 | M | `bug`, `area:native`, `area:csharp`, `topic:performance` | — | — | x64 |
| W4-69 | Frame statistics from consecutive frames only | NDIAG-02 | — | — | M | `bug`, `area:native`, `topic:performance` | — | — | x64 |
| W4-70 | Scene page: every type by name, no animations row, no fixed font | NDIAG-03, NDIAG-13, NDIAG-17 | C6 | W4-39 | M | `bug`, `area:native` | — | — | x64 |
| W4-71 | One software-paint helper and one overlay geometry; dead members out | NDIAG-12, NDIAG-05, NDIAG-16 | — | — | M | `enhancement`, `area:native` | — | — | x64 |
| W4-72 | Renderer detection into device/renderer_info; a contract for monitor state | NDIAG-08, NDIAG-06 | *NDIAG-06 only if wave 1 confirms it* | — | M | `enhancement`, `area:native` | — | yes | x64 |
| W4-73 | Debug overlay: a cache per element instead of per-frame lookups | NDIAG-18 | — | W4-36 | M | `enhancement`, `area:native`, `topic:performance` | — | — | x64 |

##### C-reconciler

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-74 | Reset and resync when a reconcile throws | REC-01, REC-15 | B1; B7; principle row Renderer / exception inside the reconcile; *only if wave 1 confirms the leak* | W4-11, W4-20 | M | `bug`, `area:csharp` | — | — | arm64 |
| W4-75 | One message store per feedback slot that keeps every writer's message | REC-02 | — | W4-20 | M | `bug`, `area:csharp` | — | — | arm64 |
| W4-76 | Key an element by its feedback slot; split the registry | REC-06, REC-10 | A2; principle row identity of one node | — | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-77 | No allocation per visit for uniforms and effect chains | REC-08, REC-09 | — | — | M | `enhancement`, `area:csharp`, `topic:performance` | — | — | arm64 |
| W4-78 | Keep primary in one place | REC-11 | — | — | S | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-79 | Rebuild stylable masks; an explicit list of stylable types | REC-12, SIP-09 | C12 | — | M | `bug`, `area:csharp`, `topic:styles` | — | — | arm64 |
| W4-80 | FeedbackRouter: OR over occurrences, cleared on dispose | ELM-02, ELM-03 | B6; principle row Hover / Is Hovered … | — | M | `bug`, `area:csharp`, `topic:input` | — | — | arm64 |
| W4-81 | One owner per FeedbackState field | ELM-07 | — | W4-75 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-82 | Make the compound commands of CommandWriter impossible to misuse | PROTO-03 | — | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-83 | Write the reconciler design note | REC-03, DOCD-07 | C14; *REC-03 and DOCD-07 create the same page (judgement call: one issue, at the end of the lane)* | W3-08, W4-78 | M | `documentation`, `area:csharp` | — | — | arm64 |

##### C-runtime

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-84 | One owner of the animation driver time | RUN-02 | B6 | W4-26 | S | `bug`, `area:csharp`, `area:native` | — | — | arm64 |
| W4-85 | QtLog follows the logger factory of the app host | RUN-03 | — | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-86 | Renderer.Update without allocation; WindowSettings keeps Icon and Bounds | RUN-04, NDIAG-15 | C1; C17; catalog Renderer / Title, Borderless, Fullscreen | — | M | `enhancement`, `area:csharp`, `area:patch`, `topic:performance`, `manual-check` | direct .vl (Title, Borderless, Fullscreen move to the Renderer) | — | arm64 |
| W4-87 | PerfStats as grouped record structs | RUN-10, NDIAG-11 | C17; D3; D4; open row PerfStats names; principle row PerfStats / Split | W4-68 | M | `enhancement`, `area:csharp`, `area:patch`, `topic:performance`, `manual-check` | direct .vl (HowTo Read Performance) | — | arm64 |
| W4-88 | Renderer node: Bounds not dirty on open, errors in Release, exported-app lifetime in one place | RUN-07, RUN-11, RUN-08, RUN-09 | — | — | M | `bug`, `area:csharp` | — | — | arm64 |
| W4-89 | Copy the apply-error reason when it is read | CAPI-10 | — | W4-12 | S | `bug`, `area:csharp` | — | — | arm64 |

##### C-elements

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-90 | Take wire and reconciler code out of the element model | ELM-09, ELM-14, QML-07 | A3 | W4-13, W4-11 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-91 | A Container base class; Children off Element | ELM-10 | — | W4-18 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-92 | One FillMode end to end; Font Weight as int; Stride's MultisampleCount | API-10, NRES-15 | catalog Image, Video / Fill Mode, FontWeight, QtTexture / Multisample Count; protocol addition | W4-17 | M | `enhancement`, `area:csharp`, `area:native`, `area:patch`, `topic:images` | mapping rows + direct .vl | — | x64 |

##### C-nodes

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-93 | Correct the wrong pin docs (NODE-02); write the common ones once | NODE-02, NODE-03, NODW-09 | D1; C16; P11 | W1-17, W1-18, W4-21 | M | `documentation`, `area:csharp` | — | — | arm64 |
| W4-94 | Default values follow D3, in one place | NODE-04, NODW-11 | D3; A2 (Matrix check in vvvv) | — | M | `enhancement`, `area:csharp`, `manual-check` | — | — | arm64 |
| W4-95 | Button: its own click channel, On Click after Anchor | NODE-07, NODW-02 | catalog Button / Is Clicked; open row D14; *NODW-02 is overturned (pin On Click, not Channel); cut as the decided thing* | — | S | `enhancement`, `area:csharp`, `area:patch`, `topic:input` | mapping rows | — | arm64 |
| W4-96 | The channel is the truth of a widget | NODW-01 | C9; principle row Slider, Input (Int) / Value | — | S | `bug`, `area:csharp`, `topic:input` | — | — | arm64 |
| W4-97 | Size optional after Auto Size; the common pin set and its order | API-04, API-03 | catalog Size, Style, Children → Input, Labeled and QmlElement order rows | W4-21 | L | `enhancement`, `area:csharp`, `area:patch` | mapping rows | — | arm64 |
| W4-98 | Catalog renames of shapes, widgets and image nodes: Fill Color, Input (String), Slider (Float), Input first | API-05, API-07, NODW-08, API-12, NODW-05 | C4; C8; open row D15; catalog Color / Fill, TextInput, Slider, Snapshot and FileWriter (Element) / Element rows; *NODW-05 is overturned (name kept); its output rename D15 is done here* | — | M | `enhancement`, `area:csharp`, `area:patch` | mapping rows; Help.xml | — | arm64 |
| W4-99 | Image nodes: one QtImage per ToQtImage, no stale image, no per-frame error text | NODW-06, NODW-07, NODW-13 | — | W4-20 | M | `bug`, `area:csharp`, `topic:images` | — | — | arm64 |

##### C-chains

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-100 | Angles in cycles on DirectionalBlur and RadialBlur, in nodes, shaders and templates | NODX-01, API-06 | C10; shader row SREF-03 | — | S | `bug`, `area:csharp`, `area:native`, `area:patch`, `topic:effects`, `manual-check` | direct .vl (values) | — | arm64 |
| W4-101 | Every effect length in DIP: @padding (SHD-01), shader effects and merged effects | SHD-01 | E1; A5; B11; shader rows SREF-01 (E2 to E5, E8 to E14) | W4-100 | L | `bug`, `area:csharp`, `area:native`, `area:patch`, `topic:effects`, `manual-check` | direct .vl (values) | — | arm64 |
| W4-102 | One ShaderEffect type and one builder for built-in and file shaders | SHD-09, NODX-12 | O5; A3; principle row generated shader effect nodes / Error | W4-101 | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-103 | Effect pin names after VL.Skia: Max Radius, Radius Multiplier, Blur Amount, Blend Mode | API-13 | A4; shader rows SREF-02, SREF-05 | — | S | `enhancement`, `area:csharp`, `area:patch`, `topic:effects` | mapping rows | — | arm64 |
| W4-104 | Optional Apply on every effect node; ColorAdjust Saturation on HSCB's scale | — | shader rows SREF-06 (E16), SREF-04 (E6, E7) | — | M | `enhancement`, `area:csharp`, `area:patch`, `topic:effects`, `manual-check` | direct .vl (Saturation values) | — | arm64 |
| W4-105 | One memo type for all chain nodes; one content comparison for spreads | NODX-05, NODX-06 | C17 | — | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-106 | Interaction core: one set of channel writers, the input error, no overwrite of a fresh Delta | NODX-03, NODX-04, NODX-09 | — | — | M | `bug`, `area:csharp`, `topic:input` | — | — | arm64 |
| W4-107 | One vocabulary for event and state pins; TouchPoint as a struct | API-08, NODX-02, ELM-13 | C13; catalog widget and interaction event rows; open rows D5, D16, D17; principle row Pinch / Is Active | — | M | `enhancement`, `area:csharp`, `area:patch`, `topic:input` | mapping rows | — | arm64 |
| W4-108 | Defaults and docs of the effect, interaction and path nodes | NODX-07, NODX-08 | D3 | W4-94 | M | `enhancement`, `area:csharp` | — | — | arm64 |
| W4-109 | Path pin names agree with Skia and with each other | API-11, NODX-13 | C12; catalog Qt.Paths rows; open row D18 | — | S | `enhancement`, `area:csharp`, `area:patch` | mapping rows | — | arm64 |

##### C-files

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-110 | QML parser: skip regex literals (QML-01); self-root and redeclaration; messages with lines | QML-01, QML-09, QML-10 | — | W4-14 | M | `bug`, `area:csharp` | — | — | arm64 |
| W4-111 | QmlElement and the generated QML nodes: one table of common pins | QML-04, QML-11 | open row D9; principle rows QmlElement / Error, generated QML / Error, generated QML / Size | W4-97 | M | `enhancement`, `area:csharp`, `area:patch` | direct .vl where Error is linked | — | arm64 |
| W4-112 | Split ShaderNodeLayout; names after the node | SHD-07, SHD-11 | B10; B11; O5 | W4-15 | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-113 | Shader element pins in one list, made unique against the right set | SHD-08, SHD-03 | principle rows Shader / Error, generated shader nodes / Error, generated shader nodes / Size | W4-21 | M | `enhancement`, `area:csharp`, `area:patch`, `topic:effects` | direct .vl where Error is linked | — | arm64 |
| W4-114 | Read and bake each shader file once | SHD-06 | — | — | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-115 | Keep the last good pins while a QML or shader file does not load | SHD-05, QML-05 | E2; C11; principle rows generated QML and shader nodes / pins while the file fails | — | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-116 | No false "set more than once" message on image pins | SHD-02 | — | — | S | `bug`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-117 | Shader factory: @category takes effect at once, relative categories, duplicates detected | SHD-04, SHD-15 | shader row SREF-09 (E19) | — | M | `bug`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-118 | Shader area: dead weight out, messages that name the type, current XML docs | SHD-13, SHD-14, SHD-16 | principle row Shader / Fragment Code default text | — | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-119 | Shader file annotations after TextureFX: @optional, @tags, @remarks, pin names | — | shader rows SREF-07 (E17), SREF-08 (E18), SREF-12 (E21) | — | M | `enhancement`, `area:csharp`, `topic:effects` | — | — | arm64 |
| W4-120 | texelSize, dpr and viewSize on the Shader element; RGBSplit template in DIP | — | shader rows SREF-10 (E22), SREF-11 (E23); closes #28 | W4-45, W4-101 | M | `enhancement`, `area:csharp`, `area:native`, `topic:effects` | — | — | x64 |

##### C-values

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-121 | Style part records carry their merge and writer; Style shows its content | SIP-08, SIP-16 | — | W4-79 | M | `enhancement`, `area:csharp`, `topic:styles` | — | — | arm64 |
| W4-122 | Style nodes: Set… setters and plain getters, six cached bundle process nodes, help in DIP | SIP-01, SIP-10 | C5; C6; C13; P5 text; principle rows Qt.Style (bundles, singles, State → States, process nodes, Font Size in DIP) | — | L | `enhancement`, `area:csharp`, `area:patch`, `topic:styles` | mapping rows (setter rename before getter rename) | — | arm64 |
| W4-123 | Paths follow Skia: AddRoundRect, AddArc, current point, join start, spline ranges | SIP-02, SIP-11, SIP-12, SIP-15 | C7; open rows D10, D11, D12; principle row AddRoundRect radii | — | M | `bug`, `area:csharp` | — | — | arm64 |
| W4-124 | Split ResourceClient.cs; one byte-array image; every failed start settles; no user code in Poll | SIP-05, SIP-04, SIP-13, SIP-03 | *SIP-03 only if wave 1 confirms it* | W4-12 | M | `enhancement`, `area:csharp`, `topic:images` | — | — | arm64 |
| W4-125 | ImageResource becomes QtResource; QtImage says it was released | SIP-06, SIP-14 | open row D13 | — | M | `enhancement`, `area:csharp`, `topic:images` | — | — | arm64 |
| W4-126 | Snapshot grab in its own file; FileWriter (Element) writes the newest grab | NODW-12, NODW-03 | *NODW-03 only if wave 1 confirms it* | W4-99 | S | `bug`, `area:csharp`, `topic:images` | — | — | arm64 |

##### companions

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-127 | QtTexture and QtRenderer: no Perf and Error pins, one target per frame | COMP-06, COMP-05 | open rows D1, D2; principle rows QtTexture / Error, QtRenderer / Error | W4-20, W4-10 | M | `enhancement`, `area:csharp`, `area:patch`, `manual-check` | direct .vl (VL.Qt.Stride/help) | — | x64 |
| W4-128 | Name the seam between VL.Qt and its companions; NotificationQueue to Scene/; QtTextureRenderer cleanup | COMP-07, COMP-14, COMP-10, COMP-11 | D7 | W4-11 | M | `enhancement`, `area:csharp` | — | — | x64 |
| W4-129 | Video (VL.Audio): latency of the running stream, explicit engine state, one version | COMP-04, COMP-13, COMP-15 | C19; principle row Video (VL.Audio) / aspect; *COMP-04 only if wave 1 confirms it* | W1-16 | M | `bug`, `area:csharp`, `topic:video` | — | — | x64 |

#### Closing

After all lanes. W4-130 adds the check script of P14 to the gate (no `#NNN` in `src`, no comment line over 160 characters, no file with two types besides the named exceptions) and fixes what is left. W4-131 is the one look at all help patches in vvvv (P19), together with #144.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W4-130 | Add the closing check script to the gate and fix what it finds | — | P14 (K1, K3, K4) | all lanes (W4-22 to W4-129) | M | `enhancement`, `area:csharp`, `area:native`, `area:ci` | — | — | x64 |
| W4-131 | Look at every help patch in vvvv; at least one help flag per node | — | P19; P20; joins #144 | W4-130 | M | `enhancement`, `area:patch`, `manual-check` | all patches; Help.xml | — | Sebastian in vvvv |

### 3.5 Wave 5 – tests

W5-01a and then W5-01b first (they touch about 45 files of every lane, so they wait for wave 4); the others in parallel.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W5-01a | Remove the test hooks from the C# library (TEST-07, part C#) | TEST-07, REC-14, RUN-14, NODE-06, SHD-12, SIP-07, NODW-10, NODX-10 | K6; P22; A1 of DECISION-ROUND (INodeMessages only if needed); *two PRs, C# and native (Sebastian, 2026-11-20)* | — | L | `enhancement`, `area:csharp`, `area:native` | — | — | arm64 |
| W5-01b | Remove the test hooks from QtNative (TEST-07, part native) | NRES-09, NDIAG-14 | K6; P22; the native half of TEST-07 | W5-01a | M | `enhancement`, `area:native` | — | yes | x64 |
| W5-02 | Mirror the library folders in the test projects | TEST-13 | — | W5-01a | L | `enhancement`, `area:csharp` | — | — | arm64 |
| W5-03 | The patch checker reads node-api.txt and runs at the checkpoints | TEST-02 | C2 of DECISION-ROUND (vl_pin_catalog.py goes); P18 | — | M | `enhancement`, `area:patch`, `area:ci` | — | — | arm64 |
| W5-04 | Automated tests for the Stride layer | TEST-06 | *judgement call: wave 5, since the wave-1 list does not name it* | — | M | `enhancement`, `area:csharp` | — | — | x64 |
| W5-05 | tests/ is no test folder; VL.Qt.Probe against the SmokeTest | TEST-14 | — | — | S | `enhancement`, `area:csharp`, `area:ci` | — | yes | x64 |
| W5-06 | A test for LoadFont | NODW-14 | — | W5-01a | S | `enhancement`, `area:csharp` | — | — | arm64 |

### 3.6 Wave 6 – documentation

The glossary (DOC-09) and the design folder (DOCD-06) moved forward into wave 3 (P3; W3-07, W3-08). DOC-10 was decided with TARGET.md; its edits of the pages run here (W6-01), with the pages. W6-19 runs last, after every design note exists. Parallel otherwise, with the dependencies listed.

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W6-01 | One name per concept and no issue numbers in the pages | DOC-10, DOC-11 | TARGET 1.2; O4; C15; *DOC-10 was decided in TARGET.md (P3); its page edits run here, with the pages* | — | M | `documentation`, `area:native`, `area:csharp` | — | — | arm64 |
| W6-02 | Fix architecture.md: render thread, offscreen scenes, missing subsystems | DOC-01 | — | — | M | `documentation`, `area:native`, `area:csharp` | — | — | arm64 |
| W6-03 | development.md; measurement history into docs/research | DOC-02, DOC-13, DOCD-13 | G8; C1 | — | M | `documentation` | — | — | arm64 |
| W6-04 | Stale user-facing facts (DOC-04) and small corrections | DOC-04, DOC-15 | — | — | S | `documentation`, `area:patch` | — | — | arm64 |
| W6-05 | Rewrite the VL.Qt.Stride README (COMP-01); add docs/stride.md | COMP-01, DOC-07 | — | — | M | `documentation`, `area:csharp` | — | — | arm64 |
| W6-06 | README as an entry page | DOC-06 | — | W6-05 | M | `documentation` | — | — | arm64 |
| W6-07 | Separate the audiences; an index by audience; plans and reviews leave docs/ | DOC-12, DOC-16 | G3; G6 | — | M | `documentation` | — | — | arm64 |
| W6-08 | One home per fact | DOC-05 | — | W6-06, W6-07 | M | `documentation` | — | — | arm64 |
| W6-09 | AGENTS.md and CONTRIBUTING.md: current rules (DOC-03), newcomer first | DOC-03, DOC-14 | D2; D3; G2; F5; P14 | W6-08 | M | `documentation` | — | — | arm64 |
| W6-10 | Walkthrough: add an element through all layers | DOC-08 | — | — | M | `documentation`, `area:native`, `area:csharp` | — | — | arm64 |
| W6-11 | Element model note to the present pin set | DOCD-01, API-14 | C2; P11; *API-14 rewrites the same page* | — | S | `documentation`, `area:csharp` | — | — | arm64 |
| W6-12 | Events note around the Interaction nodes | DOCD-02 | — | — | S | `documentation`, `area:csharp`, `area:native` | — | — | arm64 |
| W6-13 | One paths design note (DOCD-03); the finished plan and the gradients handover go | DOCD-03, DOCD-04, DOCD-14 | G6; G7 (#155 holds the follow-ups) | — | M | `documentation`, `area:csharp` | — | — | arm64 |
| W6-14 | render-texture.md into design notes and research; one GPU device note | DOCD-05, DOCD-09 | — | — | M | `documentation`, `area:native` | — | — | arm64 |
| W6-15 | Design notes: timing, user QML, Qt bridge | DOCD-08, DOCD-11, DOCD-12 | — | — | M | `documentation`, `area:native`, `area:csharp` | — | — | arm64 |
| W6-16 | Native boundary: conventions block in vlqt.h, boundary and deployment notes | DOCD-10, CAPI-08 | — | — | M | `documentation`, `area:native` | — | — | x64 |
| W6-17 | protocol.h and Protocol.cs: wire format only, semantics in docs/protocol.md | PROTO-04, PROTO-05 | — | — | M | `documentation`, `area:native`, `area:csharp` | — | — | x64 |
| W6-18 | Design notes from the native headers: effect internals, resources, input | NFX-04, NRES-14, NCMD-11 | C14 | — | L | `documentation`, `area:native` | — | — | x64 |
| W6-19 | Point the code's links to the design notes | DOCD-15 | — | — | M | `documentation`, `area:csharp`, `area:native` | — | — | x64 |

### 3.7 Wave 7 – release

| Id | Title | Findings | Implements, notes | Dep. | Eff. | Labels | Patches | WSL | Verifies |
|---|---|---|---|---|---|---|---|---|---|
| W7-01 | Release 0.10.0 | — | P15/K7 (VersionPrefix 0.10.0); P18 (workflow_dispatch, all four RIDs); refactor → develop as merge commit; release notes from the catalog fix rows | — | M | `enhancement`, `area:ci` | — | — | x64 |

## 4 Machines (P16)

| Machine | Verifies | Reason |
|---|---|---|
| x64 workstation (win-x64) | wave 3 from W3-09 on (the break); every native cut and the seven N-lanes; companions; the native and timing issues of wave 1 (W1-08, W1-09, W1-10, W1-11, W1-13, W1-15, W1-16, W1-17); the reference images; W4-21, W4-92, W4-120; the docs that edit native headers | WSL Ubuntu-24.04 with Qt 6.12 and a working `tools/test-linux.sh` exist only here, and P17 puts the WSL build into the gate of every native cut and of the break; references are made here (P9); the P8 comparison needs one machine for before and after, the same as the baseline; VL.Qt.Audio must be measured on x64 (C19). 92 issues. |
| arm64 notebook (win-arm64) | the C# cuts, the chain except W4-21, the seven C-lanes, the mechanical C# steps of wave 3, the mapping script, the C# tests of wave 1, most of waves 5 and 6 | needs no WSL and no reference set of its own unless the P9 check of W1-12 says so. 95 issues; they are smaller on average than the native ones. |
| both | W1-12, and the local checkpoint after every pull request of the break | P9 compares WARP on both machines; P18. |
| Sebastian in vvvv | W1-18, W4-131 | `manual-check`: what only vvvv shows. |

Mixed issues with a small native part that touches neither CMake nor an `#if` block (W4-84, W4-100, W4-101) are verified on arm64; the gate builds QtNative there too.

> [!NOTE]
> The arm64 notebook has WSL with Qt for Linux (Sebastian, 2026-11-20), but is not available at the moment. Until it is, the x64 workstation verifies every task. Once it is back, the split above holds, and native lane tasks that add no file to `CMakeLists.txt` and touch no `#if` block (most of N-scene, N-elements and N-effects) may move to arm64 to shorten the critical path; the local checkpoints of the break then run `tools/test-linux.sh` on both machines. `tools/test-linux.sh` is red today in `clear-pin`; W1-19 makes it green before W3-09.

## 5 Open GitHub issues (P21)

24 open issues on 2026-11-19 (`gh issue list`), read from the titles; only #21 was opened, since its title does not say what it holds. Rule: into `0.10.0` only what the refactoring does anyway or what would touch the same files twice.

| Issue | Milestone | Title | 0.10.0 | Reason |
|---|---|---|---|---|
| #144 | 1.0 | Help flags: at least one per node | moved | joins W4-131 in #193, the look at every help patch at the end of wave 4 (P21) |
| #28 | 3.0 | ShaderEffect: provide the pixel size of samplers as a uniform and use DIP for lengths | moved | done by W4-120 (#190) (`texelSize`, `dpr`, `viewSize` on the Shader element, SREF-10) and W4-101 (every effect length in DIP, E1, SREF-01); closed by W4-120 |
| #155 | 2.0 | Lines, paths and splines: follow-ups | stays | new features; W6-13 only links it and adds the gradient questions if they are missing (G7) |
| #153 | 2.0 | Normalized/PixelTopLeft: one frame with the old scale during a live resize | stays | a fix of its own; no finding touches it |
| #60 | 1.0 | CLA bot: remaining steps | stays | repository setup for going public |
| #59 | 1.0 | CI for the public repository | stays | public runners and fork PRs; W5-03 adds only a checkpoint job |
| #56 | 3.0 | Remember: synthetic mouse from touch | stays | a note, no work |
| #45 | 3.0 | Web export (Qt WASM) | stays | new feature |
| #35 | 3.0 | Clarify the usage concept | stays | question outside the code |
| #34 | 3.0 | Tooltip preview for elements with video | stays | new feature |
| #33 | 3.0 | Animated styles: stress test | stays | measurement; W4-122 makes the bundles cached process nodes, which this test would then measure |
| #32 | 3.0 | Pipeline cache in the editor | stays | new feature |
| #31 | 3.0 | Color-grading effects | stays | new effects; they will follow the conventions of E1 and A7 |
| #30 | 3.0 | Shader nodes: element as texture pin | stays | new feature of the shader factory, after W4-15 |
| #29 | 3.0 | Shader nodes: uniform arrays as pins | stays | new feature of the shader factory |
| #27 | 3.0 | Linux kiosk mode | stays | new platform; W4-25 makes it easier |
| #26 | 2.0 | Multi-pass effects: performance check and visual review | stays | judgement call: a review of its own; W4-44 and the patch look cover allocation and the help patches, not this check |
| #25 | 2.0 | IDebuggable: debug overlay for patched widgets | stays | new feature |
| #23 | 2.0 | Interop: QtRenderer as Stride and Skia layer | stays | new feature |
| #21 | 2.0 | Performance: four open measurements | stays | profiling experiments (batching, text layout, JPEG on arm64); not the A4 baseline of W1-08 |
| #16 | 2.0 | Channels: convenience nodes | stays | new nodes |
| #14 | 3.0 | Ship pre-baked .qsb files | stays | new feature; W4-59 keeps one bake path it could build on |
| #12 | 1.0 | Replace the vvvv feed with nuget.org | stays | waits for VL.Core on nuget.org |
| #10 | 1.0 | Linux: manual release test on fresh systems | stays | a test of the 1.0 release, not of 0.10.0 |

## 6 Coverage

### 6.1 Findings

All 340 findings, each in exactly one issue. Notes: **=** a duplicate of P22 or TARGET 3.2, **ovt** overturned and cut as the decided thing, **W1** confirmed or dropped (or measured) in wave 1 first, **part** a part of the finding is carried by another issue.

| Finding | Sev. | Issue | Note |
|---|---|---|---|
| REC-01 | must | W4-74 | W1: W1-14 |
| REC-02 | must | W4-75 |  |
| REC-03 | should | W4-83 |  |
| REC-04 | should | W4-11 |  |
| REC-05 | should | W4-11 |  |
| REC-06 | should | W4-76 |  |
| REC-07 | should | W4-11 |  |
| REC-08 | should | W4-77 |  |
| REC-09 | should | W4-77 |  |
| REC-10 | should | W4-76 |  |
| REC-11 | should | W4-78 |  |
| REC-12 | could | W4-79 |  |
| REC-13 | could | W3-14 |  |
| REC-14 | could | W5-01a | K6 under TEST-07 |
| REC-15 | could | W4-74 |  |
| REC-16 | should | W1-02 |  |
| ELM-01 | must | W4-13 |  |
| ELM-02 | should | W4-80 |  |
| ELM-03 | should | W4-80 |  |
| ELM-04 | should | W4-16 |  |
| ELM-05 | should | W4-18 |  |
| ELM-06 | should | W3-03 | A1 rename of wave 3 |
| ELM-07 | should | W4-81 |  |
| ELM-08 | should | W3-14 |  |
| ELM-09 | should | W4-90 |  |
| ELM-10 | should | W4-91 |  |
| ELM-11 | should | W4-13 |  |
| ELM-12 | must | W4-13 |  |
| ELM-13 | could | W4-107 |  |
| ELM-14 | could | W4-90 |  |
| RUN-01 | must | W4-20 |  |
| RUN-02 | should | W4-84 |  |
| RUN-03 | should | W4-85 |  |
| RUN-04 | should | W4-86 |  |
| RUN-05 | should | W4-10 |  |
| RUN-06 | should | W4-10 |  |
| RUN-07 | should | W4-88 |  |
| RUN-08 | should | W4-88 |  |
| RUN-09 | should | W4-88 |  |
| RUN-10 | should | W4-87 |  |
| RUN-11 | could | W4-88 |  |
| RUN-12 | could | W4-20 |  |
| RUN-13 | could | W4-16 |  |
| RUN-14 | could | W5-01a | K6 under TEST-07 |
| PROTO-01 | must | W3-09 |  |
| PROTO-02 | must | W3-14 |  |
| PROTO-03 | should | W4-82 |  |
| PROTO-04 | should | W6-17 |  |
| PROTO-05 | should | W6-17 |  |
| PROTO-06 | should | W3-11 | with CAPI-04 (TARGET 3.2) |
| PROTO-07 | should | W3-12 |  |
| PROTO-08 | should | W3-10 |  |
| PROTO-09 | should | W3-14 |  |
| PROTO-10 | should | W3-13 |  |
| PROTO-11 | could | W3-15 |  |
| PROTO-12 | could | W4-32 | = NCMD-06 (TARGET 3.2) |
| CAPI-01 | must | W3-12 | W1: W1-15 |
| CAPI-02 | should | W3-12 |  |
| CAPI-03 | should | W3-13 |  |
| CAPI-04 | should | W3-11 |  |
| CAPI-05 | should | W3-11 |  |
| CAPI-06 | should | W3-10 |  |
| CAPI-07 | should | W4-12 |  |
| CAPI-08 | should | W6-16 |  |
| CAPI-09 | could | W3-13 |  |
| CAPI-10 | could | W4-89 |  |
| NODE-01 | should | W4-19 |  |
| NODE-02 | must | W4-93 | W1: Clip grab (W1-17) |
| NODE-03 | should | W4-93 |  |
| NODE-04 | should | W4-94 |  |
| NODE-05 | should | W4-20 | with RUN-01 (TARGET 3.2) |
| NODE-06 | should | W5-01a | K6 under TEST-07 |
| NODE-07 | could | W4-95 |  |
| NODE-08 | could | W4-19 |  |
| QML-01 | must | W4-110 |  |
| QML-02 | should | W4-14 |  |
| QML-03 | should | W4-15 |  |
| QML-04 | should | W4-111 |  |
| QML-05 | should | W4-115 | E2 rule after the cut of D6 |
| QML-06 | should | W4-49 | W1: W1-15 |
| QML-07 | should | W4-90 | = ELM-14 |
| QML-08 | should | W4-49 |  |
| QML-09 | could | W4-110 |  |
| QML-10 | could | W4-110 |  |
| QML-11 | could | W4-111 |  |
| QML-12 | should | W1-03 |  |
| SHD-01 | must | W4-101 |  |
| SHD-02 | must | W4-116 |  |
| SHD-03 | should | W4-113 |  |
| SHD-04 | should | W4-117 |  |
| SHD-05 | should | W4-115 | E2 rule after the cut of D6 |
| SHD-06 | should | W4-114 |  |
| SHD-07 | should | W4-112 |  |
| SHD-08 | should | W4-113 |  |
| SHD-09 | should | W4-102 |  |
| SHD-10 | should | W4-15 | = QML-03 |
| SHD-11 | should | W4-112 |  |
| SHD-12 | should | W5-01a | K6 under TEST-07 |
| SHD-13 | could | W4-118 |  |
| SHD-14 | could | W4-118 |  |
| SHD-15 | could | W4-117 |  |
| SHD-16 | could | W4-118 |  |
| SIP-01 | should | W4-122 |  |
| SIP-02 | should | W4-123 |  |
| SIP-03 | should | W4-124 | W1: W1-14 |
| SIP-04 | should | W4-124 |  |
| SIP-05 | should | W4-124 |  |
| SIP-06 | should | W4-125 |  |
| SIP-07 | should | W5-01a | K6 under TEST-07 |
| SIP-08 | should | W4-121 |  |
| SIP-09 | should | W4-79 |  |
| SIP-10 | should | W4-122 |  |
| SIP-11 | could | W4-123 |  |
| SIP-12 | could | W4-123 |  |
| SIP-13 | could | W4-124 |  |
| SIP-14 | could | W4-125 |  |
| SIP-15 | could | W4-123 |  |
| SIP-16 | could | W4-121 |  |
| NODW-01 | should | W4-96 |  |
| NODW-02 | should | W4-95 | ovt: pin `On Click` (catalog) |
| NODW-03 | should | W4-126 | W1: W1-14 |
| NODW-04 | should | W4-20 | with RUN-01 (TARGET 3.2) |
| NODW-05 | should | W4-98 | ovt: name kept; output `Image` (D15) |
| NODW-06 | could | W4-99 |  |
| NODW-07 | could | W4-99 |  |
| NODW-08 | could | W4-98 | = API-07 |
| NODW-09 | should | W4-93 |  |
| NODW-10 | could | W5-01a | K6 under TEST-07 |
| NODW-11 | could | W4-94 |  |
| NODW-12 | could | W4-126 |  |
| NODW-13 | could | W4-99 |  |
| NODW-14 | could | W5-06 |  |
| NODX-01 | must | W4-100 |  |
| NODX-02 | should | W4-107 |  |
| NODX-03 | should | W4-106 |  |
| NODX-04 | should | W4-106 |  |
| NODX-05 | should | W4-105 |  |
| NODX-06 | should | W4-105 |  |
| NODX-07 | should | W4-108 |  |
| NODX-08 | should | W4-108 |  |
| NODX-09 | could | W4-106 |  |
| NODX-10 | should | W5-01a | K6 under TEST-07 |
| NODX-11 | could | W3-04 | with the move of S3 |
| NODX-12 | could | W4-102 |  |
| NODX-13 | could | W4-109 | = API-11 |
| NAPP-01 | should | W4-02 |  |
| NAPP-02 | must | W3-12 | = CAPI-01 |
| NAPP-03 | should | W3-12 | = CAPI-02 |
| NAPP-04 | should | W4-24 |  |
| NAPP-05 | should | W4-27 |  |
| NAPP-06 | should | W4-23 |  |
| NAPP-07 | should | W4-22 |  |
| NAPP-08 | should | W4-25 |  |
| NAPP-09 | should | W3-11 | = CAPI-04 |
| NAPP-10 | should | W4-26 | W1: W1-15 |
| NAPP-11 | should | W4-24 |  |
| NAPP-12 | should | W4-25 |  |
| NAPP-13 | could | W4-24 |  |
| NAPP-14 | should | W4-27 |  |
| NAPP-15 | could | W4-67 |  |
| NAPP-16 | could | W4-25 |  |
| NAPP-17 | could | W3-13 |  |
| NCMD-01 | must | W4-30 |  |
| NCMD-02 | must | W4-30 |  |
| NCMD-03 | must | W4-31 |  |
| NCMD-04 | should | W4-29 | part: poll convention in W3-12 |
| NCMD-05 | should | W4-03 |  |
| NCMD-06 | should | W4-32 |  |
| NCMD-07 | should | W4-33 |  |
| NCMD-08 | should | W3-15 |  |
| NCMD-09 | should | W4-34 |  |
| NCMD-10 | should | W4-35 |  |
| NCMD-11 | should | W6-18 | spec moves after the cut of NCMD-05 |
| NCMD-12 | could | W4-36 |  |
| NCMD-13 | could | W4-33 |  |
| NCMD-14 | could | W4-39 | type table of NELM-04 |
| NCMD-15 | should | W1-09 |  |
| NCMD-16 | could | W4-32 |  |
| NCMD-17 | could | W4-31 | W1: W1-15 |
| NELM-01 | must | W4-37 |  |
| NELM-02 | should | W4-38 |  |
| NELM-03 | should | W4-38 |  |
| NELM-04 | should | W4-39 |  |
| NELM-05 | should | W4-08 |  |
| NELM-06 | should | W4-40 |  |
| NELM-07 | should | W4-41 |  |
| NELM-08 | should | W4-38 |  |
| NELM-09 | should | W4-42 |  |
| NELM-10 | should | W4-08 |  |
| NELM-11 | should | W3-13 | renamed in the break |
| NELM-12 | should | W1-10 |  |
| NELM-13 | could | W4-43 |  |
| NELM-14 | could | W4-37 | W1: W1-15 |
| NELM-15 | could | W4-36 | same initializer as NCMD-12 |
| NDEV-01 | must | W4-61 |  |
| NDEV-02 | should | W4-63 |  |
| NDEV-03 | should | W3-11 | test device moves with TEST-08 |
| NDEV-04 | should | W4-07 |  |
| NDEV-05 | should | W4-62 |  |
| NDEV-06 | should | W4-63 |  |
| NDEV-07 | should | W4-64 |  |
| NDEV-08 | should | W4-01 |  |
| NDEV-09 | should | W3-11 |  |
| NDEV-10 | should | W4-66 |  |
| NDEV-11 | could | W4-65 |  |
| NDEV-12 | could | W3-12 |  |
| NDEV-13 | could | W4-65 |  |
| NDEV-14 | could | W4-66 |  |
| NRES-01 | must | W4-52 |  |
| NRES-02 | should | W4-56 | W1: measurement (W1-17) |
| NRES-03 | should | W4-04 |  |
| NRES-04 | should | W4-53 |  |
| NRES-05 | should | W4-54 |  |
| NRES-06 | should | W4-04 |  |
| NRES-07 | should | W4-55 |  |
| NRES-08 | should | W4-04 |  |
| NRES-09 | should | W5-01b | K6 under TEST-07 |
| NRES-10 | could | W4-53 |  |
| NRES-11 | could | W4-51 |  |
| NRES-12 | could | W4-57 | part: error buffer in W3-12 |
| NRES-13 | could | W4-58 |  |
| NRES-14 | could | W6-18 |  |
| NRES-15 | could | W4-92 |  |
| NFX-01 | should | W4-44 |  |
| NFX-02 | should | W4-45 |  |
| NFX-03 | should | W4-45 |  |
| NFX-04 | should | W6-18 |  |
| NFX-05 | should | W4-05 |  |
| NFX-06 | should | W4-40 | with NELM-06 |
| NFX-07 | should | W4-09 |  |
| NFX-08 | should | W4-46 |  |
| NFX-09 | should | W4-59 |  |
| NFX-10 | should | W4-46 |  |
| NFX-11 | should | W4-64 |  |
| NFX-12 | could | W4-59 |  |
| NFX-13 | could | W4-59 |  |
| NFX-14 | could | W4-47 |  |
| NFX-15 | could | W4-05 |  |
| NFX-16 | could | W4-48 |  |
| NFX-17 | could | W4-05 |  |
| NFX-18 | could | W4-45 |  |
| NFX-19 | could | W4-59 |  |
| NFX-20 | could | W4-50 |  |
| NFX-21 | could | W4-50 |  |
| NFX-22 | could | W4-46 |  |
| NFX-23 | should | W1-11 |  |
| COMP-01 | must | W6-05 |  |
| COMP-02 | should | W4-60 |  |
| COMP-03 | should | W4-60 | W1: W1-16 |
| COMP-04 | should | W4-129 | W1: W1-16 |
| COMP-05 | should | W4-127 |  |
| COMP-06 | should | W4-127 |  |
| COMP-07 | should | W4-128 |  |
| COMP-08 | should | W1-06 |  |
| COMP-09 | should | W1-06 |  |
| COMP-10 | could | W4-128 |  |
| COMP-11 | could | W4-128 |  |
| COMP-12 | could | W4-60 |  |
| COMP-13 | could | W4-129 |  |
| COMP-14 | could | W4-128 |  |
| COMP-15 | could | W4-129 |  |
| API-01 | must | W4-16 |  |
| API-02 | should | W4-17 |  |
| API-03 | should | W4-97 |  |
| API-04 | should | W4-97 |  |
| API-05 | should | W4-98 |  |
| API-06 | should | W4-100 | = NODX-01 |
| API-07 | should | W4-98 |  |
| API-08 | should | W4-107 |  |
| API-09 | should | W4-17 | ovt: names kept, category move only |
| API-10 | should | W4-92 |  |
| API-11 | could | W4-109 |  |
| API-12 | could | W4-98 |  |
| API-13 | could | W4-103 |  |
| API-14 | should | W6-11 | same page as DOCD-01 |
| API-15 | could | W4-16 |  |
| DOC-01 | must | W6-02 |  |
| DOC-02 | must | W6-03 |  |
| DOC-03 | must | W6-09 |  |
| DOC-04 | must | W6-04 |  |
| DOC-05 | should | W6-08 |  |
| DOC-06 | should | W6-06 |  |
| DOC-07 | should | W6-05 |  |
| DOC-08 | should | W6-10 |  |
| DOC-09 | should | W3-07 |  |
| DOC-10 | should | W6-01 |  |
| DOC-11 | should | W6-01 |  |
| DOC-12 | should | W6-07 |  |
| DOC-13 | should | W6-03 |  |
| DOC-14 | should | W6-09 |  |
| DOC-15 | could | W6-04 |  |
| DOC-16 | could | W6-07 |  |
| DOCD-01 | must | W6-11 |  |
| DOCD-02 | must | W6-12 |  |
| DOCD-03 | must | W6-13 |  |
| DOCD-04 | should | W6-13 |  |
| DOCD-05 | should | W6-14 |  |
| DOCD-06 | should | W3-08 |  |
| DOCD-07 | should | W4-83 | = REC-03 (same page) |
| DOCD-08 | should | W6-15 |  |
| DOCD-09 | should | W6-14 |  |
| DOCD-10 | should | W6-16 |  |
| DOCD-11 | should | W6-15 |  |
| DOCD-12 | could | W6-15 |  |
| DOCD-13 | could | W6-03 |  |
| DOCD-14 | could | W6-13 |  |
| DOCD-15 | should | W6-19 |  |
| NDIAG-01 | must | W4-68 | W1: W1-15 |
| NDIAG-02 | should | W4-69 |  |
| NDIAG-03 | should | W4-70 |  |
| NDIAG-04 | should | W4-06 |  |
| NDIAG-05 | should | W4-71 |  |
| NDIAG-06 | should | W4-72 | W1: W1-15 |
| NDIAG-07 | should | W4-28 |  |
| NDIAG-08 | could | W4-72 |  |
| NDIAG-09 | could | W4-23 |  |
| NDIAG-10 | should | W3-13 | renamed in the break |
| NDIAG-11 | should | W4-87 | = RUN-10 |
| NDIAG-12 | should | W4-71 |  |
| NDIAG-13 | should | W4-70 |  |
| NDIAG-14 | should | W5-01b | K6 under TEST-07 |
| NDIAG-15 | could | W4-86 |  |
| NDIAG-16 | could | W4-71 |  |
| NDIAG-17 | could | W4-70 |  |
| NDIAG-18 | could | W4-73 |  |
| NDIAG-19 | could | W4-06 |  |
| TEST-01 | must | W1-04 |  |
| TEST-02 | should | W5-03 |  |
| TEST-03 | should | W1-12 |  |
| TEST-04 | should | W1-13 |  |
| TEST-05 | should | W1-05 |  |
| TEST-06 | should | W5-04 |  |
| TEST-07 | should | W5-01a |  |
| TEST-08 | should | W3-11 | = CAPI-04 |
| TEST-09 | should | W1-07 |  |
| TEST-10 | should | W1-01 |  |
| TEST-11 | should | W1-01 | ovt: F5 replaces the runner |
| TEST-12 | could | W1-01 |  |
| TEST-13 | should | W5-02 |  |
| TEST-14 | could | W5-05 |  |

Totals: 340 findings (31 must, 211 should, 98 could) in 173 issues; 17 issues carry no finding of their own (checks, measurements, mechanical steps, catalog and shader rows, release). Unplaced: none.

### 6.2 Catalog rows of kind open and principle

From [review/23_api.md](review/23_api.md) section 7, "Added by the pass of P1/P2". The decided rows of the first part of the catalog follow the finding in their Finding column into the issue of section 6.1.

| Kind | Row | Issue |
|---|---|---|
| open | FileWriter, FileWriter (Element) / 16-bit images (D6) | W4-57 |
| open | generated shader effect nodes / reserved uniform names (D8) | W4-47 |
| open | generated QML nodes / `point`, `size` (D7) | W4-50 |
| open | generated QML nodes / order of the common pins (D9) | W4-111 |
| open | QtImage / State after release (D13) | W4-125 |
| open | VideoSourceToQtImage / output `Image` (D15) | W4-98 |
| open | TouchPoint as struct (D5) | W4-107 |
| open | Drag / Delta → Translation (D16) | W4-107 |
| open | Hover / Devices (D17) | W4-107 |
| open | AddArc / start of a full sweep (D10) | W4-123 |
| open | AddRoundRect / start point (D11) | W4-123 |
| open | Spline (CatmullRom) / Tension (D12) | W4-123 |
| open | AddCircle / pin order (D18) | W4-109 |
| open | Button / `On Click` position (D14) | W4-95 |
| open | QtTexture / Perf (D1) | W4-127 |
| open | QtRenderer / two targets (D2) | W4-127 |
| open | PerfStats / value names (D3, D4) | W4-87 |
| principle | Renderer / Error | W4-20 |
| principle | Renderer / exception inside the reconcile | W4-74 |
| principle | Renderer / vlqt.dll of another version | W3-09 |
| principle | FileWriter, FileWriter (Element) / Success | W4-20 |
| principle | Renderer / Enable Keyboard Shortcuts | W4-28 |
| principle | LoadImage … FileWriter (Element) / Error | W4-20 |
| principle | generated shader effect nodes / Error | W4-102 |
| principle | Video (VL.Audio) / aspect Experimental | W4-129 |
| principle | Text, Button … Shape, Path / size limit | W3-15 |
| principle | Hover, Click, widgets / state of an element shown twice | W4-80 |
| principle | all element nodes / identity in the registry | W4-76 |
| principle | Row, Column / alignment and hidden children | W4-43 |
| principle | QmlElement / animations when the clock jumps back | W4-26 |
| principle | QmlElement / Error | W4-111 |
| principle | generated QML nodes / Error | W4-111 |
| principle | generated QML nodes / Enabled | W4-21 |
| principle | generated QML nodes / Size | W4-111 |
| principle | generated QML nodes / pins while the file fails | W4-115 |
| principle | Shader / Error | W4-113 |
| principle | generated shader nodes / Error | W4-113 |
| principle | generated shader nodes / Enabled | W4-21 |
| principle | generated shader nodes / Size | W4-113 |
| principle | generated shader nodes / pins while the file does not compile | W4-115 |
| principle | Shader / Fragment Code default text | W4-118 |
| principle | LoadImage, LoadImage (Async), ToQtImage, VideoSourceToQtImage / Upload | W4-54 |
| principle | Pinch / Is Active → Is Pinching | W4-107 |
| principle | AddRoundRect / radii larger than half the rectangle | W4-123 |
| principle | Text / Font Size help and "px" in styles | W4-122 |
| principle | bundle setters Colors … Track → Set… | W4-122 |
| principle | bundle getters Get… → plain | W4-122 |
| principle | 20 single setters → Set… | W4-122 |
| principle | 20 single getters → plain | W4-122 |
| principle | setters / State → States | W4-122 |
| principle | six bundles / process nodes | W4-122 |
| principle | Slider, Input (Int) / Value with a channel outside the range | W4-96 |
| principle | QtTexture / Error | W4-127 |
| principle | QtRenderer / Error | W4-127 |
| principle | QtTexture, QtRenderer / Render Threads | W4-62 |
| principle | QtTexture … ToQtImage / device creation retried | W4-63 |
| principle | PerfStats / Split | W4-87 |
| principle | PerfStats / OverBudget | W4-68 |
| principle | Interaction / Sink and other plumbing internal | W4-16 |
| principle | Geometry / its two nodes | W4-16 |

17 open and 43 principle rows, all placed. The 31 rows of kind "fix" need no decision; they follow their finding and are the list for the release notes (W7-01).

### 6.3 Decided rows of the shader comparison

| Row | Issue |
|---|---|
| SREF-01: every effect length and its default in DIP (E2 to E5, E8 to E14), user uniforms documented as DIP | W4-101 |
| SREF-02: Blur `Max Radius` is DIP (E1) | W4-103 |
| SREF-03: angle uniforms of shaders and templates in cycles | W4-100 |
| SREF-04: ColorAdjust `Saturation` on HSCB's scale, default 0 (E6, E7) | W4-104 |
| SREF-05: Blend `Blend Mode` (E15) | W4-103 |
| SREF-06: optional `Apply` on every effect node (E16) | W4-104 |
| SREF-07: `@optional` (E17) | W4-119 |
| SREF-08: `@tags`, `@remarks` (E18) | W4-119 |
| SREF-09: relative `@category` (E19) | W4-117 |
| SREF-10: `texelSize`, `dpr`, `viewSize` on the element node (E22) | W4-120 |
| SREF-11: RGBSplit template in DIP (E23) | W4-120 |
| SREF-12: pin names split before a digit (E21) | W4-119 |
| E24: reserved uniform names, one list | W4-47 |
| SREF-13: enum pins (E20) | no issue: decided "not now", `int` plus `@summary` |
| Blur `Amount` (SREF-14, A4) and the rows "keep" of SREF-14 to SREF-16 | no issue: decided to keep |

## 7 Questions for Sebastian

Answered on 2026-11-20:

1. TEST-07 is two PRs, C# and native: W5-01a and W5-01b.
2. The arm64 notebook has WSL with Qt for Linux, but is not available at the moment (section 4).
3. Fewer issues: the tasks are grouped into GitHub issues with task lists (section 2a).
