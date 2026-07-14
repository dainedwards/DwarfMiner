using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

public sealed class Player
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius = 2.6f;            // pixels — short and stout
    public float Health = 100f;
    public float MaxHealth = 100f;
    public bool Grounded;

    /// <summary>Breathable air supply. Refills fast at the surface, drains with depth (faster
    /// the deeper you dig; scaled per-planet by <see cref="World.PlanetDef.OxygenDrainScale"/>).
    /// At zero the dwarf suffocates — HP bleeds until they climb back toward the surface. The
    /// air-tank upgrade raises the ceiling via <see cref="EffectiveMaxOxygen"/>.</summary>
    public float Oxygen = 100f;
    public const float BaseMaxOxygen = 100f;

    /// <summary>Crafted air-tank upgrade — one-time, raises the oxygen ceiling so deep dives
    /// last roughly twice as long before you must surface.</summary>
    public bool HasAirTank;
    /// <summary>Mothership-foundry upgrades (meta gear, re-applied on every entry like the
    /// jetpack): +50% air ceiling at tier I, doubled at tier II — multiplicative with the
    /// craftable air tank.</summary>
    public bool HasO2Recycler;
    public bool O2Tier2;

    public float EffectiveMaxOxygen =>
        BaseMaxOxygen * (HasAirTank ? 2f : 1f) * (O2Tier2 ? 2f : HasO2Recycler ? 1.5f : 1f);

    // ── Swimming ──────────────────────────────────────────────────────────────
    /// <summary>Immersion state, set each frame by Game1 from the cell sim (Player.Update
    /// has no Cells reference): body submerged flips the movement model to swimming; head
    /// submerged drains the breath meter.</summary>
    public bool InWater;
    public bool HeadInWater;

    /// <summary>Seconds of breath while the head is underwater. Refills fast in air; at zero
    /// the dwarf drowns (HP bleed, bypasses armor — see Game1.TickBreath). Lung upgrades
    /// raise the ceiling; the gill graft capstone stops the drain entirely.</summary>
    public float Breath = BaseMaxBreath;
    public const float BaseMaxBreath = 12f;

    /// <summary>Foundry aquatics (meta gear, re-applied on every entry like the jetpack):
    /// fins double swim speed; lung tiers 1/2 double/triple the breath ceiling; the gill
    /// graft breathes water — the meter never drains.</summary>
    public bool HasFins;
    public int LungTier;
    public bool HasGills;

    public float EffectiveMaxBreath => BaseMaxBreath * (LungTier >= 2 ? 3f : LungTier == 1 ? 2f : 1f);
    private float SwimSpeed => MoveSpeed * (HasFins ? 1.3f : 0.65f);

    /// <summary>Pickaxe tier 1..4. Drives base mining power and reach. Replaces the older
    /// <c>PickaxePower</c> int — kept as a tier so future augments can stack on top of a tier
    /// rather than being confused with raw power. Effective stats live in the
    /// <c>Effective…</c> getters below; never read this field for gameplay logic, always go
    /// through the getter so future augment modifiers slot in cleanly.</summary>
    public int PickaxeTier = 1;

    /// <summary>Carried-light tier 0..4. The dwarf sheds no light on the surface or in the
    /// dirt band regardless of tier (daylight covers it); below that the aura fades in and
    /// this tier sets its reach. 0 = bare headlamp stub (barely past arm's length),
    /// 1 = torch, 2 = lantern, 3 = miner's headlamp, 4 = sunstone charm. Replaces the older
    /// <c>HasLantern</c> bool so light upgrades ladder like pickaxes do.</summary>
    public int LightTier;

    /// <summary>Tools the player has crafted. Each is a one-time flag; crafting again is a
    /// no-op. Augments (future) will live in a separate flags struct beside these.</summary>
    public bool HasDrill;
    public bool HasHammer;
    public bool HasMiningLaser;
    public bool HasArmor;
    public bool HasCoreDrill;
    public bool HasPistol;
    public bool HasMachineGun;
    public bool HasLaser;
    public bool HasLaserCannon;
    public bool HasRocketLauncher;
    public bool HasFlamethrower;
    public bool HasAcidSpewer;
    public bool HasLightningGun;

    /// <summary>Mothership-foundry upgrades (not craftable in-run, not in the run save —
    /// re-applied from MetaSave on every planet entry). Jetpack: a worn BACK item (the
    /// paper-doll's Back slot — it only burns while equipped there). Hold jump while
    /// airborne to hover Noita-style: thrust is an acceleration fighting gravity, so you
    /// feather it to hold altitude and momentum carries through. The charge refills over a
    /// couple of seconds on the ground (and ONLY there). Burn: 1 s at tier I, +1 s per
    /// tier, +2 s for the final tier (1/2/3/5).</summary>
    public bool HasJetpack;
    public bool JetTier2;
    public bool JetTier3;
    public bool JetTier4;
    public float JetCharge = JetChargeMax;
    public const float JetChargeMax = 1f;     // seconds of burn (tier I)
    public float JetChargeCap => JetTier4 ? 5f : JetTier3 ? 3f : JetTier2 ? 2f : JetChargeMax;
    /// <summary>1-4 while owned (drives the exhaust flame colour: red→orange→yellow→blue),
    /// 0 without the pack.</summary>
    public int JetTier => JetTier4 ? 4 : JetTier3 ? 3 : JetTier2 ? 2 : HasJetpack ? 1 : 0;
    private float JetRiseSpeed => JetTier4 ? 110f : JetTier3 ? 95f : JetTier2 ? 82f : 70f;
    /// <summary>Net upward acceleration while thrusting (applied on top of cancelling
    /// gravity) — low enough that catching a fall takes a beat, the Noita float.</summary>
    private const float JetLift = 150f;
    /// <summary>Seconds a grounded refill takes, whatever the tier's cap.</summary>
    private const float JetRefillTime = 2.4f;
    /// <summary>True on frames the jet actually burned — Game1 reads it to emit the
    /// tier-coloured exhaust flame.</summary>
    public bool IsJetting;

    /// <summary>How far loose material sweeps into the pack — touch range by default; the
    /// Magnet Ring accessory turns it into a real pull radius.</summary>
    public float PickupReach => Equipment.HasAccessory("magnet_ring") ? 16f : 4f;

    /// <summary>Seconds of Leatherback EMP remaining. While positive the dwarf's tech is
    /// fried: the jetpack won't burn and energy weapons (laser / laser cannon / mining
    /// laser) won't fire — see Game1's UseSelectedSlot gate. Transient (not saved).</summary>
    public float EmpTimer;

    public float MineRange = 22f;          // pixels — dwarves have short reach

    /// <summary>Mining-laser beam length. Far past arm's reach — the whole point of the
    /// late-game upgrade is disintegrating rock from a distance.</summary>
    public const float MiningLaserRange = 90f;
    public float MoveSpeed = 78f;          // shorter legs
    public float JumpSpeed = 150f;
    public float Gravity = 320f;
    public float MineCooldown;
    public float ShootCooldown;

    /// <summary>Seconds of held aim it takes to raise one placed block/build stamp —
    /// placement is a short construction job, not an instant conjure, so cover can't be
    /// spammed up mid-fight. Progress accrues while LMB is held on the same target tile
    /// and abandons (resets) the frame the hold stops or the aim moves.</summary>
    public const float BuildTime = 0.45f;
    /// <summary>Seconds accrued toward the placement being built (HUD progress readout).</summary>
    public float BuildProgress;
    /// <summary>Fraction of the current build done, 0 when idle.</summary>
    public float BuildFraction => BuildProgress <= 0f ? 0f : MathF.Min(1f, BuildProgress / BuildTime);
    /// <summary>Anchor tile of the stamp under construction — a new aim target restarts.</summary>
    private (int x, int y)? _buildSite;
    /// <summary>Set by each frame's placement attempt; Update clears it — one frame without
    /// an attempt (LMB released, slot switched) abandons the build in progress.</summary>
    private bool _buildHeld;

    /// <summary>Last placed Beacon tile, in world coords. Pressing T teleports to it.</summary>
    public Vector2? BeaconWorld;

    /// <summary>9-slot equipment bar. Number keys 1..9 select a slot; LMB triggers the
    /// selected slot's primary action (mine, fire, place, throw, …). Crafted equipment
    /// auto-equips into the first empty slot via <see cref="Toolbelt.AutoEquip"/>; if all
    /// slots are full it stays in inventory and the player drags it onto a slot manually.</summary>
    public readonly Toolbelt Toolbelt = new();

    /// <summary>Paper-doll equipment (character screen, I key). Slots hold inventory ids —
    /// like the toolbelt they're pointers into the inventory, not a separate stash. Armor
    /// slots drive <see cref="DamageTakenMultiplier"/>; the torch slot drives the carried
    /// light via <see cref="EffectiveLightTier"/>.</summary>
    public readonly Equipment Equipment = new();

    /// <summary>Carried-light tier actually in effect: whatever light item sits in the torch
    /// slot. Crafting a light auto-equips it, so this normally tracks <see cref="LightTier"/>;
    /// unequipping the torch leaves the dwarf in the dark. LightTier itself stays the
    /// highest-crafted rung for recipe sequencing.</summary>
    public int EffectiveLightTier => Equipment.LightTierOf(Equipment.Get(EquipSlot.Torch));

    /// <summary>Headlamp upgrade rung (1-4) — recipe-only crafts raise it; the lamp item
    /// itself stays one "helm_lamp" in the light slot, shining harder per rung.</summary>
    public int HeadlampTier = 1;

    /// <summary>Owned melee weapons and their upgrade rung (id → 1..4). Absent = not owned.
    /// Rung 4 is the energy edge: the metal glows and the swing cuts terrain.</summary>
    public readonly Dictionary<string, int> MeleeTiers = new();

    /// <summary>Damage multiplier while guarding — set per frame by Game1 from the selected
    /// belt item (shield 0.55, tower shield 0.40, otherwise 1).</summary>
    public float GuardMul = 1f;

    /// <summary>Carried-light brightness multiplier from the worn light-slot item. The
    /// headlamp scales with its upgrade rung; the sunstone is the bottled-daylight top end.
    /// The bare 0.30 stub is the see-your-own-feet fallback with nothing worn.</summary>
    public float LightMul => Equipment.Get(EquipSlot.Torch) switch
    {
        "torch"       => 0.65f,
        "lantern"     => 1.0f,
        "helm_lamp"   => 1.5f + 0.45f * (Math.Clamp(HeadlampTier, 1, 4) - 1),
        "sun_crystal" => 3.0f,
        _             => 0.30f,
    };

    /// <summary>Hard cap on downward speed. Prevents tunneling at terminal velocity (a tile is
    /// 8 px and the body radius is ~2.6 px — uncapped, a long fall could move >8 px per frame
    /// and pass clean through a tile without ever overlapping it). Also gives a controlled,
    /// non-floaty fall feel.</summary>
    private const float MaxFallSpeed = 380f;
    /// <summary>Window after walking off a ledge during which a jump press still registers.
    /// Standard platformer "coyote time" — keeps jumps feeling forgiving.</summary>
    private const float CoyoteTime = 0.10f;
    /// <summary>Window before landing during which a jump press is buffered and triggers on
    /// touchdown. Means a slightly-early jump press still works — no missed jumps.</summary>
    private const float JumpBufferTime = 0.12f;
    /// <summary>Multiplier on gravity while ascending without the jump button held — gives the
    /// classic Mario-style variable jump (hold for full height, tap for a short hop).</summary>
    private const float JumpReleaseGravityMul = 2.4f;

    private bool _jumpHeldPrev;       // for edge detection inside Update
    private float _coyoteTimer;       // counts down from CoyoteTime after leaving ground
    private float _jumpBufferTimer;   // counts down from JumpBufferTime after a jump press

    /// <summary>Debug god mode: ghost flight (no gravity / no collision), super-pickaxe power,
    /// and extended mining reach. Toggled in-game with the G key. When on, mining uses the
    /// god values below; when off, it falls back to the tier-derived stats so crafted
    /// upgrades still persist across toggles.</summary>
    public bool FlyMode;
    public const int GodPickaxePower = 50;
    public const float GodMineRange = 200f;

    /// <summary>Mining power derived from the current pickaxe tier, plus future augment bonuses
    /// (none yet — tier acts as the sole input). Tier 1 = 1, Tier 2 = 2, etc. When fly mode is
    /// on, returns the god value so the dev tool always overrides crafted progress.</summary>
    public int EffectivePickaxePower
    {
        get
        {
            if (FlyMode) return GodPickaxePower;
            // Miner's Charm (accessory) — the first of the augment bonuses this getter was
            // always going to grow.
            return PickaxeTier + (Equipment.HasAccessory("miners_charm") ? 1 : 0);
        }
    }

    /// <summary>Mining reach. Tier III adds +20% — the platinum pickaxe has a longer haft.
    /// Future augments (e.g. crystal lens) will scale this further.</summary>
    public float EffectiveMineRange
    {
        get
        {
            if (FlyMode) return GodMineRange;
            var mul = PickaxeTier >= 3 ? 1.20f : 1.0f;
            return MineRange * mul;
        }
    }

    /// <summary>Per-tool mining cooldown. Pickaxe is the standard rhythm; drill is a near-
    /// continuous stream; hammer is slow but lands a heavy blow per swing; the mining laser
    /// out-paces even the drill — a held stream, not swings. Fly mode keeps the drill
    /// cadence so dev movement isn't gated by swing rate.</summary>
    public float MineCooldownFor(MiningTool tool)
    {
        if (FlyMode) return 0.04f;
        var cd = tool switch
        {
            MiningTool.Drill       => 0.04f,
            MiningTool.Hammer      => 0.34f,
            MiningTool.MiningLaser => 0.03f,
            _                      => 0.16f,   // pickaxe: a slower, weightier chop
        };
        // Worn gloves quicken every tool's rhythm.
        return cd * Equipment.MineSpeedMul;
    }

    /// <summary>True iff the given <paramref name="tool"/> can crack a tile of the given kind.
    /// Hammer is the only tool that breaks PlanetCore; core drill is the only thing that
    /// breaks the Core. Pickaxe + drill handle everything else, with tier IV getting a
    /// power bonus on Obsidian (handled inside TryMine, not here).</summary>
    public bool CanBreak(TileKind k, MiningTool tool)
    {
        if (FlyMode) return k != TileKind.Sky;
        var h = Tiles.Hardness(k);
        if (k == TileKind.Core) return HasCoreDrill;
        if (h >= 99) return tool == MiningTool.Hammer;
        return true;
    }

    /// <summary>Foundry Combat Plating (meta gear, re-applied on entry): a further 30% off
    /// incoming damage, multiplicative with the craftable armor.</summary>
    public bool HasPlating;

    /// <summary>Damage-take multiplier applied at the entity damage call sites. 1.0 = normal.
    /// Reduction now comes from what's actually worn on the equipment doll — chest plate 40%,
    /// helmet/leggings 10% each, boots 5% (full set 65%) — with foundry plating stacking
    /// multiplicatively on top. Crafting armor auto-equips it, so nothing is lost vs the old
    /// HasArmor flag; stripping the doll strips the protection.</summary>
    public float DamageTakenMultiplier =>
        (1f - Equipment.ArmorReduction) * (HasPlating ? 0.7f : 1.0f) * GuardMul;

    /// <summary>Apply incoming damage with armor scaling. Centralised so all damage paths
    /// (creature contact, boulder, falling chunk, sentry friendly-fire, boss attacks) honour
    /// armor. God mode (fly) is fully invulnerable — matches its no-collision/no-suffocation
    /// dev-tool intent and lets attacks be observed safely.</summary>
    public void TakeDamage(float amount)
    {
        if (FlyMode) return;
        Health -= amount * DamageTakenMultiplier;
    }

    public readonly Inventory Inventory = new();

    public Player(Vector2 pos)
    {
        Position = pos;
        // The pickaxe is intrinsic gear — it starts in the mining-tool slot the same way it
        // starts on the toolbelt.
        Equipment.Set(EquipSlot.MiningTool, "pickaxe");
    }

    public Vector2 Up(Planet planet) => planet.UpAt(Position);
    public Vector2 Right(Planet planet)
    {
        var u = Up(planet);
        return new Vector2(-u.Y, u.X); // tangent, 90° clockwise from up
    }

    /// <param name="moveAxis">-1 left, 0 idle, +1 right (in player-local tangent)</param>
    /// <param name="jumpHeld">whether the jump button is currently held (continuous, not edge).
    /// The Player tracks the previous frame's value to derive the press edge internally — this
    /// way variable-jump-height (hold = full apex / tap = short hop) works without the caller
    /// having to think about it.</param>
    /// <param name="verticalAxis">-1 down, 0 idle, +1 up (along local up). Used in fly mode and
    /// when the player is overlapping a ladder tile (climb up/down).</param>
    public void Update(float dt, Planet planet, int moveAxis, bool jumpHeld, int verticalAxis = 0)
    {
        var up = Up(planet);
        var right = new Vector2(-up.Y, up.X);

        // Band of Regeneration (accessory) — a slow trickle back to full health.
        if (Health > 0f && Health < MaxHealth && Equipment.HasAccessory("band_regen"))
            Health = MathF.Min(MaxHealth, Health + 1.2f * dt);

        // Edge-detect jump press from the held signal. Tracked across fly-mode frames too so
        // mode toggles don't accidentally trigger a buffered jump.
        var jumpEdge = jumpHeld && !_jumpHeldPrev;
        _jumpHeldPrev = jumpHeld;

        // A build under construction survives only while placement attempts keep arriving
        // (TryPlace/TryPlaceBuildId set the flag every held frame) — letting go abandons it.
        if (!_buildHeld) { BuildProgress = 0f; _buildSite = null; }
        _buildHeld = false;

        // Snapshot grounded state for the walk-off-an-edge check at the end of Update (it
        // seeds the fall so leaving a ledge drops immediately instead of hovering).
        var wasGrounded = Grounded;

        if (FlyMode)
        {
            // Ghost mode: direct velocity, no gravity, phase through tiles. For world-testing.
            var spd = MoveSpeed * 2.4f;
            Velocity = right * (moveAxis * spd) + up * (verticalAxis * spd);
            Position += Velocity * dt;
            Grounded = false;
            // Don't carry coyote/buffer state across a fly-mode session — flipping out of fly
            // shouldn't trigger a stale buffered jump.
            _coyoteTimer = 0f;
            _jumpBufferTimer = 0f;
            if (MineCooldown > 0) MineCooldown -= dt;
            if (ShootCooldown > 0) ShootCooldown -= dt;
            return;
        }

        // Input → desired tangent velocity. We project current velocity onto basis vectors,
        // adjust the tangent component, then recompose. This way gravity-aligned momentum
        // is preserved while horizontal-along-surface input is responsive.
        var vTangent = Vector2.Dot(Velocity, right);
        var vNormal = Vector2.Dot(Velocity, up);

        // Rail under-foot speed boost: walking over a Rail tile boosts MoveSpeed by 65%. The
        // probe samples the tile one body-radius below the centre so it kicks in the moment
        // the player lands on a rail and stops the moment they step off.
        var onRail = ProbeTileKind(planet, Position - up * (Radius + 1.5f)) == TileKind.Rail;
        var moveSpeed = onRail ? MoveSpeed * 1.65f : MoveSpeed;

        // Ladder overlap: gravity is heavily reduced and the vertical axis directly drives
        // up/down motion, so the player can climb without jumping. Detected by sampling the
        // tile under the player's centre — ladders span a tile, so any centre-overlap counts.
        var onLadder = ProbeTileKind(planet, Position) == TileKind.Ladder;

        var targetTangent = moveAxis * moveSpeed;
        var accel = Grounded ? 900f : 320f;   // snappier ground accel; tighter air control
        vTangent = MoveToward(vTangent, targetTangent, accel * dt);

        // Gravity only applies while airborne. When grounded, skipping it keeps vNormal at 0
        // every frame — without this, the per-frame "drop ~0.09 px under gravity, collision
        // shoves back up" cycle leaves the player visibly oscillating across the tile top
        // (especially noticeable because the sprite is bigger than the collision radius). With
        // gravity skipped, the player sits at restpoint+0.05 every frame, perfectly still.
        // Variable jump height (extra gravity on release) only matters mid-jump anyway, so
        // also gating it on !Grounded is correct.
        var grav = Gravity;
        if (vNormal > 0f && !jumpHeld && !Grounded) grav = Gravity * JumpReleaseGravityMul;
        if (!Grounded && !onLadder && !InWater) vNormal -= grav * dt;
        // On a ladder: vertical input (W/up = +1, S/down = -1) directly drives motion at a
        // climb rate; gravity is fully suppressed so the player stays put when no input is
        // given. Velocity *along* the surface (vTangent) still works, so you can climb +
        // step off sideways onto a platform.
        if (onLadder)
        {
            const float climbSpeed = 70f;
            vNormal = MoveToward(vNormal, verticalAxis * climbSpeed, 480f * dt);
        }

        // Swimming: submerged (and off ladders), the movement model flips — gravity gives
        // way to a gentle idle sink, W/S (or holding jump) stroke straight up and down, and
        // the walk axis becomes a swim stroke: slower than legs on land unless fins are
        // fitted, which make water the fast lane.
        if (InWater && !onLadder)
        {
            var strokeN = verticalAxis != 0 ? verticalAxis * SwimSpeed
                : jumpHeld ? SwimSpeed * 0.9f
                : -20f;
            vNormal = MoveToward(vNormal, strokeN, 500f * dt);
            vTangent = MoveToward(vTangent, moveAxis * SwimSpeed, 500f * dt);
        }

        if (EmpTimer > 0f) EmpTimer -= dt;

        // Jetpack: holding jump while airborne burns the pack — but only while it's worn
        // in the Back slot (like the torch, owning it isn't enough). Noita-style hover
        // physics: thrust is an acceleration that cancels gravity plus a modest lift, so
        // catching a fall takes a beat, feathering the button holds altitude, and momentum
        // carries sideways. The jump edge itself is still the ordinary jump (buffer/coyote
        // below), so a hop flows into a burn naturally. Gated off ladders so climbing
        // doesn't fight the thrust, and dead while EMP'd.
        IsJetting = false;
        if (HasJetpack && Equipment.Get(EquipSlot.Back) == "jetpack" && EmpTimer <= 0f)
        {
            // No burning underwater — holding jump already swims up, and a jet that works
            // submerged would make the whole aquatics line pointless.
            if (Grounded)
            {
                // Noita recharges levitation over a moment on the ground, not instantly.
                JetCharge = MathF.Min(JetChargeCap, JetCharge + JetChargeCap * dt / JetRefillTime);
            }
            else if (jumpHeld && !jumpEdge && !onLadder && !InWater && JetCharge > 0f)
            {
                // Above the rise cap (e.g. straight off a jump) the thrust adds nothing —
                // gravity bleeds the excess naturally instead of snapping the speed down.
                if (vNormal < JetRiseSpeed)
                    vNormal = MathF.Min(JetRiseSpeed, vNormal + (Gravity + JetLift) * dt);
                JetCharge -= dt;
                IsJetting = true;
            }
        }

        // Cap fall speed — see MaxFallSpeed comment for why this is essential.
        if (vNormal < -MaxFallSpeed) vNormal = -MaxFallSpeed;

        // Coyote time + jump buffer: classic platformer feel. Coyote keeps "still groundable"
        // for a few frames after leaving the floor so a slightly-late jump still works; buffer
        // remembers a slightly-early jump press until we touch ground.
        if (Grounded) _coyoteTimer = CoyoteTime;
        else if (_coyoteTimer > 0f) _coyoteTimer -= dt;
        if (jumpEdge) _jumpBufferTimer = JumpBufferTime;
        else if (_jumpBufferTimer > 0f) _jumpBufferTimer -= dt;

        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f)
        {
            vNormal = JumpSpeed;
            _jumpBufferTimer = 0f;
            _coyoteTimer = 0f;
            Grounded = false;
        }

        Velocity = right * vTangent + up * vNormal;

        // Substepped position + collision resolution. Each substep moves at most ~Radius * 0.6
        // so the player can never traverse a whole tile (8 px) in one substep — eliminates
        // the tunneling that lets fast falls pass through terrain.
        var stepVec = Velocity * dt;
        var moveLen = stepVec.Length();
        var maxStep = Radius * 0.6f;
        var substeps = Math.Max(1, (int)MathF.Ceiling(moveLen / maxStep));
        var subDt = dt / substeps;
        for (var i = 0; i < substeps; i++)
        {
            Position += Velocity * subDt;
            ResolveCollision(planet);
        }

        // Multi-point grounded probe. Sample three points under the feet (centre + left/right
        // foot offsets) so straddling a tile edge or standing on a one-tile pillar doesn't
        // flicker the grounded state — any solid contact counts.
        var feetCentre = Position - up * (Radius + 1.5f);
        var footOff = right * (Radius * 0.7f);
        Grounded = ProbeSolid(planet, feetCentre)
                || ProbeSolid(planet, feetCentre + footOff)
                || ProbeSolid(planet, feetCentre - footOff);

        // Walked off an edge (not a jump): seed a little downward velocity so the fall arc
        // begins this frame instead of hovering while gravity accumulates from zero. The old
        // ground-snap teleported the player onto any tile within one ring here — and since
        // terrain steps are always whole rings, every ledge walk-off popped instantly onto
        // the block below instead of falling. Now every drop is a real gravity arc, same as
        // the descent of a jump.
        if (!Grounded && wasGrounded && Vector2.Dot(Velocity, up) <= 0f)
            Velocity -= up * 40f;

        // When grounded, scrub any residual along-up velocity (it should be zero — the player
        // is sitting on a surface). This kills the curvature-reprojection drift that would
        // otherwise leak a little vNormal into V each frame, and the tiny downward velocity
        // collision can't fully cancel due to float precision. Clean rest pose.
        if (Grounded)
        {
            var vN = Vector2.Dot(Velocity, up);
            Velocity -= up * vN;
        }

        if (MineCooldown > 0) MineCooldown -= dt;
        if (ShootCooldown > 0) ShootCooldown -= dt;
    }

    /// <summary>
    /// Push the player out of any polar tile it's overlapping. Each tile is treated as a
    /// rotated rect aligned with its local-up; the player (a circle) projects into tile-local
    /// coords, clamps to the tile's local AABB, and gets pushed along the world-space direction
    /// of the resulting separation. Iterative for stability.
    ///
    /// <b>Inside-tile escape</b>: when the player center is inside a tile's AABB (distSq=0),
    /// escape toward the closest *sky neighbor* — not always along planet-up. Always-up
    /// was wrong: jumping into a ceiling, the up-push drives the player deeper, clipping
    /// through. The neighbor-aware choice picks the side that opens onto empty space.
    /// </summary>
    private void ResolveCollision(Planet planet)
    {
        for (var iter = 0; iter < 4; iter++)
        {
            var (tx, _) = planet.WorldToTile(Position);
            // Neighbour columns are recomputed per ring from the true world angle: rings
            // have different tile counts, so reusing this ring's ty index drifts by whole
            // tiles near the angle-2π wrap and leaves collision holes.
            var relC = Position - planet.Center;
            var ang = MathF.Atan2(relC.Y, relC.X);
            if (ang < 0) ang += MathHelper.TwoPi;
            var pushed = false;
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = tx + dx;
                if (x < 0 || x >= planet.Rings) continue;
                var nRing = planet.TilesAt(x);
                var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
                for (var dy = -2; dy <= 2; dy++)
                {
                    var y = ty0 + dy;
                    var tk = planet.Get(x, y);
                    if (!Tiles.BlocksPlayer(tk)) continue;

                    var centre = planet.TileToWorld(x, y);
                    var up = planet.UpAt(centre);
                    var right = new Vector2(-up.Y, up.X);
                    var rel = Position - centre;
                    var pLocalX = Vector2.Dot(rel, right);
                    var pLocalY = Vector2.Dot(rel, up);

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

                        // World-space push direction = local separation vector mapped through (right, up).
                        var n = right * (diffX / dist) + up * (diffY / dist);
                        Position += n * (Radius - dist + 0.05f);
                        var into = Vector2.Dot(Velocity, n);
                        if (into < 0) Velocity -= n * into;
                        pushed = true;
                    }
                    else if (distSq <= 0.0001f)
                    {
                        // Player center is inside the tile's local AABB. Escape toward the
                        // *closest sky neighbor* — that's the direction guaranteed to lead out
                        // of solid mass. (Local +X = +y in tile coords because Planet.TileToWorld
                        // angles increase with y; local +Y = +x because rings grow outward
                        // radially.) If no sky neighbor exists (player buried deep in solid),
                        // default to planet-up: surface escape is the most common case.
                        var skyOuter = !Tiles.IsSolid(planet.Get(x + 1, y));   // outward radial
                        var skyInner = !Tiles.IsSolid(planet.Get(x - 1, y));   // inward radial
                        var skyRight = !Tiles.IsSolid(planet.Get(x, y + 1));   // +tangent
                        var skyLeft  = !Tiles.IsSolid(planet.Get(x, y - 1));   // -tangent

                        var nLocalX = 0f; var nLocalY = 0f; var minDist = float.MaxValue;
                        if (skyOuter && (halfY - pLocalY) < minDist) { nLocalX = 0f; nLocalY =  1f; minDist = halfY - pLocalY; }
                        if (skyInner && (pLocalY + halfY) < minDist) { nLocalX = 0f; nLocalY = -1f; minDist = pLocalY + halfY; }
                        if (skyRight && (halfX - pLocalX) < minDist) { nLocalX =  1f; nLocalY = 0f; minDist = halfX - pLocalX; }
                        if (skyLeft  && (pLocalX + halfX) < minDist) { nLocalX = -1f; nLocalY = 0f; minDist = pLocalX + halfX; }
                        if (minDist == float.MaxValue)
                        {
                            // Buried — no sky neighbor. Default to planet-up (handles spawn
                            // case where the player is dropped into the surface column).
                            nLocalY = 1f;
                            minDist = halfY - pLocalY;
                        }

                        var n = right * nLocalX + up * nLocalY;
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

    private bool ProbeSolid(Planet planet, Vector2 worldPoint)
    {
        var (x, y) = planet.WorldToTile(worldPoint);
        // BlocksPlayer (not IsSolid) — ladders/glowshrooms/beacons are "solid" tiles in the
        // data sense (rendered, anchored, mineable) but the player walks through them, so
        // they shouldn't ground the player or count as floor.
        return Tiles.BlocksPlayer(planet.Get(x, y));
    }

    /// <summary>Sample the tile kind at a world point. Used by Update to detect under-foot
    /// rails (speed boost) and ladder overlap (climb mode).</summary>
    private static TileKind ProbeTileKind(Planet planet, Vector2 worldPoint)
    {
        var (x, y) = planet.WorldToTile(worldPoint);
        return planet.Get(x, y);
    }

    /// <summary>The open (Sky) tiles of the 2×2 footprint under the cursor that one placed
    /// block will fill — grown toward the cursor's position inside the aimed tile, so the
    /// stamp hugs where the player points. Empty when the aimed tile is occupied or every
    /// footprint tile would seal the dwarf in. One inventory unit buys the whole stamp: a
    /// 2×2 of 4-px tiles is exactly the legacy block the unit was mined from (and mining it
    /// back returns exactly one unit through the dust economy).</summary>
    private List<(int x, int y)>? PlacementStamp(Planet planet, Vector2 worldCursor, bool passable)
    {
        var (x, y) = planet.WorldToTile(worldCursor);
        if (planet.Get(x, y) != TileKind.Sky) return null;
        List<(int x, int y)>? stamp = null;
        foreach (var (fx, fy) in planet.Footprint2x2(x, y, worldCursor))
        {
            if (planet.Get(fx, fy) != TileKind.Sky) continue;
            // Don't seal the dwarf inside a tile — keep at least a body's distance for
            // blocking tiles. (Passable placeables can go right at the feet.)
            if (!passable
                && (planet.TileToWorld(fx, fy) - Position).Length() < Radius + Planet.TileSize * 0.55f)
                continue;
            (stamp ??= new List<(int x, int y)>()).Add((fx, fy));
        }
        return stamp;
    }

    /// <summary>Place a block from inventory into the sky tiles under the cursor —
    /// Terraria rules now: INSTANT placement (hold LMB to paint a run of blocks at the
    /// cooldown's rhythm), but a block needs support: an adjacent solid tile or a back
    /// wall behind it. Stone first, then the richer variants, then dirt.</summary>
    public TileKind? TryPlace(Planet planet, Physics physics, Vector2 worldCursor, float dt)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;
        if (PlacementStamp(planet, worldCursor, passable: false) is not { } stamp) return null;
        if (!PlacementSupported(planet, stamp)) return null;

        // Priority: plain stone (most abundant) → richer stone variants (granite/basalt/etc) →
        // dirt. Each variant places its own tile kind so a granite stockpile builds granite
        // walls, not generic stone — preserves the resource's identity through placement.
        (TileKind kind, string id) pick;
        if      (Inventory.Count("stone") > 0)      pick = (TileKind.Stone, "stone");
        else if (Inventory.Count("granite") > 0)    pick = (TileKind.Granite, "granite");
        else if (Inventory.Count("basalt") > 0)     pick = (TileKind.Basalt, "basalt");
        else if (Inventory.Count("moss_stone") > 0) pick = (TileKind.MossStone, "moss_stone");
        else if (Inventory.Count("obsidian") > 0)   pick = (TileKind.Obsidian, "obsidian");
        else if (Inventory.Count("gravel") > 0)     pick = (TileKind.Gravel, "gravel");
        else if (Inventory.Count("dirt") > 0)       pick = (TileKind.Dirt, "dirt");
        else return null;

        if (!Inventory.TryConsume(pick.id, 1)) return null;
        var placed = pick.kind;

        foreach (var (fx, fy) in stamp)
        {
            planet.Set(fx, fy, placed);
            physics.MarkDirty(fx, fy);
        }
        MineCooldown = 0.14f;   // painting rhythm while LMB is held
        return placed;
    }

    /// <summary>Terraria's support rule: at least one stamp tile must touch a solid
    /// neighbour or sit over a back wall — no free-floating blocks conjured in mid-air.</summary>
    private static bool PlacementSupported(Planet planet, List<(int x, int y)> stamp)
    {
        foreach (var (fx, fy) in stamp)
        {
            if (planet.GetWall(fx, fy) != TileKind.Sky) return true;
            if (Tiles.IsSolid(planet.Get(fx, fy - 1))) return true;
            if (Tiles.IsSolid(planet.Get(fx, fy + 1))) return true;
            if (fx > 0 && Tiles.IsSolid(planet.Get(fx - 1, fy))) return true;
            if (fx < planet.Rings - 1 && Tiles.IsSolid(planet.Get(fx + 1, fy))) return true;
        }
        return false;
    }

    /// <summary>Placement preview for the HUD: the stamp tiles the next click would fill,
    /// plus whether the placement is currently legal (range, support, material in stock).</summary>
    public (List<(int x, int y)> stamp, bool valid)? PlacePreview(Planet planet, Vector2 worldCursor)
    {
        if (PlacementStamp(planet, worldCursor, passable: false) is not { } stamp) return null;
        var hasMat = Inventory.Count("stone") > 0 || Inventory.Count("granite") > 0
            || Inventory.Count("basalt") > 0 || Inventory.Count("moss_stone") > 0
            || Inventory.Count("obsidian") > 0 || Inventory.Count("gravel") > 0
            || Inventory.Count("dirt") > 0;
        var valid = (worldCursor - Position).Length() <= EffectiveMineRange
            && PlacementSupported(planet, stamp) && hasMat;
        return (stamp, valid);
    }

    /// <summary>Advance the held-construction timer toward the stamp anchored at the cursor
    /// tile. Returns true the frame the build completes (and resets for the next one). A new
    /// target tile restarts the clock; fly mode builds instantly (dev tool).</summary>
    private bool TickBuild(Planet planet, Vector2 worldCursor, float dt)
    {
        _buildHeld = true;
        if (FlyMode) return true;
        var site = planet.WorldToTile(worldCursor);
        if (_buildSite != site)
        {
            _buildSite = site;
            BuildProgress = 0f;
        }
        BuildProgress += dt;
        if (BuildProgress < BuildTime) return false;
        BuildProgress = 0f;
        _buildSite = null;
        return true;
    }

    /// <summary>Where the current tool's strike actually lands. The pick and hammer are swung
    /// tools: the strike marches a short ray from the body toward the aim point and lands on
    /// the first rock within arm's reach — the cursor gives the direction, the dwarf's
    /// position gives the origin, so you can't chip tiles hidden behind a wall or out past
    /// your reach. The mining laser is the same ray at beam range. The drill (a point tool
    /// held against the wall) and god mode keep classic cursor-tile targeting. Returns null
    /// when the swing whiffs — no rock along the ray, or cursor on sky / out of range in
    /// cursor mode.</summary>
    public (int X, int Y)? ResolveMineTarget(Planet planet, Vector2 worldCursor, MiningTool tool)
    {
        if (FlyMode || tool == MiningTool.Drill)
        {
            if ((worldCursor - Position).Length() > EffectiveMineRange) return null;
            var (cx, cy) = planet.WorldToTile(worldCursor);
            return planet.Get(cx, cy) == TileKind.Sky ? null : (cx, cy);
        }

        var aim = worldCursor - Position;
        if (aim.LengthSquared() < 0.001f) return null;
        var dir = Vector2.Normalize(aim);
        var range = tool == MiningTool.MiningLaser ? MiningLaserRange : EffectiveMineRange;

        // Skip the tile the dwarf's own centre occupies (a ladder being climbed, a glowshroom
        // at the feet) unless the cursor is actually inside it — otherwise every swing taken
        // from a ladder would chew the ladder instead of the wall being aimed at. Step 1 px:
        // comfortably under the 4 px tile so the ray can't jump a tile corner.
        var self = planet.WorldToTile(Position);
        var cursorTile = planet.WorldToTile(worldCursor);
        for (var t = 0f; t <= range; t += 1f)
        {
            var (x, y) = planet.WorldToTile(Position + dir * t);
            if ((x, y) == self && (x, y) != cursorTile) continue;
            if (planet.Get(x, y) != TileKind.Sky) return (x, y);
        }
        return null;
    }

    /// <summary>Try to mine with a specific tool at the strike point resolved by
    /// <see cref="ResolveMineTarget"/>. Pickaxe is the default; drill / hammer / laser change
    /// cooldown + power profile. PlanetCore needs the hammer, the Core needs the core drill.
    /// The pickaxe and hammer normally mine via the physical swing instead
    /// (<see cref="TryStartSwing"/> / <see cref="UpdateSwing"/>) — this path still serves the
    /// drill, the mining laser, and fly mode's cursor targeting.</summary>
    public TileKind? TryMine(Planet planet, Physics physics, Vector2 worldCursor, MiningTool tool = MiningTool.Pickaxe)
    {
        if (MineCooldown > 0) return null;
        if (ResolveMineTarget(planet, worldCursor, tool) is not { } target) return null;
        var (x, y) = target;
        MineCooldown = MineCooldownFor(tool);   // spent whether or not the tile can break
        if (!CanBreak(planet.Get(x, y), tool)) return null;
        return StrikeTile(planet, physics, x, y, tool, worldCursor);
    }

    /// <summary>Every tile broken by the most recent strike — Game1 reads this after
    /// <see cref="TryMine"/> / <see cref="UpdateSwing"/> to spawn dust piles and stats per
    /// tile. A strike covers a 2×2 footprint (one legacy 8-px tile), so up to four entries.</summary>
    public readonly List<(int X, int Y, TileKind Kind)> LastBroken = new();

    /// <summary>Land one tool blow: the tool-aware power profile applied to
    /// <see cref="Planet.Mine"/> across the 2×2 footprint grown from the struck tile toward
    /// the aim point — tiles are 4 px now, so a single blow still clears the old full-size
    /// tile's worth of rock. Tier IV gets a 2× power bonus on Obsidian; hammer gets a flat
    /// power floor + an effective-hardness override so it bites bedrock at all; the mining
    /// laser doubles the pick's power (floor 6) on top of its stream cadence. Shared by the
    /// cursor path (<see cref="TryMine"/>) and the swing hitbox (<see cref="UpdateSwing"/>).
    /// Returns the broken kind of the aimed tile (or the first that broke); every broken
    /// tile lands in <see cref="LastBroken"/>.</summary>
    private TileKind? StrikeTile(Planet planet, Physics physics, int x, int y, MiningTool tool,
        Vector2 towards)
    {
        LastBroken.Clear();
        TileKind? primary = null;
        var nAim = planet.TilesAt(x);
        var aimed = (x, ((y % nAim) + nAim) % nAim);   // wrapped, to match Footprint2x2 output
        foreach (var (fx, fy) in planet.Footprint2x2(x, y, towards))
        {
            var k = planet.Get(fx, fy);
            if (k == TileKind.Sky || !CanBreak(k, tool)) continue;
            var power = EffectivePickaxePower;
            int? effectiveHardness = null;
            if (k == TileKind.Obsidian && PickaxeTier >= 4) power *= 2;
            if (tool == MiningTool.Hammer)
            {
                power = Math.Max(power, 4);
                // Treat PlanetCore as basalt-class so the hammer's swing actually does damage.
                // Other tiles take normal hardness — the boost is the power floor only.
                if (k == TileKind.PlanetCore) effectiveHardness = 8;
            }
            if (tool == MiningTool.MiningLaser) power = Math.Max(power * 2, 6);

            if (planet.Mine(fx, fy, power, effectiveHardness) is { } bk)
            {
                physics.MarkDirty(fx, fy);
                // Drop is not credited instantly — Game1 spawns a dust pile per broken tile
                // and the player collects it by walking through (Cells.CollectInRadius).
                LastBroken.Add((fx, fy, bk));
                if (primary is null || (fx, fy) == aimed) primary = bk;
            }
        }
        return primary;
    }

    // ---- Physical swing: pickaxe & hammer -----------------------------------------------
    // The swung tools are real objects now, not an invisible ray. LMB starts a swing: the
    // tool sweeps SwingArc radians through the aim direction over the tool's cooldown, and
    // the strike lands where the blade actually contacts rock during the sweep. One strike
    // per swing; the next swing starts as this one ends, so the mining cadence is unchanged.

    public float SwingTime;                    // seconds remaining in the active swing
    public float SwingDuration;                // full length of the active swing
    public Vector2 SwingAim = new(1f, 0f);     // unit aim captured at swing start
    public Vector2 SwingCursor;                // raw world cursor captured at swing start
    public MiningTool SwingTool;               // tool the active swing belongs to
    public int SwingFlip = 1;                  // alternates so consecutive chops go down-up-down
    public bool SwingLanded;                   // the active swing already spent its strike

    /// <summary>Total sweep of a swing, centred on the aim (~109°).</summary>
    public const float SwingArc = 1.9f;
    /// <summary>Gap between the dwarf's centre and the tool's grip end.</summary>
    public const float SwingHandOffset = 1.5f;
    /// <summary>Drawn length of the swung tool. Tier III's longer platinum haft shows.</summary>
    public float SwingToolLen => (PickaxeTier >= 3 ? 1.2f : 1.0f) * 5.4f;
    /// <summary>How far the swing's strike reaches from the body: hand offset + tool length
    /// + a little slop. Tied to the drawn sprite — the pick only mines what its head can
    /// visibly touch, not out to the old ray range.</summary>
    public float SwingReach => SwingHandOffset + SwingToolLen + 1.0f;
    /// <summary>Fraction of the swing that is wind-up — contact can't land before it, so the
    /// slow hammer visibly rears back before the blow while the quick pick barely pauses.</summary>
    public const float SwingWindup = 0.25f;

    public bool SwingActive => SwingTime > 0f;
    public float SwingProgress => SwingActive ? 1f - SwingTime / SwingDuration : 1f;

    /// <summary>Arm angle of the active swing at a given progress: sweeps from one side of the
    /// aim to the other, smoothstep-eased so the blade accelerates into the strike. The draw
    /// code uses the same angle as the hitbox, so the strike lands exactly where the head is.</summary>
    public float SwingAngleAt(float progress)
    {
        var theta = MathF.Atan2(SwingAim.Y, SwingAim.X);
        var s = progress * progress * (3f - 2f * progress);
        return theta + SwingFlip * SwingArc * (0.5f - s);
    }

    public float SwingAngle => SwingAngleAt(SwingProgress);

    /// <summary>Begin a pickaxe/hammer swing toward the cursor. Fails while the previous swing
    /// (or its cooldown) is still in flight. The swing direction alternates each time, and each
    /// swing starts where the last one ended — a natural chopping pendulum.</summary>
    public bool TryStartSwing(Vector2 worldCursor, MiningTool tool)
    {
        if (MineCooldown > 0 || SwingActive) return false;
        var aim = worldCursor - Position;
        SwingCursor = worldCursor;
        SwingAim = aim.LengthSquared() > 0.001f ? Vector2.Normalize(aim) : new Vector2(1f, 0f);
        SwingTool = tool;
        SwingFlip = -SwingFlip;
        SwingDuration = SwingTime = MineCooldownFor(tool);
        MineCooldown = SwingDuration;
        SwingLanded = false;
        return true;
    }

    /// <summary>One landed swing contact: the tile the blade struck, its kind, and what broke
    /// (null when the blow only damaged it — or clinked off something this tool can't break).</summary>
    public readonly record struct SwingStrike(int X, int Y, TileKind Kind, TileKind? Broken);

    /// <summary>Advance the active swing and resolve its hitbox. Called every frame by Game1
    /// (independent of LMB, so a started swing always completes). Past the wind-up, the blade
    /// looks for its strike: first straight along the aim (so precision digging targets the
    /// tile under the cursor, and can't reach through walls), then swept across the arc
    /// covered so far (so the swipe still bites rock beside the aim on a near-miss). Samples
    /// run body-out at 1 px — under the 4 px tile so nothing is skipped.</summary>
    public SwingStrike? UpdateSwing(Planet planet, Physics physics, float dt)
    {
        if (!SwingActive) return null;
        SwingTime = MathF.Max(0f, SwingTime - dt);
        if (SwingLanded || SwingProgress < SwingWindup) return null;

        var reach = SwingReach;
        var self = planet.WorldToTile(Position);
        var aimedTile = planet.WorldToTile(SwingCursor);

        (int X, int Y)? Contact(Vector2 dir)
        {
            for (var t = 0f; t <= reach; t += 1f)
            {
                var (x, y) = planet.WorldToTile(Position + dir * t);
                // Don't chew the ladder being climbed — unless it's what's being aimed at.
                if ((x, y) == self && (x, y) != aimedTile) continue;
                if (planet.Get(x, y) != TileKind.Sky) return (x, y);
            }
            return null;
        }

        var hit = Contact(SwingAim);
        if (hit is null)
        {
            // Sweep the arc from the end of the wind-up to the blade's current angle.
            var from = SwingAngleAt(SwingWindup);
            var to = SwingAngle;
            var steps = Math.Max(1, (int)(MathF.Abs(to - from) / 0.12f));
            for (var i = 0; i <= steps && hit is null; i++)
            {
                var a = from + (to - from) * i / steps;
                hit = Contact(new Vector2(MathF.Cos(a), MathF.Sin(a)));
            }
        }
        if (hit is not { } h) return null;

        SwingLanded = true;
        var k = planet.Get(h.X, h.Y);
        if (!CanBreak(k, SwingTool)) return new SwingStrike(h.X, h.Y, k, null);
        return new SwingStrike(h.X, h.Y, k, StrikeTile(planet, physics, h.X, h.Y, SwingTool, SwingCursor));
    }

    /// <summary>Backwards-compat overload: defaults to the pickaxe.</summary>
    public TileKind? TryMine(Planet planet, Physics physics, Vector2 worldCursor)
        => TryMine(planet, physics, worldCursor, MiningTool.Pickaxe);

    /// <summary>Place a build item at the cursor by inventory id. Each placeable's id maps
    /// to a tile kind via <see cref="BuildIdToTile"/>; the inventory entry with that same id
    /// is debited 1. Held construction like <see cref="TryPlace"/>: the build only lands
    /// after <see cref="BuildTime"/> of sustained aim. Returns the placed tile kind if it
    /// landed in a sky tile and stock was available; null otherwise.</summary>
    public TileKind? TryPlaceBuildId(Planet planet, Physics physics, Vector2 worldCursor, string invId, float dt)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;

        var placedKind = BuildIdToTile(invId);
        if (placedKind == TileKind.Sky) return null;   // unrecognised build id

        // Passable build items (ladder/glowshroom/beacon) skip the seal-in check inside the
        // stamp, so you can drop a torch right next to your feet.
        if (PlacementStamp(planet, worldCursor, Tiles.IsPassable(placedKind)) is not { } stamp)
            return null;
        if (Inventory.Count(invId) <= 0) return null;
        if (!TickBuild(planet, worldCursor, dt)) return null;
        if (!Inventory.TryConsume(invId, 1)) return null;

        foreach (var (fx, fy) in stamp)
        {
            planet.Set(fx, fy, placedKind);
            physics.MarkDirty(fx, fy);
        }
        if (placedKind == TileKind.Beacon)
            BeaconWorld = planet.TileToWorld(stamp[0].x, stamp[0].y);
        MineCooldown = 0.10f;
        return placedKind;
    }

    /// <summary>Inventory id → tile kind for placeable build items. Drives both placement and
    /// the toolbelt's icon picker. Returns Sky for ids that aren't placeable.</summary>
    public static TileKind BuildIdToTile(string invId) => invId switch
    {
        "support"            => TileKind.Support,
        "reinforced_support" => TileKind.ReinforcedSupport,
        "ladder"             => TileKind.Ladder,
        "door"               => TileKind.DoorClosed,
        "rail"               => TileKind.Rail,
        "glowshroom"         => TileKind.Glowshroom,
        "beacon"             => TileKind.Beacon,
        _                    => TileKind.Sky,
    };

    private static float MoveToward(float v, float target, float maxDelta)
    {
        var d = target - v;
        if (MathF.Abs(d) <= maxDelta) return target;
        return v + MathF.Sign(d) * maxDelta;
    }
}

