using System;
using System.Collections.Generic;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Projectile species. Each kind has a fixed set of behaviours wired in <see cref="Projectile"/>:
/// muzzle radius, gravity-aware path, terrain interaction (some carve craters, some pierce,
/// some bounce, some explode on a fuse), and a hit policy (single creature vs. piercing
/// through several). Body hits are resolved by <see cref="Systems.Combat"/>, which sweeps the
/// per-frame travel segment and reads the pierce/explosion fields here.
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
    /// <summary>Energy bolt: very fast, pierces several creatures in a line and chips the
    /// tile it finally hits.</summary>
    Laser,
    /// <summary>Heavy energy lance: drills straight through many walls and bodies alike,
    /// vaporising a thin tunnel along its path. The mining-laser endgame gun.</summary>
    LaserCannon,
    /// <summary>Launcher round: straight flight, explodes on contact with a proper crater.</summary>
    Rocket,
    /// <summary>Heavy satchel charge: barely throwable (strong gravity, short lob). Bounces
    /// off terrain with a dead thud a few times, settles, and detonates on its FUSE — never
    /// on contact. The biggest non-nuke blast in the game.</summary>
    Tnt,
    /// <summary>Sticky charge: the TNT pack. Same blast and fuse as the satchel, but it
    /// cements to the first wall it touches — demolition you can place on a ceiling.</summary>
    TntPack,
    /// <summary>Peacekeeper sidearm round: a weak cyan stun-bolt fired by the city militia at
    /// hostile creatures (and, feebly, at a rampaging titan). Flagged friendly-to-neutrals so
    /// a street crowd never eats a stray round, and it never aggros the titan onto the player.</summary>
    CivicBolt,
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

    /// <summary>Where this projectile started the current frame. Combat sweeps the
    /// PrevPosition→Position segment against bodies so fast rounds can't skip over them.</summary>
    public Vector2 PrevPosition;

    /// <summary>Bodies (creatures / the Titan) already damaged by this projectile. A piercer
    /// overlaps the same body for several frames while passing through — it must pay its
    /// damage once per body, not once per frame.</summary>
    public readonly HashSet<object> HitVictims = new();

    /// <summary>Contact explosives (rocket, cannon shells, nuke) blow up on the first body
    /// they touch. Fuse explosives (dynamite, TNT) tumble past bodies and only explode on
    /// terrain or fuse-out.</summary>
    public bool DetonatesOnContact => ExplosionRadius > 0f && !ExplodesOnFuse;

    /// <summary>True while the projectile is inside solid rock spending a wall-pierce charge.
    /// Charges are burned per wall *entered* (air→solid transition), not per frame.</summary>
    private bool _inWall;
    private int _bounces;      // Tnt: dead-thud hops taken so far
    private bool _resting;     // Tnt: settled — fuse burns in place
    private bool _stuck;       // TntPack: cemented to a wall — fuse burns in place

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

    /// <summary>Mining power applied to the tile this projectile dies against — gun rounds
    /// chip blocks like a weak pickaxe swing, so sustained fire digs. 0 = no tile damage.
    /// Uses <see cref="Planet.Mine"/>, so hardness scaling and anchor immunity apply.</summary>
    public int MinePower;

    /// <summary>Militia rounds: never hit non-hostile creatures (Combat skips them), and a
    /// titan struck by one doesn't re-aggro onto the player — the city's fight stays the
    /// city's fight.</summary>
    public bool FriendlyToNeutrals;

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
                MinePower = 2;
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
                MinePower = 3;
                break;
            case ProjectileKind.MachineGun:
                Radius = 1.3f;
                MinePower = 1;               // weak per round, but the cadence adds up
                break;
            case ProjectileKind.Laser:
                Radius = 1.2f;
                CreaturePierces = 3;         // burns through a short line of bodies
                MinePower = 4;               // and scorches a decent bite out of the wall
                break;
            case ProjectileKind.LaserCannon:
                Radius = 2f;
                CreaturePierces = 6;         // lances a whole column of bodies
                WallPiercesLeft = 24;        // drills through wall after wall after wall
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
            case ProjectileKind.TntPack:
                // The sticky charge: same blast as the loose satchel, but it cements to the
                // first wall it touches and rides the same fuse there.
                Radius = 3f;
                CraterTiles = 6;
                ExplosionRadius = 70f;
                ExplodesOnFuse = true;
                CreaturePierces = -1;
                break;
            case ProjectileKind.CivicBolt:
                Radius = 1.3f;
                FriendlyToNeutrals = true;
                break;
        }
    }

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Particles? particles = null)
    {
        // Thrown TIMED explosives (dynamite / dynamite pack / TNT) are fuse bombs, never
        // contact bombs: they arc under gravity, thud off terrain with a damped bounce, and
        // blow ONLY when the 3-second fuse burns out. The TNT pack instead cements to the
        // first wall it touches and burns its fuse there.
        var timed = Kind is ProjectileKind.Dynamite or ProjectileKind.DynamitePack
                        or ProjectileKind.Tnt or ProjectileKind.TntPack;
        if (timed)
        {
            // Sticks drop lighter, satchels heavier — the throw weight reads in the arc.
            var grav = Kind is ProjectileKind.Tnt or ProjectileKind.TntPack ? 380f : 260f;
            if (!_stuck) Velocity += planet.GravityAt(Position) * grav * dt;

            // The fuse always ticks, whether it's mid-air, bouncing, or cemented.
            Life -= dt;
            if (Life <= 0f)
            {
                Dead = true;
                CarveCrater(planet, physics, cells, CraterTiles, particles);
                return;
            }
            if (_stuck) return;

            // Substepped move; on terrain contact bounce (or, for the pack, cement) — but
            // keep counting down and never detonate on the hit itself.
            var mv = Velocity * dt;
            var st = Math.Max(1, (int)MathF.Ceiling(mv.Length() / (Planet.TileSize * 0.5f)));
            var stp = mv / st;
            for (var i = 0; i < st; i++)
            {
                if (planet.IsSolidAt(Position + stp))
                {
                    if (Kind == ProjectileKind.TntPack)
                    {
                        Velocity = Vector2.Zero;
                        _stuck = true;
                        return;
                    }
                    // Reflect off the local surface normal, bleeding energy so it tumbles
                    // into ever-smaller hops and keeps bouncing until the fuse ends.
                    var n = planet.UpAt(Position);
                    var vn = Vector2.Dot(Velocity, n);
                    var vt = Velocity - n * vn;
                    Velocity = vt * 0.6f + n * MathF.Abs(vn) * 0.42f;
                    _bounces++;
                    break;   // don't push into the wall this frame
                }
                Position += stp;
            }
            return;
        }

        PrevPosition = Position;
        // Substep the move so fast rounds can't tunnel: the laser covers ~2 tiles per frame,
        // so a single end-point check would skip 1-tile walls entirely. Each substep advances
        // at most half a tile. On terrain impact the projectile stops at the contact face —
        // Combat then sweeps the truncated PrevPosition→Position segment for body hits.
        var move = Velocity * dt;
        var steps = Math.Max(1, (int)MathF.Ceiling(move.Length() / (Planet.TileSize * 0.5f)));
        var step = move / steps;
        for (var i = 0; i < steps; i++)
        {
            Position += step;
            if (planet.IsSolidAt(Position))
            {
                if (!_inWall)
                {
                    // Out of pierce charges → die at the face of this wall, crater and all.
                    if (WallPiercesLeft <= 0)
                    {
                        // Gun rounds chip the tile they hit like a mining swing before dying,
                        // so sustained fire eventually shoots through a wall.
                        if (MinePower > 0)
                        {
                            var (tx, ty) = planet.WorldToTile(Position);
                            if (planet.Mine(tx, ty, MinePower) is { } chipped)
                            {
                                cells.SpawnDustInTile(tx, ty, chipped);
                                physics.MarkDirty(tx, ty);
                                particles?.EmitChips(planet.TileToWorld(tx, ty), chipped);
                            }
                        }
                        Explode(planet, physics, cells, particles);
                        return;
                    }
                    WallPiercesLeft--;   // one charge per wall entered, however thick
                    _inWall = true;
                }
                // Drill a visible puncture along the path through the rock.
                CarveCrater(planet, physics, cells, 1, particles, dust: false);
            }
            else
            {
                _inWall = false;
            }
        }

        Life -= dt;
        if (Life <= 0)
        {
            Dead = true;
            // Fuse-class explosives (dynamite/TNT) carve their crater on fuse-out the same
            // way they would on contact — otherwise a stick that lands gracefully and times
            // out would just disappear without an explosion mark.
            if (ExplodesOnFuse) CarveCrater(planet, physics, cells, CraterTiles, particles);
        }
    }

    /// <summary>Kill the projectile and carve its full crater at the current position. Called
    /// on terrain impact, and by Combat when a contact explosive detonates on a body.</summary>
    public void Explode(Planet planet, Physics physics, Cells cells, Particles? particles = null)
    {
        Dead = true;
        CarveCrater(planet, physics, cells, CraterTiles, particles);
    }

    /// <summary>Blast a roughly circular hole of the given tile radius around Position.
    /// Works ring by ring in world distance — tile counts differ per ring, so offsetting the
    /// angular index directly would shear the crater sideways at any angle far from 0.
    /// Rim tiles crumble into collectible dust cells (their drops survive the blast); the
    /// core is vaporised outright. Every removed tile wakes the settle physics so terrain
    /// undercut by the blast trembles and caves instead of hanging in the air. Destroyed
    /// tiles also fling chips in their own material colour (budget-capped so a nuke doesn't
    /// drown the particle pool).</summary>
    private void CarveCrater(Planet planet, Physics physics, Cells cells, int tiles,
        Particles? particles = null, bool dust = true)
    {
        if (tiles <= 0) return;
        // Callers still author radii in legacy 8-px tiles; convert to today's finer grid so
        // craters keep their world size.
        tiles = (int)(tiles * Planet.LegacyTileScale);
        var maxDist = tiles * Planet.TileSize;
        var maxDistSq = maxDist * maxDist;
        var rel = Position - planet.Center;
        var centerRing = (int)(rel.Length() / Planet.TileSize) - Planet.RingMin;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var chipBudget = 24;   // tiles that get a particle burst; ~2-3 particles each
        var ejectaBudget = 90; // real dust cells blasted into ballistic flight per crater
        for (var r = centerRing - tiles; r <= centerRing + tiles; r++)
        {
            if (r < 0 || r >= planet.Rings) continue;
            var ct = (int)(ang / MathHelper.TwoPi * planet.TilesAt(r));
            for (var dt2 = -(tiles + 1); dt2 <= tiles + 1; dt2++)
            {
                var t = ct + dt2;
                var world = planet.TileToWorld(r, t);
                var distSq = (world - Position).LengthSquared();
                if (distSq > maxDistSq) continue;
                var k = planet.Get(r, t);
                if (!Tiles.IsSolid(k)) continue;
                if (Tiles.IsAnchored(k))
                {
                    // City architecture is blast-RESISTANT, not blast-proof: a detonation
                    // chips it (Planet.Mine accumulates damage), so repeated charges will
                    // eventually breach a wall, but one stick never levels an apartment.
                    // Other anchored tiles (core, supports, placeables) stay untouched.
                    if (k is TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick
                        && planet.Mine(r, t, 2) is { } cracked)
                    {
                        physics.MarkDirty(r, t);
                        cells.SpawnDustInTile(r, t, cracked);
                    }
                    continue;
                }
                planet.Set(r, t, TileKind.Sky);
                // Outer ~45% of the radius crumbles to dust; inside that, vaporised.
                if (dust && distSq > maxDistSq * 0.3f)
                {
                    cells.SpawnDustInTile(r, t, k);
                    // Blast ejecta: some of that fresh dust doesn't just crumble — it's
                    // *thrown*, arcing out of the crater and raining back as real material.
                    // Inner debris flies hardest; the rim mostly slumps.
                    if (ejectaBudget > 0)
                    {
                        var outward = world - Position;
                        if (outward.LengthSquared() > 0.01f)
                        {
                            var frac = MathF.Sqrt(distSq / maxDistSq);
                            ejectaBudget -= cells.EjectFromTile(r, t,
                                Vector2.Normalize(outward), 260f - 160f * frac, 3);
                        }
                    }
                }
                else if (dust && Random.Shared.Next(2) == 0)
                {
                    // Vaporised core: seed a few flame cells in the flash — they torch any
                    // gas pocket or oil sump the blast just breached, then gutter out on
                    // bare rock in a fraction of a second.
                    cells.SpawnInTile(r, t, Material.Fire, 2);
                }
                physics.MarkDirty(r, t);
                if (particles is not null && chipBudget > 0)
                {
                    chipBudget--;
                    particles.EmitCraterChips(world, k, world - Position);
                }
            }
        }
    }
}
