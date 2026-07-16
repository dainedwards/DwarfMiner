# Worldgen: caves, strata, water, lava and volcanoes

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

2026-07-14: worldgen + spawn overhaul (user: Noita-connected caves, spawns fixed at load, spawner structures, distance-inactive creatures).

2026-07-15: **STRATIFIED UNDERGROUND + ROLLING SURFACE** (user: caves to the core, layers
deliberately NOT connected — player mines between them; Noita worldgen relief):
- `WorldGen.CaveStrata(planet, def)` is the ONE radial layout authority (seams, deep bands,
  lava-sea floor in tiles-from-centre), shared by the noise-cave pass, deep worms, AND
  Game1's flood. The lava flood is now a SEA BAND: `FillSkyTilesWithin(top, lava, floor)` —
  floor = max(R×(fill−0.14), RingMin+30). BELOW it: dry sealed strata down to the core face
  (ring 2; core shell rock is plain obsidian/basalt, nothing anchored until virtual Core).
- Seam contract is ABSOLUTE: `SealSeams` runs after all carvers and plugs any Sky inside a
  seam back to its wall material (band-clamps alone failed — worm DISKS poke ~2.5t past the
  walk point; the --strataprobe caught 100-300 holes/seam before the seal pass). Sealed =
  the sea can never drain into the strata (sealed lava sleeps, flowing lava = cell-budget
  death — the old reason deep caves were forbidden at all).
- `CarveDeepStrata`/`CarveDeepWorm`: per-band worm networks, HARD carve bands (walks drift,
  bites don't), `biteObsidian: true` (deep rock is 68% obsidian; blanket obsidian-skip would
  leave porous non-corridors) BUT detour ±0.07 rad around volcano vent bearings (throat
  linings). Ember-class (fill>0.5) worlds still skip UPPER worms but now get deep strata —
  a sealed underworld beneath the magma sea. Thick zones (>110t) split into 2 strata.
- Upper `CarveWorm` gained hardFloorPx (seam top): soft steering drift used to be harmless,
  now it must never bite the seam.
- Gas seeds gated `radTiles > seaFloor` — thousands of new deep dry tiles would otherwise
  all roll gas (wandering gas = never-sleeping cells).
- **Rolling surface**: `surfB` 64-sample channel, (sample−0.1)×7×S, ±3-4 legacy tiles —
  from an ISOLATED rng (`seed ^ 0x0511`)! First attempt drew on the shared stream and broke
  4 layout-sensitive SimTests (the exact trap this memory already warned about). Shared
  `ElevAt(a)` feeds BOTH the tile loop and SurfaceProfile. City worlds (CityLots>0, incl
  debug) damp hills ×0.3 — "civilizations grade their land" (towers/streets/QA scenarios).
- SimTest adaptations: perf test now mirrors the REAL flood (old floorless fill drowned the
  strata in lava → 75ms ticks); roundness test expects rolling (6-30 rings); rally-runway
  test GRADES a strip at the flattest bearing when nature provides none (clear sky to world
  TOP — a partial clearance left a floating mountain remnant the prey spawned onto).
- **Titan burst-up fix** (Titan.BelowLocalTerrain): wide dig craters made the ±90px flank
  column scan see open sky → boss stood in its pit forever. Now "buried" is primarily
  `SurfaceRadiusAt×TileSize − dist > 60px` (profile-based, crater-proof); columns kept for
  buried-under-mountain (profile excludes mountains).
- `--strataprobe` verifies: seams 0 holes, bands have caves, deep flood-fill NEVER escapes
  above the top seam (mining is mandatory), surface rolls. Run it after ANY carver change.

**Worm tunnels** (WorldGen.CarveWormTunnels): 20-28 perlin-worm walks, ~8-11px bite disks, meander w/ band steering, 1 branch each. Hard-won traps:
- **Lava flood line**: StartNewRun floods ALL sky below LavaFillFrac×radius (defs run 0.30-0.70!). Tunnels crossing it = permanent lava plumbing → cell sim never sleeps (ember warren went to 1 FPS, upd 30ms). Band min = max(0.50, LavaFillFrac+0.10); worlds with LavaFillFrac > 0.5 skip worms entirely (their shell is thin; warrens carry the feel — measured 3× update budget otherwise).
- **RNG stream**: worms must use `new Random(seed ^ 0x5EED)`, NOT the shared gen rng — consuming shared draws shifts every downstream stamp (geode/city/warren) and breaks the tuned SimTest worlds.
- **Order**: carve AFTER RaiseCity/CarveLizardCities (so den/district halos are populated — NearDenOrCity leaves plugs, 90px den halo + district bearing+0.03) and BEFORE LineAcidReservoirs (re-skins grazes). Never carve anchored or TileKind.Obsidian.
- SimTest borer-pursuit test needed a solid-corridor site search + "closes vs calm contrast" assertion (borers wander-dig; arrival is terrain luck in holey crust).

**Population at load** (SpawnDirector): dynamic near-player trickle spawners REMOVED — SpawnDirector.Update now ONLY ticks Session.Spawners. PopulateWorld does everything: existing city/saucer/warren/lake/herd census + NEW planet-wide cave census (55 via SpawnAt(resident:true), RollCaveSpot rejects sky/alloy/glass/brick walls) + sky flock census (rift=NullMoth — a SimTest asserts rift neutrals are null moths only). Residents freeze >900px, non-residents cull >1000px (pre-existing).

