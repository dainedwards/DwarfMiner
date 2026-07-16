# Cell sim: materials, flying cells, ambient rules

<!-- Split out of the old noita-sim note 2026-07-16. CLAUDE.md is the single source of
     truth and links here. Notes are dated and historical — trust the code over any line
     here, and correct the note when you find it stale. -->

## 2026-07-13 Noita-feel pass (branch `noita-sim`)

- **Flying cells** (Cells.cs): grid cells launched with full 2D world velocity, integrate under planet gravity, re-enter grid via TryDeposit/Place on contact. Mass-conserving (`LaunchCell`/`EjectFromTile`); `LaunchAtWorld` is emit-only (fire embers). Transient — dropped by WriteState like kinetics. Cap `MaxFlying` 4000; drawn as velocity streaks in Cells.Draw (stride==1 only).
  - GOTCHA/FIX (2026-07-14): the only speed cap was `FlyMaxSpeed=520`, so an outward-launched grain arced ~75 tiles into the sky under FlyGravity and hung near its apex as a lone 1-px "floating pixel in space", always on the side the player fired/dug. Fix = `FlyMaxOutward=170`: `UpdateFlying` bleeds off the radial-outward velocity component each tick (before the move, so even a huge explosion impulse is capped step 1). Keep this — reverting it brings the floating pixels back. Lateral/inward motion untouched, spray still arcs ~8 tiles.
- **Feeders**: CarveCrater ejecta (budget 90 cells, inner debris fastest) + fire seeds in vaporised core; TickLiquid splash (impact > TerminalCells/2, 1/3 chance, needs air above); QuenchIfWet gravel spark; Game1.OnTileBroken dig spray toward the dwarf.
- **Oil (mat 11)**: lighter than every liquid — buoyancy swap at top of TickLiquid (denser cell sinks through oil below). Seeded via Planet.OilSeeds / PlanetDef.SeedsOil (debug, verdant/living, slag; NOT ember — gas band is deeper, oil 14–42 depth) filled in Game1.BuildSessionWorld.
- **SampleHazardsNear returns 4-tuple** (lava, acid, gas, fire); player FireBurnDps 14.
- Tests: SimTest `TestNoitaFeel` — pockets must be carved by ANGLE (`Ti(r, angFrac, da)`), tile counts vary EVERY ring (chord ≈ TileSize, no bands), constant tile index skews and walls leak.
- `hazard: acid melts granite but obsidian resists` became flaky AFTER the splash change (droplets sped acid drainage from its unwalled pocket) — fixed by walling the test in obsidian, carved by angle. Verified base commit 3/3 vs branch 2-in-5 fails before the fix.
- Perf after change: meteor aftermath mean 0.40ms, worst 16ms — no regression (see [performance](performance.md)).

## 2026-07-14 big 15-item combat/world/ecosystem batch (branch noita-sim)

- **Dust labeled by source**: Cells.SrcTileAt(cx,cy); hover readout shows "MATERIAL: <src> (LOOSE)" not "Dust".

## 2026-07-15 fire-vs-snow / water-quench / gem-bed pass

- **Flying grains pass through gas cells**: UpdateFlying — Smoke/Gas/Fire cells no longer block a flying grain (IsBlocked counts ANY cell mat, so the steam curling off an acid splash was parking the next spurt mid-air → acid stacked in the sky and the stream dropped short). Pass-through gated on !SolidTileAtCell (new helper); a flying Fire grain passing a Gas cell still ignites it (IgniteCell).

## 2026-07-14 acid-depletion / granular-hose pass

- **Acid spends itself eating**: TryCorrode now returns bool; TickAcid, when any neighbour bite landed, fizzes the acid cell itself to Smoke 2-in-3 — each cell eats ~1.5 tiles then is gone (a spewer puff ≈ 4-7 tiles, no more ever-deepening pits). Contained world pools (obsidian-lined) unaffected — nothing to eat. All acid sim tests still green.

## 2026-07-15 fine-pixel-fidelity 10-pack (user: "do all those including bigger swings")

