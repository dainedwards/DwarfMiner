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

// Temporary diagnostic: `--cityprobe` counts the city-tower refit (doors/ladders/furniture)
// and checks the door-opening creature AI.
if (args.Length > 0 && args[0] == "--cityprobe")
{
    DwarfMiner.Systems.CityProbe.Run();
    return;
}

// Temporary diagnostic: `--acidprobe` measures acid corrosion + wake-set on an acid world.
if (args.Length > 0 && args[0] == "--acidprobe")
{
    DwarfMiner.Systems.AcidProbe.Run();
    return;
}

// Diagnostic: `--strataprobe` verifies the stratified underground — solid seams, sealed
// deep cave layers below the lava sea, and the rolling surface channel.
if (args.Length > 0 && args[0] == "--strataprobe")
{
    DwarfMiner.Systems.StrataProbe.Run();
    return;
}

// Diagnostic: `--smokeprobe` verifies effects-as-materials (DM_CELLFX) — explosion smoke
// plumes as real Smoke cells and landed cinders stamp real Fire cells.
if (args.Length > 0 && args[0] == "--smokeprobe")
{
    DwarfMiner.Systems.SmokeProbe.Run();
    return;
}

// Diagnostic: `--oceanprobe` verifies the water-world contract — mostly-sea surface with
// islands, obsidian-armoured seabed, dry interconnected under-sea caves, no sea→cave leak
// path, open island grotto mouths.
if (args.Length > 0 && args[0] == "--oceanprobe")
{
    DwarfMiner.Systems.OceanProbe.Run();
    return;
}

// Temporary diagnostic: `--toppleprobe` reproduces the SimTest street-level tower cut and
// reports what keeps a severed section standing when it refuses to detach.
if (args.Length > 0 && args[0] == "--toppleprobe")
{
    DwarfMiner.Systems.ToppleProbe.Run();
    return;
}

using var game = new DwarfMinerGame();
game.Run();
