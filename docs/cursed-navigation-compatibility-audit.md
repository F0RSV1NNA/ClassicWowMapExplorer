# Cursed navigation compatibility audit — 2026-10-29

Scope: compare Mappster's producer contract, Explorer's current producer, and Cursed's public navigator and Navigation Lua consumers. This is an offline audit, not a live-game acceptance result. No driver/resident internals were inspected or built, and no datasets were installed.

## Follow-up scope and implementation

The owner explicitly excluded Mappster edit/link migration: create new Explorer edits and save them to file. The migration recommendation below is historical and is not an outstanding task. Global-WMO baking and WMO transport-local baking have now been implemented in pipeline 2026.3.19; see [current notes](wmo-transport-2026.3.19.md). Runtime acceptance remains a separate gate.

## Conclusion

Explorer already emits the essential Mappster world-mesh format, area IDs, polygon flags, and liquid format. The largest missing producer features are transport-local mesh baking and global-WMO map baking. Existing curated edits also need migration. Adoption still needs controlled consumer tests, complete-map reachability analysis, and memory/streaming checks.

**Owner clarification:** portal/elevator links are for reachability analysis: land at the bottom of the Undercity elevator must remain reachable. They do not instruct Cursed to ride an elevator. A Cursed importer/action dispatcher for Explorer link sidecars is therefore **not a requirement** for this migration.

## Existing contract

| Data | Mappster / Explorer | Cursed expectation / status |
|---|---|---|
| `.mmap` | 40-byte wrapper; magic 0x4D4D4150; version 16 | Matches public dataset loader |
| `.mmtile` | 20-byte wrapper; Detour version 7; mmap version 16; little-endian C-compatible Detour payload | Both current Explorer captures load in rebuilt Cursed tests |
| Naming | Four-digit map; native row then column | Matches loader; UI column/row must not be interchanged |
| Native coordinates | Recast uses (Y,Z,X) | Same consumer conversion; display offsets must stay out of exports |
| Road | Area 12 with Ground flag | Used by road preference/exclusion |
| Ground | Area 11; flag 0x001 | Matches |
| Ocean / water | Areas 6 / 9; flags 0x020 / 0x004 | Matches |
| Magma / slime | Areas 8 / 7; flags 0x008 / 0x010 | Matches |
| Swim floor | Area 10; Swim flag 0x200 | Producer contract retained |
| Submerged / under hazard | Flags 0x040 / 0x080 | Retained; public query filters use these |
| Stranded | Flag 0x100 | Exported, but public C++ navigator has no explicit exclusion for it |
| `.mliq` | MLIQ version 2, sparse wet mask, kinds and heights | Both real captured liquid files pass Cursed parser tests |

`NavWriter.cs` and `LiquidMap.cs` are byte-identical between the two producer repositories at audit time. This establishes shared implementation, not complete runtime acceptance. Steep-ground 0x002 is reserved in the consumer; it is not a missing Explorer-emitted feature.

Sources: `src/mesh/engine/Core/Nav/{NavWriter,LiquidMap,NavBake,NavPrune}.cs`; Mappster `Nav/` counterparts; Cursed `Code/include/navigator/{nav_types,nav_liquid}.h` and `Code/src/navigator/{nav_dataset,nav_mesh_context,nav_tile_cache}.cpp`.

## Reachability links: keep their actual purpose

Explorer already embeds declared links in Detour. Portal/elevator modes use area 3 and flags 0, so normal walking filters do not treat them as walking shortcuts. Both the in-memory flood (`Core/Nav/NavPrune.cs`, `Flood`) and disk-paged graph extraction (`engine/StreamAnalysis.cs`, `StreamGraph`) traverse actual Detour adjacency without requiring walking flags. They exclude manually blocked area 4. Consequently a successfully attached analysis link can join otherwise disconnected land while remaining unavailable to ordinary walking queries.

The remaining work here is **authoring/data and regression validation**, not a Cursed traversal implementation:

- Migrate Mappster's existing curated link endpoints, radius and direction into Explorer's project.
- Verify both endpoints actually attach to the intended floors after baking, including cross-tile endpoints with both tiles loaded.
- Run reachability over the complete intended export after baking/editing links; partial-tile components cannot establish whole-map reachability.
- Add an Undercity elevator regression proving that the lower floor loses its stranded mark while an ordinary walking query cannot shortcut across the lift gap.
- Check direction deliberately: directed graph behavior is not equivalent to treating every link as an undirected union.

Existing same-map links do not connect separate map IDs. Large separate landmasses are already retained by the component-size policy. Do not create physical world bridges between continents to solve a reachability display problem.

## Confirmed gaps and priorities

### P0 — Dataset acceptance and reachability policy

Cursed's public navigator does not explicitly filter Stranded 0x100; flags alone do not establish that every consumer honors unreachable marks. Mappster's optional `drop` mode also clears surface flags, whereas Explorer's analysis marks rather than deletes geometry. Decide whether Cursed should exclude stranded polygons, or whether release export should deliberately clear their surface flags. Preserve recomputability and avoid pruning valid floors based on a partial export.

