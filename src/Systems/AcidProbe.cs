using System;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `dotnet run -- --acidprobe`): generates several acid worlds, pours
/// the acid seeds exactly like a run start, settles, then ticks the cell sim for 90 sim-seconds
/// and reports how much terrain the acid corrodes and how heavy the steady-state tick is. Used
/// to chase the "acid world melts everything and lags" report — a healthy world corrodes ~0
/// tiles after settling (obsidian-lined reservoirs) and ticks in a couple of ms.
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
            foreach (var (ax, ay) in planet.AcidSeeds) cells.FillTile(ax, ay, Material.Acid);

            var solidStart = CountSolid(planet);
            for (var i = 0; i < 120; i++) cells.Update(1f / 60f);         // pre-settle, like a run start
            var solidSettled = CountSolid(planet);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 60 * 90; i++) cells.Update(1f / 60f);
            sw.Stop();

            Console.WriteLine(
                $"acid seed {seed} ({acid.Id}): seeds={planet.AcidSeeds.Count} "
                + $"corroded(settle)={solidStart - solidSettled} "
                + $"corroded(90s more)={solidSettled - CountSolid(planet)} "
                + $"awake={cells.ActiveCellCount} tick={sw.Elapsed.TotalMilliseconds / (60 * 90):0.00}ms");
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
}
