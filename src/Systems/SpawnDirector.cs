using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Keeps the player's neighbourhood populated: cave dwellers on a fast timer, surface herds
/// and sky flyers on a slow one. Spawning is budgeted on the *local* population, not the
/// planet-wide one — a planet-wide cap fills up with creatures scattered across ~150k
/// underground tiles and the player never meets any. Stateless: the timers live in Session.
/// </summary>
public static class SpawnDirector
{
    /// <summary>Per-frame population upkeep. The world's population is rolled ONCE at load
    /// (PopulateWorld) — nothing pops into existence around the player any more. The only
    /// source of new creatures afterwards is the physical spawners: goo piles bubble out
    /// slimes, warren doors let lizardmen trickle out, city homes send their households
    /// into the street. Each ticks only inside its activity band and stops while its brood
    /// cap is alive nearby.</summary>
    public static void Update(float dt, Session run)
    {
        foreach (var s in run.Spawners)
        {
            s.Timer -= dt;
            if (s.Timer > 0f) continue;
            var toPlayer = (s.Position - run.Player.Position).Length();
            // Too close = visible pop-in; too far = frozen anyway (creature freeze band).
            if (toPlayer < 100f || toPlayer > 750f) { s.Timer = 3f; continue; }

            var (kind, cap, interval) = s.Kind switch
            {
                SpawnerKind.GooPile    => (CreatureKind.CaveSlime, 3, 22f),
                SpawnerKind.LizardDoor => (CreatureKind.Lizardman, 4, 54f),   // low rate, but in packs
                _                      => (CreatureKind.Civilian, 8, 20f),     // packed streets
            };
            if (CountKindNear(run, s.Position, 240f, kind) >= cap) { s.Timer = 8f; continue; }

            var up = run.Planet.UpAt(s.Position);
            var right = new Vector2(-up.Y, up.X);
            // Lizardmen are a pack — they trickle out of the warren door two at a time, not one
            // lone guard; everything else emerges singly.
            var pack = s.Kind == SpawnerKind.LizardDoor ? 2 : 1;
            for (var pi = 0; pi < pack; pi++)
            {
                var at = s.Position + up * 4f + right * ((pi - (pack - 1) * 0.5f) * 10f);
                var c = new Creature(at, kind) { Resident = true };
                if (HazardRejectsSpawn(run, c.Position, c)) continue;
                ClearSpawnSpace(run, c.Position, c.Radius);
                run.Creatures.Add(c);
            }
            s.Timer = interval * (0.75f + (float)Random.Shared.NextDouble() * 0.5f);
        }
    }

    /// <summary>Living population of one kind around a point — the per-spawner brood cap.</summary>
    private static int CountKindNear(Session run, Vector2 pos, float radius, CreatureKind kind)
    {
        var rSq = radius * radius;
        var n = 0;
        foreach (var c in run.Creatures)
            if (c.Kind == kind && (c.Position - pos).LengthSquared() < rSq) n++;
        return n;
    }

    /// <summary>Populate the fresh planet with ambient life. Everything is rolled here, once
    /// — the planet-wide RESIDENT census (cities staffed, warrens garrisoned, lakes stocked,
    /// caves crawling, skies flown, herds scattered) plus the physical spawners. After this,
    /// only the spawners make new creatures.</summary>
    public static void SpawnInitialFauna(Session run) => PopulateWorld(run);

