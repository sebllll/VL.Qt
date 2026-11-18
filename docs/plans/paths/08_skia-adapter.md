# Paths 08: Adapter `SKPath` → `QtPath` (`VL.Qt.Skia`)

Milestone 2.0 · Labels: `enhancement`, `area:csharp`, `area:patch` · Depends on: Paths 04 · Plan: `docs/plans/paths/08_skia-adapter.md`

## Goal
Use Skia's path nodes (boolean operations `Op`, `Simplify`, `SamplePath`, SVG, stars, spirals, …) for VL.Qt: a node `ToQt (SKPath)` converts an `SKPath` into a `QtPath`, in an optional assembly `VL.Qt.Skia`, so VL.Qt itself stays free of Skia.

## Context
- Decision: conversion in C# into the plain path value, so the reconciler/protocol route stays intact (`docs/decisions/paths.md`). This is the preferred route for path math.
- Precedent for an optional companion assembly: `VL.Qt.Stride` (project layout, packaging, help folder `VL.Qt.Stride/help`).
- `SKPath` is mutable: a reference comparison does not detect changes. Convert every frame, or compare content (`SKPath.Iterator` / `GetPoints` + verbs) before building a new `QtPath`.
- Conics (`ConicTo`) become quads (`SKPath.ConvertConicToQuads`); QtNative knows Move, Line, Quad, Cubic, Close.

## Steps
- [ ] Project `VL.Qt.Skia` modelled on `VL.Qt.Stride` (csproj, package metadata, SkiaSharp version as shipped with vvvv).
- [ ] `ToQt (SKPath)` node with content comparison; unit tests for every verb incl. conics and closed contours.
- [ ] Help patch `HowTo Use Skia Paths in Qt.vl` (`Op` of two Skia paths drawn with the Qt `Path` node).
- [ ] Docs: `docs/shapes.md` section, `docs/README.md`.

## Acceptance criteria
The node converts every verb correctly (tests); the help patch passes `vl-patch-check.py` and `vvvvc.ps1`; VL.Qt without the companion has no Skia reference.
