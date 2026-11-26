# AGENTS.md

Instructions for coding agents and contributors working on VL.Qt. The conventions are tool-neutral: any agent or person can follow them. `CLAUDE.md` only imports this file for Claude Code; personal additions go in `CLAUDE.local.md`, which stays out of this repository.

## What this repo is

VL.Qt brings Qt 6 (Qt Quick) to vvvv gamma as a library, focused on 2D graphics content (motion, typography, layout). Three layers:

- `src/VL.Qt/` – C# library: element types, vvvv nodes, reconciler (element tree diffing), interop.
- `src/VL.Qt.Native/` – **QtNative**, the C++ layer that owns Qt and the render thread (`vlqt.dll`, `libvlqt.so`).
- `help/Qt/` – help patches (`HowTo …`), the test base for nodes and the user-facing examples.

Tests live in `src/VL.Qt.Tests` (unit) and `src/VL.Qt.SmokeTest` (QtNative without vvvv). How the pieces fit: [docs/contributing/architecture.md](docs/contributing/architecture.md). Adding an element through all layers: [docs/contributing/add-an-element.md](docs/contributing/add-an-element.md). Index of all docs: [docs/README.md](docs/README.md).

## Setup and build

Prerequisites, first build, the Visual Studio solution and every script are described in [docs/contributing/development.md](docs/contributing/development.md). The first build in short, on Windows (PowerShell 7):

```powershell
tools/setup-qt.ps1          # once: Qt for your machine's architecture into C:\Qt
tools/build.ps1             # QtNative + Qt deploy + C# library
tools/start-vvvv.ps1 -Help "Draw a Rectangle"
```

