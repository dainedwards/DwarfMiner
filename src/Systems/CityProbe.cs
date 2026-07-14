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
        var bandit = new Creature(ground + up * 5f, CreatureKind.Marauder);
        var doorAt = ground + right * 14f + up * 3f;
        var (dx1, dy1) = planet.WorldToTile(doorAt);
        var (dx2, dy2) = planet.WorldToTile(doorAt + up * Planet.TileSize);
        var (dx3, dy3) = planet.WorldToTile(doorAt + up * (Planet.TileSize * 2f));
        planet.Set(dx1, dy1, TileKind.DoorClosed);
        planet.Set(dx2, dy2, TileKind.DoorClosed);
        planet.Set(dx3, dy3, TileKind.DoorClosed);
        var player = new Player(ground + right * 260f + up * 6f);
        var opened = false;
        for (var i = 0; i < 900 && !opened; i++)
        {
            bandit.Update(1f / 60f, planet, physics, cells, player, null);
            opened = planet.Get(dx1, dy1) == TileKind.DoorOpen
                  || planet.Get(dx2, dy2) == TileKind.DoorOpen;
        }
        Console.WriteLine($"  bandit opens a door on approach: {(opened ? "YES" : "NO")}");
        Console.WriteLine("CITYPROBE: DONE");
    }
}