/// <summary>Which mining tool the player is using this swing. Drives cooldown, power floor,
/// targeting mode (swung ray vs cursor tile vs beam), and which tile classes are breakable.
/// Selected via the active toolbelt slot.</summary>
public enum MiningTool { Pickaxe, Drill, Hammer, MiningLaser }

/// <summary>
/// 24-slot equipment belt. Crafted equipment auto-equips into the first empty slot via
/// <see cref="AutoEquip"/>. Slots store inventory ids ("pickaxe", "drill", "ladder",
/// "ammo_diamond", …) — Game1 dispatches the slot's primary action by id when the player
/// LMBs. Drag-and-drop UI in Game1 calls <see cref="Swap"/> / <see cref="SetSlot"/>.
///
/// Default loadout at construction: slot 0 = pickaxe (mine), 1 = bullets (basic shot),
/// 2 = blocks (place stone-class blocks). All three are permanent and always available;
/// the player can rearrange them but they can't be removed from the belt entirely (when
/// dragged off, they relocate to the first-available empty slot).
/// </summary>
public sealed class Toolbelt
{
    // 24 slots: 3 intrinsic tools + the full weapon armoury god mode loans out + generous
    // headroom for crafted tools, consumables, placeables, and future items — so nothing gets
    // stranded off the belt when god mode fills it. The HUD strip auto-scales to fit whatever
    // this is set to, so growing it later is a one-line change. Number keys only reach the
    // first 9; the rest are selected by wheel, Q/E weapon cycle, or clicking the HUD slot.
    public const int SlotCount = 24;
    public readonly string?[] Slots = new string?[SlotCount];
    public int Selected;

