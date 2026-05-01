using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

public enum ProjectileKind { Bullet, Cannon, Nuke }

public sealed class Projectile
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Damage;
    public float Life;
    public bool Dead;
    public float Radius = 1.5f;
    public ProjectileKind Kind;

    public Projectile(Vector2 pos, Vector2 vel, float damage, float life, ProjectileKind kind = ProjectileKind.Bullet)
    {
        Position = pos;
        Velocity = vel;
        Damage = damage;
        Life = life;
        Kind = kind;
        Radius = kind switch
        {
            ProjectileKind.Bullet => 1.5f,
            ProjectileKind.Cannon => 3f,
            ProjectileKind.Nuke => 5f,
            _ => 1.5f,
        };
    }

    public void Update(float dt, Planet planet)
    {
        Position += Velocity * dt;
        Life -= dt;
        if (Life <= 0) { Dead = true; return; }
        if (planet.IsSolidAt(Position))
        {
            Dead = true;
            // Bigger projectiles dig a small crater.
            if (Kind == ProjectileKind.Cannon || Kind == ProjectileKind.Nuke)
            {
                var crater = Kind == ProjectileKind.Nuke ? 6 : 2;
                var (tx, ty) = planet.WorldToTile(Position);
                for (var dy = -crater; dy <= crater; dy++)
                {
                    for (var dx = -crater; dx <= crater; dx++)
                    {
                        if (dx * dx + dy * dy > crater * crater) continue;
                        var k = planet.Get(tx + dx, ty + dy);
                        if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k))
                            planet.Set(tx + dx, ty + dy, TileKind.Sky);
                    }
                }
            }
        }
    }
}
