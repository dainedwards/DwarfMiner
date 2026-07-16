using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `--lakeprobe`): builds a full session world and audits the WATER
/// side of the fluid-containment contract the lava/acid bodies already have — the basins must
/// not be undercut by anything a carver opened. For each surface lake it reports the drain
/// mouths at load (a basin tile whose sideways/inward neighbour is open air outside the body)
/// and, after a settle sim, how much of the pour is still in its basin. A lake that loses its
/// fill has a hole; the map dump around the highest escape names the carver that made it.
/// </summary>
public static class LakeProbe
{
    public static void Run()
    {
        var def = PlanetDefs.ById("debug");
        var run = DwarfMinerGame.BuildSessionWorld(def);
        var planet = run.Planet;
        var cells = run.Cells;

        Console.WriteLine($"--- debug: rings {planet.Rings}, surfaceRing {planet.SurfaceRing}, " +
                          $"radius {planet.Radius}t, WaterSeeds {planet.WaterSeeds.Count}, " +
                          $"LavaSeeds {planet.LavaSeeds.Count}, AcidSeeds {planet.AcidSeeds.Count}, " +
                          $"OilSeeds {planet.OilSeeds.Count}, keepOut {planet.FluidKeepOut.Count}");

        // Only the SURFACE basins matter here (the crust reservoirs are flooded caves by
        // design): anything within a few rings of the surface ring.
        var (seams, _, _) = WorldGen.CaveStrata(planet, def);
        Console.WriteLine($"    crust: RingMin {Planet.RingMin}, surface radius " +
                          $"{Planet.RingMin + planet.SurfaceRing}t, {planet.SurfaceRing} rings of it");
        foreach (var (lo, hi) in seams) Console.WriteLine($"      strata seam {lo:0.0}-{hi:0.0}t");

        var lake = new HashSet<(int r, int t)>(planet.LakeBasinSeeds);
        var loR = int.MaxValue; var hiR = 0;
        foreach (var (r, _) in lake) { loR = Math.Min(loR, r); hiR = Math.Max(hiR, r); }
        Console.WriteLine($"    surface-basin water seeds: {lake.Count} (rings {loR}-{hiR})");

        // Drain mouths: a basin tile with open air sideways or inward that is NOT itself part
        // of the pour. (Outward/up is the lake's own surface — water never climbs.)
        var mouths = new List<string>();
        var mouthCount = 0;
        foreach (var (r, t) in lake)
        {
            var n = planet.TilesAt(r);
            foreach (var (dr, dt) in new[] { (-1, 0), (0, -1), (0, 1) })
            {
                var r2 = r + dr;
                if (r2 < 0 || r2 >= planet.Rings) continue;
                var n2 = planet.TilesAt(r2);
                var t2 = (((int)((t + 0.5f) / n * n2) + dt) % n2 + n2) % n2;
                if (planet.Get(r2, t2) != TileKind.Sky || lake.Contains((r2, t2))) continue;
                // Open ATMOSPHERE beside a basin tile is the lake's own shoreline, not a
                // drain — the bowl's top courses sit at ground level by construction. Only
                // air that is genuinely underground is a hole the pour can escape down.
                if (Planet.RingMin + r2 >= planet.SurfaceRadiusAt(planet.TileToWorld(r2, t2)) - 1f)
                    continue;
                // Air BESIDE the fill only drains if it keeps going down: the bowl's own
                // freeboard/rim courses sit on the water itself, and water doesn't climb.
                if (dr == 0 && !LeadsDown(planet, lake, r2, t2)) continue;
                mouthCount++;
                if (mouths.Count < 12)
                    mouths.Add($"drain mouth: water({r},{t}) bearing " +
                               $"{MathHelper.WrapAngle((t + 0.5f) / n * MathF.Tau):0.000} " +
                               $"-> Sky({r2},{t2}) dr={dr} dt={dt}");
            }
        }
        Console.WriteLine($"    drain mouths at load: {mouthCount}");
        foreach (var s in mouths) Console.WriteLine("    " + s);

        // The basins' BEARINGS, bucketed — the crust reservoirs pour at load too, and their
        // water legitimately spreads across its own cave floor, so a whole-world water census
        // can't tell a lake draining from a reservoir settling. Water that appears under a
        // lake's bearing, below the bowl, is unambiguous: nothing else put it there.
        const int buckets = 1440;
        var basinBearing = new HashSet<int>();
        foreach (var (r, t) in lake)
        {
            var b = (int)((t + 0.5f) / planet.TilesAt(r) * buckets);
            for (var d = -2; d <= 2; d++) basinBearing.Add(((b + d) % buckets + buckets) % buckets);
        }

        var initial = ScanWater(planet, cells);
        Console.WriteLine($"    water tiles at load: {initial.Count} " +
                          $"({initial.Count - lake.Count} of them crust reservoirs)");

        const float step = 1f / 60f;
        for (var tick = 0; tick < 60 * 60; tick++) cells.Update(step);

        var after = ScanWater(planet, cells);
        var retained = 0;
        foreach (var k in lake) if (after.Contains(k)) retained++;
        var escaped = new List<(int r, int t)>();
        foreach (var (r, t) in after)
        {
            if (r >= loR - 2) continue;                     // still at basin level or above
            if (!basinBearing.Contains((int)((t + 0.5f) / planet.TilesAt(r) * buckets))) continue;
            if (initial.Contains((r, t))) continue;         // a reservoir that was always there
            escaped.Add((r, t));
        }
        Console.WriteLine($"    after 60s: basin fill retained {retained}/{lake.Count}, " +
                          $"water under a basin bearing below ring {loR - 2}: {escaped.Count}");

        // The highest escape sits nearest the breach it poured through.
        if (escaped.Count > 0)
        {
            var (hr, ht) = (-1, -1);
            foreach (var (r, t) in escaped) if (r > hr) { hr = r; ht = t; }
            var hn = planet.TilesAt(hr);
            Console.WriteLine($"    map around top escape ({hr},{ht}) bearing " +
                              $"{MathHelper.WrapAngle((ht + 0.5f) / hn * MathF.Tau):0.000} " +
                              "(W = basin at load, A = water now):");
            for (var r = Math.Min(planet.Rings - 1, hr + 10); r >= Math.Max(0, hr - 6); r--)
            {
                var n2 = planet.TilesAt(r);
                var tc = (int)((ht + 0.5f) / hn * n2);
                var line = $"      r{r,3}: ";
                for (var dt = -14; dt <= 14; dt++)
                {
                    var t2 = ((tc + dt) % n2 + n2) % n2;
                    var k = planet.Get(r, t2);
                    line += initial.Contains((r, t2)) ? 'W'
                        : after.Contains((r, t2)) ? 'A'
                        : k == TileKind.Sky ? '.'
                        : k == TileKind.LavaRock ? 'R'
                        : k == TileKind.Obsidian ? 'O'
                        : k == TileKind.Basalt ? 'B'
                        : k == TileKind.Stone ? 's'
                        : k == TileKind.Dirt ? 'd'
                        : k == TileKind.Granite ? 'g'
                        : k == TileKind.Gravel ? 'v'
                        : k == TileKind.MossStone ? 'm'
                        : k == TileKind.Glowshroom ? 'G'
                        : '?';
                }
                Console.WriteLine(line);
            }
        }
        // Cross-section through the deepest basin: the bowl should be a bowl — a continuous
        // floor of rock under a body of water, not an outline whose bottom courses run into
        // a slab or off the innermost ring.
        {
            var (dr, dt) = (int.MaxValue, 0);
            foreach (var (r, t) in lake) if (r < dr) { dr = r; dt = t; }
            var dn = planet.TilesAt(dr);
            Console.WriteLine($"    cross-section of the deepest basin (bearing " +
                              $"{MathHelper.WrapAngle((dt + 0.5f) / dn * MathF.Tau):0.000}), " +
                              "w = fill, . = air, letters = rock:");
            for (var r = Math.Min(planet.Rings - 1, hiR + 3); r >= Math.Max(0, dr - 8); r--)
            {
                var n2 = planet.TilesAt(r);
                var tc = (int)((dt + 0.5f) / dn * n2);
                var line = $"      r{r,3}: ";
                for (var d = -34; d <= 34; d++)
                {
                    var t2 = ((tc + d) % n2 + n2) % n2;
                    var k = planet.Get(r, t2);
                    line += after.Contains((r, t2)) ? 'w'
                        : k == TileKind.Sky ? '.'
                        : k == TileKind.Obsidian ? 'O'
                        : k == TileKind.LavaRock ? 'R'
                        : k == TileKind.Dirt ? 'd'
                        : k == TileKind.Stone ? 's'
                        : k == TileKind.Granite ? 'g'
                        : k == TileKind.Gravel ? 'v'
                        : k == TileKind.Basalt ? 'B'
                        : k == TileKind.Grass ? 'G'
                        : k == TileKind.Snow ? 'S'
                        : '?';
                }
                Console.WriteLine(line);
            }
        }

        Console.WriteLine(mouthCount == 0 && escaped.Count == 0 && retained >= lake.Count * 0.9
            ? "    OK: every basin sealed and still holding its fill"
            : "    FAIL: basins leak");
    }

    /// <summary>True if the air tile at (r,t) has more air under it that isn't the lake —
    /// i.e. it is the mouth of a shaft rather than a course of the bowl's own rim.</summary>
    private static bool LeadsDown(Planet planet, HashSet<(int r, int t)> lake, int r, int t)
    {
        var r2 = r - 1;
        if (r2 < 0) return false;
        var n2 = planet.TilesAt(r2);
        var t2 = (int)((t + 0.5f) / planet.TilesAt(r) * n2) % n2;
        return planet.Get(r2, t2) == TileKind.Sky && !lake.Contains((r2, t2));
    }

    private static HashSet<(int r, int t)> ScanWater(Planet planet, Cells cells)
    {
        var set = new HashSet<(int r, int t)>();
        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
                for (var dy = 0; dy < Cells.Density && !set.Contains((r, t)); dy++)
                    for (var dx = 0; dx < Cells.Density; dx++)
                        if (cells.Get(t * Cells.Density + dx, r * Cells.Density + dy) == Material.Water)
                        {
                            set.Add((r, t));
                            break;
                        }
        }
        return set;
    }
}
