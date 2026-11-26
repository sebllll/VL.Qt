# Debugging

Hang, crash or exception in a running vvvv: take thread stacks and dumps instead of guessing (`dotnet-stack`, `dotnet-dump`, `procdump`, `cdb`; install them only when needed). The tools inside the app (overlays, logging, GPU device loss) are in [Diagnostics](../diagnostics.md); CPU profiling is in [Profiling](profiling.md).

- QtNative is `vlqt.dll` (Linux: `libvlqt.so`). Its PDB is in the build output of `src/VL.Qt.Native`: `.sympath+ <path>`, then breakpoints `bp vlqt!<function>`. Managed: `!bpmd VL.Qt.dll <Namespace.Class.Method>`. In a thread dump, filter for `VL.Qt`, `Monitor.Enter` and `Wait`.
- QtNative forwards Qt messages to the vvvv log (`qInstallMessageHandler`) and enables `qt.scenegraph.general` and `qt.rhi.general` through `setFilterRules`.
- `QT_LOGGING_RULES` takes precedence and must be set before vvvv starts, e.g. `qt.scenegraph.*=true;qt.rhi.*=true;qt.qml.*=true`.
- `QT_FATAL_WARNINGS=1` together with `procdump -e` finds the trigger of a warning.
- For log files while debugging see the help patch `HowTo Configure logging providers`.
