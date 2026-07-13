using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Tile collapse, two flavours per material class:
///   • Loose tiles (Dirt/Grass/Gravel/Snow/MossStone) crumble straight into the cell sim
///     once fully undercut — nothing solid below them, straight down or diagonally — the
///     tile vanishes and a half tile's worth of Material cells spawn in its place, then
///     flow with angle of repose. Diagonal support means dirt tunnels arch instead of
///     raining the entire layer down.
///   • Stone-like tiles (Stone, ores) use a connectivity check: flood-fill the connected
///     non-anchored solid region and if no path reaches an anchored tile (PlanetCore/Core/
///     Support) or the world edge within budget, the *whole region* crumbles into the cell
///     sim as dust tagged with each tile's kind — it rains down as granular debris under
///     the velocity sim and pays out the tile's drop on pickup, same as loose ground.
///     Anchored kinds never move. The size budget only saves regions that touch the crust
///     (underground, the backdrop wall is inferred to carry big spans); fully airborne
///     regions — an undercut mountain, a sky-built platform — have nothing behind them
///     and collapse no matter how big.
///
/// Driven by a dirty queue so we only revisit recently disturbed tiles. Within a single
/// settle pass, anchored regions are cached so repeated floods short-circuit.
/// </summary>
public sealed class Physics
{
    private readonly Planet _planet;
    private readonly Cells _cells;
    // Dirty set as a stamp array + list (same pattern as the flood stamps below): a mass
    // break marks ~9 dirty tiles per broken tile, so HashSet hashing made both MarkDirty and
    // the settle drain part of the break-frame hitch. Stamp == _dirtyGen means already listed.
    private readonly int[] _dirtyStamp;
    private int _dirtyGen = 1;
    private List<int> _dirtyList = new();
    /// <summary>Swap partner for <see cref="_dirtyList"/> — the pass processes one list by
    /// cursor while marks accumulate in the other. No Queue round-trip: spilling an
    /// over-budget tail is a sequential re-stamp of the list remainder.</summary>
    private List<int> _dirtyWork = new();
    /// <summary>Max tiles one settle pass may dequeue; the remainder rolls to the next settle
    /// tick (50ms later). Bounds the frame spike after a mass break — condemned regions get
    /// a 0.35s tremble anyway, so a tick or two of extra detection latency is invisible.
    /// Normal play never comes close (a whole meteor marks ~3k entries).</summary>
    public const int SettleBudget = 10000;

    /// <summary>Max tiles crumbled per Update across all pending collapses. A giant condemned
    /// region's innermost ring can run 1000+ tiles; shattering them all in one tick spawns
    /// their dust (8 cells + a wake each) in one frame. Capped, the ring finishes over 2-3
    /// ticks — visually still one sweep, since rings already pace at 30ms. A meteor crater
    /// ring is ~60 tiles, so ordinary cave-ins never notice the cap.</summary>
    public const int CrumbleBudget = 400;
    // Flood-fill visited/anchored sets as generation-stamped arrays instead of HashSets:
    // membership is one array compare, "clear" is bumping the generation. The flood is the
    // innermost loop of cave-in detection and earthquakes re-flood thousands of tiles per
    // settle pass, so hashing here dominated disaster frames. _floodVisitList keeps the
    // current flood's tiles enumerable for promotion into the anchored stamp.
    private readonly int[] _anchorStamp;
    private readonly int[] _floodStamp;
    private int _anchorGen = 1;
    private int _floodGen = 1;
    private readonly List<int> _floodVisitList = new();
    // The stack carries (x, y) alongside the flat index so a pop doesn't pay an UnIndex
    // binary search — the visitor that pushed it already had the coordinates.
    private readonly Stack<(int idx, int x, int y)> _floodStack = new();
    private readonly List<int> _floodRegion = new();
    private float _settleAccum;
    public const float SettleInterval = 0.05f; // seconds between settle ticks
    /// <summary>Max collapsible region size for baseline rock (Stone, hardness 2). Regions
    /// bigger than their strength-derived budget are treated as supported.</summary>
    public const int StoneCollapseBudget = 192;
    /// <summary>Budget shaved off per hardness tier above Stone — stronger material holds
    /// larger unsupported spans, so only smaller pockets of it can cave in.</summary>
    public const int BudgetPerHardness = 32;
    /// <summary>Obsidian is brittle: any unsupported span bigger than this caves in.</summary>
    public const int ObsidianCollapseBudget = 24;
    /// <summary>Hard flood cap for regions entirely above the crust. Airborne rock gets no
    /// size-based reprieve — this only bounds the flood's worst-case work (the biggest
    /// giant-world massifs run ~28-32k tiles on the 4-px grid).</summary>
    public const int SkyRegionCap = 80000;
    private int _regionBudgetSum;
    /// <summary>Whether the current flood region reaches at or below the crust line
    /// (baseline surface + margin). Only such regions earn the too-big-to-fall valve.</summary>
    private bool _regionTouchesCrust;

