using DwarfMiner;

// Headless sim check: `dotnet run -- --simtest` exercises creature physics without a window.
if (args.Length > 0 && args[0] == "--simtest")
{
    DwarfMiner.Systems.SimTest.Run();
    return;
}

// Temporary diagnostic: `--spawnprobe` simulates the spawn director around a parked player
// and prints the local population mix.
if (args.Length > 0 && args[0] == "--spawnprobe")
{
    DwarfMiner.Systems.SpawnProbe.Run();
    return;
}

// Temporary diagnostic: `--buildprobe` mimics boot (campaign chain + survey warm-up
// running) and times BuildSessionWorld for every active planet.
if (args.Length > 0 && args[0] == "--buildprobe")
{
    DwarfMiner.World.PlanetDefs.Activate(DwarfMiner.World.PlanetGen.Campaign(12345));
    foreach (var id in new[] { "debug", "gen5-duria" })
    {
        var def = DwarfMiner.World.PlanetDefs.ById(id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = new DwarfMiner.Session(def);
        run.Planet = DwarfMiner.World.WorldGen.Generate(12345, def);
        var tGen = sw.ElapsedMilliseconds;
        run.Cells = new DwarfMiner.Systems.Cells(run.Planet);
        run.Physics = new DwarfMiner.Systems.Physics(run.Planet, run.Cells);
        if (def.LavaFillFrac > 0f)
            run.Cells.FillSkyTilesWithin(run.Planet.Radius * def.LavaFillFrac, DwarfMiner.Systems.Material.Lava);
        foreach (var (lx, ly) in run.Planet.LavaSeeds) run.Cells.FillTile(lx, ly, DwarfMiner.Systems.Material.Lava);
        foreach (var (wx, wy) in run.Planet.WaterSeeds) run.Cells.FillTile(wx, wy, DwarfMiner.Systems.Material.Water);
        foreach (var (gx, gy) in run.Planet.GasSeeds) run.Cells.FillTile(gx, gy, DwarfMiner.Systems.Material.Gas);
        foreach (var (ax, ay) in run.Planet.AcidSeeds) run.Cells.FillTile(ax, ay, DwarfMiner.Systems.Material.Acid);
        var tSeed = sw.ElapsedMilliseconds;
        for (var i = 0; i < 120; i++) run.Cells.Update(1f / 60f);
        System.Console.WriteLine(
            $"{id,-12} gen {tGen} ms  seed {tSeed - tGen} ms  settle {sw.ElapsedMilliseconds - tSeed} ms");
    }
    return;
}

using var game = new DwarfMinerGame();
game.Run();
