using System;
using System.Diagnostics;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Headless performance harness (run with <c>dotnet run -c Release -- --perf</c>). Builds a
/// giant world and hammers the two systems disaster-heavy play leans on — the cell sim under
/// mass tile breakage and the collapse physics under earthquakes — printing per-tick timings.
/// Not a pass/fail test: it exists so perf work has before/after numbers instead of vibes.
/// </summary>
public static class PerfTest
{
    private const float Dt = 1f / 60f;

    /// <summary>The harness's own pinned world: the QA def blown back up to the 1.8× giant
    /// it used to be. DebugWorld itself shrank to 0.7× for fast loads, but the harness
    /// exists for before/after comparisons — its world must stay identical across sessions,
    /// not track playtest tuning.</summary>
    private static PlanetDef HarnessWorld => PlanetDefs.DebugWorld with
    {
        SizeScale = 1.8f,
        LakeMin = 4, LakeExtra = 1, MountainMin = 10, MountainExtra = 3,
        CrystalPockets = 3, FungalPockets = 3, AcidPools = 3, Volcanoes = 3, CityLots = 4,
    };

    public static void Run()
    {
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        var planet = WorldGen.Generate(42, HarnessWorld);   // 1.8x giant = worst case
        Console.WriteLine($"[perf] worldgen 1.8x: {sw.ElapsedMilliseconds}ms " +
                          $"({planet.Rings} rings, {planet.TileCount} tiles)");

        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        var rng = new Random(7);

        // Seed the world's liquids the way BuildSessionWorld does — lava sea + lakes via
        // the SILENT fills and one boundary wake — so this measures the real load path
        // (the old always-wake FillTile seeding made the first ticks walk every interior
        // sea cell once; at Density 8 that was the user-visible multi-second load stall).
        if (HarnessWorld.LavaFillFrac > 0f)
        {
            var (_, _, seaFloor) = WorldGen.CaveStrata(planet, HarnessWorld);
            cells.FillSkyTilesWithin(planet.Radius * HarnessWorld.LavaFillFrac,
                Material.Lava, seaFloor);
        }
        foreach (var (x, y) in planet.WaterSeeds) cells.FillTileSilent(x, y, Material.Water);
        cells.WakeFreeSurfaces(0, cells.Height - 1);
        sw.Restart();
        for (var i = 0; i < 120; i++) cells.Update(Dt);
        Console.WriteLine($"[perf] sea+water pre-settle 120 ticks: {sw.ElapsedMilliseconds}ms");

        // --- Scenario 1: meteor storm. 30 crater discs (r=10 tiles) breaking rock into dust
        // cells all around the surface, then 10 sim-seconds of cells+physics catching up.
        var surfaceR = planet.SurfaceRing - 6;
        var broken = 0;
        sw.Restart();
        for (var m = 0; m < 30; m++)
        {
            var ty0 = rng.Next(planet.TilesAt(surfaceR));
            for (var dx = -10; dx <= 10; dx++)
                for (var dy = -10; dy <= 10; dy++)
                {
                    if (dx * dx + dy * dy > 100) continue;
                    var x = surfaceR + dx;
                    var y = ty0 + dy;
                    var k = planet.Get(x, y);
                    if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;
                    planet.Set(x, y, TileKind.Sky);
                    cells.SpawnDustInTile(x, y, k);
                    physics.MarkDirty(x, y);
                    broken++;
                }
        }
        Console.WriteLine($"[perf] meteor storm: {broken} tiles broken " +
                          $"(break loop {sw.Elapsed.TotalMilliseconds:F1}ms)");

        // First ticks split by system — in-game the break loop and the first sim tick share
        // one frame, so this is where the hitch actually lives.
        for (var i = 0; i < 6; i++)
        {
            sw.Restart();
            cells.Update(Dt);
            var cellsMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            physics.Update(Dt);
            var physMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine($"[perf]   tick {i}: cells {cellsMs:F2}ms, physics {physMs:F2}ms");
        }
        RunTicks("meteor aftermath", 600, () => { cells.Update(Dt); physics.Update(Dt); });

        // --- Scenario 2: earthquakes. Six wide quakes around the planet — each re-checks
        // every solid tile in a 600-px disc, so this is the flood-fill worst case.
        sw.Restart();
        for (var q = 0; q < 6; q++)
        {
            var ang = q * MathHelper.TwoPi / 6f;
            var epi = planet.Center + new Vector2(MathF.Cos(ang), MathF.Sin(ang))
                * planet.Radius * Planet.TileSize * 0.55f;
            physics.Earthquake(epi, 600f, 3);
        }
        Console.WriteLine($"[perf] 6x earthquake r600 (sync part): {sw.ElapsedMilliseconds}ms");
        RunTicks("quake aftermath", 600, () => { cells.Update(Dt); physics.Update(Dt); });

        // --- Scenario 3: particle storm. The burst-die-off pattern disasters produce:
        // tens of thousands of chips spawned across a few frames, all expiring together.
        var particles = new Rendering.Particles();
        var surface = planet.TileToWorld(planet.SurfaceRing, 0);
        for (var i = 0; i < 4000; i++)
            particles.EmitChips(surface + new Vector2(rng.Next(-200, 200), rng.Next(-200, 200)),
                TileKind.Stone);
        Console.WriteLine($"[perf] particle storm: {particles.Count} spawned");
        RunTicks("particle die-off", 120, () => particles.Update(Dt, planet));

        Console.WriteLine($"[perf] total {total.ElapsedMilliseconds}ms");
    }

    /// <summary>Tick <paramref name="body"/> n times and print total / mean / worst-tick —
    /// worst matters most, it's the frame hitch the player feels.</summary>
    private static void RunTicks(string label, int n, Action body)
    {
        var sw = new Stopwatch();
        double worst = 0, sum = 0;
        for (var i = 0; i < n; i++)
        {
            sw.Restart();
            body();
            var ms = sw.Elapsed.TotalMilliseconds;
            sum += ms;
            if (ms > worst) worst = ms;
        }
        Console.WriteLine($"[perf] {label}: {n} ticks, total {sum:F0}ms, " +
                          $"mean {sum / n:F2}ms, worst {worst:F2}ms");
    }
}
