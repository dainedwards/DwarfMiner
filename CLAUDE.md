# DwarfMiner — notes for Claude sessions

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

When YOU launch the game to test something, prefix the run command with `DM_NOFOCUS=1`. The run
is INVISIBLE: the process never activates (SDL's macOS background-app hint) and the window is
hidden outright, so nothing appears on screen, nothing steals focus, and nothing takes the
user's keyboard or mouse. Rendering continues into the scene render target, so `DM_AUTOSHOT`
and F12 screenshots work exactly as they do in a visible run — screenshots stay your way to
see a test run.

Never print it in a run command: a hidden window is unplayable, and the user's playtests need
a real one.

If a run ever logs `[nofocus] WARNING`, the hide stopped working — that build leaves a window
on screen with NO dock icon to hide or quit it, so kill the process and fix the hide before
launching again.

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
