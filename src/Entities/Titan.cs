using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Kaiju-scale quadruped boss. The body has a small physics-collision footprint; legs are
/// procedural — each one ray-marches the terrain for a foot anchor and steps when the body
/// drifts too far from it. Hip-to-foot distance is unconstrained, so legs visibly stretch
/// over mountain peaks and compress on flat ground. The body is then lifted by its planted
/// feet (spring force toward avg-foot + hover offset) so it can walk over obstacles its
/// collision footprint alone couldn't clear. Each foot strike damages the tile underneath
/// via Planet.Mine — soft tiles (dirt/grass/snow) crack visibly and break after a few
/// stomps; harder tiles only crack cosmetically. Anger rises with player depth, unlocking
/// stomp earthquakes and ranged boulder hurls.
/// </summary>
public sealed class Titan
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 125f;       // hit-detection radius (projectile collisions)
    public float BodyRadius = 60f;    // physics-collision radius (smaller, so the kaiju can scrape past terrain)
    public float Health = 2500f;
    public float MaxHealth = 2500f;
    public float Anger;               // 0..100
    public float MoveSpeed = 58f;     // tangent pixels/sec base — deliberately slow so the kaiju lumbers
    public float Gravity = 320f;
    public float HitFlash;
    public float StompCooldown = 4f;
    public float HurlCooldown = 6f;
    public bool Grounded;
    public float Facing = 1f;         // smoothed -1..+1; which way the head/snout points along the local tangent
    public float Pulse;               // body breathing/anger pulsation (radians, advanced each tick)
    public TitanLeg[] Legs = null!;   // 4 procedural legs (quadruped)
    public Vector2[] TailNodes = null!;     // verlet chain — node 0 anchors to the body's rump
    public Vector2[] TailPrev = null!;      // previous-frame positions for verlet integration

    /// <summary>Which boss this is — drives the special attack and the render tint.</summary>
    public readonly TitanKind Kind;

    // ─── Egg spawn ────────────────────────────────────────────────────────────
    /// <summary>The boss incubates in a giant egg. It hatches when <see cref="EggTimer"/> runs
    /// out (10 minutes) or when the egg's health is beaten to zero — attack the egg to fight
    /// the boss early. Until then the body is dormant and only the egg is drawn/hittable.</summary>
    public bool Hatched;
    public float EggTimer = EggHatchSeconds;
    public float EggHealth;
    public float EggMaxHealth = 600f;
    public const float EggHatchSeconds = 600f;
    /// <summary>Set for one frame the moment the egg cracks open — Game1 reads it to fire the
    /// hatch shake + shell-burst particles, then it's cleared.</summary>
    public bool JustHatched;

    // ─── Special attacks ──────────────────────────────────────────────────────
    /// <summary>Cooldown until the next special; <see cref="SpecialState"/> is a per-attack
    /// sub-timer (fire-breath duration, laser charge, burrow time, leap airtime).</summary>
    public float SpecialCooldown;
    public float SpecialState;
    /// <summary>Sandworm: submerged and tunnelling. Invulnerable and undrawn (a dirt mound tracks
    /// it) until it erupts.</summary>
    public bool Submerged;
    /// <summary>Kong: mid-leap. Suppresses the leg-spring suspension so it actually leaves the
    /// ground, and gates the landing-slam detection. Also reused by the Sandworm's breach so it
    /// arcs freely out of the ground.</summary>
    public bool Leaping;

    /// <summary>Sandworm: erupted out of the ground and arcing mouth-first through the air
    /// (the classic Dune breach). Renderer opens the maw skyward while this is set.</summary>
    public bool Breaching;
    /// <summary>Melee AoE pending from a Kong slam or Sandworm eruption — Game1 consumes it to
    /// damage/knock-back the player and spew debris, since the Titan has no Player reference.</summary>
    public (Vector2 pos, float radius, float damage)? PendingShockwave;

    /// <summary>Projectiles can hit the boss/egg except while the Sandworm is burrowed.</summary>
    public bool Targetable => !Submerged;

    /// <summary>Seconds of aggro remaining. While > 0, the kaiju chases the player and uses
    /// stomp / boulder-hurl attacks; while ≤ 0, it lazily roams the planet surface in a random
    /// direction. Reset to AggroDuration whenever OnDamage() is called.</summary>
    public float AggroTimer;
    public const float AggroDuration = 10f;
    public bool IsAggro => AggroTimer > 0f;

    private int _roamDir;             // -1 / 0 / +1 along the body's tangent while roaming
    private float _roamTimer;         // seconds until the next roam-direction reroll
    private int _flameTick;           // frame counter throttling fire-breath grain spawns

    /// <summary>Hover height — distance the body wants to maintain above the average planted-foot
    /// position along planet-up. Higher values let the kaiju stride over taller terrain. The
    /// serpent Sandworm rides much lower (<see cref="Hover"/>) since it has no legs.</summary>
    public const float BodyHover = 105f;

    /// <summary>Effective body ride-height for this kind — bipeds stand tall, the legless
    /// serpent slithers close to the ground.</summary>
    public float Hover => Kind == TitanKind.Sandworm ? 44f : BodyHover;

    /// <summary>Verlet spine chain: a dragging tail for the bipeds, a long undulating body for
    /// the Sandworm (its heads mount at node 0). Segment length + node count vary by kind.</summary>
    private readonly float _tailSeg;
    private readonly int _tailNodeCount;

    /// <summary>Locked aim for the Mecha's beam, captured when its charge completes so the beam
    /// fires in a committed direction the player can dodge out of.</summary>
    private Vector2 _lockedAim = Vector2.UnitX;
    /// <summary>Seconds the Mecha's drilling beam keeps firing after the charge — the renderer
    /// draws a solid lance for this window and it emits carving bolts each frame.</summary>
    public float BeamTimer;

    /// <summary>Committed beam direction (read by the renderer to draw the Mecha's lance).</summary>
    public Vector2 BeamDir => _lockedAim;

    /// <summary>Windup lengths exposed so the renderer can drive the charge-up telegraphs
    /// (dorsal-spine glow for the atomic breath, the growing orb for the laser).</summary>
    public const float FireBreathWindup = 1.75f;
    public const float LaserChargeWindup = 1.5f;

    private readonly Planet _planet;

    public Titan(Planet planet, float startAngle, TitanKind kind = TitanKind.Godzilla)
    {
        _planet = planet;
        Kind = kind;
        EggHealth = EggMaxHealth;
        // Sandworm's spine is its whole body (long, many nodes); the others just drag a tail.
        (_tailNodeCount, _tailSeg) = kind == TitanKind.Sandworm ? (13, 30f) : (7, 26f);
        // Rest the egg near the ground; the hatched boss rises to hover height on its own once
        // its legs plant and the suspension lifts it.
        var hover = FindSurfaceSpawn(planet, startAngle);
        Position = hover - planet.UpAt(hover) * (BodyHover - 24f);
        InitLegs();
        InitTail();
    }

    /// <summary>Crack the egg — early (from damage) or on the 10-minute timer. The boss comes
    /// out already aggroed so the fight starts immediately.</summary>
    public void Hatch()
    {
        if (Hatched) return;
        Hatched = true;
        JustHatched = true;
        AggroTimer = AggroDuration;
        SpecialCooldown = 4f;
    }

    /// <summary>Apply damage to the egg (routed here by <see cref="Systems.Combat"/> while the
    /// boss is unhatched). Beating the egg to zero hatches it immediately.</summary>
    public void DamageEgg(float dmg)
    {
        if (Hatched) return;
        EggHealth -= dmg;
        HitFlash = 0.15f;
        if (EggHealth <= 0f) Hatch();
    }

    private void InitLegs()
    {
        // Skeleton per kind. Bipeds (Godzilla/Mecha/Kong) stand on two legs planted under the
        // body, stepping in alternation; the Sandworm is legless and slithers on its belly, so it
        // gets no legs at all (surface-follow locomotion + verlet body instead). HipForward is
        // signed along the tangent; Side is the lateral stance sign.
        Legs = Kind == TitanKind.Sandworm
            ? System.Array.Empty<TitanLeg>()
            : new[]
            {
                new TitanLeg { HipForward = 6f, Side = -1, Phase = 0.00f, HipUp = 8f },   // left
                new TitanLeg { HipForward = 6f, Side = +1, Phase = 0.50f, HipUp = 8f },   // right
            };

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

    private void InitTail()
    {
        var up = _planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        // Lay the chain out along -tangent so it dangles behind the body at spawn. Verlet picks
        // it up from there — drag/gravity will settle it on the next few frames.
        TailNodes = new Vector2[_tailNodeCount];
        TailPrev = new Vector2[_tailNodeCount];
        var root = Position + right * (Facing * -90f) + up * 18f;
        for (var i = 0; i < _tailNodeCount; i++)
        {
            TailNodes[i] = root + right * (Facing * -i * _tailSeg) + up * (-i * 4f);
            TailPrev[i] = TailNodes[i];
        }
    }

    private static Vector2 FindSurfaceSpawn(Planet planet, float angle)
    {
        // Step inward from far above the highest possible peak and stop at the first solid
        // tile, then float a leg's-reach above so the kaiju settles on its feet, not its belly.
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        for (var d = planet.Radius + 30; d > 10; d--)
        {
            var p = planet.Center + dir * (d * Planet.TileSize);
            if (planet.IsSolidAt(p))
                return p + dir * (BodyHover + 20f);
        }
        return planet.Center + dir * ((planet.Radius + 20) * Planet.TileSize);
    }

    /// <summary>Reset the aggro timer to AggroDuration. Called from the projectile-hit code
    /// when a player projectile strikes the kaiju — that's the only thing that wakes it up.</summary>
    public void OnDamage()
    {
        AggroTimer = AggroDuration;
    }

    /// <summary>Anger only rises while aggroed (driven by player depth — the deeper they go,
    /// the angrier the kaiju gets). When de-aggroed, anger decays back toward 0 so a passive
    /// kaiju is calm and won't trigger stomps/hurls.</summary>
    public void UpdateAnger(Vector2 playerPos)
    {
        if (IsAggro)
        {
            var fromCenter = (playerPos - _planet.Center).Length();
            var surface = _planet.Radius * Planet.TileSize;
            var depthFraction = MathHelper.Clamp(1f - fromCenter / surface, 0f, 1f);
            var target = depthFraction * 110f;
            Anger = MathHelper.Lerp(Anger, target, 0.01f);
        }
        else
        {
            Anger = MathHelper.Lerp(Anger, 0f, 0.01f);
        }
    }

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Vector2 playerPos,
        List<FallingBoulder> boulders, List<TitanProjectile> shots)
    {
        // Egg phase: dormant until the timer runs out or the egg is beaten open. The body
        // doesn't move, attack, or fall — only the shell wobbles (Pulse) and flashes on hits.
        if (!Hatched)
        {
            EggTimer -= dt;
            Pulse += dt * 1.2f;
            if (HitFlash > 0) HitFlash -= dt;
            if (EggTimer <= 0f) Hatch();
            return;
        }

        // Tick aggro/roam timers. Aggro counts down from AggroDuration; once it hits 0 the
        // kaiju goes back to lazily roaming the surface in a random tangent direction.
        AggroTimer -= dt;
        _roamTimer -= dt;
        if (!IsAggro && _roamTimer <= 0f)
        {
            // Re-roll roam direction. ~25% chance to stand still, then split between left/right.
            // Hold the new direction for 4–10 seconds before re-rolling.
            var r = Random.Shared.NextDouble();
            _roamDir = r < 0.25 ? 0 : (r < 0.625 ? -1 : +1);
            _roamTimer = 4f + (float)Random.Shared.NextDouble() * 6f;
        }

        UpdateAnger(playerPos);

        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);

        // Decide tangent direction. Aggroed: chase the player along the local surface.
        // Roaming: walk slowly in whatever direction we picked at the last roam reroll.
        int moveAxis;
        float speedMul;
        if (IsAggro)
        {
            var toPlayer = playerPos - Position;
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
            speedMul = 1f;
        }
        else
        {
            moveAxis = _roamDir;
            speedMul = 0.45f;   // slow roam — kaiju is just wandering, not hunting
        }

        // Decompose velocity into tangent and normal components so gravity, walking, and the
        // leg-spring lift can be handled independently of where on the planet we are.
        var vTangent = Vector2.Dot(Velocity, right);
        var vNormal = Vector2.Dot(Velocity, up);

        var targetTangent = moveAxis * MoveSpeed * speedMul * (1f + Anger / 80f);
        var accel = Grounded ? 260f : 100f;
        vTangent = MoveToward(vTangent, targetTangent, accel * dt);

        vNormal -= Gravity * dt;

        // Body suspension: a critically-damped spring holds the body at Hover above its support
        // point. Bipeds support on the average of their planted feet; the legless Sandworm supports
        // on the ground directly below its belly. When feet are planted the spring cancels
        // gravity and pulls toward the target height with no oscillation; with no support
        // (mid-leap, mid-fall) gravity wins and the body drops.
        var planted = Kind == TitanKind.Sandworm
            ? GroundBelow(up, out var hasPlanted)
            : AvgPlantedFoot(out hasPlanted);
        if (hasPlanted && !Leaping)   // a leaping Kong ignores its suspension so it can launch
        {
            var heightAboveFeet = Vector2.Dot(Position - planted, up);
            var deficit = Hover - heightAboveFeet;
            var springAcc = MathHelper.Clamp(deficit * 9f, -500f, 800f);
            var dampAcc = -vNormal * 4f;
            vNormal += (Gravity + springAcc + dampAcc) * dt;
        }

        Velocity = right * vTangent + up * vNormal;
        Position += Velocity * dt;
        // Smash through terrain rather than being shoved around by it: the body bulldozes any
        // non-anchored tile it overlaps, carving a body-sized tunnel through mountains it walks
        // into. On flat ground the body rides high enough that its plow radius never reaches the
        // floor, so it doesn't dig itself under.
        Plow(planet, physics, cells);

        // Grounded probe a little under the body using the (smaller) body collision radius.
        Grounded = ProbeSolid(planet, Position - up * (BodyRadius + 2f));

        // Smoothly turn to face the direction of motion.
        if (MathF.Abs(vTangent) > 6f)
        {
            var targetFacing = MathF.Sign(vTangent);
            Facing = MathHelper.Lerp(Facing, targetFacing, 1f - MathF.Exp(-3.5f * dt));
        }
        Pulse += dt * (1.4f + Anger * 0.04f);

        UpdateLegs(dt, planet, physics, cells, up, right, vTangent);
        UpdateTail(dt, planet, up, right);

        StompCooldown -= dt;
        HurlCooldown -= dt;

        // Stomp: earthquake centered at the kaiju's standing point. Only when aggroed and
        // grounded — a passive kaiju doesn't pound the ground, and a stomp mid-air looks
        // ridiculous (nothing's there to crack).
        if (IsAggro && StompCooldown <= 0 && Anger > 15f && Grounded)
        {
            var quakeRadius = 130f + Anger * 3f;
            var strength = 2 + (int)(Anger / 35f);
            // Use a hind foot as the stomp epicenter so the shake feels grounded in the body's
            // actual stance, not the floating body center.
            var epi = hasPlanted ? planted : Position - up * BodyRadius;
            physics.Earthquake(epi, quakeRadius, strength);
            StompCooldown = MathHelper.Lerp(8f, 2.5f, Anger / 100f);
        }

        // Hurl: lobs a boulder along the line of sight to the player. Aggro-gated.
        if (IsAggro && HurlCooldown <= 0 && Anger > 50f)
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

        SpecialCooldown -= dt;
        UpdateSpecial(dt, planet, physics, playerPos, shots);

        if (HitFlash > 0) HitFlash -= dt;
    }

    /// <summary>Per-kind signature attack. Godzilla breathes fire, Mecha fires a mouth laser,
    /// Sandworm burrows and erupts, Kong leaps and slams. All are aggro-gated so a calm boss just
    /// roams. <see cref="SpecialCooldown"/> paces them; <see cref="SpecialState"/> runs the
    /// active window (breath duration, laser charge, burrow time, leap airtime).</summary>
    private void UpdateSpecial(float dt, Planet planet, Physics physics, Vector2 playerPos, List<TitanProjectile> shots)
    {
        switch (Kind)
        {
            case TitanKind.Godzilla: TickFireBreath(dt, playerPos, shots); break;
            case TitanKind.Mecha:    TickMechaLaser(dt, playerPos, shots); break;
            case TitanKind.Sandworm:    TickSandworm(dt, planet, physics, playerPos); break;
            case TitanKind.Kong:     TickKong(dt, physics, playerPos); break;
        }
    }

    private Vector2 Mouth()
    {
        var up = _planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        return Position + up * 26f + right * (Facing * 130f);
    }

    /// <summary>True when the boss is planted on the ground (probing past the hover height,
    /// unlike the shallow <see cref="Grounded"/> body-collision probe which rarely reaches the
    /// terrain under a hovering body). Gates the launch of grounded specials — fire breath and
    /// the Kong leap — and reads false mid-leap/mid-fall so they can't re-trigger in the air.</summary>
    private bool Standing() => ProbeSolid(_planet, Position - _planet.UpAt(Position) * (BodyHover + 12f));

    /// <summary>The Godzilla-style atomic breath: a short bright wind-up (dorsal spines pulse —
    /// see the renderer), then a dense ~1.4s cone of fire poured from the mouth at the player.
    /// The renderer reads <see cref="SpecialState"/> for the spine glow; here we hose flame.</summary>
    private void TickFireBreath(float dt, Vector2 playerPos, List<TitanProjectile> shots)
    {
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            // The first ~0.35s is the charge (spines light up, no flame yet); then it erupts.
            // Spawn only on alternating frames — a couple of fat, longer-lived grains per emit
            // read as a dense billowing gout without flooding the projectile list (the old
            // 4-grains-every-frame stream spawned 200+ live fireballs and tanked the frame rate).
            if (SpecialState < FireBreathDuration - 0.35f && (++_flameTick & 1) == 0)
            {
                var mouth = Mouth();
                var aim = playerPos - mouth;
                if (aim.LengthSquared() > 0.01f)
                {
                    aim.Normalize();
                    for (var i = 0; i < 2; i++)
                    {
                        var spread = ((float)Random.Shared.NextDouble() - 0.5f) * 0.7f;
                        var c = MathF.Cos(spread); var s = MathF.Sin(spread);
                        var d = new Vector2(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c);
                        var speed = 150f + (float)Random.Shared.NextDouble() * 170f;
                        shots.Add(new TitanProjectile(mouth + d * 12f, d * speed, TitanShotKind.Flame));
                    }
                }
            }
            if (SpecialState <= 0f) SpecialCooldown = 6.5f;
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f || !Standing()) return;
        if ((playerPos - Position).Length() > 600f) return;
        SpecialState = FireBreathDuration;
    }
    private const float FireBreathDuration = FireBreathWindup;

    /// <summary>Mecha laser: a long, obvious charge (a growing orb at the mouth + a tracking
    /// telegraph line the player can dodge — both drawn by the renderer), then a sustained
    /// drilling beam. During the beam window it spits a fat carving bolt every frame along the
    /// direction locked at the end of the charge, so the beam bores a tunnel toward where the
    /// player was standing.</summary>
    private void TickMechaLaser(float dt, Vector2 playerPos, List<TitanProjectile> shots)
    {
        if (BeamTimer > 0f)
        {
            BeamTimer -= dt;
            var mouth = Mouth();
            shots.Add(new TitanProjectile(mouth + _lockedAim * 16f, _lockedAim * 900f, TitanShotKind.Laser));
            if (BeamTimer <= 0f) SpecialCooldown = 6.5f;
            return;
        }
        if (SpecialState > 0f)   // charging — track the player, then commit the aim
        {
            SpecialState -= dt;
            var aim = playerPos - Mouth();
            if (aim.LengthSquared() > 0.01f) _lockedAim = Vector2.Normalize(aim);
            if (SpecialState <= 0f) BeamTimer = 0.6f;
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f) return;
        if ((playerPos - Position).Length() > 800f) return;
        SpecialState = LaserChargeWindup;
    }

    /// <summary>Shai-Hulud, the Dune sandworm: it lives underground, tunnelling toward the
    /// player (a dirt mound tracks it), then BREACHES — erupting straight up out of the sand
    /// mouth-first with a quake + shockwave — before diving back under to hunt again. It only
    /// surfaces to breach, so the fight is a rhythm of watching the mound and dodging the
    /// eruption. The breach reuses <see cref="Leaping"/> to arc freely (spring suppressed).</summary>
    private void TickSandworm(float dt, Planet planet, Physics physics, Vector2 playerPos)
    {
        if (Breaching)
        {
            SpecialState -= dt;
            // Crash back down after a beat of airtime, then dive under to hunt again.
            var airborneLongEnough = SpecialState < BreachAirtime - 0.6f;
            if ((Grounded && airborneLongEnough) || SpecialState <= 0f)
            {
                Breaching = false;
                Leaping = false;
                physics.Earthquake(Position, 130f, 2);
                if (IsAggro) { Submerged = true; SpecialState = BurrowTime; }
                else SpecialCooldown = 5f;
            }
            return;
        }

        if (Submerged)
        {
            SpecialState -= dt;
            var toPlayer = playerPos - Position;
            if (toPlayer.LengthSquared() > 1f)
                Position += Vector2.Normalize(toPlayer) * 340f * dt;   // burrow straight at them
            if (SpecialState <= 0f || toPlayer.Length() < 130f)
                Breach(planet, physics, playerPos);
            return;
        }

        // Surfaced and idle — Shai-Hulud doesn't lounge on the sand; it dives under to hunt.
        if (!IsAggro || SpecialCooldown > 0f) return;
        if ((playerPos - Position).Length() > 1000f) return;
        Submerged = true;
        SpecialState = BurrowTime;
    }
    private const float BurrowTime = 2.0f;
    private const float BreachAirtime = 1.7f;

    /// <summary>Erupt straight up out of the sand beneath the player, maw first.</summary>
    private void Breach(Planet planet, Physics physics, Vector2 playerPos)
    {
        var rel = playerPos - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        var up = planet.UpAt(playerPos);
        // Start just under the surface below the player, then launch upward.
        Position = FindSurfaceSpawn(planet, ang) - up * (BodyHover - 20f);
        Velocity = up * 640f;
        Submerged = false;
        Breaching = true;
        Leaping = true;   // suppress the ground-spring so the eruption arcs
        SpecialState = BreachAirtime;
        physics.Earthquake(Position, 190f, 3);
        PendingShockwave = (Position, 170f, 34f);
    }

    /// <summary>Kong: leap toward the player, then slam down with a heavy quake + shockwave on
    /// landing. The leap suppresses the leg-spring (via <see cref="Leaping"/>) so it launches.</summary>
    private void TickKong(float dt, Physics physics, Vector2 playerPos)
    {
        if (Leaping)
        {
            SpecialState -= dt;
            // Slam once we touch back down after a moment of airtime — or forcibly at the end
            // of the airtime window so a Kong wedged against terrain still resolves its leap.
            var airborneLongEnough = SpecialState < 1.4f;
            if ((Grounded && airborneLongEnough) || SpecialState <= 0f)
            {
                var up = _planet.UpAt(Position);
                physics.Earthquake(Position - up * BodyRadius, 260f, 4);
                PendingShockwave = (Position, 220f, 34f);
                Leaping = false;
                SpecialCooldown = 6f;
            }
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f || !Standing()) return;
        if ((playerPos - Position).Length() > 700f) return;
        var u = _planet.UpAt(Position);
        var right = new Vector2(-u.Y, u.X);
        var dirSign = MathF.Sign(Vector2.Dot(playerPos - Position, right));
        Velocity += u * 520f + right * (dirSign * 240f);
        Leaping = true;
        SpecialState = 2.0f;
    }

    /// <summary>Mean of foot positions for legs that are currently planted (StepT ≥ 1).
    /// Mid-step legs are excluded so the body doesn't bob each time a leg arcs.</summary>
    private Vector2 AvgPlantedFoot(out bool hasAny)
    {
        var sum = Vector2.Zero;
        var count = 0;
        foreach (var leg in Legs)
        {
            if (leg.StepT >= 1f) { sum += leg.FootPos; count++; }
        }
        hasAny = count > 0;
        return hasAny ? sum / count : Vector2.Zero;
    }

    /// <summary>Bulldoze terrain: mine every non-anchored solid tile overlapping the body's
    /// collision radius so the boss smashes a tunnel through mountains it walks into instead of
    /// being shoved over them. Anchored tiles (planet core, player supports) can't be chewed —
    /// the body is pushed off those. Broken tiles drop tumbling debris + wake the settle
    /// physics, so plowing a mountain also caves in whatever it was holding up.</summary>
    private void Plow(Planet planet, Physics physics, Cells cells)
    {
        var (tx, _) = planet.WorldToTile(Position);
        var rel = Position - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var span = (int)MathF.Ceiling(BodyRadius / Planet.TileSize) + 1;
        var plowPow = 26 + (int)(Anger / 16f);   // shatters surface rock (dirt/stone/granite) fast
        var rSq = BodyRadius * BodyRadius;

        for (var dx = -span; dx <= span; dx++)
        {
            var x = tx + dx;
            if (x < 0 || x >= Planet.RingCount) continue;
            // Recompute the angular index per ring from the true body angle — ring tile counts
            // differ, so a shared index would drift and miss overlapped tiles.
            var nRing = Planet.TilesAt(x);
            var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
            for (var dy = -span; dy <= span; dy++)
            {
                var y = ty0 + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k)) continue;
                var centre = planet.TileToWorld(x, y);
                if ((centre - Position).LengthSquared() > rSq) continue;

                if (Tiles.IsAnchored(k))
                {
                    var diff = Position - centre;
                    var dist = diff.Length();
                    if (dist > 0.001f && dist < BodyRadius)
                    {
                        var n = diff / dist;
                        Position += n * (BodyRadius - dist + 0.05f);
                        var into = Vector2.Dot(Velocity, n);
                        if (into < 0) Velocity -= n * into;
                    }
                    continue;
                }

                var broken = planet.Mine(x, y, plowPow);
                if (broken.HasValue)
                {
                    physics.MarkDirty(x, y);
                    cells.SpawnDustInTile(x, y, broken.Value);
                }
            }
        }
    }

    /// <summary>Ground point directly below the body along -up (the legless Sandworm's support
    /// point). Marches down to the first solid tile; falls back to a fixed offset over open sky.</summary>
    private Vector2 GroundBelow(Vector2 up, out bool found)
    {
        for (var d = 0f; d <= 320f; d += 6f)
        {
            var probe = Position - up * d;
            if (_planet.IsSolidAt(probe)) { found = true; return probe; }
        }
        found = false;
        return Position - up * 40f;
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

    /// <summary>Search for a foot anchor for one leg by ray-marching downward (along -planet-up
    /// from the leg's lateral search start) until a solid tile is found. Returns the world-space
    /// anchor position, biased forward by the body's tangential motion so feet step ahead of
    /// where the body is going. If no terrain is found, returns a max-reach dangle position.</summary>
    private Vector2 ResolveFootAnchor(TitanLeg leg, Vector2 up, Vector2 right, Vector2 motionBias)
    {
        const float legSideStride = 95f;
        const float legSearchUp = 35f;
        const float legSearchDown = 360f;
        const float legProbeStep = 4f;

        var hipWorld = Position + right * leg.HipForward + up * leg.HipUp;
        var anchorStart = hipWorld + right * (leg.Side * legSideStride) + up * legSearchUp;

        var found = false;
        var foot = anchorStart - up * legSearchDown;
        for (var d = 0f; d <= legSearchDown; d += legProbeStep)
        {
            var probe = anchorStart - up * d;
            if (_planet.IsSolidAt(probe))
            {
                foot = probe + _planet.UpAt(probe) * 5f;
                found = true;
                break;
            }
        }
        if (found) foot += motionBias;
        return foot;
    }

    /// <summary>Per-frame leg simulation. Each leg compares its current planted foot position to
    /// the freshly-resolved terrain anchor; if they've drifted past a per-leg threshold, the leg
    /// lifts and steps to the new anchor along a sin-arc. When a step lands, the tile under the
    /// foot takes damage via Planet.Mine — soft ground cracks visibly each stomp and breaks
    /// after a few; hard rock just gets cosmetic cracks.</summary>
    private void UpdateLegs(float dt, Planet planet, Physics physics, Cells cells, Vector2 up, Vector2 right, float vTangent)
    {
        var biasMag = MathHelper.Clamp(vTangent / 80f, -1.4f, 1.4f);
        var motionBias = right * (biasMag * 28f);

        foreach (var leg in Legs)
        {
            var ideal = ResolveFootAnchor(leg, up, right, motionBias);

            if (leg.StepT >= 1f)
            {
                var threshold = 28f + leg.Phase * 22f;
                if ((leg.FootPos - ideal).LengthSquared() > threshold * threshold)
                {
                    leg.StepStart = leg.FootPos;
                    leg.StepTarget = ideal;
                    leg.StepT = 0f;
                }
            }
            else
            {
                // Slow, deliberate swing — a hundred-foot leg doesn't snap forward. Rate scales
                // gently with pace so it still keeps up at a run without ever looking twitchy.
                var stepRate = 2.4f + MathF.Abs(vTangent) * 0.03f;
                var prevT = leg.StepT;
                leg.StepT = MathF.Min(1f, leg.StepT + dt * stepRate);
                if (leg.StepT >= 1f && prevT < 1f)
                {
                    // Foot just touched down — stomp the tile. Damage falls off across a small
                    // radius so each step leaves a small footprint of cracked dirt rather than
                    // a single gouged tile.
                    leg.FootPos = leg.StepTarget;
                    StompTile(planet, physics, cells, leg.FootPos);
                }
                else
                {
                    var t = leg.StepT;
                    var smooth = t * t * (3f - 2f * t);
                    var pos = Vector2.Lerp(leg.StepStart, leg.StepTarget, smooth);
                    var arc = MathF.Sin(t * MathF.PI) * 44f;   // high, ponderous lift
                    pos += planet.UpAt(pos) * arc;
                    leg.FootPos = pos;
                }
            }
        }
    }

    /// <summary>Apply a foot-strike damage footprint at <paramref name="footPos"/>. Center
    /// tile gets the full power; the surrounding 8 tiles get a softer hit. Power scales lightly
    /// with anger so an angry kaiju cracks ground faster. Mine() applies hardness scaling, so
    /// dirt/grass/snow break in a few stomps while stone/granite only get visible cracks.
    /// When a tile actually breaks, falling-cell debris of the appropriate material is spawned
    /// in its place so the gap fills with tumbling dust rather than a clean hole.</summary>
    private void StompTile(Planet planet, Physics physics, Cells cells, Vector2 footPos)
    {
        var (fx, fy) = planet.WorldToTile(footPos);
        // A wider footprint than a single tile — these are hundred-foot feet. Full power at the
        // centre falling off to the rim; power scales with anger so an enraged boss gouges deeper.
        const int r = 2;
        var centerPower = 5 + (int)(Anger / 24f);
        for (var dy = -r; dy <= r; dy++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                var x = fx + dx; var y = fy + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k)) continue;
                var edge = MathHelper.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy) / (r + 0.5f), 0.2f, 1f);
                var pow = Math.Max(1, (int)(centerPower * edge));
                var broken = planet.Mine(x, y, pow);
                if (broken.HasValue)
                {
                    physics.MarkDirty(x, y);
                    // Spawn dust in the now-empty tile so the gap fills with tumbling debris
                    // (carrying the source tile's colour + drop) rather than a clean hole.
                    cells.SpawnDustInTile(x, y, broken.Value);
                }
            }
        }

        // Cave-in: shove a shock a few tiles below the footfall so any cavern the boss is
        // standing over loses its roof and collapses. The Settle inside Earthquake dislodges
        // unsupported tiles; Game1 turns the resulting CollapsesThisTick into screen shake.
        var up = planet.UpAt(footPos);
        physics.Earthquake(footPos - up * (Planet.TileSize * 5f), 64f + Anger * 0.5f, 1 + (int)(Anger / 45f));
    }

    /// <summary>Verlet-integrated tail. Node 0 is hard-anchored to a point on the body's rump
    /// (opposite the head's facing); subsequent nodes free-fall under planet gravity with
    /// damping, then a fixed-distance constraint keeps the chain links at TailSegLen apart.
    /// The result is a tail that drags behind the body when moving, droops downward when
    /// idle, and curls around terrain it brushes against.</summary>
    private void UpdateTail(float dt, Planet planet, Vector2 up, Vector2 right)
    {
        // Anchor the tail's root to the back of the body, opposite the head's facing.
        var root = Position + right * (Facing * -98f) + up * 18f;
        TailNodes[0] = root;
        TailPrev[0] = root;

        // Verlet integration on the free nodes.
        for (var i = 1; i < TailNodes.Length; i++)
        {
            var temp = TailNodes[i];
            var velocity = (TailNodes[i] - TailPrev[i]) * 0.94f;  // damping
            var grav = planet.GravityAt(TailNodes[i]) * 380f;
            TailNodes[i] += velocity + grav * (dt * dt);
            TailPrev[i] = temp;
        }

        // Terrain collision: any node inside a solid tile is pushed out along the local up.
        for (var i = 1; i < TailNodes.Length; i++)
        {
            for (var safety = 0; safety < 4 && planet.IsSolidAt(TailNodes[i]); safety++)
            {
                TailNodes[i] += planet.UpAt(TailNodes[i]) * 3f;
            }
        }

        // Distance constraints — multiple iterations for stability.
        for (var iter = 0; iter < 6; iter++)
        {
            for (var i = 1; i < TailNodes.Length; i++)
            {
                var d = TailNodes[i] - TailNodes[i - 1];
                var len = d.Length();
                if (len < 0.001f) continue;
                var diff = (len - _tailSeg) / len;
                // Parent stays put (it's the previous segment, already corrected this iter, or
                // the body anchor); only the child node moves to satisfy the constraint.
                TailNodes[i] -= d * diff;
            }
        }
    }
}

