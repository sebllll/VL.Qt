# Paths 09: More `Add…` nodes and `FromSVGPathData`

Milestone 2.0 · Labels: `enhancement`, `area:csharp`, `area:patch` · Depends on: Paths 04 · Plan: `docs/plans/paths/09_more-add-nodes.md`

## Goal
The remaining Skia path generators in `Qt.Paths`: `AddStar`, `AddSpiral`, `AddArrow`, `AddGrid` and `FromSVGPathData`, with the names and pins of the Skia nodes.

## Context
- Builder conventions, change detection and the arc conversion come from package 04 (`docs/plans/paths/04_path-builder.md`).
- `FromSVGPathData`: decide whether it comes through the Skia adapter (package 08) or a parser of our own in VL.Qt (SVG path syntax: M/L/H/V/C/S/Q/T/A/Z, absolute and relative). A parser of our own keeps it usable without Skia.

## Steps
- [ ] Read pins of `AddStar`, `AddSpiral`, `AddArrow`, `AddGrid`, `FromSVGPathData` in `C:\vl\VL.StandardLibs\VL.Skia\VL.Skia.vl`; implement as process nodes with tests.
- [ ] `FromSVGPathData`: decide adapter vs. own parser, record in `docs/decisions/paths.md`; implement with tests against SVG path test strings (incl. arcs and smooth curves).
- [ ] Help patch extension or `HowTo Draw SVG Path Data.vl`; `help/Help.xml`; both checks.

## Acceptance criteria
Nodes with Skia names and pins, tests pass, help patches pass both checks.
