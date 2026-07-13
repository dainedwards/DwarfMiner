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
    /// <summary>Corrosive liquid. Flows like water and eats through soft tiles it touches (like
    /// lava melts, but a different tile set and no light); burns bodies on contact.</summary>
    Acid = 8,
    /// <summary>Flammable gas pocket. Rises like smoke and lingers; chokes the dwarf (drains
    /// air) and flash-burns to smoke when it meets lava.</summary>
    Gas = 9,
}

public static class Materials
{
    /// <summary>Tiles that crumble straight to dust the moment their inward neighbour is empty.
    /// Conglomerate is compacted debris, so undercutting it spills its stored cells back out.</summary>
    public static bool IsLoose(TileKind k) => k is
        TileKind.Dirt or TileKind.Grass or TileKind.MossStone or
        TileKind.Gravel or TileKind.Snow or TileKind.Conglomerate;

    /// <summary>Cell materials the compaction sweep may press into a Conglomerate tile.</summary>
    public static bool IsCompactable(Material m) => m is
        Material.Sand or Material.Dirt or Material.Gravel or Material.Dust;
}

/// <summary>
/// Per-cell material grid laid out polar on top of <see cref="Planet"/>. Variable cells per
/// row to mirror Planet's per-band tile halving — each cell row has CellsAt(cy) cells, and
/// "down" (inward radial) maps cell angles between rows that may have different cell counts.
/// </summary>
public sealed class Cells
{
    /// <summary>Cells per tile edge. Tiles are 4 px now, so 4 keeps 1-px grains (Noita-style
    /// fine sand) and the same total cell count as the old 8-px/Density-8 grid; the sim and
    /// draw loops are view-culled and hemmed liquids sleep, which is what makes this
    /// resolution affordable.</summary>
    public const int Density = 4;
    /// <summary>How many dust cells one broken tile spawns (checkerboard = Density²/2).</summary>
    public const int DustCellsPerTile = Density * Density / 2;
    /// <summary>Dust cells that add up to one whole drop unit. Four 4-px tiles occupy one
    /// legacy 8-px tile's area, so a *quad* of broken tiles — not a single tile — pays out
    /// drop.count. Keeps the resource economy identical to the old coarse grid (and makes
    /// 2×2-stamped placeables refund exactly what they cost).</summary>
    public const int DustCellsPerDrop = DustCellsPerTile * 4;

    // --- Per-cell velocity tuning (cells/sec units, radial axis). Expressed relative to
    // Density so the *world-space* fall speed and spread stay identical if the grain
    // resolution changes: px/s = cells/s × (TileSize / Density). ---
    /// <summary>Inward acceleration while freefalling (900 px/s² equivalent), so disturbed
    /// material visibly picks up speed instead of instantly ticking downward.</summary>
    private const float GravityCells = 112.5f * Density;
    /// <summary>Speed a freed grain starts at so it steps a full cell on its very first tick
    /// instead of hanging in place for ~0.1s while gravity ramps it up from zero. 60 cells/s =
    /// one cell per 1/60s tick — enough to drop immediately without popping.</summary>
    private const float InitialFallCells = 60f;
    /// <summary>Terminal fall speed (480 px/s equivalent).</summary>
    private const float TerminalCells = 60f * Density;
    /// <summary>Hard cap on rows traversed per tick so lag frames can't tunnel through floors.</summary>
    private const int MaxStepsPerTick = Density * 2;
    /// <summary>Base lateral cells a supported liquid tries to flow per tick (~6 px equivalent).</summary>
    private const int LiquidDispersion = Density * 3 / 4;
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
    // Active set as flat lists + a dedup flag array rather than HashSets: enqueueing a cell
    // is one array test and one list append (no hashing), and the tick iterates the list in
    // place instead of snapshotting into a fresh array. This path runs ~20 times per moving
    // grain per tick (self re-adds + neighbour wakes), so it dominates heavy scenes —
    // explosions and disasters wake tens of thousands of cells at once.
    private List<int> _active = new();
    private List<int> _next = new();
    /// <summary>True while the cell index is already queued in <see cref="_next"/>.</summary>
    private readonly bool[] _queued;
    private readonly Random _rng = new();
    private readonly Dictionary<string, float> _dustAccum = new();
    private float _time;

    // --- Compaction: buried, undisturbed grains re-form into Conglomerate tiles. ---
    // Per-cell timers would keep every resting grain awake, so the mechanic is tile-level
    // and sweep-based instead: TickSand records the owning tile whenever a grain comes to
    // rest, a slow sweep promotes full+buried tiles to candidates, and a candidate converts
    // only if a *second* look CompactDelay later finds its fill untouched — "undisturbed for
    // N seconds" without any bookkeeping in the hot move path.
    /// <summary>Planet tile indices where a grain came to rest since the last sweep, as a
    /// stamp array + list (RecordRest fires for every held grain every tick during a settle
    /// wave, so HashSet hashing here was hot). Stamp == _restGen means already recorded.</summary>
    private readonly int[] _restStamp;
    private int _restGen = 1;
    private readonly List<int> _restList = new();
    /// <summary>Candidate tiles awaiting their second look: fill count at promotion + due time.</summary>
    private readonly Dictionary<int, (int fill, float due)> _compacting = new();
    private readonly List<int> _compactScratch = new();
    private float _compactSweepAt;
    private const float CompactSweepPeriod = 1.5f;
    /// <summary>Seconds a candidate must sit untouched before it converts.</summary>
    private const float CompactDelay = 45f;
    /// <summary>Occupied-cell floor for conversion — near-full so surface piles stay loose;
    /// a couple of voids are tolerated because craggy interlocked piles keep small gaps.</summary>
    private const int CompactMinFill = Density * Density - 2;
    /// <summary>Occupied-cell floor for a tile converting under grain pressure. Naturally
    /// settled piles interlock with 10-25% voids, so the sealed-pocket floor above would
    /// strand every layer after the first; a pressed tile instead pulls grains down from
    /// the column above to fill its gaps (see the steal pass in Compact).</summary>
    private const int CompactPressedMinFill = Density * Density / 2;
    /// <summary>Tiles' worth of grains that must sit above a tile before the pressed
    /// (void-tolerant, accelerated) rules apply. Below this it's a loose crest.</summary>
    private const float CompactPressureMin = 1f;
    /// <summary>Pressure cap, in tiles of grains — bounds both the eligibility scan and
    /// the delay speed-up (a 4-deep pile hardens ~5× faster than a lone buried tile).</summary>
    private const int CompactPressureCap = 4;
    /// <summary>No compaction this close to the player (world px) — never entomb the dwarf
    /// or solidify the dust pile they're actively vacuuming.</summary>
    private const float CompactExclusionRadius = 24f;
    /// <summary>Player position, refreshed by Game1 each frame; null in headless contexts.</summary>
    public Vector2? CompactionExclusion;

