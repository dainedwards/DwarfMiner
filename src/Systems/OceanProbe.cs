using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `dotnet run -- --oceanprobe`): generates ocean worlds from a few
/// campaign seeds and verifies the water-world contract — the properties that are hard to
/// eyeball but ruinous to get wrong:
///  1. THE SEA DOMINATES — most bearings carry surface water — but ISLANDS EXIST (dry
///     bearings survive in the mountains' shadows).
///  2. SEABED ARMOURED — walking inward from a deep basin's bottom, the first solid tile is
///     obsidian (the shell that keeps the sea out of the caves and the caves undrownable).
///  3. CAVES ARE DRY — no underground reservoir seeds on ocean worlds: every water seed
///     belongs to a surface basin (basin carve stamps a Dirt wall; cave seeds would carry
///     rock walls).
///  4. NO LEAK PATH — a gravity-style flood from every water seed (sideways/inward only,
///     the way settling water actually travels) must never reach meaningfully deeper than
///     the deepest basin bottom: if a worm or noise cave punctured a seabed, the flood
///     plunges into the network and this fails.
///  5. NETWORK EXISTS + INTERCONNECTED — the under-sea band holds real cave volume and its
///     largest air-connected component owns most of it (corridors, not isolated pockets).
///  6. GROTTO MOUTHS — on some dry bearing, atmosphere-connected air reaches well below
///     the surface: the island entrances into the network are genuinely open.
/// </summary>
public static class OceanProbe
{
    private const float S = Planet.LegacyTileScale;

    public static void Run()
    {
        var allOk = true;
        foreach (var seed in new[] { 21, 22, 23 })
        {
            PlanetDef? def = null;
            foreach (var d in PlanetGen.Campaign(seed))
                if (d.Biome == "ocean") { def = d; break; }
            if (def is null) { Console.WriteLine($"seed {seed}: no ocean world rolled?!"); allOk = false; continue; }

            var planet = WorldGen.Generate(seed * 7 + 1, def);
            Console.WriteLine($"--- {def.Name} (seed {seed}, LakeScale {def.LakeScale:0.0}, size {def.SizeScale:0.00}, radius {planet.Radius}t) ---");

            // Column census: per bearing, does the column hold surface water, and how deep
            // does its deepest water seed sit below the local surface (legacy tiles)?
            const int cols = 720;
            var colWet = new bool[cols];
            var colSeedDepth = new float[cols];
            foreach (var (r, t) in planet.WaterSeeds)
            {
                var pos = planet.TileToWorld(r, t);
                var rel = pos - planet.Center;
                var ang = MathF.Atan2(rel.Y, rel.X);
                if (ang < 0) ang += MathHelper.TwoPi;
                var c = (int)(ang / MathHelper.TwoPi * cols) % cols;
                colWet[c] = true;
                var depth = (planet.SurfaceRadiusAt(pos) - (Planet.RingMin + r + 0.5f)) / S;
                colSeedDepth[c] = MathF.Max(colSeedDepth[c], depth);
            }
            int wet = 0;
            foreach (var w in colWet) if (w) wet++;
            Report(ref allOk, wet >= cols * 0.55f, $"sea dominates ({100f * wet / cols:0}% of bearings wet)");
            Report(ref allOk, wet <= cols * 0.95f, $"islands exist ({cols - wet} dry bearings)");

            // 2. Obsidian under the deep basins: from each column's deepest seed, walk
            //    inward to the first solid tile — it should be the shell.
            int deepCols = 0, armoured = 0;
            for (var c = 0; c < cols; c++)
            {
                if (colSeedDepth[c] < 4f) continue;   // shore fringe stays diggable by design
                deepCols++;
                var ang = (c + 0.5f) / cols * MathHelper.TwoPi;
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                var surfRings = planet.SurfaceRadiusAt(planet.Center + dir);
                var startRing = (int)(surfRings - colSeedDepth[c] * S - Planet.RingMin);
                for (var r = Math.Min(startRing, planet.Rings - 1); r >= 0; r--)
                {
                    var n = planet.TilesAt(r);
                    var t = (int)(ang / MathHelper.TwoPi * n) % n;
                    var k = planet.Get(r, t);
                    // Air and vegetation (kelp beds root in the basins) aren't the floor.
                    if (k == TileKind.Sky || Tiles.IsFlora(k) || Tiles.IsPassable(k)) continue;
                    if (k == TileKind.Obsidian) armoured++;
                    break;
                }
            }
            // Not 100%: a mountain crag standing inside a basin is legitimate stone floor.
            Report(ref allOk, deepCols > 0 && armoured >= deepCols * 0.80f,
                $"seabed armoured (obsidian first under {armoured}/{deepCols} deep columns)");

            // 3. All water belongs to basins (Dirt-walled by the basin carve) — the cave
            //    reservoir channel must be off on ocean worlds.
            var caveSeeds = 0;
            foreach (var (r, t) in planet.WaterSeeds)
                if (planet.GetWall(r, t) != TileKind.Dirt) caveSeeds++;
            Report(ref allOk, caveSeeds == 0, $"caves dry (rock-walled water seeds: {caveSeeds})");

            // 4. Gravity flood from every seed: sideways / inward only. If it gets more
            //    than the shell-and-a-bite deeper than the deepest basin, the sea has a
            //    path into the network.
            var deepestSeed = 0f;
            foreach (var d in colSeedDepth) deepestSeed = MathF.Max(deepestSeed, d);
            var floodMax = GravityFloodMaxDepth(planet);
            Report(ref allOk, floodMax <= deepestSeed + 7f,
                $"no leak path (flood bottoms at {floodMax:0.0}t vs deepest basin {deepestSeed:0.0}t)");

            // 4b. The deepest basin floor must clear the lava flood line with room to
            //     spare — a sea bottom inside the fill band would be poured full of lava.
            var baseline = Planet.RingMin + planet.SurfaceRing;
            var deepestFloorRing = baseline - deepestSeed * S;
            var lavaTop = def.LavaFillFrac * planet.Radius;
            Report(ref allOk, deepestFloorRing > lavaTop + 8f,
                $"seas clear the lava fill (floor ring {deepestFloorRing:0} vs lava top {lavaTop:0})");

            // 5. The ocean worm band holds a real, interconnected network. The band is the
            //    worms' true bite envelope (steering floor 0.30 minus a little drift, up to
            //    the 14-legacy-tile ceiling) — measuring a narrower slice cuts corridors
            //    mid-crossing and undercounts the connectivity.
            var bandLo = planet.Radius * MathF.Max(0.30f, def.LavaFillFrac + 0.08f) - 6f;
            var bandHi = baseline - 13f * S;
            var (air, largest) = BandAirAndLargestComponent(planet, bandLo, bandHi);
            Report(ref allOk, air > 2000, $"network exists ({air} cave tiles in band {bandLo:0}-{bandHi:0}t)");
            Report(ref allOk, air > 0 && largest >= air * 0.4f,
                $"network interconnected (largest component {largest}/{air})");

            // 6. Grotto mouths: atmosphere-connected air on a solidly DRY bearing (±3
            //    columns, so a crag poking out of a sea can't fake it) reaching well below
            //    the surface — the sea basins are air at gen time, hence the exclusion.
            var dryReach = AtmosphereDryReach(planet, colWet, cols);
            Report(ref allOk, dryReach >= 10f,
                $"grotto mouths open (dry-land air reaches {dryReach:0.0}t below surface)");
        }
        Console.WriteLine(allOk ? "OCEAN PROBE: ALL OK" : "OCEAN PROBE: FAILURES ABOVE");
    }

