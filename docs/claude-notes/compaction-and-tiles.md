# Compaction, tiles and the dust economy

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

## Compaction rules and debugging stuck piles

Compaction (Cells.cs, done 2026-07-12; output changed 2026-07-13): buried grains harden via two burial proofs — *sealed* (near-full + fully roofed + solid floor; dust-packed stone pockets) and *pressed* (≥1 tile of grains above; natural piles — voids get filled by a steal pass that pulls grains down). Pressure shortens the 45s delay (÷(1+tiles above), cap 4). Crest + under-crest layers intentionally stay loose sand. **Output is now the MAJORITY grain's tile kind** (Cells.MajorityKind: tagged dust→its source tile, grass→dirt, dirt→dirt, sand/gravel/untagged→gravel) — TileKind.Conglomerate + the Planet composition table are legacy-save-only (old tiles still spill their stored cells on shatter). Grain conservation is approximate now, by user request. Gems never dust anymore, and as of 2026-07-13 gems are not tiles at all: worldgen seats them as a Planet._gem OVERLAY inside a host tile (common rock/ore, never silver/gold/platinum; 1-in-4 hash thinning keeps the quad economy — each overlay = 1 whole drop). Shatter pops a physical Pickup (no magnet, walk-over collect) via Cells.PendingGemDrops → Session.Pickups; acid/lava TakeGem-and-discard (vein-destruction hazard kept). 2026-07-13 later: EVERY gem-tile shatter is a whole drop (the old ¼-drop GemDropAccum bank made 3 of 4 mined geode/cavern crystal tiles vanish — accumulator + whole flag removed); dust is a FULL Density² tile fill (DustCellsPerTile=16, mass conserves — mined-tile debris occupies the block's space and buried piles compact sooner); block/build placement is held construction (Player.BuildTime 0.45s, TickBuild, material spent on completion, cursor progress bar in Game1.DrawBuildProgress). Renderer: small hash-angled faceted shard over host art (DrawGemCrystal, replaced the big uniform DrawGemLozenge) + slight glow from Game1's ore-scan light pass; hover names (creatures/pickups/gems) ride Game1.HoverName in the debug hover label. Survey/Scanner read GemAt. Gold+silver are charted rarities: base worldgen thresholds unreachable, only def OreBias veins (PlanetGen rolls goldVein/silverVein per campaign, frost=silver, slag=gold-ish, gold-signature slots forced; star map RARE FINDS lists GOLD/SILVER). RunSave v9→10 era: gem overlay section appended to Planet state.

**Why:** natural piles interlock with 10–25% voids and craggy tops, so any near-full/sealed-only rule strands every layer above the first.

**How to apply:** the sweep is nomination-based — three stranding traps were fixed and must be preserved: (1) Compact() re-nominates the tiles above; (2) RecordRest also nominates the tile *below* (its pressure may have just tipped); (3) a second-look fill mismatch re-arms the candidate instead of dropping it (rest events are swallowed while a tile is a candidate). To debug stuck piles: `--simtest` covers sealed/stacked/voided cases; for organic repro, patch a `--pourtest` harness into a /tmp worktree (pour grains into a carved shaft, print per-tile fill/onSolid/press verdicts) — see this conversation's pattern.


## 4px tiles, legacy units, dust economy

On the noita-sim branch (2026-07-10) tiles were quartered: `Planet.TileSize` 8→4 px, ring constants doubled (StandardRings 400, RingMin 40, SkyHeadroom 142), `Cells.Density` 8→4 (grains stay 1 px, same total cell count). Conventions to preserve when editing:

- **WorldGen and gameplay depths are still authored in legacy 8-px tile units**, converted via `Planet.LegacyTileScale` (S=2): WorldGen multiplies ring-unit values by S and computes `depth` in legacy units; noise is sampled at 8-px world pitch. SpawnDirector depth bands likewise divide by LegacyTileScale. Don't add raw ring-count magic numbers without deciding units.
- **Dust economy:** one broken tile spawns `DustCellsPerTile` (Density²/2) cells but a full drop unit needs `DustCellsPerDrop` (×4) — a 2×2 quad of fine tiles = one legacy block = one drop. Player strikes clear a 2×2 footprint (`Planet.Footprint2x2`, `Player.StrikeTile` → `Player.LastBroken` list) and placements stamp 2×2 per inventory unit, so mine/place round-trips 1:1.
- **Compaction:** buried + full + ~10 s-undisturbed granular tiles (Sand/Dirt/Gravel/Dust) press into `TileKind.Conglomerate`; exact cell makeup lives in Planet's composition side table and spills back on shatter (value conserved; `Cells.SpawnDustInTile` handles the branch). Sweep lives in `Cells.SweepCompaction`; `Cells.CompactionExclusion` (set by Game1 to player pos) keeps it away from the dwarf. Covered by `TestCompaction` in SimTest.
- RunSave `Version = 8` gates all of this; old suspend saves are discarded.

**Why:** any future edit that reuses old 8-px-era numbers (depths, radii, budgets in tiles) will silently halve feature sizes; the divisor and 2×2 stamps are what keep the resource economy exploit-free.

**How to apply:** author new world-space distances in px or multiply legacy tile counts by `Planet.LegacyTileScale`; when adding granular materials, decide whether they join `Materials.IsCompactable`. See also [[overworld-roadmap]].


