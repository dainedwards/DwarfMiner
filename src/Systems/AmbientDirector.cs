using System;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Drives ambient world events on timers: meteor strikes (all worlds, more frequent on
/// thin-atmosphere planets where more rocks get through) and magma surges (lava-rich worlds,
/// where lava suddenly wells up into a nearby cave). Meteors are added to
/// <see cref="Session.Meteors"/> for Game1 to update/draw; surges mutate the cell sim directly
/// and are reported back so Game1 can shake + sound them.
/// </summary>
public static class AmbientDirector
{
    public struct Result
    {
        public bool Surge;
        public Vector2 SurgePos;
        public bool FlareWarned;
        public bool FlareStruck;
        public bool BlizzardStarted;
    }

    public static Result Update(float dt, Session run, Particles particles)
    {
        var result = new Result();

        // Meteor cadence shortens with thin atmosphere (OxygenDrainScale: slag 1.7 → ~20s,
        // temperate 1.0 → ~34s). A slight pre-warning gap on the first one at run start.
        run.MeteorTimer -= dt;
        if (run.MeteorTimer <= 0f)
        {
            SpawnMeteor(run);
            var interval = 34f / MathF.Max(0.5f, run.Def.OxygenDrainScale);
            run.MeteorTimer = interval * (0.6f + (float)Random.Shared.NextDouble() * 0.9f);
        }

        // Solar flare: every world's star occasionally spits a radiation burst. A warned
        // window (get underground!) then a scorching phase — anyone in surface air burns.
        if (run.FlareActive > 0f)
        {
            run.FlareActive -= dt;
            if (run.FlareActive <= 0f)
                run.FlareTimer = 90f + (float)Random.Shared.NextDouble() * 70f;
        }
        else if (run.FlareWarn > 0f)
        {
            run.FlareWarn -= dt;
            if (run.FlareWarn <= 0f)
            {
                run.FlareActive = 9f;
                result.FlareStruck = true;
            }
        }
        else
        {
            run.FlareTimer -= dt;
            if (run.FlareTimer <= 0f)
            {
                run.FlareWarn = 7f;
                result.FlareWarned = true;
            }
        }

        // Blizzards — snow worlds only: a freezing squall that bites anyone caught outside.
        if (run.Def.SurfaceTile == TileKind.Snow)
        {
            if (run.BlizzardActive > 0f)
            {
                run.BlizzardActive -= dt;
                if (run.BlizzardActive <= 0f)
                    run.BlizzardTimer = 70f + (float)Random.Shared.NextDouble() * 55f;
            }
            else
            {
                run.BlizzardTimer -= dt;
                if (run.BlizzardTimer <= 0f)
                {
                    run.BlizzardActive = 15f;
                    result.BlizzardStarted = true;
                }
            }
        }

        // Magma surges — only where there's serious lava (ember, core).
        if (run.Def.LavaFillFrac >= 0.5f)
        {
            run.SurgeTimer -= dt;
            if (run.SurgeTimer <= 0f)
            {
                if (MagmaSurge(run, particles, out var pos))
                {
                    result.Surge = true;
                    result.SurgePos = pos;
                }
                run.SurgeTimer = 20f + (float)Random.Shared.NextDouble() * 16f;
            }
        }

        return result;
    }

    private static void SpawnMeteor(Session run)
    {
        var planet = run.Planet;
        // Aim near the player, offset by up to ~±14° so it's a dodgeable threat, not a homing
        // strike. Start high in the sky and streak in at a slight angle.
        var rel = run.Player.Position - planet.Center;
        var playerAng = MathF.Atan2(rel.Y, rel.X);
        var ang = playerAng + ((float)Random.Shared.NextDouble() - 0.5f) * 0.5f;
        var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
        var up = planet.UpAt(ground);
        var right = new Vector2(-up.Y, up.X);
        var start = ground + up * (360f + (float)Random.Shared.NextDouble() * 220f);
        var dir = ground - start;
        if (dir.LengthSquared() > 0.001f) dir.Normalize();
        var vel = dir * 240f + right * (((float)Random.Shared.NextDouble() - 0.5f) * 120f);
        run.Meteors.Add(new Meteor(start, vel, ground));
    }

    private static bool MagmaSurge(Session run, Particles particles, out Vector2 pos)
    {
        pos = Vector2.Zero;
        var planet = run.Planet;
        // Find an enclosed cave pocket near the player and flood it with lava welling up.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var a = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var d = 60f + (float)Random.Shared.NextDouble() * 240f;
            var cand = run.Player.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            var (tx, ty) = planet.WorldToTile(cand);
            if (tx < 0 || tx >= Planet.RingCount) continue;
            if (planet.Get(tx, ty) != TileKind.Sky) continue;
            if (planet.GetWall(tx, ty) == TileKind.Sky) continue;   // must be enclosed rock, not open sky

            run.Cells.SpawnInTile(tx, ty, Material.Lava, Cells.Density * Cells.Density);
            var inner = planet.InnerNeighbour(tx, ty);
            run.Cells.SpawnInTile(inner.x, inner.y, Material.Lava, Cells.Density * 2);
            pos = planet.TileToWorld(tx, ty);
            particles.EmitDust(pos, 8f);
            return true;
        }
        return false;
    }
}