    private static void Report(ref bool allOk, bool ok, string what)
    {
        if (!ok) allOk = false;
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
    }

    /// <summary>Flood from every water seed through air, stepping only sideways or inward
    /// (settling water never climbs); returns the deepest depth-below-local-surface reached,
    /// in legacy tiles.</summary>
    private static float GravityFloodMaxDepth(Planet planet)
    {
        var visited = new HashSet<long>();
        var frontier = new Queue<(int r, int t)>();
        long Key(int r, int t) => (long)r * 4_000_000L + (uint)t;
        foreach (var (r, t) in planet.WaterSeeds)
            if (visited.Add(Key(r, t)))
                frontier.Enqueue((r, t));

        var maxDepth = 0f;
        while (frontier.Count > 0)
        {
            var (r, t) = frontier.Dequeue();
            var depth = (planet.SurfaceRadiusAt(planet.TileToWorld(r, t))
                - (Planet.RingMin + r + 0.5f)) / S;
            maxDepth = MathF.Max(maxDepth, depth);

            var n = planet.TilesAt(r);
            Try(r, ((t - 1) % n + n) % n);
            Try(r, (t + 1) % n);
            var (ir, it) = planet.InnerNeighbour(r, t);
            Try(ir, it);

            void Try(int rr, int tt)
            {
                if (rr < 0 || rr >= planet.Rings) return;
                if (planet.Get(rr, tt) != TileKind.Sky) return;
                if (visited.Add(Key(rr, tt))) frontier.Enqueue((rr, tt));
            }
        }
        return maxDepth;
    }

