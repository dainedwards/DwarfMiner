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

// Temporary probe: --titanwalk prints body/feet telemetry while a hatched titan walks.
if (args.Length > 0 && args[0] == "--titanwalk")
{
    var p = DwarfMiner.World.WorldGen.Generate(55);
    var cells = new DwarfMiner.World.Cells(p);
    var phys = new DwarfMiner.World.Physics(p, cells);
    var t = new DwarfMiner.Entities.Titan(p, -MathF.PI / 2f, DwarfMiner.Entities.TitanKind.Godzilla);
    t.Hatch();
    var bo = new System.Collections.Generic.List<DwarfMiner.Entities.FallingBoulder>();
    var sh = new System.Collections.Generic.List<DwarfMiner.Entities.TitanProjectile>();
    const float dt = 1f / 60f;
    for (var i = 0; i <= 1200; i++)
    {
        var up = p.UpAt(t.Position);
        var right = new Microsoft.Xna.Framework.Vector2(-up.Y, up.X);
        var player = t.Position + right * 400f;   // off to the side so it chases
        t.OnDamage();
        t.Update(dt, p, phys, cells, player, bo, sh);
        if (i % 60 == 0)
        {
            var alt = (t.Position - p.Center).Length() - SurfaceDist(p, t.Position);
            var l0 = t.Legs[0]; var l1 = t.Legs[1];
            var h0 = (l0.FootPos - t.HipWorld(l0, up, right)).Length();
            var h1 = (l1.FootPos - t.HipWorld(l1, up, right)).Length();
            var x0 = Microsoft.Xna.Framework.Vector2.Dot(l0.FootPos - t.Position, right);
            var x1 = Microsoft.Xna.Framework.Vector2.Dot(l1.FootPos - t.Position, right);
            Console.WriteLine($"t={i / 60f,4:0.0}s alt={alt,6:0.0} grounded={t.Grounded} " +
                $"hip0={h0,5:0.0} hip1={h1,5:0.0} tan0={x0,6:0.0} tan1={x1,6:0.0} " +
                $"step0={l0.StepT:0.00} step1={l1.StepT:0.00}");
        }
    }
    return;

    static float SurfaceDist(DwarfMiner.World.Planet p, Microsoft.Xna.Framework.Vector2 pos)
    {
        var dir = Microsoft.Xna.Framework.Vector2.Normalize(pos - p.Center);
        for (var d = p.Radius + 30; d > 10; d--)
            if (p.IsSolidAt(p.Center + dir * (d * DwarfMiner.World.Planet.TileSize)))
                return d * DwarfMiner.World.Planet.TileSize;
        return 0f;
    }
}

using var game = new DwarfMinerGame();
game.Run();
