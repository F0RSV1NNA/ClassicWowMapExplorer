# Global-WMO instances and transport baking — 2026.3.19

This build adds whole-instance WMO baking and a stationary transport collection. Existing terrain geometry rules are unchanged. Mappster edits/links are not migrated: use Explorer's own Mesh Editor and save `mesh-edits.json`.

## Instance maps

Enter/select the instance in Explorer, then use **Mesh → Bake current map to folder**. Global-WMO instances are extracted from their actual WDT placement, including selected interior doodads and collision groups, and baked as one complete model. Their resulting native tiles can span several grid cells. “Bake all maps” includes them too.

The same collision preview, saved mesh preview, reachability analysis, edits, and rebake operations support these maps. Whole-instance jobs use the native/low-memory path when available and serialize these larger jobs. Completion markers list the actual emitted tile files; resume and reachability inventory do not invent a terrain tile at the job's placeholder coordinates.

## Stationary transports

1. Transports are placed automatically between the continents when the world loads. Open **Mesh → Transport collection → Go to transport collection** to jump there.
2. Pick a model and use **Go to selected transport**, or fly around the labelled collection. Nearby models stream as you approach, like the other map islands; the collection uses spaced shelves rather than overlapping models. The complete model is raised above the sea for inspection.
3. Use **Bake selected transport to folder**, **Bake transport under camera to folder**, or **Bake all listed transports to folder**. Select the root folder that will contain your world meshes.
4. Transport output is written under `transports/`, for example `116306.mmap`, `116306_32_32.mmtile`, and any other tiles that model occupies. The selected bake overlay appears on the staged model. Normal last-bake preview/download controls work too.

Display placement is never exported. The mesh uses Mappster's existing zero-placement local frame, matching Cursed's negative-X/Y conversion at its mesh boundary. Six-digit model FDIDs are retained. Do not place these files alongside world-map tiles at the root.

The bundled candidate inventory contains 143 WMO roots from Mappster's transport catalog, filtered against root files available in the open product. Group/LOD files (including 1497960) are excluded, and an unreadable or malformed entry is skipped without discarding valid models. Some entries are stationary/crashed variants; this is a model inventory, not a list of active server services. Missing collision dependencies fail the bake rather than silently producing a partial deck. M2-only transports are not introduced by this WMO workflow. Existing moving boats/zeppelins remain a separate viewer feature.

Each completed model has a `.transport.json` marker written last. Replacement removes obsolete tiles owned by the old completed model. Interrupted writes invalidate completion. Batch outcomes are recorded in `transports/transport-job.json`.

## Verification

- TypeScript check: exit 0.
- 126 Bun tests: exit 0, including source dependency extraction, whole-instance edits/preview, transport layout/frame conversion, export identity, stale-file replacement and interruption handling.
- Native/browser parity: byte-identical deck fixture, translated/scaled instance fixture, and a four-tile origin-crossing deck. Expected placement is checked against actual preview triangles.
- Misplaced transport and missing collision group fixtures fail as intended.
- Real WMO 106965 reused from the existing Northshire source capture: native/browser outputs match, including all four emitted tiles. This is a captured-model extraction check under fixture map 999, not a live instance or ship acceptance result.
- Existing terrain/road/swim/MLIQ parity fixture remains byte-identical across native/browser engines.

Reproduction: `bun test`, `bun run typecheck`, `bun tools/checkWmoTransport.ts`, `bun tools/checkMeshParity.ts`. The parity checker requires the browser engine built by `bun run mesh:build`.

See [verification evidence](diagnostics/wmo-transport-2026.3.19-evidence.json) for tool/artifact hashes and statuses. Real instance navigation, live ship deck acceptance in Cursed, and visual acceptance of the staging arrangement still need an owner test. No installed game files were accessed, no game was launched, and no driver/resident build was performed.

## Transport availability investigation

Explorer now searches every encoding key for each content variant when checking local presence and reading files. Previously it checked only the first encoding, incorrectly rejecting files whose alternate encoding was installed.

The transport collection displays its complete root-candidate inventory summary and can save `transport-inventory.json`. It records product/build plus an outcome for every candidate: ready, group/LOD, absent from the selected root, absent encoding mapping, non-local, encrypted, unreadable, invalid root or invalid bounds. This separates a short successfully loaded catalog from a complete one.

CDN fallback, known-key BLTE decoding and alternate-only root records are now supported; see [CASC update](casc-cdn-2026-10-29.md). The original unknown count was against Explorer's filtered root, so it did not prove that the files were absent from the raw build metadata. A fresh owner-session inventory is still needed to verify the final count.
