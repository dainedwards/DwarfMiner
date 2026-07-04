using System;
using System.Collections.Generic;
using DwarfMiner.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.World;

public enum Material : byte
{
    Empty = 0,
    Sand = 1,
    Water = 2,
    Lava = 3,
    Smoke = 4,
    Dirt = 5,
    Gravel = 6,
    /// <summary>Loose grains from a broken tile. Source TileKind is stored alongside in
    /// Cells._srcTile so the dust falls with the right colour and gives the right drop on pickup.</summary>
    Dust = 7,
}

public static class Materials
{
    /// <summary>Tiles that crumble straight to dust the moment their inward neighbour is empty.</summary>
    public static bool IsLoose(TileKind k) => k is
        TileKind.Dirt or TileKind.Grass or TileKind.MossStone or
        TileKind.Gravel or TileKind.Snow;
}

/// <summary>
/// Per-cell material grid laid out polar on top of <see cref="Planet"/>. Variable cells per
/// row to mirror Planet's per-band tile halving — each cell row has CellsAt(cy) cells, and
/// "down" (inward radial) maps cell angles between rows that may have different cell counts.
/// </summary>
public sealed class Cells
{
    public const int Density = 4;
    /// <summary>How many dust cells one broken tile spawns. Each cell carries
    /// drop.count / DustCellsPerTile worth of resource — 1 tile fully collected → 1 × drop.count.</summary>
    public const int DustCellsPerTile = 8;

    // --- Per-cell velocity tuning (cells/sec units, radial axis) ---
    /// <summary>Inward acceleration while freefalling. At 60fps a cell reaches 1 cell/frame
    /// after ~12 frames, so disturbed material visibly picks up speed instead of instantly
    /// ticking downward.</summary>
    private const float GravityCells = 300f;
    /// <summary>Terminal fall speed — 4 cells/frame at 60fps.</summary>
    private const float TerminalCells = 240f;
    /// <summary>Hard cap on rows traversed per tick so lag frames can't tunnel through floors.</summary>
    private const int MaxStepsPerTick = Density * 2;
    /// <summary>Base lateral cells a supported liquid tries to flow per tick.</summary>
    private const int LiquidDispersion = 3;
    /// <summary>Extra lateral spread per cell/sec of fall speed at the moment of landing —
    /// fast-landing water sprays outward, gently pooling water barely spreads.</summary>
    private const float SplashScale = 1f / 48f;
    /// <summary>Fall speed kept when a falling cell deflects diagonally off an obstacle.</summary>
    private const float ImpactDamping = 0.5f;
    public readonly int Height;        // total cell rows = RingCount × Density
    public readonly Planet Planet;

    private readonly int[] _cellsAt;   // cells per row
    private readonly int[] _rowOffsets; // flat-array start index per row
    private readonly byte[] _mat;
    /// <summary>Source TileKind for dust cells. 0 (Sky) for non-dust cells.</summary>
    private readonly byte[] _srcTile;
    /// <summary>Inward radial speed per cell, cells/sec. 0 for resting cells.</summary>
    private readonly float[] _velR;
    /// <summary>Fractional rows accumulated toward the next inward step. Doubles as the
    /// sub-cell draw offset so falling cells glide between rows instead of ticking.</summary>
    private readonly float[] _travel;
    /// <summary>Persistent lateral flow direction for liquids (-1/+1, 0 = unset).</summary>
    private readonly sbyte[] _flow;
    private HashSet<int> _active = new();
    private HashSet<int> _next = new();
    private readonly HashSet<int> _living = new();
    private readonly Random _rng = new();
    private readonly Dictionary<string, float> _dustAccum = new();
    private float _time;

    public Cells(Planet planet)
    {
        Planet = planet;
        Height = Planet.RingCount * Density;
        _cellsAt = new int[Height];
        _rowOffsets = new int[Height + 1];
        for (var cy = 0; cy < Height; cy++)
        {
            _cellsAt[cy] = Planet.TilesAt(cy / Density) * Density;
            _rowOffsets[cy + 1] = _rowOffsets[cy] + _cellsAt[cy];
        }
        _mat = new byte[_rowOffsets[Height]];
        _srcTile = new byte[_rowOffsets[Height]];
        _velR = new float[_rowOffsets[Height]];
        _travel = new float[_rowOffsets[Height]];
        _flow = new sbyte[_rowOffsets[Height]];
    }

