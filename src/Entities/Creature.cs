using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

public sealed class Creature
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 4f;
    public float Health = 12f;
    public float MoveSpeed = 55f;
    public float ContactDamage = 8f;
    public float HitFlash;
    public float Wander;

    public Creature(Vector2 pos) { Position = pos; }

    public void Update(float dt, Planet planet, Player player)
    {
        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);

        var toPlayer = player.Position - Position;
        var dist = toPlayer.Length();

        // Chase if close enough, else wander.
        float moveAxis;
        if (dist < 140f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0)
            {
                Wander = 1.5f + (float)Random.Shared.NextDouble() * 2f;
            }
            moveAxis = MathF.Sin(Wander * 3f);
        }

        var vT = Vector2.Dot(Velocity, right);
        var vN = Vector2.Dot(Velocity, up);
        vT = MoveToward(vT, moveAxis * MoveSpeed, 400f * dt);
        vN -= 320f * dt;
        Velocity = right * vT + up * vN;

        Position += Velocity * dt;
        ResolveTileCollision(planet);

        if (HitFlash > 0) HitFlash -= dt;

        // Damage player on contact.
        var diff = player.Position - Position;
        if (diff.Length() < Radius + player.Radius)
        {
            player.Health -= ContactDamage * dt;
            // Knockback.
            if (diff.LengthSquared() > 0.0001f)
            {
                var n = Vector2.Normalize(diff);
                player.Velocity += n * 60f;
            }
        }
    }

    private void ResolveTileCollision(Planet planet)
    {
        var (tx, ty) = planet.WorldToTile(Position);
        for (var iter = 0; iter < 3; iter++)
        {
            var pushed = false;
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var x = tx + dx; var y = ty + dy;
                    if (!Tiles.IsSolid(planet.Get(x, y))) continue;
                    var rect = new Rectangle(x * Planet.TileSize, y * Planet.TileSize, Planet.TileSize, Planet.TileSize);
                    var closest = new Vector2(
                        Math.Clamp(Position.X, rect.X, rect.X + rect.Width),
                        Math.Clamp(Position.Y, rect.Y, rect.Y + rect.Height));
                    var diff = Position - closest;
                    var dist = diff.Length();
                    if (dist < Radius && dist > 0.001f)
                    {
                        var n = diff / dist;
                        Position += n * (Radius - dist + 0.05f);
                        var into = Vector2.Dot(Velocity, n);
                        if (into < 0) Velocity -= n * into;
                        pushed = true;
                    }
                }
            }
            if (!pushed) break;
        }
    }

    private static float MoveToward(float v, float target, float maxDelta)
    {
        var d = target - v;
        if (MathF.Abs(d) <= maxDelta) return target;
        return v + MathF.Sign(d) * maxDelta;
    }
}
