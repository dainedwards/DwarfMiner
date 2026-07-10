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

/// <summary>A drifting rock the mothership must dodge or shoot. Big ones split when killed.</summary>
public sealed class Asteroid
{
    public Vector2 Pos;
    public Vector2 Vel;
    public float Radius;
    public float Hp;
    public float Rot;
    public float Spin;
}

/// <summary>One autocannon bolt from the mothership's gun.</summary>
public sealed class ShipShot
{
    public Vector2 Pos;
    public Vector2 Vel;
    public float Life;
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
    /// <summary>Surface distance at which flying at a planet becomes an atmosphere entry.
    /// Generous on purpose: entry should trigger the moment the ship visibly meets the
    /// atmosphere halo, not after grinding into the disc itself. Parking spots
    /// (<see cref="PlaceShipAt"/>) must sit safely outside this shell.</summary>
    public const float EntryRange = 90f;
    /// <summary>Set by Game1 each frame from the shard count — while locked, the Rift is the
    /// one world whose disc stays solid (its storms repel you); every other planet can be
    /// flown straight into, No Man's Sky style.</summary>
    public bool RiftLocked = true;

    public readonly List<SpacePlanet> Planets = new();
    public Vector2 ShipPos;
    public Vector2 ShipVel;
    /// <summary>Radians; 0 points along +X. The rocket only thrusts along this nose vector.</summary>
    public float ShipHeading = -MathF.PI / 2f;
    /// <summary>Set by Update each tick — the renderer keys the exhaust flame off it.</summary>
    public bool Thrusting;

    // ── Hazards & armament (mothership era) ─────────────────────────────────
    /// <summary>Collision radius of the ring station (the 48px sprite draws at 1.5×).</summary>
    public const float ShipRadius = 34f;
    public readonly List<Asteroid> Asteroids = new();
    public readonly List<ShipShot> Shots = new();
    public int Hull = 5;
    /// <summary>Seconds of post-hit invulnerability left — also the renderer's hit flash.</summary>
    public float HitTimer;
    /// <summary>Set when a collision drops the hull to zero; Game1 consumes it to run the
    /// emergency-dock sequence (reposition at the nearest world, restore hull).</summary>
    public bool HullBreached;
    /// <summary>Where the last asteroid died this tick (shot or rammed) — Game1 consumes it
    /// for the positional shatter sound.</summary>
    public Vector2? LastRockShattered;
    /// <summary>Ship tiers, fed from MetaSave upgrades by Game1: gun 2 = double rate,
    /// gun 3 = twin spread; engines 2/3 = faster + leaner burn; hull 2 = 7 pips. The
    /// deflector shield eats one impact then recharges.</summary>
    public int GunTier = 1;
    public int EngineTier = 1;
    public int HullTier = 1;
    public bool HasShield;
    /// <summary>2 = Aegis Capacitor: the shield recharges twice as fast.</summary>
    public int ShieldTier = 1;
    /// <summary>Seconds until the shield can eat another impact; ready at ≤ 0.</summary>
    public float ShieldCooldown;
    public bool ShieldReady => HasShield && ShieldCooldown <= 0f;
    public float ShieldRechargeTime => ShieldTier >= 2 ? 4f : 8f;
    public int HullMax => HullTier >= 3 ? 9 : HullTier >= 2 ? 7 : 5;

    /// <summary>Fuel plumbing: the tank lives in MetaSave, so the sim just reports demand.
    /// Game1 sets <see cref="HasFuel"/> each frame and drains whole units out of
    /// <see cref="FuelUsed"/>. Dry tank = reserve thrusters at 35% power (you can always
    /// limp somewhere to mine more — no soft-lock, just a long slow ride).</summary>
    public bool HasFuel = true;
    public float FuelUsed;
    /// <summary>Voidstone Reactor: thrust demands no fuel at all.</summary>
    public bool FreeThrust;

    private float _gunCooldown;
    /// <summary>Target live-asteroid population; maintained by Update inside a spawn donut
    /// around the ship. Zero disables spawning (tests build their own fields).</summary>
    public int AsteroidTarget = 22;

