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

    /// <summary>Pickaxe tier 1..4. Drives base mining power and reach. Replaces the older
    /// <c>PickaxePower</c> int — kept as a tier so future augments can stack on top of a tier
    /// rather than being confused with raw power. Effective stats live in the
    /// <c>Effective…</c> getters below; never read this field for gameplay logic, always go
    /// through the getter so future augment modifiers slot in cleanly.</summary>
    public int PickaxeTier = 1;

    /// <summary>Tools the player has crafted. Each is a one-time flag; crafting again is a
    /// no-op. Augments (future) will live in a separate flags struct beside these.</summary>
    public bool HasDrill;
    public bool HasHammer;
    public bool HasMiningLaser;
    public bool HasLantern;
    public bool HasArmor;
    public bool HasCoreDrill;
    public bool HasPistol;
    public bool HasMachineGun;
    public bool HasLaser;
    public bool HasLaserCannon;
    public bool HasRocketLauncher;

    /// <summary>Mothership-foundry upgrades (not craftable in-run, not in the run save —
    /// re-applied from MetaSave on every planet entry). Jetpack: hold jump while airborne to
    /// fly on a charge that refills on the ground; tier II doubles the charge and climbs
    /// harder. Magnet: loose ore leaps to the pack from much farther.</summary>
    public bool HasJetpack;
    public bool JetTier2;
    public bool JetTier3;
    public bool HasMagnet;
    public bool MagnetTier2;
    public float JetCharge = JetChargeMax;
    public const float JetChargeMax = 2.6f;   // seconds of burn (tier I)
    public float JetChargeCap => JetChargeMax * (JetTier3 ? 3f : JetTier2 ? 2f : 1f);
    private float JetRiseSpeed => JetTier3 ? 190f : JetTier2 ? 150f : 110f;
    private const float JetAccel = 420f;

    /// <summary>How far loose material leaps to the pack — foundry magnet tiers.</summary>
    public float PickupReach => MagnetTier2 ? 30f : HasMagnet ? 16f : 4f;

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

    /// <summary>Last placed Beacon tile, in world coords. Pressing T teleports to it.</summary>
    public Vector2? BeaconWorld;

    /// <summary>9-slot equipment bar. Number keys 1..9 select a slot; LMB triggers the
    /// selected slot's primary action (mine, fire, place, throw, …). Crafted equipment
    /// auto-equips into the first empty slot via <see cref="Toolbelt.AutoEquip"/>; if all
    /// slots are full it stays in inventory and the player drags it onto a slot manually.</summary>
    public readonly Toolbelt Toolbelt = new();

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
            // Future: + augment bonuses (e.g. ruby tip → +0.x chain power).
            return PickaxeTier;
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
        return tool switch
        {
            MiningTool.Drill       => 0.04f,
            MiningTool.Hammer      => 0.30f,
            MiningTool.MiningLaser => 0.03f,
            _                      => 0.10f,
        };
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

    /// <summary>Damage-take multiplier applied at the entity damage call sites. 1.0 = normal,
    /// 0.6 = 40% reduction (iron plate armor); foundry plating stacks multiplicatively.</summary>
    public float DamageTakenMultiplier => (HasArmor ? 0.6f : 1.0f) * (HasPlating ? 0.7f : 1.0f);

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

    public Player(Vector2 pos) { Position = pos; }

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

        // Edge-detect jump press from the held signal. Tracked across fly-mode frames too so
        // mode toggles don't accidentally trigger a buffered jump.
        var jumpEdge = jumpHeld && !_jumpHeldPrev;
        _jumpHeldPrev = jumpHeld;

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
        if (!Grounded && !onLadder) vNormal -= grav * dt;
        // On a ladder: vertical input (W/up = +1, S/down = -1) directly drives motion at a
        // climb rate; gravity is fully suppressed so the player stays put when no input is
        // given. Velocity *along* the surface (vTangent) still works, so you can climb +
        // step off sideways onto a platform.
        if (onLadder)
        {
            const float climbSpeed = 70f;
            vNormal = MoveToward(vNormal, verticalAxis * climbSpeed, 480f * dt);
        }

        if (EmpTimer > 0f) EmpTimer -= dt;

        // Jetpack: holding jump while airborne thrusts toward a steady rise until the charge
        // runs dry; the charge refills on the ground. The jump edge itself is still the
        // ordinary jump (buffer/coyote below), so a hop flows into a burn naturally. Gated
        // off ladders so climbing doesn't fight the thrust, and dead while EMP'd.
        if (HasJetpack && EmpTimer <= 0f)
        {
            if (Grounded) JetCharge = JetChargeCap;
            else if (jumpHeld && !jumpEdge && !onLadder && JetCharge > 0f)
            {
                vNormal = MoveToward(vNormal, JetRiseSpeed, JetAccel * dt);
                JetCharge -= dt;
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

    /// <summary>Place a block from inventory into a sky tile under the cursor. Stone first, then dirt.</summary>
    public TileKind? TryPlace(Planet planet, Physics physics, Vector2 worldCursor)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;
        var (x, y) = planet.WorldToTile(worldCursor);
        if (planet.Get(x, y) != TileKind.Sky) return null;

        // Don't seal the dwarf inside a tile — keep at least a body's distance.
        var tilePos = planet.TileToWorld(x, y);
        if ((tilePos - Position).Length() < Radius + Planet.TileSize * 0.55f) return null;

        // Priority: plain stone (most abundant) → richer stone variants (granite/basalt/etc) →
        // dirt. Each variant places its own tile kind so a granite stockpile builds granite
        // walls, not generic stone — preserves the resource's identity through placement.
        TileKind placed;
        if      (Inventory.TryConsume("stone", 1))      placed = TileKind.Stone;
        else if (Inventory.TryConsume("granite", 1))    placed = TileKind.Granite;
        else if (Inventory.TryConsume("basalt", 1))     placed = TileKind.Basalt;
        else if (Inventory.TryConsume("moss_stone", 1)) placed = TileKind.MossStone;
        else if (Inventory.TryConsume("obsidian", 1))   placed = TileKind.Obsidian;
        else if (Inventory.TryConsume("gravel", 1))     placed = TileKind.Gravel;
        else if (Inventory.TryConsume("dirt", 1))       placed = TileKind.Dirt;
        else return null;

        planet.Set(x, y, placed);
        physics.MarkDirty(x, y);
        MineCooldown = 0.10f;
        return placed;
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
        var aimed = planet.WorldToTile(planet.TileToWorld(x, y)); // wrapped key for identity
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
    public float SwingToolLen => (PickaxeTier >= 3 ? 1.2f : 1.0f) * 4.5f;
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
    /// is debited 1. Returns the placed tile kind if it landed in a sky tile and stock was
    /// available; null otherwise.</summary>
    public TileKind? TryPlaceBuildId(Planet planet, Physics physics, Vector2 worldCursor, string invId)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;
        var (x, y) = planet.WorldToTile(worldCursor);
        if (planet.Get(x, y) != TileKind.Sky) return null;

        var placedKind = BuildIdToTile(invId);
        if (placedKind == TileKind.Sky) return null;   // unrecognised build id

        // Don't seal the dwarf inside a tile — keep at least a body's distance for
        // *blocking* tiles. Passable build items (ladder/glowshroom/beacon) skip this check
        // so you can drop a torch right next to your feet.
        var passable = Tiles.IsPassable(placedKind);
        if (!passable)
        {
            var tilePos = planet.TileToWorld(x, y);
            if ((tilePos - Position).Length() < Radius + Planet.TileSize * 0.55f) return null;
        }

        if (!Inventory.TryConsume(invId, 1)) return null;

        planet.Set(x, y, placedKind);
        physics.MarkDirty(x, y);
        if (placedKind == TileKind.Beacon) BeaconWorld = planet.TileToWorld(x, y);
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
/// 13-slot equipment belt. Crafted equipment auto-equips into the first empty slot via
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
    // 13 slots: 3 intrinsic tools + room for the full 10-weapon armoury god mode loans out.
    // Number keys only reach the first 9; the rest are selected by wheel, Q/E weapon cycle,
    // or clicking the HUD slot.
    public const int SlotCount = 13;
    public readonly string?[] Slots = new string?[SlotCount];
    public int Selected;

    public Toolbelt()
    {
        Slots[0] = "pickaxe";
        Slots[1] = "bullets";
        Slots[2] = "blocks";
    }

    /// <summary>Place <paramref name="id"/> into the first empty slot. No-op if the id is
    /// already on the belt or the belt is full. Returns true on placement.</summary>
    public bool AutoEquip(string id)
    {
        for (var i = 0; i < SlotCount; i++) if (Slots[i] == id) return false;
        for (var i = 0; i < SlotCount; i++)
            if (Slots[i] is null) { Slots[i] = id; return true; }
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
