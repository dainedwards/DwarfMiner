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

    public bool Expired => Harvested || Life <= 0f;

    public Corpse(Vector2 pos, CreatureKind kind, float radius)
    {
        Position = pos;
        Kind = kind;
        Radius = radius;
    }

    public void Update(float dt, Planet planet)
    {
        Life -= dt;
        // Tumble to the floor: gravity toward the planet centre, killed on ground contact so
        // the body comes to rest instead of jittering. No walking, no AI — it's dead.
        Velocity += planet.GravityAt(Position) * 300f * dt;
        Velocity *= MathF.Max(0f, 1f - 1.5f * dt);
        var next = Position + Velocity * dt;
        if (planet.IsSolidAt(next))
            Velocity = Vector2.Zero;
        else
            Position = next;
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
