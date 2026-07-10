using DwarfMiner;

// Temporary probe: --titanwalk tracks a roaming titan's height above the local surface.
if (args.Length > 0 && args[0] == "--titanwalk")
{
    var p = DwarfMiner.World.WorldGen.Generate(55);
    var c = new DwarfMiner.World.Cells(p);
    var phys = new DwarfMiner.World.Physics(p, c);
    var sh = new System.Collections.Generic.List<DwarfMiner.Entities.TitanProjectile>();
    var bo = new System.Collections.Generic.List<DwarfMiner.Entities.FallingBoulder>();
    var t = new DwarfMiner.Entities.Titan(p, -System.MathF.PI / 2f, DwarfMiner.World.TitanKind.Godzilla);
    t.Hatch();
    var far = p.Center + new Microsoft.Xna.Framework.Vector2(0, (p.Radius + 50) * DwarfMiner.World.Planet.TileSize);
    const float dt = 1f / 60f;
    for (var i = 0; i <= 60 * 60; i++)
    {
        t.Update(dt, p, phys, c, far, bo, sh);
        if (i % 120 == 0)
        {
            var dir = Microsoft.Xna.Framework.Vector2.Normalize(t.Position - p.Center);
            var surf = 0f;
            for (var d = p.Radius + 30; d > 10; d--)
                if (p.IsSolidAt(p.Center + dir * (d * DwarfMiner.World.Planet.TileSize)))
                { surf = d * DwarfMiner.World.Planet.TileSize; break; }
            var alt = (t.Position - p.Center).Length() - surf;
            System.Console.WriteLine($"t={i / 60f,4:0.0}s alt={alt,7:0.0} vel={t.Velocity.Length(),6:0.0} " +
                $"grounded={t.Grounded} aggro={t.IsAggro}");
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
