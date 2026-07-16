# Performance: the --perf harness, pacing, liquid draw

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

Perf pass done 2026-07-12 (branch noita-sim): fixed a critical `Cells.Enqueue` infinite-recursion-into-noop bug (from the HashSet→list active-set refactor) that silently froze the ENTIRE cell sim — sand/water/lava never ticked. Then: Particles swap-remove (mass die-off worst tick 102ms→4.8ms), Planet.Index/neighbour fast-path wrap (skip double modulo), Physics flood-fill HashSets→generation-stamped arrays, analytic polar transform in Renderer.DrawWorld + Cells.Draw (killed per-tile/per-cell Atan2 + normalize).

**Lasting tool**: `dotnet run -c Release -- --perf` (src/Systems/PerfTest.cs) — headless timing harness: 1.8× giant world, 30-crater meteor storm, 6× r600 earthquakes, 32k-particle die-off. Use it for before/after numbers on any future perf work.

Mass-break spike fixed 2026-07-12 (second pass): worst combined tick 39ms→~23ms on the 30-crater storm. Levers: (a) TryMoveTo skips dest wakes except Water/Lava (only water→sleeping-lava quench actually needs arrival wakes) and skips source wakes on intermediate hops of a multi-row fall / lateral slide (transited cells are net-unchanged); (b) Physics SettleBudget=10000/pass + CrumbleBudget=400/tick spill to later ticks; (c) dirty set + rest set → stamp arrays; (d) flood stack carries (x,y) — no UnIndex per pop; (e) Planet.ReinforcedCount==0 early-outs the per-flood-tile halo probe; (f) CollapseRegion sorts by raw index (ring offsets monotonic ⇒ same order); (g) Cells.UnIdx block table. Remaining cost is genuine grain sim (~60k active cells); next lever would be threading or time-slicing Cells.Update itself — feel risk, not taken. Watch in playtests: sand piles should still settle/slip naturally (arrival wakes removed makes piles marginally stiffer).

2026-07-13 (lighting session) hard-won pacing lessons: (1) **Texture2D.SetData on a texture the GPU sampled last frame = full pipeline sync stall on macOS GL** — CPU timers show nothing (upd+drw ~12ms) yet FPS collapses to ~23; double-buffer any per-frame-uploaded texture (see LightGrid._tex[2]). (2) **DM_AUTOSTART skips the boot load screen, so the survey warm-up (full world gen per planet) contends with live play for the first ~8s** — any FPS reading in that window is garbage; measure steady-state with `DM_FPSLOG=1` (per-second console `[fps]` lines) instead of screenshot-cropping the HUD counter. (3) Diagnostics added: `DM_LIGHTPERF=1` (light-pass component ms), `DM_NOLIGHT=1` (skip light+bloom → fullbright), `DM_NORT=1` (bypass virtual-res scene RT; breaks resize scaling).

Related: [[terraria-lighting]], [[overworld-roadmap]] (parallel Claude session auto-commits to this repo — expect files to change on disk mid-session; my edits get auto-committed too).

2026-07-15 perf pass (lander drop / effect storms / water worlds), all verified live:
**New tooling** — `DM_PERF=1`: per-phase frame attribution (src/Systems/FramePerf.cs; [perf]
mean/worst ms per system + [cnt] active-cell/GC line each second, ActiveBreakdown() names
the material hogging the wake set). `DM_NOWARM=1` skips survey warm-up for clean autostart
measurements. `[fps]` line now shows `updx N`/`SLOW` — MonoGame fixed-step catch-up state.
/tmp/dm_run.sh pattern: run game N seconds counted by [fps] lines, kill, grep the log.
**Water worlds (26→60 fps)** — DrawLiquids was per-cell: 4 IsBlocked + 1 SpriteBatch draw
per water cell over an ocean (~8.5 ms CPU + GPU vertex flood → 21-26 fps). Fixed with
run-length merging of interior liquid cells (one quad per run, capped so straight quads
never sag off the ring arc; boundary cells keep blob/waterline path) + same-ring fast path
(the Density rows of one tile ring share cell count, so radial neighbour = idx±n, one byte
read, same tile ⇒ no solid check). liqRT 8.5→1.7 ms, screenshot-verified seamless.
**Effect storms (20→60 fps)** — flamethrower storm collapsed to a vsync-quantized 20 fps
with only ~10 ms of measured CPU: LightGrid.Upload SetData stall (the known macOS GL trap)
came BACK under GPU load because 2 buffers at 30 Hz upload/60 Hz sample means the back
texture was sampled 1 frame ago. Ring is now 4 deep. Bisect trick: DM_NOLIGHT=1 isolated it.
**Lander drop** — liquid RT + metaball shader + fill blend now pre-warmed in LoadContent
(first activation used to land mid-descent: 81 ms hitch, now ≤16 once). Far-field throttle:
awake cells >700 px from CompactionExclusion tick at quarter rate (stay awake, 3-of-4 ticks
re-enqueue; headless = null = off, so --perf/--simtest unaffected); orbit sets focus to the
station, descent to the pod. Ocean descent holds 59-61 fps end-to-end.
**Traps** — perf harness must use PerfTest.HarnessWorld (pinned 1.8×), not DebugWorld (now
0.7×). Parallel `--perf` + live-FPS runs contend and pollute both. fps collapse with cheap
CPU timers = GPU stall at Present (swap happens outside _drawMs) — check updx/SLOW first.