    public Toolbelt()
    {
        Slots[0] = "pickaxe";
        Slots[1] = "bullets";
        Slots[4] = "blocks";   // slot 5 on the HUD — the first general-item slot
    }

    /// <summary>The crafted melee arsenal (one- and two-handed) — weapon-slot items with
    /// per-weapon upgrade rungs in Player.MeleeTiers.</summary>
    public static readonly string[] MeleeIds =
        { "sword", "mace", "warhammer", "shield", "great_sword", "great_mace", "great_hammer", "tower_shield" };

    public static bool IsMiningToolId(string id) =>
        id is "pickaxe" or "drill" or "hammer" or "mining_laser";

    /// <summary>Everything the weapon slots (2-4 on the HUD) accept: firearms, throwables,
    /// the intrinsic bullets, and the melee arsenal.</summary>
    public static bool IsWeaponSlotId(string id)
    {
        if (id is "bullets" or "pistol" or "machine_gun" or "laser" or "laser_cannon"
            or "rocket_launcher" or "cannon" or "flamethrower" or "acid_spewer"
            or "lightning_gun" or "dynamite" or "tnt" or "harpoon" or "nuke") return true;
        foreach (var m in MeleeIds) if (m == id) return true;
        return false;
    }

    /// <summary>Hotbar layout rule: slot 1 (index 0) is the mining tool, slots 2-4 are
    /// weapons, slots 5-9 are general items; the overflow slots past 9 take anything
    /// (god-mode armoury spill, spares).</summary>
    public static bool SlotAccepts(int slot, string id)
    {
        if (slot == 0) return IsMiningToolId(id);
        if (slot <= 3) return IsWeaponSlotId(id);
        if (slot <= 8) return !IsMiningToolId(id) && !IsWeaponSlotId(id);
        return true;
    }

