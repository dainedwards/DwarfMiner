using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// A dead creature left lying where it fell. Corpses tumble to the ground under gravity,
/// rest there for a while, and can be harvested for materials by walking over them (same
/// sweep-up feel as dust collection). Unharvested corpses decay after <see cref="DecayTime"/>
/// seconds, blinking for the last few so the player can see the window closing.
/// </summary>
public sealed class Corpse
{
    public const float DecayTime = 45f;
    /// <summary>Seconds of remaining life below which the corpse blinks in the renderer.</summary>
    public const float BlinkTime = 6f;

    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius;
    public readonly CreatureKind Kind;
    public float Life = DecayTime;
    public bool Harvested;
    /// <summary>Ragdoll orientation — the corpse slab tumbles with real spin (same
    /// mini-solver as RigidBodies, minus the payload/stamping) and eases flat along the
    /// local tangent once it comes to rest.</summary>
    public float Angle;
    public float Spin;
    /// <summary>Which shoulder the body fell onto — offsets the creature-sprite pose ±90°
    /// from the tumble angle so a settled corpse lies on its side, not standing.</summary>
    public readonly int PoseSide = Random.Shared.Next(2) == 0 ? -1 : 1;
    /// <summary>Display template: a dead clone of the creature this was, drawn frozen and
    /// greyed by the renderer's corpse path so the body on the ground IS the creature.</summary>
    public Creature? Body;
    /// <summary>Harvest channel: standing over the corpse carves it up over
    /// <see cref="HarvestTime"/> seconds (bigger animals take longer), the body visibly
    /// disintegrating as the progress runs. Progress holds if the player steps away.</summary>
    public float HarvestProgress;
    public float HarvestTime => MathHelper.Clamp(1.0f + Radius * 0.25f, 1.3f, 4.5f);
    public float Dissolve => MathHelper.Clamp(HarvestProgress / HarvestTime, 0f, 1f);
    private bool _resting;
    private float _sleepTimer;
    // Same lesson as RigidBodies: a resting body alternates contact/free frames (the
    // push-out separates it, gravity re-engages), so sleep gates on touched-recently.
    private float _sinceContact = 10f;

    public bool Expired => Harvested || Life <= 0f;

    public Corpse(Vector2 pos, CreatureKind kind, float radius)
    {
        Position = pos;
        Kind = kind;
        Radius = radius;
    }

    /// <summary>Blast/impact kick: shove the body and set it tumbling again.</summary>
    public void Kick(Vector2 impulse)
    {
        Velocity += impulse;
        Spin += ((float)Random.Shared.NextDouble() - 0.5f) * 6f
            * MathF.Min(1.5f, impulse.Length() / 150f);
        _resting = false;
        _sleepTimer = 0f;
    }

    public void Update(float dt, Planet planet)
    {
        Life -= dt;

        if (_resting)
        {
            // Stay down while the floor holds; if it's mined/blasted out, resume falling.
            var g = planet.GravityAt(Position);
            if (planet.IsSolidAt(Position + g * (Radius * 0.9f)))
            {
                EaseFlat(dt, planet);
                return;
            }
            _resting = false;
        }

        // De-embed: a creature that died inside rock (a flyer splatted against a wall, a
        // digger shot mid-burrow) surfaces instead of sinking out of sight — "aliens leave
        // no corpse" was usually this, the body quietly buried in the terrain.
        if (planet.IsSolidAt(Position))
        {
            Position += planet.UpAt(Position) * (Planet.TileSize * 0.75f);
            Velocity = Vector2.Zero;
            Spin *= 0.5f;
        }

        Velocity += planet.GravityAt(Position) * 300f * dt;
        var speed = Velocity.Length();
        if (speed > 300f) Velocity *= 300f / speed;

        // Substep so a blast-flung corpse can't tunnel a thin floor in one integration and
        // end up falling through the world (the other way "no corpse" happened).
        var steps = Math.Clamp((int)MathF.Ceiling(speed * dt / 2.5f), 1, 4);
        var sub = dt / steps;
        var contacted = false;
        // Below a crawl the rotational coupling only PUMPS motion: the sample ring turns
        // with the body, so each frame's contacts land somewhere new and their impulse
        // torques rock it forever (measured ±2 rad/s standing waves). A slow corpse is a
        // particle: impulses hit velocity only, and spin chokes to zero.
        var crawling = _sinceContact < 0.12f && Velocity.LengthSquared() < 30f * 30f;
        for (var s = 0; s < steps; s++)
        {
            Position += Velocity * sub;
            Angle += Spin * sub;
            contacted |= ResolveContacts(planet, crawling);
        }
        if (crawling && contacted)
        {
            Spin *= 0.7f;
            if (MathF.Abs(Spin) < 0.5f) Spin = 0f;
        }
        if (contacted)
        {
            Velocity *= 0.94f;
            Spin *= 0.90f;
        }
        _sinceContact = contacted ? 0f : _sinceContact + dt;

        // The slow window must clear the free-fall speed regained between contact frames
        // (~15 px/s over the 0.1s hop the push-out causes) or the sleep timer flickers.
        var slow = Velocity.LengthSquared() < 24f * 24f && MathF.Abs(Spin) < 0.6f;
        _sleepTimer = _sinceContact < 0.12f && slow ? _sleepTimer + dt : 0f;
        if (_sleepTimer >= 0.4f)
        {
            _resting = true;
            Velocity = Vector2.Zero;
            Spin = 0f;
        }
    }

