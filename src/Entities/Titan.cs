using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// Kaiju-scale biped boss. The body has a small physics-collision footprint; legs are
/// procedural with a real stride gait — each foot stays planted while the body walks past
/// it, and once it has fallen a full stride behind its thrown-ahead anchor (ray-marched to
/// terrain) the leg swings forward past the other foot to plant ahead again. Legs alternate:
/// one may only lift while the other is planted. Hip-to-foot distance is capped at
/// <see cref="LegMaxReach"/> (thigh + shin + foot near-straight): anchors are clamped to
/// reach and a planted leg that gets overstretched steps early, so legs bend and stride
/// instead of rubber-banding over terrain. The body is then lifted by its planted feet
/// (spring force toward avg-foot + hover offset) so it can walk over obstacles its collision
/// footprint alone couldn't clear. Each foot strike damages the tile underneath
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
    public TitanLeg[] Legs = null!;   // 2 procedural legs (biped); empty for the worm/flyers
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

    /// <summary>Dig-down hunt: very angry, prey deep below and roughly underfoot → the walker
    /// plants itself and pounds the ground. Each timed slam (see the dig block in
    /// <see cref="Update"/>) forces a leg to rear up and stomp in place; the landing excavates
    /// a body-wide crater (<see cref="DigCrater"/>), the feet re-anchor into the hole and the
    /// suspension follows them down — it sinks toward the player one deliberate slam at a
    /// time rather than gliding through rock.</summary>
    public bool Digging { get; private set; }
    /// <summary>Anger required before it starts digging down after a burrowed player.</summary>
    public const float DigAngerGate = 55f;
    private float _digTimer;
    private int _digLeg;
    private bool _digPending;
    /// <summary>Airtime window for the generic hunt-jump (prey above, no shaft walls to
    /// chimney) — while it runs, <see cref="Leaping"/> suppresses the suspension so the leap
    /// actually leaves the ground; it clears Leaping when it expires. Kong is excluded (its
    /// special owns Leaping).</summary>
    private float _jumpTimer;

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
    /// position along planet-up. Higher values let the kaiju stride over taller terrain. Sized
    /// so the three-bone leg (thigh + shin + foot) stands with a visible knee bend at rest and
    /// still has extension in reserve for the ends of a stride. The serpent Sandworm rides much
    /// lower (<see cref="Hover"/>) since it has no legs.</summary>
    public const float BodyHover = 118f;

    /// <summary>Effective body ride-height for this kind — bipeds stand tall, the legless
    /// serpent slithers close to the ground.</summary>
    public float Hover => Kind == TitanKind.Sandworm ? 44f : BodyHover;

    /// <summary>The winged kaiju — no legs, airborne locomotion (cruises above the surface,
    /// dips low during a bombing run), and the wing-flap render skeleton.</summary>
    public bool Flyer => Kind is TitanKind.Pyrodactyl or TitanKind.Vitriodactyl;

    /// <summary>Flyer mid-bombing-run: the renderer folds the wings into the dive and the
    /// locomotion sinks to strafing height while it rains.</summary>
    public bool Bombing;

    /// <summary>Thigh and shin length (equal) of the hip→knee→ankle IK chain. Shared with
    /// <see cref="Rendering.TitanRenderer"/>'s two-bone IK so the simulation never plants a
    /// foot farther than the drawn leg can actually reach.</summary>
    public const float LegBoneLen = 66f;
    /// <summary>The third joint: the ankle sits this far above the planted toe. The foot bone
    /// (ankle→toe) is what touches ground — a digitigrade theropod leg, so the knee bows
    /// forward, the ankle kicks back, and the foot plants flat.</summary>
    public const float AnkleLift = 26f;
    /// <summary>…and this far behind the toe (against facing), so the ankle reads as the
    /// raised backward-bending joint above the heel.</summary>
    public const float AnkleBack = 12f;
    /// <summary>Maximum hip→foot distance — thigh, shin and foot almost straight, with a
    /// sliver of slack kept so the knee never fully locks out.</summary>
    public const float LegMaxReach = (LegBoneLen * 2f + AnkleLift) * 0.96f;
    /// <summary>Lateral half-spacing between the two hip sockets at the pelvis. Without this
    /// the legs share one origin point and read as a wishbone.</summary>
    public const float HipHalfSpan = 26f;
    /// <summary>Neutral stance: each foot rests this far to its side of the pelvis center —
    /// feet nearly under the body, not straddled wide.</summary>
    public const float StanceHalf = 20f;
    /// <summary>While walking, a stepping foot plants this far AHEAD of its neutral point in
    /// the direction of motion, and a planted foot lets the body walk past it until it has
    /// fallen the same distance behind — so each step covers ~2× this and the feet cross each
    /// other, a stride rather than a splayed shuffle.</summary>
    public const float StrideHalf = 52f;

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
            TitanKind.Knifehead    => (2300f, 62f),
            TitanKind.Otachi       => (2100f, 60f),
            TitanKind.Leatherback  => (3000f, 46f),
            TitanKind.Raiju        => (1700f, 96f),
            TitanKind.Slattern     => (4200f, 62f),
            TitanKind.Pyrodactyl   => (2000f, 88f),
            TitanKind.Vitriodactyl => (2000f, 88f),
            _                      => (Health, MoveSpeed),
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
        // body, stepping in alternation; the Sandworm is legless and slithers on its belly and
        // the flyers never touch down (airborne locomotion + tucked talons), so those get no
        // legs at all. Hip sockets sit HipHalfSpan apart on the pelvis (via HipWorld); Side is
        // the lateral stance sign.
        Legs = Kind == TitanKind.Sandworm || Flyer
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
            // Seed one foot a quarter-stride ahead and the other behind so the first steps
            // trigger half a cycle apart — the gait starts alternating instead of clumped.
            leg.FootPos = ResolveFootAnchor(leg, up, right, (leg.Phase - 0.25f) * 2f * StrideHalf);
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
            // Don't wander off a chasm edge: a fall into a cave system is one the boss can
            // never climb back out of, so it would spend the rest of the run underground.
            // Turn around at the edge (or stand, if it's somehow on a pillar). Aggro pursuit
            // deliberately skips this — a hunting kaiju will drop off a cliff after you.
            if (moveAxis != 0 && CliffAhead(planet, up, right, moveAxis))
            {
                moveAxis = CliffAhead(planet, up, right, -moveAxis) ? 0 : -moveAxis;
                _roamDir = moveAxis;
            }
        }

        // Knifehead's gore owns the pace: plant and crouch through the windup, then sprint
        // flat-out along the committed direction — the player dodges the line, not the chase.
        if (Kind == TitanKind.Knifehead && SpecialState > 0f)
        {
            moveAxis = Charging ? MathF.Sign(_chargeDir) : 0;
            speedMul = Charging ? 4.4f : 0f;
        }

        // Close the generic hunt-jump's airtime window (Kong's special manages its own
        // Leaping; the timer is only ever armed for the other kinds).
        if (_jumpTimer > 0f)
        {
            _jumpTimer -= dt;
            if (_jumpTimer <= 0f && Kind != TitanKind.Kong) Leaping = false;
        }

        // ── Dig-down hunt ─────────────────────────────────────────────────────
        // Only once truly enraged (past DigAngerGate), with the player deep below and
        // roughly underfoot. It stops walking and pounds: a slam every _digTimer beat,
        // each excavating one crater-layer via the landed stomp — much slower than its
        // walk, and it can't sink without stomping. A player off to the side instead gets
        // chased laterally (the aggro plow carves sideways tunnels at full power), and the
        // stair-step of walking over + digging down closes on prey at any angle.
        var playerUpDot = Vector2.Dot(playerPos - Position, up);
        var playerSideDist = MathF.Abs(Vector2.Dot(playerPos - Position, right));
        Digging = IsAggro && Anger > DigAngerGate && Legs.Length > 0 && Grounded
                  && !Leaping && SpecialState <= 0f
                  && playerUpDot < -140f && playerSideDist < 130f;
        if (Digging)
        {
            moveAxis = 0;   // plant and pound — no pacing while excavating
            _digTimer -= dt;
            if (_digTimer <= 0f)
            {
                // Rear up whichever leg is planted (alternating) and slam it in place; the
                // landing branch in UpdateLegs runs the excavation.
                for (var i = 0; i < Legs.Length; i++)
                {
                    var leg = Legs[(_digLeg + i) % Legs.Length];
                    if (leg.StepT < 1f) continue;
                    leg.StepStart = leg.FootPos;
                    leg.StepTarget = leg.FootPos;
                    leg.StepT = 0f;
                    _digPending = true;
                    _digLeg = (_digLeg + i + 1) % Legs.Length;
                    break;
                }
                _digTimer = 1.6f;
            }
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
        else if (Flyer)
        {
            // Winged flight: hold a cruising band above the terrain, bobbing on the wing
            // beat. A bombing run sinks to strafing height so the rain lands tight; the
            // gravity applied above is cancelled by the band-holding spring, so the flyer
            // never touches down (and terrain rising underneath just lifts the band).
            var surface = SurfacePoint(planet, up);
            var bob = MathF.Sin(Pulse * 0.9f) * 24f;
            var cruise = (Bombing ? 130f : 200f) + bob;
            var targetH = Vector2.Dot(surface + up * cruise - Position, up);
            vNormal = MathHelper.Clamp(targetH * 2.6f, -230f, 230f);
        }
        else
        {
            planted = AvgPlantedFoot(planet, out hasPlanted);
            if (hasPlanted && !Leaping)   // a leaping Kong ignores its suspension so it can launch
            {
                // Vehicle-style suspension, same shape as the worm/flyer branches: ease the
                // body toward ride height over its supporting feet by setting the normal
                // velocity directly. (A spring-accelerated suspension pogo-pumped here — the
                // support window snaps on/off as feet plant, and each catch-and-launch cycle
                // added energy until the boss was catapulting thousands of pixels.)
                var heightAboveFeet = Vector2.Dot(Position - planted, up);
                var deficit = Hover - heightAboveFeet;
                vNormal = MathHelper.Clamp(deficit * 6f, -160f, 230f);
            }
            // Climb back to daylight. Two triggers: (1) self-rescue — a de-aggroed walker
            // sealed beneath the surface (a caved-in roof, or a chase that plowed it deep)
            // should never live out the run stuck in a cave the player can't even see;
            // (2) mid-hunt — the prey jetpacked back ABOVE it (out of the dig shaft), so it
            // scrambles upward after them, faster than the idle climb. The hunt climb needs
            // walls to brace against — it chimneys up its own shaft or plows through solid
            // overburden, but can't rocket into open sky from flat ground.
            if (!Leaping)
            {
                if (!IsAggro)
                {
                    var surface = SurfacePoint(planet, up);
                    if (Vector2.Dot(surface - Position, up) > 60f) vNormal = 130f;
                }
                else if (playerUpDot > 140f)
                {
                    if (Braced(planet, up, right))
                    {
                        vNormal = 175f;
                    }
                    else if (Grounded && Kind != TitanKind.Kong && _jumpTimer <= 0f
                             && BelowLocalTerrain(planet, up, right))
                    {
                        // Down a hole with nothing to chimney — jump after the prey.
                        // The plow smashes the arc through whatever roof it meets, so
                        // repeated leaps headbutt a path back toward the surface.
                        vNormal = 520f;
                        Leaping = true;
                        _jumpTimer = 1.1f;
                    }
                }
            }
        }

        Velocity = right * vTangent + up * vNormal;
        Position += Velocity * dt;
        // Smash through terrain rather than being shoved around by it: the body bulldozes any
        // non-anchored tile it overlaps, carving a body-sized tunnel through mountains it walks
        // into. On flat ground the body rides high enough that its plow radius never reaches the
        // floor, so it doesn't dig itself under.
        Plow(planet, physics, cells);

        // Grounded: either terrain right under the body (belly contact — worm, or a walker
        // shoved into a hillside), or standing on legs — a planted foot with solid ground
        // under it. The body-only probe alone can never fire for a walker at ride height
        // (it hangs Hover above its feet), which starved the stomp/landing-slam gates.
        Grounded = ProbeSolid(planet, Position - up * (BodyRadius + 2f)) || AnyFootOnGround(planet);

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
            case TitanKind.Pyrodactyl:
            case TitanKind.Vitriodactyl: TickBombingRun(dt, playerPos, shots); break;
        }
    }

    /// <summary>The flyers' signature: a bombing run. The wing kaiju sinks to strafing
    /// height (see the flight branch in <see cref="Update"/>) and rains ballistic gouts
    /// down at the player for a couple of seconds — live lava from the Pyrodactyl, live
    /// acid from the Vitriodactyl. Everything that lands feeds the cell sim, so a pass
    /// leaves burning (or melting) ground behind, and the buffed acid corrosion means the
    /// Vitriodactyl's rain eats the very cover you hide under.</summary>
    private void TickBombingRun(float dt, Vector2 playerPos, List<TitanProjectile> shots)
    {
        if (SpecialState > 0f)
        {
            SpecialState -= dt;
            Bombing = true;
            if ((++_flameTick & 1) == 0)
            {
                var mouth = Mouth();
                var aim = playerPos - mouth;
                if (aim.LengthSquared() > 0.01f)
                {
                    aim.Normalize();
                    var spread = ((float)Random.Shared.NextDouble() - 0.5f) * 0.45f;
                    var c = MathF.Cos(spread); var s = MathF.Sin(spread);
                    var d = new Vector2(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c);
                    var kind = Kind == TitanKind.Pyrodactyl ? TitanShotKind.Lava : TitanShotKind.Acid;
                    shots.Add(new TitanProjectile(mouth + d * 14f,
                        d * (190f + (float)Random.Shared.NextDouble() * 80f), kind));
                }
            }
            if (SpecialState <= 0f) { Bombing = false; SpecialCooldown = 6.5f; }
            return;
        }
        if (!IsAggro || SpecialCooldown > 0f) return;
        if ((playerPos - Position).Length() > 720f) return;
        SpecialState = 2.0f;
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
                float dir = MathF.Sign(Vector2.Dot(playerPos - Position, right));
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
        if (Flyer) return Position + up * 8f + right * (Facing * 84f);   // beak tip
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

    /// <summary>True if this planted foot can actually bear weight: it rests on solid ground
    /// and hasn't been left far overhead. A planted foot can hang in mid-air (anchors clamp to
    /// leg reach over a cliff), so StepT alone can't answer "is it standing" — probe just below
    /// the sole, where a ground-resolved anchor always has the tile it planted on. Feet planted
    /// modestly ABOVE the body still support it — that's how it climbs slopes and steps (the
    /// velocity-set suspension caps the lift, so an uphill foot can't slingshot it) — but a
    /// foot the body has fallen well past gives no purchase.</summary>
    private bool FootSupports(Planet planet, TitanLeg leg)
        => leg.StepT >= 1f
           && Vector2.Dot(Position - leg.FootPos, planet.UpAt(leg.FootPos)) > -60f
           && ProbeSolid(planet, leg.FootPos - planet.UpAt(leg.FootPos) * 10f);

    private bool AnyFootOnGround(Planet planet)
    {
        foreach (var leg in Legs)
            if (FootSupports(planet, leg)) return true;
        return false;
    }

    /// <summary>Mean of foot positions for legs that can bear weight (<see cref="FootSupports"/>).
    /// Mid-step legs are excluded so the body doesn't bob each time a leg arcs; mid-air
    /// "planted" feet (reach-clamped over a drop) and feet the body has fallen past are
    /// excluded so the suspension can't hold the body up on phantom footing — off a cliff the
    /// kaiju falls like anything else, legs reaching, until a foot finds real ground.</summary>
    private Vector2 AvgPlantedFoot(Planet planet, out bool hasAny)
    {
        var sum = Vector2.Zero;
        var count = 0;
        foreach (var leg in Legs)
        {
            if (FootSupports(planet, leg)) { sum += leg.FootPos; count++; }
        }
        hasAny = count > 0;
        return hasAny ? sum / count : Vector2.Zero;
    }

    /// <summary>Bulldoze terrain: mine non-anchored solid tiles overlapping the body's
    /// collision radius so the boss smashes a notch through mountains it walks into instead of
    /// being shoved over them. Walkers NEVER chew the floor: tiles in the bottom sector of the
    /// body act as collision (push the body out) rather than demolition — without that guard a
    /// freshly-hatched or landing boss, body still below ride height, pulverised every block
    /// under itself before the suspension could lift it. The Sandworm is the exception (it
    /// tunnels through the planet by design), and anchored tiles (planet core, player supports)
    /// always push back. Plowing is also gentler while calm — a roaming boss shoulders a few
    /// blocks out of its way; an aggroed one shatters rock at full power. Broken tiles drop
    /// tumbling debris + wake the settle physics, so plowing a mountain also caves in whatever
    /// it was holding up.</summary>
    private void Plow(Planet planet, Physics physics, Cells cells)
    {
        var (tx, _) = planet.WorldToTile(Position);
        var rel = Position - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var span = (int)MathF.Ceiling(BodyRadius / Planet.TileSize) + 1;
        // Full power shatters surface rock (dirt/stone/granite) fast; calm roamers only nudge
        // soft ground loose, so a stroll leaves the landscape standing.
        var plowPow = (IsAggro ? 26 : 12) + (int)(Anger / 16f);
        var rSq = BodyRadius * BodyRadius;
        var up = planet.UpAt(Position);
        var keepFloor = Kind != TitanKind.Sandworm;

        for (var dx = -span; dx <= span; dx++)
        {
            var x = tx + dx;
            if (x < 0 || x >= planet.Rings) continue;
            // Recompute the angular index per ring from the true body angle — ring tile counts
            // differ, so a shared index would drift and miss overlapped tiles.
            var nRing = planet.TilesAt(x);
            var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
            for (var dy = -span; dy <= span; dy++)
            {
                var y = ty0 + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k)) continue;
                var centre = planet.TileToWorld(x, y);
                if ((centre - Position).LengthSquared() > rSq) continue;

                // Ground under a walker is footing, not demolition fodder.
                var floor = keepFloor && Vector2.Dot(centre - Position, up) < -BodyRadius * 0.35f;
                if (Tiles.IsAnchored(k) || floor)
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

    /// <summary>True when a walker's next stride in <paramref name="dir"/> has no ground within
    /// stepping reach below the lead-foot line — the lip of a chasm or cave mouth. The probe
    /// allows a generous step-down (the suspension lowers the body down slopes) before calling
    /// it a cliff.</summary>
    private bool CliffAhead(Planet planet, Vector2 up, Vector2 right, int dir)
    {
        if (Legs.Length == 0) return false;
        var start = Position + right * (dir * (StanceHalf + StrideHalf + 46f)) + up * 40f;
        var maxDrop = 40f + Hover + 110f;   // body height above feet + slope allowance
        for (var d = 0f; d <= maxDrop; d += 8f)
            if (planet.IsSolidAt(start - up * d)) return false;
        return true;
    }

    /// <summary>Search for a foot anchor for one leg by ray-marching downward (along -planet-up)
    /// until a solid tile is found. The search column sits at the leg's neutral stance point
    /// (<see cref="StanceHalf"/> to its side of the pelvis) shifted by <paramref name="lead"/>
    /// along the tangent — the gait passes +StrideHalf in the walk direction so feet plant
    /// ahead of the body. The result is always clamped to <see cref="LegMaxReach"/> from
    /// the hip — over a cliff or pit the foot stops at full extension in mid-air rather than
    /// stretching, and the body suspension then lowers the body until the legs reach ground.</summary>
    private Vector2 ResolveFootAnchor(TitanLeg leg, Vector2 up, Vector2 right, float lead)
    {
        const float legSearchUp = 35f;
        const float legSearchDown = 360f;
        const float legProbeStep = 4f;

        var hipWorld = HipWorld(leg, up, right);
        var anchorStart = Position + right * (leg.Side * StanceHalf + lead)
                                   + up * (leg.HipUp + legSearchUp);

        var foot = anchorStart - up * legSearchDown;
        for (var d = 0f; d <= legSearchDown; d += legProbeStep)
        {
            var probe = anchorStart - up * d;
            if (_planet.IsSolidAt(probe))
            {
                foot = probe + _planet.UpAt(probe) * 5f;
                break;
            }
        }

        // Never plant beyond what the drawn leg bones can span (slightly inside full
        // extension so the knee keeps a visible bend even at max stride).
        var toFoot = foot - hipWorld;
        var reach = toFoot.Length();
        const float maxPlant = LegMaxReach * 0.97f;
        if (reach > maxPlant) foot = hipWorld + toFoot * (maxPlant / reach);
        return foot;
    }

    /// <summary>Per-frame leg simulation — a strided, alternating biped gait. While walking,
    /// the step anchor is thrown <see cref="StrideHalf"/> ahead of each leg's neutral point;
    /// a planted foot stays put while the body walks past it and only lifts once it has fallen
    /// a full stride behind that anchor, so each swing carries the foot forward past the other
    /// leg to plant ahead again. A leg may only lift while the other is planted (one foot is
    /// always down), except when overstretched — a foot about to rubber-band steps regardless.
    /// Standing still, a small drift threshold re-plants feet after turns or terrain changes.
    /// When a step lands, the tile under the foot takes damage via Planet.Mine — soft ground
    /// cracks visibly each stomp and breaks after a few; hard rock just gets cosmetic cracks.</summary>
    private void UpdateLegs(float dt, Planet planet, Physics physics, Cells cells, Vector2 up, Vector2 right, float vTangent)
    {
        var walking = MathF.Abs(vTangent) > 8f;
        var lead = walking ? MathF.Sign(vTangent) * StrideHalf : 0f;

        for (var i = 0; i < Legs.Length; i++)
        {
            var leg = Legs[i];
            var other = Legs[Legs.Length - 1 - i];
            var ideal = ResolveFootAnchor(leg, up, right, lead);

            if (leg.StepT >= 1f)
            {
                var hip = HipWorld(leg, up, right);
                var overstretched = (leg.FootPos - hip).LengthSquared()
                    > LegMaxReach * LegMaxReach * (0.92f * 0.92f);
                // Walking: lift once the foot has fallen a full stride behind the thrown-ahead
                // anchor. Standing: just re-plant after modest drift. Phase skews the trigger
                // per leg so a disturbed gait drifts back out of lockstep on its own.
                var trigger = walking ? StrideHalf * 2f * (0.9f + leg.Phase * 0.2f)
                                      : 26f + leg.Phase * 16f;
                var otherPlanted = other == leg || other.StepT >= 1f;
                if (overstretched
                    || (otherPlanted && (leg.FootPos - ideal).LengthSquared() > trigger * trigger))
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
                var stepRate = 2.2f + MathF.Abs(vTangent) * 0.014f;
                var prevT = leg.StepT;
                leg.StepT = MathF.Min(1f, leg.StepT + dt * stepRate);
                if (leg.StepT >= 1f && prevT < 1f)
                {
                    // Foot just touched down — stomp the tile. Damage falls off across a small
                    // radius so each step leaves a small footprint of cracked dirt rather than
                    // a single gouged tile. A dig slam additionally excavates a crater-layer.
                    leg.FootPos = leg.StepTarget;
                    StompTile(planet, physics, cells, leg.FootPos);
                    if (_digPending)
                    {
                        _digPending = false;
                        DigCrater(planet, physics, cells);
                    }
                }
                else
                {
                    var t = leg.StepT;
                    var smooth = t * t * (3f - 2f * t);
                    var pos = Vector2.Lerp(leg.StepStart, leg.StepTarget, smooth);
                    // Lift scales with the stride so a long step clears ground and a small
                    // re-plant shuffle doesn't high-kick; dig slams rear up extra high so the
                    // pounding reads from across the screen.
                    var lift = MathHelper.Clamp(
                        14f + (leg.StepTarget - leg.StepStart).Length() * 0.32f, 16f, 50f)
                        + (Digging ? 30f : 0f);
                    pos += planet.UpAt(pos) * (MathF.Sin(t * MathF.PI) * lift);
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
    /// in its place so the gap fills with tumbling dust rather than a clean hole.
    /// Aggro-gated entirely: a calm boss pacing the same stretch of surface was grinding its
    /// own patrol path into a pit and falling in — only a riled kaiju wrecks what it walks on.</summary>
    private void StompTile(Planet planet, Physics physics, Cells cells, Vector2 footPos)
    {
        if (!IsAggro) return;
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

    /// <summary>True when there's rock within a leg's reach on BOTH sides of the body — inside
    /// a dig shaft (including its wider crater-bowl bottom) or sealed in solid ground. Being
    /// braced is what lets the hunt-climb ascend: the kaiju chimneys between the walls (or
    /// plows through overburden) after prey above it, but has nothing to push on out in the
    /// open. Probes two heights per side so a ragged shaft wall still counts.</summary>
    private bool Braced(Planet planet, Vector2 up, Vector2 right)
    {
        var reach = BodyRadius + 64f;
        for (var s = -1; s <= 1; s += 2)
        {
            var side = Position + right * (s * reach);
            if (!planet.IsSolidAt(side) && !planet.IsSolidAt(side - up * 30f)) return false;
        }
        return true;
    }

    /// <summary>True when terrain rises above the body on BOTH flanks — the floor of a pit,
    /// dig shaft, or cavern rather than open ground. Sampled in columns to the sides, never
    /// through the body's own radial (a dug shaft is open straight up, so a radial surface
    /// probe reports "surface below" from the bottom of a hole). Gates the hunt-jump so the
    /// boss springs out of holes after airborne prey but doesn't pogo across open plains.</summary>
    private bool BelowLocalTerrain(Planet planet, Vector2 up, Vector2 right)
    {
        for (var s = -1; s <= 1; s += 2)
        {
            var col = Position + right * (s * 90f);
            var found = false;
            for (var d = 40f; d <= 320f; d += 12f)
                if (planet.IsSolidAt(col + up * d)) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    /// <summary>One dig-slam's excavation: pulverise a body-wide bowl beneath the pelvis so the
    /// feet can re-anchor deeper and the suspension follows them down. Tied to a landed stomp
    /// and paced by the dig timer — the boss has to pound its way down, it can't just sink.
    /// Mining power sits below the plow's so hard rock (granite, basalt) takes several slams
    /// per layer; anchored tiles stop the shaft cold.</summary>
    private void DigCrater(Planet planet, Physics physics, Cells cells)
    {
        var up = planet.UpAt(Position);
        var centre = Position - up * (Hover * 0.85f);
        var radius = BodyRadius + 12f;
        var (tx, _) = planet.WorldToTile(centre);
        var rel = centre - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var span = (int)MathF.Ceiling(radius / Planet.TileSize) + 1;
        var rSq = radius * radius;

        for (var dx = -span; dx <= span; dx++)
        {
            var x = tx + dx;
            if (x < 0 || x >= planet.Rings) continue;
            // Per-ring angular index, same as Plow — ring tile counts differ.
            var nRing = planet.TilesAt(x);
            var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
            for (var dy = -span; dy <= span; dy++)
            {
                var y = ty0 + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;
                if ((planet.TileToWorld(x, y) - centre).LengthSquared() > rSq) continue;
                if (planet.Mine(x, y, 18) is { } broken)
                {
                    physics.MarkDirty(x, y);
                    cells.SpawnDustInTile(x, y, broken);
                }
            }
        }
        physics.Earthquake(centre, radius + 30f, 2);
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
/// world-space and persists across frames — a foot stays planted while the body strides past
/// it, then swings ahead of the body to a new terrain-resolved anchor (see
/// <see cref="Titan.StrideHalf"/>; legs alternate so one foot is always down). Hip→foot
/// distance is capped at <see cref="Titan.LegMaxReach"/>: anchors clamp to reach and an
/// overstretched planted leg steps early, so legs compress and bend but never rubber-band.
/// The drawn leg is three-boned — hip→knee→ankle→toe (<see cref="Titan.AnkleLift"/>).</summary>
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
/// Laser is a fast bolt from the Mecha's mouth; Acid is a lofted glob that arcs under
/// gravity and bursts into live acid cells (Otachi's spit, the Vitriodactyl's rain); Spike
/// is a straight bolt from Slattern's tail fan; Lava is the Pyrodactyl's ballistic gout
/// that bursts into live lava cells where it lands.</summary>
public enum TitanShotKind { Flame, Laser, Acid, Spike, Lava }

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

    /// <summary>Contact damage to the player. Defaults to the Titan's per-kind tuning;
    /// creature-fired shots (acid spitters, crystal-crawler shards) pass a smaller value —
    /// they borrow the Titan's shot physics, not its boss-fight punch.</summary>
    public readonly float Damage;

    public TitanProjectile(Vector2 pos, Vector2 vel, TitanShotKind kind, float? damage = null)
    {
        Position = pos;
        Velocity = vel;
        Kind = kind;
        (Radius, Life) = kind switch
        {
            TitanShotKind.Flame => (9f, 0.85f),   // fatter, shorter-lived — fewer grains read as one gout
            TitanShotKind.Acid  => (6.5f, 3.4f),  // lofted glob — lives long enough to finish its arc
            TitanShotKind.Lava  => (7f, 3.4f),
            TitanShotKind.Spike => (4f, 1.6f),
            _                   => (4f, 0.9f),   // Laser
        };
        Damage = damage ?? kind switch
        {
            TitanShotKind.Flame => 9f,
            TitanShotKind.Acid  => 13f,
            TitanShotKind.Lava  => 15f,
            TitanShotKind.Spike => 16f,
            _                   => 28f,   // Laser
        };
        _drill = kind == TitanShotKind.Laser ? 3 : 0;
    }

    /// <summary>Acid and Lava globs are the ballistic shots — they loft, arc, and rain down,
    /// bursting into live cells of their material where they land.</summary>
    private bool Ballistic => Kind is TitanShotKind.Acid or TitanShotKind.Lava;

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Player player)
    {
        if (Ballistic)
            Velocity += planet.GravityAt(Position) * 240f * dt;
        Position += Velocity * dt;
        Life -= dt;
        if (Life <= 0f)
        {
            Dead = true;
            if (Ballistic) Splash(planet, cells);
            return;
        }

        // Player contact — Flame sears, Acid burns and splashes, Laser/Spike hit harder
        // and knock back.
        var diff = player.Position - Position;
        if (diff.Length() < Radius + player.Radius)
        {
            player.TakeDamage(Damage);
            switch (Kind)
            {
                case TitanShotKind.Acid:
                case TitanShotKind.Lava:
                    Splash(planet, cells);
                    break;
                case TitanShotKind.Spike:
                    if (diff.LengthSquared() > 0.0001f) player.Velocity += Vector2.Normalize(diff) * 160f;
                    break;
                case TitanShotKind.Laser:
                    if (diff.LengthSquared() > 0.0001f) player.Velocity += Vector2.Normalize(diff) * 200f;
                    break;
            }
            Dead = true;
            return;
        }

        if (planet.IsSolidAt(Position))
        {
            if (Ballistic)
            {
                Dead = true;
                Splash(planet, cells);
                return;
            }
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

    /// <summary>Burst the glob into live cells of its material just shy of the impact point
    /// (backed off along the incoming velocity so the splash lands in open air, not inside
    /// the wall). The cell sim takes it from there — acid pools flow and corrode, lava pools
    /// burn and glow, exactly like their worldgen-seeded kin.</summary>
    private void Splash(Planet planet, Cells cells)
    {
        var splashAt = Position;
        if (Velocity.LengthSquared() > 0.01f)
            splashAt -= Vector2.Normalize(Velocity) * (Planet.TileSize * 0.9f);
        var (tx, ty) = planet.WorldToTile(splashAt);
        cells.SpawnInTile(tx, ty, Kind == TitanShotKind.Lava ? Material.Lava : Material.Acid, 6);
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