    /// <summary>Place <paramref name="id"/> into the first empty slot that accepts it.
    /// No-op if the id is already on the belt or no fitting slot is free.</summary>
    public bool AutoEquip(string id)
    {
        for (var i = 0; i < SlotCount; i++) if (Slots[i] == id) return false;
        for (var i = 0; i < SlotCount; i++)
            if (Slots[i] is null && SlotAccepts(i, id)) { Slots[i] = id; return true; }
        return false;
    }

    public string? Current => (Selected >= 0 && Selected < SlotCount) ? Slots[Selected] : null;

    public bool Contains(string id)
    {
        for (var i = 0; i < SlotCount; i++) if (Slots[i] == id) return true;
        return false;
    }

    public int FirstEmpty()
    {
        for (var i = 0; i < SlotCount; i++) if (Slots[i] is null) return i;
        return -1;
    }

    /// <summary>Swap slot contents.</summary>
    public void Swap(int a, int b)
    {
        if (a < 0 || a >= SlotCount || b < 0 || b >= SlotCount || a == b) return;
        (Slots[a], Slots[b]) = (Slots[b], Slots[a]);
    }

    /// <summary>Drop <paramref name="id"/> into <paramref name="slot"/>, displacing whatever
    /// was there. The displaced id is returned so the caller can decide where it goes (back
    /// to inventory for stackables, first-empty-slot for permanents).</summary>
    public string? SetSlot(int slot, string? id)
    {
        if (slot < 0 || slot >= SlotCount) return id;
        var prev = Slots[slot];
        Slots[slot] = id;
        return prev;
    }

