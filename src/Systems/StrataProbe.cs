using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `dotnet run -- --strataprobe`): generates worlds across the lava
/// spectrum and verifies the stratified-underground contract introduced with the deep cave
/// layers — the properties that are hard to eyeball but catastrophic to get wrong:
///  1. SEAMS ARE SOLID — zero Sky tiles inside any CaveStrata seam range (a single hole
///     lets the lava sea drain into the deep zone = permanent plumbing = cell-budget death).
///  2. DEEP ZONE EXISTS — the bands below the sea actually contain carved caves.
///  3. DEEP ZONE IS SEALED — flood-fill over Sky tiles from every deep-band cave must never
///     reach above the top seam: the player MUST mine to get in (and lava can't get in).
///  4. The lava SEA occupies only its band (sky below the sea floor stays dry at fill time).
///  5. Rolling surface — the SurfaceProfile's spread confirms the hill channel is live.
/// </summary>
public static class StrataProbe
{
    public static void Run()
    {
        var worlds = new List<(string name, int seed, PlanetDef def)>
        {
            ("verdant", 11, PlanetDefs.ById("verdant")),   // standard lava fill
            ("ember",   12, PlanetDefs.ById("ember")),     // high lava — no upper worms, deep strata only
            ("hollow",  13, PlanetDefs.ById("hollow")),    // no lava at all — strata under the crust network
        };

        var allOk = true;
        foreach (var (name, seed, def) in worlds)
        {
            var planet = WorldGen.Generate(seed, def);
            var (seams, bands, seaFloor) = WorldGen.CaveStrata(planet, def);
            Console.WriteLine($"--- {name} (LavaFillFrac {def.LavaFillFrac:0.00}, radius {planet.Radius}t) ---");
            Console.WriteLine($"    seaFloor {seaFloor:0}t, seams [{string.Join(", ", seams.ConvertAll(s => $"{s.lo:0}-{s.hi:0}"))}], " +
                              $"bands [{string.Join(", ", bands.ConvertAll(b => $"{b.lo:0}-{b.hi:0}"))}]");

            // 1. Seams solid.
            foreach (var (lo, hi) in seams)
            {
                var holes = CountSkyInRadiusRange(planet, lo, hi);
                Report(ref allOk, holes == 0, $"seam {lo:0}-{hi:0} solid (holes: {holes})");
            }

            // 2. Deep bands contain caves.
            foreach (var (lo, hi) in bands)
            {
                var caves = CountSkyInRadiusRange(planet, lo, hi);
                Report(ref allOk, caves > 60, $"band {lo:0}-{hi:0} has caves (sky tiles: {caves})");
            }

            // 3. Deep zone sealed: flood from all deep sky tiles, must not escape the top seam.
            if (bands.Count > 0)
            {
                var topSeamHi = seams[0].hi;
                var escaped = DeepFloodEscapes(planet, bands, topSeamHi, out var visited);
                Report(ref allOk, !escaped, $"deep zone sealed below {topSeamHi:0}t (flooded {visited} tiles)");
            }

            // 4. On lava worlds the dry zone stays dry at fill time (structural check: the
            //    fill respects the floor by construction; assert the floor sits above the
            //    deep bands so no band tile is inside the fill range).
            if (def.LavaFillFrac > 0f && bands.Count > 0)
                Report(ref allOk, bands[0].hi <= seaFloor,
                    $"deep bands end ({bands[0].hi:0}t) below the sea floor ({seaFloor:0}t)");

            // 5. Rolling surface: profile spread should show real hills (was ±~3 rings).
            if (planet.SurfaceProfile is { Length: > 0 } prof)
            {
                float min = float.MaxValue, max = float.MinValue;
                foreach (var p in prof) { min = MathF.Min(min, p); max = MathF.Max(max, p); }
                Report(ref allOk, max - min >= 8f, $"surface rolls (profile spread {max - min:0.0} rings)");
            }
        }
        Console.WriteLine(allOk ? "STRATA PROBE: ALL OK" : "STRATA PROBE: FAILURES ABOVE");
    }

    private static void Report(ref bool allOk, bool ok, string what)
    {
        if (!ok) allOk = false;
        Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
    }

    private static int CountSkyInRadiusRange(Planet planet, float loTiles, float hiTiles)
    {
        var count = 0;
        var r0 = Math.Max(0, (int)(loTiles - Planet.RingMin));
        var r1 = Math.Min(planet.Rings - 1, (int)(hiTiles - Planet.RingMin) + 1);
        for (var r = r0; r <= r1; r++)
        {
            var radTiles = Planet.RingMin + r + 0.5f;
            if (radTiles < loTiles || radTiles >= hiTiles) continue;
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
                if (planet.Get(r, t) == TileKind.Sky) count++;
        }
        return count;
    }

    /// <summary>BFS over Sky tiles from every cave tile inside the deep bands; true if the
    /// flood reaches any tile ABOVE the top seam (i.e. the "sealed" contract is broken).</summary>
    private static bool DeepFloodEscapes(Planet planet, List<(float lo, float hi)> bands,
        float topSeamHiTiles, out int visitedCount)
    {
        var visited = new HashSet<long>();
        var frontier = new Queue<(int r, int t)>();
        long Key(int r, int t) => (long)r * 4_000_000L + (uint)t;

        foreach (var (lo, hi) in bands)
        {
            var r0 = Math.Max(0, (int)(lo - Planet.RingMin));
            var r1 = Math.Min(planet.Rings - 1, (int)(hi - Planet.RingMin) + 1);
            for (var r = r0; r <= r1; r++)
            {
                var radTiles = Planet.RingMin + r + 0.5f;
                if (radTiles < lo || radTiles >= hi) continue;
                var n = planet.TilesAt(r);
                for (var t = 0; t < n; t++)
                    if (planet.Get(r, t) == TileKind.Sky && visited.Add(Key(r, t)))
                        frontier.Enqueue((r, t));
            }
        }

        var escaped = false;
        while (frontier.Count > 0)
        {
            var (r, t) = frontier.Dequeue();
            if (Planet.RingMin + r + 0.5f >= topSeamHiTiles) { escaped = true; break; }

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
        visitedCount = visited.Count;
        return escaped;
    }
}