**Arrival perf** 2026-07-14 (later): the census used to run at StartNewRun on the MAIN thread — ~85 ClearSpawnSpace sites marking physics dirty planet-wide = ~5s of collapse churn on landing. PopulateWorld now runs inside BuildSessionWorld (background; CountKindsNear null-guards run.Player), physics digests 30 ticks unconditionally + alongside the cancellable settle; Session.Populated gates the old main-thread call. Orbit-view costs: lightmap RT capped at viewport size (was 2909×1636 at 0.44 zoom, ~7ms), DrawWorld orbital tileStep 2/3 below zoom 0.55/0.48 (draw 15→6.5ms), cells tick at HALF RATE while parked in orbit (dt×2 every other frame). Residual: orbit sits ~25fps on heavy worlds in Debug on this Mac — GPU-side, NOT lighting (DM_NOLIGHT same), pre-existing; surface play 60fps. **Title screen + 3 save slots** (SaveSlots.cs, GameScreen.Title): per-slot dirs slotN/{meta.json,run.sav}; MigrateLegacy MUST run before the first MetaSave.Load (a fresh meta materialising first orphans the legacy campaign — this bit us; the real 16-escape profile had to be hand-restored into slot1). SelectSlot resets Survey/previews/prefetch (planet ids collide across campaigns) + cancellable survey warm (_warmCts). **Esc = pause menu** (resume/return-to-menu/quit; true freeze), never quits directly; title Esc quits. Both menus fully MOUSE-driven too (hover moves cursor, click commits; hit rects cached at draw time, toolbelt pattern). Title backdrop = BuildTitleBackdrop + DrawTitleVista (LIVING, layered): static sky tex (gradient/stars w/ cross-flares/moon) → animated sun (150s set/rise cycle, sets BEHIND ridges, x≈70 in 640-space — keep clear of slot cards) + low-sun horizon flood → ROTATING gas giant (row-sliced scrolling cylinder map, period 320 + 108 tail cols for wrap-free windows; dithered pixel-art bands + storm oval + pole dimming; static terminator/limb/atmosphere-rim shade overlay) → static land tex (5 haze ridges + alien crust: turf/shrooms/strata/crystals/glints; NO cave circles, NO lava streaks — user removed) → live ambient critters walking _titleSurfY (3 grazers w/ leg shuffle, 1 hopper, 2 flapping moths). Title Esc → QUIT confirm dialog (Y/Enter/click-YES, N/Esc/click-NO). Entry cinematic: ship BANKS — ring squashes edge-on (p<0.5) then _stationSideTex unfolds nose-along-dive w/ shudder (p>0.5). 2026-07-14 (later): planet assets HALF-res drawn 4× (chunky, map period 160/54 tall, shade 54², ring 86×24); moon is a live sprite drifting 0.7px/s w/ arc; 26 twinkles + 2 shooting-star lanes (period 7.5/11.8s, 0.7s streak); titan shadow crosses skyline right→left every 47s for 13s (drawn before land = only head/shoulders above ridge); TWO volcano cones baked between ridge passes (_titleVolcanoA/B summits) + Erupt() slow ember/ash plumes (periods 34/47s, 11s windows). GAMEPLAY same pass: play zoom 4.0→5.6 (+40%); reticle 1.1px; pickaxe len 5.4 (+20%) & cd 0.16 (slower swing); oxygen only drains on AIRLESS worlds now (atmosphere = refill at any depth; gas choke + water breath unchanged); ocean worlds LakeMin 11+4 scale 3.2-4.2 w/ boosted OreBias (iron .03/emerald .032/gold/sapphire); block placement TERRARIA-STYLE: instant @0.14s rhythm while held (TickBuild removed from TryPlace; placeables keep held-build), needs support (adjacent solid or back wall — Player.PlacementSupported), ghost preview white/red via Player.PlacePreview.

**Atmosphere-entry cinematic** 2026-07-14: AtmosphereContact no longer holds-with-toast or blocks — it commits `_entryDef` (Game1.Space.cs): ~2.2s dive (ship eased toward planet, camera press-in + rattle, DrawAtmosphereEntry heat streaks/shield glow/callout, "launch" sfx). The world bake keeps settling in the background the whole show; the settle is only cut short if still baking at cinematic end, and EnterOrbit fires the frame the task lands — the space→planet path never blocks the main thread now. `DM_ENTRYTEST=1` commits to the nearest planet on the first space frame (headless capture; bypasses rift/vacsuit locks). SimTest borer-pursuit is best-of-3 (creature AI on Random.Shared = flaky single runs).

**Spawners** (Entities/Spawner.cs, Session.Spawners, recreated by PopulateWorld on resume — not saved): GooPile (cave floors ×10, CaveSlime cap 3 per 240px, ~22s), LizardDoor (one per warren hall, Lizardman cap 2, ~60s LOW rate), AlienHome (every 6th city address, Civilian cap 3, ~35s). Tick only when player 100-750px away; spawns are Residents. Drawn in Game1 entity pass (goo pulses + drips + green glow, brick door arch, warm-lit home doorway).

**2026-07-14 worldgen additions** (all AFTER worms, isolated rng for stream stability): **StampRichVeins** (seed^0x60D5) — 0-1 (rarely 2) concentrated gold/silver ribbons in the deep crust (55-80% radius, wandering walk, converts common rock only — skips obsidian/ore/gem/anchored) = the prospector jackpot; ambient gold now runs LEAN (PlanetGen sigBias 0.15→0.12, rare-gold bias 0.11-0.14→0.07-0.09; silver unchanged) so gold is rarer than silver except in a vein. **ScatterBiomeFlora** (seed^0xF10A) — surface plants per biome (FloraFor: verdant=Fernleaf, frost=Frostcap, ember=Emberbloom, slag=Rustbramble, acid=Vitrilily, crystal=Geobloom; ocean/city/rift/belt/moon/debug=none); ~35% per-bearing on walkable soil under open sky. **Flora tiles 39-44 are NON-anchored + passable + hazard-immune** (Tiles.IsFlora; Cells.IsCorrodible excludes them, not in IsFlammable/IsMeltable) — the ember bloom survives lava, vitriol lily survives acid. CRITICAL: flora MUST NOT be anchored — anchored flora at the surface reads as an immovable obstruction to the walking titan's body-collision (Titan.cs ~1290) and WALLS THE BOSS OUT OF ITS OWN DIG SHAFT (SimTest "titan stomps a shaft down" dropped 275→64px). Renderer authored-art cases per flora kind (swaying/pulsing). **Gems** now cut as faceted jewels (Renderer.DrawGemCrystal: Crystal = diamond rhombus, others = elongated shard w/ material BaseColor+OreSpeckle); loose Pickups draw as cut jewels w/ a travelling white shine + corner sparkle (Game1 pickup pass), physics unchanged (bounce/settle in powder). Gold/silver BaseColor brightened + live travelling glint overlay in Renderer tile pass + metal dust glitters (Cells.ColorFor Dust special-case) + brighter ore-scan light. **TNT rework** (Projectile.cs): Tnt is a TIMER bomb — dead-thud bounces off terrain (max 4, or v<400) then _resting burns the fuse in place (NOT contact-detonate); new **TntPack** kind sticks to first wall (_stuck) same fuse; both fuse-strobe faster as Life→0. **Explosion self-damage** now real: Game1 projectile-death block applies p.Damage×falloff×0.75 + knockback to the player inside blastR (own bombs hurt).

