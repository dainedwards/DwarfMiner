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
    /// <summary>Particle→cell handoff: a <see cref="Material"/> stamped into the cell sim
    /// where this particle comes to REST (requires CollideTiles). 0 = none. Emitters roll
    /// their handoff chance once at spawn, so only the winning particles carry a stamp —
    /// this is how a landed cinder becomes a real Fire cell that can catch the world alight
    /// instead of a light that fades. Cleared after stamping (a resting particle may live
    /// on visually but only hands off once).</summary>
    public byte LandMat;
}

/// <summary>
/// Minimal particle pool: Update integrates, Draw blits coloured rects, AddLights feeds
/// glowing particles into the lighting pass. Emit* helpers spawn the burst patterns the
/// game needs (mining chips, projectile impacts, boulder dust).
/// </summary>
public sealed class Particles
{
    private const float GravityStrength = 200f;

    /// <summary>Effects-as-materials switch (DM_CELLFX=0 reverts): when on, the persistent
    /// half of an effect lives in the CELL SIM — explosion smoke is real Smoke cells rising
    /// out of the crater, landed cinders stamp real Fire cells — and the particle system
    /// keeps only the light-and-energy half (sparks, flashes, glows). When off, everything
    /// stays cosmetic particle quads as before.</summary>
    public static readonly bool CellFx =
        Environment.GetEnvironmentVariable("DM_CELLFX") != "0";

    private readonly List<Particle> _list = new(2048);
    private readonly Random _rng = new();

    public int Count => _list.Count;

    public void Update(float dt, Planet planet, Cells? cells = null)
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
                {
                    p.Life = MathF.Min(p.Life, p.LightRadius > 0f ? 1.4f : 0.15f);
                    // Handoff on rest: the particle's persistent half enters the cell sim
                    // (a landed cinder becomes real fire). Once only.
                    if (p.LandMat != 0 && cells != null)
                    {
                        cells.StampAtWorld(p.Position, (Material)p.LandMat);
                        p.LandMat = 0;
                    }
                }
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
            // Noita palette rule: colours cool in FOUR HARD STEPS, not a smooth ramp — a
            // spark is white, then orange, then red, then a dark ember, each a distinct
            // ink. Smooth lerps read as soft/airbrushed; the stepped ramp is what makes a
            // flame stream look like burning pixels. Ceil keeps fresh grains at full tone.
            t = MathF.Ceiling(t * 4f) * 0.25f;
            var c = Color.Lerp(p.FadeColor, p.Color, t);
            // The Noita rule, enforced at the one choke point every emitter passes through:
            // a particle is a PIXEL — capped at 0.55 world px, one sim cell at Density 8
            // (and ~one texel of the pixel-grid world target, so sparks sit on the same
            // uniform screen grid as the cell sim's grains). Emitter Size values below the
            // cap still vary the fine grain; anything larger (flash hearts, exhaust cores)
            // clamps down, because "big" must come from COUNT and LIGHT, never from
            // scaling up a featureless quad.
            var s = MathF.Min(p.Size, 0.55f);
            // Fast grains SMEAR along their motion (~three frames of travel, capped at 16
            // grains long): overlapping smears are what fuse a hose's grains into the
            // long continuous strands of fire/acid Noita streams read as — a stationary
            // square per grain reads as sprayed dots no matter how dense the emission.
            // Slow drifters (smoke, dust, snow) keep their square pixel.
            var sp2 = p.Velocity.LengthSquared();
            if (sp2 > 3600f)
            {
                var len = MathF.Min(s * 16f, MathF.Sqrt(sp2) * 0.045f);
                if (len > s)
                {
                    r.DrawRect(p.Position, new Vector2(len, s), c,
                        MathF.Atan2(p.Velocity.Y, p.Velocity.X));
                    continue;
                }
            }
            r.DrawRect(p.Position, new Vector2(s, s), c);
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