2026-07-15 load-time pass (debug world "lags on load"): BuildSessionWorld now prints a
`[load] <id>: gen/seed/census/settle` line every build — measure, don't guess. Debug world
862ms → ~200ms via: (a) skip the 30 census-digest physics ticks when def.NoFauna; (b) the
pre-settle runs under the far-field throttle (new `Cells.SimFocus`, split from
CompactionExclusion so orbit/descent/load can throttle without enabling ambient sweeps)
and EXITS EARLY — cheap tick (<0.6ms = settled), cost plateau (12 ticks no 5% improvement
= steady simmer, pre-settling can't retire it), or 700ms wall budget; (c) boot prefetch of
the nearest planet is skipped under DM_AUTOSTART (it raced the autostart build for CPU).
LightGrid.PrewarmSky(planet) moved the one-time 2048-bearing sky scan off the first live
frame into StartNewRun/ResumeRun (first-second fps 40→55). **TRAP: do NOT cap
Game.MaxElapsedTime** — 50ms locked the vsync'd fixed-step loop at a permanent 30fps with
an idle CPU (updx 2 SLOW forever); leave it at the 500ms default. SimTest's "cancelled
settle skips the heavy half" became "not slower + time-boxed" (settle self-limits now).

2026-07-15 THE PACING SAGA (debug world "takes a while to not lag") — three stacked causes,
found by instrumenting rather than guessing; **big correction to earlier attributions**:
1. **InactiveSleepTime (THE big one)**: MonoGame sleeps 20ms EVERY tick while the window
   isn't frontmost → instant 24-33fps. This poisoned every backgrounded perf capture all
   session (the "flame-storm SetData stall" and "App Nap at 48s" theories were partly or
   wholly this — the 4-deep LightGrid ring and adaptive limiter stay, they're harmless and
   correct, but focus loss was the reproducible trigger). Now `InactiveSleepTime = Zero`.
2. **Fixed-step sleep overshoot**: macOS Thread.Sleep granularity made MonoGame's fixed-step
   pacer overshoot on CHEAP frames (cheap = worse fps, heavy = clean 60). Also discovered
   vsync (SynchronizeWithVerticalRetrace) never engages on this setup — uncapped hit 78-80.
   Fix: IsFixedTimeStep now FALSE by default (DM_FIXEDSTEP=1 restores); pacing is our own
   EndDraw limiter — sleep-to-3ms-then-spin with MEASURED Sleep(1) cost fallback (if sleeps
   degrade, it spins; decays back). dt clamped to 1/30 in UpdateFrame for hitches. "swap"
   phase in DM_PERF times base.EndDraw (the present).
3. **Load-time ignition churn**: water/oil/gas pocket bands are ABSOLUTE depths that overlap
   the lava flood on small worlds (lavaTop = Radius×LavaFillFrac counts the 142-ring sky
   headroom, so ember's flood is ~12 legacy tiles under the surface — its whole deep gas band
   was always drowned + burning at load). WorldGen now keeps water/oil above lavaTop+4S; gas
   gets (deep band OR hot-shell-just-above-lava) with 1S clearance — ember keeps its gas
   (simtest "ember seeds gas pockets" guards this). Debug world still simmers ~25-40k lava
   cells for the first minute (unfound residual ignition source — benign at 60fps now).
Debug world also NoDisasters: true (new PlanetDef knob; ambient meteor cadence ~20s + the
disaster clock silenced; debug-menu EVENTS tab still triggers everything).
Verified after all of it: debug 90s = 89× 59-60fps, flame storm + ocean descent all 58-61,
--simtest PASS, --perf at baseline. Measurement rule: game captures MUST set the window
frontmost OR rely on InactiveSleepTime=0 build; sequential runs only.

2026-07-15 LIQUID CHAOS PASS (branch `liquid-perf`; MERGED to noita-sim 2026-07-16 as
23cf14c — validated in a /tmp detached worktree first: simtest PASS, lavaprobe ×3 clean,
drain mean 1.57ms on merged tree; one Cells.cs conflict = noita-sim's rain-splash muting
rebuilt on the belowMat cache; merge also added DM_TITLE window naming per CLAUDE.md
(DM_TITLE since REMOVED 2026-07-16 — the title is self-derived now, see [[window-title]]);
worktree can be retired): targets "lake completely drains" chaos.
(a) **Stale-hop enqueues were the hidden active-set inflater**: TryMoveTo enqueued the dest
of EVERY hop of a multi-step fall/dispersion — a 16-step fall parked 15 dead indices per
cell per tick, each paying clear/shuffle/dispatch next tick. New `enqueue: false` arg on
transit hops; callers guarantee the final Enqueue. TRAP: sand's landed-hard path relied on
the hop enqueue for its angle-of-repose tick — needs explicit Enqueue(i) or piles stack
columns. (b) **Arrival wakes reaction-targeted**: blanket WakeNeighbors on every hop of
moving water/lava re-armed ≤15 cells/hop; now WakeReactiveNeighbors wakes only partners
(water→Lava for lava-side quench; lava/fire→Oil for oil-side ignite; gas never sleeps so
no wake). (c) occupancy-byte-first ordering in IsBlocked/TryMoveTo (mat read before
tile-solid fetch — pool/pile probes fail cheap). (d) TickLiquid below-neighbour resolved
once (buoyancy/fall/splash/slip/hemmed shared). (e) QuenchIfWet single-pass water+snow scan
(was 2× FindNeighbour per lava cell per tick). (f) **DrawLiquids batch-break fix**: liquid
fill pass is SpriteSortMode.Deferred, and Pixel↔blob texture alternation flushed the batch
per edge cell — thousands of one-quad draw calls on a draining lake. Blob stamps now
deferred to _blobOps (same pattern as lava's _hotOps) and flushed as one textured segment.
**TRAP (user-caught, fixed 3892fe3)**: LiquidFillBlend REPLACES RGB (only alpha accumulates)
and a blob stamps its cell's LiquidBody shimmer phase (sin(ang*17)) across its whole 3-cell
footprint even at ~0 alpha — flushing blobs AFTER quads smeared each edge cell's phase over
the interior rows beside it = "replicated shear lines" crawling along pool surfaces while
panning. Correct order: blobs FIRST, then quads (also deferred, _quadOps), then waterline —
~2 texture segments, restores the interleaved look. Any liquid-pass draw reorder must
reason about RGB last-wins, not just alpha coverage. **The blob order was NOT the user's
shear-line artifact though — real cause (pre-existing since the ocean run-merge pass,
fixed 73b5eb4): a pool wider than the view is ONE run cut at the view edge, so
run-relative maxRun chunking anchored every segment boundary + flat shimmer sample to
the CAMERA — panning re-anchored them cell-by-cell at different angles per tile ring =
crawling re-tiling shear lines. Fix: chunk boundaries on absolute multiples of maxRun
(`maxRun - WrapX(cx0+s, n) % maxRun`). Residual faint moving seam-rectangles remained =
the positional shimmer itself: mixed primitives (chunk samples vs per-cell vs blobs)
can never agree on a positionally-varying colour → 28537ed makes SUBMERGED body cells
strictly flat (LiquidBody shimmer:false; DrawLiquidCell passes openOut), shimmer lives
only on the surface row + airborne droplets. Lesson: static screenshots can't catch
camera-anchored artifacts — reason about what in a draw pass is view-relative; and a
body drawn from mixed primitive sizes must use a POSITION-INDEPENDENT colour.**
Texture restored per-pixel (c5da938, then d349b0b per user "remove the shimmer"):
liquid composite shader gained PsParams3 (RuntimeEffect WriteMgfx now takes psVec4s
per effect — terrain 2, liquid 3) = planet centre in RT px + world-per-px; final state:
NO shimmer anywhere (LiquidBody strictly flat, all sin bands deleted), WATER-ONLY
depth-colour gradient base-blue→navy via 16-tap coverage march (~12 tiles range),
water identified IN-SHADER by its flat base colour 46,90,178 (hardcoded in GLSL —
keep in sync with Cells.LiquidBody), strength in PsParams2.w (pools 1.0, hot field 0);
glints DELETED (be71a3a — user read their twinkle as "lake shimmer"); gradient deepened
same commit: FULL mix to near-black navy (6,14,40) over a 16-tile march (4 wpx/tap).
**SHELL TRAP: Bash cwd resets to the MAIN checkout after dead-dir/cd — every worktree
build/run MUST cd explicitly or you build and screenshot the OTHER session's code (bit
me twice).** **WORKTREE BRANCH TRAP (2026-07-16): something switched the liquid-perf
worktree's checked-out branch to the STALE `main` (a May-old pointer on the same
lineage) — a `git reset --hard` run there advanced MAIN, not liquid-perf, and edits
auto-committed onto main; recovered via checkout liquid-perf + soft-reset recommit +
`git branch -f main 2628d5c`. ALWAYS `git branch --show-current` before any
reset/commit in the worktree. dotnet can also vanish from PATH mid-session — use
`~/.dotnet/dotnet`.**
`--perf` now has a **lake-drain scenario** (breaches biggest lake into the lava sea, prints
active breakdown). Numbers: drain mean 2.97→2.15ms worst 7.7→6.6 (active 25k→16k, remainder
is ~12k genuinely-flowing water + ~5k same-tick ghosts ≈ floor), pre-settle −25%, meteor
aftermath −22%, quake −22%. Validation: --simtest has a PRE-EXISTING FAIL on "volcano:
throat is primed down to a deep magma chamber" (control worktree at base 027868d fails it
identically — not this branch); --lavaprobe small 1-5 tile escapes appear in BOTH branch and
control (quench-spit slosh, probe has no hard FAIL); --oceanprobe ALL OK. Auto-committer
sweeps WORKTREE branches too — descriptive commit needed `git reset --soft <base>` + recommit.