    /// <summary>One contact pass over the sample ring — the RigidBodies recipe scaled down
    /// to one circle: inelastic below a bounce threshold, friction spins the slab as it
    /// scrapes, one averaged push-out (never per-contact — that stacks and pops the body
    /// airborne). Called per substep.</summary>
    private bool ResolveContacts(Planet planet, bool crawling)
    {
        var push = Vector2.Zero;
        var contacted = false;
        var invI = 2f / MathF.Max(1f, Radius * Radius);   // unit mass, disc inertia
        var cr = Radius * 0.75f;
        for (var i = 0; i < 6; i++)
        {
            var a = Angle + i * (MathF.Tau / 6f);
            var r = new Vector2(MathF.Cos(a), MathF.Sin(a)) * cr;
            var p = Position + r;
            var (tx, ty) = planet.WorldToTile(p);
            var k = planet.Get(tx, ty);
            if (!Tiles.IsSolid(k) || Tiles.IsPassable(k) || Tiles.IsFlora(k)) continue;
            var n = ContactNormal(planet, p);
            var vp = Velocity + new Vector2(-Spin * r.Y, Spin * r.X);
            var vn = Vector2.Dot(vp, n);
            if (vn < 0f)
            {
                if (crawling)
                {
                    Velocity -= n * vn;                       // inelastic, particle-style
                    Velocity *= 0.9f;                         // scrape friction
                }
                else
                {
                    var rn = r.X * n.Y - r.Y * n.X;
                    var invEff = 1f + rn * rn * invI;
                    var e = vn < -90f ? 0.25f : 0f;
                    var j = -(1f + e) * vn / invEff;
                    Velocity += n * j;
                    Spin += rn * j * invI;
                    var t = new Vector2(-n.Y, n.X);
                    vp = Velocity + new Vector2(-Spin * r.Y, Spin * r.X);
                    var vt = Vector2.Dot(vp, t);
                    var rt = r.X * t.Y - r.Y * t.X;
                    var jt = MathHelper.Clamp(-vt / (1f + rt * rt * invI), -0.5f * j, 0.5f * j);
                    Velocity += t * jt;
                    Spin += rt * jt * invI;
                }
            }
            push += n;
            contacted = true;
        }
        if (push.LengthSquared() > 0.001f)
            Position += Vector2.Normalize(push) * 0.35f;
        return contacted;
    }

    /// <summary>Once at rest, ease the slab onto the nearest lie-flat orientation (tangent
    /// mod π) — the contact solve alone can leave a 2:1 slab propped on its nose, which
    /// reads as a standing corpse.</summary>
    private void EaseFlat(float dt, Planet planet)
    {
        var up = planet.UpAt(Position);
        var flat = MathF.Atan2(up.X, -up.Y);
        var d = MathHelper.WrapAngle(flat - Angle);
        if (d > MathF.PI / 2f) d -= MathF.PI;
        if (d < -MathF.PI / 2f) d += MathF.PI;
        Angle += d * MathF.Min(1f, 6f * dt);
    }

