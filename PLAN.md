# Dwarf Miner — Roadmap

> **Status (2026-07-07):** Section 1 is implemented — GameScreen state machine, Session
> extraction, PlanetConfig (`PlanetDef`) with 5 planet archetypes, the star-map overworld,
> and the staged spaceship escape (launch pad → hull → engine → nav core → L to launch).
> Section 2 is done: the ItemDef registry (`src/Game1.Items.cs`) replaced the four per-item
> switches; god mode is opt-in (survival default); Game1 is split — `UI/CraftingMenu`,
> `UI/InventoryUi`, `Systems/SpawnDirector`, `Session`, `Game1.Items`.
> Section 3 progress:
> - **In-run save/load done** (`Systems/RunSave`) — F5 or quitting suspends the run (single
>   gzip slot, ~150 KB); R on the star map resumes; any run ending voids the save. Also fixed:
>   Esc now closes the crafting menu instead of quitting the game.
> - **Oxygen/depth pressure done** (`Systems/OxygenRules`, `Player.Oxygen`) — air refills at
>   the surface, drains with depth (per-planet `OxygenDrainScale`: slag 1.7 thinnest), empties
>   into suffocation HP loss. `air_tank` recipe doubles the ceiling. AIR bar in the HUD.
> - **Hazard cells done** — two new `Material`s in the cell sim: `Acid` (flows, dissolves soft
>   tiles incl. stone but not hard rock/ore, burns on contact) and `Gas` (rises, chokes = extra
>   air drain, flash-burns to smoke beside lava). Body-contact via `Cells.SampleHazardsNear` →
>   `Game1.TickHazardContact` (this also finally makes LAVA damage the player). Worldgen seeds
>   per-planet (`PlanetDef.SeedsGas`/`SeedsAcid`: ember=gas, slag=acid, core=both); star map
>   shows TOXINS + THIN ATMOSPHERE warnings. Cells serialize for free in the save.
> - **Boss variants + egg done** — Titan hatches from a giant egg (`Titan.Hatched`/`EggTimer`
>   10-min / `EggHealth` attackable to hatch early; Combat routes hits to the egg while unhatched).
>   Four variants (`TitanKind`, per-planet via `PlanetDef.Titan`): Godzilla fire-breath + Mecha
>   laser (both spawn `TitanProjectile` enemy shots), Hydra burrow→erupt, Kong leap→quake
>   (melee AoE via `Titan.PendingShockwave`, consumed in Game1). `DM_HATCH=<s>` shortens the egg.
> - **Boss overhaul done** — each variant now has its OWN procedural skeleton in the new
>   `Rendering/TitanRenderer.cs` (upright fire-breathing Godzilla w/ dorsal fins + tail; boxy
>   Mecha robot w/ chest reactor, visor, digitigrade legs; **Dune sandworm "Shai-Hulud"** —
>   segmented sandy tube + round inward-toothed maw, burrows and breaches straight up out of
>   the ground; broad-armed Kong ape) — NOT a re-tint. Bipeds = 2 legs, Hydra = 0 (serpent surface-follow +
>   long verlet body). Bosses **smash through mountains** (`Titan.Plow` mines overlapping
>   non-anchored tiles) instead of walking over; footfalls fire a **cave-in** quake below
>   (`StompTile`). Mecha laser now **charges** (growing orb + tracking telegraph) then fires a
>   sustained drilling beam (bolts carve terrain); Godzilla atomic breath is denser/brighter
>   with dorsal-spine charge glow. The ~250-line inline render block left Game1 (2249→1926
>   lines even with all the boss content added).
>   NOTE: god mode (`Player.FlyMode`) is now fully damage-immune (dev-tool intent).
>   Test hooks added: `DM_BOSSCAM` (egg beside spawn + camera follows boss), `DM_ZOOM=<f>`.
>   Pre-existing Stomp still gates on the shallow `Grounded` probe (under-fires) — cleanup.
> - **Surface base / storage depot done** — craft a **Storage Depot** (stone-only so it's
>   always rebuildable), stand at it and press **B** to bank all raw mats / **N** to withdraw.
>   The stash persists per-planet in `MetaSave.Bank` (survives death), cleared on escape.
>   `Tiles.IsBankable` gates what banks (raw mats only, not gear). `Session.DepotPos` +
>   `RunSave` v5. HUD shows a deposit/withdraw prompt at the depot and a "banked stash — build
>   a depot" nudge otherwise. (Skipped the optional workbench-gates-late-recipes idea.)
> - **Sound done** — fully procedural synth in `Systems/Sfx.cs` (swept oscillators + filtered
>   noise + envelopes → 16-bit PCM → MonoGame `SoundEffect`); NO asset files. 10 effects
>   (dig/break/pickup/shoot/explode/hurt/collapse/hatch/ui/launch) wired to game events,
>   positioned via `Game1.PlayAt` (rotation-aware pan + distance falloff through the camera
>   matrix), throttled per-effect. Fail-safe: all device calls try/catch behind `_ok`, so no
>   audio device = silent, never a crash. `Synth()` is static/device-free (headless-tested).
>   `DM_MUTE=1` silences. Note: audibility can't be verified headlessly — synth buffers + no
>   crash with audio active are what's confirmed.
> - **Ambient events done** (`Systems/AmbientDirector.cs`, `Entities/Meteor.cs`) — **meteor
>   strikes** fall from the sky (telegraphed by a ground reticle + fiery trail), crater the
>   surface, expose a fuel/gold ore core (feeds the ship's fuel tank!), splash lava/smoke, and
>   blast the dwarf if caught. Cadence scales with thin atmosphere (`OxygenDrainScale`). **Magma
>   surges** on lava-rich worlds (LavaFillFrac ≥ 0.5) flood a nearby cave with welling lava.
>   Wired to the sound system (explode/collapse). `Session.Meteors`, `DM_METEOR=<s>` test hook.
> - **Per-weapon shot sounds done** — each gun/thrown weapon now has its own synth voice
>   (`ItemDef.ShotSound`): pistol crack, MG rattle, laser zap, heavy drilling beam, rocket
>   whoosh, cannon boom, throwable toss (dynamite/tnt), harpoon twang (nuke reuses the rocket
>   whoosh). The single central fire-sound hook in `Update` resolves the current weapon's
>   `ShotSound` (falls back to the generic "shoot" pew), so no Fire* method changed. 8 new
>   `Sfx` synths, swept by the existing `TestSfx` name loop.
> - **Cave-in warnings done** — building on the existing tremble telegraph (`Physics` condemns
>   a region and it jitters for `TrembleTime` before crumbling): `Physics.NewlyCondemnedThisTick`
>   now sounds a groaning **"creak"** synth the instant rock is condemned (lead time before the
>   "collapse" boom), and `Game1.UpdateCaveInWarning` scans condemned tiles hanging *over* the
>   dwarf (larger polar ring = falls onto them), **sifting dust** from them and flashing a
>   **"! CAVE-IN !"** HUD banner so there's a clear "move now" cue. `TestCaveIn` covers the
>   condemn → tremble → crumble sequence.
> - **Flyable solar system done (space phase 1, 2026-07-08)** — the point-and-click star map
>   is replaced by a real space scene (`GameScreen.Space`): sun at the origin, the 5 planets
>   on slow circular orbits, a manually flown rocket (A/D turn, W thrust, S brake; drag +
>   speed cap, no gravity wells), camera zoomed out to system scale. Launching off a planet
>   now hands you the stick: `FinishLaunch` banks the escape meta, then drops the rocket in
>   space above the departed world with the camera easing out from planet scale (escape no
>   longer shows a game-over screen). Fly within `SpaceSim.LandRange` of a planet and press
>   Enter/E to land (= `StartNewRun`); locked planets read UNCHARTED and refuse; death → R
>   returns you to your ship parked at that world. Sun corona and planet discs are solid
>   (bounce/skim). Model: `src/Space/SpaceSim.cs` (pure logic, 9 SimTest checks); screen:
>   `src/Game1.Space.cs`. The space screen owns camera zoom per-frame; `_playZoom` restores
>   the in-run zoom (incl. DM_ZOOM) on landing.
>   Remaining space work: see **§0 The mothership era** below (redesigned 2026-07-08 —
>   supersedes the earlier phase 2–4 sketch that lived here).
> Next up: mothership-era phases 2–5 (§0); then a settings/volume UI, or new content
> (biomes/creatures/weapons).
> Test hooks: `DM_AUTOSHOT=<s>` screenshots on a schedule; `DM_AUTOSTART=<planet-id|resume>`
> skips the space screen (ids: verdant, frost, ember, slag, core); `DM_AUTOSAVE=<s>` timed
> suspend-save; `DM_GOD=1` starts runs in god mode (fly, free weapons); `DM_UPGRADES=1`
> opens the mothership foundry at boot.

Current state: circular polar planet with a Noita-style cell sim (water/lava/dust), structural
physics with collapses, a Titan boss, cave/surface/sky fauna, a 30+ recipe crafting tree, three
endings (kill Titan, pierce core, launch rocket with 5 rocket_parts), and cross-run meta
progression in MetaSave. Game1.cs is ~2,250 lines doing everything — input, game state,
spawning, UI, and win/lose logic.

## 0. The mothership era (agreed 2026-07-08)

**Vision.** A "run" is no longer one planet — it's conquering the whole system. The player
lives aboard a **mothership** in space (the game starts there). Loop per planet: fly the
mothership to a world → descend in a **disposable rover** (this starts the planet visit) →
mine, survive, optionally slay the titan (grants a **titan soul**; no longer ends the visit)
→ build the rocket and launch — the rocket returns you to the mothership, transferring your
raw materials into the ship's cargo hold and leftover fuel into its tank. Aboard the
mothership, spend **titan souls + rare cargo** in the upgrade foundry. Each planet hides a
**core shard** near its center (the pierce-the-core rework — piercing yields the shard
instead of ending the run). Collect all five shards to build the **warp engine** and jump to
a super-hazardous warp world that's out of normal flight range — conquering it completes the
run. The mothership mounts a **gun** for asteroids/obstacles and burns **fuel** to traverse
to farther worlds; both, plus the **engines**, are upgradeable.

**Phases.**
- **Phase 2 — mothership core (DONE 2026-07-08):** the mothership replaced the bare rocket
  as the thing you fly in space (you start aboard it; new twin-nacelle sprite). Asteroids
  drift the system (donut spawner around the ship, cull at range) — dodge them or shoot them
  with the autocannon (SPACE/LMB, fires along the nose; big rocks split in two); collisions
  cost hull (5 pips, 1s invulnerability window, red hit-flash) and hull-zero forces an
  emergency dock at the nearest charted world (position lost, hull patched — no death).
  Titan kill no longer ends the visit: the boss dies in-world (update/render gated on
  Health > 0, HUD shows SLAIN) and banks a soul into `MetaSave.TitanSouls` keyed by
  TitanKind, awarded on the health zero-crossing so resumed saves can't re-award. The rocket
  is the only way off-world; docking transfers bankable raws → `MetaSave.ShipCargo` and
  leftover mined fuel → `MetaSave.MotherFuel`. Upgrade foundry (U in space, Esc/U closes):
  JETPACK (Player.HasJetpack — hold jump airborne, 2.6s charge refills on the ground,
  re-applied from meta on every entry, not in the run save), AUTOCANNON II (2× fire rate),
  ION ENGINES II (+40% thrust/top speed) — purchases persist in `MetaSave.ShipUpgrades`,
  souls spent via `MetaSave.SpendSouls` (any-kind for now). `src/Space/Upgrades.cs` is the
  catalogue; 15 new SimTest checks (combat + foundry economy); `DM_UPGRADES=1` opens the
  foundry at boot for screenshots.
- **Phase 3 — upgrade depth + economy (DONE 2026-07-08):** **fuel replaced the escape-chain
  unlock** — every planet is landable, but thrusting burns the mothership tank
  (0.4 fuel/s; Ion Engines II sip 0.28 and also go faster) and a dry tank drops to 35%
  reserve power (slow limp, never a soft-lock; HUD shows [RESERVE POWER]). Tanks start with
  10 courtesy fuel; mined fuel banks on docking. **Rovers are consumable**
  (`MetaSave.Rovers`, start 3): landing spends one; with none left you can still land via
  emergency drop pod at **half health**; the foundry builds more (repeatable line, 5 iron +
  3 coal, no soul). **Soul costs are titan-kind-specific** (`UpgradeDef.SoulKind`,
  `MetaSave.SpendSoulsOf`; cost labels name the boss — "1 STONE APE SOUL"). New foundry
  lines: HULL PLATING (hull 5→7, Kong), O2 RECYCLER (+50% air, stacks with air tank,
  Godzilla), DRILL RIG (+1 pickaxe tier on deploy, Sandworm). **M-key system survey**
  (`src/Space/Survey.cs`): per-planet titan + souls owned + top-5 ore deposits from a cached
  fixed-seed worldgen census (~40 ms/world, first open only; signature nav-core ore always
  listed; obsidian excluded as bulk terrain). 13 new SimTest checks. `DM_SURVEY=1` opens the
  survey at boot. (Take-gear-down loadouts moved to the noted-for-later list.)
- **Phase 4 — the warp run:** core shard material near each planet's center (pierce-core
  rework); warp engine craft needing all 5 shards + souls; the **warp world** — a new
  PlanetDef harsher than Coreheart (extreme hazards, nastiest titan, maybe two) unreachable
  by normal flight; run completion = conquering it; run-summary screen; decide what "new
  run+" resets (souls? upgrades? shards?).