    /// <summary>Permanent ids: tools the player owns forever, can't be deleted from the belt.
    /// Pickaxe / bullets / blocks are intrinsic from spawn; drill / hammer / cannon /
    /// core_drill / sentry are unlocks that occupy a slot once crafted but stay on the belt
    /// (dragging one onto inventory just re-routes to first empty slot instead).</summary>
    public static bool IsPermanent(string id) => id is
        "pickaxe" or "bullets" or "blocks" or
        "drill" or "hammer" or "mining_laser" or "cannon" or "core_drill" or
        "pistol" or "machine_gun" or "laser" or "laser_cannon" or "rocket_launcher";
}

public sealed class Inventory
{
    private readonly Dictionary<string, int> _items = new();
    public IReadOnlyDictionary<string, int> Items => _items;

    public void Add(string id, int count)
    {
        _items.TryGetValue(id, out var existing);
        _items[id] = existing + count;
    }

    public bool TryConsume(string id, int count)
    {
        if (!_items.TryGetValue(id, out var have) || have < count) return false;
        _items[id] = have - count;
        return true;
    }

    public int Count(string id) => _items.GetValueOrDefault(id, 0);
}

/// <summary>The character screen's paper-doll slots. Order is the serialization order in
/// RunSave — append only.</summary>
public enum EquipSlot
{
    Torch = 0, Head = 1, Chest = 2, Legs = 3, Feet = 4,
    Weapon1 = 5, Weapon2 = 6, MiningTool = 7,
    Gloves = 8, Accessory1 = 9, Accessory2 = 10,
    Back = 11,
}

