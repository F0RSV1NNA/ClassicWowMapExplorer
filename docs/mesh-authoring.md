# Integrated mesh authoring

Explorer runs the original C# extraction and bake code in browser WebAssembly
workers, with an Explorer-owned native x64 fallback bundled in the portable app. There is no Mappster executable, service, native CASC library
or installed .NET runtime involved when running the app. The .NET 10 SDK and
`wasm-tools` workload are build dependencies only. The compiled runtime ships
inside Explorer. The x64 fallback is self-contained too.

## Controls

Open **Mesh Generate** in the top menu with the camera over the desired source tile.
**Mesh Editor** holds cursor authoring and saved-mesh analysis; **Mesh Options** holds display and
reachability settings.

- **Inspect source tile collision** shows terrain, WMO and M2 collision in green,
  blue and amber. This inspection overlay uses Explorer's parsers; the bake itself
  independently uses Mappster's original extractor.
- **Mesh view** selects wireframe or filled wireframe without rebuilding. Filled
  wireframe retains the coloured edges with matching translucent faces beneath.
- **Bake tile under camera** reads original game files through Explorer's CASC
  worker, then extracts and bakes them in a separate WASM worker. Camera streaming,
  NPC spawns, display offsets and decorative ocean planes do not become bake input.
- **Show last baked tile** restores the resulting Mappster detail mesh.
- **Show liquid surfaces** displays the liquid sheets emitted by Mappster's
  extractor, coloured by kind. They are distinct from the baked underwater floor.
- **Download last tile project** saves native `0000.mmap`,
  `0000_32_32.mmtile`-style files and an optional matching `.mliq`, plus
  `project.json` with settings, correction counts, timings and remaining limitations.
- **Bake current terrain map to folder** / **Bake all terrain maps to folder**
  process tiles sequentially into a new `ExplorerMesh-<timestamp>` output folder.
  Files use Mappster names, with separate tile metadata and a continuously updated
  `job.json`. Chromium's writable-folder API is required. WMO-only maps currently
  report unsupported rather than becoming a successful empty bake.
- **Cancel bake** terminates the active WASM worker. A storage read already in
  progress must finish. Previously saved files remain.
- **Hide mesh preview** removes the overlay.

Map/world jobs bake multiple independent tiles concurrently. Mesh Options selects
1-4 parallel lanes, with a conservative CPU/memory-based default (at most four).
Each bake owns a disposable WASM heap; Recast does not execute on the rendering
thread. Source preparation and export writes each use a serial lane, overlapping
with CPU baking without racing shared files. A whole-map job processes the full source
inventory independently of the camera, rather than baking whatever is visible.

The Mesh menu shows a progress bar for bake jobs. Map/world jobs first discover
the full tile inventory, then show processed/total tiles, percentage, current
map/tile and saved/failed counts. Failed tiles count as processed and remain
listed as failures; cancellation preserves partial progress. Discovery and a
single tile's internal bake use an indeterminate bar because the engine does not
report a reliable within-tile percentage. A finished single tile shows 1/1.

## Reporting a mesh problem

Mesh Options shows a selectable, read-only Camera position field, updated while
the camera moves. It uses native WoW X/Y/Z and map ID, not continent layout
offsets. Z is camera height, not the height of the ground under it. Tile X is
column and tile Y is row; the native filename uses row_column.

Use Copy camera position, or Copy mesh problem report after opening the export
in Mesh Editor. The report captures the current native position and view link,
plus saved tile build/backend, relevant settings, quality counters, fallback
causes and reachability state. It excludes the long source hash inventory. If
clipboard permission is unavailable, Mesh Options opens and selects the text
for Ctrl+C. Selected text stays stable while focused. Mesh Generate also shows
tile/coordinates and has a copy button. The current saved tile's plain-build
fallback is shown as a warning in Mesh Options. These controls need no rebake.

The saved Dalaran tile 31_31 used swimBuildFellBack:1 after a bad-outline contour
error, meaning the final build used the plain Recast path. The Stormwind 30_48
tile recovered from a vertex-limit error without that plain fallback. These
records identify candidates to investigate; they do not locate the reported
holes or prove a particular railing defect. Exact problem reports are needed
before changing the bake. No geometry/settings changes were made for this UI.

Focused coordinate checks verify Stormwind world-to-tile conversion, filename
axis ordering, boundaries and invalid grid positions (2 tests, 7 assertions,
exit 0). Type check and release build passed, exit 0. Built main-C4Lm-IKj.js
SHA-256: 28dba314d1a74b626837b4fe5735f1010cb76a3a07e87461d9d14a828085da53.
Executed Bun 1.4.3 SHA-256:
095db4abae203d42f29fdec26da4a882fcde6333cdd84e40c0c7828c0cf543f6.

## Bake throughput

Release builds now ahead-of-time compile the original C# engine to WASM. The
first developer build takes longer; portable users receive the compiled engine.
No cell size, detail settings, extraction rules or quality fallbacks change.
Mesh Options > Parallel tile bakes controls concurrency for the next map/world
job. More lanes require more RAM; reduce the count if paging slows the machine.
The progress display reports completed tiles/minute. job.json records concurrency,
wall elapsed time and cumulative source/bake/export times; per-tile metadata
records source and bake milliseconds. Parallel CPU times can exceed wall time.

Cancel the older job, reopen the updated portable app, open its existing export
in Mesh Editor, enable Continue open export in Mesh Generate, then continue the
map. Previously completed compatible tiles are reused. Do not run two writers
against the same export.

A focused synthetic cold bake measured 3373.65 ms before AOT and 1191.05 ms after
(about 2.8x), excluding runtime startup. This does not predict full-map duration.
Four simultaneous production workers in Edge each matched the native reference's
mesh/liquid hashes (exit 0), completing in 1725.1 ms collectively. Reachability
checks still matched original native output and the Edge analysis check passed.
66 unit tests and the release build/type check passed, exit 0.

Executed AOT module dotnet.native.9q27m3gy66.wasm SHA-256:
914bdc59d7cd226931dedfd0389879b1db27b2efd60f36f54ae37d25d7202e46.
Browser checker SHA-256: 013a17b0fb333e467134dd945401ffb82aa5a2c8e672deff8b5a75ae338d24c0.
Node executable SHA-256:
ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32.
Evidence: .cache/mesh-parity/perf-before.json, perf-after.json and browser.json.
Real continent throughput remains to be measured on resumed user data.

## Dense tiles and whole-map rendering

The SDK's implicit 2 GiB native-memory maximum caused dense tile failures even
when retried alone. The compiled module now advertises 65,536 memory pages
(4 GiB maximum, growing on demand). Raw extraction runs in a separate lifetime
scope, and source/temporary garbage is collected before Recast. This does not
change the extracted geometry or bake settings. Batch admission is capped at four
workers to contain aggregate memory; two remains a suitable starting point.
Recovery of the user's remaining real tiles still needs a resumed live bake.

Mesh overlays now use one barycentric-edge shader for fill and wireframe, rather
than separate double-sided transparent meshes. Every triangle and edge remains.
Depth writing rejects hidden mesh surfaces, reducing overdraw; this changes how
overlapping translucent layers blend, without decimating geometry. All/hide/only
filters remain supported. A real WebGL check reduced the old four calls to one
for the same two triangles, verified filled/wire pixels and filtering, exit 0.
This is not a measured fourfold FPS improvement on the user's AMD card.

Evidence: .cache/mesh-parity/overlay.json and heap-limit.json;
.cache/mesh-reachability/browser-partial-map.json verifies missing-tile partial
analysis with the expected pink triangles and unchanged native output. The raw
bake production-worker check still matched native mesh/liquid hashes. Release
build/type check and 66 unit tests passed, exit 0.
Executed AOT module dotnet.native.qenyepf70z.wasm SHA-256:
022d4bfa4f7ad06232e819b75542a6f70bc7d30d62777b0123e76c13403d2415.
WebGL fixture bundle SHA-256:
dff77ebbef3c91e938c57bed8ab22488107752e861445775bcae2c3481a84de8.
Browser checker SHA-256:
a290b287ede0d4807693db94a12c8b716896b2289dc87c1b07d24ccde44b23e8.

### Isolated low-memory recovery

A 4 GiB worker maximum is a wasm32 address-space ceiling, not a suggested RAM
allocation. Eight maximally grown workers would exhaust 32 GiB before accounting
for JS buffers, the viewer, browser, Windows or the game. VRAM is a separate pool.
The current engine cannot solve a tile by merely requesting a 6 or 8 GiB heap.

Automatic isolated recovery now also disables the cross-source-tile model cache.
Each source tile uses its own extractor; a no-inline scope releases its temporary
soup and cache, followed by collection before the next source tile. The original
extractor, order, border clipping, liquid rules and bake settings are unchanged.
The tradeoff is additional decoding time on recovery, with lower retained memory.
Successful tile metadata records lowMemoryRecovery. This does not prove that the
remaining real city tile fits; recovery still needs the user's live retry.

Worker progress now reports extraction source coordinates, Recast, liquid,
validation and serialization stages with managed heap MB. Failures include the
last stage. Managed heap readings are milestones, not peak native/browser memory.
The low-memory production-worker fixture matched all native mesh/liquid hashes
and emitted those milestones (exit 0). Release build/type check also passed.
Executed worker bake.worker-rPfuASwd.js SHA-256:
830b8cfddf90299364f7b59145b7cfc632b2331f87d72690317fce0dd0999373.
Browser checker SHA-256:
45d8339c20593d368a7f871757f7e988b4f82fe871e89655ce2188f6b8d92b2b.
Evidence: .cache/mesh-parity/browser.json (lowMemory:true).

### Native x64 recovery for dense city tiles

The city failures were inside Recast after extraction, at 4,208,590 and 5,384,944
collision triangles. Releasing extraction caches did not resolve that stage.
Portable Explorer now includes mesh-engine/Explorer.Mesh.exe: its own x64 build
of the SAME vendored extraction/bake/writers, not a Mappster executable, separate
installation or native CASC reader. It receives original bytes already collected
by the page and returns the same bake archive. No bake detail setting changes.

A WASM memory/process failure uses native recovery. When resuming a same-build
export, recorded memory failures go directly to recovery. Completed tiles are
still reused. Recovery calls are serialized, and the launcher admits one native
job; its supervised process is terminated when the upload/client disconnects.
Upload, archive and diagnostic scratch files are removed after each request.
Native errors include stage output and exception stacks. Successful tile metadata
records executionBackend:native-x64 and lowMemoryRecovery:true.

The launcher exposes only a fixed bake operation, requiring its own origin/Host
and a custom request header, with no arbitrary executable or pathname parameters.
The operation runs the packaged engine hidden. Browser-only hosting without the
launcher retains the WASM path and its 4 GiB ceiling. The portable build includes
the x64 engine automatically; build it separately with bun run mesh:native.
Keep the mesh-engine directory next to MapExplorer.exe and app. Fully restart
Explorer's launcher to enable the new operation; a page reload is insufficient.

Focused validation: checkNativeMesh.mjs launches an app-only synthetic server
without game discovery, verifies native output and preview hashes against the
reference, then exercises actual browser-worker recovery through that launcher.
It checks denied cross-origin input (403), malformed input diagnostics (500),
exclusive job admission (429), and slot release on cancellation. Exit 0; evidence
in .cache/native-launcher-check/evidence.json and .cache/mesh-parity/browser.json.
The release build/type check and 67 unit tests passed. Recovery of the user's
two real city tiles still needs a live resumed bake; no success is assumed.

Executed Explorer.Mesh.exe SHA-256:
cf70efd8f2efb01ae61273ff01761d0d36a6de2abac14ab8ca89b5ada12c2c1e.
Executed launcher SHA-256:
65796c9b25ca0d122a42ad04ebd408f1567d405deedc0c8336579baf31b7c1ac.
checkNativeMesh.mjs SHA-256:
9bef633a5669d854b6cf2252b412255b6f83ac559cac21a72344c4454aef7062.

## What now uses Mappster directly

`src/mesh/engine/Core/` contains source copies, with reviewed Explorer contour
extensions explicitly recorded alongside upstream hashes and SHA-256 provenance
in `engine/provenance.json`. The build verifies those hashes. DotRecast.Core,
DotRecast.Recast and DotRecast.Detour are pinned to Mappster's version `2026.3.1`.

- `Extractor`, `Adt`, `Terrain`, `Obj0`, `M2`, `Wmo` and `Liquid`: original terrain
  fans/holes, collision flags, placement transforms, doodad sets and liquid sheets.
  `MemoryFiles.cs` supplies only the two CASC read methods against bytes already
  read by Explorer. No C# code accesses the installed game filesystem.
