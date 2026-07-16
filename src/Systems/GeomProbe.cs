using System;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `--geomprobe`): two jobs, both about the underground's radial
/// layout. It prints a HASH of every tile on a set of fixed-seed worlds — run it either side
/// of a worldgen refactor and any world whose hash moved is a world whose layout you changed
/// — and an air histogram by depth below the surface, which is what the upper worm band, the
/// stratum seam and the deep strata actually look like from the player's side.
/// </summary>
public static class GeomProbe
{
    public static void Run()
    {
        // Vegetation OFF, or the hash is worthless: TreeEcology.Plant draws its branches from
        // Random.Shared, so a world's tree tiles differ every process even at a fixed seed.
        // (That's a real reproducibility hole in gen, not a probe artefact — but it's above
        // ground, and everything this probe is for is below it.)
        WorldGen.ScatterVegetation = false;

        foreach (var (id, seed) in new[]
                 { ("verdant", 77), ("frost", 5), ("ember", 9), ("slag", 11), ("hollow", 13) })
            Dump(PlanetDefs.ById(id), seed);

        // The generated ocean worlds aren't in the classic chain; take one from the campaign
        // generator the same way OceanProbe does.
        foreach (var def in PlanetGen.Campaign(22))
            if (def.LakeScale > 2.5f) { Dump(def, 22); break; }

        Dump(PlanetDefs.DebugWorld, 3);
    }

    private static void Dump(PlanetDef def, int seed)
    {
        var planet = WorldGen.Generate(seed, def);

        // FNV-1a over every tile kind, in ring order — sensitive to any carve moving.
        ulong hash = 14695981039346656037;
        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                hash ^= (byte)planet.Get(r, t);
                hash *= 1099511628211;
            }
        }

        var surf = Planet.RingMin + planet.SurfaceRing;
        Console.WriteLine($"--- {def.Id} (seed {seed}, size {def.SizeScale:0.00}, " +
                          $"radius {planet.Radius}t, surface {surf}t, crust {planet.SurfaceRing} rings)");
        Console.WriteLine($"    tile hash {hash:x16}");

        var (seams, bands, _) = WorldGen.CaveStrata(planet, def);
        foreach (var (lo, hi) in seams)
            Console.WriteLine($"    seam {lo:0.0}-{hi:0.0}t = {(surf - hi) / Planet.LegacyTileScale:0.0}" +
                              $"-{(surf - lo) / Planet.LegacyTileScale:0.0} legacy tiles down");
        foreach (var (lo, hi) in bands) Console.WriteLine($"    deep band {lo:0.0}-{hi:0.0}t");

        // Air by depth, in legacy-tile buckets: where the caves actually are. Depth is
        // measured from the LOCAL ground (the stamped surface profile), not the baseline
        // radius — a mountain, a valley or a lake bowl otherwise reads as a huge cave, which
        // is exactly the misread that makes a hollow crust and a lumpy one look alike.
        var buckets = new int[28];
        var total = new int[28];
        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                var localSurf = planet.SurfaceRadiusAt(planet.TileToWorld(r, t));
                var depth = (int)((localSurf - (Planet.RingMin + r + 0.5f))
                                  / Planet.LegacyTileScale / 5f);
                if (depth < 0 || depth >= buckets.Length) continue;
                total[depth]++;
                if (planet.Get(r, t) == TileKind.Sky) buckets[depth]++;
            }
        }
        for (var b = 0; b < buckets.Length; b++)
        {
            if (total[b] == 0) continue;
            var pct = 100f * buckets[b] / total[b];
            Console.WriteLine($"      {b * 5,3}-{b * 5 + 5,3} legacy down: air {pct,5:0.0}% " +
                              new string('#', (int)(pct / 2f)));
        }
    }
}
