using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Hulking six-legged creature that scuttles across the planet's surface. Body obeys
/// gravity and tile collision; legs are procedural — each one probes the terrain for a
/// foot anchor and steps when the body drifts too far from it. Hip-to-foot distance is
/// not constrained, so legs visibly stretch over mountains and compress on flat ground.
/// Anger rises with player depth, unlocking stomp earthquakes and ranged boulder hurls.
/// </summary>
public sealed class Titan
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 125f;       // body collision + projectile-hit radius
    public float Health = 2500f;
    public float MaxHealth = 2500f;
    public float Anger;               // 0..100
    public float MoveSpeed = 90f;     // tangent pixels/sec base
    public float JumpSpeed = 340f;
    public float Gravity = 320f;
    public float HitFlash;
    public float StompCooldown = 4f;
    public float HurlCooldown = 6f;
    public float JumpCooldown;
    public bool Grounded;
    public float Facing = 1f;         // smoothed -1..+1; which way the head points along the local tangent
    public float Pulse;               // body breathing/anger pulsation (radians, advanced each tick)
    public TitanLeg[] Legs = null!;   // 6 procedural legs; foot positions are world-space, hips are body-local

    private readonly Planet _planet;

    public Titan(Planet planet, float startAngle)
    {
        _planet = planet;
        Position = FindSurfaceSpawn(planet, startAngle);
        InitLegs();
    }

    private void InitLegs()
    {
        // 3 legs per side, fanned forward/back along the body's tangent axis.
        // Phase staggers the per-leg step threshold so they don't all lift at once.
        Legs = new[]
        {
            new TitanLeg { HipForward = -55f, Side = -1, Phase = 0.10f, HipUp = 14f },
            new TitanLeg { HipForward =   0f, Side = -1, Phase = 0.55f, HipUp = 18f },
            new TitanLeg { HipForward = +55f, Side = -1, Phase = 0.30f, HipUp = 14f },
            new TitanLeg { HipForward = -55f, Side = +1, Phase = 0.65f, HipUp = 14f },
            new TitanLeg { HipForward =   0f, Side = +1, Phase = 0.20f, HipUp = 18f },
            new TitanLeg { HipForward = +55f, Side = +1, Phase = 0.85f, HipUp = 14f },
        };

        // Seed each foot at its first ideal anchor so the legs render correctly on frame 1.
        var up = _planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        foreach (var leg in Legs)
        {
            leg.FootPos = ResolveFootAnchor(leg, up, right, Vector2.Zero);
            leg.StepStart = leg.FootPos;
            leg.StepTarget = leg.FootPos;
            leg.StepT = 1f;
        }
    }

    private static Vector2 FindSurfaceSpawn(Planet planet, float angle)
    {
        // Step inward from far above the highest possible peak and stop at the first solid
        // tile, then float a body's-radius above so the titan settles down under gravity.
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        for (var d = planet.Radius + 30; d > 10; d--)
        {
            var p = planet.Center + dir * (d * Planet.TileSize);
            if (planet.IsSolidAt(p))
                return p + dir * 140f;
        }
        return planet.Center + dir * ((planet.Radius + 20) * Planet.TileSize);
    }

    public void UpdateAnger(Vector2 playerPos)
    {
        var fromCenter = (playerPos - _planet.Center).Length();
        var surface = _planet.Radius * Planet.TileSize;
        var depthFraction = MathHelper.Clamp(1f - fromCenter / surface, 0f, 1f);
        var target = depthFraction * 110f;
        Anger = MathHelper.Lerp(Anger, target, 0.01f);
    }

    public void Update(float dt, Planet planet, Physics physics, Vector2 playerPos, List<FallingBoulder> boulders)
    {
        UpdateAnger(playerPos);

        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);

        // Decide tangent direction toward the player along the local surface.
        var toPlayer = playerPos - Position;
        var moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));

        // Decompose velocity into tangent and normal components so gravity and walking
        // can be handled independently of where on the planet we are.
        var vTangent = Vector2.Dot(Velocity, right);
        var vNormal = Vector2.Dot(Velocity, up);

        var targetTangent = moveAxis * MoveSpeed * (1f + Anger / 80f);
        var accel = Grounded ? 260f : 100f;
        vTangent = MoveToward(vTangent, targetTangent, accel * dt);

        vNormal -= Gravity * dt;

        // Hop when grounded, wanting to move, but not making progress — usually means a
        // wall/ledge in the way (often a pit dug by the player).
        if (Grounded && JumpCooldown <= 0 && moveAxis != 0 && MathF.Abs(vTangent) < 8f)
        {
            vNormal = JumpSpeed;
            Grounded = false;
            JumpCooldown = 0.6f;
        }

        Velocity = right * vTangent + up * vNormal;
        Position += Velocity * dt;
        ResolveCollision(planet);

        // Grounded probe a little under the body, along -up.
        Grounded = ProbeSolid(planet, Position - up * (Radius + 2f));

        // Smoothly turn to face the direction of motion. When stationary, hold the last facing
        // so the head doesn't snap when we pause between steps.
        if (MathF.Abs(vTangent) > 6f)
        {
            var targetFacing = MathF.Sign(vTangent);
            Facing = MathHelper.Lerp(Facing, targetFacing, 1f - MathF.Exp(-3.5f * dt));
        }
        Pulse += dt * (1.4f + Anger * 0.04f);

        UpdateLegs(dt, planet, up, right, vTangent);

        if (JumpCooldown > 0) JumpCooldown -= dt;

        StompCooldown -= dt;
        HurlCooldown -= dt;

        // Stomp: earthquake centered at the titan's feet. Only when grounded — a stomp
        // mid-air looks ridiculous and nothing's there to crack.
        if (StompCooldown <= 0 && Anger > 15f && Grounded)
        {
            var quakeRadius = 130f + Anger * 3f;
            var strength = 2 + (int)(Anger / 35f);
            physics.Earthquake(Position - up * Radius, quakeRadius, strength);
            StompCooldown = MathHelper.Lerp(8f, 2.5f, Anger / 100f);
        }

        // Hurl: lobs a boulder along the line of sight to the player.
        if (HurlCooldown <= 0 && Anger > 50f)
        {
            var dirToPlayer = playerPos - Position;
            if (dirToPlayer.LengthSquared() > 0.0001f)
            {
                dirToPlayer.Normalize();
                var b = new FallingBoulder(Position + dirToPlayer * (Radius + 6f), dirToPlayer * 220f);
                boulders.Add(b);
            }
            HurlCooldown = MathHelper.Lerp(7f, 2f, Anger / 100f);
        }

        if (HitFlash > 0) HitFlash -= dt;
    }

    private void ResolveCollision(Planet planet)
    {
        // Body radius spans several tiles, so iterate to converge on a stable rest pose.
        var span = (int)MathF.Ceiling(Radius / Planet.TileSize) + 1;
        for (var iter = 0; iter < 6; iter++)
        {
            var (tx, ty) = planet.WorldToTile(Position);
            var pushed = false;
            for (var dy = -span; dy <= span; dy++)
            {
                for (var dx = -span; dx <= span; dx++)
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
                    else if (dist <= 0.001f)
                    {
                        // Body center inside a tile — escape via planet up.
                        var u = planet.UpAt(Position);
                        Position += u * 2f;
                        pushed = true;
                    }
                }
            }
            if (!pushed) break;
        }
    }

    private static bool ProbeSolid(Planet planet, Vector2 worldPoint)
    {
        var (x, y) = planet.WorldToTile(worldPoint);
        return Tiles.IsSolid(planet.Get(x, y));
    }

    private static float MoveToward(float v, float target, float maxDelta)
    {
        var d = target - v;
        if (MathF.Abs(d) <= maxDelta) return target;
        return v + MathF.Sign(d) * maxDelta;
    }
}

