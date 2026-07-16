using System;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `--lavaprobe`): builds a full session world (the same
/// BuildSessionWorld path the game uses, shell pass included) and audits every lava body's
/// jacket — for each tile that holds lava, every solid tile within the 2-tile shell reach
/// must be LavaRock or one of the deliberately-spared kinds (obsidian seals, ores/gems,
/// architecture, flora, anchors). Any "naked" convertible tile is a shell hole; the probe
/// prints where it is and which direction from the lava (below/beside/above), which is
/// exactly the report needed to chase "the bottom of the tubes isn't jacketed".
/// </summary>
public static class LavaProbe
{
    public static void Run()
    {
        foreach (var id in new[] { "debug", "ember" })
        {
            var def = PlanetDefs.ById(id);
            var run = DwarfMinerGame.BuildSessionWorld(def);
            var planet = run.Planet;
            var cells = run.Cells;

            var lavaTiles = 0;
            var rockTiles = 0;
            var naked = 0;
            var nakedBelow = 0;
            var samples = new System.Collections.Generic.List<string>();

            bool IsLavaTile(int r, int t)
            {
                if (r < 0 || r >= planet.Rings) return false;
                var n = planet.TilesAt(r);
                t = (t % n + n) % n;
                if (planet.Get(r, t) != TileKind.Sky) return false;
                return cells.Get(t * Cells.Density + Cells.Density / 2,
                    r * Cells.Density + Cells.Density / 2) == Material.Lava;
            }

            for (var r = 0; r < planet.Rings; r++)
            {
                var n = planet.TilesAt(r);
                for (var t = 0; t < n; t++)
                {
                    if (planet.Get(r, t) == TileKind.LavaRock) rockTiles++;
                    if (!IsLavaTile(r, t)) continue;
                    lavaTiles++;
                    for (var dr = -2; dr <= 2; dr++)
                    {
                        var r2 = r + dr;
                        if (r2 < 0 || r2 >= planet.Rings) continue;
                        var n2 = planet.TilesAt(r2);
                        var t2c = (int)((t + 0.5f) / n * n2);
                        for (var dt = -2; dt <= 2; dt++)
                        {
                            var t2 = ((t2c + dt) % n2 + n2) % n2;
                            var k = planet.Get(r2, t2);
                            // The kinds the shell pass converts — if one still borders
                            // lava after the pass, the jacket has a hole there.
                            if (k is TileKind.Dirt or TileKind.Grass or TileKind.Stone
                                or TileKind.Gravel or TileKind.MossStone or TileKind.Granite
                                or TileKind.Basalt or TileKind.Snow or TileKind.Conglomerate)
                            {
                                naked++;
                                if (dr < 0) nakedBelow++;
                                if (samples.Count < 12)
                                    samples.Add($"lava({r},{t}) -> {k}({r2},{t2}) dr={dr} dt={dt}");
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"--- {id}: lavaTiles {lavaTiles}, lavaRockTiles {rockTiles}, " +
                              $"naked convertibles in shell reach {naked} (below: {nakedBelow})");
            foreach (var s in samples) Console.WriteLine("    " + s);
            Console.WriteLine(naked == 0 ? "    OK: every lava body fully jacketed"
                                         : "    FAIL: shell has holes");

            if (id != "debug") continue;

            // RUNTIME LEAK HUNT: snapshot every tile that holds any lava cell, run the
            // sim untrottled (headless SimFocus off) for two minutes, then report lava
            // found in tiles OUTSIDE the initial body — where it got to and what the
            // neighbourhood looks like, which localises the breach.
            var initial = new System.Collections.Generic.HashSet<(int r, int t)>();
            void ScanLava(System.Collections.Generic.HashSet<(int r, int t)> set)
            {
                for (var r = 0; r < planet.Rings; r++)
                {
                    var n = planet.TilesAt(r);
                    for (var t = 0; t < n; t++)
                        for (var dy = 0; dy < Cells.Density && !set.Contains((r, t)); dy++)
                            for (var dx = 0; dx < Cells.Density; dx++)
                                if (cells.Get(t * Cells.Density + dx, r * Cells.Density + dy)
                                    == Material.Lava)
                                {
                                    set.Add((r, t));
                                    break;
                                }
                }
            }
            ScanLava(initial);

            // Breach census: initial lava tiles whose INWARD or SIDEWAYS tile neighbour is
            // open Sky outside the body — an open drain mouth (the pool's upper surface is
            // Sky too, but that sits ABOVE, dr=+1, and is excluded).
            // Geometry dump: where is everything?
            var seedSet = new System.Collections.Generic.HashSet<(int, int)>(planet.LavaSeeds);
            var loSeed = int.MaxValue; var hiSeed = 0;
            foreach (var (sr, _) in planet.LavaSeeds)
            { loSeed = Math.Min(loSeed, sr); hiSeed = Math.Max(hiSeed, sr); }
            Console.WriteLine($"    surfaceRing {planet.SurfaceRing}, rings {planet.Rings}, " +
                              $"radius {planet.Radius}t, LavaSeeds rings {loSeed}-{hiSeed}");
            foreach (var (vx, vy, vAcid) in planet.VolcanoVents)
            {
                var vRel = planet.TileToWorld(vx, vy) - planet.Center;
                Console.WriteLine($"    vent ({vx},{vy}) acid={vAcid} " +
                                  $"bearing {MathF.Atan2(vRel.Y, vRel.X):0.000}");
            }

            var mouths = new System.Collections.Generic.List<string>();
            var mouthCount = 0;
            foreach (var (r, t) in initial)
            {
                var n = planet.TilesAt(r);
                foreach (var (dr, dtt) in new[] { (-1, 0), (0, -1), (0, 1) })
                {
                    var r2 = r + dr;
                    if (r2 < 0 || r2 >= planet.Rings) continue;
                    var n2 = planet.TilesAt(r2);
                    var t2 = ((int)((t + 0.5f) / n * n2) + dtt % n2 + n2) % n2;
                    if (planet.Get(r2, t2) != TileKind.Sky || initial.Contains((r2, t2))) continue;
                    mouthCount++;
                    if (mouths.Count < 12)
                    {
                        var bearing = Microsoft.Xna.Framework.MathHelper.WrapAngle(
                            (t + 0.5f) / n * MathF.Tau);
                        mouths.Add($"drain mouth: lava({r},{t}) bearing {bearing:0.000} " +
                                   $"seed={seedSet.Contains((r, t))} -> Sky({r2},{t2}) dr={dr} dt={dtt}");
                    }
                }
            }
            Console.WriteLine($"    drain mouths at load: {mouthCount}");
            foreach (var s in mouths) Console.WriteLine("    " + s);

            // Anatomy dump around the FIRST drain mouth: seam bands + a tile-kind map of the
            // neighbourhood, so a breach localises to "which carver bit which barrier" without
            // another bespoke probe run.
            if (mouthCount > 0)
            {
                var (seams, bands, seaFloor) = WorldGen.CaveStrata(planet, def);
                Console.WriteLine($"    strata: seaFloor {seaFloor:0.0}t, LavaFillFrac {def.LavaFillFrac:0.00} " +
                    $"(top {planet.Radius * def.LavaFillFrac:0.0}t), RingMin {Planet.RingMin}");
                foreach (var (lo, hi) in seams) Console.WriteLine($"      seam {lo:0.0}-{hi:0.0}t");
                foreach (var (lo, hi) in bands) Console.WriteLine($"      band {lo:0.0}-{hi:0.0}t");
                // First mouth's coordinates were captured in `mouths[0]` as text; re-derive
                // from the census loop instead: dump around the lowest-ring mouth lava tile.
                var (mr, mt) = (-1, -1);
                foreach (var (r, t) in initial)
                {
                    var n = planet.TilesAt(r);
                    foreach (var (dr, dtt) in new[] { (-1, 0), (0, -1), (0, 1) })
                    {
                        var r2 = r + dr;
                        if (r2 < 0 || r2 >= planet.Rings) continue;
                        var n2 = planet.TilesAt(r2);
                        var t2 = ((int)((t + 0.5f) / n * n2) + dtt % n2 + n2) % n2;
                        if (planet.Get(r2, t2) != TileKind.Sky || initial.Contains((r2, t2))) continue;
                        if (mr < 0 || r < mr) { mr = r; mt = t; }
                    }
                }
                var nm = planet.TilesAt(mr);
                Console.WriteLine($"    map around mouth lava({mr},{mt}) radTiles {Planet.RingMin + mr + 0.5f:0.0}:");
                for (var r = Math.Min(planet.Rings - 1, mr + 6); r >= Math.Max(0, mr - 6); r--)
                {
                    var n2 = planet.TilesAt(r);
                    var tc = (int)((mt + 0.5f) / nm * n2);
                    var line = $"      r{r,3} ({Planet.RingMin + r + 0.5f,5:0.0}t): ";
                    for (var dt = -10; dt <= 10; dt++)
                    {
                        var t2 = ((tc + dt) % n2 + n2) % n2;
                        var k = planet.Get(r, t2);
                        line += initial.Contains((r, t2)) ? 'L'
                            : k == TileKind.Sky ? '.'
                            : k == TileKind.LavaRock ? 'R'
                            : k == TileKind.Obsidian ? 'O'
                            : k == TileKind.Basalt ? 'B'
                            : k == TileKind.Stone ? 's'
                            : k == TileKind.Dirt ? 'd'
                            : k == TileKind.Granite ? 'g'
                            : '?';
                    }
                    Console.WriteLine(line);
                }
            }

            const float step = 1f / 60f;
            for (var tick = 0; tick < 120 * 60; tick++) cells.Update(step);

            var after = new System.Collections.Generic.HashSet<(int r, int t)>();
            ScanLava(after);
            var escaped = 0;
            var esc = new System.Collections.Generic.List<string>();
            foreach (var (r, t) in after)
            {
                // Adjacent spill (one tile of slosh around the body) is normal liquid
                // behaviour; anything further is a breach.
                var near = false;
                for (var dr = -1; dr <= 1 && !near; dr++)
                    for (var dtt = -1; dtt <= 1 && !near; dtt++)
                    {
                        var r2 = r + dr;
                        if (r2 < 0 || r2 >= planet.Rings) continue;
                        var n2 = planet.TilesAt(r2);
                        var t2 = (int)((t + 0.5f) / planet.TilesAt(r) * n2) + dtt;
                        near = initial.Contains((r2, (t2 % n2 + n2) % n2));
                    }
                if (near) continue;
                escaped++;
                if (esc.Count < 15) esc.Add($"escaped lava at ({r},{t}) kind={planet.Get(r, t)}");
            }
            Console.WriteLine($"    after 120s sim: lava tiles {after.Count} (was {initial.Count}), " +
                              $"escaped beyond body+1: {escaped}");
            foreach (var s in esc) Console.WriteLine("    " + s);

            // Map the TOP of the escape chain (highest ring = nearest the source body):
            // the breach the stream poured through is in this neighbourhood.
            if (escaped > 0)
            {
                var (hr, ht) = (-1, -1);
                foreach (var (r, t) in after)
                {
                    var near = false;
                    for (var dr = -1; dr <= 1 && !near; dr++)
                        for (var dtt = -1; dtt <= 1 && !near; dtt++)
                        {
                            var r2 = r + dr;
                            if (r2 < 0 || r2 >= planet.Rings) continue;
                            var n2 = planet.TilesAt(r2);
                            var t2 = (int)((t + 0.5f) / planet.TilesAt(r) * n2) + dtt;
                            near = initial.Contains((r2, (t2 % n2 + n2) % n2));
                        }
                    if (!near && r > hr) { hr = r; ht = t; }
                }
                var hn = planet.TilesAt(hr);
                Console.WriteLine($"    map around top escape ({hr},{ht}) " +
                                  $"bearing {MathHelper.WrapAngle((ht + 0.5f) / hn * MathF.Tau):0.000} " +
                                  $"(initial body = L, after-lava = A):");
                for (var r = Math.Min(planet.Rings - 1, hr + 8); r >= Math.Max(0, hr - 4); r--)
                {
                    var n2 = planet.TilesAt(r);
                    var tc = (int)((ht + 0.5f) / hn * n2);
                    var line = $"      r{r,3}: ";
                    for (var dt = -12; dt <= 12; dt++)
                    {
                        var t2 = ((tc + dt) % n2 + n2) % n2;
                        var k = planet.Get(r, t2);
                        line += initial.Contains((r, t2)) ? 'L'
                            : after.Contains((r, t2)) ? 'A'
                            : k == TileKind.Sky ? '.'
                            : k == TileKind.LavaRock ? 'R'
                            : k == TileKind.Obsidian ? 'O'
                            : k == TileKind.Basalt ? 'B'
                            : k == TileKind.Stone ? 's'
                            : k == TileKind.Dirt ? 'd'
                            : k == TileKind.Granite ? 'g'
                            : '?';
                    }
                    Console.WriteLine(line);
                }
            }
        }
    }
}
