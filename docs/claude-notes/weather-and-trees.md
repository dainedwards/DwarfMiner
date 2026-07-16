# Weather, trees and vegetation

<!-- Split out of the old noita-sim note 2026-07-16. CLAUDE.md is the single source of
     truth and links here. Notes are dated and historical — trust the code over any line
     here, and correct the note when you find it stale. -->

## 2026-07-14 big combat/city/tools batch

- **Trees/wood** (task): new `wood` material (harvestable, bankable). TileKinds TreeTrunk(50, drops wood×2)/TreeCanopy(51)/TreeCanopy2(52, violet)/SeaFrond(53). WorldGen.ScatterTrees (TreePlanFor density by biome: verdant 26 / ocean 20 / frost·crystal 12 / acid 7 / ember 5 / **slag 4 barren** / **airless 0 no wood**) — trunk column + canopy blob on walkable soil; ScatterWaterPlants (SeaFrond on lakebeds). Trees/plants are **NON-anchored + IsFlora** (hazard-immune, titan-crushable — anchored plants wall the boss out like the old flora bug). FindSurfaceSpawn now skips IsFlora (spawn on ground, not tree tops). Physics settle skips IsFlora (they don't cave). **WorldGen.ScatterVegetation flag OFF in SimTest** (clean surface for titan/defense mechanic tests). Renderer authored-art per kind.

## 2026-07-14 living-tree ecosystem (big pass, branch noita-sim)

- **Trees rebuilt tall/thin/varied**: WorldGen.ScatterTrees now plants 1-tile-wide SLENDER boles 6-16 tall in 4 species (0 spire/tallest, 1 broad/shortest, 2 umbrella flat cap on bare bole, 3 weeping drape). Trunk render narrowed to a ~3/8-wide bar (Renderer TreeTrunk case). Shapes live in TreeEcology.CanopyOffsets so gen + regrow build identically. Occasional off-tone canopy for variety.
- **New TileKind.TreeRoot (54)**: underground root threaded MaxRootDepth=4 below the ground tile (TreeEcology.PlantRoots), dark woody render, drops a scrap of wood, HitsToBreak 3. Roots survive felling; grubbing ALL of them out kills the tree for good.
- **Fell-to-dust**: Game1.ToppleTree (called from OnTileBroken when a TreeTrunk breaks) collapses everything above the cut — trunk tiles → full wood dust (SpawnDustInTile), canopy → 30%-of-a-tile foliage dust via new Cells.SpawnDustFraction(tx,ty,src,frac). Canopy/root now have Tiles.Drop=("wood",1) so foliage dust is pickable. Marks the matching TreeSite Standing=false, Growth=remaining.
- **TreeSite** (Planet.Trees list, Session.Trees => Planet.Trees; NOT serialized — TreeEcology.RebuildSites re-derives sites from TreeRoot columns on ResumeRun): Angle/GroundR/Species/Height/Canopy/Growth/Moisture/Standing.
- **Regrowth** (TreeEcology.Update, ticked in Game1 after AmbientDirector): felled+rooted trees regrow trunk ring-by-ring; rate 0.018*(0.4+moisture*2.8) so watered ≈6x parched. Moisture from rain (WaterBand over the cloud's angular band) + biome baseline (ocean/verdant 0.45, wet 0.22, dry 0.08) + SoakFromPools (nearby Water cells always, Acid/Fire cells only if that's the biome rain).
- **Weather** (new Weather.cs + Session.Clouds/CloudTimer + Cloud class): <=4 drifting clouds form near the player, fade in/out, drift, and rain in showers. RainKind by biome (TreeEcology.RainFor): Water default / Acid (acid biome) / Fire (ember,slag). Rain = Particles.EmitRain streaks (blue/green/orange) + WaterBand moisture; acid/fire rain also puffs a rare Gas/Smoke cell (NOT real acid/fire — never eats soil or ignites, per brief). Clouds drawn in Game1.DrawWeather (world pass, near acid-rain cloud). AcidRain DISASTER (harsh, real acid cells) is separate and unchanged.
- SimTest.TestTreeEcology: plant→trunk+roots, fell→gone-but-roots, water→regrows, grub roots→dies. ScatterVegetation still OFF in SimTest.

## 2026-07-14 big 15-item combat/world/ecosystem batch (branch noita-sim)

- **Trees taller/varied**: species heights bumped (spire 12-21, umbrella 11-18, weeping 9-15, broad 7-12) + 1-in-6 GIANT (+8-17).

## 2026-07-14 tree-toughness / red-flash / snow follow-up

- **Trees break less easily**: Hardness(TreeTrunk) 2→4, and Planet.Mine scales trunk hardness up by tree height (`+min(4, TreeHeightAt(x,y)/6)`, so a giant trunk hits ~8) — new Planet.TreeHeightAt(x,y) scans Planet.Trees. Topple-on-cut kept (physics: flora doesn't cave, so the crown must collapse when the bole is severed).
- **Snow on ice worlds**: RainKind.Snow added; TreeEcology.RainFor "frost"→Snow (the "frost" biome IS the ice planet, def already exists). Weather.Rain emits Particles.EmitSnow (slow drifting fluffy flakes, low gravity) instead of streaks, skips the gas/smoke puff; DrawWeather snow clouds pale-white. Snow still waters trees (WaterBand).

## 2026-07-14 ore-balance / gem-embed / tree-polish follow-up

- **Trees per biome**: new WorldGen.TreeSpeciesFor(biome) weights the 4 species so each world grows its own forest (frost=spires, verdant=broad/weeping, acid/ember=bare umbrellas, crystal=spires…). Replaces flat rng.Next(4).
- **Roots smaller + spread**: TreeEcology.PlantRoots = vertical taproot (trunk column, RootsAlive/RebuildSites signature) PLUS shallow lateral flare roots (depth 2, ±1-3 cols, sparse, via new TrySetRoot). RebuildSites now requires a TAPROOT signature (shallowest root that also has a root one ring inward) so lateral roots don't seed phantom sites. Renderer TreeRoot redrawn thin+wispy w/ tendrils. NOTE PlantRoots uses Random.Shared (cosmetic non-determinism; roots are saved as tiles).
- **Tree fidelity**: Renderer TreeTrunk gets hash-keyed bark grain ridges + occasional knot + sun/shade edges; TreeCanopy gets a layered under-shadow/body/sunlit-crown + 9 dappled leaf clusters in 4 tones.

## 2026-07-14 doors / hose-rework / clouds / deep-caves batch

- **Clouds ride above terrain**: Weather.TargetAlt samples 5 bearings across the cloud's band, finds the topmost non-sky tile (peaks + giant trees), and sets the cloud ~130px + random clear above the tallest; the cloud Alt eases toward that target each tick as it drifts (rises over mountains, settles over flats).

## 2026-07-15 lush-worlds pass

- **Clouds over water fixed**: Weather.TargetAlt clamps the "tallest thing" to at least the baseline surface ring — over lakes/oceans the topmost-solid scan found the LAKE BED (water is cells, not tiles), parking clouds underwater on deep seas.
- **TileKind.LilyPad = 57**: anchored (floats, never falls) + IsFlora + passable, pad-green, authored render (bobbing pad w/ rim notch, vein sheen, 1-in-4 luminous violet blossom w/ pulsing amber anther). ScatterWaterPlants rework: seaweed rooted at frondPct (ocean 24 / other wet 12) in STACKED 1-3 kelp columns (stay under waterline via basin set), lily pads on the surface row (basin tile w/ sky above; ocean 22 / other 10).
- **More vegetation everywhere**: ScatterBiomeFlora 35%→48%; FloraFor "ocean"→Fernleaf (was none); TreePlanFor verdant 26→34, ocean 20→30, frost/crystal 12→16, acid 7→10, ember 5→7, slag 4→6, city 10→14, default 12→16.
- **Oases** (WorldGen.ScatterOases, rng seed^0x0A51, inside ScatterVegetation block): gentle worlds only (Difficulty<=0.35, not airless), 2-4 sites, half-span 6-10 tiles — a tree every 3rd tile (h 9-16) with FloraFor undergrowth carpeting every other ground tile (Fernleaf fallback so even ocean/city oases have groundcover).
- **CONCURRENT-SESSION NOTE (2026-07-15 ~01:15)**: the parallel session landed a `WorldGen.CaveStrata` lava-sea rework (Game1 lava seeding now `FillSkyTilesWithin(..., seaFloor)`; "sealed dry strata" deep cave layers) MID-FEATURE while this session tested. Suite failures at that moment — city districts span 17%, provoked-borer, rallied-guard closes, fire-breath 0 grains, "ordinary worlds stay round (verdant swings 14 rings)" — are terrain-shape regressions from THAT work, not the vegetation batch (SimTest runs ScatterVegetation=false; ecosystem/gems/acid all green). Machine also heavily contended (perf 82-89ms/tick, user playtesting). Re-check after the other session settles.

## 2026-07-15 biome-tree identity pass

- **Trees per biome look DIFFERENT now**: Renderer gets `public string TreeBiome` (Game1 sets `_renderer.TreeBiome = _run.Def.Biome` right before DrawWorld — tiles stay one TileKind, only paint differs, save-safe). New `TreeBarkFor(biome)` + `TreeLeafFor(biome, alt)` palettes: verdant warm timber + green/AUTUMN-AMBER alt; ocean driftwood + sea-green/turquoise; frost dark pine + blue needles/icy pale + SNOW cap row + icicles + rime on bark; crystal glassy stalk + amethyst/sapphire + white facet glints; acid bile wood + chartreuse/olive + toxic drip + bark weep; ember charred coal + smoulder-leaf/ash + PULSING EMBER CRACKS in trunk + glowing buds; slag scrap-post + oxidised-copper/rust + bolt bands; city grey ornamental + teal/magenta + pulsing cyan biolume nodes; default alien mauve/teal/violet. Canopy2 = the biome's SIBLING SPECIES colour (not a tint).
- **Terraria proportions**: straighter (grain 1/4 sparse tick, knots removed) + taller — spire 16-27, umbrella 14-23, weeping 12-19, broad 10-16, giant +10-21 (worst ~48). TreeEcology.RebuildSites trunk scan 30→52 to measure resumed giants.

## 2026-07-15 rain-pooling / slow-regrow pass

- **Trees regrow ~4.5× slower**: TreeEcology rate base 0.018→0.004 (several real minutes even watered), per user.

## 2026-07-15 fine-pixel-fidelity 10-pack (user: "do all those including bigger swings")

- **Falling leaves**: Weather.Update rolls dt*2.5, random standing TreeSite within 0.3rad of player, emits EmitLeaf at crown in `Renderer.TreeLeafFor(biome, canopy2)` colour — TreeLeafFor made PUBLIC for this.

## 2026-07-15 titan-siege / climbing / clouds-v2 / branches wave (RunSave v23)

- **Clouds v2** (Weather.cs + Game1.DrawWeather): `Cloud.Alt` is now an ABSOLUTE radius from planet centre, fixed at spawn (BandTopRadius + 110 + rand) — no per-frame terrain-tracking lerp, no sine bob (both caused "bouncing"). Draw = many small puffs (arcLen/11) packed along the ARC at that one radius (flat bottom, connected; deterministic per-puff wobble only). Drifting into terrain that reaches the ride band (mountain/skyscraper) sets `Dissipating` → rain stops, Life clamps to 5.5s and the existing Grow fade shreds it slowly.
- **Tree branches**: TreeEcology.StampBranches (called from BuildTrunk when canopy stamps, so regrowth earns them back at full height) — trees ≥7 tall get alternating side boughs every 2-3 rings (1-2 TreeTrunk tiles + 4-tile leaf tuft), deterministic `new Random(GroundR*92821 ^ (int)(Angle*1e5))` so regrow matches. SimTest "a tall tree grows side branches".

