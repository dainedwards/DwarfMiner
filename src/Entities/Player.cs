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
    /// <summary>Mothership-foundry upgrade (meta gear, re-applied on every entry like the
    /// jetpack): +50% air ceiling, multiplicative with the craftable air tank.</summary>
    public bool HasO2Recycler;

    public float EffectiveMaxOxygen =>
        BaseMaxOxygen * (HasAirTank ? 2f : 1f) * (HasO2Recycler ? 1.5f : 1f);

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
    public bool HasLantern;
    public bool HasArmor;
    public bool HasCoreDrill;
    public bool HasPistol;
    public bool HasMachineGun;
    public bool HasLaser;
    public bool HasLaserCannon;
    public bool HasRocketLauncher;

    /// <summary>Mothership-foundry upgrade (not craftable in-run, not in the run save —
    /// re-applied from MetaSave on every planet entry). Hold jump while airborne to fly on a
    /// charge that refills on the ground.</summary>
    public bool HasJetpack;
    public float JetCharge = JetChargeMax;
    public const float JetChargeMax = 2.6f;   // seconds of burn
    private const float JetRiseSpeed = 110f;  // target upward speed under thrust
    private const float JetAccel = 420f;

    public float MineRange = 22f;          // pixels — dwarves have short reach
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
    /// continuous stream; hammer is slow but lands a heavy blow per swing. Fly mode keeps
    /// the drill cadence so dev movement isn't gated by swing rate.</summary>
    public float MineCooldownFor(MiningTool tool)
    {
        if (FlyMode) return 0.04f;
        return tool switch
        {
            MiningTool.Drill  => 0.04f,
            MiningTool.Hammer => 0.30f,
            _                 => 0.10f,
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

    /// <summary>Damage-take multiplier applied at the entity damage call sites. 1.0 = normal,
    /// 0.6 = 40% reduction (iron plate armor).</summary>
    public float DamageTakenMultiplier => HasArmor ? 0.6f : 1.0f;

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

        // Snapshot grounded state for the "ground-snap" surface-stickiness check at the end
        // of Update. World-gen's ±1.5-tile surface elevation noise means adjacent columns
        // can sit on different rings — without snap, walking over a 1-tile drop produces a
        // multi-frame visible fall before gravity accumulates enough to pull the player
        // into collision range of the lower tile.
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

        // Jetpack: holding jump while airborne thrusts toward a steady rise until the charge
        // runs dry; the charge refills on the ground. The jump edge itself is still the
        // ordinary jump (buffer/coyote below), so a hop flows into a burn naturally. Gated
        // off ladders so climbing doesn't fight the thrust.
        if (HasJetpack)
        {
            if (Grounded) JetCharge = JetChargeMax;
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

        // Ground-snap: if we were grounded before this Update, are walking, and aren't ascending
        // from a jump, but are now floating in air, probe up to one tile below the feet for the
        // next solid surface. If found, snap onto it and re-resolve collisions to align cleanly.
        // This handles surface elevation noise (1-tile drops between adjacent columns) without
        // letting the player visibly fall multiple pixels before gravity accumulates. Cliffs
        // taller than one tile still drop normally because the probe finds nothing.
        if (!Grounded && wasGrounded && moveAxis != 0 && Vector2.Dot(Velocity, up) < 5f)
        {
            const float snapMaxBelow = Planet.TileSize + 1f;   // ≈ 1 tile below the feet
            for (var d = 1.5f; d <= snapMaxBelow; d += 1.0f)
            {
                if (ProbeSolid(planet, feetCentre - up * d))
                {
                    Position -= up * d;
                    ResolveCollision(planet);
                    Grounded = true;
                    break;
                }
            }
        }

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
                if (x < 0 || x >= Planet.RingCount) continue;
                var nRing = Planet.TilesAt(x);
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
                    var halfX = MathHelper.TwoPi * ringRadius / Planet.TilesAt(x) * 0.5f;
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

    /// <summary>Try to mine the tile under the cursor with a specific tool. Pickaxe is the
    /// default; drill / hammer change cooldown + power profile. PlanetCore needs the hammer,
    /// the Core needs the core drill. Tier IV gets a 2× power bonus on Obsidian; hammer
    /// gets a flat power floor + an effective-hardness override so it bites bedrock at all.</summary>
    public TileKind? TryMine(Planet planet, Physics physics, Vector2 worldCursor, MiningTool tool = MiningTool.Pickaxe)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;
        var (x, y) = planet.WorldToTile(worldCursor);
        var k = planet.Get(x, y);
        if (k == TileKind.Sky) return null;
        if (!CanBreak(k, tool))
        {
            MineCooldown = MineCooldownFor(tool);   // still spend a swing for feedback
            return null;
        }

        // Tool-aware power. Drill matches pickaxe power but mines fast; hammer hits hard but
        // slow; hammer is the only tool that can punch PlanetCore (with the hardness override).
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

        var broken = planet.Mine(x, y, power, effectiveHardness);
        MineCooldown = MineCooldownFor(tool);
        if (broken is { } bk)
        {
            physics.MarkDirty(x, y);
            // Drop is no longer credited instantly here — Game1 spawns a dust pile of `bk` and the
            // player collects it by walking through (Cells.CollectInRadius). Mining = create dust.
            return bk;
        }
        return null;
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
/// and which tile classes are breakable. Selected via the active toolbelt slot.</summary>
public enum MiningTool { Pickaxe, Drill, Hammer }

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
        "drill" or "hammer" or "cannon" or "core_drill" or
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
