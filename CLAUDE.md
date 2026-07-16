# DwarfMiner — notes for Claude sessions

## ⚠️ RULE #1 — end every finished change with the run command

**Every reply that finishes a code change MUST end with the current run command line**
(format below). No exceptions, no "same command as before" — print the full line again.
The user's `run` launcher executes the NEWEST matching line from the session transcript;
if you don't print it, the user keeps launching a stale command and your change is
untestable from their side (this happened: DM_ERUPTSHOW was added and never printed, and
several playtests silently ran without it).

Triggers — print the line when ANY of these happened since you last printed it:
- you finished/built any code change,
- you added, removed, or renamed a `DM_*` variable,
- the right variable set for testing the current work changed,
- you set up or switched a worktree.

## Run command for testing (machine-parsed — keep the exact format)

Whenever you set up a worktree, finish a change, or the way to test changes, print the
run command as a single line in exactly this shape:

    cd <absolute checkout path> && DM_VAR=value DM_OTHER=1 dotnet bin/<Config>/net8.0/DwarfMiner.dll

Rules:

- Default to `DM_GOD=1 DM_FLY=1 DM_AUTOSTART=debug` when applicable — i.e. unless the
  change specifically needs death, normal movement, or a different start scenario to
  be tested (then drop or replace only the conflicting variable).
- Additionally include every other `DM_*` environment variable needed to test the
  current change.
- When you add a new `DM_*` test variable — or the right set of variables changes —
  print the updated command again in the same format. The newest printed command wins.
- Keep it to one line: absolute `cd` path first, then the `DM_*` assignments, then
  `dotnet …DwarfMiner.dll` last. The user's `run` launcher greps session transcripts
  for the newest line matching this format and executes it verbatim, so a command in
  any other shape makes the change untestable from the user's side.

**FORBIDDEN VARIABLES — NEVER include these:**
- `DM_TITLE` — Window title is auto-derived from `.git` at runtime. Including it breaks the launcher.
  Never print it. If you see it in a past command, that command is stale.

## Game window title (identifies which tree is running)

The game titles its own window `DwarfMiner | <branch> | <worktree>` (e.g.
`DwarfMiner | noita-sim | main`, `DwarfMiner | liquid-perf | liquid-perf`) so the user
can tell test builds apart. This needs no environment variable and nothing in the run
command — `Game1.TitleBar()` walks up from the binary to the checkout's `.git` entry and
reads the tree and branch off it at startup.

- A linked worktree (`.git` file) reports its folder name; the primary checkout (`.git`
  directory) reports `main`, since its folder is named after the repo.
- **CRITICAL: Do NOT include DM_TITLE in the run command.** The window title is auto-derived
  and adding DM_TITLE will break the launcher.
- If the branch you are on titles its window some other way, bring `TitleBar()` over as
  part of your change.

## Common test environment variables

When testing specific features, include the relevant `DM_*` variables. Variable names
below are verified against the code — grep `GetEnvironmentVariable` before inventing or
renaming one, and update this list when you add a hook:

- **Volcano/eruption demo**: `DM_ERUPTSHOW=1` (spawns on the volcano, zooms out to max,
  auto-erupts ~5s in — the whole show in one variable). Granular pieces: `DM_VOLCANO=1`
  (volcano-flank spawn only), `DM_ERUPT=<seconds>` (schedule an eruption anywhere).
- **Perf profiling**: `DM_PERF=1` (per-phase profiler; `DM_NOWARM=1` skips prewarm)
- **Liquid effects**: ON by default — `DM_LIQRT=0` / `DM_LIQFX=0` DISABLE the liquid RT /
  metaball shader (setting them to 1 does nothing)
- **Debug features**: `DM_DEBUG=1`, `DM_AUTOFIRE=1`, `DM_CRAFT=1`, `DM_JETTEST=1`,
  `DM_SWIM=1`, `DM_RAIN=1`, `DM_WARREN=1`, `DM_CITY=1`

Check game code and commit history for the full set of available test variables.
