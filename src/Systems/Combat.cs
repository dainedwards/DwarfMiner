using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Projectile hit resolution, kept out of Game1 so the headless sim test can drive it.
/// Sweeps each projectile's per-frame travel segment against bodies (no tunnelling at any
/// speed), applies hits in path order with per-victim tracking (a piercer damages each body
/// once, not once per overlapping frame), detonates contact explosives on the first body
/// struck, and applies radial blast damage with distance falloff when an explosive dies.
/// Call once per projectile per frame, right after <see cref="Projectile.Update"/>.
/// </summary>
public static class Combat
{
    /// <summary>Scratch list of (t along segment, body) candidates — single-threaded reuse.</summary>
    private static readonly List<(float t, object victim)> _hits = new();

    public static void ResolveHits(Projectile p, List<Creature> creatures, Titan? titan,
        Planet planet, Physics physics, Cells cells, Particles? particles = null)
    {
        // Fuse explosives (dynamite/TNT) are lobbed charges — they tumble past bodies without
        // touching them; all their damage comes from the blast. A contact explosive that
        // already died on terrain this frame has spent its blast there; the AoE below covers
        // anything it flew past on the way in.
        if (!p.ExplodesOnFuse && !(p.DetonatesOnContact && p.Dead))
            SweepBodies(p, creatures, titan, planet, physics, cells, particles);

        if (p.Dead && p.ExplosionRadius > 0f)
            ApplyExplosionDamage(p, creatures, titan);
    }

    /// <summary>Collect every body the PrevPosition→Position segment crosses, sorted by
    /// distance along the path, and land hits in that order until the projectile runs out of
    /// pierces or detonates. Terrain impact has already truncated the segment, so a round
    /// that died on a wall still credits the bodies it passed through before hitting it.</summary>
    private static void SweepBodies(Projectile p, List<Creature> creatures, Titan? titan,
        Planet planet, Physics physics, Cells cells, Particles? particles)
    {
        var seg = p.Position - p.PrevPosition;
        var segLenSq = seg.LengthSquared();

        _hits.Clear();
        foreach (var c in creatures)
        {
            if (c.Health <= 0 || p.HitVictims.Contains(c)) continue;
            // Militia rounds pass clean through civilians and other neutrals — the city
            // guard never guns down its own crowd chasing an invader.
            if (p.FriendlyToNeutrals && !c.Hostile) continue;
            if (SegmentHitT(p.PrevPosition, seg, segLenSq, c.Position, c.Radius + p.Radius) is { } t)
                _hits.Add((t, c));
        }
        if (titan is not null && titan.Health > 0 && titan.Targetable && !p.HitVictims.Contains(titan)
            && SegmentHitT(p.PrevPosition, seg, segLenSq, titan.Position, titan.Radius + p.Radius) is { } tt)
        {
            _hits.Add((tt, titan));
        }
        if (_hits.Count == 0) return;
        _hits.Sort((a, b) => a.t.CompareTo(b.t));

        foreach (var (t, victim) in _hits)
        {
            p.HitVictims.Add(victim);
            ApplyDirectDamage(p, victim);

            if (p.DetonatesOnContact)
            {
                p.Position = p.PrevPosition + seg * t;   // blast centred where it struck
                p.Explode(planet, physics, cells, particles);
                return;
            }
            if (p.CreaturePierces > 0 && --p.CreaturePierces == 0)
            {
                p.Dead = true;
                p.Position = p.PrevPosition + seg * t;
                return;
            }
            // CreaturePierces == -1 → unlimited pierce (harpoon): sail on through.
        }
    }

    /// <summary>Full-damage strike on a single body, with the projectile's elemental debuffs.</summary>
    private static void ApplyDirectDamage(Projectile p, object victim)
    {
        switch (victim)
        {
            case Creature c:
                c.Health -= p.Damage;
                c.HitFlash = 0.15f;
                if (p.BurnSeconds > 0f) c.BurnSeconds = MathF.Max(c.BurnSeconds, p.BurnSeconds);
                if (p.FreezeSeconds > 0f) c.FreezeSeconds = MathF.Max(c.FreezeSeconds, p.FreezeSeconds);
                break;
            case Titan t:
                // Militia pea-shooters sting the titan but don't re-aggro it onto the
                // player — the city's fight stays the city's fight.
                if (!p.FriendlyToNeutrals) t.OnDamage();   // wakes the kaiju up and resets its 10s aggro timer
                // Before hatching, hits chip the egg (and can crack it open early); after,
                // they wound the boss.
                if (!t.Hatched) t.DamageEgg(p.Damage);
                else { t.Health -= p.Damage; t.HitFlash = 0.15f; }
                break;
        }
    }

    /// <summary>Radial blast damage around a just-died explosive. Direct-hit victims are
    /// skipped — they already took the full contact damage. Everything else takes the
    /// projectile's damage with linear falloff: full at the epicentre, 40% at the edge.</summary>
    private static void ApplyExplosionDamage(Projectile p, List<Creature> creatures, Titan? titan)
    {
        var r = p.ExplosionRadius;
        foreach (var c in creatures)
        {
            if (p.HitVictims.Contains(c)) continue;
            var dist = (c.Position - p.Position).Length();
            if (dist >= r + c.Radius) continue;
            var falloff = 1f - 0.6f * MathHelper.Clamp(dist / r, 0f, 1f);
            c.Health -= p.Damage * falloff;
            c.HitFlash = 0.15f;
            if (p.BurnSeconds > 0f) c.BurnSeconds = MathF.Max(c.BurnSeconds, p.BurnSeconds);
            if (p.FreezeSeconds > 0f) c.FreezeSeconds = MathF.Max(c.FreezeSeconds, p.FreezeSeconds);
        }
        if (titan is not null && titan.Targetable && !p.HitVictims.Contains(titan)
            && (titan.Position - p.Position).Length() < r + titan.Radius)
        {
            titan.OnDamage();
            if (!titan.Hatched) titan.DamageEgg(p.Damage * 0.4f);
            else { titan.Health -= p.Damage * 0.4f; titan.HitFlash = 0.15f; }
        }
    }

    /// <summary>Earliest point on the a→a+seg segment within <paramref name="radius"/> of
    /// <paramref name="center"/>, as t in [0,1] — or null if the segment misses the circle.
    /// Closest-approach test: exact for the "did we cross this body this frame" question.</summary>
    private static float? SegmentHitT(Vector2 a, Vector2 seg, float segLenSq, Vector2 center, float radius)
    {
        var t = segLenSq < 1e-6f ? 0f : MathHelper.Clamp(Vector2.Dot(center - a, seg) / segLenSq, 0f, 1f);
        var closest = a + seg * t;
        return (center - closest).LengthSquared() < radius * radius ? t : null;
    }
}
