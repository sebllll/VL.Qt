# Internal working material

Maintainer-only working material of the release 0.10.0. The public documentation is in [docs/](../docs/README.md).

- [plans/refactor/](plans/refactor/): the plan of the refactoring, its decisions and questions, the task list, the review reports and the perf baseline.

The release notes of 0.10.0 are written from these files, so they stay until the release.

> [!WARNING]
> This folder must not reach `develop` or `main`. Remove it from the tracked files in the release pull request: `.github/workflows/public-check.yml` fails on every tracked `docs-internal/` path.
