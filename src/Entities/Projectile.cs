using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Projectile species. Each kind has a fixed set of behaviours wired in <see cref="Projectile"/>:
/// muzzle radius, gravity-aware path, terrain interaction (some carve craters, some pierce,
/// some bounce, some explode on a fuse), and a hit policy (single creature vs. piercing
/// through several). The Game1 hit-loop reads <see cref="PiercesCreatures"/> /
/// <see cref="PiercesTiles"/> to decide whether a hit consumes the projectile.
/// </summary>
public enum ProjectileKind
{
    Bullet,
    Cannon,
    Nuke,
    /// <summary>Cannon variant — passes through up to 3 creatures and 1 wall before dying.</summary>
    CannonSilver,
    /// <summary>Cannon variant — small crater on impact + lights a fire patch (burn DoT to creatures hit).</summary>
    CannonRuby,
    /// <summary>Cannon variant — small crater on impact + freezes nearby creatures (slows for 4s).</summary>
    CannonSapphire,
    /// <summary>Cannon variant — heavy crater + big damage radius. The diamond shell.</summary>
    CannonDiamond,
    /// <summary>Thrown explosive: arcs under gravity, fuse counts down, explodes on fuse-out
    /// or on solid contact with a 3-tile crater + radial creature damage.</summary>
    Dynamite,
    /// <summary>Anti-Titan harpoon: punches through tiles + creatures alike, big damage to
    /// the Titan specifically. One-shot heavy spear.</summary>
    Harpoon,
    /// <summary>Sidearm round: slower cadence than the intrinsic bullet but a solid punch.</summary>
    Pistol,
    /// <summary>Machine-gun round: weak, fast, sprayed with a small random spread.</summary>
    MachineGun,
    /// <summary>Energy bolt: very fast, no crater, pierces several creatures in a line.</summary>
    Laser,
    /// <summary>Launcher round: straight flight, explodes on contact with a proper crater.</summary>
    Rocket,
    /// <summary>Heavy satchel charge: barely throwable (strong gravity, short lob), long
    /// fuse, and the biggest non-nuke blast in the game.</summary>
    Tnt,
}

