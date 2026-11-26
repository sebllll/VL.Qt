# Contributing to VL.Qt

Thank you for your interest in VL.Qt. Bug reports, ideas and pull requests are welcome.

## Build and test

Prerequisites, first build, scripts and tests are described in [docs/contributing/development.md](docs/contributing/development.md). The conventions for code, help patches, encoding and tests, written for people and coding agents alike, are in [AGENTS.md](AGENTS.md). A change is done when the affected help patch, the unit tests and the SmokeTest pass.

## Branches and pull requests

- Every change goes in through a pull request; nobody pushes to `develop` or `main` directly (a ruleset enforces it).
- Branch from `develop` and open your pull request against `develop`; it is squash-merged. `main` only receives releases (a pull request `develop` to `main` with a merge commit, then a `v*` tag, see [docs/maintaining/](docs/maintaining/release.md)). A hotfix for a release branches from `main`, goes into `main`, and `main` is then merged into `develop`.
- Where the branch lives:
  - Contributors fork the repository, push the branch to their fork and open the pull request from there. The CI of a pull request from a fork starts after a maintainer approves it.
  - Maintainers push the branch to this repository instead of a fork, with the remote and branch named explicitly (`git push origin HEAD:fix/12-short-topic`). The pull request is the same.
- Name the branch by kind, issue number and topic: `fix/…`, `feat/…`, `docs/…`.
- Open the pull request as a draft (`gh pr create --base develop --draft`) and mark it ready when the work is complete and `tools/build.ps1 -Test` passes locally. Every push to a pull request that is not a draft starts the CI. If the CI is red afterwards: `gh pr ready --undo`, fix and check locally, `gh pr ready` again. Do not use `workflow_dispatch` as a substitute; it builds every RID and is more expensive.
- Keep pull requests focused and describe what changed and how you verified it.
- Do not rename or remove a node or pin in one step: mark it `Obsolete` first, as described in [docs/versioning.md](docs/versioning.md).

## Issues and versions

Pick the form that fits when you open an issue: bug report, feature request or task. Maintainers triage it with labels and a milestone (see below). Milestones are version numbers; `1.0` blocks the release. Until 1.0 the package stays at 0.9.x with fixes only. After 1.0, versions follow [Semantic Versioning](https://semver.org): renamed or removed nodes, pins and categories break users' patches and therefore come only with a major version.

### Issue labels (for maintainers)

Every issue gets labels and a milestone when it is created or triaged:

- Type (exactly one): `bug`, `enhancement`, `documentation` or `question` (a spike or a decision).
- Area (at least one): `area:native`, `area:csharp`, `area:patch` or `area:ci`. An issue that touches native and C# gets both labels. Repository-wide documentation (README, licensing, contributing) needs no area.
- Topic (at most one, where one fits): `topic:effects`, `topic:images`, `topic:video`, `topic:styles`, `topic:input`, `topic:linux` or `topic:performance`.
- Extra when needed: `manual-check` (needs a human to look at it in vvvv), `ready`, `good first issue`, `help wanted`.
- Do not invent new labels or milestones. Close issues that will not be done as "not planned", duplicates as "duplicate".

## License and CLA

Contributions are licensed under the [MIT License](LICENSE). Before your first pull request can be merged, you sign the [Contributor License Agreement](CLA.md) once by commenting in the pull request; the CLA bot explains how.

If your pull request is merged, your GitHub name is added to [CONTRIBUTORS.md](CONTRIBUTORS.md). To stay out of the list, write "no CONTRIBUTORS listing" in the pull request.

Maintainer topics (CI, release, repository settings, the contributors list) are in [docs/maintaining/](docs/maintaining/release.md).
