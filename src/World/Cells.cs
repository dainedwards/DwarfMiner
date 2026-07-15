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
    /// air) and ignites into a rolling flame front when it meets lava or open fire.</summary>
    Gas = 9,
    /// <summary>Open flame. Short-lived cells that flicker upward, catch adjacent oil/gas
    /// alight, char flammable tiles, spit glowing embers, and gutter to smoke — instantly
    /// steam-quenched by water. Not conserved matter: fire is emitted freely and decays.</summary>
    Fire = 10,
    /// <summary>Flammable liquid. Flows like water but lighter — every other liquid sinks
    /// through it, so oil films collect on top of pools. Inert until a flame or lava tongue
    /// touches it, then it burns away cell by cell.</summary>
    Oil = 11,
    /// <summary>Fallen snow. A powder that drifts and piles like sand; on frost worlds it
    /// lies (and eventually presses into Snow tiles via the compaction sweep), elsewhere an
    /// exposed flake slowly sublimates away. Fire and lava melt it to rain-tagged meltwater,
    /// so slush dries out instead of flooding.</summary>
    Snow = 12,
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
        Material.Sand or Material.Dirt or Material.Gravel or Material.Dust or Material.Snow;
}

/// <summary>
/// Per-cell material grid laid out polar on top of <see cref="Planet"/>. Variable cells per
/// row to mirror Planet's per-band tile halving — each cell row has CellsAt(cy) cells, and
/// "down" (inward radial) maps cell angles between rows that may have different cell counts.
/// </summary>
public sealed class Cells
{
    /// <summary>Cells per tile edge. Tiles are 4 px, so 8 gives HALF-pixel grains — the
    /// Noita-parity resolution: the dwarf (~9 world px) now spans ~18 grains, matching
    /// Noita's ~16-px-tall player in a 1-px world, and grains sit at the same apparent
    /// scale as the terrain atlas texture (~0.25-0.5 px texels). This is 4× the cell count
    /// of the old 1-px grid — the sim and draw loops are view-culled and hemmed liquids
    /// sleep, and `--perf` gates whether that stays affordable. All speed/count tuning
    /// below is Density-relative, so world-space behaviour is unchanged.</summary>
    public const int Density = 8;
    /// <summary>How many dust cells one broken tile spawns — a full Density² fill, so the
    /// loose debris occupies exactly the space the solid block did (mass conserves).</summary>
    public const int DustCellsPerTile = Density * Density;
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
    /// <summary>World px per cell edge — converts cell-sim speeds into world-space speeds.</summary>
    private const float PxPerCell = (float)Planet.TileSize / Density;
    /// <summary>Landing speed (cells/s) above which a liquid may bounce back out as a
    /// ballistic droplet instead of spreading — the splash. Half of terminal, so gentle
    /// pooling never fizzes but a waterfall's plunge pool always does.</summary>
    private const float SplashMinImpact = TerminalCells * 0.5f;