Add a dataset manifest/release check with product/build, engine pipeline, coordinate/flag schema, tile inventory, liquid inventory, transport inventory, edit identity and reachability completeness. Explorer sidecars are not automatically read by the Cursed world loader. Install into an isolated test dataset before replacing the working maps.

### P1 — Transport-local baking is missing

Mappster has `Gui/TransportJob.cs`, `Session.BuildTransport`, and a WMO-root `transports.csv` inventory. It bakes an unplaced model without an ADT clip and writes `transports/{modelFDID}.mmap` and corresponding tiles. Explorer retains the low-level global-WMO extractor but has no integrated transport bake job/catalog/export workflow. Its regular `Program.cs` bake applies `Clip.ForAdt`, which is not a transport-local bake.

Implement model-FDID selection/catalog, extraction, an unclipped model-local bake, separate transport export directory/params, resume/provenance, and local preview. Preserve six-digit FDIDs and exact naming. Do not assume all moving objects are WMO models just because the current Mappster inventory is WMO-based.

**Critical frame contract:** Cursed `Transports/frames.lua` adapts Mappster's zero-placement output by negating local X and Y at mesh boundaries. Reusing a raw model-coordinate exporter without this convention would rotate the deck mesh relative to the ship. Verify a known existing service such as model 116306 against Cursed's established frame conversion.

Cursed already has separate world/local frame conversion, deck routing and hand-verified boat/elevator services. Deck meshes do not generate docks, boarding/disembark points, schedule/state or permissions. Those remain a separate service catalog.

### P1 — Global-WMO maps are not baked

Explorer can display global-WMO maps, but `src/mesh/source.ts` explicitly rejects their bake enumeration with “WMO-only map baking is not implemented yet.” Mappster's `Gui/BakeAllJob.cs` has a dedicated global-WMO path. Implement map identity, placement, extents/tile enumeration and export for these maps, then test a representative instance. A working model viewer is not equivalent to instance navigation coverage.

### P1 — Curated edit migration

Mappster per-map edits/global edits and Explorer's versioned project are different containers. Preserve roads, cuts, additions and reachability links. Mappster's older stroke height band and Explorer's newer per-stroke defaults differ, so blindly applying current defaults could change which floors are edited. Retain source bands, review link modes, and report unsupported entries. No automatic complete migration was established in this audit.

### P2 — Transport visualization

Explorer has generic WMO rendering and optional server object spawns, but not a transport-local catalog/preview or verified transport-service overlay. Add dock/service markers and optional schematic lines between destinations, plus selectable local deck preview. Ships between continents should be a display arrangement only: keep native world and model-local exports unchanged. Static spawn records are not authoritative moving-vessel poses.

### P2 — Runtime scale/coverage acceptance

Check a multi-tile neighborhood under Cursed's real memory budget, streaming/eviction, seams, roads, shallow/deep liquid and hazards, and intended map transitions. Current detailed captures consume approximately 9.28 MB and 10.17 MB respectively in the offline single-tile streaming test. That is not a measured production neighborhood budget. Keep prior geometry regression fixtures, including the already documented full-tile seam limitation.

## Verification actually run

Rebuilt only `nav_mesh_streaming_tests`, `nav_road_tests`, and `nav_liquid_tests` with the existing Release CMake configuration: **exit 0**. No driver or resident targets were built.

- Road semantic/filter unit tests: **exit 0**.
- Liquid format/corruption tests: **exit 0**; additionally the Northshire and Goldshire Explorer liquid datasets each passed with one actual `.mliq` file.
- Current Northshire streaming test: **exit 1**. Tile loaded and path query returned; zero-clearance wall normal was NaN, failing the unit-normal assertion.
- Current Goldshire streaming test: **exit 1**. Tile loaded; the arbitrary corner-based probe produced a one-point incomplete path and the same NaN wall-normal failure.

The streaming fixture chooses tile-corner probes with broad snapping, not the accepted interior routes. These failures do not establish malformed Explorer files, nor do they establish safe consumer behavior. Resolve the zero-distance normal case and use controlled connected start/goal pairs, then compare against a Mappster-produced control. Production adoption remains unverified.

Executable SHA-256, exact commands, exit statuses, output, and dataset hashes are recorded in [the evidence JSON](diagnostics/cursed-navigation-compatibility-2026-10-29.json). Earlier stale-test-binary failures are kept separately in `.cache/cursed-compat-audit/stale-consumer-tests.json` and are not used as current-code evidence.

## Suggested implementation order

1. Establish controlled Cursed acceptance fixtures, reachability policy and release manifest.
2. Migrate curated edits and validate the Undercity analysis-only link case.
3. Add transport-local baking matching the existing frame/naming contract.
4. Add global-WMO map baking.
5. Add transport catalog/preview and service overlay, then validate complete-region streaming before switching datasets.