    /// <summary>Seed the whole planet's population up front (run start AND resume — creatures
    /// aren't saved): every city address staffed, saucers on station over each district, a
    /// guard pair in every warren hall, each lake stocked, and wild herds scattered across
    /// the surface. Everything spawns as a <see cref="Creature.Resident"/> so it exists
    /// before the player arrives and is never distance-culled — the dynamic spawners below
    /// only top up wildlife, and never inside a city.</summary>
    public static void PopulateWorld(Session run)
    {
        var planet = run.Planet;

        // City dwellers: staff the addresses (doorways + apartments), mostly citizens with
        // a peacekeeper watch mixed in. Streets are PACKED now — ~3× the old crowd — so a
        // metropolis teems with civilians.
        var budget = 260;
        foreach (var (r, t) in planet.CitySpawns)
        {
            if (budget <= 0) break;
            var home = planet.TileToWorld(r, t);
            var up = planet.UpAt(home);
            var homeRight = new Vector2(-up.Y, up.X);
            // Pack each address with a little crowd — ~3× the old one-per-door population.
            var crowd = 2 + Random.Shared.Next(2);   // 2-3 per address
            for (var n = 0; n < crowd && budget > 0; n++)
            {
                var pos = home + homeRight * (((float)Random.Shared.NextDouble() - 0.5f) * 10f);
                var kind = Random.Shared.Next(5) == 0 ? CreatureKind.Peacekeeper : CreatureKind.Civilian;
                var c = new Creature(pos, kind) { Resident = true };
                ClearSpawnSpace(run, pos, c.Radius);
                run.Creatures.Add(c);
                budget--;
            }
        }

        // Air patrol: six saucers on station over every district — a dense picket line over
        // the skyline — plus one big command saucer (multi-laser + tractor beam) per city.
        var bigDropped = false;
        foreach (var (ang, half) in planet.CityDistricts)
        {
            for (var i = 0; i < 6; i++)
            {
                var a = ang + ((float)Random.Shared.NextDouble() * 2f - 1f) * half;
                var ground = FindSurfaceSpawn(planet, a, planet.Radius);
                var alt = (ground - planet.Center).Length() + 80f + (float)Random.Shared.NextDouble() * 120f;
                alt = MathF.Min(alt, (planet.Radius - 12) * Planet.TileSize);
                var pos = planet.Center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * alt;
                run.Creatures.Add(new Creature(pos, CreatureKind.Saucer) { Resident = true });
            }
            // One capital-ship saucer, cruising higher over the largest district.
            if (!bigDropped)
            {
                bigDropped = true;
                var ground = FindSurfaceSpawn(planet, ang, planet.Radius);
                var alt = MathF.Min((ground - planet.Center).Length() + 170f,
                    (planet.Radius - 10) * Planet.TileSize);
                var pos = planet.Center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * alt;
                run.Creatures.Add(new Creature(pos, CreatureKind.BigSaucer) { Resident = true });
            }
        }

        // Warren garrisons: a guard pack (2-3) in every hall — the warren is a tight village,
        // so the halls are held in numbers, not by a lone sentry.
        foreach (var (dr, dt) in planet.LizardDens)
        {
            var den = planet.TileToWorld(dr, dt);
            var denRight = new Vector2(-planet.UpAt(den).Y, planet.UpAt(den).X);
            var garrison = 2 + Random.Shared.Next(2);
            for (var i = 0; i < garrison; i++)
            {
                var post = den + denRight * (((float)Random.Shared.NextDouble() - 0.5f) * 36f);
                var g = new Creature(post, CreatureKind.Lizardman) { Resident = true };
                if (HazardRejectsSpawn(run, post, g)) continue;   // not into a lava hall breach
                ClearSpawnSpace(run, post, g.Radius);
                run.Creatures.Add(g);
            }
        }

        // Lake fauna: sweep the whole ring for open water and stock every find.
        if (run.Def.HasWater)
        {
            var aquatics = 0;
            for (var a = 0f; a < MathHelper.TwoPi && aquatics < 12; a += 0.22f)
            {
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                for (var d = planet.Radius + 40; d > 20; d--)
                {
                    var p = planet.Center + dir * (d * Planet.TileSize);
                    if (planet.IsSolidAt(p)) break;
                    if (run.Cells.CountWaterNear(p, 3f) < 3) continue;
                    var splash = p - dir * 10f;
                    var deep = run.Cells.CountWaterNear(splash - dir * 14f, 5f) >= 8;
                    var kind = LakeKindFor(deep);
                    run.Creatures.Add(new Creature(splash, kind) { Resident = true });
                    aquatics++;
                    break;
                }
            }
        }

        // Wild herds scattered planet-wide, clear of the cities.
        for (var a = 0.15f; a < MathHelper.TwoPi; a += 0.4f)
        {
            if (Random.Shared.Next(2) == 0) continue;
            var jig = a + ((float)Random.Shared.NextDouble() - 0.5f) * 0.2f;
            var pos = FindSurfaceSpawn(planet, jig, planet.Radius);
            if (InCityDistrict(planet, pos)) continue;
            if (SurfaceFaunaFor(run.Def) is not { } kind) continue;
            var c = new Creature(pos, kind) { Resident = true };
            if (HazardRejectsSpawn(run, pos, c)) continue;   // not into a surface acid/lava pond
            ClearSpawnSpace(run, pos, c.Radius);
            run.Creatures.Add(c);
        }

        // Cave dwellers: the whole underground population rolled once, planet-wide — random
        // cave spots with the usual depth-banded rosters, all frozen Residents until the
        // player digs into their neighbourhood. (This replaces the old near-player trickle
        // spawner entirely.)
        var caveBudget = 55;
        for (var attempt = 0; attempt < 1100 && caveBudget > 0; attempt++)
        {
            if (RollCaveSpot(run) is not { } spot) continue;
            SpawnAt(run, spot, connected: true, resident: true);
            caveBudget--;
        }

        // Sky fauna on station around the whole planet (they used to trickle in near the
        // player; now the flock exists from the start). The Rift's one neutral species is
        // the null moth; airless rocks get drifting star jellies.
        for (var a = 0.1f; a < MathHelper.TwoPi; a += 0.55f)
        {
            if (Random.Shared.Next(3) == 0) continue;
            var ground = FindSurfaceSpawn(planet, a, planet.Radius);
            var up2 = planet.UpAt(ground);
            var kind = run.Def.Biome == "rift" ? CreatureKind.NullMoth
                : run.Def.Biome is "belt" or "moon" ? CreatureKind.StarJelly
                : Random.Shared.NextDouble() < 0.65 ? CreatureKind.SkyMoth
                : CreatureKind.SkyStinger;
            run.Creatures.Add(new Creature(
                ground + up2 * (50f + (float)Random.Shared.NextDouble() * 90f), kind)
            { Resident = true });
        }

        // ── Physical spawners — the only post-load source of new creatures. ──────────
        run.Spawners.Clear();
        // Goo piles: slime mounds settled on cave floors.
        var goo = 0;
        for (var attempt = 0; attempt < 500 && goo < 10; attempt++)
        {
            if (RollCaveSpot(run) is not { } spot) continue;
            // Settle the pile onto the cave floor below the spot.
            var up2 = planet.UpAt(spot);
            var floor = spot;
            var found = false;
            for (var d = 4f; d <= 48f; d += 4f)
            {
                if (!planet.IsSolidAt(spot - up2 * d)) continue;
                floor = spot - up2 * (d - 4f);
                found = true;
                break;
            }
            if (!found) continue;
            run.Spawners.Add(new Spawner(floor, SpawnerKind.GooPile));
            goo++;
        }
        // Lizard doors: one brick doorway per warren hall, settled onto the hall floor —
        // the warren's slow trickle.
        foreach (var (dr2, dt2) in planet.LizardDens)
        {
            var den = planet.TileToWorld(dr2, dt2);
            var denUp = planet.UpAt(den);
            for (var d = 4f; d <= 60f; d += 4f)
            {
                if (!planet.IsSolidAt(den - denUp * d)) continue;
                den -= denUp * (d - 4f);
                break;
            }
            run.Spawners.Add(new Spawner(den, SpawnerKind.LizardDoor));
        }
        // Alien homes: every sixth city address is a marked household that keeps sending
        // its people into the street.
        var addr = 0;
        foreach (var (hr, ht) in planet.CitySpawns)
            if (addr++ % 6 == 0)
                run.Spawners.Add(new Spawner(planet.TileToWorld(hr, ht), SpawnerKind.AlienHome));
    }

