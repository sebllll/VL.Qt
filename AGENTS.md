# AGENTS.md

Instructions for coding agents and contributors working on VL.Qt. The conventions are tool-neutral: any agent or person can follow them. `CLAUDE.md` only imports this file for Claude Code; personal additions go in `CLAUDE.local.md`, which stays out of this repository.

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
dotnet test src/VL.Qt.Tests --filter "FullyQualifiedName~DriftTests|FullyQualifiedName~NativeLayoutTests"   # protocol.h, vlqt.h and their C# mirrors agree?
```

- `tools/build.ps1 -Test` writes the SmokeTest log and grabbed PNGs to `build/smoke/`; exit code 0 means every step passed. Look at the PNGs when you change rendering.
- Rebuild QtNative (`tools/build-native.ps1` or `tools/build.ps1`) whenever you touch `src/VL.Qt.Native/`. Use `-SkipNative` for C#-only changes.
- Protocol changes: edit `src/VL.Qt.Native/protocol.h`, mirror in `src/VL.Qt/Wire/Protocol.cs` by hand, run the drift tests above. They compare constants and fixed wire sizes (`ProtocolDriftTests`), the struct layouts of `vlqt.h` (`NativeLayoutTests`, with the `static_assert`s in `app/app.cpp`) and the `VLQT_API` exports with the `[DllImport]`s (`ApiDriftTests`); none needs a GPU.
- A change is done when the affected help patch, unit tests and SmokeTest pass.

## Checking patches

`tools/vvvvc.ps1 [-File <doc.vl>]` compiles/exports a patch with vvvv's command-line compiler (default `help/HelloWorld.vl`, output in `build/vvvvc/`). Exit code 0 means no build errors. Run `dotnet build src/VL.Qt -c Release` first. The script uses the newest vvvv as described under Setup; on ARM64 it adds a shim `vvvvc.arm64.dll` to the vvvv installation (the 8.0 ARM64 previews ship an x64 `vvvvc.dll`).

The help patches in `help/Qt/` (one `HowTo …` per element) are the test base. When you change a node's pins or behavior, adapt the help patch, then check and compile-check it:

```powershell
python tools/vl-patch-check.py "help/Qt/HowTo Draw Text.vl"   # no argument: all help/Qt/*.vl, help/*.vl and VL.Qt.Stride/help/*.vl
tools/vvvvc.ps1 -File "help/Qt/HowTo Draw Text.vl"
```

`tools/vl-patch-check.py` is read-only: it compares the VL.Qt nodes in a patch with their pins in the C# sources (pin catalog `tools/vl_pin_catalog.py`, create pins included) and never moves or deletes anything. Errors (exit code 1): a hidden pin that holds a non-default value or a link (`hidden-non-default`; `--fix` makes such pins visible, its only change), an IOBox on `Interaction` (`forbidden-iobox`), a link to a pin the node no longer has (`missing-pin`). Hint (exit code 0): an input IOBox that holds the pin's default (`default-iobox`). Intended exceptions go into the baseline `help/.padlint.json` (patch, element Id, rule, reason) with `--accept <patch>`; add a short reason to each new entry. `tools/test-vl-patch-check.py` is its regression test.

Renamed nodes and pins and moved categories reach the patches through one mapping file, never by hand: add rows `step | kind | scope | old | new` to `tools/vl-patch-mapping.txt` (format in its header) and run `python tools/vl-patch-migrate.py` (`--check` lists what would change and writes nothing). It rewrites every patch of the repository at once and changes only the names a row gives, in the format vvvv saves. The rows of one step apply together, so a swap works, and a second run changes nothing; on a merge conflict in a `.vl` file, merge the mapping file, take either side of the patch and run the script again. Exit code 2 means a bad, conflicting or unknown row (new names are looked up in `src/VL.Qt.Tests/Api/node-api.txt`) or a node whose state cannot be told; nothing is written then. Details: [docs/help-patches.md](docs/help-patches.md#renames). `tools/test-vl-patch-migrate.py` is its regression test.

Rules for help patches:

- `.vl` files (vvvv's XML format) may be written in vvvv or directly, by an agent or a script. A direct edit keeps the format as vvvv saves it: a new unique `Id` for every new element, CRLF, UTF-8 with BOM, and the elements, `Id`s and positions it does not change left as they are. Afterwards `tools/vl-patch-check.py` and `tools/vvvvc.ps1 -File` must pass. A help patch written or rearranged directly gets the `manual-check` label until someone has looked at it in vvvv.
- Put an IOBox on a pin only if its value differs from the default and says something in the help patch. An IOBox at a pin is the same to the node as an attached value or channel. Example: an IOBox at `Interaction` makes the element opaque to input, so never put one there.
- Animate (LFO) only where it serves the element (motion demos); otherwise use fixed values.
- Layout: compact regions, content stacked top to bottom in data-flow order, IOBoxes as a staircase above (inputs) and below (outputs) their node. Choose defaults so the effect is visible.
- Text scheme: title (size 25), directly below ONE terse feature field (headline plus 3 to 5 short bullets), then the content, the longer notes in one `Notes:` comment at the very bottom. Details and example: [docs/help-patches.md](docs/help-patches.md).
- Every patch under `help/` is listed in `help/Help.xml` (Help Browser topics); add, rename or delete the line together with the patch.
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
- A node is a `[ProcessNode]` class with a constructor taking a `NodeContext` (vvvv hides it by its type, so no `Hidden` attribute) and one `Update(...)` method; it implements `IDisposable`.
- Outputs via `[return: Pin(Name = "Output")]` or `out` parameters, no tuples. Optional pins with `[Pin(Visibility = PinVisibility.Optional)]`.
- Set every default explicitly, as a C# parameter default or `[DefaultValue]`; for structs use the type/string form, e.g. `[DefaultValue(typeof(Int2), "1920, 1080")]`. A pin without one gets the type's default, which is rarely what the help patch needs.
- Pin names come from the C# names. Add `[Pin(Name = "...")]` where that would get the spelling, spacing or an acronym wrong, and when renaming a C# parameter of an existing pin (a changed pin name breaks patches).
- The assembly imports its public types with `[assembly: ImportAsIs(Namespace = "VL.Qt", Category = "Qt")]` (`src/VL.Qt/Properties/AssemblyInfo.cs`; VL.Qt.Stride and VL.Qt.Audio the same with their namespace): a public type that is not a node appears under the category of its namespace (`VL.Qt.Elements.ShapeCap` in `Qt.Elements`), so it sits in the namespace of its category; nodes set theirs with `Categories.*`, which stays absolute. Types no pin carries are `internal`. Of the public ones, what the node browser shows is controlled with `[Smell(SymbolSmell.Hidden)]` or `[Smell(SymbolSmell.Advanced)]` on the narrowest symbol; never hide a type that the pins of a visible node use. A node or pin is renamed or removed only after `[Smell(SymbolSmell.Obsolete)]`, unstable nodes get `[Smell(SymbolSmell.Experimental)]`; the rules are in [docs/versioning.md](docs/versioning.md). `ShaderNodeFactory` stays a node factory: its node set comes from the shader files in `qtshaders/` folders found at runtime.
- Change detection instead of rebuilding every frame: compare the input with the remembered state and rebuild only on change. Allocate nothing per frame in hot paths.
- Disposables through `SerialDisposable` / `CompositeDisposable` / `Disposable.Create`; pairs like begin/end or push/pop through `using` or `try/finally`.
- Locks only on a private `syncRoot` object, short bodies. Never call `GC.*`.
- Interop with QtNative: `unsafe` pointers with explicit ownership; platform code behind `OperatingSystem.Is…()` / `[SupportedOSPlatform]`.

**Errors and logging**
- Catch errors from Qt/QML and report them through the node's `NodeErrors` (`src/VL.Qt/Runtime/NodeErrors.cs`): one entry per phase, logged once per distinct text with a message template (`LogWarning("... {Name}", name)`, not string interpolation) and shown at the node; a message source holds a `NodeMessage`, the resource and file nodes use `ResourceNodeReport`. No `Error` output pins. Throw only for programming errors (`ArgumentNullException(nameof(x))`, `InvalidOperationException`).
- Log only through the vvvv logger (from the `NodeContext` or `QtApp`: `nodeContext.GetLogger()` / `appHost.LoggerFactory`), so everything, including Qt messages forwarded from QtNative, ends up in vvvv's log window. No `Console.*` or `Debug.Write` in the library. For log files while debugging see the help patch `HowTo Configure logging providers`.

**Documentation**
- `/// <summary>` (English, one sentence in the style "Returns true if …") on every node and every non-trivial pin: it becomes the node help in vvvv. Document `Update` pins with `<param name>` (also the `out` pins; the name must match the C# parameter) and the return pin with `<returns>`. `Summary` in `[ProcessNode]` does not replace the pin documentation.
- Inline comments in English, short, above the line, explaining why. `// TODO: sentence.` for open points.

