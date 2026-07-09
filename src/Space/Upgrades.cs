using System.Collections.Generic;
using DwarfMiner.Systems;

namespace DwarfMiner.Space;

/// <summary>One purchasable line in the mothership's upgrade foundry: a soul price plus a
/// cargo-hold materials bill. Effects are applied by whoever reads MetaSave.ShipUpgrades
/// (SpaceSim for ship tiers, StartNewRun for dwarf gear).</summary>
public sealed record UpgradeDef(
    string Id,
    string Name,
    string Desc,
    int Souls,
    (string id, int n)[] Mats);

/// <summary>The foundry catalogue. Phase 2 ships three lines; the noted-for-later list
/// (drill/armor/O2/cargo/shield/magnet/scanner/rover/sentry tiers, warp engine) lives in
/// PLAN.md §0 and lands in phases 3–4.</summary>
public static class Upgrades
{
    public static readonly UpgradeDef[] All =
    {
        new("jetpack", "Jetpack",
            "Hold JUMP while airborne to fly (charge refills on the ground)",
            Souls: 1, Mats: new[] { ("gold", 4), ("iron", 6) }),

        new("gun2", "Autocannon II",
            "Ship gun fires twice as fast",
            Souls: 1, Mats: new[] { ("iron", 8), ("coal", 6) }),

        new("engine2", "Ion Engines II",
            "Mothership thrust and top speed up 40 percent",
            Souls: 2, Mats: new[] { ("silver", 5), ("crystal", 2) }),
    };

    public static bool Owned(MetaSave meta, string id) => meta.ShipUpgrades.Contains(id);

    public static bool CanAfford(MetaSave meta, UpgradeDef def)
    {
        if (meta.TotalSouls() < def.Souls) return false;
        foreach (var (id, n) in def.Mats)
            if (!meta.ShipCargo.TryGetValue(id, out var have) || have < n) return false;
        return true;
    }

    /// <summary>Spend the price and record the purchase. False (nothing deducted) if it's
    /// already owned or unaffordable.</summary>
    public static bool TryBuy(MetaSave meta, UpgradeDef def)
    {
        if (Owned(meta, def.Id) || !CanAfford(meta, def)) return false;
        meta.SpendSouls(def.Souls);
        foreach (var (id, n) in def.Mats)
        {
            meta.ShipCargo[id] -= n;
            if (meta.ShipCargo[id] <= 0) meta.ShipCargo.Remove(id);
        }
        meta.ShipUpgrades.Add(def.Id);
        meta.Save();
        return true;
    }
}
