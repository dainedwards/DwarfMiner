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

    /// <summary>Gravity multiplier for every body on this world (PlanetDef.GravityScale —
    /// 0.25 on the Hollow asteroid). Not persisted: WorldGen stamps it at generation and
    /// RunSave re-stamps it from the def on load. Creatures read it through their ticks;
    /// the player's copy lives on Player.Gravity, set the same way.</summary>
    public float GravityScale = 1f;

    /// <summary>True on airless worlds (PlanetDef.Airless): the renderer drops the whole
    /// atmosphere kit — no dusk backdrop, no haze shell, stars burning from the surface up.
    /// Stamped alongside <see cref="GravityScale"/>, not persisted.</summary>
    public bool Airless;

    /// <summary>Terrain surface RING index per angle sample (mountains excluded): the
    /// baseline plus the elevation/lump noise world gen actually carved. Flat worlds barely
    /// deviate from <see cref="SurfaceRing"/>; the lumpy belt asteroid swings ±tens of
    /// rings, and depth-below-surface (oxygen, exposure) must follow the local ground —
    /// a valley floor under open sky is NOT underground. Null on legacy saves → callers
    /// fall back to the flat baseline. Persisted with the tile state.</summary>
    public float[]? SurfaceProfile;

    /// <summary>Local terrain-surface radius, in global ring units (RingMin included), at
    /// the bearing of <paramref name="worldPos"/> — interpolated from the profile, or the
    /// flat baseline when no profile was stamped.</summary>
    public float SurfaceRadiusAt(Vector2 worldPos)
    {
        if (SurfaceProfile is not { Length: > 0 } prof)
            return RingMin + SurfaceRing;
        var rel = worldPos - Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        var t = (ang / MathHelper.TwoPi + 1f) % 1f * prof.Length;
        var i = (int)t;
        var frac = t - i;
        var a = prof[i % prof.Length];
        var b = prof[(i + 1) % prof.Length];
        return RingMin + a + (b - a) * frac;
    }

    /// <summary>Tiles world gen wants filled with water cells (surface lakes + underground
    /// reservoirs). Water lives exclusively in the cell sim — never as solid tiles — so gen
    /// only records the sites here and Game1 pours the cells in once Cells exists.</summary>
    public readonly List<(int x, int y)> WaterSeeds = new();

    /// <summary>Cave tiles world gen wants filled with hazard cells — flammable gas (rises to
    /// cave roofs) and corrosive acid (pools on cave floors). Like <see cref="WaterSeeds"/>,
    /// gen only records the sites; Game1 pours the cells once Cells exists.</summary>
    public readonly List<(int x, int y)> GasSeeds = new();
    public readonly List<(int x, int y)> AcidSeeds = new();
    /// <summary>Cave tiles world gen wants filled with oil cells — flammable sumps that pool
    /// on cave floors, inert until fire or lava reaches them. Same contract as the others.</summary>
    public readonly List<(int x, int y)> OilSeeds = new();

    /// <summary>Volcano plumbing tiles to prime with lava — crater pool, throat, and the
    /// deep magma chamber (see WorldGen.CarveVolcanoes). Acid volcanoes record theirs in
    /// <see cref="AcidSeeds"/> instead.</summary>
    public readonly List<(int x, int y)> LavaSeeds = new();

    /// <summary>Volcano vent sites (just above each crater pool) + fluid type — where Game1
    /// spawns fresh cells during an eruption. Persisted with the tile state so resumed runs
    /// keep erupting.</summary>
    public readonly List<(int x, int y, bool acid)> VolcanoVents = new();

    /// <summary>Living trees the ecosystem tracks. WorldGen registers one per planted tree;
    /// <see cref="Systems.TreeEcology"/> regrows a felled one from its surviving underground
    /// roots (faster when the biome's rain — water, acid, or fire — waters it). Not persisted:
    /// standing trees survive in the tile grid across a reload; on load the sites are rebuilt
    /// by scanning for TreeRoot tiles so regrowth keeps working.</summary>
    public readonly List<TreeSite> Trees = new();

    /// <summary>Civilian spawn sites on city worlds — skyscraper doorways and apartment
    /// floors (WorldGen.RaiseCity). SpawnDirector prefers these when restocking the city
    /// biome's fauna so the towers actually read as inhabited. Persisted with the tile
    /// state so resumed runs keep their citizens.</summary>
    public readonly List<(int x, int y)> CitySpawns = new();

    /// <summary>Skyscraper facade frames — bearing, half-width in px (a whole multiple of
    /// half a tile), and the ring range including rooftop furniture. WorldGen.RaiseCity
    /// records one per tower; the renderer snaps engineered tiles onto each frame's column
    /// lattice (<see cref="FacadeSnapAngle"/>) so machined hulls draw dead straight instead
    /// of inheriting the polar grid's per-ring lattice drift. Persisted with the tile
    /// state.</summary>
    public readonly List<(float ang, float halfW, int footR, int topR)> CityFacades = new();

    /// <summary>Draw-time-only lattice snap for machined structures. The polar grid's
    /// tiles-per-ring drifts every ring, so a tower's tile centres jitter laterally by up to
    /// half a tile ring-to-ring and its facade renders as a leaning staircase. Towers are
    /// GENERATED column-by-column against their own lateral lattice (see WorldGen), so each
    /// tile sits within half a tile of its intended column slot: this maps a tile's bearing
    /// back to that slot. Collision and the grid never move — the shift is ≤ ~½ tile, the
    /// same order the crust shader displaces natural silhouettes.</summary>
    public float FacadeSnapAngle(int ring, float angle, float ringRadius)
    {
        foreach (var (fAng, halfW, footR, topR) in CityFacades)
        {
            if (ring < footR || ring > topR) continue;
            var lat = MathHelper.WrapAngle(angle - fAng) * ringRadius;
            if (MathF.Abs(lat) > halfW + TileSize * 1.9f) continue;   // slab ledges included
            // Exact inversion of the generator's column→tile map: candidate slots around
            // the nearest one, snapped to whichever FORWARD-maps onto this very tile. A
            // plain nearest-slot round drifts off by one near the edges of WIDE hulls
            // (ring chords run a hair off TileSize, and the error grows with |lat|),
            // which re-introduced the 4px sawtooth the snap exists to erase.
            var n = TilesAt(ring);
            var chordAng = MathHelper.TwoPi / n;
            var norm = (angle % MathHelper.TwoPi + MathHelper.TwoPi) % MathHelper.TwoPi;
            var tile = (int)MathF.Floor(norm / chordAng) % n;
            var slot0 = (int)MathF.Round((lat + halfW) / TileSize - 0.5f);
            foreach (var d in _doorSlack)   // {0, +1, -1} — reused nearest-first probe order
            {
                var slat = -halfW + (slot0 + d + 0.5f) * TileSize;
                if (MathF.Abs(slat - lat) > TileSize * 1.1f) continue;
                var tj = (int)MathF.Round((fAng + slat / ringRadius) / chordAng - 0.5f);
                if (((tj % n) + n) % n != tile) continue;
                return fAng + slat / ringRadius;
            }
            // No slot owns this tile (a backfill filler, or the map degenerated) — leave
            // it where the grid put it.
            return angle;
        }
        return angle;
    }

    /// <summary>Lizardman den sites — the chamber hearts of each underground lizard city
    /// (WorldGen.CarveLizardCities). SpawnDirector spawns warren guards near these, so the
    /// warrens stay garrisoned however often the player clears them. Persisted with the
    /// tile state so a resumed run's warrens aren't left abandoned.</summary>
    public readonly List<(int x, int y)> LizardDens = new();

    /// <summary>Playable ring count for a planet of the given size scale — shared by world
    /// gen and the run-save loader so restored planets get matching geometry.</summary>
    public static int RingsFor(float sizeScale) =>
        Math.Max(240, (int)MathF.Round(StandardRings * sizeScale));

    private readonly TileKind[] _tiles;
    private readonly byte[] _damage;
    /// <summary>Background "wall" tile kind per cell — what was there before caves were carved.
    /// Visible as a darker silhouette behind Sky tiles inside the planet (Terraria-style).</summary>
    private readonly TileKind[] _wall;
    /// <summary>Embedded gem per tile (Sky = none). Gems aren't blocks of their own — each is
    /// an object seated inside a host tile of ordinary rock or common ore. The renderer draws
    /// it as a glowing lozenge on the host's face; shattering the host pops a physical Pickup
    /// (see Cells.SpawnDustInTile); acid/lava destroying the host destroys the gem with it.</summary>
    private readonly TileKind[] _gem;

    /// <summary>What a compacted Conglomerate tile is made of: the exact cells (material +
    /// dust source, run-length counted) that pressed into it, plus the blended display tint.
    /// Breaking the tile spills these cells back out, so resource value round-trips exactly
    /// through compaction. Sparse — only Conglomerate tiles have entries.</summary>
    public sealed record TileComposition(Color Tint, (byte mat, byte src, byte count)[] Parts);

    private readonly Dictionary<int, TileComposition> _composition = new();

    // (Compaction now stamps a plain majority-kind tile via Set — see Cells.Compact. The
    // composition table below only serves legacy Conglomerate tiles restored from old saves:
    // the renderer still tints them and shattering still spills their stored cells.)

    /// <summary>Composition of the Conglomerate tile at (x,y), or null. Read-only peek (renderer tint).</summary>
    public TileComposition? GetComposition(int x, int y) =>
        InBounds(x, y) && _composition.TryGetValue(Index(x, y), out var c) ? c : null;

    /// <summary>Remove and return the composition at (x,y) — called when the tile shatters and
    /// its cells spill back into the sim. Entries are deliberately NOT cleared by
    /// <see cref="Set"/> (a crumbling tile Sets Sky first, then spawns its debris); an entry
    /// orphaned by melt/corrosion is harmless — it's only read while the tile is Conglomerate
    /// and gets overwritten if the tile ever compacts again.</summary>
    public TileComposition? TakeComposition(int x, int y)
    {
        if (!InBounds(x, y)) return null;
        var i = Index(x, y);
        if (!_composition.TryGetValue(i, out var c)) return null;
        _composition.Remove(i);
        return c;
    }

    public Planet(Vector2 center, int rings = StandardRings)
    {
        Center = center;
        Rings = rings;
        (_tilesAt, _ringOffsets, _totalTiles) = GeometryFor(rings);
        _tiles = new TileKind[_totalTiles];
        _damage = new byte[_totalTiles];
        _wall = new TileKind[_totalTiles];
        _gem = new TileKind[_totalTiles];
    }

    /// <summary>The gem embedded in the tile at (x,y), or Sky for none.</summary>
    public TileKind GemAt(int x, int y) =>
        InBounds(x, y) ? _gem[Index(x, y)] : TileKind.Sky;

    /// <summary>Seat a gem inside the tile at (x,y) (world gen).</summary>
    public void SetGem(int x, int y, TileKind gem)
    {
        if (InBounds(x, y)) _gem[Index(x, y)] = gem;
    }

    /// <summary>Remove and return the embedded gem at (x,y), or Sky if none. Shatter paths
    /// call this to pop the pickup; acid/melt call it and discard (the gem dissolves too).</summary>
    public TileKind TakeGem(int x, int y)
    {
        if (!InBounds(x, y)) return TileKind.Sky;
        var i = Index(x, y);
        var g = _gem[i];
        _gem[i] = TileKind.Sky;
        return g;
    }

    /// <summary>Total tile count across all rings — sizes per-tile scratch arrays (Physics
    /// flood stamps) without callers re-deriving the ring geometry.</summary>
    public int TileCount => _totalTiles;

    public int Index(int x, int y)
    {
        // Fast path: callers overwhelmingly pass an already-wrapped angle, and this is the
        // innermost op of tile reads, flood fills and the draw loops — skip the two divisions.
        var n = TilesAt(x);
        if ((uint)y < (uint)n) return _ringOffsets[x] + y;
        var w = y % n;
        if (w < 0) w += n;
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

    /// <summary>Live count of ReinforcedSupport tiles. They're rare and player-placed, so
    /// Physics skips the per-flood-tile anchor-halo probe entirely while this is zero —
    /// that probe is ~10 neighbour reads on every tile of every cave-in flood.</summary>
    public int ReinforcedCount { get; private set; }

    private void TrackKindChange(TileKind from, TileKind to)
    {
        if (from == TileKind.ReinforcedSupport) ReinforcedCount--;
        if (to == TileKind.ReinforcedSupport) ReinforcedCount++;
    }

    public void Set(int x, int y, TileKind k)
    {
        if (!InBounds(x, y)) return;
        var i = Index(x, y);
        TrackKindChange(_tiles[i], k);
        _tiles[i] = k;
        _damage[i] = 0;
    }

    /// <summary>Set the WHOLE door leaf through tile (x, y) — every contiguous door tile
    /// above and below it — so a tall door opens and closes as ONE piece. The polar lattice
    /// drifts a column between rings (towers stamp each ring's door at the tile nearest
    /// their straight facade line), so the old same-angle world-space walk missed drifted
    /// tiles and stranded parts of the leaf closed; this walks ring by ring, re-matching
    /// the door column with a one-tile slack each step.</summary>
    private static readonly int[] _doorSlack = { 0, 1, -1 };

    public void SetDoorRun(int x, int y, TileKind to)
    {
        static bool IsDoor(TileKind k) => k is TileKind.DoorClosed or TileKind.DoorOpen;
        if (!IsDoor(Get(x, y))) return;
        Set(x, y, to);
        for (var dir = -1; dir <= 1; dir += 2)
        {
            var (cx, cy) = (x, y);
            for (var step = 0; step < 8; step++)
            {
                var r = cx + dir;
                if (r < 1 || r >= Rings) break;
                var n = TilesAt(cx);
                var nn = TilesAt(r);
                var t0 = (int)(((cy % n + n) % n + 0.5f) / n * nn);
                var found = false;
                foreach (var d in _doorSlack)
                {
                    var t = ((t0 + d) % nn + nn) % nn;
                    if (!IsDoor(Get(r, t))) continue;
                    Set(r, t, to);
                    (cx, cy) = (r, t);
                    found = true;
                    break;
                }
                if (!found) break;
            }
        }
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

    /// <summary>A collapsing structure takes its engineered backdrop with it: when a tile
    /// above the crust line crumbles or detaches as rigid debris, any alloy/glass/masonry
    /// back-wall behind it clears too — otherwise a toppled skyscraper leaves its facade
    /// hanging in the sky as a ghost silhouette. Natural rock backdrop (mountain interiors,
    /// cave walls) is untouched, and so is everything at or below the crust.</summary>
    public void ClearStructureWall(int x, int y)
    {
        if (x <= SurfaceRing + 4 || !InBounds(x, y)) return;
        if (_wall[Index(x, y)] is TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick)
            _wall[Index(x, y)] = TileKind.Sky;
    }

    /// <summary>Apply <paramref name="power"/> mining damage to the tile at (x,y). Returns
    /// the broken tile kind on shatter, or null if it just took damage. Hardness ≥ 99 is the
    /// "anchor-class" gate (PlanetCore, Core, Support beams) — those won't take damage from a
    /// regular swing. The hammer / core drill bypass that gate via
    /// <paramref name="effectiveHardness"/>: pass a smaller value (e.g. 8) and the tile will
    /// take damage as if it had that hardness, while still being broken by repeated hits.</summary>
    /// <summary>Height of the tree whose trunk occupies tile (x,y), or 0 if none — used to
    /// scale trunk mining toughness by tree size. Cheap linear scan of the (small) tree list,
    /// only ever hit while actually chopping a trunk tile.</summary>
    public int TreeHeightAt(int x, int y)
    {
        foreach (var s in Trees)
        {
            if (x < s.GroundR + 1 || x > s.GroundR + s.Height) continue;
            var n = TilesAt(x);
            var col = (int)((s.Angle / MathHelper.TwoPi + 1f) % 1f * n);
            if (col == y) return s.Height;
        }
        return 0;
    }

    public TileKind? Mine(int x, int y, int power, int? effectiveHardness = null)
    {
        if (!InBounds(x, y)) return null;
        var i = Index(x, y);
        var k = _tiles[i];
        if (k == TileKind.Sky) return null;
        var hardness = effectiveHardness ?? Tiles.Hardness(k);
        // A trunk gets tougher the taller its tree — a towering old-growth alien is genuinely
        // hard to chop through, a sapling much less so. (Only when the caller didn't already
        // force a hardness.)
        if (k == TileKind.TreeTrunk && effectiveHardness is null)
            hardness += Math.Min(4, TreeHeightAt(x, y) / 6);
        if (hardness >= 99) return null;
        var dmg = _damage[i] + Math.Max(1, power * 32 / hardness);
        if (dmg >= 255)
        {
            TrackKindChange(k, TileKind.Sky);
            _tiles[i] = TileKind.Sky;
            _damage[i] = 0;
            return k;
        }
        _damage[i] = (byte)dmg;
        return null;
    }

    private List<(float ang, float halfWidth)>? _cityDistricts;

    /// <summary>City district bearings and angular half-widths, clustered from the doorway
    /// angles in <see cref="CitySpawns"/> (computed once, cached). Guard saucers use these to
    /// patrol the band over the city they defend instead of orbiting the whole planet. Empty
    /// on worlds with no city.</summary>
    public IReadOnlyList<(float ang, float halfWidth)> CityDistricts
    {
        get
        {
            if (_cityDistricts != null) return _cityDistricts;
            _cityDistricts = new List<(float, float)>();
            if (CitySpawns.Count == 0) return _cityDistricts;

            // Doorway bearings, sorted around the ring.
            var angs = new List<float>(CitySpawns.Count);
            foreach (var (x, y) in CitySpawns)
            {
                var rel = TileToWorld(x, y) - Center;
                var a = MathF.Atan2(rel.Y, rel.X);
                if (a < 0) a += MathHelper.TwoPi;
                angs.Add(a);
            }
            angs.Sort();

            // Split into clusters at any gap wider than a district's spacing; the districts
            // themselves are raised ≥0.95 rad apart (WorldGen.RaiseCity), so a 0.5 rad gap
            // never splits a single city yet always separates neighbouring ones.
            const float gap = 0.5f;
            var groups = new List<List<float>> { new() { angs[0] } };
            for (var i = 1; i < angs.Count; i++)
            {
                if (angs[i] - angs[i - 1] > gap) groups.Add(new List<float>());
                groups[^1].Add(angs[i]);
            }
            // Merge the first and last group if the city straddles the 0/2π seam.
            if (groups.Count > 1 && MathHelper.TwoPi - angs[^1] + angs[0] <= gap)
            {
                groups[0].AddRange(groups[^1]);
                groups.RemoveAt(groups.Count - 1);
            }

            foreach (var g in groups)
            {
                // Vector mean for a wrap-safe centre, then the widest offset as the half-width.
                var sum = Vector2.Zero;
                foreach (var a in g) sum += new Vector2(MathF.Cos(a), MathF.Sin(a));
                var centre = MathF.Atan2(sum.Y, sum.X);
                if (centre < 0) centre += MathHelper.TwoPi;
                var half = 0f;
                foreach (var a in g)
                {
                    var d = MathF.Abs(a - centre);
                    if (d > MathHelper.Pi) d = MathHelper.TwoPi - d;
                    if (d > half) half = d;
                }
                _cityDistricts.Add((centre, half));
            }
            return _cityDistricts;
        }
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
        var w = (uint)y < (uint)nOut ? y : ((y % nOut) + nOut) % nOut;
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
        var w = (uint)y < (uint)nIn ? y : ((y % nIn) + nIn) % nIn;
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
        var w = (uint)y < (uint)nIn ? y : ((y % nIn) + nIn) % nIn;
        if (nIn == nOut) return (x + 1, w);
        var first = (int)Math.Floor((double)w * nOut / nIn);
        return (x + 1, (first + which) % nOut);
    }

    /// <summary>The 2×2 tile block (one legacy 8-px tile's worth of 4-px tiles) around
    /// (x,y), grown toward <paramref name="towards"/>: the angular partner is picked by the
    /// tangential side of the point, the radial pair by its radial side. Used so one pick
    /// swing / one placed block still covers the old full-size tile footprint. Tiles are
    /// deduped (outer band boundaries can map two inner tiles onto one).</summary>
    public IEnumerable<(int x, int y)> Footprint2x2(int x, int y, Vector2 towards)
    {
        if (!InBounds(x, y)) yield break;
        var centre = TileToWorld(x, y);
        var up = UpAt(centre);
        var right = new Vector2(-up.Y, up.X);
        var d = towards - centre;
        var sy = Vector2.Dot(d, right) >= 0f ? 1 : -1;   // angular partner side
        var outward = Vector2.Dot(d, up) >= 0f;          // radial partner side

        var n = TilesAt(x);
        var w = ((y % n) + n) % n;
        yield return (x, w);
        var w2 = ((w + sy) % n + n) % n;
        yield return (x, w2);

        (int rx, int ry) a, b;
        if (outward) { a = OuterNeighbour(x, w); b = OuterNeighbour(x, w2); }
        else         { a = InnerNeighbour(x, w); b = InnerNeighbour(x, w2); }
        if (a.rx >= 0 && a.rx < Rings) yield return a;
        if ((b.rx, b.ry) != (a.rx, a.ry) && b.rx >= 0 && b.rx < Rings) yield return b;
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
        w.Write(CitySpawns.Count);
        foreach (var (x, y) in CitySpawns) { w.Write(x); w.Write(y); }
        w.Write(LizardDens.Count);
        foreach (var (x, y) in LizardDens) { w.Write(x); w.Write(y); }
        w.Write(_composition.Count);
        foreach (var (idx, comp) in _composition)
        {
            w.Write(idx);
            w.Write(comp.Tint.R); w.Write(comp.Tint.G); w.Write(comp.Tint.B);
            w.Write((byte)comp.Parts.Length);
            foreach (var (mat, src, count) in comp.Parts)
            {
                w.Write(mat); w.Write(src); w.Write(count);
            }
        }
        // Gem overlays, appended after the original layout so old saves stay readable
        // (ReadState treats a missing section as "no gems").
        var gems = new byte[_totalTiles];
        for (var i = 0; i < _totalTiles; i++) gems[i] = (byte)_gem[i];
        w.Write(gems);
        // Surface profile (lumpy-world terrain line) — count-prefixed; 0 = flat world.
        w.Write(SurfaceProfile?.Length ?? 0);
        if (SurfaceProfile is not null)
            foreach (var s in SurfaceProfile) w.Write(s);
        // Skyscraper facade frames — the renderer's straight-tower lattice (RunSave v22).
        w.Write(CityFacades.Count);
        foreach (var (fa, fw, fr, ft) in CityFacades)
        {
            w.Write(fa); w.Write(fw); w.Write(fr); w.Write(ft);
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
        ReinforcedCount = 0;
        for (var i = 0; i < _totalTiles; i++)
        {
            _tiles[i] = (TileKind)tiles[i];
            if (_tiles[i] == TileKind.ReinforcedSupport) ReinforcedCount++;
            _damage[i] = damage[i];
            _wall[i] = (TileKind)walls[i];
        }
        VolcanoVents.Clear();
        var vents = r.ReadInt32();
        for (var i = 0; i < vents; i++)
            VolcanoVents.Add((r.ReadInt32(), r.ReadInt32(), r.ReadBoolean()));
        CitySpawns.Clear();
        var citySpawns = r.ReadInt32();
        for (var i = 0; i < citySpawns; i++)
            CitySpawns.Add((r.ReadInt32(), r.ReadInt32()));
        LizardDens.Clear();
        var dens = r.ReadInt32();
        for (var i = 0; i < dens; i++)
            LizardDens.Add((r.ReadInt32(), r.ReadInt32()));
        _composition.Clear();
        var comps = r.ReadInt32();
        for (var i = 0; i < comps; i++)
        {
            var idx = r.ReadInt32();
            var tint = new Color(r.ReadByte(), r.ReadByte(), r.ReadByte());
            var parts = new (byte mat, byte src, byte count)[r.ReadByte()];
            for (var p = 0; p < parts.Length; p++)
                parts[p] = (r.ReadByte(), r.ReadByte(), r.ReadByte());
            _composition[idx] = new TileComposition(tint, parts);
        }
        // Gem overlays — section absent in pre-gem saves, so a short read means "none"
        // (those saves still carry gem TILES, which keep working via the legacy paths).
        Array.Clear(_gem, 0, _totalTiles);
        var gemBytes = r.ReadBytes(_totalTiles);
        if (gemBytes.Length == _totalTiles)
            for (var i = 0; i < _totalTiles; i++) _gem[i] = (TileKind)gemBytes[i];
        // Surface profile — the count guards flat worlds (0) and the RunSave version bump
        // guards saves from before the section existed.
        var profLen = r.ReadInt32();
        SurfaceProfile = profLen > 0 ? new float[profLen] : null;
        for (var i = 0; i < profLen; i++) SurfaceProfile![i] = r.ReadSingle();
        // Skyscraper facade frames (RunSave v22).
        CityFacades.Clear();
        var facades = r.ReadInt32();
        for (var i = 0; i < facades; i++)
            CityFacades.Add((r.ReadSingle(), r.ReadSingle(), r.ReadInt32(), r.ReadInt32()));
    }
}

/// <summary>A living tree the ecosystem tracks: where it roots, its species/size, and its
/// regrowth state. A felled tree keeps its underground roots, and TreeEcology grows the
/// trunk back over time — faster the more the biome's rain (or standing water/acid/fire)
/// waters the roots. Mutable class so the regrow tick can update it in place.</summary>
public sealed class TreeSite
{
    public float Angle;             // bearing the trunk rises on
    public int GroundR;             // ring of the ground tile; the trunk starts at GroundR + 1
    public byte Species;            // shape family: 0 spire, 1 broad, 2 umbrella, 3 weeping
    public byte Height;             // full trunk height in tiles
    public TileKind Canopy = TileKind.TreeCanopy;
    public float Growth = 1f;       // 0..1 regrow progress (1 = fully grown and standing)
    public float Moisture;          // recent watering, decays over time; speeds regrowth
    public bool Standing = true;    // trunk currently present (cleared when the base is felled)
}
