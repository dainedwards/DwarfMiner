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

// Temporary probe: --titanplow counts terrain destroyed near a hatched titan, calm vs aggro.
if (args.Length > 0 && args[0] == "--titanplow")
{
    foreach (var aggro in new[] { false, true })
    {
        var p = DwarfMiner.World.WorldGen.Generate(55);
        var c = new DwarfMiner.World.Cells(p);
        var phys = new DwarfMiner.World.Physics(p, c);
        var sh = new System.Collections.Generic.List<DwarfMiner.Entities.TitanProjectile>();
        var bo = new System.Collections.Generic.List<DwarfMiner.Entities.FallingBoulder>();
        var t = new DwarfMiner.Entities.Titan(p, -System.MathF.PI / 2f, DwarfMiner.World.TitanKind.Godzilla);
        t.Hatch();
        var up = p.UpAt(t.Position);
        var right = new Microsoft.Xna.Framework.Vector2(-up.Y, up.X);
        var player = aggro ? t.Position + right * 200f
                           : p.Center + new Microsoft.Xna.Framework.Vector2(0, (p.Radius + 50) * DwarfMiner.World.Planet.TileSize);
        var (bx, by) = p.WorldToTile(t.Position);

        int CountSolid()
        {
            var n = 0;
            for (var dx = -20; dx <= 4; dx++)
                for (var dy = -40; dy <= 40; dy++)
                    if (DwarfMiner.World.Tiles.IsSolid(p.Get(bx + dx, by + dy))) n++;
            return n;
        }

        var before = CountSolid();
        const float dt = 1f / 60f;
        for (var i = 0; i < 60 * 30; i++)
        {
            if (!aggro) t.AggroTimer = 0f;
            else if (i % 60 == 0) t.OnDamage();
            t.Update(dt, p, phys, c, player, bo, sh);
        }
        System.Console.WriteLine($"aggro={aggro}: solid tiles near spawn {before} -> {CountSolid()}");
    }
    return;
}

using var game = new DwarfMinerGame();
game.Run();