    /// <summary>Burst of chips when a tile shatters: tinted to the tile, gravity-bound, brief.
    /// Noita grain rule (applies to every emitter here): MANY ~1-px grains, never few fat
    /// quads — a scaled featureless square reads as a giant pixel and breaks the pixel-art
    /// scale. Counts go up where sizes came down so bursts keep their mass.</summary>
    public void EmitChips(Vector2 pos, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        var fade = Color.Multiply(baseColor, 0.4f);
        for (var i = 0; i < 14; i++)
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
                Size = 0.8f + (float)_rng.NextDouble() * 0.5f,
                GravityScale = 1f,
                Drag = 1.0f,
                CollideTiles = true,
            });
        }
        // Ore tiles also throw a few bright glowing flecks that emit their speckle colour as light.
        if (Tiles.IsOre(kind))
        {
            var spec = Tiles.OreSpeckle(kind);
            for (var i = 0; i < 6; i++)
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
                    Size = 0.8f,
                    GravityScale = 0.4f,
                    Drag = 1.2f,
                    // Every other fleck glows — grain counts went up across this file, so
                    // lights ride a SUBSET to keep the lighting pass at its old budget.
                    LightRadius = i % 2 == 0 ? 6f : 0f,
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
        // Orange spark jet along the direction of the drill bit. A few per frame, with slight
        // angular spread so the jet reads as conical not a single beam.
        for (var i = 0; i < 4; i++)
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
                Size = 0.8f,
                GravityScale = 0.1f,
                Drag = 3f,
                LightRadius = i % 2 == 0 ? 5f : 0f,
                LightColor = new Color(255, 180, 80),
            });
        }
        // One tile-coloured chip flung perpendicular to the drill direction so the wall
        // reads as actively shedding fragments.
        for (var i = 0; i < 2; i++)
        {
            var perp = new Vector2(-dirToTile.Y, dirToTile.X) * (_rng.NextDouble() < 0.5 ? 1f : -1f);
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = perp * (15f + (float)_rng.NextDouble() * 20f),
                Life = 0.25f + (float)_rng.NextDouble() * 0.15f,
                MaxLife = 0.40f,
                Color = baseColor,
                FadeColor = fade,
                Size = 0.8f,
                GravityScale = 1f,
                Drag = 1.5f,
                CollideTiles = true,
            });
        }
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
        const float step = 2f;
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
                Size = hotCore ? 0.8f : 1.1f,
                GravityScale = 0f,
                LightRadius = i % 6 == 0 ? 7f : 0f,
                LightColor = new Color(255, 160, 60),
            });
        }
        if (!hitting) return;
        // Molten spatter at the strike point — sprays back toward the emitter side.
        for (var i = 0; i < 4; i++)
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
                Size = 0.8f,
                GravityScale = 0.6f,
                Drag = 1.5f,
                LightRadius = i % 2 == 0 ? 5f : 0f,
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
        for (var i = 0; i < 26; i++)
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
                Size = 0.9f + (float)_rng.NextDouble() * 0.5f,
                GravityScale = 1f,
                Drag = 0.8f,
                CollideTiles = true,
            });
        }
        // Dust shroud
        for (var i = 0; i < 22; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 18f + (float)_rng.NextDouble() * 30f;
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.7f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.3f,
                Color = new Color(150, 140, 130),
                FadeColor = new Color(35, 30, 30),
                Size = 1f + (float)_rng.NextDouble() * 0.6f,
                GravityScale = -0.05f,
                Drag = 0.6f,
            });
        }
    }

    /// <summary>Mining swing that didn't shatter — a tiny chip puff for click feedback.</summary>
    public void EmitMiningTick(Vector2 pos, TileKind kind)
    {
        var baseColor = Tiles.BaseColor(kind);
        for (var i = 0; i < 4; i++)
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
                Size = 0.8f,
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
                for (var i = 0; i < 8; i++)
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
                        Size = 0.8f,
                        GravityScale = 0.3f,
                        Drag = 2f,
                        LightRadius = i % 2 == 0 ? 4f : 0f,
                        LightColor = col,
                    });
                }
                break;
            case ProjectileKind.Cannon:
                EmitExplosion(pos, strength: 14f, sparkCount: 14, smokeCount: 10, sparkColor: new Color(255, 150, 50));
                break;
            case ProjectileKind.Nuke:
                // The big one: magenta blast and ember rain. The tall rising smoke column is
                // the cell sim's (its huge crater stamps a big Smoke budget in CarveCrater);
                // the old particle plume only returns when the cell path is switched off.
                EmitExplosion(pos, strength: 26f, sparkCount: 32, smokeCount: 26, sparkColor: new Color(255, 90, 230));
                EmitEmbers(pos, count: 16);
                if (!CellFx)
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
                for (var i = 0; i < 16; i++)
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
                        Size = 0.9f,
                        GravityScale = 0.4f,
                        Drag = 1.0f,
                        LightRadius = i % 2 == 0 ? 6f : 0f,
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
                for (var i = 0; i < (kind == ProjectileKind.LaserCannon ? 18 : 9); i++)
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
                        Size = 0.8f,
                        GravityScale = 0.1f,
                        Drag = 2.5f,
                        LightRadius = i % 2 == 0 ? 6f : 0f,
                        LightColor = col,
                    });
                }
                break;
            case ProjectileKind.Harpoon:
                // Pierce trail end — sharp metallic flecks, light shockwave.
                for (var i = 0; i < 14; i++)
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
                        Size = 0.9f,
                        GravityScale = 0.3f,
                        Drag = 1.5f,
                        LightRadius = i % 2 == 0 ? 7f : 0f,
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
        for (var i = 0; i < count * 2; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = 20f + (float)_rng.NextDouble() * 25f;
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 1.2f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.8f,
                Color = new Color(255, 170, 80),
                FadeColor = new Color(120, 30, 10),
                Size = 0.8f,
                GravityScale = -0.2f,
                Drag = 0.8f,
                LightRadius = i % 2 == 0 ? 8f : 0f,
                LightColor = new Color(255, 140, 60),
            });
        }
    }

    /// <summary>Pale-blue frost crystals that shoot outward then fade. Used by sapphire shells
    /// to sell the "cold burst" — visually cold, no heat-trail.</summary>
    private void EmitFrostShards(Vector2 pos, int count)
    {
        for (var i = 0; i < count * 2; i++)
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
                Size = 0.9f,
                GravityScale = 0.3f,
                Drag = 1.5f,
                LightRadius = i % 3 == 0 ? 5f : 0f,
                LightColor = new Color(140, 200, 255),
                CollideTiles = true,
            });
        }
    }

    /// <summary>Generic dust burst — used for boulder slams.</summary>
    public void EmitDust(Vector2 pos, float strength = 10f)
    {
        for (var i = 0; i < 30; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = strength * 1.5f + (float)_rng.NextDouble() * strength * 1.5f;
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = 0.7f + (float)_rng.NextDouble() * 0.5f,
                MaxLife = 1.2f,
                Color = new Color(120, 105, 95),
                FadeColor = new Color(30, 25, 28),
                Size = 1f + (float)_rng.NextDouble() * 0.6f,
                GravityScale = -0.1f,
                Drag = 0.7f,
            });
        }
    }

    /// <summary>A single raindrop as a STREAMLET — the hose grammar pointed down: a bright
    /// leading droplet with a couple of dimmer tail grains strung behind it along the fall,
    /// the tail running fractionally slower so the string visibly stretches as it drops.
    /// A shower then reads as streaming granular water (the flamethrower/acid-spewer look)
    /// instead of lone specks. <paramref name="down"/> is the toward-surface direction; the
    /// drop is tinted by the rain kind. Grains collide with the terrain — they die in a
    /// brief splash on the surface instead of streaking on through the ground — so the
    /// life is generous; impact ends it early.</summary>
    public void EmitRain(Vector2 pos, Vector2 down, Color color)
    {
        var jitter = new Vector2(-down.Y, down.X) * (((float)_rng.NextDouble() - 0.5f) * 12f);
        var speed = 140f + (float)_rng.NextDouble() * 90f;
        for (var i = 0; i < 3; i++)
        {
            var head = i == 0;
            _list.Add(new Particle
            {
                Position = pos - down * (i * 2.2f),
                Velocity = down * (speed * (head ? 1f : 0.94f - i * 0.04f)) + jitter,
                Life = 1.0f + (float)_rng.NextDouble() * 0.6f,
                MaxLife = 1.6f,
                Color = head ? Color.Lerp(color, Color.White, 0.35f) : color,
                FadeColor = color * 0.4f,
                Size = head ? 0.55f : 0.45f,
                GravityScale = 1.1f,
                Drag = 0.02f,
                CollideTiles = true,
            });
        }
    }

    /// <summary>A single air bubble underwater: a pale speck that wobbles upward (negative
    /// gravity scale rises along the local up) and pops after a beat. Emitted by the drowning
    /// dwarf's breath, boiling quench fronts, and fire gouts dying in a pool.</summary>
    public void EmitBubble(Vector2 pos, Vector2 up)
    {
        var side = new Vector2(-up.Y, up.X);
        _list.Add(new Particle
        {
            Position = pos + side * (((float)_rng.NextDouble() - 0.5f) * 4f),
            Velocity = up * (14f + (float)_rng.NextDouble() * 16f)
                     + side * (((float)_rng.NextDouble() - 0.5f) * 14f),
            Life = 0.5f + (float)_rng.NextDouble() * 0.7f,
            MaxLife = 1.2f,
            Color = new Color(190, 220, 245),
            FadeColor = new Color(120, 160, 200),
            Size = 0.8f + (float)_rng.NextDouble() * 0.4f,
            GravityScale = -0.18f,
            Drag = 0.9f,
        });
    }

    /// <summary>A single falling leaf: a fleck in the canopy's own colour that flutters down
    /// slowly, drifting sideways — trees shed the odd leaf so a forest reads alive.</summary>
    public void EmitLeaf(Vector2 pos, Vector2 down, Color color)
    {
        var side = new Vector2(-down.Y, down.X);
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = down * (8f + (float)_rng.NextDouble() * 10f)
                     + side * (((float)_rng.NextDouble() - 0.5f) * 26f),
            Life = 2.2f + (float)_rng.NextDouble() * 1.8f,
            MaxLife = 4f,
            Color = color,
            FadeColor = Color.Multiply(color, 0.5f),
            Size = 1f + (float)_rng.NextDouble() * 0.4f,
            GravityScale = 0.05f,
            Drag = 0.75f,
            CollideTiles = true,
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
            Size = 1f + (float)_rng.NextDouble() * 0.5f,
            GravityScale = 0.12f,
            Drag = 0.6f,
        });
    }

    private void EmitExplosion(Vector2 pos, float strength, int sparkCount, int smokeCount, Color sparkColor)
    {
        // Flash core — the "bang" frame is the HERO LIGHT (hard ray-cast shadows off the
        // crater rim), not a drawn shape: the old visible quad was strength*0.55 wide — a
        // featureless 14-px white square on a nuke, the single chunkiest pixel in the game.
        // Now the drawn core is a small hot heart and the flash itself is a one-frame
        // starburst of white-hot 1-px sparks.
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.14f,
            MaxLife = 0.14f,
            Color = Color.White,
            FadeColor = sparkColor,
            Size = MathF.Min(3.5f, strength * 0.13f),
            GravityScale = 0f,
            Drag = 0f,
            LightRadius = strength * 4.5f,
            LightColor = Color.Lerp(sparkColor, Color.White, 0.5f),
            HeroLight = true,
        });
        var flashCount = (int)strength;
        for (var i = 0; i < flashCount; i++)
        {
            var ang = i / (float)flashCount * MathHelper.TwoPi + (float)(_rng.NextDouble() * 0.3);
            _list.Add(new Particle
            {
                Position = pos,
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (strength * 8f + (float)_rng.NextDouble() * strength * 10f),
                Life = 0.06f + (float)_rng.NextDouble() * 0.06f,
                MaxLife = 0.12f,
                Color = Color.White,
                FadeColor = Color.Lerp(sparkColor, Color.White, 0.4f),
                Size = 1f,
                GravityScale = 0f,
                Drag = 5f,
            });
        }
        // Lasting cinders: after the flash dies, a handful of thrown coals keep the crater
        // and its surroundings lit for a couple of seconds while they cool.
        EmitCinders(pos, Vector2.Zero, Math.Clamp((int)(strength / 3f), 4, 10), scatter: strength * 4f);
        // Fireball — a cluster of fat white-yellow→orange→red blobs boiling out of the
        // centre. Tinted slightly toward the shell's colour so elemental blasts keep their
        // identity, but the body of every explosion reads as fire.
        // Fireball as GRAINS: the old few fat 2.5-5px blobs are now a boiling cloud of
        // ~1px flecks (3x the count), so the body of the blast reads as granular burning
        // gas — Noita's pixel-fire — instead of soft puffballs.
        var fireCount = (int)(strength * 1.8f);
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
                Size = 0.9f + (float)_rng.NextDouble() * 0.7f,
                GravityScale = -0.1f,
                Drag = 2.5f,
                LightRadius = i % 3 == 0 ? 12f : 0f,
                LightColor = Color.Lerp(hot, sparkColor, 0.3f),
            });
        }
        // Shockwave ring — evenly spaced fast radial particles with hard drag, so a crisp
        // circle expands a short distance and dies. No gravity: the wavefront stays round.
        // White-hot cooling to fire orange.
        var ringCount = (int)(strength * 2.4f);
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
                Size = 1f,
                GravityScale = 0f,
                Drag = 6f,
            });
        }
        // Debris chunks — heavy scorched-rock lumps lobbed out of the blast; full gravity,
        // bounce on terrain, long life so they visibly rain back down around the crater.
        var debrisCount = (int)(strength * 1.2f);
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
                Size = 1f + (float)_rng.NextDouble() * 0.7f,
                GravityScale = 1.2f,
                Drag = 0.3f,
                CollideTiles = true,
            });
        }
        for (var i = 0; i < sparkCount * 2; i++)
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
                Size = 0.8f + (float)_rng.NextDouble() * 0.5f,
                GravityScale = 0.6f,
                Drag = 1.0f,
                LightRadius = i % 4 == 0 ? 8f : 0f,
                LightColor = sparkColor,
                CollideTiles = true,
            });
        }
        // The lingering smoke PLUME is the cell sim's job now (CarveCrater stamps real Smoke
        // cells that rise, pool under ceilings, and drift out of cave mouths). The particle
        // side keeps only a brief soot puff for the first-instant read — full fat-blob smoke
        // only when the cell path is switched off.
        var puffCount = CellFx ? Math.Min(smokeCount, 8) : smokeCount * 2;
        var puffLife = CellFx ? 0.45f : 1.0f;
        for (var i = 0; i < puffCount; i++)
        {
            var ang = (float)(_rng.NextDouble() * MathHelper.TwoPi);
            var spd = strength * 1.5f + (float)_rng.NextDouble() * strength * 1.5f;
            // Smoke starts ember-lit (warm orange-brown, fresh out of the fireball) and
            // cools to near-black as it rises — fire turning into soot.
            _list.Add(new Particle
            {
                Position = pos + Jitter(2f),
                Velocity = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spd,
                Life = (0.8f + (float)_rng.NextDouble() * 0.8f) * puffLife,
                MaxLife = 1.6f * puffLife,
                Color = new Color(185, 115, 60),
                FadeColor = new Color(22, 18, 20),
                Size = 1f + (float)_rng.NextDouble() * 0.5f,
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
        for (var i = 0; i < 4; i++)
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
                Size = 0.9f + (float)_rng.NextDouble() * 0.5f,
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
            Size = 1.6f,
            GravityScale = 0f,
            Drag = 0f,
            // One-frame hero flash: each shot strobes the cave with hard shadows — the
            // propagated grid alone made muzzle flashes read as a faint blush. The LIGHT
            // is the flash; the drawn quad is just a small hot heart at the muzzle.
            LightRadius = 44f,
            LightColor = color,
            HeroLight = true,
        });
        for (var i = 0; i < 8; i++)
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
                Size = 0.8f,
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
        // Bright light at the nozzle sells the flare in the lighting pass; the drawn core
        // is small — the plume body below carries the visible mass as grains.
        _list.Add(new Particle
        {
            Position = pos,
            Velocity = Vector2.Zero,
            Life = 0.05f,
            MaxLife = 0.05f,
            Color = new Color(255, 240, 200),
            FadeColor = new Color(255, 150, 60),
            Size = 2.5f,
            LightRadius = 60f,
            LightColor = new Color(255, 170, 90),
        });
        for (var i = 0; i < 26; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.9f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var hot = i < 13;
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 4f + Jitter(1.2f),
                Velocity = d * (120f + (float)_rng.NextDouble() * 160f),
                Life = 0.25f + (float)_rng.NextDouble() * 0.4f,
                MaxLife = 0.65f,
                Color = hot ? new Color(255, 230, 150) : new Color(200, 90, 50),
                FadeColor = hot ? new Color(220, 100, 50) : new Color(70, 70, 75),
                Size = hot ? 1f : 1.4f,
                GravityScale = -0.05f,
                Drag = 2.2f,
                LightRadius = hot && i % 3 == 0 ? 24f : 0f,
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
                // Exhaust: grey grains shed at the tail every frame, drifting apart.
                for (var i = 0; i < 3; i++)
                    _list.Add(new Particle
                    {
                        Position = p.Position + back * (4f + (float)_rng.NextDouble() * 4f) + Jitter(1.2f),
                        Velocity = back * 20f + Jitter(10f),
                        Life = 0.35f + (float)_rng.NextDouble() * 0.25f,
                        MaxLife = 0.6f,
                        Color = new Color(150, 140, 135),
                        FadeColor = new Color(40, 35, 35),
                        Size = 0.9f + (float)_rng.NextDouble() * 0.4f,
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
        // Flow speed EXACTLY matches Game1's payload launch (reach*1.7): any gap between
        // the two puts the landing fire beyond (or short of) the visible tongue tip — the
        // "mismatch" read. Still ~35% slower than the original 2.6 spray; grain lives are
        // sized so travel ≈ reach at every hold length.
        var jetSpeed = reach * 1.7f;
        // Many TINY grains rather than a few fat blobs — the stream reads as granular burning
        // fluid (Noita's pixel-fire) instead of soft puffballs. Grain colours pick from a
        // FOUR-TONE fire ramp (white-yellow → gold → orange → red-orange) so the stream body
        // is speckled with distinct inks like Noita's, rather than two flat colours; the
        // stepped life-fade in Draw then cools each grain through hard palette jumps.
        // Lights ride the hot minority only: 22 grains all shedding 30-60px light would
        // swamp the lighting pass.
        for (var i = 0; i < 22; i++)
        {
            // Tighter cone + narrower speed band than the old fan: grains stay bunched
            // along the stream axis, so their motion-smears overlap into one rope.
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.068f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            var hot = i < 7;
            var tone = hot ? _rng.Next(2) : _rng.Next(4);
            var vel = d * (jetSpeed * (0.85f + (float)_rng.NextDouble() * 0.3f));
            // De-pulse: every grain starts with a random fraction of one puff interval
            // already travelled, so consecutive puffs interleave into one continuous flow
            // instead of reading as discrete waves marching down the stream.
            var lead = (float)_rng.NextDouble() * 0.06f;
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 5f + vel * lead,
                Velocity = vel,
                Life = 0.5f + (float)_rng.NextDouble() * 0.35f - lead,
                MaxLife = 0.85f,
                Color = tone switch
                {
                    0 => new Color(255, 250, 200),
                    1 => new Color(255, 220, 110),
                    2 => new Color(255, 170, 60),
                    _ => new Color(255, 120, 35),
                },
                FadeColor = new Color(120, 35, 15),
                Size = hot ? 0.7f : 0.8f + (float)_rng.NextDouble() * 0.4f,
                GravityScale = HoseArcGravity,   // same arc as the fuel cells
                Drag = 1.2f,
                CollideTiles = true,
                LightRadius = hot ? 60f : i % 3 == 0 ? 30f : 0f,
                LightColor = new Color(255, 170, 70),
                // Every flame grain that lands IS fire: it stamps a real Fire cell where
                // it rests (StampAtWorld only fills open cells, the sim's fire budget
                // throttles spread, and starved fire gutters ~0.8s — so the tongue's
                // landing zone burns for real without becoming an arson machine).
                LandMat = CellFx ? (byte)Material.Fire : (byte)0,
            });
        }
        // Fire is BUOYANT: tongues lick UP off the stream as it travels — the Noita curl.
        // A few short-lived grains seeded along the tongue with negative gravity slow,
        // detach, and flick upward before dying to a dark ember (mid-air seeding is right
        // here, unlike the acid wisps: rising flame, not falling rain).
        for (var i = 0; i < 5; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.3f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                // Seed only the near two-fifths of the tongue with a strong stall (high
                // drag, short life): licks flick up and die WITHIN the stream's length —
                // they were riding above the drooping tongue and past its tip, reading as
                // stray yellow strands out-ranging the fire.
                Position = pos + d * (6f + (float)_rng.NextDouble() * (reach * 0.4f)) + Jitter(1.2f),
                Velocity = d * (jetSpeed * 0.25f),
                Life = 0.18f + (float)_rng.NextDouble() * 0.17f,
                MaxLife = 0.35f,
                Color = _rng.Next(2) == 0 ? new Color(255, 220, 110) : new Color(255, 160, 55),
                FadeColor = new Color(120, 35, 15),
                Size = 0.5f,
                GravityScale = -0.5f,
                Drag = 3.2f,
                CollideTiles = true,
                LandMat = CellFx ? (byte)Material.Fire : (byte)0,
            });
        }
        // Sooty flecks shed along the tongue — they inherit the arc, then buoy upward as they
        // cool (weak net gravity), so spent flame rolls off the stream like Noita's smoke.
        for (var i = 0; i < 6; i++)
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
                Size = 0.9f + (float)_rng.NextDouble() * 0.4f,
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
        // The hose sheds tumbling cinders that keep burning where they land. Launched at
        // well under stream speed with the ember's LOW gravity scale — at reach*0.9 they
        // flew flatter than the drooping tongue and sailed past its tip as stray bright
        // strands out-ranging the visible fire.
        if (_rng.Next(3) == 0) EmitCinders(pos + dir * 8f, dir * (reach * 0.4f), 1);
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
                Size = 0.9f,
                GravityScale = 0.55f,
                Drag = 0.9f,
                CollideTiles = true,
                // AddLights scales by remaining life, so the ember's glow dims as it cools.
                LightRadius = 55f,
                LightColor = new Color(255, 160, 70),
                // A quarter of the coals are still burning when they land: they stamp a
                // real Fire cell where they rest, so a blast near oil, gas, or timber can
                // genuinely start a fire. The sim's own spread throttle and gutter-out on
                // bare rock keep this from being an arson machine.
                LandMat = CellFx && _rng.Next(4) == 0 ? (byte)Material.Fire : (byte)0,
            });
        }
    }

    /// <summary>Acid spewer spray: caustic green droplets with a sickly glow. The corrosive
    /// payload is real Acid cells launched by Game1 — this is the visible mist around it.</summary>
    public void EmitAcidJet(Vector2 pos, Vector2 dir, float reach)
    {
        // Speed matches Game1's payload launch exactly — see EmitFlameJet.
        var jetSpeed = reach * 1.7f;
        // Many TINY droplets — a granular liquid rope, not fat green puffs. Lights on the
        // bright leading droplets only (see EmitFlameJet).
        for (var i = 0; i < 22; i++)
        {
            // Caustic rope — droplets COLLIDE with tiles and fall on the SAME arc as the acid
            // cells, so the visible spray lands exactly where the corrosive payload pools.
            // Tight cone: the motion-smears in Draw fuse bunched grains into one rope.
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.064f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            // Three liquid inks like Noita's acid: neon highlight, bright body, and the
            // occasional DEEP green — a liquid rope needs dark grains for depth, where
            // fire wants none.
            var tone = i < 7 ? 0 : _rng.Next(4);
            var vel = d * (jetSpeed * (0.85f + (float)_rng.NextDouble() * 0.3f));
            // De-pulse — see EmitFlameJet: random emission-time head start interleaves puffs.
            var lead = (float)_rng.NextDouble() * 0.06f;
            _list.Add(new Particle
            {
                Position = pos + d * (float)_rng.NextDouble() * 5f + vel * lead,
                Velocity = vel,
                Life = 0.5f + (float)_rng.NextDouble() * 0.32f - lead,
                MaxLife = 0.82f,
                Color = tone switch
                {
                    0 => new Color(215, 255, 100),
                    1 or 2 => new Color(130, 225, 55),
                    _ => new Color(70, 150, 35),
                },
                FadeColor = new Color(40, 90, 25),
                Size = i < 7 ? 0.7f : 0.8f + (float)_rng.NextDouble() * 0.4f,
                GravityScale = HoseArcGravity,   // same arc as the acid cells
                Drag = 1.0f,
                CollideTiles = true,
                LightRadius = i < 7 ? 16f : i % 3 == 0 ? 7f : 0f,
                LightColor = new Color(150, 240, 80),
            });
        }
        // A few caustic vapour wisps riding the rope FROM THE MUZZLE (never seeded
        // mid-air along the stream — that materialised droplets in space that fell like rain).
        for (var i = 0; i < 3; i++)
        {
            var spread = (float)(_rng.NextDouble() - 0.5) * 0.22f;
            var c = MathF.Cos(spread);
            var s = MathF.Sin(spread);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            _list.Add(new Particle
            {
                Position = pos + d * (4f + (float)_rng.NextDouble() * 6f),
                Velocity = d * (jetSpeed * (0.55f + (float)_rng.NextDouble() * 0.25f)),
                Life = 0.35f + (float)_rng.NextDouble() * 0.3f,
                MaxLife = 0.65f,
                Color = new Color(90, 150, 55),
                FadeColor = new Color(30, 55, 25),
                Size = 0.9f + (float)_rng.NextDouble() * 0.4f,
                GravityScale = HoseArcGravity * 0.8f,   // rides (nearly) the rope's own arc
                Drag = 1.6f,
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
            // Nodes along the segment so the bolt reads as a line, not dots — 1-px nodes at
            // a tighter pitch keep the line solid; light on alternate nodes only.
            var steps = Math.Max(1, (int)((node - prev).Length() / 1.8f));
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
                    Size = 1.1f,
                    GravityScale = 0f,
                    Drag = 0f,
                    LightRadius = sIdx % 2 == 0 ? 12f : 0f,
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
                        Size = 0.8f,
                        LightRadius = bi == 1 ? 6f : 0f,
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
