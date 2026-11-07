# AGENTS.md

Instructions for coding agents and contributors working on VL.Qt. The conventions are tool-neutral: any agent or person can follow them.

## What this repo is

VL.Qt brings Qt 6 (Qt Quick) to vvvv gamma as a library, focused on 2D graphics content (motion, typography, layout). Three layers:

- `src/VL.Qt/` – C# library: element types, vvvv nodes, reconciler (scene diffing), interop.
- `src/VL.Qt.Native/` – **QtNative**, the C++ layer that owns Qt and the render thread (`vlqt.dll`, `libvlqt.so`).
- `help/Qt/` – help patches (`HowTo …`), the test base for nodes and the user-facing examples.

Tests live in `src/VL.Qt.Tests` (unit) and `src/VL.Qt.SmokeTest` (QtNative without vvvv). How the pieces fit: [docs/architecture.md](docs/architecture.md). Index of all docs: [docs/README.md](docs/README.md).

## Setup and build

Prerequisites, first build, the Visual Studio solution and every script are described in [docs/development.md](docs/development.md). In short, on Windows (PowerShell 7):

```powershell
tools/setup-qt.ps1          # once: Qt for your machine's architecture into C:\Qt
tools/build.ps1             # QtNative + Qt deploy + C# library
tools/start-vvvv.ps1 -Help "Draw a Rectangle"
```

- Check the architecture with PowerShell (`$env:PROCESSOR_ARCHITECTURE`: ARM64 → `win-arm64`, otherwise `win-x64`). Git Bash can report the wrong one on ARM64.
- Only the Qt of your own architecture is needed locally; the other one is built by the CI.
- vvvv comes from `-VvvvDir`, else `%VVVV_DIR%`, else the newest `C:\vvvv\vvvv_gamma_*-win-<arch>`.
- Linux (build, run, tests under WSL or natively): [docs/linux.md](docs/linux.md), scripts `tools/*.sh`.
- After changes in QtNative, restart vvvv (it does not hot-reload native code).

## Tests

```powershell
tools/build.ps1 -Test                          # fresh native build, C# build, unit tests, SmokeTest, summary per step
tools/build.ps1 -Test -Only <scenario> -Patch "help/Qt/HowTo Draw a Rectangle.vl"   # narrow SmokeTest + compile-check one patch
dotnet test src/VL.Qt.Tests                    # unit tests only
dotnet run --project src/VL.Qt.SmokeTest -c Release -- --all   # SmokeTest only
dotnet run --project src/VL.Qt.SmokeTest -c Release -- --drift # protocol.h and Protocol.cs agree?
```

- `tools/build.ps1 -Test` writes the SmokeTest log and grabbed PNGs to `build/smoke/`; exit code 0 means every step passed. Look at the PNGs when you change rendering.
- Rebuild QtNative (`tools/build-native.ps1` or `tools/build.ps1`) whenever you touch `src/VL.Qt.Native/`. Use `-SkipNative` for C#-only changes.
- Protocol changes: edit `src/VL.Qt.Native/protocol.h`, mirror in `src/VL.Qt/Protocol.cs` by hand, run `--drift`.
- A change is done when the affected help patch, unit tests and SmokeTest pass.

## Checking patches

`tools/vvvvc.ps1 [-File <doc.vl>]` compiles/exports a patch with vvvv's command-line compiler (default `help/HelloWorld.vl`, output in `build/vvvvc/`). Exit code 0 means no build errors. Run `dotnet build src/VL.Qt -c Release` first. The script uses the newest vvvv as described under Setup; on ARM64 it adds a shim `vvvvc.arm64.dll` to the vvvv installation (the 8.0 ARM64 previews ship an x64 `vvvvc.dll`).

The help patches in `help/Qt/` (one `HowTo …` per element) are the test base. When you change a node's pins or behavior, adapt the help patch and compile-check it:

```powershell
python tools/vl-pad-layout.py "help/Qt/HowTo Draw Text.vl" [--animate=Text]
tools/vvvvc.ps1 -File "help/Qt/HowTo Draw Text.vl"
```

Rules for help patches:

