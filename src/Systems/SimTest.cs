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

        // --- 7. Fuel ore: a fresh world must seed enough mineable fuel to actually launch.
        {
            var fuelPlanet = WorldGen.Generate(31);
            var fuelTiles = 0;
            foreach (var (x, y) in fuelPlanet.AllTiles())
                if (fuelPlanet.Get(x, y) == TileKind.FuelOre) fuelTiles++;
            Check($"fuel: world seeds mineable fuel ore ({fuelTiles} tiles)",
                fuelTiles >= DwarfMinerGame.FuelToLaunch, $"{fuelTiles} tiles");
        }

        TestRunSave();
        TestOxygen();
        TestHazards();
        TestTitanVariants();
        TestDepotBank();
        TestSfx();
        TestMeteors();
        TestCaveIn();
        TestSpaceSim();

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
                ShipFuel = 7,
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
            run.Titan = new Titan(run.Planet, 1.2f, TitanKind.Sandworm)
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
                loaded.ShipStage == 2 && loaded.ShipFuel == 7 && loaded.PadPos == run.PadPos && loaded.HasCannon && System.Math.Abs(loaded.RunTime - 123.5f) < 0.01f);
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
                loaded.Titan.Kind == TitanKind.Sandworm && !loaded.Titan.Hatched
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

    /// <summary>Boss variants: the egg hatches on both its timer and enough damage, and each
    /// kind's signature attack fires when aggroed near the player (fire/laser shots, Sandworm
    /// burrow+erupt, Kong leap+slam shockwave).</summary>
    private static void TestTitanVariants()
    {
        const float dt = 1f / 60f;

        // --- Egg hatches on the timer ---
        var planet = WorldGen.Generate(55);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        var shots = new System.Collections.Generic.List<TitanProjectile>();
        var boulders = new System.Collections.Generic.List<FallingBoulder>();

        var eggTimer = new Titan(planet, -MathF.PI / 2f, TitanKind.Kong) { EggTimer = 0.05f };
        Check("titan: starts unhatched in the egg", !eggTimer.Hatched);
        for (var i = 0; i < 10; i++) eggTimer.Update(dt, planet, physics, cells, eggTimer.Position, boulders, shots);
        Check("titan: egg hatches when the timer runs out", eggTimer.Hatched);

        // --- Egg hatches early when beaten open ---
        var eggHit = new Titan(planet, -MathF.PI / 2f, TitanKind.Mecha);
        eggHit.DamageEgg(eggHit.EggMaxHealth * 0.5f);
        Check("titan: egg survives a partial hit", !eggHit.Hatched);
        eggHit.DamageEgg(eggHit.EggMaxHealth);
        Check("titan: egg cracks open when its health is spent", eggHit.Hatched);

        // --- Each variant's special attack fires when aggroed near the player ---
        foreach (var kind in new[] { TitanKind.Godzilla, TitanKind.Mecha, TitanKind.Sandworm, TitanKind.Kong })
        {
            var p = WorldGen.Generate(60);
            var c = new Cells(p);
            var phys = new Physics(p, c);
            var sh = new System.Collections.Generic.List<TitanProjectile>();
            var bo = new System.Collections.Generic.List<FallingBoulder>();
            var titan = new Titan(p, -MathF.PI / 2f, kind);
            titan.Hatch();
            // Settle on the ground so grounded-gated specials (fire, leap) can trigger.
            for (var i = 0; i < 180; i++) titan.Update(dt, p, phys, c, titan.Position, bo, sh);
            var up = p.UpAt(titan.Position);
            var right = new Vector2(-up.Y, up.X);
            var player = new Player(titan.Position + right * 130f);

            bool sawShot = false, sawShock = false;
            for (var i = 0; i < 60 * 16; i++)
            {
                titan.OnDamage();   // keep it aggroed
                titan.Update(dt, p, phys, c, player.Position, bo, sh);
                if (sh.Count > 0) sawShot = true;
                if (titan.PendingShockwave is not null) { sawShock = true; titan.PendingShockwave = null; }
            }

            switch (kind)
            {
                case TitanKind.Godzilla:
                case TitanKind.Mecha:
                    Check($"titan: {kind} fires ranged shots", sawShot);
                    break;
                case TitanKind.Sandworm:
                    Check("titan: Sandworm slithers up and bites (shockwave)", sawShock);
                    break;
                case TitanKind.Kong:
                    Check("titan: Kong leaps and slams (shockwave)", sawShock);
                    break;
            }
        }

        // --- Planet mapping wires distinct bosses ---
        Check("titan: ember hatches the fire-breather",
            PlanetDefs.ById("ember").Titan == TitanKind.Godzilla);
        Check("titan: slag hatches the mecha",
            PlanetDefs.ById("slag").Titan == TitanKind.Mecha);

        // --- Terrain plow: a boss overlapping solid rock smashes through it ---
        {
            var pp = WorldGen.Generate(70);
            var pc = new Cells(pp);
            var pphys = new Physics(pp, pc);
            var psh = new System.Collections.Generic.List<TitanProjectile>();
            var pbo = new System.Collections.Generic.List<FallingBoulder>();
            var boss = new Titan(pp, -MathF.PI / 2f, TitanKind.Kong);
            boss.Hatch();
            // Fill the tiles under the body's centre with solid rock, then tick once.
            var (bx, by) = pp.WorldToTile(boss.Position);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    pp.Set(bx + dx, by + dy, TileKind.Stone);
            var solidBefore = 0;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (Tiles.IsSolid(pp.Get(bx + dx, by + dy))) solidBefore++;
            boss.Update(1f / 60f, pp, pphys, pc, boss.Position, pbo, psh);
            var solidAfter = 0;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (Tiles.IsSolid(pp.Get(bx + dx, by + dy))) solidAfter++;
            Check($"titan: boss plows through rock it overlaps ({solidBefore}→{solidAfter})", solidAfter < solidBefore);
        }
    }

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

    /// <summary>Meteor strikes: the director spawns them, and a strike craters the surface and
    /// leaves fuel/gold ore exposed to mine.</summary>
    private static void TestMeteors()
    {
        var planet = WorldGen.Generate(88);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        var particles = new DwarfMiner.Rendering.Particles();
        var player = new Player(SpawnDirector.FindSurfaceSpawn(planet, -MathF.PI / 2f, planet.Radius));
        var run = new Session(PlanetDefs.ById("slag"))
        {
            Planet = planet, Cells = cells, Physics = physics, Player = player,
        };

        run.MeteorTimer = 0.001f;
        AmbientDirector.Update(1f / 60f, run, particles);
        Check("meteor: director spawns a strike on its timer", run.Meteors.Count > 0);

        // Fire a meteor down onto solid ground and confirm it craters + exposes ore.
        var ground = SpawnDirector.FindSurfaceSpawn(planet, 0.7f, planet.Radius);
        var up = planet.UpAt(ground);
        var m = new Meteor(ground + up * 120f, -up * 260f, ground);
        var solidBefore = CountSolidNear(planet, ground, 5);
        for (var i = 0; i < 600 && !m.Dead; i++)
            m.Update(1f / 60f, planet, physics, cells, player, particles);
        Check("meteor: strikes and dies", m.Dead);
        var solidAfter = CountSolidNear(planet, ground, 5);
        Check($"meteor: blasts a crater ({solidBefore}->{solidAfter})", solidAfter < solidBefore);

        var (mx, my) = planet.WorldToTile(m.Position);
        var ore = false;
        for (var dx = -3; dx <= 3 && !ore; dx++)
            for (var dy = -3; dy <= 3 && !ore; dy++)
                if (planet.Get(mx + dx, my + dy) is TileKind.FuelOre or TileKind.GoldOre) ore = true;
        Check("meteor: crater exposes fuel/gold ore", ore);
    }

    /// <summary>Cave-in warning: an isolated unsupported rock island must be condemned into the
    /// tremble window (the warning), stay standing through it, then crumble — exercising
    /// Physics.NewlyCondemnedThisTick / TremblingTiles that drive the creak + HUD banner.</summary>
    private static void TestCaveIn()
    {
        var planet = WorldGen.Generate(7);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        const float dt = 1f / 60f;

        // Carve a hollow pocket at mid-depth and hang a small stone island in it — nothing
        // solid inward, outward, or beside it, so the connectivity check finds no anchor.
        const int ring = 100;
        const int ang = 100;
        for (var dr = -4; dr <= 3; dr++)
            for (var da = -4; da <= 4; da++)
                planet.Set(ring + dr, ang + da, TileKind.Sky);
        var island = new List<(int x, int y)>();
        for (var dr = 0; dr <= 1; dr++)
            for (var da = 0; da <= 1; da++)
            {
                planet.Set(ring + dr, ang + da, TileKind.Stone);
                island.Add((ring + dr, ang + da));
            }
        foreach (var (x, y) in island) physics.MarkDirty(x, y);

        // Settle runs every 0.05s, so tick a few frames for the first pass to condemn it.
        var condemned = false;
        for (var i = 0; i < 10 && !condemned; i++)
        {
            physics.Update(dt);
            if (physics.NewlyCondemnedThisTick > 0) condemned = true;
        }
        Check("cave-in: unsupported rock is condemned into the tremble window",
            condemned && physics.TremblingTiles.Count > 0);
        Check("cave-in: condemned rock still stands during the warning window",
            island.TrueForAll(t => Tiles.IsSolid(planet.Get(t.x, t.y))));

        // Run past the tremble + ring cascade; the island must crumble away to cells.
        var crumbled = false;
        for (var i = 0; i < 120; i++)
        {
            physics.Update(dt);
            if (physics.CollapsesThisTick > 0) crumbled = true;
        }
        Check("cave-in: condemned rock crumbles after the warning window",
            crumbled && island.TrueForAll(t => !Tiles.IsSolid(planet.Get(t.x, t.y))));
    }

    private static int CountSolidNear(Planet planet, Vector2 world, int r)
    {
        var (cx, cy) = planet.WorldToTile(world);
        var n = 0;
        for (var dx = -r; dx <= r; dx++)
            for (var dy = -r; dy <= r; dy++)
                if (Tiles.IsSolid(planet.Get(cx + dx, cy + dy))) n++;
        return n;
    }

    /// <summary>Procedural audio synthesis is device-free and headless-testable: every named
    /// effect renders a non-empty, non-silent 16-bit buffer without touching an audio device.</summary>
    private static void TestSfx()
    {
        foreach (var name in Sfx.Names)
        {
            var buf = Sfx.Synth(name);
            var peak = 0;
            foreach (var s in buf) peak = System.Math.Max(peak, System.Math.Abs((int)s));
            Check($"sfx: {name} renders audible PCM ({buf.Length} samples, peak {peak})",
                buf.Length > 100 && peak > 1000);
        }
    }

    /// <summary>Storage depot: only raw mats are bankable, and a planet's vault survives the
    /// meta save/load (so death doesn't wipe it).</summary>
    private static void TestDepotBank()
    {
        Check("depot: raw ore is bankable",
            Tiles.IsBankable("iron") && Tiles.IsBankable("diamond") && Tiles.IsBankable("stone"));
        Check("depot: crafted gear is not bankable",
            !Tiles.IsBankable("pistol") && !Tiles.IsBankable("sentry") && !Tiles.IsBankable("ammo_ruby"));

        var meta = new MetaSave();
        var bank = meta.BankFor("frost");
        bank["iron"] = 12;
        bank["sapphire"] = 3;
        Check("depot: BankFor returns the live per-planet dict", meta.BankFor("frost")["iron"] == 12);
        Check("depot: banks are per-planet", meta.BankFor("ember").Count == 0);

        // Mirror MetaSave.Save/Load — the nested bank dictionary must round-trip.
        var json = System.Text.Json.JsonSerializer.Serialize(meta);
        var back = System.Text.Json.JsonSerializer.Deserialize<MetaSave>(json);
        Check("depot: vault survives a meta save/load",
            back is not null && back.BankFor("frost")["iron"] == 12 && back.BankFor("frost")["sapphire"] == 3);
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

    /// <summary>The solar-system flight model: thrust moves the ship, braking stops it, the
    /// sun repels, ordinary planets are open atmosphere (flying in = entry contact), and
    /// only the locked Rift's storm wall stays solid.</summary>
    private static void TestSpaceSim()
    {
        const float dt = 1f / 60f;

        var sim = new Space.SpaceSim();
        Check("space: one planet per PlanetDef", sim.Planets.Count == World.PlanetDefs.All.Length);
        Check("space: ship parks near the start planet",
            sim.NearestPlanet().planet?.Def.Id == sim.Planets[0].Def.Id);

        // Thrust straight out for 3s: the ship must actually go somewhere.
        var start = sim.ShipPos;
        for (var i = 0; i < 180; i++) sim.Update(dt, 0f, thrust: true, brake: false);
        var travelled = (sim.ShipPos - start).Length();
        Check("space: 3s of thrust covers real distance", travelled > 500f, $"{travelled:0} px");

        // Brake to a stop: speed must collapse to near zero within 2s.
        for (var i = 0; i < 120; i++) sim.Update(dt, 0f, thrust: false, brake: true);
        Check("space: braking kills velocity", sim.ShipVel.Length() < 5f, $"{sim.ShipVel.Length():0.0} px/s");

        // Turning changes the heading at the advertised rate (≈3.4 rad in 1s of full turn).
        var h0 = sim.ShipHeading;
        for (var i = 0; i < 60; i++) sim.Update(dt, 1f, thrust: false, brake: false);
        Check("space: turning sweeps the heading", MathF.Abs(sim.ShipHeading - h0) > 2.5f);

        // Fly dead at the sun from nearby: the corona must keep the ship outside it.
        sim.ShipPos = new Vector2(Space.SpaceSim.SunRadius + 400f, 0f);
        sim.ShipVel = new Vector2(-640f, 0f);
        sim.ShipHeading = MathF.PI;
        for (var i = 0; i < 240; i++) sim.Update(dt, 0f, thrust: true, brake: false);
        Check("space: sun corona repels the ship", sim.ShipPos.Length() > Space.SpaceSim.SunRadius,
            $"dist {sim.ShipPos.Length():0}");

        // Ram a planet: the ship skims off the disc instead of tunnelling inside.
        var p = sim.Planets[2];
        sim.ShipPos = p.Pos + new Vector2(p.BodyRadius + 300f, 0f);
        sim.ShipVel = new Vector2(-640f, 0f);
        var inside = false;
        for (var i = 0; i < 240; i++)
        {
            sim.Update(dt, 0f, thrust: false, brake: false);
            if ((sim.ShipPos - p.Pos).Length() < p.BodyRadius) inside = true;
        }
        Check("space: planet disc is solid", !inside);
        Check("space: parked at the disc = landing prompt", sim.LandingCandidate()?.Def.Id == p.Def.Id);

        // Far from everything: no landing candidate.
        sim.ShipPos = new Vector2(0f, -20000f);
        Check("space: deep space offers no landing", sim.LandingCandidate() is null);

        TestSpaceCombat();
        TestFoundry();
    }

    /// <summary>Asteroids and the autocannon: rocks ram the hull (with an invulnerability
    /// window), bolts kill rocks, big rocks split, and the maintenance spawner stocks the
    /// field.</summary>
    private static void TestSpaceCombat()
    {
        const float dt = 1f / 60f;

        var sim = new Space.SpaceSim { AsteroidTarget = 0 };
        sim.ShipPos = new Vector2(0f, -30000f);   // clear of sun/planets
        sim.ShipVel = Vector2.Zero;

        // A rock drifting straight into the ship must cost exactly one hull (invuln window).
        sim.SpawnAsteroid(sim.ShipPos + new Vector2(200f, 0f), new Vector2(-300f, 0f), 20f);
        for (var i = 0; i < 120; i++) sim.Update(dt, 0f, false, false);
        Check("space: asteroid impact costs one hull", sim.Hull == sim.HullMax - 1,
            $"hull {sim.Hull}");
        Check("space: impact consumes the asteroid", sim.Asteroids.Count == 0);

        // Shoot a small rock dead ahead: bolts must connect and destroy it (no split < 26px).
        sim.ShipVel = Vector2.Zero;
        sim.ShipHeading = 0f;   // nose +X
        sim.SpawnAsteroid(sim.ShipPos + new Vector2(400f, 0f), Vector2.Zero, 20f);
        for (var i = 0; i < 90; i++)
        {
            sim.TryFire();
            sim.Update(dt, 0f, false, false);
        }
        Check("space: autocannon destroys a small rock", sim.Asteroids.Count == 0,
            $"{sim.Asteroids.Count} left");

        // A big rock splits into two smaller ones when killed.
        var big = sim.SpawnAsteroid(sim.ShipPos + new Vector2(400f, 0f), Vector2.Zero, 40f);
        big.Hp = 1f;   // one bolt finishes it
        for (var i = 0; i < 60 && sim.Asteroids.Contains(big); i++)
        {
            sim.TryFire();
            sim.Update(dt, 0f, false, false);
        }
        Check("space: big rock splits when killed",
            !sim.Asteroids.Contains(big) && sim.Asteroids.Count == 2,
            $"{sim.Asteroids.Count} fragments");

        // Ram the hull to zero: the breach flag must raise for Game1's emergency dock.
        sim.Asteroids.Clear();
        sim.Hull = 1;
        sim.HitTimer = 0f;
        sim.SpawnAsteroid(sim.ShipPos + new Vector2(60f, 0f), new Vector2(-300f, 0f), 20f);
        for (var i = 0; i < 60; i++) sim.Update(dt, 0f, false, false);
        Check("space: hull zero raises the breach flag", sim.HullBreached);

        // Maintenance spawner stocks the field when a target is set.
        sim.AsteroidTarget = 10;
        sim.Update(dt, 0f, false, false);
        Check("space: spawner stocks the asteroid field", sim.Asteroids.Count == 10);

        // Ion Engines II must actually be faster over the same burn.
        var slow = new Space.SpaceSim { AsteroidTarget = 0 };
        var fast = new Space.SpaceSim { AsteroidTarget = 0, EngineTier = 2 };
        slow.ShipPos = fast.ShipPos = new Vector2(0f, -30000f);
        var s0 = slow.ShipPos;
        for (var i = 0; i < 120; i++) { slow.Update(dt, 0f, true, false); fast.Update(dt, 0f, true, false); }
        Check("space: ion engines II outrun tier 1",
            (fast.ShipPos - s0).Length() > (slow.ShipPos - s0).Length() * 1.2f);

        // …and sip fuel doing it: 2s of burn demands less from the tank than tier 1.
        Check("space: thrust burns fuel", slow.FuelUsed > 0.5f, $"{slow.FuelUsed:0.00} used");
        Check("space: ion engines II burn less fuel", fast.FuelUsed < slow.FuelUsed * 0.8f,
            $"{fast.FuelUsed:0.00} vs {slow.FuelUsed:0.00}");

        // A dry tank drops to reserve power: same burn covers far less ground, and no fuel
        // is demanded.
        var dry = new Space.SpaceSim { AsteroidTarget = 0, HasFuel = false };
        dry.ShipPos = new Vector2(0f, -30000f);
        var d0 = dry.ShipPos;
        for (var i = 0; i < 120; i++) dry.Update(dt, 0f, true, false);
        Check("space: dry tank limps at reserve power",
            (dry.ShipPos - d0).Length() < (slow.ShipPos - s0).Length() * 0.6f);
        Check("space: reserve power burns nothing", dry.FuelUsed == 0f);

        // Hull plating raises the ceiling.
        Check("space: hull plating raises max hull",
            new Space.SpaceSim { HullTier = 2 }.HullMax == 7
            && new Space.SpaceSim().HullMax == 5);

        // Autocannon III fires a twin spread; lower tiers a single bolt.
        var twin = new Space.SpaceSim { AsteroidTarget = 0, GunTier = 3 };
        twin.ShipPos = new Vector2(0f, -30000f);
        twin.TryFire();
        Check("space: autocannon III fires twin bolts", twin.Shots.Count == 2,
            $"{twin.Shots.Count} bolts");

        // Ion Engines III outrun tier 2 and sip even less.
        var t2 = new Space.SpaceSim { AsteroidTarget = 0, EngineTier = 2 };
        var t3 = new Space.SpaceSim { AsteroidTarget = 0, EngineTier = 3 };
        t2.ShipPos = t3.ShipPos = new Vector2(0f, -30000f);
        var t0pos = t2.ShipPos;
        for (var i = 0; i < 120; i++) { t2.Update(dt, 0f, true, false); t3.Update(dt, 0f, true, false); }
        Check("space: ion engines III outrun tier 2",
            (t3.ShipPos - t0pos).Length() > (t2.ShipPos - t0pos).Length() * 1.15f);
        Check("space: ion engines III burn least", t3.FuelUsed < t2.FuelUsed);

        // The deflector shield eats the first impact (no hull loss), then the hull pays
        // while it recharges.
        var sh = new Space.SpaceSim { AsteroidTarget = 0, HasShield = true };
        sh.ShipPos = new Vector2(0f, -30000f);
        sh.ShipVel = Vector2.Zero;
        sh.SpawnAsteroid(sh.ShipPos + new Vector2(120f, 0f), new Vector2(-300f, 0f), 20f);
        for (var i = 0; i < 90; i++) sh.Update(dt, 0f, false, false);
        Check("space: shield eats the first impact", sh.Hull == sh.HullMax && sh.ShieldCooldown > 0f,
            $"hull {sh.Hull} cd {sh.ShieldCooldown:0.0}");
        sh.ShipVel = Vector2.Zero;
        sh.HitTimer = 0f;
        sh.SpawnAsteroid(sh.ShipPos + new Vector2(120f, 0f), new Vector2(-300f, 0f), 20f);
        for (var i = 0; i < 90; i++) sh.Update(dt, 0f, false, false);
        Check("space: recharging shield lets the hull pay", sh.Hull == sh.HullMax - 1,
            $"hull {sh.Hull}");
    }

    /// <summary>The upgrade foundry economy: affordability gates, souls + cargo deducted,
    /// purchase recorded, double-buy refused. Uses a scratch MetaSave (never saved).</summary>
    private static void TestFoundry()
    {
        // The dock refinery: raw metals smelt 4:1 with the remainder staying raw; gems and
        // sub-batch amounts pass through untouched.
        var refMeta = new MetaSave();
        refMeta.ShipCargo["iron"] = 11;    // → 2 pure + 3 raw
        refMeta.ShipCargo["gold"] = 4;     // → 1 pure, raw gone
        refMeta.ShipCargo["coal"] = 3;     // sub-batch: stays raw
        refMeta.ShipCargo["ruby"] = 9;     // rare: never refined
        refMeta.RefineCargo();
        Check("refinery: 11 iron smelts to 2 pure + 3 raw",
            refMeta.ShipCargo.GetValueOrDefault("pure_iron") == 2 && refMeta.ShipCargo.GetValueOrDefault("iron") == 3);
        Check("refinery: exact batch leaves no raw",
            refMeta.ShipCargo.GetValueOrDefault("pure_gold") == 1 && !refMeta.ShipCargo.ContainsKey("gold"));
        Check("refinery: sub-batch coal stays raw",
            refMeta.ShipCargo.GetValueOrDefault("coal") == 3 && !refMeta.ShipCargo.ContainsKey("pure_coal"));
        Check("refinery: gems are never refined",
            refMeta.ShipCargo.GetValueOrDefault("ruby") == 9 && !refMeta.ShipCargo.ContainsKey("pure_ruby"));

        var meta = new MetaSave();
        var jet = System.Array.Find(Space.Upgrades.All, u => u.Id == "jetpack")!;   // 1 Kong soul + 2 pure gold + 3 pure iron

        Check("foundry: broke dwarf can't afford", !Space.Upgrades.CanAfford(meta, jet));
        meta.TitanSouls["Kong"] = 1;
        meta.ShipCargo["pure_gold"] = 2;
        meta.ShipCargo["pure_iron"] = 4;
        Check("foundry: souls + cargo afford the jetpack", Space.Upgrades.CanAfford(meta, jet));

        // TryBuy calls meta.Save() — point the write at a scratch profile? MetaSave writes to
        // the real profile dir, so snapshot + restore around the buy.
        var real = MetaSave.Load();
        try
        {
            Check("foundry: purchase succeeds", Space.Upgrades.TryBuy(meta, jet));
        }
        finally
        {
            real.Save();
        }
        Check("foundry: purchase recorded", Space.Upgrades.Owned(meta, "jetpack"));
        Check("foundry: souls spent", meta.TotalSouls() == 0);
        Check("foundry: cargo deducted (pure gold gone, 1 pure iron left)",
            !meta.ShipCargo.ContainsKey("pure_gold") && meta.ShipCargo["pure_iron"] == 1);
        Check("foundry: double-buy refused", !Space.Upgrades.TryBuy(meta, jet));

        // Kind-specific souls: the jetpack wants a Kong soul — a Mecha soul must not do.
        var meta2 = new MetaSave();
        meta2.TitanSouls["Mecha"] = 3;
        meta2.ShipCargo["pure_gold"] = 9; meta2.ShipCargo["pure_iron"] = 99; meta2.ShipCargo["pure_coal"] = 99;
        Check("foundry: wrong-kind soul refused", !Space.Upgrades.CanAfford(meta2, jet));
        meta2.TitanSouls["Kong"] = 1;
        Check("foundry: right-kind soul accepted", Space.Upgrades.CanAfford(meta2, jet));

        // Rovers are repeatable: two buys, two more rovers, kind-agnostic (no soul cost).
        var rover = System.Array.Find(Space.Upgrades.All, u => u.Id == "rover")!;
        var before = meta2.Rovers;
        var real2 = MetaSave.Load();
        try
        {
            Check("foundry: rover buys repeat",
                Space.Upgrades.TryBuy(meta2, rover) && Space.Upgrades.TryBuy(meta2, rover));
        }
        finally
        {
            real2.Save();
        }
        Check("foundry: rover count grew by two", meta2.Rovers == before + 2,
            $"{before} -> {meta2.Rovers}");

        // Tier gating: Jetpack II is locked until the base jetpack is installed, however
        // rich the buyer.
        var jet2 = System.Array.Find(Space.Upgrades.All, u => u.Id == "jetpack2")!;
        var meta3 = new MetaSave();
        meta3.TitanSouls["Kong"] = 9;
        meta3.ShipCargo["ruby"] = 9;
        Check("foundry: tier II locked without tier I", Space.Upgrades.Locked(meta3, jet2)
            && !Space.Upgrades.TryBuy(meta3, jet2));
        Check("foundry: lock spent nothing", meta3.TotalSouls() == 9 && meta3.ShipCargo["ruby"] == 9);
        meta3.ShipUpgrades.Add("jetpack");
        var real3 = MetaSave.Load();
        try
        {
            Check("foundry: tier II unlocks behind tier I", Space.Upgrades.TryBuy(meta3, jet2));
        }
        finally
        {
            real3.Save();
        }

        // The long-range survey finds real deposits (ember is the ruby world) and caches.
        var ember = World.PlanetDefs.ById("ember");
        var t0 = Environment.TickCount64;
        var deposits = Space.Survey.For(ember);
        var genMs = Environment.TickCount64 - t0;
        var hasRuby = false; var hasFuel = false;
        foreach (var (label, n) in deposits)
        {
            if (label == "RUBY" && n > 0) hasRuby = true;
            if (label == "FUEL" && n > 0) hasFuel = true;
        }
        Check("survey: ember shows ruby + fuel deposits", hasRuby && hasFuel,
            string.Join(" ", System.Array.ConvertAll(deposits, d => $"{d.label}:{d.count}")));
        t0 = Environment.TickCount64;
        Space.Survey.For(ember);
        Check("survey: second read is cached (instant)",
            Environment.TickCount64 - t0 < genMs / 2 + 5, $"first {genMs}ms");

        // The warp world: the def exists, its hellworld terrain generates cleanly, and the
        // warp gate demands one shard per ordinary planet.
        var rift = World.PlanetDefs.ById("rift");
        Check("rift: def registered", rift.Id == "rift");
        Check("rift: shards needed = every ordinary world",
            World.PlanetDefs.WarpShardsNeeded == World.PlanetDefs.All.Length - 1);
        var riftDeposits = Space.Survey.For(rift);
        Check("rift: hellworld generates + surveys", riftDeposits.Length > 0,
            string.Join(" ", System.Array.ConvertAll(riftDeposits, d => $"{d.label}:{d.count}")));
        var sim6 = new Space.SpaceSim { AsteroidTarget = 0 };
        var last = sim6.Planets[^1];
        Check("rift: sits far beyond the ordinary orbits",
            last.Def.Id == "rift" && last.OrbitRadius > sim6.Planets[^2].OrbitRadius * 1.5f);

        // Seamless-landing prefetch: the heavy session build must work off the main thread
        // and produce a world with a findable surface and a station parked in orbit above it.
        var pre = System.Threading.Tasks.Task.Run(
            () => DwarfMinerGame.BuildSessionWorld(World.PlanetDefs.ById("verdant"))).GetAwaiter().GetResult();
        Check("prefetch: background session build completes",
            pre.Planet is not null && pre.Cells is not null && pre.Physics is not null);
        var preSpawn = SpawnDirector.FindSurfaceSpawn(pre.Planet, -MathF.PI / 2f, pre.Planet.Radius);
        var stationAlt = (pre.StationPos - pre.Planet.Center).Length()
                         - pre.Planet.Radius * World.Planet.TileSize;
        Check("prefetch: station parks at orbit altitude above the spawn",
            MathF.Abs(stationAlt - Session.OrbitAltitude) < 1f
            && !pre.Planet.IsSolidAt(pre.StationPos),
            $"alt {stationAlt:0} spawn {preSpawn.Length():0}");
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
