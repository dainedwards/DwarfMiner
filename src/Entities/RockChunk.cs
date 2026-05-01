using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// One tile-sized rock fragment thrown loose by a stone collapse. Lifecycle:
///   1. Tremble: spawn in place and oscillate for ~0.3s as a Terraria-style warning so the
///      player can step out from under a falling roof.
///   2. Fall: gain an initial gravity kick + tumble (rotation), accelerate under planet
///      gravity, integrate position.
///   3. Land: the moment the chunk's centre is inside a solid tile, try to settle back into
///      the last sky tile it passed through; if that slot is taken, just shatter.
/// Per-chunk colour jitter and angular velocity are sampled at construction so a region of
/// 50 chunks looks like 50 different fragments instead of a uniform stamp.
/// </summary>
public sealed class RockChunk
{
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 Anchor;
    public TileKind Kind;
    public float Radius = 3.5f;
    public bool Dead;
    public float Age;
    /// <summary>Seconds left in the tremble warning. Counts down to zero, after which the chunk falls.</summary>
    public float Tremble;
    public float Rotation;
    public float AngularVelocity;
    /// <summary>-1..1 RGB shift applied at draw time so chunks of the same kind aren't carbon copies.</summary>
    public float ColorJitter;

    public const float TrembleDuration = 0.30f;

    public RockChunk(Vector2 pos, TileKind kind)
    {
        Position = pos;
        Anchor = pos;
        Kind = kind;
        Tremble = TrembleDuration;
        AngularVelocity = (float)(Random.Shared.NextDouble() - 0.5) * 4f;  // ±2 rad/s
        ColorJitter = (float)(Random.Shared.NextDouble() - 0.5) * 2f;       // -1..1
    }

    public void Update(float dt, Planet planet, Physics physics, Player player)
    {
        Age += dt;

        // Tremble: tile shakes in place, doesn't collide, doesn't fall. Amplitude eases out.
        if (Tremble > 0f)
        {
            Tremble -= dt;
            var ease = MathHelper.Clamp(Tremble / TrembleDuration, 0f, 1f);
            var amp = 0.7f * ease;
            var t = Age * 30f;
            Position = Anchor + new Vector2(MathF.Sin(t) * amp, MathF.Cos(t * 1.3f) * amp);
            return;
        }

        var grav = planet.GravityAt(Position);
        // First post-tremble frame: a small kick along gravity so the chunk separates cleanly from its origin.
        if (Velocity == Vector2.Zero) Velocity = grav * 12f;
        Velocity += grav * 320f * dt;
        Position += Velocity * dt;
        Rotation += AngularVelocity * dt;

        // Brief grace after tremble before chunks can hurt the player.
        if (Age > TrembleDuration + 0.12f)
        {
            var diff = player.Position - Position;
            if (diff.Length() < Radius + player.Radius)
            {
                player.TakeDamage(8f);
                if (diff.LengthSquared() > 0.0001f)
                    player.Velocity += Vector2.Normalize(diff) * 120f;
                Dead = true;
                return;
            }
        }

        if (planet.IsSolidAt(Position))
        {
            Dead = true;
            // Try to settle back into the tile we came from — one step opposite gravity from
            // where we crashed. If that slot is still sky, re-tile-ify; else shatter.
            var (tx, ty) = planet.WorldToTile(Position);
            var (dx, dy) = SnapCardinal(grav);
            var rx = tx - dx;
            var ry = ty - dy;
            if (planet.InBounds(rx, ry) && planet.Get(rx, ry) == TileKind.Sky)
            {
                planet.Set(rx, ry, Kind);
                physics.MarkDirty(rx, ry);
            }
        }
    }

    private static (int x, int y) SnapCardinal(Vector2 v)
    {
        if (MathF.Abs(v.X) > MathF.Abs(v.Y)) return (Math.Sign(v.X), 0);
        return (0, Math.Sign(v.Y));
    }
}
