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

// Diagnostic: `--geomprobe` hashes fixed-seed worlds (A/B a worldgen refactor: a moved hash
// is a moved layout) and prints where each world's caves sit below the surface.
if (args.Length > 0 && args[0] == "--geomprobe")
{
    DwarfMiner.Systems.GeomProbe.Run();
    return;
}

// Diagnostic: `--lakeprobe` audits the water side of the fluid-containment contract — the
// surface basins must hold their pour instead of draining down a carver's hole.
if (args.Length > 0 && args[0] == "--lakeprobe")
{
    DwarfMiner.Systems.LakeProbe.Run();
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

// Temporary diagnostic: `--lavaprobe` audits the lava-rock jacket around every lava body
// on a built session world (shell holes reported by direction from the lava).
if (args.Length > 0 && args[0] == "--lavaprobe")
{
    DwarfMiner.Systems.LavaProbe.Run();
    return;
}

// Temporary diagnostic: `--toppleprobe` reproduces the SimTest street-level tower cut and
// reports what keeps a severed section standing when it refuses to detach.
if (args.Length > 0 && args[0] == "--toppleprobe")
{
    DwarfMiner.Systems.ToppleProbe.Run();
    return;
}

// `DM_NOFOCUS=1` — bring the window up WITHOUT stealing focus, so an automated/test launch
// does not yank the user out of whatever they are doing. SDL's macOS path activates the app
// (`activateIgnoringOtherApps`) unless SDL_MAC_BACKGROUND_APP is set, which leaves it an
// accessory app: window visible and rendering (screenshots still work), no dock activation,
// no focus theft. .NET's SetEnvironmentVariable writes a MANAGED copy only, so SDL's native
// getenv would never see the hint — poke the real environment through libc, and do it before
// the GraphicsDeviceManager in the game's constructor initialises SDL.
if (System.Environment.GetEnvironmentVariable("DM_NOFOCUS") is { Length: > 0 }
    && System.OperatingSystem.IsMacOS())
    Native.setenv("SDL_MAC_BACKGROUND_APP", "1", 1);

using var game = new DwarfMinerGame();
game.Run();

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("libc",
        CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    internal static extern int setenv(string name, string value, int overwrite);
}
