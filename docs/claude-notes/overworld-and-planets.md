# Overworld: planets, cities, roadmap

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

The agreed roadmap is in `PLAN.md` at the repo root (written 2026-07-07). Done: star-map
overworld, PlanetDef archetypes, Session extraction, staged spaceship escape, ItemDef
registry, god-mode opt-in, the Game1 split (UI/CraftingMenu, UI/InventoryUi,
Systems/SpawnDirector), in-run save/load (Systems/RunSave: F5/quit suspends, R on the star
map resumes, run endings void the save), the oxygen/depth-pressure meter
(Systems/OxygenRules + Player.Oxygen; air_tank recipe doubles the ceiling; AIR HUD bar;
per-planet OxygenDrainScale), and hazard cells (Material.Acid + Material.Gas in the cell
sim; Cells.SampleHazardsNear → Game1.TickHazardContact applies lava/acid burn + gas choke;
worldgen seeds per-planet via PlanetDef.SeedsGas/SeedsAcid), and boss variants + egg spawn
(Titan hatches from a giant egg: 10-min EggTimer or beat EggHealth to hatch early; four
TitanKind variants per-planet — Godzilla fire-breath, Mecha mouth-laser, Hydra burrow,
Kong leap-quake; TitanProjectile enemy shots; Titan.PendingShockwave for melee AoE;
DM_HATCH=<s> test hook). Then a boss overhaul (2026-07-07): each variant has its OWN
procedural skeleton in Rendering/TitanRenderer.cs (upright Godzilla, boxy Mecha robot,
Dune sandworm "Shai-Hulud" = segmented tube + round toothed maw that burrows & breaches
vertically [TitanKind.Sandworm, was Hydra — renamed 2026-07-08], big-armed Kong) — not a
re-tint; bipeds have 2 legs, sandworm 0 (verlet body); bosses plow through mountains + cave
in; Mecha laser charges then drills. Then the surface storage depot (2026-07-08):
craft a Storage Depot, B deposits raw mats / N withdraws, stash persists per-planet in
MetaSave.Bank across death (cleared on escape); Tiles.IsBankable gates it; Session.DepotPos +
RunSave v5. Then sound (2026-07-08): fully procedural synth in Systems/Sfx.cs (no asset
files — swept osc + filtered noise + envelopes → PCM → MonoGame SoundEffect); 10 effects
wired to events via Game1.PlayAt (camera-matrix pan + distance falloff), fail-safe behind
_ok/try-catch so no-audio-device = silent, DM_MUTE=1 silences, Synth() is static so it's
headless-tested. Then ambient events (2026-07-08): Systems/AmbientDirector +
Entities/Meteor — meteor strikes (telegraphed, crater + expose fuel/gold ore core, blast
the dwarf; cadence scales with OxygenDrainScale) and magma surges on lava-rich worlds
(flood a nearby cave). Session.Meteors, DM_METEOR=<s> hook. Then per-weapon shot sounds (2026-07-08): each
gun/thrown weapon declares its own synth voice via ItemDef.ShotSound (pistol crack, MG
rattle, laser zap, heavy beam, rocket whoosh, cannon boom, throw toss, harpoon twang;
nuke reuses rocket); the one central fire-sound hook in Update resolves it (falls back to
"shoot"), so no Fire* method changed; 8 new Sfx synths auto-covered by TestSfx's Names
loop. Then cave-in warnings (2026-07-08): Physics.NewlyCondemnedThisTick counts tiles that
just entered the tremble window → Game1.UpdateCaveInWarning sounds a "creak" synth (lead
time before the "collapse" boom) and, for condemned tiles hanging OVER the dwarf (larger
polar ring index), sifts dust + flashes a "! CAVE-IN !" HUD banner; TestCaveIn builds an
isolated unsupported stone island and asserts condemn→tremble→crumble. Next agreed steps:
settings/volume UI, or new content (biomes/creatures/weapons).
NOTE: the perf test ("steady-state cell tick under 6 ms") is a wall-clock micro-benchmark
that spikes to 7ms+ under IDE/background CPU load — re-run a few times before treating a
perf FAIL as a real regression; it's ~3.5ms when the machine is quiet.
bosses PLOW through mountains (Titan.Plow mines overlapping tiles) + footfalls cave-in;
Mecha laser charges then drills; god mode (FlyMode) is now damage-immune. Test hooks:
DM_BOSSCAM (egg by spawn + boss-follow cam), DM_ZOOM=<f>. Next agreed steps: sound (still
zero audio), surface base/storage depot, ambient events. The user actively plays between sessions (4 escapes / 5 planets
unlocked by 2026-07-07) and gives concrete design direction — treat `meta.json` and
`run.sav` as real player data, back up + restore around any test that writes them.

