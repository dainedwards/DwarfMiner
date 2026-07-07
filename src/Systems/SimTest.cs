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

            // 5e. Gun rounds chip blocks: repeated pistol fire into the same wall face must
            // eventually break through (Mine damage accumulates tile-side between hits).
            if (site is { } gunPos)
            {
                var before = CountSolidPlanet(pPlanet);
                for (var shot = 0; shot < 10; shot++)
                {
                    var round = new Projectile(gunPos, up * 480f, 14f, 1.5f, ProjectileKind.Pistol);
                    for (var step = 0; step < 120 && !round.Dead; step++)
                        round.Update(dt, pPlanet, pPhysics, pCells);
                }
                var after = CountSolidPlanet(pPlanet);
                Check("guns: sustained pistol fire breaks blocks", after < before, $"{before} → {after}");
            }

            // 5f. Laser cannon drills through the whole rock band instead of stopping at it,
            // vaporising a tunnel along the way.
            if (site is { } lancePos)
            {
                var before = CountSolidPlanet(pPlanet);
                var lance = new Projectile(lancePos, up * 800f, 40f, 1.0f, ProjectileKind.LaserCannon);
                for (var step = 0; step < 90 && !lance.Dead; step++)
                    lance.Update(dt, pPlanet, pPhysics, pCells);
                var travelled = (lance.Position - lancePos).Length();
                var after = CountSolidPlanet(pPlanet);
                Check("laser cannon: drills through many blocks", travelled > 60f && after < before,
                    $"travelled {travelled:0}px, solids {before} → {after}");
            }
        }

        // --- 6. Corpses: every creature kind pays out materials, and a corpse dropped into a
        // cave settles onto the floor instead of sinking through it or drifting.
        {
            foreach (CreatureKind kind in Enum.GetValues<CreatureKind>())
            {
                var drops = Corpse.DropsFor(kind);
                var total = 0;
                foreach (var (_, count) in drops) total += count;
                Check($"corpse: {kind} yields materials", drops.Length > 0 && total > 0);
            }

            var cPlanet = WorldGen.Generate(23);
            if (FindCavePos(cPlanet, seedOffset: 77) is { } drop)
            {
                var corpse = new Corpse(drop, CreatureKind.Grub, 4f);
                for (var step = 0; step < 60 * 5; step++)
                    corpse.Update(dt, cPlanet);
                Check("corpse: settles without embedding in rock",
                    !cPlanet.IsSolidAt(corpse.Position) && corpse.Velocity.LengthSquared() < 1f,
                    $"vel {corpse.Velocity.Length():0.00}");
                Check("corpse: still fresh within decay window", !corpse.Expired);
            }
            else
            {
                Check("corpse: drop site found", false);
            }
        }

        TestRunSave();
        TestOxygen();
        TestHazards();
        TestTitanVariants();

        Console.WriteLine(_failed ? "SIMTEST: FAIL" : "SIMTEST: PASS");
        Environment.Exit(_failed ? 1 : 0);
    }

    /// <summary>Round-trip the suspend save: build a mutated session, write, read back, and
    /// compare everything RunSave promises to preserve. The player's real save slot is
    /// untouched — any existing run.sav is backed up and restored around the test.</summary>
    private static void TestRunSave()
    {
        var backup = RunSave.Exists ? System.IO.File.ReadAllBytes(RunSavePath()) : null;
        try
        {
            var def = PlanetDefs.ById("ember");
            var run = new Session(def)
            {
                Planet = WorldGen.Generate(77, def),
                RunTime = 123.5f,
                HasCannon = true,
                ShipStage = 2,
                PadPos = new Vector2(2400, 1200),
            };
            run.Cells = new Cells(run.Planet);
            run.Physics = new Physics(run.Planet, run.Cells);
            // Mutations a real run would have: mined tunnel, damaged tile, spilled cells.
            for (var r = 120; r < 129; r++) run.Planet.Set(r, 10, TileKind.Sky);
            run.Planet.Mine(119, 10, 1);
            run.Cells.FillTile(125, 10, Material.Water);

            run.Player = new Player(new Vector2(2400, 1300))
            {
                Health = 61.5f, Oxygen = 44.25f, HasAirTank = true,
                PickaxeTier = 3, HasDrill = true, HasLaser = true,
                BeaconWorld = new Vector2(2000, 2000),
            };
            run.Player.Inventory.Add("iron", 14);
            run.Player.Inventory.Add("ruby", 4);
            run.Player.Toolbelt.AutoEquip("drill");
            run.Player.Toolbelt.Selected = 2;
            run.Titan = new Titan(run.Planet, 1.2f, TitanKind.Hydra)
            {
                Health = 1234f, Anger = 42f, EggTimer = 222f, EggHealth = 333f,
            };
            run.Sentries.Add(new Sentry(new Vector2(2100, 2100)) { Health = 17f });

            RunSave.Write(run);
            var loaded = RunSave.TryRead();
            Check("save: round-trip loads", loaded is not null);
            if (loaded is null) return;

            Check("save: planet id survives", loaded.Def.Id == "ember");
            var tilesMatch = true;
            foreach (var (x, y) in run.Planet.AllTiles())
                if (run.Planet.Get(x, y) != loaded.Planet.Get(x, y)
                    || run.Planet.GetWall(x, y) != loaded.Planet.GetWall(x, y)
                    || run.Planet.Damage(x, y) != loaded.Planet.Damage(x, y))
                { tilesMatch = false; break; }
            Check("save: every tile/wall/damage matches", tilesMatch);
            var waterFound = false;
            for (var cy = 0; cy < loaded.Cells.Height && !waterFound; cy++)
                for (var cx = 0; cx < loaded.Cells.CellsAt(cy); cx += 3)
                    if (loaded.Cells.Get(cx, cy) == Material.Water) { waterFound = true; break; }
            Check("save: spilled water present after load", waterFound);
            Check("save: ship progress survives",
                loaded.ShipStage == 2 && loaded.PadPos == run.PadPos && loaded.HasCannon && System.Math.Abs(loaded.RunTime - 123.5f) < 0.01f);
            var p = loaded.Player;
            Check("save: player stats survive",
                p.Position == run.Player.Position && System.Math.Abs(p.Health - 61.5f) < 0.01f
                && p.PickaxeTier == 3 && p.HasDrill && p.HasLaser && !p.HasHammer
                && p.BeaconWorld == run.Player.BeaconWorld);
            Check("save: oxygen + air tank survive",
                System.Math.Abs(p.Oxygen - 44.25f) < 0.01f && p.HasAirTank
                && System.Math.Abs(p.EffectiveMaxOxygen - 200f) < 0.01f);
            Check("save: inventory survives",
                p.Inventory.Count("iron") == 14 && p.Inventory.Count("ruby") == 4);
            Check("save: toolbelt survives",
                p.Toolbelt.Selected == 2 && p.Toolbelt.Contains("drill"));
            Check("save: titan survives",
                loaded.Titan.Position == run.Titan.Position
                && System.Math.Abs(loaded.Titan.Health - 1234f) < 0.01f
                && System.Math.Abs(loaded.Titan.Anger - 42f) < 0.01f);
            Check("save: titan kind + egg state survive",
                loaded.Titan.Kind == TitanKind.Hydra && !loaded.Titan.Hatched
                && System.Math.Abs(loaded.Titan.EggTimer - 222f) < 0.01f
                && System.Math.Abs(loaded.Titan.EggHealth - 333f) < 0.01f);
            Check("save: sentries survive",
                loaded.Sentries.Count == 1 && System.Math.Abs(loaded.Sentries[0].Health - 17f) < 0.01f);

            // Loaded cells must tick clean (everything was woken on read).
            for (var i = 0; i < 30; i++) loaded.Cells.Update(1f / 60f);
            Check("save: loaded cells settle without incident", true);
        }
        finally
        {
            RunSave.Delete();
            if (backup is not null) System.IO.File.WriteAllBytes(RunSavePath(), backup);
        }
    }

    private static string RunSavePath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DwarfMiner", "run.sav");

    /// <summary>Hazard cells: gas rises and flash-burns near lava, acid dissolves soft tiles,
    /// and the body-contact probe reports them. Uses hand-carved tiles for determinism.</summary>
    private static void TestHazards()
    {
        var planet = WorldGen.Generate(99);
        var cells = new Cells(planet);
        const float dt = 1f / 60f;

        // --- Gas rises through an open shaft ---
        int gr = 100, gt = 50;
        for (var dr = 0; dr <= 6; dr++) planet.Set(gr + dr, gt, TileKind.Sky);
        planet.Set(gr - 1, gt, TileKind.Stone); // floor
        cells.FillTile(gr, gt, Material.Gas);
        for (var i = 0; i < 60; i++) cells.Update(dt);
        Check("hazard: gas rises through a shaft", CountMatInTile(cells, gr + 3, gt, Material.Gas) > 0);

        // --- Acid dissolves a soft floor tile ---
        int ar = 120, at = 80;
        planet.Set(ar, at, TileKind.Dirt);   // corrodible floor
        planet.Set(ar + 1, at, TileKind.Sky);
        planet.Set(ar + 2, at, TileKind.Sky);
        cells.FillTile(ar + 1, at, Material.Acid);
        Check("hazard: contact probe sees acid",
            cells.SampleHazardsNear(planet.TileToWorld(ar + 1, at), Planet.TileSize).acid > 0);
        var corroded = false;
        for (var i = 0; i < 3000 && !corroded; i++)
        {
            cells.Update(dt);
            if (planet.Get(ar, at) == TileKind.Sky) corroded = true;
        }
        Check("hazard: acid dissolves a soft tile", corroded);
        Check("hazard: acid does NOT dissolve hard rock", DissolveHardRockStays());

        // --- Gas flash-burns to smoke on contact with lava ---
        int br = 140, bt = 20;
        planet.Set(br, bt, TileKind.Sky);
        var cy = br * Cells.Density + 2;
        var cx = bt * Cells.Density + 3;
        cells.Place(cx, cy, Material.Gas);
        cells.Place(cx + 1, cy, Material.Lava);
        var burned = false;
        for (var i = 0; i < 20 && !burned; i++)
        {
            cells.Update(dt);
            if (cells.Get(cx, cy) != Material.Gas) burned = true;
        }
        Check("hazard: gas flash-burns beside lava", burned);

        // --- Planet gating: the right worlds seed the right hazards ---
        Check("hazard: ember world seeds gas pockets",
            WorldGen.Generate(5, PlanetDefs.ById("ember")).GasSeeds.Count > 0);
        Check("hazard: slag world seeds acid pools",
            WorldGen.Generate(5, PlanetDefs.ById("slag")).AcidSeeds.Count > 0);
        var verdant = WorldGen.Generate(5, PlanetDefs.ById("verdant"));
        Check("hazard: temperate world seeds no gas/acid",
            verdant.GasSeeds.Count == 0 && verdant.AcidSeeds.Count == 0);
    }

    /// <summary>Acid over a granite tile must leave it intact — hard rock resists corrosion.</summary>
    private static bool DissolveHardRockStays()
    {
        var planet = WorldGen.Generate(101);
        var cells = new Cells(planet);
        int r = 118, t = 40;
        planet.Set(r, t, TileKind.Granite);
        planet.Set(r + 1, t, TileKind.Sky);
        cells.FillTile(r + 1, t, Material.Acid);
        for (var i = 0; i < 1500; i++) cells.Update(1f / 60f);
        return planet.Get(r, t) == TileKind.Granite;
    }

    private static int CountMatInTile(Cells cells, int tx, int ty, Material m)
    {
        var n = 0;
        for (var dy = 0; dy < Cells.Density; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                if (cells.Get(ty * Cells.Density + dx, tx * Cells.Density + dy) == m) n++;
        return n;
    }

    /// <summary>Air-supply rules: refill at the surface, monotonic drain with depth, and
    /// thin-atmosphere planets draining faster than the temperate starter.</summary>
    private static void TestOxygen()
    {
        Check("oxygen: surface breathes (no drain)",
            OxygenRules.AtSurfaceAir(0f) && OxygenRules.DrainPerSecond(2f, 1f) == 0f);
        Check("oxygen: deep drains", OxygenRules.DrainPerSecond(60f, 1f) > 0f);
        Check("oxygen: drain rises with depth",
            OxygenRules.DrainPerSecond(100f, 1f) > OxygenRules.DrainPerSecond(40f, 1f));
        Check("oxygen: drain caps at MaxDrain",
            System.Math.Abs(OxygenRules.DrainPerSecond(500f, 1f) - OxygenRules.MaxDrain) < 0.001f);

        var verdant = PlanetDefs.ById("verdant").OxygenDrainScale;
        var slag = PlanetDefs.ById("slag").OxygenDrainScale;
        Check("oxygen: thin-atmosphere world drains faster",
            OxygenRules.DrainPerSecond(80f, slag) > OxygenRules.DrainPerSecond(80f, verdant));

        // A full base tank at a steady mid-depth should last a meaningful but finite dive.
        var seconds = Player.BaseMaxOxygen / OxygenRules.DrainPerSecond(70f, 1f);
        Check($"oxygen: base tank lasts a sensible mid-depth window ({seconds:0}s)",
            seconds is > 15f and < 90f);
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