    /// <summary>Shattered gem sites awaiting their physical drop (see the gem handling in
    /// <see cref="SpawnDustInTile"/>). Game1 drains this into Session.Pickups. <c>whole</c>
    /// distinguishes an embedded-gem pop (one full drop) from a legacy gem *tile* shattering
    /// (¼ drop — four fine tiles made up one legacy tile).</summary>
    public readonly List<(Vector2 pos, TileKind kind, bool whole)> PendingGemDrops = new();

    public Cells(Planet planet)
    {
        Planet = planet;
        Height = Planet.Rings * Density;
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
        _queued = new bool[_rowOffsets[Height]];
        _restStamp = new int[planet.TileCount];
        // Coarse row lookup for UnIdx: the row containing the first cell of each block.
        _rowOfBlock = new int[(_rowOffsets[Height] >> RowBlockShift) + 2];
        var row = 0;
        for (var b = 0; b < _rowOfBlock.Length; b++)
        {
            var first = b << RowBlockShift;
            while (row < Height - 1 && _rowOffsets[row + 1] <= first) row++;
            _rowOfBlock[b] = row;
        }
    }

    private const int RowBlockShift = 12;
    /// <summary>Row containing cell index (block « RowBlockShift) — see <see cref="UnIdx"/>.</summary>
    private readonly int[] _rowOfBlock;

    /// <summary>Queue the cell for the next tick (deduplicated).</summary>
    private void Enqueue(int i)
    {
        if (_queued[i]) return;
        _queued[i] = true;
        _next.Add(i);
    }

    private void ClearKinetics(int i)
    {
        _velR[i] = 0f;
        _travel[i] = 0f;
        _flow[i] = 0;
    }

