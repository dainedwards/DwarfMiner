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
        var byRing = new SortedDictionary<int, int>();
        foreach (var (r, _) in planet.WaterSeeds) byRing[r] = byRing.TryGetValue(r, out var c) ? c + 1 : 1;
        Console.Write("    water seeds by ring:");
        foreach (var (r, c) in byRing) Console.Write($" {r}:{c}");
        Console.WriteLine();

        var lake = new HashSet<(int r, int t)>();
        foreach (var (r, t) in planet.WaterSeeds)
            if (r > planet.SurfaceRing - 30) lake.Add((r, t));
        Console.WriteLine($"    surface-basin water seeds: {lake.Count}");

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
                mouthCount++;
                if (mouths.Count < 12)
                    mouths.Add($"drain mouth: water({r},{t}) bearing " +
                               $"{MathHelper.WrapAngle((t + 0.5f) / n * MathF.Tau):0.000} " +
                               $"-> Sky({r2},{t2}) dr={dr} dt={dt}");
            }
        }
        Console.WriteLine($"    drain mouths at load: {mouthCount}");
        foreach (var s in mouths) Console.WriteLine("    " + s);

        var initial = ScanWater(planet, cells);
        Console.WriteLine($"    water tiles at load: {initial.Count}");

        const float step = 1f / 60f;
        for (var tick = 0; tick < 60 * 60; tick++) cells.Update(step);

        var after = ScanWater(planet, cells);
        var escaped = new List<(int r, int t)>();
        foreach (var (r, t) in after)
        {
            var near = false;
            for (var dr = -2; dr <= 2 && !near; dr++)
                for (var dt = -2; dt <= 2 && !near; dt++)
                {
                    var r2 = r + dr;
                    if (r2 < 0 || r2 >= planet.Rings) continue;
                    var n2 = planet.TilesAt(r2);
                    var t2 = (int)((t + 0.5f) / planet.TilesAt(r) * n2) + dt;
                    near = initial.Contains((r2, (t2 % n2 + n2) % n2));
                }
            if (!near) escaped.Add((r, t));
        }
        Console.WriteLine($"    after 60s: water tiles {after.Count} (was {initial.Count}), " +
                          $"escaped beyond body+2: {escaped.Count}");

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
        Console.WriteLine(mouthCount == 0 && escaped.Count == 0
            ? "    OK: every basin sealed" : "    FAIL: basins leak");
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
