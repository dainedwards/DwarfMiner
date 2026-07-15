using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>What a biome's rain is made of — and so what waters (or, on the harsh worlds,
/// what a tree has adapted to drink): plain water on temperate worlds, thin acid on acid
/// worlds, ember-rain on the burning ones.</summary>
public enum RainKind { Water, Acid, Fire, Snow }

/// <summary>The tree half of the living ecosystem. It owns the shape code that turns a
/// <see cref="TreeSite"/> into tiles — shared by world gen (plant a full tree) and regrowth
/// (grow the trunk back a ring at a time) so a regrown tree matches the felled one — and the
/// per-frame regrow tick: a felled tree with living roots grows back, slowly when parched and
/// several times faster when its roots are watered by the biome's rain or a nearby pool.</summary>
public static class TreeEcology
{
    /// <summary>How deep a tree's roots reach below its ground tile. Grub every one of them
    /// out (dig the TreeRoot tiles) and the tree is gone for good — nothing left to regrow.</summary>
    public const int MaxRootDepth = 4;

    public static RainKind RainFor(PlanetDef def) => def.Biome switch
    {
        "acid"            => RainKind.Acid,
        "ember" or "slag" => RainKind.Fire,
        "frost"           => RainKind.Snow,   // ice worlds: the clouds shed SNOW, not rain
        _                 => RainKind.Water,
    };