- `NavBake`: original settings and full bake order, including indoor bridge and
  railing handling, region splitting, detail Delaunay triangulation, spike/blade
  correction and the original guarded fallbacks. Disabled experiments remain off.
- `NavContours`: Mappster's patched contour builder, including its winding and
  zero-area fixes, plus the reviewed Explorer winding-preservation extension
  described below. There is no C++ contour substitute or JS retry approximation.
- `RoadMask` / `Tex0`: original texture coverage, closing/opening and road-area
  assignment, using the same bundled `tilesets.csv` snapshot. Authored brush edits
  are not connected to the UI yet.
- `LiquidMap`: original MLIQ v2 coverage, heights and per-cell kind writer.
- `NavWriter`: original mmap/mmtile writers and a loadback check on every completed
  bake. Failed or incomplete native tiles fail the job.
- Reachability: disk-paged traversal preserving the original `NavPrune` directed
  connectivity, liquid-body connections and separate-landmass policy. The original
  whole-object pass remains available as a verification reference.

The old `@recast-navigation` dependency and approximate TypeScript bake helpers
have been removed. Defaults and exported settings come from the C# settings
object rather than a second JavaScript settings table.

Liquid classes come from the installed data's `LiquidType.SoundBank`. The field
is located by the DB2 layout hash using the pinned WoWDBDefs definition under
`src/mesh/definitions/`. This needs no runtime internet request. Unknown layouts
and unsupported liquid classes fail explicitly; names and legacy ID cycles no
longer decide safety. Texture names are a bundled data snapshot and may need an
update when new game textures appear.

The adapter matches Mappster's native row/column order, keeps the whole requested
tile's soup, and clips neighbouring soups to its eight-yard border. Mappster
performs its own native XYZ to Recast conversion. The viewer separately applies
continent display offsets to the returned native XYZ detail geometry.

## Reported Dalaran and terrain defects

The reported Dalaran camera point is map 0 (314.197,323.482,91.555), tile
31_31. The saved tile used the plain-build fallback after an orphan-outline
contour error. The terrain-poking point (-262.233,59.012,65.278), tile X31/Y32,
completed the custom pipeline with no fallback. Treat them separately. Camera
height and a saved mesh do not supply the original terrain collision heights.
Read-only copies of both saved tiles were retained in
.cache/mesh-hole-investigation/before for comparison.

A reviewed contour extension now preserves nonzero raw winding when a simplified
boundary collapses or reverses. It first tightens simplification. A closed portal
loop with one neighbour on every edge can remain at two vertices even at zero
error, because the original simplifier does not tessellate portal edges. In that
case the loop receives deterministic geometric simplification with its original
edge flags. Keeping every raster vertex exceeded Detour's vertex budget on the
actual Dalaran input; that first candidate was rejected. Holes are now assigned
to their enclosing same-area outline when a region has multiple outlines.
Valid contours remain unchanged. The
original license is retained; provenance records upstream/current hashes and
explorerPatches. This is an intentional extension, not unchanged upstream code.

The actual captured Dalaran input now completes the custom pipeline at the
21-yard bounded-region recovery rung: 22,842 polygons, no plain-build fallback,
and zero detail edges over 40 yards (previously 119). The voxel size and agent
dimensions are unchanged. This rung precedes the existing entirely-unsplit
fallback. Two `Bad triangulation` warnings remain; completion does not prove
that every local hole is repaired. Inspect the same scene after rebaking.

The terrain input independently confirmed shallow ridges penetrating the saved
detail surface by up to 0.2083 yards near the reported point. Changing detail
height error from one cell to half a cell retained the missing height samples.
In the 70-by-70-yard comparison, columns with penetration over 0.05 yards fell
from 118 to zero. This changes actual exported geometry, with no rendering offset
or blanket terrain clipping; it does not remove cave/building floors. The cost
on this tile is a larger mmtile (2.93 MB to 6.17 MB). A tenth-cell experiment
added excessive detail and was rejected. These are local measurements, not a
worldwide guarantee.

Focused native checks cover outline/hole collapse and reversal, valid-contour
preservation, portal/border flags, and separate outlines sharing a region (exit
0). The isolated Dalaran bounded-region replay exited 0 using `REGION_SPLIT=21`;
check.dll SHA-256:
5580617b86d0b1532f240f69deeca2d15c67296cc5e61d1b41f00413d58fe0f1.
The final packaged native engine SHA-256 is
8426ebdf5fd70d55ddddb534df0d0669dd49e9a74496dc734b46129ef57e26f3.
Local evidence, preserved originals, collision/detail comparisons and plots
are in `.cache/mesh-hole-investigation/`. The Dalaran local floor comparison
includes erosion and overlapping storeys; missing samples alone do not identify
a defect. Final visual acceptance remains pending.
The final packaged terrain replay and the production WASM fixture check both
exited 0; the healthy fixture remains byte-for-byte identical to its reference.
Production/native builds, contour checks and all 69 unit tests passed. Executed
tool hashes and exit statuses are retained in that directory's `evidence.json`.

**Mesh Editor > Capture tile source for investigation** writes only the selected
camera tile's original bake input and camera/build context into the open export's
diagnostics directory. Filenames here are 0-31-31.input and 0-31-32.input, each
with a matching JSON sidecar. It does not change mesh files or run Recast. These
inputs allow exact collision-vs-mesh measurement and isolated reproduction.
Native x64 is now preferred for local tile updates too, avoiding another wasm32
OOM on city edits. Recompute reachability after accepted geometry updates.

Because the engine source fingerprint changed, continuing an old full-map export
will treat its tiles as old-engine outputs. Use targeted tile updates for local
validation; do not accidentally schedule a continent rebake while testing this.

## Low fences and blocked prop preservation

The captured Northshire graveyard fence at map 0
(-8935.036,-210.915,83.013), tile `0000_48_32.mmtile`, exposed a filter-order
bug. Its collision triangles are doodads and `BlockDoodads` correctly marks
them non-walkable during rasterization. Recast's later low-hanging-obstacle
filter can nevertheless promote a short null-area span to the ground area of
the span below. The original blocked-prop identities were retained only for
roof classification, so that promotion survived into the compact field.

The filter now restores those identified prop spans to null immediately after
low-obstacle promotion. Collision remains solid for clearance/erosion; low
steps without blocked-prop provenance retain the original filter behavior.
Agent climb/slope settings, terrain detail and indoor railing rules are unchanged.
`quality.doodadSpansReblocked` reports how many promoted prop spans were restored.
This is a general provenance rule, not a coordinate/model-ID exclusion. WMO
railings embedded in building geometry still use their existing geometric
classification; this change does not indiscriminately block WMO floors.

The exact captured tile replay exited 0 without a plain-build fallback. A
0.1-yard sample grid in a 24-by-24-yard neighborhood found 729 overlapping
low-prop/navigation columns before and four after. A few residual overlap
samples remain; the overlay shows a continuous blocked strip along
the reported fence. This measurement is not a claim that every railing on
every map is fixed. The saved export was preserved; visual acceptance requires
a targeted rebake in Explorer. Inspect adjacent walkable ground and entrances too.

Focused `blocked-props` checks verify that low/high blocked props remain null
and solid while ordinary low steps and terrain remain walkable (exit 0).
Executed checker SHA-256:
`53d5cf299c7f4faf577b34162dddcfc0102a865cd8af11b449561683386b0371`.
The captured bake used checker SHA-256:
`4176f6e44b6ee82b15cf441e40f735f228b9c068287424e0b0d7ac164062df4d`
(exit 0). Preserved mesh, captured collision/detail comparisons and the
before/after visualization are under `.cache/fence-investigation/`.

### Raised edges beside blocked props

The subsequent full-map export `ExplorerMesh-1792623955186` exposed raised wedges
beside the same fence near (-8952.730,-201.379,80.467). The crossing was blocked,
but some solid spans had already lost their blocked-prop identity during
rasterization: Recast combines touching solid spans and uses the larger area id
when their tops are within climb height. The temporary doodad marker was 3,
below ground (11) and road (12). A merged fence/ground span could therefore
retain the ground tag while taking the fence height.

The temporary marker is now 63, above every actual surface area. It is cleared
before compact-heightfield construction and never becomes an exported surface
type. This preserves blocked collision provenance through span merging; the
post-filter protection above remains necessary too. No coordinate/model list
or render-only correction is involved. Real stairs and WMO floors retain their
source classification.

An isolated replay of the existing captured input completed without fallback
(exit 0). Within eight yards of the new reported point, five mesh vertices
were more than 1.2 yards above terrain; after the correction there are none.
The maximum local vertex/terrain gap fell from 1.2772 to 0.5876 yards, consistent
with the adjacent voxelized detail surface rather than fence-height wedges.
The cross-section in `.cache/fence-height-investigation/profile.svg` compares
actual exported triangles with captured terrain collision; gaps remain blocked.
The complete map has not been rebaked for this change. Validate this tile and
adjacent openings first, then regenerate reachability for accepted geometry.

Regression checks rasterize overlapping ground and prop triangles in both input
orders, then verify blocked low/high props and ordinary low-step filtering
(exit 0). The captured replay checker SHA-256 is
`810bbfc6307df0249768c512a1d21a0d3a529867e5da05254c9d0313832be472`;
the regression-check DLL SHA-256 is
`97e2c9bf6e1098bf3c575a34731cb3610b53091d2c69e865a7e3a19cdec4952f`.

## General surface-height and coverage correction

The later indoor reports at (258.447,283.426,98.209) and
(169.426,370.083,99.327), both tile `0000_31_31.mmtile`, showed that prop
classification alone did not resolve floor-height distortion. The Northshire
report at (-8926.959,-123.132,83.847), tile `0000_48_32.mmtile`, also exposed
small missing triangles. These corrections run in the common bake pipeline:

- Contour corners ignore compact spans removed by erosion/railing cleanup.
  A removed raised surface can no longer pull the neighbouring floor upward.
  Live steps still participate, preserving staircase connectivity.
- Detail height lookup/correction also excludes removed compact spans.
- Small/steep triangles with positive horizontal footprint are retained.
  The previous cleanup deleted real coverage, including flat-ground slivers.
  Only exactly zero-footprint triangles can be discarded.
- `SourceSurfaceFit.cs` indexes original upward, walkable collision triangles.
  Final Detour hull and detail vertices are fitted at their existing XY to a
  nearby supported surface, with one height-cell clearance. Bounded vertical
  selection protects separate storeys; insufficient headroom disqualifies
  terrain hidden just beneath a WMO floor. Blocked prop faces are excluded.
  Unsupported points retain their original height instead of being guessed.
- Exact terrain/triangle intersection checks preserve clearance where a fitted
  triangle would chord through a terrain ridge. Shared vertices receive a
  conservative correction. Surface areas, XY footprints, polygon topology and
  transport/off-mesh endpoints remain unchanged by fitting.
- Detour query bounds conservatively include corrected heights. Preview
  triangles are generated from the final Detour representation, including its
  real hull vertices, so the drawing matches the exported navigation geometry.

`quality.sourceSurfaceVertices` counts source-surface adjustments. This is actual
geometry correction, not rendering bias. Geometry still uses the documented
voxel/detail resolution and clearance; it is not a triangle-for-triangle copy
of game collision. Deliberate obstacle and clearance cutouts remain necessary.

Focused checks cover removed ledges, live steps, flat floors, ramps, stacked
floors, headroom, unused BV-tree capacity, unchanged XY and positive-area detail
coverage. The current native/WASM fixture agrees byte-for-byte; its mmtile
SHA-256 is `59ec78ba054ed071e1cd5ab93954afba56b4e50a91ce83244d5c630940101426`.
This intentionally replaces the earlier fixture geometry, because final
surface heights changed. The old fixture reference was retained; the browser
check accepts an explicit `--expected=` reference for this comparison.

Actual input comparisons used the captured Dalaran and Northshire source, plus
the prior terrain input. The indoor 15-yard floor-neighbourhood probes found:

| Probe | Vertices >0.5 yd above the selected floor, before | After |
| --- | ---: | ---: |
| Indoor A | 15 | 0 |
| Indoor B | 2 | 0 |

