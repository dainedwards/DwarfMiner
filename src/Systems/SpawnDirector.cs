using System;
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
        // are ambience, not a threat escalation.
        run.FaunaTimer -= dt;
        if (run.FaunaTimer <= 0)
        {
            run.FaunaTimer = 6f + (float)Random.Shared.NextDouble() * 4f;
            if (CountKindsNear(run, 700f, surface: true) < 7) TrySpawnSurfaceAnimal(run);
            if (CountKindsNear(run, 700f, sky: true) < 6) TrySpawnSkyAnimal(run);
        }
    }

    /// <summary>Populate the fresh planet with ambient life: herds on the surface, flyers in
    /// the sky. Run-start only; Update keeps numbers topped up afterwards. All spawn helpers
    /// place relative to the player, so this stocks the starting neighbourhood.</summary>
    public static void SpawnInitialFauna(Session run)
    {
        for (var i = 0; i < 7; i++) TrySpawnSurfaceAnimal(run);
        for (var i = 0; i < 6; i++) TrySpawnSkyAnimal(run);
        for (var i = 0; i < 12; i++) TrySpawnCreature(run);
    }

    /// <summary>Walk down from far above the given angle until the first solid tile, then
    /// float a few pixels above it — used for the player spawn and surface fauna.</summary>
    public static Vector2 FindSurfaceSpawn(Planet planet, float angle, int radius)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        // Start well above the highest possible peak and step inward until we hit solid.
        for (var d = radius + 30; d > 10; d--)
        {
            var p = planet.Center + dir * (d * Planet.TileSize);
            if (planet.IsSolidAt(p))
                return p - dir * (Planet.TileSize * 1.5f);
        }
        return planet.Center + dir * ((radius + 12) * Planet.TileSize);
    }

    /// <summary>Population count within a radius of the player, filtered by habitat kind.</summary>
    private static int CountKindsNear(Session run, float radius,
        bool cave = false, bool surface = false, bool sky = false)
    {
        var rSq = radius * radius;
        var n = 0;
        foreach (var c in run.Creatures)
            if ((cave && c.IsCaveKind) || (surface && c.IsSurfaceKind) || (sky && c.IsSkyKind))
                if ((c.Position - run.Player.Position).LengthSquared() < rSq)
                    n++;
        return n;
    }

    private static void TrySpawnCreature(Session run)
    {
        var planet = run.Planet;
        // Pick a cave tile (Sky foreground with a rock wall behind it) in a donut around the
        // player: close enough to be met within a minute of wandering, far enough to never
        // pop in on-screen.
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

            // Roster shifts with depth: slimes, moles and skitterers riddle the upper crust
            // (with snapper vines in the pockets), the mid-band belongs to the diggers plus
            // the nasty tricks (spitters, bombers), and the lava zone is magma slugs, spitter
            // batteries and hardened delver war-parties. Rock mimics are a rare bad day at
            // any depth below the surface.
            var fromCenter = (pos - planet.Center).Length() / Planet.TileSize;
            var depth = planet.SurfaceRing - (fromCenter - Planet.RingMin); // tiles below the baseline surface
            var roll = Random.Shared.NextDouble();
            CreatureKind kind;
            if (depth > 45f)
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

            // Biome specials override the generic roster: wraiths haunt the Rift at every
            // depth, crawlers stalk the deeps of crystal-pocket worlds, and spore bats flit
            // through the shallow caves of worlds with fungal groves.
            var special = Random.Shared.NextDouble();
            if (run.Def.Id == "rift" && special < 0.25)
                kind = CreatureKind.VoidWraith;
            else if (run.Def.CrystalPockets > 0 && depth > 40f && special < 0.18)
                kind = CreatureKind.CrystalCrawler;
            else if (run.Def.FungalPockets > 0 && depth < 30f && special < 0.22)
                kind = CreatureKind.SporeBat;

            var c = new Creature(pos, kind);
            ClearSpawnSpace(run, pos, c.Radius);
            run.Creatures.Add(c);
            return;
        }
    }

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

    private static void TrySpawnSurfaceAnimal(Session run)
    {
        var angle = NearbySurfaceAngle(run, 220f, 550f);
        var pos = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        if ((pos - run.Player.Position).Length() < 160f) return; // don't pop in on-screen
        var kind = Random.Shared.Next(2) == 0 ? CreatureKind.Grazer : CreatureKind.Hopper;
        var c = new Creature(pos, kind);
        ClearSpawnSpace(run, pos, c.Radius);
        run.Creatures.Add(c);
    }

    private static void TrySpawnSkyAnimal(Session run)
    {
        var angle = NearbySurfaceAngle(run, 220f, 550f);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var ground = FindSurfaceSpawn(run.Planet, angle, run.Planet.Radius);
        // 60–200 px above the local terrain, capped inside the tile grid so the flyer's
        // collision probes stay in-bounds.
        var alt = (ground - run.Planet.Center).Length() + 60f + (float)Random.Shared.NextDouble() * 140f;
        alt = MathF.Min(alt, (run.Planet.Radius - 6) * Planet.TileSize);
        var pos = run.Planet.Center + dir * alt;
        if ((pos - run.Player.Position).Length() < 160f) return;
        var kind = Random.Shared.NextDouble() < 0.65 ? CreatureKind.SkyMoth : CreatureKind.SkyStinger;
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