    private static int ColAt(Planet p, int r, float ang)
    {
        var n = p.TilesAt(r);
        return (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
    }

    private static int WrapCol(Planet p, int r, float ang, int dt)
    {
        var n = p.TilesAt(r);
        return ((ColAt(p, r, ang) + dt) % n + n) % n;
    }

    // ---- planting ------------------------------------------------------------------------

    /// <summary>Grow a fresh full tree: roots down into the soil, trunk up, canopy on top.</summary>
    public static void Plant(Planet p, TreeSite s)
    {
        PlantRoots(p, s);
        BuildTrunk(p, s, s.Height, canopy: true);
    }

    /// <summary>Thread roots down through the soil under the trunk. These persist through a
    /// felling and are what regrowth reads to know the tree can come back.</summary>
    public static void PlantRoots(Planet p, TreeSite s)
    {
        // Central taproot: a vertical run straight down the trunk column. This is the root
        // signature RootsAlive and RebuildSites key off (a run of ≥2 stacked roots).
        for (var d = 1; d <= MaxRootDepth; d++)
        {
            var rr = s.GroundR - d;
            if (rr < 1) break;
            TrySetRoot(p, rr, ColAt(p, rr, s.Angle));
        }
        // Spreading lateral roots: a shallow flare reaching OUT to either side (single tiles
        // at depth 2, thinning toward the tips) so the root system spreads sideways instead of
        // just plunging — you grub it out over a wider patch of ground.
        var lr = s.GroundR - 2;
        if (lr >= 1)
            for (var side = -1; side <= 1; side += 2)
                for (var lat = 1; lat <= 3; lat++)
                {
                    if (Random.Shared.Next(lat + 1) == 0) continue;   // sparser toward the tips
                    var n = p.TilesAt(lr);
                    var col = ((ColAt(p, lr, s.Angle) + side * lat) % n + n) % n;
                    TrySetRoot(p, lr, col);
                }
    }

    private static void TrySetRoot(Planet p, int rr, int col)
    {
        if (p.Get(rr, col) is TileKind.Dirt or TileKind.Grass or TileKind.Snow
            or TileKind.MossStone or TileKind.Gravel or TileKind.Basalt)
            p.Set(rr, col, TileKind.TreeRoot);
    }

    /// <summary>Build the trunk up to <paramref name="tiles"/> tiles tall (only over open sky,
    /// so it never grows through rock the player built or dug), optionally capping it with the
    /// species' canopy once it has reached full height.</summary>
    private static void BuildTrunk(Planet p, TreeSite s, int tiles, bool canopy)
    {
        for (var h = 1; h <= tiles; h++)
        {
            var rr = s.GroundR + h;
            if (rr >= p.Rings - 1) break;
            var col = ColAt(p, rr, s.Angle);
            if (p.Get(rr, col) == TileKind.Sky) p.Set(rr, col, TileKind.TreeTrunk);
        }
        if (!canopy) return;
        foreach (var (dr, dt) in CanopyOffsets(s.Species))
        {
            var rr = s.GroundR + s.Height + dr;
            if (rr < 1 || rr >= p.Rings - 1) continue;
            var col = WrapCol(p, rr, s.Angle, dt);
            if (p.Get(rr, col) == TileKind.Sky) p.Set(rr, col, s.Canopy);
        }
    }

    /// <summary>Canopy tile offsets (dr rings above the trunk top, dt tiles sideways) per
    /// species — the four silhouettes the surface grows: a narrow spire plume, a fat round
    /// blob, a flat umbrella cap on a bare bole, and a weeping crown that drapes down.</summary>
    private static IEnumerable<(int dr, int dt)> CanopyOffsets(int species)
    {
        switch (species)
        {
            case 0: // spire — a narrow tall plume
                yield return (0, 0); yield return (0, -1); yield return (0, 1);
                yield return (1, 0); yield return (1, -1); yield return (1, 1);
                yield return (2, 0); yield return (2, -1); yield return (2, 1);
                yield return (3, 0); yield return (4, 0);
                break;
            case 2: // umbrella — a flat wide cap crowning a long bare trunk
                for (var dt = -3; dt <= 3; dt++) yield return (0, dt);
                for (var dt = -2; dt <= 2; dt++) yield return (1, dt);
                yield return (2, -1); yield return (2, 0); yield return (2, 1);
                break;
            case 3: // weeping — a round crown that drapes fronds down the sides
                for (var dt = -2; dt <= 2; dt++) yield return (0, dt);
                for (var dt = -2; dt <= 2; dt++) yield return (1, dt);
                yield return (2, -1); yield return (2, 0); yield return (2, 1);
                yield return (-1, -2); yield return (-1, 2);
                yield return (-2, -2); yield return (-2, 2);
                break;
            default: // broad — a fat round blob
                for (var dt = -1; dt <= 1; dt++) yield return (-1, dt);
                for (var dt = -2; dt <= 2; dt++) yield return (0, dt);
                for (var dt = -2; dt <= 2; dt++) yield return (1, dt);
                for (var dt = -1; dt <= 1; dt++) yield return (2, dt);
                break;
        }
    }

    /// <summary>Reconstruct the tree sites of a resumed world from the TreeRoot tiles saved in
    /// its grid (the sites list itself isn't serialized). Each root column's shallowest root
    /// marks one tree: the ground sits one ring out, and the standing trunk height is measured
    /// off the grid (a tree felled before the save has no trunk, so it's flagged to regrow).</summary>
    public static void RebuildSites(Session run)
    {
        var p = run.Planet;
        if (p is null || p.Trees.Count > 0) return;   // fresh worlds already carry their sites
        var lo = Math.Max(1, p.SurfaceRing - 34);
        var hi = Math.Min(p.Rings - 2, p.SurfaceRing + 4);
        for (var r = lo; r <= hi; r++)
        {
            var n = p.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                if (p.Get(r, t) != TileKind.TreeRoot) continue;
                // Seed a site only from a TAPROOT top: the tile one ring outward is soil (this
                // is the shallowest root) AND the tile one ring inward is another root (a
                // vertical taproot run below). This ignores the shallow lateral flare roots,
                // which are lone tiles, so each tree is rebuilt exactly once — never a phantom
                // site under a spreading side-root.
                var outN = p.TilesAt(r + 1);
                var outT = (int)((t + 0.5f) / n * outN);
                if (p.Get(r + 1, outT) == TileKind.TreeRoot) continue;   // not the shallowest
                var inN = p.TilesAt(r - 1);
                var inT = (int)((t + 0.5f) / n * inN);
                if (p.Get(r - 1, inT) != TileKind.TreeRoot) continue;    // lone lateral root — skip

                var groundR = r + 1;
                var ang = (t + 0.5f) / n * MathHelper.TwoPi;
                var h = 0;
                for (var hh = 1; hh < 30; hh++)
                {
                    var rr = groundR + hh;
                    if (rr >= p.Rings - 1) break;
                    if (p.Get(rr, ColAt(p, rr, ang)) != TileKind.TreeTrunk) break;
                    h = hh;
                }
                var standing = h > 0;
                var hash = (uint)(r * 92821 + t * 68917);
                p.Trees.Add(new TreeSite
                {
                    Angle = ang,
                    GroundR = groundR,
                    Species = (byte)(hash % 4),
                    Height = standing ? (byte)h : (byte)(6 + hash % 8),
                    Canopy = TileKind.TreeCanopy,
                    Standing = standing,
                    Growth = standing ? 1f : 0f,
                });
            }
        }
    }