    /// <summary>Seconds a condemned region trembles before it starts crumbling — the
    /// Terraria-style warning window for the player to step out from under it.</summary>
    public const float TrembleTime = 0.35f;
    /// <summary>Seconds between successive rings crumbling, pacing the bottom-to-top cascade.</summary>
    public const float CrumbleRingInterval = 0.03f;

    /// <summary>A condemned region mid-collapse: tiles sorted innermost-ring-first, a timer
    /// that runs down the tremble and then paces the ring-by-ring crumble.</summary>
    private sealed class PendingCollapse
    {
        public readonly List<int> Tiles = new();
        public float Timer = TrembleTime;
        public int Next;
    }

    private readonly List<PendingCollapse> _pendingCollapses = new();
    private readonly HashSet<int> _pendingTiles = new();
    /// <summary>Planet indices of tiles condemned but not yet crumbled. The renderer reads
    /// this to jitter their draw position (tremble telegraph).</summary>
    public HashSet<int> TremblingTiles => _pendingTiles;
    public int CollapsesThisTick { get; private set; }

    /// <summary>Tiles newly condemned this Update (they entered the tremble window but haven't
    /// crumbled yet). Game1 reads this to sound the cave-in warning creak with lead time,
    /// before the "collapse" boom that <see cref="CollapsesThisTick"/> drives.</summary>
    public int NewlyCondemnedThisTick { get; private set; }

    public Physics(Planet planet, Cells cells)
    {
        _planet = planet;
        _cells = cells;
        _anchorStamp = new int[planet.TileCount];
        _floodStamp = new int[planet.TileCount];
        _dirtyStamp = new int[planet.TileCount];
    }

    public void MarkDirty(int x, int y)
    {
        if (!_planet.InBounds(x, y)) return;
        // Wake up neighbors too — when the player digs out a tile, surrounding tiles become candidates.
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (_planet.InBounds(x + dx, y + dy))
                    MarkDirtyIdx(_planet.Index(x + dx, y + dy));
    }

    private void MarkDirtyIdx(int idx)
    {
        if (_dirtyStamp[idx] == _dirtyGen) return;
        _dirtyStamp[idx] = _dirtyGen;
        _dirtyList.Add(idx);
    }

    public void Update(float dt)
    {
        CollapsesThisTick = 0;
        NewlyCondemnedThisTick = 0;
        TickPendingCollapses(dt);
        _settleAccum += dt;
        if (_settleAccum < SettleInterval) return;
        _settleAccum = 0f;
        Settle();
    }

    /// <summary>Run pending collapses: count down each region's tremble, then crumble it
    /// one ring at a time from the innermost (bottom) up, so cave-ins sweep upward.</summary>
    private void TickPendingCollapses(float dt)
    {
        var crumbled = 0;
        for (var i = _pendingCollapses.Count - 1; i >= 0; i--)
        {
            var p = _pendingCollapses[i];
            p.Timer -= dt;
            while (p.Timer <= 0f && p.Next < p.Tiles.Count)
            {
                var ring = _planet.UnIndex(p.Tiles[p.Next]).x;
                while (p.Next < p.Tiles.Count && _planet.UnIndex(p.Tiles[p.Next]).x == ring)
                {
                    Crumble(p.Tiles[p.Next++]);
                    // Budget spent: stop mid-ring; Timer is still ≤ 0, so the very next
                    // Update resumes exactly here — the ring completes over a few ticks
                    // instead of dumping all its dust into one frame.
                    if (++crumbled >= CrumbleBudget) return;
                }
                p.Timer += CrumbleRingInterval;
            }
            if (p.Next >= p.Tiles.Count) _pendingCollapses.RemoveAt(i);
        }
    }