Corrected floor vertex offsets were about 0.2667 yd, the configured height-cell
clearance, instead of the previous variable offsets up to 0.6456/0.8337 yd.
In a 12-by-12-yard grid at the reported Northshire hole (0.1-yard spacing),
missing samples fell from five to zero; all 14,520 samples have mesh coverage,
and none lie below the terrain in that probe. The earlier 70-by-70-yard terrain
probe retained zero columns penetrating more than 0.05 yd. These local results
do not prove that every contour, collision model or tile worldwide is defect-free.
Dense Dalaran still emits the previously recorded two contour-triangulation
warnings, which require separate investigation.

The packaged Northshire full bake, source-fit/loadback checks, regression
checks, production/native builds and browser fixture comparison exited 0.
Native executable SHA-256:
`36f74c2a55c9c621cdb3709ef9f38de3c7e058f5beb4ccfd0d77311619c88108`.
Regression checker SHA-256:
`eeffd720a10e85824ae1f4392b77550bc9fb896b83125ec3135e2044af11f96d`.
Evidence is retained under `.cache/general-surface-investigation/`. An earlier
candidate failed on unused null BV-tree entries; the bounded traversal and
regression check were added before packaging. Final visual acceptance remains
pending. Re-bake the two reported tiles first, inspect, then regenerate
reachability after accepting geometry. Existing exports were not overwritten.

## Build and focused verification

```powershell
dotnet workload install wasm-tools
bun install
bun run build
```

`bun run dev` also builds/bundles the engine. Portable users just run the existing
launcher with its accompanying app folder; no SDK installation is needed.

Focused checks, when engine integration changes justify them:

```powershell
bun test
bun tools/checkMeshParity.ts
node tools/checkMeshBrowser.mjs
```

The parity check uses one small synthetic raw ADT/MH2O/texture fixture. It executes
the same original C# source natively and as actual WASM, compares mesh files and
detail geometry byte-for-byte, and verifies road/swim classification and MLIQ
output. It is not a real-world route sweep. The browser check sends that fixture
through the built production worker in headless Edge, verifies the exported-file
hashes against the native result, and exits. Evidence is kept under ignored
`.cache/mesh-parity/`; nothing reads installed game files during these checks.

The browser startup bug was concrete: setting `self.onmessage` before importing
.NET caused its loader to identify our standalone worker as an internal pthread
worker and wait indefinitely. The bake worker uses `addEventListener` instead,
leaving `onmessage` unset during runtime detection. Preserve this ordering.

## Unreachable polygons

In **Mesh Editor**, choose **Open mesh folder** and select the actual export
folder containing `.mmap`, `.mmtile` and `.mliq` files. Select the map, then click
**Analyse and save reachability flags**. The streamed pass extracts actual Detour
links in neighbouring-tile windows, accounts for swimming connections, marks stranded polygons with `0x100`,
and saves changed tiles plus `reachability-<map>.json`. Surface areas and walkable
flags remain intact; polygons are not deleted. Counts distinguish polygons from
their rendered detail triangles.

Opening an export now progressively displays its entire selected map, with one
independent overlay per tile. **Show whole saved map** reloads it. Tiles appear
as map/world bakes finish and remain visible together; camera movement does not
replace the whole-map view with a 3x3 patch. The GPU can cull tiles outside the
view. Fill/wireframe and all/hide/only-unreachable settings apply to every tile.
Colours use normalized byte attributes to reduce continent display memory.

The reader transfers one tile at a time and waits for the viewer to accept it
before decoding the next, avoiding a continent-sized message or combined array.
Loading can be cancelled while retaining already displayed tiles. Whole-map
detail still consumes RAM/VRAM proportional to the displayed mesh. For lower
memory use, **Show saved tile under camera** restores the camera-following
current-tile/3x3 mode when follow-camera is enabled.

Unanalysed or stale exports are labelled unclassified. Pink comes from saved
reachability flags, not bake completion alone. A failed tile prevents a complete
map verdict. Manual partial analysis requires its option. Automatic post-bake analysis now
processes available tiles even if some failed, with an explicit PARTIAL label
and partial:true in the saved marker. Missing neighbours can change that verdict.

The first reported Eastern Kingdoms export saved 714/736 tiles: 11 explicit
out-of-memory errors and 11 process exit(1) failures, plus the deferred-analysis
notice. The source job.json was read without modifying the export. Parallel
jobs now retry those memory/process failures once in isolation after draining
the batch, with original errors and recovery outcomes recorded in memoryRetries.
Unrecovered failures leave the job incomplete; no mesh quality reduction occurs.
Continue the existing export to retry missing outputs rather than rebaking the
714 completed tiles. Use fewer parallel workers if memory pressure persists.

A production Edge check streamed both tiles of the native two-tile seam fixture,
209 detail triangles including two pink triangles for one stranded polygon;
all native-file hashes matched (exit 0). This tests progressive delivery and
acknowledgement, not live visual acceptance of the full continent. Evidence:
.cache/mesh-reachability/browser-whole-map.json. Browser checker SHA-256:
d61f2737754d517fc81dffe24283d6bdb8ab746c151fa0d8f17379bf52742619.
Node executable SHA-256:
ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32.

The default separate-area threshold is Mappster's 1000 polygons: keep the largest
component and any substantial separate places; mark smaller disconnected scraps.
Zero keeps only the largest. Re-running recomputes flags, including clearing old
marks after connectivity or the threshold changes. Preferences persist locally.

The export is checked against the source map's tile inventory. Incomplete or
unverifiable sets require the explicit **Allow analysis of incomplete tile sets**
option. Such results are labelled partial: a path through an omitted neighbouring
tile is not considered present. A fresh single-tile bake receives no global
reachability verdict. To inspect a single-tile project, unzip its download and
explicitly choose partial-set analysis, understanding this boundary.

**Analyse reachability after map bakes and tile updates** defaults on. World
jobs analyse each map after saving its tiles. Failed map bakes still analyse
the available set as partial and record failures and reachability status in
job.json; they do not discard successful tile output.

The old 128 MB input and three-million-wet-cell limits are removed. A background
worker pages adjacency, visitation, traversal stacks, discovery order and liquid
grids through browser origin-private storage (OPFS). Graph and liquid caches
default to 32 MB each; native tile windows and compact component metadata require
additional memory. Disk space is still required. Scratch files are cleaned on
completion/cancellation; abandoned scratch is reclaimed without deleting another
active tab's work. Source inventory is not silently truncated.

Liquid pages preserve highest overlapping surfaces, four-neighbour connectivity,
the two-yard height tolerance and original 2.2-yard floor slack. Ordered directed
DFS preserves the original graph semantics, including one-way links. Changed
flags are written using the original native tile writer. Cancellation during
saving can leave partially updated flags; the verdict remains incomplete until
a full rerun succeeds. Inputs are checked for external changes before saving.

### Continuing and updating exports

Open an existing export in Mesh Editor. In Mesh Generate, enable **Continue open
export (skip completed tiles)**, then continue the current map or all terrain
maps. Compatible completed tiles are reused; missing/incompatible outputs are
rebaked. Version-3 completion metadata checks source build, engine provenance and
file sizes. Older exports safely rebake rather than guessing compatibility.

For a local correction, use **Rebake tile under camera into this export** in Mesh
Editor. Border neighbours are included by default (Mesh Options can disable this).
Only these tiles are baked; reachability then recomputes across the assembled map
because one changed connection can affect distant components. Metadata is written
last, stale liquid files are removed when a tile becomes dry, and old verdicts
are invalidated before replacement. No complete continent rebake is required.

### Streamed validation

63 unit tests and the production build/type check passed. Native/WASM comparison
checks matched both reports and output bytes against original NavPrune on one-
and two-tile fixtures, including land and liquid seams. A disk-backed scale check
processed 16 tiles with **4,562,496 wet cells** using 4 MB caches per kind, joining
them into one water-connected component in about 3.6 seconds. This is synthetic
scale coverage, not a completed real-continent bake or a browser peak-RAM claim.

Executed Node bundles (exit 0): check-stream.mjs SHA-256
6383c5a5881d6b670997a526006a912cd8490911c2bec721950a36d7865aff1b;
check-stream-scale.mjs SHA-256
6018d4d6a50971e5d041e1c6a960e7e57aa4bcb13c78a8df3ea54a20f6e200ee.
The production Edge/OPFS check also exited 0, saving the expected stranded flag:
reachability.worker-wEVYxVzM.js SHA-256
20b1f8186c3368c5b6b3893d916f05db6fab025ca175596a1ca8df27dd4c5973.
Evidence is under .cache/mesh-stream, .cache/mesh-stream-scale and
.cache/mesh-reachability. Real-continent throughput, disk use and visual inspection
remain the next acceptance step.

The following records describe the earlier engine integration checks:

Focused validation: `bun tools/checkReachability.ts` compares native and actual
WASM output on one fixture with mainland, isolated scrap and a disconnected
submerged floor connected through liquid. It checks flags, original surface
classes, clearing/reapplying flags, and detail preview. The fixture has 179
polygons in two final components: 178 in the largest, one stranded polygon.
`node tools/checkMeshBrowser.mjs --reachability` checks the production worker's
directory-handle transfer, file writes and flagged preview in Edge. The browser
saved one changed tile and returned two pink detail triangles for that polygon.
Both checks exited 0, with evidence under `.cache/mesh-reachability/`.
The checked production worker `reachability.worker-DXfbx9mk.js` has SHA-256
`0e5223775eb7b050d54287f5921da9362420f3766d7cf148fa17f2c5ea5b56e2`;
the native harness `check.dll` has
`532490f63315224b7b898525f5af8218d5dba0901cc5fa7850ee7eeba93df885`.
The matching flagged fixture tile has SHA-256
`e7cc066320aa0a72c221a6938292c7c1d1aff56a954acefc8ab318c60162a2c3`.
The type check, production build and 56 unit tests also passed. The rebuilt
portable launcher has SHA-256
`a47c270aa9bad36ed9978dc38869c39f708b9a1a2f376bfa0bf72c786900472a`.

The 2026-10-21 acceptance run passed 55 unit tests, production build and the
native/WASM/browser comparison (exit 0). The fixture produced 120 polygons and
240 detail triangles. Matching output SHA-256 values:

Executed artifacts (all completed checks exited 0): production worker
`bake.worker-B0-hQIGp.js` SHA-256
`50fb45aea95556635a4355fcdc1e3d25a12cf97b3eb94059376c9919fabd7be4`;
native harness `check.exe`
`c95af08c9f36af6498b969ee3366089d98e1e7168cc31e131a1b59dd4567c12a`;
Node v24.21.0 `node.exe`
`ba4e6d110e8c1592a1ecd390f6b05f3da124b13871a5be62b341a07a853c6c32`.

| File | SHA-256 |
| --- | --- |
| `0000.mmap` | `50e2fb9273741143d164663ef46649aeb02e5c5cb70a60ae0d23929392ebf303` |
| `0000_32_32.mmtile` | `1e31193e3ae775cc92604349840f52dd1af01bb829e5c3f350d6f8a2d7c9cfb9` |
| `0000_32_32.mliq` | `7355b0812affdf2bd903722d50e964f4ee50eb1a666fba438892a26f37504080` |

This verifies the integration on that fixture; it does not establish visual or
whole-world equivalence for every real map. Re-bake the same difficult indoor
tile in the app for the next manual comparison. Remaining suite features are
tracked in [the parity plan](mappster-parity-plan.md).

## Mesh pipeline code review â€” 2026-10-22

### Follow-up: general surface and passage rules

The later Dalaran/Stormwind/Northshire reports exposed general policies that the
earlier height-only corrections could not solve. The generator now:

- Honors `IndoorErodeCells` directly by default. The previous default secretly
  selected full indoor erosion plus shortest-connector restoration, producing
  threads through otherwise wider passages. The old experimental mode is opt-in
  with `INDOOR_BRIDGE=1`; it commits a component join only after **both** restoration
  paths are accepted.
- Requires level, raised-floor evidence before deleting narrow indoor strips;
  narrowness alone is not sufficient. The railing pass preserves stair
  continuations and no longer treats small area alone as furniture. The lower
  floor used as trim evidence must be within climb plus one height cell, preventing
  an ordinary lower storey from condemning a narrow upper corridor.
- Tracks dry walkable WMO tops temporarily through rasterization and blocks proven
  narrow raised caps before compact construction. Across a cap there must be lower
  floor and either more lower floor or a solid wall; same/higher walkable
  continuation preserves stair treads. Terrain and submerged areas are excluded
  from this classifier. Decisions are simultaneous, not order-dependent. The
  `raisedWmoCapsBlocked` metric records the count.
