# Mesh quality review — 2026-10-28

The owner confirmed the new Abbey screenshots came from the current build. Treat them as current regressions, not stale-export artifacts. Navigation/questing is paused pending mesh-generation work. Do not use another whole-world bake as the first acceptance test.

## Reviewed implementation

Read `docs/mesh-authoring.md` (including 3.2–3.8 correction history), `WmoCapPatches.cs`, `SurfaceClearance.cs`, `SourceSurfaceFit.cs`, `SourceSurfaceContinuity.cs`, and the region/detail settings and repair integration in `NavBake.cs`. Retained collision replay: `.cache/abbey-full-investigation/abbey-source.json`.

Confirmed code behavior:

- Source patch classification runs before voxelization. Coplanar patches are assembled using shared exact edges and a normal tolerance. Broad patches can bypass narrow-shelf rejection; suspended furniture requires a downward-facing underside and a nearby lower WMO floor.
- Suspended furniture, tread and lower-floor decisions currently use triangle-count majorities. They are sensitive to source tessellation: many small triangles can outweigh a few large triangles. Face-centroid evidence is not complete footprint evidence. This is a concrete implementation weakness, not yet proven the cause of each screenshot.
- Narrow raised strips can be rejected even though the surrounding lower floor is usable. The rule has to distinguish exposed wall footings/railings from a low, open, traversable step or platform rim. A hollow ring's bounding box is not proof of standing room, but a small patch's width alone is also not proof that walking across it is impossible.
- Raised WMO rejection, exact body clearance, voxel filtering/erosion, pinhole restoration and final source-height fitting all affect coverage. A missing output triangle does not establish which stage removed its support.
- RegionSplit defaults to 7 yards, independently of whether a floor is planar. Detail sampling is 2 cells / maximum error 0.5 cells. Retriangulation retains samples; buried-ramp repair can add edge midpoints and triangle centers. High triangle count therefore is not necessarily additional useful ground detail.

## Current replay evidence

Checker build succeeded, exit 0. One retained-source replay completed, exit 0: 2,252 coarse polygons, 23,316 preview triangles, 2,962 blocked WMO-cap spans, no swim fallback. This is a cropped retained source, not a full current tile bake or proof of world correctness. Some table-region patches are already classified as suspended furniture while other raised patches remain broad. Do not claim that a single changed threshold solves all these surfaces.

SHA256:

- Checker `tools/mesh-native-check/bin/Release/net10.0/check.dll`: EB6987C156F8755A64BC51DA169217376B70984975BC5F87C0683DC98E98B55F
- Source capture: 13E6F6083E1EEECC3C88BBA7FBC755D263972AF6D543FBAC6AF0C6FA195402CD
- Replay tile: 69A3D3F3EDB419C25AA567C256A0D6F77291BFAF09C42FB8EE0587FE3E330511

## Implementation order and acceptance

1. **Surface classification:** measure physical footprint/area rather than triangle votes. Test tessellation invariance and both positive/negative cases: solid low platforms, suspended furniture, open raised rims, wall-backed footings, fences, stair treads and upper storeys. Keep solid obstacle collision even when its top cannot serve as navigation support. Do not globally flatten interiors; preserve actual floor elevation changes.
2. **Coverage tracing:** for the same source locations, compare eligible collision, compact spans before/after clearance and erosion, regions, coarse polygons and final detail. Locate the first lost-support stage for stair notches and platform gaps. Fix that stage; do not fill arbitrary final holes or connect geometry through walls.
3. **Adaptive detail:** measure coarse polygons separately from detail triangles and serialized bytes. Remove only redundant coplanar detail while preserving boundaries, shared edge profiles, flags and connectivity. Preserve nonplanar terrain and necessary stair/ramp samples. Quantify before/after coverage and height error; do not simply increase cell size or detail tolerance globally.
4. **Integrated verification:** compare the retained Abbey views for tabletop cut-outs, continuous raised-floor access and both stair corners, including exterior wall bases. Verify stage and serialized geometry before bundling native and WASM engines in the portable build. Ask the owner for a single-tile rebake and visual inspection before a world rebake.

No bake policy or shipped executable was changed during this review. The review is the starting baseline, not a completed fix or a promise of perfect mesh.


## Implementation follow-up

Implemented and packaged pipeline 2026.3.9. See the final section of `mesh-authoring.md` for the retained rules, rejected wider area-weight change, focused evidence, measured detail savings and remaining visual acceptance. The earlier statement that no executable changed applied to the initial review only; the updated portable app is now built. A current-build single-tile visual rebake remains required before a world bake.

## Follow-up after the rejected 3.9 visual result

