using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

public sealed record Recipe(string Id, string Name, IReadOnlyDictionary<string, int> Cost);

public static class Crafting
{
    /// <summary>Active recipe table — the base list plus the current planet's ship-stage
    /// recipes (whose costs vary per planet). Rebuilt by <see cref="SetPlanet"/> at run start;
    /// defaults to the starter planet so SimTest and tools see a full table.</summary>
    public static IReadOnlyList<Recipe> All { get; private set; }

    // Static ctor rather than a field initializer: BuildFor reads Base, which is declared
    // *below* — textual-order field init would hand it a null.
    static Crafting() => All = BuildFor(PlanetDefs.All[0]);

    /// <summary>Rebuild the recipe table for a planet: the ship's nav core demands that
    /// planet's signature deep ore, so leaving always requires a dive to where it lives.</summary>
    public static void SetPlanet(PlanetDef def) => All = BuildFor(def);

    private static IReadOnlyList<Recipe> BuildFor(PlanetDef def)
    {
        var list = new List<Recipe>(Base)
        {
            // ─── Spaceship — the way off this planet ─────────────────────────
            // Pad first (placed at your feet on the surface), then hull → engine → nav in
            // order, each crafted standing at the pad. Nav costs the planet's signature ore.
            new("launch_pad", "Launch pad — build site (craft on the surface)",
                new Dictionary<string, int> { ["stone"] = 8, ["iron"] = 4 }),
            new("ship_hull", "Ship hull — stage 1 (craft at the pad)",
                new Dictionary<string, int> { ["iron"] = 12, ["stone"] = 10 }),
            new("ship_engine", "Ship engine — stage 2 (craft at the pad)",
                new Dictionary<string, int> { ["gold"] = 4, ["coal"] = 10, ["iron"] = 6 }),
            new("ship_nav", "Nav core — stage 3, ready to launch (L at the pad)",
                new Dictionary<string, int> { ["crystal"] = 3, [def.ShipOre] = def.ShipOreCount }),
        };
        return list;
    }

