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
    Peacekeeper,  // city militia — armed alien: neutral to the dwarf, guns down hostile invaders
    Saucer,       // city air patrol — disc-craft cruising over the towers, guns down invaders
    BigSaucer,    // city command ship — huge saucer: fires a fan of lasers + a tractor beam that drags/slows the player
    // Aquatic-only fauna (spawned into the lakes; see SpawnDirector's water spawner):
    AlienWhale,   // gentle glowing leviathan cruising the deep basins — never leaves the water
    AlienCrab,    // armoured lakebed scuttler — territorial, pinches anything that wades close
    // Hostile sea monsters — the deep water is dangerous now:
    AlienShark,   // sleek torpedo predator — circles, then rushes anything swimming in its water
    Gulper,       // deep-water anglerfish — drifts with a glowing lure, then lunges a huge bite
    Brinespitter, // reef lurker that spits water-globs at swimmers from range (aquatic artillery)
    // The belt natives (the Hollow asteroid, biome "belt") — the ONLY things living there,
    // all of them creatures that never needed air (Metroid / Dead Space / Half-Life school):
    Moonlet,      // floating boulder-parasite — falls into orbit around the dwarf, then slingshots itself at them
    VacLeech,     // pale vacuum lamprey — pounces, clamps onto the suit, and siphons the AIR TANK, not blood
    Glimmermaw,   // near-invisible drifting maw dangling a gem-bright lure — the sparkle in the dark is teeth
    StarJelly,    // vacuum medusa — drifts over the airless surface trailing stinging filaments; touching it burns
    VoidBarnacle, // cave-wall ambusher — anchors to rock and REELS prey in on an invisible gravity tongue
    // The cratered moon's own natives (biome "moon" — shares Moonlet/VacLeech/StarJelly
    // with the belt, plus these two):
    Selenite,     // crystalline moon-spider — glassy shard-legged pouncer that haunts the crater caves
    DustDevil,    // charged regolith vortex — a spinning column of moon dust that hounds anything grounded
    // Cave bandits: humanoid outcasts carrying WEAKER versions of the dwarf's own arsenal —
    // the first enemies that shoot back with guns instead of glands.
    Marauder,     // ground bandit with a slug pistol — holds a firing band and pops off aimed rounds
    Raider,       // jetpack bandit — hovers on a sputtering pack and rakes short SMG bursts
    Pyro,         // suited brute lugging a tank flamethrower — closes in and hoses real fire
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

    /// <summary>Pre-seeded population (SpawnDirector.PopulateWorld): city dwellers, warren
    /// garrisons, lake fauna and the scattered wild herds that exist from the moment the
    /// world does — BEFORE the player arrives anywhere. Residents are never distance-culled;
    /// Game1 freezes their updates beyond ~900px instead, so the planet-wide census costs
    /// nothing until the player walks into it.</summary>
    public bool Resident;

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
    private float _patrolAng = float.NaN; // Saucer: bearing of the city it guards (radians)
    private float _patrolHalf;    // Saucer: half-width of the patrol band around _patrolAng
    private Vector2? _shelter;    // Civilian/Peacekeeper: cached nearest doorway while taking cover
    private float _shelterCd;     // take-cover: countdown to re-find the nearest shelter
    private readonly float _phase; // per-creature animation phase offset
    private float _aggroT;         // HornedDelver: seconds of aggro memory remaining
    private float _swing;          // HornedDelver: pickaxe swing / spit-maw / blink-shimmer timer
    private Vector2 _gunAim;       // bandits: last aim direction (drives the drawn weapon)
    private Vector2 _lungeDir;     // gulper: locked lunge direction during a strike
    private int _burst;            // Raider: rounds left in the current SMG burst
    private float _burstT;         // Raider: delay until the next round in the burst
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
            // Civilians stay soft (a fleeing citizen isn't a bullet sponge); every OTHER
            // alien-civilisation entity is now 4× hardier — the guards, air patrol and warren
            // warriors take real firepower to drop.
            case CreatureKind.Civilian:
                Radius = 3.5f; Health = 24f; MoveSpeed = 42f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.Lizardman:
                Radius = 4f; Health = 120f; MoveSpeed = 58f; ContactDamage = 12f;
                _cd = 0.8f + (float)Random.Shared.NextDouble(); // first spear is never instant
                break;
            case CreatureKind.Peacekeeper:
                Radius = 3.8f; Health = 208f; MoveSpeed = 48f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.Saucer:
                Radius = 6f; Health = 176f; MoveSpeed = 55f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.AlienWhale:
                Radius = 11f; Health = 90f; MoveSpeed = 26f; ContactDamage = 0f; Hostile = false;
                break;
            case CreatureKind.AlienCrab:
                Radius = 4.5f; Health = 34f; MoveSpeed = 26f; ContactDamage = 10f;
                break;
            case CreatureKind.AlienShark:
                Radius = 6f; Health = 46f; MoveSpeed = 96f; ContactDamage = 18f;
                break;
            case CreatureKind.Gulper:
                Radius = 5.5f; Health = 60f; MoveSpeed = 38f; ContactDamage = 24f;
                break;
            case CreatureKind.Brinespitter:
                Radius = 4.5f; Health = 40f; MoveSpeed = 30f; ContactDamage = 8f;
                break;
            case CreatureKind.Moonlet:
                Radius = 4f; Health = 26f; MoveSpeed = 40f; ContactDamage = 11f;
                _cd = 2f + (float)Random.Shared.NextDouble() * 1.5f; // first slingshot is never instant
                break;
            case CreatureKind.VacLeech:
                // Feeble bite — the real theft is the air siphon in the contact block below.
                Radius = 2.4f; Health = 8f; MoveSpeed = 92f; ContactDamage = 2f;
                break;
            case CreatureKind.Glimmermaw:
                Radius = 4.2f; Health = 24f; MoveSpeed = 24f; ContactDamage = 13f;
                _cd = 1f + (float)Random.Shared.NextDouble();
                break;
            case CreatureKind.StarJelly:
                // Not a hunter — a drifting hazard. It never chases; you drift into IT.
                Radius = 4.5f; Health = 12f; MoveSpeed = 18f; ContactDamage = 7f;
                break;
            case CreatureKind.VoidBarnacle:
                Radius = 4.5f; Health = 40f; MoveSpeed = 0f; ContactDamage = 15f;
                break;
            case CreatureKind.Selenite:
                Radius = 3.2f; Health = 14f; MoveSpeed = 78f; ContactDamage = 9f;
                break;
            case CreatureKind.DustDevil:
                Radius = 4f; Health = 16f; MoveSpeed = 62f; ContactDamage = 7f;
                break;
            case CreatureKind.Marauder:
                Radius = 4f; Health = 26f; MoveSpeed = 42f; ContactDamage = 6f;
                break;
            case CreatureKind.Raider:
                Radius = 4f; Health = 22f; MoveSpeed = 50f; ContactDamage = 6f;
                break;
            case CreatureKind.Pyro:
                Radius = 4.4f; Health = 38f; MoveSpeed = 30f; ContactDamage = 10f;
                break;
        }
    }

    /// <summary>Peacekeeper targeting, set each frame by Game1's militia pass: the nearest
    /// hostile invader (or the rampaging titan) in guard range, or null when the street is
    /// quiet. The tick moves to engage it; Game1 fires the actual bolts.</summary>
    public Vector2? GuardTarget;

    /// <summary>Peacekeeper bolt cooldown, ticked and consumed by Game1's militia pass.</summary>
    public float GuardFireCd;

    /// <summary>Called by Game1 the frame a peacekeeper bolt leaves — briefly lights the
    /// muzzle-flash in the sprite (rides the shared _swing animation timer).</summary>
    public void GuardMuzzleFlash() => _swing = 0.2f;

    /// <summary>City-alien disaster response, set each frame by Game1 while a planet disaster
    /// is live (see AmbientDirector.DisasterActive): civilians and peacekeepers break off their
    /// routine and run for the nearest building doorway to shelter until the sky clears. Never
    /// set on saucers — the air patrol is disaster-proof and keeps flying overhead.</summary>
    public bool TakeCover;

    /// <summary>Set the frame a lizardman FIRST sights prey (calm → aggro edge). Game1's
    /// war-cry pass consumes it: every lizardman in a wide radius gets
    /// <see cref="RallyToWar"/>, so aggroing one guard brings the warren.</summary>
    public bool CallingBackup;

    /// <summary>Answer a warren war-cry: pick up the hunt as if the prey were in sight.
    /// Deliberately does NOT set <see cref="CallingBackup"/> — rallied guards don't chain
    /// fresh cries, one shriek per sighting.</summary>
    public void RallyToWar()
    {
        _aggroT = MathF.Max(_aggroT, 7f);
        _provokedT = MathF.Max(_provokedT, 8f);
    }

    public bool IsSkyKind => Kind is CreatureKind.SkyMoth or CreatureKind.SkyStinger
        or CreatureKind.NullMoth or CreatureKind.Saucer or CreatureKind.StarJelly;
    public bool IsSurfaceKind => Kind is CreatureKind.Grazer or CreatureKind.Hopper
        or CreatureKind.SnowLoper or CreatureKind.CinderSkink or CreatureKind.RustBack
        or CreatureKind.TidePuddler or CreatureKind.AcidStrider or CreatureKind.PrismSnail
        or CreatureKind.Civilian or CreatureKind.Peacekeeper
        // The moon's dust devil is surface-budgeted like the herds — but it's no herd
        // animal: it hunts (the one hostile the surface spawner places).
        or CreatureKind.DustDevil;
    /// <summary>Aquatic-only kinds — spawned into water by the director's lake spawner and
    /// budgeted separately from every land habitat.</summary>
    public bool IsWaterKind => Kind is CreatureKind.AlienWhale or CreatureKind.AlienCrab
        or CreatureKind.AlienShark or CreatureKind.Gulper or CreatureKind.Brinespitter;
    public bool IsCaveKind => !IsSkyKind && !IsSurfaceKind && !IsWaterKind;

    /// <summary>Land kinds that can also swim: submerged, buoyancy replaces the plummet
    /// (see the swim block in <see cref="Update"/>). Amphibians paddle by nature; lizardmen
    /// swim like the reptiles they are — a lake is no moat against the warren; grubs are
    /// sealed, buoyant sacks. Everything else sinks and walks the bottom.</summary>
    public bool Swims => Kind is CreatureKind.TidePuddler or CreatureKind.Lizardman
        or CreatureKind.Grub or CreatureKind.AlienWhale;

    /// <summary>Whether this kind can live inside a hazard cell — the gate the spawner uses so a
    /// creature is never dropped into a material that would drown/burn/dissolve it, and the
    /// filter the environmental-damage tick uses once it's alive. Water: only swimmers and
    /// water-native kinds. Lava/fire: molten-blooded fire fauna (magma slugs, cinder skinks).
    /// Acid: the acid-world natives (striders and acid spitters). Everything else stays out of
    /// the pool. Anything not a body-contact hazard (gas, empty) never blocks a spawn.</summary>
    public bool ImmuneTo(Material m) => m switch
    {
        Material.Water => Swims || IsWaterKind,
        // The pyro's suit is rated for its own hose (fire only — lava still cooks it).
        Material.Fire => Kind is CreatureKind.MagmaSlug or CreatureKind.CinderSkink
            or CreatureKind.Pyro,
        Material.Lava => Kind is CreatureKind.MagmaSlug or CreatureKind.CinderSkink,
        Material.Acid => Kind is CreatureKind.AcidStrider or CreatureKind.AcidSpitter,
        _ => true,
    };

    // Environmental hazard contact (per second of overlap). Tuned to creature HP (5-45,
    // aliens 2x): lava kills a grub in under a second and a big brute in a couple — the same
    // "nothing wades through lava" rule the dwarf lives under — while acid gives even small
    // fauna a beat to scramble out. Fire mostly works through the burn debuff, so a creature
    // that dashes clear keeps cooking briefly instead of shrugging the flame off.
    private const float LavaDps = 26f;
    private const float AcidDps = 12f;
    private const float FireDps = 5f;
    /// <summary>Seconds between hazard probes. SampleHazardsNear walks a body-sized cell
    /// window, so ticking it every frame for every creature near a pool adds up; damage is
    /// applied per-period at the same per-second rates. Started at a random phase so a herd
    /// standing in one pool doesn't probe in lockstep.</summary>
    private const float HazardProbePeriod = 0.2f;
    private float _hazardProbeT = (float)Random.Shared.NextDouble() * HazardProbePeriod;

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

        // Environmental hazard contact: the same cell probe the dwarf uses, throttled and
        // immunity-filtered — lava sears anything not molten-blooded, acid eats anything
        // not acid-native, open flame sets the burn debuff alight. Deliberately NOT wired
        // to aggro: a grazer scalded by a lava seep flees pain, it doesn't blame the dwarf
        // (HitFlash doubles as the provocation signal above, so it must stay untouched).
        _hazardProbeT -= dt;
        if (_hazardProbeT <= 0f)
        {
            _hazardProbeT += HazardProbePeriod;
            var (lava, acid, _, fire) = cells.SampleHazardsNear(Position, Radius + 1.5f);
            if (lava > 0 && !ImmuneTo(Material.Lava))
            {
                Health -= LavaDps * HazardProbePeriod;
                BurnSeconds = MathF.Max(BurnSeconds, 1.5f);
            }
            if (acid > 0 && !ImmuneTo(Material.Acid))
                Health -= AcidDps * HazardProbePeriod;
            if (fire > 0 && !ImmuneTo(Material.Fire))
            {
                Health -= FireDps * HazardProbePeriod;
                BurnSeconds = MathF.Max(BurnSeconds, 1.5f);
            }
        }

        // A shot digger holds a grudge — the pain flash doubles as the provocation signal.
        // All the tunnelling kinds use it: unprovoked, they wander-dig their own galleries
        // and leave the dwarf alone; only a hit (or point-blank crowding, per-kind) turns
        // their jaws toward the player.
        if (HitFlash > 0f && Kind is CreatureKind.MoleBeast or CreatureKind.Borer
            or CreatureKind.Centipede or CreatureKind.HornedDelver) _provokedT = 8f;
        if (_provokedT > 0f) _provokedT -= dt;
        if (HitFlash > 0) HitFlash -= dt;
        var speedMul = FreezeSeconds > 0f ? 0.5f : 1.0f;

        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        var toPlayer = player.Position - Position;
        var dist = toPlayer.Length();

        // Door-users tidy up: standing beside an open door, they occasionally swing it
        // shut behind them (rare roll so doorways aren't slamming nonstop, and never with
        // the dwarf close enough to be standing in the frame).
        if (CanUseDoors(Kind) && dist > 60f && Random.Shared.Next(700) == 0)
            foreach (var side in new[] { 1f, -1f })
            {
                var beside = Position + right * (side * (Radius + 5f));
                var (bx, by) = planet.WorldToTile(beside);
                if (planet.Get(bx, by) != TileKind.DoorOpen) continue;
                SetDoorRun(planet, up, beside, TileKind.DoorClosed);
                break;
            }

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
            case CreatureKind.Marauder:   TickMarauder(dt, planet, up, right, toPlayer, dist, speedMul, shots); break;
            case CreatureKind.Raider:     TickRaider(dt, planet, up, right, toPlayer, dist, speedMul, shots); break;
            case CreatureKind.Pyro:       TickPyro(dt, planet, cells, up, right, toPlayer, dist, speedMul); break;
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
            case CreatureKind.Peacekeeper: TickPeacekeeper(dt, planet, up, right, speedMul); break;
            case CreatureKind.Saucer:     TickSaucer(dt, planet, up, right, speedMul); break;
            case CreatureKind.AlienWhale: TickWhale(dt, planet, cells, up, right, speedMul); break;
            // The crab is a lakebed walker on the grub brain with a short territorial fuse —
            // it scuttles the bottom rather than swimming, so no special water physics.
            case CreatureKind.AlienCrab:  TickCrab(dt, planet, up, right, toPlayer, dist, speedMul); break;
            // Hostile sea monsters: shark torpedo-hunts, gulper drifts then lunges, and the
            // brinespitter lobs water-globs from range (its own artillery brain via shots).
            case CreatureKind.AlienShark: TickShark(dt, planet, cells, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Gulper:     TickGulper(dt, planet, cells, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Brinespitter: TickBrinespitter(dt, planet, cells, up, right, toPlayer, dist, speedMul, shots); break;
            // Belt natives. The moonlet and glimmermaw run bespoke brains below; the leech
            // hunts on the skitterer's pounce brain (its air siphon lives in the contact
            // block, not the tick).
            case CreatureKind.Moonlet:    TickMoonlet(dt, toPlayer, dist, speedMul); break;
            case CreatureKind.VacLeech:   TickSkitterer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.Glimmermaw: TickGlimmermaw(dt, planet, toPlayer, dist, speedMul); break;
            // The jelly rides the moth's drift brain — a vacuum needs no wings.
            case CreatureKind.StarJelly:  TickFlyer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.VoidBarnacle: TickBarnacle(dt, planet, toPlayer, dist, player); break;
            // Moon natives on proven brains: the selenite pounces like a skitterer, the
            // dust devil hounds along the surface on the grub chase — their identity is
            // in the crystal shards and the spinning regolith column.
            case CreatureKind.Selenite:   TickSkitterer(dt, planet, up, right, toPlayer, dist, speedMul); break;
            case CreatureKind.DustDevil:  TickGrub(dt, planet, up, right, toPlayer, dist, speedMul); break;
        }

        // Land swimmers: submerged, buoyancy replaces the plummet the tick just applied —
        // hunters paddle toward their prey's level, the rest stroke gently up toward air.
        // (The whale runs its own full water model in its tick; the crab sinks on purpose.)
        if (Swims && Kind != CreatureKind.AlienWhale
            && cells.CountWaterNear(Position, Radius + 1f) >= 3)
        {
            var swimT = Vector2.Dot(Velocity, right);
            var wantN = Hostile && dist < 260f && dist > 0.01f
                ? MathHelper.Clamp(Vector2.Dot(toPlayer, up) * 0.5f, -45f, 45f)
                : 26f;
            // The correction rate must clearly beat the ~320/s gravity the tick already
            // applied, or buoyancy only slows the sink instead of winning it.
            var swimN = MoveToward(Vector2.Dot(Velocity, up), wantN, 900f * dt);
            Velocity = right * (swimT * MathF.Exp(-1.1f * dt)) + up * swimN;
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
                // The vac leech's real theft: clamped on, it siphons the suit's air tank —
                // on the airless Hollow that meter IS the dive clock.
                if (Kind == CreatureKind.VacLeech)
                    player.Oxygen = MathF.Max(0f, player.Oxygen - 14f * dt);
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
        // Citizens caught in a disaster drop everything and run for shelter.
        if (TakeCover && TickTakeCover(dt, planet, up, right, speedMul)) return;
        Wander -= dt;
        if (Wander <= 0)
        {
            Wander = 2f + (float)Random.Shared.NextDouble() * 3f;
            _amble = Random.Shared.Next(3) - 1; // stroll left / graze in place / stroll right
        }
        // Terrain-aware stroll: turn at tall walls (building hulls) and cliff edges.
        var moveAxis = NavAxis(planet, up, right, _amble * 0.5f, avoidCliffs: true);
        // Spooked: bolt directly away from the player, faster than the amble cap — panic
        // overrides caution, so a fleeing animal WILL take the drop.
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
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        else
        {
            // Mid-hop: ballistic, with the shared terminal-velocity cap.
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
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
        // Provocation-gated hunting: an unprovoked borer just chews its own galleries. It
        // only turns its jaws on the dwarf after taking a hit (8s grudge) or being crowded
        // at point-blank range.
        if ((_provokedT > 0f || dist < 45f) && dist > 0.01f)
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
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
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
                // Jaws vs engineering: city architecture chews at power 1 (a borer takes
                // ~10s per alloy tile instead of ~2), and the deep hard layers (basalt,
                // obsidian, dense ores) resist at a third power — creatures rummage the
                // soft crust freely but grind slowly through anything built or deep.
                var bitePow = power;
                if (k is TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick)
                    bitePow = 1;
                else if (Tiles.Hardness(k) >= 4)
                    bitePow = Math.Max(1, power / 3);
                if (planet.Mine(x, y, bitePow) is { } broken)
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
        // Provocation-gated like the other diggers: a delver on its rounds ignores a dwarf
        // it merely notices — it hunts (and mines toward) prey that hit it or walked into
        // its face. Once triggered, the old 220px memory band keeps the chase sticky.
        if (dist < 220f && (_provokedT > 0f || dist < 70f)) _aggroT = 6f; else _aggroT -= dt;

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
        // Same provocation gate as the borer: it meanders until hit or crowded.
        if ((_provokedT > 0f || dist < 45f) && dist > 0.01f)
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
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
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
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        // Big claws but an unhurried pace — half the old bite rate, so a mole's burrow
        // creeps rather than races.
        Chew(dt, planet, physics, cells, _digDir, 0.4f, 9);
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

    // ---------------------------------------------------------------- belt natives

    /// <summary>Moonlet: a boulder-parasite that treats anything massive as a primary. Far
    /// from prey it drifts weightlessly through the caves; close in, it falls into a tight
    /// orbit around the DWARF and circles like a captured moon — then drops out of orbit
    /// and slingshots itself straight at them, re-entering orbit after the pass. Shooting
    /// the rock is the only way off the gravity leash.</summary>
    private void TickMoonlet(float dt, Vector2 toPlayer, float dist, float speedMul)
    {
        _cd -= dt;
        if (_swing > 0f)
        {
            // Mid-slingshot: pure ballistics, no steering — dodge it and it sails past.
            _swing -= dt;
            return;
        }
        if (dist < 300f && dist > 0.01f)
        {
            if (_cd <= 0f && dist < 190f)
            {
                Velocity = toPlayer / dist * 270f * speedMul;
                _cd = 3.4f;
                _swing = 0.55f;
                return;
            }
            // Servo onto the orbit slot circling the dwarf.
            _heading += 2.1f * _orbitSign * dt;
            var anchor = Position + toPlayer;
            var slot = anchor + new Vector2(MathF.Cos(_heading), MathF.Sin(_heading)) * 74f;
            Velocity = Vector2.Lerp(Velocity, (slot - Position) * 4f,
                MathHelper.Clamp(dt * 6f, 0f, 1f));
            var spd = Velocity.Length();
            var cap = 170f * speedMul;
            if (spd > cap) Velocity *= cap / spd;
        }
        else
        {
            // Unmoored: a slow weightless drift, re-aimed every few seconds. It's a rock
            // that floats — on the half-gravity Hollow, nobody questions it.
            Wander -= dt;
            if (Wander <= 0f)
            {
                Wander = 2f + (float)Random.Shared.NextDouble() * 2.5f;
                _heading = (float)Random.Shared.NextDouble() * MathF.Tau;
            }
            var drift = new Vector2(MathF.Cos(_heading), MathF.Sin(_heading)) * 18f;
            Velocity = Vector2.Lerp(Velocity, drift, MathHelper.Clamp(dt * 2f, 0f, 1f));
        }
    }

    /// <summary>Glimmermaw: a barely-visible drifting maw dangling a gem-bright lure. It
    /// patrols the tunnels on the cave-eye brain but never chases — the lure does the
    /// hunting, glinting like a dropped gem across a dark cave. Anything that comes to
    /// collect gets the lunge.</summary>
    private void TickGlimmermaw(float dt, Planet planet, Vector2 toPlayer, float dist, float speedMul)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;
        if (_cd <= 0f && dist < 105f && dist > 0.01f)
        {
            Velocity = toPlayer / dist * 310f * speedMul;
            _cd = 2.8f;
            _swing = 0.5f;
            return;
        }
        if (_swing > 0f) return;   // mid-lunge: commit
        // aggro 0: it drifts its patrol loop and lets the lure bring dinner to it.
        TickCaveEye(dt, planet, toPlayer, dist, speedMul, aggro: 0f);
    }

    /// <summary>The lure's world position — a stalk length above the maw. Shared by the
    /// sprite, the light pass, and nothing else (it has no hitbox; the maw is the hitbox).</summary>
    private Vector2 LurePos(Planet planet)
    {
        var up = planet.UpAt(Position);
        var sway = MathF.Sin(_phase + _heading) * 2f;
        return Position + up * (Radius + 5.5f) + new Vector2(-up.Y, up.X) * sway;
    }

    /// <summary>VoidBarnacle: the Half-Life-barnacle of the belt. It cements itself to the
    /// rock where it spawned and never moves again; anything that drifts inside its reach
    /// (with a clear line) is REELED IN — a steady velocity pull toward the shell, fought by
    /// walking/jetting away — until the beak's contact damage does the rest. Shooting it is
    /// the reliable out. <see cref="Pulling"/> is read by the sprite to draw the tongue.</summary>
    private void TickBarnacle(float dt, Planet planet, Vector2 toPlayer, float dist, Player player)
    {
        if (!_rooted)
        {
            // Settle first, cement second: fall until the shell rests on rock and the
            // collider has had its say, THEN root there. Rooting at the raw spawn point
            // re-fights the push-out every tick (the body is wider than one tile), and in
            // a tight slit that fight can walk the shell into solid rock.
            var up0 = planet.UpAt(Position);
            Velocity = up0 * MathF.Max(Vector2.Dot(Velocity, up0) - Grav(planet) * dt, -120f);
            if (IsGrounded(planet, up0))
            {
                _rooted = true;
                _root = Position;
                Velocity = Vector2.Zero;
            }
            return;
        }
        Position = _root;
        Velocity = Vector2.Zero;
        Pulling = dist < 140f && dist > 6f && HasLineOfSight(planet, toPlayer, dist);
        if (Pulling)
        {
            // Constant reel plus a bite of extra grip up close — escapable at the rim with
            // a determined walk, a real fight inside half range.
            var grip = MathHelper.Lerp(330f, 160f, dist / 140f);
            player.Velocity -= toPlayer / dist * grip * dt;
        }
    }

    /// <summary>Set each tick a void barnacle has prey on the tongue — drawn by its sprite.</summary>
    public bool Pulling;

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
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
            Velocity = right * vT + up * vN;
        }
        else
        {
            var vN = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -260f);
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

    /// <summary>Marauder: a cave bandit with a slug pistol — the budget version of the
    /// dwarf's own sidearm. It walks to hold a mid firing band (backing off when crowded,
    /// ambling closer when the mark drifts out of range) and pops off aimed rounds with a
    /// touch of scatter. No gland tricks, no burrowing: it fights exactly like the player's
    /// early game, which is what makes it readable.</summary>
    private void TickMarauder(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;

        float moveAxis;
        if (dist < 70f) moveAxis = -MathF.Sign(Vector2.Dot(toPlayer, right));
        else if (dist < 200f) moveAxis = 0f; // in the band — plant feet and shoot
        else if (dist < 340f) moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 2f + (float)Random.Shared.NextDouble() * 3f;
            moveAxis = MathF.Sin(Wander * 1.6f) * 0.5f;
        }
        moveAxis = NavAxis(planet, up, right, moveAxis, avoidCliffs: false);
        GroundMove(dt, planet, up, right, moveAxis, speedMul);

        if (dist < 220f && dist > 0.01f && _cd <= 0f && shots is not null
            && HasLineOfSight(planet, toPlayer, dist))
        {
            var scatter = ((float)Random.Shared.NextDouble() - 0.5f) * 0.12f;
            var dir = Rotate(toPlayer / dist, scatter);
            _gunAim = dir;
            shots.Add(new TitanProjectile(Position + dir * (Radius + 3f), dir * 300f,
                TitanShotKind.Slug, damage: 6f));
            _cd = 1.5f + (float)Random.Shared.NextDouble() * 0.7f;
            _swing = 0.15f; // muzzle flash
        }
        else if (dist > 0.01f) _gunAim = toPlayer / dist;
    }

    /// <summary>Raider: a bandit strapped to a sputtering jetpack with a machine-pistol —
    /// the flying, spraying cousin of the marauder. It hovers a bobbing station diagonal-up
    /// from its mark (jetpacks fight from where legs can't) and rakes three-round bursts
    /// with real spread. The pack physics are simple hover-seek, but the exhaust flame in
    /// the sprite sells it.</summary>
    private void TickRaider(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;

        // Hover station: off to a side and above the player, drifting with a slow figure
        // sway so it never sits still enough to line up on easily.
        var side = MathF.Sin(_phase) >= 0f ? 1f : -1f;
        var sway = MathF.Sin(Wander * 1.3f + _phase) * 18f;
        Wander += dt;
        var station = -toPlayer + right * (side * 70f + sway) + up * 55f;
        if (dist > 260f) station = -toPlayer * 0.4f; // far away: just close the gap
        var seek = station.LengthSquared() > 1f ? Vector2.Normalize(station) : Vector2.Zero;
        Velocity = MoveTowardV(Velocity, seek * MoveSpeed * speedMul * 1.4f, 260f * dt);
        // The pack fights gravity imperfectly — a visible sag it keeps correcting.
        Velocity += up * (MathF.Sin(Wander * 6f + _phase) * 14f - 6f) * dt;

        if (dist < 240f && dist > 0.01f && shots is not null
            && HasLineOfSight(planet, toPlayer, dist))
        {
            _gunAim = toPlayer / dist;
            if (_cd <= 0f && _burst <= 0) { _burst = 3; _cd = 2.2f + (float)Random.Shared.NextDouble(); }
            _burstT -= dt;
            if (_burst > 0 && _burstT <= 0f)
            {
                _burst--;
                _burstT = 0.13f;
                var scatter = ((float)Random.Shared.NextDouble() - 0.5f) * 0.2f;
                var dir = Rotate(toPlayer / dist, scatter);
                shots.Add(new TitanProjectile(Position + dir * (Radius + 3f), dir * 280f,
                    TitanShotKind.Slug, damage: 4f));
                _swing = 0.1f;
            }
        }
        else _burst = 0;
    }

    /// <summary>Pyro: a heavy in a scorched pressure suit lugging a tank flamethrower — the
    /// weaker sibling of the dwarf's own hose. It stomps into close range and gouts REAL
    /// fire cells at its mark (immune to its own flame); everything else about the fire —
    /// ignition, light, the burn — is the cell sim's problem, exactly like the player's
    /// weapon. Kill it at range or wear the burn.</summary>
    private void TickPyro(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        if (_swing > 0f) _swing -= dt;
        _cd -= dt;

        float moveAxis;
        if (dist < 45f) moveAxis = -MathF.Sign(Vector2.Dot(toPlayer, right)) * 0.5f;
        else if (dist < 260f) moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right));
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 3f + (float)Random.Shared.NextDouble() * 3f;
            moveAxis = MathF.Sin(Wander * 1.4f) * 0.4f;
        }
        moveAxis = NavAxis(planet, up, right, moveAxis, avoidCliffs: false);
        GroundMove(dt, planet, up, right, moveAxis, speedMul);

        if (dist < 95f && dist > 0.01f && _cd <= 0f
            && HasLineOfSight(planet, toPlayer, dist))
        {
            var dir = toPlayer / dist;
            _gunAim = dir;
            for (var i = 0; i < 3; i++)
            {
                var spread = ((float)Random.Shared.NextDouble() - 0.5f) * 0.4f;
                cells.LaunchAtWorld(Position + dir * (Radius + 3f),
                    Rotate(dir, spread) * (150f + (float)Random.Shared.NextDouble() * 60f),
                    Material.Fire);
            }
            _cd = 0.35f;
            _swing = 0.35f;
        }
        else if (dist > 0.01f) _gunAim = toPlayer / dist;
    }

    private static Vector2 MoveTowardV(Vector2 v, Vector2 target, float maxDelta)
    {
        var d = target - v;
        var len = d.Length();
        return len <= maxDelta ? target : v + d / len * maxDelta;
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
        if (dist < 240f)
        {
            // Fresh sighting (calm → aggro edge): shriek for backup. Game1's war-cry pass
            // rallies every guard in a wide radius onto the same hunt.
            if (_aggroT <= 0f) CallingBackup = true;
            _aggroT = 7f;
        }
        else
        {
            _aggroT -= dt;
        }

        if (_aggroT > 0f)
        {
            var tDist = Vector2.Dot(toPlayer, right);
            var moveAxis = MathF.Abs(tDist) > 8f ? MathF.Sign(tDist) : 0f;
            // A tall wall stops the charge (no endless hopping) — the spear arm takes over.
            GroundMove(dt, planet, up, right,
                NavAxis(planet, up, right, moveAxis, avoidCliffs: false), speedMul);

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
            // Patrol with terrain sense: about-face at hall walls and shaft edges.
            GroundMove(dt, planet, up, right,
                NavAxis(planet, up, right, _amble * 0.6f, avoidCliffs: true), speedMul);
        }
    }

    /// <summary>Peacekeeper: the city militia. Off duty it walks a beat like any citizen.
    /// When Game1's militia pass hands it a target (the nearest hostile invader, or the
    /// titan), it advances to firing range and holds there, facing the threat — the actual
    /// bolts are fired by Game1, which owns both the creature list and the projectile
    /// list. It never targets the dwarf: the player is a fellow neutral to the city.</summary>
    private void TickPeacekeeper(float dt, Planet planet, Vector2 up, Vector2 right, float speedMul)
    {
        if (_swing > 0f) _swing -= dt;
        if (GuardTarget is { } threat)
        {
            var toThreat = threat - Position;
            var tDist = Vector2.Dot(toThreat, right);
            // Close to firing range, but hold a standoff band — militia don't melee. A
            // tall wall in the way turns the advance into a hold (they're ranged; posting
            // at the wall beats hopping against it).
            var moveAxis = 0f;
            if (MathF.Abs(tDist) > 130f) moveAxis = MathF.Sign(tDist);
            else if (MathF.Abs(tDist) < 45f) moveAxis = -MathF.Sign(tDist) * 0.7f;
            GroundMove(dt, planet, up, right,
                NavAxis(planet, up, right, moveAxis, avoidCliffs: false), speedMul);
            return;
        }
        // No invader to fight, but a disaster overhead: post is abandoned for shelter like
        // everyone else (a live GuardTarget above already took priority — duty outranks cover).
        if (TakeCover && TickTakeCover(dt, planet, up, right, speedMul)) return;
        // Quiet street: walk the beat (grazer amble, no flee — this one doesn't spook),
        // turning at building hulls and roof edges like any sensible pedestrian.
        Wander -= dt;
        if (Wander <= 0)
        {
            Wander = 2f + (float)Random.Shared.NextDouble() * 3f;
            _amble = Random.Shared.Next(3) - 1;
        }
        GroundMove(dt, planet, up, right,
            NavAxis(planet, up, right, _amble * 0.5f, avoidCliffs: true), speedMul);
    }

    /// <summary>Saucer: the city's air patrol. On quiet watch it sweeps back and forth over
    /// the band above ITS city — turning back at the district edge rather than orbiting the
    /// whole planet — bobbing gently. Handed a target by Game1's militia pass, it slides over
    /// to hold station ~90px above the threat and lets the shared bolt-fire pass do the
    /// shooting. Terrain (or a tower) ahead makes it climb — it never rams the skyline it
    /// guards.</summary>
    private void TickSaucer(float dt, Planet planet, Vector2 up, Vector2 right, float speedMul)
    {
        if (_swing > 0f) _swing -= dt;
        var alt = (Position - planet.Center).Length();
        if (_prefAlt <= 0f) _prefAlt = alt;

        // Adopt the nearest city district as the patrol beat (self-heals for saucers restocked
        // after a save/load, which don't carry the spawn-time bearing). A generous margin on
        // the district half-width lets the lap sweep the full skyline, edge to edge.
        var rel = Position - planet.Center;
        var bearing = MathF.Atan2(rel.Y, rel.X);
        if (float.IsNaN(_patrolAng))
        {
            var best = float.MaxValue;
            foreach (var (ang, half) in planet.CityDistricts)
            {
                var d = MathF.Abs(WrapPi(bearing - ang));
                if (d < best) { best = d; _patrolAng = ang; _patrolHalf = half + 0.18f; }
            }
            if (float.IsNaN(_patrolAng)) { _patrolAng = bearing; _patrolHalf = 0.35f; }
        }

        Vector2 desired;
        if (GuardTarget is { } threat)
        {
            // Combat station: hover above the threat, matching its drift.
            var hover = threat + planet.UpAt(threat) * 90f;
            desired = (hover - Position) * 2.2f;
            var len = desired.Length();
            if (len > 130f) desired *= 130f / len;
        }
        else
        {
            // Bounce off the district edges: past the band, aim the lap back toward the city
            // (right = +bearing, so +1 heads to higher angles). This keeps the patrol over the
            // towers instead of drifting off across open ground.
            var off = WrapPi(bearing - _patrolAng);
            if (off > _patrolHalf) _orbitSign = -1;
            else if (off < -_patrolHalf) _orbitSign = 1;

            Wander += dt * 1.8f;
            desired = right * (_orbitSign * MoveSpeed * speedMul);
            desired += up * MathHelper.Clamp((_prefAlt - alt) * 0.8f, -40f, 40f);
            desired += up * MathF.Sin(_phase + Wander) * 6f;   // gentle patrol bob
        }

        // Skyline ahead: climb over it, occasionally reversing the patrol lap.
        var probe = desired.LengthSquared() > 1f ? Vector2.Normalize(desired) : right * _orbitSign;
        if (planet.IsSolidAt(Position + probe * (Radius + 10f)))
        {
            desired = up * 60f;
            if (Random.Shared.Next(40) == 0) _orbitSign = -_orbitSign;
        }

        Velocity = Vector2.Lerp(Velocity, desired, MathF.Min(1f, 3.5f * dt));
    }

    // ---------------------------------------------------------------- aquatic fauna

    /// <summary>Alien whale: a slow leviathan lapping its basin. It cruises tangentially,
    /// turning when the shore (or the water's edge) looms; a lazy sine bob keeps it moving
    /// through the column, diving when its back breaks the surface and lifting off the
    /// floor. Out of water (a drained lake, a breach carried too far) it beaches — heavy,
    /// helpless, flopping under gravity until water finds it again.</summary>
    private void TickWhale(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        float speedMul)
    {
        if (cells.CountWaterNear(Position, Radius) < 4)
        {
            var vT0 = MoveToward(Vector2.Dot(Velocity, right), 0f, 200f * dt);
            var vN0 = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -200f);
            Velocity = right * vT0 + up * vN0;
            return;
        }

        Wander += dt;
        // Lap the basin: shore or dry water ahead turns the cruise around.
        var ahead = Position + right * (_orbitSign * (Radius + 14f));
        if (planet.IsSolidAt(ahead) || cells.CountWaterNear(ahead, 4f) < 3)
            _orbitSign = -_orbitSign;

        // Depth-keeping: bob through the column, dive off the surface, lift off the floor.
        var wantN = MathF.Sin(Wander * 0.6f + _phase) * 9f;
        if (cells.CountWaterNear(Position + up * (Radius + 3f), 3f) < 2) wantN = -16f;
        else if (planet.IsSolidAt(Position - up * (Radius + 5f))) wantN = 14f;

        var vT = MoveToward(Vector2.Dot(Velocity, right), _orbitSign * MoveSpeed * speedMul, 60f * dt);
        var vN = MoveToward(Vector2.Dot(Velocity, up), wantN, 120f * dt);
        Velocity = right * vT + up * vN;
    }

    /// <summary>Alien crab: an armoured scuttler that walks the lakebed. Placid at range,
    /// but anything that wades inside its patch gets rushed and pinched — a short fuse and
    /// a short memory, so backing out of the territory ends the fight.</summary>
    private void TickCrab(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        float moveAxis;
        if (dist < 110f)
        {
            moveAxis = MathF.Sign(Vector2.Dot(toPlayer, right)) * 1.3f;   // territorial rush
        }
        else
        {
            Wander -= dt;
            if (Wander <= 0) Wander = 2f + (float)Random.Shared.NextDouble() * 3f;
            moveAxis = MathF.Sin(Wander * 2.2f) * 0.4f;                   // sideways shuffle
        }
        GroundMove(dt, planet, up, right, moveAxis, speedMul);
    }

    /// <summary>Swim toward a target point inside the water: accelerate along the direction
    /// to it, kept inside the basin (a shore or the air line above turns the swimmer back).
    /// Shared by the hunting sea monsters. Beached (no water around), it just sinks so it
    /// can't thrash across dry land.</summary>
    private void SwimToward(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        Vector2 target, float speed, float accel)
    {
        // NOTE: only sets Velocity — the shared substepped integration in Update moves the
        // body and resolves tile collision, so this must never touch Position itself.
        if (cells.CountWaterNear(Position, Radius) < 3)
        {
            // Out of water — sink and coast, don't flop across the ground.
            var vT0 = MoveToward(Vector2.Dot(Velocity, right), 0f, 150f * dt);
            var vN0 = MathF.Max(Vector2.Dot(Velocity, up) - Grav(planet) * dt, -180f);
            Velocity = right * vT0 + up * vN0;
            return;
        }
        var to = target - Position;
        if (to.LengthSquared() > 0.01f) to.Normalize();
        // Don't swim out of the water: if the step ahead leaves the basin, cancel that push.
        var ahead = Position + to * (Radius + 8f);
        if (planet.IsSolidAt(ahead) || cells.CountWaterNear(ahead + up * (Radius + 2f), 2f) < 1)
            to -= up * Vector2.Dot(to, up);   // strip the outward/vertical component near a wall/surface
        Velocity = MoveTowardV(Velocity, to * speed, accel * dt);
    }

    /// <summary>Alien shark: a torpedo predator. When the dwarf is in its water it accelerates
    /// straight at them for a bite; otherwise it laps the basin like the whale. Fast and
    /// relentless, but it can only get you in the water — climb out and it can't follow.</summary>
    private void TickShark(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        // Hunt when the prey is in the water and within scent range; else patrol.
        var hunt = dist < 340f && cells.CountWaterNear(Position + toPlayer * 0f, 3f) >= 3
                   && cells.CountWaterNear(Position + (dist > 0.01f ? toPlayer / dist : right) * 12f, 2f) >= 1;
        if (hunt && dist > 0.01f)
        {
            SwimToward(dt, planet, cells, up, right, Position + toPlayer, MoveSpeed * speedMul, 220f);
            _swing = 0.2f;   // gnashing animation while charging
            if (_swing > 0f) _swing -= dt;
            return;
        }
        // Patrol: lap the basin, turning at shores/surface.
        Wander += dt;
        var laneAhead = Position + right * (_orbitSign * (Radius + 16f));
        if (planet.IsSolidAt(laneAhead) || cells.CountWaterNear(laneAhead, 3f) < 2)
            _orbitSign = -_orbitSign;
        var bob = MathF.Sin(Wander * 0.7f + _phase) * 8f;
        if (cells.CountWaterNear(Position + up * (Radius + 3f), 3f) < 2) bob = -14f;
        else if (planet.IsSolidAt(Position - up * (Radius + 5f))) bob = 14f;
        SwimToward(dt, planet, cells, up, right,
            Position + right * (_orbitSign * 40f) + up * bob, MoveSpeed * 0.5f * speedMul, 90f);
    }

    /// <summary>Gulper (deep-water anglerfish): drifts slowly near the bottom trailing a
    /// glowing lure; when prey strays inside striking range it LUNGES — a short, fast dart
    /// with a huge bite — then coasts and resets. Slow to reposition, deadly up close.</summary>
    private void TickGulper(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        _cd -= dt;
        if (_swing > 0f) _swing -= dt;
        var preyInWater = cells.CountWaterNear(Position, 3f) >= 3;
        if (preyInWater && dist < 130f && dist > 0.01f && _cd <= 0f)
        {
            _lungeDir = toPlayer / dist;
            _swing = 0.5f;      // maw-open + lunge window
            _cd = 2.2f;
        }
        if (_swing > 0f && dist > 0.01f)
        {
            // Lunge: a fast committed dart along the locked direction.
            SwimToward(dt, planet, cells, up, right, Position + _lungeDir * 60f,
                MoveSpeed * 3.2f * speedMul, 400f);
            return;
        }
        // Idle drift: hang near the floor, sway gently.
        Wander += dt;
        var sway = MathF.Sin(Wander * 0.5f + _phase) * 10f;
        var wantN = planet.IsSolidAt(Position - up * (Radius + 6f)) ? 10f : -6f;   // hug the bottom
        SwimToward(dt, planet, cells, up, right,
            Position + right * sway + up * wantN, MoveSpeed * speedMul, 60f);
    }

    /// <summary>Brinespitter: a rooted-ish reef lurker that fires pressurised water-globs at
    /// swimmers from range — aquatic artillery. It holds a loose station and lobs the shared
    /// projectiles (borrowing the acid-glob physics but as a plain water slug).</summary>
    private void TickBrinespitter(float dt, Planet planet, Cells cells, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul, List<TitanProjectile>? shots)
    {
        _cd -= dt;
        if (_swing > 0f) _swing -= dt;
        // Keep a loose depth station near the floor; drift a little.
        Wander += dt;
        var sway = MathF.Sin(Wander * 0.6f + _phase) * 6f;
        var wantN = planet.IsSolidAt(Position - up * (Radius + 6f)) ? 8f : -5f;
        SwimToward(dt, planet, cells, up, right,
            Position + right * sway + up * wantN, MoveSpeed * 0.4f * speedMul, 50f);

        if (dist is > 20f and < 240f && _cd <= 0f && shots is not null
            && cells.CountWaterNear(Position, 3f) >= 3 && HasLineOfSight(planet, toPlayer, dist))
        {
            var dir = toPlayer / dist;
            var aim = Vector2.Normalize(dir + up * 0.15f);
            shots.Add(new TitanProjectile(Position + aim * (Radius + 2f), aim * 210f,
                TitanShotKind.Acid, damage: 9f));   // reuses the glob physics as a water slug
            _cd = 2.0f + (float)Random.Shared.NextDouble() * 0.8f;
            _swing = 0.3f;
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

    /// <summary>Disaster response for city aliens (see <see cref="TakeCover"/>): sprint to the
    /// nearest building doorway and huddle against it until the sky clears. The shelter fix is
    /// cached and refreshed on a timer so a crowd of citizens isn't scanning every doorway every
    /// frame. Returns true once the creature is handling cover, so the caller skips its normal
    /// amble/beat. With no doorway in reach it just hunkers where it stands.</summary>
    private bool TickTakeCover(float dt, Planet planet, Vector2 up, Vector2 right, float speedMul)
    {
        _shelterCd -= dt;
        if (_shelter is null || _shelterCd <= 0f)
        {
            _shelterCd = 1.5f;
            _shelter = NearestShelter(planet);
        }
        if (_shelter is not { } s)
        {
            GroundMove(dt, planet, up, right, 0f, speedMul);   // nowhere to run — freeze in place
            return true;
        }
        // Close enough: press into the doorway and hold (crouched, out of the weather).
        var along = Vector2.Dot(s - Position, right);
        if (MathF.Abs(along) < Radius + 4f)
        {
            GroundMove(dt, planet, up, right, 0f, speedMul);
            return true;
        }
        // Panic sprint toward it — bypasses the cliff-caution of the normal amble, a citizen
        // scrambling for a door will take the drop to get inside.
        GroundMove(dt, planet, up, right, MathF.Sign(along) * 1.5f, speedMul);
        return true;
    }

    /// <summary>Nearest city doorway/floor site (Planet.CitySpawns) to this creature within a
    /// few hundred px — the shelter a citizen bolts to when a disaster hits. Null when none is
    /// close (a citizen out past the city edge just hunkers where it is).</summary>
    private Vector2? NearestShelter(Planet planet)
    {
        Vector2? best = null;
        var bestSq = 500f * 500f;
        foreach (var (sr, st) in planet.CitySpawns)
        {
            var w = planet.TileToWorld(sr, st);
            var dSq = (w - Position).LengthSquared();
            if (dSq < bestSq) { bestSq = dSq; best = w; }
        }
        return best;
    }

    /// <summary>Walker integrator: tangent drive + core-ward gravity + a reflexive hop when
    /// walking into a wall while grounded, so cave dwellers climb tunnel lips instead of
    /// grinding against them.</summary>
    private void GroundMove(float dt, Planet planet, Vector2 up, Vector2 right,
        float moveAxis, float speedMul)
    {
        var vT = Vector2.Dot(Velocity, right);
        var vN = Vector2.Dot(Velocity, up);
        vT = MoveToward(vT, moveAxis * MoveSpeed * speedMul, 400f * dt);
        vN = MathF.Max(vN - Grav(planet) * dt, -260f); // terminal velocity — keeps substeps bounded
        if (MathF.Abs(moveAxis) > 0.1f && IsGrounded(planet, up)
            && planet.IsSolidAt(Position + right * (MathF.Sign(moveAxis) * (Radius + 3f))))
        {
            // Door-users work the latch instead of hopping at the leaf.
            var ahead = Position + right * (MathF.Sign(moveAxis) * (Radius + 3f));
            if (!(CanUseDoors(Kind) && TryOpenDoorAt(planet, up, ahead)))
                vN = 120f;
        }
        Velocity = right * vT + up * vN;
    }

    private bool IsGrounded(Planet planet, Vector2 up) =>
        planet.IsSolidAt(Position - up * (Radius + 1.5f));

    /// <summary>Kinds with hands and the sense to use a latch — city folk, warren guards,
    /// and the bandits (who are, after all, people).</summary>
    private static bool CanUseDoors(CreatureKind k) =>
        k is CreatureKind.Civilian or CreatureKind.Peacekeeper or CreatureKind.Lizardman
          or CreatureKind.Marauder or CreatureKind.Pyro;

    /// <summary>If the probed spot (or head height above it) is a closed door, swing the
    /// whole contiguous vertical leaf open and report success — the walker strolls through
    /// instead of turning around.</summary>
    private bool TryOpenDoorAt(Planet planet, Vector2 up, Vector2 at)
    {
        for (var lift = 0f; lift <= 16f; lift += 8f)
        {
            var probe = at + up * lift;
            var (tx, ty) = planet.WorldToTile(probe);
            if (planet.Get(tx, ty) != TileKind.DoorClosed) continue;
            SetDoorRun(planet, up, probe, TileKind.DoorOpen);
            return true;
        }
        return false;
    }

    /// <summary>Set every door tile in the contiguous vertical run through <paramref name="at"/>.</summary>
    private static void SetDoorRun(Planet planet, Vector2 up, Vector2 at, TileKind to)
    {
        var (tx, ty) = planet.WorldToTile(at);
        planet.Set(tx, ty, to);
        foreach (var s in new[] { 1f, -1f })
            for (var step = 1; step <= 6; step++)
            {
                var (nx, ny) = planet.WorldToTile(at + up * (Planet.TileSize * step * s));
                if (planet.Get(nx, ny) is not (TileKind.DoorClosed or TileKind.DoorOpen)) break;
                planet.Set(nx, ny, to);
            }
    }

    /// <summary>Terrain sense for walkers: adjusts a desired walk axis so the creature
    /// navigates instead of grinding. A climbable lip passes through unchanged (GroundMove's
    /// reflex hop handles it); a TALL wall — a building hull, a cliff face — returns 0 and
    /// flips the amble so the walker turns around rather than hopping against it forever;
    /// with <paramref name="avoidCliffs"/>, a drop of more than ~7 tiles past the next step
    /// also turns it around, so citizens pace their tower floors and plazas instead of
    /// raining off the edges. Panic states (fleeing, hunting) skip this on purpose.</summary>
    private float NavAxis(Planet planet, Vector2 up, Vector2 right, float moveAxis, bool avoidCliffs)
    {
        if (MathF.Abs(moveAxis) < 0.1f || !IsGrounded(planet, up)) return moveAxis;
        var dir = MathF.Sign(moveAxis);
        var ahead = Position + right * (dir * (Radius + 4f));
        if (planet.IsSolidAt(ahead) && planet.IsSolidAt(ahead + up * 10f)
            && planet.IsSolidAt(ahead + up * 18f))
        {
            // A tall "wall" that is actually a closed door gets opened and walked through.
            if (CanUseDoors(Kind) && TryOpenDoorAt(planet, up, ahead)) return moveAxis;
            _amble = -(int)dir;
            return 0f;
        }
        if (avoidCliffs)
        {
            var step = Position + right * (dir * (Radius + 6f));
            var footing = false;
            for (var d = 0f; d <= 28f && !footing; d += 6f)
                footing = planet.IsSolidAt(step - up * (Radius + 2f + d));
            if (!footing)
            {
                _amble = -(int)dir;
                return 0f;
            }
        }
        return moveAxis;
    }

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
            case CreatureKind.Peacekeeper:
            {
                // City militia: the civilian frame in an armoured navy uniform — visored
                // helmet over the big head, shoulder plates, and an alloy sidearm that
                // comes up level when a threat is on the scope.
                var uniform = Tinted(new Color(58, 78, 112));
                var plate = Tinted(new Color(120, 140, 170));
                var skin = Tinted(new Color(180, 195, 175));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = moving ? MathF.Sin(t * 11f + _phase + i * 1.6f) * 0.45f : 0f;
                    r.DrawRect(Position - up * 1.6f + right * (i * 1.0f), new Vector2(1.0f, 3.0f), uniform, rot + a);
                }
                r.DrawRect(Position + up * 1.4f, new Vector2(4.0f, 4.6f), uniform, rot);
                r.DrawRect(Position + up * 3.2f + right * 1.9f, new Vector2(1.6f, 1.2f), plate, rot);
                r.DrawRect(Position + up * 3.2f - right * 1.9f, new Vector2(1.6f, 1.2f), plate, rot);
                var head = Position + up * 4.8f;
                r.DrawCircle(head, 2.6f, skin);
                // Visored helmet: a plated cap with a glowing scan-line eye slit.
                r.DrawRect(head + up * 1.2f, new Vector2(5.0f, 2.2f), plate, rot);
                var alert = GuardTarget is not null;
                r.DrawRect(head + right * (facing * 0.6f) + up * 0.2f, new Vector2(2.8f, 0.8f),
                    alert ? new Color(255, 150, 90) : new Color(120, 220, 255), rot);
                // Sidearm: holstered angle at ease, levelled at the threat when tracking.
                var aimDir = GuardTarget is { } g && (g - Position).LengthSquared() > 1f
                    ? Vector2.Normalize(g - Position)
                    : Rotate(right * facing, -0.5f * facing);
                var gAng = MathF.Atan2(aimDir.Y, aimDir.X);
                var gripP = Position + right * (facing * 2.2f) + up * 1.6f;
                r.DrawRect(gripP + aimDir * 2.0f, new Vector2(3.6f, 1.1f), Tinted(new Color(150, 160, 180)), gAng);
                if (_swing > 0f) // muzzle flash the frame a bolt leaves
                    r.DrawCircle(gripP + aimDir * 4.4f, 1.4f, new Color(160, 235, 255));
                break;
            }
            case CreatureKind.Marauder:
            {
                // Cave bandit: a scavenger in patched rust-leather with a rag mask and a
                // scuffed slug pistol that tracks the dwarf. The player's own silhouette,
                // gone feral.
                var leather = Tinted(new Color(120, 82, 52));
                var rag = Tinted(new Color(150, 60, 55));
                var skin = Tinted(new Color(172, 150, 128));
                var moving = MathF.Abs(Vector2.Dot(Velocity, right)) > 4f;
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = moving ? MathF.Sin(t * 11f + _phase + i * 1.6f) * 0.45f : 0f;
                    r.DrawRect(Position - up * 1.6f + right * (i * 1.0f), new Vector2(1.0f, 3.0f), leather, rot + a);
                }
                r.DrawRect(Position + up * 1.4f, new Vector2(3.8f, 4.4f), leather, rot);
                // Bandolier slung across the chest.
                r.DrawRect(Position + up * 1.6f, new Vector2(4.2f, 0.9f), Tinted(new Color(70, 55, 40)), rot + 0.6f);
                var head = Position + up * 4.6f;
                r.DrawCircle(head, 2.3f, skin);
                r.DrawRect(head - up * 0.4f, new Vector2(4.4f, 1.6f), rag, rot); // rag mask
                r.DrawCircle(head + right * (facing * 1.0f) + up * 0.8f, 0.7f, Color.Black);
                // Pistol arm follows the last aim.
                var aim = _gunAim.LengthSquared() > 0.01f ? _gunAim : right * facing;
                var pAng = MathF.Atan2(aim.Y, aim.X);
                var grip = Position + right * (facing * 2.0f) + up * 1.8f;
                r.DrawRect(grip + aim * 2.2f, new Vector2(3.4f, 1.1f), Tinted(new Color(90, 92, 100)), pAng);
                if (_swing > 0f)
                    r.DrawCircle(grip + aim * 4.6f, 1.3f, new Color(255, 220, 140));
                break;
            }
            case CreatureKind.Raider:
            {
                // Jetpack bandit: the marauder frame slung under a soot-stained pack with a
                // live sputtering flame, goggles up, machine-pistol raking bursts. Hangs in
                // the air with a visible bob.
                var leather = Tinted(new Color(105, 78, 60));
                var packC = Tinted(new Color(95, 100, 112));
                var skin = Tinted(new Color(172, 150, 128));
                // Pack on the back (trailing side) with its exhaust flame flickering below.
                var back = Position - right * (facing * 2.4f) + up * 1.8f;
                r.DrawRect(back, new Vector2(2.2f, 3.6f), packC, rot);
                r.DrawRect(back + up * 1.4f, new Vector2(1.6f, 0.9f), Tinted(new Color(140, 145, 158)), rot);
                var flick = (float)Random.Shared.NextDouble();
                r.DrawRect(back - up * (2.6f + flick * 1.4f), new Vector2(1.3f, 2.2f + flick * 1.6f),
                    new Color(255, 170, 60), rot);
                r.DrawRect(back - up * 2.2f, new Vector2(0.8f, 1.2f), new Color(255, 235, 160), rot);
                // Dangling legs (no ground to stride on).
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = MathF.Sin(t * 5f + _phase + i * 2f) * 0.25f;
                    r.DrawRect(Position - up * 1.8f + right * (i * 1.0f), new Vector2(0.9f, 2.8f), leather, rot + a + 0.2f * i);
                }
                r.DrawRect(Position + up * 1.2f, new Vector2(3.6f, 4.2f), leather, rot);
                var head = Position + up * 4.4f;
                r.DrawCircle(head, 2.2f, skin);
                r.DrawRect(head + up * 1.0f, new Vector2(4.0f, 1.3f), packC, rot); // goggle band
                r.DrawCircle(head + right * (facing * 1.0f) + up * 1.0f, 0.8f, Tinted(new Color(190, 220, 240)));
                var aim = _gunAim.LengthSquared() > 0.01f ? _gunAim : right * facing;
                var pAng = MathF.Atan2(aim.Y, aim.X);
                var grip = Position + right * (facing * 2.0f) + up * 1.6f;
                r.DrawRect(grip + aim * 2.4f, new Vector2(3.8f, 1.2f), Tinted(new Color(80, 82, 92)), pAng);
                if (_swing > 0f)
                    r.DrawCircle(grip + aim * 5.0f, 1.4f, new Color(255, 220, 140));
                break;
            }
            case CreatureKind.Pyro:
            {
                // Flame heavy: a broad scorched pressure suit, riveted fuel tank on the
                // back, hose to a wide nozzle. The visor glows like a furnace when the hose
                // is open.
                var suit = Tinted(new Color(96, 74, 58));
                var scorch = Tinted(new Color(60, 48, 42));
                var tank = Tinted(new Color(140, 60, 45));
                var stride = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 30f, 1f);
                for (var i = -1; i <= 1; i += 2)
                {
                    var a = MathF.Sin(t * 8f + _phase + i * 1.6f) * 0.4f * stride;
                    r.DrawRect(Position - up * 1.8f + right * (i * 1.4f), new Vector2(1.4f, 3.2f), scorch, rot + a);
                }
                // Fuel tank on the trailing shoulder.
                r.DrawRect(Position - right * (facing * 2.8f) + up * 2.2f, new Vector2(2.4f, 4.0f), tank, rot);
                r.DrawRect(Position - right * (facing * 2.8f) + up * 4.2f, new Vector2(1.6f, 0.8f), Tinted(new Color(200, 190, 170)), rot);
                r.DrawRect(Position + up * 1.6f, new Vector2(5.2f, 5.2f), suit, rot);
                r.DrawRect(Position + up * 0.4f, new Vector2(5.2f, 1.0f), scorch, rot); // belt of soot
                var head = Position + up * 5.2f + right * (facing * 0.4f);
                r.DrawCircle(head, 2.4f, suit);
                // Furnace visor: banked coals at rest, white-hot while hosing.
                r.DrawRect(head + right * (facing * 0.8f) + up * 0.2f, new Vector2(2.6f, 1.1f),
                    _swing > 0f ? new Color(255, 230, 150) : new Color(200, 90, 40), rot);
                // Nozzle held two-handed, with a pilot flame (and gout while firing).
                var aim = _gunAim.LengthSquared() > 0.01f ? _gunAim : right * facing;
                var nAng = MathF.Atan2(aim.Y, aim.X);
                var grip = Position + right * (facing * 2.6f) + up * 1.4f;
                r.DrawRect(grip + aim * 2.6f, new Vector2(4.6f, 1.5f), scorch, nAng);
                r.DrawCircle(grip + aim * 5.2f, _swing > 0f ? 1.8f : 0.8f,
                    _swing > 0f ? new Color(255, 200, 90) : new Color(255, 140, 60));
                break;
            }
            case CreatureKind.Saucer:
            {
                // Classic disc-craft: a wide banked hull with a glass canopy dome, running
                // lights chasing around the rim, and a belly lamp that burns amber when the
                // patrol is on a target. A gentle hover-tilt follows its drift.
                var hull = Tinted(new Color(126, 138, 158));
                var hullDark = Tinted(new Color(88, 98, 116));
                var tilt = MathHelper.Clamp(Vector2.Dot(Velocity, right) * 0.004f, -0.3f, 0.3f);
                var bob = MathF.Sin(t * 2.2f + _phase) * 0.8f;
                var c = Position + up * bob;
                r.DrawRect(c, new Vector2(Radius * 2.4f, 3.2f), hull, rot + tilt);
                r.DrawRect(c - up * 1.4f, new Vector2(Radius * 1.5f, 2.0f), hullDark, rot + tilt);
                // Canopy dome with a silhouetted pilot blob.
                r.DrawCircle(c + up * 2.4f, 3.0f, Tinted(new Color(150, 210, 220)) * 0.9f);
                r.DrawCircle(c + up * 2.2f, 1.3f, Tinted(new Color(70, 80, 90)));
                // Rim running lights, chasing in sequence.
                for (var i = -1; i <= 1; i++)
                {
                    var blink = MathF.Sin(t * 6f + _phase + i * 2.1f) * 0.5f + 0.5f;
                    r.DrawCircle(c + right * (i * Radius * 0.95f) - up * 0.2f, 0.8f,
                        Color.Lerp(new Color(70, 90, 110), new Color(160, 240, 255), blink));
                }
                // Belly lamp: cool idle glow, hot amber on the hunt (plus bolt flash).
                var lampCol = GuardTarget is not null
                    ? new Color(255, 170, 90) : new Color(120, 210, 230);
                r.DrawCircle(c - up * 2.6f, _swing > 0f ? 2.2f : 1.4f, lampCol);
                break;
            }
            case CreatureKind.AlienWhale:
            {
                // A glowing leviathan: long tapered body of overlapping discs, a raked tail
                // fluke sweeping with the swim, a small dorsal fin, one placid eye, and a
                // line of bioluminescent spots pulsing down the flank.
                var hide = Tinted(new Color(56, 82, 118));
                var bellyC = Tinted(new Color(110, 140, 165));
                var dir = right * facing;
                for (var s = 0; s < 5; s++)
                {
                    var f = s / 4f;
                    var seg = Position - dir * (f * Radius * 1.7f)
                              + up * MathF.Sin(t * 1.6f + _phase + f * 1.8f) * 1.2f;
                    r.DrawCircle(seg, Radius * (1f - f * 0.55f), hide);
                }
                r.DrawCircle(Position + dir * (Radius * 0.55f) - up * (Radius * 0.3f),
                    Radius * 0.55f, bellyC);
                // Tail fluke, sweeping.
                var tailBase = Position - dir * (Radius * 1.9f);
                var sweep = MathF.Sin(t * 1.6f + _phase) * 0.5f;
                var tAng = MathF.Atan2(dir.Y, dir.X);
                r.DrawRect(tailBase - dir * 2f, new Vector2(5.5f, 1.6f), hide, tAng + sweep + 0.9f);
                r.DrawRect(tailBase - dir * 2f, new Vector2(5.5f, 1.6f), hide, tAng - sweep - 0.9f);
                // Dorsal fin + eye.
                r.DrawRect(Position + up * (Radius * 0.9f), new Vector2(1.6f, 3.4f), hide, rot + facing * 0.5f);
                r.DrawCircle(Position + dir * (Radius * 0.8f) + up * (Radius * 0.25f), 1.1f, Color.Black);
                // Bioluminescent flank spots, pulsing in sequence.
                for (var i = 0; i < 4; i++)
                {
                    var glow = MathF.Sin(t * 2.4f + _phase + i * 1.5f) * 0.5f + 0.5f;
                    r.DrawCircle(Position - dir * (i * Radius * 0.45f) + up * (Radius * 0.1f),
                        0.9f + glow * 0.5f,
                        Color.Lerp(new Color(60, 120, 150), new Color(140, 240, 255), glow));
                }
                break;
            }
            case CreatureKind.AlienCrab:
            {
                // Lakebed scuttler: a wide domed shell over scissoring leg pairs, stalked
                // eyes, and two front claws that spread wide when something wades too close.
                var shell = Tinted(new Color(150, 82, 70));
                var shellHi = Tinted(new Color(190, 116, 92));
                var legCol = Tinted(new Color(110, 60, 52));
                var scur = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 25f, 1f);
                for (var i = -1; i <= 1; i++)
                {
                    var a = MathF.Sin(t * 14f + _phase + i * 2.1f) * 0.5f * scur;
                    r.DrawRect(Position - up * 1.4f + right * (i * 2.6f), new Vector2(1.0f, 3.0f), legCol, rot + a + i * 0.3f);
                }
                r.DrawCircle(Position + up * 0.6f, Radius, shell);
                r.DrawRect(Position + up * 1.8f, new Vector2(Radius * 1.5f, 1.4f), shellHi, rot);
                // Claws: folded at rest, thrown wide in the territorial rush.
                var angry = Velocity.LengthSquared() > 500f;
                var clawA = angry ? 0.9f + MathF.Sin(t * 10f) * 0.25f : 0.35f;
                r.DrawRect(Position + right * (facing * (Radius + 1.2f)) + up * 0.4f,
                    new Vector2(2.8f, 1.6f), shellHi, rot + facing * clawA);
                r.DrawRect(Position - right * (facing * (Radius + 1.2f)) + up * 0.4f,
                    new Vector2(2.4f, 1.4f), shellHi, rot - facing * clawA);
                // Eye stalks.
                r.DrawRect(Position + up * (Radius + 0.8f) + right * 1.0f, new Vector2(0.5f, 1.8f), legCol, rot + 0.2f);
                r.DrawRect(Position + up * (Radius + 0.8f) - right * 1.0f, new Vector2(0.5f, 1.8f), legCol, rot - 0.2f);
                r.DrawCircle(Position + up * (Radius + 1.8f) + right * 1.2f, 0.6f, Color.Black);
                r.DrawCircle(Position + up * (Radius + 1.8f) - right * 0.8f, 0.6f, Color.Black);
                break;
            }
            case CreatureKind.AlienShark:
            {
                // Sleek torpedo predator: a tapered slate body, tall dorsal + tail fins, a
                // pale belly, a cold eye, and a gash of a mouth that gnashes open while it
                // charges (_swing).
                var hide = Tinted(new Color(70, 92, 112));
                var bellyC = Tinted(new Color(150, 170, 185));
                var dir = right * facing;
                for (var s = 0; s < 4; s++)
                {
                    var f = s / 3f;
                    var seg = Position - dir * (f * Radius * 1.5f)
                              + up * MathF.Sin(t * 5f + _phase + f * 1.6f) * 1.0f;
                    r.DrawCircle(seg, Radius * (1f - f * 0.6f), hide);
                }
                r.DrawCircle(Position + dir * (Radius * 0.4f) - up * (Radius * 0.35f), Radius * 0.5f, bellyC);
                // Snout, mouth (open + teeth while charging), eye.
                var snout = Position + dir * (Radius * 1.0f);
                var mouthOpen = _swing > 0f ? 1.4f : 0.6f;
                r.DrawRect(snout - up * 0.6f, new Vector2(3.2f, mouthOpen), Color.Black, rot);
                if (_swing > 0f)
                    r.DrawRect(snout - up * 0.6f, new Vector2(3.0f, 0.6f), new Color(220, 220, 230), rot);
                r.DrawCircle(Position + dir * (Radius * 0.6f) + up * (Radius * 0.35f), 0.9f, new Color(230, 60, 50));
                // Tall dorsal + swept tail.
                r.DrawRect(Position + up * (Radius * 0.9f), new Vector2(2.0f, 4.2f), hide, rot + facing * 0.55f);
                var tail = Position - dir * (Radius * 1.5f);
                var sweep = MathF.Sin(t * 5f + _phase) * 0.6f;
                var tAng = MathF.Atan2(dir.Y, dir.X);
                r.DrawRect(tail, new Vector2(5.5f, 1.6f), hide, tAng + sweep + 1.0f);
                r.DrawRect(tail, new Vector2(4.0f, 1.4f), hide, tAng - sweep - 1.0f);
                // Pectoral fin.
                r.DrawRect(Position + dir * 0.6f - up * (Radius * 0.5f), new Vector2(2.6f, 1.1f), hide, rot + facing * 0.9f);
                break;
            }
            case CreatureKind.Gulper:
            {
                // Deep-water anglerfish: a bulbous dark body dominated by a cavernous toothy
                // maw (yawning wide on the lunge, _swing), tiny fins, and a glowing lure on a
                // thin stalk dangling out front — the light in the dark that's actually bait.
                var body = Tinted(new Color(46, 60, 74));
                var bodyDk = Tinted(new Color(30, 40, 52));
                var dir = right * facing;
                r.DrawCircle(Position, Radius, body);
                r.DrawCircle(Position - dir * (Radius * 0.7f), Radius * 0.7f, bodyDk);
                // Cavernous maw at the front, hinging wide on the lunge.
                var maw = Position + dir * (Radius * 0.6f);
                var gape = _swing > 0f ? Radius * 1.1f : Radius * 0.5f;
                r.DrawCircle(maw, gape, Color.Black);
                // A ring of pale teeth around the maw.
                for (var i = 0; i < 6; i++)
                {
                    var a = (i / 6f) * MathF.Tau;
                    r.DrawRect(maw + new Vector2(MathF.Cos(a), MathF.Sin(a)) * gape * 0.8f,
                        new Vector2(1.2f, 1.2f), new Color(210, 210, 200), rot + a);
                }
                // Eye.
                r.DrawCircle(Position - dir * (Radius * 0.1f) + up * (Radius * 0.6f), 1.0f, new Color(255, 220, 120));
                // Lure: a thin stalk arcing over the maw with a pulsing glowing bulb.
                var pulse = MathF.Sin(t * 3f + _phase) * 0.5f + 0.5f;
                var lure = maw + dir * (Radius * 0.9f) + up * (Radius * 1.3f);
                r.DrawRect(Position + up * (Radius * 1.1f), new Vector2(0.6f, Radius * 1.2f), body, rot + facing * 0.5f);
                r.DrawCircle(lure, 1.2f + pulse * 0.8f, Color.Lerp(new Color(120, 200, 150), new Color(200, 255, 210), pulse));
                // Little tail fin.
                r.DrawRect(Position - dir * (Radius * 1.2f), new Vector2(3.0f, 1.4f),
                    body, MathF.Atan2(dir.Y, dir.X) + MathF.Sin(t * 4f + _phase) * 0.4f);
                break;
            }
            case CreatureKind.Brinespitter:
            {
                // Reef lurker: a squat mottled dome with a puckered spout snout, stubby fins,
                // and beady eyes. The spout flashes pale when it fires a water-glob (_swing).
                var body = Tinted(new Color(70, 110, 96));
                var bodyHi = Tinted(new Color(104, 150, 128));
                var dir = right * facing;
                r.DrawCircle(Position, Radius, body);
                r.DrawRect(Position + up * (Radius * 0.7f), new Vector2(Radius * 1.4f, 1.4f), bodyHi, rot);
                // Spout snout, aimed toward the facing.
                var spout = Position + dir * (Radius * 0.9f);
                r.DrawRect(spout, new Vector2(2.6f, 2.0f), bodyHi, rot);
                r.DrawCircle(spout + dir * 1.4f, _swing > 0f ? 1.8f : 0.8f,
                    _swing > 0f ? new Color(180, 220, 255) : new Color(60, 90, 110));
                // Stubby fins + eyes.
                r.DrawRect(Position - up * (Radius * 0.4f) + right * (Radius * 0.8f), new Vector2(1.8f, 1.0f), body, rot + 0.6f);
                r.DrawRect(Position - up * (Radius * 0.4f) - right * (Radius * 0.8f), new Vector2(1.8f, 1.0f), body, rot - 0.6f);
                r.DrawCircle(Position + dir * (Radius * 0.3f) + up * (Radius * 0.5f), 0.8f, Color.Black);
                r.DrawCircle(Position + dir * (Radius * 0.7f) + up * (Radius * 0.5f), 0.7f, Color.Black);
                break;
            }
            case CreatureKind.Moonlet:
            {
                // A boulder that shouldn't float: cratered grey rock with its own ring of
                // orbiting dust motes — the tell that this one isn't scenery. The core
                // smoulders brighter as the slingshot winds up.
                var rock = Tinted(new Color(112, 106, 100));
                var pock = Tinted(new Color(86, 80, 76));
                r.DrawCircle(Position, Radius, rock);
                r.DrawCircle(Position + new Vector2(MathF.Cos(_phase), MathF.Sin(_phase)) * Radius * 0.45f,
                    Radius * 0.32f, pock);
                r.DrawCircle(Position - new Vector2(MathF.Sin(_phase), MathF.Cos(_phase)) * Radius * 0.4f,
                    Radius * 0.22f, pock);
                var windup = MathHelper.Clamp(1f - _cd / 1.2f, 0f, 1f);
                if (windup > 0f || _swing > 0f)
                    r.DrawCircle(Position, 1.3f + windup,
                        Color.Lerp(new Color(140, 120, 110), new Color(255, 150, 70), MathF.Max(windup, _swing > 0f ? 1f : 0f)));
                for (var i = 0; i < 3; i++)
                {
                    var a = t * 2.6f * _orbitSign + _phase + i * (MathF.Tau / 3f);
                    var mote = Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (Radius + 2.6f);
                    r.DrawRect(mote, new Vector2(1f, 1f), new Color(180, 172, 160) * 0.8f);
                }
                break;
            }
            case CreatureKind.VacLeech:
            {
                // Pale vacuum lamprey: a bloodless segmented tube ending in a dark sucker
                // disc that always faces its meal. The swollen tail sac shows the stolen
                // air — it bulges as it feeds (rides the hit-flash-free body tint).
                var hide = Tinted(new Color(214, 208, 196));
                var dark = Tinted(new Color(150, 142, 132));
                var toP = player.Position - Position;
                var mDir = toP.LengthSquared() > 1f ? Vector2.Normalize(toP) : right * facing;
                var wrig = MathF.Sin(t * 11f + _phase) * 0.8f;
                r.DrawCircle(Position - mDir * 2.6f + up * (wrig * 0.4f), Radius * 0.95f, dark);
                r.DrawCircle(Position - mDir * 4.6f - up * (wrig * 0.4f), Radius * 0.8f, hide);
                r.DrawCircle(Position, Radius, hide);
                // Sucker disc + tooth ring.
                r.DrawCircle(Position + mDir * (Radius * 0.7f), Radius * 0.7f, Tinted(new Color(70, 58, 62)));
                for (var i = -1; i <= 1; i++)
                    r.DrawRect(Position + mDir * (Radius + 0.6f) + new Vector2(-mDir.Y, mDir.X) * (i * 1.1f),
                        new Vector2(0.6f, 1.2f), Color.White, MathF.Atan2(mDir.Y, mDir.X));
                break;
            }
            case CreatureKind.Glimmermaw:
            {
                // The body barely exists — a translucent void-dark smudge — because the
                // LURE is the creature: a gem-bright glint on a stalk, twinkling exactly
                // like a dropped diamond. By the time the jaws read, it's usually too late.
                var body = new Color(18, 16, 26) * 0.75f;
                r.DrawCircle(Position, Radius, HitFlash > 0 ? new Color(90, 85, 110) : body);
                var lure = LurePos(planet);
                r.DrawRect((Position + lure) / 2f, new Vector2(0.6f, Radius + 4f),
                    new Color(40, 38, 52) * 0.8f, rot);
                var glint = MathF.Sin(t * 3.1f + _phase) * 0.5f + 0.5f;
                r.DrawRect(lure, new Vector2(2.2f, 4.2f), Tinted(new Color(180, 220, 230)), t * 1.4f);
                r.DrawRect(lure, new Vector2(1.1f + glint * 0.6f, 2.2f + glint), Color.White, t * 1.4f);
                if (((int)(t * 2.5f + _phase) & 3) == 0)
                    r.DrawRect(lure + new Vector2(1.2f, -1.2f), new Vector2(1f, 1f), Color.White);
                // Jaws: two pale crescents that only show while lunging (or point-blank).
                var open = _swing > 0f ? 1f : MathHelper.Clamp(1f - (player.Position - Position).Length() / 60f, 0f, 0.4f);
                if (open > 0.05f)
                {
                    var toP2 = player.Position - Position;
                    var jDir = toP2.LengthSquared() > 1f ? Vector2.Normalize(toP2) : right * facing;
                    var jAng = MathF.Atan2(jDir.Y, jDir.X);
                    var teeth = Tinted(new Color(226, 222, 210));
                    r.DrawRect(Position + jDir * Radius + Rotate(jDir, 0.7f * open) * 2f,
                        new Vector2(4.4f, 1.1f), teeth, jAng + 0.7f * open);
                    r.DrawRect(Position + jDir * Radius + Rotate(jDir, -0.7f * open) * 2f,
                        new Vector2(4.4f, 1.1f), teeth, jAng - 0.7f * open);
                }
                break;
            }
            case CreatureKind.StarJelly:
            {
                // Vacuum medusa: a translucent pulsing bell full of faint star-motes,
                // trailing four stinging filaments that ripple behind the drift.
                var pulse = MathF.Sin(t * 2.2f + _phase) * 0.5f + 0.5f;
                var bell = Tinted(new Color(120, 180, 220)) * 0.55f;
                var bellRim = Tinted(new Color(180, 230, 255)) * 0.8f;
                var c = Position + up * (pulse * 1.2f);
                r.DrawCircle(c, Radius + pulse * 1.2f, bell);
                r.DrawCircle(c + up * 1f, (Radius + pulse * 1.2f) * 0.7f, bellRim * 0.5f);
                // Star-motes inside the bell.
                for (var i = 0; i < 3; i++)
                {
                    var ma = _phase + i * 2.1f + t * 0.8f;
                    r.DrawRect(c + new Vector2(MathF.Cos(ma), MathF.Sin(ma)) * Radius * 0.45f,
                        new Vector2(1f, 1f), Color.White * (0.5f + pulse * 0.5f));
                }
                // Trailing filaments — hang against the drift so they stream behind.
                var drift = Velocity.LengthSquared() > 4f ? Vector2.Normalize(Velocity) : up;
                for (var i = -1; i <= 2; i++)
                {
                    var fBase = c - up * (Radius * 0.6f) + right * (i * 1.8f - 0.9f);
                    var ripple = MathF.Sin(t * 4f + _phase + i * 1.3f) * 2.2f;
                    var tip = fBase - drift * (7f + i % 2 * 3f) + right * ripple - up * 4f;
                    r.DrawRect((fBase + tip) / 2f, new Vector2(0.7f, (tip - fBase).Length()),
                        bellRim * 0.6f, MathF.Atan2(tip.Y - fBase.Y, tip.X - fBase.X) + MathF.PI / 2f);
                }
                break;
            }
            case CreatureKind.VoidBarnacle:
            {
                // A calcified cone cemented to the rock, mouth-plates parted around a dark
                // gullet — and, while prey is hooked, the near-invisible gravity tongue: a
                // faint shimmer line running all the way to the player.
                var shell = Tinted(new Color(150, 140, 128));
                var shellDark = Tinted(new Color(110, 102, 94));
                r.DrawRect(Position - up * 1f, new Vector2(10f, 6f), shellDark, rot);
                r.DrawRect(Position + up * 2f, new Vector2(7.5f, 5f), shell, rot);
                r.DrawRect(Position + up * 4.5f, new Vector2(5f, 3f), shellDark, rot);
                // Mouth plates — gape wider while pulling.
                var gape = Pulling ? 1.6f + MathF.Sin(t * 8f) * 0.4f : 0.7f;
                r.DrawRect(Position + up * 6f - right * gape, new Vector2(1.6f, 3f), shell, rot + 0.3f);
                r.DrawRect(Position + up * 6f + right * gape, new Vector2(1.6f, 3f), shell, rot - 0.3f);
                r.DrawCircle(Position + up * 5.5f, 1.6f, new Color(20, 12, 26));
                if (Pulling)
                {
                    var toP = player.Position - (Position + up * 6f);
                    var steps = 7;
                    for (var s = 1; s < steps; s++)
                    {
                        var pp = Position + up * 6f + toP * (s / (float)steps);
                        var shimmer = MathF.Sin(t * 14f - s * 1.2f) * 0.5f + 0.5f;
                        r.DrawCircle(pp, 1f + shimmer * 0.8f, new Color(170, 130, 240) * (0.25f + shimmer * 0.3f));
                    }
                }
                break;
            }
            case CreatureKind.Selenite:
            {
                // Crystalline moon-spider: a faceted selenite shard for a body on six
                // glassy needle legs, refracting a cold gleam as it scuttles.
                var glass = Tinted(new Color(210, 224, 240));
                var glassDim = Tinted(new Color(150, 168, 195));
                var scur = MathF.Min(MathF.Abs(Vector2.Dot(Velocity, right)) / 40f, 1f);
                for (var i = -1; i <= 1; i++)
                {
                    var a = MathF.Sin(t * 15f + _phase + i * 2.1f) * 0.5f * scur;
                    r.DrawRect(Position - up * 1f + right * (i * 1.9f), new Vector2(0.7f, 3.8f),
                        glassDim, rot + a + i * 0.25f);
                }
                // Body: two crossed shard rects read as a faceted crystal cluster.
                r.DrawRect(Position + up * 1.2f, new Vector2(3.2f, 5.4f), glassDim, rot + 0.5f);
                r.DrawRect(Position + up * 1.6f, new Vector2(2.4f, 4.6f), glass, rot - 0.35f);
                r.DrawRect(Position + up * 2.2f, new Vector2(1.2f, 2.6f), Color.White, rot + 0.1f);
                // The refracted gleam — a moving glint along the facets.
                var glint = MathF.Sin(t * 4f + _phase) * 2f;
                r.DrawRect(Position + up * (1.5f + glint * 0.4f) + right * glint,
                    new Vector2(1f, 1f), Color.White);
                break;
            }
            case CreatureKind.DustDevil:
            {
                // Charged regolith vortex: stacked counter-swaying dust bands widening
                // upward, static sparks crackling through the column. No face — the
                // menace is that a pile of moon dust is CHASING you.
                var dust = Tinted(new Color(150, 146, 138));
                var dustDark = Tinted(new Color(112, 108, 102));
                for (var s = 0; s < 5; s++)
                {
                    var sway = MathF.Sin(t * 13f + _phase + s * 1.9f) * (1.2f + s * 0.4f);
                    var w = 3.5f + s * 1.6f;
                    r.DrawRect(Position + up * (s * 2.2f - 1f) + right * sway,
                        new Vector2(w, 2.2f), (s & 1) == 0 ? dust : dustDark, rot + sway * 0.06f);
                }
                // Orbiting grit and the occasional static spark.
                for (var i = 0; i < 3; i++)
                {
                    var a = t * (5f + i) + _phase + i * 2.1f;
                    var grit = Position + up * (2f + i * 2.5f)
                             + right * (MathF.Cos(a) * (4f + i * 1.5f));
                    r.DrawRect(grit, new Vector2(1f, 1f), dustDark);
                }
                if (((int)(t * 7f + _phase) & 3) == 0)
                    r.DrawRect(Position + up * (2f + MathF.Sin(t * 31f) * 4f),
                        new Vector2(1.2f, 3.4f), new Color(190, 220, 255), rot + 0.4f);
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
    /// carry a faint cold gleam so you spot one drifting down a dark tunnel before it spots
    /// you. <paramref name="planet"/> is only needed by kinds whose light hangs off the body
    /// (the glimmermaw's lure); headless callers may pass null.</summary>
    public void AddLight(Renderer r, Planet? planet = null)
    {
        switch (Kind)
        {
            case CreatureKind.Glimmermaw:
            {
                // The whole hunt: a gem-glint twinkling in a dark cave, indistinguishable
                // from a dropped diamond until it's much too close.
                var twinkle = MathF.Sin(r.Time * 3.1f + _phase) * 5f;
                r.AddLight(planet is null ? Position : LurePos(planet), 15f + twinkle,
                    new Color(170, 225, 245));
                break;
            }
            case CreatureKind.StarJelly:
            {
                // The bell glows like a drowned constellation drifting over the regolith.
                var pulse = MathF.Sin(r.Time * 2.2f + _phase) * 4f;
                r.AddLight(Position, 14f + pulse, new Color(140, 200, 240));
                break;
            }
            case CreatureKind.VoidBarnacle:
                // Only the gullet betrays it — a dim violet ember in the cave wall,
                // flaring while the tongue reels something in.
                r.AddLight(Position, Pulling ? 16f : 7f, new Color(160, 110, 230));
                break;
            case CreatureKind.Selenite:
                // Cold crystal gleam — a wandering glint in a dark crater cave.
                r.AddLight(Position, 10f + MathF.Sin(r.Time * 4f + _phase) * 3f,
                    new Color(190, 215, 245));
                break;
            case CreatureKind.DustDevil:
                // Static discharge flickers through the column.
                if (((int)(r.Time * 7f + _phase) & 3) == 0)
                    r.AddLight(Position, 12f, new Color(170, 210, 255));
                break;
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
            case CreatureKind.Civilian:
                // The antenna mood-light — a soft teal bob drifting down a night street.
                r.AddLight(Position, 9f, new Color(110, 200, 190));
                break;
            case CreatureKind.Lizardman:
                // Hunting guards announce themselves with a red eye-gleam, like the delver.
                if (_aggroT > 0f) r.AddLight(Position, 12f, new Color(255, 60, 40));
                break;
            case CreatureKind.Peacekeeper:
                // Visor scan-line: cool on patrol, hot amber when tracking a threat.
                r.AddLight(Position, 10f, GuardTarget is not null
                    ? new Color(255, 150, 80) : new Color(90, 190, 230));
                break;
            case CreatureKind.Saucer:
                // The belly lamp sweeps the streets below — a moving pool of patrol light.
                r.AddLight(Position, GuardTarget is not null ? 26f : 18f,
                    GuardTarget is not null ? new Color(255, 160, 80) : new Color(110, 200, 230));
                break;
            case CreatureKind.AlienWhale:
            {
                // The flank spots wash the basin in slow-breathing blue — a moving reef light.
                var pulse = MathF.Sin(r.Time * 2.4f + _phase) * 6f;
                r.AddLight(Position, 30f + pulse, new Color(90, 180, 220));
                break;
            }
            case CreatureKind.Gulper:
            {
                // The anglerfish lure casts a small pulsing green glow — a false beacon in
                // the black deep water.
                var pulse = MathF.Sin(r.Time * 3f + _phase) * 0.5f + 0.5f;
                var up = planet?.UpAt(Position) ?? new Vector2(0f, -1f);
                var lure = Position + up * (Radius * 1.7f) + new Vector2(-up.Y, up.X) * (Radius * 0.9f);
                r.AddLight(lure, 20f + pulse * 8f, new Color(120, 220, 160));
                break;
            }
        }
    }

    // ---------------------------------------------------------------- small math helpers

    /// <summary>Downward acceleration on this world — the classic 320 px/s² scaled by the
    /// planet's gravity (0.45 on the Hollow asteroid, where everything falls like a feather).</summary>
    private static float Grav(Planet planet) => 320f * planet.GravityScale;

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