- Removes rejected cap faces from the final source-height authority too. Otherwise
  projection could raise contour edges straight back onto the rejected trim.
- Groups walkable WMO collision faces by shared edges. The interior anti-penetration
  envelope now includes the same connected WMO floor layer, rather than terrain
  only. An unrelated cap or another unsupported layer cannot raise the whole floor.
- Preserves tiny post-split regions connected to other walkable floor. Previously
  the size cutoff could remove a legitimate boundary fragment and open a hole.
  The cutoff now removes only isolated tiny regions.

These are global rules; there are no coordinate exceptions for the reported spots.
Exported tile/area/flag formats and outdoor radius settings are unchanged. Resume
fingerprints change, so rebaking applies the new generator instead of reusing old
completion markers.

Validation: `surface-rules` covers WMO caps, wall-base trim, real stair treads,
terrain preservation, narrow corridors, connected versus isolated tiny regions,
an interior WMO bump, and exclusion of rejected caps from source fitting. It and
the existing four geometry regression modes exit 0. A local replay uses previously
cached building collision around the reported Dalaran stair, without requiring new
captures; this is a clipped source neighbourhood, **not a full-tile/world bake**.
It loads back successfully and retains substantially more passage coverage than
the old bridge mode. Native/WASM fixture output matches byte-for-byte, exit 0;
production AOT/TypeScript and native builds exit 0.

Portable assets are updated. Evidence is in ignored
`.cache/reported-surfaces/evidence.json`. Native executable SHA-256:
`ee70a6dfb03190be03992bc697f5c944869c5fd640df2ce6b8f837d65b51b2fd`;
checker assembly SHA-256:
`33d4f529b0b6f328d09383989a173e102648426d0775ac12b23483526ca8e61c`.
The prior complete raw captures are no longer present; their failed replay attempt
is not counted as validation. Cached local collision and focused regressions were
used instead. The prior dense Dalaran polygon-triangulation warning remains an
open acceptance item; none of these results establishes perfection across every
world tile.

Scope: collision-source collection and raw adapter, terrain/model extraction,
voxel filtering and erosion, contour construction/recovery, detail cleanup,
source-surface fitting, Detour conversion/writing/loadback, previews, streamed
reachability and export/resume bookkeeping. This is a review of the mesh pipeline,
not a certification of the unrelated viewer/game systems or every world asset.

The review found and corrected these additional issues:

| Severity | Finding and consequence | Correction |
| --- | --- | --- |
| High | `ClampDetailToPolygon` assigned an indoor vertex's lower bound back to the polygon-wide `lo`. Later outdoor vertices inherited it, raising otherwise valid ground according to vertex order. | Keep the roof-dependent bound local to each vertex. |
| High | `RetriangulateDetail` exported Delaunay's temporary fourth triangle slot, which starts at `-1`, as Detour boundary flags. Interior diagonals could be labelled as boundaries. | Reconstruct the three boundary bits from actual hull edges. |
| High | Replacement detail triangulations were checked for edge length and count, but not complete coverage. Delaunay can remove dangling faces. | Require consistent winding, matching projected hull area, exactly one incidence on every hull edge, and opposite paired incidences on internal edges. Reject incomplete/overlapping candidates and preserve the original triangulation. |
| High | A failed contour-hole bridge warned and continued, leaving an outer contour without its intended hole. | Throw with region/area context so the existing recovery chain can retry. |
| High | Missing nonzero collision dependencies and malformed M2 collision arrays could become empty geometry, silently removing blockers from a successful bake. | Missing dependencies and malformed/nonfinite/out-of-range model data fail explicitly. Valid models with zero collision indices still remain noncolliding. |
| Medium | Contour walking could return incomplete data on a missing neighbour or after its iteration limit. | Require the boundary walk to close; otherwise retry through the existing failure path. |
| Medium | Optional contour densification sampled compact columns by subtracting an already-removed border; its vertical comparison also used horizontal cell size. | Add the border when returning to compact coordinates and use `chf.ch` for height. Densification remains disabled by default. |
| Medium | Detour conversion exceptions were swallowed, discarding the actual failure reason. | Preserve tile coordinates and the original exception in a reported conversion failure. |
| Medium | Road-name initialization wrote `tilesets.csv` beside the executable. Concurrent native invocations could contend for it, and read-only application directories could fail. | Parse the pinned embedded snapshot directly into memory. |
| Medium | Resume fingerprints covered Core files but omitted the source/preparation/serialization adapters. An adapter change could reuse old output. | Hash/check both Core and adapter inputs and include both in the completion fingerprint. Old completion markers are deliberately invalidated. |

