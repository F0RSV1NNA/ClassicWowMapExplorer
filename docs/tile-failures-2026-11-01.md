# Full-map bake failure fixes, 2026-11-01

Input: Documents/mmaps/ExplorerMesh-1793596051073/job.json, wowf 1.60.1.70707.
736 attempted, 697 completed, 39 failed: 11 shared-edge detail capacity errors
and 28 Failed to fetch native transport errors. Reachability was partial.

## Native request failures

The native server rejected busy/low-memory requests before consuming the full
fixed-length upload, then closed with unread data. A controlled 16 MiB upload
while eight jobs occupied the slots reproduced a Windows connection-aborted
error instead of a usable HTTP 429. The corrected server drains the upload
before the busy response. The same reproduction now returns HTTP 429.

The client retries connection/result-stream resets up to three times. Reported
engine errors are not retried as transport errors. Busy requests remain queued
and cancellable. Native requests now opt into a framed response with immediate
headers and heartbeat frames during long bakes. This avoids leaving the request
silent for the full child runtime; one real-geometry control took over six
minutes. It does not establish that a timeout caused every recorded fetch error.
Legacy clients retain ordinary ZIP/error responses. Tests cover fragmented
stream frames, truncated results, streamed errors, eight slots and cancellation.

If a job still fails, its exact encoded input and error metadata are saved as
failed-{map}-{column}-{row}.input/.json in the selected export folder. No new
manual source capture is required for the next investigation.

## Shared-edge detail capacity

The original repair adds a center vertex and a triangle fan for each split
face. Polygons already near Detour's 255-entry detail limits can overflow.
The successful old repair path is retained. Only an overflow invokes the new
path: existing planar-only reduction frees redundant interior detail, then
capacity-safe ear clipping preserves all boundary samples without unnecessary
new centers. Unreferenced samples are compacted; coarse polygons, neighbors,
area and flags remain unchanged. If mandatory detail still cannot fit, the
bake fails with the polygon index/counts rather than dropping samples or faces.

The geometry identity explicitly recognizes this one failure-only source
revision as compatible with previously successful outputs. Other unrecognized
geometry changes still invalidate resume. All 697 existing completed tile
markers pass the read-only resume check.

## Validation

- Near-limit regression: 254 detail triangles retain two complete unit floors,
  corrected shared boundary heights, indices, flags and adjacency.
- Existing surface rules, collision-detail/stair envelope, independent-floor,
  wall/trim, narrow-corridor and audit guards: exit 0.
- Dense captured Goldshire native tile remains byte-identical to its previously
  accepted artifact.
- Real-geometry control: retarget the existing Goldshire source capture to
  failing Stormwind tile column 30 / row 48. Previous engine fails with the
  shared-edge limit (exit 1, 388 s); fixed engine completes and validates the
  exported tile (exit 0, 381 s, about 4.36 GiB peak working set). This capture's
  source build/neighbor padding can differ from the finished current dataset;
  it is a real reproduction of the error, not verification of all 11 inputs.
- Native concurrency/cancellation, large busy uploads and streamed responses
  are exercised against the compiled launcher. A sleeping test child verifies
  admission/cancellation; real engine requests verify result/error delivery.

## Apply

Restart the rebuilt Explorer, then Resume the existing
ExplorerMesh-1793596051073 folder. Keep Native x64 / eight workers. Completed
697 tiles are reused; missing/failed jobs are attempted. Let reachability run
again after the retries. The full 39-tile owner retry remains the final live
verification; it has not been reported as passed.

No global resolution, obstacle rules, simplification settings or Cursed code
were changed. The separate Cursed staging/cache capacity work remains pending.

See diagnostics/tile-failures-2026-11-01.json for tool/artifact hashes and results.
