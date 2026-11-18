# Goldshire stair coverage and surface painting, 2026.3.18

The preceding route tests were insufficient: a narrow route can connect two rooms while losing valid tread area. This revision adds a separate fixture spanning the inn's interior transition, its exterior doorstep and the rocky house entrance. It checks 37 source-backed points across the tread widths, in addition to the previous 12 floor points and exclusion checks.

## Corrections

- The inn's short sloping transition and a rejected lower tread shared a voxel. Restore the directly supported short ramp only with cell-clipped burial proof and retained physical headroom. Overlaid flat treads may sit just below the independently supported smooth ramp; require the existing two-voxel stair envelope rather than exact equality. Neighboring rejected inclined stringers remain nonwalkable, but their legally low sides cannot erase this short transition's standing area. Tall trim and exposed flat caps retain blocking clearance.
- The exterior inn step was mistaken for wall footing near the edge of a broad threshold. Preserve a collision-clear, same-height route to standing room within one body radius, specifically on a low threshold above nearby terrain. Protect retained threshold cells against contour simplification cutting across them. This exception does not apply to upper-storey trim.
- The two rocky-house treads receive separate narrow-height Add mesh strokes in the updated `Goldshire-touchups.json`, covering the tread surfaces rather than only the path center.
- All paint tools now pick the selected collision surface. Prop collision is included in paint geometry and cursor placement. Previously prop picking, WMO-only decals and ground-only tools disagreed, which could put the paint beneath the selected stairs. A transformed-prop integration test checks both the hit height and paint height, with nearby terrain unable to steal the seed.

A full-ramp contour protection experiment changed 28 accepted samples and was discarded; existing ramp outlines remain unchanged. A broader doorstep exception changed 104 accepted Northshire upstairs samples and was rejected. The terrain-bounded rule preserves all 1,536 control samples exactly. A physical cap remains blocked at the rocky doorway, and existing table/trim, upper-floor, empty-air, headroom and Undo/Restore tests remain active.

## Apply

Reopen the portable Explorer and confirm pipeline `explorer-recast-2026.3.18`. Re-import the updated `Goldshire-touchups.json` from beside the executable (or the repository's `docs/diagnostics/goldshire-touchups.json`), Save Edits, then Apply edits / Rebake changed tiles for map 0, column 31, row 49. The two inn corrections are automatic; the exterior house corrections require the updated strokes.

Import replaces the active editor project, so download existing edits first if they need to be retained. The new Add mesh brush follows collision, uses the chosen vertical reach, and remains reversible. It does not invent floating floor or remove physical walls.

## Evidence

`diagnostics/goldshire-2026.3.18-evidence.json` records executable/runtime hashes, full-capture bake commands, all exit statuses, fixture results, scoped seam checks and route results. Before/after collision renders are stored alongside it. These are captured-region checks, not a claim of world-wide perfection.

The previously recorded Northshire full-tile seam failure outside the approved regions remains: one 0.015881-yard discrepancy near (-8533.359, -190.10735, 84.1074), exit 3762504530. It is recorded as a failure; the accepted scoped regions pass. No installed game files, drivers, browser automation, commits or pushes are involved.

A separate dense source-face diagnostic is retained in the evidence. It still reports 11 unmatched source samples in the wider stair bounds, including edge/alternate-layer samples; it is not reported as a passing zero-missing area gate. The reported square hole is explicitly included in the point fixture and software-rendered comparison.