    private void Crumble(int idx)
    {
        _pendingTiles.Remove(idx);
        var (x, y) = _planet.UnIndex(idx);
        var k = _planet.Get(x, y);
        // The tile may have been mined (or melted) during the tremble — nothing to do.
        if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) return;
        _planet.Set(x, y, TileKind.Sky);
        _cells.SpawnDustInTile(x, y, k);
        CollapsesThisTick++;
        MarkDirty(x, y); // wake neighbours — adjacent regions may now be unanchored too.
    }

    private void Settle()
    {
        if (_dirtyList.Count == 0) return;
        (_dirtyWork, _dirtyList) = (_dirtyList, _dirtyWork);
        _dirtyGen++;
        _anchorGen++;

        for (var qi = 0; qi < _dirtyWork.Count; qi++)
        {
            if (qi >= SettleBudget)
            {
                // Out of budget: roll the remainder into the next settle tick's dirty list
                // (the fresh generation) so a mass break spreads detection over a few 50ms
                // ticks instead of spiking one frame.
                for (; qi < _dirtyWork.Count; qi++) MarkDirtyIdx(_dirtyWork[qi]);
                break;
            }
            var idx = _dirtyWork[qi];
            var (x, y) = _planet.UnIndex(idx);
            var k = _planet.Get(x, y);
            if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;

            // Loose ground: cardinal-down (= inward radial in polar) empty → crumble to dust
            // tagged with the original tile kind so it falls in the right colour and pays out
            // that tile's drop when the player walks through it.
            if (Materials.IsLoose(k))
            {
                // Cohesion: loose ground holds if anything solid sits below it — straight
                // down or either inner diagonal. Only fully undercut tiles crumble, so a
                // tunnel through dirt self-arches instead of cascading the whole layer down.
                var (inX, inY) = _planet.InnerNeighbour(x, y);
                if (!Tiles.IsSolid(_planet.Get(inX, inY))
                    && !Tiles.IsSolid(_planet.Get(inX, inY - 1))
                    && !Tiles.IsSolid(_planet.Get(inX, inY + 1)))
                {
                    _planet.Set(x, y, TileKind.Sky);
                    _cells.SpawnDustInTile(x, y, k);
                    CollapsesThisTick++;
                    MarkDirty(x, y);
                }
                continue;
            }

            // Stone-like: connectivity check. Skip the cheap-pass bail-outs first — only run a
            // flood when at least one cardinal neighbour is non-solid (otherwise definitely supported).
            if (!HasEmptyCardinalNeighbor(x, y)) continue;
            if (_anchorStamp[idx] == _anchorGen) continue;
            var dbgSw = System.Diagnostics.Stopwatch.StartNew();
            var anchored = IsRegionAnchored(x, y);
            DbgFloodMs += dbgSw.Elapsed.TotalMilliseconds;
            DbgFloods++;
            DbgFloodVisits += _floodVisitList.Count;
            if (anchored) continue;

            CollapseRegion(_floodRegion);
        }
        _dirtyWork.Clear();
    }

    /// <summary>True iff this tile has a ReinforcedSupport tile within the 8-neighbour ring.
    /// Drives the anchor halo — one reinforced beam stabilises everything in its 3×3 footprint.
    /// Uses the polar neighbour helpers for inner/outer so band-boundary mapping is correct.</summary>
    private bool HasReinforcedNeighbor(int x, int y)
    {
        // No reinforced supports on the whole planet (the common case — they're crafted,
        // not generated): skip the ~10 neighbour reads this costs per flooded tile.
        if (_planet.ReinforcedCount == 0) return false;
        var (inR, inT) = _planet.InnerNeighbour(x, y);
        if (_planet.Get(inR, inT) == TileKind.ReinforcedSupport) return true;
        if (_planet.Get(inR, inT - 1) == TileKind.ReinforcedSupport) return true;
        if (_planet.Get(inR, inT + 1) == TileKind.ReinforcedSupport) return true;
        if (_planet.Get(x, y - 1) == TileKind.ReinforcedSupport) return true;
        if (_planet.Get(x, y + 1) == TileKind.ReinforcedSupport) return true;
        var oc = _planet.OuterNeighbourCount(x, y);
        for (var i = 0; i < oc; i++)
        {
            var (or_, ot_) = _planet.OuterNeighbour(x, y, i);
            if (_planet.Get(or_, ot_) == TileKind.ReinforcedSupport) return true;
            if (_planet.Get(or_, ot_ - 1) == TileKind.ReinforcedSupport) return true;
            if (_planet.Get(or_, ot_ + 1) == TileKind.ReinforcedSupport) return true;
        }
        return false;
    }

    /// <summary>True iff at least one of the cardinal neighbours is non-solid. Inner/outer use
    /// the polar neighbour helpers so band-boundary radial mapping is correct.</summary>
    private bool HasEmptyCardinalNeighbor(int x, int y)
    {
        var (inR, inT) = _planet.InnerNeighbour(x, y);
        if (!Tiles.IsSolid(_planet.Get(inR, inT))) return true;
        var oc = _planet.OuterNeighbourCount(x, y);
        for (var i = 0; i < oc; i++)
        {
            var (or_, ot_) = _planet.OuterNeighbour(x, y, i);
            if (!Tiles.IsSolid(_planet.Get(or_, ot_))) return true;
        }
        if (!Tiles.IsSolid(_planet.Get(x, y + 1))) return true;
        if (!Tiles.IsSolid(_planet.Get(x, y - 1))) return true;
        return false;
    }

    /// <summary>Push a flood neighbour onto the stack if not yet visited and solid.</summary>
    private void VisitFloodNeighbour(int x, int y)
    {
        var ni = _planet.Index(x, y);
        if (_floodStamp[ni] == _floodGen) return;
        _floodStamp[ni] = _floodGen;
        _floodVisitList.Add(ni);
        var nk = _planet.Get(x, y);
        if (!Tiles.IsSolid(nk)) return;
        _floodStack.Push((ni, x, y));
    }

    /// <summary>
    /// Flood-fill the connected non-anchored solid region starting at (sx,sy). Returns true if
    /// the region reaches an anchored tile, the world edge, or another known-anchored cell —
    /// in which case the visited tiles are stamped anchored for this settle pass. Returns false
    /// (with the region in <see cref="_floodRegion"/>) if it is fully unsupported and small
    /// enough to evaluate; treats too-large regions as anchored as a safety valve.
    /// </summary>
    private bool IsRegionAnchored(int sx, int sy)
    {
        _floodGen++;
        _floodVisitList.Clear();
        _floodStack.Clear();
        _floodRegion.Clear();
        _regionBudgetSum = 0;
        _regionTouchesCrust = false;
        var crustRing = _planet.SurfaceRing + 4;   // covers the ±3-ring (±1.5 legacy tile) surface elev noise

        var startIdx = _planet.Index(sx, sy);
        _floodStack.Push((startIdx, sx, sy));
        _floodStamp[startIdx] = _floodGen;
        _floodVisitList.Add(startIdx);

        while (_floodStack.Count > 0)
        {
            var (idx, x, y) = _floodStack.Pop();

            // Cache hit — region is anchored. Promote everything we've seen.
            if (_anchorStamp[idx] == _anchorGen)
            {
                foreach (var v in _floodVisitList) _anchorStamp[v] = _anchorGen;
                return true;
            }

            var k = _planet.Get(x, y);
            if (!Tiles.IsSolid(k)) continue;
            if (Tiles.IsAnchored(k))
            {
                foreach (var v in _floodVisitList) _anchorStamp[v] = _anchorGen;
                return true;
            }
            // ReinforcedSupport halo: any tile within a 1-tile orthogonal+diagonal radius of
            // a placed reinforced support is anchored, even if the support itself isn't part
            // of the connected solid region. Lets one beam stabilise a 3×3 neighbourhood
            // including unsupported diagonal stone, which a plain Support can't.
            if (HasReinforcedNeighbor(x, y))
            {
                foreach (var v in _floodVisitList) _anchorStamp[v] = _anchorGen;
                return true;
            }
            _floodRegion.Add(idx);
            _regionBudgetSum += BudgetFor(k);
            if (x <= crustRing) _regionTouchesCrust = true;
            // Compare region size against the average per-material budget of its members
            // (count > sum/count, kept in integers as count² > sum) — a mixed seam of stone
            // and ore gets a threshold between the two, weighted by composition. The valve
            // only fires for regions that touch the crust: underground there's a backdrop
            // wall inferred to carry big spans, but a rock mass floating in open sky has
            // nothing behind it, so the flood keeps going (bounded by SkyRegionCap) and an
            // unsupported result condemns the whole thing regardless of size.
            if (_floodRegion.Count * _floodRegion.Count > _regionBudgetSum
                && (_regionTouchesCrust || _floodRegion.Count > SkyRegionCap))
            {
                // Region too big for its material strength — treat as supported.
                foreach (var v in _floodVisitList) _anchorStamp[v] = _anchorGen;
                return true;
            }

            // Cardinal neighbours under per-band polar adjacency:
            //   inner = 1 tile (always); outer = 1 or 2 across halving boundaries; angular = ±1.
            // Inner.
            var (innerR, innerT) = _planet.InnerNeighbour(x, y);
            if (innerR < 0)
            {
                // Reached the Core boundary — anchor.
                foreach (var v in _floodVisitList) _anchorStamp[v] = _anchorGen;
                return true;
            }
            VisitFloodNeighbour(innerR, innerT);

            // Outer (1 or 2). Out-of-bounds outer = sky, doesn't anchor.
            var oc = _planet.OuterNeighbourCount(x, y);
            for (var oi = 0; oi < oc; oi++)
            {
                var (or_, ot_) = _planet.OuterNeighbour(x, y, oi);
                if (or_ >= _planet.Rings) continue;
                VisitFloodNeighbour(or_, ot_);
            }

            // Angular ±1.
            VisitFloodNeighbour(x, y - 1);
            VisitFloodNeighbour(x, y + 1);
        }
        return false;
    }

    /// <summary>Collapse budget for one tile, derived from its mining hardness: Stone
    /// (hardness 2) gets the baseline, each tier above shaves a little off, and soft
    /// hardness-1 ground gets a little extra. Floored so exotic kinds can still cave.</summary>
    private static int BudgetFor(TileKind k) =>
        k == TileKind.Obsidian
            ? ObsidianCollapseBudget
            : Math.Max(BudgetPerHardness, StoneCollapseBudget - BudgetPerHardness * (Tiles.Hardness(k) - 2));

    /// <summary>Condemn a region: enqueue it to tremble, then crumble bottom-to-top. Tiles
    /// already pending from an earlier settle pass are skipped, so the re-detection that
    /// happens every settle tick while the region still stands doesn't restart the timer.</summary>
    private void CollapseRegion(List<int> region)
    {
        PendingCollapse? p = null;
        foreach (var idx in region)
        {
            if (_pendingTiles.Contains(idx)) continue;
            var (x, y) = _planet.UnIndex(idx);
            var k = _planet.Get(x, y);
            if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;
            p ??= new PendingCollapse();
            p.Tiles.Add(idx);
            _pendingTiles.Add(idx);
            NewlyCondemnedThisTick++;
        }
        if (p is null) return;
        // Innermost ring first = "bottom" in polar gravity, so the crumble sweeps upward.
        // Plain int sort: ring offsets are monotonic, so flat indices already order by ring
        // (the old comparator paid two UnIndex binary searches per comparison for the same
        // grouping — a real cost when a quake condemns thousands of tiles at once).
        p.Tiles.Sort();
        _pendingCollapses.Add(p);
    }

    private static (int x, int y) SnapToCardinal(Vector2 v)
    {
        var ax = MathF.Abs(v.X);
        var ay = MathF.Abs(v.Y);
        var sx = v.X > 0 ? 1 : v.X < 0 ? -1 : 0;
        var sy = v.Y > 0 ? 1 : v.Y < 0 ? -1 : 0;
        if (ax > ay * 2.4f) return (sx, 0);
        if (ay > ax * 2.4f) return (0, sy);
        return (sx, sy);
    }

    /// <summary>Trigger a planet-wide rumble: every solid tile within a radius gets re-checked.</summary>
    public void Earthquake(Vector2 epicenterWorld, float radiusPixels, int strength)
    {
        var (cx, cy) = _planet.WorldToTile(epicenterWorld);
        var rTiles = (int)(radiusPixels / Planet.TileSize) + 1;
        var rSq = rTiles * rTiles;
        for (var dy = -rTiles; dy <= rTiles; dy++)
        {
            for (var dx = -rTiles; dx <= rTiles; dx++)
            {
                if (dx * dx + dy * dy > rSq) continue;
                var x = cx + dx;
                var y = cy + dy;
                if (!_planet.InBounds(x, y)) continue;
                if (Tiles.IsSolid(_planet.Get(x, y))) MarkDirty(x, y);
            }
        }
        for (var i = 0; i < strength; i++) Settle();
    }
}
