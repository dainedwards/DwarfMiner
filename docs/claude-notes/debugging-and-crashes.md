# Debugging: crashes, probes and what fooled us

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

## Read the macOS crash reports before theorising

An "error" dialog, or a game that just vanishes, is almost never whatever happened right
before it. `~/Library/Logs/DiagnosticReports/*.ips` is the record.

- `EXC_CRASH (SIGABRT)` + `JIT_RngChkFail` → `abort()` = an unhandled
  `IndexOutOfRangeException`. `PROCAbort`/`TerminateProcess` above it is just the runtime dying.
- The .ips does NOT carry the managed stack — JIT frames are unsymbolicated. **Capture the
  game's stdout** and read the exception there; that is where the file/line lives.
- Check report timestamps and pids against processes you never touched before believing any
  correlation. Several reports were from the user's own playtests and from a parallel session.
- `kill -TERM` / `pkill` exit the game CLEANLY (status 143) and never write a crash report. If
  you are blamed for a kill, this is how you check.

**Worked example (2026-07-16).** The user reported "an error every time you pkill". pkill was
innocent: every report going back a full day — across pids nobody had touched — was the same
`JIT_RngChkFail`. It was rain (below), crashing sessions at random for days. Attributing a
crash to the last thing that happened is exactly how it hid for so long.

## The rain crash, and why `--rainprobe` exists

`SpawnDirector.FindSurfaceSpawn` returns a fallback point at `(radius + 24)` tiles when a
column holds no solid ground. `Planet.Radius = RingMin + Rings`, so `Cells.WorldToCell` maps
that to `cy = Height + 24*Density` — above the outermost cell row. `SettleToSurface` walked it
and `InnerCell` indexed `_cellsAt[cy-1]` off the end. One cloud drifting over one groundless
column aborted the process, which is why it read as a random crash 40s–17min into a session.

Fix: `SettleToSurface` clamps its start row (`cy = Min(cy, Height-1)` — `WorldToCell` already
builds cx in the CLAMPED row's angle space, so the pair stays consistent), plus a symmetric
high-side guard in `InnerCell`. It guarded `cy <= 0` only, while `Place`/`IsBlocked`/
`OuterCellCount` all guarded both ends — that inconsistency WAS the bug.

`--rainprobe` sweeps every angle of debug/verdant/frost driving the real landing path. It
reproduced the crash deterministically on **frost** (4 groundless columns / 1600 angles, cy
3215 vs top row 3199) while debug and verdant showed 0 that seed.

**OPEN QUESTION:** why frost has groundless columns at all. The fix is at the consumer, so a
worldgen hole (or a legitimately bottomless column) would still be worth understanding.

## Probe philosophy

Worlds are TIME-SEEDED (`BuildSessionWorld`), so an in-game repro is a coin flip and a soak
proves little: 2 minutes of `DM_RAIN=1` never reproduced the rain crash, while sweeping the
whole ring in a probe pinned it in seconds. Prefer a probe that drives the real code path over
every angle/case to soaking the GUI and hoping.

The repo keeps a family of these in `Program.cs` — `--simtest --perf --rainprobe --lakeprobe
--oceanprobe --lavaprobe --strataprobe --geomprobe --cityprobe --acidprobe --toppleprobe`.
Add one when a bug is stochastic; it converts luck into a test.

## Verification traps that have actually bitten

- **A screenshot only sees the current macOS Space** — see
  [windowing-and-input](windowing-and-input.md). Twice a window was declared hidden while it
  was plainly on screen.
- **Always run the negative control.** A checker that reports "clean" is worthless until you
  have watched it report "dirty" on a known-bad case.
- **`dotnet run` deadlocks against the auto-committer** — run the built dll for probes/tests.
- **macOS has no `timeout`**: background the run, `sleep`, then `kill <pid>`.
