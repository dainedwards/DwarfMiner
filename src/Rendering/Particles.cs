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
    /// <summary>Flash-class light (explosion cores, muzzle flashes): rendered through the
    /// ray-cast hero-light pass, so the flash throws crisp Noita-style shadows instead of
    /// only feeding the soft propagated grid.</summary>
    public bool HeroLight;
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
            if (p.Life <= 0)
            {
                // Swap-remove: RemoveAt(i) shifts the whole tail, which turns mass die-offs
                // (disaster bursts expiring together) into O(n²) struct copies. Order doesn't
                // matter for 1-2 px unsorted quads. The last element was already updated this
                // pass (we iterate backwards), so no particle is skipped.
                _list[i] = _list[_list.Count - 1];
                _list.RemoveAt(_list.Count - 1);
                continue;
            }

            if (p.GravityScale != 0f)
                p.Velocity += planet.GravityAt(p.Position) * GravityStrength * p.GravityScale * dt;
            if (p.Drag > 0)
                p.Velocity *= MathF.Max(0f, 1f - p.Drag * dt);

            var next = p.Position + p.Velocity * dt;
            if (p.CollideTiles && planet.IsSolidAt(next))
            {
                // Bounce-then-rest: reflect roughly along the local up, lose most energy. Once
                // the particle is barely moving, shorten its life so settled debris vanishes
                // quickly — EXCEPT glowing debris (cinders): a coal that lands should sit and
                // shed its light while it cools, not wink out on touchdown.
                var n = planet.UpAt(p.Position);
                var dot = Vector2.Dot(p.Velocity, n);
                p.Velocity = (p.Velocity - n * dot * 1.5f) * 0.4f;
                if (p.Velocity.LengthSquared() < 4f)
                    p.Life = MathF.Min(p.Life, p.LightRadius > 0f ? 1.4f : 0.15f);
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
            if (p.HeroLight) r.AddHeroLight(p.Position, p.LightRadius * t, p.LightColor);
            else r.AddLight(p.Position, p.LightRadius * t, p.LightColor);
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

    /// <summary>Continuous mining-laser beam: hot glow motes strung along the beam line plus
    /// molten spatter where it eats rock. Called every frame the beam is held, so mote
    /// lifetimes are tiny — the beam vanishes the instant the trigger is released.</summary>
    public void EmitMiningBeam(Vector2 from, Vector2 to, bool hitting)
    {
        var d = to - from;
        var len = d.Length();
        if (len < 1f) return;
        var dir = d / len;
        // Beam body: a mote every ~3 px, drifting toward the strike point so the stream reads
        // as flowing. Only every fourth mote glows — a full-beam light string would flood the
        // lighting pass.
        const float step = 3f;
        var n = (int)(len / step);
        for (var i = 0; i <= n; i++)
        {
            var hotCore = _rng.NextDouble() < 0.35;
            _list.Add(new Particle
            {
                Position = from + dir * (i * step) + Jitter(0.7f),
                Velocity = dir * 25f,
                Life = 0.04f + (float)_rng.NextDouble() * 0.04f,
                MaxLife = 0.08f,
                Color = hotCore ? new Color(255, 240, 200) : new Color(255, 150, 40),
                FadeColor = new Color(180, 50, 10),
                Size = hotCore ? 1f : 1.5f,
                GravityScale = 0f,
                LightRadius = i % 4 == 0 ? 7f : 0f,
                LightColor = new Color(255, 160, 60),
            });
        }
        if (!hitting) return;
        // Molten spatter at the strike point — sprays back toward the emitter side.
        for (var i = 0; i < 2; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 1.6f;
            var back = -dir;
            var rotated = new Vector2(
                back.X * MathF.Cos(spread) - back.Y * MathF.Sin(spread),
                back.X * MathF.Sin(spread) + back.Y * MathF.Cos(spread));
            _list.Add(new Particle
            {
                Position = to,
                Velocity = rotated * (50f + (float)_rng.NextDouble() * 50f),
                Life = 0.15f + (float)_rng.NextDouble() * 0.15f,
                MaxLife = 0.30f,
                Color = new Color(255, 200, 90),
                FadeColor = new Color(120, 30, 10),
                Size = 1f,
                GravityScale = 0.6f,
                Drag = 1.5f,
                LightRadius = 5f,
                LightColor = new Color(255, 150, 60),
                CollideTiles = true,
            });
        }
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
            case ProjectileKind.TntPack:
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
            case ProjectileKind.DynamitePack:
                EmitExplosion(pos, strength: 34f, sparkCount: 44, smokeCount: 30, sparkColor: new Color(255, 165, 55));
                EmitEmbers(pos, count: 16);
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

    /// <summary>A single falling raindrop: a short streak that starts high under a cloud and
    /// accelerates toward the ground under planet gravity. <paramref name="down"/> is the
    /// toward-surface direction; the drop is tinted by the rain kind.</summary>
    public void EmitRain(Vector2 pos, Vector2 down, Color color)
    {
        var jitter = new Vector2(-down.Y, down.X) * (((float)_rng.NextDouble() - 0.5f) * 12f);
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = down * (140f + (float)_rng.NextDouble() * 90f) + jitter,
            Life = 0.6f + (float)_rng.NextDouble() * 0.5f,
            MaxLife = 1.1f,
            Color = color,
            FadeColor = color * 0.4f,
            Size = 1.4f + (float)_rng.NextDouble() * 1.1f,
            GravityScale = 1.1f,
            Drag = 0.02f,
        });
    }

    /// <summary>A single snowflake: drifts down slowly, wafting side to side, barely pulled by
    /// gravity — fluffy and light, the ice world's answer to rain.</summary>
    public void EmitSnow(Vector2 pos, Vector2 down, Color color)
    {
        var side = new Vector2(-down.Y, down.X);
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = down * (26f + (float)_rng.NextDouble() * 20f) + side * (((float)_rng.NextDouble() - 0.5f) * 24f),
            Life = 1.4f + (float)_rng.NextDouble() * 1.4f,
            MaxLife = 2.8f,
            Color = color,
            FadeColor = color * 0.6f,
            Size = 1.3f + (float)_rng.NextDouble() * 1.4f,
            GravityScale = 0.12f,
            Drag = 0.6f,
        });
    }

    private void EmitExplosion(Vector2 pos, float strength, int sparkCount, int smokeCount, Color sparkColor)
    {
        // Flash core — one big, near-instant blob of light at the epicentre. Sells the "bang"
        // frame before the sparks/smoke read as an aftermath. Hero-lit: the flash throws
        // hard ray-cast shadows off the crater rim and every creature silhouette near it.
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.14f,
            MaxLife = 0.14f,
            Color = Color.White,
            FadeColor = sparkColor,
            Size = strength * 0.55f,
            GravityScale = 0f,
            Drag = 0f,
            LightRadius = strength * 4.5f,
            LightColor = Color.Lerp(sparkColor, Color.White, 0.5f),
            HeroLight = true,
        });
        // Lasting cinders: after the flash dies, a handful of thrown coals keep the crater
        // and its surroundings lit for a couple of seconds while they cool.
        EmitCinders(pos, Vector2.Zero, Math.Clamp((int)(strength / 3f), 4, 10), scatter: strength * 4f);
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
            // One-frame hero flash: each shot strobes the cave with hard shadows — the
            // propagated grid alone made muzzle flashes read as a faint blush.
            LightRadius = 44f,
            LightColor = color,
            HeroLight = true,
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

    /// <summary>Rocket liftoff plume: a hot white-yellow core fading to smoke, sprayed along
    /// <paramref name="dir"/> (the exhaust direction, i.e. away from the ship's nose). Called
    /// every frame during the launch cinematic to build a continuous column of flame.</summary>
    public void EmitRocketExhaust(Vector2 pos, Vector2 dir)
    {
        // Bright light at the nozzle sells the flare in the lighting pass.
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.05f,
            MaxLife = 0.05f,
            Color = new Color(255, 240, 200),
            FadeColor = new Color(255, 150, 60),
            Size = 7f,
            LightRadius = 60f,
            LightColor = new Color(255, 170, 90),
        });
        for (var i = 0; i < 10; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.9f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var hot = i < 5;
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 3f,
                Velocity = d * (120f + (float)_rng.NextDouble() * 160f),
                Life = 0.25f + (float)_rng.NextDouble() * 0.4f,
                MaxLife = 0.65f,
                Color = hot ? new Color(255, 230, 150) : new Color(200, 90, 50),
                FadeColor = hot ? new Color(220, 100, 50) : new Color(70, 70, 75),
                Size = hot ? 3.5f : 5f,
                GravityScale = -0.05f,
                Drag = 2.2f,
                LightRadius = hot ? 24f : 0f,
                LightColor = new Color(255, 160, 80),
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
            case ProjectileKind.DynamitePack:
            case ProjectileKind.Tnt:
            case ProjectileKind.TntPack:
                // Sputtering fuse — intermittent tiny sparks tumbling off the stick, plus a
                // flickering glow riding the charge itself so a lobbed stick visibly burns
                // its way through a dark cave right up to the bang.
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
                        LightRadius = 10f,
                        LightColor = new Color(255, 200, 100),
                    });
                }
                _list.Add(new Particle
                {
                    Position = p.Position,
                    Velocity = Vector2.Zero,
                    Life = 0.05f,
                    MaxLife = 0.05f,
                    Color = Color.Transparent,   // pure light carrier — no visible quad
                    FadeColor = Color.Transparent,
                    Size = 0f,
                    LightRadius = 16f + (float)_rng.NextDouble() * 8f,   // fuse flicker
                    LightColor = new Color(255, 190, 90),
                });
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

    /// <summary>Flamethrower tongue: a fat cone of white→orange→ember blobs riding the aim,
    /// each shedding real light, plus a hero-lit throat flicker so the hose cuts hard
    /// shadows while it roars. Called every puff, so held fire reads as one continuous
    /// jet. (The burning FUEL is real Fire cells launched by Game1 — these are the glow
    /// around it.)</summary>
    /// <summary>Gravity multiplier that gives a hose particle the SAME ballistic arc as the
    /// flying fuel cells it rides with: cells pull ~450 px/s² toward the core (FlyGravity),
    /// particles pull GravityStrength(200) × scale — so 2.25 matches, and the visible tongue
    /// droops along exactly the trajectory the real fire/acid lands on.</summary>
    private const float HoseArcGravity = 2.25f;

    public void EmitFlameJet(Vector2 pos, Vector2 dir, float reach)
    {
        // Particle speed tuned so a blob travels roughly `reach` before its life ends, so the
        // visible tongue matches the stream's current length. Blobs COLLIDE with tiles and fall
        // on the same arc as the launched fuel cells — the Noita look: a dense bright throat
        // that stretches into a drooping, smoking tongue.
        var jetSpeed = reach * 2.6f;
        for (var i = 0; i < 9; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.24f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var hot = i < 3;
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 5f,
                Velocity = d * (jetSpeed * (0.75f + (float)_rng.NextDouble() * 0.5f)),
                Life = 0.18f + (float)_rng.NextDouble() * 0.22f,
                MaxLife = 0.4f,
                Color = hot ? new Color(255, 245, 190) : new Color(255, 160, 60),
                FadeColor = new Color(150, 45, 15),
                Size = hot ? 1.8f : 2.4f + (float)_rng.NextDouble() * 0.8f,
                GravityScale = HoseArcGravity,   // same arc as the fuel cells
                Drag = 1.2f,
                CollideTiles = true,
                LightRadius = hot ? 66f : 36f,
                LightColor = new Color(255, 170, 70),
            });
        }
        // Sooty curls shed along the tongue — they inherit the arc, then buoy upward as they
        // cool (weak net gravity), so spent flame rolls off the stream like Noita's smoke.
        for (var i = 0; i < 3; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.3f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                Position = pos + d * (8f + (float)_rng.NextDouble() * (reach * 0.35f)),
                Velocity = d * (jetSpeed * 0.45f),
                Life = 0.4f + (float)_rng.NextDouble() * 0.45f,
                MaxLife = 0.9f,
                Color = new Color(95, 62, 45),
                FadeColor = new Color(35, 28, 30),
                Size = 2.1f + (float)_rng.NextDouble(),
                GravityScale = -0.18f,
                Drag = 2.2f,
                CollideTiles = true,
            });
        }
        // Hero flicker riding a third of the way down the tongue: the shadow-casting part
        // of the fire, jittered per puff so the whole cave breathes with the hose.
        _list.Add(new Particle
        {
            Position = pos + dir * (reach * (0.3f + (float)_rng.NextDouble() * 0.25f)),
            Velocity = dir * 60f,
            Life = 0.07f,
            MaxLife = 0.07f,
            Color = Color.Transparent,   // pure light carrier
            FadeColor = Color.Transparent,
            Size = 0f,
            Drag = 0f,
            CollideTiles = true,
            LightRadius = 40f + (float)_rng.NextDouble() * 16f,
            LightColor = new Color(255, 170, 80),
            HeroLight = true,
        });
        // The hose sheds tumbling cinders that keep burning where they land.
        if (_rng.Next(3) == 0) EmitCinders(pos + dir * 8f, dir * (reach * 0.9f), 1);
    }

    /// <summary>Jetpack exhaust, coloured by tier: red (I) → orange (II) → yellow (III) →
    /// blue (IV). A short downward tongue of hot core + tinted flame per burning frame,
    /// with real light so night hovers glow.</summary>
    public void EmitJetExhaust(Vector2 pos, Vector2 dir, int tier)
    {
        var flame = tier switch
        {
            1 => new Color(235, 70, 45),     // sputtering red stub
            2 => new Color(255, 150, 50),    // orange burner
            3 => new Color(255, 225, 90),    // hot yellow
            _ => new Color(110, 185, 255),   // tier IV: full-blue jet
        };
        // Fade stays saturated (not sooty brown/near-black) so the whole length of the
        // column reads as flame, not smoke.
        var fade = tier >= 4 ? new Color(50, 90, 210) : new Color(215, 75, 25);
        // An upside-down candle flame rooted to the nozzle: a couple of slow flecks that
        // barely travel — a compact teardrop of fire under the pack, not a jet column.
        for (var i = 0; i < 2; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.18f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var hot = i == 0;
            _list.Add(new Particle
            {
                Position = pos + Jitter(0.5f),
                Velocity = d * (22f + (float)_rng.NextDouble() * 26f),
                Life = 0.08f + (float)_rng.NextDouble() * 0.08f,
                MaxLife = 0.16f,
                Color = hot ? Color.Lerp(flame, Color.White, 0.6f) : flame,
                FadeColor = fade,
                Size = hot ? 0.8f : 1.1f,
                GravityScale = 0f,
                Drag = 4f,
                // Deliberately small glow — the pack lights the dwarf's boots, not the
                // cave; a worn torch out-shines it.
                LightRadius = hot ? 26f : 0f,
                LightColor = flame,
            });
        }
        // The occasional spark spitting out of the teardrop.
        if (_rng.Next(4) == 0)
        {
            _list.Add(new Particle
            {
                Position = pos + Jitter(0.5f),
                Velocity = dir * (90f + (float)_rng.NextDouble() * 60f) + Jitter(20f),
                Life = 0.06f,
                MaxLife = 0.06f,
                Color = Color.Lerp(flame, Color.White, 0.7f),
                FadeColor = fade,
                Size = 0.6f,
                GravityScale = 0f,
                Drag = 1.0f,
            });
        }
    }

    /// <summary>Lasting cinders: glowing embers that arc out, bounce, and keep shedding
    /// warm light for a couple of seconds while they cool — the afterglow that makes fire
    /// linger instead of strobing off the instant the flash dies. Shared by explosions,
    /// the flamethrower, and anything else that burns.</summary>
    public void EmitCinders(Vector2 pos, Vector2 baseVel, int count, float scatter = 60f)
    {
        for (var i = 0; i < count; i++)
        {
            var hot = _rng.Next(3) != 0;
            _list.Add(new Particle
            {
                Position = pos + Jitter(3f),
                Velocity = baseVel * (0.4f + (float)_rng.NextDouble() * 0.8f) + Jitter(scatter),
                Life = 1.3f + (float)_rng.NextDouble() * 1.4f,
                MaxLife = 2.7f,
                Color = hot ? new Color(255, 200, 90) : new Color(255, 140, 50),
                FadeColor = new Color(90, 25, 10),   // cools to a dull coal
                Size = 1.3f,
                GravityScale = 0.55f,
                Drag = 0.9f,
                CollideTiles = true,
                // AddLights scales by remaining life, so the ember's glow dims as it cools.
                LightRadius = 55f,
                LightColor = new Color(255, 160, 70),
            });
        }
    }

    /// <summary>Acid spewer spray: caustic green droplets with a sickly glow. The corrosive
    /// payload is real Acid cells launched by Game1 — this is the visible mist around it.</summary>
    public void EmitAcidJet(Vector2 pos, Vector2 dir, float reach)
    {
        var jetSpeed = reach * 2.6f;
        for (var i = 0; i < 5; i++)
        {
            // Narrow, focused caustic rope — droplets COLLIDE with tiles so it runs down walls
            // rather than spraying through them.
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.14f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 4f,
                Velocity = d * (jetSpeed * (0.8f + (float)_rng.NextDouble() * 0.4f)),
                Life = 0.16f + (float)_rng.NextDouble() * 0.18f,
                MaxLife = 0.4f,
                Color = i < 2 ? new Color(200, 255, 120) : new Color(120, 220, 60),
                FadeColor = new Color(40, 90, 25),
                Size = 1.3f + (float)_rng.NextDouble() * 0.8f,
                GravityScale = 0.4f,
                Drag = 1.1f,
                CollideTiles = true,
                LightRadius = i < 2 ? 18f : 8f,
                LightColor = new Color(150, 240, 80),
            });
        }
        // A caustic vapour wisp or two riding the rope — also collides.
        for (var i = 0; i < 2; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.16f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                Position = pos + d * (6f + (float)_rng.NextDouble() * 6f),
                Velocity = d * (jetSpeed * 0.45f),
                Life = 0.4f + (float)_rng.NextDouble() * 0.4f,
                MaxLife = 0.9f,
                Color = new Color(90, 150, 55),
                FadeColor = new Color(30, 55, 25),
                Size = 1.7f,
                GravityScale = -0.08f,
                Drag = 2.6f,
                CollideTiles = true,
            });
        }
    }

    /// <summary>One lightning arc segment chain from <paramref name="from"/> to
    /// <paramref name="to"/>: a jagged polyline of blinding white-violet nodes (with dimmer
    /// branch flecks) that lives for a couple of frames. Every node sheds light, so the
    /// whole bolt strobes its surroundings; the endpoints get hero flashes for hard shadows.</summary>
    public void EmitLightning(Vector2 from, Vector2 to)
    {
        var span = to - from;
        var len = span.Length();
        if (len < 2f) return;
        var dir = span / len;
        var perp = new Vector2(-dir.Y, dir.X);
        var segs = Math.Clamp((int)(len / 7f), 3, 16);
        var prev = from;
        for (var i = 1; i <= segs; i++)
        {
            var t = i / (float)segs;
            // Mid-bolt wanders hardest; the endpoints stay pinned to source and victim.
            var wander = MathF.Sin(t * MathF.PI) * ((float)_rng.NextDouble() * 2f - 1f) * len * 0.14f;
            var node = from + span * t + perp * wander;
            // Nodes along the segment so the bolt reads as a line, not dots.
            var steps = Math.Max(1, (int)((node - prev).Length() / 2.5f));
            for (var sIdx = 0; sIdx <= steps; sIdx++)
            {
                var p = Vector2.Lerp(prev, node, sIdx / (float)steps);
                _list.Add(new Particle
                {
                    Position = p,
                    Velocity = Vector2.Zero,
                    Life = 0.07f + (float)_rng.NextDouble() * 0.05f,
                    MaxLife = 0.12f,
                    Color = new Color(235, 235, 255),
                    FadeColor = new Color(120, 90, 220),
                    Size = 1.4f,
                    GravityScale = 0f,
                    Drag = 0f,
                    LightRadius = 12f,
                    LightColor = new Color(180, 160, 255),
                });
            }
            // Occasional dead-end branch fork off a node — sells "electricity", not "rope".
            if (_rng.Next(3) == 0)
            {
                var bDir = dir + perp * ((float)_rng.NextDouble() * 2f - 1f);
                bDir.Normalize();
                for (var bi = 1; bi <= 3; bi++)
                {
                    _list.Add(new Particle
                    {
                        Position = node + bDir * bi * 3f + Jitter(1.5f),
                        Velocity = Vector2.Zero,
                        Life = 0.06f,
                        MaxLife = 0.06f,
                        Color = new Color(190, 180, 255),
                        FadeColor = new Color(70, 50, 140),
                        Size = 1f,
                        LightRadius = 6f,
                        LightColor = new Color(160, 140, 255),
                    });
                }
            }
            prev = node;
        }
        // Endpoint flashes: hard-shadow strobe at the muzzle and the struck body.
        foreach (var end in new[] { from, to })
        {
            _list.Add(new Particle
            {
                Position = end,
                Velocity = Vector2.Zero,
                Life = 0.08f,
                MaxLife = 0.08f,
                Color = Color.White,
                FadeColor = new Color(150, 120, 255),
                Size = 3f,
                LightRadius = 46f,
                LightColor = new Color(200, 185, 255),
                HeroLight = true,
            });
        }
    }

    private Vector2 Jitter(float r) =>
        new((float)(_rng.NextDouble() * 2 - 1) * r, (float)(_rng.NextDouble() * 2 - 1) * r);
}
