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
            "Worn on the back: hold JUMP airborne to hover (1s burn, refills grounded)",
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

        // ── Tier II of the dwarf/ship lines + the wishlist batch (phase 6) ──

        new("jetpack2", "Jetpack II",
            "Burn 30 percent longer, a stronger climb - orange flame",
            Souls: 1, Mats: new[] { ("ruby", 3) }, SoulKind: "Kong", Requires: "jetpack"),

        new("gun3", "Autocannon III",
            "Twin-barrel spread, same blistering rate",
            Souls: 2, Mats: new[] { ("pure_iron", 4), ("crystal", 2) }, SoulKind: "Mecha", Requires: "gun2"),

        new("engine3", "Ion Engines III",
            "75 percent over stock thrust and the leanest burn",
            Souls: 2, Mats: new[] { ("pure_platinum", 3), ("diamond", 2) }, SoulKind: "Godzilla", Requires: "engine2"),

        new("shield", "Deflector Shield",
            "Absorbs one impact, recharges in 8 seconds",
            Souls: 2, Mats: new[] { ("diamond", 3) }, SoulKind: "Mecha"),

        new("dampeners", "Pod Dampeners",
            "Emergency drop pods land soft - no more suit damage",
            Souls: 1, Mats: new[] { ("pure_silver", 2), ("sapphire", 2) }, SoulKind: "Sandworm"),

        new("armory", "Rover Armory",
            "Every rover deploys with a pistol and 90 rounds",
            Souls: 1, Mats: new[] { ("pure_iron", 4), ("pure_gold", 2) }, SoulKind: "Sandworm"),

        new("scanner", "Geo Scanner",
            "HUD arrows track the nearest fuel, signature ore, and the titan",
            Souls: 1, Mats: new[] { ("pure_silver", 2), ("crystal", 2) }, SoulKind: "Mecha"),

        new("plating", "Combat Plating",
            "Suit takes 30 percent less damage (stacks with crafted armor)",
            Souls: 1, Mats: new[] { ("pure_iron", 3), ("pure_platinum", 2) }, SoulKind: "Kong"),

        new("supplies", "Supply Cache",
            "Every rover deploys with 2 poultices, 40 blocks, and a sentry",
            Souls: 1, Mats: new[] { ("pure_gold", 2), ("pure_coal", 2) }, SoulKind: "Godzilla"),

        // ── Third-tier lines (phase 10) ──

        new("jetpack3", "Jetpack III",
            "Another 30 percent of burn and more lift - yellow flame",
            Souls: 2, Mats: new[] { ("ruby", 2), ("diamond", 2) }, SoulKind: "Kong", Requires: "jetpack2"),

        new("jetpack4", "Jetpack IV",
            "The full-blue burner: longest burn, hardest climb",
            Souls: 3, Mats: new[] { ("diamond", 2), ("voidstone", 2) }, SoulKind: "Kong", Requires: "jetpack3"),

        new("shield2", "Aegis Capacitor",
            "Deflector shield recharges twice as fast",
            Souls: 2, Mats: new[] { ("pure_platinum", 2), ("crystal", 2) }, SoulKind: "Mecha", Requires: "shield"),

        new("o22", "O2 Reserves II",
            "Air supply doubled (stacks with the air tank)",
            Souls: 1, Mats: new[] { ("sapphire", 3), ("diamond", 1) }, SoulKind: "Godzilla", Requires: "o2"),

        new("hull3", "Hull Plating II",
            "Mothership hull raised from 7 to 9",
            Souls: 2, Mats: new[] { ("pure_iron", 4), ("pure_platinum", 2) }, SoulKind: "Kong", Requires: "hull2"),

        // ── Aquatics: swimming and the breath meter ──

        new("fins", "Hydrofoil Fins",
            "Swim twice as fast - water becomes the fast lane",
            Souls: 1, Mats: new[] { ("pure_silver", 2), ("crystal", 1) }, SoulKind: "Sandworm"),

        new("lungs", "Deep Lungs",
            "Breath meter doubled - twice as long underwater",
            Souls: 1, Mats: new[] { ("sapphire", 2) }, SoulKind: "Godzilla"),

        new("lungs2", "Abyssal Lungs",
            "Breath meter tripled - the lake floor is a stroll",
            Souls: 1, Mats: new[] { ("sapphire", 2), ("emerald", 1) }, SoulKind: "Godzilla",
            Requires: "lungs"),

        new("gills", "Gill Graft",
            "Breathe water like air - the meter never drains",
            Souls: 2, Mats: new[] { ("emerald", 2), ("diamond", 1) }, SoulKind: "Sandworm",
            Requires: "lungs2"),

        // ── The Vac Suit opens every airless world: the belt Hollow + the cratered moon ──

        new("vacsuit", "Vac Suit",
            "Sealed exosuit - required to land on airless worlds (the Hollow, dead moons)",
            Souls: 1, Mats: new[] { ("pure_iron", 4), ("sapphire", 2) }, SoulKind: "Mecha"),

        // ── Rare-gem capstones (phase 11): emerald seams + Rift voidstone ──

        new("vitality", "Emerald Weave",
            "Suit weave laced with emerald - max health up 40 percent",
            Souls: 1, Mats: new[] { ("emerald", 3) }, SoulKind: "Kong"),

        new("voidcore", "Voidstone Reactor",
            "The engines feed on the void - thrust burns no fuel, ever",
            Souls: 3, Mats: new[] { ("voidstone", 2), ("diamond", 2) }, SoulKind: "Mecha"),
    };

    /// <summary>True when a tiered line's prerequisite hasn't been installed yet.</summary>
    public static bool Locked(MetaSave meta, UpgradeDef def)
        => def.Requires is { } req && !Owned(meta, req);

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
        if (Locked(meta, def) || !CanAfford(meta, def)) return false;
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
