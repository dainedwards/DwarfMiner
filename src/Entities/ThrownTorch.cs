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
    /// <summary>The planted pose: world rotation the stick settles at (flame end up-ish).</summary>
    public float BaseAngle;
    /// <summary>Per-torch wobble phase so a row of torches doesn't swing in lockstep.</summary>
    public float Phase = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;

    public ThrownTorch(Vector2 pos, Vector2 vel)
    {
        Position = pos;
        Velocity = vel;
    }

    /// <summary>Integrate flight until contact; then freeze into the stuck pose. Returns
    /// true the frame it sticks (for the thunk sound).</summary>
    public bool Update(float dt, Planet planet)
    {
        if (Stuck) return false;
        Velocity += planet.GravityAt(Position) * 260f * dt;
        var next = Position + Velocity * dt;
        if (planet.IsSolidAt(next))
        {
            // Back the head out of the wall so the stick sits ON the surface, then plant
            // at a slight tilt off local-up (leaning the way it flew in — hand-jammed).
            for (var i = 0; i < 12 && planet.IsSolidAt(next); i++)
                next -= Velocity * (dt / 12f);
            Position = next;
            Stuck = true;
            var up = planet.UpAt(Position);
            var lean = MathF.Sign(Vector2.Dot(Velocity, new Vector2(-up.Y, up.X))) * 0.35f;
            BaseAngle = MathF.Atan2(up.X, -up.Y) + lean;
            Velocity = Vector2.Zero;
            return true;
        }
        Position = next;
        return false;
    }

    /// <summary>Current draw rotation: the planted pose plus the dangle — a light pendulum
    /// swing that never quite settles, as if draughts keep nudging it.</summary>
    public float Swing(float time) => BaseAngle + MathF.Sin(time * 2.1f + Phase) * 0.10f;
}
