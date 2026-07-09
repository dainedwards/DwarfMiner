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
> - **Phase 11 — content drop (DONE 2026-07-09):** **Rare gems**: EMERALD (deep seams on
>   verdant/frost, TileKind 28) and VOIDSTONE (TileKind 29 — base threshold unreachable,
>   only the Rift's bias spawns it: the campaign's endgame gem, ~30 tiles/world). Both wired
>   through drops/banking/refinery-exempt/atlas/survey/scanner. **Underground biomes**
>   (`WorldGen.SeedBiomePockets`, per-def counts): CRYSTAL CAVERNS (mid-depth cavities lined
>   in crystal) and FUNGAL GROVES (shallow moss-walled caves with wild glowshrooms — first
>   natural light underground). **Disasters** (AmbientDirector): SOLAR FLARE (all worlds,
>   ~7s warned window then 9s of 7 dps to anyone in surface air; DM_FLARE=<s> hook) and
>   BLIZZARD (snow worlds, 15s squall, 2.5 dps exposed). **Gem capstone upgrades**: EMERALD
>   WEAVE (Kong + 3 emerald — max HP 140) and VOIDSTONE REACTOR (Mecha ×3 + voidstone —
>   thrust burns no fuel, ever). **Held weapons**: pistol/MG/laser/laser-cannon/rocket-
>   launcher/cannon/harpoon/mining-laser get pixel sprites drawn in the dwarf's grip,
>   rotated to the aim and flipped left. 6 new SimTest checks.
> - **Phase 12 — biome fauna (DONE 2026-07-09):** three creatures tied to the phase-11
>   biomes, all reusing proven AI brains with distinct stat blocks. **SPORE BAT** (shallow
>   caves on fungal-grove worlds — frail flitter on the cave-eye brain, bursts into a
>   choking gas puff on death: kill it at arm's length). **CRYSTAL CRAWLER** (deep caves on
>   crystal-pocket worlds — 45 HP armoured slab on the grub brain, 13 contact damage, walking
>   geode art; drops 2 crystal + chitin). **VOID WRAITH** (the Rift, any depth, 25% of
>   spawns — fast vicious phantom with a pulsing violet aura and wisp trail; **drops 1
>   voidstone**, making the endgame gem farmable but Rift-gated). Spawn tables get a
>   biome-special override roll keyed off def pocket counts / rift id. Collision sweep now
>   covers all 7 tested kinds (175 placements); 3 new checks. `DM_FAUNA=1` parades the new
>   kinds beside spawn for screenshots.
> Next up: weapon variety, a settings UI beyond the F6 volume cycle, difficulty/balance
> tuning from playtests, or a run-summary/stats screen.
> Test hooks: `DM_AUTOSHOT=<s>` screenshots on a schedule; `DM_AUTOSTART=<planet-id|resume>`
> skips the space screen (ids: verdant, frost, ember, slag, core); `DM_AUTOSAVE=<s>` timed
> suspend-save; `DM_GOD=1` starts runs in god mode (fly, free weapons); `DM_UPGRADES=1`
> opens the mothership foundry at boot; `DM_SURVEY=1` opens the system survey at boot;
> `DM_DESCEND=1` forces the rover descent under DM_AUTOSTART; `DM_ORBIT=<planet-id>` boots
> straight into the parking orbit.

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
- **Phase 4 — the warp run (DONE 2026-07-09), plus two riders:**
  **Refinery (balance):** docking smelts raw metals 4:1 into pure ingots
  (`MetaSave.RefineCargo`: iron/coal/silver/gold/platinum → `pure_*`; remainders stay raw
  and join the next batch; gems/crystal stay precious as-is; stone stays bulk). All foundry
  costs now bill pure ingots, so a purchase represents a real haul, not six pebbles.
  **Lander descent:** deploying from the mothership now actually launches the rover — a
  visible pod drops from 300 px up at 85 px/s with **A/D lateral steering** (semi-controlled
  landing site), retro-flame, world simulating beneath, touchdown dust + thump exactly where
  it settles (never embedded — nudge-out pass). Steel/glass/skids pod drawn over the dwarf;
  `DM_DESCEND=1` forces it under DM_AUTOSTART for tooling.
  **The warp run:** the core drill's pierce no longer ends anything — it yields the planet's
  **CORE SHARD** (`MetaSave.CoreShards`, once per world, banked instantly so dying on the
  climb out can't lose it; HUD + survey track n/5). All five shards wake the warp drive:
  **J warps the mothership** to **THE RIFT** (`rift` PlanetDef — orbit 9800 far past the
  system, body 210, LavaFillFrac .7, O2 drain 2.2, spawn cap 30, gas+acid, Mecha titan,
  diamond/ruby/platinum-rich), which refuses landings until shard-complete. **Escaping the
  Rift by rocket with its titan slain = campaign complete** (`MetaSave.RunsCompleted`,
  victory overlay); escaping with it alive docks you with a taunt. 12 new SimTest checks
  (refinery ratios, rift def/worldgen/orbit, rebalanced foundry costs).
  Deferred from this phase: a proper run-summary screen and the "new run+" reset decision
  (souls/upgrades/shards currently all persist across campaigns).
- **Phase 5 — polish/persistence (DONE 2026-07-09):** the mothership persists across app
  restarts — position, heading, and hull snapshot into MetaSave on landing and on quit
  (`ShipStateSaved` flag, not NaN sentinels: System.Text.Json refuses NaN, which would have
  silently killed saving). Space audio: "thrust" ion-rumble synth re-triggered while burning
  (quieter on reserve power), positional rock-shatter via PlayAt when an asteroid dies, hull
  "hurt" thud on impacts. **F6 cycles master volume** (FULL/LOW/QUIET/MUTED, persists in
  `MetaSave.VolumeStep`) on any screen. The game-over overlay is multi-line now, and the
  campaign victory shows a proper summary. **New-run+ decision:** souls, upgrades, and the
  mothership endure across campaigns; the warp home burns the five core shards, so each
  campaign re-pierces the worlds. Remaining polish ideas: a full settings UI, landing/launch
  transition flourishes beyond the zoom eases, and the pre-existing Stomp `Grounded` probe
  cleanup.

- **Phase 6 — foundry depth (DONE 2026-07-09):** seven wishlist upgrades landed, with
  `UpgradeDef.Requires` tier-gating (locked rows render dim with "REQUIRES …"; TryBuy
  refuses) and a **scrolling foundry window** (8 visible rows, cursor-follow, "+ MORE"
  cues). New lines: **JETPACK II** (Kong+ruby, needs jetpack — double charge, harder
  climb), **AUTOCANNON III** (Mecha, needs II — twin-barrel spread at the same rate),
  **ION ENGINES III** (Godzilla+diamond, needs II — 1.75× thrust/speed, 0.2/s burn),
  **DEFLECTOR SHIELD** (Mecha+diamond — eats one impact, 8s recharge, pulsing cyan halo
  while charged, knockback still applies), **ORE MAGNET** (Kong — pickup sweep 4→16 px),
  **POD DAMPENERS** (Sandworm — roverless drop pods no longer cost health), **ROVER
  ARMORY** (Sandworm — every drop deploys with a pistol + 90 rounds). Gem sinks: ruby,
  diamond ×2, sapphire, crystal now all have foundry demand. 8 new SimTest checks (twin
  bolts, tier-3 engines, shield absorb/recharge, prerequisite gating). NOTE: the pixel font
  also lacks the apostrophe — UI strings must avoid ' as well as % and em-dash.

- **Phase 7 — orbital integration + fidelity (DONE 2026-07-09):** the mothership became a
  **circular ring station** (procedural 48px texture: hull ring, four spokes, glazed hub,
  cardinal warning stripes, docking notch — spins slowly, chasing running lights) and it now
  has a **real position in the planet view**: it parks at `Session.StationPos`
  (`OrbitAltitude` 480px above the surface at the spawn bearing) and hangs there all visit.
  **The rover departs from it** — descents start at the station in orbit, camera zoomed to
  1.5 easing to play zoom on the way down (140 px/s sink, A/D 175 px/s). **Escape is a real
  rendezvous**: the liftoff cinematic hands over at 1.2s to a manual ascent (`_ascending` —
  climb auto-eases to a stop at orbit altitude, A/D steers 220 px/s laterally, then a
  650 px/s docking-computer glide guarantees the rendezvous; contact = dock = the old
  FinishLaunch). **Seamless loading**: `BuildSessionWorld` (worldgen + seeding + pre-settle)
  was extracted static and is **prefetched on a background thread** while the ship loiters
  near a planet, so pressing Enter usually lands instantly; a 0.6s white transition flash
  masks the one unavoidable coordinate/scale cut in each direction. **System view fidelity**:
  3-layer parallax starfield with tinted stars, soft nebula blobs, sun flare rays + hot
  core, and planets with atmosphere halos, drifting hashed surface blotches, two-step
  terminators, and polar highlights. 2 new SimTest checks (background build off-thread,
  station altitude/clearance).

- **Phase 8 — No Man's Sky-style entry (DONE 2026-07-09):** the Enter-to-land prompt is
  gone. Planets are real places you fly to: an **approach zoom** pulls the camera onto the
  ship inside 650 px of any surface, and **flying into the upper atmosphere IS the
  transition** (`SpaceSim.AtmosphereContact`, EntryRange 18 px) — planets are no longer
  solid in space; only the shard-locked Rift keeps its storm wall (`SpaceSim.RiftLocked`).
  Entry drops you into a new **parking-orbit state** (`_orbiting`): the world is live below
  a wide orbital shot (zoom 0.53 framing station + curved surface), **A/D slides the orbit**
  around the planet to pick a drop site, **SPACE launches the rover** (consumption/pod
  penalty moved here from the old prompt), **W breaks orbit** back to space. Quitting from
  orbit discards the unentered world (no RunSave). Prefetch now triggers at 900 px on
  approach, so entry is seamless. **Planet discs are real terrain**: each disc is rasterized
  from its cached survey world (`Survey.WorldFor` — thread-safe, warmed by a boot task;
  `BuildPlanetPreview` samples tiles polar-wise: transparent sky above the limb = mountain
  silhouettes, caves read as dark rock), so the map disc genuinely resembles the world you
  land on. 5 SimTest checks updated/added for the entry model. `DM_ORBIT=<planet-id>` boots
  straight into the parking orbit for tooling.
  **Phase 8b riders (2026-07-09):** the planet view shows the mothership's **side profile**
  (procedural: deck hull, glass command dome, engine pods, masts, keel docking bay — the
  top-down ring stays in the system view); entry **arrives at the bearing you flew in on**
  and the ship **automatically glides down** into its parking orbit
  (`Session.OrbitEntryOffset` 420 px decaying at 340 px/s); orbit controls are
  **LEFT/RIGHT orbit, ENTER launch lander, SPACE leave planet**; loading hitch fixes —
  prefetch kicks off at 2200 px (build always beats a full-throttle run-in) and disc
  previews rasterize at most one per frame.
  **Phase 8c riders (2026-07-09):** descent zoom is altitude-driven (wide 0.72 while high —
  you can see where you're steering — closing to play zoom only near the ground);
  touchdown gouges a **small crater** (meteor rules: soft tiles only) and leaves the
  **broken rover as wreckage** (`Session.RoverWreck` — listing scorched shell, sheared
  skid, smoke wisp; cosmetic, not in the run save); the **parking orbit rose to 700 px**
  and the station **drifts around the planet** (`Session.StationDriftRate` 0.0035 rad/s)
  from the moment the rover drops — surface, descent, and ascent all advance it, so the
  return rocket is a true moving rendezvous (the ascent glide tracks it); a cyan **SHIP
  edge-arrow** points to the station whenever it's out of frame in the planet view; the
  solar map labels every planet with **name + range (KM = px/10)** and draws **accent-
  colored edge arrows with name + range** for off-screen worlds.
  Follow-ups (same day): the SHIP indicator shrank to a stubby wide chevron hugging the
  screen edge (margin 14, no text); rocket liftoff is a slow heave (65 px/s² for 2.2s,
  thrust carried into the ascent so there's no velocity jump, cruise capped 300);
  **orbit-view performance**: `Renderer.DrawWorld` gained a low-detail mode below zoom 0.9 —
  one flat jittered quad per tile (atlas/rims/decor are sub-pixel there) and interior rings
  under r=60 skipped — cutting the orbital draw load by roughly an order of magnitude.
  Round 2 (the real culprit): `Cells.Draw` and `Cells.AddLights` scanned the FULL cell grid
  inside the view radius — millions of array reads at orbital radii, plus a lightmap blit
  per surfaced lava cell. Both now take a `stride` LOD (zoom < 0.9 → 3, < 0.55 → 6: every
  Nth cell on both axes at N× size, deep rows cut like the tile LOD). Also added a
  permanent **FPS overlay** top-right on every screen — real rendered FPS (fixed-step
  MonoGame drops Draws when frames overrun, so this is the stutter number) plus smoothed
  UPD/DRW CPU ms to attribute blame (Update/Draw are wrapped by thin timing shims around
  `UpdateFrame`/`DrawFrame`).

- **Phase 9 — foundry wishlist completion (DONE 2026-07-09):** the three meaningful
  remaining wishlist slots landed. **GEO SCANNER** (Mecha soul + pure silver + crystal):
  HUD edge-arrows with ranges (in tiles, "M") to the nearest **fuel deposit**, the planet's
  **signature nav-core ore**, and the **titan** — powered by `Systems/Scanner.FindNearest`
  (ring-band tile sweep, refreshed on a 1.5s timer, ~20k tiles per 620px fix; pure/static,
  headless-tested) and the shared `DrawEdgeArrow` chevron language. **COMBAT PLATING**
  (Kong soul + pure iron/platinum): 30% off all incoming damage via
  `Player.DamageTakenMultiplier`, multiplicative with crafted armor (0.42× stacked).
  **SUPPLY CACHE** (Godzilla soul + pure gold/coal): every rover deploys with 2 poultices,
  40 blocks, and a sentry. Foundry is now 17 lines. 8 new SimTest checks.
  Dropped from the wishlist as non-features: cargo hold capacity (no cap exists — adding
  one would only annoy), sentry capacity (no cap exists), further pickaxe tiers (crafted
  tiers already cover the curve).

- **Phase 10 — wishlist finale (DONE 2026-07-09):** the last wishlist ideas plus a tier-III
  batch. Foundry (now 22 lines): **JETPACK III** (needs II — 7.8s charge, 190 px/s climb),
  **AEGIS CAPACITOR** (needs shield — recharge 8s → 4s via `SpaceSim.ShieldTier`),
  **ORE MAGNET II** (reach 16 → 30 px, `Player.PickupReach`), **O2 RESERVES II** (air ×2,
  replaces tier I's ×1.5), **HULL PLATING II** (7 → 9 pips). And the **rover loadout menu**
  (`L` in orbit, `src/Space/Loadouts.cs`): per-drop supply kits priced in cargo (no souls) —
  Ammo/Med/Demo/Builder/Sentry packs — stack in a pending manifest shown in the orbit HUD
  and pay out into the pack when the rover launches (manifest survives breaking orbit;
  cargo already spent). `DM_LOADOUT=1` opens it on arrival for tooling. 9 new SimTest
  checks (tier curves, shield recharge, loadout economy). The §0 upgrade wishlist is now
  fully resolved — every idea is either shipped or explicitly dropped with reasoning.

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