public sealed class Projectile
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Damage;
    public float Life;
    public bool Dead;
    public float Radius = 1.5f;
    public ProjectileKind Kind;

    /// <summary>How many more creature hits this projectile can take before it dies. -1 means
    /// "never dies on creature hit" (used by AoE explosions whose damage is dealt at impact).
    /// 1 = standard one-shot bullet; 3 = silver shell pierce.</summary>
    public int CreaturePierces = 1;

    /// <summary>True if this projectile rolls past a solid tile without dying. The harpoon
    /// uses this to spear through walls; silver shell uses one-wall punch via WallPiercesLeft.</summary>
    public int WallPiercesLeft;

    /// <summary>Burning debuff applied to creatures hit, in seconds. Ruby shell sets this so
    /// hit creatures take damage over time after the projectile dies.</summary>
    public float BurnSeconds;

    /// <summary>Freeze debuff applied to creatures hit, in seconds. Sapphire shell sets this.</summary>
    public float FreezeSeconds;

    /// <summary>Explode-radius (world units) when this projectile dies. 0 = single-target.
    /// Used by ruby/sapphire/diamond shells, dynamite, and the nuke for the area damage step
    /// — Game1's hit loop applies it on death.</summary>
    public float ExplosionRadius;

    /// <summary>Projectile is a thrown explosive that explodes on Life=0 (fuse) instead of
    /// just dying. Set true for dynamite. Other projectiles die quietly when Life expires.</summary>
    public bool ExplodesOnFuse;

    /// <summary>Crater radius in tiles for explosive contact. Cannon = 2, nuke = 6, ruby = 3,
    /// sapphire = 3, diamond = 5, dynamite = 4. 0 = no crater.</summary>
    public int CraterTiles;

    public Projectile(Vector2 pos, Vector2 vel, float damage, float life, ProjectileKind kind = ProjectileKind.Bullet)
    {
        Position = pos;
        Velocity = vel;
        Damage = damage;
        Life = life;
        Kind = kind;
        ConfigureForKind();
    }

    /// <summary>Set radius / pierce / explosion / fuse flags from <see cref="Kind"/>. Centralised
    /// so callers don't have to know the per-kind tuning. Tweaks land here — keeps Game1 lean.</summary>
    private void ConfigureForKind()
    {
        switch (Kind)
        {
            case ProjectileKind.Bullet:
                Radius = 1.5f;
                break;
            case ProjectileKind.Cannon:
                Radius = 3f;
                CraterTiles = 2;
                break;
            case ProjectileKind.Nuke:
                Radius = 5f;
                CraterTiles = 6;
                ExplosionRadius = 90f;
                CreaturePierces = -1;
                break;
            case ProjectileKind.CannonSilver:
                Radius = 2.5f;
                CreaturePierces = 3;
                WallPiercesLeft = 1;
                break;
            case ProjectileKind.CannonRuby:
                Radius = 3f;
                CraterTiles = 3;
                ExplosionRadius = 36f;
                BurnSeconds = 4f;
                CreaturePierces = -1;
                break;
            case ProjectileKind.CannonSapphire:
                Radius = 3f;
                CraterTiles = 3;
                ExplosionRadius = 40f;
                FreezeSeconds = 4f;
                CreaturePierces = -1;
                break;
            case ProjectileKind.CannonDiamond:
                Radius = 4f;
                CraterTiles = 5;
                ExplosionRadius = 56f;
                CreaturePierces = -1;
                break;
            case ProjectileKind.Dynamite:
                Radius = 2.2f;
                CraterTiles = 4;
                ExplosionRadius = 50f;
                ExplodesOnFuse = true;
                CreaturePierces = -1;
                break;
            case ProjectileKind.Harpoon:
                Radius = 2f;
                CreaturePierces = -1;        // skewers everything in its path
                WallPiercesLeft = 8;          // travels through stone like a railgun
                break;
            case ProjectileKind.Pistol:
                Radius = 1.6f;
                break;
            case ProjectileKind.MachineGun:
                Radius = 1.3f;
                break;
            case ProjectileKind.Laser:
                Radius = 1.2f;
                CreaturePierces = 3;         // burns through a short line of bodies
                break;
            case ProjectileKind.Rocket:
                Radius = 2.5f;
                CraterTiles = 3;
                ExplosionRadius = 42f;
                CreaturePierces = -1;
                break;
            case ProjectileKind.Tnt:
                Radius = 3f;
                CraterTiles = 6;
                ExplosionRadius = 70f;
                ExplodesOnFuse = true;
                CreaturePierces = -1;
                break;
        }
    }

    public void Update(float dt, Planet planet)
    {
        // Dynamite arcs — gravity pulls it down so the throw feels weighty. Other projectiles
        // travel ballistic for their lifetime so aim is direct.
        if (Kind == ProjectileKind.Dynamite)
            Velocity += planet.GravityAt(Position) * 240f * dt;

        Position += Velocity * dt;
        Life -= dt;
        if (Life <= 0)
        {
            Dead = true;
            // Fuse-class explosives (dynamite) carve their crater on fuse-out the same way
            // they would on contact — otherwise a stick that lands gracefully and times out
            // would just disappear without an explosion mark.
            if (ExplodesOnFuse) CarveCrater(planet, CraterTiles);
            return;
        }
        if (planet.IsSolidAt(Position))
        {
            // Wall-piercing projectiles burn one charge and keep going. Once charges are out,
            // the next solid hit kills the projectile and may carve a crater.
            if (WallPiercesLeft > 0)
            {
                WallPiercesLeft--;
                CarveCrater(planet, 1);   // tiny puncture so the path is visible
                return;
            }
            Dead = true;
            CarveCrater(planet, CraterTiles);
        }
    }

    private void CarveCrater(Planet planet, int tiles)
    {
        if (tiles <= 0) return;
        var (tx, ty) = planet.WorldToTile(Position);
        for (var dy = -tiles; dy <= tiles; dy++)
        {
            for (var dx = -tiles; dx <= tiles; dx++)
            {
                if (dx * dx + dy * dy > tiles * tiles) continue;
                var k = planet.Get(tx + dx, ty + dy);
                if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k))
                    planet.Set(tx + dx, ty + dy, TileKind.Sky);
            }
        }
    }
}