    /// <summary>One random underground cave position: open foreground with a natural rock
    /// wall behind (not open sky, not a building or warren interior). Null on a miss —
    /// callers loop attempts.</summary>
    private static Vector2? RollCaveSpot(Session run)
    {
        var planet = run.Planet;
        var a = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
        var frac = 0.42f + (float)Random.Shared.NextDouble() * 0.52f;
        var pos = planet.Center + new Vector2(MathF.Cos(a), MathF.Sin(a))
            * planet.Radius * frac * Planet.TileSize;
        var (r, t) = planet.WorldToTile(pos);
        if (r < 1 || r >= planet.Rings) return null;
        if (planet.Get(r, t) != TileKind.Sky) return null;
        var wall = planet.GetWall(r, t);
        if (wall is TileKind.Sky or TileKind.AlienAlloy or TileKind.CityGlass
            or TileKind.LizardBrick) return null;
        return pos;
    }

    /// <summary>True when the bearing of <paramref name="pos"/> falls inside a city district
    /// (plus a margin). The dynamic spawners keep out of the cities entirely — city life is
    /// seeded once by <see cref="PopulateWorld"/>, so nothing ever pops into existence on a
    /// watched street or inside an apartment.</summary>
    private static bool InCityDistrict(Planet planet, Vector2 pos, float margin = 0.06f)
    {
        if (planet.CityDistricts.Count == 0) return false;
        var rel = pos - planet.Center;
        var a = MathF.Atan2(rel.Y, rel.X);
        foreach (var (ang, half) in planet.CityDistricts)
            if (MathF.Abs(MathHelper.WrapAngle(a - ang)) < half + margin) return true;
        return false;
    }

