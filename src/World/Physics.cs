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
    private readonly HashSet<int> _dirty = new();
    private readonly Queue<int> _processQueue = new();
    private readonly HashSet<int> _anchoredCache = new();
    private readonly HashSet<int> _floodVisited = new();
    private readonly Stack<int> _floodStack = new();
    private readonly List<int> _floodRegion = new();
    private float _settleAccum;
    public const float SettleInterval = 0.05f; // seconds between settle ticks
    /// <summary>Max collapsible region size for baseline rock (Stone, hardness 2). Regions
    /// bigger than their strength-derived budget are treated as supported.</summary>
    public const int StoneCollapseBudget = 48;
    /// <summary>Budget shaved off per hardness tier above Stone — stronger material holds
    /// larger unsupported spans, so only smaller pockets of it can cave in.</summary>
    public const int BudgetPerHardness = 8;
    /// <summary>Obsidian is brittle: any unsupported span bigger than this caves in.</summary>
    public const int ObsidianCollapseBudget = 6;
    private int _regionBudgetSum;

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

    public Physics(Planet planet, Cells cells) { _planet = planet; _cells = cells; }

    public void MarkDirty(int x, int y)
    {
        if (!_planet.InBounds(x, y)) return;
        _dirty.Add(_planet.Index(x, y));
        // Wake up neighbors too — when the player digs out a tile, surrounding tiles become candidates.
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (_planet.InBounds(x + dx, y + dy))
                    _dirty.Add(_planet.Index(x + dx, y + dy));
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
        for (var i = _pendingCollapses.Count - 1; i >= 0; i--)
        {
            var p = _pendingCollapses[i];
            p.Timer -= dt;
            while (p.Timer <= 0f && p.Next < p.Tiles.Count)
            {
                var ring = _planet.UnIndex(p.Tiles[p.Next]).x;
                while (p.Next < p.Tiles.Count && _planet.UnIndex(p.Tiles[p.Next]).x == ring)
                    Crumble(p.Tiles[p.Next++]);
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
        if (_dirty.Count == 0) return;
        foreach (var i in _dirty) _processQueue.Enqueue(i);
        _dirty.Clear();
        _anchoredCache.Clear();

        while (_processQueue.Count > 0)
        {
            var idx = _processQueue.Dequeue();
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
            if (_anchoredCache.Contains(idx)) continue;
            if (IsRegionAnchored(x, y)) continue;

            CollapseRegion(_floodRegion);
        }
    }

    /// <summary>True iff this tile has a ReinforcedSupport tile within the 8-neighbour ring.
    /// Drives the anchor halo — one reinforced beam stabilises everything in its 3×3 footprint.
    /// Uses the polar neighbour helpers for inner/outer so band-boundary mapping is correct.</summary>
    private bool HasReinforcedNeighbor(int x, int y)
    {
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
        if (_floodVisited.Contains(ni)) return;
        _floodVisited.Add(ni);
        var nk = _planet.Get(x, y);
        if (!Tiles.IsSolid(nk)) return;
        _floodStack.Push(ni);
    }

    /// <summary>
    /// Flood-fill the connected non-anchored solid region starting at (sx,sy). Returns true if
    /// the region reaches an anchored tile, the world edge, or another known-anchored cell —
    /// in which case the visited tiles are added to <see cref="_anchoredCache"/>. Returns false
    /// (with the region in <see cref="_floodRegion"/>) if it is fully unsupported and small
    /// enough to evaluate; treats too-large regions as anchored as a safety valve.
    /// </summary>
    private bool IsRegionAnchored(int sx, int sy)
    {
        _floodVisited.Clear();
        _floodStack.Clear();
        _floodRegion.Clear();
        _regionBudgetSum = 0;

        var startIdx = _planet.Index(sx, sy);
        _floodStack.Push(startIdx);
        _floodVisited.Add(startIdx);

        while (_floodStack.Count > 0)
        {
            var idx = _floodStack.Pop();
            var (x, y) = _planet.UnIndex(idx);

            // Cache hit — region is anchored. Promote everything we've seen.
            if (_anchoredCache.Contains(idx))
            {
                foreach (var v in _floodVisited) _anchoredCache.Add(v);
                return true;
            }

            var k = _planet.Get(x, y);
            if (!Tiles.IsSolid(k)) continue;
            if (Tiles.IsAnchored(k))
            {
                foreach (var v in _floodVisited) _anchoredCache.Add(v);
                return true;
            }
            // ReinforcedSupport halo: any tile within a 1-tile orthogonal+diagonal radius of
            // a placed reinforced support is anchored, even if the support itself isn't part
            // of the connected solid region. Lets one beam stabilise a 3×3 neighbourhood
            // including unsupported diagonal stone, which a plain Support can't.
            if (HasReinforcedNeighbor(x, y))
            {
                foreach (var v in _floodVisited) _anchoredCache.Add(v);
                return true;
            }
            _floodRegion.Add(idx);
            _regionBudgetSum += BudgetFor(k);
            // Compare region size against the average per-material budget of its members
            // (count > sum/count, kept in integers as count² > sum) — a mixed seam of stone
            // and ore gets a threshold between the two, weighted by composition.
            if (_floodRegion.Count * _floodRegion.Count > _regionBudgetSum)
            {
                // Region too big for its material strength — treat as supported.
                foreach (var v in _floodVisited) _anchoredCache.Add(v);
                return true;
            }

            // Cardinal neighbours under per-band polar adjacency:
            //   inner = 1 tile (always); outer = 1 or 2 across halving boundaries; angular = ±1.
            // Inner.
            var (innerR, innerT) = _planet.InnerNeighbour(x, y);
            if (innerR < 0)
            {
                // Reached the Core boundary — anchor.
                foreach (var v in _floodVisited) _anchoredCache.Add(v);
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
        p.Tiles.Sort((a, b) => _planet.UnIndex(a).x.CompareTo(_planet.UnIndex(b).x));
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
