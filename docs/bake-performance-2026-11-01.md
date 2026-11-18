# Bake execution audit, 2026-11-01

Read-only snapshot of the owner's running Explorer bake. Current settings and
executables were left in place so the bake can finish. Exact job/errors/stage
statistics are in diagnostics/bake-performance-2026-11-01.json.

The dominant measured cost is mesh generation, not source reads or file writes.
The machine has 8 physical cores, 16 logical processors and about 32 GB RAM.
The UI, restored options and batch runner all cap the job at four lanes even
though parallelTiles supports up to eight. That cap is a conservative software
choice, not a four-core limit in the Recast algorithm.

Each ordinary tile starts a fresh browser worker and .NET WASM runtime. Native
x64 is requested only for low-memory/recovery work. Memory failures are queued
until after the normal map jobs, re-extracted and baked on a serialized recovery
lane. This spends time on an unsuccessful WASM attempt before the native retry.
The low-memory extractor also forces GC between source tiles to reduce peaks.
A native recovery job does not inherently imply a lower-quality mesh; contour
or geometry fallbacks are separate quality diagnostics.

Two recorded tiles fail with Shared-edge detail exceeds Detour's byte-index
limit. This is a local detail-mesh vertex-index constraint, not out-of-memory;
merely increasing staging or using x64 will not remove it. Preserve the captured
failure identities for a geometry-preserving local subdivision/edge-detail fix.

Recommended next implementation: benchmark representative captured inputs with
the native backend; add native-first batch mode when available; separate backend
choice from low-memory extraction policy; allow measured, memory-bounded native
concurrency rather than immediately enabling sixteen jobs; improve per-stage
and retry timings. Retain current geometric settings and compare output/parity
before adopting any optimization. Then investigate the build-stage hotspots.
Do not treat lower global mesh quality as the first performance fix.