- **Material.Snow = 12** powder: rides TickSand (SlipChance 70), IsCompactable → compacts to TileKind.Snow (MajorityKind). `Cells.SnowPersists` set by Weather EVERY frame from biome=="frost" (headless defaults false). Non-frost: exposed resting flake sublimates 1/900/tick (stays awake only while ExposedAbove; buried flakes sleep). **SpawnSnow saturation cap**: skip if CountNear(pos,8,Snow)>20 — snow has NO evaporation counterweight on frost, this cap is the mass bound AND keeps blankets too shallow to compact. Weather deposits snow cells like rain (1-in-3/frame under snowing cloud).
- **Snow melts to RAIN-TAGGED water** both fire-side (TickFire probe `case Material.Snow` 1/2) and lava-side (QuenchIfWet falls back to FindNeighbour(Snow) when no water) — slush dries via the rain-evaporation path.
- **Ambient sweep** (Cells.Update, 0.5s, 36 samples in 340px disc, gated on CompactionExclusion.HasValue so ALL headless tests skip it): (a) **ceiling drips** — open-air sample walks outward ≤12 cells to a bare ceiling; IsPorous kinds (Dirt/Grass/Gravel/MossStone/Conglomerate) with ≥3 water cells above the tile weep a rain-tagged droplet 1/4 (mass-duplicating BY DESIGN — draining would empty lakes through their beds; evaporation is the sink); (b) **moss creep** — water sample walks inward ≤3 to its bed, plain Stone→MossStone 1/30 (MossStone is IsLoose — mossy floors crumble when undercut, accepted).
- **TickSmoke seeps sideways under roofs** (same 2 lines as gas) — cave fires fill chambers ceiling-down and plume out openings.
- **Bubbles**: Cells.PendingBubbles (cap 200, QueueBubble at quench-boil + flying-fire-drowns) drained by Game1 → 3×EmitBubble; player drowning exhales bubble trail in TickAir (underwater branch, dt*2.5 roll). EmitBubble = negative GravityScale rises along local up.
- **Lightning conduction**: `Cells.ZapWater(pos)` BFS ≤700 connected water cells → transient `_zapped`+`_zapUntil` 0.22s (ColorFor water flashes electric white-blue; NOT serialized), returns every-90th positions for arcs. `ZappedAt(pos)` ±2-cell test. FireLightningGun: ray-marches for water BEFORE rock grounding (pool beats rock; closer creature beats pool), chain victim standing in water dumps the arc into its pool; ONE conduction per shot; zapped creatures 16dmg, PLAYER 10 via TakeDamage — never stand in the pool you shoot.
- Suite: all green ×2 runs EXCEPT `rigid: mountain-scale slab breaks into several boulders` — that's the PARALLEL session's rigid-bodies-v2 test (added 01:59, files touched 02:37, mid-feature TryDetach signature race broke the build for ~10 min); `hazard: acid dissolves a soft tile` flaked once, passed 2/2 after.

## 2026-07-15 effects-as-materials — IN WORKTREE `cell-effects`, NOT merged (user wanted it isolated for testing; worktree at `.claude/worktrees/cell-effects`, branched off noita-sim; auto-committer commits there too, so no single feature commit)

- **Explosion smoke = real Smoke cells**: CarveCrater stamps `smokeBudget = min(150, 30 + tiles*10)` Smoke cells (3/tile) into the VAPORISED core; plume rises/pools/decays via existing TickSmoke (~3s gutter). Airburst (nothing carved) stamps a 10-cell epicentre puff via new `Cells.StampAtWorld` (= PlaceAtWorld but refuses cells inside solid tiles — particle rest positions can sit a hair inside a wall's cell footprint).
- **`DM_CELLFX=0` reverts everything** (static `Particles.CellFx`). **`--smokeprobe`** (SmokeProbe.cs + Program.cs arm) verifies: ground burst ≥20 cells + plume rises + decays to ~0 by 8s, airburst 1..10 cells, cinder handoff ≥3 concurrent fire cells. All green first run.
- Not done here (deferred): draw-side pixel polish (raster snapping, quantized fades) — discussed; EmitDust ambient sites (titans/corpses/lander) deliberately left as particles.
Deliberately NOT done (discussed with user): rigid-body chunks (would fight the dust/compaction economy, see [compaction-and-tiles](compaction-and-tiles.md)), blood/stains on bodies — user later approved wet/oily STATUSES (mechanical + subtle tint), which are in.