    /// <summary>Open-direction probe around a penetrating point (RigidBodies' gradient
    /// trick); radial up deep inside rock.</summary>
    private static Vector2 ContactNormal(Planet planet, Vector2 wp)
    {
        var n = Vector2.Zero;
        const float step = Planet.TileSize;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var off = new Vector2(dx * step, dy * step);
                var (tx, ty) = planet.WorldToTile(wp + off);
                var k = planet.Get(tx, ty);
                if (!Tiles.IsSolid(k) || Tiles.IsPassable(k) || Tiles.IsFlora(k)) n += off;
            }
        return n.LengthSquared() > 0.001f ? Vector2.Normalize(n) : planet.UpAt(wp);
    }

    /// <summary>What harvesting this creature's corpse pays out. New organics (meat / hide /
    /// chitin) feed the feast + chitin-armour recipes; a few kinds also carry a mineral —
    /// the magma slug's coal hide, the cave eye's crystal lens, the delver's iron pick-head.</summary>
    public static (string id, int count)[] DropsFor(CreatureKind kind) => kind switch
    {
        CreatureKind.Grub         => new[] { ("meat", 1) },
        CreatureKind.Skitterer    => new[] { ("chitin", 1) },
        CreatureKind.Borer        => new[] { ("chitin", 2) },
        CreatureKind.CaveEye      => new[] { ("meat", 1), ("crystal", 1) },
        CreatureKind.MagmaSlug    => new[] { ("meat", 1), ("coal", 2) },
        CreatureKind.Grazer       => new[] { ("meat", 2), ("hide", 1) },
        CreatureKind.Hopper       => new[] { ("meat", 1) },
        CreatureKind.SkyMoth      => new[] { ("hide", 1) },
        CreatureKind.SkyStinger   => new[] { ("chitin", 1), ("meat", 1) },
        CreatureKind.HornedDelver => new[] { ("hide", 1), ("iron", 1) },
        CreatureKind.Centipede    => new[] { ("chitin", 3) },
        CreatureKind.MoleBeast    => new[] { ("meat", 2), ("hide", 2) },
        CreatureKind.SporeBat     => new[] { ("meat", 1) },
        CreatureKind.CrystalCrawler => new[] { ("crystal", 2), ("chitin", 1) },
        // The wraith is the renewable voidstone source — Rift-gated, but farmable.
        CreatureKind.VoidWraith   => new[] { ("voidstone", 1) },
        CreatureKind.CaveSlime    => new[] { ("meat", 1) },
        CreatureKind.Slimelet     => new[] { ("meat", 1) },
        CreatureKind.AcidSpitter  => new[] { ("meat", 2) },
        CreatureKind.BomberBeetle => new[] { ("chitin", 1) },   // unused in practice — bombers self-destruct
        CreatureKind.SnapperVine  => new[] { ("meat", 1), ("hide", 1) },
        // The mimic's hoard — the payoff for calling the bluff instead of walking past.
        CreatureKind.RockMimic    => new[] { ("gold", 3), ("crystal", 1) },
        // Biome fauna: hunting the local herd pays in that world's flavour — wool-hide on
        // the ice, coal off the skink's back, iron scrap from the rustback's shell.
        CreatureKind.SnowLoper    => new[] { ("meat", 2), ("hide", 2) },
        CreatureKind.CinderSkink  => new[] { ("meat", 1), ("coal", 1) },
        CreatureKind.RustBack     => new[] { ("meat", 1), ("iron", 1) },
        CreatureKind.TidePuddler  => new[] { ("meat", 1) },
        CreatureKind.AcidStrider  => new[] { ("meat", 1), ("chitin", 1) },
        CreatureKind.PrismSnail   => new[] { ("meat", 1), ("crystal", 1) },
        CreatureKind.NullMoth     => new[] { ("hide", 1) },
        // A citizen leaves nothing worth taking — that kill was on you.
        CreatureKind.Civilian     => System.Array.Empty<(string, int)>(),
        // Warren guards carry their kit: scaled hide, and the odd looted nugget.
        CreatureKind.Lizardman    => new[] { ("hide", 1), ("gold", 1) },
        // A downed peacekeeper's alloy sidearm strips to scrap iron.
        CreatureKind.Peacekeeper  => new[] { ("iron", 1) },
        // A crashed patrol saucer is a small salvage yard.
        CreatureKind.Saucer       => new[] { ("iron", 2), ("crystal", 1) },
        // Bandits carry their kit: the gunmen drop scrap and powder-coal; the raider's
        // wrecked pack and the pyro's tank both drain a little fuel.
        CreatureKind.Marauder     => new[] { ("iron", 1), ("coal", 1) },
        CreatureKind.Raider       => new[] { ("iron", 1), ("fuel", 1) },
        CreatureKind.Pyro         => new[] { ("coal", 2), ("fuel", 1) },
        // A whale carcass is a feast; a cracked crab is prime chitin.
        CreatureKind.AlienWhale   => new[] { ("meat", 6), ("hide", 2) },
        CreatureKind.AlienCrab    => new[] { ("chitin", 2), ("meat", 1) },
        // Sea monsters: prime meat and tough hide off the shark, more meat off the deep
        // gulper, a bit of chitin off the spitter's reef-plated shell.
        CreatureKind.AlienShark   => new[] { ("meat", 3), ("hide", 2) },
        CreatureKind.Gulper       => new[] { ("meat", 4), ("hide", 1) },
        CreatureKind.Brinespitter => new[] { ("chitin", 1), ("meat", 2) },
        // Deep-cave horrors: quill chitin off the flyer, a shard of the wisp's warped core,
        // a heap of spine-chitin off the beetle.
        CreatureKind.Quillwing    => new[] { ("chitin", 1), ("meat", 1) },
        CreatureKind.Warpwisp     => new[] { ("crystal", 1) },
        CreatureKind.Thornback    => new[] { ("chitin", 2) },
        _                         => new[] { ("meat", 1) },
    };

    /// <summary>Desaturated body colour for the corpse sprite — recognisably the creature,
    /// visibly lifeless. Matches the living palette in the creature renderer.</summary>
    public static Color BodyColor(CreatureKind kind) => kind switch
    {
        CreatureKind.Grub         => new Color(130, 55, 68),
        CreatureKind.Skitterer    => new Color(52, 44, 62),
        CreatureKind.Borer        => new Color(112, 82, 58),
        CreatureKind.CaveEye      => new Color(170, 168, 165),
        CreatureKind.MagmaSlug    => new Color(62, 42, 45),
        CreatureKind.Grazer       => new Color(96, 138, 124),
        CreatureKind.Hopper       => new Color(120, 130, 90),
        CreatureKind.SkyMoth      => new Color(180, 180, 190),
        CreatureKind.SkyStinger   => new Color(140, 120, 70),
        CreatureKind.HornedDelver => new Color(110, 90, 80),
        CreatureKind.Centipede    => new Color(100, 78, 60),
        CreatureKind.MoleBeast    => new Color(105, 85, 95),
        CreatureKind.SporeBat     => new Color(120, 140, 105),
        CreatureKind.CrystalCrawler => new Color(85, 80, 105),
        CreatureKind.VoidWraith   => new Color(70, 45, 95),
        CreatureKind.CaveSlime    => new Color(70, 140, 118),
        CreatureKind.Slimelet     => new Color(95, 160, 132),
        CreatureKind.AcidSpitter  => new Color(82, 100, 56),
        CreatureKind.BomberBeetle => new Color(48, 42, 38),
        CreatureKind.SnapperVine  => new Color(72, 100, 58),
        CreatureKind.RockMimic    => new Color(92, 88, 84),
        CreatureKind.SnowLoper    => new Color(196, 206, 216),
        CreatureKind.CinderSkink  => new Color(58, 46, 44),
        CreatureKind.RustBack     => new Color(120, 78, 52),
        CreatureKind.TidePuddler  => new Color(72, 130, 145),
        CreatureKind.AcidStrider  => new Color(100, 112, 58),
        CreatureKind.PrismSnail   => new Color(120, 100, 140),
        CreatureKind.NullMoth     => new Color(40, 32, 56),
        CreatureKind.Civilian     => new Color(140, 150, 138),
        CreatureKind.Lizardman    => new Color(58, 92, 52),
        CreatureKind.Peacekeeper  => new Color(52, 66, 90),
        CreatureKind.Saucer       => new Color(96, 106, 124),
        CreatureKind.AlienWhale   => new Color(48, 68, 96),
        CreatureKind.AlienCrab    => new Color(118, 66, 58),
        _                         => new Color(110, 90, 90),
    };
}