/// <summary>
/// Worn equipment — the character screen's paper doll. Slots store inventory ids and, like
/// the toolbelt, are pointers to the inventory entry rather than a separate stash: equipping
/// never changes counts, so the same pistol can sit on the belt and in a weapon slot.
/// Armor slots feed <see cref="Player.DamageTakenMultiplier"/>; the torch slot feeds
/// <see cref="Player.EffectiveLightTier"/>; weapon / mining-tool slots are the loadout the
/// doll displays (use still dispatches through the toolbelt).
/// </summary>
public sealed class Equipment
{
    public const int SlotCount = 12;
    public readonly string?[] Slots = new string?[SlotCount];

    public string? Get(EquipSlot s) => Slots[(int)s];
    public void Set(EquipSlot s, string? id) => Slots[(int)s] = id;

    /// <summary>Slot gating — which ids each paper-doll slot accepts.</summary>
    public static bool Fits(string id, EquipSlot slot) => slot switch
    {
        EquipSlot.Torch      => LightTierOf(id) > 0,
        EquipSlot.Head       => id is "iron_helmet" or "chitin_helmet",
        EquipSlot.Chest      => id is "armor" or "chitin_armor",
        EquipSlot.Legs       => id is "iron_leggings" or "chitin_leggings",
        EquipSlot.Feet       => id is "iron_boots" or "chitin_boots",
        EquipSlot.Gloves     => id is "leather_gloves" or "iron_gauntlets",
        EquipSlot.Weapon1 or EquipSlot.Weapon2 => IsWeapon(id),
        EquipSlot.MiningTool => id is "pickaxe" or "drill" or "hammer" or "mining_laser",
        EquipSlot.Accessory1 or EquipSlot.Accessory2 => IsAccessory(id),
        EquipSlot.Back       => id is "jetpack",
        _ => false,
    };