- Edit `.vl` files (vvvv's XML format) only through vvvv or `tools/vl-pad-layout.py`. Run the tool with `--check` first on patches that were tidied by hand; the options are documented at the top of the script. `tools/test-vl-pad-layout.py` is its regression test.
- Put an IOBox on a pin only if its value differs from the default and says something in the help patch. An IOBox at a pin is the same to the node as an attached value or channel. Example: an IOBox at `Interaction` makes the element opaque to input, so never put one there.
- Animate (LFO) only where it serves the element (motion demos); otherwise use fixed values.
- Layout: compact regions, content stacked top to bottom in data-flow order, IOBoxes as a staircase above (inputs) and below (outputs) their node. Choose defaults so the effect is visible.
- Each patch has a title and a short bullet description of what it shows.
- Background: [vvvv gray book](https://thegraybook.vvvv.org) for the patching conventions.

`help/*.props` (export settings) are tracked and written by vvvv; do not edit them by hand.

## C# code style

The reference is the code written by the vvvv team in [VL.StandardLibs](https://github.com/vvvv/VL.StandardLibs). Existing files may deviate; correct deviations when you touch the code, not in separate reformatting PRs.

**Formatting and naming**
- Allman braces, 4 spaces, block-scoped `namespace X { }`, no `#region`.
- `var` for locals, `=>` for one-line members; one-line `if`/`foreach` bodies without braces, multi-line bodies and `lock` with braces.
- Private fields `_camelCase` (no `F` prefix), private statics `s_camelCase`, constants camelCase. PascalCase for types, methods, properties.
- `<Nullable>enable</Nullable>`; use `Optional<T>` for "not set" (not `T?` on value types).
- `sealed`, `internal`, `readonly` as the default; `public` only for node and API types.
- Node classes end in `Node`, the vvvv name comes from the attribute (`[ProcessNode(Name = "...")]`). File name = main type.

**Nodes**
- A node is a `[ProcessNode]` class with a constructor taking a hidden `NodeContext` (`[Pin(Visibility = PinVisibility.Hidden)]`) and one `Update(...)` method; it implements `IDisposable`.
- Outputs via `[return: Pin(Name = "Output")]` or `out` parameters, no tuples. Pin defaults as C# parameter defaults or `[DefaultValue]`; optional pins with `[Pin(Visibility = PinVisibility.Optional)]`.
- Change detection instead of rebuilding every frame: compare the input with the remembered state and rebuild only on change. Allocate nothing per frame in hot paths.
- Disposables through `SerialDisposable` / `CompositeDisposable` / `Disposable.Create`; pairs like begin/end or push/pop through `using` or `try/finally`.
- Locks only on a private `syncRoot` object, short bodies. Never call `GC.*`.
- Interop with QtNative: `unsafe` pointers with explicit ownership; platform code behind `OperatingSystem.Is…()` / `[SupportedOSPlatform]`.

**Errors and logging**
- Catch errors from Qt/QML, log them with message templates (`LogWarning("... {Name}", name)`, not string interpolation) and show them at the node (`Error` output or the shared `AddException` helper). Throw only for programming errors (`ArgumentNullException(nameof(x))`, `InvalidOperationException`).
- Log only through the vvvv logger (from the `NodeContext` or `QtApp`: `nodeContext.GetLogger()` / `appHost.LoggerFactory`), so everything, including Qt messages forwarded from QtNative, ends up in vvvv's log window. No `Console.*` or `Debug.Write` in the library. For log files while debugging see the help patch `HowTo Configure logging providers`.

**Documentation**
- `/// <summary>` (English, one sentence in the style "Returns true if …") on every node and every non-trivial pin: it becomes the node help in vvvv. Document `Update` pins with `<param name>`.
- Inline comments in English, short, above the line, explaining why. `// TODO: sentence.` for open points.

## Line endings and encoding

- Everything is LF in the repository. `.gitattributes` enforces it; do not convert line endings by hand and do not commit EOL-only fixes. Checked out, `.vl` and `help/**/*.props` have CRLF (that is how vvvv saves them), everything else LF.
- UTF-8 **with BOM** only for `.cs` and `.vl`; all other files **without** BOM. `.editorconfig` covers VS, Rider and VS Code.
- If a diff shows whole files as changed, the cause is EOL or BOM: `git add --renormalize .` or the script below, do not rewrite content.

`tools/fix-encoding.py` checks and repairs the BOM. A git hook runs it on the staged files at every commit; enable it once per clone:

```sh
git config core.hooksPath .githooks
```

```sh
python tools/fix-encoding.py --all             # repair all tracked files
python tools/fix-encoding.py --check --all     # only report (exit 1 on a wrong BOM)
```

Details: [docs/development.md](docs/development.md#encoding-and-git-hook).

## Debugging

Hang, crash or exception in a running vvvv: take thread stacks and dumps instead of guessing (`dotnet-stack`, `dotnet-dump`, `procdump`, `cdb`; install them only when needed).

- QtNative is `vlqt.dll` (Linux: `libvlqt.so`). Its PDB is in the build output of `src/VL.Qt.Native`: `.sympath+ <path>`, then breakpoints `bp vlqt!<function>`. Managed: `!bpmd VL.Qt.dll <Namespace.Class.Method>`. In a thread dump, filter for `VL.Qt`, `Monitor.Enter` and `Wait`.
- QtNative forwards Qt messages to the vvvv log (`qInstallMessageHandler`) and enables `qt.scenegraph.general` and `qt.rhi.general` through `setFilterRules`.
- `QT_LOGGING_RULES` takes precedence and must be set before vvvv starts, e.g. `qt.scenegraph.*=true;qt.rhi.*=true;qt.qml.*=true`.
- `QT_FATAL_WARNINGS=1` together with `procdump -e` finds the trigger of a warning.

In-app overlays, logging and device loss: [docs/diagnostics.md](docs/diagnostics.md). CPU profiling: [docs/profiling.md](docs/profiling.md).

## Branches and pull requests

- `develop` is the working branch. Branch feature branches from `develop`, open PRs against `develop`, squash-merge.
- `main` only receives releases: PR `develop` → `main` (merge commit, no squash), then a `v*` tag on `main` (CI builds all RIDs, the `.nupkg` and the release). No feature PRs directly to `main`.
- Hotfix for a release: branch from `main`, PR against `main`, then merge `main` into `develop`.
- **Save CI minutes:** every push to a PR that is not a draft starts the Windows CI (QtNative, pack, smoke test). Open PRs as drafts (`gh pr create --base develop --draft`) and keep them drafts while you work. When the work is complete and `tools/build.ps1 -Test` passes locally, run `gh pr ready` (the CI then runs once). If the CI is red afterwards: `gh pr ready --undo`, fix and check locally, `gh pr ready` again. Do not use `workflow_dispatch` as a substitute; it builds every RID and is more expensive.
- Keep PRs focused and describe what changed and how you verified it (which scripts, which help patches).

## Issues

Every issue gets a type label, an area label and, where one fits, a milestone (set them when you create the issue):

- Type: `bug`, `enhancement`, `documentation` or `question`.
- Area: `area:native`, `area:csharp`, `area:native+csharp`, `area:vvvv-patch` or `area:ci`.
- Extra when needed: `manual-check` (needs a human to look at it in vvvv), `ready`.
- Use milestones instead of release labels; do not invent new labels or milestones.
