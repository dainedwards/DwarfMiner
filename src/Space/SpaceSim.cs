using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Space;

/// <summary>One world on the system map: a PlanetDef placed on a slow circular orbit around
/// the sun (which sits at the origin of space coordinates).</summary>
public sealed class SpacePlanet
{
    public readonly PlanetDef Def;
    public readonly float OrbitRadius;
    public readonly float BodyRadius;
    public readonly float AngularVel;
    public float Angle;

    public SpacePlanet(PlanetDef def, float orbitRadius, float bodyRadius, float angle, float angularVel)
    {
        Def = def; OrbitRadius = orbitRadius; BodyRadius = bodyRadius;
        Angle = angle; AngularVel = angularVel;
    }

    public Vector2 Pos => new(MathF.Cos(Angle) * OrbitRadius, MathF.Sin(Angle) * OrbitRadius);
}

/// <summary>
/// The flyable solar system between planet runs. Owns the orbiting planets and the player's
/// rocket (rotate/thrust/brake with drag and a speed cap — arcade handling, no gravity wells).
/// Space coordinates are their own plane (the sun at the origin), unrelated to any planet's
/// tile space. Pure logic — no input reads, no rendering — so SimTest can tick it headless.
/// </summary>
public sealed class SpaceSim
{
    public const float SunRadius = 240f;
    /// <summary>How far off a planet's surface the landing prompt reaches.</summary>
    public const float LandRange = 130f;

    public readonly List<SpacePlanet> Planets = new();
    public Vector2 ShipPos;
    public Vector2 ShipVel;
    /// <summary>Radians; 0 points along +X. The rocket only thrusts along this nose vector.</summary>
    public float ShipHeading = -MathF.PI / 2f;
    /// <summary>Set by Update each tick — the renderer keys the exhaust flame off it.</summary>
    public bool Thrusting;

    private const float Accel = 460f;
    private const float Brake = 380f;
    private const float TurnRate = 3.4f;
    private const float MaxSpeed = 640f;
    private const float Drag = 0.20f;    // per-second exponential decay — coasting bleeds off slowly

    public SpaceSim()
    {
        // Orbit spacing wide enough that each gap is a short flight (~2s at cruise), body radii
        // growing outward so the finale world looms. Initial angles fan the planets around the
        // sun rather than lining them up.
        for (var i = 0; i < PlanetDefs.All.Length; i++)
        {
            var def = PlanetDefs.All[i];
            Planets.Add(new SpacePlanet(def,
                orbitRadius: 1500f + i * 1050f,
                bodyRadius: 120f + i * 18f,
                angle: i * 2.23f + 0.6f,
                angularVel: 0.012f / MathF.Sqrt(1f + i * 0.7f)));
        }
        PlaceShipAt(0);
    }

    public Vector2 ShipDir => new(MathF.Cos(ShipHeading), MathF.Sin(ShipHeading));

    /// <summary>Park the ship just off a planet's outward (sun-away) side, nose pointing out —
    /// where you appear after launching off that world, dying on it, or booting the game.</summary>
    public void PlaceShipAt(int planetIndex, float exitSpeed = 0f)
    {
        var p = Planets[Math.Clamp(planetIndex, 0, Planets.Count - 1)];
        var outward = p.Pos.LengthSquared() > 1f ? Vector2.Normalize(p.Pos) : new Vector2(0f, -1f);
        ShipPos = p.Pos + outward * (p.BodyRadius + 80f);
        ShipVel = outward * exitSpeed;
        ShipHeading = MathF.Atan2(outward.Y, outward.X);
    }

    /// <summary>One tick: turn ∈ [-1, 1], thrust along the nose, brake kills velocity directly
    /// (retro jets), then drag, the speed cap, and solid-body pushes off the sun and planets.</summary>
    public void Update(float dt, float turn, bool thrust, bool brake)
    {
        foreach (var p in Planets) p.Angle += p.AngularVel * dt;

        ShipHeading += turn * TurnRate * dt;
        Thrusting = thrust;
        if (thrust) ShipVel += ShipDir * (Accel * dt);
        if (brake)
        {
            var speed = ShipVel.Length();
            if (speed > 1f) ShipVel -= ShipVel / speed * MathF.Min(Brake * dt, speed);
            else ShipVel = Vector2.Zero;
        }

        ShipVel *= MathF.Exp(-Drag * dt);
        var spd = ShipVel.Length();
        if (spd > MaxSpeed) ShipVel *= MaxSpeed / spd;
        ShipPos += ShipVel * dt;

        // The sun is not landable: the corona shoves the ship back out and reflects any
        // inbound velocity, so flying at it reads as a bounce rather than a clip-through.
        var fromSun = ShipPos.Length();
        var minSun = SunRadius + 70f;
        if (fromSun < minSun)
        {
            var outward = fromSun > 1f ? ShipPos / fromSun : new Vector2(0f, -1f);
            ShipPos = outward * minSun;
            var radial = Vector2.Dot(ShipVel, outward);
            if (radial < 0f) ShipVel -= outward * (radial * 1.6f);
        }

        // Planets are solid: skim along the disc instead of flying through it, so hovering
        // at the landing prompt doesn't drift you inside the artwork.
        foreach (var p in Planets)
        {
            var d = ShipPos - p.Pos;
            var dist = d.Length();
            var min = p.BodyRadius + 16f;
            if (dist < min && dist > 0.5f)
            {
                var outward = d / dist;
                ShipPos = p.Pos + outward * min;
                var radial = Vector2.Dot(ShipVel, outward);
                if (radial < 0f) ShipVel -= outward * radial;
            }
        }
    }

    /// <summary>The planet the ship could land on right now — within LandRange of its
    /// surface — or null. At most one qualifies since orbits never bring worlds that close.</summary>
    public SpacePlanet? LandingCandidate()
    {
        foreach (var p in Planets)
            if ((ShipPos - p.Pos).Length() - p.BodyRadius < LandRange)
                return p;
        return null;
    }
}
