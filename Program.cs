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
    var warmup = System.Threading.Tasks.Task.Run(() =>
    {
        foreach (var def in DwarfMiner.World.PlanetDefs.All) DwarfMiner.Space.Survey.WorldFor(def);
    });
    foreach (var def in DwarfMiner.World.PlanetDefs.All)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DwarfMinerGame.BuildSessionWorld(def);
        System.Console.WriteLine($"{def.Id,-12} scale {def.SizeScale:0.0}  {sw.ElapsedMilliseconds} ms");
    }
    System.Console.WriteLine($"warmup done: {warmup.IsCompleted}");
    return;
}

using var game = new DwarfMinerGame();
game.Run();