/// <summary>Boulder hurled by the Titan. Punches through tiles on impact, damages player.</summary>
public sealed class FallingBoulder
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 8f;
    public bool Dead;

    public FallingBoulder(Vector2 pos, Vector2 vel)
    {
        Position = pos;
        Velocity = vel;
    }

    public void Update(float dt, Planet planet, Physics physics, Player player)
    {
        var grav = planet.GravityAt(Position);
        Velocity += grav * 280f * dt;
        Position += Velocity * dt;

        // Damage player on contact.
        var diff = player.Position - Position;
        if (diff.Length() < Radius + player.Radius)
        {
            player.Health -= 25f;
            if (diff.LengthSquared() > 0.0001f)
                player.Velocity += Vector2.Normalize(diff) * 220f;
            Dead = true;
            ExplodeTerrain(planet, physics);
            return;
        }

        if (planet.IsSolidAt(Position))
        {
            Dead = true;
            ExplodeTerrain(planet, physics);
        }
    }

    private void ExplodeTerrain(Planet planet, Physics physics)
    {
        var (tx, ty) = planet.WorldToTile(Position);
        const int r = 4;
        for (var dy = -r; dy <= r; dy++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var k = planet.Get(tx + dx, ty + dy);
                if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k))
                {
                    planet.Set(tx + dx, ty + dy, TileKind.Sky);
                    physics.MarkDirty(tx + dx, ty + dy);
                }
            }
        }
    }
}