    /// <summary>Master recipe table. Order is the order shown in the in-game crafting menu —
    /// permanent upgrades first, then placeable build items, then ammos, then consumables and
    /// late-game super-weapons. New ids that appear in the cost / output should also be wired
    /// into <see cref="World.Tiles.ResourceOrder"/>, <see cref="World.Tiles.ResourceColor"/>,
    /// and <see cref="World.Tiles.ResourceLabel"/> so they render in the inventory panel.</summary>
    private static readonly IReadOnlyList<Recipe> Base = new List<Recipe>
    {
        // ─── Pickaxe tier ladder ──────────────────────────────────────────────
        // Each tier sets PickaxeTier to its value; recipes are gated by tier so you can't
        // skip — e.g. pickaxe_iv refuses to craft until you already hold pickaxe_iii's level.
        new("pickaxe_ii", "Pickaxe II — Iron (+1 mining)",
            new Dictionary<string, int> { ["iron"] = 5 }),
        new("pickaxe_iii", "Pickaxe III — Platinum (+2 mining, +20% reach)",
            new Dictionary<string, int> { ["platinum"] = 3, ["iron"] = 8 }),
        new("pickaxe_iv", "Pickaxe IV — Diamond (+3 mining, breaks obsidian)",
            new Dictionary<string, int> { ["diamond"] = 3, ["platinum"] = 5 }),

        // ─── Specialty mining tools ───────────────────────────────────────────
        new("drill", "Drill — continuous mining (LMB hold)",
            new Dictionary<string, int> { ["coal"] = 6, ["iron"] = 4 }),
        new("hammer", "Hammer — shatters bedrock",
            new Dictionary<string, int> { ["basalt"] = 4, ["granite"] = 4 }),

        // ─── Geo-scanner ──────────────────────────────────────────────────────
        // Craftable prospecting sense: HUD arrows point from you to the nearest deposits.
        // Each rung widens the detection set (ores → metals → gems → super-rare) and steps
        // sequentially like the pickaxe.
        new("scanner", "Geo-scanner — arrows to the nearest ores",
            new Dictionary<string, int> { ["iron"] = 4, ["coal"] = 4 }),
        new("scanner_ii", "Geo-scanner II — adds gold / silver / platinum",
            new Dictionary<string, int> { ["silver"] = 3, ["iron"] = 6 }),
        new("scanner_iii", "Geo-scanner III — adds ruby / sapphire / emerald",
            new Dictionary<string, int> { ["gold"] = 3, ["crystal"] = 2 }),
        new("scanner_iv", "Geo-scanner IV — adds diamond / crystal / voidstone",
            new Dictionary<string, int> { ["diamond"] = 2, ["platinum"] = 4 }),

        // ─── Light & utility ──────────────────────────────────────────────────
        // Carried-light ladder — the deep dark is near-total without one. Tiers step
        // sequentially like pickaxes; each bumps Player.LightTier. None of them help on the
        // surface or in the dirt band, where daylight already covers you.
        new("torch", "Torch — carried flame; LMB throws it to stick and burn",
            new Dictionary<string, int> { ["coal"] = 1, ["stone"] = 1 }),
        new("lantern", "Lantern — steady oil glow (light II)",
            new Dictionary<string, int> { ["coal"] = 2, ["iron"] = 1 }),
        new("helm_lamp", "Headlamp — hands-free beam (light III, upgradeable)",
            new Dictionary<string, int> { ["silver"] = 2, ["crystal"] = 1, ["iron"] = 2 }),
        new("headlamp_ii", "Headlamp II — brighter bulb",
            new Dictionary<string, int> { ["silver"] = 3, ["crystal"] = 1 }),
        new("headlamp_iii", "Headlamp III — focused lens",
            new Dictionary<string, int> { ["gold"] = 2, ["crystal"] = 2 }),
        new("headlamp_iv", "Headlamp IV — arc filament",
            new Dictionary<string, int> { ["platinum"] = 2, ["ruby"] = 1, ["crystal"] = 2 }),
        new("sun_crystal", "Sunstone charm — bottled daylight (light IV)",
            new Dictionary<string, int> { ["diamond"] = 1, ["ruby"] = 2, ["crystal"] = 2 }),
        new("air_tank", "Air tank — doubles your air supply for deeper dives",
            new Dictionary<string, int> { ["iron"] = 6, ["silver"] = 2 }),
        new("glowshroom", "Glow-shroom torch (placeable green light)",
            new Dictionary<string, int> { ["moss_stone"] = 3 }),
        new("beacon", "Crystal beacon (placeable; press T to recall)",
            new Dictionary<string, int> { ["crystal"] = 1 }),

        // ─── Building ─────────────────────────────────────────────────────────
        // Stone-only so it's always rebuildable from surface mining after a death — the whole
        // point is to withdraw the stash you banked on your last run.
        new("storage_depot", "Storage depot — bank resources (B deposit / N withdraw); survives death",
            new Dictionary<string, int> { ["stone"] = 8 }),
        new("support", "Place support beam",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("reinforced_support", "Reinforced support (anchors a 3×3 area)",
            new Dictionary<string, int> { ["iron"] = 2, ["stone"] = 4 }),
        new("ladder", "Ladder (5×) — climb without carving",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("rail", "Rail (5×) — speed boost where laid",
            new Dictionary<string, int> { ["iron"] = 3 }),
        new("door", "Door (2×) — pops open and shut (E to use)",
            new Dictionary<string, int> { ["stone"] = 3, ["iron"] = 1 }),

        // ─── Base-building blocks (Terraria-lite) ─────────────────────────────
        // Neat crafted tiles for making proper bases — tidier than raw mined rock.
        new("brick", "Brick (4×) — tidy stone masonry",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("plating", "Iron plating (4×) — sturdy metal wall",
            new Dictionary<string, int> { ["iron"] = 1, ["stone"] = 2 }),
        new("glass_block", "Glass block (4×) — a clear window pane",
            new Dictionary<string, int> { ["gravel"] = 3 }),
        new("platform", "Platform (6×) — stand on top, jump up through",
            new Dictionary<string, int> { ["stone"] = 1 }),

        // ─── Combat ───────────────────────────────────────────────────────────
        new("armor", "Iron plate armor — chest (−40% damage taken)",
            new Dictionary<string, int> { ["iron"] = 8 }),
        // Hunter's path to armour: same protection as iron plate, paid in creature parts.
        new("chitin_armor", "Chitin armor — chest (−40% damage taken) — hunted, not mined",
            new Dictionary<string, int> { ["chitin"] = 8, ["hide"] = 3 }),
        // Armor pieces for the rest of the doll (character screen, I key) — helmet and
        // leggings −10% each, boots −5%; a full set on top of a chest plate reaches −65%.
        new("iron_helmet", "Iron helmet (−10% damage taken)",
            new Dictionary<string, int> { ["iron"] = 3 }),
        new("iron_leggings", "Iron leggings (−10% damage taken)",
            new Dictionary<string, int> { ["iron"] = 5 }),
        new("iron_boots", "Iron boots (−5% damage taken)",
            new Dictionary<string, int> { ["iron"] = 2 }),
        new("chitin_helmet", "Chitin helmet (−10% damage taken) — hunted",
            new Dictionary<string, int> { ["chitin"] = 3, ["hide"] = 1 }),
        new("chitin_leggings", "Chitin leggings (−10% damage taken) — hunted",
            new Dictionary<string, int> { ["chitin"] = 4, ["hide"] = 2 }),
        new("chitin_boots", "Chitin boots (−5% damage taken) — hunted",
            new Dictionary<string, int> { ["chitin"] = 2, ["hide"] = 1 }),
        // Gloves — worn on the doll's glove slot; quicker swings with every mining tool.
        new("leather_gloves", "Leather gloves (swing 15% faster, −5% damage)",
            new Dictionary<string, int> { ["hide"] = 3 }),
        new("iron_gauntlets", "Iron gauntlets (swing 30% faster, −5% damage)",
            new Dictionary<string, int> { ["iron"] = 5, ["hide"] = 2 }),

        // ─── Accessories (character screen trinket slots — wear any two) ──────
        new("band_regen", "Band of Regeneration (slowly restores HP)",
            new Dictionary<string, int> { ["moss_stone"] = 4, ["silver"] = 2 }),
        new("magnet_ring", "Magnet Ring (pulls loose ore into the pack)",
            new Dictionary<string, int> { ["iron"] = 4, ["silver"] = 2 }),
        new("miners_charm", "Miner's Charm (+1 mining power)",
            new Dictionary<string, int> { ["gold"] = 3, ["crystal"] = 1 }),
        new("aegis_pendant", "Aegis Pendant (−10% damage taken)",
            new Dictionary<string, int> { ["platinum"] = 2, ["sapphire"] = 2 }),
        new("sentry", "Sentry turret (placeable, auto-fires)",
            new Dictionary<string, int> { ["iron"] = 4, ["coal"] = 3 }),
        // Firearm ladder — each weapon has its own resource profile: the pistol is cheap
        // early iron, the machine gun wants silver for its mechanism, the laser is a
        // crystal-optics build, and the rocket launcher burns gold on guidance.
        new("pistol", "Pistol — solid single shots",
            new Dictionary<string, int> { ["iron"] = 2, ["coal"] = 1 }),
        new("machine_gun", "Machine gun — hold LMB to spray",
            new Dictionary<string, int> { ["iron"] = 6, ["silver"] = 2, ["coal"] = 4 }),
        new("laser", "Laser — piercing energy beam",
            new Dictionary<string, int> { ["crystal"] = 2, ["ruby"] = 1, ["iron"] = 3 }),
        new("laser_cannon", "Laser cannon — lance that drills through walls",
            new Dictionary<string, int> { ["crystal"] = 4, ["diamond"] = 1, ["iron"] = 6 }),
        new("rocket_launcher", "Rocket launcher — fires crafted rockets",
            new Dictionary<string, int> { ["iron"] = 5, ["gold"] = 2, ["coal"] = 3 }),
        // Elemental arms — fire and acid hose the cell sim itself; lightning chains bodies.
        new("flamethrower", "Flamethrower — hose a cone of burning fuel",
            new Dictionary<string, int> { ["iron"] = 4, ["coal"] = 8 }),
        new("acid_spewer", "Acid spewer — sprays rock-melting acid",
            new Dictionary<string, int> { ["iron"] = 4, ["emerald"] = 2 }),
        new("lightning_gun", "Lightning gun — chain arcs between foes",
            new Dictionary<string, int> { ["crystal"] = 3, ["platinum"] = 2, ["gold"] = 2 }),
        new("rocket", "Rocket (3×) — launcher ammo",
            new Dictionary<string, int> { ["iron"] = 1, ["coal"] = 2 }),
        new("cannon", "Cannon (right-click upgrade)",
            new Dictionary<string, int> { ["iron"] = 3, ["coal"] = 5 }),
        // ─── Melee arsenal — swung arms with per-weapon upgrade lines. Each "hone" is
        // craftable three times (rungs II-IV); rung IV is the glowing energy edge. ───────
        new("sword", "Sword — quick swings",
            new Dictionary<string, int> { ["iron"] = 3, ["coal"] = 1 }),
        new("mace", "Mace — heavy knockback",
            new Dictionary<string, int> { ["iron"] = 3, ["stone"] = 3 }),
        new("warhammer", "Warhammer — slow, crushing blows",
            new Dictionary<string, int> { ["iron"] = 4, ["stone"] = 3 }),
        new("shield", "Shield — halves damage while raised (selected)",
            new Dictionary<string, int> { ["iron"] = 3, ["hide"] = 1 }),
        new("great_sword", "Greatsword — two-handed reach and power",
            new Dictionary<string, int> { ["iron"] = 6, ["silver"] = 2 }),
        new("great_mace", "Great mace — two-handed wrecking ball",
            new Dictionary<string, int> { ["iron"] = 6, ["silver"] = 2, ["stone"] = 4 }),
        new("great_hammer", "Great hammer — the slowest, hardest hit",
            new Dictionary<string, int> { ["iron"] = 8, ["silver"] = 3 }),
        new("tower_shield", "Tower shield — the best guard in the game",
            new Dictionary<string, int> { ["iron"] = 6, ["silver"] = 2, ["hide"] = 2 }),
        new("sword_up", "Hone sword (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 2, ["gold"] = 1, ["crystal"] = 1 }),
        new("mace_up", "Hone mace (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 2, ["gold"] = 1, ["crystal"] = 1 }),
        new("warhammer_up", "Hone warhammer (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 2, ["gold"] = 1, ["crystal"] = 1 }),
        new("shield_up", "Reinforce shield (3×: steel, gilded, energy rim)",
            new Dictionary<string, int> { ["silver"] = 2, ["gold"] = 1, ["crystal"] = 1 }),
        new("great_sword_up", "Hone greatsword (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 3, ["gold"] = 2, ["crystal"] = 1 }),
        new("great_mace_up", "Hone great mace (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 3, ["gold"] = 2, ["crystal"] = 1 }),
        new("great_hammer_up", "Hone great hammer (3×: steel, gilded, energy edge)",
            new Dictionary<string, int> { ["silver"] = 3, ["gold"] = 2, ["crystal"] = 1 }),
        new("tower_shield_up", "Reinforce tower shield (3×: steel, gilded, energy rim)",
            new Dictionary<string, int> { ["silver"] = 3, ["gold"] = 2, ["crystal"] = 1 }),
        // Jetpack tiers — buyable in-run too (the foundry route still works): each craft
        // is a PERMANENT upgrade, banked to the meta save like the mothership purchase.
        new("jetpack_ii", "Jetpack II — +1s burn, orange flame (permanent)",
            new Dictionary<string, int> { ["ruby"] = 3, ["iron"] = 4 }),
        new("jetpack_iii", "Jetpack III — +1s burn, yellow flame (permanent)",
            new Dictionary<string, int> { ["ruby"] = 2, ["diamond"] = 2 }),
        new("jetpack_iv", "Jetpack IV — 5s burn, blue flame (permanent)",
            new Dictionary<string, int> { ["diamond"] = 2, ["voidstone"] = 2 }),
        // Cannon shells — special ammo. Each is a single inventory item; firing the cannon
        // consumes the highest-tier shell available before falling back to the base shot.
        new("ammo_silver", "Silver shell (piercing)",
            new Dictionary<string, int> { ["silver"] = 1, ["coal"] = 1 }),
        new("ammo_ruby", "Ruby shell (incendiary)",
            new Dictionary<string, int> { ["ruby"] = 1, ["coal"] = 1 }),
        new("ammo_sapphire", "Sapphire shell (freezing)",
            new Dictionary<string, int> { ["sapphire"] = 1, ["coal"] = 1 }),
        new("ammo_diamond", "Diamond shell (heavy AoE)",
            new Dictionary<string, int> { ["diamond"] = 1, ["coal"] = 2 }),

        // ─── Consumables ──────────────────────────────────────────────────────
        new("poultice", "Healing poultice (+30 HP, press H)",
            new Dictionary<string, int> { ["moss_stone"] = 3, ["dirt"] = 2 }),
        new("feast", "Hearty feast (+60 HP) — cooked from harvested meat",
            new Dictionary<string, int> { ["meat"] = 4, ["coal"] = 1 }),
        new("dynamite", "Dynamite — 3s fuse, hold to charge the throw",
            new Dictionary<string, int> { ["coal"] = 3, ["gravel"] = 4 }),
        new("dynamite_pack", "Dynamite pack — 3× blast, 3s fuse",
            new Dictionary<string, int> { ["coal"] = 9, ["gravel"] = 8, ["iron"] = 1 }),
        new("tnt", "TNT satchel — bounces, then the fuse blows",
            new Dictionary<string, int> { ["coal"] = 6, ["gravel"] = 6, ["iron"] = 2 }),
        new("tnt_pack", "TNT pack — sticks to walls, same fuse",
            new Dictionary<string, int> { ["coal"] = 6, ["gravel"] = 4, ["iron"] = 2, ["hide"] = 1 }),

        // ─── Late-game ────────────────────────────────────────────────────────
        new("nuke", "Energy blaster — charge-up alien cannon (hold to power up)",
            new Dictionary<string, int> { ["crystal"] = 3, ["gold"] = 10 }),
        new("harpoon", "Anti-Titan harpoon (press Y to fire)",
            new Dictionary<string, int> { ["platinum"] = 4, ["ruby"] = 4 }),
        new("mining_laser", "Mining laser — hold LMB: a beam that disintegrates rock at range",
            new Dictionary<string, int> { ["crystal"] = 4, ["ruby"] = 3, ["diamond"] = 2, ["platinum"] = 4 }),
        new("core_drill", "Core drill — only thing that can pierce the Core",
            new Dictionary<string, int> { ["diamond"] = 5, ["platinum"] = 5, ["crystal"] = 3 }),
    };

    public static bool CanAfford(Recipe r, Inventory inv)
    {
        foreach (var (id, count) in r.Cost)
            if (inv.Count(id) < count) return false;
        return true;
    }

    public static bool TryPay(Recipe r, Inventory inv)
    {
        if (!CanAfford(r, inv)) return false;
        foreach (var (id, count) in r.Cost) inv.TryConsume(id, count);
        return true;
    }
}
