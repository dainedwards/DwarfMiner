using DwarfMiner;

// Headless sim check: `dotnet run -- --simtest` exercises creature physics without a window.
if (args.Length > 0 && args[0] == "--simtest")
{
    DwarfMiner.Systems.SimTest.Run();
    return;
}

// Performance harness: `dotnet run -c Release -- --perf` times the cell sim and collapse
// physics on a giant world under mass tile breakage and earthquakes.
if (args.Length > 0 && args[0] == "--perf")
{
    DwarfMiner.Systems.PerfTest.Run();
    return;
}

// Temporary diagnostic: `--spawnprobe` simulates the spawn director around a parked player
// and prints the local population mix.
if (args.Length > 0 && args[0] == "--spawnprobe")
{
    DwarfMiner.Systems.SpawnProbe.Run();
    return;
}

using var game = new DwarfMinerGame();
game.Run();