Mothership era (2026-07-08, PLAN.md §0 is the authoritative design): a "run" is conquering
the whole system then warping to a super-hazardous warp world (needs a core shard from near
each planet's center). Flyable solar system replaced the star map — `GameScreen.Space`,
model `src/Space/SpaceSim.cs` (pure logic, SimTest-covered), screen `src/Game1.Space.cs`,
foundry catalogue `src/Space/Upgrades.cs`. Done through phase 2: mothership flight +
asteroids + autocannon + hull/emergency dock; titan kill banks a soul (MetaSave.TitanSouls)
instead of ending the visit; rocket-return docks cargo/fuel into MetaSave.ShipCargo/
MotherFuel; foundry (U): jetpack / autocannon II / ion engines II. Phase 3 done same day:
fuel burn replaced the unlock chain (dry tank = 35% reserve power, never soft-locked),
consumable rovers (roverless landing = drop pod at half health), kind-specific soul costs,
hull plating / O2 recycler / drill rig, M-key system survey (`src/Space/Survey.cs`, cached
fixed-seed worldgen census). Phase 4 done 2026-07-09 + riders: dock refinery smelts raw
metals 4:1 into `pure_*` ingots (foundry bills pure), semi-controlled lander descent
(A/D steer, DM_DESCEND hook), core drill yields CORE SHARDS (MetaSave.CoreShards, 1/world),
5 shards → J warps to THE RIFT (`rift` PlanetDef, warp-locked hellworld); escape it with
its titan slain = campaign complete (MetaSave.RunsCompleted). Phase 5 done 2026-07-09:
mothership position/heading/hull persist (ShipStateSaved flag — NEVER use float.NaN in
MetaSave, System.Text.Json throws on it and Save() swallows the error silently), thrust
rumble + rock-shatter + hull-thud sfx, F6 volume cycle (MetaSave.VolumeStep), multi-line
game-over overlay w/ victory summary, new-run+ = souls/upgrades/ship endure + core shards
burn. Phase 6 done 2026-07-09: foundry depth — jetpack II / autocannon III / ion engines
III (UpgradeDef.Requires tier-gating), deflector shield (one hit, 8s recharge, cyan halo),
ore magnet, pod dampeners, rover armory (pistol+90 rounds per drop); foundry menu scrolls
(8 rows + MORE cues). Phase 7 done 2026-07-09: mothership = circular ring station
(procedural texture) with a real orbit position in the planet view (Session.StationPos,
480px up); rover departs from it, escape = manual rocket ascent + auto-glide dock
(_ascending state); BuildSessionWorld extracted static + prefetched on a background thread
while loitering near a planet (seamless landings); white _transitionFlash masks the
coordinate swap; system view got nebulae/3-layer stars/sun rays/planet blotches +
atmosphere. Phase 8 done 2026-07-09 (NMS-style): no landing prompt — flying into a planet's
atmosphere IS the transition (SpaceSim.AtmosphereContact; planets not solid in space, only
locked Rift storm wall); approach zoom near surfaces; parking-orbit state (_orbiting: A/D
shifts orbit to pick drop site, SPACE launches rover, W breaks orbit; quitting from orbit
discards, no RunSave); planet discs rasterized from cached survey worlds
(Survey.WorldFor/TryWorld thread-safe + BuildPlanetPreview — real mountain silhouettes).
DM_ORBIT=<planet-id> boots into orbit. 8b riders: side-profile mothership in planet view
(ring stays top-down in space), entry arrives at the flown-in bearing + auto-glides down
(Session.OrbitEntryOffset), orbit controls LEFT/RIGHT + ENTER lander + SPACE leave,
prefetch at 2200px + preview builds throttled 1/frame. 8c riders: altitude-driven descent
zoom, touchdown crater + rover wreckage (Session.RoverWreck), orbit raised to 700px +
station drifts (Session.StationDriftRate — moving rendezvous), SHIP edge-arrow in planet
view, solar-map planet arrows w/ names + ranges (KM = px/10); SHIP chevron small/wide at
margin 14; slow liftoff (65 px/s², thrust carries into ascent); Renderer.DrawWorld
low-detail mode below zoom 0.9 (flat quad per tile + skip rings < 60); the REAL orbit hog
was Cells.Draw/AddLights full-grid scans — both take stride LOD now (3 at zoom<0.9, 6 at
<0.55). FPS overlay (FPS + UPD/DRW ms) always on, top-right; Update/Draw wrapped by timing
shims (UpdateFrame/DrawFrame). Phase 9 done 2026-07-09: foundry wishlist complete — GEO
SCANNER (Systems/Scanner.FindNearest ring-band sweep + DrawEdgeArrow fixes to fuel/
signature ore/titan w/ tile ranges), COMBAT PLATING (0.7x dmg, stacks w/ armor), SUPPLY
CACHE (poultices/blocks/sentry per rover). Phase 11 done 2026-07-09 (content): EMERALD +
VOIDSTONE gems (voidstone = rift-only endgame), crystal cavern + fungal grove worldgen
pockets (WorldGen.SeedBiomePockets, per-def counts), SOLAR FLARE + BLIZZARD disasters
(AmbientDirector warn→strike, surface exposure damage, DM_FLARE hook), EMERALD WEAVE (140
HP) + VOIDSTONE REACTOR (free thrust) upgrades, held-weapon sprites in the dwarf's grip
(_weaponTex, incl. user's new mining_laser). Phase 12 done 2026-07-09: biome fauna —
SPORE BAT (gas puff on death), CRYSTAL CRAWLER (deep tank, crystal drops), VOID WRAITH
(rift, drops 1 voidstone = renewable endgame gem); spawn-table biome override roll;
DM_FAUNA=1 parade hook. Bestiary wave 2 done 2026-07-09 (Noita/Terraria school): CAVE SLIME
(hostile hopper, splits into 2 SLIMELETs on death — split lives in Game1's death handler),
ACID SPITTER (holds range, lobs TitanShotKind.Acid globs at 8 dmg into run.TitanShots —
Creature.Update grew an optional shots param; TitanProjectile grew a Damage field w/
per-kind defaults), BOMBER BEETLE (chases, arms 0.7s strobing fuse at 26px, ANY death =
instant-fuse Dynamite projectile crater + manual player falloff dmg, no corpse — bombers
chain), SNAPPER VINE (rooted tether-lunge plant, 52px tether spring), ROCK MIMIC (spawns
Hostile=false disguised as gold-speckled boulder so sentries ignore it; wakes at 42px or
on hit; drops gold 3 + crystal). Refinements: VoidWraith blinks 46px toward prey every
~2.5s (refuses solid destinations), CrystalCrawler sprays 6 radial Spike shards (6 dmg)
when shot, MagmaSlug bursts into live lava cells on death. Depth roster rebalanced in
SpawnDirector (mimic ~2-4% everywhere below surface). ENCOUNTER FIX (user report "hardly
any enemies", same day): population hit cap but 0-1 encounters/3min — detect ranges (90-150px)
were smaller than the spawn donut (200-450px) and spawns landed in sealed pockets. Fixes:
(1) hunt ranges raised to 170-260px per kind (TickCaveEye takes an aggro param now);
(2) SpawnDirector.ReachableCaveSpots = bounded 8k-tile BFS from the player through non-solid
tiles (radial steps re-map angular index across ring tile counts) — spawns go to
air-connected caves so they can physically reach the player; sealed-pocket fallback spawns
tunnellers only (they dig to you); (3) stationary kinds (vine/mimic) no longer count toward
CaveSpawnCap — own allowance of 3, overflow re-rolls to a mover. Verified with
`dotnet run -- --spawnprobe` (src/Systems/SpawnProbe.cs, kept as a tuning tool): encounters
per 3 sim-min went 0→12 (verdant cave), 1→10 (surface), 0→3 (ember sealed pocket). NOTE: user codes in parallel — added HasMiningLaser/mining_laser
themselves. Kaiju wave done 2026-07-09 (Pacific Rim): 5 new TitanKinds appended after Kong
(RunSave int-cast safe) — Knifehead (blade-crest gore charge), Otachi (arcing acid globs →
live Material.Acid cells), Leatherback (EMP: Titan.PendingEmp → Player.EmpTimer kills
jetpack + laser/laser_cannon/mining_laser, HUD banner), Raiju (fast triple-dash chains),
Slattern (Rift apex, 4200 HP, tail-spike fans + sonic pulse, replaced Mecha there).
Coreheart got PlanetDef.TitanPool (rolls 1 of the 4 new per visit; survey shows UNSTABLE);
kill credit/HUD switched to _run.Titan.Kind (not def.Titan) so pooled souls bank right.
DM_BOSSCAM=<TitanKind name> forces a kind (DM_BOSSCAM=1 stays the plain follow cam).
Procedural-system era (2026-07-09, second wave): Planet.RingCount const became per-instance
planet.Rings (geometry cached per size; Planet.StandardRings=200, SkyHeadroom=71,
planet.SurfaceRing replaced WorldGen.BaselineSurfaceRing) so PlanetDef.SizeScale 0.7-1.8
gives real planet sizes. PlanetGen.Campaign(seed) rolls 7 worlds/campaign (biome archetypes:
verdant/frost/ember/slag + NEW ocean [LakeScale ~3x, mostly sea] + NEW acid world
[AcidPools + AcidRain toxic-cloud storms] + crystal) with difficulty = slot index = distance
(size, oxygen, ship-ore depth all ramp); 4 classic titan soul kinds always farmable, other 3
slots roll new kaiju; Rift appended fixed. Seed persists as MetaSave.WorldSeed (0 = roll at
boot); campaign completion rerolls it + clears PlanetsEscaped/Bank/PlanetsUnlocked.
PlanetDefs.All is now a swappable property (PlanetDefs.Activate); PlanetDefs.Classic keeps
the hand-tuned defs — ById falls back to Classic so DM_AUTOSTART=verdant etc. still work;
NEW aliases DM_AUTOSTART=ocean|acidworld hit the campaign's guaranteed biome instances;
DM_ACIDRAIN=<s> forces the first storm. Acid now corrodes ALL non-anchored tiles except
obsidian (2x faster). Flyer titans Pyrodactyl (lava rain) / Vitriodactyl (acid rain):
legless airborne locomotion (Titan.Flyer/Bombing), TitanShotKind.Lava+ballistic Splash()
into live cells. Phase 10 done 2026-07-09: jetpack III / aegis
capacitor (shield 4s) / magnet II (30px) / O2 II (x2) / hull II (9 pips) + ROVER LOADOUT
menu (L in orbit, src/Space/Loadouts.cs — cargo-priced per-drop kits, pending manifest
pays out at launch; DM_LOADOUT hook). Foundry = 22 lines; wishlist fully resolved. Next
candidates: new content (biomes/creatures/weapons), settings UI, balance, run-summary. THE MOTHERSHIP ERA (§0) IS COMPLETE THROUGH PHASE 8.
Star map done 2026-07-09: M in space = full-screen chart (sun-centred, dotted orbits, live
planet positions w/ names, ship marker "YOU" w/ heading arrow); radial sqrt compression
(MapProject/MapUnproject in Game1.Space.cs) fits the Rift; hover tooltip = survey deposits
(RARE FINDS split out), titan + soul kind + banked count, hazard manifest off PlanetDef
knobs (HazardsOf), shard status, range; sun has a corona no-go ring + hover card and BURNS
hull on contact in SpaceSim (asteroid-hit cadence, shield eats one, breach = emergency
dock); debug mode right-click warps the mothership (planet parking spot or open-space
point, held outside corona). DM_SURVEY=1 opens map at boot; DM_SURVEY=<planet id|sun>
ALSO force-hovers that body's tooltip (overrides real mouse) for DM_AUTOSHOT screenshots.
Remaining
upgrade ideas + next candidates in PLAN.md. Pixel font lacks ' (apostrophe) too — UI
strings: A-Z 0-9 .:-+/!?)[]=* only.
The user playtests BETWEEN my turns (their run.sav/meta.json change while I work — check
timestamps before assuming my tests clobbered something; window-close writes run.sav +
meta.json in the same second via the Exiting handler). The pixel font has NO '%' or em-dash glyphs — keep UI strings
to A-Z0-9 .:-+/!?)[]=*.

Headless testing: combine `DM_AUTOSTART=<planet-id|resume>` (skips the space screen; ids:
verdant, frost, ember, slag, core), `DM_LAUNCH=1` (with DM_AUTOSTART: plants a fuelled
rocket and lifts off at once — screenshots the escape ascent; added 2026-07-09), `DM_AUTOSAVE=<seconds>` (timed suspend-save), and
`DM_GOD=1` (god mode, off by default since 2026-07-07) with
[textures-and-crust](textures-and-crust.md)'s `DM_AUTOSHOT=<seconds>` screenshot hook. The space screen
itself is captured by running DM_AUTOSHOT *without* DM_AUTOSTART. Meta progression (planet
unlocks) is in `~/Library/Application Support/DwarfMiner/meta.json`. osascript keystroke
injection is NOT permitted on this machine — to exercise input-gated UI, temporarily force
the state in code, screenshot, revert.

Terrain era (2026-07-09, third wave): mountains became massifs (main peak + 1-3 shoulder
peaks, pow-1.7 profile, 512-sample ridge noise crags the silhouette ±22%, granite-veined
body, snow caps when SurfaceTile==Snow or HasWater above 20*MountainHeightScale rings).
VOLCANOES (WorldGen.CarveVolcanoes): basalt cone + open crater pool + 3-wide primed throat
down to an obsidian-shelled magma chamber 55-80 rings deep; fluid recorded as
Planet.LavaSeeds (acid volcanoes → AcidSeeds; their chambers clamp above the global
lava-fill line). PlanetDef.Volcanoes/VolcanoScale/VolcanoAcid: ember 2-3 big, acid worlds
1-2 acid, other biomes 1-in-4 chance of one small. Eruptions: Planet.VolcanoVents
(persisted in Planet state, RunSave v7) + Session.VolcanoTimer/EruptionLeft — Game1 spews
12 cells/frame at the vent for 5-9s every ~70-140s*QuakeScale. Physics: the too-big-region
"supported" valve now requires the flood to touch the crust (SurfaceRing+2); fully airborne
rock (undercut mountains, sky platforms) collapses at ANY size (SkyRegionCap 20000 = perf
valve only). Also fixed: RunSave now sizes the restored Planet by def.SizeScale
(Planet.RingsFor) — resume was silently discarding saves on non-1.0-scale worlds. Hooks:
DM_ERUPT=<s> forces first eruption, DM_VOLCANO=1 spawns the dwarf beside vent 0. SimTest:
TestSkyCollapse/TestVolcanoes; the collision "embedded" checks use Tiles.BlocksPlayer now
(glowshrooms are passable, IsSolidAt false-flagged grove spawns).

Debug mode (2026-07-09): PlanetDefs.DebugMode is ON by default (DM_DEBUG=0 disables) —
PlanetDefs.Activate appends PlanetDefs.DebugWorld (id "debug") to every campaign: SizeScale
1.8, SurfaceTile Snow (arms blizzards) + PlanetDef.SurfaceBands (NEW knob — WorldGen cycles
the ground cover through Grass/Snow/Gravel/Dirt/Basalt wedges), every disaster + biome
pocket + every ore biased in. Orbit 950px (inside planet 0, near the sun; special-cased in
SpaceSim like "rift"). Excluded from the shard economy everywhere (WarpShardsNeeded counts
non-rift non-debug; core piercing + GrantCoreShards skip it). DM_AUTOSTART=debug works.
Boot-clearance fix same day: RestoreShipState re-parks via PlaceShipAt if the saved
mothership pos boots within 260px of a planet surface — saved pos dates from atmosphere
entry and planets re-rack to boot angles, and a planet's orbital motion sweeps over an idle
ship parked closer than that (sun-away parking is the graze-safe spot).

Feel fixes (2026-07-10, user-reported): Player.Update's ground-snap (teleport onto any
block ≤1 ring below when walking off) is GONE — walk-offs seed vNormal -40 and fall a real
gravity arc; don't re-add snap if downhill walking looks bouncy, the user explicitly wants
the fall. SpaceSim.EntryRange 18→90 ("push into the planet too long" complaint) and
PlaceShipAt parks at BodyRadius + EntryRange + 80 — ANY future parking/spawn spot in space
must clear EntryRange or it force-enters on frame one. Prefetch-hold at entry aerobrakes
(vel *= exp(-6dt)) instead of zeroing.

Biome herds + ballistic rocket (2026-07-10): PlanetDef.Biome string (verdant/frost/ember/
slag/ocean/acid/crystal/rift/debug, set by Classic defs + PlanetGen.Stamp) keys
SpawnDirector's neutral fauna — 7 new CreatureKinds appended after RockMimic: SnowLoper
(frost), CinderSkink (ember, ember-glow light), RustBack (slag, drops iron), TidePuddler
(ocean), AcidStrider (acid), PrismSnail (crystal, violet light), NullMoth (rift SKY kind —
the Rift has NO ground fauna, its sky spawner is 100% null moths; debug world rolls all).
They reuse TickGrazer/TickHopper/TickFlyer brains; verdant keeps Grazer+Hopper exclusively.
Escape-rocket ascent (Game1.UpdateAscent) is now ballistic: thrust 430 along nose, gravity
175 toward core every frame, drag ~0 (0.12/s), cap 300 — jets off = arc over and fall back;
grounded contact damps tangential slide; docking glide damps velocity so gravity can't yank
the ship off the autopilot. A DM_LAUNCH boot with no input now hops ~10px and settles back
on the pad (it no longer hovers), so ascent screenshots need held thrust or a quick capture.

Titan leg/gait rework (2026-07-10, user: "legs don't make sense"): biped legs are now
3-jointed (hip-knee-ankle-toe digitigrade, Titan.AnkleLift/AnkleBack shared with renderer
IK) with a real alternating stride (StanceHalf/StrideHalf — feet plant ahead, body walks
past, feet cross; one foot always down). Suspension is velocity-SET like the worm/flyer
branches (vNormal = clamp(deficit*6,-160,230)) — do NOT revert to spring-accel + damping,
the on/off foot-support window pogo-pumped it into 800px/s catapults. Feet only support
when planted on REAL ground (FootSupports probes below the sole; ≤60px-above-body feet
count so it climbs slopes) — no more phantom mid-air hover. Roam-state guards: CliffAhead
turnaround, footstep damage+cave-in quakes aggro-gated (calm strolling was grinding its own
patrol path into a pit), and a buried de-aggroed walker plows straight UP to breach
(self-rescue in Update). Verified via temp --titanwalk telemetry probe (since removed) +
DM_BOSSCAM=Godzilla DM_HATCH=1 DM_AUTOSHOT frames; screenshots land in
bin/Debug/net8.0/screenshots/. Plow tamed same day (user: "destroying every block under
them at spawn"): walkers treat the bottom sector of the body (below 0.35*BodyRadius along
up) as COLLISION (push-out, shares the anchored-tile branch) — never demolition — and calm
plow power is 12 vs 26 aggroed; only the Sandworm still tunnels the floor. SimTest's plow
test now asserts both: slab at body height breaks, floor row survives (tiles placed via
world coords — reusing one angular index across rings drifts off-body). Dig-down hunt added
same day (user request): Anger > Titan.DigAngerGate (55) + player 140px+ below + roughly
underfoot → Digging: plants, forced stomp every 1.6s, each landing runs DigCrater (body-wide
bowl, power 18 < plow) so it sinks ~9px/s — slow and stomp-driven by design. Prey ABOVE by
140px+ while aggro → chimney climb (vNormal 175, needs Braced: walls both sides within
BodyRadius+64) or, unbraced in a pit (BelowLocalTerrain: solid above both flanks — NEVER
probe the surface through its own radial, a dug shaft is open straight up), a hunt-jump
(vNormal 520 one-shot + Leaping via _jumpTimer 1.1s; Kong excluded, its special owns
Leaping). Sideways = normal aggro chase + full-power plow at body height.

Another Claude session codes in this repo IN PARALLEL with auto-commit hooks (2026-07-10:
it was editing Titan.cs/TitanRenderer.cs/Program.cs while I shipped fauna) — expect
interleaved commits, rebuild before final verification, and beware `pkill -f -i dwarfminer`
kills THEIR game instances too (two DM_AUTOSHOT games running at once = interleaved
screenshot dirs).

BURNED ONCE (2026-07-09): a DM_LAUNCH test run DOCKS via FinishLaunch, which mutates real
player data — Escapes++, PlanetsEscaped, Bank.Remove(planet), ShipCargo/MotherFuel bank-in,
mothership ShipPos overwrite, and RunSave.Delete() (kills any suspended run). ALWAYS
`cp -a ~/Library/Application\ Support/DwarfMiner ~/tmp-dm-backup` BEFORE any headless run
that can dock/escape/die, and restore after. `kill` (SIGTERM) does NOT fire the Exiting
save handler, so a killed test won't write run.sav — but anything FinishLaunch already
saved is permanent.

City planets + lizard warrens (2026-07-13): NEW biome "city" — PlanetDef.CityLots raises
alien skyscrapers (WorldGen.RaiseCity: anchored AlienAlloy hulls w/ straight px-width sides,
CityGlass window bands that glow warm in Game1's ore-scan light pass, floor slabs w/
alternating stair gaps — NO slab on the ground storey, the plinth is its floor — doorway,
beacon-tipped antenna) and PlanetDef.LizardCities buries lizardman warrens
(WorldGen.CarveLizardCities: descending LizardBrick chamber chain + CarveTunnel bores +
brick-lined surface shaft; glowshroom lamps, huts, deepest hall = vault w/ gold + ruby;
all clamped above the lava-fill line). New TileKinds 31-33 AlienAlloy/CityGlass/LizardBrick —
all ANCHORED (never crumble, acid-proof) but mineable (drop iron/nothing/stone). Planet
grew CitySpawns + LizardDens lists (persisted; RunSave v10). New CreatureKinds appended:
Civilian (neutral city fauna, grazer brain, 3 robe dyes, corpse drops NOTHING — deliberate,
SimTest exempts it) and Lizardman (hostile warren guard: patrols, TitanShotKind.Spike bone
spears 70-210px w/ LOS, lunges <60px, red eye-light when aggroed, drops hide+gold).
SpawnDirector: "city" biome → Civilian (prefers CitySpawns addresses 200-650px from player),
cave spawns within 300px of a LizardDen re-roll 60% into Lizardman guards (after biome
specials, so warrens win). Classic chain gained "city"/Neonspire (before rift — Classic is
7 worlds now); PlanetGen guarantees 1 city per campaign at slot 3 (only mid slot the
ocean/acid guarantees can't claim) + ~1/3 of slot≥2 worlds get a warren; slag/core classics
+ DebugWorld carry warrens. Star map hazard tag LIZARDMAN WARRENS. Hooks: DM_WARREN=1
spawns INSIDE the first den hall (pairs w/ DM_AUTOSTART=city); DM_FAUNA parade includes
both new kinds. SimTest.TestCities covers all of it (268 checks PASS).

City defense + digger rebalance (2026-07-13, same-day riders): DIGGERS provocation-gated —
Borer/Centipede/HornedDelver now share MoleBeast's _provokedT grudge (set on HitFlash or
point-blank: 45px worms / 70px delver); unprovoked they wander-dig only. Chew() bites
architecture (AlienAlloy/CityGlass/LizardBrick) at power 1 (~10s/tile) and hardness>=4 rock
at power/3. ARCHITECTURE blast-RESISTANT not immune: Projectile.CarveCrater chips anchored
building tiles via Mine(power 2) instead of skipping. Towers upgraded: 3 size classes
(low-rise/mid/spire), curtain-wall vs banded facades, ledge ribs at slab rows, rooftop
styles (beacon mast/stepped crown/glass dome), foundations 14 legacy tiles deep + 1 wider.
CityLots 11 classic / 9-13 gen. NEW CreatureKind.Peacekeeper (city militia, neutral to
dwarf, drops iron): Game1 militia pass (before corpse loop) targets nearest Hostile
creature in 240px or hatched titan in 320px → ProjectileKind.CivicBolt (3 dmg, cyan dash,
FriendlyToNeutrals flag: Combat skips !Hostile creatures AND skips titan.OnDamage so militia
fire never re-aggros the titan onto the player — Hatch() starts titans aggroed, remember
in tests). City surface fauna cap 14 (vs 7), 1-in-3 spawns a peacekeeper. TITANS: roam
re-rolls 75% steer toward nearest CitySpawns tower (RoamSignTowardCity, 0=loiter);
Plow grew a wrecking bite — anchored building tiles take Mine(5) every 0.3s (_wreckTimer)
so a wall breaches in seconds-not-instantly (still push-out collision). SANDWORM: weave
flattened (±22px @ 0.45 pulse, gain 1.5, clamp ±90 — near-straight bores), pace 0.42x,
terrain bites throttled to 0.18s cadence at plowPow/3 — much slower block destruction.
SimTest.TestCityDefense covers all (blast chip, jaw resistance, provocation gate, bolt
neutrality/titan-no-aggro, slow wreck). 280 checks PASS.

Districts + civilisation exclusivity (2026-07-13, third wave): RaiseCity restructured —
towers now build in 2-4 DISTRICT clusters (centres rejection-placed >=0.55 rad apart; each
district rolls its towers first, centres the row on the bearing, then a cursor walks
tower+street-gap [12-24px] west-to-east; blocked lots skip). CityLots up again: 14 classic /
13-17 gen / 4 debug. ONE CIVILISATION PER PLANET: city defs lost LizardCities everywhere
(incl. DebugWorld — warren QA = DM_AUTOSTART=slag + DM_WARREN=1, hook comment updated);
WorldGen.Generate hard-gates CarveLizardCities behind def.CityLots == 0 regardless of def;
PlanetGen.Campaign guarantees >=1 warren world per campaign (post-stamp fixup on a random
non-city slot 2-6 if none rolled). TestCities rewritten: warren assertions moved to the slag
world, added no-warren-under-city, district-clustering (bearings w/ neighbour <0.14 rad),
and 3-seed campaign exclusivity checks. 286 checks PASS.

Warren biomes + megacity + saucers (2026-07-13, fourth wave): LIZARD WARRENS now acid/ember
(lava) ONLY — classic chain: ember carries it (slag/core lost theirs); PlanetGen rolls 1/2 on
acid/ember worlds, guarantee fixup targets the first acid/ember world (acid always exists).
Warrens 30% BIGGER (5-7 chambers, halfH 8-12 rings, halfWpx 39-78) with lava-band clamps
(halfH squashed + cr pinned so interiors stay dry above ember's 62% flood — REQUIRED there,
band is ~25 rings). WAR-CRY: Creature.CallingBackup set on lizardman calm→aggro edge;
Game1 pass rallies every lizardman within 800px via RallyToWar() (sets aggro+provoke, no
chain-cries); down-pitched "hurt" as the shriek. CITY = 1/3 of surface: CityLots 34 classic /
(int)(32*size)+rng gen; districts 3-6 (centres >=0.72 rad apart, towers also check `placed`
so wide rows never interpenetrate); class mix 18% small shopfront (6-12 legacy tall) / 42%
mid / 40% spire (30-56 legacy) — mostly skyscrapers, varied heights. NEW CreatureKind.Saucer:
neutral city AIR patrol (sky kind, 70% of city sky spawns, sky cap 8) — TickSaucer cruises an
altitude band, slides to hover 90px over GuardTarget; shares Game1's militia bolt pass
(scan 320/fire 280 vs peacekeeper 240/230); disc sprite w/ dome, chasing rim lights, belly
lamp (amber when tracking) + patrol light; crashes to iron 2 + crystal 1. Aliens 2x HP:
Civilian 24, Peacekeeper 52 (Saucer 44). SimTest: coverage check (wall-footprint fraction +
street-merged span >=25%), rally test (flat-runway scan first — arbitrary bearings wedge on
mountains), saucer patrol/station tests, campaign warrens-only-acid/ember assert. 295 PASS.

Walker nav + capital layout (2026-07-13, fifth wave): Creature.NavAxis(planet, up, right,
axis, avoidCliffs) — shared terrain sense for walkers: climbable lips still use GroundMove's
reflex hop; TALL walls (3 probes at +0/+10/+18px — building hulls) return 0 and flip _amble;
avoidCliffs turns around at drops > ~28px (so citizens pace tower floors/roofs — stair-gap
descents 32px now blocked for amblers, deliberate). Wired: TickGrazer amble (all herds +
civilians; flee skips it), Peacekeeper patrol+engage (engage = walls only, holds at wall),
Lizardman patrol (cliffs too) + chase (walls only — spears take over). MoleBeast digs 50%
slower (Chew interval 0.2→0.4). CITY LAYOUT = 1 CAPITAL (~60% of lots) + 1-2 satellite
towns: districtCount 2-3, centres >=0.95 rad apart, remainder split round-robin. Tests:
roof-edge stroll test (StampBlock platform on scanned-flat ground, 15s no fall), capital
grouping (sorted bearings split at >0.25 rad gaps w/ wrap merge → 2-3 groups, largest
>=45%). ALSO: parallel session added city-slot titan walker-swap in PlanetGen (Walks helper
— we both added it simultaneously, deduped to theirs; races like this are real, rebuild
before final verify). 297 checks PASS.

Saucer city-tether + disaster response (2026-07-13, sixth wave): SAUCERS now patrol their
CITY, not the whole planet — Planet.CityDistricts (lazy, cached, derived from CitySpawns
doorway angles: sort → gap-split at 0.5 rad → wrap-safe vector-mean centre + max-offset
half-width; empty off-city so no save-format change). TickSaucer adopts the nearest district
as its beat (_patrolAng/_patrolHalf, +0.18 rad margin; self-heals for restocked saucers) and
flips _orbitSign at the band edge to sweep back over the towers. SpawnDirector spawns city
saucers over the district NEAREST the player (jittered in-band), not a random bearing.
DISASTER MODEL (was already true, now explicit + tested): disasters damage ONLY the player
(TickHazardContact) — every creature, saucers included, is already disaster-proof; buildings
too (AlienAlloy/CityGlass/LizardBrick are ANCHORED → immune to acid corrode, quake/cave-in,
meteor + blast craters, and not in Cells.IsMeltable → lava can't melt them). TAKE COVER:
new Creature.TakeCover flag (set by Game1 each frame while AmbientDirector.DisasterActive on
a city world, ONLY for Civilian/Peacekeeper — NEVER saucers) → TickTakeCover sprints to the
nearest CitySpawn doorway (cached, 1.5s refresh) and huddles; peacekeepers still prioritise a
live GuardTarget (invader/titan) over cover. Saucers keep patrolling through it; the titan's
PendingShockwave still damages them (that loop hits all _run.Creatures) so they're vulnerable
to titan abilities, not disasters. SimTest.TestCityDefense +5: anchored assert, lava-can't-
melt-glass (vs dirt control that melts), citizen-runs-to-shelter (150→1px). 307 checks PASS.

Swimming era (2026-07-13, sixth wave): Cells.CountWaterNear(pos, radius) = immersion probe.
PLAYER SWIM: Game1 sets Player.InWater/HeadInWater pre-Update (Player has no Cells ref);
in-water movement flips — gravity off, idle sink -20, W/S/jump-hold strokes at SwimSpeed
(0.65x walk; fins 1.3x), jetpack won't burn submerged. BREATH: Player.Breath seconds
(base 12), drains head-under, refills max/2.5s in air, 0 = drown 9dps bypassing armor
(Game1.TickBreath); transient not saved. HUD BREATH bar (blue, y=72, only when diving,
status shifts to y=96) in Renderer.DrawHudBars. FOUNDRY AQUATICS: fins (2x swim,
Sandworm 1), lungs (x2, Godzilla 1), lungs2 (x3, req lungs), gills (never drains, req
lungs2, Sandworm 2) — applied in StartNewRun AND ResumeRun. CREATURE SWIMMERS:
Creature.Swims = TidePuddler/Lizardman/Grub/AlienWhale; post-tick buoyancy block (submerged:
vN → prey level for hunters / +26 surface-ward, rate 900/s — MUST beat the tick's 320/s
gravity or it only slows the sink [was 300, bug]). NEW WATER-ONLY kinds: AlienWhale (r11
neutral leviathan — TickWhale cruises basin, turns at shore/dry water, dives off surface,
lifts off floor, BEACHES helplessly out of water; bioluminescent flank spots + basin light;
drops meat 6 hide 2) and AlienCrab (r4.5 hostile lakebed scuttler, territorial rush <110px,
contact 10, drops chitin 2 meat 1). IsWaterKind (excluded from IsCaveKind); SpawnDirector
water cap 4 in 700px on HasWater worlds: TrySpawnAquatic walks radials near player for
open water (dry shore = abort), deep basins (>=8 cells below) roll whales 35%.
Hooks: DM_SWIM=1 spawns at the first lake (pairs w/ DM_AUTOSTART=verdant); parade includes
whale/crab (they beach on land — expected). SimTest.TestAquatics: pool carve+fill, stroke/
sink/fins/lung-tier ratios, lizard buoyancy, whale/crab legality. NOTE "hazard: acid melts
granite" is RNG-flaky (bounded ticks vs Random.Shared corrosion) — rerun before blaming a
change. 315 checks PASS.

Titan city-damage + census pre-spawn (2026-07-13, seventh wave): wrecking bite Mine(5)→
Mine(16) @0.2s cadence — alloy tile falls in ~3 bites (0.6s), wall torn open in seconds
("way more damage to cities"; explosions/jaws stay feeble). Titan.LevelAim clamps fire-
breath + acid-spray grains POST-spread to >=-15deg dip (sin 0.26) so Godzilla/Otachi sweep
across the surface instead of trenching their own footing (test: prey parked underfoot,
worst dip exactly -0.26). SPAWN OVERHAUL: Creature.Resident flag +
SpawnDirector.PopulateWorld(run) seeds the WHOLE planet at run start AND resume (Game1.
ResumeRun calls it — creatures aren't saved): city addresses staffed (budget 70, 1-in-4
peacekeeper), 2 saucers/district, 2 lizardmen/den, lakes swept every 0.22 rad (cap 12
aquatics), wild herds every ~0.4 rad outside districts. Residents NEVER distance-culled;
Game1 freezes their Update beyond 900px (cheap census) and gates draw+light at 1400px.
Dynamic spawners now NEVER spawn in cities: InCityDistrict(planet,pos) gate on surface+sky
spawns, building interiors (wall Alloy/Glass) excluded from cave-spawn spots, city biome
dynamic rolls = wild grazer/hopper + moths only (saucer/civilian dynamic rolls REMOVED —
superseded by census; the parallel session's nearest-district saucer spawn block was
deleted with it). SimTest: TestPopulateWorld (city 60 civs/10 militia/6 saucers pre-spawned,
79 creatures beyond 1000px, warrens 2/den) + breath-clamp + retuned wreck-speed checks.
318 checks PASS.

Acid reservoir lining (2026-07-13, eighth wave — user: "acid world everything melts + lags"):
WorldGen.LineAcidReservoirs(planet) runs LAST in Generate — multi-source flood from every
planet.AcidSeeds tile spreads <=10 tiles through ENCLOSED air only (stops at open atmosphere =
a Sky background wall, so it never spills out a pool's open mouth and glasses the surface),
converting each corrodible solid tile it borders into OBSIDIAN (acid- AND lava-proof via
Cells.IsCorrodible/IsMeltable both excluding it, but still mineable/blastable at hardness 6).
Skins surface acid pools + acid-volcano crater/throat/chamber + scattered crust seeps
uniformly; anchored tiles + existing obsidian skipped. THE LAG FIX: hemmed-in acid has no
corrodible neighbour so it settles and SLEEPS (TickLiquid sleep clause) instead of corroding
forever and waking tens of thousands of cells. Meteors also made slower same day (unrelated):
SpawnMeteor vel 240→110 (+lateral 120→70), Meteor.Update gravity 520→200, _life 8→16s.
SimTest: "plangen: acid pools are obsidian-lined" asserts 0/6310 acid seeds border corrodible
rock (neighbour walk via Planet.Inner/OuterNeighbour). 322 checks PASS. NOTE: parallel session
had just deleted my nearest-district saucer spawn block (superseded by census PopulateWorld) —
ALWAYS rebuild before verify, a stale --no-build binary showed a phantom titan-wreck FAIL.

Acid airtight rewrite + spawn-hazard guard (2026-07-13, ninth wave — "acid STILL leaks through
volcano/pools + lags"). Diagnosis via NEW `dotnet run -- --acidprobe` (src/Systems/AcidProbe.cs,
kept as a tuning tool; + Cells.ActiveCellCount wake-set accessor): original lining left ~22000
tiles/world corroding, wake-set ~20k. THREE real leaks found & fixed: (1) BURIED open-cave acid
pockets (old SeedsAcid seeds in bigN/smallN caves) poured into the cavern network — acid is
non-depleting so it floods the whole crust; REMOVED entirely (worldgen cave block no longer
seeds acid). Worlds keep acid only via surface pools + acid volcano + acid rain; slag def
SeedsAcid→AcidPools:3; debug already had AcidPools+AcidRain. (2) SURFACE POOLS connected to
caves via shared air → added `acidBuffer` (acidDepth hoisted; suppresses cave carve where
depth < acidDepth+12) so each pool is a sealed bowl. (3) ACID VOLCANO CRATER overflowed into
its open mouth (flood's atmosphere guard can't line open-sky-wall tiles) and ate the basalt
bowl — for def.VolcanoAcid the crater band (f<craterFrac+0.15) + throat walls are now Obsidian;
LAVA craters keep the EXACT legacy `rng.Next(3)` basalt/obsidian draw (changing it shifted RNG
and moved a warren den heart into rock — the "den hearts open 6/7" fail). KEY LINING BUG: acid
CELLS straddle two inner tiles at ring boundaries (tile counts differ), so lining only each
seed's single InnerNeighbour left a bare Dirt floor tile at pool edges that cells fell into →
cascade. LineAcidReservoirs now also skins the inner/outer tiles under each flooded tile's
ANGULAR neighbours (t±1), closing the straddle. RESULT: 0 corrosion after settle (was ~22k),
steady tick 1–1.6ms, wake ~4k = normal open-pool surface slosh (plateaus). SPAWN-HAZARD GUARD
(part 2 of the ask): Creature.ImmuneTo(Material) — Water=Swims|IsWaterKind, Lava=MagmaSlug|
CinderSkink, Acid=AcidStrider|AcidSpitter, else true; SpawnDirector.HazardRejectsSpawn(run,pos,c)
via Cells.SampleHazardsNear/CountWaterNear gates cave (TrySpawnCreature), surface
(TrySpawnSurfaceAnimal) + PopulateWorld (warren guards, wild herds) — cells are poured+settled
before both SpawnInitialFauna (calls PopulateWorld) and ResumeRun. SimTest: immunity map +
"nobody hatches inside lava/acid" (0 trapped/291 on flooded slag). 324 PASS. FLAKY (pre-existing,
per memory): "acid dissolves a soft tile" + "acid melts granite" fail ~1/3 on RNG — rerun.

The Hollow — belt mega-asteroid (2026-07-13, user ask: "asteroid so large you can land on it,
needs a space suit upgrade, rare materials, weird enemies, edge of the system"). PlanetDefs
.HollowWorld (id `hollow`, biome `belt`) appended to EVERY chain in Activate (like debug; the
append is one-way — TestHollow must run LAST in SimTest). Rides SpaceSim.BeltOrbitRadius=11500
(BeltHalfWidth 320) between last ordinary orbit (~8850) and Rift (14000); near the belt the
asteroid spawner raises its target +14 and 70% of spawns are prograde belt rocks
(SpawnBeltRock); dust band drawn in DrawSpace + star map. GATE: foundry `vacsuit` (1 Mecha
soul + 4 pure_iron + 2 sapphire) → SpaceSim.VacSuitLocked; airless defs bounce the ship like
the locked Rift (shared sealedOff loop), AtmosphereContact/EnsurePrefetch skip, amber
[VAC SUIT REQUIRED] label/approach/toast/tooltip. New PlanetDef knobs: GravityScale (0.45 —
Player.Gravity set in StartNewRun/ResumeRun, Planet.GravityScale stamped by WorldGen +
RunSave.TryRead, Creature.Grav(planet) replaced all hardcoded 320f), Airless, Craters (dry
lake-style bowls, 8), GreatGeode (WorldGen.CarveGreatGeode: huge crystal-shelled cavern
55-70 legacy tiles down, outer shell SetGem voidstone/diamond/emerald). LavaFillFrac 0 /
HasWater false / no gas/acid = totally dead rock; OxygenDrainScale 2.6 (meteors ~13s).
Platinum signature (ship 5), gold+silver+voidstone(0.105) biases. NO core shard: excluded
alongside rift/debug in WarpShardsNeeded, core-pierce grant/toast, GrantCoreShards, map
tooltip. Belt natives (CreatureKind append: Moonlet, VacLeech, Glimmermaw), ~45% of connected
cave spawns on belt biome; no surface herds or sky fauna (SurfaceFaunaFor/TrySpawnSkyAnimal
return null/early). Moonlet: orbits the DWARF at 74px via servo then ballistic slingshot
(_swing = no-steer window); VacLeech: skitterer brain + drains player.Oxygen 14/s in the
contact block (NOT ContactDamage); Glimmermaw: caveeye brain aggro:0 + lunge, lure at
LurePos(planet), light via Creature.AddLight(Renderer, Planet? planet=null) — call site
passes _run.Planet. 13 TestHollow checks + belt kinds added to the collision sweep; 337 PASS.
GOTCHA: leech contact test needs OPEN-AIR placement — surface spawn points can wedge a small
body and the collision escape push throws off position clamping (13px/tick teleport).
DM_AUTOSTART=hollow / DM_ORBIT=hollow / DM_SURVEY=hollow all work. NOTE: killed test sessions
write run.sav (suspend slot) — I deleted my god-mode test save after; watch for clobbering the
user's real suspend when live-testing.

Hollow deepened (2026-07-13, second wave — user: "no regular creatures, cosmic octopus titan
w/ egg near core, remove atmosphere, unique space creatures, remove dirt, lower gravity").
GravityScale 0.25; Planet.Airless flag (stamped WorldGen + RunSave like GravityScale) drives
Renderer.DrawWorld: no dusk band / no _atmoTex shell / no haze wisps, starAlpha from elev
0.62 — stars at full strength standing on the surface; space-view halos skipped for airless
defs. OxygenRules airless mode: AtSurfaceAir(depth, airless) halves the refill band,
DrainPerSecond(…, airless) has NO grace band (drains from depth 0), refill ×0.25
(AirlessRefillFrac, "starlight recycler"); def OxygenDrainScale rebalanced 2.6→1.5 to
compensate. No dirt: belt biome shallow band + buried seams → Gravel. CLOSED roster:
SpawnDirector SpawnAt has a belt override (Moonlet .26/VacLeech .52/Glimmermaw .74/
VoidBarnacle else, barnacle rides the stationary allowance + snaps to cave floor, NO
ClearSpawnSpace for it), TrySpawnCreature returns before the unconnected-tunneller fallback
on belt, sky spawner → StarJelly (SpawnSkyAt factored helper). New kinds: StarJelly (flyer
brain, translucent bell + filaments, sting 7) and VoidBarnacle (TickBarnacle: SETTLE UNDER
GRAVITY THEN ROOT — rooting at the raw spawn point re-fights the collider every tick and
can walk the shell into rock, the 1/450 embed fail; pull = player.Velocity toward shell,
grip lerp 330→160 by dist/140, LoS-gated; Pulling flag drives tongue sprite + light).
TITAN: TitanKind.CosmicOctopus "Starspawn" (append; RunSave int cast safe). Chassis 3400HP/
66spd, legless (InitLegs + Flyer-style exclusion), egg at (RingMin+34)*TileSize along
startAngle (ctor branch), Game1.CarveTitanNest() opens the chamber in StartNewRun;
DM_BOSSCAM relocates it hatched beside the dwarf. Movement branch: vNormal = clamp((targetR
- myR)*1.4, ±75) where targetR = player radius when aggro else nest band — swims through
rock via the worm-throttled Plow (worm flag now includes octopus, keepFloor off); stomp
excluded. TickStarspawn alternates OctoPulseNext: gravity well (PendingGravityWell
(pos,460,2.4s) → Game1 fields _gravityWell*, pull lerp 620→160 by dist, violet light) vs
7-bolt void volley (TitanShotKind.Void: r6 life2.4 dmg15, drill budget 3 so volleys chew
thin cave walls). TitanRenderer.DrawStarspawn: dome mantle + 14 hashed twinkling star-flecks
+ 8 sine-curled tentacles w/ sucker pips + under-mantle beak + windup telegraphs (collapsing
mote spiral for the well, growing null-orb at beak for the volley); palette violet; Mouth()/
MouthPos in sync (Position - up*26 + right*Facing*30). Game1: Void shot draw (black core,
violet corona) + light entry, TitanName "Starspawn", debug-menu row. 21 TestHollow checks
incl. closed-roster (SpawnInitialFauna + forced Update ticks → 0 outsiders), buried egg,
swims-through-rock (424→3px), volley + well seen, dirt-free census, barnacle reel. 344 PASS.

Hollow lumpiness (2026-07-13, third wave — "asteroid shaped lumpyness, not a perfect circle").
PlanetDef.Lumpiness (legacy tiles; hollow 40) → WorldGen LumpAt(ang): 6-sample lobe + 16-sample
dent angular noise ×1.5/×0.5, MINUS 0.3 bias (valleys cut deep freely; crests share SkyHeadroom
142 rings with mountains — hollow mountains cut to Min5/Scale0.35 to fit). Noise channels are
GATED on Lumpiness>0 so ordinary worlds consume no extra RNG draws (bit-identical gen streams —
the warren-den lesson). Terrain line (baseline + elev + lumps, mountains EXCLUDED) stamped into
Planet.SurfaceProfile (720 floats) + Planet.SurfaceRadiusAt(worldPos) interpolator; PERSISTED in
Planet.WriteState (count-prefixed after gems) → RunSave Version 12 (parallel session had just
bumped 11 for 24-slot toolbelt). Game1.DepthBelowSurface now reads SurfaceRadiusAt — critical
with airless no-grace O2: a lobe-valley floor under open sky is SURFACE, not a 40-tile mine.
BuildPlanetPreview transparency heuristic (rr > 0.80) → above-local-terrain test so the space
disc shows the potato limb. ~70-ring profile swing (≈14% radius); verdant stays <10. GOTCHA
FIXED: TestSpaceSim's brake check flaked (203px/s) — default AsteroidTarget 22 let a random
rock ram the ship mid-brake (+260 kick); the flight-model sim now runs AsteroidTarget 0
(TestSpaceCombat owns collision testing). "acid melts granite" failed once = the KNOWN 1/3 RNG
flake, passes on rerun. 4 new TestHollow checks (silhouette swing, round-world control,
SurfaceRadiusAt spread). 348 PASS.

Moons (2026-07-13, fourth wave — "cratered moon circling a planet, reuse belt creatures +
new ones, smaller of the two water planets becomes a moon, moons with and without
atmosphere"). PlanetDef.MoonOf (host id) → SpacePlanet.Parent + OrbitCentre (Pos chains off
parent's LIVE pos; OrbitRadius/AngularVel now MUTABLE — moon binding is a second pass since
hosts can sit later in All). SpaceSim ctor: solarSlot counter so moons don't eat a solar
band; binding pass: OrbitRadius = parentBody+330+lane*180, angVel 0.12 (52s lap); missing
host degrades to solar orbit. PlaceShipAt parks HOST-away for moons (sun-away can face into
the parent disc); ParkShipTrailing parent-centred. Live orbit rings parent-centred; star map
moons get a 14-dot halo around the parent marker (sqrt projection can't do off-centre
circles) + tooltip "MOON OF X - AIRLESS/HOLDS AN ATMOSPHERE". PlanetGen.Campaign now returns
9 defs: [7] rift, [8] = CraterMoon(rng, host chain[1+rng.Next(5)], NewName) — id "moon",
biome "moon", Airless, GravityScale 0.4, Craters 12, Lumpiness 8, size 0.65-0.75, silver
signature (ship 4), Raiju titan, NO shard (excluded in WarpShardsNeeded/core-pierce/
GrantCoreShards/tooltip alongside hollow). All moon RNG draws happen AFTER the 7-world
stamp, so existing campaign seeds keep their planets (ids stable — user meta safe). OCEAN
MOON: ≥2 ocean biomes in [0..6] → smallest in slots 1-6 gets MoonOf = biggest world's id,
SizeScale ≤0.8, keeps water/air/shard. Moon roster CLOSED (SpawnAt belt-or-moon override):
Selenite .40/VacLeech .70/Moonlet else; surface fauna = DustDevil (hostile, added to
IsSurfaceKind); sky = StarJelly; unconnected fallback skipped. New kinds: Selenite
(skitterer brain, faceted crystal + glass legs, cold gleam light) and DustDevil (grub chase
brain, stacked counter-swaying dust bands + static sparks, flicker light). Vacsuit desc now
covers "airless worlds". WorldGen dirt removal biome check → "belt" or "moon". 10 TestMoons
checks (runs before TestHollow; both Activate — one-way). plangen count test → 9 defs.
DM_AUTOSTART=moon / DM_SURVEY=moon work. 361 PASS.

2026-07-14 city-life pass: **doors** (TileKind.DoorClosed=34 solid / DoorOpen=35 passable, both anchored, drop "door"; recipe stone3+iron1 crafts 2; E toggles the contiguous vertical run — Game1.TryToggleDoor; city street doorways gen the outer column as DoorClosed) + creatures open them (Creature.CanUseDoors: Civilian/Peacekeeper/Lizardman/Marauder/Pyro; hooks in GroundMove reflex-hop + NavAxis tall-wall; idle door-users occasionally close open doors when the dwarf is >60px). **Towers refit** (WorldGen.BuildTower): full-height Ladder shaft opposite the street door (punches slabs), furniture row on each slab via POSITION HASH not rng (rng draws would shift downstream stamps) — AlienPlant/HoverPod/OrbLamp (=36/37/38, passable+anchored+hardness1, animated authored art in Renderer, OrbLamp glows in the ore-scan). **CityWrath** (Session.CityWrath 0-100, transient): resident kill +35 (skipped if titan rampaging within 380px), city tile broken +2.5 (OnTileBroken + melee cut); decays 1.2/s; ≥50 → toast, civilians TakeCover, peacekeepers+saucers add the PLAYER to threat scan and fire TitanShotKind.Slug at them (CivicBolt is hard-coded dwarf-friendly). Saucers 2→4/district. **Titan vs buildings**: wreck bite Mine power 22 @ 0.12s cadence (SimTest pins "first instant only cracks"); titan SHOT impacts (non-Slug) chip alloy/glass/brick Mine 14. Worms denser: 30-40 walks, longer, branch chance 1/55, branches re-branch. **Fullbright debug-menu toggle** (_fullbright rides the DM_NOLIGHT skip). Gold/silver: brighter BaseColor, travelling glint overlay in Renderer tile pass, metal dust glitters (Cells.ColorFor Dust special-case). Title: volcano fn rewritten (concave flanks/ledges/lit west flank/ember crater lip), A smaller @100, B smaller @505 (right), planet terminator posterized to 4 dithered cels. Hooks: DM_AUTOSTART=city, DM_CITY=1 (spawn at district), `--cityprobe` (gen counts + door-AI walk test).

2026-07-15 QA-rig rework (user request): DebugWorld is now SMALL (SizeScale 0.7 — the
generator's proven floor; a 0.55 try left the lava sea leaking into the dry strata, ~75k
cells churning forever) and CREATURE-FREE (`PlanetDef.NoFauna` skips PopulateWorld + the
spawner trickle). The F9 debug menu is TABBED (TITANS / CREATURES / EVENTS / MISC, Left/
Right or Tab or click headers, long tabs scroll): the CREATURES tab lists every
CreatureKind straight off the enum and spawns at the mouse cursor. PerfTest pins its own
`HarnessWorld` (DebugWorld `with` the old 1.8× giant knobs) so `--perf` numbers stay
comparable — the harness must NOT track QA-rig tuning. Also fixed a real sign bug:
SpawnDirector.FindSurfaceSpawn returned a point ~1.5 tiles INSIDE the ground (`p - dir`
walks inward); it now backs out to clear air + clearance — every surface spawn (player,
herds, simtest subjects) starts above ground. New feature simtests must place subjects via
SimTest.SurfaceSpawnAbove (see its doc comment) — raw tile centres embed any body.

