# Review brief – VL.Qt before 0.10.0

Every review session reads this page first. It says what the review is for, what counts as a finding and how a report is written. The prompt of a session only adds its area.

## Why this review

VL.Qt is about to become a public open-source library for the vvvv community. Before 1.0 the code, the node API and the documentation are reviewed once, thoroughly, and then refactored on the branch `refactor`. The result is release 0.10.0. The reports of this review become the issues of the milestone `refactor`, which several orchestrators then work through. A report is therefore read by a planner who cuts it into issues, and later by an agent who implements one issue without knowing this review.

Decisions taken so far: [../DECISIONS.md](../DECISIONS.md). The ones that shape a review:

- **Breaking changes are allowed.** Nodes, pins, categories, types, namespaces, files and the protocol between C# and QtNative may be renamed or reshaped. There is no `Obsolete` phase and no migration effort for users; the help patches are updated with the change. Propose the state that is right, not the smallest step.
- **The architecture is not rebuilt for its own sake.** The element tree, the reconciler, the command protocol and the render thread stay unless you can show a concrete defect: something wrong, unsafe, or not maintainable by a person who did not write it. Such a point goes into "Questions for Sebastian", with the alternatives and your recommendation.
- **Behaviour stays unless a finding says otherwise.** A refactoring that changes what a patch draws or when an event arrives is a separate finding with `Breaking: yes`.

## The reader the code is written for

A person from the vvvv community: patches well, writes C#, has written a node or two, has read VL.StandardLibs. Knows Qt barely and does not know the history of this project. They open the repository to fix a bug, add an element or understand why something behaves as it does. Ask of every file:

- Can they find it? (name, folder, namespace)
- Can they understand it without reading five other files first?
- Can they tell what is the contract and what is an incident of history?
- Would they dare to change it? (tests, clear ownership, no hidden coupling)

Sebastian (the maintainer) knows vvvv very well and Qt little. Explain a Qt term the first time you use it in a question or a proposal.

## The measure

- **C#:** the code Elias (azeno) and Sebastian (gregsn) write in the official vvvv repositories. The rules, with evidence, are in the skill `vvvv:vvvv-library-codestyle` (`SKILL.md` and `rules.md` under `C:\Users\seblh\.claude\plugins\synced\7e059ad0-ec30-4f1d-8db2-c8362de02850_41ae327b-dd66-4ad6-b264-edba4d7817f6\vvvv\skills\vvvv-library-codestyle\`); neighbouring skills in the same `skills` folder cover nodes (`vvvv-custom-nodes`), libraries and node naming (`vvvv-node-libraries`) and interop (`vvvv-native-interop`). The sources are checked out read-only at `C:\vl\VL.StandardLibs`: when a rule is in doubt, look how VL.Skia, VL.ImGui, VL.Core or VL.Stride do it and cite the file.
- **QtNative (C++):** Qt's own conventions and the best parts of the existing code. Consistency inside the layer counts more than a style guide from outside.
- **The repository's own rules:** `AGENTS.md`, `docs/architecture.md`, `docs/decisions/`, `docs/versioning.md`. Where the code and these pages disagree, say which one is right.

## What to look at

1. **Understandability.** Names that say what a thing is; one responsibility per type; files and methods a person can hold in their head; control flow that can be followed; no knowledge needed that exists only in an issue.
2. **Structure.** Layering and dependencies between folders and projects, what is `public` and why, one type per file, namespace and folder, where a helper belongs, duplication, code that exists only for a test.
3. **Naming and API.** Types, methods, files, namespaces; for nodes also what vvvv shows: node name, category, pin names, pin order, defaults, visibility.
4. **Comments and XML docs.** They describe the present state and the reason, in a few sentences. A class summary of forty lines is a design note in the wrong place: say where it belongs (`docs/`) and what stays. XML docs become the node and pin help in vvvv: check that they help a user.
5. **Correctness and robustness.** Report every real defect you see while reading, with the evidence: threading, lifetime and ownership (especially across the native boundary), error paths, disposal, races, undefined behaviour, leaks, wrong assumptions. Do not guess: a suspicion you could not confirm is reported as one, with what would confirm it.
6. **Performance contracts.** Hot paths must not allocate per frame. Name the hot paths of your area so that a refactoring does not break them, and report where the rule is already broken.
7. **Dead weight.** Unused code, workarounds whose cause is gone, options nobody sets, leftovers of earlier designs. Check with a search before you call something unused.
8. **Tests.** Which tests protect the area and where a refactoring would run without a net.

## Known repository-wide findings

