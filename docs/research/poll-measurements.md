# Poll measurements (research, 2026-11-08)

What the poll of a scene (`vlqt_scene_poll`) costs for static scenes, before and after two changes: measuring only elements whose geometry changed, and skipping `Scene::polish` when nothing changed. The page [Profiling](../profiling.md) states the conclusion; the numbers are here.

## Machine and versions

- Platform: win-x64, Release build, as recorded when the numbers were taken. **arm64 not measured.**
- Machine and Qt version were not recorded.
- Date: 2026-11-08, the date of the two code changes.

## Scenario to rerun

SmokeTest `measure` (`src/VL.Qt.SmokeTest/MeasureTest.cs`): `tools/build.ps1 -Test -Only measure`. The `INFO poll N bound rectangles: ...` lines are in the test output of the scenario (`build/smoke/smoke.trx`). N bound `Rectangle` in one Group, median of 201 polls, a Stopwatch around `vlqt_scene_poll`; the items measured per poll are counted as `EVENT_MEASURED` and `EVENT_SCENE_BOUNDS` records in the poll buffer. The tables are from this scenario; the "Before" columns come from the code before the changes and cannot be reproduced, the "After" columns can.

## Measuring only changed elements

`Measured Size` and `Scene Bounds` of every bound element come from the poll (`collectMeasurements`, `src/VL.Qt.Native/scene/measure.cpp`). Before the change the poll read the geometry of every bound element; after it, only the elements whose geometry changed since the last poll (x/y, size, implicit size, parent, Matrix4x4, anchor, Group frame) and everything below them. Media, error, clicks and user values are read only for element types that have them.

| Scene | Before: poll | Before: measured/poll | After: poll | After: measured/poll |
|---|---|---|---|---|
| 500 static | 0.044 ms | 500 | 0.004 ms | 0 |
| 2000 static | 0.183 ms | 2000 | 0.017 ms | 0 |
| 500, one moving | 0.042 ms | 500 | 0.001 ms | 1 |
| 2000, one moving | 0.186 ms | 2000 | 0.002 ms | 1 |

What remained in the static rows was not the measurement: when Qt did not render in a pump (nothing changed), the poll first ran `Scene::polish` over all elements, which is linear in the element count. In the "one moving" rows Qt rendered, so the poll skips it. A change of an ancestor still measures its whole subtree, as it must (all of it moved).

## Skipping polish when nothing changed

`Scene::polish` returns at once when nothing changed since the last poll: no apply, no marked geometry, no window geometry change and no polish request pending in the window (`itemsToPolish`, where Row/Column, Text implicit size and Group frames register until Qt polishes them in a render). Same scenario, static scenes only (the "one moving" rows did not run it before either):

| Scene | Before: poll | After: poll |
|---|---|---|
| 500 static | 0.0039 ms | 0.0001 ms |
| 2000 static | 0.0171 ms | 0.0001 ms |

The same scenario checks that a poll without a render right after an apply still reports the new layout (Button text in a Row, Column spacing, Text measure) and that the next render agrees with it.
