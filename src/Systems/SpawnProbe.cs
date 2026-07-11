using System;
using System.Collections.Generic;
using System.Linq;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Temporary diagnostic (invoked via `dotnet run -- --spawnprobe`): parks a player in a cave
/// and on the surface, ticks SpawnDirector + creature AI for several sim-minutes, and prints
/// the population the player would actually meet. Used to chase "hardly any enemies" reports.
/// </summary>
public static class SpawnProbe
{
    public static void Run()
    {
        Probe("verdant", surface: false);
        Probe("verdant", surface: true);
        Probe("ember", surface: false);
    }

    private static void Probe(string defId, bool surface)
    {
        var def = PlanetDefs.ById(defId);
        var run = new Session(def)
        {
            Planet = WorldGen.Generate(42, def),
        };
        run.Cells = new Cells(run.Planet);
        run.Physics = new Physics(run.Planet, run.Cells);

        Vector2 pos;
        if (surface)
        {
            pos = SpawnDirector.FindSurfaceSpawn(run.Planet, -MathF.PI / 2f, run.Planet.Radius);
        }
        else
        {
            pos = FindCave(run.Planet) ?? SpawnDirector.FindSurfaceSpawn(run.Planet, 0f, run.Planet.Radius);
        }
        run.Player = new Player(pos);

        const float dt = 1f / 60f;
        var shots = new List<TitanProjectile>();
        // "Encounters" = unique creatures that ever closed to within 150px of the player —
        // the number the player would actually have to fight over the window.
        var encountered = new HashSet<Creature>();
        for (var step = 0; step < 60 * 180; step++)   // 3 sim-minutes
        {
            SpawnDirector.Update(dt, run);
            for (var i = run.Creatures.Count - 1; i >= 0; i--)
            {
                var c = run.Creatures[i];
                c.Update(dt, run.Planet, run.Physics, run.Cells, run.Player, shots);
                if (c.Hostile && (c.Position - run.Player.Position).LengthSquared() < 150f * 150f)
                    encountered.Add(c);
                if (c.Health <= 0 || (c.Position - run.Player.Position).LengthSquared() > 1000f * 1000f)
                    run.Creatures.RemoveAt(i);
            }
            shots.Clear(); // don't let globs accumulate; we only care about population
        }

        var cave = run.Creatures.Where(c => c.IsCaveKind).ToList();
        var near550 = cave.Where(c => (c.Position - run.Player.Position).Length() < 550f).ToList();
        var near250 = cave.Count(c => (c.Position - run.Player.Position).Length() < 250f);
        var hist = near550.GroupBy(c => c.Kind).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}");
        Console.WriteLine($"[{defId} {(surface ? "surface" : "cave")}] cap={def.CaveSpawnCap} " +
            $"total={run.Creatures.Count} cave<550px={near550.Count} cave<250px={near250} " +
            $"encounters(3min)={encountered.Count}");
        Console.WriteLine($"    mix: {string.Join(" ", hist)}");
    }

    private static Vector2? FindCave(Planet planet)
    {
        var rng = new Random(4321);
        for (var attempt = 0; attempt < 4000; attempt++)
        {
            var r = planet.SurfaceRing - 60 + rng.Next(40); // ~10-30 legacy tiles down
            var t = rng.Next(planet.TilesAt(r));
            if (planet.Get(r, t) != TileKind.Sky) continue;
            if (planet.GetWall(r, t) == TileKind.Sky) continue;
            return planet.TileToWorld(r, t);
        }
        return null;
    }
}
