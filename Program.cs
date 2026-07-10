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
    var keep = new System.Collections.Generic.List<object>();
    foreach (var def in DwarfMiner.World.PlanetDefs.All)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        keep.Add(DwarfMinerGame.BuildSessionWorld(def));
        System.Console.WriteLine(
            $"{def.Id,-12} {sw.ElapsedMilliseconds} ms  heap {System.GC.GetTotalMemory(true) / (1024 * 1024)} MB");
    }
    return;
}

using var game = new DwarfMinerGame();
game.Run();
