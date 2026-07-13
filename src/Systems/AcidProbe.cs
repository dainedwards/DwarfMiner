using System;
using System.Collections.Generic;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

/// <summary>
/// Temporary diagnostic (invoked via `dotnet run -- --acidprobe`): generates an acid world,
/// pours the acid seeds exactly like a run start, settles, then ticks the cell sim for a long
/// stretch and reports how much terrain the acid corrodes and how many cells stay awake. A
/// steady stream of corrosion or a persistently large wake-set is the "acid leaks + lags" bug.
/// </summary>
public static class AcidProbe
{
    public static void Run()
    {
        for (var seed = 1; seed <= 3; seed++)
        {
            var chain = PlanetGen.Campaign(1000 + seed);
            PlanetDef acid = null!;
            foreach (var d in chain) if (d.Biome == "acid") { acid = d; break; }
            if (acid is null) { Console.WriteLine($"seed {seed}: no acid world"); continue; }

            var planet = WorldGen.Generate(seed, acid);
            var cells = new Cells(planet);
            var _ = new Physics(planet, cells);

            // Pour acid exactly like Game1.BuildSessionWorld. (Temporary split: shallow seeds
            // are surface pools, deep ones are the volcano plumbing — pour only one to isolate.)
            var poolSeeds = 0; var volcSeeds = 0;
            foreach (var (ax, ay) in planet.AcidSeeds)
            {
                var d = planet.SurfaceRing - ax;
                var isPool = d >= 0 && d <= 20;       // surface pools; volcano = above surface or deep
                if (isPool) poolSeeds++; else volcSeeds++;
                if (Environment.GetEnvironmentVariable("ACID_ONLY") is "pool" && !isPool) continue;
                if (Environment.GetEnvironmentVariable("ACID_ONLY") is "volc" && isPool) continue;
                cells.FillTile(ax, ay, Material.Acid);
            }
            Console.WriteLine($"  (poolSeeds={poolSeeds} volcSeeds={volcSeeds} ACID_ONLY={Environment.GetEnvironmentVariable("ACID_ONLY")})");

            // Dump a vertical column through the shallowest surface acid pool (gen-time, before
            // pouring) to see whether its floor/walls are actually obsidian.
            if (seed == 1)
            {
                (int r, int t) shallow = (-1, -1);
                var bestDepth = 999;
                foreach (var (ax, ay) in planet.AcidSeeds)
                {
                    var dd = planet.SurfaceRing - ax;
                    if (dd >= 1 && dd < bestDepth) { bestDepth = dd; shallow = (ax, ay); }
                }
                if (shallow.r >= 0)
                {
                    Console.WriteLine($"  column at pool seed r={shallow.r} t={shallow.t} (depth {bestDepth}):");
                    for (var rr = planet.SurfaceRing + 2; rr >= planet.SurfaceRing - 16; rr--)
                    {
                        var tt = (int)((double)shallow.t / planet.TilesAt(shallow.r) * planet.TilesAt(rr));
                        Console.WriteLine($"    r={rr} depth={planet.SurfaceRing - rr,3} {planet.Get(rr, tt)}");
                    }
                }
            }

            // Gen-time seal check: how many acid seeds still border corrodible rock?
            var leakySeeds = 0;
            foreach (var (r, t) in planet.AcidSeeds)
                foreach (var (nr, nt) in Neighbours(planet, r, t))
                {
                    var k = planet.Get(nr, nt);
                    if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian)
                    { leakySeeds++; break; }
                }
            Console.WriteLine($"  gen-time leaky seeds (border corrodible rock): {leakySeeds}");

            var solidStart = CountSolid(planet);

            // Pre-settle (the run start does 120 ticks).
            for (var i = 0; i < 120; i++)
            {
                cells.Update(1f / 60f);
                if (seed == 1 && i == 8) DumpShallowestLeak(planet, cells);
            }
            var solidAfterSettle = CountSolid(planet);
            var awakeAfterSettle = cells.ActiveCellCount;

            // Long run: 90 sim-seconds, timing the steady-state tick, to see whether the
            // wake-set plateaus (normal open-pool sloshing) or keeps climbing (a leak).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 60 * 90; i++) cells.Update(1f / 60f);
            sw.Stop();
            var solidEnd = CountSolid(planet);
            var awakeEnd = cells.ActiveCellCount;

            Console.WriteLine(
                $"acid seed {seed} ({acid.Id}): seeds={planet.AcidSeeds.Count} "
                + $"corroded(settle)={solidStart - solidAfterSettle} "
                + $"corroded(90s more)={solidAfterSettle - solidEnd} "
                + $"awake(settle)={awakeAfterSettle} awake(end)={awakeEnd} "
                + $"tick={sw.Elapsed.TotalMilliseconds / (60 * 90):0.00}ms");

            // Where is the acid touching corrodible rock after settling? Sample a few.
            ReportLeaks(planet, cells);
        }
    }

    private static int CountSolid(Planet planet)
    {
        var n = 0;
        for (var r = 0; r < planet.Rings; r++)
            for (var t = 0; t < planet.TilesAt(r); t++)
                if (Tiles.IsSolid(planet.Get(r, t))) n++;
        return n;
    }

    /// <summary>Find tiles that currently hold acid cells and whose tile-neighbours are still
    /// corrodible rock — the active leak sites — and print a small sample with context.</summary>
    private static void ReportLeaks(Planet planet, Cells cells)
    {
        var found = 0;
        // Scan from the surface DOWN (high ring first) so the shallowest leak — the escape
        // point where acid first meets corrodible rock — shows up first.
        for (var r = planet.Rings - 1; r >= 0 && found < 10; r--)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n && found < 10; t++)
            {
                if (Tiles.IsSolid(planet.Get(r, t))) continue;
                if (!TileHasAcid(planet, cells, r, t)) continue;
                foreach (var (nr, nt) in Neighbours(planet, r, t))
                {
                    var k = planet.Get(nr, nt);
                    if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian)
                    {
                        var depth = planet.SurfaceRing - r;
                        var below = planet.Get(planet.InnerNeighbour(r, t).x, planet.InnerNeighbour(r, t).y);
                        Console.WriteLine($"    LEAK acid r={r} depth={depth} touches {k}; tile-below={below}");
                        found++;
                        break;
                    }
                }
            }
        }
        if (found == 0) Console.WriteLine("    no acid-vs-corrodible contacts found");
    }

    /// <summary>After a few settle ticks, find the SHALLOWEST tile where acid touches
    /// corrodible rock (the first escape point) and dump its exact neighbourhood.</summary>
    private static void DumpShallowestLeak(Planet planet, Cells cells)
    {
        for (var r = planet.Rings - 1; r >= 0; r--)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                if (Tiles.IsSolid(planet.Get(r, t)) || !TileHasAcid(planet, cells, r, t)) continue;
                var bad = false;
                foreach (var (nr, nt) in Neighbours(planet, r, t))
                {
                    var k = planet.Get(nr, nt);
                    if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian) bad = true;
                }
                if (!bad) continue;
                Console.WriteLine($"  FIRST-ESCAPE @ r={r} depth={planet.SurfaceRing - r} t={t}:");
                foreach (var (nr, nt) in Neighbours(planet, r, t))
                    Console.WriteLine($"      nb r={nr} depth={planet.SurfaceRing - nr} "
                        + $"{planet.Get(nr, nt)} wall={planet.GetWall(nr, nt)} "
                        + $"acid={TileHasAcid(planet, cells, nr, nt)}");
                return;
            }
        }
    }

    private static bool TileHasAcid(Planet planet, Cells cells, int r, int t)
    {
        var cx0 = t * Cells.Density;
        var cy0 = r * Cells.Density;
        for (var dy = 0; dy < Cells.Density; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                if (cells.Get(cx0 + dx, cy0 + dy) == Material.Acid) return true;
        return false;
    }

    private static IEnumerable<(int r, int t)> Neighbours(Planet planet, int r, int t)
    {
        var n = planet.TilesAt(r);
        yield return (r, ((t - 1) % n + n) % n);
        yield return (r, (t + 1) % n);
        yield return planet.InnerNeighbour(r, t);
        var oc = planet.OuterNeighbourCount(r, t);
        for (var i = 0; i < oc; i++) yield return planet.OuterNeighbour(r, t, i);
    }
}