    /// <summary>Trinkets the two accessory slots accept — each carries a passive effect
    /// wired into the Player getters (regen tick, magnet pull, +1 mining power, −10% damage).</summary>
    public static bool IsAccessory(string id) => id is
        "band_regen" or "magnet_ring" or "miners_charm" or "aegis_pendant";

    /// <summary>True while the id sits in either accessory slot.</summary>
    public bool HasAccessory(string id) =>
        Get(EquipSlot.Accessory1) == id || Get(EquipSlot.Accessory2) == id;

    /// <summary>Mining-cooldown multiplier from worn gloves — leather 15% faster swings,
    /// iron gauntlets 30%.</summary>
    public float MineSpeedMul => Get(EquipSlot.Gloves) switch
    {
        "leather_gloves" => 0.85f,
        "iron_gauntlets" => 0.70f,
        _ => 1f,
    };

    /// <summary>Firearms + melee the doll's weapon slots accept. Throwables/ammo stay
    /// toolbelt-only — the doll shows what the dwarf carries at the ready, not the satchel.</summary>
    public static bool IsWeapon(string id)
    {
        if (id is "pistol" or "machine_gun" or "laser" or "laser_cannon" or "rocket_launcher"
            or "cannon" or "flamethrower" or "acid_spewer" or "lightning_gun") return true;
        foreach (var m in Toolbelt.MeleeIds) if (m == id) return true;
        return false;
    }