**2026-07-15 WATER WORLD (kraken + dry under-sea caves + drowning)** (user: more water,
islands, interconnected DRY caves under the sea behind hard rock, kraken, gun-humanoids
drown / some get breathers). Ocean marker everywhere = `def.LakeScale > 2.5f`.
- PlanetGen ocean: LakeMin 15+5, LakeScale 3.9-4.8, **LavaFillFrac 0.35→0.22** (deep seas
  bottom at ~43-48 legacy tiles; at 0.35 they'd dip into the lava fill on small worlds).
- Tile loop: `lakeDepth` hoisted like acidDepth → `seaShell` (obsidian, depth lakeDepth..+5,
  gate lakeDepth>1.5 keeps beaches diggable; ore/gem pass skips shell — one ore tile in the
  armour = flooding shortcut) + `seaBuffer` (+11, suppresses noise caves). Ocean skips cave
  water reservoirs (dry-caves promise). Worms can't bite obsidian → shell is worm-proof.
- **Drain traps found by --oceanprobe** (run it after ANY ocean/carver change): (1) fungal
  groves carve at 8-30 legacy below BASELINE and eat obsidian → SeedBiomePockets now takes
  `lakes` and skips wet bearings on ocean worlds (skip AFTER the rolls — stream-stable);
  (2) upper-worm ceiling is GLOBAL (baseline−14 legacy) but valleys dip ~10 → bites
  punctured shallow shore basins; ocean worms pass `localCeiling: true` = bite also gated
  on SurfaceRadiusAt−28 rings. NOTE: CarveWorm's disk-radius rng roll must STAY inside the
  guarded call (hoisting it shifts every world's stream — nearly shipped that bug).
- Ocean under-sea network: +20 worms, minFrac 0.30 (vs 0.38), 9-13 vaulted chambers (3-5
  lobed disk bites, each launches a connector worm) + 3-4 **island grottoes** (seed^0x0CEA,
  winding surface shafts on bearings ≥ l.w+0.06 clear of every lake — margin covers shaft
  drift). Network measured 99% one component; probe band must span the full bite envelope
  (0.30R−6 .. baseline−13 legacy) or it cuts corridors and reads ~35%.
- Creatures: `Kraken` (enum END; census-only via PopulateWorld — 1-2 per ocean world into
  deep basins ≥24 water cells at −30px, spaced 700px; NOT in LakeKindFor). TickKraken =
  shark-style hunt + lunge flurry (_swing) + 3-jet brine volley (TitanShotKind.Acid) at
  dry shore-taunters. `HasBreather` field: Swims||HasBreather → ImmuneTo(Water); rolled in
  SpawnDirector.SpawnAt for Marauder/Raider (50% ocean / 18% elsewhere; Pyro never — tank
  is ballast); drawn as mask pod + hip flask. **Drowning**: hazard-probe block, Breathes
  (excludes machines/void/stone/vacuum natives) && !ImmuneTo(Water) && head submerged →
  4s grace then 9 HP/s. Drown counterweight IS the obsidian shell (can't casually flood
  the caves). SimTest: drown/breather/kraken cases in TestAquatics + kraken census in
  TestPopulateWorld.

**2026-07-15 QUAKE CAVE-INS = A FEW SIZE-ROLLED CHUNKS** (user, two passes: cave-ins at
earthquakes not on break; then "don't bring down everything — singles common, ~10-groups
less common"): Physics.Settle gate — unsupported CRUST-TOUCHING regions NEVER condemn
(not from mining/blast dirty marks, not during quakes; stamped anchored per pass — the
QuakeShaking wholesale-condemn flag existed for one commit and was REMOVED). SKY regions
(severed towers, undercut mountains) still topple on the spot — the rigid set-pieces
depend on it; loose ground still sloughs. Earthquake rolls 6+4×strength cave-ROOF probes
(seed = solid non-loose CanFall tile with air below); each hit runs ShakeLooseBlob:
size roll 45% single / 27% 2-4 / 18% 5-10 / 8% 11-18 / 2% 19-32, grown as a CONNECTED
random-order BFS chunk from the seed; skips anchored + ReinforcedSupport halo (reinforced
beams quake-proof their 3×3, plain supports don't). Small chunks ride the rigid path =
tumbling boulders. AmbientDirector quake epicentre = PLAYER pos + 120-400px radius 300
(was random-bearing@0.5R — cave-ins must land where the player is). TestCaveIn: hangs on
break → quake loop (up to 40 strikes, roofs are sparse) shakes chunks → island >1/3
survives. NOTE: a quake under the ocean shell can still crack the seabed via a shaken
blob if the player tunneled right beneath it (deliberate). `dotnet run` MSBuild can
DEADLOCK against the parallel auto-committer session's builds — run the built dll
directly (`dotnet bin/Debug/net8.0/DwarfMiner.dll --simtest`, ~57s) when runs hang/empty.

**2026-07-15 LAVA-ROCK JACKET + VOLCANO DRAIN** (user: lava rock shell around every lava
body; debug volcano poured out its bottom): TileKind.LavaRock (59, drops basalt, in neither
IsMeltable nor IsFlammable) — Cells.ShellLavaBodies (BuildSessionWorld, after lava fills,
before water) converts plain terrain within Chebyshev-2 of lava tiles; quench (water+lava)
now makes LavaRock-tagged gravel that COMPACTS to LavaRock tiles; lava-volcano linings
(chamber shell + crater mix) are LavaRock, acid volcanoes keep obsidian (LavaRock is
corrodible). **The drain**: worms carve AFTER volcanoes — deep worms' ±0.07 rad vent margin
never covered the CHAMBER (10-16 tiles wide, S=2 legacy scale!) and upper CarveWorm had NO
vent awareness at all (bit basalt throat sleeves forever — the lava sea masked it; a
sea-less world drains its whole primed column to the core face). Fix = Planet.PlumbingZones
(generation-only capsules: chamber disc + throat segment) enforced in **CarveWormDisk, the
single chokepoint every worm family passes through** — never re-add per-worm-family checks.
2-tile chamber shell (1-tile Euclidean annulus had diagonal pinholes at polar ring remaps).
`--lavaprobe` audits jacket holes + drain mouths + 120s escape count; residual ≤~100
"escapes" at ring ~surface = demo-lake shoreline slosh, benign. Debug world: LavaFillFrac 0,
1 volcano, LakePair flag = water+lava demo lakes side by side. **`--lavaprobe` runs the 120s
escape sim ONLY on the "debug" world (`if (id != "debug") continue;`) — ember only gets the
static jacket audit, so a missing ember "after 120s" line is BY DESIGN, not a failure.** And
`dotnet build` defaults to DEBUG — the probe runs `bin/Release/...`, so `dotnet build -c
Release` FIRST or you validate a stale binary (bit me: stale Release just booted the game +
[ALSOFT] audio spam instead of the audit).

**2026-07-16 VOLCANO GEYSER + SHALLOW LAVA TUBE + BIGGER ERUPTIONS** (user: end the tube
below the 1st layer under the volcano; 3-tile bore not counting lavarock; tube bottom = a
"lava geyser" that occasionally pumps → eruptions/overflow; connect through the top bowl;
eruptions much bigger, spew lava chunks, leak over the rim). WorldGen.CarveVolcanoes:
- Deep near-core magma chamber → shallow **GEYSER well**: `chamberR = surfaceR − (16..23)*S −
  chamberRad` (was `surfaceR − (55..81)*S`), well radius `(3 + rng.Next(2) + scale)*S` (was
  `4 + rng.Next(3) + scale*1.5`). Sits just below the ~10-tile dirt band, in the stone crust —
  a reachable conduit, not a core reservoir. Acid still clamped above the lava flood.
- Tube = 3-tile OPEN bore lined with **LavaRock** (was ~7-wide basalt): `throatOpen=1` (dt
  −1..1 = 3 tiles), `throatLine=2` courses each side, `throatSpan = throatOpen + throatLine`;
  lining `Abs(dt) > throatOpen` → LavaRock (lava) / Obsidian (acid). Basalt→LavaRock is if
  anything MORE sealed (CarveWormDisk + ShellLavaBodies ALWAYS spare LavaRock). Throat still
  runs `chamberR+chamberRad−1 .. floorR` so it opens into the crater-bowl floor. PlumbingZones
  throat capsule radius `(throatSpan+0.5)*TileSize` unchanged in form.
- Eruption tick (Game1.cs ~1986) reworked: PULSES (`pulse = 0.5+0.5*sin(EruptionLeft*3.2)`,
  `peak = pulse>0.55`) so surges alternate ~every second. Traces the bore inward from the
  vent, **pumps dense magma from the bottom** + jets a slug up the bore on peak. Bigger spew
  (18–30 gobs peak vs 9–15, higher speed), **rim-leak** low near-horizontal gobs spilling
  over the lip, **EmitLavaChunks** (glowing scoria bombs, CollideTiles, cool to maroon) on
  peak (lava only). Duration 12–20s (was 8–14, AmbientDirector). See [fire-and-hoses](fire-and-hoses.md) and [pixel-look](pixel-look.md) for the fx.

**Round 2 (same day — user: tube not connected to bowl / geyser OBJECT / goopy jet / higher
pool / half bulb):**
- **BOWL-WEDGE TRAP**: throat stopping at `floorR` NEVER connected — the bowl floor profile
  `h = coneH − craterDepth*(1−f/craterFrac)^1.5` dips to floorR only at f=0 EXACTLY; tile-
  centre bearings give f>0 so a solid basalt wedge 2–5 rings thick sat over the bore. Fix:
  carve to `floorR + craterDepth*0.6`, above floorR only touching tiles still SOLID (bowl
  air untouched → no lining pillars in the crater).
- **TileKind.Geyser (60)**: solid node (dist ≤1.6t) at the well centre, ANCHORED (quake/
  blast/acid/worm/ShellLavaBodies/compaction ALL respect anchored or only write Sky tiles —
  only mining kills it), hardness 8, authored throbbing-core art (Renderer UsesAuthoredArt
  gate — REMEMBER to add new authored kinds there or the case never runs), ore-light glow
  case in Game1. `Game1.HandleGeyserBroken`: BFS-shatters the whole node, ONE physical
  Pickup(TileKind.Geyser) → Drop = ("lava_core",1) "LAVA CORE", finds the vent by bearing
  (<0.2 rad) and RemoveAt from VolcanoVents (persisted = permanent silence; fixes up
  EruptionVent index). OnTileBroken early-returns for Geyser (dust piles credit per-tile —
  would double-pay). 2×2 strike can report 2 geyser tiles → empty-cluster+no-vent guard.
- Bulb HALVED: `chamberRad = max(1.5*S, ((3+rng.Next(2))*0.5 + scale*0.5)*S)` — same single
  rng draw (stream stability).
- Pool BRIMS: `poolTop = coneH − 1.5*S` (was −craterDepth*0.25) → 3-ring freeboard, settle
  slosh contained, eruption pump overflows the rim fast.
- **Goopy lava fountain**: `Particles.EmitLavaFountain` = acid-spewer-style metaball
  droplets with `Fluid = Material.Lava`, inked via new `DrawFluid(Material.Lava)` call in
  Game1's HOT RT batch (with DrawHotLiquids + Fire) → fountain fuses with the crater pool,
  hot-rim composite. 1/3 stamp real Lava (LandMat).
- Eruption = 3 ACTS via `Session.EruptionTotal` (set beside EruptionLeft): RUMBLE first 2s
  (shake ramp + smoke + sparks, no lava — a warning beat), MAIN pulsed surges, TAIL last 2s
  (pulse *= EruptionLeft*0.5 dying spurts).
- **Bearing-anchored descent** everywhere (eruption pump, LavaProbe, SimTest): walk
  `t = (int)(angF * TilesAt(r))` down from the vent — InnerNeighbour hopping can drift off
  the 3-wide bore over ~60 rings. LavaProbe now prints per-vent "tube bottoms at ring N on
  <kind> (CONNECTED to geyser)". SimTest TestVolcanoes rewritten: shallow-well depth bands
  (seeds < surf−30 yes, < surf−70 NO) + open-bore-to-Geyser walk on every vent (the old
  "deep magma chamber" assert FAILED after the shallowing — update it with the geometry).
- Verified: probe CONNECTED + 0 escapes/120s, 4 total fresh seeds jacket-clean (worst
  escape=1 = demo-lake surface slosh on the lake bearing, map-verified), simtest 444 PASS.

**Round 3 (user: flamethrower-style eruption column / rest level −20% / erupting level
slowly rises to 110–130% and bubbles over the sides):**
- Resting pool = **80% of the bowl**: `poolTop = coneH − craterDepth*0.2 = 0.91·coneH`.
  The eruption tick DERIVES cone geometry back from the vent ring
  (`poolRest = ventR − surfaceR − 2S; coneH = poolRest/0.91; craterD = 0.45·coneH`) —
  vents only persist (x,y,acid), and changing that tuple = save-format surgery, so the
  0.91 constant is a CONTRACT between CarveVolcanoes and Game1 (comments both ends).
- **Rising level**: `Session.EruptionPeakFrac` (1.1–1.3/eruption, AmbientDirector; field
  default 1.2 guards unset) → `levelFrac = lerp(0.8, peak, mainP)` over the main act →
  `levelR = surfaceR + coneH − craterD*(1−frac)`. Fill = scan levelR downward at the vent
  bearing for first solid-or-liquid (Cells.LiquidKindAtWorld centre-cell sample), spawn
  Density² ×(2|4 on peak) ONE course above the surface, skip while surface ≥ line — a
  controlled climb. Past frac 1.0 the line is above the rim so lava can't stand: the fill
  feeds a continuous bubble-over down the flanks (that's the feature, volume bounded by
  duration).
- **EmitEruptionJet** = flamethrower at cone scale: Fluid=Fire puffs — scaled via the
  dedicated **Particle.JetScale field (NOT Size!)**: DrawFluid multiplies wid/len-floor by
  JetScale>1 in both generic and fire branches; handheld hoses never set it so their
  metaball path is bit-identical (user explicitly demanded decoupling from flamethrower;
  Size also drives the strand-quad fallback, so overloading it was wrong twice). Speeds
  170–330, lives 0.8–1.3s, buoyant, hero-flicker 1/3 up the column every 3rd frame.
  Lava-only (acid vents get no burning column).
- **Round 3b** (user: spout from deeper in bowl / decouple from flamethrower / 2 angled
  acid-spitter-style side jets): **EmitLavaSpew** = acid-spewer mechanics scaled up
  (JetScale 2.2–3, Fluid=Lava hot composite, LandMat: ½ Lava + ⅓-of-rest Fire w/ fuse =
  ignites the slopes). Rim-leak gobs stay at the vent (they ARE the rim).
- **Round 3c** (user: spout 10 blocks BELOW the lava line / bigger / side spouts same
  origin at 45° arcing over the ledge): `spoutR = max(2, surfR − 10)` — the whole spout
  family (column/fountain/gobs/chunks/side spews) launches SUBMERGED and bursts through
  the surface. KEY: the particle liquid-crossing gate only kills on ENTRY
  (`!LiquidAtWorld(p.Position)` guard) — particles BORN inside a pool fly out unharmed
  and die when they arc back in, so submerged emitters just work. Side spouts: same
  spoutPos origin, ±0.785 rad, on all main act (`0.35 + 0.65·pulse`, +0.2 past crest);
  EmitLavaSpew GravityScale 0.9 (NOT HoseArcGravity 2.25 — must clear the ledge at 45°
  from below the surface), jetSpeed 210+130. Bigger: jet count 6+9/speed 220+220/JetScale
  2.8–4.4, fountain 4+9/140+180/JetScale 2.2, gobs 22+14 @ 280+130, chunks 150+190.
  simtest 444 PASS (round 3b+3c).
- **Round 3d retune** (user: 20° / actual lava full trajectory / reach −70%): side spouts
  ±0.349 rad; EmitLavaSpew jetSpeed 210+130 → **115+72 (×0.55 — ballistic reach goes with
  v², so ×0.3)**; **EVERY droplet stamps Lava** (`LandMat = CellFx ? Lava : 0`, the ½-Lava
  + ⅓-Fire mix and LandFuse removed) and **Life 1.6–2.2 must OUTLAST the lob** — a droplet
  expiring mid-air never stamps, which read as "the lava disappeared in flight". Look
  (tones/JetScale/Fluid=Lava metaball rope) unchanged. NOTE: the MEMORY.md index briefly
  described this retune as already-merged from the parallel `carve-everywhere` session
  BEFORE this branch actually had it — the code still said ±45°; always grep the working
  tree before trusting a cross-session memory line.
- **Rounds 3e-3g FINAL side-spout lifecycle** (user iterated 3×: actual lava w/ spitter
  look → pure cells only → blobs that pool then convert): the shipping design is
  **EmitLavaSpew blobs that ARE the lava carriers** — Life 2.4-3.0 outlasts the whole lob,
  EVERY droplet has LightRadius>0 (hot 22 / rest 8) which routes it through the particle
  rest-rule's lit-debris clamp = it POOLS ~1.4s as a cooling molten blob on touchdown, then
  the rest-handoff stamps ONE real Lava cell exactly where it pooled. **LandFuse MUST stay
  0** — LandFuse>0 + LandMat routes to the fire-blob FIRST-TOUCH path (StampFireBlob), not
  the lava rest-stamp. No separate mid-air cell volley (tried in 3e/3f: flying cells draw
  as streak grains NOT the hot metaball composite — _hotOps is grid-scan-only — so pure
  cells lost the goopy read; briefly hybrid, then blobs-only won). Ballistics note for
  future emitters: FlyGravity = GravityCells·PxPerCell = **450 px/s²** vs particle
  200·GravityScale — matching a particle arc needs cell speed ×√(450/(200·gs)).
- **Round 3h** (user: varied scatter / higher peak / drain after / solid orange): spew
  blobs now ONE FLAT COLOUR = Cells.LiquidBody(Lava) (235,92,20), Color==FadeColor — the
  hot composite's fill blend REPLACES colour, so any non-body tone stamps hard edges where
  blobs cross the pool (this was the "different texture" read). Scatter = coneArc 0.09→
  0.24 + speed spread 0.7–1.25× (range ∝ v²) + per-frame aim wander in Game1 (sin sweep
  ±0.12 + jitter ±0.05 per side). Peak 1.2–1.45 (was 1.1–1.3). **POST-ERUPTION SUBSIDENCE**:
  new `Cells.DrainLiquidInTile(tx,ty,mat,max)` (mirror of the gas-dissipate removal: _mat=0
  + ClearKinetics + WakeNeighbors); eruption end hands EruptionVent → Session.EruptionDrainVent;
  each frame the drain finds the pool surface at the vent bearing (stop on solid = crusted
  over) and skims 3 tiles' worth off the top row across ±0.05 rad until the surface is back
  at the resting line (restR = ventR − 2S, the geometry contract again). Same-vent
  re-eruption cancels its drain; HandleGeyserBroken fixes up the drain index on RemoveAt. **Round 3i**: Session.EruptionDrainWait = 4.5s pause
  before subsidence (airborne gobs settle first) + budget 3→1 tile/frame (slow, stately);
  EmitLavaFountain also flat LiquidBody orange (the eruption JET keeps its fire colours +
  soot tail — that's the Fire-fluid branch, untouched); rumble-phase EmitLavaSparks REMOVED
  (vent ring floats above the resting pool → mid-air flecks read as a glitch). **DM_ERUPTSHOW=1** = one-stop eruption demo: volcano-flank spawn + max zoom-out +
  auto-eruption at 5s. TWO TRAPS fixed 2026-07-16: (1) the rig's `NoDisasters: true`
  gated the WHOLE disaster clock — forced disasters (run.NextDisaster from DM_ hooks) now
  override it for one firing; (2) zoom must be set in LoadContent's camera default
  (rides DM_ZOOM), NOT in StartNewRun — LoadContent runs after and `_playZoom =
  _camera.Zoom` clobbers anything set earlier. `[erupt]` console breadcrumbs (schedule +
  start) make headless verification greppable; verify via DM_NOFOCUS=1 + DM_AUTOSHOT=<s>
  invisible runs (macOS has NO `timeout` — background + until-loop + kill, and wait for
  the png to finish writing or the kill truncates it). **Round 3j**: omnidirectional
  EmitCinders burst at the vent REMOVED (Jitter(140) scatter overwhelmed the 90 up-vel =
  ball of sparks flying every way); spout ANCHORED via Session.EruptionSpoutR (reset
  int.MaxValue at eruption start, `min(prev, surfR−10)` clamp ≥ gx+1 — never climbs with
  the rising pool, only ratchets DOWN if the column drains); surface scan now runs
  levelR→gx (the bulb) and finding NOTHING = drained past the bulb → eruption deactivates
  entirely (EruptionLeft=0, EruptionVent=-1, no subsidence handoff). **Round 3k (rig
  guarantees)**: debug def AcidPools 2→1 (exactly one basin of each: water + trio-lava +
  trio-acid; the 2nd pool was a stray acid pond) — NOTE AcidPools changes the shared rng
  stream downstream; CarveVolcanoes gained a DETERMINISTIC fallback sweep (128 bearings,
  max clearance from spawn/avoid/placed, mountains fair game, NO rng draws) when the 80
  random tries fail — the shrunken rig's crowded circumference sometimes spawned NO
  volcano; only refuses if best clearance < 0 (cone would bury a basin/spawn). SimTest:
  3-seed "QA rig always places its volcano" assertions (447 tests now). **Round 3l
  tuning** (user): side spouts ±10° (0.175), spew blob Life 3.2-4.0; **JetScale-scaled
  rest-stamp dollop** (Update rest-handoff: JetScale>1 carriers stamp +JetScale·2 extra
  cells jittered 2.5px — hoses JetScale 0 unchanged); fountain = 10% LANDER roll (Life
  2.6-3.4 + LandMat+LandSparks, other 90% pure body, old ⅓-stamp gone); eruption jet shed
  sparks: ¼ become falling EMBERS when parent JetScale>1 (gravity 1.0, CollideTiles,
  LandSparks, Life 1.8-3.2 — rest-rule kills JetSpark 0.1s on touch; flamethrower's
  no-ground-contact contract untouched); jet nozzle 0.22→0.286 (+30%); drain wait 4s +
  budget Density²/4 per frame (much slower). **Round 3m (landers vanishing + zoom-2
  render degradation)**: rest-rule clamp gains `JetScale>1 → 2.5s` pooled linger (unlit
  landers were fading in 0.15s ON TOUCH = "disappear right before landing"), landers
  always glow; **ZOOM TRAP: liquid metaball pass needs cellPx = Zoom·TileSize/Density ≥
  1.6 → Zoom ≥ 3.2** — below that cellStride>1 kills liquidPass and every Fluid blob
  degrades to flat strand quads + per-cell lava flicker ("blocky textures + flashing");
  DM_ERUPTSHOW zoom 2→3.2. Blob POP → alpha-only melt (coverage ramps out over last 0.4s;
  rgb must stay body colour — fill blend REPLACES), fire boil flicker ±25%→±10% for
  JetScale grains, hero flash tightened 52-60px. **Round 3n — the REAL lander killer**: not
  the rest clamp but the LIQUID-CROSSING GATE — an erupting flank is coated in fresh lava,
  and the gate's "everything dies at the surface" rule ate every lander at that coating
  before CollideTiles could ever rest it (lava cells live in Sky tiles → never IsSolidAt).
  Carve-out in the gate: `pool==Lava && Fluid==Lava && JetScale>1` → LAND on the lava
  (stop at surface, dollop stamp, 3-spark splash, 2.5s pooled linger, `continue` — safe,
  the update loop decrements in its header). Applies to spew blobs landing on their own
  coating too. Fountain lander Life 3.2-4.0 so the tallest lobs can't expire mid-air.
- SimTest note: "compaction: voided pile hardens" is time-seeded FLAKY (failed once,
  passed clean re-run with identical binaries — Cells sim rng, like the acid-dissolve
  test). Verified: probe 0 drain mouths + CONNECTED + 0 escapes, simtest 444 PASS.

**2026-07-14 (later) explosives + building rework**: Projectile Update's timed-explosive block REWRITTEN — Dynamite/DynamitePack/Tnt/TntPack share one `timed` path: gravity + fuse tick + damped bounce (`Velocity = vt*0.6 + n*|vn|*0.42`) on terrain contact, NEVER contact-detonate; TntPack sticks (_stuck) instead. `_resting` removed (bounces until fuse now). Fuses all 3.0s. New **DynamitePack** kind = 3× dynamite blast (ExplosionRadius 150, CraterTiles 12); item dynamite_pack (Weapon, FireDynamitePack). Building blocks (Brick/Plating/GlassBlock/Platform, TileKinds 45/46/47/49) — see [equipment-and-ui](equipment-and-ui.md) 6th pass for the Platform one-way collision detail. Throw charge-gauge on reticle (ThrowSpeed(min,max) read by all Fire*/ThrowTorch).

2026-07-15 LAVA-BARRIER SAGA (user: tunnels through lavarock breaking lava barriers) — the leak was NEVER one bug; five stacked mechanisms, each probe-caught on `--lavaprobe` (now dumps seam bands + tile-kind maps around the first drain mouth AND the top of the escape chain — keep those dumps, they solved every round). **BuildSessionWorld seeds from DateTime.Now.Ticks — every run a fresh world, so breaches are probabilistic: validate with 8-12 probe loops, never one.**
1. Carvers had no fluid awareness → `Planet.FluidKeepOut` (lava+acid seed halo-2, built in Generate after CarveVolcanoes) checked by CarveWormDisk + CarveTunnel; CarveWormDisk now ALWAYS spares LavaRock, CarveTunnel spares LavaRock+Obsidian. Water deliberately excluded (ocean under-sea worm net runs against the seabed shell by design).
2. Noise caves carve in the SAME tile pass that records lake seeds (no ordering) → lava lake now gets the `lavaBuffer` solid cave-free band (acid always had acidBuffer; water lakes deliberately keep caves — flooded grotto is gameplay).
3. Settle slosh crests ONE COURSE over the fill line → lava lake fill gets freeboard (seed depth≥2, water stays ≥1) + the basin's carved-unseeded LID courses recorded (lavaLid/acidLid) and PlugFluidBreaches walls the LID's edges too.
4. Sky-only plugs weren't barriers: the crest MELTED through grass/sand/dirt shorelines → plug halos convert ShellLavaBodies' whole Convertible list to LavaRock/Obsidian, not just Sky.
5. PlugFluidBreaches = the absolute contract (SealSeams spirit): 2-tile halo at dr∈[-2..0] around every lava/acid seed AND lid tile, exempting the fill+lid; outward rows stay open (fluid never climbs; crater mouths/lake surfaces). Runs LAST in Generate.
Result: escaped-tile census 400+→0-13 across 12 probe worlds; residual "escapes" are in-basin surface redistribution (map-verified contained by the rim), not drains. Titan burst-up test needed its climb window 20s→30s (denser overburden on its fixed seed 70 — behaviour intact at 101px, threshold 120).
FLORA/FAUNA-IN-LAKES same session: flora scatters walk to topmost SOLID = lake BED (Dirt/Basalt pass the soil lists!) → ScatterBiomeFlora/ScatterTrees/ScatterOases skip when tile-above-ground ∈ FluidFillSet (water+lava+acid seeds); sky fauna spawned at bed+50-140px = INSIDE deep seas → altitude clamped to baseline surface ring + HazardRejectsSpawn veto (the one census path that lacked it). New SimTest `TestFluidContainment` (verdant+debug, re-enables ScatterVegetation locally — the suite runs veg OFF, debug is NoFauna: gate fauna assertions on !Def.NoFauna or they fail on "0 land").

2026-07-16: **QA-RIG LAKE TRIO + SHRINK** (user: godmode fly 2x, debug world 30% smaller,
acid/lava/water lakes adjacent + 3x bigger):
- `LakePair` → **`LakeTrio`** (PlanetDef): water, **lava (MIDDLE seat)**, acid in a row, so
  lava borders BOTH others. The acid pool is conscripted from the SEPARATE `acidPools` array
  (kind is NOT an enum — water-vs-lava is `def.LakeTrio && lakeIdx == 1`, acid is its own
  array + own carve branch + own `acidBuffer`). Trio block therefore had to MOVE below the
  acidPools loop (needs acid `w` for its gap), but must stay ABOVE the `blocked` list build
  (~line 653) so volcano/city/warren siting routes around the scaled widths.
- Spacing formula `centre-to-centre = 1.3 * (wA + wB)` leaves a 0.3-strip of land. **Scale the
  basins BEFORE measuring gaps** → strips stay proportional, row grows without merging.
- **TRAP: `Planet.RingsFor` had a `Math.Max(240, ...)` floor that SILENTLY swallows any
  SizeScale below 0.6.** Setting DebugWorld 0.7→0.49 alone does nothing. Lowered floor to 190;
  inert for campaign (PlanetGen min is `J(0.65,0.75)`, moon `Min(host, 0.8)`).
- The "0.7 is the floor, 0.55 leaked" comment was **over-general**: that failure needs a lava
  SEA. Rig has `LavaFillFrac: 0f` → no sea → class can't fire. Verified: rings 280→196
  (exactly −30%), radius 320→236t, seam 81.7–89.7t, lake bottoms ~134t = far outside seams.
- A/B'd 6× `--lavaprobe` each (time-seeded!): escapes baseline 0/0/0/2/4/14 vs changed
  0/0/1/1/3/18 — SAME slosh regime, lava tiles stable over 120s = no drain. Debug load
  463ms→211ms (shrink outpays 3x lakes). 444 simtests pass.
- **AUTO-COMMITTER TRAP (worse than noted before): it commits DURING the session, so
  `git status` is CLEAN and HEAD already contains your own edits.** A worktree at HEAD is NOT
  a baseline. Find the true pre-session commit by walking `git log` back until the blob shows
  the old value (`git show <c>:<path> | grep ...`); the conversation-start gitStatus snapshot
  names it. `git log -S` finds the commit that CHANGED a line, i.e. one that already has your
  edit — use its parent, and verify the blob.

2026-07-16 **QUARTIC LAKE BOWLS** (user: shores meeting water should be slopes/bowls "like
a real hill or lakebed"; on worktree `carve-everywhere`, UNMERGED): the lake/acid basin
profile changed from parabola `(1-f²)·depth` (steepest exactly AT the rim = hard tile-step
shores) to quartic `(1-f²)²·depth` — same centre depth, zero slope at the rim → shelving
beaches above and below the waterline. Every downstream consumer (waterline course, ocean
seabed shell/buffer) reads the hoisted `lakeDepth`, so the whole armour follows the shape
automatically. Plus a GRADE CAP at placement: quartic max mid-slope ≈1.54·depth/halfWidth,
so deep-narrow rolls widen w until the bed stays under ~40° (`1.8·depth·8px/surfPx`) —
without it small worlds (debug rig) carved funnels, since w scales with planet radius while
depth is rolled in legacy units. Ocean seas (LakeScale > 2.5) are EXEMPT from the cap: the
plunge to deep water is the designed dive and capping would flood the surface. Craters keep
the old parabola on purpose (impact scars want crisp rims). No extra rng draws — layout and
the layout-sensitive SimTest scenarios stay bit-identical (PASS).

2026-07-16: **WATER-BASIN CONTAINMENT** (user: "fix the lakes in the debug planet, the tunnel
carving is cutting off the bottom … tunnels never carve into lake bodies or empty out lava
pockets"). Water was never a first-class fluid in the containment passes — lava/acid had the
keep-out + buffer + plug trio, water had none ("water lakes deliberately keep their caves"
was the old, now-reversed stance). Five parts, all in WorldGen:
- **`Planet.Radius` COUNTS THE SKY** (`RingMin + Rings`, and Rings carries a FIXED 142-ring
  `SkyHeadroom`). So every `radius * frac` is meaningless on small worlds: on the 0.49 rig the
  sky is 2/3 of the radius, so CaveStrata's `radius*0.38` deep-strata ceiling — and its 8-tile
  seam — sat FOUR tiles under the grass. Lake bowls were carved through the seam, `SealSeams`
  honoured its absolute contract and slabbed them back = literally "the bottom cut off". Fixed
  by clamping deepCeil to `RingMin + SurfaceRing*0.55` for LavaFillFrac==0 worlds only (sea
  worlds' ceiling is the sea floor, which Game1's flood shares). Suspect this class anywhere
  `planet.Radius * frac` appears (CarveWormTunnels minFrac still does).
- Basin depth CAP (after CaveStrata, before the tile pass): depths are rolled in ABSOLUTE
  legacy tiles, but the rig's whole crust is ~27 — the 3× trio bowls rolled 15-29 and punched
  through the world. Cap = min(55% of crust, 3t clear of the topmost seam). Clamp after the
  roll ⇒ rng stream untouched.
- `Planet.LakeBasinSeeds` (gen-only) = the surface-basin subset of WaterSeeds; reservoirs need
  DIFFERENT treatment (a reservoir IS its cave — plugging its neighbours deforms the pocket).
- FluidKeepOut now halos Water+Oil too; PlugFluidBreaches gained a water pass (`melts: false`
  = Sky-only, plugs with the tile's stored WALL kind). waterBuffer mirrors lavaBuffer.
- **OCEAN SEAS ARE EXEMPT from both** (`LakeScale > 2.5`): their contract is the obsidian
  seabed shell + buffer, their WaterSeeds are 70%+ of the bearings, and haloing/plugging them
  walls up the island grottoes (`--oceanprobe` grotto check catches it instantly).
- `--lakeprobe` (new): drain mouths at load + basin fill retained after 60s + a bowl
  cross-section. Traps it taught: (a) whole-world water census can't tell a lake draining from
  a RESERVOIR settling across its own cave floor — judge basins by bearing+ring; (b) rim air
  at the waterline is the bowl's own lid, not a mouth (only count air that LEADS DOWN).
- **`BasinDepthAt` returns 0 off-basin** — `top < d + 14` then condemns every shallow grove on
  the planet. Gate on `d > 0.5` (the tile pass's own gate).
- SeedBiomePockets: the ocean-only wet-skip sat ABOVE the radius roll, so applying it to all
  worlds SHIFTED THE SHARED RNG STREAM and silently re-rolled every downstream carver (that,
  not the pockets, is what broke DefenseTest). Roll everything first, then decide; and WALK
  the pocket to a dry bearing (golden angle) rather than delete it — verdant has 5 pockets
  total and a grove is a destination.
- SimTest `FindCorridorSite` was a knife-edge fixture: 60 candidates, best site EXACTLY at the
  0.8-solid bar on verdant/77 — any cave-layout shift failed the whole DefenseTest. Widened to
  400 candidates (raise the search, not lower the bar).

2026-07-16 (same session, follow-up): **UPPER WORM BAND un-degenerated** — the second half of
the `planet.Radius` bug above. `WorldGen.WormBand(planet, def, minFrac, seams)` now owns the
band's radial envelope:
- FLOOR: was `planet.Radius * minFrac`. For a NO-SEA world it's now `seams[0].hi` = CaveStrata's
  deepCeil, which that function ALREADY documents as "the upper worm network's floor" — so the
  two agree by construction and the floor inherits the crust clamp. Sea worlds keep
  `Radius * minFrac` (their floor is a margin above the FLOOD line, and Game1 floods by a
  fraction of the same sky-inclusive radius — that pairing is correct).
- CEILING: was a flat `16 legacy tiles` under the surface = 12% of a standard crust but 59% of
  the rig's 27-tile one. Now `min(16, crust*0.35)`; the drift ceiling keeps its +2 relationship.
- On the rig the two bugs together meant floor(89.7) > ceiling(62): EVERY step steered inward,
  the "upper network" collapsed onto the seam, and SealSeams then plugged what it carved — the
  rig had almost no cave system at all. It now has a real band and reads like a cave world again.
- `--geomprobe` (new): FNV tile hash per fixed-seed world + air-by-depth histogram. A/B a
  worldgen refactor with it — **only `debug` moved**, every campaign world bit-identical.
  Two traps it taught: (1) **WorldGen.Generate is NOT reproducible across processes** —
  `TreeEcology.Plant` draws branches from `Random.Shared`, so the probe sets
  `WorldGen.ScatterVegetation = false` (real gen bug, still unfixed, above ground only);
  (2) measure depth from the LOCAL surface (`SurfaceRadiusAt`), never the baseline radius, or
  mountains/valleys/lake bowls read as giant caves and a hollow crust looks like a lumpy one.
- Worm COUNT (30-41) and walk LENGTH (260-560 steps) are still absolute — on the rig one walk
  ≈ the whole circumference, so its band runs ~78% air. Left alone deliberately: scaling them
  by crust area would move every non-1.0-size campaign world, which is beyond a bug fix. The
  rig's shallow band being cavernous is cosmetic; the lakes above it stay sealed (keep-out +
  plug, `--lakeprobe` green over 6 time-seeded worlds).
- `--lakeprobe` verdict = drain mouths at load + fill retained ONLY. "Water under a basin
  bearing" is a HINT: a crust reservoir settling across its own cave floor lands under a lake
  bearing often enough to false-FAIL an otherwise 95/95-retained lake.



## Moved from the old noita-sim note (2026-07-16)

### 2026-07-14 zoom / eruption / lizard / flame / gem-embed follow-up

- **Debug/scheduled eruption picks NEAREST volcano** to the player (AmbientDirector.TryBegin Eruption loops VolcanoVents for min-dist), not a random vent. Duration bumped 5-9s → 8-14s.
- **Eruptions MUCH bigger** (were a 12-cell trickle): Game1 eruption tick now wells up a fat magma surge (3× SpawnInTile at Density² each = ~48 cells/frame that overflow the crater) AND SPEWS a fountain — 9-16 gobs/frame LaunchAtWorld'd up+out of the vent mouth (spread ±0.5rad, speed 150-210, arc over and rain lava down the flanks; note FlyMaxOutward=170 caps fountain height, that's fine) + smoke plume + EmitCinders + shake 0.4 + a low "collapse" rumble (minGap 0.5).

### 2026-07-14 doors / hose-rework / clouds / deep-caves batch

- **Cave networks reach the lower depths**: CarveWormTunnels minFrac 0.50→max(0.38, LavaFillFrac+0.08) — worms thread the lower crust now, still a safe margin above the lava fill line.
- **SimTest**: delver site now also requires solid ground BELOW (the deeper caves left open space under some sites → delver fell instead of digging).

