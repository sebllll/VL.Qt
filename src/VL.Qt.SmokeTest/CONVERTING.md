# Converting a SmokeTest scenario to NUnit (W1-01, temporary)

This file (and `checks-diff.py`) lives only while W1-01 (#169) runs; both are deleted with the last conversion.

The SmokeTest is an NUnit project. Qt starts once per test run on a dedicated STA thread (`Framework/QtHost.cs`), every
scenario body runs there (`Scenario.Run`). Not yet converted scenarios still run through the legacy adapter
`Framework/LegacyScenarios.cs`: one table row per scenario, in the order of the former `Program.Main`, each a test case
named and categorized by the old scenario name. Converting a scenario = rewriting its file + deleting its row.

Examples already converted: `CoreTest.cs` (the former inline checks: `core-app`, `core-rect`, `core-perf`, `core-log`),
`RectangleTest.cs`, `DeviceLostTest.cs` (own process), `ProtocolDrift.cs` (`drift`, no GPU), `TearingTest.cs` (explicit).

## Recipe for one file

1. Make the class an NUnit fixture and add one test per former entry point. The category is the old scenario name
   (exactly, including case: `Msaa`, `QtTextureInput`, `clear-pin`); it is the `--only` name of the old runner and the
   scenario column of `build/smoke/checks.tsv`.

   Before:
   ```csharp
   static class RectangleTest
   {
       static System.Drawing.Color Px(Bitmap a, int x, int y) => ...;   // per-file copies
       static bool Near(...) => ...;
       static bool IsBg(...) => ...;

       public static void Run(string outDir, Action<bool, string> check, Action<int> pump, Func<IntPtr, string, Bitmap> grab)
       {
           check(w != IntPtr.Zero, "05b: window_create");
           pump(3);
           using (var a = grab(w, Path.Combine(outDir, "05_rect.png"))) { ... }
   ```
   After:
   ```csharp
   using NUnit.Framework;
   using static Pixels;

   [TestFixture, NonParallelizable]
   public static class RectangleTest
   {
       /// <summary>Color, radius and border of a rectangle, errors, and the cost of 1000 rectangles.</summary>
       [Test, Category("rectangle")]
       public static void Rectangle() => Scenario.Run(Run);

       static void Run(Scenario s)
       {
           s.Check(w != IntPtr.Zero, "window_create");
           s.Pump(3);
           using (var a = s.Grab(w, "05_rect.png")) { ... }
   ```
   A file with several entry points (e.g. `HandlerTest.RunTap`, `RunPoint`, ...) gets one `[Test, Category("tap")]`
   method per entry point; an entry point that ran two methods (`interaction-chain`) becomes one test that calls both.
   The fixture class must be `public static class`; the bodies stay `static` (private or internal).

2. Replace the delegates by the `Scenario` members:

   | old | new |
   |---|---|
   | `check(ok, what)` | `s.Check(ok, what)` (soft: all checks run, failures are collected) |
   | `pump(n)` | `s.Pump(n)` |
   | `grab(w, Path.Combine(outDir, "x.png"))` | `s.Grab(w, "x.png")` (relative path = grab folder; keep the file name) |
   | `outDir` | `s.OutDir` |
   | `log()` / `() => s_log.ToString()` | `s.Log` |
   | `Console.WriteLine($"INFO ...")` | `s.Info($"...")` |
   | `Console.WriteLine($"SKIP ...")` | `s.Skip($"...")` |
   | `Console.WriteLine($"WARN ...")` | `s.Warn($"...")` (NUnit warning, does not fail) |
   | `Program.NeedsRhi(check, why)(ok, what)` | `s.NeedsRhi(ok, what, why)` |
   | `Program.PositionByWindowManager(check)(ok, what)` | `s.CheckPosition(ok, what)` |
   | `Program.QuickSoftware`, `Program.Wslg` | `s.QuickSoftware`, `QtHost.Wslg` |
   | `PerfLimits.Below(check, ...)` | `PerfLimits.Below(s, ...)` |
   | `stressSeconds`, `stressSeed`, other options | `SmokeParameters.StressSeconds`, `SmokeParameters.StressSeed`, ... |

   Helper methods that took `Action<bool, string> check` / `Action<int> pump` take `Scenario s` instead. Public helpers
   that **other** files call (e.g. `PumpTest.HighResTimer`, `SnapshotTest`/`PerfOverlayTest`/`QtTextureTest` helpers,
   `GeometryExtensions`) keep their signature unless every caller is in your group.
   Other console lines (`TEARING ...`, `REPLAY ...`) may stay: the console of a scenario goes to the test output.

3. Pixels (TEST-12): delete the per-file `Px`, `Near`, `IsBg`, `IsOrange`, `SameImage` that match `Framework/Pixels.cs`
   (800x600, tolerance 8 exclusive, #202020) and add `using static Pixels;`. A helper with other values (another window
   size, another tolerance) stays local or uses the overloads `Px(a, x, y, w, h)` / `Near(c, r, g, b, tolerance)`; a local
   method of the class wins over the `using static` import.

4. Messages: drop the plan-step tags at the start of a message (`"05b: "`, `"367: "`, `"116: "`, `"(04e) "`, `"INFO 05b "`)
   and keep the rest of the text **unchanged**: the verifier compares the check lines with the baseline modulo that prefix.
   The scenario name in a failure comes from the NUnit test.

5. Delete the row of the scenario from `LegacyScenarios.All` (with its `#if WINDOWS` around it, if any). If the file no
   longer uses `Program.*`, nothing else changes; the `Program` forwarders go with the last conversion.

6. Special kinds:
   - **Explicit** (ran only when named): `[Test, Explicit("reason"), Category("name")]`.
   - **Own process** (needs its own Qt: env before the first window, thread mode, render-thread count): the test is
     `Scenario.RunLocal(s => OwnProcess.Run(s, new[] { "name" }, env, outDir, expectInQtLog))`; register the body in
     `OwnProcess.s_bodies` (`["name"] = XTest.Run`) so the helper executable finds it. See `DeviceLostTest.cs` and the
     rows `qttexture-render-thread` / `qttexture-main-thread` / `qttextureperf`.
   - **No GPU**: `Scenario.RunLocal(body)` (Qt is not started); see `ProtocolDrift.cs`.
   - `ClearPinTest` (leaves two VSync windows open) and `ResourceTest.LeaveRunningJobs` (busy workers at process end) ran
     last in the old runner. NUnit runs fixtures in the alphabetical order of their names (observed, not guaranteed;
     `[Order]` only orders within a fixture, and ordered tests run *before* unordered ones). Put both tests into one fixture
     `ZzLastTest` (file `ZzLastTest.cs`) so they stay last, and say in the PR that the order rests on that convention.

7. Allocation checks (`GC.GetAllocatedBytesForCurrentThread`): keep the measured loop in a small method of its own. In
   `core-perf` the same loop inside the scenario lambda allocated 80 bytes per iteration (JIT on-stack replacement of a long
   method with captured locals), outside it allocates 0.

8. Encoding: `.cs` files are UTF-8 with BOM, LF (`python tools/fix-encoding.py --check --all`).

## Running

```powershell
dotnet build src/VL.Qt.SmokeTest -c Release
dotnet test src/VL.Qt.SmokeTest -c Release --no-build --filter TestCategory=rectangle                  # one scenario
dotnet test src/VL.Qt.SmokeTest -c Release --no-build --filter "TestCategory=tap|TestCategory=point"   # several
dotnet test src/VL.Qt.SmokeTest -c Release --no-build --filter TestCategory=tearing -- "TestRunParameters.Parameter(name=\"TearingArgs\", value=\"--windows 2 --seconds 10\")"
dotnet test src/VL.Qt.SmokeTest -c Release --no-build --list-tests
```

- Results: test output per test (`--logger "console;verbosity=detailed"` shows the PASS/FAIL lines), grabs in
  `build/smoke/grabs/`, the Qt log in `build/smoke/smoke.log`, one line per check in `build/smoke/checks.tsv`
  (`scenario TAB status TAB message`; rewritten by each run). A helper process writes its Qt log to `build/smoke/<name>.log`.
- Parameters (run settings or environment `VLQT_SMOKE_<NAME>`): `SmokeDir`, `OutDir`, `PerfFactor`, `PerfWarnOnly`,
  `StressSeconds`, `StressSeed`, `ScenarioTimeoutSeconds` (default 600), `TearingArgs`, `ReplayTrace`, `ReplayArgs`,
  `OverlayCostRounds` (`Framework/SmokeParameters.cs`). `VLQT_PERF_FACTOR` / `VLQT_PERF_WARN_ONLY` still work.
- Compare with the baseline: `G:\VL\VL.QT\build\baseline\w1-01\checks.tsv` (`mode TAB scenario TAB status TAB message`;
  its scenario column is derived heuristically, compare the status+message multiset per scenario first).
- Expected differences to the baseline: plan-step prefixes, absolute paths (worktree), pointer values; the QtTexture mode
  runs no longer repeat the core checks (`app_init`, drift, `log shows D3D11`) but check their thread-mode line
  (`Qt log shows '...'`); `core-log` opens a window of its own (one more `window_create` / `apply test scene`).
  `python src/VL.Qt.SmokeTest/checks-diff.py <baseline checks.tsv> build/smoke/checks.tsv [scenario to ignore ...]` lists
  the (status, message) lines only on one side, with paths, numbers and the prefix normalized (temporary, goes with this file).
- A timing check may fail while another GPU job runs on the machine; rerun the scenario alone before you look for a bug.

## Groups for parallel workers

Files that share helpers are in one group. Sizes are about equal (KB of source).

**Group 1 – input, controls, windows (~320 KB)**
`HandlerTest.cs`, `HandlerTest.Latency.cs`, `HandlerTest.Multitouch.cs`, `InteractionTest.cs`,
`InteractionTest.Multitouch.cs`, `InteractionTest.Pointer.cs`, `ButtonTest.cs`, `SpinBoxTest.cs`, `ValueInputsTest.cs`,
`KeyRouteTest.cs`, `MeasureTest.cs`, `PumpTest.cs`, `StyleTest.cs`, `StylableTest.cs`, `MultiPlacementTest.cs`,
`RendererTest.cs`, `BorderlessTest.cs`, `FullscreenTest.cs`, `WindowIconTest.cs`
(`PumpTest` helpers are used by groups 2 and 4: keep their signatures.)

**Group 2 – QtTexture, offscreen, snapshots, overlays (~330 KB)**
`QtTextureTest.cs`, `QtTextureTest.Input.cs`, `QtTextureTest.Leak.cs`, `QtTextureTest.Render.cs`,
`QtTextureTest.Robust.cs`, `D3DDebugLayer.cs`, `QtTexturePerfTest.cs`, `ClearPinTest.cs`, `MultiRendererTest.cs`,
`MsaaTest.cs`, `SharedDeviceTest.cs`, `SnapshotTest.cs`, `TooltipTest.cs`, `GroupFrameTest.cs`, `PerfOverlayTest.cs`
(includes the own-process rows `qttexture-render-thread`, `qttexture-main-thread`, `qttextureperf` and the explicit
`overlay-cost`.)

**Group 3 – effects and shaders (~315 KB)**
`EffectLayerTest.cs`, `EffectMaskTest.cs`, `EffectChainTest.cs`, `EffectChainCommands.cs`, `EffectPassesTest.cs`,
`GlowTest.cs`, `InnerShadowTest.cs`, `BlurMaskTest.cs`, `DisplaceTest.cs`, `BlendTest.cs`, `BlendChainTest.cs`,
`MotionBlurTest.cs`, `SamplerTest.cs`, `ShaderEffectTest.cs`, `ShaderSourceTest.cs`, `GroupShaderSourceTest.cs`,
`GroupShaderWindowDestroyTest.cs`, `OpacityZeroTest.cs`, `QmlTest.cs`, `ReadmeImagesTest.cs`, `ReadmeGalleryTest.cs`

**Group 4 – elements, images, video, resources, layout (~330 KB)**
`ApplyTest.cs`, `ElementsTest.cs`, `ShapeTest.cs`, `TextTest.cs`, `ImageTest.cs`, `ImagePreviewTest.cs`,
`ImageShareTest.cs`, `ImageResourceTest.cs`, `ImageWriteTest.cs`, `QtImageTest.cs`, `VideoTest.cs`, `VideoAudioTest.cs`,
`VideoStopTest.cs`, `ReplayTest.cs`, `RowColumnTest.cs`, `SizeResetTest.cs`, `ReconcilerTest.cs`, `ContainerNodeTest.cs`,
`ResyncTest.cs`, `AnchorTest.cs`, `DebugOverlayTest.cs`, `PerfStatsTest.cs`, `ResourceTest.cs`, `ResourceStressTest.cs`,
`RestartTest.cs`, `FontTest.cs`, `SpaceTest.cs`
(includes the explicit rows `resource-stress`, `replay-cli`, `video-stop-repro` and the row `resources-leave-jobs`.)

`GeometryExtensions.cs`, `PngImage.cs`, `PerfLimits.cs` and `Framework/` are shared: change them only together with the
orchestrator. `LegacyScenarios.cs` is edited by every group (one row deletion each): merge conflicts there are expected
and trivial.
