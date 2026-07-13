using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// A physical gem lying in the world — popped out of its host tile when the rock it was
/// embedded in shatters (see <see cref="Planet.TakeGem"/>). Falls and settles like a corpse;
/// the player picks it up by walking over it — no magnet, the gem stays where it landed.
/// No decay: a dropped diamond waits where it fell.
/// </summary>
public sealed class Pickup
{
    public Vector2 Position;
    public Vector2 Velocity;
    public readonly TileKind Kind;
    public float Age;
    public bool Collected;

    public Pickup(Vector2 pos, TileKind kind, Vector2 vel)
    {
        Position = pos;
        Kind = kind;
        Velocity = vel;
    }

    public void Update(float dt, Planet planet, Cells cells)
    {
        Age += dt;
        Velocity += planet.GravityAt(Position) * 300f * dt;
        Velocity *= MathF.Max(0f, 1f - 2.2f * dt);
        var next = Position + Velocity * dt;
        // Powder cells (sand/dirt/gravel/dust) count as ground too, so a gem rests on or
        // just inside a loose pile instead of sifting through it to the tile floor below.
        if (planet.IsSolidAt(next) || cells.PowderAtWorld(next))
        {
            // Light bounce-then-rest off the local surface, like settled debris.
            var n = planet.UpAt(Position);
            var dot = Vector2.Dot(Velocity, n);
            Velocity = (Velocity - n * dot * 1.6f) * 0.35f;
        }
        else
        {
            Position = next;
        }
    }
}
