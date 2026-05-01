using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Player-placed turret. Sits where it was deployed, scans for nearby creatures every tick,
/// and fires a low-damage bullet at the nearest one inside <see cref="ScanRange"/>. Soft HP
/// so creatures can chew through one if it's exposed; the visual is a chunky dwarf-cannon
/// silhouette drawn directly by Game1's render block (Sentry exposes the simulation state).
///
/// Aim: tracks the target with a smoothed angle so the barrel reads as "swinging onto" the
/// creature rather than snapping. Cooldown gates fire-rate. The barrel emits muzzle flash
/// particles into Game1's particle pool when it shoots.
/// </summary>
public sealed class Sentry
{
    public Vector2 Position;
    public float Radius = 4f;
    public float Health = 30f;
    public float MaxHealth = 30f;
    public float HitFlash;
    public float Aim;             // smoothed barrel angle, world-space
    public float Cooldown;
    public bool Dead;

    public const float ScanRange = 110f;
    public const float FireRate = 0.42f;   // seconds between shots when locked on
    public const float TurnRate = 6.0f;    // rad/sec — barrel swings smoothly
    public const float BulletDamage = 9f;
    public const float BulletSpeed = 380f;

    public Sentry(Vector2 pos)
    {
        Position = pos;
    }

    /// <summary>Tick the sentry. <paramref name="onFire"/> is invoked with the muzzle world
    /// position and a normalised aim vector when the sentry decides to shoot — the caller
    /// owns spawning the actual <see cref="Projectile"/> so we don't take a dependency on the
    /// projectile list type here.</summary>
    public void Update(float dt, Planet planet, IReadOnlyList<Creature> creatures,
                       System.Action<Vector2, Vector2> onFire)
    {
        if (Dead) return;
        if (HitFlash > 0) HitFlash -= dt;
        Cooldown -= dt;

        // Find nearest creature within scan range.
        Creature? target = null;
        var bestSq = ScanRange * ScanRange;
        foreach (var c in creatures)
        {
            var dSq = (c.Position - Position).LengthSquared();
            if (dSq < bestSq)
            {
                bestSq = dSq;
                target = c;
            }
        }

        if (target is null)
        {
            // Idle barrel: drift slowly toward planet-up so a sentry with no targets points
            // at the cave roof rather than locking on its last engagement angle.
            var up = planet.UpAt(Position);
            var idleAim = MathF.Atan2(up.Y, up.X);
            Aim = SmoothAngle(Aim, idleAim, TurnRate * 0.4f * dt);
            return;
        }

        var to = target.Position - Position;
        var targetAim = MathF.Atan2(to.Y, to.X);
        Aim = SmoothAngle(Aim, targetAim, TurnRate * dt);

        // Only fire when the barrel is roughly on target — keeps shots from flying off-axis
        // during a fast swing onto a new target.
        var aimErr = MathF.Abs(WrapPi(targetAim - Aim));
        if (Cooldown <= 0f && aimErr < 0.15f)
        {
            var dir = new Vector2(MathF.Cos(Aim), MathF.Sin(Aim));
            var muzzle = Position + dir * (Radius + 2.5f);
            onFire(muzzle, dir);
            Cooldown = FireRate;
        }
    }

    /// <summary>Take damage from a creature touching the turret. Once dead, Game1 sweeps it
    /// out of the list and emits a debris burst.</summary>
    public void TakeHit(float dmg)
    {
        Health -= dmg;
        HitFlash = 0.15f;
        if (Health <= 0f) Dead = true;
    }

    private static float SmoothAngle(float current, float target, float maxStep)
    {
        var d = WrapPi(target - current);
        if (MathF.Abs(d) <= maxStep) return target;
        return current + System.MathF.Sign(d) * maxStep;
    }

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= MathHelper.TwoPi;
        while (a < -MathF.PI) a += MathHelper.TwoPi;
        return a;
    }
}
