## What changed and why

<!-- One or two sentences. Link the issue: "Closes #123". -->

## How it was verified

<!-- Check what applies and name the scripts and help patches you ran. -->

- [ ] `tools/build.ps1 -Test` passes (unit tests and SmokeTest); for C#-only changes `dotnet test src/VL.Qt.Tests`
- [ ] Changed nodes or pins: the `help/Qt/HowTo …` patch is adapted and compile-checks with `tools/vvvvc.ps1 -File <patch>`
- [ ] Changed rendering: looked at the PNGs in `build/smoke/`
- [ ] Docs under `docs/` updated where behavior or pins changed

## Checklist

- [ ] I have read CONTRIBUTING.md
