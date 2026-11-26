# Pump spikes (research, 2026-11-08)

Which phase of a pump the rare long frames come from, measured by replaying an animated scene. The page [Diagnostics](../diagnostics.md#pump-spikes-where-they-come-from) states the conclusion; the numbers are here.

## Machine and versions

- Platform: win-x64, Direct3D 11, flip model, maximum frame latency 2, as recorded when the numbers were taken. **arm64 not measured.**
- Machine and Qt version were not recorded.
- Date: 2026-11-08, the date of the replay change.

## Scenario to rerun

SmokeTest `replay-cli` (explicit, `src/VL.Qt.SmokeTest/ReplayTest.cs`) with the built-in trace `anim`: moving semi-transparent rectangles plus texts that change every frame, played at 60 fps. The parameters `ReplayTrace` and `ReplayArgs` come from the run settings or from the environment variables `VLQT_SMOKE_REPLAYTRACE` and `VLQT_SMOKE_REPLAYARGS` (`src/VL.Qt.SmokeTest/Framework/SmokeParameters.cs`):

```powershell
$env:VLQT_SMOKE_REPLAYTRACE = 'anim:3600:300'
$env:VLQT_SMOKE_REPLAYARGS = '--paced --loop 5'
dotnet test src/VL.Qt.SmokeTest -c Release --filter TestCategory=replay-cli
```

`anim[:frames[:rects]]` sets the length and the rectangle count; `--paced` pumps like the vvvv main loop, `--loop N` plays the scene N times with a new window per round, `--load N` keeps N threads busy on the CPU. The run attributes every spike above 8 ms to its largest phase (apply, advance, events, sync, render, present) and ends with a `REPLAY spike attribution` line and the number of pumps whose present took longer than 4 ms. The rows below were taken with swap interval 0 (`VSync` off, so that present does not wait for the refresh), 3600 frames per run.

## Result

| Run | Pump median | Spikes > 8 ms (steady) | Dominant phase | Present > 4 ms |
|---|---|---|---|---|
| 300 rectangles | 0.75 ms | 1 | present (once after the window was shown) | 1 of 3591 pumps |
| 4000 rectangles (GPU-heavy) | 4.9 ms | 1 | present (once after the window was shown) | 1 of 3591 pumps |
| 300 rectangles, every core busy | 0.69 ms | 13 | apply 8, events 4, present 1 (once after show) | 1 of 3591 pumps |

- With `VSync` off, present does not block in steady operation. Outside start-up it stays at 0.1 ms or less, also with a GPU-heavy scene or a fully loaded CPU.
- The only slow present comes once per window, 10 to 25 frames after it is shown: it takes 10 to 32 ms, and the frame interval of that pump is about 40 ms. The window is then going through its first frames with the compositor (5 replays in a row: once in every round).
- The spikes under CPU load are in the C# apply and in the Qt event phase (`events`: posted events, polish, animation ticks): the thread was preempted, nothing is waiting on the GPU or the compositor. A frame latency waitable object, a tearing present or a present outside the pump would not remove them.