- **Phase 5 — polish/persistence:** persist mothership position/hull; thruster + gun sfx;
  landing/launch transition flourishes; DM_SPACE-style test hooks; settings/volume UI.

**Upgrade ideas noted for later** (foundry slots to add in phases 3–4): jetpack tiers
(charge/thrust), drill/pickaxe tiers, armor suit, oxygen tank tiers, cargo hold capacity,
ship shield, ore magnet (pickup radius), scanner (ore/titan intel on the HUD), rover
quality/count, sentry capacity, ship gun spread/damage tiers, engine range/speed tiers,
warp engine (phase 4 capstone).

**Open design questions:** does death on a planet cost more than the current visit (e.g. the
rover)? Do souls/upgrades persist across completed runs (prestige reset vs. permanent)?
Should the warp world end the game or open a second system?

## 1. The big feature: overworld + spaceship (first)

The rocket escape (`TryLaunchRocket`) is already a proto-spaceship, and `MetaSave` is already
the cross-run persistence layer.

### Game state machine
Replace the `_gameOver` / `_craftingOpen` booleans with
`enum GameScreen { Overworld, Playing, GameOver }`, overworld as the entry screen.
This is the enabling refactor for everything below.

### Session extraction
Bundle the per-run world (planet, cells, physics, player, titan, entity lists, timers) into a
`Session` class that `StartNewRun` builds — the unit that gets swapped atomically when the
player travels to a different planet. Also exactly what in-run save/load will serialize later.

