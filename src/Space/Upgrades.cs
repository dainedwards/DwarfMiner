using System.Collections.Generic;
using DwarfMiner.Systems;

namespace DwarfMiner.Space;

/// <summary>One purchasable line in the mothership's upgrade foundry: a soul price (of a
/// specific titan kind when <see cref="SoulKind"/> is set — slay that boss to afford it)
/// plus a cargo-hold materials bill. <see cref="Repeatable"/> lines are consumables
/// (rovers) rather than one-time installs; <see cref="Requires"/> gates a tier behind its
/// predecessor. Effects are applied by whoever reads MetaSave.ShipUpgrades (SpaceSim for
/// ship tiers, StartNewRun for dwarf gear).</summary>
public sealed record UpgradeDef(
    string Id,
    string Name,
    string Desc,
    int Souls,
    (string id, int n)[] Mats,
    string? SoulKind = null,
    bool Repeatable = false,
    string? Requires = null);

/// <summary>The foundry catalogue. The still-unbuilt wishlist (cargo hold capacity, shield,
/// ore magnet, scanner, sentry capacity, jetpack tiers, warp engine…) lives in PLAN.md §0
/// and lands in phase 4+.</summary>
public static class Upgrades
{
    public static readonly UpgradeDef[] All =
    {
        new("rover", "Rover",
            "One disposable descent rover (roverless drops cost half your health)",
            Souls: 0, Mats: new[] { ("pure_iron", 3), ("pure_coal", 2) }, Repeatable: true),

        new("jetpack", "Jetpack",
            "Hold JUMP while airborne to fly (charge refills on the ground)",
            Souls: 1, Mats: new[] { ("pure_gold", 2), ("pure_iron", 3) }, SoulKind: "Kong"),

        new("gun2", "Autocannon II",
            "Ship gun fires twice as fast",
            Souls: 1, Mats: new[] { ("pure_iron", 4), ("pure_coal", 3) }, SoulKind: "Mecha"),

        new("engine2", "Ion Engines II",
            "Mothership thrust and top speed up 40 percent, burns less fuel",
            Souls: 2, Mats: new[] { ("pure_silver", 3), ("crystal", 2) }, SoulKind: "Godzilla"),

        new("hull2", "Hull Plating",
            "Mothership hull raised from 5 to 7",
            Souls: 1, Mats: new[] { ("pure_iron", 5), ("stone", 8) }, SoulKind: "Kong"),

        new("o2", "O2 Recycler",
            "Air supply up 50 percent (stacks with the air tank)",
            Souls: 1, Mats: new[] { ("sapphire", 3) }, SoulKind: "Godzilla"),

        new("drill", "Drill Rig",
            "Rovers deploy with a +1 pickaxe tier",
            Souls: 1, Mats: new[] { ("pure_platinum", 2), ("pure_iron", 3) }, SoulKind: "Sandworm"),
    };

    public static bool Owned(MetaSave meta, string id) => meta.ShipUpgrades.Contains(id);

    public static bool CanAfford(MetaSave meta, UpgradeDef def)
    {
        if (def.SoulKind is { } kind ? meta.SoulsOf(kind) < def.Souls : meta.TotalSouls() < def.Souls)
            return false;
        foreach (var (id, n) in def.Mats)
            if (!meta.ShipCargo.TryGetValue(id, out var have) || have < n) return false;
        return true;
    }

    /// <summary>Spend the price and record the purchase (or, for repeatables, apply the
    /// consumable). False (nothing deducted) if it's already owned or unaffordable.</summary>
    public static bool TryBuy(MetaSave meta, UpgradeDef def)
    {
        if (!def.Repeatable && Owned(meta, def.Id)) return false;
        if (!CanAfford(meta, def)) return false;
        if (def.SoulKind is { } kind) meta.SpendSoulsOf(kind, def.Souls);
        else meta.SpendSouls(def.Souls);
        foreach (var (id, n) in def.Mats)
        {
            meta.ShipCargo[id] -= n;
            if (meta.ShipCargo[id] <= 0) meta.ShipCargo.Remove(id);
        }
        if (def.Repeatable)
        {
            if (def.Id == "rover") meta.Rovers++;
        }
        else
        {
            meta.ShipUpgrades.Add(def.Id);
        }
        meta.Save();
        return true;
    }
}
