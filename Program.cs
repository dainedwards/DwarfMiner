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

// Temporary diagnostic: `--buildprobe` times BuildSessionWorld for every planet.
if (args.Length > 0 && args[0] == "--buildprobe")
{
    foreach (var def in DwarfMiner.World.PlanetDefs.All)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DwarfMinerGame.BuildSessionWorld(def);
        Console.WriteLine($"{def.Id,-12} {sw.ElapsedMilliseconds} ms");
    }
    return;
}

using var game = new DwarfMinerGame();
game.Run();
