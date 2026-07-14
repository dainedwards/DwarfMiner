using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Temporary diagnostic (invoked via `dotnet run -- --cityprobe`): generates the campaign's
/// city world and prints the counts that verify the tower refit — working doors at street
/// level, ladder shafts through the storeys, apartment furniture, and the doubled saucer
/// patrol — plus a quick door-AI check (a civilian walking at a closed door opens it).
/// </summary>
public static class CityProbe
{
    public static void Run()
    {
        var chain = PlanetGen.Campaign(1234);
        PlanetDefs.Activate(chain);
        PlanetDef? def = null;
        foreach (var d in chain) if (d.Biome == "city") def = d;
        if (def is null) { Console.WriteLine("CITYPROBE: no city world in chain"); return; }

        var planet = WorldGen.Generate(42, def);
        var counts = new Dictionary<TileKind, int>();
        for (var r = 0; r < planet.Rings; r++)
            for (var t = 0; t < planet.TilesAt(r); t++)
            {
                var k = planet.Get(r, t);
                if (k is TileKind.DoorClosed or TileKind.DoorOpen or TileKind.Ladder
                    or TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp)
                    counts[k] = counts.GetValueOrDefault(k) + 1;
            }
        foreach (var (k, n) in counts) Console.WriteLine($"  {k}: {n}");
        Console.WriteLine($"  districts: {planet.CityDistricts.Count}");

        // Door AI: a marauder with its mark beyond a closed door should work the latch on
        // its way over rather than hopping at the leaf forever.
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        var ang = planet.CityDistricts.Count > 0 ? planet.CityDistricts[0].ang : 1.0f;
        var ground = SpawnDirector.FindSurfaceSpawn(planet, ang + 0.5f, planet.Radius);
        var up = planet.UpAt(ground);
        var right = new Vector2(-up.Y, up.X);
        // Hand-build a flat controlled site: a cleared corridor over a stone floor with a
        // three-tall door leaf across it, so the walk is about the door and nothing else.
        void Set(Vector2 at, TileKind k)
        {
            var (tx, ty) = planet.WorldToTile(at);
            planet.Set(tx, ty, k);
        }
        for (var ox = -30f; ox <= 60f; ox += 2f)
        {
            for (var oy = 2f; oy <= 34f; oy += 2f) Set(ground + right * ox + up * oy, TileKind.Sky);
            Set(ground + right * ox - up * 2f, TileKind.Stone);
            Set(ground + right * ox + up * 0f, TileKind.Stone);
        }
        var doorTiles = new List<(int x, int y)>();
        for (var oy = 4f; oy <= 12f; oy += 4f)
        {
            var (tx, ty) = planet.WorldToTile(ground + right * 20f + up * oy);
            planet.Set(tx, ty, TileKind.DoorClosed);
            doorTiles.Add((tx, ty));
        }
        var bandit = new Creature(ground + up * 9f, CreatureKind.Marauder);
        var player = new Player(ground + right * 280f + up * 9f);
        var opened = false;
        for (var i = 0; i < 900 && !opened; i++)
        {
            bandit.Update(1f / 60f, planet, physics, cells, player, null);
            foreach (var (tx, ty) in doorTiles)
                opened |= planet.Get(tx, ty) == TileKind.DoorOpen;
        }
        Console.WriteLine($"  bandit opens a door on approach: {(opened ? "YES" : "NO")}");
        Console.WriteLine("CITYPROBE: DONE");
    }
}
