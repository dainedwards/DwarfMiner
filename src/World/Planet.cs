using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Polar tile grid with per-band halving so each tile's arc width stays roughly 4–8px at
/// every depth. The world is divided into bands of constant <c>TilesAt(r)</c>; whenever the
/// ring radius halves, the band's tile count halves too. Inner-ring radial neighbours are 1
/// tile (collapsed); outer-ring radial neighbours are 1 or 2 tiles (split across the boundary).
///
/// Public API still uses (x, y) → (ring, angle). Below ring 0 = Core; above outer ring = Sky.
/// </summary>
public sealed class Planet
{
    /// <summary>Ring count of a scale-1.0 planet — the norm PlanetDef.SizeScale multiplies.</summary>
    public const int StandardRings = 400;
    public const int RingMin = 40;
    public const int TileSize = 4;

    /// <summary>World-gen scale shim: how many of today's rings span one legacy 8-px tile.
    /// The 4-px tile grid quartered every block, but feature sizes (mountains, caves, depth
    /// bands) are still authored in the original 8-px tile units and multiplied by this.</summary>
    public const float LegacyTileScale = 8f / TileSize;

    /// <summary>Rings of headroom kept between the baseline surface and the top of the tile
    /// grid, at every planet size — enough for the tallest mountains plus clear sky. The
    /// surface therefore sits at <see cref="SurfaceRing"/> = Rings − SkyHeadroom.</summary>
    public const int SkyHeadroom = 142;

    /// <summary>Playable ring count for THIS planet — varies with PlanetDef.SizeScale
    /// (≈140 for a 0.7× dwarf world up to ≈360 for a 1.8× giant).</summary>
    public readonly int Rings;

    /// <summary>Ring index of the baseline surface (before mountains/lakes) on this planet.
    /// Depth and oxygen math measure "below surface" from here.</summary>
    public int SurfaceRing => Rings - SkyHeadroom;

    /// <summary>Tile count per ring. Geometry depends only on the ring count, so it's built
    /// once per distinct size and shared between planets (survey worlds spawn many).</summary>
    private readonly int[] _tilesAt;
    private readonly int[] _ringOffsets;
    private readonly int _totalTiles;

    private static readonly Dictionary<int, (int[] tilesAt, int[] offsets, int total)> _geometryCache = new();

    private static (int[] tilesAt, int[] offsets, int total) GeometryFor(int rings)
    {
        lock (_geometryCache)
        {
            if (_geometryCache.TryGetValue(rings, out var g)) return g;
            var tilesAt = new int[rings];
            var offsets = new int[rings + 1];
            for (var r = 0; r < rings; r++)
            {
                tilesAt[r] = ComputeTilesAt(r);
                offsets[r + 1] = offsets[r] + tilesAt[r];
            }
            g = (tilesAt, offsets, offsets[rings]);
            _geometryCache[rings] = g;
            return g;
        }
    }

    /// <summary>Per-ring tile count chosen so each tile's chord ≈ <see cref="TileSize"/> px —
    /// roughly square at every depth. TPR varies smoothly with radius rather than in discrete
    /// halving bands, eliminating sliver tiles deep underground at the cost of variable
    /// (non-power-of-2) ring counts.</summary>
    private static int ComputeTilesAt(int r)
    {
        var globalRadius = RingMin + r + 0.5f;
        return Math.Max(8, (int)Math.Round(MathHelper.TwoPi * globalRadius));
    }

    public int TilesAt(int r)
    {
        if (r < 0 || r >= Rings) return 1;
        return _tilesAt[r];
    }

    public Vector2 Center;
    public int Radius => RingMin + Rings;
    public int Size => Rings;

    /// <summary>Tiles world gen wants filled with water cells (surface lakes + underground
    /// reservoirs). Water lives exclusively in the cell sim — never as solid tiles — so gen
    /// only records the sites here and Game1 pours the cells in once Cells exists.</summary>
    public readonly List<(int x, int y)> WaterSeeds = new();

    /// <summary>Cave tiles world gen wants filled with hazard cells — flammable gas (rises to
    /// cave roofs) and corrosive acid (pools on cave floors). Like <see cref="WaterSeeds"/>,
    /// gen only records the sites; Game1 pours the cells once Cells exists.</summary>
    public readonly List<(int x, int y)> GasSeeds = new();
    public readonly List<(int x, int y)> AcidSeeds = new();

    /// <summary>Volcano plumbing tiles to prime with lava — crater pool, throat, and the
    /// deep magma chamber (see WorldGen.CarveVolcanoes). Acid volcanoes record theirs in
    /// <see cref="AcidSeeds"/> instead.</summary>
    public readonly List<(int x, int y)> LavaSeeds = new();