- Check the architecture, the vvvv lookup and the Linux build there too ([development.md](docs/contributing/development.md#first-build), [linux.md](docs/contributing/linux.md)).
- After changes in QtNative, rebuild it and restart vvvv (it does not hot-reload native code); use `-SkipNative` for C#-only changes.

## Tests

All commands (the full gate, one scenario, unit tests, SmokeTest, drift checks) and what they write are in [development.md](docs/contributing/development.md#build-and-test-step-by-step). Protocol changes follow [Change the protocol](docs/contributing/development.md#typical-workflows).

- A change is done when the affected help patch, unit tests and SmokeTest pass (`tools/build.ps1 -Test` runs all of them).

## Checking patches

The help patches in `help/Qt/` (one `HowTo …` per element) are the test base. When you change a node's pins or behavior, adapt the help patch, then check and compile-check it:

```powershell
python tools/vl-patch-check.py "help/Qt/HowTo Draw Text.vl"
tools/vvvvc.ps1 -File "help/Qt/HowTo Draw Text.vl"       # run dotnet build src/VL.Qt -c Release first
```

Both must pass (exit code 0). What the scripts check, `vl-patch-mapping.txt` for renamed nodes and pins, and the rules for writing a help patch are in [docs/contributing/help-patches.md](docs/contributing/help-patches.md) and the [scripts table](docs/contributing/development.md#scripts).

- Renamed nodes and pins and moved categories reach the patches only through `tools/vl-patch-mapping.txt` and `tools/vl-patch-migrate.py`, never by hand ([Renames](docs/contributing/help-patches.md#renames)).
- A `.vl` file written directly keeps the format as vvvv saves it (new unique `Id`s, CRLF, UTF-8 with BOM, unchanged elements untouched) and gets the `manual-check` label until someone has looked at it in vvvv.
- An IOBox on a pin only where its value differs from the default; never one on `Interaction`.
- Text scheme (title, one feature field, content, `Notes:`): [docs/contributing/help-patches.md](docs/contributing/help-patches.md#text-scheme).
- Every patch under `help/` is listed in `help/Help.xml`; add, rename or delete the line together with the patch.

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
- Set a default explicitly unless `default(T)` is the intended value (`false`, `0`, `null`, the first enum member): then leave the pin without one, never write `[DefaultValue(false)]` or `[DefaultValue(0f)]`. Write it as `[DefaultValue(...)]` on the input, in the same list as `Pin` (`[Pin(Visibility = PinVisibility.Optional), DefaultValue(1f)]`), not as a C# parameter default; keep the `out` parameters last (vvvv orders inputs and outputs separately, so the pin order does not change). The form depends on the type (a form that vvvv cannot read silently gives `default(T)`; checked in the C# that vvvvc generates):
  - Enums: `[DefaultValue(typeof(E), nameof(E.X))]`. `Int2` also takes `[DefaultValue(typeof(Int2), "1920, 1080")]`.
  - Other Stride structs (`Vector2`, `Color4`, `RectangleF`): the IOBox string form `[DefaultValue("1, 1, 1, 1")]`, invariant culture. The `typeof` form gave `default(T)` for `Color4` and `RectangleF` when checked.
  - `Matrix`: never an attribute; vvvv's default of a `Matrix` pin is the identity.
- Pin names come from the C# names (`strokeColor` is "Stroke Color"). Add `[Pin(Name = "...")]` only where the derived name has the wrong spelling, spacing or an acronym, and when renaming a C# parameter of an existing pin (a changed pin name breaks patches).
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
- Pin texts that several nodes share (Size, Transform, Anchor, Opacity, Clip, Debug, Interaction, Effect, Enabled, Measured Size, Scene Bounds, the widget channel, `<returns>` of an element) live once in `src/VL.Qt/Nodes/CommonPins.xml`, in a set per family of nodes. A node takes one with `/// <include file="../CommonPins.xml" path="docs/set[@name='element']/param[@name='opacity']"/>` in place of the `<param>` (the include replaces the whole `<param>`; the path is relative to the source file). Change the text there, not at the nodes; a text only one node needs stays at the node. The pins of generated nodes (`QmlNodeLayout`, `ShaderNodeDescription`) repeat the shared texts as strings; tests compare them with the XML. Write for the patcher: no Qt type names, no implementation facts.
- Inline comments in English, short, above the line, explaining why. `// TODO: sentence.` for open points.

## Line endings and encoding

- Everything is LF in the repository (`.gitattributes`); `.vl` and `help/**/*.props` are checked out with CRLF, as are new `.bat`/`.cmd` files. Do not convert line endings by hand and do not commit EOL-only fixes.
- UTF-8 **with BOM** only for `.cs` and `.vl`; all other files **without** BOM.
- Do not change the BOM or trailing whitespace of lines you do not edit.
- If a diff shows whole files as changed, the cause is line endings or BOM: `git add --renormalize .` or `tools/fix-encoding.py`, do not rewrite content.

A git hook repairs the BOM at every commit; enable it once per clone, see [Encoding and git hook](docs/contributing/development.md#encoding-and-git-hook) (also: multi-line string literals, the check script).

## Debugging

Hang, crash or exception in a running vvvv: take thread stacks and dumps instead of guessing (`dotnet-stack`, `dotnet-dump`, `procdump`, `cdb`; install them only when needed).

- QtNative is `vlqt.dll` (Linux: `libvlqt.so`). Its PDB is in the build output of `src/VL.Qt.Native`: `.sympath+ <path>`, then breakpoints `bp vlqt!<function>`. Managed: `!bpmd VL.Qt.dll <Namespace.Class.Method>`. In a thread dump, filter for `VL.Qt`, `Monitor.Enter` and `Wait`.
- QtNative forwards Qt messages to the vvvv log (`qInstallMessageHandler`) and enables `qt.scenegraph.general` and `qt.rhi.general` through `setFilterRules`.
- `QT_LOGGING_RULES` takes precedence and must be set before vvvv starts, e.g. `qt.scenegraph.*=true;qt.rhi.*=true;qt.qml.*=true`.
- `QT_FATAL_WARNINGS=1` together with `procdump -e` finds the trigger of a warning.

In-app overlays, logging and device loss: [docs/diagnostics.md](docs/diagnostics.md). CPU profiling: [docs/contributing/profiling.md](docs/contributing/profiling.md).

## Branches and pull requests

Branches, pull requests, the fork and maintainer paths and the issue labels are in [CONTRIBUTING.md](CONTRIBUTING.md). What an agent must not forget:

- Work on a branch from `develop` and open the pull request as a draft against `develop`; run `gh pr ready` only after `tools/build.ps1 -Test` passes locally (every push to a non-draft PR starts the CI).
- Never push to `develop` or `main`, never a bare `git push`: name remote and branch (`git push origin HEAD:<kind>/<issue>-<topic>`).
- Every new issue gets a type, an area and a milestone from the labels in [CONTRIBUTING.md](CONTRIBUTING.md#issue-labels-for-maintainers); do not invent new labels or milestones.
