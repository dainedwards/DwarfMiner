using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// A thrown torch: arcs under gravity, sticks to the first solid surface it hits, and
/// stays there as a persistent light source — planted at an angle and dangling slightly
/// (a soft pendulum wobble around its stuck pose), so a corridor of thrown torches reads
/// as live flames, not painted sprites. Stuck torches persist in the run save.
/// </summary>
public sealed class ThrownTorch
{
    public Vector2 Position;
    public Vector2 Velocity;
    public bool Stuck;
    /// <summary>The planted pose: world rotation the stick settles at (flame end up-ish).
    /// The torch hangs BY ITS HILT from <see cref="Position"/> (the contact point) — the
    /// stick and flame extend from there, and the pendulum swings about it.</summary>
    public float BaseAngle;
    /// <summary>Damped-pendulum state: angular offset from the rest pose and its velocity.
    /// The impact converts the throw's tangential speed into an initial swing, which the
    /// spring bleeds off over a couple of seconds.</summary>
    public float AngleOff;
    public float AngVel;
    /// <summary>Per-torch wobble phase so a row of torches doesn't swing in lockstep.</summary>
    public float Phase = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;

    public ThrownTorch(Vector2 pos, Vector2 vel)
    {
        Position = pos;
        Velocity = vel;
    }

    /// <summary>Integrate flight until contact; once planted, run the hilt pendulum —
    /// spring toward the rest pose with damping, so a fresh torch swings hard and settles
    /// into the idle sway. Returns true the frame it sticks (for the thunk sound).</summary>
    public bool Update(float dt, Planet planet)
    {
        if (Stuck)
        {
            AngVel += (-AngleOff * 30f - AngVel * 2.4f) * dt;
            AngleOff += AngVel * dt;
            return false;
        }
        Velocity += planet.GravityAt(Position) * 260f * dt;
        var next = Position + Velocity * dt;
        if (planet.IsSolidAt(next))
        {
            // Back the hilt out of the wall so it anchors ON the surface, then plant at a
            // slight tilt off local-up (leaning the way it flew in — hand-jammed).
            for (var i = 0; i < 12 && planet.IsSolidAt(next); i++)
                next -= Velocity * (dt / 12f);
            Position = next;
            Stuck = true;
            var up = planet.UpAt(Position);
            var tangent = new Vector2(-up.Y, up.X);
            var lean = MathF.Sign(Vector2.Dot(Velocity, tangent)) * 0.3f;
            BaseAngle = MathF.Atan2(up.X, -up.Y) + lean;
            // Impact energy → initial pendulum kick, clamped so a hard throw doesn't spin.
            AngVel = MathHelper.Clamp(Vector2.Dot(Velocity, tangent) * 0.03f, -5f, 5f);
            AngleOff = 0f;
            Velocity = Vector2.Zero;
            return true;
        }
        Position = next;
        return false;
    }

    /// <summary>Current draw rotation: rest pose + live pendulum + a faint perpetual sway,
    /// as if draughts keep nudging it.</summary>
    public float Swing(float time) =>
        BaseAngle + AngleOff + MathF.Sin(time * 2.1f + Phase) * 0.05f;
}
