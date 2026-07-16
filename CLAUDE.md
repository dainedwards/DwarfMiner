# DwarfMiner — notes for Claude sessions

## ⚠️ RULE #0 — notes go in THESE FILES, never in memory

**This repo is the single source of truth. Do NOT write project knowledge to Claude's memory
directory — no memory files, no MEMORY.md entries.** Memory was folded into the repo on
2026-07-16 precisely to end the split; anything you save there is a second copy that goes stale
and that the user cannot read, review, or diff.

When you learn something worth keeping:

- **Operational rules** — how to run, launch, test, what never to do → this file, in the
  matching section. Keep it short: every session pays for this file in full.
- **Everything else** — how a system works, why it is built that way, contracts, traps, dead
  ends already tried → the matching **Linked Note** in `docs/claude-notes/` (index at the
  bottom). Add a new note only for a genuinely new system, and link it from the index.
- Fix notes in place when you find them stale, and delete what is superseded. A note is a
  living document, not a log — don't append a dated entry when editing the existing line says
  it better.
- Notes are dated and historical by nature: **trust the code over any line in them.**

## ⚠️ RULE #1 — print the run command whenever it CHANGES (and only then)

The user's `run` launcher executes the NEWEST matching line from the session transcript.
Print the full run command line (format below) when the correct command is different
from the last one printed — and NOT otherwise (don't repeat an unchanged line after
every change; that's noise).

Print it when ANY of these make the command differ from the last printed line:
- you added, removed, or renamed a `DM_*` variable relevant to testing the current work,
- the right variable set / start scenario for testing changed,
- the build config or checkout path changed (e.g. you set up or switched a worktree),
- no line has been printed yet THIS SESSION (the launcher may otherwise pick up a stale
  command from an old transcript — this happened: DM_ERUPTSHOW was added and never
  printed, and several playtests silently ran without it).

## Run command for testing (machine-parsed — keep the exact format)

Whenever the run command changes (Rule #1), print it as a single line in exactly this
shape — the `RUNCMD:` marker included:

    RUNCMD: cd <absolute checkout path> && DM_VAR=value DM_OTHER=1 dotnet bin/<Config>/net8.0/DwarfMiner.dll

Rules:

- **The `RUNCMD:` prefix is REQUIRED and RESERVED.** The launcher only honours marked
  lines (picked by entry timestamp across all sessions), because bare command-shaped
  text also lands in transcripts via tool payloads, memory files, quoted output, and
  the launcher's own "launched:" echo — unmarked commands used to let stale echoes win
  forever. Never write `RUNCMD:` followed by a real-path command unless you INTEND to
  set the run command (in discussion, break the marker up or use a placeholder path).
- Default to `DM_GOD=1 DM_FLY=1 DM_AUTOSTART=debug` when applicable — i.e. unless the
  change specifically needs death, normal movement, or a different start scenario to
  be tested (then drop or replace only the conflicting variable).
- Additionally include every other `DM_*` environment variable needed to test the
  current change.
- Keep it to one line: `RUNCMD:` first, absolute `cd` path, then the `DM_*`
  assignments, then `dotnet …DwarfMiner.dll` last. The newest marked command wins.

**FORBIDDEN VARIABLES — NEVER include these:**
- `DM_TITLE` — Window title is auto-derived from `.git` at runtime. Including it breaks the launcher.
  Never print it. If you see DM_TITLE in a command from the transcript (stale from an older session),
  that command is broken — print a corrected version without DM_TITLE to override it.
- `DM_NOFOCUS` — Claude-side only (see below). The user's own playtests must take focus, so
  this must never appear in a printed run command.

## Launching the game yourself (DM_NOFOCUS)

Prefix YOUR OWN launches with `DM_NOFOCUS=1`. The run is invisible end to end — no window, no
focus theft, no flash, nothing taking the user's keyboard or mouse — while rendering carries on
into the scene render target, so `DM_AUTOSHOT` and F12 screenshots work exactly as in a visible
run. Screenshots stay your way to see a test run.

- **Never print it in a run command**: a hidden window is unplayable, and playtests need a real one.
- If a run logs `[nofocus] WARNING`, the hide broke: that build leaves a window on screen with
  no dock icon to hide or quit it. Kill the process and fix it before launching again.
- **Verifying "it's hidden" needs the window server, not a screenshot** — `screencapture` only
  sees the current macOS Space, and it has produced a false all-clear twice.

→ [windowing-and-input](docs/claude-notes/windowing-and-input.md) for the mechanism, the
verification recipe, and the unfocused-input gate.

## Never `pkill -f DwarfMiner.dll`

Another Claude session codes this repo in parallel and the user playtests between turns, so two
or three DwarfMiners are often running. `kill <your pid>` only. For the same reason `pgrep -f
DwarfMiner.dll` often returns several lines — pass ONE pid to osascript or it dies on a syntax
error, and check for a second pid before assuming a stray window is yours.

## When something crashes, read the crash report first

`~/Library/Logs/DiagnosticReports/*.ips` — `EXC_CRASH (SIGABRT)` + `JIT_RngChkFail` is an
unhandled `IndexOutOfRangeException`. The managed stack is NOT in the .ips; capture the game's
stdout for it. Check timestamps and pids against processes you never touched before believing
any correlation — `kill`/`pkill` exit cleanly (143) and never write a report. Worlds are
TIME-SEEDED, so prefer a probe that sweeps every case over soaking the GUI.

→ [debugging-and-crashes](docs/claude-notes/debugging-and-crashes.md) for the triage recipe,
the rain-crash worked example, and the probe family.

## Game window title (identifies which tree is running)

The game titles its own window `DwarfMiner | <branch> | <worktree>` (e.g.
`DwarfMiner | noita-sim | main`) so the user can tell test builds apart. It needs no
environment variable and nothing in the run command — `Game1.TitleBar()` derives it from the
checkout's `.git` at startup. If the branch you are on titles its window some other way, bring
`TitleBar()` over as part of your change.

→ [windowing-and-input](docs/claude-notes/windowing-and-input.md#window-title-internals).

## Common test environment variables

Verified against the code — grep `GetEnvironmentVariable` before inventing or renaming one, and
update this list when you add a hook:

- **Volcano/eruption demo**: `DM_ERUPTSHOW=1` (spawns on the volcano, zooms out to max,
  auto-erupts ~5s in — the whole show in one variable). Granular pieces: `DM_VOLCANO=1`
  (volcano-flank spawn only), `DM_ERUPT=<seconds>` (schedule an eruption anywhere).
- **Perf profiling**: `DM_PERF=1` (per-phase profiler; `DM_NOWARM=1` skips prewarm)
- **Liquid effects**: ON by default — `DM_LIQRT=0` / `DM_LIQFX=0` DISABLE the liquid RT /
  metaball shader (setting them to 1 does nothing)
- **Debug features**: `DM_DEBUG=1`, `DM_AUTOFIRE=1`, `DM_CRAFT=1`, `DM_JETTEST=1`,
  `DM_SWIM=1`, `DM_RAIN=1`, `DM_WARREN=1`, `DM_CITY=1`

Check game code and commit history for the full set.

## Working alongside the other session

Another Claude session codes this repo in parallel, and an auto-committer commits DURING a
session:

- `git status` clean + HEAD holding your own edits is NORMAL — a HEAD worktree is NOT a
  baseline for an A/B, and stash-based control runs get defeated. Branch or copy to compare.
- Before editing a shared system, `git log -p <file>` for their refinements, and merge
  `noita-sim` in first.
- SimTest carries flaky/in-progress failures from the other session — compare failure sets at
  the SAME commit before calling something a regression.
- `dotnet run` deadlocks against the auto-committer: run the built dll (`dotnet
  bin/<Config>/net8.0/DwarfMiner.dll --simtest`). macOS has no `timeout` — background the run,
  `sleep`, then `kill <pid>`.

## Linked Notes (docs/claude-notes/)

Per-system detail lives here — contracts, traps, and the reasons the code can't explain. **Read
the one that covers what you are about to touch, and write what you learn back into it (Rule
#0).**

- **[worldgen-caves-and-lava](docs/claude-notes/worldgen-caves-and-lava.md)** — worm tunnels,
  strata, water/ocean worlds, basin containment, lava barriers, volcanoes, quake cave-ins, ore
  and flora scatters. Probes: `--strataprobe --lakeprobe --oceanprobe --lavaprobe --geomprobe`.
  *Before you touch it:* `Planet.Radius` COUNTS THE SKY, and `BuildSessionWorld` is TIME-SEEDED.
- **[cells-and-materials](docs/claude-notes/cells-and-materials.md)** — the cell sim core:
  flying cells, oil, snow, acid, smoke, bubbles, lightning conduction, the ambient sweep
  (ceiling drips, moss). *Flying grains need `FlyMaxOutward` or they hang in the sky as
  "floating pixels".*
- **[fire-and-hoses](docs/claude-notes/fire-and-hoses.md)** — fire as a material, the burn fuse,
  the `BurningTiles` registry rewrite, and the flamethrower/acid-spewer saga. Long, and worth
  it before touching either weapon: ~70 tuning rounds, several dead ends named as dead ends.
- **[liquids-and-water](docs/claude-notes/liquids-and-water.md)** — liquid RT + metaball
  cohesion, flat body colour, waterline, plunge/dispersion, rain pooling and joining water
  bodies, water-surface interaction.
- **[weather-and-trees](docs/claude-notes/weather-and-trees.md)** — clouds v2, rain/snow by
  biome, falling leaves, and the tree ecosystem (roots, regrowth, branches, biome identity,
  oases, vegetation scatter).
- **[creatures-and-combat](docs/claude-notes/creatures-and-combat.md)** — bandits, sea monsters,
  the flyer/caster/mortar enemies (Quillwing, Warpwisp, Thornback), lizardmen, player weapons,
  blast falloff, the AIR meter, jump/jet controls (Space-only), city combat buffs.
- **[titans](docs/claude-notes/titans.md)** — siege (kick/smash), weakpoints, riding, shake-off,
  grapple + rope, fight camera, pulverize.
- **[pixel-look](docs/claude-notes/pixel-look.md)** — the grain ladder: particle size caps, cell
  draw padding, `Density` 4→8, the pixel-grid world RT, motion smear, stride/LOD.
- **[ore-and-gems](docs/claude-notes/ore-and-gems.md)** — gold/silver/platinum balance, gem
  rarity and embedding, host-rock blending, depth gradients.
- **[overworld-and-planets](docs/claude-notes/overworld-and-planets.md)** — star map, campaign
  planets, cities, warrens, moons, The Hollow, spawn director, population census.
- **[performance](docs/claude-notes/performance.md)** — `--perf` harness, frame-pacing saga,
  LightGrid SetData stalls, liquid draw batching, far-field throttle.
- **[runtime-shader](docs/claude-notes/runtime-shader.md)** — the hand-written MGFX/GLSL effect
  built at runtime, and the Noita edge carve. *The posFixup epilogue is REQUIRED* — omit it and
  the whole world batch silently back-face-culls.
- **[textures-and-crust](docs/claude-notes/textures-and-crust.md)** — CC0 detail + palette
  remap, procedural fallback, additive crust, the 45° slope experiment that was backed out.
- **[lighting](docs/claude-notes/lighting.md)** — propagated LightGrid, sky-heightmap sun
  seeding, hero raycasts, elemental arms.
- **[rigid-bodies](docs/claude-notes/rigid-bodies.md)** — detach/tumble/re-stamp debris, corpse
  ragdolls, whole-structure topples, three resting-jitter lessons. Bodies are Cartesian.
- **[city-facades](docs/claude-notes/city-facades.md)** — why towers looked jagged (polar
  lattice drift, NOT the rasterizer), the facade lattice, the `MarkDirty` ring-drift bug.
- **[compaction-and-tiles](docs/claude-notes/compaction-and-tiles.md)** — 4px tiles with legacy
  8px authoring units, the dust economy, the compaction sweep's stranding traps.
- **[equipment-and-ui](docs/claude-notes/equipment-and-ui.md)** — character screen, equipment,
  crafting, jetpack, building blocks, hotbar.
- **[windowing-and-input](docs/claude-notes/windowing-and-input.md)** — DM_NOFOCUS mechanism,
  hidden-window verification, unfocused-input gate, window title internals.
- **[debugging-and-crashes](docs/claude-notes/debugging-and-crashes.md)** — crash-report triage,
  the rain crash, probe philosophy, verification traps that have actually bitten.