## Line endings and encoding

- Everything is LF in the repository. `.gitattributes` enforces it; do not convert line endings by hand and do not commit EOL-only fixes. Checked out, `.vl` and `help/**/*.props` have CRLF (that is how vvvv saves them), `*.bat`/`*.cmd` CRLF (for `cmd.exe`), everything else LF. Write new `.bat`/`.cmd` files with CRLF: they run from disk before git normalizes them.
- Multi-line verbatim or raw string literals in C# get the line ending of the file. Do not compare them, or checked-in expected-output files, byte for byte with generated text; normalize line endings first.
- Do not change the BOM or trailing whitespace of lines you do not edit.
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
- Every change lands through a PR, never by a push to `develop` or `main`. Contributors push their branch to a fork; maintainers push it to this repository, always with remote and branch named (`git push origin HEAD:<kind>/<issue>-<topic>`), since a bare `git push` may go to another remote or branch. Details: [CONTRIBUTING.md](CONTRIBUTING.md).
- `main` only receives releases: PR `develop` → `main` (merge commit, no squash), then a `v*` tag on `main` (CI builds all RIDs, the `.nupkg` and the release). No feature PRs directly to `main`.
- Hotfix for a release: branch from `main`, PR against `main`, then merge `main` into `develop`.
- **Save CI minutes:** every push to a PR that is not a draft starts the Windows CI (QtNative, pack, smoke test). Open PRs as drafts (`gh pr create --base develop --draft`) and keep them drafts while you work. When the work is complete and `tools/build.ps1 -Test` passes locally, run `gh pr ready` (the CI then runs once). If the CI is red afterwards: `gh pr ready --undo`, fix and check locally, `gh pr ready` again. Do not use `workflow_dispatch` as a substitute; it builds every RID and is more expensive.
- Keep PRs focused and describe what changed and how you verified it (which scripts, which help patches).

## Issues

Use the issue forms in `.github/ISSUE_TEMPLATE/` (bug report, feature request, task). Every issue gets labels and a milestone when it is created or triaged:

- Type (exactly one): `bug`, `enhancement`, `documentation` or `question` (a spike or a decision).
- Area (at least one): `area:native`, `area:csharp`, `area:patch` or `area:ci`. An issue that touches native and C# gets both labels. Repository-wide documentation (README, licensing, contributing) needs no area.
- Topic (at most one, where one fits): `topic:effects`, `topic:images`, `topic:video`, `topic:styles`, `topic:input`, `topic:linux` or `topic:performance`.
- Extra when needed: `manual-check` (needs a human to look at it in vvvv), `ready`, `good first issue`, `help wanted`.
- Milestone: version numbers only. `1.0` blocks the release, `2.0` is urgent but does not block it, `3.0` holds distant features and ideas without a date. Until 1.0 is released the package stays at 0.9.x and every issue that has to be done goes into `1.0`.
- Do not invent new labels or milestones. Close issues that will not be done as "not planned", duplicates as "duplicate".
