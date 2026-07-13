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
        var player = new Player(planet.Center + new Vector2(0, -(planet.SurfaceRing + Planet.RingMin) * Planet.TileSize));

        const float dt = 1f / 60f;

        // --- 1. Collision: drop walkers into caves at many angles; none may end up in rock.
        var embedded = 0;
        var tested = 0;
        foreach (var kind in new[] { CreatureKind.Grub, CreatureKind.Skitterer, CreatureKind.Grazer, CreatureKind.CaveEye,
                                     CreatureKind.SporeBat, CreatureKind.CrystalCrawler, CreatureKind.VoidWraith,
                                     CreatureKind.CaveSlime, CreatureKind.Slimelet, CreatureKind.AcidSpitter,
                                     CreatureKind.BomberBeetle, CreatureKind.SnapperVine, CreatureKind.RockMimic,
                                     CreatureKind.Moonlet, CreatureKind.VacLeech, CreatureKind.Glimmermaw,
                                     CreatureKind.StarJelly, CreatureKind.VoidBarnacle,
                                     CreatureKind.Selenite, CreatureKind.DustDevil })
        {
            for (var i = 0; i < 25; i++)
            {
                var pos = FindCavePos(planet, seedOffset: i * 37 + (int)kind * 101);
                if (pos is not { } p) continue;
                tested++;
                var c = new Creature(p, kind);
                for (var step = 0; step < 60 * 8; step++)
                    c.Update(dt, planet, physics, cells, player);
                if (EmbeddedInRock(planet, c.Position)) embedded++;
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
            Check($"digging: {kind} not embedded in rock", !EmbeddedInRock(planet, c.Position));
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
                var launch = pPlanet.Center + new Vector2(0, (Planet.RingMin + pPlanet.SurfaceRing + 32) * Planet.TileSize);
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
                // Civilians are the deliberate exception: gunning down a citizen pays
                // nothing — that kill was on you.
                if (kind == CreatureKind.Civilian) continue;
                var drops = Corpse.DropsFor(kind);
                var total = 0;
                foreach (var (_, count) in drops) total += count;
                Check($"corpse: {kind} yields materials", drops.Length > 0 && total > 0);
            }
            Check("corpse: civilian deliberately yields nothing",
                Corpse.DropsFor(CreatureKind.Civilian).Length == 0);

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
        TestSkyCollapse();
        TestCompaction();
        TestGemDrops();
        TestVolcanoes();
        TestCities();
        TestCityDefense();
        TestAquatics();
        TestPopulateWorld();
        TestSpaceSim();
        // These two run last on purpose: both Activate a chain (appending the Hollow +
        // debug rig to PlanetDefs.All), and there is no way to un-append.
        TestMoons();
        TestHollow();

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
            var sfc = run.Planet.SurfaceRing;
            for (var r = sfc - 9; r < sfc; r++) run.Planet.Set(r, 10, TileKind.Sky);
            run.Planet.Mine(sfc - 10, 10, 1);
            run.Cells.FillTile(sfc - 4, 10, Material.Water);

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
        foreach (var kind in new[]
        {
            TitanKind.Godzilla, TitanKind.Mecha, TitanKind.Sandworm, TitanKind.Kong,
            TitanKind.Knifehead, TitanKind.Otachi, TitanKind.Leatherback, TitanKind.Raiju,
            TitanKind.Slattern, TitanKind.Pyrodactyl, TitanKind.Vitriodactyl,
        })
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

            bool sawShot = false, sawShock = false, sawEmp = false;
            for (var i = 0; i < 60 * 16; i++)
            {
                titan.OnDamage();   // keep it aggroed
                titan.Update(dt, p, phys, c, player.Position, bo, sh);
                if (sh.Count > 0) sawShot = true;
                if (titan.PendingShockwave is not null) { sawShock = true; titan.PendingShockwave = null; }
                if (titan.PendingEmp is not null) { sawEmp = true; titan.PendingEmp = null; }
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
                case TitanKind.Knifehead:
                    Check("titan: Knifehead's gore charge connects (shockwave)", sawShock);
                    break;
                case TitanKind.Otachi:
                    Check("titan: Otachi sprays acid globs", sawShot);
                    break;
                case TitanKind.Leatherback:
                    Check("titan: Leatherback detonates an EMP", sawEmp);
                    break;
                case TitanKind.Raiju:
                    Check("titan: Raiju's dash chain clips the player (shockwave)", sawShock);
                    break;
                case TitanKind.Slattern:
                    Check("titan: Slattern flings tail-spike barrages", sawShot);
                    Check("titan: Slattern's sonic pulse lands (shockwave)", sawShock);
                    break;
                case TitanKind.Pyrodactyl:
                    Check("titan: Pyrodactyl flies legless", titan.Legs.Length == 0);
                    Check("titan: Pyrodactyl rains lava on a bombing run", sawShot);
                    break;
                case TitanKind.Vitriodactyl:
                    Check("titan: Vitriodactyl rains acid on a bombing run", sawShot);
                    break;
            }
        }

        // --- Procedural campaigns: 7 generated worlds + the Rift finale ---
        {
            var chainA = PlanetGen.Campaign(1234);
            var chainB = PlanetGen.Campaign(1234);
            var chainC = PlanetGen.Campaign(99);
            Check("plangen: campaign = 7 worlds + the Rift + the cratered moon",
                chainA.Length == 9 && chainA[7].Id == "rift" && chainA[8].Id == "moon");
            var same = true;
            for (var i = 0; i < 9; i++) same &= chainA[i].Id == chainB[i].Id && chainA[i].Titan == chainB[i].Titan;
            Check("plangen: same seed = same system", same);
            var diff = false;
            for (var i = 0; i < 7; i++) diff |= chainA[i].Id != chainC[i].Id;
            Check("plangen: new seed = new system", diff);
            Check("plangen: size ramps with difficulty (0.7 to 1.8)",
                chainA[0].SizeScale < chainA[6].SizeScale
                && chainA[0].SizeScale >= 0.69f && chainA[6].SizeScale <= 1.81f);
            var kinds = new System.Collections.Generic.HashSet<TitanKind>();
            for (var i = 0; i < 7; i++) kinds.Add(chainA[i].Titan);
            Check("plangen: the four classic soul kinds stay farmable",
                kinds.Contains(TitanKind.Kong) && kinds.Contains(TitanKind.Sandworm)
                && kinds.Contains(TitanKind.Godzilla) && kinds.Contains(TitanKind.Mecha));
            var hasOcean = false; var hasAcid = false;
            foreach (var d in chainA) { hasOcean |= d.LakeScale > 2f; hasAcid |= d.AcidRain; }
            Check("plangen: every campaign has an ocean world and an acid world", hasOcean && hasAcid);

            // Ocean world actually reads as mostly water: most surface tiles sit over a
            // carved basin. Count water seeds vs a plain world's.
            PlanetDef ocean = null!, acidWorld = null!;
            foreach (var d in chainA) { if (d.LakeScale > 2f && ocean is null) ocean = d; if (d.AcidRain && acidWorld is null) acidWorld = d; }
            var oceanWorld = WorldGen.Generate(7, ocean);
            var plainWorld = WorldGen.Generate(7, PlanetDefs.ById("verdant"));
            Check($"plangen: ocean world is mostly water ({oceanWorld.WaterSeeds.Count} seeds vs {plainWorld.WaterSeeds.Count})",
                oceanWorld.WaterSeeds.Count > plainWorld.WaterSeeds.Count * 3);
            var acidGen = WorldGen.Generate(8, acidWorld);
            Check($"plangen: acid world carves surface acid pools ({acidGen.AcidSeeds.Count} seeds)",
                acidGen.AcidSeeds.Count > 0);
            // Every acid reservoir is skinned in obsidian: no acid seed borders a corrodible
            // tile, so the pools can't eat outward through the crust — and the hemmed-in acid
            // settles and sleeps instead of melting the whole world (the acid-world lag fix).
            var leaky = 0;
            foreach (var (r, t) in acidGen.AcidSeeds)
            {
                var n = acidGen.TilesAt(r);
                var nb = new List<(int x, int y)>
                {
                    (r, ((t - 1) % n + n) % n), (r, (t + 1) % n), acidGen.InnerNeighbour(r, t),
                };
                for (var i = 0; i < acidGen.OuterNeighbourCount(r, t); i++)
                    nb.Add(acidGen.OuterNeighbour(r, t, i));
                foreach (var (nr, nt) in nb)
                {
                    var k = acidGen.Get(nr, nt);
                    if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian)
                    {
                        leaky++;
                        break;
                    }
                }
            }
            Check($"plangen: acid pools are obsidian-lined ({leaky}/{acidGen.AcidSeeds.Count} seeds touch corrodible rock)",
                leaky == 0);
            // Size actually changes the tile grid.
            Check($"plangen: SizeScale drives real ring counts ({oceanWorld.Rings} vs {plainWorld.Rings})",
                WorldGen.Generate(9, chainA[6]).Rings > WorldGen.Generate(9, chainA[0]).Rings);

            // The metropolis always draws a walking titan (the worm/flyer swaps elsewhere),
            // and the swap never costs the campaign its farmable Sandworm. Sweep seeds —
            // the guarantee is a per-roll swap, not a one-seed accident.
            var cityWalks = true; var wormStays = true;
            for (var seed = 0; seed < 200; seed++)
            {
                var chain = PlanetGen.Campaign(seed);
                var sawWorm = false;
                for (var i = 0; i < 7; i++)
                {
                    if (chain[i].Biome == "city")
                        cityWalks &= chain[i].Titan is not (TitanKind.Sandworm
                            or TitanKind.Pyrodactyl or TitanKind.Vitriodactyl);
                    sawWorm |= chain[i].Titan == TitanKind.Sandworm;
                }
                wormStays &= sawWorm;
            }
            Check("plangen: city worlds always get a walking titan (200 seeds)", cityWalks);
            Check("plangen: the worm swap keeps Shai-Hulud farmable (200 seeds)", wormStays);
        }

        // --- Planet mapping wires distinct bosses ---
        Check("titan: ember hatches the fire-breather",
            PlanetDefs.ById("ember").Titan == TitanKind.Godzilla);
        Check("titan: slag hatches the mecha",
            PlanetDefs.ById("slag").Titan == TitanKind.Mecha);
        Check("titan: the Rift hatches the category-5 apex",
            PlanetDefs.ById("rift").Titan == TitanKind.Slattern);
        Check("titan: Coreheart rolls from the kaiju pool",
            PlanetDefs.ById("core").TitanPool is { Length: 4 });

        // --- Terrain plow: an aggroed boss smashes rock blocking it at body height, but the
        // ground under it is footing, not demolition fodder (it used to pulverise every tile
        // beneath itself the moment it hatched) ---
        {
            var pp = WorldGen.Generate(70);
            var pc = new Cells(pp);
            var pphys = new Physics(pp, pc);
            var psh = new System.Collections.Generic.List<TitanProjectile>();
            var pbo = new System.Collections.Generic.List<FallingBoulder>();
            var boss = new Titan(pp, -MathF.PI / 2f, TitanKind.Kong);
            boss.Hatch();   // hatch = aggroed, so the plow runs at full power
            // Let the hatch transient finish first: the freshly hatched body pops tens of px
            // up to ride height over the first ticks, which would carry it away from any
            // slab placed around the pre-hatch position.
            for (var i = 0; i < 30; i++)
                boss.Update(1f / 60f, pp, pphys, pc, boss.Position, pbo, psh);
            // A stone slab at body height (16-32px up, inside the plow radius) and a stone
            // row well below the body centre (-40px — inside the protected floor sector),
            // then tick once. Offsets are world px so the geometry is tile-size-independent;
            // tiles are placed through world coordinates — ring tile counts differ, so
            // reusing one angular index across rings would drift the slab away from the body.
            var bup = pp.UpAt(boss.Position);
            var bright = new Vector2(-bup.Y, bup.X);
            var slab = new System.Collections.Generic.List<(int x, int y)>();
            var floor = new System.Collections.Generic.List<(int x, int y)>();
            for (var dyPx = -8; dyPx <= 8; dyPx += Planet.TileSize)
            {
                for (var dxPx = 16; dxPx <= 32; dxPx += Planet.TileSize)
                    slab.Add(pp.WorldToTile(boss.Position + bup * dxPx + bright * dyPx));
                floor.Add(pp.WorldToTile(boss.Position - bup * 40f + bright * dyPx));
            }
            foreach (var (x, y) in slab) pp.Set(x, y, TileKind.Stone);
            foreach (var (x, y) in floor) pp.Set(x, y, TileKind.Stone);
            int CountSlab()
            {
                var n = 0;
                foreach (var (x, y) in slab) if (Tiles.IsSolid(pp.Get(x, y))) n++;
                return n;
            }
            int CountFloor()
            {
                var n = 0;
                foreach (var (x, y) in floor) if (Tiles.IsSolid(pp.Get(x, y))) n++;
                return n;
            }
            var slabBefore = CountSlab();
            var floorBefore = CountFloor();
            boss.Update(1f / 60f, pp, pphys, pc, boss.Position, pbo, psh);
            Check($"titan: boss plows rock blocking it at body height ({slabBefore}→{CountSlab()})",
                CountSlab() < slabBefore);
            Check($"titan: the floor under the boss survives the plow ({floorBefore}→{CountFloor()})",
                CountFloor() == floorBefore);
        }

        // --- Dig-down hunt: an enraged boss stomps a shaft toward a player deep below it,
        // then bursts back up through the overburden when the player gets above it ---
        {
            var pp = WorldGen.Generate(70);
            var pc = new Cells(pp);
            var pphys = new Physics(pp, pc);
            var psh = new System.Collections.Generic.List<TitanProjectile>();
            var pbo = new System.Collections.Generic.List<FallingBoulder>();
            var boss = new Titan(pp, -MathF.PI / 2f, TitanKind.Godzilla);
            boss.Hatch();
            // Settle onto its feet, then hold it enraged with the player 420px straight down.
            for (var i = 0; i < 180; i++) boss.Update(dt, pp, pphys, pc, boss.Position, pbo, psh);
            var digPlayer = boss.Position - pp.UpAt(boss.Position) * 420f;
            var startRadial = (boss.Position - pp.Center).Length();
            for (var i = 0; i < 60 * 30; i++)
            {
                boss.OnDamage();
                boss.Anger = 90f;   // hold it past DigAngerGate (UpdateAnger lerps slowly)
                boss.Update(dt, pp, pphys, pc, digPlayer, pbo, psh);
            }
            var dugRadial = (boss.Position - pp.Center).Length();
            Check($"titan: enraged boss stomps a shaft down toward buried prey ({startRadial - dugRadial:0}px)",
                startRadial - dugRadial > 120f);

            // Prey escapes upward — the boss should climb back toward the surface.
            var upPlayer = boss.Position + pp.UpAt(boss.Position) * 500f;
            for (var i = 0; i < 60 * 20; i++)
            {
                boss.OnDamage();
                boss.Anger = 90f;
                boss.Update(dt, pp, pphys, pc, upPlayer, pbo, psh);
            }
            var rose = (boss.Position - pp.Center).Length() - dugRadial;
            Check($"titan: boss bursts back up when the prey gets above it ({rose:0}px)", rose > 120f);
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
        // Fully hand-carve a sealed chimney: per-ring tile counts skew a constant-index
        // column in angle, so the shaft needs width and its own walls rather than trusting
        // whatever terrain the seed put beside it.
        int gr = 100, gt = 50;
        for (var dr = -1; dr <= 7; dr++)
            for (var da = -2; da <= 4; da++)
                planet.Set(gr + dr, gt + da,
                    dr is -1 or 7 || da is -2 or 4 ? TileKind.Stone : TileKind.Sky);
        cells.FillTile(gr, gt + 1, Material.Gas);
        for (var i = 0; i < 60; i++) cells.Update(dt);
        var risen = 0;
        for (var dr = 2; dr <= 6; dr++)
            for (var da = -1; da <= 3; da++)
                risen += CountMatInTile(cells, gr + dr, gt + da, Material.Gas);
        Check("hazard: gas rises through a shaft", risen > 0);

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
        Check("hazard: acid melts granite but obsidian resists", DissolveHardRockStays());

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

    /// <summary>The buffed corrosion rule: acid melts through most materials — granite
    /// included — but obsidian resists, keeping the deep crust as the hard bound so a
    /// spill can't chew an open tunnel all the way to the core.</summary>
    private static bool DissolveHardRockStays()
    {
        var planet = WorldGen.Generate(101);
        var cells = new Cells(planet);
        int r = 118, t = 40;
        planet.Set(r, t, TileKind.Granite);
        planet.Set(r + 1, t, TileKind.Sky);
        cells.FillTile(r + 1, t, Material.Acid);
        var graniteMelted = false;
        for (var i = 0; i < 3000 && !graniteMelted; i++)
        {
            cells.Update(1f / 60f);
            if (planet.Get(r, t) != TileKind.Granite) graniteMelted = true;
        }

        var planet2 = WorldGen.Generate(102);
        var cells2 = new Cells(planet2);
        planet2.Set(r, t, TileKind.Obsidian);
        planet2.Set(r + 1, t, TileKind.Sky);
        cells2.FillTile(r + 1, t, Material.Acid);
        for (var i = 0; i < 1500; i++) cells2.Update(1f / 60f);
        return graniteMelted && planet2.Get(r, t) == TileKind.Obsidian;
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

    /// <summary>Above-crust support rule: a floating slab in open sky far bigger than the
    /// underground collapse budget must still fall (nothing behind it to infer support
    /// from), while the same slab hung in an underground pocket stays put — the budget
    /// valve keeps inferring backdrop support below the crust.</summary>
    private static void TestSkyCollapse()
    {
        var planet = WorldGen.Generate(9);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        const float dt = 1f / 60f;
        const int slabH = 16, slabW = 32;   // 512 tiles (same world size as ever) — past the stone budget of 192

        // Find a stretch of open sky above the surface (clear of mountains/volcanoes).
        var ring0 = planet.SurfaceRing + 20;
        var ang0 = -1;
        var n = planet.TilesAt(ring0);
        for (var t = 0; t < n && ang0 < 0; t++)
        {
            var clear = true;
            for (var dr = -2; dr <= slabH + 1 && clear; dr++)
                for (var da = -2; da <= slabW + 1 && clear; da++)
                    clear = planet.Get(ring0 + dr, t + da) == TileKind.Sky;
            if (clear) ang0 = t;
        }
        Check("sky-collapse: open-sky test site found", ang0 >= 0);
        if (ang0 < 0) return;

        var slab = new List<(int x, int y)>();
        for (var dr = 0; dr < slabH; dr++)
            for (var da = 0; da < slabW; da++)
            {
                planet.Set(ring0 + dr, ang0 + da, TileKind.Stone);
                slab.Add((ring0 + dr, ang0 + da));
            }
        foreach (var (x, y) in slab) physics.MarkDirty(x, y);
        for (var i = 0; i < 300; i++) physics.Update(dt);
        Check("sky-collapse: oversized airborne slab crumbles (no backdrop above the crust)",
            slab.TrueForAll(t => !Tiles.IsSolid(planet.Get(t.x, t.y))));

        // Control: the same slab in a carved pocket underground is saved by the budget valve.
        var uRing = planet.SurfaceRing - 40;
        const int uAng = 60;
        for (var dr = -2; dr <= slabH + 1; dr++)
            for (var da = -2; da <= slabW + 1; da++)
                planet.Set(uRing + dr, uAng + da, TileKind.Sky);
        var buried = new List<(int x, int y)>();
        for (var dr = 0; dr < slabH; dr++)
            for (var da = 0; da < slabW; da++)
            {
                planet.Set(uRing + dr, uAng + da, TileKind.Stone);
                buried.Add((uRing + dr, uAng + da));
            }
        foreach (var (x, y) in buried) physics.MarkDirty(x, y);
        for (var i = 0; i < 300; i++) physics.Update(dt);
        Check("sky-collapse: same slab underground stays (backdrop support inferred)",
            buried.TrueForAll(t => Tiles.IsSolid(planet.Get(t.x, t.y))));
    }

    /// <summary>Compaction: buried, undisturbed grains press into a solid tile of whatever
    /// kind the majority of them came from — stone dust re-forms stone, sand beds into
    /// gravel — while crests stay loose. (Composition-backed Conglomerate is legacy-only.)</summary>
    private static void TestCompaction()
    {
        var planet = WorldGen.Generate(11);
        var cells = new Cells(planet);
        const float dt = 1f / 60f;

        // A sealed one-tile pocket deep in stone, packed full of stone dust: solid floor
        // below, solid roof above (buried), nothing moving — the compaction sweep's second
        // look ~45s later presses it into a Conglomerate.
        var r = planet.SurfaceRing - 60;
        const int t = 33;
        for (var dr = -1; dr <= 1; dr++)
            for (var da = -1; da <= 1; da++)
                planet.Set(r + dr, t + da, TileKind.Stone);
        planet.Set(r, t, TileKind.Sky);
        for (var dy = 0; dy < Cells.Density; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                cells.Place(t * Cells.Density + dx, r * Cells.Density + dy, Material.Dust, TileKind.Stone);

        for (var i = 0; i < 60 * 50 && planet.Get(r, t) != TileKind.Stone; i++)
            cells.Update(dt);
        Check("compaction: buried undisturbed stone dust re-forms into stone",
            planet.Get(r, t) == TileKind.Stone);

        // A sealed two-tile-tall pocket: only the bottom tile rests on solid at first, so
        // it converts alone; converting must re-nominate the tile above so the pile keeps
        // forming blocks layer by layer instead of stopping at the bottom row.
        var r2 = planet.SurfaceRing - 70;
        const int t2 = 55;
        for (var dr = -1; dr <= 2; dr++)
            for (var da = -1; da <= 1; da++)
                planet.Set(r2 + dr, t2 + da, TileKind.Stone);
        planet.Set(r2, t2, TileKind.Sky);
        planet.Set(r2 + 1, t2, TileKind.Sky);
        for (var dy = 0; dy < Cells.Density * 2; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                cells.Place(t2 * Cells.Density + dx, r2 * Cells.Density + dy, Material.Dust, TileKind.Stone);

        for (var i = 0; i < 60 * 100 && planet.Get(r2 + 1, t2) != TileKind.Stone; i++)
            cells.Update(dt);
        Check("compaction: stacked pile keeps converting layer by layer",
            planet.Get(r2, t2) == TileKind.Stone
            && planet.Get(r2 + 1, t2) == TileKind.Stone);

        // A naturally-settled-style column in a stone shaft: the bottom tile has craggy
        // voids (12/16) and the crest is a loose dusting (6/16). The pressed rules must
        // still harden the weighed-down layers — pulling grains down to fill the voids —
        // while the under-crest layer and crest stay loose sand ("not enough sand").
        var r3 = planet.SurfaceRing - 80;
        const int t3 = 77;
        for (var dr = -1; dr <= 4; dr++)
            for (var da = -1; da <= 1; da++)
                planet.Set(r3 + dr, t3 + da, TileKind.Stone);
        for (var dr = 0; dr <= 3; dr++)
            planet.Set(r3 + dr, t3, TileKind.Sky);
        planet.Set(r3 + 4, t3, TileKind.Sky);   // open headroom above the pile
        var grains = 0;
        for (var dy = 0; dy < Cells.Density * 4; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
            {
                var voidCell = dy switch
                {
                    < Cells.Density => (dx + dy) % 4 == 1,          // bottom tile: 12/16
                    >= Cells.Density * 3 => dy % Cells.Density > 0
                        || dx is 0 or 2,                            // crest: 2/16 after settle
                    _ => false,                                     // middle tiles: full
                };
                if (voidCell) continue;
                cells.Place(t3 * Cells.Density + dx, r3 * Cells.Density + dy, Material.Sand);
                grains++;
            }
        for (var i = 0; i < 60 * 150 && planet.Get(r3 + 1, t3) != TileKind.Gravel; i++)
            cells.Update(dt);
        // Grain accounting is approximate now (a compacted tile is a plain tile, not a
        // stored composition), so assert the shape of the outcome: pressed layers hardened
        // into gravel (sand's granular rock), loose sand still present above them, and the
        // pile actually consumed grains (some loose sand gone).
        var looseSand = 0;
        for (var dr = 0; dr <= 4; dr++)
            for (var dy = 0; dy < Cells.Density; dy++)
                for (var dx = 0; dx < Cells.Density; dx++)
                    if (cells.Get(t3 * Cells.Density + dx, (r3 + dr) * Cells.Density + dy) == Material.Sand)
                        looseSand++;
        Check("compaction: voided pile hardens under pressure, crest stays sand",
            planet.Get(r3, t3) == TileKind.Gravel
            && planet.Get(r3 + 1, t3) == TileKind.Gravel
            && planet.Get(r3 + 3, t3) == TileKind.Sky);
        Check($"compaction: crest sand survives, hardened layers ate the rest ({looseSand} loose of {grains})",
            looseSand > 0 && looseSand < grains);

        // The re-formed tile is a real tile of its kind — mines like stone, drops like stone.
        Check("compaction: re-formed stone mines as stone",
            planet.Mine(r, t, 99) == TileKind.Stone);
    }

    /// <summary>Gem tiles don't crumble to dust: shattering one queues a physical pickup
    /// site (drained into Session.Pickups by Game1) and spawns no cells.</summary>
    private static void TestGemDrops()
    {
        var planet = WorldGen.Generate(13);
        var cells = new Cells(planet);
        var r = planet.SurfaceRing - 40;
        const int t = 21;
        planet.Set(r, t, TileKind.Ruby);

        planet.Set(r, t, TileKind.Sky);
        cells.SpawnDustInTile(r, t, TileKind.Ruby);
        var dust = 0;
        for (var dy = 0; dy < Cells.Density; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                if (cells.Get(t * Cells.Density + dx, r * Cells.Density + dy) != Material.Empty)
                    dust++;
        Check("gems: shattered ruby spawns no dust cells", dust == 0);
        Check("gems: shattered ruby queues one physical drop site",
            cells.PendingGemDrops.Count == 1 && cells.PendingGemDrops[0].kind == TileKind.Ruby);
        Check("gems: ordinary stone still dusts", StoneStillDusts(planet, cells));
    }

    private static bool StoneStillDusts(Planet planet, Cells cells)
    {
        var r = planet.SurfaceRing - 40;
        const int t = 25;
        planet.Set(r, t, TileKind.Sky);
        cells.SpawnDustInTile(r, t, TileKind.Stone);
        var dust = 0;
        for (var dy = 0; dy < Cells.Density; dy++)
            for (var dx = 0; dx < Cells.Density; dx++)
                if (cells.Get(t * Cells.Density + dx, r * Cells.Density + dy) == Material.Dust)
                    dust++;
        return dust == Cells.DustCellsPerTile;
    }

    /// <summary>Volcanoes: fire worlds raise lava-primed cones whose plumbing reaches a deep
    /// chamber; the acid flag reroutes the fluid to the acid seed channel.</summary>
    private static void TestVolcanoes()
    {
        var ember = WorldGen.Generate(5, PlanetDefs.ById("ember"));
        Check($"volcano: ember world raises primed volcanoes ({ember.VolcanoVents.Count} vents, "
            + $"{ember.LavaSeeds.Count} lava sites)",
            ember.VolcanoVents.Count >= 1 && ember.LavaSeeds.Count > 0);
        var deep = false;
        foreach (var (x, _) in ember.LavaSeeds) deep |= x < ember.SurfaceRing - 80;
        Check("volcano: throat is primed down to a deep magma chamber", deep);
        var aboveSurface = false;
        foreach (var (x, _) in ember.LavaSeeds) aboveSurface |= x > ember.SurfaceRing;
        Check("volcano: crater pool sits above the surface", aboveSurface);

        var acidDef = PlanetDefs.ById("ember") with { VolcanoAcid = true };
        var acidWorld = WorldGen.Generate(6, acidDef);
        var acidVent = false;
        foreach (var v in acidWorld.VolcanoVents) acidVent |= v.acid;
        Check("volcano: acid volcanoes vent acid and seed the acid channel",
            acidVent && acidWorld.LavaSeeds.Count == 0 && acidWorld.AcidSeeds.Count > 0);
    }

    /// <summary>City worlds and lizard warrens, and the wall between them: towers rise in
    /// clustered districts with civilian addresses recorded and NO warren underneath; warren
    /// worlds (slag here) carve brick halls above the lava line with dens recorded; and
    /// campaigns always roll exactly one metropolis plus at least one warren world, never
    /// both civilisations on the same def.</summary>
    private static void TestCities()
    {
        var city = WorldGen.Generate(11, PlanetDefs.ById("city"));
        int alloy = 0, glass = 0, cityBrick = 0;
        foreach (var (x, y) in city.AllTiles())
        {
            var k = city.Get(x, y);
            if (k == TileKind.AlienAlloy) alloy++;
            else if (k == TileKind.CityGlass) glass++;
            else if (k == TileKind.LizardBrick) cityBrick++;
        }
        Check($"city: skyscrapers stand (alloy {alloy}, glass {glass})", alloy > 800 && glass > 80);
        Check($"city: civilian addresses recorded ({city.CitySpawns.Count})", city.CitySpawns.Count >= 8);
        var openHomes = 0;
        foreach (var (r, t) in city.CitySpawns)
            if (!Tiles.BlocksPlayer(city.Get(r, t))) openHomes++;
        Check($"city: civilian addresses are open air ({openHomes}/{city.CitySpawns.Count})",
            openHomes == city.CitySpawns.Count);
        var towersAboveSurface = false;
        foreach (var (x, y) in city.AllTiles())
            if (x > city.SurfaceRing + 20 && city.Get(x, y) == TileKind.AlienAlloy)
            { towersAboveSurface = true; break; }
        Check("city: towers rise well above the surface", towersAboveSurface);
        Check($"city: NO lizard warren under the metropolis (brick {cityBrick}, dens {city.LizardDens.Count})",
            cityBrick == 0 && city.LizardDens.Count == 0);

        // Clustering: gather distinct tower bearings from the spawn addresses; most towers
        // must have a district neighbour within ~0.14 rad (a lone spire has none).
        {
            var bearings = new List<float>();
            foreach (var (r, t) in city.CitySpawns)
            {
                var rel = city.TileToWorld(r, t) - city.Center;
                var a = MathF.Atan2(rel.Y, rel.X);
                var known = false;
                foreach (var b in bearings)
                    if (MathF.Abs(MathHelper.WrapAngle(a - b)) < 0.02f) { known = true; break; }
                if (!known) bearings.Add(a);
            }
            var clustered = 0;
            foreach (var a in bearings)
            {
                foreach (var b in bearings)
                {
                    if (a == b) continue;
                    if (MathF.Abs(MathHelper.WrapAngle(a - b)) < 0.14f) { clustered++; break; }
                }
            }
            Check($"city: towers stand in districts ({clustered}/{bearings.Count} have a close neighbour)",
                bearings.Count >= 8 && clustered >= bearings.Count * 7 / 10);

            // Capital layout: split the sorted bearings into groups at gaps > 0.25 rad —
            // there must be one dominant metropolis plus one or two smaller towns.
            bearings.Sort();
            var groups = new List<int>();
            var size = 1;
            for (var i = 1; i < bearings.Count; i++)
            {
                if (bearings[i] - bearings[i - 1] > 0.25f) { groups.Add(size); size = 1; }
                else size++;
            }
            // The list wraps: merge the last run into the first when the seam gap is small.
            if (groups.Count > 0
                && bearings[0] + MathHelper.TwoPi - bearings[^1] <= 0.25f)
            {
                groups[0] += size;
            }
            else
            {
                groups.Add(size);
            }
            groups.Sort();
            Check($"city: one capital plus smaller towns (groups {string.Join("/", groups)})",
                groups.Count is >= 2 and <= 3 && groups[^1] >= bearings.Count * 45 / 100);
        }

        // City coverage: at one ring above the streets, the alloy-backed footprint (towers,
        // door gaps included via the wall layer) plus the narrow streets between them must
        // span roughly a third of the planet's circumference.
        {
            var r6 = city.SurfaceRing + 6;
            var n = city.TilesAt(r6);
            var covered = 0;
            var angles = new List<float>();
            for (var t = 0; t < n; t++)
            {
                if (city.GetWall(r6, t) != TileKind.AlienAlloy) continue;
                covered++;
                angles.Add((t + 0.5f) / n * MathHelper.TwoPi);
            }
            var builtFrac = covered / (float)n;
            // District span = built tiles plus street gaps under ~0.05 rad between them.
            angles.Sort();
            var span = 0f;
            for (var i = 0; i < angles.Count; i++)
            {
                var gap = angles[(i + 1) % angles.Count] - angles[i];
                if (gap < 0) gap += MathHelper.TwoPi;
                span += MathF.Min(gap, 0.05f);
            }
            var spanFrac = span / MathHelper.TwoPi;
            Check($"city: districts span about a third of the surface (built {builtFrac:P0}, with streets {spanFrac:P0})",
                builtFrac > 0.15f && spanFrac > 0.25f);
        }

        // Warren world: ember (the lava homeland) carries the buried lizard city now — the
        // warrens live only under acid and lava worlds.
        var emberDef = PlanetDefs.ById("ember");
        var warren = WorldGen.Generate(12, emberDef);
        var brick = 0;
        foreach (var (x, y) in warren.AllTiles())
            if (warren.Get(x, y) == TileKind.LizardBrick) brick++;
        Check($"warren: lizard city carved on the lava world (brick {brick})", brick > 260);
        Check($"warren: dens recorded ({warren.LizardDens.Count})", warren.LizardDens.Count >= 4);
        Check("warren: no alien towers on the warren world",
            warren.CitySpawns.Count == 0 && emberDef.CityLots == 0);
        var openDens = 0;
        var lavaTop = (int)(warren.Radius * emberDef.LavaFillFrac) - Planet.RingMin;
        var allAboveLava = true;
        foreach (var (r, t) in warren.LizardDens)
        {
            if (!Tiles.BlocksPlayer(warren.Get(r, t))) openDens++;
            allAboveLava &= r > lavaTop;
        }
        Check($"warren: den hearts are open hall air ({openDens}/{warren.LizardDens.Count})",
            openDens == warren.LizardDens.Count);
        Check("warren: every hall sits above the lava flood line", allAboveLava);
        var gold = 0;
        foreach (var (x, y) in warren.AllTiles())
            if (warren.Get(x, y) == TileKind.GoldOre) gold++;
        Check($"warren: vault hoard seeded ({gold} gold tiles)", gold > 0);

        // Lizardman guard: drop one in a warren hall with the dwarf beside it; it must fight
        // (spear casts and/or contact pressure) without ending the fight embedded in a wall.
        var (dr, dt) = warren.LizardDens[0];
        var denPos = warren.TileToWorld(dr, dt);
        var wCells = new Cells(warren);
        var wPhysics = new Physics(warren, wCells);
        var prey = new Player(denPos + new Vector2(30f, 0f));
        var guard = new Creature(denPos, CreatureKind.Lizardman);
        var shots = new List<TitanProjectile>();
        for (var step = 0; step < 60 * 8; step++)
            guard.Update(1f / 60f, warren, wPhysics, wCells, prey, shots);
        Check("warren: guard is not embedded after 8s of combat", !EmbeddedInRock(warren, guard.Position));
        Check($"warren: guard fought back ({shots.Count} spears, prey hp {prey.Health:0})",
            shots.Count > 0 || prey.Health < 100f);

        // Civilian: ambles the city surface without ending up inside a tower wall.
        var home = city.CitySpawns[0];
        var civPos = city.TileToWorld(home.x, home.y);
        var cCells = new Cells(city);
        var cPhysics = new Physics(city, cCells);
        var civ = new Creature(civPos, CreatureKind.Civilian);
        var farPlayer = new Player(civPos + new Vector2(500f, 0f));
        for (var step = 0; step < 60 * 8; step++)
            civ.Update(1f / 60f, city, cPhysics, cCells, farPlayer);
        Check("city: civilian not embedded after 8s", !EmbeddedInRock(city, civ.Position));

        // Campaigns: exactly one metropolis, at least one warren world, warrens only under
        // acid/lava biomes, and never both civilisations on the same planet — several seeds.
        for (var seed = 1234; seed < 1237; seed++)
        {
            var chain = PlanetGen.Campaign(seed);
            var cities = 0;
            var warrens = 0;
            var mixed = 0;
            var misplaced = 0;
            foreach (var d in chain)
            {
                if (d.Biome == "city") cities++;
                if (d.LizardCities > 0) warrens++;
                if (d.CityLots > 0 && d.LizardCities > 0) mixed++;
                if (d.LizardCities > 0 && d.Biome != "acid" && d.Biome != "ember") misplaced++;
            }
            Check($"campaign {seed}: 1 metropolis, {warrens} warren worlds (acid/lava only), never mixed",
                cities == 1 && warrens >= 1 && mixed == 0 && misplaced == 0);
        }
    }

    /// <summary>The city's defences and resistances: architecture shrugs off explosions and
    /// creature jaws, diggers only hunt when provoked, militia bolts spare neutrals and don't
    /// aggro the titan, and a titan's wrecking bite fells a wall slowly rather than instantly.</summary>
    private static void TestCityDefense()
    {
        const float dt = 1f / 60f;
        var planet = WorldGen.Generate(77);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);

        // Stamp a free-standing alloy block on the surface at a bearing clear of the spawn.
        void StampBlock(float ang, TileKind kind, int wide, int tall, out List<(int r, int t)> tiles)
        {
            tiles = new List<(int r, int t)>();
            for (var dr = 0; dr < tall; dr++)
            {
                var r = planet.SurfaceRing + 1 + dr;
                var n = planet.TilesAt(r);
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                for (var dtc = -wide; dtc <= wide; dtc++)
                {
                    var t = ((t0 + dtc) % n + n) % n;
                    planet.Set(r, t, kind);
                    planet.SetWall(r, t, kind);
                    tiles.Add((r, t));
                }
            }
        }

        // --- 1. Explosions chip city architecture instead of levelling it ---
        {
            StampBlock(0.6f, TileKind.AlienAlloy, wide: 4, tall: 10, out var wall);
            var at = planet.TileToWorld(planet.SurfaceRing + 4, wall[0].t);
            var tnt = new Projectile(at, Vector2.Zero, 120f, 0.01f, ProjectileKind.Tnt);
            tnt.Explode(planet, physics, cells);
            int standing = 0, chipped = 0;
            foreach (var (r, t) in wall)
            {
                if (planet.Get(r, t) == TileKind.AlienAlloy) standing++;
                if (planet.Damage(r, t) > 0) chipped++;
            }
            Check($"defense: TNT leaves the alloy wall standing ({standing}/{wall.Count})",
                standing == wall.Count);
            Check($"defense: but the blast visibly chips it ({chipped} tiles damaged)", chipped > 0);
        }

        // --- 2. Creature jaws barely dent architecture (a stone box falls in seconds) ---
        {
            StampBlock(1.1f, TileKind.AlienAlloy, wide: 3, tall: 6, out var alloyBox);
            var inAlloy = planet.TileToWorld(planet.SurfaceRing + 3, alloyBox[0].t);
            var farPlayer = new Player(planet.Center + new Vector2(0, -(planet.Radius + 20) * Planet.TileSize));
            var borer = new Creature(inAlloy, CreatureKind.Borer);
            for (var i = 0; i < 60 * 8; i++) borer.Update(dt, planet, physics, cells, farPlayer);
            var alloyLeft = 0;
            foreach (var (r, t) in alloyBox) if (planet.Get(r, t) == TileKind.AlienAlloy) alloyLeft++;
            Check($"defense: 8s of borer jaws leaves alloy mostly intact ({alloyLeft}/{alloyBox.Count})",
                alloyLeft > alloyBox.Count / 2);
        }

        // --- 3. Diggers hunt only when provoked ---
        {
            var pos = FindCavePos(planet, seedOffset: 4321);
            if (pos is { } cavePos)
            {
                var up = planet.UpAt(cavePos);
                var right = new Vector2(-up.Y, up.X);
                var prey = new Player(cavePos + right * 150f);

                var calm = new Creature(cavePos, CreatureKind.Borer);
                for (var i = 0; i < 60 * 8; i++) calm.Update(dt, planet, physics, cells, prey);
                var calmDist = (calm.Position - prey.Position).Length();
                Check($"defense: unprovoked borer ignores the dwarf ({calmDist:0}px away)",
                    calmDist > 55f);

                var angry = new Creature(cavePos, CreatureKind.Borer);
                for (var i = 0; i < 60 * 14; i++)
                {
                    if (i % 60 == 0) angry.HitFlash = 0.2f;   // keep the grudge fresh
                    angry.Update(dt, planet, physics, cells, prey);
                }
                var angryDist = (angry.Position - prey.Position).Length();
                Check($"defense: provoked borer digs to the prey ({angryDist:0}px away)",
                    angryDist < 80f);
            }
            else
            {
                Check("defense: provocation test cave found", false);
            }
        }

        // --- 4. Militia bolts pass through neutrals, sting hostiles, don't aggro the titan ---
        {
            var muzzle = planet.Center + new Vector2(0, -(planet.Radius + 30) * Planet.TileSize);
            var lane = new Vector2(1, 0);
            var civ = new Creature(muzzle + lane * 14f, CreatureKind.Civilian);
            var grub = new Creature(muzzle + lane * 30f, CreatureKind.Grub);
            var crowd = new List<Creature> { civ, grub };
            var civHp = civ.Health;
            var grubHp = grub.Health;
            var bolt = new Projectile(muzzle, lane * 260f, 3f, 1.1f, ProjectileKind.CivicBolt);
            for (var i = 0; i < 30 && !bolt.Dead; i++)
            {
                bolt.Update(dt, planet, physics, cells);
                Combat.ResolveHits(bolt, crowd, null, planet, physics, cells);
            }
            Check("defense: civic bolt spares the civilian", civ.Health == civHp);
            Check($"defense: civic bolt stings the invader ({grubHp} -> {grub.Health})",
                grub.Health < grubHp);

            var titan = new Titan(planet, 2.2f, TitanKind.Kong);
            titan.Hatch();
            titan.AggroTimer = 0f;   // hatching starts it angry — calm it for the gate test
            var tHp = titan.Health;
            var tb = new Projectile(titan.Position - lane * 20f, lane * 260f, 3f, 1.1f,
                ProjectileKind.CivicBolt);
            for (var i = 0; i < 30 && !tb.Dead; i++)
            {
                tb.Update(dt, planet, physics, cells);
                Combat.ResolveHits(tb, new List<Creature>(), titan, planet, physics, cells);
            }
            Check($"defense: civic bolt wounds the titan a little ({tHp - titan.Health:0.#} dmg)",
                titan.Health < tHp && tHp - titan.Health < 10f);
            Check("defense: civic bolt does NOT aggro the titan onto the player", !titan.IsAggro);
            // Contrast: the player's own round through the same gate DOES wake it.
            var pb = new Projectile(titan.Position - lane * 20f, lane * 480f, 14f, 1.5f,
                ProjectileKind.Pistol);
            for (var i = 0; i < 30 && !pb.Dead; i++)
            {
                pb.Update(dt, planet, physics, cells);
                Combat.ResolveHits(pb, new List<Creature>(), titan, planet, physics, cells);
            }
            Check("defense: a player round still aggros it", titan.IsAggro);
        }

        // --- 5. A titan wrecks a tower wall slowly, not instantly ---
        {
            StampBlock(1.8f, TileKind.AlienAlloy, wide: 2, tall: 14, out var tower);
            var n0 = planet.TilesAt(planet.SurfaceRing + 5);
            var tt = (int)((1.8f / MathHelper.TwoPi + 1f) % 1f * n0);
            var lean = planet.TileToWorld(planet.SurfaceRing + 5, tt);
            var kaiju = new Titan(planet, 1.8f, TitanKind.Kong);
            kaiju.Hatch();
            var boulders = new List<FallingBoulder>();
            var shots = new List<TitanProjectile>();
            int BrokenTiles()
            {
                var broken = 0;
                foreach (var (r, t) in tower) if (planet.Get(r, t) != TileKind.AlienAlloy) broken++;
                return broken;
            }
            void Lean(int frames)
            {
                for (var i = 0; i < frames; i++)
                {
                    kaiju.Position = lean;   // pin the body against the wall — no wandering off
                    kaiju.Velocity = Vector2.Zero;
                    kaiju.Update(dt, planet, physics, cells, kaiju.Position + new Vector2(900f, 0), boulders, shots);
                }
            }
            // The wrecking bite hits HARD now (a kaiju is the one thing a city can't shrug
            // off): the first instants only crack the wall, but a few seconds level it.
            Lean(9);    // 0.15s — one bite: cracked, not breached
            Check($"defense: an instant of titan contact only cracks the wall ({BrokenTiles()} broken)",
                BrokenTiles() <= 2);
            Lean(60 * 4);
            Check($"defense: four seconds of titan wrecking tears it open ({BrokenTiles()} broken)",
                BrokenTiles() > 8);
        }

        // --- 6. Warren war-cry: the first sighting raises the backup flag, and a rallied
        // guard far beyond sight range picks up the hunt.
        {
            var dAng = 2.6f;
            var post = SpawnDirector.FindSurfaceSpawn(planet, dAng, planet.Radius);
            var sentry = new Creature(post, CreatureKind.Lizardman);
            var closePrey = new Player(post + planet.UpAt(post) * 20f);
            sentry.Update(dt, planet, physics, cells, closePrey);
            Check("defense: sighting guard raises the war-cry flag", sentry.CallingBackup);

            // The march test needs runnable ground: scan for a flat stretch (no mountain
            // flank or lake pit between guard and prey) so a wedge can't fake a failure.
            var aAng = 0.2f;
            var flatFound = false;
            for (var a = 0.2f; a < MathHelper.TwoPi && !flatFound; a += 0.13f)
            {
                var p0 = SpawnDirector.FindSurfaceSpawn(planet, a, planet.Radius);
                var r0 = (p0 - planet.Center).Length();
                var flat = true;
                for (var off = 0.05f; off <= 0.4f && flat; off += 0.05f)
                {
                    var p = SpawnDirector.FindSurfaceSpawn(planet, a + off, planet.Radius);
                    flat = MathF.Abs((p - planet.Center).Length() - r0) < 10f;
                }
                if (flat) { aAng = a; flatFound = true; }
            }
            Check("defense: flat rally runway found", flatFound);
            var allyPos = SpawnDirector.FindSurfaceSpawn(planet, aAng, planet.Radius);
            var runR = (allyPos - planet.Center).Length();
            var farPrey = new Player(SpawnDirector.FindSurfaceSpawn(
                planet, aAng + 420f / runR, planet.Radius));   // ~420px along the surface
            var ally = new Creature(allyPos, CreatureKind.Lizardman);
            for (var i = 0; i < 60 * 3; i++) ally.Update(dt, planet, physics, cells, farPrey);
            var calmDist = (ally.Position - farPrey.Position).Length();
            Check($"defense: unrallied distant guard stays on post ({calmDist:0}px)", calmDist > 280f);
            ally.RallyToWar();
            for (var i = 0; i < 60 * 3; i++) ally.Update(dt, planet, physics, cells, farPrey);
            var ralliedDist = (ally.Position - farPrey.Position).Length();
            Check($"defense: rallied guard closes on distant prey ({calmDist:0} -> {ralliedDist:0}px)",
                ralliedDist < calmDist - 60f);
        }

        // --- 7. Saucer patrol: cruises without embedding, then slides over a handed target.
        {
            var sAng = 4.4f;
            var ground = SpawnDirector.FindSurfaceSpawn(planet, sAng, planet.Radius);
            var upS = planet.UpAt(ground);
            var saucer = new Creature(ground + upS * 120f, CreatureKind.Saucer);
            var idlePilot = new Player(ground + upS * 800f);
            for (var i = 0; i < 60 * 6; i++) saucer.Update(dt, planet, physics, cells, idlePilot);
            Check("defense: saucer patrols without embedding", !EmbeddedInRock(planet, saucer.Position));
            var threat = ground + upS * 10f;
            saucer.GuardTarget = threat;
            for (var i = 0; i < 60 * 5; i++) saucer.Update(dt, planet, physics, cells, idlePilot);
            var station = threat + planet.UpAt(threat) * 90f;
            Check($"defense: saucer holds station over the threat ({(saucer.Position - station).Length():0}px off)",
                (saucer.Position - station).Length() < 70f);
        }

        // --- 8. Doubled alien constitutions.
        Check("defense: aliens toughened (civilian 24hp, peacekeeper 52hp)",
            new Creature(Vector2.Zero, CreatureKind.Civilian).Health == 24f
            && new Creature(Vector2.Zero, CreatureKind.Peacekeeper).Health == 52f);

        // --- 9. Flame/acid walkers hose level, never into their own footing: park the prey
        // straight under the titan's chin and every grain of the breath still leaves within
        // ~15 degrees of the horizon.
        {
            var breather = new Titan(planet, 3.0f, TitanKind.Godzilla);
            breather.Hatch();
            var boulders2 = new List<FallingBoulder>();
            var flames = new List<TitanProjectile>();
            for (var i = 0; i < 180; i++)   // settle onto its feet
                breather.Update(dt, planet, physics, cells, breather.Position, boulders2, flames);
            flames.Clear();
            var prey = breather.Position - planet.UpAt(breather.Position) * 90f; // right below
            for (var i = 0; i < 60 * 6; i++)
            {
                breather.OnDamage();
                breather.Update(dt, planet, physics, cells, prey, boulders2, flames);
            }
            var flameCount = 0;
            var worstDip = 0f;
            var upB = planet.UpAt(breather.Position);
            foreach (var shot in flames)
            {
                if (shot.Kind != TitanShotKind.Flame) continue;
                flameCount++;
                var v = shot.Velocity;
                if (v.LengthSquared() < 1f) continue;
                worstDip = MathF.Min(worstDip, Vector2.Dot(Vector2.Normalize(v), upB));
            }
            Check($"defense: fire breath stays level over prey underfoot ({flameCount} grains, worst dip {worstDip:0.00})",
                flameCount > 10 && worstDip >= -0.32f);
        }

        // --- 10. Terrain sense: a citizen ambling on a raised rooftop platform turns at the
        // edges instead of walking off — 15 seconds of strolling never leaves the roof.
        {
            // Site the platform on flat baseline ground so no mountain flank pokes above it.
            var pAng = 5.4f;
            var baseline = (Planet.RingMin + planet.SurfaceRing) * Planet.TileSize;
            for (var a = 5.4f; a < 5.4f + MathHelper.TwoPi; a += 0.17f)
            {
                var srf = SpawnDirector.FindSurfaceSpawn(planet, a, planet.Radius);
                if (MathF.Abs((srf - planet.Center).Length() - baseline) < 14f) { pAng = a; break; }
            }
            StampBlock(pAng, TileKind.AlienAlloy, wide: 10, tall: 16, out _);
            var nT = planet.TilesAt(planet.SurfaceRing + 17);
            var tT = (int)((pAng % MathHelper.TwoPi / MathHelper.TwoPi + 1f) % 1f * nT);
            var roof = planet.TileToWorld(planet.SurfaceRing + 17, tT);
            var roofUp = planet.UpAt(roof);
            var stroller = new Creature(roof + roofUp * 5f, CreatureKind.Civilian);
            var nobody = new Player(roof + roofUp * 700f);
            for (var i = 0; i < 60 * 15; i++)
                stroller.Update(dt, planet, physics, cells, nobody);
            var drop = (roof - planet.Center).Length() - (stroller.Position - planet.Center).Length();
            Check($"defense: citizen keeps off the roof edge (dropped {drop:0}px)", drop < 10f);
        }

        // --- 10. City architecture shrugs off the disasters that dissolve ordinary ground ---
        {
            // Corrosion, cave-ins/quakes, meteor and blast craters all gate on the anchored
            // flag: every built tile carries it, so acid rain, earthquakes and meteor strikes
            // can't eat the city the way they chew open terrain.
            Check("defense: city architecture is anchored (acid-rain / quake / meteor proof)",
                Tiles.IsAnchored(TileKind.AlienAlloy) && Tiles.IsAnchored(TileKind.CityGlass)
                && Tiles.IsAnchored(TileKind.LizardBrick));

            // Melt is a separate rule: a lava flow (eruption / magma surge) pools on a glass
            // roof without melting it, though the same soak eats a dirt control to nothing.
            StampBlock(2.6f, TileKind.CityGlass, wide: 2, tall: 3, out var glass);
            StampBlock(3.4f, TileKind.Dirt, wide: 2, tall: 3, out var control);
            void PourLavaOn(List<(int r, int t)> b)
            {
                var top = 0;
                foreach (var (r, _) in b) top = Math.Max(top, r);
                foreach (var (r, t) in b) if (r == top) cells.FillTile(r + 1, t, Material.Lava);
            }
            for (var s = 0; s < 60 * 8; s++)
            {
                if (s % 30 == 0) { PourLavaOn(glass); PourLavaOn(control); }   // keep the flow fed
                cells.Update(dt);
            }
            var glassLeft = 0;
            foreach (var (r, t) in glass) if (planet.Get(r, t) == TileKind.CityGlass) glassLeft++;
            var controlLeft = 0;
            foreach (var (r, t) in control) if (planet.Get(r, t) == TileKind.Dirt) controlLeft++;
            Check($"defense: lava can't melt the glass roof ({glassLeft}/{glass.Count} intact)",
                glassLeft == glass.Count);
            Check($"defense: the same lava melts the dirt control ({control.Count - controlLeft}/{control.Count} gone)",
                controlLeft < control.Count);
        }

        // --- 11. Citizens take cover when a disaster strikes: flagged to shelter, a civilian
        // out on the street sprints to the nearest doorway and huddles instead of ambling ---
        {
            // Find a flat stretch of street so the run isn't blocked by a mountain flank.
            var cAng = 0.9f;
            var flat = false;
            var baseline = (Planet.RingMin + planet.SurfaceRing) * Planet.TileSize;
            for (var a = 0.9f; a < 0.9f + MathHelper.TwoPi; a += 0.13f)
            {
                var ok = true;
                for (var off = 0f; off <= 0.06f && ok; off += 0.02f)
                {
                    var p = SpawnDirector.FindSurfaceSpawn(planet, a + off, planet.Radius);
                    ok = MathF.Abs((p - planet.Center).Length() - baseline) < 12f;
                }
                if (ok) { cAng = a; flat = true; break; }
            }
            Check("defense: flat street for the cover drill found", flat);

            var ground = SpawnDirector.FindSurfaceSpawn(planet, cAng, planet.Radius);
            var up = planet.UpAt(ground);
            var right = new Vector2(-up.Y, up.X);
            // A doorway ~150px along the street, registered as a city-spawn shelter site.
            var shelter = SpawnDirector.FindSurfaceSpawn(
                planet, cAng + 150f / (ground - planet.Center).Length(), planet.Radius);
            var (dr, dtl) = planet.WorldToTile(shelter);
            var spawnsBefore = planet.CitySpawns.Count;
            planet.CitySpawns.Add((dr, dtl));

            var civ = new Creature(ground + up * 4f, CreatureKind.Civilian);
            var startGap = MathF.Abs(Vector2.Dot(shelter - civ.Position, right));
            var nobody = new Player(ground + up * 800f);
            for (var i = 0; i < 60 * 6; i++)
            {
                civ.TakeCover = true;   // Game1 sets this each frame a disaster is live
                civ.Update(dt, planet, physics, cells, nobody);
            }
            var endGap = MathF.Abs(Vector2.Dot(shelter - civ.Position, right));
            // Don't leak the test doorway into later suites sharing this planet.
            planet.CitySpawns.RemoveRange(spawnsBefore, planet.CitySpawns.Count - spawnsBefore);
            Check($"defense: a citizen runs to shelter in a disaster (gap {startGap:0} -> {endGap:0}px)",
                endGap < 15f && endGap < startGap * 0.4f);
        }
    }

    /// <summary>The planet-wide resident census (PopulateWorld): cities staffed, saucers on
    /// station, warrens garrisoned and lakes stocked BEFORE the player goes anywhere — and
    /// all of it flagged Resident so the distance cull never erases it.</summary>
    private static void TestPopulateWorld()
    {
        // City world: dwellers at the addresses, saucers over the districts.
        var cityDef = PlanetDefs.ById("city");
        var city = new Session(cityDef) { Planet = WorldGen.Generate(11, cityDef) };
        city.Cells = new Cells(city.Planet);
        city.Physics = new Physics(city.Planet, city.Cells);
        city.Player = new Player(SpawnDirector.FindSurfaceSpawn(city.Planet, -MathF.PI / 2f, city.Planet.Radius));
        SpawnDirector.PopulateWorld(city);
        int civs = 0, keepers = 0, saucers = 0, farFromSpawn = 0, nonResident = 0;
        foreach (var c in city.Creatures)
        {
            if (!c.Resident) { nonResident++; continue; }
            if (c.Kind == CreatureKind.Civilian) civs++;
            else if (c.Kind == CreatureKind.Peacekeeper) keepers++;
            else if (c.Kind == CreatureKind.Saucer) saucers++;
            if ((c.Position - city.Player.Position).Length() > 1000f) farFromSpawn++;
        }
        Check($"census: city fully staffed before arrival ({civs} civs, {keepers} militia, {saucers} saucers)",
            civs >= 20 && keepers >= 4 && saucers >= 2);
        Check($"census: population exists far beyond the spawn bubble ({farFromSpawn} beyond 1000px)",
            farFromSpawn > 10);
        Check("census: everything seeded is a resident", nonResident == 0);

        // Warren world: a guard pair stationed in every hall from minute zero.
        var emberDef = PlanetDefs.ById("ember");
        var warren = new Session(emberDef) { Planet = WorldGen.Generate(12, emberDef) };
        warren.Cells = new Cells(warren.Planet);
        warren.Physics = new Physics(warren.Planet, warren.Cells);
        warren.Player = new Player(SpawnDirector.FindSurfaceSpawn(warren.Planet, -MathF.PI / 2f, warren.Planet.Radius));
        SpawnDirector.PopulateWorld(warren);
        var guards = 0;
        foreach (var c in warren.Creatures)
            if (c is { Kind: CreatureKind.Lizardman, Resident: true }) guards++;
        Check($"census: warrens garrisoned before arrival ({guards} guards / {warren.Planet.LizardDens.Count} dens)",
            guards >= warren.Planet.LizardDens.Count * 2);

        // Per-material immunity: only the natives can live in each hazard.
        Check("spawn: immunity maps to the right natives",
            new Creature(Vector2.Zero, CreatureKind.MagmaSlug).ImmuneTo(Material.Lava)
            && !new Creature(Vector2.Zero, CreatureKind.Grazer).ImmuneTo(Material.Lava)
            && new Creature(Vector2.Zero, CreatureKind.AcidStrider).ImmuneTo(Material.Acid)
            && !new Creature(Vector2.Zero, CreatureKind.Grazer).ImmuneTo(Material.Acid)
            && new Creature(Vector2.Zero, CreatureKind.AlienWhale).ImmuneTo(Material.Water)
            && !new Creature(Vector2.Zero, CreatureKind.Grazer).ImmuneTo(Material.Water));

        // Nothing is ever seeded inside a hazard it can't survive: flood a lava/acid world's
        // caves and pools, populate it, and confirm no non-immune creature sits in the burn.
        var slagDef = PlanetDefs.ById("slag");
        var slag = new Session(slagDef) { Planet = WorldGen.Generate(5, slagDef) };
        slag.Cells = new Cells(slag.Planet);
        slag.Physics = new Physics(slag.Planet, slag.Cells);
        slag.Player = new Player(SpawnDirector.FindSurfaceSpawn(slag.Planet, -MathF.PI / 2f, slag.Planet.Radius));
        slag.Cells.FillSkyTilesWithin(slag.Planet.Radius * slagDef.LavaFillFrac, Material.Lava);
        foreach (var (ax, ay) in slag.Planet.AcidSeeds) slag.Cells.FillTile(ax, ay, Material.Acid);
        for (var i = 0; i < 120; i++) slag.Cells.Update(1f / 60f);
        // Many spawn passes (surface + cave) so the guard is genuinely exercised near the lava.
        for (var i = 0; i < 10; i++) SpawnDirector.SpawnInitialFauna(slag);
        var trapped = 0;
        foreach (var c in slag.Creatures)
        {
            var (lava, acid, _) = slag.Cells.SampleHazardsNear(c.Position, c.Radius + 2f);
            if ((lava > 0 && !c.ImmuneTo(Material.Lava))
                || (acid > 0 && !c.ImmuneTo(Material.Acid))
                || (!c.ImmuneTo(Material.Water) && slag.Cells.CountWaterNear(c.Position, c.Radius + 2f) > 0))
                trapped++;
        }
        Check($"spawn: nobody hatches inside lava/acid they can't survive ({trapped} trapped of {slag.Creatures.Count})",
            trapped == 0);
    }

    /// <summary>The aquatics: player swimming (strokes rise, idle sinks gently, fins are
    /// faster), breath-ceiling tiers, land-swimmer buoyancy, and the two water-only species
    /// living in a carved test pool without embedding or beaching.</summary>
    private static void TestAquatics()
    {
        const float dt = 1f / 60f;
        var planet = WorldGen.Generate(88);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);

        // Carve a test pool below the baseline surface and fill it with water cells.
        const float pAng = 1.0f;
        var surfaceR = planet.SurfaceRing;
        for (var r = surfaceR - 14; r <= surfaceR - 1; r++)
        {
            var n = planet.TilesAt(r);
            var t0 = (int)(pAng / MathHelper.TwoPi * n);
            for (var dt2 = -16; dt2 <= 16; dt2++)
            {
                var tt = ((t0 + dt2) % n + n) % n;
                planet.Set(r, tt, TileKind.Sky);
                planet.SetWall(r, tt, TileKind.Stone);
                if (r <= surfaceR - 3) cells.FillTile(r, tt, Material.Water);
            }
        }
        for (var i = 0; i < 90; i++) cells.Update(dt);
        var nC = planet.TilesAt(surfaceR - 8);
        var poolCentre = planet.TileToWorld(surfaceR - 8, (int)(pAng / MathHelper.TwoPi * nC));
        var upP = planet.UpAt(poolCentre);
        Check($"aquatic: test pool holds water ({cells.CountWaterNear(poolCentre, 6f)} cells)",
            cells.CountWaterNear(poolCentre, 6f) >= 8);

        // --- Player swimming ---
        {
            var swimmer = new Player(poolCentre) { InWater = true };
            for (var i = 0; i < 60; i++) swimmer.Update(dt, planet, 0, false, +1);
            var rose = Vector2.Dot(swimmer.Position - poolCentre, upP);
            Check($"swim: stroking up rises ({rose:0}px in 1s)", rose > 8f);

            var idler = new Player(poolCentre) { InWater = true };
            for (var i = 0; i < 60; i++) idler.Update(dt, planet, 0, false);
            var sank = Vector2.Dot(idler.Position - poolCentre, upP);
            Check($"swim: idle sink is gentle ({sank:0}px in 1s)", sank < 0f && sank > -35f);

            var finned = new Player(poolCentre) { InWater = true, HasFins = true };
            var slow = new Player(poolCentre) { InWater = true };
            for (var i = 0; i < 45; i++)
            {
                finned.Update(dt, planet, +1, false);
                slow.Update(dt, planet, +1, false);
            }
            var upF = planet.UpAt(poolCentre);
            var rF = new Vector2(-upF.Y, upF.X);
            var dFin = Vector2.Dot(finned.Position - poolCentre, rF);
            var dSlow = Vector2.Dot(slow.Position - poolCentre, rF);
            Check($"swim: fins double the stroke ({dSlow:0} -> {dFin:0}px)", dFin > dSlow * 1.5f);

            var lungs = new Player(Vector2.Zero);
            var ok = lungs.EffectiveMaxBreath == Player.BaseMaxBreath;
            lungs.LungTier = 1; ok &= lungs.EffectiveMaxBreath == Player.BaseMaxBreath * 2f;
            lungs.LungTier = 2; ok &= lungs.EffectiveMaxBreath == Player.BaseMaxBreath * 3f;
            Check("swim: lung tiers scale the breath ceiling x1/x2/x3", ok);
        }

        // --- Land swimmer buoyancy: a lizardman dropped to the pool floor strokes back up ---
        {
            var floorPos = poolCentre - upP * 18f;
            var reptile = new Creature(floorPos, CreatureKind.Lizardman);
            var nobody = new Player(poolCentre + upP * 900f);
            for (var i = 0; i < 60 * 4; i++) reptile.Update(dt, planet, physics, cells, nobody);
            var rise = Vector2.Dot(reptile.Position - floorPos, planet.UpAt(floorPos));
            Check($"aquatic: submerged lizardman floats up ({rise:0}px)", rise > 6f);
        }

        // --- Water-only species stay put and stay legal ---
        {
            var whale = new Creature(poolCentre - upP * 6f, CreatureKind.AlienWhale);
            var nobody = new Player(poolCentre + upP * 900f);
            for (var i = 0; i < 60 * 8; i++)
            {
                whale.Update(dt, planet, physics, cells, nobody);
                cells.Update(dt);
            }
            Check("aquatic: whale not embedded after 8s", !EmbeddedInRock(planet, whale.Position));
            Check($"aquatic: whale stays in its basin ({cells.CountWaterNear(whale.Position, whale.Radius)} water cells around it)",
                cells.CountWaterNear(whale.Position, whale.Radius) >= 3);

            var crab = new Creature(poolCentre, CreatureKind.AlienCrab);
            for (var i = 0; i < 60 * 6; i++) crab.Update(dt, planet, physics, cells, nobody);
            Check("aquatic: crab settles the lakebed without embedding",
                !EmbeddedInRock(planet, crab.Position));
        }
    }

    /// <summary>True if the position sits inside a tile that actually blocks bodies —
    /// creature collision uses Tiles.BlocksPlayer, so passable tiles (glowshrooms in a
    /// fungal grove, ladders) don't count as "embedded in rock".</summary>
    private static bool EmbeddedInRock(Planet planet, Vector2 pos)
    {
        var (x, y) = planet.WorldToTile(pos);
        return Tiles.BlocksPlayer(planet.Get(x, y));
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

        // AsteroidTarget 0: this test is about the flight model, and a randomly drifting
        // rock ramming the ship mid-brake (+260 px/s kick) flakes the velocity checks.
        // Rock collisions get their own deterministic field in TestSpaceCombat.
        var sim = new Space.SpaceSim { AsteroidTarget = 0 };
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

        // The corona is also a hazard: 4s pressed against it must scorch hull off (one hit
        // per invulnerability window, same cadence as an asteroid strike).
        sim.Hull = sim.HullMax;
        sim.HitTimer = 0f;
        sim.ShipPos = new Vector2(Space.SpaceSim.SunRadius + 71f, 0f);
        sim.ShipVel = Vector2.Zero;
        sim.ShipHeading = MathF.PI;
        for (var i = 0; i < 240; i++) sim.Update(dt, 0f, thrust: true, brake: false);
        Check("space: corona contact burns the hull", sim.Hull < sim.HullMax, $"hull {sim.Hull}");

        // Fly at an ordinary planet: no wall — the ship reaches atmosphere-entry contact.
        var p = sim.Planets[2];
        sim.ShipPos = p.Pos + new Vector2(p.BodyRadius + 300f, 0f);
        sim.ShipVel = new Vector2(-300f, 0f);
        Space.SpacePlanet? contact = null;
        for (var i = 0; i < 240 && contact is null; i++)
        {
            sim.Update(dt, 0f, thrust: false, brake: false);
            contact = sim.AtmosphereContact();
        }
        Check("space: flying into a planet reaches entry contact", contact?.Def.Id == p.Def.Id);

        // The locked Rift's storm wall stays solid — no entry contact, ship shoved out.
        var rift = sim.Planets[^1];
        sim.ShipPos = rift.Pos + new Vector2(rift.BodyRadius + 300f, 0f);
        sim.ShipVel = new Vector2(-640f, 0f);
        var riftInside = false;
        var riftContact = false;
        for (var i = 0; i < 240; i++)
        {
            sim.Update(dt, 0f, thrust: false, brake: false);
            if ((sim.ShipPos - rift.Pos).Length() < rift.BodyRadius) riftInside = true;
            if (sim.AtmosphereContact() is not null) riftContact = true;
        }
        Check("space: locked rift storm wall repels", !riftInside && !riftContact);
        sim.RiftLocked = false;
        sim.ShipPos = rift.Pos + new Vector2(rift.BodyRadius + 10f, 0f);
        Check("space: unlocked rift accepts entry", sim.AtmosphereContact()?.Def.Id == "rift");

        // Far from everything: no entry contact.
        sim.ShipPos = new Vector2(0f, -20000f);
        Check("space: deep space offers no entry", sim.AtmosphereContact() is null);

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

        // Combat Plating: 30% off incoming damage, multiplicative with crafted armor.
        var dummy = new Player(Vector2.Zero) { Health = 100f };
        dummy.HasPlating = true;
        dummy.TakeDamage(10f);
        Check("plating: 30 percent damage cut", MathF.Abs(dummy.Health - 93f) < 0.01f,
            $"hp {dummy.Health}");
        dummy.HasArmor = true;
        dummy.TakeDamage(10f);
        Check("plating: stacks with crafted armor (x0.42)",
            MathF.Abs(dummy.Health - (93f - 4.2f)) < 0.01f, $"hp {dummy.Health}");

        // The Geo Scanner finds the deposits it promises, honours its radius, and hunts the
        // right tile kind for each signature ore id.
        var scanWorld = Space.Survey.WorldFor(World.PlanetDefs.ById("verdant"));
        var scanFrom = SpawnDirector.FindSurfaceSpawn(scanWorld, -MathF.PI / 2f, scanWorld.Radius);
        var iron = Scanner.FindNearest(scanWorld, scanFrom, TileKind.IronOre, 620f);
        Check("scanner: finds iron near the verdant surface", iron is not null);
        if (iron is { } ironPos)
        {
            var (ix, iy) = scanWorld.WorldToTile(ironPos);
            Check("scanner: fix actually points at iron", scanWorld.Get(ix, iy) == TileKind.IronOre);
            Check("scanner: fix respects the radius", (ironPos - scanFrom).Length() <= 620f,
                $"{(ironPos - scanFrom).Length():0} px");
        }
        Check("scanner: tiny radius finds nothing",
            Scanner.FindNearest(scanWorld, scanFrom, TileKind.Diamond, 24f) is null);
        Check("scanner: ore ids map to ore tiles",
            Scanner.OreTileFor("ruby") == TileKind.Ruby && Scanner.OreTileFor("platinum") == TileKind.PlatinumOre);

        // Tier-III effects: jetpack charge/climb curve, O2 ceiling, hull 9,
        // and the Aegis shield recharge.
        var tiers = new Player(Vector2.Zero) { HasJetpack = true };
        var cap1 = tiers.JetChargeCap;
        tiers.JetTier2 = true;
        var cap2 = tiers.JetChargeCap;
        tiers.JetTier3 = true;
        Check("tiers: jetpack charge 1x/2x/3x",
            MathF.Abs(cap1 - 2.6f) < 0.01f && MathF.Abs(cap2 - 5.2f) < 0.01f
            && MathF.Abs(tiers.JetChargeCap - 7.8f) < 0.01f);
        Check("tiers: pickup reach stays touch-range (no magnet)", tiers.PickupReach == 4f);
        tiers.HasO2Recycler = true;
        tiers.O2Tier2 = true;
        Check("tiers: O2 reserves II doubles the ceiling",
            MathF.Abs(tiers.EffectiveMaxOxygen - 200f) < 0.01f);
        Check("tiers: hull plating II reaches 9 pips",
            new Space.SpaceSim { HullTier = 3 }.HullMax == 9);
        Check("tiers: aegis capacitor halves shield recharge",
            new Space.SpaceSim { ShieldTier = 2 }.ShieldRechargeTime == 4f
            && new Space.SpaceSim().ShieldRechargeTime == 8f);

        // The rover loadout manifest: kits price in cargo, stack in the pending manifest,
        // and refuse gracefully when the hold is short.
        var loadMeta = new MetaSave();
        var pending = new System.Collections.Generic.Dictionary<string, int>();
        var ammo = System.Array.Find(Space.Loadouts.All, l => l.Id == "ammo")!;
        Check("loadout: empty hold refused", !Space.Loadouts.CanAfford(loadMeta, ammo));
        loadMeta.ShipCargo["pure_iron"] = 2;
        var realL = MetaSave.Load();
        try
        {
            Check("loadout: two buys stack",
                Space.Loadouts.TryBuy(loadMeta, ammo, pending)
                && Space.Loadouts.TryBuy(loadMeta, ammo, pending)
                && !Space.Loadouts.TryBuy(loadMeta, ammo, pending));
        }
        finally
        {
            realL.Save();
        }
        Check("loadout: manifest counts kits", pending.GetValueOrDefault("ammo") == 2);
        Check("loadout: cargo drained exactly", !loadMeta.ShipCargo.ContainsKey("pure_iron"));

        // Phase-11 content: rare gems generate where (and only where) they should, biome
        // pockets stamp real features, the voidstone reactor frees the tank, and the new
        // disasters run their warn → strike phases.
        const float dt2 = 1f / 60f;
        int CountKind(Planet w, TileKind kind)
        {
            var total = 0;
            for (var r = 0; r < w.Rings; r++)
            {
                var n = w.TilesAt(r);
                for (var t = 0; t < n; t++)
                    if (w.Get(r, t) == kind) total++;
            }
            return total;
        }
        // Gems are embedded overlays riding host tiles now, so gem censuses read GemAt.
        int CountGem(Planet w, TileKind kind)
        {
            var total = 0;
            for (var r = 0; r < w.Rings; r++)
            {
                var n = w.TilesAt(r);
                for (var t = 0; t < n; t++)
                    if (w.GemAt(r, t) == kind) total++;
            }
            return total;
        }
        var verdantWorld = Space.Survey.WorldFor(World.PlanetDefs.ById("verdant"));
        var riftWorld = Space.Survey.WorldFor(World.PlanetDefs.ById("rift"));
        Check("content: emerald seams on verdant", CountGem(verdantWorld, TileKind.Emerald) > 0,
            $"{CountGem(verdantWorld, TileKind.Emerald)} gems");
        Check("content: voidstone only in the rift",
            CountGem(riftWorld, TileKind.Voidstone) > 0 && CountGem(verdantWorld, TileKind.Voidstone) == 0,
            $"rift {CountGem(riftWorld, TileKind.Voidstone)}");
        Check("content: gems never generate as their own tiles",
            CountKind(verdantWorld, TileKind.Emerald) == 0 && CountKind(riftWorld, TileKind.Voidstone) == 0);
        Check("content: gold charted on verdant (nav core demands it), absent from the rift",
            CountKind(verdantWorld, TileKind.GoldOre) > 0 && CountKind(riftWorld, TileKind.GoldOre) == 0,
            $"verdant {CountKind(verdantWorld, TileKind.GoldOre)}");
        Check("content: fungal groves sprout wild glowshrooms",
            CountKind(verdantWorld, TileKind.Glowshroom) > 0,
            $"{CountKind(verdantWorld, TileKind.Glowshroom)} shrooms");

        var voidSim = new Space.SpaceSim { AsteroidTarget = 0, FreeThrust = true, HasFuel = false };
        voidSim.ShipPos = new Vector2(0f, -30000f);
        var v0 = voidSim.ShipPos;
        for (var i = 0; i < 120; i++) voidSim.Update(dt2, 0f, true, false);
        Check("content: voidstone reactor = full thrust on a dry tank, no burn",
            voidSim.FuelUsed == 0f && (voidSim.ShipPos - v0).Length() > 500f);

        var flareRun = new Session(World.PlanetDefs.ById("verdant"))
            { MeteorTimer = 9999f, DisasterTimer = 0.3f, NextDisaster = DisasterKind.Flare };
        var flareParticles = new Rendering.Particles();
        bool warned = false, struck = false;
        for (var i = 0; i < 60 * 9 && !struck; i++)
        {
            var res = AmbientDirector.Update(dt2, flareRun, flareParticles);
            warned |= res.FlareWarned;
            struck |= res.FlareStruck;
        }
        Check("content: solar flare warns then strikes", warned && struck && flareRun.FlareActive > 0f);

        // One disaster at a time: with the flare still burning, the shared clock must hold —
        // a due earthquake can't fire until the world is quiet again.
        flareRun.DisasterTimer = 0.05f;
        flareRun.NextDisaster = DisasterKind.Earthquake;
        var held = true;
        for (var i = 0; i < 30; i++)
            held &= !AmbientDirector.Update(dt2, flareRun, flareParticles).QuakeStruck;
        Check("disasters: only one at a time (clock holds while live)",
            held && flareRun.DisasterTimer >= 0.05f && AmbientDirector.DisasterActive(flareRun));

        // Spacing scales with planet difficulty: 7 minutes on the gentlest world down to
        // 2 minutes on the hardest, jitter within ±15%.
        Check("disasters: spacing 7 min gentle → 2 min brutal",
            MathF.Abs(AmbientDirector.BaseInterval(World.PlanetDefs.ById("verdant")) - 420f) < 0.01f
            && MathF.Abs(AmbientDirector.BaseInterval(World.PlanetDefs.ById("rift")) - 120f) < 0.01f);
        var spaced = true;
        for (var i = 0; i < 20; i++)
        {
            var roll = AmbientDirector.NextInterval(World.PlanetDefs.ById("frost"));
            var expect = AmbientDirector.BaseInterval(World.PlanetDefs.ById("frost"));
            spaced &= roll >= expect * 0.85f - 0.01f && roll <= expect * 1.15f + 0.01f;
        }
        Check("disasters: clock re-rolls stay within the jitter band", spaced);

        var blizzRun = new Session(World.PlanetDefs.ById("frost"))
            { MeteorTimer = 9999f, DisasterTimer = 0.3f, NextDisaster = DisasterKind.Blizzard };
        var blizzStarted = false;
        for (var i = 0; i < 60 && !blizzStarted; i++)
            blizzStarted |= AmbientDirector.Update(dt2, blizzRun, flareParticles).BlizzardStarted;
        Check("content: blizzard breaks on the frost world", blizzStarted && blizzRun.BlizzardActive > 0f);

        // Biome fauna: the wraith is the renewable voidstone source, the crawler pays out
        // crystal, and the new kinds carry sane stat blocks.
        var wraithDrops = Corpse.DropsFor(CreatureKind.VoidWraith);
        Check("fauna: void wraith sheds voidstone",
            System.Array.Exists(wraithDrops, d => d.id == "voidstone" && d.count >= 1));
        Check("fauna: crystal crawler pays out crystal",
            System.Array.Exists(Corpse.DropsFor(CreatureKind.CrystalCrawler), d => d.id == "crystal"));
        var bat = new Creature(Vector2.Zero, CreatureKind.SporeBat);
        var crawler = new Creature(Vector2.Zero, CreatureKind.CrystalCrawler);
        var wraith = new Creature(Vector2.Zero, CreatureKind.VoidWraith);
        Check("fauna: stat blocks read right",
            bat.Health < crawler.Health && wraith.MoveSpeed > crawler.MoveSpeed
            && crawler.ContactDamage > bat.ContactDamage);
        Check("fauna: rock mimic hoards gold and spawns disguised (non-hostile)",
            System.Array.Exists(Corpse.DropsFor(CreatureKind.RockMimic), d => d.id == "gold")
            && !new Creature(Vector2.Zero, CreatureKind.RockMimic).Hostile);
        var slimelet = new Creature(Vector2.Zero, CreatureKind.Slimelet);
        var slime = new Creature(Vector2.Zero, CreatureKind.CaveSlime);
        Check("fauna: slimelet is a strictly smaller cave slime",
            slimelet.Health < slime.Health && slimelet.Radius < slime.Radius
            && slimelet.ContactDamage < slime.ContactDamage);

        // Biome herds: each world archetype fields its own neutral surface species — a
        // frost run stocks snow lopers, never the verdant grazer/hopper pair — and the
        // Rift's only gentle life is the airborne null moth.
        {
            var faunaCells = new Cells(verdantWorld);
            var faunaPhysics = new Physics(verdantWorld, faunaCells);
            Session HerdRun(string defId) => new(World.PlanetDefs.ById(defId))
            {
                Planet = verdantWorld, Cells = faunaCells, Physics = faunaPhysics,
                Player = new Player(SpawnDirector.FindSurfaceSpawn(verdantWorld, -MathF.PI / 2f, verdantWorld.Radius)),
            };

            var frostHerd = HerdRun("frost");
            SpawnDirector.SpawnInitialFauna(frostHerd);
            var lopers = frostHerd.Creatures.FindAll(c => c.Kind == CreatureKind.SnowLoper);
            Check("fauna: frost world stocks snow lopers, no verdant herd",
                lopers.Count > 0 && lopers.TrueForAll(c => !c.Hostile)
                && !frostHerd.Creatures.Exists(c => c.Kind is CreatureKind.Grazer or CreatureKind.Hopper),
                $"{lopers.Count} lopers");

            var riftHerd = HerdRun("rift");
            SpawnDirector.SpawnInitialFauna(riftHerd);
            Check("fauna: rift's neutral life is airborne null moths only",
                riftHerd.Creatures.Exists(c => c.Kind == CreatureKind.NullMoth)
                && !riftHerd.Creatures.Exists(c => c.IsSurfaceKind)
                && !riftHerd.Creatures.Exists(c => c.Kind is CreatureKind.SkyMoth));
        }

        // Acid spitter: parked in line of sight of a target, it must lob acid globs into the
        // shared enemy-shot list within a few seconds.
        {
            var sCells = new Cells(verdantWorld);
            var sPhysics = new Physics(verdantWorld, sCells);
            // Need a pocket with ~36px of clear air beside the spitter so the LOS check
            // can pass — probe candidate sites until one has an open lane.
            Vector2? sSite = null;
            var preyAt = Vector2.Zero;
            for (var s = 0; s < 200 && sSite is null; s++)
            {
                if (FindCavePos(verdantWorld, seedOffset: 5000 + s * 17) is not { } q) continue;
                foreach (var lane in new[] { new Vector2(36f, 0f), new Vector2(-36f, 0f),
                                             new Vector2(0f, 36f), new Vector2(0f, -36f) })
                {
                    var open = true;
                    for (var k = 1; k <= 4 && open; k++)
                        if (verdantWorld.IsSolidAt(q + lane * (k / 4f))) open = false;
                    if (open) { sSite = q; preyAt = q + lane; break; }
                }
            }
            if (sSite is { } sp)
            {
                var spitter = new Creature(sp, CreatureKind.AcidSpitter);
                var prey = new Player(preyAt);
                var shots = new System.Collections.Generic.List<TitanProjectile>();
                for (var i = 0; i < 60 * 6 && shots.Count == 0; i++)
                    spitter.Update(dt2, verdantWorld, sPhysics, sCells, prey, shots);
                Check("fauna: acid spitter opens fire on visible prey",
                    shots.Count > 0 && shots.TrueForAll(s => s.Kind == TitanShotKind.Acid));
            }
            else Check("fauna: acid spitter test site found", false, "no open lane");
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

        // Cancellable settle: atmosphere entry fires the token mid-build and takes the world
        // as soon as generation is done — the leftover settle runs live in the orbit frames.
        // A pre-cancelled token must still yield a complete world, in a fraction of the time.
        var settleSw = System.Diagnostics.Stopwatch.StartNew();
        DwarfMinerGame.BuildSessionWorld(World.PlanetDefs.ById("verdant"));
        var fullMs = settleSw.ElapsedMilliseconds;
        using (var settleCts = new System.Threading.CancellationTokenSource())
        {
            settleCts.Cancel();
            settleSw.Restart();
            var quick = DwarfMinerGame.BuildSessionWorld(World.PlanetDefs.ById("verdant"), settleCts.Token);
            Check("prefetch: cancelled settle still yields a usable world",
                quick.Planet is not null && quick.Cells is not null && quick.Physics is not null);
            Check("prefetch: cancelled settle skips the heavy half",
                settleSw.ElapsedMilliseconds < fullMs / 2,
                $"{settleSw.ElapsedMilliseconds}ms vs {fullMs}ms settled");
        }

        // Aim predictor: flying at a planet names it long before it's the nearest body, so
        // the prefetch gets the whole cruise as build lead. Flying the gap names nothing.
        var sim7 = new Space.SpaceSim { AsteroidTarget = 0 };
        var target = sim7.Planets[2];
        var away = target.Pos + Vector2.Normalize(target.Pos) * 3000f;   // sun-away, far out
        sim7.ShipPos = away;
        sim7.ShipVel = Vector2.Normalize(target.Pos - away) * 500f;
        Check("prefetch: aim predictor names the planet dead ahead",
            sim7.AimedPlanet()?.Def.Id == target.Def.Id);
        sim7.ShipVel = new Vector2(-sim7.ShipVel.Y, sim7.ShipVel.X);   // 90° off — no target
        var offAim = sim7.AimedPlanet();
        Check("prefetch: aim predictor stays quiet flying past",
            offAim is null || offAim.Def.Id != target.Def.Id,
            offAim is null ? "null" : offAim.Def.Id);

        // Boot park: restoring a save near a planet re-parks the ship trailing it on its
        // own orbit ring — far enough for the boot build to win the dive race, nose on the
        // planet, and receding from (not swept by) the orbital motion when left idle.
        var sim8 = new Space.SpaceSim { AsteroidTarget = 0 };
        sim8.ParkShipTrailing(2);
        var parked = sim8.Planets[2];
        var parkSurf = (sim8.ShipPos - parked.Pos).Length() - parked.BodyRadius;
        Check("bootpark: parks a build-lead outside the surface",
            parkSurf > Space.SpaceSim.BootParkDistance * 0.9f
            && parkSurf < Space.SpaceSim.BootParkDistance * 1.1f, $"{parkSurf:0}");
        Check("bootpark: sits on the planet's own orbit ring",
            MathF.Abs(sim8.ShipPos.Length() - parked.OrbitRadius) < 1f);
        Check("bootpark: nose aimed at the planet",
            Vector2.Dot(sim8.ShipDir, Vector2.Normalize(parked.Pos - sim8.ShipPos)) > 0.99f);
        for (var t = 0; t < 1800; t++) sim8.Update(1f / 60f, 0f, thrust: false, brake: false);
        Check("bootpark: idle ship recedes instead of being swept",
            (sim8.ShipPos - parked.Pos).Length() - parked.BodyRadius > parkSurf - 1f);
    }

    /// <summary>Moons: every campaign hangs a cratered airless moon on a mid-chain host;
    /// a second rolled ocean world becomes an atmospheric ocean moon; SpaceSim rides moons
    /// on live satellite orbits around their parents; and the cratered moon's world is
    /// airless, dirt-free regolith with a closed vacuum-native roster.</summary>
    private static void TestMoons()
    {
        // The cratered moon def, and the water-moon conversion across many seeds.
        var chain = PlanetGen.Campaign(1234);
        var crater = chain[8];
        var hostFound = Array.Exists(chain[..7], d => d.Id == crater.MoonOf);
        Check("moons: cratered moon orbits a campaign world",
            crater.Id == "moon" && crater.MoonOf is not null && hostFound);
        Check("moons: cratered moon is airless, low-g, and actually cratered",
            crater.Airless && crater.GravityScale < 1f && crater.Craters > 0
            && crater.Biome == "moon");

        var sawTwoOceans = false;
        var conversionOk = true;
        for (var seed = 1; seed <= 60 && !sawTwoOceans; seed++)
        {
            var c = PlanetGen.Campaign(seed);
            var oceans = 0;
            foreach (var d in c[..7]) if (d.Biome == "ocean") oceans++;
            if (oceans < 2) continue;
            sawTwoOceans = true;
            var oceanMoons = 0;
            var solarOceans = 0;
            foreach (var d in c[..7])
            {
                if (d.Biome != "ocean") continue;
                if (d.MoonOf is not null)
                {
                    oceanMoons++;
                    // Atmospheric moon: keeps its seas and its air (no vac gate).
                    conversionOk &= !d.Airless && d.HasWater
                        && Array.Exists(c[..7], h => h.Id == d.MoonOf);
                }
                else solarOceans++;
            }
            conversionOk &= oceanMoons == 1 && solarOceans >= 1;
        }
        Check("moons: a second ocean world becomes an atmospheric ocean moon",
            sawTwoOceans && conversionOk);

        // Satellite orbits: Activate the chain and watch the moon ride its parent.
        World.PlanetDefs.Activate(chain);
        var sim = new Space.SpaceSim { AsteroidTarget = 0 };
        var moonP = sim.Planets.Find(p => p.Def.Id == "moon")!;
        var host2 = sim.Planets.Find(p => p.Def.Id == crater.MoonOf)!;
        Check("moons: sim binds the moon to its parent", moonP.Parent == host2);
        var maxSep = 0f;
        var swept = 0f;
        var prevBearing = MathF.Atan2(moonP.Pos.Y - host2.Pos.Y, moonP.Pos.X - host2.Pos.X);
        for (var i = 0; i < 60 * 30; i++)
        {
            sim.Update(1f / 60f, 0f, thrust: false, brake: false);
            maxSep = MathF.Max(maxSep, (moonP.Pos - host2.Pos).Length());
            var b = MathF.Atan2(moonP.Pos.Y - host2.Pos.Y, moonP.Pos.X - host2.Pos.X);
            var db = b - prevBearing;
            while (db > MathF.PI) db -= MathF.Tau;
            while (db < -MathF.PI) db += MathF.Tau;
            swept += MathF.Abs(db);
            prevBearing = b;
        }
        Check($"moons: moon stays leashed to its host ({maxSep:0} px max)",
            maxSep < host2.BodyRadius + 800f);
        Check($"moons: moon actually circles its host ({swept:0.0} rad in 30s)", swept > 2f);
        sim.ParkShipTrailing(sim.Planets.IndexOf(moonP));
        var parkDist = (sim.ShipPos - moonP.Pos).Length() - moonP.BodyRadius;
        Check($"moons: boot park trails the moon itself ({parkDist:0} px)",
            parkDist > Space.SpaceSim.EntryRange && parkDist < 900f);

        // The cratered moon's world: airless regolith, craters, closed roster.
        var moonWorld = WorldGen.Generate(777, crater);
        Check("moons: moon world is airless + low-g", moonWorld.Airless
            && MathF.Abs(moonWorld.GravityScale - crater.GravityScale) < 0.001f);
        var moonDirt = 0;
        foreach (var (x, y) in moonWorld.AllTiles())
            if (moonWorld.Get(x, y) == TileKind.Dirt) moonDirt++;
        Check($"moons: no dirt on the dead moon ({moonDirt} tiles)", moonDirt == 0);

        var mCells = new Cells(moonWorld);
        var mPhysics = new Physics(moonWorld, mCells);
        var mSurface = SpawnDirector.FindSurfaceSpawn(moonWorld, -MathF.PI / 2f, moonWorld.Radius);
        var moonRun = new Session(crater)
        {
            Planet = moonWorld, Cells = mCells, Physics = mPhysics,
            Player = new Player(mSurface),
        };
        SpawnDirector.SpawnInitialFauna(moonRun);
        for (var i = 0; i < 40; i++)
        {
            moonRun.SpawnTimer = 0f;
            moonRun.FaunaTimer = 0f;
            SpawnDirector.Update(0.05f, moonRun);
        }
        var moonNatives = 0;
        var moonOutsiders = 0;
        foreach (var c in moonRun.Creatures)
        {
            if (c.Kind is CreatureKind.Moonlet or CreatureKind.VacLeech or CreatureKind.Selenite
                or CreatureKind.DustDevil or CreatureKind.StarJelly) moonNatives++;
            else moonOutsiders++;
        }
        Check($"moons: only vacuum natives spawn ({moonNatives} natives, {moonOutsiders} outsiders)",
            moonNatives > 0 && moonOutsiders == 0);
    }

    /// <summary>The Hollow — the landable mega-asteroid in the outer belt: def shape, the
    /// airless/low-gravity/geode worldgen promises, the vac-suit space gate, and the belt
    /// natives' signature behaviours (the moonlet's orbit, the vac leech's air siphon).</summary>
    private static void TestHollow()
    {
        var def = World.PlanetDefs.HollowWorld;
        Check("hollow: def is airless, quarter-g, dead rock, starspawn-guarded",
            def.Airless && def.GravityScale <= 0.25f && def.LavaFillFrac == 0f && !def.HasWater
            && def.Titan == TitanKind.CosmicOctopus);
        Check("hollow: id resolves without an Activated chain",
            World.PlanetDefs.ById("hollow").Id == "hollow");
        Check("hollow: vac suit line exists in the foundry",
            Array.Exists(Space.Upgrades.All, u => u.Id == "vacsuit"));

        var world = WorldGen.Generate(1234, def);
        Check("hollow: gravity scale + airless flag stamped on the planet",
            MathF.Abs(world.GravityScale - def.GravityScale) < 0.001f && world.Airless);
        Check("hollow: dead rock seeds no lava/water/acid/gas",
            world.LavaSeeds.Count == 0 && world.WaterSeeds.Count == 0
            && world.AcidSeeds.Count == 0 && world.GasSeeds.Count == 0);

        // Asteroids grow no soil: the whole world must be dirt-free (regolith gravel instead).
        var dirtTiles = 0;
        foreach (var (x, y) in world.AllTiles())
            if (world.Get(x, y) == TileKind.Dirt) dirtTiles++;
        Check($"hollow: no dirt anywhere ({dirtTiles} tiles)", dirtTiles == 0);

        // Asteroid silhouette: the terrain line must swing by whole lobes — a potato, not a
        // lathed circle — while a classic world's profile stays essentially flat.
        float profLo = float.MaxValue, profHi = float.MinValue;
        foreach (var s in world.SurfaceProfile!)
        {
            profLo = MathF.Min(profLo, s);
            profHi = MathF.Max(profHi, s);
        }
        Check($"hollow: lumpy asteroid silhouette (terrain line swings {profHi - profLo:0} rings)",
            profHi - profLo > 45f);
        var roundWorld = WorldGen.Generate(1234, World.PlanetDefs.ById("verdant"));
        float rLo = float.MaxValue, rHi = float.MinValue;
        foreach (var s in roundWorld.SurfaceProfile!)
        {
            rLo = MathF.Min(rLo, s);
            rHi = MathF.Max(rHi, s);
        }
        Check($"hollow: ordinary worlds stay round (verdant swings {rHi - rLo:0} rings)",
            rHi - rLo < 10f);
        // The local terrain line is what depth reads against: sampling SurfaceRadiusAt
        // around the asteroid must reproduce the lobes (crests and valleys disagree about
        // where "the surface" is — that's what keeps a valley floor from draining air).
        float sLo = float.MaxValue, sHi = float.MinValue;
        for (var i = 0; i < 16; i++)
        {
            var a = i / 16f * MathHelper.TwoPi;
            var sr = world.SurfaceRadiusAt(
                world.Center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 2000f);
            sLo = MathF.Min(sLo, sr);
            sHi = MathF.Max(sHi, sr);
        }
        Check($"hollow: SurfaceRadiusAt follows the lumps (spread {sHi - sLo:0} rings)",
            sHi - sLo > 25f);

        // The Great Geode: the same seed without the flag must have far fewer open tiles in
        // the geode's depth band (55-70 legacy tiles down, ±2 for the carve radius).
        int OpenInBand(Planet p)
        {
            var lo = Math.Max(0, p.SurfaceRing - (int)(74 * Planet.LegacyTileScale));
            var hi = p.SurfaceRing - (int)(51 * Planet.LegacyTileScale);
            var n = 0;
            for (var r = lo; r < hi; r++)
                for (var t = 0; t < p.TilesAt(r); t++)
                    if (p.Get(r, t) == TileKind.Sky) n++;
            return n;
        }
        var flat = WorldGen.Generate(1234, def with { GreatGeode = false });
        var opened = OpenInBand(world) - OpenInBand(flat);
        Check($"hollow: the Great Geode opens a vast cavern (+{opened} open tiles in band)",
            opened > 600);

        // The prospecting promise: voidstone exists outside the Rift here (embedded gems).
        var voidstone = 0;
        foreach (var (x, y) in world.AllTiles())
            if (world.GemAt(x, y) == TileKind.Voidstone) voidstone++;
        Check($"hollow: voidstone findable outside the Rift ({voidstone} gems)", voidstone > 0);

        // Vac-suit gating in space. Activate appends the Hollow to the chain (this test
        // runs last — see the Run dispatch — because the append can't be undone).
        World.PlanetDefs.Activate(World.PlanetDefs.Classic);
        var sim = new Space.SpaceSim { AsteroidTarget = 0 };
        var hp = sim.Planets.Find(p => p.Def.Id == "hollow");
        Check("hollow: rides the outer belt orbit", hp is not null
            && MathF.Abs(hp.OrbitRadius - Space.SpaceSim.BeltOrbitRadius) < 1f);
        if (hp is not null)
        {
            var outward = Vector2.Normalize(hp.Pos);
            sim.VacSuitLocked = true;
            sim.ShipPos = hp.Pos + outward * (hp.BodyRadius + 30f);
            Check("hollow: no atmosphere contact without the vac suit",
                sim.AtmosphereContact() is null);
            sim.ShipPos = hp.Pos + outward * (hp.BodyRadius + 4f);
            sim.ShipVel = -outward * 120f;
            sim.Update(1f / 60f, 0f, thrust: false, brake: false);
            Check("hollow: airless rock bounces the suitless ship",
                (sim.ShipPos - hp.Pos).Length() - hp.BodyRadius > 14f);
            sim.VacSuitLocked = false;
            sim.ShipPos = hp.Pos + Vector2.Normalize(sim.ShipPos - hp.Pos) * (hp.BodyRadius + 30f);
            Check("hollow: suit aboard = atmosphere contact clears",
                sim.AtmosphereContact()?.Def.Id == "hollow");
        }

        // Belt natives. The moonlet falls into orbit around the dwarf in open sky and stays
        // on the leash; the vac leech siphons the air tank on contact.
        var cells = new Cells(world);
        var physics = new Physics(world, cells);
        var surface = SpawnDirector.FindSurfaceSpawn(world, -MathF.PI / 2f, world.Radius);
        var skyUp = world.UpAt(surface);
        var dwarf = new Player(surface + skyUp * 140f);
        var moonlet = new Creature(dwarf.Position + skyUp * 60f, CreatureKind.Moonlet);
        for (var i = 0; i < 60 * 5; i++)
            moonlet.Update(1f / 60f, world, physics, cells, dwarf);
        var leash = (moonlet.Position - dwarf.Position).Length();
        Check($"hollow: moonlet holds orbit around the dwarf ({leash:0} px)",
            leash > 20f && leash < 260f);

        // Open air (like the moonlet above): surface spawn points can wedge a small body
        // against slope tiles and the escape push throws off the clamp below.
        var victim = new Player(surface + skyUp * 120f) { Oxygen = 100f };
        var leech = new Creature(victim.Position, CreatureKind.VacLeech);
        for (var i = 0; i < 60; i++)
        {
            leech.Position = victim.Position;   // stay clamped on for the whole second
            leech.Update(1f / 60f, world, physics, cells, victim);
        }
        Check($"hollow: vac leech siphons the air tank ({victim.Oxygen:0.0} left)",
            victim.Oxygen < 92f);

        // The void barnacle: settles onto the rock, cements there, and reels exposed prey
        // toward its shell (the settle-then-root is why it gets a beat before the pull).
        var barnacle = new Creature(surface, CreatureKind.VoidBarnacle);
        var reeled = new Player(surface + skyUp * 70f);
        for (var i = 0; i < 90; i++)
            barnacle.Update(1f / 60f, world, physics, cells, reeled);
        Check($"hollow: void barnacle reels prey in (pull {Vector2.Dot(reeled.Velocity, -skyUp):0} px/s)",
            Vector2.Dot(reeled.Velocity, -skyUp) > 20f);

        // The roster is CLOSED: seed the whole world's fauna and run the spawner hard —
        // every creature that appears must be a belt native. No grubs. No slimes. Nothing
        // that breathes.
        var beltRun = new Session(def)
        {
            Planet = world, Cells = cells, Physics = physics,
            Player = new Player(surface),
        };
        SpawnDirector.SpawnInitialFauna(beltRun);
        for (var i = 0; i < 40; i++)
        {
            beltRun.SpawnTimer = 0f;
            beltRun.FaunaTimer = 0f;
            SpawnDirector.Update(0.05f, beltRun);
        }
        var natives = 0;
        var outsiders = 0;
        foreach (var c in beltRun.Creatures)
        {
            if (c.Kind is CreatureKind.Moonlet or CreatureKind.VacLeech or CreatureKind.Glimmermaw
                or CreatureKind.StarJelly or CreatureKind.VoidBarnacle) natives++;
            else outsiders++;
        }
        Check($"hollow: only belt natives spawn ({natives} natives, {outsiders} outsiders)",
            natives > 0 && outsiders == 0);

        // The Starspawn: its egg is buried near the core, and once hatched it swims through
        // solid rock toward prey, spitting void volleys and arming the gravity well.
        var octo = new Entities.Titan(world, 1.3f, TitanKind.CosmicOctopus);
        Check($"hollow: starspawn egg buried near the core "
            + $"({(octo.Position - world.Center).Length() / Planet.TileSize:0} tiles out)",
            (octo.Position - world.Center).Length() < (Planet.RingMin + 60) * Planet.TileSize);
        octo.Hatch();
        var preyDir = new Vector2(MathF.Cos(1.3f), MathF.Sin(1.3f));
        var prey = world.Center + preyDir * ((Planet.RingMin + 140) * Planet.TileSize);
        var startDist = (octo.Position - prey).Length();
        var octoShots = new List<Entities.TitanProjectile>();
        var octoBoulders = new List<Entities.FallingBoulder>();
        var sawWell = false;
        for (var i = 0; i < 60 * 12; i++)
        {
            octo.OnDamage();   // hold aggro for the whole window
            octo.Update(1f / 60f, world, physics, cells, prey, octoBoulders, octoShots);
            if (octo.PendingGravityWell is not null) { sawWell = true; octo.PendingGravityWell = null; }
        }
        var endDist = (octo.Position - prey).Length();
        Check($"hollow: starspawn swims through rock toward prey ({startDist:0} → {endDist:0} px)",
            endDist < startDist - 100f);
        Check("hollow: starspawn spits void volleys",
            octoShots.Exists(s => s.Kind == Entities.TitanShotKind.Void));
        Check("hollow: starspawn arms its gravity well", sawWell);
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
            var r = planet.SurfaceRing - 78 + rng.Next(70); // depth ~4..39 legacy tiles below baseline surface
            var t = rng.Next(planet.TilesAt(r));
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