### Planet types (PlanetConfig)
`WorldGen.Generate(seed)` → `Generate(seed, PlanetConfig)`; the config drives knobs that
already exist as constants: lava fill radius (0.45 in Game1), lake count, mountain count/height,
ore threshold biases, surface palette, spawn rates, earthquake cadence.

Archetypes:
- **Verdant** (starter): current tuning — water, shallow lava, gentle fauna
- **Frozen**: snow surface (Snow TileKind exists unused), sapphire-rich, more water
- **Volcanic**: lava at ~65% radius, no surface lakes, coal/ruby/obsidian-rich, harsher spawns
- **Barren/metallic**: no water, darker lighting, dense iron/gold/platinum, more earthquakes
- **Core world** (finale): brutal spawns, abundant diamonds — where the Titan fight and
  core-pierce endings live as the climax

### Overworld screen
Star map: 5–8 planets as small colored circles in a chain (reuse ring rendering for
miniatures). Select with cursor/mouse; shows planet type, hazard level, notable resources.
Progression (`CurrentPlanet`, per-planet Visited/Escaped, unlock chain) lives in MetaSave.

### Spaceship building (replaces "5 rocket parts + L")
Physical and staged, using the existing placeable-tile pattern:
1. Craft a **Launch Pad** (placeable, surface-only) — anchors the ship site
2. Craft ship stages at the pad: **Hull** (iron/stone), **Engine** (gold/coal), **Nav Core**
   (crystal/gems) — each renders progressively on the pad so the ship visibly grows
