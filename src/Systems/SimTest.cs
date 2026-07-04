using System;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Headless physics smoke-test (run with <c>dotnet run -- --simtest</c>). No graphics device:
/// generates a world and ticks creatures directly, asserting the two invariants that broke
/// once already — bodies never end a tick inside solid rock, and diggers actually remove
/// tiles. Prints PASS/FAIL per check and exits non-zero on any failure.
/// </summary>
public static class SimTest
{
    private static bool _failed;

    public static void Run()
    {
        var planet = WorldGen.Generate(42);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        // Park the player far away on the surface so aggro doesn't skew the wander tests.
        var player = new Player(planet.Center + new Vector2(0, -(129 + Planet.RingMin) * Planet.TileSize));

        const float dt = 1f / 60f;

        // --- 1. Collision: drop walkers into caves at many angles; none may end up in rock.
        var embedded = 0;
        var tested = 0;
        foreach (var kind in new[] { CreatureKind.Grub, CreatureKind.Skitterer, CreatureKind.Grazer, CreatureKind.CaveEye })
        {
            for (var i = 0; i < 25; i++)
            {
                var pos = FindCavePos(planet, seedOffset: i * 37 + (int)kind * 101);
                if (pos is not { } p) continue;
                tested++;
                var c = new Creature(p, kind);
                for (var step = 0; step < 60 * 8; step++)
                    c.Update(dt, planet, physics, cells, player);
                if (planet.IsSolidAt(c.Position)) embedded++;
            }
        }
        Check("collision: no creature embedded in rock after 8s", embedded == 0,
            $"{embedded}/{tested} embedded");

        // --- 2. Digging: seal a borer/centipede/mole/delver in a small pocket deep in stone;
        // after a while the solid tile count around it must have dropped.
        foreach (var kind in new[] { CreatureKind.Borer, CreatureKind.Centipede, CreatureKind.MoleBeast })
        {
            var pos = FindCavePos(planet, seedOffset: (int)kind * 211);
            if (pos is not { } p) { Check($"digging: {kind} pocket found", false, "no cave"); continue; }
            var before = CountSolidAround(planet, p, 12);
            var c = new Creature(p, kind);
            for (var step = 0; step < 60 * 20; step++)
                c.Update(dt, planet, physics, cells, player);
            var after = CountSolidAround(planet, p, 12);
            Check($"digging: {kind} removed tiles (before {before} → after {after})", after < before);
            Check($"digging: {kind} not embedded in rock", !planet.IsSolidAt(c.Position));
        }

        // --- 3. Delver: aggro-mines toward a player standing behind rock.
        {
            var pos = FindCavePos(planet, seedOffset: 999);
            if (pos is { } p)
            {
                var up = planet.UpAt(p);
                var closePlayer = new Player(p + up * 40f); // 5 tiles of rock above, inside aggro radius
                var before = CountSolidAround(planet, p, 10);
                var c = new Creature(p, CreatureKind.HornedDelver);
                for (var step = 0; step < 60 * 20; step++)
                    c.Update(dt, planet, physics, cells, closePlayer);
                var after = CountSolidAround(planet, p, 10);
                Check($"delver: mined toward aggro target (before {before} → after {after})", after < before);
            }
        }

        Console.WriteLine(_failed ? "SIMTEST: FAIL" : "SIMTEST: PASS");
        Environment.Exit(_failed ? 1 : 0);
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _failed = true;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? $" ({detail})" : "")}");
    }

    /// <summary>Deterministic hunt for a cave tile (Sky with rock wall behind it) in the
    /// mid-depth band, away from the lava zone.</summary>
    private static Vector2? FindCavePos(Planet planet, int seedOffset)
    {
        var rng = new Random(1000 + seedOffset);
        for (var attempt = 0; attempt < 4000; attempt++)
        {
            var r = 90 + rng.Next(35); // depth ~4..39 below baseline surface (ring 129)
            var t = rng.Next(Planet.TilesAt(r));
            if (planet.Get(r, t) != TileKind.Sky) continue;
            if (planet.GetWall(r, t) == TileKind.Sky) continue;
            return planet.TileToWorld(r, t);
        }
        return null;
    }

    private static int CountSolidAround(Planet planet, Vector2 world, int radiusTiles)
    {
        var (cx, cy) = planet.WorldToTile(world);
        var n = 0;
        for (var dy = -radiusTiles; dy <= radiusTiles; dy++)
            for (var dx = -radiusTiles; dx <= radiusTiles; dx++)
                if (planet.InBounds(cx + dx, cy + dy) && Tiles.IsSolid(planet.Get(cx + dx, cy + dy)))
                    n++;
        return n;
    }
}
