using DwarfMiner.Entities;
using Microsoft.Xna.Framework;

namespace DwarfMiner.UI;

/// <summary>Inventory sorting category — every item id maps to exactly one (plus the ALL
/// pseudo-category for the unfiltered tab). Drives the backpack tabs and each cell's
/// colour-coded underline.</summary>
public enum ItemCategory { All, Gear, Weapons, Tools, Materials, Building }

public static class ItemInfo
{
    /// <summary>Tab order on the backpack pane.</summary>
    public static readonly ItemCategory[] Tabs =
    {
        ItemCategory.All, ItemCategory.Gear, ItemCategory.Weapons,
        ItemCategory.Tools, ItemCategory.Materials, ItemCategory.Building,
    };

    public static string LabelOf(ItemCategory c) => c switch
    {
        ItemCategory.Gear => "GEAR",
        ItemCategory.Weapons => "WEAPONS",
        ItemCategory.Tools => "TOOLS",
        ItemCategory.Materials => "MATERIALS",
        ItemCategory.Building => "BUILDING",
        _ => "ALL",
    };

    public static Color ColorOf(ItemCategory c) => c switch
    {
        ItemCategory.Gear => new Color(255, 205, 110),      // gold — worn things
        ItemCategory.Weapons => new Color(255, 120, 110),   // red — things that hurt
        ItemCategory.Tools => new Color(120, 215, 255),     // cyan — things that dig/fix
        ItemCategory.Materials => new Color(150, 220, 140), // green — raw stock
        ItemCategory.Building => new Color(235, 160, 80),   // orange — things you place
        _ => new Color(210, 214, 228),
    };

    public static ItemCategory CategoryOf(string id)
    {
        // Worn gear: everything the paper doll accepts, plus the light ladder.
        if (id is "torch" or "lantern" or "helm_lamp" or "sun_crystal" or "jetpack"
            or "armor" or "chitin_armor" or "iron_helmet" or "chitin_helmet"
            or "iron_leggings" or "chitin_leggings" or "iron_boots" or "chitin_boots"
            or "leather_gloves" or "iron_gauntlets"
            or "band_regen" or "magnet_ring" or "miners_charm" or "aegis_pendant"
            or "air_tank")
            return ItemCategory.Gear;

        // Weapons: the belt's weapon-slot set (guns, throwables, melee) and their ammo.
        if (Toolbelt.IsWeaponSlotId(id)
            || id is "rocket" or "ammo_silver" or "ammo_ruby" or "ammo_sapphire" or "ammo_diamond")
            return ItemCategory.Weapons;

        // Tools: mining implements and field kit — including the geo-scanner rungs and
        // the mobility gear (grapple line, rope coils).
        if (Toolbelt.IsMiningToolId(id) || id is "core_drill" or "poultice" or "feast" or "sentry"
            or "scanner" or "scanner_ii" or "scanner_iii" or "scanner_iv"
            or "grapple" or "rope")
            return ItemCategory.Tools;

        // Building: everything placeable, plus the base/ship construction chain.
        if (id is "blocks" or "ladder" or "door" or "rail" or "support" or "reinforced_support"
            or "glowshroom" or "beacon" or "storage_depot" or "launch_pad"
            or "ship_hull" or "ship_engine" or "ship_nav"
            or "brick" or "plating" or "glass_block" or "platform")
            return ItemCategory.Building;

        // Everything else is raw stock: ores, gems, soils, creature parts.
        return ItemCategory.Materials;
    }
}