The boundary-flag correction follows the pinned upstream API's separation between
[`DelaunayHull` and `SetTriFlags`](https://github.com/ikpil/DotRecast/blob/2026.3.1/src/DotRecast.Recast/RcMeshDetails.cs).
Explorer reconstructs flags from explicit undirected hull edges because its detail
seeds do not necessarily occupy a contiguous prefix of vertex indices.

Validation performed for this review:

- New `audit-guards` cases verify a complete Delaunay disk, correct outer-edge bits,
  rejection of missing/duplicate/reversed faces, independent indoor/outdoor height
  bounds, corrupt collision rejection and in-memory texture-name loading: exit 0.
- Existing `surface-heights`, `blocked-props`, and `contour-winding`: exit 0 each.
- One full bake of the captured Northshire `0-32-48.input`: exit 0, 14,045 polygons,
  no plain-build fallback. This checks the changed extraction and detail pipeline
  against the actual reported tile, not every tile on the continent.
- Existing TypeScript suite: 69 passed, exit 0. After adding the adapter-fingerprint
  regression, the four export tests passed, exit 0.
- Production WASM/AOT + TypeScript/Vite build and native publish: exit 0 each.
  The browser fixture matches the current native fixture byte-for-byte, exit 0.
  The fixture hashes remain those of the previous source-surface correction:
  `mmtile=59ec78ba054ed071e1cd5ab93954afba56b4e50a91ce83244d5c630940101426`.

Evidence is kept in the ignored `.cache/mesh-code-review/evidence.json` and adjacent
logs, not committed as build artifacts. Checked native executable SHA-256:
`7c0c5945e291b917d33e2378c19921a2cb2358ea0d748ed48259c98b39eb35a6`.
Checked regression assembly `check.dll` SHA-256:
`95fee3feb33788b3be8df77fec8c532734a5daf5df235b0c80dcaf0dd3f98112`.
Both portable application assets and its bundled native engine are updated.

Still open:

- The two previously observed dense Dalaran **polygon** triangulation warnings
  remain unresolved. The detail replacement checks above do not repair the
  earlier polygon triangulator or prove its coverage. Do not report these as fixed.
- Source-surface fitting keeps XY and topology; it cannot repair a missing
  collision asset or turn an incorrectly walkable WMO ledge into an obstacle.
  Its bounded layer selection and terrain-envelope lifting still need broad
  real-world/adjacent-tile acceptance, especially around stacked floors and seams.
- No full-world bake was performed as part of this review. Intentional obstacle
  cutouts and radius clearance must remain; filling every visible gap is not a
  valid substitute for checking ground coverage.

## Cursor authoring workflow

Open your existing export folder in **Mesh Editor**. Choose a cursor tool and drag on loaded collision to paint; choose Off or press Escape to return to normal camera controls. Brush radius controls horizontal coverage; vertical reach confines edits to the selected floor. Undo/Redo groups each drag into one operation.

- Paint road / Remove road override automatic road classification.
- Mark non-navigable retains a pink surface with no walking permission; reachability recomputation preserves this exclusion.
- Cut removes the affected mesh. Restore overrides earlier strokes with the original classification; it does not invent missing collision or undo physical obstacles.
- Connect uses two clicks on the same map. Select walking, portal or elevator, radius and direction. Connections can be selected, relabelled, updated and deleted. Walking links are navigable; portal/elevator links carry action metadata and participate in reachability without granting ordinary walking permission. CURSED still needs a consumer for these exported action records before it can execute them. Cross-map journeys remain separate.
- Inspect road lists blended terrain texture layers. Auto / Always road / Never road overrides let you correct classification. Automatic detection now examines texture basenames rather than directory names and excludes crater names. This remains a heuristic, not semantic recognition of every road.

Edits have a local recovery draft per product. New sessions start empty; Recover unsaved session explicitly restores that draft. Save Edits writes `mesh-edits.json` into the open export folder. Download/import provides a portable backup. Loading a recipe is undoable. After editing, use **Rebake edited tiles**: per-tile fingerprints include nearby brush borders and connection starts, so only affected saved tiles are rebuilt. Texture overrides conservatively invalidate every tile; repeat the update on other affected maps. Existing baked files remain unchanged until rebaked. Resume also checks authoring fingerprints.

Typed connections are exported in per-tile `.links.json` sidecars with stable IDs. Save the recipe alongside the mesh; a standalone tile download is not a backup of the full authoring project. Brush exclusions affect mesh surfaces, not the independent MLIQ free-swimming volume.

Focused verification covers brush height isolation between two floors, road paint, cut/restore coverage, persistent blocked permissions, typed links, saved metadata, loadback, both reachability paths and blended texture inspection. A headless browser component check covers drag grouping, undo/redo, connection editing, folder persistence, dirty-tile selection and the bake-time input gate. Live visual acceptance of the cursor against real-world collision remains necessary.

### Painting palette

The editor starts with a compact tool palette and pinned Undo, Redo and Save Edits buttons. Drag paints a continuous filled overlay; only the current brush cursor has an outline. Each drag is one undo step. Save Edits writes `mesh-edits.json` into the open export, or downloads that file if no folder is open. Apply edits rebuilds affected tiles. Brush height settings, backups, connections, texture rules and saved-mesh analysis are expandable secondary controls.

Paint previews use a translucent union of brush coverage projected onto loaded terrain/building collision, with depth testing and a small surface offset. Coverage is sampled on a local grid; steep discontinuities and missing collision are omitted instead of bridged with floating paint. The cursor outline is projected too. This affects preview rendering only; saved brush recipes and bake semantics are unchanged.

The stair preview now copies loaded terrain and object collision triangles, with brush radius and vertical-band clipping in the fragment shader. It no longer constructs a sampled height-grid surface, so steps retain their original faces. A per-pixel stencil union keeps translucent overlapping dabs from accumulating opacity, without relying on equal interpolated triangle depths. A focused WebGL check verifies visible paint and equal brightness for one versus five overlapping copies; live Northshire stair appearance still needs acceptance.

Painting performance: surface extraction is cached per dab, completed 32-dab batches remain on the GPU, and only the active tail is rebuilt. Source faces are clipped to brush coverage before upload, avoiding repeated rasterization of entire large floor triangles. Cursor probes are reduced from 64 to 16 and ignore decorative prop colliders. The overlay follows the connected floor/stair component nearest the dab; tall vertical faces and disconnected caps cannot propagate coverage. These are preview changes only; brush recipes and bake behavior remain unchanged.

Paint compositing uses the viewer scene target's stencil attachment: the newest covered fragment claims the pixel, and later paint faces cannot blend it again. Paint does not write scene depth. This avoids the triangular opacity artifacts caused by depth rounding on clipped coplanar faces. The scene target retains floating-point depth for reversed-depth rendering. File saving is explicit; recovery drafts do not silently load or overwrite the mesh folder. New / Clear edits starts an undoable empty project.

Black-screen regression correction: scene target construction is shared with the WebGL check. Its depth-stencil texture must have stencilBuffer enabled on the target, including MSAA attachments. The production configuration passed framebuffer completeness, nonblack scene and paint checks in logarithmic and active reversed-depth modes. A unit guard checks the attachment invariant.

### Connection editor

Open Connections and click New connection. Click the exact visible world surface for blue A, then orange B. Placement ends after B; the final click is consumed rather than entering camera control. Connection picking includes visible object geometry even without collision and excludes hidden surfaces. It uses a separate mode from brush ground picking.

The current-map list selects a connection and highlights its labelled A/B markers. Edit its label, walking/portal/elevator type, attachment radius or direction and click Save changes (Enter also saves a label). Move A or Move B replaces that endpoint with one world click; Cancel or Escape leaves existing coordinates intact. Delete is undoable. Save Edits writes the recipe file and Apply edits rebuilds affected tiles; runtime execution of portal/elevator action records remains a separate consumer task.

Graphics-context loss/restoration is now reported in the performance log and as a viewer notice. A reported menu-time black/redraw event was not reproduced; it must not be considered resolved solely by the editor click handling change.

### Wall-base and raised-shelf correction

The raised-WMO-cap classifier now follows a bounded shelf/bevel across cardinal and diagonal directions instead of sampling a single radius. It can find the lower floor and solid wall beyond the shelf; continuing stairs/ramps remain supported across the probe window. Cap rejection also preserves separate lower floor collision faces: only the nearest quantized cap layer loses height authority, and a raster-cell corner overlap must not cause the underlying floor to be discarded. Tests cover a shelf wider than the old probe, close cap/floor layers, corner-only caps, terrain, treads, narrow corridors and stacked/ramped floors. A focused replay uses cached Northshire collision covering the reported locations; live acceptance of a rebaked 0000_48_32 tile remains necessary before a world rebake.

Indoor/stair regression correction: the expanded shelf probe could look past a valid rising tread into a shaft wall and reject the tread. A finite, roofed, wall-bounded staircase regression test reproduces the failure on the previous code and passes with nearby stair-continuation protection. Expanded probes are now restricted to unroofed surfaces, excluding decorative tree roofs from that decision. Roofed surfaces use the original cap rule plus the stair protection; a single distant wall no longer overrides a nearby tread. The cached Northshire replay restores indoor cells relative to the regressed build. This remains a focused replay, not proof of perfect mesh across the world.

Indoor follow-up (supersedes the roof exception above): short sloped WMO bevels now retain distinct source provenance from structural treads. Bounded shelf rejection applies indoors too, while stair protection requires nearby supported step heights and full headroom. Cap rejection removes height authority only within the rejected raster patch, rather than across an entire collision triangle. Detail triangles adapt to floor/ramp transitions with shared edge splits and Detour byte-budget guards; coarse polygon connectivity remains unchanged. The cached Northshire replay corrected six below-collision stair samples: all 21 supported stair probes now have coverage without measured dips or excessive lifts. A landing cutout next to solid prop collision remains intentional. Focused samples do not establish world-wide correctness; visually inspect the room and stairs after rebaking tile 0000_48_32.

Visual regression correction: the adaptive post-bake detail refinement described above is removed. It multiplied triangles without resolving raised indoor surfaces. The following revision keeps the original Recast detail geometry, requires two successive height changes for stair exemption, uses equal-top raster area merging, and preserves dry steep-WMO blocking through low-obstacle filtering. WMO blockers are independent of decorative doodad roofs. In the cached local replay, detail triangles fell from 48,168 to 15,018 and raised geometry in the sampled wall/bench region from 756 triangles to 3 (3.11 to 0.14 square yards). This is a substantial reduction, not proof that every wall artifact is gone. Stair samples retain coverage but five remain below collision by 0.09?0.27 yards; conservative ramp fitting is retained rather than lifting adjacent floor or reintroducing subdivision. Reinspect tile 0000_48_32 before a world bake.

### Source shelves, solid wall clearance and stair corners

The next correction acts before voxel merging: group coplanar walkable WMO collision into patches, remove only narrow raised shelves with nearby lower WMO floor, and preserve broad standing surfaces and shared-edge ramp landings. Rejected source shelves cannot raise final fitted vertices. Source-backed tests cover a shelf, broad floor, square landing and narrow ramp-connected landing.

Indoor boundary erosion remains configurable, but zero indoor erosion no longer disables body clearance from solid walls. This separate pass ignores legal treads and swim floors and prevents its wall removals from being restored as pinholes. This deliberately creates clearance beside collision; absence of mesh inside that clearance is not a floor hole.

Stair detail uses a single bounded repair of verified below-collision WMO ramps. Flat floors, decorative caps and downward differences never seed refinement. Shared edge splits and per-polygon byte budgets remain enforced. In the cached Northshire sample, all 21 supported ramp probes have coverage and no measured dips, while the old room wall/bench ROI has zero raised triangles. Total local detail triangles are 15,403 versus 15,018 in the previous delivered revision (not the earlier 48,168-triangle regression). Physical wall clearance conservatively excludes additional edge coverage. These are cached local checks; the third newly reported coordinate lies outside cached collision coverage, and full-tile/live acceptance is pending. New quality counters: sourceWmoCapsBlocked and solidWallCells.

The source-patch guard also preserves actual sequences of flat treads; ramp geometry is not required. Physical-clearance checks of newly excluded room samples found 64 within body collision and 174 within the conservative voxel margin; one additional edge sample remains unexplained and requires full-source/live inspection. This is not a proof that every apparent hole is an intended obstacle cutout.

Native/browser cached Northshire comparison: strict tile hash comparison fails because eight coordinates differ by at most 0.0009765625 yards. Typed geometry comparison passes at a 0.002-yard tolerance with matching topology, flags, counts and bounds (1,065 polygons / 15,403 detail triangles). No coordinate rounding was introduced to force byte identity. Both results are retained in the ignored investigation evidence.

### Complete Abbey capture: floor-edge and flat-tread failures

The 86.5 MB 0-32-48.input capture from client 70532 reproduces the newly reported room and upper staircase. Earlier cropped 70450 source did not cover this staircase. Two remaining raised room corners lacked an eligible direct floor hit and retained their voxel height; other edge vertices could fit to terrain beneath the building. A bounded nearest WMO floor-plane extension (at most 1.5 horizontal cells, with vertical and headroom guards) now resolves such contour-edge points without extending across large gaps or changing polygon XY/connectivity. Contour corner-height gathering also stops at removed intermediate spans instead of reading higher diagonal surfaces through them.

The upper stairs have flat tread collision over ramp collision. The existing half-cell residual envelope now applies to flat treads as well as ramps; it does not require new detail subdivisions. Regression checks cover null intermediate diagonal spans, a WMO edge above terrain, rejection of a wide unsupported gap and a flat tread residual without subdivision.

Full captured-tile replay retains 14,781 polygons. Sampled covered-surface dips below collision drop from 590 to zero in the bad room and from 10 to zero in the upper-stair region. The exterior sample has no measured below-surface defect before or after; this does not establish that every visual or disconnected-surface complaint there is resolved. Existing obstacle-clearance exclusions are not counted as repaired holes. Visual acceptance of the three reported views remains pending.

The complete captured tile was also run through the production browser engine. Both backends show zero below-collision samples in the corrected room and upper-stair regions. Native detail triangles decrease from 344,143 to 343,941; native polygon count stays 14,781. Global byte/strict topology parity does not pass on this capture: browser reports 14,779 polygons. This difference is recorded, not treated as an exact-parity pass. The exterior view remains visually pending; its sampled geometry largely did not change. A collision-only before/after renderer reproduces the supplied camera positions for review.

### Abbey entrance and wall-base correction (2026-10-22)

The outdoor entrance regression came from unequal probe horizons: cap rejection could look past a deep tread into the end wall, while stair protection stopped before the next two treads. The continuation trace now covers twice the cap horizon and requires monotonic steps. A captured Abbey replay now connects exterior terrain, entrance hall, interior rooms and the upper staircase in one Detour component; the preceding replay separated the interior and exterior.

Source classification now also handles short inclined wall mouldings, actual narrow patch width and bent trim strips. A small bevel no longer grants an entire attached shelf the exemption intended for a continuing ramp. Outdoor terrain is accepted as lower-floor evidence only with a tall backing wall along the shelf's narrow axis, so entrance thresholds are not removed simply because terrain exists below the building.

Verification uses the retained 41,302-triangle Abbey collision extraction from build 70532, not a new whole-world bake. The original raw input/export directory is no longer present locally; full-tile/full-world and user visual acceptance remain pending. Synthetic guards cover deep entrance treads, terrain-backed wall shelves, narrow ramp landings, ordinary stairs and source height fitting. Optional checker stage dumps require EXPLORER_MESH_STAGES=1. These results do not establish that every wall in the world is corrected.

### Physical WMO extraction correction (explorer-recast-2026.3.2)

The inherited WMO reader incorrectly named MOPY 0x04 "NoCollision" and retained every other triangle. Several enum values were shifted one bit. This discarded explicit collision when combined with detail and admitted hint-only/nonphysical geometry. Changing floor fitting or erosion cannot recover triangles omitted at extraction.

Both native/browser C# extraction and the TypeScript collision-preview path now select explicit collision (`0x08`) OR rendered (`0x20`) non-detail (`!0x04`) faces. Detail+collision is retained. Missing flag metadata keeps the existing compatibility fallback; a present zero flag is not treated as physical. This follows [TrinityCore's extractor predicate](https://github.com/TrinityCore/TrinityCore/blob/master/src/tools/vmap4_extractor/wmo.cpp) and [flag definitions](https://github.com/TrinityCore/TrinityCore/blob/master/src/tools/vmap4_extractor/wmo.h); the viewer parser independently already identified collision-only triangles using 0x08.

The C# check parses synthetic MOPY/MOVI/MOVT chunks, and the TypeScript test covers the same physical/nonphysical cases. Both pass. Diagnostic changes to clearance, climb limits and height masks were rejected and removed; the previously delivered bake/shaping policy remains in place. Existing saved meshes must be rebaked; opening them does not repair their source selection. The retained Abbey triangle capture has already lost original flags and omitted triangles, so no claim is made that replaying it validates this correction. A fresh Abbey bake from original assets and visual acceptance at the reported three positions remain necessary before another world bake.

### Collision-derived navigation surfaces (explorer-recast-2026.3.3)

Supersedes the previous final-detail interpolation policy above. Voxel corner heights and conservative triangle lifting could produce wall-base ramps even when the original collision was flat. Exported ground/road/swim-floor detail now clips actual eligible collision triangles to the navigation footprint, resolves overlapping floors with headroom/layer checks, and unions duplicate coplanar collision. Flat pieces merge rather than reproduce every source seam. Only a bounded, source-supported contour fringe may extend beyond collision; unsupported footprints retain their existing detail. Heights are evaluated at the float XY actually serialized, preventing rounding from producing vertical slivers at large world coordinates. Only exactly collapsed projected triangles are skipped.

Complex footprints can subdivide locally to fit Detour's byte-sized detail limits. Neighbours share midpoint vertices; original adjacency, external tile portals, BV bounds and exported counts are rebuilt. Child fallback clips the parent's existing detail rather than creating a coarse interpolated ramp. Subdivision is skipped on tiles containing off-mesh connections to preserve authored action links. Overlap work, recursion and output budgets are bounded. Quality counters sourceDetailPolygons/sourceDetailFallbacks expose reconstructed versus retained footprints. Fallback is a limitation, not a claim of exact collision conformity.

Indoor boundary erosion defaults to one cell; morphology, narrow-strip deletion and erosion across valid linked step heights are disabled by default. Separate physical solid-wall clearance and source cap rejection remain. This avoids deleting real doorway/entrance/stair floor as an attempted cure for height errors.

Verification uses the retained 41,302-triangle Abbey collision extraction, not a full-world bake. Targeted source-height, overlapping-floor, step, small-triangle, subdivision-adjacency and authoring/action-link guards pass. Serialized Detour queries verify exterior-to-room and exterior-to-upper-stair connectivity. Samples around the three reported positions show no covered-floor dips or rises above 0.4 yards; coverage exclusions remain and are not all classified as intentional. The raw complete capture is no longer present, and full-world/native-browser topology parity is not established. Rebuild and visually inspect tile 0000_48_32 before another world bake.

### Continuous walking-envelope regression fix (explorer-recast-2026.3.4)

Supersedes and removes the 2026.3.3 collision-detail reconstruction and local coarse subdivision described above. Clipping separate collision tread tops produced visually separated steps; the reduced 0.04-yard clearance and independently reconstructed boundary profiles introduced terrain/edge artifacts even when a Detour route completed. The measured route completion checks were insufficient acceptance criteria.

Keep the original Recast walking envelope, polygons, triangulation and terrain clearance. Source collision fitting, cap rejection, solid-wall clearance and the bounded residual envelope remain. General indoor boundary erosion returns to zero so missing-link/narrow stair edges are not eroded as walls; morphology, strip deletion and erosion across legal linked step heights remain disabled by default. A final millimetre-tolerance height weld reconciles coincident fitted samples only within the same or directly neighbouring polygons. It does not move XY, flatten curved edge sampling, join disconnected storeys, remove floor triangles or add tessellation.

The retained Abbey replay passes 2,914 shared horizontal-floor edge checks with zero missing edge coverage, zero gaps over 0.01 yards, maximum measured side difference 0.00254 yards, and zero internal tread-edge height cracks. Three existing zero-XY-area vertical Recast step-connector edges are reported separately rather than presented as tested horizontal floor. Synthetic checks now require continuous legal-step traversal and retained projected coverage, rather than incorrectly requiring separate perfectly flat tread surfaces. The original complete raw capture is unavailable; these checks do not establish whole-world correctness, nor classify all missing outdoor coverage as intended. Visual acceptance of a rebaked 0000_48_32 remains required.

### Source classification and terrain/step contact (explorer-recast-2026.3.5)

The reported Abbey wall spikes and entrance strip exposed defects before triangulation. Ordinary steep WMO risers had been given the same permanent-block provenance as rejected mouldings, and raster merging used zero climb tolerance. At terrain/step overlap this erased usable footing. Ordinary steep faces now remain null-area solid collision and use Recast's climb-bounded raster merging; source-rejected trim and props remain hard blockers. Their temporary area priorities outrank walkable WMO provenance (58/59 below 60/63). Dry null collision pieces are retained during waterline clipping.

Cap classification protects treads under a smooth collision ramp, including treads slightly below that ramp, without requiring shared triangle edges. Wall backing is checked across a connected patch rather than only its first triangle, through the standing body band rather than requiring a tall single wall triangle beginning at the bevel midpoint. Nonwalkable steep wall pieces count as backing. Step continuation must be monotonic and avoid actual body-blocking collision. Low lips and taller stacked bevels are tested against nearby lower floor. Closed rings use clearance to their real boundary, not a large bounding box, to establish standing room. Optional EXPLORER_CAP_TRACE reports reasons only when requested.

Targeted guards cover the high-starting wall bevel, a visible tread below a smooth collision ramp, a narrow closed trim ring, and order-independent raster merging that admits ordinary risers while retaining rejected caps and blocked props. Retained Abbey collision renders compare the five supplied camera views and a top-down entrance view. In the central two-yard entrance lane, the delivered 2026.3.4 mesh misses 57 of 610 supported floor samples; this revision covers all 610 with no below-collision samples. Wider side-margin samples are not all classified, so they are not presented as a no-holes pass. Whole-world visual correctness remains unestablished. These tests supplement, rather than replace, floor height/clearance and serialized route checks.

The surrounding outdoor audit samples 26,033 retained-capture terrain points with upward normal > 0.7, headroom, nearby terrain support and source-body clearance. Initial uncovered samples are explicitly classified rather than all called holes: the remaining emitted-region cell-center misses occur next to null/unregioned boundaries; none has a fully walkable 3x3 cell neighbourhood. This establishes no sampled open-ground interior hole in that capture, not complete boundary correctness or whole-world coverage. The raw lists and stage classification remain in ignored investigation evidence.

### Layered wall bases and exact wall clearance (explorer-recast-2026.3.6)

The 3.5 visual test still showed wall-base slopes and entrance margin gaps. Source classification had looked for backing walls only within roughly one body diameter. A bevel followed by a shelf and then a wall therefore appeared to be an unbacked ramp. Wall evidence now searches through the layered base, independently of the physical erosion radius. Short inclines are measured in their own gradient frame; an angled backing wall no longer causes an entire moulding to fail that width test. Flat landing guards, actual ramp joins and stair continuation remain. The voxel shelf pass also checks farther for a lower-floor/backing-wall pair, but does not use that larger window to reject a freestanding platform merely for having lower floor on both sides.

The remaining sampled entrance gaps were introduced by solid-wall clearance: conservative voxel columns extended into physically clear ramp margins. A voxel wall hit now needs confirmation against source collision clipped to the standing body's height band. This removes the extra voxel inflation, retains the configured agent radius and ignores legal low risers. No missing floor is filled blindly, no collision is discarded, and no independent stair-tread reconstruction is introduced. SourceBodyClearance uses spatial bins and is shared by tile retries. Terrain is not classified as architectural trim.

The retained Abbey replay's three-yard entrance strip has 881 supported, body-clear samples: 11 were missing in 3.5; 3.6 covers all 881, with none below collision. Serialized floor-edge checks report zero missing/gapped shared edges and zero internal cracks (2,901 edges; maximum side difference 0.00596 yards). Additional checks cover distant/angled backing walls, true cylinder contact versus inflated voxel contact, storey separation and legal risers. Broader room samples improve, but still include raised/absent samples near stairs, platforms and obstacle boundaries that are not all classified. These results are not a claim of a perfect whole Abbey or world. Reopen the updated portable program and rebake 0000_48_32 before deciding whether another world bake is warranted; opening old exports does not apply bake fixes. Existing authoring edits/data are preserved.

### Pipeline review after the 3.6 visual failure (explorer-recast-2026.3.7)

The previous entrance and seam checks were insufficient acceptance criteria. This review follows collision extraction/provenance, raster merging/filtering, cap and body clearance, indoor erosion, hole restoration, region/contour construction, detail cleanup, source fitting, boundary welding, Detour packing and preview coordinates. It does not treat successful routes or a loadable tile as proof of correct geometry.

Confirmed defects and corrections:

- **Clearance restored as a hole:** generic FillPinholes restored 2,500 cells deliberately removed by physical wall clearance in the retained Abbey's lower-floor diagnostic window. It also treated missing neighbour links as a closed boundary. Explicit clearance/trim exclusions now remain protected through later passes, including cells already removed by radius erosion; open boundaries are not fillable. A genuinely enclosed, unprotected hole can still be filled.
- **Main floors misclassified as local trim:** the voxel supplement examined every flat WMO floor. Near exterior drops it could reject a real building floor, revoke its source-height authority, and subsequently fit a contour vertex roughly 1.5 yards downward onto underlying terrain. Only source-labelled bevels enter this supplemental heuristic now. Complete source-patch classification still handles raised flat shelves.
- **Rejected geometry vanished from height-fit headroom:** excluding a cap/prop as a walking support also removed its physical overhead collision from the fitting lookup. Walking eligibility and solid geometry are now separate. Rejected caps, blocked props and underside faces still prevent fitting a floor underneath them without enough headroom. The old guard expecting usable floor directly under a solid low cap was incorrect and has been replaced with physical headroom and adjacent-clear-floor checks.
- **Extrapolated wall planes overrode terrain:** a nearby WMO edge could win against directly supported outdoor terrain merely by being nearer the original raised voxel height. Direct terrain within the voxel-error band now takes priority; bounded WMO edge support remains available for genuine built-floor margins where terrain belongs to a lower storey.
- **Roof incorrectly implied a flat floor:** the indoor detail clamp could raise valid recessed/ramped collision detail toward the polygon hull. Collision-verified detail within that polygon's storey band is retained. Outlier/storey safeguards remain.
- **Narrow sloping railings passed as ramps:** the ramp exemption checked length along the slope without checking usable standing room. Thin raised strips over a wider genuine ramp are now rejected, and their attached flat caps do not inherit a ramp exemption. A bent/U-shaped strip is not made safe by its wide bounding projection. Open supported narrow transitions, real wide ramps and actual treads remain guarded. Captured source patches 5707, 5778, 7020 and 8044 were previously accepted and are now rejected.
- **Duplicate/nonmanifold patch boundaries:** repeated WMO faces from neighbouring source tiles could cancel the apparent perimeter, and multiple same-side faces on an edge were incorrectly treated as an interior edge. Physical faces are measured once, every rejected copy retains its rejection, and edge-side coverage determines whether a boundary exists.
- **Positive-area triangles could round to zero:** the detail cleanup's float cross products could cancel for very thin positive footprints. The exact-zero decision now uses double arithmetic on the stored float coordinates.
- **Detail fitting could change storeys:** an already bad detail height could make underlying terrain appear nearer than the staircase owning that detail. Coarse polygon vertices are fitted first; detail fitting uses the owning polygon's interpolated height/storey band. Verified local detail and real terrain relief remain supported; an unresolved detail point falls back to its own coarse surface rather than an unrelated lower floor.
- **Ceilings could be promoted as footing:** down-facing WMO triangles shared null-area treatment with useful ordinary low risers. They now have separate temporary raster provenance, cleared before compacting and protected from low-obstacle promotion. Its merge priority is below real terrain/WMO/swim footing so an overlapping underside cannot erase usable ground. Steep/vertical ordinary risers retain their existing climb handling. This marker is never an exported navigation area.
- **Unemitted regions raised contour corners:** a non-null area with region zero could still raise a neighbouring contour. Corner height and border evidence now come only from emitted regions, including valid border regions. A region-zero intermediate cannot transmit a higher diagonal corner.

Rejected low caps/props also receive an explicit clearance margin indoors; legal stair risers do not receive that treatment. Like tall-wall clearance, this is checked against actual source geometry rather than inflated voxel boxes. An initial voxel-only attempt cut five entrance side samples and was rejected; exact cap-footprint checks retain all 881 tested entrance samples. No coarse subdivision, global retessellation or independent tread-surface reconstruction was added.

Review diagnostics can record compact areas and fitted vertex arrays at successive stages under EXPLORER_MESH_STAGES. These are off by default and do not change the geometry. The evidence includes targeted policy guards, captured source rejection reasons, serialized seam checks, entrance coverage, an outside-to-upper-stair route and native/browser comparison. A captured source replay is still not the complete raw tile or a whole-world visual certification. Inspect a rebake of 0000_48_32 with 3.7 before a further world bake. Some camera-local samples near furniture/platforms remain ambiguous and are not reported as proven clear floor.

Backend comparisons distinguish bit identity from geometric agreement: a strict captured native/WASM byte comparison exposed floating-point differences despite matching bake metrics. The focused comparison checks exact topology/flags/bounds, finite coordinates within a recorded 0.002-yard tolerance, and both serialized meshes' seam/route behavior; bit-identical output is not assumed. Regression guards explicitly reject NaN where a usable height is expected, rather than relying on an absolute-difference comparison that NaN can silently pass.

### Exterior footings, furniture and stair transitions (explorer-recast-2026.3.8)

The owner's 3.7 inspection confirmed that the interior wall-climbing problem improved, but identified remaining exterior footing strips, tabletops, entrance stair irregularities and stairwell trim on Northshire tile `0000_48_32`. The retained source capture reproduces these surfaces; the fixes contain no Abbey coordinates or object IDs.

- **Local exterior footing rejection:** a narrow exposed lip may share one coplanar source patch with the building's main floor. Rejecting that complete patch would destroy the rooms. Flat structural raster spans now require an actual standing-height backing wall and a directly supported, uncovered lower floor on the same side of the wall before being rejected locally. Terrain beneath a room does not qualify because the room floor blocks standing headroom. Decisions are collected before applying exclusions. Ordinary radius erosion and the existing interior wall clearance remain unchanged.
- **Preserved stair approaches:** valid broad ramps, their treads and a bounded ramp-to-landing edge band are excluded from local footing rejection. An intermediate attempt removed entrance side coverage and was rejected. The final retained-capture check covers all 881 original entrance samples with no missing or below-collision samples, and preserves the outside-to-upper-stair route. This sample check does not certify every pixel or every edge of a complete raw tile.
- **Broad suspended furniture:** standing width alone cannot identify a usable floor. A flat WMO patch is rejected as suspended furniture when most sampled faces have both a nearby underside and a lower WMO floor too close for a character to stand between them. Ordinary solid landings, open single-ramp landings and terrain under buildings are not sufficient evidence. The captured tabletop patches are rejected while the surrounding room floor remains.
- **Stair trim joined to railings:** a narrow flat patch connecting several narrow sloping railings no longer receives an automatic ramp-landing exemption. Actual wide ramp connections and the existing single open-ramp landing case remain valid. The captured raised stairwell ring is rejected while its lower landing is retained.
- **Ramp/tread height fitting:** a smooth collision ramp and its overlapping treads can have different source patch IDs. The bounded buried-ramp repair now accepts their verified walking heights within two height cells instead of requiring patch identity. The existing continuous Recast surface is retained; this does not export independent tread surfaces or introduce global subdivision.

Targeted regression guards cover local inside/outside classification on one shared floor plane, suspended tables versus floor/landings, trim joined to multiple railings, ordinary stair coverage and floor-height ownership. Native/browser builds and captured serialized geometry are checked separately. Reopen the updated portable application and rebake this tile before another world bake; existing saved tiles are not modified automatically. Whole-world visual perfection is not a verified claim.


### Open floor steps, furniture bevels and planar detail (explorer-recast-2026.3.9)

Current-build Abbey screenshots showed walkable raised rims removed along with trim, furniture edges retained as ramps, and excessive flat-surface triangulation. This revision changes the shared native/WASM bake pipeline; existing exports require rebaking.

- Low open flat transitions up to two height cells (bounded by AgentMaxClimb) can remain even when their narrow rim cannot contain a whole standing disk. They require lower, body-clear WMO-floor evidence in multiple directions and no backing wall. Wall-backed footings and high narrow fence caps retain their existing rules. The subsequent physical body-clearance filters remain authoritative.
- Suspended-furniture evidence is weighted by actual projected triangle area, rather than number of faces. Sloped undersides can provide the same physical evidence as flat undersides. Rejection follows shared collision edges onto attached furniture bevels only with underlying-floor/suspension evidence, preventing the tabletop's edge from surviving as a ramp after its top was removed. Underlying room floors are retained.
- A proposed broader conversion of the stair/tread/trim votes to area weights produced two real upper-stair seam gaps in the captured replay. That version was rejected. Existing stair-continuation votes remain; the area-weight change is limited to furniture and the new low-step evidence.
- Final detail reduction removes redundant interior samples from single-loop convex planar patches and local planar fans. Coplanarity tolerance is 0.0001 yard. All boundary samples/flags, coarse polygons/links, curved edge profiles, nonplanar relief and holes remain. No global cell/detail resolution change or generic hole filling was introduced. Metadata records `planarDetailVerticesRemoved`.

The retained Abbey source replay removes 464 redundant detail vertices (924 preview triangles relative to the same classification before local-fan reduction). Compared with the prior 3.8 replay, preview triangles change from 23,316 to 22,589; serialized size changes from 599,252 to 597,172 bytes. This is a modest measured reduction, not a claim that world exports are dramatically smaller. More valid floor retained by classification can offset some savings.

Validation: native and actual WASM outputs agree within the existing 0.002-yard backend tolerance, including topology/flags/bounds. Both serialized outputs pass shared-edge/internal-seam and outside-to-upper-stair route checks. The direct captured replay has zero gaps/missing shared edges over 2,752 edges, and preserves all 881 checked entrance samples with none below collision. Synthetic guards cover low open rims, table/bevel exclusion, planar fan reduction, boundary samples/flags and nonplanar relief. Existing surface/height guards and 15 TypeScript tests (72 assertions) passed. Build/typecheck, native publish, comparison and packaging exited 0.

These focused checks do not certify every Abbey pixel or every world surface. Broad room sampling still includes absent/raised points around obstacle margins, rejected trim and stairs that are not all independently classified. Reopen the portable build and rebake only `0000_48_32`; inspect the reported platform rims, table edges and stair corners before a world bake. Saved editor/browser data is preserved by packaging, and is excluded from the distribution archive.

Evidence: `.cache/oct28-mesh-quality/evidence.json` and `.cache/abbey-full-investigation/oct28-parity-evidence.json`. Rebuilt launcher SHA256 `f04b88a5a499d100c4a37755225fb24dbd1379153ac5408af2496e2f9cc114fc`; native bake executable SHA256 `84caef5c61bc9eba47248596c2fd9af6431427e6e16ff18034b5f2b9321c6309`; portable ZIP SHA256 `1209c120d9f3d14a87e787cf440ed98176dc1f944005ff838080dc39cfc6e492`. All final checks above exited 0; failed bisection runs remain recorded separately.

## Collision-data investigation and revision 2026.3.10

The owner rejected the 3.9 visual result. Its passing route, seam and selected
coverage checks did not establish correct surfaces throughout the building.
This revision was investigated directly from captured triangles and Detour
stage snapshots, without operating the viewer or game UI.

Concrete defects found and changed:

- Whole coplanar source patches were being used to reject small trim. Such a
  patch can also contain a legitimate floor or raised rim. Production patch
  rejection is now restricted to inclined exterior obstacles with exposed
  terrain and headroom evidence. Horizontal floors are handled locally.
- The configured climb tolerance could connect furniture to the room floor.
  Default indoor links now retain two-voxel treads (about 0.53 yards with current
  defaults), instead of accepting the entire general climb allowance. Explicit
  IndoorStepCut overrides remain honored. Physical body clearance still uses
  the configured radius; its ignored lower band is three voxels, bounded by
  AgentMaxClimb.
- Integrated WMO tables do not necessarily contain a room floor beneath their
  centres. The connected-surface pass checks underlying AND adjacent floor
  support before rejecting isolated raised surfaces. Connected stair/landing
  surfaces remain with the walking floor. Rejected surfaces also lose authority
  over subsequent source-height fitting. This is a geometric policy, not a
  semantic guarantee that every world object is classified correctly.
- Source-edge extrapolation could raise a low mesh edge onto an unrelated
  nearby surface. One captured exterior vertex moved from 81.06667 to 81.67911
  during fitting alone. Upward extrapolation is now limited to one voxel; that
  vertex remains at 81.06667. Direct supported fitting is separate.
- Welding only coincident vertices did not synchronize different detail samples
  on the same linked coarse edge. The new pass shares the piecewise height
  profile and breakpoints, canonicalizes float XY positions, and splits only
  affected detail triangles. It preserves boundary flags and enforces Detour's
  byte-index limits explicitly. It does not flatten stepped edges into chords.

### Inspectable evidence

`tools/reviewMeshCollision.py` renders depth-buffered collision/previous/current
cutaways from JSON triangle data. It needs Python, numpy and Pillow, and takes
`--source`, `--baseline`, `--candidate`, `--regions` and `--out`. The mesh folders
contain `surface-weld.json`, emitted by the native checker with
`EXPLORER_MESH_STAGES=1`. Coordinates in these snapshots are native WoW XYZ.
`docs/diagnostics/abbey-regions.json` records six comparison windows;
`docs/diagnostics/abbey-2026.3.10.png` contains their comparison. The cutaway ceiling
is shown in each panel; a missing upper storey above that ceiling is intentional.

The retained Abbey source is a **cropped capture**, not the missing original
86.5 MB full-tile input. Its SHA256 is
`13e6f6083e1eeecc3c88bba7fbc755d263972af6d543fbac6af0c6fa195402cd`.
It supports analysis of this building, not a claim that the whole continent is
correct. Earlier experiments with reduced CellHeight/climb, unrestricted source
patch filtering, and unshared detail edge profiles were rejected. Intermediate
failed seam checks are retained under `.cache/oct29-mesh-review/`.

The final captured replay has no seam samples over the 0.01-yard tolerance,
no missing linked-edge coverage, and no internal tread cracks. Its largest
measured linked-edge difference is approximately 0.00955 yards; it is not zero.
The outside-to-upper-stair route is complete (32 polygons). Three raw entrance
cross sections at X -8914, -8913 and -8912 show continuous mesh above collision.
Synthetic checks also cover an integrated table with no floor under its centre,
and unequal shared-edge sampling with a raised midpoint. These are bounded
checks, not a promise of perfect world geometry. Full-tile visual acceptance
remains necessary before recommending another world bake.

Build/package and native/browser parity completed with exit 0. Exact executable,
WASM, checker, input and portable archive hashes and per-command exit statuses:
`docs/diagnostics/abbey-2026.3.10-evidence.json`.
Native engine SHA256:
`4f79b516c4c49b110d381cc25446294408caeffafe4220e4f8d0b113305ab419`.
The portable launcher is unchanged; the application assets and both baking
engines have been updated. Saved browser/editor data was preserved and excluded
from the portable archive. Inspect a fresh single-tile bake before another world
bake; existing mesh files do not change merely by updating the program.

## Revision 2026.3.11 — remaining wall/window trim and stair margins

The owner's 2026-10-29 screenshots exposed wall spikes remaining after 3.10,
including the paladin room, upper corridor, stair banisters and exterior trim.
Source/mesh cutaways for these views are recorded in
`docs/diagnostics/abbey-2026.3.11.png` (collision, 3.10, candidate). Regions are
recorded in `docs/diagnostics/abbey-remaining-regions.json`.

Specific corrections:

- The production cap filter was restricted to exterior bevels. Actual indoor
  window-base faces already rejected by the geometric classifier were allowed
  back in. Production local-obstacle filtering now applies inside too. Broad
  horizontal coplanar floors remain protected; individual open steps/rims are
  distinguished from narrow rejected trim. No building IDs or coordinates enter
  the production policy.
- Small face height span does not imply a low step: one remaining window bevel
  spans only 0.2242 yards vertically, but sits roughly a yard above its room.
  Preserve small inclines only when they have verified nearby low-step access
  and no standing-height backing wall. This removed the remaining pointed
  window bevels instead of globally flattening interior geometry.
- The local wall-footing ramp exemption could accept a long, short moulding
  solely for tangent length. It now checks meaningful rise and gradient run.
- Raised cap components at open windows could be skipped by the roof-based
  indoor label. Direct WMO support now permits checking those components while
  preserving terrain components; a one-cell component needs one support sample,
  rather than an impossible two distinct members.
- Ordinary body clearance once again honors AgentMaxClimb. The smaller
  three-voxel cutoff introduced in 3.10 could treat a configured legal stair
  corner as a wall. Explicit source-rejected cap/prop clearance remains separate.

Focused checks include an elevated short indoor bevel (floor retained, bevel
rejected), configured legal-riser clearance, the prior open-rim/table cases and
unequal shared-edge sampling. The captured replay's shared-edge check reports
0 gaps over 0.01 yards, 0 missing edge samples and 0 internal cracks; its worst
edge difference is about 0.00091 yards. The outside-to-upper-floor route remains
complete (33 polygons).

The saved source remains a cropped investigation capture. A scan of its entrance
collision/mesh overlap found no below-collision samples in the checked band;
that does not rule out a missing full-tile corner. The full-tile staircase-hole
report is not declared conclusively resolved by that scan or the route check.
Visual acceptance of a fresh tile bake is still needed. No world rebake is
recommended on the strength of these bounded checks alone.

Production build, native publish, native/browser parity and portable archive
validation finished with exit 0. Full hashes and command results are in
`docs/diagnostics/abbey-2026.3.11-evidence.json`. Native engine SHA256:
`a3916abc37d9c73690509a39d56a65e5860ddfb89111f4e84045d9dc0f7d1eff`.
The existing portable folder and ZIP now contain 3.11 assets/engines. The
launcher executable and browser/editor profile were preserved. Reopen the app
and rebake tile 32,48 for fresh visual acceptance; saved mesh is not rewritten
merely by updating the program.


## 2026.3.12: shared contour gaps, stair storeys and low floor thresholds

This revision was located and checked directly against retained collision and
serialized Detour geometry. No viewer automation or new camera reports were used.
The source is still a cropped Abbey investigation capture, not the full raw tile.

Three distinct causes were reproduced:

- A winding-repaired contour retained a bend on a portal while its neighbouring
  contour used a chord. Both source compact regions were walkable, but the
  mismatched outlines left a triangular floor crack near (-8905.072, -203.571).
  Portal construction now shares the union of raw breakpoints selected by either
  side, before hole merging and polygon construction. It preserves edge flags
  and does not copy every raster vertex or globally tighten simplification.
- A stair contour corner outside the upper collision footprint could snap onto
  a lower WMO floor. Near (-8909.551, -202.05), two fitted vertices fell about
  1.47 yards; the resulting triangle lay 0.414 yards below the stair. Edge support
  is now considered for a wrong lower WMO storey as well as terrain. A directly
  supported floor within climb remains authoritative unless the alternative is
  a full-width rising continuation. This also corrects the reproduced corner
  near (-8874.601, -181.35), without pulling flat landings onto narrow trim.
- Short circular-floor transitions were being classified as wall-footing/bevel
  obstacles. Sub-voxel changes are no longer footing evidence. A low transition
  must have broad, exposed flat support rather than a small neighbouring tread
  or shelf. Short bevel exemptions similarly require a nearby broad exposed
  walking floor; their own small height span alone does not qualify them.

A wider low-footing exemption was rejected because it changed 82 upper-landing
samples by more than .05 yard. The retained rule preserves 1,537 sampled flat
upstairs walkway positions within .01 yard (largest difference .00768). One
sample on an adjacent nonflat transition changes by .058 yard; that sample is
outside the flat control band. No claim of byte-identical upstairs topology is
made.

Both shipped native and browser outputs pass the four named collision-point
checks (two stair corners, shared-contour floor gap, circular threshold). Their
serialized geometry/topology/flags/bounds match within the existing .002-yard
backend tolerance. Shared-edge and internal-crack checks report zero failures;
the entrance-to-upper-floor route remains complete. Focused mesh-quality,
surface-rule and surface-height tests all exit 0, including new positive and
negative cases for portal symmetry, direct floor precedence and low thresholds.

The captured tile changes from 590,956 to 594,500 bytes (about .6%); this is not a
global detail-density increase. Redundant planar detail reduction remains enabled.

Reproducible tools: `tools/checkMeshRegression.py`,
`tools/mesh-native-check/CoverageAudit.cs`, and `tools/reviewMeshCollision.py`.
Named points and the upstairs control are in
`docs/diagnostics/abbey-regression-points.json`. Images and full tool hashes/exit
results are in `docs/diagnostics/abbey-2026.3.12.png` and
`docs/diagnostics/abbey-2026.3.12-evidence.json`.

The broader coverage audit intentionally reports candidates rather than claiming
all source triangles should be walkable: intended props, roof tops and capture
boundaries are present. These results do not prove every photographed location
or the entire world is perfect. Full-tile visual acceptance remains necessary.
Native publish, browser/typecheck build, parity and archive validation exit 0.
Native engine SHA256:
`c483f176e4beb4d5f674b78acba11dc06f7d8a9e8b950de3258a8b09e33ff562`.
The existing portable folder and ZIP contain 3.12. Reopen Explorer and rebake
map 0, column 32, row 48. Updating the application does not rewrite saved tiles.


### Capture from an unsaved preview

The capture button was incorrectly gated with saved-export analysis actions. A single-tile preview did not assign editorDirectory/editorMap, leaving capture disabled. Capture now depends only on not being busy, prompts for a writable destination before awaiting source reads when no export is open, and saves the matching baked tile to diagnostics/<map>-<column>-<row>-baked alongside the input. It does not attach the diagnostics folder as an editor export or change saved mesh. Typecheck, frontend build and portable archive validation exit 0; the mesh engine remains 3.12. Evidence: diagnostics/capture-preview-2026-10-29-evidence.json. Full current tile input is pending for the five newly reported locations.


## 2026.3.13: full captured tile, remaining Abbey trim and stair holes

The owner provided `WoW-Archives/diagnostics/0-32-48.input` (90,745,914 bytes)
with the matching baked mesh. This is the full map-0 tile input from client
1.60.1.70532, rather than the earlier cropped source. Its SHA256 is
`4d5160a38300283f711bb04cbf22b2827bd77285331e2185eac1286edf8bd186`.
All inspection was direct collision/serialized-mesh analysis and software
rendering; no game launch, game-directory access or viewer automation occurred.

Reproduced causes and retained fixes:

- Low-cap exemptions counted multiple floor probes on the same side of a wall
  as a walkable step. Opposite broad exposed floor supports are now required,
  with body-clear approaches between the cap and the floor. This removes the
  reproduced central-wall cap, stair-edge trim and low window bevel while
  retaining the low circular floor threshold. Floors behind a wall do not
  establish an approach through it.
- The exterior stair hole at (-8915.025, -138.825) was one ground cell surrounded
  by road cells. Its movement links and floor were valid, but region filtering
  left its ground region id zero. Recover only floor area-label components
  physically connected to a retained ground/road region; preserve their area
  labels. Null, blocked and truly isolated filtered surfaces are not recovered.
- A retained singleton region also had all four contour boundary bits set.
  The inherited contour walker skipped that case. Walking its valid four-edge
  contour closes small area-boundary holes without dense global triangulation.
- The indoor stair hole at (-8902.266, -186.0) was a broad floor cell deleted
  as wall footing before compact construction. The classifier found terrain
  through a nearby WMO footprint gap, despite exposed WMO floor under the full
  local character footprint and no body or rejected-obstacle collision at the
  hole. That terrain evidence no longer revokes the supported floor. A lower
  WMO stair layer remains separate evidence; unconditional broad-floor exemption
  changed the upper landing and was rejected.

The exact saved output and the actual packaged native and browser outputs are
checked with `abbey-current-regression-points.json`. Six positive coverage points
(including both new stair holes and the previous fixes) and three negative trim
points pass on both engines. All 1,536 sampled upstairs flat walkway heights
match the owner's prior saved mesh exactly. Both also pass 1,225 central disk
interior samples against collision height plus display clearance, with maximum
deviation .000317 yard. The Abbey-scoped seam check covers 2,790 linked edges,
with zero missing samples, zero gaps over .01 yard and zero internal cracks over
.02 yard. The entrance-to-upstairs route completes in 30 polygons. Focused
quality, surface-rule and surface-height tests exit 0. Tests include the ground
cell surrounded by road, isolated filtered-surface exclusion, a low wall-backed
bevel with floors on both sides, and the broad-floor/terrain-footprint case.

The captured browser mesh had 14,783 coarse polygons; new browser/native outputs
have 14,696/14,698. This is not a global density increase. Tile metadata now takes
its pipeline string from the actual engine result instead of the old static
3.8 placeholder.

Two broader failures are explicitly retained in the evidence:

- Strict ordered native/browser comparison fails on vertex counts. The full
  files differ by 344 bytes; the previous full native replay versus the owner's
  previous browser mesh has the same size difference. Both current outputs pass
  the reported-location and continuity checks independently; identical backend
  triangulation is not claimed.
- The full-tile seam scan finds one .015881-yard difference near
  (-8533.359, -190.10735), outside the Abbey at the tile edge. The owner's baseline
  has precisely the same failure. No new full-tile seam failure is observed;
  this pre-existing boundary discrepancy is not declared fixed by the Abbey work.

A wide central-floor comparison also includes changing trim/clearance boundaries;
41 samples change there. It is not used to claim unchanged perimeter geometry.
The interior disk and upstairs controls above are separate, explicit tests.
Failed broad-footprint experiments and strict comparisons remain in
`.cache/oct29-current/`; nothing is relabelled as passing.

Full hashes, exit codes, point heights and limitations are in
`docs/diagnostics/abbey-2026.3.13-evidence.json`; the collision/previous/candidate
cutaways are in `docs/diagnostics/abbey-2026.3.13.png`. Both publish builds,
TypeScript checks and portable archive validation exit 0. Native engine SHA256:
`f5f721bb0f37adb377f5fb90938ab2bee53b412e53f9bdae70d1d554fa13d930`.
The existing portable folder and ZIP contain 3.13. Reopen and rebake map 0,
column 32, row 48 for visual acceptance. The owner's capture and saved mesh were
read only and have not been rewritten. These are full-tile checks of the
reported Abbey defects, not a claim of whole-world perfection.

## Follow-up 2026.3.14 — floor rings, wooden landing and cave

The full captured map 0 tile (32,48) reproduces the round table's over-cut floor ring, the central room's reduced floor edge, the upper wooden landing at camera Z117.362, and the nearby cave gaps. Collision/serialized-mesh cutaways are in `diagnostics/abbey-cave-2026.3.14.png`; exact executable, WASM, checker, capture and package hashes with command exit statuses are in `diagnostics/abbey-cave-2026.3.14-evidence.json`.

The source cap filter now verifies an exposed standing footprint across adjacent floor triangles. Individually narrow coplanar patches are not sufficient evidence of furniture or trim, particularly on the gently varying cave floor. Low floor borders can use continuing support at the same elevation. Furniture removal requires lower-floor evidence along most of the component's real perimeter; two nearby lower stair samples no longer delete a whole landing.

Preserving the wooden landing alone left an isolated island. Its short transition turns around a railing, with clear upper/lower floor approaches approximately 90 degrees apart. The final transition rule permits that verified turn, instead of demanding opposite compass rays. It still requires both elevations and collision-clear approaches. Same-side lower-floor probes do not establish a turning transition.

Both packaged engines pass 11 captured coverage points, four trim/tabletop exclusion points, 1,536 unchanged upstairs floor samples, Abbey/cave seam checks and routes through the cave and from outside to the wooden landing. The native tile has 14,806 coarse polygons, compared with 14,698 in the prior native build. No global cell-size or detail-density increase was introduced.

This is bounded verification, not perfect whole-world coverage. The broad cave diagnostic audit improves from 2,254 missing samples to 638 among 29,164 eligible samples; five raised samples remain. These boundary/ledge diagnostic findings are not a passed all-floor gate. Exact source connectivity, sampled source paths and graded-floor experiments did not reduce those residual findings and were removed. The previously documented full-tile 0.015881-yard seam discrepancy outside the Abbey/cave also remains; scoped seam checks pass. The owner must reopen the portable app and rebake tile (32,48) for visual acceptance before a world bake.

## Follow-up 2026.3.15 — cave floor omissions

Five additional cave camera locations now reproduce from the full captured tile: (-8655.045,-124.708), (-8641.078,-131.283), (-8592.298,-146.533), (-8587.675,-165.839) and (-8541.596,-171.485). Actual collision/baseline/shipped-native cutaways are in `diagnostics/cave-2026.3.15.png`. The Abbey comparison is `diagnostics/abbey-control-2026.3.15.png`. Hashes, exact exits and retained failures are in `diagnostics/cave-2026.3.15-evidence.json`.

The footprint test incorrectly expanded a whole body at every perimeter probe, effectively doubling required wall clearance. Body clearance is now checked once at the standing center. Floor support follows short, directly supported samples bounded by the configured slope, allowing real cave-floor creases without inventing support across an abrupt drop. Several inset standing positions are tested before rejecting a whole long face; one centroid beside a wall does not prove the entire triangle is an obstacle. Physical wall clearance remains per compact cell.

The south bend also lost valid compact-floor coverage during contour simplification. The repair refines only shallow, selected WMO floor boundaries that omit retained floor-cell centers, preserving raw outline/hole winding and portal flags. Full stair/ramp slopes and flat landings retain the established contour policy. The broader first variant changed 46 upstairs samples; a later arbitrary-slope variant changed 12. Both were rejected. The final revision leaves all 1,536 approved upstairs control samples unchanged in both packaged engines.

Native and WASM pass all 16 named coverage points, four tabletop/trim exclusions, the five new local cave routes, existing Abbey/cave routes and scoped seam checks. Native coarse polygons increase from 14,806 to 15,446 as previously missing floor is retained; tile bytes increase from 5,912,752 to 6,050,776. Cell size and global detail sampling settings are unchanged.

The broad raw-surface diagnostic examines 120,873 samples with identical current-source eligibility against both meshes: missing samples decrease from 15,975 to 5,013. Buried samples change from 73 to 130 and raised samples from 32 to 73. This is not a passed whole-cave coverage gate: it includes narrow ledges, boundaries and overlapping source layers without complete standing-footprint/headroom eligibility. Those remaining diagnostic samples are retained honestly for investigation. The existing 0.015881-yard tile-border seam discrepancy remains outside the reported cave/Abbey scopes. Adjacent-tile absence cannot be repaired by rebaking only this tile. Reopen the portable application and rebake map 0, tile (32,48) for visual acceptance.


## Follow-up 2026.3.16 - bridge deck pinching

The bridge at camera (-8980.903,-296.641,74.594), map 0 tile (32,48), reproduces from the same complete EMR2 capture. Its source deck is continuous. Rejected support faces underneath it contaminate the higher deck's area during successive Recast span merges. At (-8980.933,-296.6673), a merged span extending from Z67.73333 to Z71.73333 incorrectly carries area 60. Clearing that marker removes the deck, then outdoor erosion widens the omission into the visible side bites.

Rejected WMO faces are now rasterized separately to retain their own top evidence. A merged span is restored only when every overlapping rejected top is buried by more than one voxel and an eligible WMO triangle clipped to the same cell confirms the surface top. Exposed/coplanar caps remain blocked. Clipping the whole source cell matters: a sloped plank's high corner can quantize above its center sample. No global radius, slope, cell size or detail setting changed.

Both packaged engines pass 46 bridge deck coverage points, 16 prior Abbey/cave points, six obstacle exclusions, 1,536 exactly unchanged upstairs samples, bridge traversal and existing routes. Scoped bridge, Abbey and cave seam checks pass. Native coarse polygons are 15,481 and tile size is 6,056,324 bytes (3.15: 15,446 and 6,050,776). The complete tile restores 527 buried-marker spans. Synthetic checks retain exposed/co-quantized caps, require direct support, and cover sloped high-corner support.

`diagnostics/bridge-2026.3.16.png` compares actual source collision, the previous saved tile and the new native tile. Abbey and cave control comparisons are also retained. `diagnostics/bridge-2026.3.16-evidence.json` records executable/WASM/checker/capture hashes and exact command exits. The pre-existing 0.015881-yard far tile-border seam failure remains recorded; the full-tile seam gate is not declared passed. The portable folder and ZIP contain 3.16. Reopen the application and rebake tile (32,48) for visual acceptance.
