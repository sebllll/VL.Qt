# Decisions – refactor (release 0.10.0)

State of the discussion for the review and refactoring of VL.Qt before 1.0. Phase: **brainstorm** (no plan, no issues, no code changes yet). The review is finished: 23 reports with 340 findings, index in [review/README.md](review/README.md). Newest entries first in each section. Format: **Topic (who, date):** decision. Reason.

## Decided

- **Review one session at a time, mostly Opus (Sebastian, 2026-11-19):** the review runs as a sequence of small read-only sessions, one after the other, each on an area of at most about 150 KB of source, each writing `docs/plans/refactor/review/NN_<area>.md` after the brief [review/00_brief.md](review/00_brief.md). Opus is the default model, Sonnet for surveys, Fable only where it is needed or clearly worth it. After the first session (the reconciler, on Opus) cost and quality are looked at before the next one starts. Reason: the usage limit of the account.
- **No extra effort for breaking changes (Sebastian, 2026-11-18):** the library is still a preview. No `Obsolete` phase, no upgrade page, no migration for patches of users. The help patches are updated together with the change that breaks them.
- **`develop` is frozen (Sebastian, 2026-11-18):** only hotfixes land on `develop` until 0.10.0, because the refactoring moves and renames many files.

- **CI only by hand, at checkpoints (Sebastian, 2026-11-18):** The repository is still private and Actions credits are short. Pull requests against `refactor` start no CI (`build.yml` only reacts to the bases `develop` and `main`), and that stays so. The gate for every issue is `tools/build.ps1 -Test` locally. A checkpoint run is started through one standing draft PR `refactor` → `develop`: `gh pr ready` runs the x64 tier once (win-x64 and linux-x64), `gh pr ready --undo` puts it back. `workflow_dispatch` builds all four RIDs and is kept for the release candidate.
- **Breaking changes are allowed (Sebastian, 2026-11-18):** Before 1.0 nodes, pins, categories, types and the protocol may be renamed or reshaped. Goal: do it right once, so the foundation is as solid and maintainable as possible. This replaces the first proposal "strictly behaviour and patch compatible".
- **The whole documentation is in scope (Sebastian, 2026-11-18):** `docs/`, README, AGENTS.md, XML docs and code comments are reviewed and rewritten where needed, not only kept in sync.
- **Scope of the code review (Sebastian, 2026-11-18):** `src/VL.Qt`, `src/VL.Qt.Native`, `src/VL.Qt.Stride` and `src/VL.Qt.Audio` in full. Tests and the SmokeTest follow the refactoring; `tools/`, CI and help patches change only where the refactoring makes it necessary (a renamed node changes its help patch).
- **Style reference (Sebastian, 2026-11-18):** C# as Elias (azeno) and Sebastian (gregsn) write it in the official vvvv repositories (VL.StandardLibs). QtNative has no such model: Qt's own conventions and the existing code are the measure.
- **Audience (Sebastian, 2026-11-18):** the open-source vvvv community. Code has to be understandable without the history of the project.
- **Milestone and release (Sebastian, 2026-11-18):** all issues go into a new milestone `refactor`; the result is released as 0.10.0, and 0.10.x is the last release before the repository becomes public. The two rules in AGENTS.md that say otherwise ("milestones are version numbers only", "the package stays at 0.9.x until 1.0") are adapted with the plan.
- **Branch (Sebastian, 2026-11-18):** one feature branch `refactor` on origin, branched from `develop` at 647392e (the content of release 0.9.10). The plan is its first commit, the sub-features follow as separate commits.

## Open

- **Pull request per issue:** proposed: one branch and one draft PR per issue against `refactor` (squash), at the end one PR `refactor` → `develop` as merge commit. Not confirmed yet.
- **Order of the work:** proposed waves: safety net and tooling, API decisions on paper, repository-wide mechanical changes (alone, one after the other), refactoring per area in parallel, tests, documentation, release.

## Rejected

- **Upgrade page and migration script for users (2026-11-18):** rejected with "No extra effort for breaking changes".

- **CI for the base `refactor` (2026-11-18):** rejected, every push of every sub-PR would cost credits. Replaced by "CI only by hand, at checkpoints".

## Overturned

- ~~**Fifteen parallel review sessions, eight of them on Fable (2026-11-18)**~~ → all fifteen ran into the 5-hour usage limit after two minutes and wrote no report; overturned on 2026-11-19, see "Review one session at a time, mostly Opus".
- ~~**Strictly behaviour and patch compatible (proposal, 2026-11-18)**~~ → overturned the same day, see "Breaking changes are allowed".
