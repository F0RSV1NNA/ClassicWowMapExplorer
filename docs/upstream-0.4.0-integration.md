# Upstream 0.4.0 integration - 2026-10-29

Merged Coldensjo/ClassicWowMapExplorer through `4eddd687176658114821b332797685194edcb9b7` (45 commits after the shared base) into the fork based on `9734f04` (Northshire 100% perfect). The merge preserves the fork's mesh generation/editor/export features and the approved 2026.3.16 engine.

Upstream additions include game-version selection and optional quick switching, invisible-wall highlighting, Classic DB2 layout fixes, Classic distant terrain textures, loading improvements, a revised start screen, and an MIT license. The unused storage inspector and the old direct-access folder-opening option were removed upstream. Mesh export/editor folder selection remains available.

Two text conflicts were resolved. The mesh menus and new Game version menu are separate siblings. Db2 retains upstream's numeric layout hash and field maps; the mesh liquid adapter converts numeric hashes to DBD hexadecimal layout names. Numeric and string inputs are covered by tests. The portable package includes upstream's license, and the launcher now reports 0.4.0.

All 86 TypeScript tests pass, type checking and browser/native/launcher builds succeed, and static built-page checks confirm unique IDs and correct menu structure. Both actual engines replay the complete Northshire capture to a tile byte-identical to their respective approved 3.16 outputs. All 62 coverage points, six exclusions and 1,536 upstairs samples pass; bridge/Abbey/cave seams and existing routes pass. The known far tile-border seam failure remains recorded. Hashes and exact mesh command exits are in [the evidence](diagnostics/upstream-0.4.0-integration-evidence.json).

An experimental MPY2 physical-filter extension was rejected because it changed the already accepted source collision policy. The final merge retains upstream's MPY2 invisible-wall display without changing any approved C# extractor or navigation source. A captured-format regression test guards that distinction.

Automatic approval review blocked the proposed headless browser smoke test without providing a specific reason. No browser runtime pass is claimed. Reopen the portable application to check the revised menus and game-version switching in use. No installed game directory was accessed during verification.