    /// <summary>Volcano vent sites (just above each crater pool) + fluid type — where Game1
    /// spawns fresh cells during an eruption. Persisted with the tile state so resumed runs
    /// keep erupting.</summary>
    public readonly List<(int x, int y, bool acid)> VolcanoVents = new();

    /// <summary>Playable ring count for a planet of the given size scale — shared by world
    /// gen and the run-save loader so restored planets get matching geometry.</summary>
    public static int RingsFor(float sizeScale) =>
        Math.Max(120, (int)MathF.Round(StandardRings * sizeScale));

    private readonly TileKind[] _tiles;
    private readonly byte[] _damage;
    /// <summary>Background "wall" tile kind per cell — what was there before caves were carved.
    /// Visible as a darker silhouette behind Sky tiles inside the planet (Terraria-style).</summary>
    private readonly TileKind[] _wall;

    public Planet(Vector2 center, int rings = StandardRings)
    {
        Center = center;
        Rings = rings;
        (_tilesAt, _ringOffsets, _totalTiles) = GeometryFor(rings);
        _tiles = new TileKind[_totalTiles];
        _damage = new byte[_totalTiles];
        _wall = new TileKind[_totalTiles];
    }

    public int Index(int x, int y)
    {
        var n = TilesAt(x);
        var w = ((y % n) + n) % n;
        return _ringOffsets[x] + w;
    }