    private void ClearKinetics(int i)
    {
        _velR[i] = 0f;
        _travel[i] = 0f;
        _flow[i] = 0;
    }

    public int CellsAt(int cy) => (cy < 0 || cy >= Height) ? 1 : _cellsAt[cy];

    private int WrapX(int cx, int n) => ((cx % n) + n) % n;

    public int Idx(int cx, int cy)
    {
        var n = _cellsAt[cy];
        return _rowOffsets[cy] + WrapX(cx, n);
    }

    public bool InBounds(int cx, int cy) => cy >= 0 && cy < Height;

    public Material Get(int cx, int cy) => InBounds(cx, cy) ? (Material)_mat[Idx(cx, cy)] : Material.Empty;

    public Vector2 CellToWorld(int cx, int cy)
    {
        var n = CellsAt(cy);
        var radius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
        var angle = (WrapX(cx, n) + 0.5f) / n * MathHelper.TwoPi;
        return Planet.Center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    public (int cx, int cy) WorldToCell(Vector2 p)
    {
        var rel = p - Planet.Center;
        var dist = rel.Length();
        var cy = (int)(dist / Planet.TileSize * Density) - Planet.RingMin * Density;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var cyc = MathHelper.Clamp(cy, 0, Height - 1);
        var n = _cellsAt[cyc];
        var cx = (int)(ang / MathHelper.TwoPi * n);
        return (cx, cy);
    }

    /// <summary>Inner cell (cy-1, mapped to its row's angle space).</summary>
    public (int cx, int cy) InnerCell(int cx, int cy)
    {
        if (cy <= 0) return (0, -1);
        var nIn = _cellsAt[cy - 1];
        var nOut = _cellsAt[cy];
        var w = WrapX(cx, nOut);
        if (nIn == nOut) return (w, cy - 1);
        return ((int)((w + 0.5) * nIn / nOut) % nIn, cy - 1);
    }

    public int OuterCellCount(int cx, int cy)
    {
        if (cy >= Height - 1) return 1;
        var nIn = _cellsAt[cy];
        var nOut = _cellsAt[cy + 1];
        if (nIn == nOut) return 1;
        var w = WrapX(cx, nIn);
        var first = (int)Math.Floor((double)w * nOut / nIn);
        var lastExcl = (int)Math.Ceiling((double)(w + 1) * nOut / nIn);
        return Math.Max(1, lastExcl - first);
    }

    public (int cx, int cy) OuterCell(int cx, int cy, int which = 0)
    {
        if (cy >= Height - 1) return (0, Height);
        var nIn = _cellsAt[cy];
        var nOut = _cellsAt[cy + 1];
        var w = WrapX(cx, nIn);
        if (nIn == nOut) return (w, cy + 1);
        var first = (int)Math.Floor((double)w * nOut / nIn);
        return ((first + which) % nOut, cy + 1);
    }

    public void Place(int cx, int cy, Material m, TileKind src = TileKind.Sky)
    {
        if (!InBounds(cx, cy)) return;
        var i = Idx(cx, cy);
        if (m != Material.Empty && _mat[i] != 0) return;
        _mat[i] = (byte)m;
        _srcTile[i] = (byte)src;
        if (m == Material.Empty) _living.Remove(i);
        else { _living.Add(i); _active.Add(i); }
        WakeNeighbors(cx, cy);
    }

    public void PlaceAtWorld(Vector2 worldPos, Material m)
    {
        var (cx, cy) = WorldToCell(worldPos);
        Place(cx, cy, m);
    }

    /// <summary>Spawn cells inside the polar tile (tx = ring, ty = angle). Picks random sub-cells.</summary>
    public void SpawnInTile(int tx, int ty, Material m, int count)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        for (var i = 0; i < count; i++)
        {
            var cx = c0x + _rng.Next(Density);
            var cy = c0y + _rng.Next(Density);
            Place(cx, cy, m);
        }
    }