    // --- Flying cells: material mid-flight between grid sites (Noita's "particle" state).
    // A grid cell can be launched with a full 2D world-space velocity — explosion ejecta,
    // splash droplets, dig spray, quench sparks — and it arcs under planet gravity until it
    // hits something, where it re-enters the grid via Place. Mass-conserving for launched
    // cells (LaunchCell removes the grid cell first); fire embers are emitted freely because
    // flame isn't matter. Transient like the kinetics arrays: dropped by WriteState.
    private struct Flying
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public byte Mat;
        public byte Src;
        public float Age;
    }

    private readonly List<Flying> _flying = new(512);
    /// <summary>Hard cap so a cataclysm can't flood the list — launches past it are simply
    /// skipped and the material stays grid-bound (still correct, just less showy).</summary>
    private const int MaxFlying = 4000;
    /// <summary>Same pull as the grid integrator (GravityCells, converted to world px/s²),
    /// so an ejected grain falls exactly like a grid grain.</summary>
    private const float FlyGravity = GravityCells * PxPerCell;
    private const float FlyMaxSpeed = 520f;
    /// <summary>Hard cap on the OUTWARD (away-from-core) speed of a flying cell. FlyMaxSpeed
    /// alone let ejecta rocket ~75 tiles straight up under FlyGravity and hang near its apex
    /// for the better part of a second — a lone 1-px grain drawn floating in the sky, always
    /// on the side the player fired/dug. Capping just the radial-outward component keeps spray
    /// arcing a lively ~8 tiles while making a space-escape impossible; lateral spread and the
    /// inward fall are untouched. See <see cref="UpdateFlying"/>.</summary>
    private const float FlyMaxOutward = 170f;
    /// <summary>Safety net: a cell that somehow never lands banks itself after this long.</summary>
    private const float FlyMaxAge = 6f;
    /// <summary>Flying cells currently airborne — diagnostics/perf only.</summary>
    public int FlyingCellCount => _flying.Count;
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
    /// <summary>Cells the sim will tick next frame — the wake-set size. Steady-state should be
    /// near zero once seeded liquids settle; a persistently high count means something (e.g.
    /// acid still eating tiles) never goes to sleep. Diagnostics/perf only.</summary>
    public int ActiveCellCount => _next.Count;
    /// <summary>True while the cell index is already queued in <see cref="_next"/>.</summary>
    private readonly bool[] _queued;
    private readonly Random _rng = new();
    private readonly Dictionary<string, float> _dustAccum = new();
    private float _time;

    // Fire-spread throttle: catching a NEW tile alight, and spitting a fresh ember, each draw
    // one point from a budget that refills slowly. A blaze can still sweep a grove or a grass
    // field (that's a lot of tiles, but bounded — burnt tiles turn to sky and stop feeding it),
    // yet a lava tongue can never cascade fire clear across a whole planet: once the budget is
    // dry, existing flames keep burning out but nothing new ignites until it recovers.
    private float _fireBudget = FireBudgetMax;
    private const float FireBudgetMax = 90f;
    private const float FireBudgetRegen = 22f;   // points per second
    private bool SpendFire()
    {
        if (_fireBudget < 1f) return false;
        _fireBudget -= 1f;
        return true;
    }

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
    /// ~12% voids are tolerated because craggy interlocked piles keep small gaps. Expressed
    /// as a FRACTION of the tile (was a flat "-2", which silently tightened to 3% tolerance
    /// when Density went 4→8).</summary>
    private const int CompactMinFill = Density * Density - Density * Density / 8;
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
    /// <see cref="SpawnDustInTile"/>). Game1 drains this into Session.Pickups. Every entry —
    /// embedded-gem pop or shattered gem tile — is one whole drop at the shatter site.</summary>
    public readonly List<(Vector2 pos, TileKind kind)> PendingGemDrops = new();

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
    /// (velocity/travel/flow) and airborne flying cells are deliberately dropped — both are
    /// sub-second transients (a save mid-splash loses at most a few grains of dust).</summary>
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
        // Wake only the free surfaces. Waking EVERY occupied cell made the first ticks
        // after resume walk millions of hemmed sea/lake interiors just to re-discover
        // they're asleep — seconds of stall at Density 8. Anything saved mid-flight has
        // open neighbours by definition, so it still wakes and resettles.
        WakeFreeSurfaces(0, Height - 1);
    }

    /// <summary>Wake only the FREE-SURFACE cells of a row band: occupied cells with at
    /// least one open (non-solid, non-occupied) cardinal neighbour. Bulk seeding and save
    /// restore used to wake every cell they touched, which made the first sim ticks walk
    /// entire seas of hemmed interior cells once each before they slept — a multi-second
    /// load stall at Density 8. Interior cells born asleep behave identically: asleep IS
    /// their steady state, and any later disturbance still wakes them via WakeNeighbors
    /// like every other sleeping cell.</summary>
    public void WakeFreeSurfaces(int cyMin, int cyMax)
    {
        cyMin = Math.Max(0, cyMin);
        cyMax = Math.Min(Height - 1, cyMax);
        for (var cy = cyMin; cy <= cyMax; cy++)
        {
            var n = _cellsAt[cy];
            var row = _rowOffsets[cy];
            for (var cx = 0; cx < n; cx++)
            {
                var i = row + cx;
                if (_mat[i] == 0) continue;
                var open = !IsBlocked(cx - 1, cy) || !IsBlocked(cx + 1, cy);
                if (!open && cy > 0)
                {
                    var (icx, icy) = InnerCell(cx, cy);
                    open = !IsBlocked(icx, icy);
                }
                if (!open && cy < Height - 1)
                {
                    var oc = OuterCellCount(cx, cy);
                    for (var w = 0; w < oc && !open; w++)
                    {
                        var (ocx, ocy) = OuterCell(cx, cy, w);
                        open = !IsBlocked(ocx, ocy);
                    }
                }
                if (open) Enqueue(i);
            }
        }
    }

    /// <summary>Raw cell write for bulk seeding: no enqueue, no neighbour wake — callers
    /// run one <see cref="WakeFreeSurfaces"/> pass when the whole fill is in place.</summary>
    private void PlaceSilent(int cx, int cy, Material m)
    {
        if (!InBounds(cx, cy)) return;
        var i = Idx(cx, cy);
        if (_mat[i] != 0) return;
        _mat[i] = (byte)m;
        _srcTile[i] = (byte)TileKind.Sky;
        ClearKinetics(i);
    }

    /// <summary>Silent full-tile fill for load-time seeding (lakes, gas pockets, acid,
    /// oil sumps) — pair with one <see cref="WakeFreeSurfaces"/> after ALL seeding.
    /// Full tiles have no interior holes, so the boundary wake reaches everything that
    /// could possibly move.</summary>
    public void FillTileSilent(int tx, int ty, Material m)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        for (var dy = 0; dy < Density; dy++)
            for (var dx = 0; dx < Density; dx++)
                PlaceSilent(c0x + dx, c0y + dy, m);
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

    /// <summary>Source TileKind a dust cell came from (Sky for non-dust). "Dust" is only a
    /// state — this is the actual material (stone, wood, ore…) so the readout can name it.</summary>
    public TileKind SrcTileAt(int cx, int cy) => InBounds(cx, cy) ? (TileKind)_srcTile[Idx(cx, cy)] : TileKind.Sky;

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

    /// <summary>Particle→cell handoff: place a material at a world position only if the cell
    /// is genuinely open air — unlike <see cref="PlaceAtWorld"/> this refuses cells inside
    /// solid tiles, because handoff positions come from the PARTICLE collision test (world-
    /// space float vs tile grid), which can rest a particle a hair inside a wall's cell
    /// footprint. A cinder that lands stamps fire where the fire can actually live.</summary>
    public void StampAtWorld(Vector2 worldPos, Material m)
    {
        var (cx, cy) = WorldToCell(worldPos);
        if (!IsBlocked(cx, cy)) Place(cx, cy, m);
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

    /// <summary>_srcTile marker for water shed by rain. Not a real TileKind — water cells
    /// never use their source slot, so it's free to carry provenance: rain-fed water slowly
    /// evaporates once it rests exposed to open air near the surface (see the rain clause in
    /// <see cref="TickLiquid"/>), so a thin shower film dries out instead of creeping into a
    /// permanent flood — but rain that reaches a REAL body of water (or pools a full tile
    /// deep in a basin) sheds the tag and JOINS it for good (see <see cref="JoinsWaterBody"/>):
    /// lakes and seas rise with the weather. The marker rides every move/swap/launch like dust's
    /// source kind does, survives the save, and clears if the cell steams away against lava.</summary>
    private const byte RainWaterSrc = 255;

    /// <summary>_srcTile marker for CEILING-DRIP water (the ambient-sweep seep): behaves
    /// like rain — pools, then evaporates once exposed — but NEVER absorbs into a permanent
    /// body. Drips mass-duplicate from the pool above by design (draining would empty every
    /// lake through its bed), so evaporation must remain their only sink: an absorbing drip
    /// would slow-flood every cave under a lake for good.</summary>
    private const byte DripWaterSrc = 254;

    /// <summary>Spawn one rain-fed water cell at a world position — the real, pooling half of
    /// a shower (the streaking drops are particles). Skipped silently if the cell is occupied,
    /// so rain over a lake just merges into the top of it.</summary>
    public void SpawnRainWater(Vector2 worldPos)
    {
        var (cx, cy) = WorldToCell(worldPos);
        Place(cx, cy, Material.Water, (TileKind)RainWaterSrc);
    }

    /// <summary>Whether resting snow lies for good (frost worlds) or sublimates away.
    /// Refreshed by Weather each frame from the biome, so headless worlds default to
    /// melting-out — no test can strand an ever-awake snowfield.</summary>
    public bool SnowPersists;

    /// <summary>Spawn one falling snow cell — the accumulating half of a snowfall (the
    /// drifting flakes are particles). Piles like sand where it lands. Saturation-capped:
    /// fresh flakes stop sticking once a blanket ~2 grains deep already lies underneath, so
    /// even the frost world's endless showers can't bury terrain without limit (rain has
    /// evaporation as its mass counterweight; lying snow needs this cap instead — and it
    /// also keeps the blanket too shallow for the compaction sweep to entomb it).</summary>
    public void SpawnSnow(Vector2 worldPos)
    {
        if (CountNear(worldPos, 8f, Material.Snow) > 20) return;
        var (cx, cy) = WorldToCell(worldPos);
        Place(cx, cy, Material.Snow);
    }

    /// <summary>World positions where the sim just made bubbles (steam popping off a quench,
    /// a flame gout dying in a pool). Game1 drains this into bubble particles; headless
    /// callers just leave it. Capped so a cataclysm can't flood it.</summary>
    public readonly List<Vector2> PendingBubbles = new();
    private const int MaxPendingBubbles = 200;
    private void QueueBubble(Vector2 pos)
    {
        if (PendingBubbles.Count < MaxPendingBubbles) PendingBubbles.Add(pos);
    }

    /// <summary>Spawn dust cells filling the whole polar tile, tagged with the source TileKind
    /// so the cells render in that tile's colours and pay out that tile's drop on pickup.
    /// Deterministic count (DustCellsPerTile = Density²) so the debris occupies exactly the
    /// space the block did and accumulation math is exact: collecting every cell from a 2×2
    /// tile quad yields exactly drop.count units. A shattered Conglomerate instead spills the
    /// exact cells that were pressed into it (see the compaction sweep) — value round-trips,
    /// nothing is minted.</summary>
    public void SpawnDustInTile(int tx, int ty, TileKind src)
    {
        // A gem embedded in this tile pops out as a physical pickup when the host shatters.
        // Cells has no entity access, so shatter sites queue here and Game1 drains the queue
        // into Session.Pickups (headless callers just leave it; the list is per-planet).
        if (Planet.TakeGem(tx, ty) is var embedded && embedded != TileKind.Sky)
            PendingGemDrops.Add((Planet.TileToWorld(tx, ty), embedded));
        // Gem tiles (geode/cavern linings + old worlds' veins): each shattered crystal is its
        // own find, so it pops a whole physical drop right where it broke — resting in a bed
        // of dust from the SURROUNDING rock (the material it was dug out of), not in a bare
        // black hole. The bed is tagged with a neighbouring host kind so it falls in that
        // rock's colours.
        if (Tiles.IsGem(src))
        {
            PendingGemDrops.Add((Planet.TileToWorld(tx, ty), src));
            var host = TileKind.Stone;
            Span<(int x, int y)> nbrs = stackalloc[]
                { Planet.InnerNeighbour(tx, ty), (tx, ty - 1), (tx, ty + 1) };
            foreach (var (nx, ny) in nbrs)
            {
                var nk = Planet.Get(nx, ny);
                if (Tiles.IsSolid(nk) && !Tiles.IsGem(nk) && !Tiles.IsAnchored(nk)) { host = nk; break; }
            }
            var b0y = tx * Density;
            var b0x = ty * Density;
            for (var dy = 0; dy < Density; dy++)
                for (var dx = 0; dx < Density; dx++)
                    Place(b0x + dx, b0y + dy, Material.Dust, host);
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
                Place(c0x + dx, c0y + dy, Material.Dust, src);
    }

    /// <summary>Spawn dust filling only a fraction of the tile, tagged with the source kind —
    /// used for airy debris like felled foliage, which sheds a thin 30%-of-a-tile puff of dust
    /// rather than a solid block's worth. Still pays that tile's drop on pickup, pro-rated.</summary>
    public void SpawnDustFraction(int tx, int ty, TileKind src, float frac)
    {
        var count = Math.Max(1, (int)(frac * Density * Density));
        var c0y = tx * Density;
        var c0x = ty * Density;
        for (var i = 0; i < count; i++)
            Place(c0x + _rng.Next(Density), c0y + _rng.Next(Density), Material.Dust, src);
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

    /// <summary>Whether the TILE holding this cell is solid rock (ignores cell contents) —
    /// the flying-cell pass-through check needs to tell "smoke in open air" apart from
    /// "cell somehow inside a wall".</summary>
    private bool SolidTileAtCell(int cx, int cy)
    {
        var tx = cy / Density;
        var ty = WrapX(cx, _cellsAt[cy]) / Density;
        return Tiles.IsSolid(Planet.Get(tx, ty));
    }

    /// <summary>Whether the cell at a world position holds water — the particle pass keys
    /// surface behaviour off it (rain crowns, flame fizzles, acid settles on top) so
    /// colliding effects stop at a pool's surface instead of sailing through the body.</summary>
    public bool WaterAtWorld(Vector2 worldPos)
    {
        var (cx, cy) = WorldToCell(worldPos);
        if (cy < 0 || cy >= Height) return false;
        return Get(cx, cy) == Material.Water;
    }

    /// <summary>Whether the cell at a world position holds settled powder (sand/dirt/gravel/
    /// dust). Pickups treat these as ground so a dropped gem rests on a pile, not under it.</summary>
    public bool PowderAtWorld(Vector2 worldPos)
    {
        var (cx, cy) = WorldToCell(worldPos);
        return Materials.IsCompactable(Get(cx, cy));
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
        // relying on exactly this wake), and fire drifting against a sleeping oil surface
        // must wake it or the pool never catches. Lava kept out of caution for bulk flows.
        // Inert grains — the entire mass-break dust case — skip it, halving the wake fan-out.
        if (m == (byte)Material.Water || m == (byte)Material.Lava || m == (byte)Material.Fire)
            WakeNeighbors(dCx, dCy);
        return true;
    }

    /// <summary>Exchange two cells' full state (buoyancy swaps). Both wake — a swap changes
    /// both neighbourhoods, and the lighter cell must keep bobbing up next tick.</summary>
    private void SwapCells(int aCx, int aCy, int bCx, int bCy)
    {
        var ai = Idx(aCx, aCy);
        var bi = Idx(bCx, bCy);
        (_mat[ai], _mat[bi]) = (_mat[bi], _mat[ai]);
        (_srcTile[ai], _srcTile[bi]) = (_srcTile[bi], _srcTile[ai]);
        ClearKinetics(ai);
        ClearKinetics(bi);
        Enqueue(ai);
        Enqueue(bi);
        WakeNeighbors(aCx, aCy);
        WakeNeighbors(bCx, bCy);
    }

    /// <summary>Local surface axes at a cell: outward radial (up) and its perpendicular
    /// (tangent) — the frame splash/spark launch velocities are authored in.</summary>
    private (Vector2 up, Vector2 tan) AxesAt(int cx, int cy)
    {
        var n = CellsAt(cy);
        var ang = (WrapX(cx, n) + 0.5f) / n * MathHelper.TwoPi;
        var up = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        return (up, new Vector2(-up.Y, up.X));
    }

    /// <summary>Emit a flying cell from thin air (no grid cell consumed) — fire embers only;
    /// anything that is matter should go through <see cref="LaunchCell"/> so mass conserves.</summary>
    public void LaunchAtWorld(Vector2 pos, Vector2 vel, Material m, TileKind src = TileKind.Sky)
    {
        if (_flying.Count >= MaxFlying) return;
        _flying.Add(new Flying { Pos = pos, Vel = vel, Mat = (byte)m, Src = (byte)src });
    }

    /// <summary>Pluck the grid cell at (cx,cy) into ballistic flight with the given
    /// world-space velocity. The cell leaves the grid (neighbours wake into the hole) and
    /// re-enters wherever it lands, so launched material is conserved.</summary>
    public bool LaunchCell(int cx, int cy, Vector2 vel)
    {
        if (!InBounds(cx, cy) || _flying.Count >= MaxFlying) return false;
        var i = Idx(cx, cy);
        if (_mat[i] == 0) return false;
        _flying.Add(new Flying { Pos = CellToWorld(cx, cy), Vel = vel, Mat = _mat[i], Src = _srcTile[i] });
        _mat[i] = 0;
        _srcTile[i] = 0;
        ClearKinetics(i);
        WakeNeighbors(cx, cy);
        return true;
    }

    /// <summary>Fling up to <paramref name="count"/> of the polar tile's cells into flight
    /// along <paramref name="dir"/> (with angular spread and speed jitter). Gases and flame
    /// are skipped — only matter with weight gets thrown. Returns how many launched; callers
    /// budget on that so a mass event can't drown the flying list.</summary>
    public int EjectFromTile(int tx, int ty, Vector2 dir, float speed, int count)
    {
        var c0y = tx * Density;
        var c0x = ty * Density;
        var launched = 0;
        for (var i = 0; i < count; i++)
        {
            var cx = c0x + _rng.Next(Density);
            var cy = c0y + _rng.Next(Density);
            if (!InBounds(cx, cy)) continue;
            var m = (Material)_mat[Idx(cx, cy)];
            if (m is Material.Empty or Material.Smoke or Material.Gas or Material.Fire) continue;
            var spread = ((float)_rng.NextDouble() - 0.5f) * 1.1f;
            var (c, s) = (MathF.Cos(spread), MathF.Sin(spread));
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            if (LaunchCell(cx, cy, d * speed * (0.6f + (float)_rng.NextDouble() * 0.8f)))
                launched++;
        }
        return launched;
    }

    /// <summary>Integrate the flying cells: planet-centre gravity, sub-cell stepping so fast
    /// grains can't tunnel, and re-entry into the grid on contact. Runs before the grid tick
    /// each frame; landings enqueue + wake through Place, so a just-landed grain gets its
    /// first grid tick the same frame's pass.</summary>
    private void UpdateFlying(float dt)
    {
        for (var fi = _flying.Count - 1; fi >= 0; fi--)
        {
            var f = _flying[fi];
            f.Age += dt;
            var toCore = Planet.Center - f.Pos;
            var dist = toCore.Length();
            if (dist > 1f) f.Vel += toCore * (FlyGravity * dt / dist);
            var speed = f.Vel.Length();
            if (speed > FlyMaxSpeed) f.Vel *= FlyMaxSpeed / speed;
            // Bleed off any excess OUTWARD speed so ejecta can't launch off into the sky and
            // hang there as a stray floating pixel (runs every tick before the move, so even a
            // huge explosion impulse is capped on its first step). Lateral/inward motion kept.
            if (dist > 1f)
            {
                var outUnit = -toCore / dist;
                var vOut = Vector2.Dot(f.Vel, outUnit);
                if (vOut > FlyMaxOutward) f.Vel -= outUnit * (vOut - FlyMaxOutward);
            }

            var move = f.Vel * dt;
            var steps = Math.Clamp((int)(move.Length() / PxPerCell) + 1, 1, 12);
            var step = move / steps;
            var done = false;
            for (var s = 0; s < steps; s++)
            {
                var next = f.Pos + step;
                var (cx, cy) = WorldToCell(next);
                if (cy >= Height) { f.Pos = next; continue; }   // open sky: pure ballistics
                if (cy < 0 || IsBlocked(cx, cy))
                {
                    // Gas-like cells (smoke/steam, gas pockets, open flame) are NOT obstacles
                    // to a flying grain — it punches straight through them. Without this, the
                    // steam curling back off an acid splash blocked the next spurt mid-air,
                    // stacking acid in the sky and making the stream drop far short. A fire
                    // grain passing through a gas pocket still lights it on the way.
                    if (cy >= 0 && !SolidTileAtCell(cx, cy)
                        && Get(cx, cy) is Material.Smoke or Material.Gas or Material.Fire)
                    {
                        if ((Material)f.Mat == Material.Fire && Get(cx, cy) == Material.Gas)
                            IgniteCell(cx, cy);
                        f.Pos = next;
                        continue;
                    }
                    // Open flame dies the moment it meets water: a flying fire gout that hits
                    // (or is hosed into) a pool flashes to a wisp of steam instead of banking
                    // a burning cell on the surface.
                    if ((Material)f.Mat == Material.Fire && cy >= 0 && Get(cx, cy) == Material.Water)
                    {
                        var (qx, qy) = WorldToCell(f.Pos);
                        if (!IsBlocked(qx, qy)) Place(qx, qy, Material.Smoke);
                        QueueBubble(next);   // the gout dies with a burst of bubbles
                        done = true;
                        break;
                    }
                    // Contact: bank the cell in the last free spot along its path. A failed
                    // deposit (landing site filled since) damps the cell and lets it keep
                    // falling — it re-tries next tick from wherever it slid to.
                    done = TryDeposit(f);
                    if (!done) f.Vel *= 0.3f;
                    break;
                }
                f.Pos = next;
            }
            if (!done && f.Age > FlyMaxAge) done = TryDeposit(f) || true; // give up either way
            if (done)
            {
                _flying[fi] = _flying[^1];   // swap-remove; order is irrelevant
                _flying.RemoveAt(_flying.Count - 1);
                continue;
            }
            _flying[fi] = f;
        }
    }

    /// <summary>Re-enter the grid at the flying cell's position, probing outward a few rows
    /// when the exact cell is taken (a droplet landing on a pool surfaces on top of it).</summary>
    private bool TryDeposit(in Flying f)
    {
        var (cx, cy) = WorldToCell(f.Pos);
        if (cy < 0) cy = 0;
        for (var k = 0; k < 6; k++)
        {
            if (cy >= Height) return false;
            if (!IsBlocked(cx, cy))
            {
                Place(cx, cy, (Material)f.Mat, (TileKind)f.Src);
                return true;
            }
            (cx, cy) = OuterCell(cx, cy);
        }
        return false;
    }

    public void Update(float dt)
    {
        _time += dt;
        if (_fireBudget < FireBudgetMax) _fireBudget = MathF.Min(FireBudgetMax, _fireBudget + FireBudgetRegen * dt);

        UpdateFlying(dt);

        if (_time >= _compactSweepAt)
        {
            _compactSweepAt = _time + CompactSweepPeriod;
            SweepCompaction();
        }

        // Ambient trickle effects (ceiling drips, moss creep) sample randomly around the
        // player — cheap, bounded, and they only happen where someone can see them. Headless
        // contexts never set CompactionExclusion, so tests are untouched.
        if (CompactionExclusion is { } eye && _time >= _ambientSweepAt)
        {
            _ambientSweepAt = _time + AmbientSweepPeriod;
            SweepAmbient(eye);
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
                case Material.Snow:
                    TickSand(cx, cy, dt);
                    break;
                case Material.Water: TickLiquid(cx, cy, dt); break;
                case Material.Lava:  TickLava(cx, cy, dt); break;
                case Material.Acid:  TickAcid(cx, cy, dt); break;
                case Material.Oil:   TickOil(cx, cy, dt); break;
                case Material.Fire:  TickFire(cx, cy); break;
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
        Material.Snow => 70,    // fluffy — drifts smooth out almost like sand
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
            // Snow off its home world doesn't keep: an exposed resting flake sublimates on a
            // slow roll (~15s expected), staying awake only while exposed — a buried flake
            // sleeps like any grain and is re-woken when the layer above it clears. Frost
            // worlds set SnowPersists, so there drifts lie, bury, and press into Snow tiles
            // through the ordinary compaction sweep.
            if ((Material)_mat[i] == Material.Snow && !SnowPersists && ExposedAbove(cx, cy))
            {
                if (_rng.Next(900) == 0)
                {
                    _mat[i] = 0;
                    ClearKinetics(i);
                    WakeNeighbors(cx, cy);
                    return;
                }
                Enqueue(i);
            }
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
        Material.Snow => TileKind.Snow,   // a buried drift presses into packed snow
        _ => TileKind.Gravel,   // Sand, Gravel, untagged/legacy dust
    };

    private void TickLiquid(int cx, int cy, float dt)
    {
        var i = Idx(cx, cy);
        var impact = 0f;

        // Buoyancy: every other liquid is denser than oil, so a cell sitting on oil sinks
        // through it (one swap per tick — mixed pools separate into clean layers over a
        // second or two rather than snapping). Lava sinking into an oil film is the fun
        // case: guaranteed contact, and TickOil torches it next tick.
        if ((Material)_mat[i] != Material.Oil && cy > 0)
        {
            var (ocx, ocy) = InnerCell(cx, cy);
            if (Get(ocx, ocy) == Material.Oil)
            {
                SwapCells(cx, cy, ocx, ocy);
                return;
            }
        }

        var (bcx, bcy) = InnerCell(cx, cy);
        if (cy > 0 && !IsBlocked(bcx, bcy))
        {
            // Airborne: same fall integrator as sand.
            _velR[i] = MathF.Min(_velR[i] + GravityCells * dt, TerminalCells);
            _travel[i] += _velR[i] * dt;
            var steps = Math.Min((int)_travel[i], MaxStepsPerTick);
            _travel[i] = MathF.Min(_travel[i] - steps, 1f);
            if (steps == 0) { Enqueue(i); return; }

            var selfMat = (Material)_mat[i];
            var fallSpeed = _velR[i];
            var plunges = 0;
            for (var s = 0; s < steps && cy > 0; s++)
            {
                var (icx, icy) = InnerCell(cx, cy);
                if (TryMoveTo(cx, cy, icx, icy, s == 0)) { cx = icx; cy = icy; continue; }
                // Plunge: a HARD-falling stream meeting its own pool dives INTO it instead
                // of stopping dead on the surface — swap with the liquid below and keep
                // going (the displaced cell surfaces, wakes, and spreads). The speed gate
                // keeps it to waterfalls: gently-fallen drops (rain, drips) rest ON the
                // surface and splash there instead of silently vanishing under it.
                // Probabilistic and depth-capped so half the stream still splashes and a
                // deep dive can't chew a whole tick; different liquids never plunge
                // (buoyancy owns that).
                if (plunges < 2 && fallSpeed > TerminalCells * 0.6f
                    && (Material)_mat[Idx(icx, icy)] == selfMat
                    && !IsTileSolidAt(icx, icy) && _rng.Next(2) == 0)
                {
                    SwapCells(cx, cy, icx, icy);
                    cx = icx; cy = icy; plunges++;
                    continue;
                }
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

        // Splash: a hard landing sometimes flings the cell straight back out as a ballistic
        // droplet — waterfalls fizz at the plunge pool, poured lava spits glowing beads.
        // Needs open air above (a droplet can't jump inside a filled pool interior).
        // Landing ON LIQUID splashes far easier than landing on rock (a pool's surface
        // gives): raindrops crown off a lake at speeds that would land silently on stone.
        var splashBar = SplashMinImpact;
        if (cy > 0)
        {
            var (lcx, lcy) = InnerCell(cx, cy);
            var below = Get(lcx, lcy);
            if (below is Material.Water or Material.Acid or Material.Oil or Material.Lava)
                splashBar *= 0.35f;
        }
        if (impact > splashBar && _rng.Next(3) == 0
            && _flying.Count < MaxFlying && cy < Height - 1)
        {
            var (ucx, ucy) = OuterCell(cx, cy);
            if (!IsBlocked(ucx, ucy))
            {
                var (up, tan) = AxesAt(cx, cy);
                var v = up * (impact * PxPerCell * (0.35f + (float)_rng.NextDouble() * 0.3f))
                      + tan * (((float)_rng.NextDouble() - 0.5f) * impact * PxPerCell * 0.8f);
                if (LaunchCell(cx, cy, v)) return;
            }
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
        // Rain water dries out: a rain-fed cell resting exposed to open air evaporates on a
        // slow per-tick roll (~10s expected once exposed), so puddles shrink top-down and a
        // shower never leaves a permanent flood. Only rain/drip-marked water — seeded lakes
        // and oceans keep their mass forever.
        var srcB = _srcTile[i];
        var rainFed = self == Material.Water && (srcB == RainWaterSrc || srcB == DripWaterSrc);
        // Rain JOINS bodies of water: an atmospheric rain cell touching permanent water —
        // or buried under a full tile of pooled water — sheds its tag and becomes lake
        // water for good, so showers raise lakes and seas, and a hard rain filling a basin
        // leaves a real pond. The wake lets the untag spread through a connected puddle via
        // this same contact rule. Thin films still evaporate; ceiling-drip water never
        // converts (see DripWaterSrc — its mass is duplicated, evaporation is its sink).
        if (rainFed && srcB == RainWaterSrc && JoinsWaterBody(cx, cy))
        {
            _srcTile[i] = 0;
            rainFed = false;
            WakeNeighbors(cx, cy);
        }
        if (rainFed && ExposedAbove(cx, cy) && _rng.Next(600) == 0)
        {
            _mat[i] = 0;
            _srcTile[i] = 0;
            ClearKinetics(i);
            WakeNeighbors(cx, cy);
            return;
        }
        if (self == Material.Water || self == Material.Lava || self == Material.Acid || self == Material.Oil)
        {
            var (scx, scy) = InnerCell(cx, cy);
            var hemmed = (cy <= 0 || IsBlocked(scx, scy)) && IsBlocked(cx - 1, cy) && IsBlocked(cx + 1, cy);
            // Lava and acid stay awake while there's still something adjacent to eat, so a
            // hemmed pool keeps gnawing at the tile it rests against instead of sleeping on it.
            var stillEating = (self == Material.Lava && HasMeltableNeighbour(cx, cy))
                           || (self == Material.Acid && HasCorrodibleNeighbour(cx, cy));
            if (hemmed && !stillEating)
            {
                // Hemmed rain water that's still open to the air above must keep ticking or
                // its evaporation roll never fires (a flat one-deep puddle is hemmed by its
                // own row-mates). Buried rain cells sleep like any lake interior — if the
                // water above them ever clears, the neighbour wake re-arms them.
                if (rainFed && ExposedAbove(cx, cy)) Enqueue(i);
                return;
            }
        }

        // Lateral dispersion: flow several cells per tick in a persistent direction so pools
        // level out quickly, plus a splash bonus proportional to how hard the cell just landed.
        // Per-material rate is the viscosity knob (Noita's trick): water races out flat,
        // acid pours, oil crawls as a slick, lava creeps as molten rock.
        var dir = (int)_flow[i];
        if (dir == 0) dir = _rng.Next(2) == 0 ? 1 : -1;
        var spread = DispersionFor(self) + (int)(impact * SplashScale);
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

    /// <summary>Lateral flow rate in cells per tick — per-material viscosity. Scaled off
    /// Density like the old shared constant so proportions survive Density changes.</summary>
    private static int DispersionFor(Material m) => m switch
    {
        Material.Water => Density,          // 8 — levels out fast, the Noita water feel
        Material.Acid => Density * 5 / 8,   // 5
        Material.Oil => Density / 2,        // 4 — syrupy slick
        Material.Lava => Density * 3 / 8,   // 3 — molten creep
        _ => LiquidDispersion,
    };

    private void TickLava(int cx, int cy, float dt)
    {
        if (QuenchIfWet(cx, cy)) return;

        TryMelt(InnerCell(cx, cy));
        TryMelt((cx + 1, cy));
        TryMelt((cx - 1, cy));
        TryIgniteTile(InnerCell(cx, cy));
        TryIgniteTile((cx + 1, cy));
        TryIgniteTile((cx - 1, cy));
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++) { TryMelt(OuterCell(cx, cy, i)); TryIgniteTile(OuterCell(cx, cy, i)); }
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
        if (wi < 0)
        {
            // No water, but a snow grain against lava flashes to meltwater (rain-tagged so
            // the slush dries out later) — the real quench then fires against the droplet
            // on the next tick.
            var si = FindNeighbour(cx, cy, Material.Snow);
            if (si < 0) return false;
            _mat[si] = (byte)Material.Water;
            _srcTile[si] = RainWaterSrc;
            ClearKinetics(si);
            Enqueue(si);
            var (mcx, mcy) = UnIdx(si);
            WakeNeighbors(mcx, mcy);
            Enqueue(Idx(cx, cy));
            return true;
        }
        if (_rng.Next(3) != 0) { Enqueue(Idx(cx, cy)); return true; } // stay awake, react soon

        _mat[wi] = (byte)Material.Smoke;
        _srcTile[wi] = 0;
        ClearKinetics(wi);
        Enqueue(wi);
        var (wcx, wcy) = UnIdx(wi);
        WakeNeighbors(wcx, wcy);
        QueueBubble(CellToWorld(wcx, wcy));   // the boil fizzes off a bubble train

        var i = Idx(cx, cy);
        _mat[i] = (byte)Material.Gravel;
        _srcTile[i] = 0;
        ClearKinetics(i);
        Enqueue(i);
        WakeNeighbors(cx, cy);
        // The quench spits: sometimes the fresh gravel pops out as a hot ballistic bead, so
        // a lava/water front crackles instead of quietly trading cells.
        if (_rng.Next(3) == 0 && _flying.Count < MaxFlying)
        {
            var (up, tan) = AxesAt(cx, cy);
            LaunchCell(cx, cy, up * (70f + _rng.Next(70)) + tan * (_rng.Next(120) - 60));
        }
        return true;
    }

    // --- Ambient sweep: the slow, visible-world-only trickle effects. Random samples in a
    // disc around the player each period; each sample either finds open air (and looks up
    // for a dripping ceiling) or standing water (and looks down for a stone bed to moss).
    private float _ambientSweepAt;
    private const float AmbientSweepPeriod = 0.5f;
    private const int AmbientSamples = 36;
    private const float AmbientSweepRadius = 340f;

    /// <summary>Porous kinds a pool can weep through — soils and rubble, not sealed rock.
    /// This is what makes caves under wet ground drip after rain.</summary>
    private static bool IsPorous(TileKind k) => k is
        TileKind.Dirt or TileKind.Grass or TileKind.Gravel or TileKind.MossStone
        or TileKind.Conglomerate;

    private void SweepAmbient(Vector2 centre)
    {
        for (var s = 0; s < AmbientSamples; s++)
        {
            var ang = (float)_rng.NextDouble() * MathHelper.TwoPi;
            var dist = MathF.Sqrt((float)_rng.NextDouble()) * AmbientSweepRadius;
            var pos = centre + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * dist;
            var (cx, cy) = WorldToCell(pos);
            if (cy < 1 || cy >= Height - 1) continue;
            var m = (Material)_mat[Idx(cx, cy)];
            if (m == Material.Water)
            {
                // Moss creep: bare stone that has sat under standing water slowly greens
                // over. Walk down through the pool (≤3 cells) to its bed; only plain Stone
                // converts, ores and everything else are left alone. Very low per-hit odds —
                // "long-standing" is approximated by how many sweeps a permanent pool eats.
                var (wx, wy) = (cx, cy);
                for (var k = 0; k < 3 && wy > 0; k++)
                {
                    (wx, wy) = InnerCell(wx, wy);
                    if (!IsTileSolidAt(wx, wy))
                    {
                        if (_mat[Idx(wx, wy)] == 0) break;   // air gap under the pool — no bed
                        continue;                            // deeper water — keep walking
                    }
                    var tx = wy / Density;
                    var ty = WrapX(wx, _cellsAt[wy]) / Density;
                    if (Planet.Get(tx, ty) == TileKind.Stone && _rng.Next(30) == 0)
                        Planet.Set(tx, ty, TileKind.MossStone);
                    break;
                }
            }
            else if (m == Material.Empty && !SolidTileAtCell(cx, cy))
            {
                // Ceiling drip: open air under a porous tile with water pooled on top weeps
                // the odd droplet through. The droplet is DRIP-tagged (never converts to
                // permanent water, unlike rain), so a dripping cave pools puddles that
                // evaporate — never a slow flood — and the pool above is NOT drained (a
                // real seep would empty every lake through its bed).
                var (px, py) = (cx, cy);
                for (var k = 0; k < 12 && py < Height - 1; k++)
                {
                    var (ox, oy) = OuterCell(px, py);
                    if (_mat[Idx(ox, oy)] != 0) break;        // something hanging here already
                    if (IsTileSolidAt(ox, oy))
                    {
                        var tx = oy / Density;
                        var ty = WrapX(ox, _cellsAt[oy]) / Density;
                        if (!IsPorous(Planet.Get(tx, ty))) break;
                        var face = Planet.TileToWorld(tx, ty);
                        var up = face - Planet.Center;
                        if (up.LengthSquared() < 1f) break;
                        up.Normalize();
                        if (CountWaterNear(face + up * Planet.TileSize, 3.5f) >= 3
                            && _rng.Next(4) == 0)
                            Place(px, py, Material.Water, (TileKind)DripWaterSrc);
                        break;
                    }
                    (px, py) = (ox, oy);
                }
            }
        }
    }

    /// <summary>Whether any cell in the row just outward is open air (no solid tile, no
    /// material) — the "exposed to the sky-side" test rain-water evaporation keys off.</summary>
    private bool ExposedAbove(int cx, int cy)
    {
        if (cy >= Height - 1) return true;
        var oc = OuterCellCount(cx, cy);
        for (var k = 0; k < oc; k++)
        {
            var (ocx, ocy) = OuterCell(cx, cy, k);
            if (!IsBlocked(ocx, ocy)) return true;
        }
        return false;
    }

    /// <summary>Whether a rain cell merges into permanent water here: any cardinal
    /// neighbour is untagged Water (the body absorbs the raindrop), or a FULL TILE's worth
    /// of water presses from directly above (a basin filling ≥1 tile deep starts converting
    /// to a real pond from the bottom up, and the contact rule then spreads the untag
    /// through the connected pool). The tile-depth bar is deliberate: a shallower rule
    /// (any sandwiched cell) turned every 1.5-px dip permanent and killed the evaporation
    /// sink — the creeping flood the rain tag exists to prevent.</summary>
    private bool JoinsWaterBody(int cx, int cy)
    {
        bool Permanent(int ncx, int ncy)
        {
            if (ncy < 0 || ncy >= Height) return false;
            var ni = Idx(ncx, ncy);
            return (Material)_mat[ni] == Material.Water
                && _srcTile[ni] != RainWaterSrc && _srcTile[ni] != DripWaterSrc;
        }

        if (Permanent(cx - 1, cy) || Permanent(cx + 1, cy)) return true;
        if (cy > 0)
        {
            var (icx, icy) = InnerCell(cx, cy);
            if (Permanent(icx, icy)) return true;
        }
        if (cy < Height - 1)
        {
            var (ocx, ocy) = OuterCell(cx, cy, 0);
            if (Permanent(ocx, ocy)) return true;
        }
        // Deep-pool conversion: one continuous tile of water (any provenance) overhead.
        var (px, py) = (cx, cy);
        for (var k = 0; k < Density; k++)
        {
            if (py >= Height - 1) return false;
            (px, py) = OuterCell(px, py);
            if ((Material)_mat[Idx(px, py)] != Material.Water) return false;
        }
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
        Planet.TakeGem(tx, ty);   // an embedded gem melts with its host — no pop
        Planet.Set(tx, ty, TileKind.Sky);
        SpawnInTile(tx, ty, Material.Smoke, Density); // scaled so a melt puff reads the same at any grain size
        if (_rng.Next(3) == 0) SpawnInTile(tx, ty, Material.Lava, Density / 4);
    }

    /// <summary>Lava licking a flammable tile (wood, grass, fronds) sets it ALIGHT rather than
    /// melting it — the tile becomes a fire cell, which then spreads through TickFire like any
    /// other blaze (and is bound by the same spread budget, so lava can't torch a planet).</summary>
    private void TryIgniteTile((int cx, int cy) c)
    {
        if (_rng.Next(24) != 0) return;
        if (c.cy < 0 || c.cy >= Height) return;
        var tx = c.cy / Density;
        var ty = WrapX(c.cx, _cellsAt[c.cy]) / Density;
        var k = Planet.Get(tx, ty);
        if (!IsFlammable(k)) return;
        if (!SpendFire()) return;
        Planet.TakeGem(tx, ty);
        Planet.Set(tx, ty, TileKind.Sky);
        SpawnInTile(tx, ty, Material.Fire, Density);
        SpawnInTile(tx, ty, Material.Smoke, Density / 2);
        ShedBurningLeaves(tx, ty, k);
    }

    /// <summary>Leafy tiles don't just vanish into a stationary flame — the burning foliage
    /// sheds a few glowing embers that tumble down out of the crown (flying fire cells, so
    /// they arc, can spot-fire what they land on, and die in steam over water). This is what
    /// makes a canopy fire read pixel-by-pixel instead of tile-by-tile. Fire isn't conserved
    /// matter, so the embers are emitted freely; count is small and the flying list is capped.</summary>
    private void ShedBurningLeaves(int tx, int ty, TileKind k)
    {
        if (k is not (TileKind.TreeCanopy or TileKind.TreeCanopy2 or TileKind.Fernleaf
            or TileKind.SeaFrond or TileKind.Grass)) return;
        var pos = Planet.TileToWorld(tx, ty);
        var up = pos - Planet.Center;
        if (up.LengthSquared() < 1f) return;
        up.Normalize();
        var tan = new Vector2(-up.Y, up.X);
        var count = 2 + _rng.Next(2);
        for (var e = 0; e < count && _flying.Count < MaxFlying; e++)
            LaunchAtWorld(pos + tan * (_rng.Next(5) - 2) + up * (_rng.Next(5) - 2),
                -up * (10f + _rng.Next(50)) + tan * (_rng.Next(120) - 60), Material.Fire);
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
        Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian
        && !Tiles.IsFlora(k);   // flora is acid-adapted on the acid world, hardy elsewhere

    /// <summary>Acid: corrode a touching soft tile now and then, otherwise flow like water.
    /// Mirrors <see cref="TickLava"/>'s structure (eat-then-flow) but with no self-light and a
    /// different tile set. Non-depleting like lava — it sleeps once hemmed with nothing left
    /// to eat (see the sleep clause in <see cref="TickLiquid"/>).</summary>
    private void TickAcid(int cx, int cy, float dt)
    {
        var ate = TryCorrode(InnerCell(cx, cy));
        ate |= TryCorrode((cx + 1, cy));
        ate |= TryCorrode((cx - 1, cy));
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var i = 0; i < oc; i++) ate |= TryCorrode(OuterCell(cx, cy, i));
        }

        // Acid SPENDS ITSELF eating: most bites neutralise the cell that took them (it fizzes
        // to smoke), so a splash of spewer acid dissolves a couple of tiles and is gone rather
        // than chewing an ever-deepening pit. (Contained world pools sit in obsidian with
        // nothing to eat, so they keep their menace.)
        if (ate && _rng.Next(3) != 0)
        {
            var i = Idx(cx, cy);
            _mat[i] = (byte)Material.Smoke;
            _srcTile[i] = 0;
            ClearKinetics(i);
            Enqueue(i);
            WakeNeighbors(cx, cy);
            return;
        }

        if (_rng.Next(2) == 0) { Enqueue(Idx(cx, cy)); return; }
        TickLiquid(cx, cy, dt);
    }

    private bool TryCorrode((int cx, int cy) c)
    {
        // Halved from the old 1-in-45 melt rate: with the acid spewer putting player-aimed
        // acid everywhere, full-rate corrosion chewed terrain (and cities) far too fast.
        if (_rng.Next(90) != 0) return false;
        if (c.cy < 0 || c.cy >= Height) return false;
        var tx = c.cy / Density;
        var ty = WrapX(c.cx, _cellsAt[c.cy]) / Density;
        var k = Planet.Get(tx, ty);
        if (!IsCorrodible(k)) return false;
        Planet.TakeGem(tx, ty);   // acid dissolves the gem along with its host — the old
                                  // "spilled acid destroys a vein" hazard, kept intact
        Planet.Set(tx, ty, TileKind.Sky);
        SpawnInTile(tx, ty, Material.Smoke, Density / 2); // acrid fizz
        return true;
    }

    /// <summary>Oil: inert dark liquid until lava touches it (fire-side ignition lives in
    /// <see cref="TickFire"/>'s probe so sleeping pools still catch). Otherwise flows like
    /// water; the buoyancy swap in TickLiquid keeps it floating on everything else.</summary>
    private void TickOil(int cx, int cy, float dt)
    {
        if (FindNeighbour(cx, cy, Material.Lava) >= 0 && _rng.Next(3) == 0)
        {
            IgniteCell(cx, cy);
            return;
        }
        TickLiquid(cx, cy, dt);
    }

    /// <summary>Convert a fuel cell (oil/gas) to open flame in place.</summary>
    private void IgniteCell(int cx, int cy)
    {
        var i = Idx(cx, cy);
        _mat[i] = (byte)Material.Fire;
        _srcTile[i] = 0;
        ClearKinetics(i);
        Enqueue(i);
        WakeNeighbors(cx, cy);
    }

    /// <summary>Tiles open flame chars away: living groundcover, leafy fernbrush, all of the
    /// wood — trunks, canopy, roots, water fronds — and the wooden builds. The biome-adapted
    /// flora (emberbloom, vitrilily, frostcap, geobloom, rustbramble) is deliberately NOT here,
    /// so it still shrugs off fire the way it shrugs off its home world's lava/acid. Lava melts
    /// a different, mineral set (IsMeltable) because molten rock outranks a campfire.</summary>
    private static bool IsFlammable(TileKind k) => k is
        TileKind.Grass or TileKind.Fernleaf or TileKind.Support or TileKind.Ladder
        or TileKind.Platform or TileKind.TreeTrunk or TileKind.TreeCanopy
        or TileKind.TreeCanopy2 or TileKind.TreeRoot or TileKind.SeaFrond;

    /// <summary>Open flame. One cardinal probe pass drives everything: water on any side
    /// quenches it to steam, fuel cells (oil/gas) catch alight and anchor the flame, and
    /// flammable tiles char through now and then. A fuelled flame lives ~0.7s and licks
    /// upward; a starved one gutters out in a tenth — fire on bare rock dies where it
    /// stands, so a stray ember is only dangerous if it finds something to eat.</summary>
    private void TickFire(int cx, int cy)
    {
        var i = Idx(cx, cy);
        var fuelled = false;
        var doused = false;

        void Probe(int ncx, int ncy)
        {
            if (ncy < 0 || ncy >= Height) return;
            var ni = Idx(ncx, ncy);
            switch ((Material)_mat[ni])
            {
                case Material.Water:
                    doused = true;
                    return;
                case Material.Oil:
                case Material.Gas:
                    fuelled = true;
                    // Flame front: catch the neighbouring fuel cell alight. Probabilistic so
                    // a pool burns across its surface over a second, not in one frame.
                    if (_rng.Next(3) == 0)
                    {
                        var (fcx, fcy) = UnIdx(ni);
                        IgniteCell(fcx, fcy);
                    }
                    return;
                case Material.Snow:
                    // Fire licking a snow GRAIN melts it to a droplet of rain-tagged
                    // meltwater — which then douses the flame naturally, exactly like the
                    // snow-tile melt below.
                    if (_rng.Next(2) == 0)
                    {
                        _srcTile[ni] = RainWaterSrc;
                        _mat[ni] = (byte)Material.Water;
                        _velR[ni] = 0f; _travel[ni] = 0f; _flow[ni] = 0;
                        Enqueue(ni);
                        var (mcx, mcy) = UnIdx(ni);
                        WakeNeighbors(mcx, mcy);
                    }
                    return;
            }
            var k = TileAt(ncx, ncy);
            // Fire MELTS snow: the tile flashes to meltwater and steam. Not budget-gated —
            // melting isn't spreading flame — and the puddle it leaves will douse the fire
            // naturally on a later probe, so a flame eats a snowbank but drowns in the melt.
            if (k == TileKind.Snow)
            {
                if (_rng.Next(6) != 0) return;
                var sx = ncy / Density;
                var sy = WrapX(ncx, _cellsAt[ncy]) / Density;
                Planet.Set(sx, sy, TileKind.Sky);
                SpawnInTile(sx, sy, Material.Water, Density / 2);
                SpawnInTile(sx, sy, Material.Smoke, Density / 3);   // steam
                return;
            }
            if (!IsFlammable(k)) return;
            fuelled = true;
            // Char the tile through, same shape as TryMelt: the tile becomes fire + smoke,
            // which is what walks a grass fire along the surface. Throttled by the spread
            // budget so the front advances but can't blanket the planet. Rate raised (was
            // 1-in-50) so flame licking a flammable surface reliably takes hold.
            if (_rng.Next(28) != 0) return;
            if (!SpendFire()) return;
            var tx = ncy / Density;
            var ty = WrapX(ncx, _cellsAt[ncy]) / Density;
            Planet.TakeGem(tx, ty);
            Planet.Set(tx, ty, TileKind.Sky);
            SpawnInTile(tx, ty, Material.Fire, Density);
            SpawnInTile(tx, ty, Material.Smoke, Density / 2);
            ShedBurningLeaves(tx, ty, k);
        }

        Probe(cx + 1, cy);
        Probe(cx - 1, cy);
        var (icx, icy) = InnerCell(cx, cy);
        Probe(icx, icy);
        if (cy < Height - 1)
        {
            var oc = OuterCellCount(cx, cy);
            for (var w = 0; w < oc; w++)
            {
                var (ocx, ocy) = OuterCell(cx, cy, w);
                Probe(ocx, ocy);
            }
        }

        if (doused)
        {
            // Steam-quenched — the flame flashes to smoke without touching the water.
            _mat[i] = (byte)Material.Smoke;
            _srcTile[i] = 0;
            ClearKinetics(i);
            Enqueue(i);
            WakeNeighbors(cx, cy);
            return;
        }

        // Gutter out: half to a smoke wisp, half to nothing (all-smoke fires read as grey
        // soup over a burning pool). A fuelled flame lives ~0.9s; a STARVED one now pools
        // ~0.8s before dying (was ~0.27s) — flame dropped on bare rock visibly burns as a
        // fire for a beat instead of blinking out, per user. Spread stays budget-gated, so
        // longer-lived starved flame can't creep further, it just LOOKS alive longer.
        if (_rng.Next(fuelled ? 56 : 48) == 0)
        {
            _mat[i] = _rng.Next(2) == 0 ? (byte)Material.Smoke : (byte)0;
            _srcTile[i] = 0;
            ClearKinetics(i);
            if (_mat[i] != 0) Enqueue(i);
            WakeNeighbors(cx, cy);
            return;
        }

        // Ember spit: a rare glowing mote arcs off a fuelled blaze and can start a spot
        // fire where it lands. Emitted, not launched — flame isn't conserved matter. Gated by
        // the spread budget so a windborne ember can't seed an unstoppable second front.
        if (fuelled && _rng.Next(60) == 0 && _flying.Count < MaxFlying && SpendFire())
        {
            var (up, tan) = AxesAt(cx, cy);
            LaunchAtWorld(CellToWorld(cx, cy),
                up * (50f + _rng.Next(60)) + tan * (_rng.Next(140) - 70), Material.Fire);
        }

        // Flicker upward sometimes so flames lick and dance instead of squatting.
        if (_rng.Next(3) == 0 && cy < Height - 1)
        {
            var oc2 = OuterCellCount(cx, cy);
            var (ux, uy) = OuterCell(cx, cy, _rng.Next(oc2));
            if (TryMoveTo(cx, cy, ux, uy)) return;
        }
        Enqueue(Idx(cx, cy));
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

    /// <summary>Flammable gas: rise outward like smoke, but ignite the moment it touches lava
    /// — a gas pocket you tunnel lava into (or vent onto the surface toward a lava flow) goes
    /// up as a rolling flame front. Lingers far longer than smoke so pockets survive to be
    /// found. (Open fire also ignites gas, but that spark comes from the fire side —
    /// TickFire's neighbour probe — so a sleeping cloud still catches.)</summary>
    private void TickGas(int cx, int cy)
    {
        // Ignition: any adjacent lava sets it off. Convert to FIRE, not smoke — the flame
        // cell then torches its own gas neighbours (TickFire), so the burn rolls through the
        // cloud as a visible front and each flame gutters to smoke behind it.
        if (FindNeighbour(cx, cy, Material.Lava) >= 0)
        {
            var bi = Idx(cx, cy);
            _mat[bi] = (byte)Material.Fire;
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
            // Trapped under a roof: pool along the ceiling like gas does — smoke from a cave
            // fire hunts sideways for a way up instead of dying where it rose, so a burning
            // chamber fills from the ceiling down and plumes out of whatever opening exists.
            if (TryMoveTo(cx, cy, cx + first, cy)) return;
            if (TryMoveTo(cx, cy, cx - first, cy)) return;
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
    /// player/creatures use to take lava/acid/fire burn and gas choke. Same polar row/col walk
    /// as <see cref="CollectInRadius"/>, counting only the four hazard materials.</summary>
    public (int lava, int acid, int gas, int fire) SampleHazardsNear(Vector2 worldPos, float radius)
    {
        var rSq = radius * radius;
        var (_, cy0) = WorldToCell(worldPos);
        var radial = (float)Planet.TileSize / Density;
        var rRows = (int)MathF.Ceiling(radius / radial) + 1;
        var rel = worldPos - Planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        int lava = 0, acid = 0, gas = 0, fire = 0;
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
                if (m != Material.Lava && m != Material.Acid && m != Material.Gas && m != Material.Fire) continue;
                if (Vector2.DistanceSquared(CellToWorld(cx0 + dx, cy), worldPos) > rSq) continue;
                if (m == Material.Lava) lava++;
                else if (m == Material.Acid) acid++;
                else if (m == Material.Fire) fire++;
                else gas++;
            }
        }
        return (lava, acid, gas, fire);
    }

    /// <summary>Water cell count within a world-space radius — the immersion probe behind
    /// swimming (player and creatures) and the breath meter. Same polar row/col walk as
    /// <see cref="SampleHazardsNear"/>, counting only water.</summary>
    public int CountWaterNear(Vector2 worldPos, float radius) =>
        CountNear(worldPos, radius, Material.Water);

    /// <summary>Cells of the given material within a world-space radius — the water counter
    /// generalised (creature status probes need oil too).</summary>
    public int CountNear(Vector2 worldPos, float radius, Material what)
    {
        var rSq = radius * radius;
        var (_, cy0) = WorldToCell(worldPos);
        var radial = (float)Planet.TileSize / Density;
        var rRows = (int)MathF.Ceiling(radius / radial) + 1;
        var rel = worldPos - Planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var count = 0;
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
                if ((Material)_mat[Idx(cx0 + dx, cy)] != what) continue;
                if (Vector2.DistanceSquared(CellToWorld(cx0 + dx, cy), worldPos) > rSq) continue;
                count++;
            }
        }
        return count;
    }

    // --- Lightning conduction: a strike into a pool flashes through every connected water
    // cell. The zapped set is transient (draw tint + the "am I in the electrified pool"
    // damage test) and expires in a fraction of a second; nothing is serialized.
    private readonly HashSet<int> _zapped = new();
    private float _zapUntil;

    /// <summary>Conduct a lightning strike through the body of water at (or just beside)
    /// <paramref name="worldPos"/>: flood-fills connected water cells (bounded by
    /// <paramref name="maxCells"/>), flash-tints them for a beat, and returns a few world
    /// positions scattered across the pool for arc/light effects. Empty list = no water at
    /// the strike point, nothing conducted.</summary>
    public List<Vector2> ZapWater(Vector2 worldPos, int maxCells = 700)
    {
        var hits = new List<Vector2>();
        var (sx, sy) = WorldToCell(worldPos);
        var startX = -1; var startY = -1;
        for (var dy = -2; dy <= 2 && startX < 0; dy++)
        {
            var cy = sy + dy;
            if (cy < 0 || cy >= Height) continue;
            for (var dx = -2; dx <= 2; dx++)
                if ((Material)_mat[Idx(sx + dx, cy)] == Material.Water)
                {
                    startX = WrapX(sx + dx, _cellsAt[cy]);
                    startY = cy;
                    break;
                }
        }
        if (startX < 0) return hits;

        _zapped.Clear();
        _zapUntil = _time + 0.22f;
        var queue = new Queue<(int cx, int cy)>();
        void Visit(int cx, int cy)
        {
            if (cy < 0 || cy >= Height || _zapped.Count >= maxCells) return;
            var i = Idx(cx, cy);
            if ((Material)_mat[i] != Material.Water || !_zapped.Add(i)) return;
            queue.Enqueue((WrapX(cx, _cellsAt[cy]), cy));
        }
        Visit(startX, startY);
        var visited = 0;
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            if (visited++ % 90 == 0) hits.Add(CellToWorld(cx, cy));
            Visit(cx + 1, cy);
            Visit(cx - 1, cy);
            var (icx, icy) = InnerCell(cx, cy);
            Visit(icx, icy);
            if (cy < Height - 1)
            {
                var oc = OuterCellCount(cx, cy);
                for (var w = 0; w < oc; w++)
                {
                    var (ocx, ocy) = OuterCell(cx, cy, w);
                    Visit(ocx, ocy);
                }
            }
        }
        return hits;
    }

    /// <summary>Whether a world position sits in (or right beside) water the current
    /// <see cref="ZapWater"/> flash is conducting through — the submerged-damage test.</summary>
    public bool ZappedAt(Vector2 worldPos)
    {
        if (_time >= _zapUntil || _zapped.Count == 0) return false;
        var (cx, cy) = WorldToCell(worldPos);
        for (var dy = -2; dy <= 2; dy++)
        {
            var y = cy + dy;
            if (y < 0 || y >= Height) continue;
            for (var dx = -2; dx <= 2; dx++)
                if (_zapped.Contains(Idx(cx + dx, y))) return true;
        }
        return false;
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
    /// <summary><paramref name="skipLiquids"/>: Water/Acid/Oil grid cells are being drawn
    /// by the dedicated liquid RT pass (<see cref="DrawLiquids"/>) this frame — skip them
    /// here. Flying liquid cells stay in this pass either way: they're airborne streaks,
    /// not part of a pool body.</summary>
    public void Draw(Renderer r, Vector2 viewCentre, float viewRadius, int stride = 1, bool skipLiquids = false)
    {
        var radial = (float)Planet.TileSize / Density;
        var (cyMin, cyMax) = VisibleRows(viewCentre, viewRadius, out var camAng);
        if (stride > 2) cyMin = Math.Max(cyMin, 120 * Density);   // orbital LOD only: stride 2 now occurs CLOSE-UP (fight-zoom, light-seed thinning) where deep cells must stay
        for (var cy = cyMin; cy <= cyMax; cy += stride)
        {
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            // Quad padding is NEIGHBOUR-AWARE at the close-up LOD (see below): the flat
            // 50% bleed that keeps pools seamless made every ISOLATED cell — a falling
            // droplet, a fire flick, a stray sand grain — read 50% fatter than the cell it
            // drew. Sizes are now picked per cell; this is the fallback for the zoomed-out
            // stride, where scaled quads need the full bleed and the oversize is sub-pixel.
            var size = new Vector2(chord * 1.5f * stride, radial * 1.5f * stride);
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
                if (skipLiquids && m is Material.Water or Material.Acid or Material.Oil) continue;
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
                // Neighbour-aware padding (stride 1 only): bleed an axis by the seam pad
                // ONLY where something abuts it — into a pool neighbour (hides the hairline
                // cracks between rotated polar quads) or into solid ground (invisible, the
                // terrain is behind). Toward open air the quad stays a crisp single grain.
                // Pads are FRACTIONS of the cell so the proportions survive Density changes.
                if (stride == 1)
                {
                    var chordPad = IsBlocked(cx - 1, cy) || IsBlocked(cx + 1, cy) ? 0.5f : 0.1f;
                    var radialPad = 0.1f;
                    if (cy > 0)
                    {
                        var (icx, icy) = InnerCell(cx, cy);
                        if (IsBlocked(icx, icy)) radialPad = 0.5f;
                    }
                    if (radialPad < 0.5f && cy < Height - 1)
                    {
                        var (pcx, pcy) = OuterCell(cx, cy, 0);
                        if (IsBlocked(pcx, pcy)) radialPad = 0.5f;
                    }
                    size = new Vector2(chord * (1f + chordPad), radial * (1f + radialPad));
                    // Rising flame dances: each fire grain jitters around its lattice site
                    // on its own ~12 Hz hash phase, so a column of flame licks and wavers
                    // grain-by-grain (the loose scattered look, kept for FIRE only — on
                    // liquids and powders it read as weird per user).
                    if (m == Material.Fire)
                    {
                        var tb = (int)(_time * 12f);
                        var fh = (cx * 73856093) ^ (cy * 19349663) ^ (tb * 48271);
                        var right = new Vector2(-up.Y, up.X);
                        centre += right * ((((fh >> 3) & 7) - 3.5f) * radial * 0.22f)
                                + up * ((((fh >> 9) & 7) - 3.5f) * radial * 0.22f);
                    }
                }
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

        // Flying cells: material mid-arc between grid sites, drawn as short streaks along
        // their velocity so fast ejecta reads as motion even at grain size. Skipped at the
        // zoomed-out LOD — sub-tile grains are invisible from orbit anyway.
        if (stride == 1)
        {
            var maxDistSq = (viewRadius + 8f) * (viewRadius + 8f);
            foreach (var f in _flying)
            {
                if (Vector2.DistanceSquared(f.Pos, viewCentre) > maxDistSq) continue;
                var col = ColorFor((Material)f.Mat, (int)f.Pos.X, (int)f.Pos.Y, f.Src, airborne: true);
                var speed = f.Vel.Length();
                // Streak length/width in CELL units so the smear scales with the grain:
                // at least one full cell long (sub-cell quads flicker against the pixel-
                // grid target's texel centres), at most 4 cells at full speed, thinning as
                // it stretches so it stays one grain's worth of ink — motion blur of a
                // grain, not a growing rectangle.
                var len = MathHelper.Clamp(speed * 0.016f, PxPerCell, PxPerCell * 6f);
                var wid = MathF.Max(PxPerCell * 0.8f, PxPerCell * PxPerCell / len);
                var rot = speed > 1f ? MathF.Atan2(f.Vel.Y, f.Vel.X) : 0f;
                r.Batch.Draw(r.Pixel, f.Pos, null, col, rot,
                    new Vector2(0.5f, 0.5f), new Vector2(len, wid), SpriteEffects.None, 0f);
            }
        }
    }

    /// <summary>Scratch for the waterline pass — surface cells found during the liquid
    /// scan, drawn as one overlapping band strip after the bodies.</summary>
    private readonly List<(int cx, int cy, byte m)> _surface = new();

    /// <summary>The LIQUID PASS: Water/Acid/Oil grid cells rasterized into the dedicated
    /// liquid render target (Game1 owns the target + batch state and composites the result
    /// over the world in ONE blend — see Renderer.CompositeLiquids). Two fill modes:
    ///  - blob (<paramref name="blobMode"/>, composite shader live): every cell touching
    ///    air draws the soft LiquidBlob three cells wide so its alpha COVERAGE spills into
    ///    neighbouring texels; the shader's threshold then rounds pool edges and fuses
    ///    near droplets — the metaball surface-tension look. Interior cells (all four
    ///    sides blocked) stay flat single-cell quads: their coverage is saturated anyway,
    ///    and the small quad keeps the shimmer per-cell instead of blob-stamping it.
    ///  - plain (no shader): hard quads with per-material translucency in the ALPHA
    ///    channel, drawn under BlendState.Opaque (replace, not blend) — the
    ///    NonPremultiplied composite blit then blends the body over the scene exactly once.
    /// Surface cells additionally draw the WATERLINE: wide overlapping bright bands whose
    /// bob phase is an integer wave count around the ring (continuous across the wrap), so
    /// pools get one connected undulating surface line instead of per-cell bobbing squares.
    /// Runs at stride 1 only — the zoomed-out LODs keep liquids in <see cref="Draw"/>.</summary>
    public void DrawLiquids(Renderer r, Vector2 viewCentre, float viewRadius, bool blobMode)
    {
        var radial = (float)Planet.TileSize / Density;
        var (cyMin, cyMax) = VisibleRows(viewCentre, viewRadius, out var camAng);
        var blob = r.LiquidBlob;
        var blobOrigin = new Vector2(blob.Width / 2f, blob.Height / 2f);
        _surface.Clear();
        for (var cy = cyMin; cy <= cyMax; cy++)
        {
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            var halfAng = MathF.Min(MathF.PI, viewRadius / MathF.Max(ringRadius, 1f));
            var cx0 = (int)(camAng / MathHelper.TwoPi * n);
            var range = Math.Min(n / 2, (int)(halfAng / MathHelper.TwoPi * n) + 2);
            var angStep = MathHelper.TwoPi / n;
            for (var d = -range; d <= range; d++)
            {
                var cx = cx0 + d;
                var idx = Idx(cx, cy);
                var m = (Material)_mat[idx];
                if (m is not (Material.Water or Material.Acid or Material.Oil)) continue;
                var cellAng = (cx + 0.5f) * angStep;
                var up = new Vector2(MathF.Cos(cellAng), MathF.Sin(cellAng));
                var centre = Planet.Center + up * ringRadius;
                var rotation = cellAng + MathHelper.PiOver2;
                // Sub-cell fall offset, same as Draw — streams glide between rows.
                var frac = MathF.Min(_travel[idx], 1f);
                if (frac > 0f) centre -= up * (frac * radial);
                var openOut = false;
                if (cy < Height - 1)
                {
                    var (ocx, ocy) = OuterCell(cx, cy, 0);
                    openOut = !IsBlocked(ocx, ocy);
                }
                var openIn = false;
                if (cy > 0)
                {
                    var (icx, icy) = InnerCell(cx, cy);
                    openIn = !IsBlocked(icx, icy);
                }
                var body = LiquidBody(m, cx, cy);
                var col = new Color(body.R, body.G, body.B, (byte)(MatAlpha(m) * 255f));
                var interior = !openOut && !openIn && IsBlocked(cx - 1, cy) && IsBlocked(cx + 1, cy);
                if (blobMode && !interior)
                {
                    r.Batch.Draw(blob, centre, null, col, rotation, blobOrigin,
                        new Vector2(chord * 3f / blob.Width, radial * 3f / blob.Height),
                        SpriteEffects.None, 0f);
                }
                else
                {
                    // Neighbour-aware padding as in Draw: bleed only into occupied sides so
                    // pools stay seamless while a lone droplet keeps its own grain size.
                    var chordPad = IsBlocked(cx - 1, cy) || IsBlocked(cx + 1, cy) ? 0.5f : 0.1f;
                    var radialPad = !openIn || !openOut ? 0.5f : 0.1f;
                    r.Batch.Draw(r.Pixel, centre, null, col, rotation, new Vector2(0.5f, 0.5f),
                        new Vector2(chord * (1f + chordPad), radial * (1f + radialPad)),
                        SpriteEffects.None, 0f);
                }
                if (openOut && _surface.Count < 4096) _surface.Add((cx, cy, (byte)m));
            }
        }

        // Waterline: one wide band per surface cell, overlapping its neighbours, bobbing
        // on a wave whose count divides the ring — a continuous line, drawn after every
        // body quad so its colour wins the replace blend.
        foreach (var (cx, cy, mb) in _surface)
        {
            var m = (Material)mb;
            var n = _cellsAt[cy];
            var ringRadius = (Planet.RingMin + (cy + 0.5f) / Density) * Planet.TileSize;
            var chord = MathHelper.TwoPi * ringRadius / n;
            var cellAng = (WrapX(cx, n) + 0.5f) * (MathHelper.TwoPi / n);
            var up = new Vector2(MathF.Cos(cellAng), MathF.Sin(cellAng));
            var waves = Math.Max(1, n / 24);
            var speed = m == Material.Water ? 2.4f : m == Material.Acid ? 1.8f : 1.0f;
            var wave = MathF.Sin(_time * speed - cellAng * waves);
            var centre = Planet.Center + up * (ringRadius + wave * radial * 0.6f);
            var body = SurfaceColor(m);
            var col = new Color(body.R, body.G, body.B, (byte)(MatAlpha(m) * 255f));
            r.Batch.Draw(r.Pixel, centre, null, col, cellAng + MathHelper.PiOver2,
                new Vector2(0.5f, 0.5f), new Vector2(chord * 2.2f, radial * 0.9f),
                SpriteEffects.None, 0f);
        }
    }

    /// <summary>Lava/acid glow emitters. Same LOD contract as <see cref="Draw"/> — the
    /// zoomed-out stride keeps the scan (and the per-light lightmap blits) bounded.</summary>
    public void AddLights(Renderer r, Vector2 viewCentre, float viewRadius, int stride = 1)
    {
        var (cyMin, cyMax) = VisibleRows(viewCentre, viewRadius, out var camAng);
        if (stride > 2) cyMin = Math.Max(cyMin, 120 * Density);   // orbital LOD only — see Draw
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
                if (lm != Material.Lava && lm != Material.Acid && lm != Material.Fire) continue;
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
                // Acid glows a dim toxic green; lava a hot orange; open flame bright and warm.
                // Radii are real reach: under the propagated grid, seeds combine by MAX (the
                // old additive blits summed), so a burning pool must carry its brightness in
                // each seed — a fire that lights 15 tiles of cave needs to say so.
                r.AddLight(CellToWorld(cx, cy),
                    lm == Material.Acid ? 24f : lm == Material.Fire ? 60f : 50f,
                    lm == Material.Acid ? new Color(90, 190, 40)
                    : lm == Material.Fire ? new Color(255, 170, 70) : new Color(255, 130, 40));
            }
        }

        // Airborne lava beads and embers glow too — capped so a spark storm can't flood
        // the lightmap blit budget.
        var flyLights = 0;
        var flyDistSq = viewRadius * viewRadius;
        foreach (var f in _flying)
        {
            var fm = (Material)f.Mat;
            if (fm != Material.Lava && fm != Material.Fire) continue;
            if (Vector2.DistanceSquared(f.Pos, viewCentre) > flyDistSq) continue;
            // Flying fire IS the flamethrower's burning fuel — it has to visibly carry
            // its light along the arc, not just glow once it lands.
            r.AddLight(f.Pos, 45f, new Color(255, 150, 60));
            if (++flyLights >= 48) break;
        }
    }

    /// <summary>Fill every open (Sky) tile between two radii with a material — the lava
    /// sea. <paramref name="floorTilesFromCentre"/> keeps the fill OFF the sealed deep cave
    /// strata beneath (0 = the old fill-everything-below behaviour).</summary>
    public void FillSkyTilesWithin(float radiusTilesFromCentre, Material m,
        float floorTilesFromCentre = 0f)
    {
        var maxRing = (int)radiusTilesFromCentre - Planet.RingMin;
        if (maxRing <= 0) return;
        maxRing = Math.Min(maxRing, Planet.Rings);
        var minRing = Math.Max(0, (int)floorTilesFromCentre - Planet.RingMin);
        // FULL, SILENT fill. The old SpawnInTile path drew Density² random cells with
        // replacement — a ~63% porous sea that was never a design choice, just the
        // artifact of sampling with replacement — and enqueued every one, so the load
        // stall was first a wake storm over the whole sea and then a sea-wide collapse
        // as the porosity settled out (seconds of 0-fps at Density 8). A solid sea is
        // born at rest: the boundary wake below rouses only the surface and the cave
        // mouths, and nothing has anywhere to fall. The band's top (LavaFillFrac) and
        // floor (the strata seam) are the designed sea contract either way.
        for (var r = minRing; r < maxRing; r++)
        {
            var n = Planet.TilesAt(r);
            for (var t = 0; t < n; t++)
                if (Planet.Get(r, t) == TileKind.Sky)
                {
                    var c0y = r * Density;
                    var c0x = t * Density;
                    for (var dy = 0; dy < Density; dy++)
                        for (var dx = 0; dx < Density; dx++)
                            PlaceSilent(c0x + dx, c0y + dy, m);
                }
        }
        WakeFreeSurfaces(minRing * Density, maxRing * Density);
    }

    /// <summary>Flat liquid body colour: ONE colour per material plus a slow shimmer band
    /// travelling smoothly across the pool. The old per-cell hash jitter and hash-phased
    /// shimmer made adjacent cells sparkle out of sync — which is exactly what read as "a
    /// bunch of stacked pixels" instead of one body of water. Phase is a smooth function
    /// of position (an INTEGER multiple of the ring angle, so it's continuous across the
    /// wrap seam). Returns an opaque colour; callers apply translucency.</summary>
    private Color LiquidBody(Material m, int cx, int cy)
    {
        // Flying liquid cells land here with world px coords (see Draw) — out of row
        // bounds, so they get a coarse positional phase instead of an angular one.
        float ang;
        if (cy >= 0 && cy < Height)
        {
            var n = _cellsAt[cy];
            ang = (WrapX(cx, n) + 0.5f) / n * MathHelper.TwoPi;
        }
        else ang = cx * 0.01f;
        switch (m)
        {
            case Material.Water:
                // A conducting lightning strike flashes the whole pool electric white-blue
                // for a beat (see ZapWater).
                if (_zapUntil > _time && cy >= 0 && cy < Height
                    && _zapped.Contains(_rowOffsets[cy] + WrapX(cx, _cellsAt[cy])))
                    return new Color(216, 236, 255);
                return Tint(new Color(46, 90, 178),
                    (int)(MathF.Sin(_time * 1.6f + ang * 17f + cy * 0.045f) * 10f));
            case Material.Acid:
                return Tint(new Color(120, 200, 40),
                    (int)(MathF.Sin(_time * 2.0f + ang * 23f + cy * 0.05f) * 12f));
            default: // Oil — near-black slick with a slow crawling sheen.
                return Tint(new Color(38, 32, 26),
                    (int)(MathF.Sin(_time * 1.1f + ang * 13f + cy * 0.035f) * 7f));
        }
    }

    /// <summary>Translucency for the plain (no-shader) liquid RT fill — carried in the RT's
    /// alpha channel and applied once by the NonPremultiplied composite blit.</summary>
    private static float MatAlpha(Material m) => m switch
    {
        Material.Water => 0.78f,
        Material.Acid => 0.82f,
        _ => 0.94f,
    };

    /// <summary>Waterline band colour — the brighter surface strip over an open pool.</summary>
    private static Color SurfaceColor(Material m) => m switch
    {
        Material.Water => new Color(110, 175, 230),
        Material.Acid => new Color(178, 235, 96),
        _ => new Color(84, 72, 58),
    };

    private Color ColorFor(Material m, int cx, int cy, byte srcByte, bool airborne = false)
    {
        var hash = (cx * 73856093) ^ (cy * 19349663);
        var jitter = ((hash >> 4) & 31) - 16;
        switch (m)
        {
            case Material.Sand:    return Tint(new Color(190, 158, 92), jitter / 3);
            case Material.Snow:    return Tint(new Color(232, 240, 250), jitter / 5);
            case Material.Water:
                return LiquidBody(m, cx, cy) * 0.78f;
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
                return LiquidBody(m, cx, cy) * 0.82f;
            case Material.Gas:
            {
                // Faint yellow-green haze — low alpha so the wall behind reads through it.
                var swirl = (int)(MathF.Sin(_time * 1.2f + ((hash >> 2) & 15) * 0.5f) * 10f);
                var g = new Color(150, 190, 70);
                return new Color(g.R + jitter / 5, g.G + jitter / 5 + swirl, g.B + jitter / 5, (byte)110);
            }
            case Material.Fire:
            {
                // Fast per-cell flicker over a hard fire ramp, TIERED BY BODY POSITION the
                // way a real flame (and Noita's) shades: the BASE — nothing burning below
                // it — rolls white-hot tones; the middle body burns gold-orange; the TIPS
                // (fire below, open above) cool to deep red, so a burning pool reads as
                // licking tongues bright at the root and dark at the crown instead of
                // uniform orange noise. Flying embers land here with world coords (hash
                // duty only) — the bounds guard keeps their tier probe safe, and a random
                // tier on an airborne mote is invisible anyway.
                var t = (int)(_time * 14f);
                var h2 = (uint)(hash ^ (t * 48271));
                // Airborne fire (hose payload, flying embers) pins to the MID body tier:
                // the grid probe below reads garbage at world-px coords and mostly rolled
                // tier 0 — pale white-yellow streaks flickering out of key with the flame
                // stream they fly inside.
                var tier = airborne ? 1 : 0;
                if (!airborne && cy > 0 && cy < Height - 1)
                {
                    var (icx, icy) = InnerCell(cx, cy);
                    if (Get(icx, icy) == Material.Fire)
                    {
                        var (ocx, ocy) = OuterCell(cx, cy, 0);
                        tier = Get(ocx, ocy) == Material.Fire ? 1 : 2;
                    }
                }
                return (tier, h2 & 3) switch
                {
                    (0, 0) => new Color(255, 252, 210),
                    (0, 1) => new Color(255, 240, 150),
                    (0, _) => new Color(255, 210, 90),
                    (1, 0) => new Color(255, 220, 110),
                    (1, 1 or 2) => new Color(255, 165, 50),
                    (1, _) => new Color(255, 120, 30),
                    (2, 0) => new Color(255, 140, 40),
                    (2, 1 or 2) => new Color(225, 90, 22),
                    _ => new Color(160, 45, 18),
                };
            }
            case Material.Oil:
                return LiquidBody(m, cx, cy) * 0.94f;
            case Material.Dirt:    return Tint(new Color(115, 75, 42), jitter / 3);
            case Material.Gravel:  return Tint(new Color(125, 120, 110), jitter / 3);
            case Material.Dust:
            {
                // Lighten the source tile's base colour so dust reads as granular crumb rather
                // than a tile facsimile. Sky source (= no tag) falls back to a generic sand tone.
                var src = (TileKind)srcByte;
                var b = src == TileKind.Sky ? new Color(190, 158, 92) : Tiles.BaseColor(src);
                // Precious-metal dust GLITTERS: grains take the metal's bright speckle
                // colour, and a time-cycling hash picks a few grains per moment to flash
                // white — a pile of gold filings winks at you.
                if (src is TileKind.GoldOre or TileKind.SilverOre or TileKind.PlatinumOre)
                {
                    var tick = (uint)(_time * 5f);
                    if ((((uint)(hash >> 6) ^ (tick * 2654435761u)) & 15u) == 0) return Color.White;
                    var metal = Tiles.OreSpeckle(src);
                    return Tint(Color.Lerp(b, metal, 0.55f), jitter / 4);
                }
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