3. Resource twist: each planet's ship demands deep-mining that planet's signature ore —
   building the ship IS the reason to dig deep
4. Board and launch → back to overworld with the next planet unlocked

Death still resets the current planet (roguelite loop intact); the meta layer becomes a map
instead of just stat bonuses.

## 2. Refactoring (priority order)

1. **Split Game1.cs**
   - `Screens/` — PlayScreen, OverworldScreen, each with Update/Draw (forced by state machine)
   - `UI/CraftingMenu.cs`, `UI/InventoryUi.cs` — cursor, drag-carry state, hit-test dicts
   - `Systems/SpawnDirector.cs` — the three spawn timers + TrySpawnCreature/fauna/CountKindsNear
   - `Session.cs` — the per-run world bundle
2. **Item/weapon data table** — the string-id switches (UseSelectedSlot, craft-output wiring,
   visibility) mean one new item touches 4+ scattered switches. A single `ItemDef` registry
   (id, verb kind, tier gate, craft effect) collapses those. Do before adding many more items.
3. **Game-over/victory into the state machine** — "escaped to overworld" is a fourth outcome
   that isn't game over at all.
4. Leave Cells, Physics, Planet, Renderer alone — cohesive and well-commented.
5. God-mode default-on (`FlyMode = true` in StartNewRun) → env var / launch flag before shipping.

## 3. Gameplay additions (post-overworld, by value/cost)

- **In-run save/load** — runs get longer once ships take real mining; serialize tile array +
  inventory (cell sim re-settles on load like StartNewRun's 120-tick pre-settle).
- **Oxygen/depth pressure meter** — soft timer making deep dives planning exercises (air
  tanks, supply caches). Fits "dig deep for ship parts" perfectly.
- **Hazard cells** (sim already supports): flammable gas pockets (rises), acid (dissolves
  tiles), freezing water on ice planets. Each is one new Material in Cells.
- **Mini-bosses per planet** — Titan variants (magma titan, frost wyrm) reusing the Titan
  chassis with different attacks; kills could drop a unique ship component.
- **Surface base value** — storage depot (bank resources so death doesn't wipe them) and a
  workbench requirement for late recipes so the pad becomes a home base.
- **Ambient events** — meteor strikes (danger + rare ore delivery), cave-in warnings, magma
  surges on volcanic worlds.
- **Sound** — there is no audio at all. Even minimal procedural blips for mining/collapse/
  damage would transform feel; collapses especially (screen shake is the only feedback now).
