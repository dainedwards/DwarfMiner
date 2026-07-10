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

// Temporary diagnostic: `--buildprobe` mimics boot (campaign chain + survey warm-up
// running) and times BuildSessionWorld for every active planet.
if (args.Length > 0 && args[0] == "--buildprobe")
{
    DwarfMiner.World.PlanetDefs.Activate(DwarfMiner.World.PlanetGen.Campaign(12345));
    foreach (var id in new[] { "debug", "gen5-duria" })
    {
        var def = DwarfMiner.World.PlanetDefs.ById(id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var run = new DwarfMiner.Session(def);
        run.Planet = DwarfMiner.World.WorldGen.Generate(12345, def);
        var tGen = sw.ElapsedMilliseconds;
        run.Cells = new DwarfMiner.World.Cells(run.Planet);
        run.Physics = new DwarfMiner.World.Physics(run.Planet, run.Cells);
        if (def.LavaFillFrac > 0f)
            run.Cells.FillSkyTilesWithin(run.Planet.Radius * def.LavaFillFrac, DwarfMiner.World.Material.Lava);
        foreach (var (lx, ly) in run.Planet.LavaSeeds) run.Cells.FillTile(lx, ly, DwarfMiner.World.Material.Lava);
        foreach (var (wx, wy) in run.Planet.WaterSeeds) run.Cells.FillTile(wx, wy, DwarfMiner.World.Material.Water);
        foreach (var (gx, gy) in run.Planet.GasSeeds) run.Cells.FillTile(gx, gy, DwarfMiner.World.Material.Gas);
        foreach (var (ax, ay) in run.Planet.AcidSeeds) run.Cells.FillTile(ax, ay, DwarfMiner.World.Material.Acid);
        var tSeed = sw.ElapsedMilliseconds;
        for (var i = 0; i < 120; i++) run.Cells.Update(1f / 60f);
        System.Console.WriteLine(
            $"{id,-12} gen {tGen} ms  seed {tSeed - tGen} ms  settle {sw.ElapsedMilliseconds - tSeed} ms");
    }
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
