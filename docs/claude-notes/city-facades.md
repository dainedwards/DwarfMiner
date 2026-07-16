# Straight city facades and the polar lattice

<!-- Folded out of Claude's memory into the repo 2026-07-16: CLAUDE.md is the single
     source of truth and links here. Notes are dated and historical by nature — trust
     the code over any line here, and correct the note when you find it stale. -->

User asked for straighter alien buildings 2026-07-15. **Root cause was NOT worldgen rounding**: an A/B with the new straightness SimTest showed the old floor-based rasterizer measured the same (wobble 4.1px, roughness 1.02px/row) as a nearest-line raster — the jaggedness is the polar grid itself. `ComputeTilesAt` = Round(2π·g) varies tiles-per-ring smoothly (~+2π per ring), so a fixed bearing's tile-space phase drifts every ring; tile centres jitter ±½ tile ring-to-ring and each 4px tile draws as its own quad → towers rendered as leaning sawtooth staircases. No grid-level rasterizer can fix that (hysteresis tried, useless — measure first!). Chord ≈ TileSize within ±0.5% everywhere (n = Round(2π·(RingMin+r+0.5))), which is what makes the fix below gap-free.

The fix (three coupled parts, must agree exactly):
1. **WorldGen.BuildTower is column-major**: hull width quantised to whole tiles (`cols`, `halfW = cols·TileSize/2`), each ring maps logical column j → grid tile via `Round((ang + lat_j/ringRadius)/chordAng − 0.5)`, roles (door j∈{0,cols−1}, 2-col edge/windows, ladder spine j=midJ, slabs/furniture by dt=j−midJ) keyed off j so every tile sits ≤½ tile from its slot. Backfill `prevT+1..tj` covers the rare skipped grid tile (chord≠TileSize drift). Mast/crown/dome ride the same lattice.
2. **Planet.CityFacades** `(ang, halfW, footR, topR+7S margin)` recorded per tower; persisted at the END of Planet.WriteState/ReadState; **RunSave v22**.
3. **Renderer tile pass**: `SnapsToFacade(k)` (engineered + doors/ladder/furniture/beacon) → `Planet.FacadeSnapAngle(r, angle, ringRadius)` snaps the drawn bearing onto the tower's slot lattice (guard: bail if >0.62 tile from a slot). Visual-only — grid/collision unmoved, same order as the crust shader displaces natural silhouettes. TileAtlas already forces mask 0 / no carve for engineered kinds ([textures-and-crust](textures-and-crust.md)).

Result: SimTest "tower facades draw dead straight" measures DRAWN geometry via FacadeSnapAngle — 0.64px wobble / 0.017px-per-row roughness over 78 storeys (was 4.2 / 1.07). Screenshot-verified (DM_AUTOSTART=city DM_CITY=1 DM_AUTOSHOT=10; macOS has no `timeout` — background the run + sleep + kill).

Fidelity question (user: "consider smaller blocks"): declined halving TileSize 4→2 — 4× tiles (~3.1M giant world), 4× settle/lighting/render on an already tight perf budget ([performance](performance.md)), plus ring-unit constants (StandardRings/RingMin/SkyHeadroom) and save geometry all bake in TileSize. The facade lattice removed the main visual driver; residual world texture already gets sub-tile detail from the carve shader + cell sim.

2026-07-15 wider-towers / bridges / one-piece-doors wave:
- **Towers widened** (user: room for furniture + citizens): small 30-42 / mid 30-46 / spire 22-32 halfWidthPx (was 22-32/20-32/15-24); furniture hash gate 150→260 of 1023. TRAP: wider hulls broke `FacadeSnapAngle`'s nearest-slot round — chord-vs-TileSize error grows with |lat|, edge slots went off by one → 4.2px sawtooth returned. Fix: snap candidates {0,+1,−1} verified by the generator's FORWARD map (slot whose `Round((fAng+slat/R)/chord−0.5)` equals THIS tile's index wins; no match → unsnapped). Wobble now 0.33px at 23 cols.
- **District centring row-aware**: lot shares planned up front (capital 60%), each district's centre demands `rowHalfAng = lots·96px/2/R` clearance from mountains + the drop bearing + every avoid entry for the first 90 tries (relaxes to the old margins after) — long rows used to split into fragments over mid-row skips, failing "one capital plus smaller towns".
- **Street bridges**: per-district `rowTowers` list; every edge-to-edge gap ≤120px gets an anchored AlienAlloy deck at ring surfaceR (= door threshold level) + 4·S rings of Sky headroom above (IsAnchored guard protects doors/hull/furniture during clearing). Anchored = disasters can't eat the crossing. Min street gap 12→16px (quantization + slab ledges could fuse neighbours' ledges angularly). SimTest "street decks bridge the tower gaps".
- **One-piece doors**: `Planet.SetDoorRun(x, y, to)` walks the leaf RING BY RING re-matching the door column with ±1 slack — the old same-angle world-space walk missed drifted columns and stranded upper tiles closed ("bunch of small pieces"). Game1.TryToggleDoor + Creature door open/close now call it. SimTest "a door leaf opens as ONE piece".
- **Floor connectivity (same day, user: "floors aren't connected, breaks the rigid body")**: the stair gap used to hug the WALL (`dt*gapSide < span-4`) — on the widened towers that left every other floor's outer strip a free-floating shelf, and the 3-wide climb channel severed all slabs from the ladder spine, so a toppling tower shattered into strands (4 bodies) and shelves rained once anything woke settle. Fix: gap is now a 2-col hole BESIDE the channel (`dt*gapSide is 2 or 3`, alternating sides = zig-zag kept, slabs always reach both walls), and at slab rows the channel's flanking columns carry LADDER landings instead of Sky (passable → climb unchanged, but they lace spine↔slabs). Furniture skips the gap columns. Topple now 2 bodies/788 cells; SimTest "tower interior is one connected structure" floods 3 real towers (≥92% reachable).
- **ENGINE BUG exposed + fixed — Physics.MarkDirty ring drift**: neighbour marks used raw index ±1 across rings, but tiles-per-ring grows ~+6/ring, so at bearings far from angle 0 the "tile above" is 2-3 indices away → the region above a break was NEVER woken → a tower severed at the street on the far side of the planet hung in the sky uncondemned (SimTest topple test only ever passed because the old capital spawned near angle 0). Fix: map neighbouring rings' columns by ANGLE (`t0 = (y+0.5)/n·nn`) then mark t0±1. Diagnosed with `--toppleprobe` (kept in Program.cs) + temporary DM_TOPDBG prints in Settle (removed).



## Moved from the old noita-sim note (2026-07-16)

### 2026-07-14 blast + ladder pass

- **Tower ladder shaft fix**: the center ladder (dt==0) was a 1-tile hole through a 2-tile-thick floor slab — the flanking slab tiles (dt=+/-1) walled the dwarf in so floors above blocked the climb. WorldGen.BuildTower now clears dt in {-1,0,1} to Sky full-height (before the slab/furniture fill) → clean 3-wide open climb channel, ladder centered, alloy wall backing kept via SetWall. cityprobe Ladder ~2121.

### 2026-07-14 doors / hose-rework / clouds / deep-caves batch

- **Doors one piece**: Renderer DoorClosed now draws a continuous leaf (stiles + panel lines full-height) with the brass handle drawn ONCE — only on the bottom tile of the run (innerK not a door) — so a tall door reads as one door, not stacked panels each with a knob. Functionally already toggled as a whole run (SetDoorRun). Aliens already open doors: CanUseDoors (Civilian/Peacekeeper/Lizardman/Marauder/Pyro) is wired into GroundMove.

