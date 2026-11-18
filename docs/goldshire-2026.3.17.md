# Goldshire tile 0/31/49, pipeline 2026.3.17

The captured inn stair gap had two causes. Contour simplification cut across a retained short ramp-to-flat transition. Separately, a rejected lower tread and the true ramp above it merged into the same raster voxel, allowing the lower tread's blocker to erase the ramp. The correction protects directly supported short transition cells and restores the ramp only when actual rejected collision, clipped to that cell, lies strictly below it. Coincident caps remain blocked.

The two exterior houses use prop collision for walkable entrance surfaces. Global prop blocking therefore removes the porch and embedded-rock approach. These locations are corrected with explicit height-bounded authoring, not a global exception for every prop. `diagnostics/goldshire-touchups.json` contains the tested strokes. There are no coordinate-specific fixes in the engine.

## Apply the tested corrections

1. Open the updated portable Explorer. The engine reports `explorer-recast-2026.3.17`.
2. Open Mesh Editor, choose the same product, and go to Files / Backups.
3. If the current editor project contains edits you want to keep, download that project first. Import replaces the active project; Undo is available.
4. Import `Goldshire-touchups.json` beside the portable executable, or `docs/diagnostics/goldshire-touchups.json` in the repository.
5. Save Edits, then Apply edits / Rebake changed tiles. Rebake tile column 31, row 49. The inn fix is automatic; the two exterior entrances depend on the imported strokes.

## Add mesh brush

The new Add mesh tool paints upward collision surfaces, including props, using the selected radius and vertical reach. It overrides automatic prop/trim exclusions in that band, preserving actual standing headroom and existing climb connections. It does not create unsupported floating floor or erase physical walls. Use a narrow vertical reach on stacked floors. Undo removes a stroke; Restore returns that spot to automatic classification on rebake. Later Cut and Block strokes also take precedence.

## Verification

The original full input is `C:/Users/Ryan/Documents/WoW-Archives/diagnostics/0-31-49.input`; the source capture is left untouched. Checks replay this input, with the separate touch-up project, through the actual packaged native executable and browser WASM engine. Software-rendered collision/mesh comparisons are saved under `docs/diagnostics/goldshire-2026.3.17-*.png`.

The Goldshire fixture checks 12 floor points, a physical wall beside the rocky doorway, 303 unchanged inn floor samples, all three complete routes and shared-edge seams in the three reported regions. The Northshire fixture retains 62 coverage points, six exclusions and 1,536 exact upstairs floor samples, plus Abbey/cave/bridge routes and scoped seam checks. This is captured-region evidence, not a claim about every tile.

The pre-existing Northshire full-tile seam failure remains: one 0.015881-yard discrepancy near (-8533.359, -190.10735, 84.1074), outside the accepted scoped regions. Its nonzero exit is recorded explicitly in the evidence. The accepted scoped regions pass.

An initial broader transition rule was rejected after it changed 28 Northshire upstairs control samples. The final short-transition rule changes none. Broad exterior override circles were replaced with height-narrow platform strokes and focused entrance paths.

See `diagnostics/goldshire-2026.3.17-evidence.json` for hashes, commands and exit statuses. No driver work, installed game files, browser automation, commits or pushes were part of this change.
