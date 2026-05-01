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
    public bool Grounded;

    public int PickaxePower = 1;
    public float MineRange = 22f;          // pixels — dwarves have short reach
    public float MoveSpeed = 78f;          // shorter legs
    public float JumpSpeed = 150f;
    public float Gravity = 320f;
    public float MineCooldown;
    public float ShootCooldown;

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
    /// god values below; when off, it falls back to PickaxePower/MineRange so crafted upgrades
    /// (pickaxe_ii etc.) still persist across toggles.</summary>
    public bool FlyMode;
    public const int GodPickaxePower = 50;
    public const float GodMineRange = 200f;
    public int EffectivePickaxePower => FlyMode ? GodPickaxePower : PickaxePower;
    public float EffectiveMineRange  => FlyMode ? GodMineRange    : MineRange;

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
    /// <param name="verticalAxis">-1 down, 0 idle, +1 up (along local up). Only used when <see cref="FlyMode"/> is on.</param>
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

        var targetTangent = moveAxis * MoveSpeed;
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
        if (!Grounded) vNormal -= grav * dt;
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
    /// Two notable behaviours:
    /// <list type="bullet">
    /// <item><b>Inside-tile escape</b>: when the player center is inside a tile's AABB
    ///   (distSq=0), we escape via the *nearest local edge*, not always along planet-up.
    ///   Always-up was wrong: jumping into a ceiling, the player center crosses into the
    ///   ceiling tile and the up-push drives them deeper, clipping through.</item>
    /// <item><b>Step-climb</b>: when the player is grounded and bumps a wall-like obstacle
    ///   (collision normal is mostly tangential) whose top is within ~1 tile, we lift the
    ///   player over it instead of pushing them backward. World-gen's ±1.5-tile surface
    ///   elevation noise creates 1-tile vertical walls between adjacent surface columns;
    ///   without auto-step the player would snag on every bump.</item>
    /// </list>
    /// </summary>
    private void ResolveCollision(Planet planet)
    {
        for (var iter = 0; iter < 4; iter++)
        {
            var (tx, ty) = planet.WorldToTile(Position);
            var pushed = false;
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var x = tx + dx; var y = ty + dy;
                    if (!Tiles.IsSolid(planet.Get(x, y))) continue;

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
        return Tiles.IsSolid(planet.Get(x, y));
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

        TileKind placed;
        if (Inventory.TryConsume("stone", 1)) placed = TileKind.Stone;
        else if (Inventory.TryConsume("dirt", 1)) placed = TileKind.Dirt;
        else return null;

        planet.Set(x, y, placed);
        physics.MarkDirty(x, y);
        MineCooldown = 0.10f;
        return placed;
    }

    /// <summary>Try to mine the tile under the cursor. Returns the tile kind if shattered.</summary>
    public TileKind? TryMine(Planet planet, Physics physics, Vector2 worldCursor)
    {
        if (MineCooldown > 0) return null;
        var d = worldCursor - Position;
        if (d.Length() > EffectiveMineRange) return null;
        var (x, y) = planet.WorldToTile(worldCursor);
        var k = planet.Get(x, y);
        if (k == TileKind.Sky) return null;
        var broken = planet.Mine(x, y, EffectivePickaxePower);
        MineCooldown = 0.10f;
        if (broken is { } bk)
        {
            physics.MarkDirty(x, y);
            var drop = Tiles.Drop(bk);
            if (drop is { } d2) Inventory.Add(d2.id, d2.count);
            return bk;
        }
        return null;
    }

    private static float MoveToward(float v, float target, float maxDelta)
    {
        var d = target - v;
        if (MathF.Abs(d) <= maxDelta) return target;
        return v + MathF.Sign(d) * maxDelta;
    }
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
