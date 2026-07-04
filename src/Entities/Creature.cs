using System;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// The planet's bestiary. Three broad habitats, each with its own movement physics:
///   • Cave dwellers (Grub, Skitterer, MagmaSlug) — walkers: gravity toward the core,
///     tangent locomotion, tile collision.
///   • Tunnellers/explorers (Borer digs its own tunnels through real tiles via Planet.Mine —
///     debris crumbles into the cell sim and collapse checks run, so its tunnels obey the
///     same physics as player mining; CaveEye floats through tunnels that already exist,
///     steering along open space and never phasing through walls).
///   • Surface fauna (Grazer, Hopper — passive walkers that flee) and sky fauna (SkyMoth
///     passive, SkyStinger dive-bomber) — flyers hold an altitude band, collide with
///     terrain, and climb over mountains rather than clipping them.
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
        }
    }

    public bool IsSkyKind => Kind is CreatureKind.SkyMoth or CreatureKind.SkyStinger;
    public bool IsSurfaceKind => Kind is CreatureKind.Grazer or CreatureKind.Hopper;
    public bool IsCaveKind => !IsSkyKind && !IsSurfaceKind;

    public void Update(float dt, Planet planet, Physics physics, Cells cells, Player player)
    {
        // Status effect tick: burn drains HP per-second; freeze halves move speed.
        if (BurnSeconds > 0f)
        {
            Health -= 3f * dt;
            BurnSeconds -= dt;
        }
        if (FreezeSeconds > 0f) FreezeSeconds -= dt;
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
        }

        Position += Velocity * dt;
        ResolveTileCollision(planet);

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
        }
    }

    // ---------------------------------------------------------------- cave walkers

    private void TickGrub(float dt, Planet planet, Vector2 up, Vector2 right,
        Vector2 toPlayer, float dist, float speedMul)
    {
        float moveAxis;
        if (dist < 140f)
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
        if (dist < 130f)
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
        if (dist < 90f)
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
            var vN = Vector2.Dot(Velocity, up) - 320f * dt;
            Velocity = right * vT + up * vN;
        }
        else
        {
            Velocity -= up * (320f * dt); // mid-hop: ballistic
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
        if (dist < 110f && dist > 0.01f)
        {
            _digDir = toPlayer / dist; // smells the dwarf through rock
        }
        else if (_retarget <= 0f)
        {
            _retarget = 4f + (float)Random.Shared.NextDouble() * 4f;
            // Bias tunnels tangentially (gallery-like) with a random radial tilt.
            var baseDir = right * (Random.Shared.Next(2) == 0 ? 1f : -1f);
            var tilt = ((float)Random.Shared.NextDouble() - 0.5f) * 1.6f;
            _digDir = Rotate(baseDir, tilt);
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
            var vN = Vector2.Dot(Velocity, up) - 320f * dt;
            Velocity = right * vT + up * vN;
        }

        // Chew the tile ahead of the snout.
        _cd -= dt;
        if (_cd <= 0f)
        {
            var probe = Position + _digDir * (Radius + 4f);
            var (tx, ty) = planet.WorldToTile(probe);
            var k = planet.Get(tx, ty);
            if (Tiles.IsSolid(k))
            {
                _cd = 0.22f;
                if (Tiles.Hardness(k) >= 90)
                {
                    _retarget = 0f; // core / support beam — bounce off, pick a new heading
                }
                else if (planet.Mine(tx, ty, 2) is { } broken)
                {
                    physics.MarkDirty(tx, ty);
                    cells.SpawnDustInTile(tx, ty, broken);
                }
            }
        }
    }

    /// <summary>CaveEye: hovers through open tunnels. Never digs — it steers by probing ahead
    /// and turning toward open space, so it genuinely explores whatever tunnel network exists
    /// (natural caves, player shafts, borer galleries).</summary>
    private void TickCaveEye(float dt, Planet planet, Vector2 toPlayer, float dist, float speedMul)
    {
        // Idle drift wobbles the heading; a nearby dwarf pulls it.
        _heading += ((float)Random.Shared.NextDouble() - 0.5f) * 2.4f * dt;
        var speed = MoveSpeed;
        if (dist < 110f && dist > 0.01f)
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
        vN -= 320f * dt;
        if (MathF.Abs(moveAxis) > 0.1f && IsGrounded(planet, up)
            && planet.IsSolidAt(Position + right * (MathF.Sign(moveAxis) * (Radius + 3f))))
        {
            vN = 120f;
        }
        Velocity = right * vT + up * vN;
    }

    private bool IsGrounded(Planet planet, Vector2 up) =>
        planet.IsSolidAt(Position - up * (Radius + 1.5f));

    private bool AnySolidNear(Planet planet)
    {
        var (tx, ty) = planet.WorldToTile(Position);
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                if (Tiles.IsSolid(planet.Get(tx + dx, ty + dy)))
                    return true;
        return false;
    }

    private void ResolveTileCollision(Planet planet)
    {
        var (tx, ty) = planet.WorldToTile(Position);
        for (var iter = 0; iter < 3; iter++)
        {
            var pushed = false;
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var x = tx + dx; var y = ty + dy;
                    if (!Tiles.IsSolid(planet.Get(x, y))) continue;
                    var rect = new Rectangle(x * Planet.TileSize, y * Planet.TileSize, Planet.TileSize, Planet.TileSize);
                    var closest = new Vector2(
                        Math.Clamp(Position.X, rect.X, rect.X + rect.Width),
                        Math.Clamp(Position.Y, rect.Y, rect.Y + rect.Height));
                    var diff = Position - closest;
                    var dist = diff.Length();
                    if (dist < Radius && dist > 0.001f)
                    {
                        var n = diff / dist;
                        Position += n * (Radius - dist + 0.05f);
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