    /// <summary>Spawn a checkerboard of dust cells filling half the polar tile, tagged with the
    /// source TileKind so the cells render in that tile's colours and pay out that tile's drop on
    /// pickup. Deterministic count (DustCellsPerTile = Density² / 2 = 8) so accumulation math is
    /// exact: collecting every cell from one tile yields exactly drop.count units.</summary>
    public void SpawnDustInTile(int tx, int ty, TileKind src)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
                if (((dx + dy) & 1) == 0)
                    Place(c0x + dx, c0y + dy, Material.Dust, src);
    }

    public bool IsBlocked(int cx, int cy)
    {
        if (cy < 0) return true;
        if (cy >= Height) return true;
        var tx = cy / Density;
        var ty = WrapX(cx, _cellsAt[cy]) / Density;
        if (Tiles.IsSolid(Planet.Get(tx, ty))) return true;
        return _mat[Idx(cx, cy)] != 0;
    }

    private bool IsTileSolidAt(int cx, int cy)
    {
        if (cy < 0 || cy >= Height) return true;
        return Tiles.IsSolid(Planet.Get(cy / Density, WrapX(cx, _cellsAt[cy]) / Density));
    }

    private void Wake(int cx, int cy)
    {
        if (cy < 0 || cy >= Height) return;
        var idx = Idx(cx, cy);
        if (_mat[idx] != 0) _next.Add(idx);
    }

    private void WakeNeighbors(int cx, int cy)
    {
        // Same-row angular neighbours.
        Wake(cx - 1, cy);
        Wake(cx + 1, cy);
        // Inner row + its angular neighbours.
        if (cy > 0)
        {
            var (icx, icy) = InnerCell(cx, cy);
            Wake(icx, icy);
            Wake(icx - 1, icy);
            Wake(icx + 1, icy);
        }
        // Outer row(s) + their angular neighbours.
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++)
            {
                var (ocx, ocy) = OuterCell(cx, cy, i);
                Wake(ocx, ocy);
                Wake(ocx - 1, ocy);
                Wake(ocx + 1, ocy);
            }
        }
    }

    /// <summary>Move from (sCx,sCy) to (dCx,dCy). Both rows are bounds-checked; angles wrap.</summary>
    private bool TryMoveTo(int sCx, int sCy, int dCx, int dCy)
    {
        if (dCy < 0 || dCy >= Height) return false;
        if (IsTileSolidAt(dCx, dCy)) return false;
        var di = Idx(dCx, dCy);
        if (_mat[di] != 0) return false;
        var si = Idx(sCx, sCy);
        _mat[di] = _mat[si];
        _srcTile[di] = _srcTile[si];
        _mat[si] = 0;
        _srcTile[si] = 0;
        _living.Remove(si); _living.Add(di);
        _next.Add(di);
        WakeNeighbors(sCx, sCy);
        WakeNeighbors(dCx, dCy);
        return true;
    }

    public void Update(float dt)
    {
        _time += dt;

        (_active, _next) = (_next, _active);
        _next.Clear();
        if (_active.Count == 0) return;

        var snapshot = new int[_active.Count];
        _active.CopyTo(snapshot);
        Shuffle(snapshot);

        foreach (var idx in snapshot)
        {
            if ((uint)idx >= (uint)_mat.Length) continue;
            var m = (Material)_mat[idx];
            if (m == Material.Empty) continue;
            var (cx, cy) = UnIdx(idx);
            switch (m)
            {
                case Material.Sand:
                case Material.Dirt:
                case Material.Gravel:
                case Material.Dust:
                    TickSand(cx, cy);
                    break;
                case Material.Water: TickLiquid(cx, cy); break;
                case Material.Lava:  TickLava(cx, cy); break;
                case Material.Smoke: TickSmoke(cx, cy); break;
            }
        }
    }

    /// <summary>Decompose a flat cell index back to (cx, cy) by binary search through row offsets.</summary>
    private (int cx, int cy) UnIdx(int idx)
    {
        int lo = 0, hi = Height;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_rowOffsets[mid + 1] <= idx) lo = mid + 1;
            else hi = mid;
        }
        return (idx - _rowOffsets[lo], lo);
    }

    private void TickSand(int cx, int cy)
    {
        if (cy <= 0) return;
        var (icx, icy) = InnerCell(cx, cy);
        if (TryMoveTo(cx, cy, icx, icy)) return;
        var first = _rng.Next(2) == 0 ? 1 : -1;
        if (TryMoveTo(cx, cy, icx + first, icy)) return;
        if (TryMoveTo(cx, cy, icx - first, icy)) return;
    }

    private void TickLiquid(int cx, int cy)
    {
        if (cy > 0)
        {
            var (icx, icy) = InnerCell(cx, cy);
            if (TryMoveTo(cx, cy, icx, icy)) return;
            var firstD = _rng.Next(2) == 0 ? 1 : -1;
            if (TryMoveTo(cx, cy, icx + firstD, icy)) return;
            if (TryMoveTo(cx, cy, icx - firstD, icy)) return;
        }
        var first = _rng.Next(2) == 0 ? 1 : -1;
        if (TryMoveTo(cx, cy, cx + first, cy)) return;
        if (TryMoveTo(cx, cy, cx - first, cy)) return;
        _next.Add(Idx(cx, cy));
    }

    private void TickLava(int cx, int cy)
    {
        TryMelt(InnerCell(cx, cy));
        TryMelt((cx + 1, cy));
        TryMelt((cx - 1, cy));
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++) TryMelt(OuterCell(cx, cy, i));
        }

        if (_rng.Next(2) == 0) { _next.Add(Idx(cx, cy)); return; }
        TickLiquid(cx, cy);
    }

    private void TryMelt((int cx, int cy) c)
    {
        if (_rng.Next(40) != 0) return;
        if (c.cy < 0 || c.cy >= Height) return;
        var tx = c.cy / Density;
        var ty = WrapX(c.cx, _cellsAt[c.cy]) / Density;
        var k = Planet.Get(tx, ty);
        if (!IsMeltable(k)) return;
        Planet.Set(tx, ty, TileKind.Sky);
        SpawnInTile(tx, ty, Material.Smoke, 4);
        if (_rng.Next(3) == 0) SpawnInTile(tx, ty, Material.Lava, 1);
    }

    private static bool IsMeltable(TileKind k) => k is
        TileKind.Dirt or TileKind.Grass or TileKind.Gravel or
        TileKind.MossStone or TileKind.Snow or TileKind.Support;

    private void TickSmoke(int cx, int cy)
    {
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            var (ocx, ocy) = OuterCell(cx, cy, _rng.Next(oc));
            if (TryMoveTo(cx, cy, ocx, ocy)) return;
            var first = _rng.Next(2) == 0 ? 1 : -1;
            if (TryMoveTo(cx, cy, ocx + first, ocy)) return;
            if (TryMoveTo(cx, cy, ocx - first, ocy)) return;
        }
        if (_rng.Next(180) == 0)
        {
            var i = Idx(cx, cy);
            _mat[i] = 0;
            _living.Remove(i);
            WakeNeighbors(cx, cy);
        }
        else _next.Add(Idx(cx, cy));
    }

    private void Shuffle(int[] arr)
    {
        for (var i = arr.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (arr[i], arr[j]) = (arr[j], arr[i]);
        }
    }

    /// <summary>
    /// Sweep all dust cells within a world-space radius into the dust accumulator and return any
    /// whole resource units that have built up. Each collected cell adds drop.count /
    /// DustCellsPerTile worth of fractional resource per its source TileKind; the accumulator
    /// rolls over to integer increments which are returned and added to the player's inventory by
    /// the caller. Cells with no drop (Sky/Core source) are simply removed without accumulation.
    /// Returns null when nothing was collected this call.
    /// </summary>
    public Dictionary<string, int>? CollectInRadius(Vector2 worldPos, float radius)
    {
        var rSq = radius * radius;
        var (_, cy0) = WorldToCell(worldPos);
        var radial = (float)Planet.TileSize / Density;
        var rRows = (int)MathF.Ceiling(radius / radial) + 1;
        var rel = worldPos - Planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var any = false;
        for (var dy = -rRows; dy <= rRows; dy++)
        {
            var cy = cy0 + dy;
            if (cy < 0 || cy >= Height) continue;
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            var rCols = (int)MathF.Ceiling(radius / MathF.Max(chord, 0.01f)) + 1;
            var cx0 = (int)(ang / MathHelper.TwoPi * n);
            for (var dx = -rCols; dx <= rCols; dx++)
            {
                var cx = cx0 + dx;
                var idx = Idx(cx, cy);
                if ((Material)_mat[idx] != Material.Dust) continue;
                if (Vector2.DistanceSquared(CellToWorld(cx, cy), worldPos) > rSq) continue;
                var src = (TileKind)_srcTile[idx];
                var drop = Tiles.Drop(src);
                if (drop is { } d)
                {
                    _dustAccum.TryGetValue(d.id, out var existing);
                    _dustAccum[d.id] = existing + (float)d.count / DustCellsPerTile;
                }
                _mat[idx] = 0;
                _srcTile[idx] = 0;
                _living.Remove(idx);
                // Wake the surrounding cells so anything resting on this one re-runs its fall
                // logic — without this, a collected cell at the bottom of a column leaves the
                // pile above it floating until something else perturbs it.
                WakeNeighbors(cx, cy);
                any = true;
            }
        }
        if (!any) return null;
        Dictionary<string, int>? result = null;
        // Snapshot keys so we can mutate values during iteration.
        var keys = new List<string>(_dustAccum.Keys);
        foreach (var id in keys)
        {
            var val = _dustAccum[id];
            var whole = (int)MathF.Floor(val);
            if (whole <= 0) continue;
            result ??= new Dictionary<string, int>();
            result[id] = whole;
            _dustAccum[id] = val - whole;
        }
        return result;
    }

    public void Draw(Renderer r)
    {
        var radial = (float)Planet.TileSize / Density;
        foreach (var idx in _living)
        {
            var m = (Material)_mat[idx];
            if (m == Material.Empty) continue;
            var (cx, cy) = UnIdx(idx);
            var centre = CellToWorld(cx, cy);
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            var size = new Vector2(chord + 0.5f, radial + 0.5f);
            var up = Planet.UpAt(centre);
            var rotation = MathF.Atan2(up.X, -up.Y);
            var col = ColorFor(m, cx, cy, _srcTile[idx]);
            r.Batch.Draw(r.Pixel, centre, null, col, rotation,
                new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
        }
    }

    public void AddLights(Renderer r)
    {
        var step = 0;
        foreach (var idx in _living)
        {
            if ((Material)_mat[idx] != Material.Lava) continue;
            var (cx, cy) = UnIdx(idx);
            // Skip interior pool cells whose four cardinal neighbours are all blocked.
            var (icx, icy) = InnerCell(cx, cy);
            var allBlocked = IsBlocked(cx - 1, cy) && IsBlocked(cx + 1, cy) && IsBlocked(icx, icy);
            if (allBlocked && cy < Height - 1)
            {
                var (ocx, ocy) = OuterCell(cx, cy, 0);
                if (IsBlocked(ocx, ocy)) continue;
            }
            if ((step++ % 2) != 0) continue;
            r.AddLight(CellToWorld(cx, cy), 10f, new Color(255, 130, 40));
        }
    }

    public void FillSkyTilesWithin(float radiusTilesFromCentre, Material m)
    {
        var maxRing = (int)radiusTilesFromCentre - Planet.RingMin;
        if (maxRing <= 0) return;
        maxRing = Math.Min(maxRing, Planet.RingCount);
        for (var r = 0; r < maxRing; r++)
        {
            var n = Planet.TilesAt(r);
            for (var t = 0; t < n; t++)
                if (Planet.Get(r, t) == TileKind.Sky)
                    SpawnInTile(r, t, m, Density * Density);
        }
    }

    private Color ColorFor(Material m, int cx, int cy, byte srcByte)
    {
        var hash = (cx * 73856093) ^ (cy * 19349663);
        var jitter = ((hash >> 4) & 31) - 16;
        switch (m)
        {
            case Material.Sand:    return Tint(new Color(190, 158, 92), jitter / 3);
            case Material.Water:   return Tint(new Color(58, 100, 190), jitter / 4);
            case Material.Lava:
            {
                var t = (int)(_time * 8f);
                var h2 = hash ^ (t * 83492791);
                var jit2 = ((h2 >> 5) & 63) - 32;
                return Tint(new Color(255, 110, 30), jit2 / 3);
            }
            case Material.Smoke:
            {
                var b = new Color(80, 75, 80);
                return new Color(b.R + jitter / 6, b.G + jitter / 6, b.B + jitter / 6, (byte)200);
            }
            case Material.Dirt:    return Tint(new Color(115, 75, 42), jitter / 3);
            case Material.Gravel:  return Tint(new Color(125, 120, 110), jitter / 3);
            case Material.Dust:
            {
                // Lighten the source tile's base colour so dust reads as granular crumb rather
                // than a tile facsimile. Sky source (= no tag) falls back to a generic sand tone.
                var src = (TileKind)srcByte;
                var b = src == TileKind.Sky ? new Color(190, 158, 92) : Tiles.BaseColor(src);
                var lit = new Color(
                    Math.Min(255, b.R + 24),
                    Math.Min(255, b.G + 24),
                    Math.Min(255, b.B + 24));
                return Tint(lit, jitter / 3);
            }
            default: return Color.Magenta;
        }
    }

    private static Color Tint(Color c, int delta) => new(
        Math.Clamp(c.R + delta, 0, 255),
        Math.Clamp(c.G + delta, 0, 255),
        Math.Clamp(c.B + delta, 0, 255));
}
