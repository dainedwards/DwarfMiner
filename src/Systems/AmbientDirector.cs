using System;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>The scheduled planet disasters. Which of these a world can roll depends on its
/// def (blizzards need snow, acid rain needs AcidRain, surges need serious lava, eruptions
/// need vents); flares and earthquakes threaten every world.</summary>
public enum DisasterKind { Flare, Blizzard, AcidRain, MagmaSurge, Eruption, Earthquake }

/// <summary>
/// Drives ambient world events. Meteors stay a frequent dodge-it hazard on their own cadence
/// (shorter with thin atmosphere). Everything bigger — solar flares, blizzards, acid rain,
/// magma surges, volcanic eruptions, earthquakes — runs on ONE shared disaster clock per
/// planet: only one disaster can be live at a time, and consecutive disasters are spaced by
/// planet difficulty (~7 min on the gentlest worlds down to ~2 min on the hardest). Meteors
/// are added to <see cref="Session.Meteors"/> for Game1 to update/draw; the rest mutate the
/// run directly and report back so Game1 can toast/shake/sound them.
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
        public bool AcidRainStarted;
        public bool EruptionStarted;
        public Vector2 EruptionPos;
        public bool QuakeStruck;
    }

    /// <summary>Disaster spacing before jitter: 7 minutes on a difficulty-0 world easing to
    /// 2 minutes at difficulty 1.</summary>
    public static float BaseInterval(PlanetDef def) => MathHelper.Lerp(420f, 120f, def.Difficulty);

    /// <summary>A fresh roll of the shared disaster clock (base spacing ±15%).</summary>
    public static float NextInterval(PlanetDef def)
        => BaseInterval(def) * (0.85f + (float)Random.Shared.NextDouble() * 0.3f);

    /// <summary>True while any disaster is still playing out — the shared clock holds (and
    /// nothing new can start) until the world is quiet again. Surges and quakes are
    /// instantaneous, so they never hold the clock.</summary>
    public static bool DisasterActive(Session run) =>
        run.FlareWarn > 0f || run.FlareActive > 0f || run.BlizzardActive > 0f
        || run.AcidRainActive > 0f || run.EruptionLeft > 0f;

    public static Result Update(float dt, Session run, Particles particles)
    {
        var result = new Result();

        // Meteor cadence shortens with thin atmosphere (OxygenDrainScale: slag 1.7 → ~20s,
        // temperate 1.0 → ~34s). A slight pre-warning gap on the first one at run start.
        // NoDisasters worlds (the QA rig) skip the ambient cadence — the debug menu's
        // EVENTS tab is their only weather.
        if (!run.Def.NoDisasters)
        {
            run.MeteorTimer -= dt;
            if (run.MeteorTimer <= 0f)
            {
                SpawnMeteor(run);
                var interval = 34f / MathF.Max(0.5f, run.Def.OxygenDrainScale);
                run.MeteorTimer = interval * (0.6f + (float)Random.Shared.NextDouble() * 0.9f);
            }
        }

        // Tick whatever disaster is live. Phases only — starting a new one is the shared
        // clock's job below, so these never reschedule themselves.
        if (run.FlareActive > 0f)
            run.FlareActive -= dt;
        else if (run.FlareWarn > 0f)
        {
            run.FlareWarn -= dt;
            if (run.FlareWarn <= 0f)
            {
                run.FlareActive = 9f;
                result.FlareStruck = true;
            }
        }

        if (run.BlizzardActive > 0f)
            run.BlizzardActive -= dt;

        if (run.AcidRainActive > 0f)
        {
            run.AcidRainActive -= dt;
            run.AcidRainAngle += 0.012f * dt;   // slow downwind drift
            RainAcid(run);
        }

        // The shared disaster clock: it only runs while the world is quiet, so disasters
        // can never overlap, and the spacing between them is the difficulty interval.
        if (!run.Def.NoDisasters && !DisasterActive(run))
        {
            run.DisasterTimer -= dt;
            if (run.DisasterTimer <= 0f)
            {
                var kind = run.NextDisaster ?? Pick(run);
                run.NextDisaster = null;
                TryBegin(kind, run, particles, ref result);
                run.DisasterTimer = NextInterval(run.Def);
            }
        }

        return result;
    }

    /// <summary>Roll the next disaster from whatever this world has armed. Flares and quakes
    /// threaten everywhere; the rest are gated by the def / generated terrain.</summary>
    private static DisasterKind Pick(Session run)
    {
        Span<DisasterKind> armed = stackalloc DisasterKind[6];
        var n = 0;
        armed[n++] = DisasterKind.Flare;
        armed[n++] = DisasterKind.Earthquake;
        if (run.Def.SurfaceTile == TileKind.Snow) armed[n++] = DisasterKind.Blizzard;
        if (run.Def.AcidRain) armed[n++] = DisasterKind.AcidRain;
        if (run.Def.LavaFillFrac >= 0.5f) armed[n++] = DisasterKind.MagmaSurge;
        if (run.Planet is { VolcanoVents.Count: > 0 }) armed[n++] = DisasterKind.Eruption;
        return armed[Random.Shared.Next(n)];
    }

    /// <summary>Start a disaster right now (the clock firing, or a debug-menu force). Returns
    /// false when this world can't host it — no vents to erupt, no enclosed pocket to surge
    /// into. The caller owns resetting the shared clock.</summary>
    public static bool TryBegin(DisasterKind kind, Session run, Particles particles, ref Result result)
    {
        switch (kind)
        {
            case DisasterKind.Flare:
                run.FlareWarn = 7f;
                result.FlareWarned = true;
                return true;

            case DisasterKind.Blizzard:
                run.BlizzardActive = 15f;
                result.BlizzardStarted = true;
                return true;

            case DisasterKind.AcidRain:
            {
                var rel = run.Player.Position - run.Planet.Center;
                run.AcidRainAngle = MathF.Atan2(rel.Y, rel.X);
                run.AcidRainActive = 12f;
                result.AcidRainStarted = true;
                return true;
            }

            case DisasterKind.MagmaSurge:
                if (!MagmaSurge(run, particles, out var pos)) return false;
                result.Surge = true;
                result.SurgePos = pos;
                return true;

            case DisasterKind.Eruption:
            {
                if (run.Planet is not { VolcanoVents.Count: > 0 }) return false;
                // Erupt the volcano NEAREST the player, so a debug trigger (or a scheduled
                // eruption) always fires the vent you can actually see rather than one on the
                // far side of the world.
                var best = 0;
                var bestSq = float.MaxValue;
                for (var i = 0; i < run.Planet.VolcanoVents.Count; i++)
                {
                    var (vvx, vvy, _) = run.Planet.VolcanoVents[i];
                    var d = (run.Planet.TileToWorld(vvx, vvy) - run.Player.Position).LengthSquared();
                    if (d < bestSq) { bestSq = d; best = i; }
                }
                run.EruptionVent = best;
                run.EruptionLeft = 12f + (float)Random.Shared.NextDouble() * 8f;
                run.EruptionTotal = run.EruptionLeft;
                // How far past the rim this one drives the pool (see Session.EruptionPeakFrac).
                run.EruptionPeakFrac = 1.2f + (float)Random.Shared.NextDouble() * 0.25f;
                // A fresh eruption at the SAME vent cancels its subsidence (another
                // volcano's drain keeps running — they're independent pools).
                if (run.EruptionDrainVent == best) run.EruptionDrainVent = -1;
                run.EruptionSpoutR = int.MaxValue;   // re-anchor the spout on first frame
                var (vx, vy, _) = run.Planet.VolcanoVents[best];
                result.EruptionStarted = true;
                result.EruptionPos = run.Planet.TileToWorld(vx, vy);
                return true;
            }

            case DisasterKind.Earthquake:
            {
                if (run.Physics is null) return false;
                // Strike the player's neighbourhood, not a random bearing at half-radius:
                // quakes are now the ONLY thing that brings down undercut rock (see
                // Physics.QuakeShaking), so the epicentre must land where the mining
                // actually happened — a cave-in nobody is near is one nobody experiences.
                var off = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
                var epi = run.Player.Position + new Vector2(MathF.Cos(off), MathF.Sin(off))
                    * (120f + (float)Random.Shared.NextDouble() * 280f);
                run.Physics.Earthquake(epi, 300f, 2);
                result.QuakeStruck = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>One storm tick: drop a few acid cells from cloud height across the cloud's
    /// angular band. Throttled so a 12s storm sheds a heavy but sim-friendly shower.</summary>
    private static void RainAcid(Session run)
    {
        if (Random.Shared.Next(2) == 0) return;
        var planet = run.Planet;
        for (var i = 0; i < 3; i++)
        {
            var ang = run.AcidRainAngle + ((float)Random.Shared.NextDouble() - 0.5f) * 0.28f;
            var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
            var up = planet.UpAt(ground);
            var drop = ground + up * (180f + (float)Random.Shared.NextDouble() * 90f);
            var (tx, ty) = planet.WorldToTile(drop);
            if (tx < 0 || tx >= planet.Rings) continue;
            run.Cells.SpawnInTile(tx, ty, Material.Acid, 1);
        }
    }

    public static void SpawnMeteor(Session run)
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
        var vel = dir * 110f + right * (((float)Random.Shared.NextDouble() - 0.5f) * 70f);
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
            if (tx < 0 || tx >= planet.Rings) continue;
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