    /// <summary>CAVE-tile count in a radial band plus the size of the largest air-connected
    /// component (both counting only rock-walled cave tiles — the sea basins in the band
    /// are Dirt-walled by the bowl carve and mustn't pollute the network numbers; the BFS
    /// still walks through everything so a corridor crossing a dirt seam doesn't split).</summary>
    private static (int air, int largest) BandAirAndLargestComponent(Planet planet,
        float loTiles, float hiTiles)
    {
        var visited = new HashSet<long>();
        long Key(int r, int t) => (long)r * 4_000_000L + (uint)t;
        bool InBand(int r) => Planet.RingMin + r + 0.5f >= loTiles && Planet.RingMin + r + 0.5f < hiTiles;
        bool IsCave(int r, int t) => planet.GetWall(r, t) != TileKind.Dirt;

        var caves = 0;
        var largest = 0;
        var r0 = Math.Max(0, (int)(loTiles - Planet.RingMin));
        var r1 = Math.Min(planet.Rings - 1, (int)(hiTiles - Planet.RingMin) + 1);
        for (var r = r0; r <= r1; r++)
        {
            if (!InBand(r)) continue;
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                if (planet.Get(r, t) != TileKind.Sky) continue;
                if (IsCave(r, t)) caves++;
                if (!visited.Add(Key(r, t))) continue;
                // BFS this component; size counts cave tiles only.
                var size = IsCave(r, t) ? 1 : 0;
                var frontier = new Queue<(int r, int t)>();
                frontier.Enqueue((r, t));
                while (frontier.Count > 0)
                {
                    var (cr, ct) = frontier.Dequeue();
                    var cn = planet.TilesAt(cr);
                    Try(cr, ((ct - 1) % cn + cn) % cn);
                    Try(cr, (ct + 1) % cn);
                    var (ir, it) = planet.InnerNeighbour(cr, ct);
                    Try(ir, it);
                    var oc = planet.OuterNeighbourCount(cr, ct);
                    for (var i = 0; i < oc; i++)
                    {
                        var (or2, ot2) = planet.OuterNeighbour(cr, ct, i);
                        Try(or2, ot2);
                    }

                    void Try(int rr, int tt)
                    {
                        if (rr < 0 || rr >= planet.Rings || !InBand(rr)) return;
                        if (planet.Get(rr, tt) != TileKind.Sky) return;
                        if (visited.Add(Key(rr, tt)))
                        {
                            if (IsCave(rr, tt)) size++;
                            frontier.Enqueue((rr, tt));
                        }
                    }
                }
                largest = Math.Max(largest, size);
            }
        }
        return (caves, largest);
    }

    /// <summary>BFS through air from the open atmosphere; returns the deepest
    /// depth-below-local-surface (legacy tiles) reached on a DRY bearing — the signature
    /// of an open island grotto (wet bearings are basins: air at gen time, excluded).</summary>
    private static float AtmosphereDryReach(Planet planet, bool[] colWet, int cols)
    {
        var visited = new HashSet<long>();
        var frontier = new Queue<(int r, int t)>();
        long Key(int r, int t) => (long)r * 4_000_000L + (uint)t;

        // Seed from a ring safely above every surface feature.
        var seedRing = planet.Rings - 2;
        for (var t = 0; t < planet.TilesAt(seedRing); t++)
            if (planet.Get(seedRing, t) == TileKind.Sky && visited.Add(Key(seedRing, t)))
                frontier.Enqueue((seedRing, t));

        var reach = 0f;
        while (frontier.Count > 0)
        {
            var (r, t) = frontier.Dequeue();
            var pos = planet.TileToWorld(r, t);
            var rel = pos - planet.Center;
            var ang = MathF.Atan2(rel.Y, rel.X);
            if (ang < 0) ang += MathHelper.TwoPi;
            var c = (int)(ang / MathHelper.TwoPi * cols) % cols;
            var dry = true;
            for (var w = -3; w <= 3 && dry; w++)
                if (colWet[((c + w) % cols + cols) % cols]) dry = false;
            if (dry)
            {
                var depth = (planet.SurfaceRadiusAt(pos) - (Planet.RingMin + r + 0.5f)) / S;
                reach = MathF.Max(reach, depth);
            }

            var n = planet.TilesAt(r);
            Try(r, ((t - 1) % n + n) % n);
            Try(r, (t + 1) % n);
            var (ir, it) = planet.InnerNeighbour(r, t);
            Try(ir, it);
            var oc = planet.OuterNeighbourCount(r, t);
            for (var i = 0; i < oc; i++)
            {
                var (or2, ot2) = planet.OuterNeighbour(r, t, i);
                Try(or2, ot2);
            }

            void Try(int rr, int tt)
            {
                if (rr < 0 || rr >= planet.Rings) return;
                if (planet.Get(rr, tt) != TileKind.Sky) return;
                if (visited.Add(Key(rr, tt))) frontier.Enqueue((rr, tt));
            }
        }
        return reach;
    }
}
