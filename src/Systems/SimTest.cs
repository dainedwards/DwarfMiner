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
        foreach (var kind in new[] { CreatureKind.Grub, CreatureKind.Skitterer, CreatureKind.Grazer, CreatureKind.CaveEye,
                                     CreatureKind.SporeBat, CreatureKind.CrystalCrawler, CreatureKind.VoidWraith,
                                     CreatureKind.CaveSlime, CreatureKind.Slimelet, CreatureKind.AcidSpitter,
                                     CreatureKind.BomberBeetle, CreatureKind.SnapperVine, CreatureKind.RockMimic })
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
        TestSkyCollapse();
        TestVolcanoes();
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
            Check("plangen: campaign = 7 worlds + the Rift", chainA.Length == 8 && chainA[7].Id == "rift");
            var same = true;
            for (var i = 0; i < 8; i++) same &= chainA[i].Id == chainB[i].Id && chainA[i].Titan == chainB[i].Titan;
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
            // Size actually changes the tile grid.
            Check($"plangen: SizeScale drives real ring counts ({oceanWorld.Rings} vs {plainWorld.Rings})",
                WorldGen.Generate(9, chainA[6]).Rings > WorldGen.Generate(9, chainA[0]).Rings);
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
            // A stone slab at body height (16-32px up, inside the plow radius) and a stone
            // row well below the body centre (the floor sector), then tick once. Tiles are
            // placed through world coordinates — ring tile counts differ, so reusing one
            // angular index across rings would drift the slab away from the body.
            var bup = pp.UpAt(boss.Position);
            var bright = new Vector2(-bup.Y, bup.X);
            var slab = new System.Collections.Generic.List<(int x, int y)>();
            var floor = new System.Collections.Generic.List<(int x, int y)>();
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = 2; dx <= 4; dx++)
                    slab.Add(pp.WorldToTile(boss.Position + bup * (dx * Planet.TileSize)
                                                          + bright * (dy * Planet.TileSize)));
                floor.Add(pp.WorldToTile(boss.Position - bup * (5 * Planet.TileSize)
                                                       + bright * (dy * Planet.TileSize)));
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
        const int slabH = 8, slabW = 16;   // 128 tiles — well past the stone budget of 48

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

    /// <summary>Volcanoes: fire worlds raise lava-primed cones whose plumbing reaches a deep
    /// chamber; the acid flag reroutes the fluid to the acid seed channel.</summary>
    private static void TestVolcanoes()
    {
        var ember = WorldGen.Generate(5, PlanetDefs.ById("ember"));
        Check($"volcano: ember world raises primed volcanoes ({ember.VolcanoVents.Count} vents, "
            + $"{ember.LavaSeeds.Count} lava sites)",
            ember.VolcanoVents.Count >= 1 && ember.LavaSeeds.Count > 0);
        var deep = false;
        foreach (var (x, _) in ember.LavaSeeds) deep |= x < ember.SurfaceRing - 40;
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

        // Tier-III effects: jetpack charge/climb curve, magnet reach, O2 ceiling, hull 9,
        // and the Aegis shield recharge.
        var tiers = new Player(Vector2.Zero) { HasJetpack = true };
        var cap1 = tiers.JetChargeCap;
        tiers.JetTier2 = true;
        var cap2 = tiers.JetChargeCap;
        tiers.JetTier3 = true;
        Check("tiers: jetpack charge 1x/2x/3x",
            MathF.Abs(cap1 - 2.6f) < 0.01f && MathF.Abs(cap2 - 5.2f) < 0.01f
            && MathF.Abs(tiers.JetChargeCap - 7.8f) < 0.01f);
        tiers.HasMagnet = true;
        var reach1 = tiers.PickupReach;
        tiers.MagnetTier2 = true;
        Check("tiers: magnet reach 16 then 30", reach1 == 16f && tiers.PickupReach == 30f);
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
        var verdantWorld = Space.Survey.WorldFor(World.PlanetDefs.ById("verdant"));
        var riftWorld = Space.Survey.WorldFor(World.PlanetDefs.ById("rift"));
        Check("content: emerald seams on verdant", CountKind(verdantWorld, TileKind.Emerald) > 0,
            $"{CountKind(verdantWorld, TileKind.Emerald)} tiles");
        Check("content: voidstone only in the rift",
            CountKind(riftWorld, TileKind.Voidstone) > 0 && CountKind(verdantWorld, TileKind.Voidstone) == 0,
            $"rift {CountKind(riftWorld, TileKind.Voidstone)}");
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