    // ---- regrowth ------------------------------------------------------------------------

    private static bool RootsAlive(Planet p, TreeSite s)
    {
        for (var d = 1; d <= MaxRootDepth; d++)
        {
            var rr = s.GroundR - d;
            if (rr < 1) break;
            if (p.Get(rr, ColAt(p, rr, s.Angle)) == TileKind.TreeRoot) return true;
        }
        return false;
    }

    /// <summary>Rain (or a debug water pour) falling over an angular band feeds every tree
    /// rooted under it. Matching drink for the biome (the rain the trees evolved with, or plain
    /// water anywhere) fills the moisture reserve fastest; the wrong element still helps a
    /// little.</summary>
    public static void WaterBand(Session run, float angle, float halfWidth, RainKind kind, float amount)
    {
        var biomeRain = RainFor(run.Def);
        foreach (var s in run.Trees)
        {
            var da = MathF.Abs(MathHelper.WrapAngle(s.Angle - angle));
            if (da > halfWidth) continue;
            // Plain water quenches anything; the biome's own rain is the ideal drink; a
            // mismatched element (acid on a water tree, say) only trickles in.
            var gain = kind == RainKind.Water || kind == biomeRain ? amount : amount * 0.3f;
            s.Moisture = MathHelper.Clamp(s.Moisture + gain, 0f, 1f);
        }
    }

    public static void Update(float dt, Session run)
    {
        var p = run.Planet;
        if (p is null) return;
        // Dry-day baseline: wet worlds keep their trees damp enough to recover on their own;
        // parched worlds barely, so there a felled tree leans on rain and nearby pools.
        var baseline = run.Def.Biome is "ocean" or "verdant" ? 0.45f
                     : run.Def.HasWater ? 0.22f : 0.08f;
        for (var i = run.Trees.Count - 1; i >= 0; i--)
        {
            var s = run.Trees[i];
            s.Moisture = MathHelper.Lerp(s.Moisture, baseline, dt * 0.08f);
            if (s.Standing) continue;                                   // only felled trees regrow
            if (!RootsAlive(p, s)) { run.Trees.RemoveAt(i); continue; } // roots grubbed out → gone

            // A pool lapping at the roots keeps them fed even without rain (the "or water").
            SoakFromPools(run, s);

            // Slow when parched, up to ~6x faster when the roots are well watered.
            var rate = 0.018f * (0.4f + s.Moisture * 2.8f);
            var prev = (int)(s.Growth * s.Height);
            s.Growth = MathHelper.Clamp(s.Growth + rate * dt, 0f, 1f);
            var now = (int)(s.Growth * s.Height);
            if (now > prev) BuildTrunk(p, s, now, canopy: false);
            if (s.Growth >= 1f)
            {
                BuildTrunk(p, s, s.Height, canopy: true);
                s.Standing = true;
                s.Growth = 1f;
            }
        }
    }

    /// <summary>Bump a felled tree's moisture from standing cells lapping at its roots — water
    /// for anything, or the matching element (acid / fire) for a tree adapted to it.</summary>
    private static void SoakFromPools(Session run, TreeSite s)
    {
        if (run.Cells is null) return;
        var ground = SpawnDirector.FindSurfaceSpawn(run.Planet, s.Angle, run.Planet.Radius);
        var (_, acid, _, fire) = run.Cells.SampleHazardsNear(ground, 36f);
        var wet = CountWater(run, ground);
        var biomeRain = RainFor(run.Def);
        var feed = wet * 0.10f
                 + (biomeRain == RainKind.Acid ? acid * 0.08f : 0f)
                 + (biomeRain == RainKind.Fire ? fire * 0.08f : 0f);
        if (feed > 0f) s.Moisture = MathHelper.Clamp(s.Moisture + feed * 0.05f, 0f, 1f);
    }

    private static int CountWater(Session run, Vector2 worldPos)
    {
        var (cx, cy) = run.Cells.WorldToCell(worldPos);
        var n = 0;
        for (var dy = -3; dy <= 3; dy++)
            for (var dx = -3; dx <= 3; dx++)
                if (run.Cells.Get(cx + dx, cy + dy) == Material.Water) n++;
        return n;
    }
}
