using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Kaiju-scale quadruped boss. The body has a small physics-collision footprint; legs are
/// procedural — each one ray-marches the terrain for a foot anchor and steps when the body
/// drifts too far from it. Hip-to-foot distance is capped at <see cref="LegMaxReach"/> (the
/// two drawn leg bones near-straight): anchors are clamped to reach and a planted leg that
/// gets overstretched steps early, so legs bend and stride instead of rubber-banding over
/// terrain. The body is then lifted by its planted feet (spring force toward avg-foot +
/// hover offset) so it can walk over obstacles its collision footprint alone couldn't clear. Each foot strike damages the tile underneath
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

    /// <summary>Knifehead mid-gore / Raiju mid-dash — the renderer leans the body into the
    /// sprint and Game1's HUD can read it. Cleared when the burst resolves.</summary>
    public bool Charging;
    /// <summary>EMP pulse pending from Leatherback's back-turbine discharge — Game1 consumes
    /// it to fry the dwarf's tech (jetpack + energy weapons) for <c>seconds</c> if they're
    /// inside the radius. Mirrors the PendingShockwave hand-off pattern.</summary>
    public (Vector2 pos, float radius, float seconds)? PendingEmp;

    /// <summary>Committed gore direction (tangent sign) while Knifehead charges.</summary>
    private float _chargeDir;
    /// <summary>Raiju lunge chain: dashes remaining in the current burst, plus a short
    /// grace between contact hits so an overlap doesn't shred the player every frame.</summary>
    private int _dashesLeft;
    private float _clipTimer;
    /// <summary>Slattern alternates tail-spike barrages with sonic pulses.</summary>
    private bool _slatternPulse;

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

    /// <summary>Length of each drawn leg bone (thigh and shin are equal). Shared with
    /// <see cref="Rendering.TitanRenderer"/>'s two-bone IK so the simulation never plants a
    /// foot farther than the drawn leg can actually reach.</summary>
    public const float LegBoneLen = 80f;
    /// <summary>Maximum hip→foot distance — both bones almost straight, with a sliver of
    /// slack kept so the knee never fully locks out.</summary>
    public const float LegMaxReach = LegBoneLen * 1.96f;
    /// <summary>Lateral half-spacing between the two hip sockets at the pelvis. Without this
    /// the legs share one origin point and read as a wishbone.</summary>
    public const float HipHalfSpan = 26f;

    /// <summary>World position of a leg's hip socket — pelvis center offset along the tangent
    /// by the leg's side. Single source of truth used by both the anchor search and the
    /// renderer's IK, so the drawn thigh always roots where the simulation thinks it does.</summary>
    public Vector2 HipWorld(TitanLeg leg, Vector2 up, Vector2 right)
        => Position + right * (leg.HipForward + leg.Side * HipHalfSpan) + up * leg.HipUp;

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
    /// (dorsal-spine glow for the atomic breath, the growing orb for the laser, the crest
    /// shimmer before a gore, the crackling turbine before the EMP).</summary>
    public const float FireBreathWindup = 1.75f;
    public const float LaserChargeWindup = 1.5f;
    public const float GoreWindup = 0.9f;
    public const float EmpWindup = 1.4f;
    public const float AcidSprayWindup = 2.0f;   // 0.9s rear-up + 1.1s spray (mirrors fire breath's shape)

    private readonly Planet _planet;

    public Titan(Planet planet, float startAngle, TitanKind kind = TitanKind.Godzilla)
    {
        _planet = planet;
        Kind = kind;
        EggHealth = EggMaxHealth;
        // Per-kind chassis: the kaiju wave trades bulk for speed in both directions —
        // Raiju is a glass-cannon sprinter, Leatherback a slow tank, Slattern the
        // category-5 apex (biggest body, deepest health pool, guards the Rift).
        (Health, MoveSpeed) = kind switch
        {
            TitanKind.Knifehead   => (2300f, 62f),
            TitanKind.Otachi      => (2100f, 60f),
            TitanKind.Leatherback => (3000f, 46f),
            TitanKind.Raiju       => (1700f, 96f),
            TitanKind.Slattern    => (4200f, 62f),
            _                     => (Health, MoveSpeed),
        };
        MaxHealth = Health;
        if (kind == TitanKind.Slattern) { Radius = 150f; BodyRadius = 70f; }
        // Sandworm's spine is its whole body (long, many nodes); Slattern drags a long
        // spiked lash it whips barrages from; the others just drag a tail.
        (_tailNodeCount, _tailSeg) = kind switch
        {
            TitanKind.Sandworm => (13, 30f),
            TitanKind.Slattern => (10, 32f),
            _                  => (7, 26f),
        };
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
        // gets no legs at all (surface-follow locomotion + verlet body instead). Hip sockets sit
        // HipHalfSpan apart on the pelvis (via HipWorld); Side is the lateral stance sign.
        Legs = Kind == TitanKind.Sandworm
            ? System.Array.Empty<TitanLeg>()
            : new[]
            {
                new TitanLeg { HipForward = 0f, Side = -1, Phase = 0.00f, HipUp = 8f },   // left
                new TitanLeg { HipForward = 0f, Side = +1, Phase = 0.50f, HipUp = 8f },   // right
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
        // Lay the chain out behind the body at spawn; verlet picks it up from there. Bipeds root
        // it at the rump so it dangles behind + down (a dragging tail). The Sandworm's chain IS
        // its body — it roots at the HEAD and lays straight back, so the maw (drawn on node 0)
        // is always welded to the segments trailing behind it.
        TailNodes = new Vector2[_tailNodeCount];
        TailPrev = new Vector2[_tailNodeCount];
        var worm = Kind == TitanKind.Sandworm;
        // Biped tail roots at the lower back, tucked just inside the torso so its base is covered
        // by the body and the chain emerges from the rump with no gap (was floating ~50px behind).
        var root = worm ? Position + right * (Facing * 8f)
                        : Position + right * (Facing * -46f) + up * 20f;
        for (var i = 0; i < _tailNodeCount; i++)
        {
            TailNodes[i] = root + right * (Facing * -i * _tailSeg) + up * (worm ? 0f : -i * 4f);
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

        // Knifehead's gore owns the pace: plant and crouch through the windup, then sprint
        // flat-out along the committed direction — the player dodges the line, not the chase.
        if (Kind == TitanKind.Knifehead && SpecialState > 0f)
        {
            moveAxis = Charging ? MathF.Sign(_chargeDir) : 0;
            speedMul = Charging ? 4.4f : 0f;
        }

        // Decompose velocity into tangent and normal components so gravity, walking, and the
        // leg-spring lift can be handled independently of where on the planet we are.
        var vTangent = Vector2.Dot(Velocity, right);
        var vNormal = Vector2.Dot(Velocity, up);

        // The worm slithers, it doesn't charge — hold its forward pace down to a slow crawl.
        var paceMul = Kind == TitanKind.Sandworm ? 0.55f : 1f;
        var targetTangent = moveAxis * MoveSpeed * speedMul * paceMul * (1f + Anger / 80f);
        var accel = Charging ? 900f : Grounded ? 260f : 100f;
        vTangent = MoveToward(vTangent, targetTangent, accel * dt);

        vNormal -= Gravity * dt;

        // Body suspension / locomotion. Bipeds ride a critically-damped spring that holds the
        // body at Hover above the average of their planted feet (gravity cancelled when planted,
        // gravity wins mid-leap/mid-fall). The legless Sandworm instead weaves like a snake: no
        // spring and no jump — its head eases toward a point that traces the terrain surface but
        // sine-oscillates above and below it, so the worm threads up out of the ground and back
        // under as it slides forward, moving through the planet rather than standing on it.
        var planted = Vector2.Zero;
        var hasPlanted = false;
        if (Kind == TitanKind.Sandworm)
        {
            var surface = SurfacePoint(planet, up);
            var weave = MathF.Sin(Pulse * 1.1f) * 78f;      // crest over / dive under the surface line
            var targetH = Vector2.Dot(surface + up * weave - Position, up);
            vNormal = MathHelper.Clamp(targetH * 3.2f, -170f, 170f);
        }
        else
        {
            planted = AvgPlantedFoot(out hasPlanted);
            if (hasPlanted && !Leaping)   // a leaping Kong ignores its suspension so it can launch
            {
                var heightAboveFeet = Vector2.Dot(Position - planted, up);
                var deficit = Hover - heightAboveFeet;
                var springAcc = MathHelper.Clamp(deficit * 9f, -500f, 800f);
                var dampAcc = -vNormal * 4f;
                vNormal += (Gravity + springAcc + dampAcc) * dt;
            }
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
        if (IsAggro && StompCooldown <= 0 && Anger > 15f && Grounded && Kind != TitanKind.Sandworm)
        {
            var quakeRadius = 130f + Anger * 3f;
            var strength = 2 + (int)(Anger / 35f);
            // Use a hind foot as the stomp epicenter so the shake feels grounded in the body's
            // actual stance, not the floating body center.
            var epi = hasPlanted ? planted : Position - up * BodyRadius;
            physics.Earthquake(epi, quakeRadius, strength);
            StompCooldown = MathHelper.Lerp(8f, 2.5f, Anger / 100f);
        }

        // Hurl: lobs a boulder along the line of sight to the player. Aggro-gated. Only the
        // kinds with arms (and the temper) throw — the worm has none, the blade-headed and
        // dash kaiju fight with their bodies, and Otachi spits instead.
        var hurls = Kind is TitanKind.Godzilla or TitanKind.Mecha or TitanKind.Kong
                         or TitanKind.Leatherback or TitanKind.Slattern;
        if (IsAggro && HurlCooldown <= 0 && Anger > 50f && hurls)
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
    /// Sandworm burrows and erupts, Kong leaps and slams; the kaiju wave — Knifehead gores,
    /// Otachi spits acid, Leatherback EMPs, Raiju dash-chains, Slattern whips spike barrages.
    /// All are aggro-gated so a calm boss just roams. <see cref="SpecialCooldown"/> paces
    /// them; <see cref="SpecialState"/> runs the active window (breath duration, laser
    /// charge, burrow time, leap airtime, gore sprint, spray, dash chain).</summary>
    private void UpdateSpecial(float dt, Planet planet, Physics physics, Vector2 playerPos, List<TitanProjectile> shots)
    {
        switch (Kind)
        {
            case TitanKind.Godzilla:    TickFireBreath(dt, playerPos, shots); break;
            case TitanKind.Mecha:       TickMechaLaser(dt, playerPos, shots); break;
            case TitanKind.Sandworm:    TickSandworm(dt, planet, physics, playerPos); break;
            case TitanKind.Kong:        TickKong(dt, physics, playerPos); break;
            case TitanKind.Knifehead:   TickKnifehead(dt, physics, playerPos); break;
            case TitanKind.Otachi:      TickAcidSpray(dt, playerPos, shots); break;
            case TitanKind.Leatherback: TickEmp(dt, physics, playerPos); break;
            case TitanKind.Raiju:       TickRaiju(dt, playerPos); break;
            case TitanKind.Slattern:    TickSlattern(dt, physics, playerPos, shots); break;
        }
    }

    /// <summary>Knifehead: crouch through a telegraphed windup, then gore — a flat-out
    /// blade-first sprint along the surface (the pace override lives in <see cref="Update"/>).
    /// Catching the player on the blade is a heavy hit + launch; the sprint also plows
    /// whatever terrain stands in the line. Whiffing just runs the sprint out.</summary>
    private void TickKnifehead(float dt, Physics physics, Vector2 playerPos)
    {
        const float goreDuration = 1.5f;
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            if (!Charging && SpecialState <= goreDuration)
                Charging = true;   // windup over — commit to the sprint
            if (Charging && (playerPos - Position).Length() < BodyRadius + 55f)
            {
                // Gored: heavy damage + a launch along the charge direction (Game1 adds the
                // radial knockback; the quake sells the impact).
                PendingShockwave = (Position, 150f, 36f);
                physics.Earthquake(Position, 120f, 2);
                SpecialState = 0f;
            }
            if (SpecialState <= 0f) { Charging = false; SpecialCooldown = 5.5f; }
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f || !Standing()) return;
        if ((playerPos - Position).Length() > 620f) return;
        var right = new Vector2(-_planet.UpAt(Position).Y, _planet.UpAt(Position).X);
        _chargeDir = MathF.Sign(Vector2.Dot(playerPos - Position, right));
        if (_chargeDir == 0f) _chargeDir = Facing >= 0f ? 1f : -1f;
        SpecialState = GoreWindup + goreDuration;
    }

    /// <summary>Otachi: rears up and hoses a volley of acid globs at the player. The globs
    /// arc under gravity and burst into real acid cells (see <see cref="TitanProjectile"/>),
    /// so every spray leaves corrosive pools eating the terrain where it landed — the player
    /// is dodging the arc AND ceding ground.</summary>
    private void TickAcidSpray(float dt, Vector2 playerPos, List<TitanProjectile> shots)
    {
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            // First 0.9s is the rear-up; then the spray, a glob every other frame.
            if (SpecialState < AcidSprayWindup - 0.9f && (++_flameTick & 1) == 0)
            {
                var mouth = Mouth();
                var aim = playerPos - mouth;
                if (aim.LengthSquared() > 0.01f)
                {
                    aim.Normalize();
                    var up = _planet.UpAt(Position);
                    var spread = ((float)Random.Shared.NextDouble() - 0.5f) * 0.5f;
                    var c = MathF.Cos(spread); var s = MathF.Sin(spread);
                    var d = new Vector2(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c);
                    // Loft the glob so it arcs — the acid rains down rather than darting flat.
                    var vel = d * (200f + (float)Random.Shared.NextDouble() * 90f) + up * 130f;
                    shots.Add(new TitanProjectile(mouth + d * 12f, vel, TitanShotKind.Acid));
                }
            }
            if (SpecialState <= 0f) SpecialCooldown = 7f;
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f) return;
        if ((playerPos - Position).Length() > 700f) return;
        SpecialState = AcidSprayWindup;
    }

    /// <summary>Leatherback: spins up the back turbine (the renderer crackles arcs over it),
    /// then detonates an EMP. Game1 consumes <see cref="PendingEmp"/> — inside the radius the
    /// dwarf's tech dies for the duration: jetpack, energy weapons, scanner fixes. Light on
    /// raw damage; the danger is being de-teched next to a three-thousand-HP ape.</summary>
    private void TickEmp(float dt, Physics physics, Vector2 playerPos)
    {
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            if (SpecialState <= 0f)
            {
                PendingEmp = (Position, 430f, 6f);
                PendingShockwave = (Position, 200f, 10f);   // the pressure wave itself stings
                physics.Earthquake(Position, 140f, 2);
                SpecialCooldown = 9f;
            }
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f || !Standing()) return;
        if ((playerPos - Position).Length() > 520f) return;
        SpecialState = EmpWindup;
    }

    /// <summary>Raiju: the fastest kaiju fights in lunge chains — three quick dashes, each an
    /// impulse re-aimed at the player, each clipping them for a modest hit on contact. The
    /// chain telegraphs itself through sheer motion; the counter-window is the cooldown after
    /// the third lunge.</summary>
    private void TickRaiju(float dt, Vector2 playerPos)
    {
        _clipTimer -= dt;
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            Charging = true;
            // A fresh impulse every 0.45s while dashes remain.
            if (_dashesLeft > 0 && SpecialState <= _dashesLeft * 0.45f)
            {
                _dashesLeft--;
                var up = _planet.UpAt(Position);
                var right = new Vector2(-up.Y, up.X);
                var dir = MathF.Sign(Vector2.Dot(playerPos - Position, right));
                if (dir == 0f) dir = Facing >= 0f ? 1f : -1f;
                Velocity += right * (dir * 430f) + up * 70f;
            }
            if (_clipTimer <= 0f && (playerPos - Position).Length() < BodyRadius + 45f)
            {
                PendingShockwave = (Position, 100f, 15f);   // clipped mid-lunge
                _clipTimer = 0.5f;                          // one clip per pass, not per frame
            }
            if (SpecialState <= 0f) { Charging = false; SpecialCooldown = 6f; }
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f || !Standing()) return;
        if ((playerPos - Position).Length() > 560f) return;
        _dashesLeft = 3;
        SpecialState = 3 * 0.45f;
    }

    /// <summary>Slattern, the category-5 apex: alternates a radial tail-spike barrage — a fan
    /// of fast bolts flung from the tail tip toward the player — with a sonic pulse, a huge
    /// low-damage shockwave that shoves the player off their footing (and off ledges). Paced
    /// faster than any other special; this is the campaign's final wall.</summary>
    private void TickSlattern(float dt, Physics physics, Vector2 playerPos, List<TitanProjectile> shots)
    {
        if (!IsAggro || SpecialCooldown > 0f) return;
        if ((playerPos - Position).Length() > 760f) return;
        _slatternPulse = !_slatternPulse;
        if (_slatternPulse)
        {
            physics.Earthquake(Position, 200f, 3);
            PendingShockwave = (Position, 320f, 18f);
        }
        else
        {
            var tip = TailNodes[^1];
            var aim = playerPos - tip;
            if (aim.LengthSquared() < 0.01f) return;
            aim.Normalize();
            const int spikes = 9;
            for (var i = 0; i < spikes; i++)
            {
                var spread = (i / (float)(spikes - 1) - 0.5f) * 1.0f;
                var c = MathF.Cos(spread); var s = MathF.Sin(spread);
                var d = new Vector2(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c);
                shots.Add(new TitanProjectile(tip + d * 10f, d * 340f, TitanShotKind.Spike));
            }
        }
        SpecialCooldown = 4.5f;
    }

    /// <summary>World position the boss's ranged attacks issue from — the actual drawn mouth.
    /// MUST stay in sync with <see cref="Rendering.TitanRenderer"/>'s MouthPos, or the beam/flame
    /// will spawn (and aim) from a different point than it's drawn, so the visible lance won't
    /// pass through where it actually hits.</summary>
    private Vector2 Mouth()
    {
        var up = _planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        return Kind == TitanKind.Mecha
            ? Position + up * 90f + right * (Facing * 78f)
            : Position + up * 106f + right * (Facing * 92f);
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

    /// <summary>Shai-Hulud, the Dune sandworm. It no longer burrows or breaches — it simply
    /// slithers slowly through the planet toward the player, weaving up out of the ground and back
    /// under (the serpentine locomotion lives in <see cref="Update"/>). This just adds the bite:
    /// when the maw catches up to within snapping range it snaps shut — a ground shock plus a
    /// knockback pulse (<see cref="PendingShockwave"/>) — paced by the cooldown so it can't
    /// chain-chomp.</summary>
    private void TickSandworm(float dt, Planet planet, Physics physics, Vector2 playerPos)
    {
        if (!IsAggro || SpecialCooldown > 0f) return;
        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        var maw = Position + right * (Facing * 30f);
        if ((playerPos - maw).Length() > 130f) return;
        physics.Earthquake(Position, 90f, 2);
        PendingShockwave = (maw, 120f, 24f);
        SpecialCooldown = 3.5f;
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

    /// <summary>Terrain surface directly along the head's radial: march down from well above the
    /// head to the first solid tile. Traces the reference line the Sandworm's serpentine weave
    /// oscillates around, so the head can dive below it and crest over it as it slithers.</summary>
    private Vector2 SurfacePoint(Planet planet, Vector2 up)
    {
        var top = Position + up * 240f;
        for (var d = 0f; d <= 480f; d += 5f)
        {
            var p = top - up * d;
            if (planet.IsSolidAt(p)) return p;
        }
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
    /// where the body is going. The result is always clamped to <see cref="LegMaxReach"/> from
    /// the hip — over a cliff or pit the foot stops at full extension in mid-air rather than
    /// stretching, and the body suspension then lowers the body until the legs reach ground.</summary>
    private Vector2 ResolveFootAnchor(TitanLeg leg, Vector2 up, Vector2 right, Vector2 motionBias)
    {
        const float legSideStride = 70f;
        const float legSearchUp = 35f;
        const float legSearchDown = 360f;
        const float legProbeStep = 4f;

        var hipWorld = HipWorld(leg, up, right);
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

        // Never plant beyond what the two drawn leg bones can span (slightly inside full
        // extension so the knee keeps a visible bend even at max stride).
        var toFoot = foot - hipWorld;
        var reach = toFoot.Length();
        const float maxPlant = LegMaxReach * 0.97f;
        if (reach > maxPlant) foot = hipWorld + toFoot * (maxPlant / reach);
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
                // Step when the terrain anchor has drifted away from the planted foot — or
                // earlier if the body has walked the leg near full extension, so a planted
                // foot never gets left behind stretching the leg past its bone length.
                var hip = HipWorld(leg, up, right);
                var overstretched = (leg.FootPos - hip).LengthSquared()
                    > LegMaxReach * LegMaxReach * (0.92f * 0.92f);
                if (overstretched || (leg.FootPos - ideal).LengthSquared() > threshold * threshold)
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

    /// <summary>Verlet-integrated spine chain. For bipeds node 0 anchors at the rump (opposite the
    /// head's facing) so the tail drags behind and droops; for the Sandworm node 0 anchors at the
    /// HEAD so the whole chain — the worm's body — stays welded to the maw and undulates behind it
    /// as the head weaves. Free nodes free-fall with damping, then a fixed-distance constraint
    /// keeps the links at TailSegLen apart.</summary>
    private void UpdateTail(float dt, Planet planet, Vector2 up, Vector2 right)
    {
        var worm = Kind == TitanKind.Sandworm;
        // Anchor node 0. Bipeds: the lower back, tucked just inside the torso so the tail base is
        // covered by the body and emerges from the rump seamlessly. Sandworm: the head (body trails).
        var root = worm
            ? Position + right * (Facing * 8f)
            : Position + right * (Facing * -46f) + up * 20f;
        TailNodes[0] = root;
        TailPrev[0] = root;

        // Verlet integration on the free nodes. The worm's body follows the head's weave rather
        // than drooping, so it barely feels gravity; a biped's tail hangs under full gravity.
        var gravMag = worm ? 90f : 380f;
        for (var i = 1; i < TailNodes.Length; i++)
        {
            var temp = TailNodes[i];
            var velocity = (TailNodes[i] - TailPrev[i]) * 0.94f;  // damping
            var grav = planet.GravityAt(TailNodes[i]) * gravMag;
            TailNodes[i] += velocity + grav * (dt * dt);
            TailPrev[i] = temp;
        }

        // Terrain collision keeps a biped's tail from clipping into the ground. The Sandworm is
        // *meant* to thread through the planet, so its body passes freely through solid rock (its
        // head plows a tunnel) instead of being shoved back up to the surface.
        if (!worm)
            for (var i = 1; i < TailNodes.Length; i++)
                for (var safety = 0; safety < 4 && planet.IsSolidAt(TailNodes[i]); safety++)
                    TailNodes[i] += planet.UpAt(TailNodes[i]) * 3f;

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

/// <summary>One procedural leg of the Titan. Hip is body-local (see <see cref="Titan.HipWorld"/>
/// — the two sockets sit <see cref="Titan.HipHalfSpan"/> apart on the pelvis); foot is
/// world-space and persists across frames — feet stay planted until the body has moved enough
/// to trigger a step, then arc to a new terrain-resolved anchor. Hip→foot distance is capped
/// at <see cref="Titan.LegMaxReach"/>: anchors clamp to reach and an overstretched planted leg
/// steps early, so legs compress and bend but never rubber-band.</summary>
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
/// Laser is a fast bolt from the Mecha's mouth; Acid is Otachi's lofted glob that arcs under
/// gravity and bursts into live acid cells; Spike is a straight bolt from Slattern's tail fan.</summary>
public enum TitanShotKind { Flame, Laser, Acid, Spike }

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