    private const float Accel = 460f;
    private const float Brake = 380f;
    private const float TurnRate = 3.4f;
    private const float MaxSpeed = 640f;
    private const float Drag = 0.20f;    // per-second exponential decay — coasting bleeds off slowly
    private const float ShotSpeed = 1050f;
    private const float ShotDamage = 30f;

    public SpaceSim()
    {
        // Orbit spacing wide enough that each gap is a short flight (~2s at cruise), body radii
        // growing outward so the finale world looms. Initial angles fan the planets around the
        // sun rather than lining them up.
        for (var i = 0; i < PlanetDefs.All.Length; i++)
        {
            var def = PlanetDefs.All[i];
            // The Rift sits far beyond the ordinary orbits — warp territory, not a cruise.
            // The debug rig does the opposite: it parks on a tight inner orbit, hugging the
            // sun where a QA flight can reach it in seconds.
            var rift = def.Id == "rift";
            var debug = def.Id == "debug";
            // Body radius tracks the def's SizeScale so the system view honestly previews
            // how big each world is - the far giants loom, the near dwarfs look like moons.
            Planets.Add(new SpacePlanet(def,
                orbitRadius: rift ? 9800f : debug ? 950f : 1500f + i * 1050f,
                bodyRadius: rift ? 210f : 130f * def.SizeScale,
                angle: i * 2.23f + 0.6f,
                angularVel: rift ? 0.004f : debug ? 0.014f : 0.012f / MathF.Sqrt(1f + i * 0.7f)));
        }
        PlaceShipAt(0);
    }

    public Vector2 ShipDir => new(MathF.Cos(ShipHeading), MathF.Sin(ShipHeading));

    /// <summary>Park the ship just off a planet's outward (sun-away) side, nose pointing out —
    /// where you appear after launching off that world, dying on it, or booting the game.
    /// The distance must clear <see cref="EntryRange"/> with margin, or parking would
    /// immediately re-enter the atmosphere.</summary>
    public void PlaceShipAt(int planetIndex, float exitSpeed = 0f)
    {
        var p = Planets[Math.Clamp(planetIndex, 0, Planets.Count - 1)];
        var outward = p.Pos.LengthSquared() > 1f ? Vector2.Normalize(p.Pos) : new Vector2(0f, -1f);
        ShipPos = p.Pos + outward * (p.BodyRadius + EntryRange + 80f);
        ShipVel = outward * exitSpeed;
        ShipHeading = MathF.Atan2(outward.Y, outward.X);
    }

    /// <summary>One tick: turn ∈ [-1, 1], thrust along the nose, brake kills velocity directly
    /// (retro jets), then drag, the speed cap, and solid-body pushes off the sun and planets.</summary>
    public void Update(float dt, float turn, bool thrust, bool brake)
    {
        foreach (var p in Planets) p.Angle += p.AngularVel * dt;

        var engineMul = (EngineTier >= 3 ? 1.75f : EngineTier >= 2 ? 1.4f : 1f)
                      * (HasFuel || FreeThrust ? 1f : 0.35f);
        ShipHeading += turn * TurnRate * dt;
        Thrusting = thrust;
        if (thrust)
        {
            ShipVel += ShipDir * (Accel * engineMul * dt);
            // Higher ion tiers sip rather than gulp — range is the real payoff. The
            // voidstone reactor skips the tank entirely.
            if (HasFuel && !FreeThrust)
                FuelUsed += (EngineTier >= 3 ? 0.2f : EngineTier >= 2 ? 0.28f : 0.4f) * dt;
        }
        if (brake)
        {
            var speed = ShipVel.Length();
            if (speed > 1f) ShipVel -= ShipVel / speed * MathF.Min(Brake * dt, speed);
            else ShipVel = Vector2.Zero;
        }

        ShipVel *= MathF.Exp(-Drag * dt);
        var spd = ShipVel.Length();
        var maxSpd = MaxSpeed * engineMul;
        if (spd > maxSpd) ShipVel *= maxSpd / spd;
        ShipPos += ShipVel * dt;

        if (_gunCooldown > 0f) _gunCooldown -= dt;
        if (HitTimer > 0f) HitTimer -= dt;
        if (ShieldCooldown > 0f) ShieldCooldown -= dt;
        UpdateShots(dt);
        UpdateAsteroids(dt);

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
            // The corona is a hazard, not just a wall: each contact scorches the hull on
            // the asteroid-strike cadence (a charged shield eats one lick, then recharges).
            // Press against the sun long enough and the ship burns down to a breach — the
            // emergency dock is the way out.
            if (HitTimer <= 0f)
            {
                if (ShieldReady) { ShieldCooldown = ShieldRechargeTime; HitTimer = 0.4f; }
                else
                {
                    Hull--;
                    HitTimer = 1.0f;
                    if (Hull <= 0) HullBreached = true;
                }
            }
        }

