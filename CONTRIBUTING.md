# Contributing to VL.Qt

Thank you for your interest in VL.Qt. Bug reports, ideas and pull requests are welcome.

## Build and test

Prerequisites, first build, scripts and tests are described in [docs/development.md](docs/development.md). The conventions for code, help patches, encoding and tests, written for people and coding agents alike, are in [AGENTS.md](AGENTS.md). A change is done when the affected help patch, the unit tests and the SmokeTest pass.

## Branches and pull requests

- Branch from `develop` and open your pull request against `develop`. `main` only receives releases.
- Open the pull request as a draft (`gh pr create --base develop --draft`) and mark it ready when the work is complete and `tools/build.ps1 -Test` passes locally. Every push to a pull request that is not a draft starts the CI.
- Keep pull requests focused and describe what changed and how you verified it.

## License and CLA

Contributions are licensed under the [MIT License](LICENSE). Before your first pull request can be merged, you sign the [Contributor License Agreement](CLA.md) once by commenting in the pull request; the CLA bot explains how.

If your pull request is merged, your GitHub name is added to [CONTRIBUTORS.md](CONTRIBUTORS.md). To stay out of the list, write "no CONTRIBUTORS listing" in the pull request.

## The contributors list (maintainers)

The list is [CONTRIBUTORS.md on the branch `cla-signatures`](https://github.com/sebllll/VL.Qt/blob/cla-signatures/CONTRIBUTORS.md); `CONTRIBUTORS.md` in `develop` only links to it, because `develop` and `main` take changes only through pull requests. The workflow `.github/workflows/contributors.yml` writes it with `tools/update-contributors.py` after every push to `develop` or `main`. It lists the signers of the CLA (`signatures/cla.json` on the branch `cla-signatures`) who have at least one merged pull request. Opt-outs are one GitHub login per line in `signatures/optout.txt` on the same branch (lines starting with `#` are comments); a maintainer adds a login there when a pull request contains "no CONTRIBUTORS listing" and then runs the workflow by hand. Do not edit the list by hand.
