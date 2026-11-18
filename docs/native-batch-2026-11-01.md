# Native x64 batch baking, 2026-11-01

## Usage

Restart the rebuilt portable Explorer. Mesh options now offer Native x64
(preferred) and Browser/WASM, with up to eight parallel tile bakes. On a
16-logical-processor / 32 GiB machine the native default is eight; restored
settings from the previous four-lane UI are migrated using the launcher's
actual physical RAM report. Select eight explicitly if retaining a manual
preference. The browser-only path remains available when the native bridge
is absent. Native errors are reported rather than silently rebaked in WASM.

Use Resume and choose the existing ExplorerMesh-1793589091261 folder itself.
The offline read-only resume check accepted all 256 completed tile markers.
Same product/build, authoring edits, output sizes and all geometry-producing
engine hashes are still required. The native runner hash is execution identity
and no longer invalidates otherwise unchanged completed geometry.

## Execution and memory

Ordinary native jobs call BakeRaw; isolated low-memory recovery uses
BakeRawLowMemory. This removes repeated forced collections between source
chunks from normal native jobs. The native server now atomically admits up to
eight jobs, with unique per-request temporary files and cancellation jobs.
Busy requests wait and retry. Cancelling a request kills only its own child.
New jobs wait when available physical RAM is below 2 GiB.

The WASM build was already configured for its 4 GiB address-space maximum;
there was no smaller cap to raise. Native x64 is not restricted to that 4 GiB
address space and uses normal .NET/OS memory management. This is not a fixed
aggregate eight-job RAM guarantee: running jobs can grow after admission.
The 2 GiB admission reserve is a start guard, not an aggregate hard limit.
No source resolution, simplification, edits, collision rules or bake geometry
settings were changed. Cursed staging/cache changes are a separate task.

## Verification

- 136 Bun tests passed, including eight-lane scheduling and resume identity.
- Web/type check, native engine and portable launcher builds: exit 0.
- Native normal and low-memory modes match existing WASM payloads byte-for-byte
  on transport, translated instance, origin-crossing deck and captured WMO
  fixtures (eight native runs).
- Dense Goldshire capture: native 33.2 s, WASM 117.97 s (both exit 0), native peak
  working set 2,102 MiB. This is one sample, not a complete-continent forecast.
  The native tile matches the previously accepted native Goldshire artifact
  exactly (SHA-256 ddb26c4e9732a52defb6b71e2126cb9e082ce3ea6d6e516efc07fa0d9dfdb5e8).
- Dense Goldshire does not have exact native/WASM byte parity. Coarse polygon
  records match, coarse vertex delta is at most 0.00016022 yd, and one detail
  vertex/triangle differs. The original strict equality check failed (exit 1)
  and is retained in evidence rather than called a pass.
- Launcher concurrency/cancellation: a controlled sleeping test engine admits
  eight requests, rejects the ninth with 429, terminates a disconnected child,
  then admits eight again. Real production engine HTTP requests in both modes
  match corresponding CLI payloads.
- No game launch, installed-game file access, driver build or deployment.

Existing Shared-edge detail exceeds Detour's byte-index limit failures are not
addressed by switching execution backends. They remain a separate local detail
format issue; mesh quality has not been reduced to hide them.

Tool, executable, source and dataset hashes and exit statuses are in
[validation evidence](diagnostics/native-batch-2026-11-01.json).