/// <summary>One procedural leg of the Titan. Hip is body-local (offset along the body's tangent
/// axis, lifted slightly above the body center); foot is world-space and persists across frames
/// — feet stay planted until the body has moved enough to trigger a step, then arc to a new
/// terrain-resolved anchor. Hip→foot distance is unconstrained, so legs visibly stretch over
/// peaks and pits and compress on flat ground.</summary>
public sealed class TitanLeg
{
    public float HipForward;       // body-tangent offset of the hip (signed: front/back)
    public float HipUp = 18f;      // body-up offset of the hip
    public int Side;               // -1 = left, +1 = right (sign of the lateral search direction)
    public float Phase;            // 0..1, staggers step thresholds so legs don't lift in lockstep
    public Vector2 FootPos;        // current world-space foot position
    public Vector2 StepStart;      // world-space foot position at the moment a step began
    public Vector2 StepTarget;     // world-space anchor the step is reaching for
    public float StepT = 1f;       // 0..1 during a step; ≥1 means planted
}

/// <summary>Ranged attack fired by a boss at the player. Self-contained like
/// <see cref="FallingBoulder"/> (checks player contact itself) so it never tangles with the
/// player-projectile Combat sweep. Flame is a short-lived fireball from Godzilla's breath;
/// Laser is a fast bolt from the Mecha's mouth.</summary>
public enum TitanShotKind { Flame, Laser }