    public static bool IsEquippable(string id)
    {
        for (var s = 0; s < SlotCount; s++)
            if (Fits(id, (EquipSlot)s)) return true;
        return false;
    }

    public bool IsEquipped(string id)
    {
        for (var s = 0; s < SlotCount; s++)
            if (Slots[s] == id) return true;
        return false;
    }

    /// <summary>Craft-time convenience: put the id into the first fitting slot that's empty
    /// (weapons try slot 1 then 2). No-op if it's already worn or every fitting slot is
    /// occupied — the character screen exists for deliberate swaps.</summary>
    public bool AutoEquip(string id)
    {
        if (IsEquipped(id)) return false;
        for (var s = 0; s < SlotCount; s++)
            if (Fits(id, (EquipSlot)s) && Slots[s] is null) { Slots[s] = id; return true; }
        return false;
    }

    /// <summary>Light tier of a carried-light id, 0 for anything else (including null).</summary>
    public static int LightTierOf(string? id) => id switch
    {
        "torch" => 1, "lantern" => 2, "helm_lamp" => 3, "sun_crystal" => 4, _ => 0,
    };

    /// <summary>Summed damage reduction from worn armor: chest 40% (the old full-armor
    /// value), helmet and leggings 10% each, boots and gauntlets 5% — plus 10% from the
    /// Aegis Pendant accessory. Full iron kit with the pendant reaches 80%, so the sum is
    /// left uncapped short of that.</summary>
    public float ArmorReduction =>
        (Get(EquipSlot.Chest) is not null ? 0.40f : 0f) +
        (Get(EquipSlot.Head)  is not null ? 0.10f : 0f) +
        (Get(EquipSlot.Legs)  is not null ? 0.10f : 0f) +
        (Get(EquipSlot.Feet)  is not null ? 0.05f : 0f) +
        (Get(EquipSlot.Gloves) is not null ? 0.05f : 0f) +
        (HasAccessory("aegis_pendant") ? 0.10f : 0f);
}
