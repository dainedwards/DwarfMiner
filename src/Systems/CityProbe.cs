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

        // Door AI: park a civilian on flat ground, wall a 2-tall door in front of it, and
        // march it at the leaf — the walker should pop it open within a couple of seconds.
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        var ang = planet.CityDistricts.Count > 0 ? planet.CityDistricts[0].ang : 1.0f;
        var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
        var up = planet.UpAt(ground);
        var right = new Vector2(-up.Y, up.X);
        var civ = new Creature(ground + up * 5f, CreatureKind.Civilian);
        var doorAt = ground + right * 10f + up * 4f;
        var (dx1, dy1) = planet.WorldToTile(doorAt);
        var (dx2, dy2) = planet.WorldToTile(doorAt + up * Planet.TileSize);
        planet.Set(dx1, dy1, TileKind.DoorClosed);
        planet.Set(dx2, dy2, TileKind.DoorClosed);
        var player = new Player { Position = ground + right * 200f };
        var opened = false;
        for (var i = 0; i < 600 && !opened; i++)
        {
            civ.Update(1f / 60f, planet, physics, cells, player, null);
            // Keep it marching at the door whatever its amble decided.
            civ.Velocity = right * 30f + civ.Velocity - right * Vector2.Dot(civ.Velocity, right);
            civ.Position += right * (30f / 60f);
            opened = planet.Get(dx1, dy1) == TileKind.DoorOpen
                  || planet.Get(dx2, dy2) == TileKind.DoorOpen;
        }
        Console.WriteLine($"  civilian opens a door: {(opened ? "YES" : "NO")}");
        Console.WriteLine("CITYPROBE: DONE");
    }
}
