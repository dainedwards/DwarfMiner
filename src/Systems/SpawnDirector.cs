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
    /// <summary>Per-frame population upkeep — the two spawn-timer loops.</summary>
    public static void Update(float dt, Session run)
    {
        // Cave dwellers. Cap counts them only — surface/sky fauna have their own loop.
        run.SpawnTimer -= dt;
        if (run.SpawnTimer <= 0 && CountKindsNear(run, 550f, cave: true) < run.Def.CaveSpawnCap)
        {
            TrySpawnCreature(run);
            run.SpawnTimer = 2.3f + (float)Random.Shared.NextDouble() * 1.7f;
        }

        // Fauna upkeep: keep the surface grazed and the sky populated. Slow cadence — these
        // are ambience, not a threat escalation. City streets run at double the herd cap:
        // a metropolis with six pedestrians reads abandoned.
        run.FaunaTimer -= dt;
        if (run.FaunaTimer <= 0)
        {
            run.FaunaTimer = 6f + (float)Random.Shared.NextDouble() * 4f;
            var surfaceCap = run.Def.Biome == "city" ? 14 : 7;
            var skyCap = run.Def.Biome == "city" ? 8 : 6;
            if (CountKindsNear(run, 700f, surface: true) < surfaceCap) TrySpawnSurfaceAnimal(run);
            if (CountKindsNear(run, 700f, sky: true) < skyCap) TrySpawnSkyAnimal(run);
            // Lakes get their own small population of aquatic-only life.
            if (run.Def.HasWater && CountKindsNear(run, 700f, water: true) < 4)
                TrySpawnAquatic(run);
        }
    }

    /// <summary>Populate the fresh planet with ambient life: herds on the surface, flyers in
    /// the sky. Run-start only; Update keeps numbers topped up afterwards. All spawn helpers
    /// place relative to the player, so this stocks the starting neighbourhood.</summary>
    public static void SpawnInitialFauna(Session run)
    {
        var surface = run.Def.Biome == "city" ? 14 : 7;
        for (var i = 0; i < surface; i++) TrySpawnSurfaceAnimal(run);
        for (var i = 0; i < 6; i++) TrySpawnSkyAnimal(run);
        for (var i = 0; i < 12; i++) TrySpawnCreature(run);
        if (run.Def.HasWater)
            for (var i = 0; i < 4; i++) TrySpawnAquatic(run);
    }

    /// <summary>Walk down from far above the given angle until the first solid tile, then
    /// float a few pixels above it — used for the player spawn and surface fauna.</summary>
    public static Vector2 FindSurfaceSpawn(Planet planet, float angle, int radius)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        // Start well above the highest possible peak and step inward until we hit solid.
        for (var d = radius + 60; d > 20; d--)
        {
            var p = planet.Center + dir * (d * Planet.TileSize);
            if (planet.IsSolidAt(p))
                return p - dir * (Planet.TileSize * 1.5f);
        }
        return planet.Center + dir * ((radius + 24) * Planet.TileSize);
    }

    /// <summary>Rooted / lying-in-wait kinds. They never seek the player, so they must not
    /// eat the cave-population budget — a cap full of vines and disguised mimics plays as an
    /// empty planet. They get their own small allowance instead (see TrySpawnCreature).</summary>
    private static bool IsStationary(CreatureKind k) =>
        k is CreatureKind.SnapperVine or CreatureKind.RockMimic;

    /// <summary>Population count within a radius of the player, filtered by habitat kind.
    /// Stationary ambushers are excluded from the cave count — see <see cref="IsStationary"/>.</summary>
    private static int CountKindsNear(Session run, float radius,
        bool cave = false, bool surface = false, bool sky = false, bool stationary = false)
    {
        var rSq = radius * radius;
        var n = 0;
        foreach (var c in run.Creatures)
            if ((cave && c.IsCaveKind && !IsStationary(c.Kind))
                || (surface && c.IsSurfaceKind) || (sky && c.IsSkyKind)
                || (stationary && IsStationary(c.Kind)))
                if ((c.Position - run.Player.Position).LengthSquared() < rSq)
                    n++;
        return n;
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
        // the one class that can open its own way out of a sealed pocket.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var a = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var d = 200f + (float)Random.Shared.NextDouble() * 250f;
            var candidate = run.Player.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            var (r, t) = planet.WorldToTile(candidate);
            if (r < 0 || r >= planet.Rings) continue;
            if (planet.Get(r, t) != TileKind.Sky) continue;
            if (planet.GetWall(r, t) == TileKind.Sky) continue;
            var pos = planet.TileToWorld(r, t);
            if ((pos - run.Player.Position).Length() < 180f) continue;
            SpawnAt(run, pos, connected: false);
            return;
        }
    }

    /// <summary>Roll a kind for the depth band at <paramref name="pos"/> and spawn it.
    /// Unconnected sites only ever get diggers — anything else would be stranded.</summary>
    private static void SpawnAt(Session run, Vector2 pos, bool connected)
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
            kind = roll < 0.28 ? CreatureKind.MagmaSlug
                 : roll < 0.44 ? CreatureKind.HornedDelver
                 : roll < 0.56 ? CreatureKind.Centipede
                 : roll < 0.68 ? CreatureKind.AcidSpitter
                 : roll < 0.78 ? CreatureKind.CaveEye
                 : roll < 0.87 ? CreatureKind.BomberBeetle
                 : roll < 0.96 ? CreatureKind.Grub
                 : CreatureKind.RockMimic;
        }
        else if (depth > 20f)
        {
            kind = roll < 0.14 ? CreatureKind.HornedDelver
                 : roll < 0.26 ? CreatureKind.Centipede
                 : roll < 0.38 ? CreatureKind.Borer
                 : roll < 0.48 ? CreatureKind.MoleBeast
                 : roll < 0.58 ? CreatureKind.CaveSlime
                 : roll < 0.68 ? CreatureKind.AcidSpitter
                 : roll < 0.76 ? CreatureKind.BomberBeetle
                 : roll < 0.84 ? CreatureKind.CaveEye
                 : roll < 0.90 ? CreatureKind.Grub
                 : roll < 0.97 ? CreatureKind.SnapperVine
                 : CreatureKind.RockMimic;
        }
        else
        {
            kind = roll < 0.20 ? CreatureKind.Skitterer
                 : roll < 0.40 ? CreatureKind.CaveSlime
                 : roll < 0.55 ? CreatureKind.MoleBeast
                 : roll < 0.65 ? CreatureKind.Grub
                 : roll < 0.75 ? CreatureKind.CaveEye
                 : roll < 0.85 ? CreatureKind.SnapperVine
                 : roll < 0.93 ? CreatureKind.HornedDelver
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

        var c = new Creature(pos, kind);
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
            if (dSq >= minSq && planet.GetWall(r, t) != TileKind.Sky)
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
        // City streets: mostly citizens, with a peacekeeper patrol mixed in every few
        // spawns — enough militia that an invading creature gets met with return fire.
        "city"    => Random.Shared.Next(3) == 0 ? CreatureKind.Peacekeeper : CreatureKind.Civilian,
        "rift"    => null,
        "debug"   => AllSurfaceFauna[Random.Shared.Next(AllSurfaceFauna.Length)],
        _         => Random.Shared.Next(2) == 0 ? CreatureKind.Grazer : CreatureKind.Hopper,
    };

    private static void TrySpawnSurfaceAnimal(Session run)
    {
        if (SurfaceFaunaFor(run.Def) is not { } kind) return;

        // Citizens prefer their own addresses: when a skyscraper doorway or apartment floor
        // (Planet.CitySpawns) sits in the spawn band, place the civilian there so the towers
        // read inhabited — street/wilderness spawns are the fallback, not the rule.
        // Peacekeepers post at the same addresses (tower doors are their beat).
        if (kind is CreatureKind.Civilian or CreatureKind.Peacekeeper
            && run.Planet.CitySpawns.Count > 0)
        {
            var sites = new List<Vector2>();
            foreach (var (sr, st) in run.Planet.CitySpawns)
            {
                var world = run.Planet.TileToWorld(sr, st);
                var d = (world - run.Player.Position).Length();
                if (d is > 200f and < 650f) sites.Add(world);
            }
            if (sites.Count > 0 && Random.Shared.NextDouble() < 0.7)
            {
                var home = sites[Random.Shared.Next(sites.Count)];
                var cc = new Creature(home, kind);
                ClearSpawnSpace(run, home, cc.Radius);
                run.Creatures.Add(cc);
                return;
            }
        }

        var angle = NearbySurfaceAngle(run, 220f, 550f);
        var pos = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        if ((pos - run.Player.Position).Length() < 160f) return; // don't pop in on-screen
        var c = new Creature(pos, kind);
        ClearSpawnSpace(run, pos, c.Radius);
        run.Creatures.Add(c);
    }

    private static void TrySpawnSkyAnimal(Session run)
    {
        // The Rift's sky belongs to null moths — its one neutral species. City skies are
        // patrolled by guard saucers (with the odd wild moth drifting through); everywhere
        // else flies the moth/stinger mix (the debug rig throws null moths into the pot too).
        var kind = run.Def.Biome == "rift"
            ? CreatureKind.NullMoth
            : run.Def.Biome == "city" && Random.Shared.NextDouble() < 0.7
                ? CreatureKind.Saucer
            : run.Def.Biome == "debug" && Random.Shared.Next(3) == 0
                ? CreatureKind.NullMoth
                : Random.Shared.NextDouble() < 0.65 ? CreatureKind.SkyMoth : CreatureKind.SkyStinger;

        // Guard saucers spawn over a city district (the one nearest the player, so they show
        // up where the action is) and patrol its band — never out over empty ground. Other
        // sky fauna drift in near the player as before.
        float angle;
        if (kind == CreatureKind.Saucer && run.Planet.CityDistricts.Count > 0)
        {
            var rel = run.Player.Position - run.Planet.Center;
            var pAng = MathF.Atan2(rel.Y, rel.X);
            var best = float.MaxValue;
            var chosen = run.Planet.CityDistricts[0];
            foreach (var dct in run.Planet.CityDistricts)
            {
                var d = MathF.Abs(MathHelper.WrapAngle(pAng - dct.ang));
                if (d < best) { best = d; chosen = dct; }
            }
            angle = chosen.ang + ((float)Random.Shared.NextDouble() * 2f - 1f) * chosen.halfWidth;
        }
        else
        {
            angle = NearbySurfaceAngle(run, 220f, 550f);
        }
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var ground = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        // 60–200 px above the local terrain, capped inside the tile grid so the flyer's
        // collision probes stay in-bounds.
        var alt = (ground - run.Planet.Center).Length() + 60f + (float)Random.Shared.NextDouble() * 140f;
        alt = MathF.Min(alt, (run.Planet.Radius - 12) * Planet.TileSize);
        var pos = run.Planet.Center + dir * alt;
        if ((pos - run.Player.Position).Length() < 160f) return;
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
