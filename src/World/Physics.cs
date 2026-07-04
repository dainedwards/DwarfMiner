using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Tile collapse, two flavours per material class:
///   • Loose tiles (Dirt/Grass/Gravel/Snow/MossStone) crumble straight into the cell sim
///     the moment their cardinal-down neighbour is empty — the tile vanishes and a half
///     tile's worth of Material cells spawn in its place, then flow with angle of repose.
///   • Stone-like tiles (Stone, ores) use a connectivity check: flood-fill the connected
///     non-anchored solid region and if no path reaches an anchored tile (HardStone/Core/
///     Support) or the world edge within budget, the *whole region* crumbles into the cell
///     sim as dust tagged with each tile's kind — it rains down as granular debris under
///     the velocity sim and pays out the tile's drop on pickup, same as loose ground.
///     Anchored kinds never move.
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
    private int _regionBudgetSum;
    public int CollapsesThisTick { get; private set; }

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
        _settleAccum += dt;
        CollapsesThisTick = 0;
        if (_settleAccum < SettleInterval) return;
        _settleAccum = 0f;
        Settle();
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
                var (inX, inY) = _planet.InnerNeighbour(x, y);
                if (!Tiles.IsSolid(_planet.Get(inX, inY)))
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
                if (or_ >= Planet.RingCount) continue;
                VisitFloodNeighbour(or_, ot_);
            }

            // Angular ±1.
            VisitFloodNeighbour(x, y - 1);
            VisitFloodNeighbour(x, y + 1);
        }
        return false;
    }

    private void CollapseRegion(List<int> region)
    {
        foreach (var idx in region)
        {
            var (x, y) = _planet.UnIndex(idx);
            var k = _planet.Get(x, y);
            if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;
            _planet.Set(x, y, TileKind.Sky);
            _cells.SpawnDustInTile(x, y, k);
            CollapsesThisTick++;
            MarkDirty(x, y); // wake neighbours — adjacent regions may now be unanchored too.
        }
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
