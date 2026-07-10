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

// Temporary probe: --titandig replays the SimTest dig/burst scenario with telemetry.
if (args.Length > 0 && args[0] == "--titandig")
{
    var pp = DwarfMiner.World.WorldGen.Generate(70);
    var pc = new DwarfMiner.World.Cells(pp);
    var pphys = new DwarfMiner.World.Physics(pp, pc);
    var psh = new System.Collections.Generic.List<DwarfMiner.Entities.TitanProjectile>();
    var pbo = new System.Collections.Generic.List<DwarfMiner.Entities.FallingBoulder>();
    var boss = new DwarfMiner.Entities.Titan(pp, -System.MathF.PI / 2f, DwarfMiner.World.TitanKind.Godzilla);
    boss.Hatch();
    const float dt = 1f / 60f;
    for (var i = 0; i < 180; i++) boss.Update(dt, pp, pphys, pc, boss.Position, pbo, psh);
    var digPlayer = boss.Position - pp.UpAt(boss.Position) * 420f;
    for (var i = 0; i < 60 * 30; i++)
    {
        boss.OnDamage(); boss.Anger = 90f;
        boss.Update(dt, pp, pphys, pc, digPlayer, pbo, psh);
    }
    System.Console.WriteLine("--- prey moves 500px above ---");
    var upPlayer = boss.Position + pp.UpAt(boss.Position) * 500f;
    for (var i = 0; i < 60 * 20; i++)
    {
        boss.OnDamage(); boss.Anger = 90f;
        boss.Update(dt, pp, pphys, pc, upPlayer, pbo, psh);
        if (i % 60 == 0)
        {
            var up = pp.UpAt(boss.Position);
            var right = new Microsoft.Xna.Framework.Vector2(-up.Y, up.X);
            var reach = boss.BodyRadius + 26f;
            var braceL = pp.IsSolidAt(boss.Position - right * reach) || pp.IsSolidAt(boss.Position - right * reach - up * 30f);
            var braceR = pp.IsSolidAt(boss.Position + right * reach) || pp.IsSolidAt(boss.Position + right * reach - up * 30f);
            var upDot = Microsoft.Xna.Framework.Vector2.Dot(upPlayer - boss.Position, up);
            System.Console.WriteLine($"t={i / 60}s radial={(boss.Position - pp.Center).Length():0} " +
                $"upDot={upDot:0} braceL={braceL} braceR={braceR} grounded={boss.Grounded} " +
                $"leaping={boss.Leaping} digging={boss.Digging} vel={boss.Velocity.Length():0}");
        }
    }
    return;
}

using var game = new DwarfMinerGame();
game.Run();