These are already recorded and become issues of their own. Do not list their occurrences. Do use them when you describe the target state of your area.

- **K1 Issue numbers in comments.** About 1,700 references such as `#455` in `src`; most point to a private repository that the community cannot read. All of them go. Where a comment only makes sense with the issue, your proposal for that code says what the comment should explain instead.
- **K2 `private` is not spelled out.** About 700 field declarations in 119 files.
- **K3 Several types in one file.** 45 files. For your area, give the target file layout (section 4 of the report).
- **K4 Long comment lines and long class summaries.** About 400 comment lines over 160 characters.
- **K5 `NodeContext` constructor parameter without `[Pin(Visibility = PinVisibility.Hidden)]`.** 55 of 61 nodes; AGENTS.md asks for the attribute. Whether vvvv needs it is checked by the API review (01).
- **K6 Test hooks in the library.** Constructors, counters and properties that exist "for tests" (about 70 places), and `TEST ONLY` functions in QtNative. For your area, say which ones exist and how the same test could work without them.
- **K7 Version.** `Directory.Build.props` says 0.1.0, the CI overrides it.

## Rules for a review session

- **Read-only.** The only file you create or change is your own report. No other edit, no commit, no branch, no build, no test run, no vvvv: other sessions may use this working directory. Reading, searching and read-only git commands (`git log`, `git blame`) are fine.
- **Work in few, large steps.** The usage limit of the account is short, and every tool call reads your whole context again. Read the files of your area in one or two batches of parallel reads, not one by one, and do not read a file twice. Look things up outside your area (VL.StandardLibs, other folders, tests) with a targeted search and a short excerpt, a handful of times at most. Then write the whole report with one `Write` call; correct it with a few edits at most. Do not narrate between steps.
- **Read your area completely,** file by file, not by sampling, unless your prompt says otherwise. List in the report what you read in full and what you only skimmed.
- **Evidence.** Every finding names `path:line`. Check a claim before you write it: search for the callers before you call something unused, read the test before you say there is none.
- **Concrete proposals.** "Split this class" is not a proposal. Name the target: the types, their responsibility, the file names, the signature, the new name. If there are two good ways, give both and recommend one.
- **Stay in your area, look over the fence.** A finding in a neighbouring area goes into "For other areas" in one line.
- **No padding.** A report without filler is worth more than a long one. Do not report style points that K1 to K7 cover, and do not praise.
- **Language.** English, plain sentences, one fact in one place.
- Do not start sub-sessions. Do not ask the user; open questions go into the report.

## The report

Write it to the path your prompt names, in this structure:

```markdown
# Review NN – <area>

Reviewer model, date. Files read in full: … Skimmed: … Not read: …

## 1 Summary
Five to ten lines: the state of the area, and the three to five changes that matter most.

## 2 What works and must survive
Design decisions, invariants and hot paths a refactoring has to keep, each with the reason. Short.

## 3 Findings
(one block per finding, most important first)

## 4 Target structure
The area after the refactoring: folders, files, types (one line each: name, responsibility), namespaces,
what is public. Mark what is renamed (old → new), split, merged or deleted.

## 5 Questions for Sebastian
Decisions only he can take. Each with the alternatives, the consequences and your recommendation.

## 6 For other areas
One line each: area, what you saw, where.
```

A finding:

```markdown
### <AREA>-NN <title: the change in a few words>
- **Kind:** api | architecture | structure | naming | docs | correctness | threading | lifetime | performance | dead-code | tests
- **Severity:** must | should | could
- **Breaking:** no | yes: <what a patch or a C# user of the library notices>
- **Where:** `path:line`, …
- **Finding:** what is there and why it is a problem for the reader described above, or why it is wrong. With the evidence.
- **Proposal:** the target state, concrete enough to implement without this review.
- **Effort:** S | M | L, mechanical: yes | no
- **Touches:** files or folders the change edits (as complete as you can tell)
- **Depends on:** other findings (ids), or none
- **Verification:** which unit test, SmokeTest scenario or help patch shows that the change is right
```

- `must`: wrong, unsafe or misleading; 0.10.0 should not ship with it. `should`: a clear gain in understandability or maintainability. `could`: polish.
- Effort: `S` up to three files, `M` up to about fifteen, `L` more or across layers. `mechanical: yes` means it can be done by search and replace or a tool without understanding each place.
- `Touches` is what lets the planner run issues in parallel: be exact.
- The area prefix is given in your prompt (for example `REC`).

Your final answer in the chat is short (at most fifteen lines): the path of the report, the number of findings by severity, the three most important ones and the questions that block planning.
