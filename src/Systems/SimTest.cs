using System;
using System.Collections.Generic;
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

        // --- 2. Digging: drop each digger into a cave pocket; after a while the planet-wide
        // solid tile count must have dropped (window-relative counts miss wandering diggers).
        foreach (var kind in new[] { CreatureKind.Borer, CreatureKind.Centipede, CreatureKind.MoleBeast })
        {
            var pos = FindCavePos(planet, seedOffset: (int)kind * 211);
            if (pos is not { } p) { Check($"digging: {kind} pocket found", false, "no cave"); continue; }
            var before = CountSolidPlanet(planet);
            var c = new Creature(p, kind);
            for (var step = 0; step < 60 * 20; step++)
                c.Update(dt, planet, physics, cells, player);
            var after = CountSolidPlanet(planet);
            Check($"digging: {kind} removed tiles (before {before} → after {after})", after < before);
            Check($"digging: {kind} not embedded in rock", !planet.IsSolidAt(c.Position));
        }

        // --- 3. Delver: aggro-mines toward a player separated from it by real rock. Pick a
        // cave site with a verified solid band overhead so there is actually something to dig.
        {
            Vector2? site = null;
            var up = Vector2.Zero;
            for (var s = 0; s < 300 && site is null; s++)
            {
                if (FindCavePos(planet, seedOffset: 999 + s * 13) is not { } q) continue;
                var u = planet.UpAt(q);
                if (planet.IsSolidAt(q + u * 14f) && planet.IsSolidAt(q + u * 22f) && planet.IsSolidAt(q + u * 30f))
                {
                    site = q;
                    up = u;
                }
            }
            if (site is { } p)
            {
                var closePlayer = new Player(p + up * 40f); // behind ≥2 tiles of rock, inside aggro radius
                var before = CountSolidPlanet(planet);
                var c = new Creature(p, CreatureKind.HornedDelver);
                for (var step = 0; step < 60 * 20; step++)
                    c.Update(dt, planet, physics, cells, closePlayer);
                var after = CountSolidPlanet(planet);
                Check($"delver: mined toward aggro target (before {before} → after {after})", after < before);
            }
            else
            {
                Check("delver: test site with rock band overhead found", false);
            }
        }

        // --- 4. Cell-sim perf: replicate StartNewRun's lava + water seeding on a fresh world
        // and time the settle. The first ticks carry every seeded cell awake; steady state
        // must come back under a frame budget or Density is too high for the machine.
        {
            var perfPlanet = WorldGen.Generate(7);
            var perfCells = new Cells(perfPlanet);
            perfCells.FillSkyTilesWithin(perfPlanet.Radius * 0.45f, Material.Lava);
            foreach (var (wsx, wsy) in perfPlanet.WaterSeeds)
                perfCells.FillTile(wsx, wsy, Material.Water);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 60; i++) perfCells.Update(dt);
            var first = sw.Elapsed.TotalMilliseconds / 60.0;
            for (var i = 0; i < 240; i++) perfCells.Update(dt);
            sw.Restart();
            for (var i = 0; i < 120; i++) perfCells.Update(dt);
            var steady = sw.Elapsed.TotalMilliseconds / 120.0;
            Console.WriteLine($"  [info] cells: first-second avg {first:0.00} ms/tick, steady {steady:0.00} ms/tick");
            Check("perf: steady-state cell tick under 6 ms", steady < 6.0, $"{steady:0.00} ms");
        }

        // --- 5. Projectiles: swept collision (no tunnelling), single-hit piercing, contact
        // detonation, and explosion craters that spawn debris + wake the settle physics.
        {
            var pPlanet = WorldGen.Generate(11);
            var pCells = new Cells(pPlanet);
            var pPhysics = new Physics(pPlanet, pCells);

            // 5a. Tunnelling: fire the fastest round in the game into a rock band overhead;
            // it must die at the wall face, not sail through it.
            Vector2? site = null;
            var up = Vector2.Zero;
            for (var s = 0; s < 300 && site is null; s++)
            {
                if (FindCavePos(pPlanet, seedOffset: 555 + s * 17) is not { } q) continue;
                var u = pPlanet.UpAt(q);
                if (pPlanet.IsSolidAt(q + u * 14f) && pPlanet.IsSolidAt(q + u * 22f) && pPlanet.IsSolidAt(q + u * 30f))
                {
                    site = q;
                    up = u;
                }
            }
            if (site is { } start)
            {
                var firstSolid = 0f;
                while (firstSolid < 60f && !pPlanet.IsSolidAt(start + up * firstSolid)) firstSolid += 1f;
                var laser = new Projectile(start, up * 900f, 18f, 0.6f, ProjectileKind.Laser);
                for (var step = 0; step < 120 && !laser.Dead; step++)
                    laser.Update(dt, pPlanet, pPhysics, pCells);
                var travelled = (laser.Position - start).Length();
                Check("projectile: laser dies at the wall instead of tunnelling",
                    laser.Dead && travelled <= firstSolid + Planet.TileSize,
                    $"first solid {firstSolid:0}px, stopped {travelled:0}px");
            }
            else
            {
                Check("projectile: wall test site found", false);
            }

            // 5b. A piercer damages each body once — not once per overlapping frame. Open sky
            // outside the planet so terrain can't interfere.
            var skyOrigin = pPlanet.Center + new Vector2(0, -(pPlanet.Radius + 30) * Planet.TileSize);
            {
                var grub = new Creature(skyOrigin + new Vector2(60, 0), CreatureKind.Grub);
                var before = grub.Health;
                var creatures = new List<Creature> { grub };
                var harpoon = new Projectile(skyOrigin, new Vector2(520, 0), 600f, 2.2f, ProjectileKind.Harpoon);
                for (var step = 0; step < 30; step++)
                {
                    harpoon.Update(dt, pPlanet, pPhysics, pCells);
                    Combat.ResolveHits(harpoon, creatures, null, pPlanet, pPhysics, pCells);
                }
                Check("projectile: harpoon damages a pierced body exactly once",
                    MathF.Abs(before - grub.Health - 600f) < 0.01f, $"dealt {before - grub.Health}");
            }

            // 5c. Contact explosives detonate on the first body struck instead of flying
            // through it and hitting again every frame.
            {
                var grub = new Creature(skyOrigin + new Vector2(80, 0), CreatureKind.Grub);
                var before = grub.Health;
                var creatures = new List<Creature> { grub };
                var rocket = new Projectile(skyOrigin, new Vector2(250, 0), 60f, 2.5f, ProjectileKind.Rocket);
                for (var step = 0; step < 60 && !rocket.Dead; step++)
                {
                    rocket.Update(dt, pPlanet, pPhysics, pCells);
                    Combat.ResolveHits(rocket, creatures, null, pPlanet, pPhysics, pCells);
                }
                Check("projectile: rocket detonates on body contact with exact direct damage",
                    rocket.Dead && MathF.Abs(before - grub.Health - 60f) < 0.01f,
                    $"dead {rocket.Dead}, dealt {before - grub.Health}");
            }

            // 5d. Explosion craters: tiles removed, rim crumbled into dust cells, and the
            // settle physics + cell sim tick clean afterwards.
            {
                var launch = pPlanet.Center + new Vector2(0, (Planet.RingMin + 145) * Planet.TileSize);
                var rocket = new Projectile(launch, pPlanet.GravityAt(launch) * 250f, 60f, 3f, ProjectileKind.Rocket);
                var before = CountSolidPlanet(pPlanet);
                for (var step = 0; step < 60 * 3 && !rocket.Dead; step++)
                    rocket.Update(dt, pPlanet, pPhysics, pCells);
                var after = CountSolidPlanet(pPlanet);
                Check("explosion: crater removed tiles", rocket.Dead && after < before, $"{before} → {after}");

                var dust = 0;
                for (var dy = -32; dy <= 32; dy += 2)
                    for (var dx = -32; dx <= 32; dx += 2)
                    {
                        var (cx, cy) = pCells.WorldToCell(rocket.Position + new Vector2(dx, dy));
                        if (pCells.Get(cx, cy) == Material.Dust) dust++;
                    }
                Check("explosion: rim crumbled into dust cells", dust > 0, $"{dust} dust samples");

                for (var step = 0; step < 60 * 2; step++)
                {
                    pPhysics.Update(dt);
                    pCells.Update(dt);
                }
                Check("explosion: settle physics + cells ticked clean after blast", true);
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

    private static int CountSolidPlanet(Planet planet)
    {
        var n = 0;
        foreach (var (x, y) in planet.AllTiles())
            if (Tiles.IsSolid(planet.Get(x, y)))
                n++;
        return n;
    }
}
