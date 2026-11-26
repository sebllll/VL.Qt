# Maintaining VL.Qt

For maintainers of the repository `sebllll/VL.Qt`: CI, release, repository settings and the contributors list. Building and testing, for contributors: [Development](../contributing/development.md); how to open a pull request: [CONTRIBUTING.md](../../CONTRIBUTING.md).

## CI

The workflows are in `.github/workflows/` (`build.yml`, `public-check.yml`; `sanitizers.yml` runs by hand). Every action is pinned to a full commit SHA with the version as a comment, Dependabot keeps the pins current.

The repository `sebllll/VL.Qt` differs from forks and private copies in a few places. What costs minutes is keyed on the repository being public (`github.event.repository.private == false`), so it stays off while the repository is private; the nuget.org push, the CLA and the contributor workflows check the name `sebllll/VL.Qt` (GitHub compares it case-insensitively):

- The hosted runners are free for public repositories, so the `win-arm64` and `linux-arm64` entries also run on pull requests. Elsewhere they run only on tags and on a plain `workflow_dispatch`.
- The workflow `public-check.yml` (keyed on the name only, so it runs while the repository is private too) fails on every pull request and push to `develop` and `main` if a file of a maintainer-only working directory (plans, internal notes, local agent settings) is tracked. Its job is a required status check.
- The `release` job runs only for a `v*` tag whose commit is on `main` (job `release-gate`), in the GitHub environment `release`. In `sebllll/VL.Qt` with the environment variable `NUGET_USER` set, it also pushes all `.nupkg` files (`VL.Qt`, `VL.Qt.Stride`, `VL.Qt.Audio`) to nuget.org through Trusted Publishing (no stored API key); elsewhere or without the variable that step is skipped with a notice. See [Release](#release).
- Pull requests use `pull_request` (not `pull_request_target`); their jobs get no secrets and only `contents: read`.

## Release

One-time setup by a maintainer (repository settings):

- [ ] Environment `release` (Settings > Environments): deployment branches and tags limited to `v*`. A required reviewer is optional: the tag ruleset below already decides who can release.
- [ ] nuget.org Trusted Publishing policy (nuget.org > account > Trusted Publishing): one single policy for all packages, repository owner `sebllll`, repository `VL.Qt`, workflow file `build.yml`, environment `release`, package patterns `VL.Qt`, `VL.Qt.Audio`, `VL.Qt.Stride`, scope "Push new packages and package versions" (needed for package IDs that do not exist yet). Do not create one policy per package: with three policies of the same origin the push of `VL.Qt.Audio` got a 403 (v0.9.9). A package ID that is added later must be added to the patterns by hand on nuget.org before the first release that contains it; nothing in the repository or the CI does that, and without it the push of that package fails. A policy for a private repository is only active for a few days until its first successful push binds it to the repository; if it expired, create it again.
- [ ] Environment variable `NUGET_USER` in `release` (not a secret): the nuget.org profile name that owns the policy.
- [ ] Rulesets (Settings > Rules > Rulesets):
  - `develop` and `main`: pull request required, no deletion or force push, required check `public check (no private files or content)`.
  - `main` only: restrict updates, bypass for the role Repository admin in pull requests only. Others with write access can open pull requests to `main` but not merge them.
  - Tags `refs/tags/v*`: restrict creation, update and deletion, bypass for the role Repository admin. Only admins can start a release. This is the actual protection: a tag runs `build.yml` as it is in the tagged commit, so the job `release-gate` alone does not stop a tag on a branch with a changed workflow.

Per release:

- [ ] Everything for the release is merged into `develop`; `tools/build.ps1 -Test` and the CI on `develop` are green.
- [ ] Open the PR `develop` -> `main` and merge it with a merge commit (no squash).
- [ ] Tag the merge commit on `main`: `git tag v<version>` (for example `v1.0.0`, `v1.1.0-rc.1`) and `git push origin v<version>`. The tag name is the package version.
- [ ] The tag starts `build.yml` with all four RIDs. Check the job `pack`: the step "Verify package" prints the package size (limit 250 MB), the `qt/<rid>/` layout and the nuspec metadata (id, version, license, repository); the steps "Verify package (VL.Qt.Stride)" and "Verify package (VL.Qt.Audio)" do the same for the companion packages.
- [ ] The job `release` creates the GitHub release with all `.nupkg` files and pushes them to nuget.org (approve the deployment first if the environment has a required reviewer).
- [ ] Without `NUGET_USER` (or in a fork) the push is skipped with a notice instead of an error. A failed login means the policy does not match (repository, workflow file, environment) or has expired. Fallback: push by hand with `dotnet nuget push <file> -s https://api.nuget.org/v3/index.json -k <key>` and a short-lived API key.
- [ ] A tag on a commit that is not on `main` builds and tests but does not release (warning in the job `release-gate`). Delete the tag and tag the right commit.
- [ ] After publishing: check the package on nuget.org and install it with `tools/start-vvvv-with-nupkg.ps1`.

## The contributors list (maintainers)

The list is [CONTRIBUTORS.md on the branch `cla-signatures`](https://github.com/sebllll/VL.Qt/blob/cla-signatures/CONTRIBUTORS.md); `CONTRIBUTORS.md` in `develop` only links to it, because `develop` and `main` take changes only through pull requests. The workflow `.github/workflows/contributors.yml` writes it with `tools/update-contributors.py` after every push to `develop` or `main`. It lists the signers of the CLA (`signatures/cla.json` on the branch `cla-signatures`) who have at least one merged pull request. Opt-outs are one GitHub login per line in `signatures/optout.txt` on the same branch (lines starting with `#` are comments); a maintainer adds a login there when a pull request contains "no CONTRIBUTORS listing" and then runs the workflow by hand. Do not edit the list by hand.
