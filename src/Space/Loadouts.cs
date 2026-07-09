using System.Collections.Generic;
using DwarfMiner.Systems;

namespace DwarfMiner.Space;

/// <summary>One per-drop supply kit, bought with cargo from the orbit loadout menu (L).
/// Grants are item stacks added to the dwarf's pack when the rover launches.</summary>
public sealed record LoadoutDef(
    string Id,
    string Name,
    string Desc,
    (string id, int n)[] Mats,
    (string id, int n)[] Grants);

/// <summary>The rover loadout catalogue — repeatable, cargo-priced (no souls: these are
/// consumables, not installs). Pending purchases stack and pay out on the next drop.</summary>
public static class Loadouts
{
    public static readonly LoadoutDef[] All =
    {
        new("ammo", "Ammo Pack", "60 pistol rounds",
            Mats: new[] { ("pure_iron", 1) },
            Grants: new[] { ("bullets", 60) }),

        new("med", "Med Pack", "3 healing poultices",
            Mats: new[] { ("pure_silver", 1) },
            Grants: new[] { ("poultice", 3) }),

        new("demo", "Demo Pack", "4 sticks of dynamite",
            Mats: new[] { ("pure_coal", 2) },
            Grants: new[] { ("dynamite", 4) }),

        new("build", "Builder Pack", "60 placeable blocks",
            Mats: new[] { ("stone", 4) },
            Grants: new[] { ("blocks", 60) }),

        new("turret", "Sentry Pack", "2 deployable sentries",
            Mats: new[] { ("pure_iron", 2) },
            Grants: new[] { ("sentry", 2) }),
    };

    public static bool CanAfford(MetaSave meta, LoadoutDef def)
    {
        foreach (var (id, n) in def.Mats)
            if (!meta.ShipCargo.TryGetValue(id, out var have) || have < n) return false;
        return true;
    }

    /// <summary>Pay the cargo price and add one kit to the pending manifest. False
    /// (nothing deducted) when the hold can't cover it.</summary>
    public static bool TryBuy(MetaSave meta, LoadoutDef def, Dictionary<string, int> pending)
    {
        if (!CanAfford(meta, def)) return false;
        foreach (var (id, n) in def.Mats)
        {
            meta.ShipCargo[id] -= n;
            if (meta.ShipCargo[id] <= 0) meta.ShipCargo.Remove(id);
        }
        pending[def.Id] = pending.GetValueOrDefault(def.Id) + 1;
        meta.Save();
        return true;
    }
}
