using System;
using System.Collections.Generic;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// The planet's bestiary. Three broad habitats, each with its own movement physics:
///   • Cave dwellers (Grub, Skitterer, MagmaSlug) — walkers: gravity toward the core,
///     tangent locomotion, tile collision.
///   • Tunnellers/explorers (Borer, Centipede and MoleBeast dig their own tunnels through
///     real tiles via Planet.Mine — debris crumbles into the cell sim and collapse checks
///     run, so their tunnels obey the same physics as player mining; the HornedDelver is a
///     walker that mines with a pickaxe to reach aggroed prey; CaveEye floats through
///     tunnels that already exist, steering along open space and never phasing through
///     walls).
///   • Surface fauna (Grazer, Hopper — passive walkers that flee) and sky fauna (SkyMoth
///     passive, SkyStinger dive-bomber) — flyers hold an altitude band, collide with
///     terrain, and climb over mountains rather than clipping them.
///   • Ambushers &amp; artillery (Noita/Terraria-school threats): CaveSlime bounds at prey and
///     splits when killed; AcidSpitter lobs ballistic acid globs into the cell sim;
///     BomberBeetle closes, arms, and detonates on any death; SnapperVine lunges on a rooted
///     tether; RockMimic plays boulder until poked; VoidWraith blinks; CrystalCrawler sprays
///     shards when shot.
///   • Biome fauna (SnowLoper … NullMoth): neutral signature species, one per planet
///     archetype, spawned by SpawnDirector off PlanetDef.Biome. They reuse the grazer /
///     hopper / flyer movement brains — the identity is in stats, silhouette and palette.
/// </summary>
public enum CreatureKind : byte
{
    Grub,         // classic cave chaser blob
    Skitterer,    // fast cave spider — pounces
    Borer,        // armoured rock-worm — chews its own tunnels
    CaveEye,      // floating eyeball — patrols existing tunnels
    MagmaSlug,    // deep-cave slug, cracked hide glows like a coal
    Grazer,       // placid six-legged surface herbivore, flees
    Hopper,       // tiny surface bouncer, harmless
    SkyMoth,      // pale high-altitude flyer, drifts in lazy orbits
    SkyStinger,   // territorial flyer — dives at the dwarf, then climbs away
    HornedDelver, // horned humanoid miner — swings a pickaxe, tunnels toward aggroed prey
    Centipede,    // long segmented tunneller — chews fast winding galleries
    MoleBeast,    // alien mole — digs burrows, shy unless cornered or provoked
    SporeBat,     // fungal-grove flitter — frail, chokes its killer with a spore puff
    CrystalCrawler, // armoured deep-cave tank — slow, hits hard, sprays shards when shot
    VoidWraith,   // Rift-only phantom — fast, vicious, blinks toward prey, sheds voidstone
    CaveSlime,    // gelatinous hopper — bounds at the dwarf, splits in two when killed
    Slimelet,     // half-size split product of a cave slime; never spawns on its own
    AcidSpitter,  // squat gland-toad — keeps its distance and lobs acid globs
    BomberBeetle, // volatile scuttler — closes in, arms its abdomen, and detonates
    SnapperVine,  // rooted lunge-plant — strikes anything that drifts inside its tether
    RockMimic,    // ore-speckled "boulder" that wakes when prodded or approached
    // Biome fauna (all neutral): every planet archetype keeps its own signature species —
    // SpawnDirector rolls these off PlanetDef.Biome so no two world types share a herd.
    SnowLoper,    // frost — stilt-legged woolly strider, plods the ice fields
    CinderSkink,  // ember — ember-freckled lizard basking on the basalt
    RustBack,     // slag — dome-shelled grazer plated in oxidised scrap
    TidePuddler,  // ocean — glossy amphibian that flops along the shorelines
    AcidStrider,  // acid — long-legged wader, sips the vitriol pools unharmed
    PrismSnail,   // crystal — crystalline-shelled snail with a faint gem glow
    NullMoth,     // rift — void-black flyer, the one gentle thing out there
    // Civilisation (city worlds + lizard warrens):
    Civilian,     // city — timid alien citizen, ambles the streets and towers, flees trouble
    Lizardman,    // warren guard — evil lizardman warrior: patrols, hurls bone spears, lunges
}