    /// <summary>Serialize the cell materials + source tiles for the run save. Kinetics
    /// (velocity/travel/flow) are deliberately dropped — they're sub-second transients.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        w.Write(_mat.Length);
        w.Write(_mat);
        w.Write(_srcTile);
    }

    /// <summary>Restore state written by <see cref="WriteState"/> into a freshly constructed
    /// grid. Every occupied cell is woken so anything saved mid-flight resettles over the
    /// next ticks (the caller should burn a few pre-settle updates, like world gen does);
    /// hemmed pool interiors go back to sleep on their own.</summary>
    public void ReadState(System.IO.BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n != _mat.Length)
            throw new System.IO.InvalidDataException($"cell count mismatch: {n} vs {_mat.Length}");
        r.ReadBytes(n).CopyTo(_mat, 0);
        r.ReadBytes(n).CopyTo(_srcTile, 0);
        for (var i = 0; i < n; i++)
            if (_mat[i] != 0) Enqueue(i);
    }

    public int CellsAt(int cy) => (cy < 0 || cy >= Height) ? 1 : _cellsAt[cy];

    private static int WrapX(int cx, int n)
    {
        // Fast path: neighbour probes are almost always already in range, so skip the two
        // divisions of the general modulo — this is the innermost op of the whole sim.
        if ((uint)cx < (uint)n) return cx;
        cx %= n;
        return cx < 0 ? cx + n : cx;
    }

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
        ClearKinetics(i);
        // Queue into _next (not the being-consumed _active) so a cell placed between ticks —
        // or mid-tick by a melt/corrode reaction — is guaranteed to get its first tick.
        if (m != Material.Empty) Enqueue(i);
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
    /// pickup. Deterministic count (DustCellsPerTile = Density² / 2) so accumulation math is
    /// exact: collecting every cell from a 2×2 tile quad yields exactly drop.count units.
    /// A shattered Conglomerate ignores the checkerboard and spills the exact cells that were
    /// pressed into it (see the compaction sweep) — value round-trips, nothing is minted.</summary>
    public void SpawnDustInTile(int tx, int ty, TileKind src)
    {
        // A gem embedded in this tile pops out as a physical pickup when the host shatters.
        // Cells has no entity access, so shatter sites queue here and Game1 drains the queue
        // into Session.Pickups (headless callers just leave it; the list is per-planet).
        if (Planet.TakeGem(tx, ty) is var embedded && embedded != TileKind.Sky)
            PendingGemDrops.Add((Planet.TileToWorld(tx, ty), embedded, true));
        // Legacy gem *tiles* (old worlds only — gen now embeds gems in hosts): no dust,
        // queue a quarter-drop instead.
        if (Tiles.IsGem(src))
        {
            PendingGemDrops.Add((Planet.TileToWorld(tx, ty), src, false));
            return;
        }
        var c0y = tx * Density;
        var c0x = ty * Density;
        if (src == TileKind.Conglomerate)
        {
            if (Planet.TakeComposition(tx, ty) is { } comp)
            {
                var slot = 0;
                foreach (var (mat, dustSrc, count) in comp.Parts)
                    for (var i = 0; i < count && slot < Density * Density; i++, slot++)
                        Place(c0x + slot % Density, c0y + slot / Density,
                            (Material)mat, (TileKind)dustSrc);
            }
            // No stored composition (stale save edge case): the tile just crumbles to nothing
            // rather than minting dust that was never paid in.
            return;
        }
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
                if (((dx + dy) & 1) == 0)
                    Place(c0x + dx, c0y + dy, Material.Dust, src);
    }

    /// <summary>Fill every sub-cell of the polar tile with material — used for water seeding
    /// at world start, where the random scatter of <see cref="SpawnInTile"/> would leave
    /// lakes visibly frothy with holes.</summary>
    public void FillTile(int tx, int ty, Material m)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
                Place(c0x + dx, c0y + dy, m);
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
        if (_mat[idx] != 0) Enqueue(idx);
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

    /// <summary>Move from (sCx,sCy) to (dCx,dCy). Both rows are bounds-checked; angles wrap.
    /// <paramref name="wakeSource"/> false = this is an intermediate hop of a multi-step move
    /// this tick: the cell being vacated was empty before the tick started (the mover only
    /// transited it), so the neighbourhood's net state is unchanged and no wake is owed —
    /// only the *original* departure cell is a real change. At terminal velocity this cuts
    /// the wake fan-out of a falling grain by up to 8×.</summary>
    private bool TryMoveTo(int sCx, int sCy, int dCx, int dCy, bool wakeSource = true)
    {
        if (dCy < 0 || dCy >= Height) return false;
        if (IsTileSolidAt(dCx, dCy)) return false;
        var di = Idx(dCx, dCy);
        if (_mat[di] != 0) return false;
        var si = Idx(sCx, sCy);
        var m = _mat[si];
        _mat[di] = m;
        _srcTile[di] = _srcTile[si];
        _velR[di] = _velR[si];
        _travel[di] = _travel[si];
        _flow[di] = _flow[si];
        _mat[si] = 0;
        _srcTile[si] = 0;
        ClearKinetics(si);
        Enqueue(di);
        if (wakeSource) WakeNeighbors(sCx, sCy);
        // Vacating always wakes (neighbours may now move into the hole), but *arriving* only
        // matters when the arriver can trigger a reaction in a sleeping neighbour: water must
        // wake hemmed lava it lands on (quench is lava-side — the sleep clause documents
        // relying on exactly this wake). Lava kept out of caution for bulk flows. Inert grains
        // — the entire mass-break dust case — skip it, halving the wake fan-out per move.
        if (m == (byte)Material.Water || m == (byte)Material.Lava)
            WakeNeighbors(dCx, dCy);
        return true;
    }

    public void Update(float dt)
    {
        _time += dt;

        if (_time >= _compactSweepAt)
        {
            _compactSweepAt = _time + CompactSweepPeriod;
            SweepCompaction();
        }

        (_active, _next) = (_next, _active);
        _next.Clear();
        if (_active.Count == 0) return;

        // Clear the queued flags up front (so any cell can re-enqueue itself or be woken
        // again during this tick), then process in shuffled order — a fixed visit order
        // would bias flows sideways.
        var snapshot = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_active);
        foreach (var idx in snapshot) _queued[idx] = false;
        Shuffle(snapshot);

        foreach (var idx in snapshot)
        {
            var m = (Material)_mat[idx];
            if (m == Material.Empty) continue;
            var (cx, cy) = UnIdx(idx);
            switch (m)
            {
                case Material.Sand:
                case Material.Dirt:
                case Material.Gravel:
                case Material.Dust:
                    TickSand(cx, cy, dt);
                    break;
                case Material.Water: TickLiquid(cx, cy, dt); break;
                case Material.Lava:  TickLava(cx, cy, dt); break;
                case Material.Acid:  TickAcid(cx, cy, dt); break;
                case Material.Gas:   TickGas(cx, cy); break;
                case Material.Smoke: TickSmoke(cx, cy); break;
            }
        }
    }

    /// <summary>Decompose a flat cell index back to (cx, cy): coarse block lookup, then a
    /// short forward walk. Runs once per active cell per tick, so the storm ticks after a
    /// mass break call it tens of thousands of times — the block table turns the old 12-step
    /// binary search into ≤5 sequential, predictable steps (rows are ≥ ~1000 cells wide, so
    /// one 4096-cell block spans at most a handful of rows).</summary>
    private (int cx, int cy) UnIdx(int idx)
    {
        var lo = _rowOfBlock[idx >> RowBlockShift];
        while (_rowOffsets[lo + 1] <= idx) lo++;
        return (idx - _rowOffsets[lo], lo);
    }

    /// <summary>Chance (percent) that a resting grain takes an available diagonal slip on a
    /// given tick. Failing the roll leaves it asleep until a neighbour disturbs it. Sand
    /// pours loosest; gravel locks up quickest; dirt and tile dust sit between.</summary>
    private static int SlipChance(Material m) => m switch
    {
        Material.Sand => 80,
        Material.Gravel => 35,
        _ => 55, // Dirt, Dust
    };

    private void TickSand(int cx, int cy, float dt)
    {
        if (cy <= 0) return;
        var i = Idx(cx, cy);
        var (icx, icy) = InnerCell(cx, cy);

        if (IsBlocked(icx, icy))
        {
            // Resting: angle-of-repose slip with granular flavour. One random side per tick
            // (not both), the grain must fit past the side cell on its own row (no squeezing
            // through sealed corner gaps), and per-material friction lets grains simply hold
            // where they lie. A grain that holds goes back to sleep untried — piles keep
            // their craggy, interlocked shape until a neighbour's move wakes them again,
            // instead of levelling out like a liquid.
            _velR[i] = 0f;
            _travel[i] = 0f;
            var d = _rng.Next(2) == 0 ? 1 : -1;
            if (_rng.Next(100) < SlipChance((Material)_mat[i]) && !IsBlocked(cx + d, cy)
                && TryMoveTo(cx, cy, icx + d, icy))
                return;
            RecordRest(cx, cy);   // held in place — its tile is a compaction candidate
            return;
        }

        // Freefall: accelerate inward and traverse multiple rows per tick at speed.
        // Rows are stepped one at a time because InnerCell remaps cx at band-halving
        // boundaries, and per-step collision prevents tunneling through thin floors.
        // A just-freed grain (velR still 0) gets an initial kick so it drops on this very
        // tick instead of hanging while gravity ramps it up from a standstill.
        if (_velR[i] == 0f) _velR[i] = InitialFallCells;
        _velR[i] = MathF.Min(_velR[i] + GravityCells * dt, TerminalCells);
        _travel[i] += _velR[i] * dt;
        var steps = Math.Min((int)_travel[i], MaxStepsPerTick);
        _travel[i] = MathF.Min(_travel[i] - steps, 1f);
        if (steps == 0) { Enqueue(i); return; } // still gaining speed — stay awake

        for (var s = 0; s < steps && cy > 0; s++)
        {
            (icx, icy) = InnerCell(cx, cy);
            if (TryMoveTo(cx, cy, icx, icy, s == 0)) { cx = icx; cy = icy; continue; }
            // Blocked mid-flight: deflect diagonally, bleeding off speed on the impact.
            var d = _rng.Next(2) == 0 ? 1 : -1;
            if (TryMoveTo(cx, cy, icx + d, icy, s == 0)) { _velR[Idx(icx + d, icy)] *= ImpactDamping; return; }
            if (TryMoveTo(cx, cy, icx - d, icy, s == 0)) { _velR[Idx(icx - d, icy)] *= ImpactDamping; return; }
            // Landed hard: kill velocity so the cell can sleep.
            i = Idx(cx, cy);
            _velR[i] = 0f;
            _travel[i] = 0f;
            RecordRest(cx, cy);
            return;
        }
        // Covered the full distance without obstruction — TryMoveTo kept the cell awake.
    }

    /// <summary>Note the owning planet tile of a grain that just came to rest — and the
    /// tile below it, whose burial pressure this grain's weight may have just tipped past
    /// the compaction threshold (nothing else would ever re-nominate a tile whose own
    /// grains went to sleep long ago).</summary>
    private void RecordRest(int cx, int cy)
    {
        var tx = cy / Density;
        var ty = WrapX(cx, _cellsAt[cy]) / Density;
        RecordRestTile(Planet.Index(tx, ty));
        if (tx > 0)
        {
            var (ix, iy) = Planet.InnerNeighbour(tx, ty);
            RecordRestTile(Planet.Index(ix, iy));
        }
    }

    private void RecordRestTile(int idx)
    {
        if (_restStamp[idx] == _restGen) return;
        _restStamp[idx] = _restGen;
        _restList.Add(idx);
    }

    /// <summary>Compaction sweep (every <see cref="CompactSweepPeriod"/>): convert candidate
    /// tiles whose second look finds them unchanged, then promote freshly rested tiles to
    /// candidates. Both passes are bounded by actual grain churn, not world size.</summary>
    private void SweepCompaction()
    {
        if (_compacting.Count > 0)
        {
            _compactScratch.Clear();
            foreach (var (idx, (fill, due)) in _compacting)
                if (_time >= due) _compactScratch.Add(idx);
            foreach (var idx in _compactScratch)
            {
                var fill = _compacting[idx].fill;
                _compacting.Remove(idx);
                var (tx, ty) = Planet.UnIndex(idx);
                // Convert only if the fill is exactly what it was a full delay ago — any
                // grain that arrived or left since means the pile is still live. A tile
                // that changed but is still eligible re-arms with its current fill rather
                // than dropping out: the rest events that disturbed it were swallowed
                // while it was a candidate, so nothing else would ever re-nominate it.
                var now = CompactableFill(tx, ty);
                if (now == fill) Compact(tx, ty);
                else if (now > 0)
                    _compacting[idx] = (now, _time + CompactDelay / (1f + PressureAbove(tx, ty)));
            }
        }

        if (_restList.Count == 0) return;
        foreach (var idx in _restList)
        {
            if (_compacting.ContainsKey(idx)) continue;
            var (tx, ty) = Planet.UnIndex(idx);
            var fill = CompactableFill(tx, ty);
            // Weight above shortens the wait: a lone buried tile takes the full delay,
            // the bottom of a CompactPressureCap-deep pile ~1/5th of it.
            if (fill > 0) _compacting[idx] = (fill, _time + CompactDelay / (1f + PressureAbove(tx, ty)));
        }
        _restList.Clear();
        _restGen++;
    }

    /// <summary>Occupied-cell count if the tile at (tx,ty) is eligible to compact, else -1.
    /// Always required: open (Sky) tile, only compactable grain (any liquid/gas
    /// disqualifies), at least half full, and not within
    /// <see cref="CompactExclusionRadius"/> of the player. Then one of two burial proofs:
    /// - pressed: at least <see cref="CompactPressureMin"/> tiles of loose grains stacked
    ///   above. Naturally settled piles interlock with voids and craggy tops, so this is
    ///   their only route — Compact's steal pass fills the voids from that same column; or
    /// - sealed: on a solid floor, near-full (<see cref="CompactMinFill"/>), every
    ///   outer-edge cell roofed — the original rule, still what lets a dust-packed stone
    ///   pocket re-form.</summary>
    private int CompactableFill(int tx, int ty)
    {
        if (tx <= 0 || Planet.Get(tx, ty) != TileKind.Sky) return -1;
        if (CompactionExclusion is { } avoid
            && Vector2.DistanceSquared(Planet.TileToWorld(tx, ty), avoid)
               < CompactExclusionRadius * CompactExclusionRadius)
            return -1;

        var c0y = tx * Density;
        var c0x = ty * Density;
        var fill = 0;
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
            {
                var m = (Material)_mat[Idx(c0x + dx, c0y + dy)];
                if (m == Material.Empty) continue;
                if (!Materials.IsCompactable(m)) return -1;
                fill++;
            }
        if (fill < CompactPressedMinFill) return -1;
        // Pressed tiles skip the solid-floor test: piles arch over hollows, and a column
        // that bridges one would otherwise strand every layer above it. If a pressed
        // block is later undercut it's Conglomerate — IsLoose — and crumbles back out.
        if (PressureAbove(tx, ty) >= CompactPressureMin) return fill;
        if (!Tiles.IsSolid(Planet.Get(Planet.InnerNeighbour(tx, ty).x, Planet.InnerNeighbour(tx, ty).y)))
            return -1;
        if (fill < CompactMinFill) return -1;

        // Sealed: every cell along the tile's outer edge has something (cell or solid tile)
        // directly above it, so this is pile interior — never the loose, visible crest.
        var topRow = c0y + Density - 1;
        for (var dx = 0; dx < Density; dx++)
        {
            var (ocx, ocy) = OuterCell(c0x + dx, topRow);
            if (!IsBlocked(ocx, ocy)) return -1;
        }
        return fill;
    }

    /// <summary>Loose compactable grains stacked directly above the tile, in tiles' worth
    /// (16 cells = 1.0), capped at <see cref="CompactPressureCap"/>. Each of the tile's cell
    /// columns is walked outward until it leaves the grain column — deliberately counting
    /// only real grains, not solid roofs, so a sealed stone pocket keeps the stricter
    /// near-full rule and a half-empty one stays dust ("not enough sand stays sand").</summary>
    private float PressureAbove(int tx, int ty)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        var topRow = c0y + Density - 1;
        var total = 0;
        const int maxRise = Density * CompactPressureCap;
        for (var dx = 0; dx < Density; dx++)
        {
            var cx = c0x + dx;
            var cy = topRow;
            for (var k = 0; k < maxRise; k++)
            {
                (cx, cy) = OuterCell(cx, cy);
                if (cy >= Height) break;
                if (!Materials.IsCompactable((Material)_mat[Idx(cx, cy)])) break;
                total++;
            }
        }
        return total / (float)(Density * Density);
    }

    /// <summary>Press the tile's grains into a solid tile of whatever kind the majority of
    /// them came from — stone dust re-forms stone, dirt beds back into dirt, mixed piles go
    /// to the biggest constituent. Voids are filled by pulling grains down from the column
    /// above (compression — the pile visibly settles), never invented. (Old saves may still
    /// hold composition-backed Conglomerate tiles; their spill path in SpawnDustInTile
    /// remains for those.)</summary>
    private void Compact(int tx, int ty)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        var counts = new Dictionary<(byte mat, byte src), byte>();
        var n = 0;
        void Absorb(int i)
        {
            var m = (Material)_mat[i];
            var key = ((byte)m, _srcTile[i]);
            counts.TryGetValue(key, out var c);
            counts[key] = (byte)(c + 1);
            n++;
            _mat[i] = 0;
            _srcTile[i] = 0;
            ClearKinetics(i);
        }
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
            {
                var i = Idx(c0x + dx, c0y + dy);
                if ((Material)_mat[i] != Material.Empty) { Absorb(i); continue; }
                // Void: press a grain down out of the column above this cell. Grains can
                // arc over interior gaps, so empties along the way are skipped; anything
                // non-grain seals the column and the void just stays a void.
                var cx = c0x + dx;
                var cy = c0y + Density - 1;
                for (var k = 0; k < Density * CompactPressureCap; k++)
                {
                    (cx, cy) = OuterCell(cx, cy);
                    if (cy >= Height || (IsBlocked(cx, cy) && _mat[Idx(cx, cy)] == 0)) break;
                    var gi = Idx(cx, cy);
                    var gm = (Material)_mat[gi];
                    if (gm == Material.Empty) continue;
                    if (!Materials.IsCompactable(gm)) break;
                    Absorb(gi);
                    // The theft leaves a hole mid-pile: wake the neighbourhood so grains
                    // above sift down, and let the donor tile earn a fresh window.
                    WakeNeighbors(cx, cy);
                    RecordRest(cx, cy);
                    break;
                }
            }
        if (n == 0) return;

        var best = ((byte)0, (byte)0);
        var bestCount = 0;
        foreach (var (key, count) in counts)
            if (count > bestCount) { bestCount = count; best = key; }
        Planet.Set(tx, ty, MajorityKind((Material)best.Item1, (TileKind)best.Item2));

        // The grains above now rest on a solid tile, but they're asleep and will never
        // fire RecordRest again — re-nominate the outer neighbours so a buried pile keeps
        // converting layer by layer instead of stopping at the bottom row. Partially
        // filled tiles fail CompactableFill and simply stay loose sand.
        for (var w = 0; w < Planet.OuterNeighbourCount(tx, ty); w++)
        {
            var (ox, oy) = Planet.OuterNeighbour(tx, ty, w);
            RecordRestTile(Planet.Index(ox, oy));
        }
    }

    /// <summary>Tile kind a compacted grain votes for. Tagged dust re-forms its source tile
    /// (grass beds down to plain dirt underground); loose dirt/gravel keep their kind; sand
    /// and untagged dust read as coarse grit — Gravel is the nearest granular rock.</summary>
    private static TileKind MajorityKind(Material m, TileKind src) => m switch
    {
        Material.Dust when src == TileKind.Grass => TileKind.Dirt,
        Material.Dust when src != TileKind.Sky && src != TileKind.Conglomerate => src,
        Material.Dirt => TileKind.Dirt,
        _ => TileKind.Gravel,   // Sand, Gravel, untagged/legacy dust
    };

    private void TickLiquid(int cx, int cy, float dt)
    {
        var i = Idx(cx, cy);
        var impact = 0f;

        var (bcx, bcy) = InnerCell(cx, cy);
        if (cy > 0 && !IsBlocked(bcx, bcy))
        {
            // Airborne: same fall integrator as sand.
            _velR[i] = MathF.Min(_velR[i] + GravityCells * dt, TerminalCells);
            _travel[i] += _velR[i] * dt;
            var steps = Math.Min((int)_travel[i], MaxStepsPerTick);
            _travel[i] = MathF.Min(_travel[i] - steps, 1f);
            if (steps == 0) { Enqueue(i); return; }

            for (var s = 0; s < steps && cy > 0; s++)
            {
                var (icx, icy) = InnerCell(cx, cy);
                if (TryMoveTo(cx, cy, icx, icy, s == 0)) { cx = icx; cy = icy; continue; }
                var dd = _rng.Next(2) == 0 ? 1 : -1;
                if (TryMoveTo(cx, cy, icx + dd, icy, s == 0)) { cx = WrapX(icx + dd, CellsAt(icy)); cy = icy; continue; }
                if (TryMoveTo(cx, cy, icx - dd, icy, s == 0)) { cx = WrapX(icx - dd, CellsAt(icy)); cy = icy; continue; }
                // Hit the surface: remaining fall speed becomes lateral splash below.
                i = Idx(cx, cy);
                impact = _velR[i];
                _velR[i] = 0f;
                _travel[i] = 0f;
                break;
            }
            if (impact == 0f) { Enqueue(Idx(cx, cy)); return; } // still airborne
        }
        else
        {
            impact = _velR[i];
            _velR[i] = 0f;
            _travel[i] = 0f;
        }

        // Supported: diagonal-down slip first, as before.
        if (cy > 0)
        {
            var (icx, icy) = InnerCell(cx, cy);
            var dd = _rng.Next(2) == 0 ? 1 : -1;
            if (TryMoveTo(cx, cy, icx + dd, icy)) return;
            if (TryMoveTo(cx, cy, icx - dd, icy)) return;
        }

        // Fully hemmed-in liquid sleeps: supported below and walled both sides (rock or other
        // liquid) means it cannot move until a neighbour changes — and any change wakes it via
        // WakeNeighbors. Keeps large pools from ticking every interior cell forever. Lava
        // additionally stays awake while any adjacent tile is meltable, so pooled lava still
        // eats through dirt floors and roofs; quenching needs no wakeup because arriving
        // water is itself a neighbour change.
        i = Idx(cx, cy);
        var self = (Material)_mat[i];
        if (self == Material.Water || self == Material.Lava || self == Material.Acid)
        {
            var (scx, scy) = InnerCell(cx, cy);
            var hemmed = (cy <= 0 || IsBlocked(scx, scy)) && IsBlocked(cx - 1, cy) && IsBlocked(cx + 1, cy);
            // Lava and acid stay awake while there's still something adjacent to eat, so a
            // hemmed pool keeps gnawing at the tile it rests against instead of sleeping on it.
            var stillEating = (self == Material.Lava && HasMeltableNeighbour(cx, cy))
                           || (self == Material.Acid && HasCorrodibleNeighbour(cx, cy));
            if (hemmed && !stillEating) return;
        }

        // Lateral dispersion: flow several cells per tick in a persistent direction so pools
        // level out quickly, plus a splash bonus proportional to how hard the cell just landed.
        var dir = (int)_flow[i];
        if (dir == 0) dir = _rng.Next(2) == 0 ? 1 : -1;
        var spread = LiquidDispersion + (int)(impact * SplashScale);
        var bounced = false;
        var movedYet = false;   // a bounce advances s without moving, so s==0 can't stand in for "still at the departure cell"
        for (var s = 0; s < spread; s++)
        {
            if (TryMoveTo(cx, cy, cx + dir, cy, !movedYet))
            {
                movedYet = true;
                cx = WrapX(cx + dir, CellsAt(cy));
                // Found an edge to pour over — let gravity take it next tick.
                var (icx, icy) = InnerCell(cx, cy);
                if (cy > 0 && !IsBlocked(icx, icy)) break;
                continue;
            }
            if (bounced) break;
            bounced = true;
            dir = -dir;
        }
        _flow[Idx(cx, cy)] = (sbyte)dir;
        Enqueue(Idx(cx, cy)); // liquids stay awake, as before
    }

    private void TickLava(int cx, int cy, float dt)
    {
        if (QuenchIfWet(cx, cy)) return;

        TryMelt(InnerCell(cx, cy));
        TryMelt((cx + 1, cy));
        TryMelt((cx - 1, cy));
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++) TryMelt(OuterCell(cx, cy, i));
        }

        if (_rng.Next(2) == 0) { Enqueue(Idx(cx, cy)); return; }
        TickLiquid(cx, cy, dt);
    }

    /// <summary>Water contact: the lava cell cools to gravel and the touching water cell
    /// flashes to smoke (steam) — one-for-one, so lava/water fronts eat each other and leave
    /// a rubble crust behind. Probabilistic per tick so the reaction sizzles over several
    /// frames instead of converting a whole front instantly.</summary>
    private bool QuenchIfWet(int cx, int cy)
    {
        var wi = FindNeighbour(cx, cy, Material.Water);
        if (wi < 0) return false;
        if (_rng.Next(3) != 0) { Enqueue(Idx(cx, cy)); return true; } // stay awake, react soon

        _mat[wi] = (byte)Material.Smoke;
        _srcTile[wi] = 0;
        ClearKinetics(wi);
        Enqueue(wi);
        var (wcx, wcy) = UnIdx(wi);
        WakeNeighbors(wcx, wcy);

        var i = Idx(cx, cy);
        _mat[i] = (byte)Material.Gravel;
        _srcTile[i] = 0;
        ClearKinetics(i);
        Enqueue(i);
        WakeNeighbors(cx, cy);
        return true;
    }

    /// <summary>Flat index of the first cardinal neighbour holding material m, or -1.</summary>
    private int FindNeighbour(int cx, int cy, Material m)
    {
        if (Get(cx + 1, cy) == m) return Idx(cx + 1, cy);
        if (Get(cx - 1, cy) == m) return Idx(cx - 1, cy);
        if (cy > 0)
        {
            var (icx, icy) = InnerCell(cx, cy);
            if (Get(icx, icy) == m) return Idx(icx, icy);
        }
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var k = 0; k < oc; k++)
            {
                var (ocx, ocy) = OuterCell(cx, cy, k);
                if (Get(ocx, ocy) == m) return Idx(ocx, ocy);
            }
        }
        return -1;
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
        SpawnInTile(tx, ty, Material.Smoke, Density); // scaled so a melt puff reads the same at any grain size
        if (_rng.Next(3) == 0) SpawnInTile(tx, ty, Material.Lava, Density / 4);
    }

    private static bool IsMeltable(TileKind k) => k is
        TileKind.Dirt or TileKind.Grass or TileKind.Gravel or
        TileKind.MossStone or TileKind.Snow or TileKind.Support or
        TileKind.Conglomerate;

    /// <summary>Tiles acid can dissolve — most materials, ores included (spilled acid
    /// destroying a vein is a real hazard). Only obsidian and the anchor-class tiles
    /// (planet core, bedrock) resist, so acid melts through nearly anything you'd build or
    /// mine but still can't chew an unbounded hole into the deep obsidian crust.</summary>
    private static bool IsCorrodible(TileKind k) =>
        Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian;

    /// <summary>Acid: corrode a touching soft tile now and then, otherwise flow like water.
    /// Mirrors <see cref="TickLava"/>'s structure (eat-then-flow) but with no self-light and a
    /// different tile set. Non-depleting like lava — it sleeps once hemmed with nothing left
    /// to eat (see the sleep clause in <see cref="TickLiquid"/>).</summary>
    private void TickAcid(int cx, int cy, float dt)
    {
        TryCorrode(InnerCell(cx, cy));
        TryCorrode((cx + 1, cy));
        TryCorrode((cx - 1, cy));
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++) TryCorrode(OuterCell(cx, cy, i));
        }

        if (_rng.Next(2) == 0) { Enqueue(Idx(cx, cy)); return; }
        TickLiquid(cx, cy, dt);
    }

    private void TryCorrode((int cx, int cy) c)
    {
        // Faster than the old sizzle — acid is meant to MELT through, not tickle.
        if (_rng.Next(45) != 0) return;
        if (c.cy < 0 || c.cy >= Height) return;
        var tx = c.cy / Density;
        var ty = WrapX(c.cx, _cellsAt[c.cy]) / Density;
        var k = Planet.Get(tx, ty);
        if (!IsCorrodible(k)) return;
        Planet.Set(tx, ty, TileKind.Sky);
        SpawnInTile(tx, ty, Material.Smoke, Density / 2); // acrid fizz
    }

    private bool HasCorrodibleNeighbour(int cx, int cy)
    {
        if (IsCorrodible(TileAt(cx + 1, cy)) || IsCorrodible(TileAt(cx - 1, cy))) return true;
        var (icx, icy) = InnerCell(cx, cy);
        if (IsCorrodible(TileAt(icx, icy))) return true;
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++)
            {
                var (ocx, ocy) = OuterCell(cx, cy, i);
                if (IsCorrodible(TileAt(ocx, ocy))) return true;
            }
        }
        return false;
    }

    /// <summary>Flammable gas: rise outward like smoke, but flash-burn to smoke the moment it
    /// touches lava — a gas pocket you tunnel lava into (or vent onto the surface toward a lava
    /// flow) goes up in a puff. Lingers far longer than smoke so pockets survive to be found.</summary>
    private void TickGas(int cx, int cy)
    {
        // Ignition: any adjacent lava sets it off. Convert to smoke; neighbours are woken by
        // the tile change, so an adjacent gas cell ignites on its own next tick — the burn
        // walks through the cloud one ring per tick.
        if (FindNeighbour(cx, cy, Material.Lava) >= 0)
        {
            var bi = Idx(cx, cy);
            _mat[bi] = (byte)Material.Smoke;
            ClearKinetics(bi);
            Enqueue(bi);
            WakeNeighbors(cx, cy);
            return;
        }

        // Rising movement — outward (away from core), same probes as smoke.
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            var (ocx, ocy) = OuterCell(cx, cy, _rng.Next(oc));
            if (TryMoveTo(cx, cy, ocx, ocy)) return;
            var first = _rng.Next(2) == 0 ? 1 : -1;
            if (TryMoveTo(cx, cy, ocx + first, ocy)) return;
            if (TryMoveTo(cx, cy, ocx - first, ocy)) return;
            // Trapped under a roof: seep sideways so a pocket spreads along the ceiling.
            if (TryMoveTo(cx, cy, cx + first, cy)) return;
            if (TryMoveTo(cx, cy, cx - first, cy)) return;
        }
        // Very slow dissipation so pockets persist until disturbed (smoke fades ~50× faster).
        if (_rng.Next(2200) == 0)
        {
            var i = Idx(cx, cy);
            _mat[i] = 0;
            ClearKinetics(i);
            WakeNeighbors(cx, cy);
        }
        else Enqueue(Idx(cx, cy));
    }

    /// <summary>Tile kind under the given cell (Sky when off-grid).</summary>
    private TileKind TileAt(int cx, int cy) =>
        (cy < 0 || cy >= Height) ? TileKind.Sky : Planet.Get(cy / Density, WrapX(cx, _cellsAt[cy]) / Density);

    /// <summary>True if any cardinal-neighbour cell of (cx,cy) sits in a meltable tile —
    /// gates the lava sleep so pooled lava keeps gnawing at dirt it touches.</summary>
    private bool HasMeltableNeighbour(int cx, int cy)
    {
        if (IsMeltable(TileAt(cx + 1, cy)) || IsMeltable(TileAt(cx - 1, cy))) return true;
        var (icx, icy) = InnerCell(cx, cy);
        if (IsMeltable(TileAt(icx, icy))) return true;
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++)
            {
                var (ocx, ocy) = OuterCell(cx, cy, i);
                if (IsMeltable(TileAt(ocx, ocy))) return true;
            }
        }
        return false;
    }

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
            ClearKinetics(i);
            WakeNeighbors(cx, cy);
        }
        else Enqueue(Idx(cx, cy));
    }

    private void Shuffle(Span<int> arr)
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
                    _dustAccum[d.id] = existing + (float)d.count / DustCellsPerDrop;
                }
                _mat[idx] = 0;
                _srcTile[idx] = 0;
                ClearKinetics(idx);
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

    /// <summary>Hazardous cell counts within a world-space radius — the body-contact probe the
    /// player/creatures use to take lava/acid burn and gas choke. Same polar row/col walk as
    /// <see cref="CollectInRadius"/>, counting only the three hazard materials.</summary>
    public (int lava, int acid, int gas) SampleHazardsNear(Vector2 worldPos, float radius)
    {
        var rSq = radius * radius;
        var (_, cy0) = WorldToCell(worldPos);
        var radial = (float)Planet.TileSize / Density;
        var rRows = (int)MathF.Ceiling(radius / radial) + 1;
        var rel = worldPos - Planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        int lava = 0, acid = 0, gas = 0;
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
                var m = (Material)_mat[Idx(cx0 + dx, cy)];
                if (m != Material.Lava && m != Material.Acid && m != Material.Gas) continue;
                if (Vector2.DistanceSquared(CellToWorld(cx0 + dx, cy), worldPos) > rSq) continue;
                if (m == Material.Lava) lava++;
                else if (m == Material.Acid) acid++;
                else gas++;
            }
        }
        return (lava, acid, gas);
    }

    /// <summary>Flash-burn any gas cells within a small world-space radius (Godzilla's breath
    /// igniting a pocket). Gas → smoke, waking neighbours so the burn chains through the cloud
    /// on subsequent ticks exactly like a lava-triggered ignition.</summary>
    public void IgniteGasNear(Vector2 worldPos, float radius)
    {
        var (cx0, cy0) = WorldToCell(worldPos);
        var radial = (float)Planet.TileSize / Density;
        var rRows = (int)MathF.Ceiling(radius / radial) + 1;
        var rSq = radius * radius;
        for (var dy = -rRows; dy <= rRows; dy++)
        {
            var cy = cy0 + dy;
            if (cy < 0 || cy >= Height) continue;
            for (var dx = -rRows; dx <= rRows; dx++)
            {
                var cx = cx0 + dx;
                var idx = Idx(cx, cy);
                if ((Material)_mat[idx] != Material.Gas) continue;
                if (Vector2.DistanceSquared(CellToWorld(cx, cy), worldPos) > rSq) continue;
                _mat[idx] = (byte)Material.Smoke;
                ClearKinetics(idx);
                Enqueue(idx);
                WakeNeighbors(cx, cy);
            }
        }
    }

    /// <summary>Row window + camera polar coords for the view circle, shared by the culled
    /// draw/light passes. Iterating the whole grid stopped being an option at Density 8
    /// (~10M cells), so both passes walk only rows and arcs that can intersect the view.</summary>
    private (int cyMin, int cyMax) VisibleRows(Vector2 viewCentre, float viewRadius, out float camAng)
    {
        var rel = viewCentre - Planet.Center;
        var camDist = rel.Length();
        camAng = MathF.Atan2(rel.Y, rel.X);
        if (camAng < 0) camAng += MathHelper.TwoPi;
        var radial = (float)Planet.TileSize / Density;
        var inner = Planet.RingMin * Planet.TileSize;
        var cyMin = Math.Max(0, (int)((camDist - viewRadius - inner) / radial));
        var cyMax = Math.Min(Height - 1, (int)((camDist + viewRadius - inner) / radial) + 1);
        return (cyMin, cyMax);
    }

    /// <summary>Draw the live cells around the view. <paramref name="stride"/> &gt; 1 is
    /// the zoomed-out LOD (orbit/high descent): sample every Nth cell on both axes at N×
    /// size and skip rows buried deep in the crust — at orbital view radii the full scan
    /// touches millions of cells, and a 3-px tile can't show sub-tile grains anyway.</summary>
    public void Draw(Renderer r, Vector2 viewCentre, float viewRadius, int stride = 1)
    {
        var radial = (float)Planet.TileSize / Density;
        var (cyMin, cyMax) = VisibleRows(viewCentre, viewRadius, out var camAng);
        if (stride > 1) cyMin = Math.Max(cyMin, 120 * Density);   // matches the tile LOD's interior cut
        for (var cy = cyMin; cy <= cyMax; cy += stride)
        {
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            var size = new Vector2((chord + 0.5f) * stride, (radial + 0.5f) * stride);
            var halfAng = MathF.Min(MathF.PI, viewRadius / MathF.Max(ringRadius, 1f));
            var cx0 = (int)(camAng / MathHelper.TwoPi * n);
            var range = Math.Min(n / 2, (int)(halfAng / MathHelper.TwoPi * n) + 2);
            var angStep = MathHelper.TwoPi / n;
            for (var d = -range; d <= range; d += stride)
            {
                var cx = cx0 + d;
                var idx = Idx(cx, cy);
                var m = (Material)_mat[idx];
                if (m == Material.Empty) continue;
                // Analytic polar transform — one cos+sin per drawn cell instead of
                // CellToWorld's wrap+trig plus UpAt's normalize plus an Atan2: the cell
                // angle is already fixed by the loop index (cos/sin ignore the wrap), and
                // a radial quad's rotation is just that angle + 90°.
                var cellAng = (cx + 0.5f) * angStep;
                var up = new Vector2(MathF.Cos(cellAng), MathF.Sin(cellAng));
                var centre = Planet.Center + up * ringRadius;
                var rotation = cellAng + MathHelper.PiOver2;
                // Sub-cell offset: _travel is progress toward the next inward row, so falling
                // cells glide smoothly between rows instead of ticking a whole cell at a time.
                var frac = MathF.Min(_travel[idx], 1f);
                if (frac > 0f) centre -= up * (frac * radial);
                var col = ColorFor(m, cx, cy, _srcTile[idx]);
                // Waterline: water open to air above draws as a brighter band that bobs with
                // a travelling wave, so pools get a live surface instead of a flat blue slab.
                if (m == Material.Water)
                {
                    var (ocx, ocy) = OuterCell(cx, cy, 0);
                    if (!IsBlocked(ocx, ocy))
                    {
                        var wave = MathF.Sin(_time * 2.4f - WrapX(cx, n) * 0.55f + cy * 0.2f);
                        col = Tint(new Color(96, 158, 214), (int)(wave * 14f)) * 0.9f;
                        centre += up * (wave * 0.35f);
                    }
                }
                r.Batch.Draw(r.Pixel, centre, null, col, rotation,
                    new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Lava/acid glow emitters. Same LOD contract as <see cref="Draw"/> — the
    /// zoomed-out stride keeps the scan (and the per-light lightmap blits) bounded.</summary>
    public void AddLights(Renderer r, Vector2 viewCentre, float viewRadius, int stride = 1)
    {
        var (cyMin, cyMax) = VisibleRows(viewCentre, viewRadius, out var camAng);
        if (stride > 1) cyMin = Math.Max(cyMin, 120 * Density);
        var step = 0;
        for (var cy = cyMin; cy <= cyMax; cy += stride)
        {
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var halfAng = MathF.Min(MathF.PI, viewRadius / MathF.Max(ringRadius, 1f));
            var cx0 = (int)(camAng / MathHelper.TwoPi * n);
            var range = Math.Min(n / 2, (int)(halfAng / MathHelper.TwoPi * n) + 2);
            for (var d = -range; d <= range; d += stride)
            {
                var cx = cx0 + d;
                var lm = (Material)_mat[Idx(cx, cy)];
                if (lm != Material.Lava && lm != Material.Acid) continue;
                // Skip interior pool cells whose four cardinal neighbours are all blocked.
                var (icx, icy) = InnerCell(cx, cy);
                var allBlocked = IsBlocked(cx - 1, cy) && IsBlocked(cx + 1, cy) && IsBlocked(icx, icy);
                if (allBlocked && cy < Height - 1)
                {
                    var (ocx, ocy) = OuterCell(cx, cy, 0);
                    if (IsBlocked(ocx, ocy)) continue;
                }
                // Thin the emitters with grain resolution — a pool surface has Density/2 more
                // cells per world unit than the old 2-px grid, and each light is a lightmap blit.
                if ((step++ % (Density / 2)) != 0) continue;
                // Acid glows a dim toxic green; lava a hot orange.
                r.AddLight(CellToWorld(cx, cy), lm == Material.Acid ? 7f : 10f,
                    lm == Material.Acid ? new Color(90, 190, 40) : new Color(255, 130, 40));
            }
        }
    }

    public void FillSkyTilesWithin(float radiusTilesFromCentre, Material m)
    {
        var maxRing = (int)radiusTilesFromCentre - Planet.RingMin;
        if (maxRing <= 0) return;
        maxRing = Math.Min(maxRing, Planet.Rings);
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
            case Material.Water:
            {
                // Translucent body with slow-moving shimmer bands — tiles and back-wall
                // ghost through, and pools read as liquid even while the sim has them
                // asleep (colour is computed at draw time, not sim time).
                var shimmer = (int)(MathF.Sin(_time * 1.6f + ((hash >> 3) & 7) * 0.8f + cy * 0.3f) * 9f);
                return Tint(new Color(46, 90, 178), jitter / 5 + shimmer) * 0.78f;
            }
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
            case Material.Acid:
            {
                // Sickly green with a slow toxic shimmer, semi-translucent like water.
                var shimmer = (int)(MathF.Sin(_time * 2.0f + ((hash >> 3) & 7) * 0.7f + cy * 0.3f) * 12f);
                return Tint(new Color(120, 200, 40), jitter / 5 + shimmer) * 0.82f;
            }
            case Material.Gas:
            {
                // Faint yellow-green haze — low alpha so the wall behind reads through it.
                var swirl = (int)(MathF.Sin(_time * 1.2f + ((hash >> 2) & 15) * 0.5f) * 10f);
                var g = new Color(150, 190, 70);
                return new Color(g.R + jitter / 5, g.G + jitter / 5 + swirl, g.B + jitter / 5, (byte)110);
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