    /// <summary>Walk down from far above the given angle until the first solid tile, then
    /// float a few pixels above it — used for the player spawn and surface fauna.</summary>
    public static Vector2 FindSurfaceSpawn(Planet planet, float angle, int radius)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        // Start well above the highest possible peak and step inward until we hit solid GROUND
        // — flora, trees and water plants are transparent here (they sit on the ground; a
        // creature spawns on the soil, not on a tree top).
        for (var d = radius + 60; d > 20; d--)
        {
            var p = planet.Center + dir * (d * Planet.TileSize);
            var (tx, ty) = planet.WorldToTile(p);
            var k = planet.Get(tx, ty);
            if (Tiles.IsSolid(k) && !Tiles.IsFlora(k))
                return p - dir * (Planet.TileSize * 1.5f);
        }
        return planet.Center + dir * ((radius + 24) * Planet.TileSize);
    }

    /// <summary>True when this spot holds a body-contact hazard cell (lava/acid/water) the
    /// creature can't survive — the spawner skips it, so a land animal is never dropped into a
    /// lava lake or an acid pond, and a non-swimmer is never spawned underwater to drown. Immune
    /// natives (magma slug in lava, acid strider in acid, swimmers in water) are allowed in.</summary>
    private static bool HazardRejectsSpawn(Session run, Vector2 pos, Creature c)
    {
        var probe = c.Radius + 2f;
        var (lava, acid, _, _) = run.Cells.SampleHazardsNear(pos, probe);
        if (lava > 0 && !c.ImmuneTo(Material.Lava)) return true;
        if (acid > 0 && !c.ImmuneTo(Material.Acid)) return true;
        if (!c.ImmuneTo(Material.Water) && run.Cells.CountWaterNear(pos, probe) > 0) return true;
        return false;
    }

    /// <summary>Rooted / lying-in-wait kinds. They never seek the player, so they must not
    /// eat the cave-population budget — a cap full of vines and disguised mimics plays as an
    /// empty planet. They get their own small allowance instead (see TrySpawnCreature).</summary>
    private static bool IsStationary(CreatureKind k) =>
        k is CreatureKind.SnapperVine or CreatureKind.RockMimic or CreatureKind.VoidBarnacle;

    /// <summary>Population count within a radius of the player, filtered by habitat kind.
    /// Stationary ambushers are excluded from the cave count — see <see cref="IsStationary"/>.</summary>
    private static int CountKindsNear(Session run, float radius,
        bool cave = false, bool surface = false, bool sky = false, bool stationary = false,
        bool water = false)
    {
        // Census placement runs on the build thread before the Player exists — nothing is
        // "near the player" yet.
        if (run.Player is null) return 0;
        var rSq = radius * radius;
        var n = 0;
        foreach (var c in run.Creatures)
            if ((cave && c.IsCaveKind && !IsStationary(c.Kind))
                || (surface && c.IsSurfaceKind) || (sky && c.IsSkyKind)
                || (water && c.IsWaterKind)
                || (stationary && IsStationary(c.Kind)))
                if ((c.Position - run.Player.Position).LengthSquared() < rSq)
                    n++;
        return n;
    }

    /// <summary>Aquatic fauna: find open water along a bearing near the player — walk down
    /// the radial from the sky; hitting dry ground first means no lake on that bearing —
    /// and drop a water-only creature into it. Whales want room, so they take the deeper
    /// finds; crabs take whatever puddle is going.</summary>
    private static void TrySpawnAquatic(Session run)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var angle = NearbySurfaceAngle(run, 200f, 600f);
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            Vector2? found = null;
            for (var d = run.Planet.Radius + 60; d > 20; d--)
            {
                var p = run.Planet.Center + dir * (d * Planet.TileSize);
                if (run.Planet.IsSolidAt(p)) break;                    // dry shore — no lake here
                if (run.Cells.CountWaterNear(p, 3f) >= 3) { found = p - dir * 10f; break; }
            }
            if (found is not { } splash) continue;
            if ((splash - run.Player.Position).Length() < 160f) continue;

            // Depth gate: deep basins host the leviathans and the big predators; shallows
            // get crabs (and the odd shark cruising in).
            var deep = run.Cells.CountWaterNear(splash - dir * 14f, 5f) >= 8;
            var c = new Creature(splash, LakeKindFor(deep));
            run.Creatures.Add(c);
            return;
        }
    }

    /// <summary>Roll a lake resident. Deep water is the dangerous water: sharks, the anglerfish
    /// gulper, and the brine-spitter share it with the gentle whale; shallows are crabs and
    /// the odd shark. Roughly half the deep rolls are now hostile sea monsters.</summary>
    private static CreatureKind LakeKindFor(bool deep)
    {
        var r = Random.Shared.NextDouble();
        if (deep)
            return r < 0.24 ? CreatureKind.AlienWhale
                 : r < 0.50 ? CreatureKind.AlienShark
                 : r < 0.68 ? CreatureKind.Gulper
                 : r < 0.82 ? CreatureKind.Brinespitter
                 : CreatureKind.AlienCrab;
        return r < 0.22 ? CreatureKind.AlienShark : CreatureKind.AlienCrab;
    }

    private static void TrySpawnCreature(Session run)
    {
        var planet = run.Planet;

        // Preferred: a cave tile *air-connected* to the player. A creature placed there can
        // physically walk/fly to the dwarf, so the spawn becomes an encounter instead of a
        // ghost in a sealed pocket the player will never open.
        var spots = ReachableCaveSpots(run, 200f, 450f);
        if (spots.Count > 0)
        {
            var (r, t) = spots[Random.Shared.Next(spots.Count)];
            SpawnAt(run, planet.TileToWorld(r, t), connected: true);
            return;
        }

        // Fallback (no connected caves in range — fresh surface landing, or the player has
        // sealed themselves in): pick any cave tile in the donut and spawn a tunneller —
        // the one class that can open its own way out of a sealed pocket. The airless
        // worlds (belt asteroid, cratered moon) have no tunneller natives (and NOTHING
        // ordinary lives there), so they simply skip unconnected spawns — their population
        // is what can reach you.
        if (run.Def.Biome is "belt" or "moon") return;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var a = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var d = 200f + (float)Random.Shared.NextDouble() * 250f;
            var candidate = run.Player.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            var (r, t) = planet.WorldToTile(candidate);
            if (r < 0 || r >= planet.Rings) continue;
            if (planet.Get(r, t) != TileKind.Sky) continue;
            var wall = planet.GetWall(r, t);
            if (wall is TileKind.Sky or TileKind.AlienAlloy or TileKind.CityGlass) continue;
            var pos = planet.TileToWorld(r, t);
            if ((pos - run.Player.Position).Length() < 180f) continue;
            SpawnAt(run, pos, connected: false);
            return;
        }
    }

    /// <summary>Roll a kind for the depth band at <paramref name="pos"/> and spawn it.
    /// Unconnected sites only ever get diggers — anything else would be stranded.
    /// <paramref name="resident"/> marks census placements: frozen at distance, never
    /// culled (see Creature.Resident).</summary>
    private static void SpawnAt(Session run, Vector2 pos, bool connected, bool resident = false)
    {
        var planet = run.Planet;
        // Roster shifts with depth: slimes, moles and skitterers riddle the upper crust
        // (with snapper vines in the pockets), the mid-band belongs to the diggers plus
        // the nasty tricks (spitters, bombers), and the lava zone is magma slugs, spitter
        // batteries and hardened delver war-parties. Rock mimics are a rare bad day at
        // any depth below the surface.
        var fromCenter = (pos - planet.Center).Length() / Planet.TileSize;
        var depth = (planet.SurfaceRing - (fromCenter - Planet.RingMin)) / Planet.LegacyTileScale; // legacy 8-px tiles below the baseline surface
        var roll = Random.Shared.NextDouble();
        CreatureKind kind;

        // The airless rosters are CLOSED: only vacuum natives spawn in the Hollow's and the
        // cratered moon's caves — no grubs, slimes, diggers, mimics or any other ordinary
        // planet life. Nothing here ever breathed. The moon shares the belt's moonlets and
        // vac leeches but swaps the ambushers for its own crystal selenites.
        // (Unconnected pockets already returned in TrySpawnCreature.)
        if (run.Def.Biome is "belt" or "moon")
        {
            var native = run.Def.Biome == "moon"
                ? roll < 0.40 ? CreatureKind.Selenite
                    : roll < 0.70 ? CreatureKind.VacLeech
                    : CreatureKind.Moonlet
                : roll < 0.26 ? CreatureKind.Moonlet
                    : roll < 0.52 ? CreatureKind.VacLeech
                    : roll < 0.74 ? CreatureKind.Glimmermaw
                    : CreatureKind.VoidBarnacle;
            // Barnacles ride the stationary-ambusher allowance, same as vines/mimics.
            if (native == CreatureKind.VoidBarnacle
                && CountKindsNear(run, 550f, stationary: true) >= 3)
                native = CreatureKind.VacLeech;
            var pos2 = pos;
            if (native == CreatureKind.VoidBarnacle)
            {
                // Cement the barnacle against the cave floor instead of mid-air.
                var bUp = planet.UpAt(pos);
                for (var d = 4f; d <= 20f; d += 4f)
                {
                    if (!planet.IsSolidAt(pos - bUp * d)) continue;
                    pos2 = pos - bUp * (d - 5f);
                    break;
                }
            }
            var beltC = new Creature(pos2, native) { Resident = resident };
            // Barnacles WANT to touch the rock they cement to — no spawn-space carve.
            if (native != CreatureKind.VoidBarnacle) ClearSpawnSpace(run, pos2, beltC.Radius);
            run.Creatures.Add(beltC);
            return;
        }
        if (!connected)
        {
            // Sealed pocket: tunnellers only — the one habitat class that can open its own
            // way out. (Since the provocation gate they wander-dig rather than beeline at
            // the dwarf, so these are ambience and future ambushes, not homing threats.)
            kind = depth > 45f ? (roll < 0.5 ? CreatureKind.HornedDelver : CreatureKind.Centipede)
                 : roll < 0.35 ? CreatureKind.Borer
                 : roll < 0.60 ? CreatureKind.MoleBeast
                 : roll < 0.80 ? CreatureKind.Centipede
                 : CreatureKind.HornedDelver;
        }
        else if (depth > 45f)
        {
            // Deep band gets the heavy bandits: the pyro brute and the jetpack raider.
            kind = roll < 0.26 ? CreatureKind.MagmaSlug
                 : roll < 0.40 ? CreatureKind.HornedDelver
                 : roll < 0.51 ? CreatureKind.Centipede
                 : roll < 0.62 ? CreatureKind.AcidSpitter
                 : roll < 0.71 ? CreatureKind.CaveEye
                 : roll < 0.79 ? CreatureKind.BomberBeetle
                 : roll < 0.86 ? CreatureKind.Pyro
                 : roll < 0.92 ? CreatureKind.Raider
                 : roll < 0.97 ? CreatureKind.Grub
                 : CreatureKind.RockMimic;
        }
        else if (depth > 20f)
        {
            // Mid band: marauder gunmen prowl the tunnels, the odd raider drops in.
            kind = roll < 0.13 ? CreatureKind.HornedDelver
                 : roll < 0.24 ? CreatureKind.Centipede
                 : roll < 0.35 ? CreatureKind.Borer
                 : roll < 0.44 ? CreatureKind.MoleBeast
                 : roll < 0.53 ? CreatureKind.CaveSlime
                 : roll < 0.62 ? CreatureKind.AcidSpitter
                 : roll < 0.69 ? CreatureKind.BomberBeetle
                 : roll < 0.77 ? CreatureKind.Marauder
                 : roll < 0.82 ? CreatureKind.Raider
                 : roll < 0.88 ? CreatureKind.CaveEye
                 : roll < 0.92 ? CreatureKind.Grub
                 : roll < 0.97 ? CreatureKind.SnapperVine
                 : CreatureKind.RockMimic;
        }
        else
        {
            kind = roll < 0.20 ? CreatureKind.Skitterer
                 : roll < 0.38 ? CreatureKind.CaveSlime
                 : roll < 0.52 ? CreatureKind.MoleBeast
                 : roll < 0.62 ? CreatureKind.Grub
                 : roll < 0.72 ? CreatureKind.CaveEye
                 : roll < 0.81 ? CreatureKind.SnapperVine
                 : roll < 0.88 ? CreatureKind.HornedDelver
                 : roll < 0.94 ? CreatureKind.Marauder
                 : roll < 0.98 ? CreatureKind.BomberBeetle
                 : CreatureKind.RockMimic;
        }

        // Stationary ambushers have their own small allowance (they don't count toward
        // the cave cap); when it's full, the slot becomes a hunter from the same band so
        // the spawn tick is never wasted on a fourth vine.
        if (IsStationary(kind) && CountKindsNear(run, 550f, stationary: true) >= 3)
        {
            kind = depth > 45f ? CreatureKind.HornedDelver
                 : depth > 20f ? CreatureKind.Borer
                 : CreatureKind.CaveSlime;
        }

        // Biome specials override the generic roster: wraiths haunt the Rift at every
        // depth, crawlers stalk the deeps of crystal-pocket worlds, and spore bats flit
        // through the shallow caves of worlds with fungal groves. Connected sites only —
        // none of the specials can dig out of a sealed pocket.
        if (connected)
        {
            var special = Random.Shared.NextDouble();
            if (run.Def.Id == "rift" && special < 0.25)
                kind = CreatureKind.VoidWraith;
            else if (run.Def.CrystalPockets > 0 && depth > 40f && special < 0.18)
                kind = CreatureKind.CrystalCrawler;
            else if (run.Def.FungalPockets > 0 && depth < 30f && special < 0.22)
                kind = CreatureKind.SporeBat;

            // Warren territory trumps everything: a cave spawn near a lizard-city den is
            // most likely a guard on patrol, so the halls stay garrisoned however often
            // the player clears them.
            if (NearLizardDen(run.Planet, pos, 300f) && Random.Shared.NextDouble() < 0.6)
                kind = CreatureKind.Lizardman;
        }

        var c = new Creature(pos, kind) { Resident = resident };
        if (HazardRejectsSpawn(run, pos, c)) return;   // don't hatch it inside lava/acid/water
        ClearSpawnSpace(run, pos, c.Radius);
        run.Creatures.Add(c);
    }

    /// <summary>True when <paramref name="pos"/> sits within <paramref name="range"/> px of a
    /// lizard-city chamber heart. Dens number a handful per planet, so a linear scan is free.</summary>
    private static bool NearLizardDen(Planet planet, Vector2 pos, float range)
    {
        var rSq = range * range;
        foreach (var (dr, dt) in planet.LizardDens)
            if ((planet.TileToWorld(dr, dt) - pos).LengthSquared() < rSq)
                return true;
        return false;
    }

    // Scratch collections for the reachability flood — single-threaded reuse, zero per-tick GC.
    private static readonly List<(int r, int t)> _spotScratch = new();
    private static readonly Queue<(int r, int t)> _bfsQueue = new();
    private static readonly HashSet<(int r, int t)> _bfsSeen = new();

    /// <summary>Cave tiles (open foreground, rock wall behind) that are air-connected to the
    /// player, within a world-distance band. Bounded BFS over the polar grid from the
    /// player's tile through non-solid tiles; radial steps re-map the angular index because
    /// ring tile counts differ. Runs once per cave-spawn tick (~every 3s), capped at 8000
    /// visited tiles, so cost stays negligible.</summary>
    private static List<(int r, int t)> ReachableCaveSpots(Session run, float minDist, float maxDist)
    {
        var planet = run.Planet;
        _spotScratch.Clear();
        _bfsQueue.Clear();
        _bfsSeen.Clear();

        var (pr, pt) = planet.WorldToTile(run.Player.Position);
        if (pr < 0 || pr >= planet.Rings) return _spotScratch;
        pt = Mod(pt, planet.TilesAt(pr));
        if (Tiles.IsSolid(planet.Get(pr, pt))) return _spotScratch; // buried — fall back

        var minSq = minDist * minDist;
        var maxSq = maxDist * maxDist;
        _bfsQueue.Enqueue((pr, pt));
        _bfsSeen.Add((pr, pt));
        while (_bfsQueue.Count > 0 && _bfsSeen.Count < 8000)
        {
            var (r, t) = _bfsQueue.Dequeue();
            var world = planet.TileToWorld(r, t);
            var dSq = (world - run.Player.Position).LengthSquared();
            if (dSq > maxSq) continue; // band edge — don't expand outward past it
            // Building interiors (alloy-backed walls) are off the spawn menu: nothing
            // materialises inside an apartment the player might be looking at.
            var wall = planet.GetWall(r, t);
            if (dSq >= minSq && wall != TileKind.Sky
                && wall != TileKind.AlienAlloy && wall != TileKind.CityGlass)
                _spotScratch.Add((r, t));

            var n = planet.TilesAt(r);
            Push(r, Mod(t - 1, n));
            Push(r, Mod(t + 1, n));
            if (r > 0)
            {
                // Inward: the single tile covering this tile's centre angle.
                var n2 = planet.TilesAt(r - 1);
                Push(r - 1, Mod((int)((t + 0.5f) * n2 / n), n2));
            }
            if (r < planet.Rings - 1)
            {
                // Outward: every tile overlapping this tile's angular span (outer rings
                // have more tiles, so one inner tile can face several outer ones).
                var n2 = planet.TilesAt(r + 1);
                var first = (int)((float)t * n2 / n);
                var last = (int)((t + 1f) * n2 / n);
                for (var t2 = first; t2 <= last && t2 - first < 4; t2++)
                    Push(r + 1, Mod(t2, n2));
            }
        }
        return _spotScratch;

        void Push(int r, int t)
        {
            if (!Tiles.IsSolid(planet.Get(r, t)) && _bfsSeen.Add((r, t)))
                _bfsQueue.Enqueue((r, t));
        }
    }

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    /// <summary>Angle a few hundred px along the surface from the player — fauna spawns in
    /// the player's neighbourhood (just off-screen), not at a random point on the planet.</summary>
    private static float NearbySurfaceAngle(Session run, float minArc, float maxArc)
    {
        var rel = run.Player.Position - run.Planet.Center;
        var playerAng = MathF.Atan2(rel.Y, rel.X);
        var surfaceRadius = MathF.Max(rel.Length(), Planet.RingMin * Planet.TileSize);
        var arc = minArc + (float)Random.Shared.NextDouble() * (maxArc - minArc);
        var sign = Random.Shared.Next(2) == 0 ? 1f : -1f;
        return playerAng + sign * (arc / surfaceRadius);
    }

    /// <summary>The debug rig's menagerie — every ground species at once, for QA sweeps.</summary>
    private static readonly CreatureKind[] AllSurfaceFauna =
    {
        CreatureKind.Grazer, CreatureKind.Hopper, CreatureKind.SnowLoper, CreatureKind.CinderSkink,
        CreatureKind.RustBack, CreatureKind.TidePuddler, CreatureKind.AcidStrider, CreatureKind.PrismSnail,
        CreatureKind.Civilian, CreatureKind.Peacekeeper,
    };

    /// <summary>Each planet archetype keeps its own herd: the verdant start rolls the classic
    /// grazer/hopper pair, every other biome fields its signature species. Null = no ground
    /// fauna (the Rift's only gentle life is airborne — see the sky spawner).</summary>
    private static CreatureKind? SurfaceFaunaFor(PlanetDef def) => def.Biome switch
    {
        "frost"   => CreatureKind.SnowLoper,
        "ember"   => CreatureKind.CinderSkink,
        "slag"    => CreatureKind.RustBack,
        "ocean"   => CreatureKind.TidePuddler,
        "acid"    => CreatureKind.AcidStrider,
        "crystal" => CreatureKind.PrismSnail,
        // City worlds: the citizens and militia are seeded once by PopulateWorld (nothing
        // pops in downtown); the dynamic spawner only tops up the wild herds outside town.
        "city"    => Random.Shared.Next(2) == 0 ? CreatureKind.Grazer : CreatureKind.Hopper,
        "rift"    => null,
        // Airless rock grows no herds: the Hollow's only life is the hostiles in its caves.
        "belt"    => null,
        // The cratered moon's "herd" hunts you: charged regolith vortices roam the craters.
        "moon"    => CreatureKind.DustDevil,
        "debug"   => AllSurfaceFauna[Random.Shared.Next(AllSurfaceFauna.Length)],
        _         => Random.Shared.Next(2) == 0 ? CreatureKind.Grazer : CreatureKind.Hopper,
    };

    private static void TrySpawnSurfaceAnimal(Session run)
    {
        if (SurfaceFaunaFor(run.Def) is not { } kind) return;

        var angle = NearbySurfaceAngle(run, 220f, 550f);
        var pos = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        if ((pos - run.Player.Position).Length() < 160f) return; // don't pop in on-screen
        if (InCityDistrict(run.Planet, pos)) return;             // city life never pops in
        var c = new Creature(pos, kind);
        if (HazardRejectsSpawn(run, pos, c)) return;             // not into a surface acid pond
        ClearSpawnSpace(run, pos, c.Radius);
        run.Creatures.Add(c);
    }

    private static void TrySpawnSkyAnimal(Session run)
    {
        // No atmosphere, no wings — but the airless skies aren't empty: star jellies need
        // no air at all, and drift over the regolith like drowned constellations (the belt
        // asteroid and the cratered moon both).
        if (run.Def.Biome is "belt" or "moon")
        {
            SpawnSkyAt(run, CreatureKind.StarJelly);
            return;
        }
        // The Rift's sky belongs to null moths — its one neutral species. Everywhere else
        // flies the wild moth/stinger mix (the debug rig throws null moths into the pot too).
        // Saucers are NOT rolled here: the air patrol is seeded by PopulateWorld, on station
        // over its district before the player ever sees the skyline — no pop-in overhead.
        var kind = run.Def.Biome == "rift"
            ? CreatureKind.NullMoth
            : run.Def.Biome == "debug" && Random.Shared.Next(3) == 0
                ? CreatureKind.NullMoth
                : Random.Shared.NextDouble() < 0.65 ? CreatureKind.SkyMoth : CreatureKind.SkyStinger;
        SpawnSkyAt(run, kind);
    }

    /// <summary>Place one airborne creature in the altitude band over the player's
    /// neighbourhood (60-200 px above local terrain, off-screen, out of city districts).</summary>
    private static void SpawnSkyAt(Session run, CreatureKind kind)
    {
        var angle = NearbySurfaceAngle(run, 220f, 550f);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var ground = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        // 60–200 px above the local terrain, capped inside the tile grid so the flyer's
        // collision probes stay in-bounds.
        var alt = (ground - run.Planet.Center).Length() + 60f + (float)Random.Shared.NextDouble() * 140f;
        alt = MathF.Min(alt, (run.Planet.Radius - 12) * Planet.TileSize);
        var pos = run.Planet.Center + dir * alt;
        if ((pos - run.Player.Position).Length() < 160f) return;
        if (InCityDistrict(run.Planet, pos)) return;   // city sky belongs to the seeded patrol
        var c = new Creature(pos, kind);
        ClearSpawnSpace(run, pos, c.Radius); // altitude is above local ground, but a mountain flank can still clip the band
        run.Creatures.Add(c);
    }

    /// <summary>Carve out any solid tiles overlapping a freshly spawned body so nothing starts
    /// life embedded in rock — an embedded spawn forces the collider's inside-tile escape
    /// push on its first frame, which reads as teleporting/clipping through the world.
    /// Overlap uses the same polar-rect math as the collider; anchored tiles are left alone
    /// and physics is dirty-marked so any overhang this opens up settles normally.</summary>
    private static void ClearSpawnSpace(Session run, Vector2 pos, float radius)
    {
        var planet = run.Planet;
        var (tx, _) = planet.WorldToTile(pos);
        var relC = pos - planet.Center;
        var ang = MathF.Atan2(relC.Y, relC.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var rSq = (radius + 0.5f) * (radius + 0.5f);
        for (var dx = -2; dx <= 2; dx++)
        {
            var x = tx + dx;
            if (x < 0 || x >= planet.Rings) continue;
            // Per-ring column from the true world angle — ring tile counts differ, so a
            // shared ty index would drift near the angle wrap and miss overlapped tiles.
            var nRing = planet.TilesAt(x);
            var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
            for (var dy = -2; dy <= 2; dy++)
            {
                var y = ty0 + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;

                var centre = planet.TileToWorld(x, y);
                var up = planet.UpAt(centre);
                var right = new Vector2(-up.Y, up.X);
                var rel = pos - centre;
                var lx = Vector2.Dot(rel, right);
                var ly = Vector2.Dot(rel, up);
                var ringRadius = (Planet.RingMin + x + 0.5f) * Planet.TileSize;
                var halfX = MathHelper.TwoPi * ringRadius / planet.TilesAt(x) * 0.5f;
                var halfY = Planet.TileSize * 0.5f;
                var ox = lx - MathHelper.Clamp(lx, -halfX, halfX);
                var oy = ly - MathHelper.Clamp(ly, -halfY, halfY);
                if (ox * ox + oy * oy >= rSq) continue;

                planet.Set(x, y, TileKind.Sky);
                run.Physics.MarkDirty(x, y);
            }
        }
    }
}
