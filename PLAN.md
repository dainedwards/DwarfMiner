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
>   Mecha robot w/ chest reactor, visor, digitigrade legs; legless 3-headed Hydra serpent;
>   broad-armed Kong ape) — NOT a re-tint. Bipeds = 2 legs, Hydra = 0 (serpent surface-follow +
>   long verlet body). Bosses **smash through mountains** (`Titan.Plow` mines overlapping
>   non-anchored tiles) instead of walking over; footfalls fire a **cave-in** quake below
>   (`StompTile`). Mecha laser now **charges** (growing orb + tracking telegraph) then fires a
>   sustained drilling beam (bolts carve terrain); Godzilla atomic breath is denser/brighter
>   with dorsal-spine charge glow. The ~250-line inline render block left Game1 (2249→1926
>   lines even with all the boss content added).
>   NOTE: god mode (`Player.FlyMode`) is now fully damage-immune (dev-tool intent).
>   Test hooks added: `DM_BOSSCAM` (egg beside spawn + camera follows boss), `DM_ZOOM=<f>`.
>   Pre-existing Stomp still gates on the shallow `Grounded` probe (under-fires) — cleanup.
> Next up: sound (still no audio at all), or surface base (storage depot), or ambient events.
> Test hooks: `DM_AUTOSHOT=<s>` screenshots on a schedule; `DM_AUTOSTART=<planet-id|resume>`
> skips the star map (ids: verdant, frost, ember, slag, core); `DM_AUTOSAVE=<s>` timed
> suspend-save; `DM_GOD=1` starts runs in god mode (fly, free weapons).

Current state: circular polar planet with a Noita-style cell sim (water/lava/dust), structural
physics with collapses, a Titan boss, cave/surface/sky fauna, a 30+ recipe crafting tree, three
endings (kill Titan, pierce core, launch rocket with 5 rocket_parts), and cross-run meta
progression in MetaSave. Game1.cs is ~2,250 lines doing everything — input, game state,
spawning, UI, and win/lose logic.

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
