# Cursed tile-capacity audit, 2026-11-01

The owner is baking a new dataset under Documents/mmaps/ExplorerMesh-1793589091261.
The working Mappster control is Desktop/mmaps. Neither dataset was modified.
The Explorer scan is a snapshot of an unfinished bake (217 complete tiles at scan
start), not a complete-region capacity guarantee.

## Findings

- Current declared arena: 256 MiB total. World heap 240 MiB, world staging 8 MiB;
  transport heap 6 MiB, transport staging 2 MiB. World cache has 100 slots.
- Existing Mappster dataset: 1,323 world tiles, median payload 1.96 MiB,
  maximum 4.98 MiB. None exceeds the world staging contract.
- Explorer snapshot: median payload 3.96 MiB, maximum 8.15949 MiB.
  0000_30_39.mmtile contains an 8,555,844-byte payload, exceeding 8,388,608
  bytes by 167,236 bytes. It does not fit the declared world staging buffer.
- Among 215 matching tile names, median Explorer/Mappster payload ratio is 1.95.
  This is a file-size comparison, not proof that collision complexity doubled.
- The largest completed 5x5 neighborhood in the snapshot totals 146.09 MiB of
  payloads. That alone is below the world heap, but omits Detour mesh/query
  overhead, allocator fragmentation and retained older tiles.
- Cache eviction runs on slot exhaustion. If allocation fails with a free slot,
  acquire returns failure without trying memory-pressure eviction. If one slot
  eviction frees too little, it also returns without evicting additional tiles.
- A host-only probe compiled the actual public NavArenaHeap and NavTileCache
  sources and read completed Explorer payloads through a reader enforcing the
  declared 8 MiB staging limit. At the later snapshot (229 tiles), 126 loaded,
  103 acquisitions failed, 1 was oversized, and 94 successful reads were refused
  by allocation while free cache slots existed. This reproduces the pressure
  behavior. It is not a live-game route test. The probe gives the cache the full
  240 MiB heap and omits Detour allocations, so its memory allowance is optimistic.
- No transport tiles were present under either scanned root. The boat's 2 MiB
  per-tile staging and 6 MiB aggregate heap requirements remain unmeasured.

## Next work

Keep baking. Before using the complete dataset, measure final world/boat maxima,
address the staging ceilings with measured headroom, add bounded eviction and
allocation retry for heap pressure, and verify dense-neighborhood transitions
with real Detour/query allocations. Do not increase slot count as a substitute
for memory management. Do not lower mesh quality merely to fit old assumptions.
No Cursed implementation, runtime, driver or installed dataset was changed.

## Evidence

- [File-size scan](diagnostics/cursed-tile-size-2026-11-01.json)
- [Host cache reproduction](diagnostics/cursed-cache-pressure-2026-11-01.json)

The host probe build and final reproduction both exited 0; zero in this probe
means the suspected failure was reproduced. Executable/compiler/source SHA-256
hashes and two earlier overly restrictive assertions (exit 1) are retained in
the reproduction evidence. No resident/driver build or game launch was performed.
