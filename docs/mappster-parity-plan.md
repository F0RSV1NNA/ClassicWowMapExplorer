# Mappster parity plan

## Architecture

Explorer owns mesh inspection, generation and authoring. Mappster is a source
donor, not a separate process or runtime dependency. Its original C# extraction,
bake and file writers compile into WebAssembly bundled with Explorer and run in
a disposable background worker. Portable Explorer also bundles its own x64
engine for memory-heavy recovery, with no installed runtime or Mappster dependency.
DotRecast is pinned to the same version.

The previous plan to translate helpers around recast-navigation-js left real
differences in contours, triangulation and extraction. That approach is retired;
the source now executes directly, with the reviewed contour winding extension
recorded in provenance. WASM helper visibility is no longer
a parity blocker.

## Completed engine work

- Original ADT/WMO/M2 extraction, placement transforms, holes, collision rules,
  terrain/WMO/doodad provenance and liquid sheets.
- Original bake settings, world grid, filters, erosion, indoor bridge/railing
  logic, region splitting, patched contours, detail triangulation and corrections.
- Original road paint classification using terrain texture coverage and the same
  texture-name snapshot.
- Schema-aware LiquidType.SoundBank lookup and original MLIQ v2 generation.
- Original mmap/mmtile writers, with loadback validation and explicit failures.
- Worker cancellation, filled-wireframe inspection, bounded parallel map/world jobs,
  metadata and progress reporting.
- Mesh Generate/Editor/Options menus; disk-paged continent-scale reachability,
  original directed/liquid/threshold semantics, saved stranded flags and pink
  rendering, partial-set labelling and default post-bake analysis.
- Resumable map/world exports, local tile plus border-neighbour updates, stale
  verdict invalidation, progressive whole-map display, live completed-tile overlays,
  optional folder-backed camera streaming and isolated memory-failure retries.
- Single-pass filled-wireframe rendering without decimation; 4 GiB per-worker
  maximum and extraction lifetime isolation for dense tiles. Automatic analysis
  of incomplete bakes is explicitly marked partial, rather than skipped.
- Native x64 recovery beyond the wasm32 memory ceiling, same-build failure
  history routing, supervised cancellation and self-contained portable packaging.
- Byte-identical native/WASM outputs on a focused synthetic fixture, also checked
  through the production worker in Edge. See [mesh authoring](mesh-authoring.md).

## Next acceptance step

Re-bake the user's difficult indoor tile and inspect its spiral staircase,
railings, floors and roof clearance against Mappster at the same native location.
Then inspect a shoreline and an ADT seam. Confirm source-file completeness before
attributing a discrepancy to baking. Do not hide penetration with rendering bias
or change proven Mappster settings merely to make an overlay prettier.

## Remaining application features

1. **Real-continent acceptance:** Eastern Kingdoms completed all 736 tiles using
   native recovery. Investigate reported holes at tile 31_31 and terrain penetration
   at X31/Y32 using captured original inputs. Actual Dalaran replay now retains
   the custom pipeline with bounded regions and no long detail edges; two
   triangulation warnings still need investigation. Half-cell detail height
   tolerance removed the measured local terrain penetration, at the cost of
   additional detail vertices. Visually accept these two rebaked tiles before
   applying the changes to a whole continent.
   The Northshire graveyard capture also exposed low-obstacle promotion undoing
   explicit prop blocking. The bake now preserves blocked prop spans through
   that filter; its exact replay leaves a blocked strip along the fence. Confirm
   visually and verify nearby entrances before accepting the local update.
   A follow-up span-merge correction preserves that blocked-prop identity when
   obstacle and ground voxels overlap, removing the measured fence-height
   wedges. Confirm the updated tile before another continent rebake.
   The shared pipeline now rejects removed spans as height sources, preserves
   positive-area detail coverage, fits final geometry to supported original
   collision surfaces, and draws the actual exported triangles. Captured indoor
   floor probes and the new Northshire hole probe improved; visually accept
   these updates before worldwide regeneration. Existing Dalaran triangulation
   warnings remain open rather than being treated as proof of a perfect bake.
   **Scale:** the disk-paged implementation removes the former
   input/wet-cell caps and passes native parity and a 4.56-million-cell synthetic
   check. Measure an actual full continent's throughput, scratch space and memory;
   inspect seams and saved pink flags. Neighbour-link and path-query previews
   remain pending.
2. **Editing:** implemented cursor road/remove/cut/restore/permanent-block brushes,
   height isolation, grouped undo/redo, portable recipes, blended texture inspection
   and overrides, typed same-map connections and affected-tile rebaking. Live
   authoring acceptance remains; CURSED execution of exported portal/elevator
   action records and cross-map authoring are separate follow-up work.
3. **Transport and WMO-only baking:** expose original transport-local extraction
   and baking, independent of dock placement; support global-WMO map jobs.
4. **Job robustness:** dependency-based incremental rebuilds and storage-space
   forecasting. Resume and build-aware tile completion checks are implemented;
   verify exported files with CURSED on real continent data.
5. **Performance:** AOT compilation and configurable parallel tile baking are
   implemented with unchanged settings and verified fixture output. Measure dense
   real tiles and resumed continent throughput; use new stage timings to decide
   whether extraction caching is the next bottleneck.

Full Mappster *suite* parity is still broader than the bake engine. Keep these
remaining features explicit rather than claiming all of Mappster is finished.

## Source ownership

`src/mesh/engine/provenance.json` records hashes of the vendored Mappster source
and texture data. The build checks them. Update source copies deliberately; do
not patch around them in JavaScript. Preserve the license header on NavContours.
No build or runtime step needs a sibling Mappster checkout.

The 2026-10-22 [mesh pipeline review](mesh-authoring.md#mesh-pipeline-code-review--2026-10-22)
also corrected per-vertex height-bound leakage, detail coverage acceptance and
boundary flags, silent collision/contour/conversion failures, optional contour
sampling offsets, shared texture-snapshot writes, and incomplete resume
fingerprints. Source adapters are now fingerprinted alongside Core. Native and
WASM production builds are updated; targeted regressions and a captured Northshire
bake passed. Existing Dalaran polygon-triangulation warnings remain open; this
review does not establish a hole-free world bake.

The subsequent surface/passage correction replaces the default full-erosion plus
thin-bridge policy indoors, preserves connected tiny floor fragments, requires
raised-floor evidence for trim removal, and extends source coverage protection to
connected WMO floors. Rejected WMO caps cannot re-enter through final height
projection. These are global generator changes, not location patches. Focused
regressions and a cached local building replay pass; full-world acceptance and the
earlier dense Dalaran polygon-triangulation warning remain open.