Revision 3.10 replaces horizontal whole-patch rejection with local support and
connected-surface decisions, restricts exterior bevel handling, bounds upward
source-edge extrapolation, and synchronizes complete shared detail profiles.
See the 3.10 section in `mesh-authoring.md` and the actual collision/mesh cutaways
in `docs/diagnostics/abbey-2026.3.10.png`. The saved input is cropped; no whole-world
correctness or full-tile visual pass is claimed. Failed experiments remain
recorded in `.cache/oct29-mesh-review/`.


## Follow-up 2026.3.12

Reproduced and fixed asymmetric shared contour portals, stair vertices choosing a lower WMO storey, and false low-floor threshold rejection. See the 3.12 section of mesh-authoring.md and docs/diagnostics/abbey-2026.3.12-evidence.json for the rejected experiment, four serialized-point checks, 1,537-point flat upstairs control, backend parity, hashes and explicit capture limits. The portable build is updated; full-tile visual acceptance is still required.


## Follow-up 2026.3.13

Full current input and matching saved mesh supplied by the owner now reproduce both remaining stair holes and trim cases. Fixes distinguish two-sided clear floor transitions from wall/railing trim, preserve connected ground/road label islands and singleton contours, and reject terrain-through-WMO-gap evidence against a fully supported indoor floor. Both packaged engines pass the six coverage points, three trim exclusions, unchanged upstairs control and central disk core control. See mesh-authoring.md and diagnostics/abbey-2026.3.13-evidence.json for actual hashes, exit results, rejected experiments, strict-backend mismatch and the unchanged tile-edge seam failure outside the Abbey. Portable build updated; fresh single-tile visual acceptance remains necessary.

## Follow-up 2026.3.14

Restored supported low floor borders and cave tessellation patches, required furniture evidence around most of the actual component boundary, and preserved a collision-clear turning transition into the wooden upper landing. Both packaged backends pass the named geometry checks and complete outside-to-landing/cave routes. See `mesh-authoring.md` and `diagnostics/abbey-cave-2026.3.14-evidence.json` for exact hashes, exits, remaining broad audit findings, rejected experiments and visual acceptance limits.

## Follow-up 2026.3.15

Reproduced all five new cave locations. Fixed doubled standing-radius validation, whole-face rejection based only on a centroid, and coplanar-only support checks across natural floor creases. Added selective shallow WMO contour coverage preservation for the remaining south-bend omission. Both packaged backends pass five local cave routes and 16 source coverage points, while preserving all 1,536 approved upstairs control samples and four obstacle exclusions. See `mesh-authoring.md`, `diagnostics/cave-2026.3.15.png` and `diagnostics/cave-2026.3.15-evidence.json` for hashes, exits, rejected variants and remaining broad diagnostic findings.


## Follow-up 2026.3.16 - bridge deck pinching

The bridge at camera (-8980.903,-296.641,74.594), map 0 tile (32,48), reproduces from the same complete EMR2 capture. Its source deck is continuous. Rejected support faces underneath it contaminate the higher deck's area during successive Recast span merges. At (-8980.933,-296.6673), a merged span extending from Z67.73333 to Z71.73333 incorrectly carries area 60. Clearing that marker removes the deck, then outdoor erosion widens the omission into the visible side bites.

Rejected WMO faces are now rasterized separately to retain their own top evidence. A merged span is restored only when every overlapping rejected top is buried by more than one voxel and an eligible WMO triangle clipped to the same cell confirms the surface top. Exposed/coplanar caps remain blocked. Clipping the whole source cell matters: a sloped plank's high corner can quantize above its center sample. No global radius, slope, cell size or detail setting changed.

Both packaged engines pass 46 bridge deck coverage points, 16 prior Abbey/cave points, six obstacle exclusions, 1,536 exactly unchanged upstairs samples, bridge traversal and existing routes. Scoped bridge, Abbey and cave seam checks pass. Native coarse polygons are 15,481 and tile size is 6,056,324 bytes (3.15: 15,446 and 6,050,776). The complete tile restores 527 buried-marker spans. Synthetic checks retain exposed/co-quantized caps, require direct support, and cover sloped high-corner support.

`diagnostics/bridge-2026.3.16.png` compares actual source collision, the previous saved tile and the new native tile. Abbey and cave control comparisons are also retained. `diagnostics/bridge-2026.3.16-evidence.json` records executable/WASM/checker/capture hashes and exact command exits. The pre-existing 0.015881-yard far tile-border seam failure remains recorded; the full-tile seam gate is not declared passed. The portable folder and ZIP contain 3.16. Reopen the application and rebake tile (32,48) for visual acceptance.