public sealed class Creature
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 4f;
    public float Health = 12f;
    public float MoveSpeed = 55f;
    public float ContactDamage = 8f;
    public float HitFlash;
    public float Wander;
    public readonly CreatureKind Kind;
    /// <summary>False for fauna (Grazer/Hopper/SkyMoth): they never deal contact damage and
    /// sentries won't waste shots on them. The player can still hit them with anything.</summary>
    public bool Hostile = true;

    /// <summary>Burning debuff. While > 0, ticks ~3 HP per second and emits ember light. Set
    /// by ruby cannon shells / incendiary hits. Decays linearly with dt.</summary>
    public float BurnSeconds;

    /// <summary>Freezing debuff. While > 0, MoveSpeed is halved and the creature is rendered
    /// with a pale-blue tint. Set by sapphire cannon shells.</summary>
    public float FreezeSeconds;

    // --- Per-kind scratch state ---
    private float _cd;             // multi-use cooldown: pounce / hop / stinger-retreat
    private float _retarget;       // seconds until the Borer re-picks a dig heading
    private int _amble;            // Grazer stroll direction: -1 / 0 / +1
    private float _heading;        // CaveEye flight heading, world-space radians
    private Vector2 _digDir = Vector2.UnitX;
    private float _prefAlt;        // flyers: preferred distance from planet centre (px)
    private int _orbitSign = 1;    // flyers: orbit direction around the planet
    private readonly float _phase; // per-creature animation phase offset
    private float _aggroT;         // HornedDelver: seconds of aggro memory remaining
    private float _swing;          // HornedDelver: pickaxe swing / spit-maw / blink-shimmer timer
    private float _provokedT;      // MoleBeast: seconds of rage remaining after being hit
    private float _fuse;           // BomberBeetle: armed-detonation countdown (0 = not armed)
    private Vector2 _root;         // SnapperVine: anchor point it is tethered to
    private bool _rooted;          // SnapperVine: anchor captured on first tick
    private bool _awake;           // RockMimic: dropped the boulder disguise
    // Centipede body: breadcrumb ring buffer of past head positions; segments sit along the
    // trail so the body snakes through the exact tunnel the head chewed, never through rock.
    private const int CrumbCount = 64;
    private const float CrumbSpacing = 2f;
    private const int SegCount = 8;
    private Vector2[]? _crumbs;
    private int _crumbHead;

    public Creature(Vector2 pos, CreatureKind kind = CreatureKind.Grub)
    {
        Position = pos;
        Kind = kind;
        _phase = (float)Random.Shared.NextDouble() * MathF.Tau;
        _heading = (float)Random.Shared.NextDouble() * MathF.Tau;
        _orbitSign = Random.Shared.Next(2) == 0 ? 1 : -1;

        switch (kind)
        {
            case CreatureKind.Grub:
                break; // defaults above are the classic grub stats
            case CreatureKind.Skitterer:
                Radius = 3f; Health = 7f; MoveSpeed = 85f; ContactDamage = 6f;
                break;
            case CreatureKind.Borer:
                Radius = 4.5f; Health = 30f; MoveSpeed = 22f; ContactDamage = 10f;
                break;
            case CreatureKind.CaveEye:
                Radius = 4f; Health = 10f; MoveSpeed = 34f; ContactDamage = 7f;
                break;
            case CreatureKind.MagmaSlug:
                Radius = 5f; Health = 40f; MoveSpeed = 18f; ContactDamage = 16f;
                break;
            case CreatureKind.Grazer:
                Radius = 4.5f; Health = 18f; MoveSpeed = 30f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.Hopper:
                Radius = 2.5f; Health = 6f; MoveSpeed = 40f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.SkyMoth:
                Radius = 3.5f; Health = 8f; MoveSpeed = 26f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.SkyStinger:
                Radius = 3.5f; Health = 10f; MoveSpeed = 40f; ContactDamage = 9f;
                break;
            case CreatureKind.HornedDelver:
                Radius = 4f; Health = 28f; MoveSpeed = 46f; ContactDamage = 12f;
                break;
            case CreatureKind.Centipede:
                Radius = 3.2f; Health = 26f; MoveSpeed = 30f; ContactDamage = 9f;
                _crumbs = new Vector2[CrumbCount];
                for (var i = 0; i < CrumbCount; i++) _crumbs[i] = pos;
                break;
            case CreatureKind.MoleBeast:
                Radius = 4.2f; Health = 22f; MoveSpeed = 26f; ContactDamage = 8f;
                break;
            case CreatureKind.SporeBat:
                Radius = 2.8f; Health = 6f; MoveSpeed = 44f; ContactDamage = 4f;
                break;
            case CreatureKind.CrystalCrawler:
                Radius = 5f; Health = 45f; MoveSpeed = 14f; ContactDamage = 13f;
                break;
            case CreatureKind.VoidWraith:
                Radius = 3.8f; Health = 20f; MoveSpeed = 62f; ContactDamage = 15f;
                _cd = 1f + (float)Random.Shared.NextDouble() * 1.5f; // first blink is never instant
                break;
            case CreatureKind.CaveSlime:
                Radius = 4.5f; Health = 16f; MoveSpeed = 55f; ContactDamage = 7f;
                break;
            case CreatureKind.Slimelet:
                Radius = 2.4f; Health = 5f; MoveSpeed = 65f; ContactDamage = 4f;
                break;
            case CreatureKind.AcidSpitter:
                Radius = 4.2f; Health = 18f; MoveSpeed = 28f; ContactDamage = 6f;
                break;
            case CreatureKind.BomberBeetle:
                Radius = 3.6f; Health = 12f; MoveSpeed = 70f; ContactDamage = 5f;
                break;
            case CreatureKind.SnapperVine:
                Radius = 3.5f; Health = 24f; MoveSpeed = 130f; ContactDamage = 12f;
                break;
            case CreatureKind.RockMimic:
                // Plays dead: Hostile stays off until it wakes so sentries ignore the
                // "boulder" and the ambush actually lands.
                Radius = 5f; Health = 60f; MoveSpeed = 58f; ContactDamage = 14f; Hostile = false;
                break;
            case CreatureKind.SnowLoper:
                Radius = 5f; Health = 22f; MoveSpeed = 26f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.CinderSkink:
                Radius = 3f; Health = 10f; MoveSpeed = 55f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.RustBack:
                Radius = 5f; Health = 32f; MoveSpeed = 12f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.TidePuddler:
                Radius = 3f; Health = 8f; MoveSpeed = 42f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.AcidStrider:
                Radius = 4.5f; Health = 14f; MoveSpeed = 34f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.PrismSnail:
                Radius = 3.5f; Health = 26f; MoveSpeed = 7f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.NullMoth:
                Radius = 3.5f; Health = 9f; MoveSpeed = 30f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.Civilian:
                Radius = 3.5f; Health = 12f; MoveSpeed = 42f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.Lizardman:
                Radius = 4f; Health = 30f; MoveSpeed = 58f; ContactDamage = 12f;
                _cd = 0.8f + (float)Random.Shared.NextDouble(); // first spear is never instant
                break;
        }
    }

    public bool IsSkyKind => Kind is CreatureKind.SkyMoth or CreatureKind.SkyStinger
        or CreatureKind.NullMoth;
    public bool IsSurfaceKind => Kind is CreatureKind.Grazer or CreatureKind.Hopper
        or CreatureKind.SnowLoper or CreatureKind.CinderSkink or CreatureKind.RustBack
        or CreatureKind.TidePuddler or CreatureKind.AcidStrider or CreatureKind.PrismSnail
        or CreatureKind.Civilian;
    public bool IsCaveKind => !IsSkyKind && !IsSurfaceKind;

    /// <summary><paramref name="shots"/> is the shared enemy-shot list (the Titan's): ranged
    /// creatures (acid spitters, crystal crawlers) add their projectiles to it and the existing
    /// shot update/draw path handles the rest. Null (headless tests) just disables spitting.</summary>
    public void Update(float dt, Planet planet, Physics physics, Cells cells, Player player,
        List<TitanProjectile>? shots = null)
    {
        // Status effect tick: burn drains HP per-second; freeze halves move speed.
        if (BurnSeconds > 0f)
        {
            Health -= 3f * dt;
            BurnSeconds -= dt;
        }
        if (FreezeSeconds > 0f) FreezeSeconds -= dt;
        // A shot mole holds a grudge — the pain flash doubles as the provocation signal.
        if (HitFlash > 0f && Kind == CreatureKind.MoleBeast) _provokedT = 8f;
        if (_provokedT > 0f) _provokedT -= dt;
        if (HitFlash > 0) HitFlash -= dt;
        var speedMul = FreezeSeconds > 0f ? 0.5f : 1.0f;

        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        var toPlayer = player.Position - Position;
        var dist = toPlayer.Length();

        switch (Kind)
        {
            case CreatureKind.Grub:       TickGrub(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Skitterer:  TickSkitterer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.MagmaSlug:  TickMagmaSlug(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Grazer:     TickGrazer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Hopper:     TickHopper(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Borer:      TickBorer(dt, planet, physics, cells, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.CaveEye:    TickCaveEye(dt, planet, toPlayer, dist, speedMul); break;
            case CreatureKind.SkyMoth:
            case CreatureKind.SkyStinger: TickFlyer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.HornedDelver: TickDelver(dt, planet, physics, cells, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Centipede:  TickCentipede(dt, planet, physics, cells, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.MoleBeast:  TickMole(dt, planet, physics, cells, up, right, toPlayer, dist, speedMul); break;
            // Spore bats patrol tunnels on the proven cave-eye brain — the stats make them
            // feel nothing alike.
            case CreatureKind.SporeBat: TickCaveEye(dt, planet, toPlayer, dist, speedMul, aggro: 150f); break;
            case CreatureKind.VoidWraith: TickWraith(dt, planet, toPlayer, dist, speedMul); break;
            case CreatureKind.CrystalCrawler: TickCrawler(dt, planet, up, right, toPlayer, dist, speedMul, shots); break;
            case CreatureKind.CaveSlime:
            case CreatureKind.Slimelet:   TickSlime(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.AcidSpitter: TickSpitter(dt, planet, up, right, toPlayer, dist, speedMul, shots); break;
            case CreatureKind.BomberBeetle: TickBomber(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.SnapperVine: TickVine(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.RockMimic:  TickMimic(dt, planet, up, right, toPlayer, dist, speedMul); break;
            // Biome fauna ride the proven ambience brains — grazer amble/flee, hopper
            // bounce, moth orbit — with per-species stats doing the differentiating.
            case CreatureKind.SnowLoper:
            case CreatureKind.CinderSkink:
            case CreatureKind.RustBack:
            case CreatureKind.AcidStrider:
            case CreatureKind.PrismSnail: TickGrazer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.TidePuddler: TickHopper(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.NullMoth:   TickFlyer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            // Citizens amble and bolt on the grazer brain; the identity is in the sprite.
            case CreatureKind.Civilian:   TickGrazer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Lizardman:  TickLizardman(dt, planet, up, right, toPlayer, dist, speedMul, shots); break;
        }

        // Substepped integration: each step moves at most ~60% of the body radius so a fast
        // fall or pounce can never tunnel through a tile between collision checks.
        var moveLen = Velocity.Length() * dt;
        var substeps = Math.Clamp((int)MathF.Ceiling(moveLen / MathF.Max(Radius * 0.6f, 1.5f)), 1, 8);
        var subDt = dt / substeps;
        for (var s = 0; s < substeps; s++)
        {
            Position += Velocity * subDt;
            ResolveTileCollision(planet);
        }

        // Drop breadcrumbs after the head's final position is known so the centipede's body
        // threads the tunnel the head actually took.
        if (_crumbs is not null)
        {
            while ((Position - _crumbs[_crumbHead]).LengthSquared() >= CrumbSpacing * CrumbSpacing)
            {
                var prev = _crumbs[_crumbHead];
                var step = Position - prev;
                _crumbHead = (_crumbHead + 1) % CrumbCount;
                _crumbs[_crumbHead] = prev + Vector2.Normalize(step) * CrumbSpacing;
            }
        }

        // Damage player on contact — hostile kinds only. Fauna just bumps past.
        if (Hostile && ContactDamage > 0f)
        {
            var diff = player.Position - Position;
            if (diff.Length() < Radius + player.Radius)
            {
                player.TakeDamage(ContactDamage * dt);
                if (diff.LengthSquared() > 0.0001f)
                {
                    var n = Vector2.Normalize(diff);
                    player.Velocity += n * 60f;
                }
                // A stinger that connects breaks off and climbs back to altitude.
                if (Kind == CreatureKind.SkyStinger) _cd = 2.2f;
            }

            // Centipede body segments sting too — brushing the trailing coils hurts, at half
            // the head's bite. Checked on every other segment to keep it cheap.
            if (_crumbs is not null)
            {
                for (var s = 2; s <= SegCount; s += 2)
                {
                    var seg = SegPos(s);
                    var d = player.Position - seg;
                    if (d.Length() < Radius + player.Radius)
                    {
                        player.TakeDamage(ContactDamage * 0.5f * dt);
                        break;
                    }
                }
            }
        }
    }

    private Vector2 SegPos(int i) =>
        _crumbs![((_crumbHead - i * 2) % CrumbCount + CrumbCount) % CrumbCount];

    // ---------------------------------------------------------------- cave walkers

    // Hunt ranges are deliberately larger than the spawn donut's inner edge (200px): a
    // creature the director places just off-screen must be able to *sense* the dwarf and
    // come looking, or the local population never turns into encounters.
    private void TickGrub(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        float moveAxis;
        if (dist < 260f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 1.5f + (float)Random.Shared.NextDouble() * 2f;
            moveAxis = MathF.Sin(Wander * 3f);
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    private void TickSkitterer(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        _cd -= dt;
        // Pounce: close, grounded, off cooldown → leap straight at the dwarf.
        if (dist < 55f && dist > 0.01f && _cd <= 0f && IsGrounded(planet, up))
        {
            Velocity = toPlayer / dist * 110f * speedMul + up * 70f;
            _cd = 1.4f;
            return;
        }
        float moveAxis;
        if (dist < 240f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 0.8f + (float)Random.Shared.NextDouble() * 1.4f;
            moveAxis = MathF.Sin(Wander * 5f);
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    private void TickMagmaSlug(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        float moveAxis;
        if (dist < 170f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 3f + (float)Random.Shared.NextDouble() * 3f;
            moveAxis = MathF.Sin(Wander * 1.5f) * 0.5f;
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    // ---------------------------------------------------------------- surface fauna

    private void TickGrazer(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        Wander -= dt;
        if (Wander <= 0)
        {
            Wander = 2f + (float)Random.Shared.NextDouble() * 3f;
            _amble = Random.Shared.Next(3) - 1; // stroll left / graze in place / stroll right
        }
        var moveAxis = _amble * 0.5f;
        // Spooked: bolt directly away from the player, faster than the amble cap.
        if (dist < 70f) moveAxis = -MathF.Sign(Vector2.Dot(toPlayer, right)) * 1.6f;
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    private void TickHopper(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        _cd -= dt;
        if (IsGrounded(planet, up))
        {
            if (_cd <= 0f)
            {
                _cd = 1.1f + (float)Random.Shared.NextDouble() * 1.6f;
                var s = dist < 60f
                    ? -MathF.Sign(Vector2.Dot(toPlayer, right))          // hop away from danger
                    : (Random.Shared.Next(2) == 0 ? 1f : -1f);           // hop wherever
                Velocity = right * (s * 45f * speedMul) + up * 105f;
                return;
            }
            // Resting between hops: bleed tangent speed, keep gravity glue.
            var vT = MoveToward(Vector2.Dot(Velocity, right), 0f, 300f * dt);
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        else
        {
            // Mid-hop: ballistic, with the shared terminal-velocity cap.
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * Vector2.Dot(Velocity, right) + up * vN;
        }
    }

    // ---------------------------------------------------------------- tunnellers

    /// <summary>Borer: eats through rock. It only "flies" through stone by *removing* the
    /// stone — Planet.Mine chips the tile over several bites, and on break the debris enters
    /// the cell sim and Physics gets a dirty-mark, so borer tunnels crumble, cave in and
    /// spill dust exactly like player-dug ones. In open air with no wall to grip it falls.</summary>
    private void TickBorer(float dt, Planet planet, Physics physics, Cells cells,
        Vector2 up, Vector2 right, Vector2 toPlayer, float dist, float speedMul)
    {
        _retarget -= dt;
        if (dist < 180f && dist > 0.01f)
        {
            _digDir = toPlayer / dist; // smells the dwarf through rock
        }
        else if (_retarget <= 0f)
        {
            _retarget = 4f + (float)Random.Shared.NextDouble() * 4f;
            _digDir = PickDigHeading(planet, right, 1.6f);
        }

        // Grip: with any solid tile in the surrounding 3×3 it clings and inches along its
        // dig heading; in open space it's just a heavy worm — gravity wins.
        var gripping = AnySolidNear(planet);
        if (gripping)
        {
            Velocity = Vector2.Lerp(Velocity, _digDir * MoveSpeed * speedMul, MathF.Min(1f, 6f * dt));
        }
        else
        {
            var vT = MoveToward(Vector2.Dot(Velocity, right), 0f, 100f * dt);
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * vT + up * vN;
        }

        // Chew the tile ahead of the snout.
        Chew(dt, planet, physics, cells, _digDir, 0.22f, 6);
    }

    /// <summary>Shared excavation bite for every digger (Borer, Centipede, MoleBeast, and the
    /// HornedDelver's pickaxe). Chips a disc of tiles around the first solid contact along
    /// <paramref name="dir"/> (or around the leading edge when the ray finds nothing) through
    /// the same physics path as player mining: Planet.Mine damage, dirty-mark on break so
    /// collapse checks run, and debris spilled into the cell sim. Anchor-class tiles (core,
    /// supports) refuse the bite and, with nothing else to chew, force a heading re-pick.
    /// Pass null to just tick the cooldown without biting.</summary>
    private void Chew(float dt, Planet planet, Physics physics, Cells cells,
        Vector2? dir, float interval, int power, float reach = 0f)
    {
        _cd -= dt;
        if (dir is not { } d || _cd > 0f) return;
        if (reach <= 0f) reach = Radius + 12f; // worms: snout contact plus ~1.5 tiles
        // Scan along the heading in half-tile steps for the first solid contact. If the ray
        // threads open air (glancing corner contact), fall back to the leading edge so the
        // area bite below still clears whatever is clipping the body.
        var centre = Position + d * (Radius + 3f);
        for (var distAlong = Radius + 3f; distAlong <= reach; distAlong += Planet.TileSize * 0.5f)
        {
            var probe = Position + d * distAlong;
            if (planet.IsSolidAt(probe)) { centre = probe; break; }
        }

        // Bite an area, not a single tile: chip every mineable tile whose centre falls within
        // the bite disc. The disc spans the body cross-section plus the adjacent corner tiles,
        // so a digger can't be pinned by a block a single-tile probe would miss.
        var biteR = Radius + Planet.TileSize * 1.8f;
        var relC = centre - planet.Center;
        var ang = MathF.Atan2(relC.Y, relC.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var (cx, _) = planet.WorldToTile(centre);
        var bit = false;
        var blocked = false;
        var span = (int)(biteR / Planet.TileSize) + 1;
        for (var dx = -span; dx <= span; dx++)
        {
            var x = cx + dx;
            if (x < 0 || x >= planet.Rings) continue;
            // Angular index recomputed per ring — rings have different tile counts.
            var ty0 = (int)(ang / MathHelper.TwoPi * planet.TilesAt(x));
            for (var dy = -span; dy <= span; dy++)
            {
                var y = ty0 + dy;
                var k = planet.Get(x, y);
                if (!Tiles.IsSolid(k)) continue;
                if ((planet.TileToWorld(x, y) - centre).LengthSquared() > biteR * biteR) continue;
                if (Tiles.Hardness(k) >= 90) { blocked = true; continue; } // core / support beam
                if (planet.Mine(x, y, power) is { } broken)
                {
                    physics.MarkDirty(x, y);
                    cells.SpawnDustInTile(x, y, broken);
                }
                bit = true;
            }
        }
        if (bit)
        {
            _cd = interval;
            _swing = 0.3f;
        }
        else if (blocked)
        {
            _retarget = 0f; // only unbiteable anchors ahead — bounce off, pick a new heading
        }
    }

    /// <summary>HornedDelver: a pick-swinging humanoid miner. Walks and hops like the dwarf;
    /// once the player enters its aggro radius it holds a grudge for several seconds (memory
    /// survives losing sight) and mines whatever separates them — clearing walls ahead,
    /// sinking straight shafts when the prey is below, cutting diagonal stairs when above.
    /// Off aggro it patrols its gallery and keeps extending it rather than turning back.</summary>
    private void TickDelver(float dt, Planet planet, Physics physics, Cells cells,
        Vector2 up, Vector2 right, Vector2 toPlayer, float dist, float speedMul)
    {
        if (_swing > 0f) _swing -= dt;
        if (dist < 220f) _aggroT = 6f; else _aggroT -= dt;

        if (_aggroT > 0f)
        {
            var tDist = Vector2.Dot(toPlayer, right);
            var nDist = Vector2.Dot(toPlayer, up);
            var moveAxis = MathF.Abs(tDist) > 6f ? MathF.Sign(tDist) : 0f;
            GroundMove(dt, planet, up, right, moveAxis, speedMul);

            // Choose what to mine: mostly-vertical separation digs a shaft (straight down,
            // or a diagonal staircase when there's sideways ground to cover), otherwise
            // clear the wall in the walking direction.
            Vector2? digDir = null;
            if (MathF.Abs(nDist) > 10f && MathF.Abs(nDist) > MathF.Abs(tDist) * 0.7f)
            {
                var vert = up * MathF.Sign(nDist);
                digDir = MathF.Abs(tDist) > 6f
                    ? Vector2.Normalize(right * (MathF.Sign(tDist) * 0.7f) + vert)
                    : vert;
            }
            else if (moveAxis != 0f && planet.IsSolidAt(Position + right * (moveAxis * (Radius + 4f))))
            {
                digDir = right * moveAxis;
            }
            // Pickaxe reach matches the player's mine range, so a delver can clear a
            // ceiling two tiles overhead the same way a dwarf would.
            Chew(dt, planet, physics, cells, digDir, 0.38f, 9, reach: 22f);

            // Scramble upward into headroom it just cut so staircase digs actually ascend.
            if (nDist > 10f && MathF.Abs(tDist) < 30f && IsGrounded(planet, up)
                && !planet.IsSolidAt(Position + up * (Radius + 5f)))
            {
                Velocity += up * 130f;
            }
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0f)
            {
                Wander = 2.5f + (float)Random.Shared.NextDouble() * 3.5f;
                _amble = Random.Shared.Next(3) - 1;
            }
            GroundMove(dt, planet, up, right, _amble, speedMul);
            // A patrolling delver blocked by rock keeps extending its gallery.
            if (_amble != 0 && planet.IsSolidAt(Position + right * (_amble * (Radius + 4f))))
                Chew(dt, planet, physics, cells, right * _amble, 0.55f, 6, reach: 12f);
        }
    }

    /// <summary>Centipede: a long segmented tunneller. The head digs like a fast borer but its
    /// idle headings *rotate* rather than reset, so its galleries curve and meander. The body
    /// follows the breadcrumb trail laid by the head — always inside the tunnel it chewed.</summary>
    private void TickCentipede(float dt, Planet planet, Physics physics, Cells cells,
        Vector2 up, Vector2 right, Vector2 toPlayer, float dist, float speedMul)
    {
        _retarget -= dt;
        if (dist < 200f && dist > 0.01f)
        {
            _digDir = toPlayer / dist;
        }
        else if (_retarget <= 0f)
        {
            _retarget = 3f + (float)Random.Shared.NextDouble() * 3f;
            // Curve the existing heading (winding galleries), preferring a bend that meets rock.
            var best = _digDir;
            for (var i = 0; i < 5; i++)
            {
                best = Rotate(_digDir, ((float)Random.Shared.NextDouble() - 0.5f) * 2.2f);
                if (planet.IsSolidAt(Position + best * (Radius + 6f))) break;
            }
            _digDir = best;
        }

        if (AnySolidNear(planet))
        {
            Velocity = Vector2.Lerp(Velocity, _digDir * MoveSpeed * speedMul, MathF.Min(1f, 7f * dt));
        }
        else
        {
            var vT = MoveToward(Vector2.Dot(Velocity, right), 0f, 100f * dt);
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        Chew(dt, planet, physics, cells, _digDir, 0.15f, 6);
    }

    /// <summary>MoleBeast: an alien mole that mostly wants nothing to do with you. It chews
    /// shallow, mostly-level burrows; a dwarf crowding it makes it dig *away*, and it only
    /// turns hostile at point-blank range or after taking a hit (8s grudge).</summary>
    private void TickMole(float dt, Planet planet, Physics physics, Cells cells,
        Vector2 up, Vector2 right, Vector2 toPlayer, float dist, float speedMul)
    {
        _retarget -= dt;
        var angry = _provokedT > 0f || dist < 30f;
        if (angry && dist > 0.01f)
        {
            _digDir = toPlayer / dist;
        }
        else if (dist < 70f && dist > 0.01f)
        {
            _digDir = -toPlayer / dist; // crowded — burrow away from the noise
        }
        else if (_retarget <= 0f)
        {
            _retarget = 4f + (float)Random.Shared.NextDouble() * 4f;
            _digDir = PickDigHeading(planet, right, 0.9f);
        }

        var haste = angry ? 1.5f : 1f;
        if (AnySolidNear(planet))
        {
            Velocity = Vector2.Lerp(Velocity, _digDir * MoveSpeed * haste * speedMul, MathF.Min(1f, 6f * dt));
        }
        else
        {
            var vT = MoveToward(Vector2.Dot(Velocity, right), 0f, 100f * dt);
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        // Big claws — digs faster than a borer bites.
        Chew(dt, planet, physics, cells, _digDir, 0.2f, 9);
    }

    /// <summary>CaveEye: hovers through open tunnels. Never digs — it steers by probing ahead
    /// and turning toward open space, so it genuinely explores whatever tunnel network exists
    /// (natural caves, player shafts, borer galleries).</summary>
    private void TickCaveEye(float dt, Planet planet, Vector2 toPlayer, float dist, float speedMul,
        float aggro = 200f)
    {
        // Idle drift wobbles the heading; a nearby dwarf pulls it.
        _heading += ((float)Random.Shared.NextDouble() - 0.5f) * 2.4f * dt;
        var speed = MoveSpeed;
        if (dist < aggro && dist > 0.01f)
        {
            var want = MathF.Atan2(toPlayer.Y, toPlayer.X);
            _heading += WrapPi(want - _heading) * MathF.Min(1f, 3.5f * dt);
            speed = 60f;
        }

        // Wall avoidance: probe ahead; if blocked, swing toward whichever side is open.
        var dir = new Vector2(MathF.Cos(_heading), MathF.Sin(_heading));
        if (planet.IsSolidAt(Position + dir * (Radius + 7f)))
        {
            var lDir = Rotate(dir, 0.9f);
            var rDir = Rotate(dir, -0.9f);
            var lOpen = !planet.IsSolidAt(Position + lDir * (Radius + 7f));
            var rOpen = !planet.IsSolidAt(Position + rDir * (Radius + 7f));
            if (lOpen && !rOpen) _heading += 3.2f * dt;
            else if (rOpen && !lOpen) _heading -= 3.2f * dt;
            else if (!lOpen) _heading += MathF.PI * 0.6f; // dead end — swing around hard
            dir = new Vector2(MathF.Cos(_heading), MathF.Sin(_heading));
            speed *= 0.5f;
        }

        Velocity = Vector2.Lerp(Velocity, dir * speed * speedMul, MathF.Min(1f, 4f * dt));
    }

    // ---------------------------------------------------------------- ambushers & artillery

    /// <summary>VoidWraith: patrols like a cave eye, but every few seconds it *blinks* — a
    /// short teleport toward nearby prey through open space. It never phases through rock
    /// (the blink is refused if the destination is solid), it just stops being where your
    /// crosshair was.</summary>
    private void TickWraith(float dt, Planet planet, Vector2 toPlayer, float dist, float speedMul)
    {
        TickCaveEye(dt, planet, toPlayer, dist, speedMul, aggro: 220f);
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;
        if (_cd <= 0f && dist < 200f && dist > 40f)
        {
            var hop = toPlayer / dist * MathF.Min(46f, dist - 24f);
            if (!planet.IsSolidAt(Position + hop))
            {
                Position += hop;
                _heading = MathF.Atan2(toPlayer.Y, toPlayer.X); // arrive already facing prey
                _swing = 0.35f;                                 // afterimage shimmer
                _cd = 2.2f + (float)Random.Shared.NextDouble() * 1.2f;
            }
            else
            {
                _cd = 0.6f; // wall in the way — retry once it has drifted somewhere open
            }
        }
    }

    /// <summary>CrystalCrawler: stalks like a grub, but shooting it is a mistake at close
    /// range — each hit that lands (off an internal cooldown) shatters part of its back into
    /// a radial spray of crystal shards.</summary>
    private void TickCrawler(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        TickGrub(dt, planet, up, right, toPlayer, dist, speedMul);
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;
        // HitFlash is set to 0.15 the frame a hit lands and decays with dt, so > 0.12
        // reads "was hit within the last frame or two".
        if (HitFlash > 0.12f && _cd <= 0f && shots is not null)
        {
            _cd = 1.6f;
            _swing = 0.3f;
            for (var i = 0; i < 6; i++)
            {
                var a = _phase + i * (MathF.Tau / 6f);
                var dir = Rotate(up, a);
                shots.Add(new TitanProjectile(Position + dir * (Radius + 2f), dir * 170f,
                    TitanShotKind.Spike, damage: 6f));
            }
        }
    }

    /// <summary>CaveSlime / Slimelet: a hopper with intent. Grounded and off cooldown it
    /// bounds toward the dwarf (higher arc when the prey is above); between hops it sits and
    /// jiggles. Killing a full-size slime splits it into two slimelets — handled at the death
    /// site in Game1, since a creature can't add to the list it lives in.</summary>
    private void TickSlime(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        _cd -= dt;
        if (IsGrounded(planet, up))
        {
            if (_cd <= 0f)
            {
                var small = Kind == CreatureKind.Slimelet;
                _cd = (small ? 0.7f : 1.0f) + (float)Random.Shared.NextDouble() * 0.7f;
                var s = dist < 260f
                    ? MathF.Sign(Vector2.Dot(toPlayer, right))           // bound at the dwarf
                    : (Random.Shared.Next(2) == 0 ? 1f : -1f);           // idle wandering hop
                // Leap higher when the prey is above — slimes climb ledges by committing.
                var lift = dist < 260f && Vector2.Dot(toPlayer, up) > 15f ? 150f : 110f;
                Velocity = right * (s * MoveSpeed * speedMul) + up * (small ? lift * 0.8f : lift);
                return;
            }
            var vT = MoveToward(Vector2.Dot(Velocity, right), 0f, 300f * dt);
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        else
        {
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - 320f * dt, -260f);
            Velocity = right * Vector2.Dot(Velocity, right) + up * vN;
        }
    }

    /// <summary>AcidSpitter: cave artillery. It shuffles to hold a comfortable band — backing
    /// off when crowded — and lobs ballistic acid globs at anything it can see. The globs are
    /// TitanShotKind.Acid at spitter damage: they arc, burst into live acid cells, and the
    /// cell sim does the area denial.</summary>
    private void TickSpitter(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;

        float moveAxis;
        if (dist < 60f)
        {
            moveAxis = -MathF.Sign(Vector2.Dot(toPlayer, right)); // too close — waddle away
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 2.5f + (float)Random.Shared.NextDouble() * 3f;
            moveAxis = MathF.Sin(Wander * 1.8f) * 0.4f;
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);

        if (dist < 185f && dist > 0.01f && _cd <= 0f && shots is not null
            && HasLineOfSight(planet, toPlayer, dist))
        {
            // Lofted lead: aim above the straight line, more loft with range, so the
            // gravity-pulled glob comes down on the target instead of undershooting.
            var dir = toPlayer / dist;
            var aim = Vector2.Normalize(dir + up * (0.2f + dist * 0.0022f));
            shots.Add(new TitanProjectile(Position + aim * (Radius + 2f), aim * 155f,
                TitanShotKind.Acid, damage: 8f));
            _cd = 2.4f + (float)Random.Shared.NextDouble() * 0.8f;
            _swing = 0.4f; // maw-open animation
        }
    }

    /// <summary>BomberBeetle: a fast scuttler with a volatile abdomen. It sprints at the
    /// dwarf; at arm's length it stops, arms (rapid flashing), and detonates. Any death —
    /// fuse-out or gunned down mid-charge — explodes it (see the death handler in Game1),
    /// so range is the counter and packed corridors are a chain reaction waiting to happen.</summary>
    private void TickBomber(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        if (_fuse > 0f)
        {
            _fuse -= dt;
            GroundMove(dt, planet, up, right, 0f, speedMul); // plants its feet and cooks
            if (_fuse <= 0f) Health = 0f;                    // death handler does the blast
            return;
        }
        if (dist < 26f)
        {
            _fuse = 0.7f;
            return;
        }
        float moveAxis;
        if (dist < 260f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 1f + (float)Random.Shared.NextDouble() * 1.5f;
            moveAxis = MathF.Sin(Wander * 4f);
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    /// <summary>SnapperVine: a lunge-plant rooted where it sprouted. The head sways on its
    /// stalk until something edible drifts inside range, then whips at it — but it can never
    /// leave its tether, so the counter is simply staying a step outside the circle (or
    /// shooting the head off from there).</summary>
    private void TickVine(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        if (!_rooted)
        {
            _root = Position;
            _rooted = true;
        }
        const float tether = 52f;
        Wander += dt;

        Vector2 desired;
        if (dist < 120f && dist > 0.01f)
        {
            desired = toPlayer / dist * MoveSpeed * speedMul; // strike
        }
        else
        {
            // Idle: hover above the root, swaying.
            var rest = _root + up * 16f + right * (MathF.Sin(Wander * 1.4f + _phase) * 9f);
            desired = (rest - Position) * 3f;
        }

        // Tether spring: past the limit the stalk hauls the head back hard, so a whiffed
        // lunge snaps back instead of dragging the plant across the cave.
        var fromRoot = Position - _root;
        var stretch = fromRoot.Length();
        if (stretch > tether)
            desired += -fromRoot / stretch * ((stretch - tether) * 10f);

        Velocity = Vector2.Lerp(Velocity, desired, MathF.Min(1f, 6f * dt));
    }

    /// <summary>RockMimic: sits disguised as an ore-speckled boulder — the gold glint is the
    /// bait. It wakes when a dwarf gets greedy (close approach) or pokes it with anything,
    /// then chases hard and permanently. Until it wakes it is non-hostile so sentries ignore
    /// the disguise too.</summary>
    private void TickMimic(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        if (!_awake)
        {
            GroundMove(dt, planet, up, right, 0f, speedMul); // settle like any loose rock
            if (dist < 42f || HitFlash > 0f)
            {
                _awake = true;
                Hostile = true;
                _swing = 0.5f; // wake-lurch animation
            }
            return;
        }
        if (_swing > 0f) _swing -= dt;
        float moveAxis;
        if (dist < 240f) // long memory, wide aggro — once it's up, it's coming
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 1.5f + (float)Random.Shared.NextDouble() * 2f;
            moveAxis = MathF.Sin(Wander * 3f);
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    /// <summary>Lizardman: a warren-guard warrior. Off aggro it patrols its gallery like a
    /// sentry on rounds. Aggroed (long memory, like the delver) it presses the prey on foot,
    /// hurls a bone spear from mid-range whenever it has line of sight, and lunges into bite
    /// range up close. It never digs — the warren's tunnels are its ground, and a dwarf who
    /// seals himself in has genuinely escaped it.</summary>
    private void TickLizardman(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;
        _retarget -= dt;
        if (dist < 240f) _aggroT = 7f; else _aggroT -= dt;

        if (_aggroT > 0f)
        {
            var tDist = Vector2.Dot(toPlayer, right);
            var moveAxis = MathF.Abs(tDist) > 8f ? MathF.Sign(tDist) : 0f;
            GroundMove(dt, planet, up, right, moveAxis, speedMul);

            // Bone spear: mid-range, sighted, off cooldown. Slight loft so the cast drops
            // onto the target rather than undershooting.
            if (dist > 70f && dist < 210f && _cd <= 0f && shots is not null
                && HasLineOfSight(planet, toPlayer, dist))
            {
                var dir = toPlayer / dist;
                var aim = Vector2.Normalize(dir + up * (0.05f + dist * 0.001f));
                shots.Add(new TitanProjectile(Position + aim * (Radius + 2f), aim * 230f,
                    TitanShotKind.Spike, damage: 9f));
                _cd = 2.4f + (float)Random.Shared.NextDouble() * 0.9f;
                _swing = 0.35f;
            }
            // Lunge: a short leap into bite range when the spear arm is spent.
            else if (dist < 60f && dist > 0.01f && _retarget <= 0f && IsGrounded(planet, up))
            {
                Velocity = toPlayer / dist * 115f * speedMul + up * 65f;
                _retarget = 1.5f;
            }
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0f)
            {
                Wander = 2f + (float)Random.Shared.NextDouble() * 2.5f;
                _amble = Random.Shared.Next(3) - 1;
            }
            GroundMove(dt, planet, up, right, _amble * 0.6f, speedMul);
        }
    }

    // ---------------------------------------------------------------- sky fauna

    private void TickFlyer(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        var alt = (Position - planet.Center).Length();
        if (_prefAlt <= 0f) _prefAlt = alt; // adopt spawn altitude as home band
        _cd -= dt;

        var tangent = right * _orbitSign;
        Vector2 desired;

        // Stinger dive: player in range, not retreating, and a clear line (three-sample LOS
        // so it doesn't try to dive through a mountain).
        if (Kind == CreatureKind.SkyStinger && _cd <= 0f && dist < 180f && dist > 0.01f
            && HasLineOfSight(planet, toPlayer, dist))
        {
            desired = toPlayer / dist * 140f * speedMul;
        }
        else
        {
            desired = tangent * MoveSpeed * speedMul;
            desired += up * MathHelper.Clamp((_prefAlt - alt) * 0.6f, -28f, 28f);
            // Lazy sine bob so flocks don't fly a perfect circle.
            desired += up * MathF.Sin(_phase + alt * 0.01f + Wander) * 7f;
            Wander += dt * 2.1f;
        }

        // Terrain ahead (mountain flank): climb over it, occasionally turn around instead.
        if (planet.IsSolidAt(Position + tangent * 14f) || planet.IsSolidAt(Position + Velocity * 0.25f))
        {
            desired = up * 55f;
            if (Random.Shared.Next(50) == 0) _orbitSign = -_orbitSign;
        }

        Velocity = Vector2.Lerp(Velocity, desired, MathF.Min(1f, 3f * dt));
    }

    private bool HasLineOfSight(Planet planet, Vector2 toPlayer, float dist)
    {
        for (var s = 1; s <= 3; s++)
            if (planet.IsSolidAt(Position + toPlayer * (s / 4f)))
                return false;
        return true;
    }

    // ---------------------------------------------------------------- shared movement

    /// <summary>Walker integrator: tangent drive + core-ward gravity + a reflexive hop when
    /// walking into a wall while grounded, so cave dwellers climb tunnel lips instead of
    /// grinding against them.</summary>
    private void GroundMove(float dt, Planet planet, Vector2 up, Vector2 right,
        float moveAxis, float speedMul)
    {
        var vT = Vector2.Dot(Velocity, right);
        var vN = Vector2.Dot(Velocity, up);
        vT = MoveToward(vT, moveAxis * MoveSpeed * speedMul, 400f * dt);
        vN = MathF.Max(vN - 320f * dt, -260f); // terminal velocity — keeps substeps bounded
        if (MathF.Abs(moveAxis) > 0.1f && IsGrounded(planet, up)
            && planet.IsSolidAt(Position + right * (MathF.Sign(moveAxis) * (Radius + 3f))))
        {
            vN = 120f;
        }
        Velocity = right * vT + up * vN;
    }

    private bool IsGrounded(Planet planet, Vector2 up) =>
        planet.IsSolidAt(Position - up * (Radius + 1.5f));

    /// <summary>Pick a wander heading for a digger: tangent-biased with a random radial tilt,
    /// sampled a few times preferring a heading that points into nearby rock — so tunnellers
    /// spend their time tunnelling instead of pinballing around open caverns.</summary>
    private Vector2 PickDigHeading(Planet planet, Vector2 right, float tiltRange)
    {
        var pick = right;
        for (var i = 0; i < 6; i++)
        {
            var baseDir = right * (Random.Shared.Next(2) == 0 ? 1f : -1f);
            pick = Rotate(baseDir, ((float)Random.Shared.NextDouble() - 0.5f) * tiltRange);
            if (planet.IsSolidAt(Position + pick * (Radius + 6f))) return pick;
        }
        return pick;
    }

    /// <summary>Digger grip sense: probe a ring of world-space points just outside the body
    /// for solid rock. Any contact lets a tunneller cling and inch along its dig heading.</summary>
    private bool AnySolidNear(Planet planet)
    {
        for (var i = 0; i < 8; i++)
        {
            var a = i * (MathF.Tau / 8f);
            var probe = Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (Radius + 2.5f);
            if (planet.IsSolidAt(probe)) return true;
        }
        return false;
    }

    /// <summary>Push the creature out of any solid tile it overlaps. Tiles are rotated rects
    /// in polar space — centre at TileToWorld, local basis (tangent, radial-up), extents
    /// chord × TileSize — mirroring Player.ResolveCollision. (The previous Cartesian
    /// x*TileSize rects were only meaningful near angle zero, so creatures phased straight
    /// through the world almost everywhere.)</summary>
    private void ResolveTileCollision(Planet planet)
    {
        for (var iter = 0; iter < 4; iter++)
        {
            var (tx, _) = planet.WorldToTile(Position);
            // Neighbour columns must be recomputed per ring from the true world angle:
            // rings have different tile counts, so reusing this ring's ty index drifts by
            // whole tiles near the angle-2π wrap and leaves collision holes.
            var relC = Position - planet.Center;
            var ang = MathF.Atan2(relC.Y, relC.X);
            if (ang < 0) ang += MathHelper.TwoPi;
            var pushed = false;
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = tx + dx;
                // Below ring 0 Planet.Get reports the synthetic Core pseudo-tile, which
                // has no world-space rect — skip rather than collide with garbage.
                if (x < 0 || x >= planet.Rings) continue;
                var nRing = planet.TilesAt(x);
                var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
                for (var dy = -2; dy <= 2; dy++)
                {
                    var y = ty0 + dy;
                    if (!Tiles.BlocksPlayer(planet.Get(x, y))) continue;

                    var centre = planet.TileToWorld(x, y);
                    var tUp = planet.UpAt(centre);
                    var tRight = new Vector2(-tUp.Y, tUp.X);
                    var rel = Position - centre;
                    var pLocalX = Vector2.Dot(rel, tRight);
                    var pLocalY = Vector2.Dot(rel, tUp);

                    // Tile-local extents: chord (arc width) × TileSize (radial).
                    var ringRadius = (Planet.RingMin + x + 0.5f) * Planet.TileSize;
                    var halfX = MathHelper.TwoPi * ringRadius / planet.TilesAt(x) * 0.5f;
                    var halfY = Planet.TileSize * 0.5f;

                    var cLocalX = MathHelper.Clamp(pLocalX, -halfX, halfX);
                    var cLocalY = MathHelper.Clamp(pLocalY, -halfY, halfY);
                    var diffX = pLocalX - cLocalX;
                    var diffY = pLocalY - cLocalY;
                    var distSq = diffX * diffX + diffY * diffY;

                    if (distSq < Radius * Radius && distSq > 0.0001f)
                    {
                        var dist = MathF.Sqrt(distSq);
                        var n = tRight * (diffX / dist) + tUp * (diffY / dist);
                        Position += n * (Radius - dist + 0.05f);
                        var into = Vector2.Dot(Velocity, n);
                        if (into < 0) Velocity -= n * into;
                        pushed = true;
                    }
                    else if (distSq <= 0.0001f)
                    {
                        // Centre is inside the tile (fresh spawn, cave-in, or a digger whose
                        // tunnel collapsed on it). Escape toward the nearest open neighbour;
                        // if fully buried, push outward radially.
                        var skyOuter = !Tiles.IsSolid(planet.Get(x + 1, y));
                        var skyInner = !Tiles.IsSolid(planet.Get(x - 1, y));
                        var skyRight = !Tiles.IsSolid(planet.Get(x, y + 1));
                        var skyLeft  = !Tiles.IsSolid(planet.Get(x, y - 1));

                        var nLocalX = 0f; var nLocalY = 0f; var minDist = float.MaxValue;
                        if (skyOuter && (halfY - pLocalY) < minDist) { nLocalX = 0f; nLocalY =  1f; minDist = halfY - pLocalY; }
                        if (skyInner && (pLocalY + halfY) < minDist) { nLocalX = 0f; nLocalY = -1f; minDist = pLocalY + halfY; }
                        if (skyRight && (halfX - pLocalX) < minDist) { nLocalX =  1f; nLocalY = 0f; minDist = halfX - pLocalX; }
                        if (skyLeft  && (pLocalX + halfX) < minDist) { nLocalX = -1f; nLocalY = 0f; minDist = pLocalX + halfX; }
                        if (minDist == float.MaxValue)
                        {
                            nLocalY = 1f;
                            minDist = halfY - pLocalY;
                        }

                        var n = tRight * nLocalX + tUp * nLocalY;
                        Position += n * (minDist + Radius + 0.05f);
                        var into = Vector2.Dot(Velocity, n);
                        if (into < 0) Velocity -= n * into;
                        pushed = true;
                    }
                }
            }
            if (!pushed) break;
        }
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>Per-kind procedural sprite, drawn with the renderer primitives so no textures
    /// are needed. Status tints match the old creature block: white on hit-flash, pale blue
    /// frozen, ember orange burning.</summary>
    public void Draw(Renderer r, Planet planet, Player player)
    {
        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y);
        var t = r.Time;
        var facing = Vector2.Dot(Velocity, right) >= 0f ? 1f : -1f;

        Color Tinted(Color baseCol)
        {
            if (HitFlash > 0) return Color.White;
            if (FreezeSeconds > 0) return new Color(150, 200, 240);
            if (BurnSeconds > 0) return new Color(220, 110, 70);
            return baseCol;
        }

        switch (Kind)
        {
            case CreatureKind.Grub:
            {
                r.DrawCircle(Position, Radius, Tinted(new Color(180, 60, 80)));
                r.DrawCircle(Position + up * 1f, 1.5f, Color.Black);
                break;
            }
            case CreatureKind.Skitterer:
            {
                var body = Tinted(new Color(58, 48, 70));
                // Three leg pairs scissoring; swing speed keys off actual tangent speed.
                var swing = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i++)
                {
                    var a = MathF.Sin(t * 16f + _phase + i * 2.1f) * 0.55f * swing;
                    r.DrawRect(Position - up * 1.2f + right * (i * 1.8f), new Vector2(0.8f, 3.6f), body, rot + a);
                }
                r.DrawCircle(Position, 2.6f, body);
                r.DrawCircle(Position + right * (facing * 2.0f) + up * 0.5f, 1.6f, Tinted(new Color(80, 66, 96)));
                r.DrawCircle(Position + right * (facing * 2.6f) + up * 1.0f, 0.6f, new Color(210, 230, 120));
                r.DrawCircle(Position + right * (facing * 1.8f) + up * 1.2f, 0.6f, new Color(210, 230, 120));
                break;
            }
            case CreatureKind.Borer:
            {
                var dir = _digDir.LengthSquared() > 0.01f ? Vector2.Normalize(_digDir) : right * facing;
                // Three chitin segments trailing the head, gently undulating.
                for (var s = 2; s >= 0; s--)
                {
                    var wob = MathF.Sin(t * 6f + _phase + s * 1.3f) * 0.8f;
                    var seg = Position - dir * (s * 3.4f) + new Vector2(-dir.Y, dir.X) * wob;
                    var col = (s & 1) == 0 ? new Color(150, 108, 74) : new Color(118, 84, 58);
                    r.DrawCircle(seg, Radius - s * 0.8f, Tinted(col));
                }
                // Mandibles chomp while it's chewing.
                var chomp = 0.35f + MathF.Abs(MathF.Sin(t * 10f + _phase)) * 0.4f;
                var mAng = MathF.Atan2(dir.Y, dir.X);
                var snout = Position + dir * (Radius - 0.5f);
                r.DrawRect(snout + Rotate(dir, chomp) * 2.2f, new Vector2(3.4f, 1.1f), Tinted(new Color(70, 52, 40)), mAng + chomp);
                r.DrawRect(snout + Rotate(dir, -chomp) * 2.2f, new Vector2(3.4f, 1.1f), Tinted(new Color(70, 52, 40)), mAng - chomp);
                break;
            }
            case CreatureKind.CaveEye:
            {
                var bob = up * MathF.Sin(t * 3f + _phase) * 0.8f;
                var c = Position + bob;
                r.DrawCircle(c, Radius, Tinted(new Color(235, 230, 225)));
                // Iris tracks the player when close, otherwise looks where it's going.
                var toP = player.Position - c;
                var look = toP.Length() < 110f && toP.LengthSquared() > 0.01f
                    ? Vector2.Normalize(toP)
                    : new Vector2(MathF.Cos(_heading), MathF.Sin(_heading));
                r.DrawCircle(c + look * 1.6f, 2.1f, Tinted(new Color(150, 60, 60)));
                r.DrawCircle(c + look * 2.1f, 1.0f, Color.Black);
                break;
            }
            case CreatureKind.MagmaSlug:
            {
                var body = Tinted(new Color(72, 46, 50));
                r.DrawCircle(Position - right * (facing * 3f) + up * 0.3f, Radius - 1.2f, body);
                r.DrawCircle(Position, Radius, body);
                // Cracked-hide embers flickering out of the shell.
                for (var i = 0; i < 3; i++)
                {
                    var flick = MathF.Sin(t * 9f + _phase + i * 2.4f) * 0.5f + 0.5f;
                    var ember = Color.Lerp(new Color(255, 110, 40), new Color(255, 210, 90), flick);
                    var off = right * ((i - 1) * 2.2f * facing) + up * (1.2f + (i & 1));
                    r.DrawCircle(Position + off, 0.9f + flick * 0.5f, ember);
                }
                break;
            }
            case CreatureKind.Grazer:
            {
                var hide = Tinted(new Color(128, 186, 168));
                var dark = Tinted(new Color(94, 142, 130));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                // Six spindly legs — three pairs, alternating gait when moving.
                for (var i = -1; i <= 1; i++)
                {
                    var a = moving ? MathF.Sin(t * 9f + _phase + i * 2.0f) * 0.4f : 0f;
                    r.DrawRect(Position - up * 0.5f + right * (i * 2.6f), new Vector2(0.9f, 5.0f), dark, rot + a);
                }
                r.DrawRect(Position + up * 2.2f, new Vector2(9f, 4.2f), hide, rot);
                // Neck + head reaching forward, dipping to graze when idle.
                var dip = moving ? 0f : MathF.Sin(t * 1.3f + _phase) * 1.5f - 0.5f;
                var head = Position + up * (4.2f + dip) + right * (facing * 5.4f);
                r.DrawRect(Position + up * 3.6f + right * (facing * 4.2f), new Vector2(1.6f, 4.4f), hide, rot + facing * 0.6f);
                r.DrawCircle(head, 1.9f, hide);
                r.DrawCircle(head + right * (facing * 1.2f) + up * 0.4f, 0.6f, Color.Black);
                // Paired antennae with glow-tips — unmistakably not an Earth elk.
                r.DrawRect(head + up * 2.0f, new Vector2(0.5f, 2.6f), dark, rot + facing * 0.35f);
                r.DrawCircle(head + up * 3.4f + right * (facing * 0.8f), 0.7f, new Color(200, 255, 220));
                break;
            }
            case CreatureKind.Hopper:
            {
                var fur = Tinted(new Color(186, 162, 208));
                var squash = IsGrounded(planet, up) ? 0.85f : 1.1f;
                r.DrawCircle(Position, Radius * squash, fur);
                // Ear stalks and big nocturnal eye.
                r.DrawRect(Position + up * (Radius + 1.2f) + right * 0.8f, new Vector2(0.6f, 2.4f), fur, rot + 0.25f);
                r.DrawRect(Position + up * (Radius + 1.2f) - right * 0.8f, new Vector2(0.6f, 2.4f), fur, rot - 0.25f);
                r.DrawCircle(Position + right * (facing * 1.0f) + up * 0.6f, 0.9f, Color.Black);
                break;
            }
            case CreatureKind.SkyMoth:
            {
                var wingBeat = MathF.Sin(t * 9f + _phase) * 0.9f;
                var wing = Tinted(new Color(214, 224, 244, 220));
                r.DrawRect(Position + right * 2.4f + up * 0.6f, new Vector2(5.2f, 1.8f), wing, rot + 1.57f + wingBeat);
                r.DrawRect(Position - right * 2.4f + up * 0.6f, new Vector2(5.2f, 1.8f), wing, rot + 1.57f - wingBeat);
                r.DrawCircle(Position, 2.0f, Tinted(new Color(168, 178, 210)));
                r.DrawCircle(Position - up * 1.8f, 1.3f, Tinted(new Color(140, 150, 186)));
                break;
            }
            case CreatureKind.SkyStinger:
            {
                var vDir = Velocity.LengthSquared() > 1f ? Vector2.Normalize(Velocity) : right * facing;
                var vAng = MathF.Atan2(vDir.Y, vDir.X);
                var shell = Tinted(new Color(206, 128, 54));
                // Tail spike trailing opposite the flight direction.
                r.DrawRect(Position - vDir * 3.6f, new Vector2(4.6f, 1.4f), Tinted(new Color(96, 62, 34)), vAng);
                r.DrawRect(Position - vDir * 6.2f, new Vector2(2.4f, 0.8f), Tinted(new Color(60, 40, 24)), vAng);
                var wingBeat = MathF.Sin(t * 18f + _phase) * 1.0f;
                var wing = new Color(232, 238, 248, 170);
                r.DrawRect(Position + up * 1.6f + right * 1.2f, new Vector2(4.4f, 1.2f), wing, rot + 1.57f + wingBeat);
                r.DrawRect(Position + up * 1.6f - right * 1.2f, new Vector2(4.4f, 1.2f), wing, rot + 1.57f - wingBeat);
                r.DrawCircle(Position, 3.0f, shell);
                r.DrawRect(Position, new Vector2(1.2f, 4.6f), Tinted(new Color(60, 44, 30)), vAng);
                r.DrawCircle(Position + vDir * 2.2f, 1.5f, Tinted(new Color(120, 78, 40)));
                break;
            }
            case CreatureKind.HornedDelver:
            {
                var skin = Tinted(new Color(152, 140, 152));
                var cloth = Tinted(new Color(70, 50, 82));
                var horn = Tinted(new Color(222, 210, 188));
                // Legs — stride keyed to actual tangent speed so idle delvers stand still.
                var stride = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = MathF.Sin(t * 12f + _phase + i * 1.6f) * 0.5f * stride;
                    r.DrawRect(Position - up * 1.4f + right * (i * 1.2f), new Vector2(1.1f, 3.4f), cloth, rot + a);
                }
                r.DrawRect(Position + up * 1.6f, new Vector2(4.6f, 5.2f), cloth, rot);
                var head = Position + up * 5.2f;
                r.DrawCircle(head, 2.2f, skin);
                r.DrawRect(head + up * 1.9f + right * 1.5f, new Vector2(0.9f, 2.8f), horn, rot + 0.55f);
                r.DrawRect(head + up * 1.9f - right * 1.5f, new Vector2(0.9f, 2.8f), horn, rot - 0.55f);
                // Eyes smoulder brighter while it's hunting.
                var eyeCol = _aggroT > 0f ? new Color(255, 70, 40) : new Color(190, 90, 60);
                r.DrawCircle(head + right * (facing * 1.1f) + up * 0.2f, 0.7f, eyeCol);
                r.DrawCircle(head + right * (facing * 0.1f) + up * 0.2f, 0.6f, eyeCol);
                // Pickaxe: rests on the shoulder, arcs hard when a swing lands.
                var swingA = _swing > 0f ? MathF.Sin(_swing * 20f) * 1.2f : MathF.Sin(t * 2f + _phase) * 0.12f;
                var handleDir = Rotate(up, facing * (0.95f - swingA));
                var gripPos = Position + right * (facing * 3.0f) + up * 2.0f;
                var hAng = MathF.Atan2(handleDir.Y, handleDir.X);
                r.DrawRect(gripPos + handleDir * 2.8f, new Vector2(6.4f, 1.0f), Tinted(new Color(130, 95, 55)), hAng);
                r.DrawRect(gripPos + handleDir * 5.8f, new Vector2(1.2f, 4.6f), Tinted(new Color(160, 165, 178)), hAng);
                break;
            }
            case CreatureKind.Centipede:
            {
                // Body tail-first along the breadcrumb trail so the head overlaps the neck.
                for (var s = SegCount; s >= 1; s--)
                {
                    var seg = SegPos(s);
                    var next = s > 1 ? SegPos(s - 1) : Position;
                    var d = next - seg;
                    var perp = d.LengthSquared() > 0.001f ? Vector2.Normalize(new Vector2(-d.Y, d.X)) : right;
                    var pAng = MathF.Atan2(perp.Y, perp.X);
                    var la = MathF.Sin(t * 14f + _phase + s * 1.7f) * 0.5f;
                    var legCol = Tinted(new Color(84, 38, 32));
                    r.DrawRect(seg + perp * 2.0f, new Vector2(2.6f, 0.7f), legCol, pAng + la);
                    r.DrawRect(seg - perp * 2.0f, new Vector2(2.6f, 0.7f), legCol, pAng - la);
                    var col = (s & 1) == 0 ? new Color(148, 66, 52) : new Color(114, 48, 40);
                    r.DrawCircle(seg, 2.7f - s * 0.12f, Tinted(col));
                }
                var hd = _digDir.LengthSquared() > 0.01f ? Vector2.Normalize(_digDir) : right * facing;
                var hPerp = new Vector2(-hd.Y, hd.X);
                var hAng2 = MathF.Atan2(hd.Y, hd.X);
                r.DrawCircle(Position, 3.2f, Tinted(new Color(162, 78, 58)));
                // Antennae sweeping ahead, mandibles working while it chews.
                var chomp2 = 0.3f + MathF.Abs(MathF.Sin(t * 12f + _phase)) * 0.35f;
                r.DrawRect(Position + hd * 3.2f + hPerp * 1.2f, new Vector2(3.0f, 0.7f), Tinted(new Color(84, 38, 32)), hAng2 + chomp2);
                r.DrawRect(Position + hd * 3.2f - hPerp * 1.2f, new Vector2(3.0f, 0.7f), Tinted(new Color(84, 38, 32)), hAng2 - chomp2);
                r.DrawRect(Position + hd * 2.6f + hPerp * 2.2f, new Vector2(3.6f, 0.5f), Tinted(new Color(190, 120, 90)), hAng2 + 0.7f);
                r.DrawRect(Position + hd * 2.6f - hPerp * 2.2f, new Vector2(3.6f, 0.5f), Tinted(new Color(190, 120, 90)), hAng2 - 0.7f);
                r.DrawCircle(Position + hd * 1.6f + hPerp * 1.1f, 0.6f, Color.Black);
                r.DrawCircle(Position + hd * 1.6f - hPerp * 1.1f, 0.6f, Color.Black);
                break;
            }
            case CreatureKind.MoleBeast:
            {
                var dir = _digDir.LengthSquared() > 0.01f ? Vector2.Normalize(_digDir) : right * facing;
                var perp = new Vector2(-dir.Y, dir.X);
                var dAng = MathF.Atan2(dir.Y, dir.X);
                var fur = Tinted(new Color(96, 88, 118));
                r.DrawCircle(Position - dir * 2.2f, Radius, fur);
                r.DrawCircle(Position + dir * 1.0f, Radius - 1.0f, fur);
                var snout = Position + dir * (Radius + 0.4f);
                r.DrawCircle(snout, 1.8f, Tinted(new Color(122, 112, 142)));
                r.DrawCircle(snout + dir * 1.4f, 1.0f, Tinted(new Color(228, 142, 152))); // star-nose
                // Pale digging claws churning ahead of the snout.
                var churn = MathF.Sin(t * 11f + _phase) * 0.4f;
                var claw = Tinted(new Color(214, 200, 172));
                r.DrawRect(Position + dir * (Radius + 1.6f) + perp * 1.7f, new Vector2(2.9f, 1.1f), claw, dAng + 0.5f + churn);
                r.DrawRect(Position + dir * (Radius + 1.6f) - perp * 1.7f, new Vector2(2.9f, 1.1f), claw, dAng - 0.5f - churn);
                // Near-blind beady eyes — glowing red while it holds a grudge.
                var moleEye = _provokedT > 0f ? new Color(255, 70, 50) : Color.Black;
                r.DrawCircle(Position + dir * 2.0f + perp * 1.3f, 0.5f, moleEye);
                r.DrawCircle(Position + dir * 2.0f - perp * 1.3f, 0.5f, moleEye);
                break;
            }
            case CreatureKind.SporeBat:
            {
                // Pale fungal flitter: round spore-dusted body, two membrane wings beating
                // out of phase, drooping antennae.
                var flap = MathF.Sin(t * 13f + _phase) * 0.9f;
                var body = Tinted(new Color(150, 180, 130));
                var wing = Tinted(new Color(190, 220, 170)) * 0.85f;
                r.DrawRect(Position + right * 2.4f + up * (flap * 1.4f), new Vector2(4.4f, 1.6f), wing, rot + 0.5f + flap * 0.5f);
                r.DrawRect(Position - right * 2.4f + up * (flap * 1.4f), new Vector2(4.4f, 1.6f), wing, rot - 0.5f - flap * 0.5f);
                r.DrawCircle(Position, Radius, body);
                r.DrawCircle(Position + up * 0.8f + right * 0.9f, 0.8f, Tinted(new Color(110, 150, 95)));
                r.DrawCircle(Position - up * 0.6f - right * 1.0f, 0.6f, Tinted(new Color(110, 150, 95)));
                r.DrawCircle(Position + up * (Radius + 0.6f), 0.5f, Tinted(new Color(210, 240, 180)));
                break;
            }
            case CreatureKind.CrystalCrawler:
            {
                // Low armoured slab with a jagged crystal ridge down its back and stubby
                // scuttling legs — a walking geode.
                var shell = Tinted(new Color(95, 90, 120));
                var shard = Tinted(new Color(180, 130, 230));
                r.DrawRect(Position - up * 0.6f, new Vector2(Radius * 2f, Radius * 1.2f), shell, rot);
                var scuttle = MathF.Sin(t * 9f + _phase) * 0.7f;
                var leg = Tinted(new Color(70, 66, 92));
                for (var i = -1; i <= 1; i++)
                    r.DrawRect(Position - up * (Radius * 0.8f) + right * (i * 3.0f + scuttle * 0.6f),
                        new Vector2(1.2f, 2.2f), leg, rot);
                for (var i = 0; i < 3; i++)
                {
                    var bx = (i - 1) * 2.6f;
                    r.DrawRect(Position + up * (Radius * 0.7f) + right * bx,
                        new Vector2(1.6f, 3.4f + (i == 1 ? 1.4f : 0f)), shard, rot + (i - 1) * 0.35f);
                }
                r.DrawCircle(Position + right * facing * (Radius - 0.8f) + up * 0.4f, 0.6f, Color.Black);
                break;
            }
            case CreatureKind.VoidWraith:
            {
                // A torn shred of the Rift: layered violet shroud, white-hot core, and a
                // wake of fading wisps behind its flight path.
                var drift = Velocity.LengthSquared() > 1f ? Vector2.Normalize(Velocity) : right;
                for (var i = 1; i <= 3; i++)
                    r.DrawCircle(Position - drift * (i * 2.6f), Radius - i * 0.8f,
                        Tinted(new Color(90, 40, 130)) * (0.5f - i * 0.12f));
                var pulse = MathF.Sin(t * 6f + _phase) * 0.5f;
                r.DrawCircle(Position, Radius + pulse * 0.5f, Tinted(new Color(70, 30, 110)) * 0.85f);
                r.DrawCircle(Position, Radius * 0.55f, Tinted(new Color(140, 80, 200)));
                r.DrawCircle(Position, 1.1f, Tinted(new Color(240, 220, 255)));
                // Post-blink shimmer: an expanding ring where it re-materialised.
                if (_swing > 0f)
                {
                    var ringT = 1f - _swing / 0.35f;
                    r.DrawCircle(Position, Radius + 2f + ringT * 8f, new Color(180, 120, 255) * (0.5f * (1f - ringT)));
                }
                break;
            }
            case CreatureKind.CaveSlime:
            case CreatureKind.Slimelet:
            {
                // Gelatinous dome: squashes on the ground, stretches mid-hop, jiggles at rest.
                var grounded = IsGrounded(planet, up);
                var jiggle = MathF.Sin(t * 8f + _phase) * 0.08f;
                var squashX = (grounded ? 1.15f : 0.85f) + jiggle;
                var squashY = (grounded ? 0.8f : 1.15f) - jiggle;
                var gel = Kind == CreatureKind.Slimelet
                    ? Tinted(new Color(120, 210, 170)) * 0.85f
                    : Tinted(new Color(80, 180, 150)) * 0.85f;
                r.DrawRect(Position, new Vector2(Radius * 2f * squashX, Radius * 1.7f * squashY), gel, rot);
                r.DrawCircle(Position + up * (Radius * 0.35f * squashY), Radius * 0.9f * squashX, gel);
                // Nucleus blob drifting inside — reads as "creature", not "puddle".
                r.DrawCircle(Position + right * (facing * 0.8f) - up * 0.3f, Radius * 0.4f,
                    Tinted(new Color(50, 130, 105)));
                r.DrawCircle(Position + right * (facing * Radius * 0.45f) + up * (Radius * 0.3f), 0.8f, Color.Black);
                if (Kind == CreatureKind.CaveSlime)
                    r.DrawCircle(Position + right * (facing * Radius * 0.1f) + up * (Radius * 0.35f), 0.7f, Color.Black);
                break;
            }
            case CreatureKind.AcidSpitter:
            {
                // Squat gland-toad: wide warty body, swollen throat sac that pulses brighter
                // as the next glob readies, maw gaping while it spits.
                var body = Tinted(new Color(96, 122, 62));
                var belly = Tinted(new Color(140, 168, 88));
                r.DrawCircle(Position - up * 0.6f, Radius, body);
                r.DrawCircle(Position + up * 1.2f, Radius * 0.75f, body);
                r.DrawCircle(Position - up * 1.2f + right * (facing * 1.2f), Radius * 0.55f, belly);
                // Throat sac: dim just after a spit, glowing full as the cooldown ends.
                var charge = 1f - MathHelper.Clamp(_cd / 2.4f, 0f, 1f);
                var sac = Color.Lerp(new Color(110, 150, 60), new Color(190, 240, 90), charge);
                r.DrawCircle(Position + right * (facing * 2.2f) - up * 0.4f, 1.6f + charge * 0.8f, Tinted(sac));
                var head = Position + up * 2.6f + right * (facing * 1.6f);
                r.DrawCircle(head, 2.0f, body);
                if (_swing > 0f) // maw agape mid-spit
                    r.DrawCircle(head + right * (facing * 1.4f), 1.2f, Tinted(new Color(210, 250, 120)));
                r.DrawCircle(head + up * 1.0f + right * (facing * 0.6f), 0.7f, new Color(230, 220, 90));
                r.DrawCircle(head + up * 1.0f - right * (facing * 0.8f), 0.6f, new Color(230, 220, 90));
                break;
            }
            case CreatureKind.BomberBeetle:
            {
                // Scuttling legs up front, volatile amber abdomen behind. Armed, the abdomen
                // strobes faster and faster until the bang.
                var shellCol = Tinted(new Color(56, 50, 44));
                var swing = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i++)
                {
                    var a = MathF.Sin(t * 18f + _phase + i * 2.1f) * 0.5f * swing;
                    r.DrawRect(Position - up * 1.0f + right * (i * 1.6f), new Vector2(0.8f, 3.2f), shellCol, rot + a);
                }
                var abdomen = new Color(210, 130, 40);
                if (_fuse > 0f)
                {
                    // Strobe accelerates as the fuse runs down.
                    var rate = 10f + (0.7f - _fuse) * 40f;
                    abdomen = MathF.Sin(t * rate) > 0f ? new Color(255, 240, 220) : new Color(255, 60, 30);
                }
                r.DrawCircle(Position - right * (facing * 2.2f), Radius * 0.95f, Tinted(abdomen));
                r.DrawCircle(Position + right * (facing * 1.2f), 2.4f, shellCol);
                r.DrawCircle(Position + right * (facing * 3.0f) + up * 0.6f, 0.6f, new Color(255, 190, 90));
                break;
            }
            case CreatureKind.SnapperVine:
            {
                // Stalk drawn root→head in sagging segments, base leaves, and a bud-jaw that
                // gapes as prey gets close.
                var stem = Tinted(new Color(70, 120, 55));
                var leaf = Tinted(new Color(95, 150, 70));
                var rootUp = planet.UpAt(_rooted ? _root : Position);
                var basePos = _rooted ? _root : Position;
                r.DrawRect(basePos + new Vector2(-rootUp.Y, rootUp.X) * 2.2f, new Vector2(4.4f, 1.4f), leaf,
                    MathF.Atan2(rootUp.X, -rootUp.Y) + 0.9f);
                r.DrawRect(basePos - new Vector2(-rootUp.Y, rootUp.X) * 2.2f, new Vector2(4.4f, 1.4f), leaf,
                    MathF.Atan2(rootUp.X, -rootUp.Y) - 0.9f);
                for (var i = 1; i <= 4; i++)
                {
                    var f = i / 5f;
                    var seg = Vector2.Lerp(basePos, Position, f);
                    seg += new Vector2(-rootUp.Y, rootUp.X) * (MathF.Sin(t * 2f + _phase + f * 4f) * 1.2f * (1f - f));
                    r.DrawCircle(seg, 1.6f - f * 0.4f, stem);
                }
                var toP = player.Position - Position;
                var headDir = toP.LengthSquared() > 0.01f ? Vector2.Normalize(toP) : rootUp;
                var hAngV = MathF.Atan2(headDir.Y, headDir.X);
                var gape = MathHelper.Clamp(1f - toP.Length() / 120f, 0f, 1f) * 0.9f + 0.15f;
                r.DrawCircle(Position, Radius, Tinted(new Color(110, 60, 90)));       // bud
                r.DrawCircle(Position + headDir * 1.2f, Radius * 0.5f, Tinted(new Color(200, 90, 120))); // maw
                r.DrawRect(Position + Rotate(headDir, gape) * (Radius + 0.8f), new Vector2(4.2f, 1.3f),
                    Tinted(new Color(80, 130, 60)), hAngV + gape);
                r.DrawRect(Position + Rotate(headDir, -gape) * (Radius + 0.8f), new Vector2(4.2f, 1.3f),
                    Tinted(new Color(80, 130, 60)), hAngV - gape);
                break;
            }
            case CreatureKind.RockMimic:
            {
                // Disguise: a lichen-flecked boulder with a gold glint — bait for a miner.
                // Awake it's the same boulder with a maw and hateful eyes, moving fast.
                var rock = Tinted(new Color(104, 100, 96));
                var dark = Tinted(new Color(84, 80, 78));
                var lurch = _awake ? MathF.Sin(t * 10f + _phase) * 0.6f : 0f;
                r.DrawCircle(Position - up * 0.6f, Radius, rock);
                r.DrawCircle(Position + right * (2.4f + lurch) + up * 1.4f, Radius * 0.6f, dark);
                r.DrawCircle(Position - right * (2.6f - lurch) + up * 1.2f, Radius * 0.55f, dark);
                r.DrawCircle(Position + up * 2.6f, Radius * 0.5f, rock);
                // The bait: gold speckles, twinkling faintly.
                var glint = MathF.Sin(t * 3f + _phase) * 0.5f + 0.5f;
                r.DrawCircle(Position + right * 1.8f - up * 0.8f, 0.7f, Color.Lerp(new Color(190, 150, 40), new Color(255, 220, 110), glint));
                r.DrawCircle(Position - right * 1.2f + up * 1.8f, 0.6f, new Color(210, 170, 60));
                if (_awake)
                {
                    r.DrawRect(Position + right * (facing * 1.0f) - up * 1.2f, new Vector2(5.4f, 2.0f),
                        Tinted(new Color(30, 24, 22)), rot); // maw
                    r.DrawCircle(Position + right * (facing * 2.0f) + up * 1.6f, 0.9f, new Color(255, 70, 40));
                    r.DrawCircle(Position + right * (facing * 0.2f) + up * 1.9f, 0.8f, new Color(255, 70, 40));
                }
                break;
            }
            case CreatureKind.SnowLoper:
            {
                // Woolly body riding high on two stilt legs, frost-blue crest — built to
                // keep its belly off the ice.
                var wool = Tinted(new Color(226, 234, 242));
                var dusk = Tinted(new Color(168, 186, 204));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = moving ? MathF.Sin(t * 7f + _phase + i * 1.7f) * 0.35f : 0f;
                    r.DrawRect(Position + up * 1.2f + right * (i * 2.2f), new Vector2(1.0f, 7.0f), dusk, rot + a);
                }
                r.DrawCircle(Position + up * 4.6f, 4.0f, wool);
                r.DrawCircle(Position + up * 5.6f - right * (facing * 2.2f), 2.8f, wool); // rump tuft
                var head = Position + up * 7.0f + right * (facing * 4.6f);
                r.DrawRect(Position + up * 6.0f + right * (facing * 3.2f), new Vector2(1.4f, 3.6f), wool, rot + facing * 0.5f);
                r.DrawCircle(head, 1.8f, dusk);
                r.DrawCircle(head + right * (facing * 1.1f) + up * 0.3f, 0.6f, Color.Black);
                r.DrawCircle(head + up * 1.6f, 0.8f, new Color(140, 200, 255)); // crest glow
                break;
            }
            case CreatureKind.CinderSkink:
            {
                // Low basalt-hided lizard, ember freckles pulsing down the spine while it
                // basks; legs scissor into a blur when it bolts.
                var hide = Tinted(new Color(66, 50, 48));
                var d = right * facing;
                r.DrawRect(Position - d * 3.4f, new Vector2(4.2f, 1.2f), hide, rot); // tail
                var scur = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = MathF.Sin(t * 20f + _phase + i * 2.0f) * 0.6f * scur;
                    r.DrawRect(Position - up * 0.8f + right * (i * 1.4f), new Vector2(0.7f, 2.2f), hide, rot + a);
                }
                r.DrawCircle(Position, 2.4f, hide);
                r.DrawCircle(Position + d * 2.6f + up * 0.4f, 1.5f, hide);
                for (var i = 0; i < 3; i++)
                {
                    var glow = MathF.Sin(t * 5f + _phase + i * 1.9f) * 0.5f + 0.5f;
                    r.DrawCircle(Position + up * 1.2f - d * (i * 1.6f - 1.2f), 0.6f,
                        Color.Lerp(new Color(200, 90, 30), new Color(255, 200, 90), glow));
                }
                r.DrawCircle(Position + d * 3.2f + up * 0.8f, 0.5f, new Color(255, 220, 120));
                break;
            }
            case CreatureKind.RustBack:
            {
                // Dome-shelled grazer plated in oxidised scrap — it eats the metal-rich
                // gravel, and the shell shows it: rust ridge, oxide speckle, verdigris.
                var shell = Tinted(new Color(140, 84, 52));
                var plate = Tinted(new Color(170, 110, 66));
                var hide = Tinted(new Color(96, 88, 82));
                var plod = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 20f, 1f);
                for (var i = -1; i <= 1; i++)
                {
                    var a = MathF.Sin(t * 5f + _phase + i * 2.1f) * 0.3f * plod;
                    r.DrawRect(Position - up * 1.2f + right * (i * 2.6f), new Vector2(1.2f, 2.6f), hide, rot + a);
                }
                r.DrawCircle(Position + up * 1.6f, Radius, shell);
                r.DrawRect(Position + up * 2.8f, new Vector2(5.6f, 1.2f), plate, rot);
                r.DrawCircle(Position + up * 3.4f, 1.4f, plate);
                var head = Position + right * (facing * (Radius + 1.2f)) + up * 0.6f;
                r.DrawCircle(head, 1.6f, hide);
                r.DrawCircle(head + right * (facing * 0.9f) + up * 0.4f, 0.5f, Color.Black);
                r.DrawCircle(Position + up * 1.8f + right * 1.8f, 0.6f, new Color(210, 140, 60));
                r.DrawCircle(Position + up * 2.4f - right * 1.6f, 0.5f, new Color(96, 150, 130));
                break;
            }
            case CreatureKind.TidePuddler:
            {
                // Glossy shoreline amphibian: squashes on landing, stretches mid-hop, tail
                // fin flicking as if it's swimming through the air.
                var skin = Tinted(new Color(70, 150, 170));
                var belly = Tinted(new Color(150, 210, 210));
                var squish = IsGrounded(planet, up) ? 0.8f : 1.15f;
                var flick = MathF.Sin(t * 12f + _phase) * 0.5f;
                r.DrawRect(Position - right * (facing * (Radius + 1.2f)), new Vector2(2.8f, 1.6f), skin, rot + flick);
                r.DrawCircle(Position, Radius * squish, skin);
                r.DrawCircle(Position - up * 0.6f + right * (facing * 0.6f), Radius * 0.6f, belly);
                var eye = Position + up * (Radius * 0.7f) + right * (facing * 1.2f);
                r.DrawCircle(eye, 1.2f, Color.White);
                r.DrawCircle(eye + right * (facing * 0.4f), 0.7f, Color.Black);
                break;
            }
            case CreatureKind.AcidStrider:
            {
                // Stilt-walker: three splayed legs hold the body high over the vitriol
                // pools it drinks from through a dangling proboscis.
                var chit = Tinted(new Color(112, 128, 60));
                var pale = Tinted(new Color(168, 186, 90));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                for (var i = -1; i <= 1; i++)
                {
                    var a = (moving ? MathF.Sin(t * 8f + _phase + i * 2.1f) * 0.35f : 0f) + i * 0.18f;
                    r.DrawRect(Position + up * 1.4f + right * (i * 2.0f), new Vector2(0.7f, 8.0f), chit, rot + a);
                }
                r.DrawRect(Position + up * 5.4f, new Vector2(7.0f, 2.8f), pale, rot);
                var head = Position + up * 6.0f + right * (facing * 4.4f);
                r.DrawCircle(head, 1.6f, chit);
                r.DrawRect(head + right * (facing * 1.6f) - up * 0.8f, new Vector2(3.0f, 0.6f), chit, rot + facing * 0.9f);
                r.DrawCircle(head + up * 0.6f, 0.6f, new Color(220, 250, 120));
                break;
            }
            case CreatureKind.PrismSnail:
            {
                // A walking geode at snail pace: gliding foot, one eyestalk, and a faceted
                // crystal shell that catches the light.
                var foot = Tinted(new Color(150, 130, 170));
                r.DrawRect(Position - up * 0.6f, new Vector2(Radius * 2.2f, 1.8f), foot, rot);
                var head = Position + right * (facing * (Radius + 0.8f));
                r.DrawCircle(head, 1.3f, foot);
                r.DrawRect(head + up * 1.4f + right * (facing * 0.5f), new Vector2(0.5f, 2.0f), foot, rot + facing * 0.3f);
                r.DrawCircle(head + up * 2.4f + right * (facing * 0.9f), 0.5f, new Color(230, 210, 255));
                var glint = MathF.Sin(t * 2.5f + _phase) * 0.5f + 0.5f;
                var gem = Color.Lerp(new Color(160, 110, 220), new Color(220, 180, 255), glint);
                r.DrawCircle(Position + up * 1.6f - right * (facing * 0.8f), Radius * 0.9f, Tinted(new Color(110, 80, 150)));
                for (var i = 0; i < 3; i++)
                    r.DrawRect(Position + up * (2.2f + (i == 1 ? 1.0f : 0f)) + right * ((i - 1) * 1.8f - facing * 0.8f),
                        new Vector2(1.3f, 2.8f), Tinted(gem), rot + (i - 1) * 0.4f);
                break;
            }
            case CreatureKind.NullMoth:
            {
                // A moth-shaped absence: void-black wings edged in violet, and a single
                // bright mote where a face should be.
                var beat = MathF.Sin(t * 7f + _phase) * 0.8f;
                var wing = Tinted(new Color(30, 22, 44));
                var fringe = new Color(120, 70, 190) * 0.6f;
                r.DrawRect(Position + right * 2.6f + up * 0.5f, new Vector2(5.6f, 2.0f), wing, rot + 1.57f + beat);
                r.DrawRect(Position - right * 2.6f + up * 0.5f, new Vector2(5.6f, 2.0f), wing, rot + 1.57f - beat);
                r.DrawCircle(Position + right * 4.2f + up * 0.5f, 0.7f, fringe);
                r.DrawCircle(Position - right * 4.2f + up * 0.5f, 0.7f, fringe);
                r.DrawCircle(Position, 1.9f, Tinted(new Color(18, 14, 26)));
                r.DrawCircle(Position - up * 1.6f, 1.2f, Tinted(new Color(26, 20, 36)));
                r.DrawCircle(Position + up * 0.6f, 0.6f, new Color(190, 140, 255));
                break;
            }
            case CreatureKind.Civilian:
            {
                // A timid alien citizen: bulbous head over a slim robed body, all-black
                // eyes, a single antenna. Robes come in a few dyes (hashed off _phase) so a
                // street crowd doesn't read as clones; arms fly up when it bolts.
                var dye = (int)(_phase * 10f) % 3;
                var robe = Tinted(dye == 0 ? new Color(90, 130, 150)
                    : dye == 1 ? new Color(140, 105, 160) : new Color(160, 130, 80));
                var skin = Tinted(new Color(180, 195, 175));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                var fleeing = MathF.Abs(Vector2.Dot(Velocity, right)) > 45f;
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = moving ? MathF.Sin(t * 11f + _phase + i * 1.6f) * 0.45f : 0f;
                    r.DrawRect(Position - up * 1.6f + right * (i * 1.0f), new Vector2(0.9f, 3.0f), robe, rot + a);
                }
                r.DrawRect(Position + up * 1.4f, new Vector2(3.8f, 4.6f), robe, rot);
                // Arms: folded at rest, thrown up in panic.
                var armA = fleeing ? 2.4f : 0.5f;
                r.DrawRect(Position + up * (fleeing ? 3.2f : 1.8f) + right * 1.9f, new Vector2(0.8f, 2.6f), skin, rot + armA);
                r.DrawRect(Position + up * (fleeing ? 3.2f : 1.8f) - right * 1.9f, new Vector2(0.8f, 2.6f), skin, rot - armA);
                var head = Position + up * 4.8f;
                r.DrawCircle(head, 2.6f, skin);
                r.DrawCircle(head + right * (facing * 1.1f) + up * 0.3f, 0.9f, Color.Black);
                r.DrawCircle(head - right * (facing * 0.4f) + up * 0.3f, 0.8f, Color.Black);
                // Antenna with a soft mood-light tip.
                r.DrawRect(head + up * 2.6f, new Vector2(0.4f, 2.2f), skin, rot + 0.15f);
                r.DrawCircle(head + up * 3.8f + right * 0.4f, 0.6f,
                    fleeing ? new Color(255, 120, 90) : new Color(130, 220, 210));
                break;
            }
            case CreatureKind.Lizardman:
            {
                // Evil warren-guard: green scaled biped with a swaying tail, pale belly,
                // toothy snout, bone crest — and a bone spear that cocks back and whips
                // forward on the cast. Eyes smoulder red while it hunts.
                var scale = Tinted(new Color(70, 118, 62));
                var belly = Tinted(new Color(140, 160, 100));
                var bone = Tinted(new Color(222, 212, 184));
                // Tail: two sagging segments trailing the facing.
                var sway = MathF.Sin(t * 3f + _phase) * 0.25f;
                r.DrawRect(Position - right * (facing * 3.6f) - up * 0.6f, new Vector2(4.2f, 1.4f), scale, rot + sway * facing);
                r.DrawRect(Position - right * (facing * 6.2f) - up * 1.4f, new Vector2(3.0f, 1.0f), scale, rot + (sway + 0.35f) * facing);
                // Digitigrade legs, striding with actual tangent speed.
                var stride = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = MathF.Sin(t * 12f + _phase + i * 1.6f) * 0.5f * stride;
                    r.DrawRect(Position - up * 1.4f + right * (i * 1.3f), new Vector2(1.1f, 3.4f), scale, rot + a - 0.15f * i);
                }
                r.DrawRect(Position + up * 1.6f, new Vector2(4.2f, 5.0f), scale, rot);
                r.DrawRect(Position + up * 1.2f + right * (facing * 0.8f), new Vector2(2.0f, 3.4f), belly, rot);
                // Head: snout forward, bone crest raking back.
                var head = Position + up * 5.0f + right * (facing * 0.8f);
                r.DrawCircle(head, 2.0f, scale);
                r.DrawRect(head + right * (facing * 2.0f) - up * 0.2f, new Vector2(2.6f, 1.3f), scale, rot);
                r.DrawRect(head + right * (facing * 2.6f) - up * 0.9f, new Vector2(1.4f, 0.6f), bone, rot); // teeth
                r.DrawRect(head - right * (facing * 1.2f) + up * 1.6f, new Vector2(0.9f, 2.4f), bone, rot - facing * 0.7f);
                var eye = _aggroT > 0f ? new Color(255, 70, 40) : new Color(230, 200, 70);
                r.DrawCircle(head + right * (facing * 1.2f) + up * 0.6f, 0.7f, eye);
                // Bone spear: shouldered on patrol, cocked back and whipped through the
                // cast while _swing runs.
                var castA = _swing > 0f ? MathF.Sin(_swing * 18f) * 1.1f : MathF.Sin(t * 1.7f + _phase) * 0.1f;
                var spearDir = Rotate(right * facing, facing * (0.35f - castA));
                var grip = Position + right * (facing * 2.6f) + up * 2.4f;
                var sAng = MathF.Atan2(spearDir.Y, spearDir.X);
                r.DrawRect(grip + spearDir * 1.5f, new Vector2(8.0f, 0.8f), Tinted(new Color(150, 120, 80)), sAng);
                r.DrawRect(grip + spearDir * 5.8f, new Vector2(2.2f, 1.2f), bone, sAng);
                break;
            }
        }

        // Burning creatures get a flickering ember dot above them, whatever the species.
        if (BurnSeconds > 0f)
        {
            var flick = MathF.Sin(t * 22f + Position.X) * 0.5f + 0.5f;
            var emCol = Color.Lerp(new Color(255, 130, 60), new Color(255, 220, 100), flick);
            r.DrawCircle(Position + up * (Radius + 2f), 1.2f + flick * 0.6f, emCol);
        }
    }

    /// <summary>Light emission for the lighting pass. Magma slugs are living coals; cave eyes
    /// carry a faint cold gleam so you spot one drifting down a dark tunnel before it spots you.</summary>
    public void AddLight(Renderer r)
    {
        switch (Kind)
        {
            case CreatureKind.MagmaSlug:
            {
                var flick = MathF.Sin(r.Time * 7f + _phase) * 4f;
                r.AddLight(Position, 26f + flick, new Color(255, 120, 40));
                break;
            }
            case CreatureKind.CaveEye:
                r.AddLight(Position, 14f, new Color(170, 180, 220));
                break;
            case CreatureKind.HornedDelver:
                // Hunting delvers announce themselves with a red eye-gleam in the dark.
                if (_aggroT > 0f) r.AddLight(Position, 12f, new Color(255, 60, 40));
                break;
            case CreatureKind.MoleBeast:
                if (_provokedT > 0f) r.AddLight(Position, 10f, new Color(255, 70, 50));
                break;
            case CreatureKind.SporeBat:
                r.AddLight(Position, 11f, new Color(140, 210, 130));
                break;
            case CreatureKind.VoidWraith:
            {
                var pulse = MathF.Sin(r.Time * 5f + _phase) * 4f;
                r.AddLight(Position, 22f + pulse, new Color(150, 80, 220));
                break;
            }
            case CreatureKind.AcidSpitter:
                // Throat sac glows as the next glob charges — the tell in a dark cave.
                r.AddLight(Position, 8f + (1f - MathHelper.Clamp(_cd / 2.4f, 0f, 1f)) * 8f,
                    new Color(160, 220, 80));
                break;
            case CreatureKind.BomberBeetle:
                // An armed bomber floods its corridor with strobing warning light.
                if (_fuse > 0f)
                    r.AddLight(Position, 20f + MathF.Sin(r.Time * 30f) * 8f, new Color(255, 120, 50));
                break;
            case CreatureKind.CaveSlime:
            case CreatureKind.Slimelet:
                r.AddLight(Position, 9f, new Color(90, 200, 160));
                break;
            case CreatureKind.CinderSkink:
                // Ember freckles — a faint coal-glow crossing the night-side basalt.
                r.AddLight(Position, 8f + MathF.Sin(r.Time * 5f + _phase) * 2f, new Color(255, 140, 50));
                break;
            case CreatureKind.PrismSnail:
                r.AddLight(Position, 12f, new Color(190, 140, 240));
                break;
            case CreatureKind.NullMoth:
                r.AddLight(Position, 10f, new Color(150, 90, 220));
                break;
        }
    }

    // ---------------------------------------------------------------- small math helpers

    private static float MoveToward(float v, float target, float maxDelta)
    {
        var d = target - v;
        if (MathF.Abs(d) <= maxDelta) return target;
        return v + MathF.Sign(d) * maxDelta;
    }

    private static Vector2 Rotate(Vector2 v, float a)
    {
        var c = MathF.Cos(a);
        var s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= MathHelper.TwoPi;
        while (a < -MathF.PI) a += MathHelper.TwoPi;
        return a;
    }
}