    /// <summary>Decompose a flat tile index back to (ring, angle). Linear search across rings;
    /// fine for the rates we call it at (dirty queue processing).</summary>
    public (int x, int y) UnIndex(int idx)
    {
        int lo = 0, hi = Rings;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_ringOffsets[mid + 1] <= idx) lo = mid + 1;
            else hi = mid;
        }
        return (lo, idx - _ringOffsets[lo]);
    }

    public bool InBounds(int x, int y) => x >= 0 && x < Rings;

    public TileKind Get(int x, int y)
    {
        if (x < 0) return TileKind.Core;
        if (x >= Rings) return TileKind.Sky;
        return _tiles[Index(x, y)];
    }

    public void Set(int x, int y, TileKind k)
    {
        if (!InBounds(x, y)) return;
        var i = Index(x, y);
        _tiles[i] = k;
        _damage[i] = 0;
    }

    public byte Damage(int x, int y) =>
        InBounds(x, y) ? _damage[Index(x, y)] : (byte)0;

    /// <summary>Background-wall material at this tile. Returns Sky if outside the playable rings.</summary>
    public TileKind GetWall(int x, int y) =>
        InBounds(x, y) ? _wall[Index(x, y)] : TileKind.Sky;

    /// <summary>Set the background wall (called during world gen).</summary>
    public void SetWall(int x, int y, TileKind k)
    {
        if (!InBounds(x, y)) return;
        _wall[Index(x, y)] = k;
    }

    /// <summary>Apply <paramref name="power"/> mining damage to the tile at (x,y). Returns
    /// the broken tile kind on shatter, or null if it just took damage. Hardness ≥ 99 is the
    /// "anchor-class" gate (PlanetCore, Core, Support beams) — those won't take damage from a
    /// regular swing. The hammer / core drill bypass that gate via
    /// <paramref name="effectiveHardness"/>: pass a smaller value (e.g. 8) and the tile will
    /// take damage as if it had that hardness, while still being broken by repeated hits.</summary>
    public TileKind? Mine(int x, int y, int power, int? effectiveHardness = null)
    {
        if (!InBounds(x, y)) return null;
        var i = Index(x, y);
        var k = _tiles[i];
        if (k == TileKind.Sky) return null;
        var hardness = effectiveHardness ?? Tiles.Hardness(k);
        if (hardness >= 99) return null;
        var dmg = _damage[i] + Math.Max(1, power * 32 / hardness);
        if (dmg >= 255)
        {
            _tiles[i] = TileKind.Sky;
            _damage[i] = 0;
            return k;
        }
        _damage[i] = (byte)dmg;
        return null;
    }

    public Vector2 TileToWorld(int x, int y)
    {
        if (x < 0) return Center;
        var n = TilesAt(x);
        var w = ((y % n) + n) % n;
        var radius = (RingMin + x + 0.5f) * TileSize;
        var angle = (w + 0.5f) / n * MathHelper.TwoPi;
        return Center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    public (int x, int y) WorldToTile(Vector2 p)
    {
        var rel = p - Center;
        var dist = rel.Length();
        var ringIdx = (int)(dist / TileSize) - RingMin;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var nRing = MathHelper.Clamp(ringIdx, 0, Rings - 1);
        var n = TilesAt(nRing);
        var t = (int)(ang / MathHelper.TwoPi * n);
        if (t >= n) t = n - 1;
        return (ringIdx, t);
    }

    public Vector2 GravityAt(Vector2 worldPos)
    {
        var to = Center - worldPos;
        var len = to.Length();
        if (len < 0.0001f) return Vector2.Zero;
        return to / len;
    }

    public Vector2 UpAt(Vector2 worldPos)
    {
        var up = worldPos - Center;
        var len = up.Length();
        if (len < 0.0001f) return -Vector2.UnitY;
        return up / len;
    }

    public bool IsSolidAt(Vector2 worldPos)
    {
        var (x, y) = WorldToTile(worldPos);
        return Tiles.IsSolid(Get(x, y));
    }

    /// <summary>Inner radial neighbour. With smooth per-ring TPR the inner ring has slightly
    /// fewer tiles; we take the inner tile that contains this tile's centre angle.</summary>
    public (int x, int y) InnerNeighbour(int x, int y)
    {
        if (x <= 0) return (-1, 0);
        var nIn = TilesAt(x - 1);
        var nOut = TilesAt(x);
        var w = ((y % nOut) + nOut) % nOut;
        if (nIn == nOut) return (x - 1, w);
        return (x - 1, (int)((w + 0.5) * nIn / nOut) % nIn);
    }

    /// <summary>Outer-radial neighbour count for this specific tile — usually 1, occasionally 2
    /// when the outer ring's TPR is large enough that this tile's arc spans two outer tiles.</summary>
    public int OuterNeighbourCount(int x, int y)
    {
        if (x >= Rings - 1) return 1;
        var nIn = TilesAt(x);
        var nOut = TilesAt(x + 1);
        if (nIn == nOut) return 1;
        var w = ((y % nIn) + nIn) % nIn;
        var first = (int)Math.Floor((double)w * nOut / nIn);
        var lastExcl = (int)Math.Ceiling((double)(w + 1) * nOut / nIn);
        return Math.Max(1, lastExcl - first);
    }

    /// <summary>Outer-radial neighbour at index <paramref name="which"/> in [0, OuterNeighbourCount).</summary>
    public (int x, int y) OuterNeighbour(int x, int y, int which = 0)
    {
        if (x >= Rings - 1) return (Rings, 0);
        var nIn = TilesAt(x);
        var nOut = TilesAt(x + 1);
        var w = ((y % nIn) + nIn) % nIn;
        if (nIn == nOut) return (x + 1, w);
        var first = (int)Math.Floor((double)w * nOut / nIn);
        return (x + 1, (first + which) % nOut);
    }

    public IEnumerable<(int x, int y)> AllTiles()
    {
        for (var x = 0; x < Rings; x++)
        {
            var n = TilesAt(x);
            for (var y = 0; y < n; y++)
                yield return (x, y);
        }
    }

    /// <summary>Serialize the full mutable tile state (foreground, damage, walls) for the
    /// run save. Array lengths are derived from the static ring geometry, so only the raw
    /// bytes are written.</summary>
    public void WriteState(System.IO.BinaryWriter w)
    {
        var tiles = new byte[_totalTiles];
        var walls = new byte[_totalTiles];
        for (var i = 0; i < _totalTiles; i++)
        {
            tiles[i] = (byte)_tiles[i];
            walls[i] = (byte)_wall[i];
        }
        w.Write(_totalTiles);
        w.Write(tiles);
        w.Write(_damage);
        w.Write(walls);
        w.Write(VolcanoVents.Count);
        foreach (var (x, y, acid) in VolcanoVents)
        {
            w.Write(x);
            w.Write(y);
            w.Write(acid);
        }
    }

    /// <summary>Restore state written by <see cref="WriteState"/>. Throws on a geometry
    /// mismatch (save from an incompatible build).</summary>
    public void ReadState(System.IO.BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n != _totalTiles)
            throw new System.IO.InvalidDataException($"planet tile count mismatch: {n} vs {_totalTiles}");
        var tiles = r.ReadBytes(_totalTiles);
        var damage = r.ReadBytes(_totalTiles);
        var walls = r.ReadBytes(_totalTiles);
        for (var i = 0; i < _totalTiles; i++)
        {
            _tiles[i] = (TileKind)tiles[i];
            _damage[i] = damage[i];
            _wall[i] = (TileKind)walls[i];
        }
        VolcanoVents.Clear();
        var vents = r.ReadInt32();
        for (var i = 0; i < vents; i++)
            VolcanoVents.Add((r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()));
    }
}
