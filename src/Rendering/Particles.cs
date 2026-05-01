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
                for (var i = 0; i < 4; i++)
                {
                    var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
                    _list.Add(new Particle
                    {
                        Position = pos,
                        Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (30f + (float)_rng.NextDouble() * 40f),
                        Life = 0.20f + (float)_rng.NextDouble() * 0.15f,
                        MaxLife = 0.35f,
                        Color = new Color(255, 220, 130),
                        FadeColor = new Color(120, 60, 20),
                        Size = 1f,
                        GravityScale = 0.3f,
                        Drag = 2f,
                        LightRadius = 4f,
                        LightColor = new Color(255, 220, 130),
                    });
                }
                break;
            case ProjectileKind.Cannon:
                EmitExplosion(pos, strength: 14f, sparkCount: 14, smokeCount: 10, sparkColor: new Color(255, 150, 50));
                break;
            case ProjectileKind.Nuke:
                EmitExplosion(pos, strength: 26f, sparkCount: 32, smokeCount: 26, sparkColor: new Color(255, 90, 230));
                break;
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
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.8f + (float)_rng.NextDouble() * 0.8f,
                MaxLife = 1.6f,
                Color = new Color(110, 100, 95),
                FadeColor = new Color(20, 18, 22),
                Size = 2f + (float)_rng.NextDouble() * 1.5f,
                GravityScale = -0.15f,
                Drag = 0.7f,
            });
        }
    }

    private Vector2 Jitter(float r) =>
        new((float)(_rng.NextDouble() * 2 - 1) * r, (float)(_rng.NextDouble() * 2 - 1) * r);
}
