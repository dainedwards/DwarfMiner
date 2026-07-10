using DwarfMiner;

// Temporary probe: --titanwalk replays the SimTest Slattern scenario with telemetry.
if (args.Length > 0 && args[0] == "--titanwalk")
{
    var p = DwarfMiner.World.WorldGen.Generate(60);
    var c = new DwarfMiner.World.Cells(p);
    var phys = new DwarfMiner.World.Physics(p, c);
    var sh = new System.Collections.Generic.List<DwarfMiner.Entities.TitanProjectile>();
    var bo = new System.Collections.Generic.List<DwarfMiner.Entities.FallingBoulder>();
    var t = new DwarfMiner.Entities.Titan(p, -System.MathF.PI / 2f, DwarfMiner.World.TitanKind.Slattern);
    t.Hatch();
    const float dt = 1f / 60f;
    for (var i = 0; i < 180; i++) t.Update(dt, p, phys, c, t.Position, bo, sh);
    var up0 = p.UpAt(t.Position);
    var right0 = new Microsoft.Xna.Framework.Vector2(-up0.Y, up0.X);
    var player = t.Position + right0 * 130f;
    for (var i = 0; i < 60 * 16; i++)
    {
        t.OnDamage();
        t.Update(dt, p, phys, c, player, bo, sh);
        if (i % 60 == 0)
        {
            var l0 = t.Legs[0]; var l1 = t.Legs[1];
            System.Console.WriteLine($"t={i / 60f,4:0.0}s dist={(player - t.Position).Length(),6:0.0} " +
                $"shots={sh.Count} grounded={t.Grounded} cd={t.SpecialCooldown,5:0.00} " +
                $"step0={l0.StepT:0.00} step1={l1.StepT:0.00} vel={t.Velocity.Length():0.0}");
        }
    }
    return;
}

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

using var game = new DwarfMinerGame();
game.Run();
