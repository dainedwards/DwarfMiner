using System;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// A falling meteor — an ambient hazard-and-reward event. It streaks down from high sky
/// trailing fire, and on impact blows a crater, exposes a small vein of ore (fuel + gold, so
/// meteor craters are worth hunting for ship fuel), sprays molten cells, and hurts the dwarf
/// if they're caught in the blast. Self-contained like <see cref="FallingBoulder"/>: it does
/// its own terrain/player interaction so it never touches the projectile-Combat path.
/// </summary>
public sealed class Meteor
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 7f;
    public bool Dead;
    /// <summary>Ground point the meteor is aimed at — Game1 draws a warning reticle here while
    /// it falls so the player can scramble clear.</summary>
    public readonly Vector2 Target;
    private float _life = 16f;  // safety timeout so a stray meteor never lingers forever
                                // (generous — a slow meteor takes several seconds to arrive)

    public Meteor(Vector2 pos, Vector2 vel, Vector2 target)
    {
        Position = pos;
        Velocity = vel;
        Target = target;
    }

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Player player, Particles particles)
    {
        _life -= dt;
        Velocity += planet.GravityAt(Position) * 520f * dt;   // accelerates as it plunges
        Position += Velocity * dt;

        // Fiery trail pointing back along travel.
        var v = Velocity;
        if (v.LengthSquared() > 1f) v.Normalize();
        particles.EmitRocketExhaust(Position, -v);

        var hitPlayer = (player.Position - Position).LengthSquared()
                        < (Radius + player.Radius + 4f) * (Radius + player.Radius + 4f);
        if (planet.IsSolidAt(Position) || hitPlayer || _life <= 0f)
        {
            Impact(planet, physics, cells, player, particles);
            Dead = true;
        }
    }

    private void Impact(Planet planet, Physics physics, Cells cells, Player player, Particles particles)
    {
        var (cx, cy) = planet.WorldToTile(Position);
        const int r = 10;
        for (var dy = -r; dy <= r; dy++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                var d2 = dx * dx + dy * dy;
                if (d2 > r * r) continue;
                var x = cx + dx; var y = cy + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;

                if (d2 <= 8)
                {
                    // Ore core — an exposed nugget in the crater floor. Fuel-heavy so meteor
                    // hunting feeds the ship's tank; the odd gold slug sweetens it.
                    planet.Set(x, y, Random.Shared.NextDouble() < 0.7 ? TileKind.FuelOre : TileKind.GoldOre);
                }
                else
                {
                    planet.Set(x, y, TileKind.Sky);
                    cells.SpawnDustInTile(x, y, k);
                }
                physics.MarkDirty(x, y);
            }
        }

        // Molten splash + smoke at the impact point.
        cells.SpawnInTile(cx, cy, Material.Lava, Cells.Density * 2);
        cells.SpawnInTile(cx, cy, Material.Smoke, Cells.Density * 3);
        particles.EmitImpact(Position, ProjectileKind.Rocket);

        // Blast: hurt + knock back the dwarf if caught near the strike.
        var toPlayer = player.Position - Position;
        var dist = toPlayer.Length();
        const float blast = r * Planet.TileSize + 12f;
        if (dist < blast)
        {
            player.TakeDamage(38f * (1f - dist / blast));
            if (dist > 0.01f) player.Velocity += toPlayer / dist * 240f;
        }
    }
}
