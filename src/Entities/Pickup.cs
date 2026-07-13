using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// A physical gem lying in the world — popped out when a gem-class tile (see
/// <see cref="Tiles.IsGem"/>) shatters, instead of the vacuumable dust ordinary tiles crumble
/// into. Falls and settles like a corpse, then a short-range magnet draws it to the dwarf and
/// walking over it banks the drop. No decay: a dropped diamond waits where it fell.
/// </summary>
public sealed class Pickup
{
    /// <summary>Distance at which the gem starts sliding toward the dwarf.</summary>
    public const float MagnetRadius = 30f;

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

    public void Update(float dt, Planet planet, Player player)
    {
        Age += dt;
        var toPlayer = player.Position - Position;
        var d2 = toPlayer.LengthSquared();
        if (d2 < MagnetRadius * MagnetRadius && d2 > 0.01f)
        {
            // Magnet: pull hardens as the gem closes so the last stretch snaps in. Tile
            // collision below still applies — the magnet helps in the open, it doesn't
            // drag gems through walls.
            var d = MathF.Sqrt(d2);
            Velocity += toPlayer / d * ((1f - d / MagnetRadius) * 900f + 220f) * dt;
        }
        else
        {
            Velocity += planet.GravityAt(Position) * 300f * dt;
        }
        Velocity *= MathF.Max(0f, 1f - 2.2f * dt);
        var next = Position + Velocity * dt;
        if (planet.IsSolidAt(next))
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