        // Planets are open sky now — flying into one is how you enter its atmosphere. The
        // exception is the shard-locked Rift, whose storm wall shoves the ship back out.
        if (RiftLocked)
        {
            foreach (var p in Planets)
            {
                if (p.Def.Id != "rift") continue;
                var d = ShipPos - p.Pos;
                var dist = d.Length();
                var min = p.BodyRadius + 16f;
                if (dist < min && dist > 0.5f)
                {
                    var outward = d / dist;
                    ShipPos = p.Pos + outward * min;
                    var radial = Vector2.Dot(ShipVel, outward);
                    if (radial < 0f) ShipVel -= outward * (radial * 1.4f);
                }
            }
        }
    }

    /// <summary>Fire the autocannon along the nose if the gun is off cooldown. Autocannon II
    /// (GunTier 2) doubles the fire rate. Returns true when a bolt actually left the barrel
    /// (drives muzzle feedback in the renderer).</summary>
    public bool TryFire()
    {
        if (_gunCooldown > 0f) return false;
        _gunCooldown = GunTier >= 2 ? 0.13f : 0.26f;
        // Autocannon III: twin barrels in a slight spread; below that, one straight bolt.
        var spread = GunTier >= 3 ? 0.055f : 0f;
        for (var s = GunTier >= 3 ? -1 : 0; s <= (GunTier >= 3 ? 1 : 0); s += 2)
        {
            var a = ShipHeading + spread * s;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            Shots.Add(new ShipShot
            {
                Pos = ShipPos + dir * (ShipRadius + 6f),
                Vel = dir * ShotSpeed + ShipVel,
                Life = 1.5f,
            });
        }
        return true;
    }

    /// <summary>Drop a rock into the field — the maintenance spawner and the tests both come
    /// through here. Hp scales with size so big rocks soak a burst, not one bolt.</summary>
    public Asteroid SpawnAsteroid(Vector2 pos, Vector2 vel, float radius)
    {
        var a = new Asteroid
        {
            Pos = pos, Vel = vel, Radius = radius, Hp = radius * 1.6f,
            Spin = (float)(Random.Shared.NextDouble() - 0.5) * 2.2f,
        };
        Asteroids.Add(a);
        return a;
    }

    private void UpdateShots(float dt)
    {
        for (var i = Shots.Count - 1; i >= 0; i--)
        {
            var s = Shots[i];
            s.Pos += s.Vel * dt;
            s.Life -= dt;
            if (s.Life <= 0f) { Shots.RemoveAt(i); continue; }
            // Indexed (not foreach): KillAsteroid mutates the list, then we break.
            for (var j = 0; j < Asteroids.Count; j++)
            {
                var a = Asteroids[j];
                if ((s.Pos - a.Pos).LengthSquared() > a.Radius * a.Radius) continue;
                a.Hp -= ShotDamage;
                if (a.Hp <= 0f) KillAsteroid(a);
                Shots.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>Big rocks calve into two smaller ones; small ones just die. The fragments
    /// inherit the parent's drift plus a sideways kick so they visibly split apart.</summary>
    private void KillAsteroid(Asteroid a)
    {
        Asteroids.Remove(a);
        LastRockShattered = a.Pos;
        if (a.Radius < 26f) return;
        var kick = new Vector2(-a.Vel.Y, a.Vel.X);
        if (kick.LengthSquared() < 1f) kick = new Vector2(0f, 40f);
        kick = Vector2.Normalize(kick) * 46f;
        SpawnAsteroid(a.Pos + kick * 0.3f, a.Vel + kick, a.Radius * 0.55f);
        SpawnAsteroid(a.Pos - kick * 0.3f, a.Vel - kick, a.Radius * 0.55f);
    }

    private void UpdateAsteroids(float dt)
    {
        for (var i = Asteroids.Count - 1; i >= 0; i--)
        {
            var a = Asteroids[i];
            a.Pos += a.Vel * dt;
            a.Rot += a.Spin * dt;

            // Cull rocks that drifted far out of play; maintenance below replaces them.
            if ((a.Pos - ShipPos).LengthSquared() > 3200f * 3200f)
            {
                Asteroids.RemoveAt(i);
                continue;
            }

            // Ram the mothership: lose hull, shove the ship along the impact normal, and eat
            // the rock. The invulnerability window stops one debris cloud double-tapping.
            // A charged deflector shield eats the impact instead (knockback still applies)
            // and starts its recharge.
            var d = ShipPos - a.Pos;
            var dist = d.Length();
            if (dist < a.Radius + ShipRadius && HitTimer <= 0f)
            {
                if (ShieldReady)
                {
                    ShieldCooldown = ShieldRechargeTime;
                    HitTimer = 0.4f;
                }
                else
                {
                    Hull--;
                    HitTimer = 1.0f;
                    if (Hull <= 0) HullBreached = true;
                }
                var n = dist > 0.5f ? d / dist : new Vector2(0f, -1f);
                ShipVel += n * 260f;
                LastRockShattered = a.Pos;
                Asteroids.RemoveAt(i);
            }
        }

        // Keep the field stocked: new rocks appear in a donut around the ship — outside the
        // screen at system zoom, close enough to matter soon — drifting loosely shipward.
        while (Asteroids.Count < AsteroidTarget)
        {
            var ang = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var distOut = 1500f + (float)Random.Shared.NextDouble() * 1200f;
            var pos = ShipPos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * distOut;
            var drift = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var speed = 30f + (float)Random.Shared.NextDouble() * 70f;
            var vel = new Vector2(MathF.Cos(drift), MathF.Sin(drift)) * speed
                      - new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 25f;
            SpawnAsteroid(pos, vel, 16f + (float)Random.Shared.NextDouble() * 26f);
        }
    }

    /// <summary>The closest planet and the ship's distance to its disc surface (negative =
    /// inside). Drives the approach zoom, the prefetch, and atmosphere-entry detection.</summary>
    public (SpacePlanet? planet, float surfaceDist) NearestPlanet()
    {
        SpacePlanet? best = null;
        var bestD = float.MaxValue;
        foreach (var p in Planets)
        {
            var d = (ShipPos - p.Pos).Length() - p.BodyRadius;
            if (d < bestD) { bestD = d; best = p; }
        }
        return (best, bestD);
    }

    /// <summary>The planet the ship is flying at: the closest body whose disc sits inside
    /// a cone (~18° half-angle) around the velocity — or the nose, when nearly still. Null
    /// when nothing is dead ahead. Drives the world prefetch, which wants the *destination*
    /// planet long before it becomes the nearest one.</summary>
    public SpacePlanet? AimedPlanet()
    {
        var dir = ShipVel.LengthSquared() > 40f * 40f ? Vector2.Normalize(ShipVel) : ShipDir;
        SpacePlanet? best = null;
        var bestDist = float.MaxValue;
        foreach (var p in Planets)
        {
            var to = p.Pos - ShipPos;
            var dist = to.Length();
            if (dist <= 1f || dist >= bestDist) continue;
            var along = Vector2.Dot(to, dir);
            if (along <= 0f) continue;   // behind the ship
            // Hit when the lateral miss distance is inside the disc plus a cone that
            // widens with range (0.32 ≈ tan 18°) — coarse aim far out still counts.
            var lateral = MathF.Sqrt(MathF.Max(0f, dist * dist - along * along));
            if (lateral < p.BodyRadius + EntryRange + along * 0.32f)
            {
                best = p;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>The planet whose upper atmosphere the ship has just flown into, or null.
    /// The locked Rift never returns — its storm wall keeps the ship outside this range.</summary>
    public SpacePlanet? AtmosphereContact()
    {
        var (p, d) = NearestPlanet();
        if (p is null || d > EntryRange) return null;
        if (p.Def.Id == "rift" && RiftLocked) return null;
        return p;
    }
}
