using System.Collections.Generic;
using DwarfMiner.Entities;

namespace DwarfMiner.Systems;

public sealed record Recipe(string Id, string Name, IReadOnlyDictionary<string, int> Cost);

public static class Crafting
{
    /// <summary>Master recipe table. Order is the order shown in the in-game crafting menu —
    /// permanent upgrades first, then placeable build items, then ammos, then consumables and
    /// late-game super-weapons. New ids that appear in the cost / output should also be wired
    /// into <see cref="World.Tiles.ResourceOrder"/>, <see cref="World.Tiles.ResourceColor"/>,
    /// and <see cref="World.Tiles.ResourceLabel"/> so they render in the inventory panel.</summary>
    public static readonly IReadOnlyList<Recipe> All = new List<Recipe>
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

        // ─── Light & utility ──────────────────────────────────────────────────
        new("lantern", "Lantern — wider headlamp aura",
            new Dictionary<string, int> { ["coal"] = 2, ["iron"] = 1 }),
        new("glowshroom", "Glow-shroom torch (placeable green light)",
            new Dictionary<string, int> { ["moss_stone"] = 3 }),
        new("beacon", "Crystal beacon (placeable; press T to recall)",
            new Dictionary<string, int> { ["crystal"] = 1 }),

        // ─── Building ─────────────────────────────────────────────────────────
        new("support", "Place support beam",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("reinforced_support", "Reinforced support (anchors a 3×3 area)",
            new Dictionary<string, int> { ["iron"] = 2, ["stone"] = 4 }),
        new("ladder", "Ladder (5×) — climb without carving",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("rail", "Rail (5×) — speed boost where laid",
            new Dictionary<string, int> { ["iron"] = 3 }),

        // ─── Combat ───────────────────────────────────────────────────────────
        new("armor", "Iron plate armor (−40% damage taken)",
            new Dictionary<string, int> { ["iron"] = 8 }),
        // Hunter's path to armour: same protection as iron plate, paid in creature parts.
        new("chitin_armor", "Chitin armor (−40% damage taken) — hunted, not mined",
            new Dictionary<string, int> { ["chitin"] = 8, ["hide"] = 3 }),
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
        new("rocket", "Rocket (3×) — launcher ammo",
            new Dictionary<string, int> { ["iron"] = 1, ["coal"] = 2 }),
        new("cannon", "Cannon (right-click upgrade)",
            new Dictionary<string, int> { ["iron"] = 3, ["coal"] = 5 }),
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
        new("dynamite", "Dynamite (press Z to throw)",
            new Dictionary<string, int> { ["coal"] = 3, ["gravel"] = 4 }),
        new("tnt", "TNT satchel — short toss, huge blast",
            new Dictionary<string, int> { ["coal"] = 6, ["gravel"] = 6, ["iron"] = 2 }),

        // ─── Late-game ────────────────────────────────────────────────────────
        new("rocket_part", "Rocket part",
            new Dictionary<string, int> { ["iron"] = 5, ["gold"] = 3 }),
        new("nuke", "Titan-killing nuke",
            new Dictionary<string, int> { ["crystal"] = 3, ["gold"] = 10 }),
        new("harpoon", "Anti-Titan harpoon (press Y to fire)",
            new Dictionary<string, int> { ["platinum"] = 4, ["ruby"] = 4 }),
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
