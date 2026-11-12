# Contributing to VL.Qt

Thank you for your interest in VL.Qt. Bug reports, ideas and pull requests are welcome.

## Build and test

Prerequisites, first build, scripts and tests are described in [docs/development.md](docs/development.md). The conventions for code, help patches, encoding and tests, written for people and coding agents alike, are in [AGENTS.md](AGENTS.md). A change is done when the affected help patch, the unit tests and the SmokeTest pass.

## Branches and pull requests

- Every change goes in through a pull request; nobody pushes to `develop` or `main` directly (a ruleset enforces it).
- Branch from `develop` and open your pull request against `develop`. `main` only receives releases.
- Where the branch lives:
  - Contributors fork the repository, push the branch to their fork and open the pull request from there. The CI of a pull request from a fork starts after a maintainer approves it.
  - Maintainers push the branch to this repository instead of a fork, with the remote and branch named explicitly (`git push origin HEAD:fix/12-short-topic`). The pull request is the same.
- Name the branch by kind, issue number and topic: `fix/…`, `feat/…`, `docs/…`.
- Open the pull request as a draft (`gh pr create --base develop --draft`) and mark it ready when the work is complete and `tools/build.ps1 -Test` passes locally. Every push to a pull request that is not a draft starts the CI.
- Keep pull requests focused and describe what changed and how you verified it.
- Do not rename or remove a node or pin in one step: mark it `Obsolete` first, as described in [docs/versioning.md](docs/versioning.md).

## Issues and versions

Pick the form that fits when you open an issue: bug report, feature request or task. Maintainers triage it with a type, an area and a milestone. Milestones are version numbers: `1.0` blocks the first stable release, `2.0` holds urgent work that does not block it, `3.0` holds distant features. Until 1.0 the package stays at 0.9.x with fixes only. After 1.0, versions follow [Semantic Versioning](https://semver.org): renamed or removed nodes, pins and categories break users' patches and therefore come only with a major version.

## License and CLA

Contributions are licensed under the [MIT License](LICENSE). Before your first pull request can be merged, you sign the [Contributor License Agreement](CLA.md) once by commenting in the pull request; the CLA bot explains how.

If your pull request is merged, your GitHub name is added to [CONTRIBUTORS.md](CONTRIBUTORS.md). To stay out of the list, write "no CONTRIBUTORS listing" in the pull request.

## The contributors list (maintainers)

The list is [CONTRIBUTORS.md on the branch `cla-signatures`](https://github.com/sebllll/VL.Qt/blob/cla-signatures/CONTRIBUTORS.md); `CONTRIBUTORS.md` in `develop` only links to it, because `develop` and `main` take changes only through pull requests. The workflow `.github/workflows/contributors.yml` writes it with `tools/update-contributors.py` after every push to `develop` or `main`. It lists the signers of the CLA (`signatures/cla.json` on the branch `cla-signatures`) who have at least one merged pull request. Opt-outs are one GitHub login per line in `signatures/optout.txt` on the same branch (lines starting with `#` are comments); a maintainer adds a login there when a pull request contains "no CONTRIBUTORS listing" and then runs the workflow by hand. Do not edit the list by hand.
