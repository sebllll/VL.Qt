# Paths 07: Mass data beyond about 8000 points

Milestone 2.0 · Labels: `enhancement`, `area:native`, `area:csharp`, `topic:performance` · Depends on: Paths 06 · Plan: `docs/plans/paths/07_mass-data.md`

## Goal
Shapes with more than 8190 points (plots, trails, generative drawings), as far as the renderers can draw them at frame rate.

## Context
- A SET payload is capped at 65535 bytes (u16 `payloadLen`), so 8190 points per element (`docs/decisions/paths.md`).
- Images already travel through a resource route instead of SET (`src/VL.Qt.Native/resources.h`, `image_resource.h`, `docs/images.md`): upload in a worker, budget per frame.
- Benchmarks from `docs/research/02_shapes-spike.md` and package 06 show whether `Curve` or `Geometry` can draw such counts at all; if neither can, the alternative is an Experimental element with its own `QSGGeometryNode` (per-vertex color/width, as in `perf.cpp`).

## Steps
- [ ] Decide with numbers: resource route for point lists, a chunked SET, or an own geometry element; record in `docs/decisions/paths.md`.
- [ ] Spike the chosen route with 100 000 points changing every frame; measure.
- [ ] Split into implementation packages (protocol, native, C#, help patch) and add them as issues.

## Acceptance criteria
A decision with numbers in `docs/decisions/paths.md` and follow-up issues; the 8190 limit in tooltips and help patches updated once implemented.
