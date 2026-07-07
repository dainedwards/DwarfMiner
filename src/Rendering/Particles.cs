using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Rendering;

/// <summary>One short-lived particle. Struct so the list is cache-friendly.</summary>
public struct Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Life;          // remaining seconds
    public float MaxLife;       // for fade ratio
    public Color Color;         // colour at full life
    public Color FadeColor;     // colour at zero life — particle lerps between as it ages
    public float Size;          // world pixels
    public float GravityScale;  // 1 = full planet gravity, 0 = floats, negative = rises (smoke)
    public float Drag;          // per-second velocity decay
    public float LightRadius;   // 0 = no light emission
    public Color LightColor;
    public bool CollideTiles;
}

/// <summary>
/// Minimal particle pool: Update integrates, Draw blits coloured rects, AddLights feeds
/// glowing particles into the lighting pass. Emit* helpers spawn the burst patterns the
/// game needs (mining chips, projectile impacts, boulder dust).
/// </summary>
public sealed class Particles
{
    private const float GravityStrength = 200f;

    private readonly List<Particle> _list = new(2048);
    private readonly Random _rng = new();

    public int Count => _list.Count;

    public void Update(float dt, Planet planet)
    {
        for (var i = _list.Count - 1; i >= 0; i--)
        {
            var p = _list[i];
            p.Life -= dt;
            if (p.Life <= 0) { _list.RemoveAt(i); continue; }

            if (p.GravityScale != 0f)
                p.Velocity += planet.GravityAt(p.Position) * GravityStrength * p.GravityScale * dt;
            if (p.Drag > 0)
                p.Velocity *= MathF.Max(0f, 1f - p.Drag * dt);

            var next = p.Position + p.Velocity * dt;
            if (p.CollideTiles && planet.IsSolidAt(next))
            {
                // Bounce-then-rest: reflect roughly along the local up, lose most energy. Once the
                // particle is barely moving, shorten its life so settled debris vanishes quickly.
                var n = planet.UpAt(p.Position);
                var dot = Vector2.Dot(p.Velocity, n);
                p.Velocity = (p.Velocity - n * dot * 1.5f) * 0.4f;
                if (p.Velocity.LengthSquared() < 4f) p.Life = MathF.Min(p.Life, 0.15f);
                next = p.Position;
            }
            p.Position = next;
            _list[i] = p;
        }
    }

    public void Draw(Renderer r)
    {
        foreach (var p in _list)
        {
            var t = MathHelper.Clamp(p.Life / p.MaxLife, 0f, 1f);
            var c = Color.Lerp(p.FadeColor, p.Color, t);
            r.DrawRect(p.Position, new Vector2(p.Size, p.Size), c);
        }
    }

    public void AddLights(Renderer r)
    {
        foreach (var p in _list)
        {
            if (p.LightRadius <= 0f) continue;
            var t = MathHelper.Clamp(p.Life / p.MaxLife, 0f, 1f);
            r.AddLight(p.Position, p.LightRadius * t, p.LightColor);
        }
    }