public sealed class TitanProjectile
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Life;
    public bool Dead;
    public float Radius;
    public readonly TitanShotKind Kind;

    /// <summary>Wall-pierce budget for the drilling laser — it bores through this many solid
    /// tiles before dying, so the Mecha's beam carves a real tunnel instead of stopping at the
    /// first wall. Flame has none (it splashes on rock).</summary>
    private int _drill;

    public TitanProjectile(Vector2 pos, Vector2 vel, TitanShotKind kind)
    {
        Position = pos;
        Velocity = vel;
        Kind = kind;
        (Radius, Life) = kind switch
        {
            TitanShotKind.Flame => (9f, 0.85f),   // fatter, shorter-lived — fewer grains read as one gout
            _                   => (4f, 0.9f),   // Laser
        };
        _drill = kind == TitanShotKind.Laser ? 3 : 0;
    }

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Player player)
    {
        Position += Velocity * dt;
        Life -= dt;
        if (Life <= 0f) { Dead = true; return; }

        // Player contact — Flame sears, Laser hits harder and knocks back.
        var diff = player.Position - Position;
        if (diff.Length() < Radius + player.Radius)
        {
            if (Kind == TitanShotKind.Flame)
            {
                player.TakeDamage(9f);
            }
            else
            {
                player.TakeDamage(28f);
                if (diff.LengthSquared() > 0.0001f) player.Velocity += Vector2.Normalize(diff) * 200f;
            }
            Dead = true;
            return;
        }

        if (planet.IsSolidAt(Position))
        {
            if (Kind == TitanShotKind.Laser && _drill > 0)
            {
                // Drill the wall: vaporise the tile and keep going until the pierce budget runs
                // out, boring a glowing tunnel through terrain (and player cover).
                var (tx, ty) = planet.WorldToTile(Position);
                var k = planet.Get(tx, ty);
                if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k))
                {
                    if (planet.Mine(tx, ty, 40) is { } broken)
                    {
                        physics.MarkDirty(tx, ty);
                        cells.SpawnDustInTile(tx, ty, broken);
                    }
                    _drill--;
                }
                else { Dead = true; return; }   // hit bedrock/anchor — beam stops
            }
            else
            {
                Dead = true;   // Flame splashes; a spent beam dies
                return;
            }
        }
        if (Kind == TitanShotKind.Flame)
            cells.IgniteGasNear(Position, 6f);
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
            player.TakeDamage(25f);
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