    /// <summary>Burst of chips when a tile shatters: tinted to the tile, gravity-bound, brief.</summary>
    public void EmitChips(Vector2 pos, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        var fade = Color.Multiply(baseColor, 0.4f);
        for (var i = 0; i < 8; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 30f + (float)_rng.NextDouble() * 60f;
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.5f + (float)_rng.NextDouble() * 0.4f,
                MaxLife = 0.9f,
                Color = baseColor,
                FadeColor = fade,
                Size = 1f + (float)_rng.NextDouble(),
                GravityScale = 1f,
                Drag = 1.0f,
                CollideTiles = true,
            });
        }
        // Ore tiles also throw a few bright glowing flecks that emit their speckle colour as light.
        if (Tiles.IsOre(kind))
        {
            var spec = Tiles.OreSpeckle(kind);
            for (var i = 0; i < 4; i++)
            {
                var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                var spd = 50f + (float)_rng.NextDouble() * 70f;
                _list.Add(new Particle
                {
                    Position = pos,
                    Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                    Life = 0.35f + (float)_rng.NextDouble() * 0.25f,
                    MaxLife = 0.6f,
                    Color = spec,
                    FadeColor = Color.Black,
                    Size = 1f,
                    GravityScale = 0.4f,
                    Drag = 1.2f,
                    LightRadius = 6f,
                    LightColor = spec,
                    CollideTiles = true,
                });
            }
        }
    }

    /// <summary>Continuous drill chip stream. Drill mining produces a steady jet of bright
    /// orange sparks tangent to the swing direction plus tile-coloured chips falling away.
    /// Called every frame the drill is active.</summary>
    public void EmitDrillChips(Vector2 pos, Vector2 dirToTile, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        var fade = Color.Multiply(baseColor, 0.3f);
        // Orange spark jet along the direction of the drill bit. Two per frame, with slight
        // angular spread so the jet reads as conical not a single beam.
        for (var i = 0; i < 2; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.6f;
            var rotated = new Vector2(
                dirToTile.X * MathF.Cos(spread) - dirToTile.Y * MathF.Sin(spread),
                dirToTile.X * MathF.Sin(spread) + dirToTile.Y * MathF.Cos(spread));
            _list.Add(new Particle
            {
                Position = pos + rotated * 0.5f,
                Velocity = -rotated * (40f + (float)_rng.NextDouble() * 30f),
                Life = 0.08f + (float)_rng.NextDouble() * 0.10f,
                MaxLife = 0.18f,
                Color = new Color(255, 220, 120),
                FadeColor = new Color(180, 60, 20),
                Size = 1f,
                GravityScale = 0.1f,
                Drag = 3f,
                LightRadius = 5f,
                LightColor = new Color(255, 180, 80),
            });
        }
        // One tile-coloured chip flung perpendicular to the drill direction so the wall
        // reads as actively shedding fragments.
        var perp = new Vector2(-dirToTile.Y, dirToTile.X) * (_rng.NextDouble() < 0.5 ? 1f : -1f);
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = perp * (15f + (float)_rng.NextDouble() * 20f),
            Life = 0.25f + (float)_rng.NextDouble() * 0.15f,
            MaxLife = 0.40f,
            Color = baseColor,
            FadeColor = fade,
            Size = 1f,
            GravityScale = 1f,
            Drag = 1.5f,
            CollideTiles = true,
        });
    }

    /// <summary>Hammer impact: a heavy ring of stone shards plus a dust cloud. Used when the
    /// hammer cracks bedrock — heavier than EmitChips, lighter than an explosion.</summary>
    public void EmitHammerImpact(Vector2 pos, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        var fade = Color.Multiply(baseColor, 0.25f);
        // Ring of shards thrown radially outward — heavier and slower than mining chips.
        for (var i = 0; i < 14; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 80f + (float)_rng.NextDouble() * 70f;
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.6f + (float)_rng.NextDouble() * 0.5f,
                MaxLife = 1.1f,
                Color = baseColor,
                FadeColor = fade,
                Size = 1.5f + (float)_rng.NextDouble(),
                GravityScale = 1f,
                Drag = 0.8f,
                CollideTiles = true,
            });
        }
        // Dust shroud
        for (var i = 0; i < 10; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 18f + (float)_rng.NextDouble() * 30f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.7f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.3f,
                Color = new Color(150, 140, 130),
                FadeColor = new Color(35, 30, 30),
                Size = 2f + (float)_rng.NextDouble() * 1.5f,
                GravityScale = -0.05f,
                Drag = 0.6f,
            });
        }
    }

    /// <summary>Mining swing that didn't shatter — a tiny chip puff for click feedback.</summary>
    public void EmitMiningTick(Vector2 pos, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        for (var i = 0; i < 2; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 20f + (float)_rng.NextDouble() * 30f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.18f + (float)_rng.NextDouble() * 0.12f,
                MaxLife = 0.30f,
                Color = baseColor,
                FadeColor = Color.Multiply(baseColor, 0.3f),
                Size = 1f,
                GravityScale = 0.6f,
                Drag = 1.5f,
                CollideTiles = false,
            });
        }
    }

    public void EmitImpact(Vector2 pos, ProjectileKind kind)
    {
        switch (kind)
        {
            case ProjectileKind.Bullet:
            case ProjectileKind.Pistol:
            case ProjectileKind.MachineGun:
            case ProjectileKind.CannonSilver:   // silver shells leave a small spark trail too
                for (var i = 0; i < 4; i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    var col = kind == ProjectileKind.CannonSilver
                        ? new Color(220, 230, 255)
                        : new Color(255, 220, 130);
                    var fade = kind == ProjectileKind.CannonSilver
                        ? new Color(80, 100, 140)
                        : new Color(120, 60, 20);
                    _list.Add(new Particle
                    {
                        Position = pos,
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (30f + (float)_rng.NextDouble() * 40f),
                        Life = 0.20f + (float)_rng.NextDouble() * 0.15f,
                        MaxLife = 0.35f,
                        Color = col,
                        FadeColor = fade,
                        Size = 1f,
                        GravityScale = 0.3f,
                        Drag = 2f,
                        LightRadius = 4f,
                        LightColor = col,
                    });
                }
                break;
            case ProjectileKind.Cannon:
                EmitExplosion(pos, strength: 14f, sparkCount: 14, smokeCount: 10, sparkColor: new Color(255, 150, 50));
                break;
            case ProjectileKind.Nuke:
                // The big one: magenta blast, ember rain, and a tall rising smoke column
                // (strong negative gravity pushes the plume away from the planet centre).
                EmitExplosion(pos, strength: 26f, sparkCount: 32, smokeCount: 26, sparkColor: new Color(255, 90, 230));
                EmitEmbers(pos, count: 16);
                for (var i = 0; i < 14; i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    _list.Add(new Particle
                    {
                        Position = pos + Jitter(6f),
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (10f + (float)_rng.NextDouble() * 20f),
                        Life = 1.6f + (float)_rng.NextDouble() * 1.2f,
                        MaxLife = 2.8f,
                        Color = new Color(150, 110, 150),
                        FadeColor = new Color(30, 20, 35),
                        Size = 2.5f + (float)_rng.NextDouble() * 2f,
                        GravityScale = -0.6f,
                        Drag = 0.4f,
                    });
                }
                break;
            case ProjectileKind.Rocket:
                EmitExplosion(pos, strength: 16f, sparkCount: 18, smokeCount: 12, sparkColor: new Color(255, 150, 60));
                break;
            case ProjectileKind.Tnt:
                EmitExplosion(pos, strength: 24f, sparkCount: 30, smokeCount: 22, sparkColor: new Color(255, 160, 60));
                EmitEmbers(pos, count: 12);
                break;
            case ProjectileKind.CannonRuby:
                // Hot incendiary burst — orange-red sparks, lots of smoke, lingering embers.
                EmitExplosion(pos, strength: 18f, sparkCount: 22, smokeCount: 14, sparkColor: new Color(255, 110, 60));
                EmitEmbers(pos, count: 10);
                break;
            case ProjectileKind.CannonSapphire:
                // Cryo burst — cool blue sparks, bright cold flash, frost shards.
                EmitExplosion(pos, strength: 16f, sparkCount: 18, smokeCount: 8, sparkColor: new Color(140, 200, 255));
                EmitFrostShards(pos, count: 14);
                break;
            case ProjectileKind.CannonDiamond:
                // Heavy AoE — bigger sparks, prismatic flecks, longer-lived smoke.
                EmitExplosion(pos, strength: 22f, sparkCount: 30, smokeCount: 18, sparkColor: new Color(220, 240, 255));
                for (var i = 0; i < 8; i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    var hue = i % 3 == 0 ? new Color(255, 180, 220)
                            : i % 3 == 1 ? new Color(180, 220, 255)
                            : new Color(255, 240, 200);
                    _list.Add(new Particle
                    {
                        Position = pos,
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (110f + (float)_rng.NextDouble() * 80f),
                        Life = 0.5f + (float)_rng.NextDouble() * 0.4f,
                        MaxLife = 0.9f,
                        Color = hue,
                        FadeColor = Color.Black,
                        Size = 1.2f,
                        GravityScale = 0.4f,
                        Drag = 1.0f,
                        LightRadius = 6f,
                        LightColor = hue,
                        CollideTiles = true,
                    });
                }
                break;
            case ProjectileKind.Dynamite:
                EmitExplosion(pos, strength: 20f, sparkCount: 26, smokeCount: 18, sparkColor: new Color(255, 170, 60));
                break;
            case ProjectileKind.Laser:
            case ProjectileKind.LaserCannon:
                // Energy scorch — a hot flash of ionised flecks in the beam's colour.
                for (var i = 0; i < (kind == ProjectileKind.LaserCannon ? 10 : 5); i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    var col = kind == ProjectileKind.LaserCannon
                        ? new Color(140, 230, 255)
                        : new Color(255, 120, 120);
                    _list.Add(new Particle
                    {
                        Position = pos,
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (40f + (float)_rng.NextDouble() * 60f),
                        Life = 0.15f + (float)_rng.NextDouble() * 0.15f,
                        MaxLife = 0.3f,
                        Color = col,
                        FadeColor = new Color(40, 30, 60),
                        Size = 1f,
                        GravityScale = 0.1f,
                        Drag = 2.5f,
                        LightRadius = 6f,
                        LightColor = col,
                    });
                }
                break;
            case ProjectileKind.Harpoon:
                // Pierce trail end — sharp metallic flecks, light shockwave.
                for (var i = 0; i < 8; i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    _list.Add(new Particle
                    {
                        Position = pos,
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (60f + (float)_rng.NextDouble() * 80f),
                        Life = 0.3f + (float)_rng.NextDouble() * 0.2f,
                        MaxLife = 0.5f,
                        Color = new Color(220, 200, 160),
                        FadeColor = new Color(80, 60, 30),
                        Size = 1.2f,
                        GravityScale = 0.3f,
                        Drag = 1.5f,
                        LightRadius = 7f,
                        LightColor = new Color(255, 200, 130),
                        CollideTiles = true,
                    });
                }
                break;
        }
    }

    /// <summary>Long-lived embers for incendiary explosions. Each ember floats slowly,
    /// glows, and drifts upward like cinders. Visible for ~1.5s.</summary>
    private void EmitEmbers(Vector2 pos, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 20f + (float)_rng.NextDouble() * 25f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 1.2f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.8f,
                Color = new Color(255, 170, 80),
                FadeColor = new Color(120, 30, 10),
                Size = 1f,
                GravityScale = -0.2f,
                Drag = 0.8f,
                LightRadius = 8f,
                LightColor = new Color(255, 140, 60),
            });
        }
    }

    /// <summary>Pale-blue frost crystals that shoot outward then fade. Used by sapphire shells
    /// to sell the "cold burst" — visually cold, no heat-trail.</summary>
    private void EmitFrostShards(Vector2 pos, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 70f + (float)_rng.NextDouble() * 60f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.6f + (float)_rng.NextDouble() * 0.3f,
                MaxLife = 0.9f,
                Color = new Color(220, 240, 255),
                FadeColor = new Color(60, 100, 150),
                Size = 1.2f,
                GravityScale = 0.3f,
                Drag = 1.5f,
                LightRadius = 5f,
                LightColor = new Color(140, 200, 255),
                CollideTiles = true,
            });
        }
    }

    /// <summary>Generic dust burst — used for boulder slams.</summary>
    public void EmitDust(Vector2 pos, float strength = 10f)
    {
        for (var i = 0; i < 14; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = strength * 1.5f + (float)_rng.NextDouble() * strength * 1.5f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.7f + (float)_rng.NextDouble() * 0.5f,
                MaxLife = 1.2f,
                Color = new Color(120, 105, 95),
                FadeColor = new Color(30, 25, 28),
                Size = 2f + (float)_rng.NextDouble() * 1.5f,
                GravityScale = -0.1f,
                Drag = 0.7f,
            });
        }
    }

    private void EmitExplosion(Vector2 pos, float strength, int sparkCount, int smokeCount, Color sparkColor)
    {
        // Flash core — one big, near-instant blob of light at the epicentre. Sells the "bang"
        // frame before the sparks/smoke read as an aftermath.
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.1f,
            MaxLife = 0.1f,
            Color = Color.White,
            FadeColor = sparkColor,
            Size = strength * 0.55f,
            GravityScale = 0f,
            Drag = 0f,
            LightRadius = strength * 3.5f,
            LightColor = sparkColor,
        });
        // Fireball — a cluster of fat white-yellow→orange→red blobs boiling out of the
        // centre. Tinted slightly toward the shell's colour so elemental blasts keep their
        // identity, but the body of every explosion reads as fire.
        var fireCount = (int)(strength * 0.6f);
        for (var i = 0; i < fireCount; i++)
        {
            var hot = _rng.Next(3) switch
            {
                0 => new Color(255, 245, 160),
                1 => new Color(255, 180, 70),
                _ => new Color(255, 110, 40),
            };
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            _list.Add(new Particle
            {
                Position = pos + Jitter(3f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (strength * 2.5f + (float)_rng.NextDouble() * strength * 3f),
                Life = 0.25f + (float)_rng.NextDouble() * 0.25f,
                MaxLife = 0.5f,
                Color = Color.Lerp(hot, sparkColor, 0.3f),
                FadeColor = new Color(120, 25, 10),
                Size = 2.5f + (float)_rng.NextDouble() * 2.5f,
                GravityScale = -0.1f,
                Drag = 2.5f,
                LightRadius = 12f,
                LightColor = Color.Lerp(hot, sparkColor, 0.3f),
            });
        }
        // Shockwave ring — evenly spaced fast radial particles with hard drag, so a crisp
        // circle expands a short distance and dies. No gravity: the wavefront stays round.
        // White-hot cooling to fire orange.
        var ringCount = (int)(strength * 1.3f);
        for (var i = 0; i < ringCount; i++)
        {
            var ang = i / (float)ringCount * MathHelper.TwoPi + (float)(_rng.NextDouble() * 0.12);
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * strength * 14f,
                Life = 0.18f + (float)_rng.NextDouble() * 0.08f,
                MaxLife = 0.26f,
                Color = new Color(255, 250, 220),
                FadeColor = Color.Lerp(new Color(255, 150, 50), sparkColor, 0.4f),
                Size = 1.5f,
                GravityScale = 0f,
                Drag = 6f,
            });
        }
        // Debris chunks — heavy scorched-rock lumps lobbed out of the blast; full gravity,
        // bounce on terrain, long life so they visibly rain back down around the crater.
        var debrisCount = (int)(strength * 0.6f);
        for (var i = 0; i < debrisCount; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var shade = 55 + _rng.Next(50);
            _list.Add(new Particle
            {
                Position = pos + Jitter(3f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (strength * 4f + (float)_rng.NextDouble() * strength * 5f),
                Life = 1.0f + (float)_rng.NextDouble() * 0.9f,
                MaxLife = 1.9f,
                Color = new Color(shade + 25, shade + 10, shade),
                FadeColor = new Color(25, 20, 18),
                Size = 2f + (float)_rng.NextDouble() * 1.6f,
                GravityScale = 1.2f,
                Drag = 0.3f,
                CollideTiles = true,
            });
        }
        for (var i = 0; i < sparkCount; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = strength * 6f + (float)_rng.NextDouble() * strength * 6f;
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.4f + (float)_rng.NextDouble() * 0.5f,
                MaxLife = 0.9f,
                Color = sparkColor,
                FadeColor = new Color(80, 40, 20),
                Size = 1.2f + (float)_rng.NextDouble() * 1.2f,
                GravityScale = 0.6f,
                Drag = 1.0f,
                LightRadius = 8f,
                LightColor = sparkColor,
                CollideTiles = true,
            });
        }
        for (var i = 0; i < smokeCount; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = strength * 1.5f + (float)_rng.NextDouble() * strength * 1.5f;
            // Smoke starts ember-lit (warm orange-brown, fresh out of the fireball) and
            // cools to near-black as it rises — fire turning into soot.
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.8f + (float)_rng.NextDouble() * 0.8f,
                MaxLife = 1.6f,
                Color = new Color(185, 115, 60),
                FadeColor = new Color(22, 18, 20),
                Size = 2f + (float)_rng.NextDouble() * 1.5f,
                GravityScale = -0.15f,
                Drag = 0.7f,
            });
        }
    }

    /// <summary>Tile-coloured chips flung out of an explosion crater — the actual destroyed
    /// material visibly blasting away. Direction is biased outward from the epicentre so the
    /// crater reads as erupting. Ore tiles add a glowing speckle so blasted seams sparkle.</summary>
    public void EmitCraterChips(Vector2 pos, TileKind kind, Vector2 outward)
    {
        var baseColor = Tiles.BaseColor(kind);
        var fade = Color.Multiply(baseColor, 0.35f);
        if (outward.LengthSquared() < 0.01f) outward = Jitter(1f);
        outward.Normalize();
        for (var i = 0; i < 2; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 1.1f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(outward.X * c - outward.Y * s, outward.X * s + outward.Y * c);
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = d * (60f + (float)_rng.NextDouble() * 90f),
                Life = 0.7f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.3f,
                Color = baseColor,
                FadeColor = fade,
                Size = 1.5f + (float)_rng.NextDouble(),
                GravityScale = 1f,
                Drag = 0.5f,
                CollideTiles = true,
            });
        }
        if (Tiles.IsOre(kind))
        {
            var spec = Tiles.OreSpeckle(kind);
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = outward * (80f + (float)_rng.NextDouble() * 60f) + Jitter(20f),
                Life = 0.4f + (float)_rng.NextDouble() * 0.3f,
                MaxLife = 0.7f,
                Color = spec,
                FadeColor = Color.Black,
                Size = 1f,
                GravityScale = 0.5f,
                Drag = 1f,
                LightRadius = 6f,
                LightColor = spec,
                CollideTiles = true,
            });
        }
    }

    /// <summary>Short forward cone of hot sparks + a one-frame flash at a gun's muzzle.
    /// Called once per shot; colour matches the weapon's projectile glow.</summary>
    public void EmitMuzzleFlash(Vector2 pos, Vector2 dir, Color color)
    {
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.06f,
            MaxLife = 0.06f,
            Color = Color.White,
            FadeColor = color,
            Size = 3f,
            GravityScale = 0f,
            Drag = 0f,
            LightRadius = 16f,
            LightColor = color,
        });
        for (var i = 0; i < 4; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.7f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = d * (90f + (float)_rng.NextDouble() * 80f),
                Life = 0.08f + (float)_rng.NextDouble() * 0.08f,
                MaxLife = 0.16f,
                Color = color,
                FadeColor = new Color(60, 30, 20),
                Size = 1f,
                GravityScale = 0f,
                Drag = 6f,
            });
        }
    }

    /// <summary>Per-frame in-flight trail keyed by projectile kind: rocket exhaust smoke,
    /// sputtering fuse sparks on thrown explosives, energy motes behind beam weapons, and a
    /// radioactive sparkle on the nuke. Cheap no-op for kinds without a trail.</summary>
    public void EmitTrail(Projectile p)
    {
        if (p.Velocity.LengthSquared() < 1f) return;
        var back = -Vector2.Normalize(p.Velocity);
        switch (p.Kind)
        {
            case ProjectileKind.Rocket:
                // Exhaust: grey puff shed at the tail every frame, drifting and expanding.
                _list.Add(new Particle
                {
                    Position = p.Position + back * 6f + Jitter(1f),
                    Velocity = back * 20f + Jitter(8f),
                    Life = 0.35f + (float)_rng.NextDouble() * 0.25f,
                    MaxLife = 0.6f,
                    Color = new Color(150, 140, 135),
                    FadeColor = new Color(40, 35, 35),
                    Size = 1.5f + (float)_rng.NextDouble(),
                    GravityScale = -0.05f,
                    Drag = 1.2f,
                });
                break;
            case ProjectileKind.Dynamite:
            case ProjectileKind.Tnt:
                // Sputtering fuse — intermittent tiny sparks tumbling off the stick.
                if (_rng.NextDouble() < 0.55)
                {
                    _list.Add(new Particle
                    {
                        Position = p.Position + Jitter(2f),
                        Velocity = Jitter(20f),
                        Life = 0.15f + (float)_rng.NextDouble() * 0.15f,
                        MaxLife = 0.3f,
                        Color = new Color(255, 230, 130),
                        FadeColor = new Color(150, 60, 20),
                        Size = 1f,
                        GravityScale = 0.4f,
                        Drag = 1.5f,
                        LightRadius = 4f,
                        LightColor = new Color(255, 200, 100),
                    });
                }
                break;
            case ProjectileKind.LaserCannon:
                // Ionised wake — cyan motes hanging briefly along the beam's path.
                _list.Add(new Particle
                {
                    Position = p.Position + back * (float)(_rng.NextDouble() * 8.0) + Jitter(1.5f),
                    Velocity = Jitter(10f),
                    Life = 0.12f + (float)_rng.NextDouble() * 0.12f,
                    MaxLife = 0.24f,
                    Color = new Color(140, 230, 255),
                    FadeColor = new Color(30, 50, 80),
                    Size = 1f,
                    GravityScale = 0f,
                    Drag = 2f,
                    LightRadius = 5f,
                    LightColor = new Color(120, 225, 255),
                });
                break;
            case ProjectileKind.Nuke:
                // Radioactive shimmer — magenta flecks shed along the whole flight.
                if (_rng.NextDouble() < 0.7)
                {
                    _list.Add(new Particle
                    {
                        Position = p.Position + Jitter(3f),
                        Velocity = Jitter(15f),
                        Life = 0.3f + (float)_rng.NextDouble() * 0.25f,
                        MaxLife = 0.55f,
                        Color = new Color(255, 120, 235),
                        FadeColor = new Color(60, 20, 60),
                        Size = 1f,
                        GravityScale = 0f,
                        Drag = 1f,
                        LightRadius = 6f,
                        LightColor = new Color(255, 90, 230),
                    });
                }
                break;
            case ProjectileKind.Harpoon:
                // Thin slipstream flecks so the spear's speed reads.
                if (_rng.NextDouble() < 0.4)
                {
                    _list.Add(new Particle
                    {
                        Position = p.Position + back * 8f + Jitter(1f),
                        Velocity = back * 30f,
                        Life = 0.12f,
                        MaxLife = 0.12f,
                        Color = new Color(210, 200, 180),
                        FadeColor = new Color(60, 50, 40),
                        Size = 1f,
                        GravityScale = 0f,
                        Drag = 3f,
                    });
                }
                break;
        }
    }

    private Vector2 Jitter(float r) =>
        new((float)(_rng.NextDouble() * 2 - 1) * r, (float)(_rng.NextDouble() * 2 - 1) * r);
}
