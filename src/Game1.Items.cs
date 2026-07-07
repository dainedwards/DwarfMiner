using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.Systems;
using Microsoft.Xna.Framework;

namespace DwarfMiner;

/// <summary>
/// The item registry — one declarative row per item/recipe id, replacing the four switches
/// (use-dispatch, craft gates, craft outputs, ownership/weapon classification) that each new
/// item used to touch. Adding an item is now: recipe in Crafting, one ItemDef entry here,
/// and optionally an icon.
/// </summary>
public sealed partial class DwarfMinerGame
{
    /// <summary>One row of the item table. Every field is optional so raw-material ids can
    /// simply be absent; a missing row means "no belt action, stock-on-craft".</summary>
    private sealed record ItemDef
    {
        /// <summary>Weapon semantics: Q/E cycling includes it, god mode loans it on the belt
        /// and fires it without ownership or ammo.</summary>
        public bool Weapon { get; init; }

        /// <summary>Permanent-ownership probe (drill flag, pickaxe tier, ship stage…). Null
        /// means stackable — "ownership" is just inventory count. Drives the crafting menu's
        /// owned-dim, the double-craft guard, and the god-loaner sweep.</summary>
        public Func<bool>? Owned { get; init; }

        /// <summary>Use requires ShootCooldown expired (the firing-rate gate).</summary>
        public bool NeedsCooldown { get; init; }

        /// <summary>Inventory id consumed per use — the item itself for throwables and
        /// consumables, "rocket" for the launcher. God mode fires Weapon rows for free.</summary>
        public string? Ammo { get; init; }

        /// <summary>The belt action while LMB is held. Null = not usable from the belt.</summary>
        public Action<Vector2>? Use { get; init; }

        /// <summary>Craft gate beyond affordability and Owned — tier sequencing, ship-chain
        /// order, pad placement rules. True blocks the craft silently.</summary>
        public Func<bool>? Blocked { get; init; }

        /// <summary>Craft output. Null = the stackable default: add 1 and auto-equip.</summary>
        public Action? OnCraft { get; init; }
    }

    private readonly Dictionary<string, ItemDef> _items;

    private Dictionary<string, ItemDef> BuildItems()
    {
        // Craft output for permanent tools/weapons: flip the ownership flag, stash the
        // 1-count display marker in the inventory, and put it on the belt.
        Action Own(string id, Action setFlag) => () =>
        {
            setFlag();
            _run.Player.Inventory.Add(id, 1);
            _run.Player.Toolbelt.AutoEquip(id);
        };
        // Multi-count stackables (the "5×" recipes) — stock N, then equip like the default.
        Action Stock(string id, int count) => () =>
        {
            _run.Player.Inventory.Add(id, count);
            _run.Player.Toolbelt.AutoEquip(id);
        };
        Action<Vector2> Place(string id) => c =>
            _run.Player.TryPlaceBuildId(_run.Planet, _run.Physics, c, id);

        return new Dictionary<string, ItemDef>
        {
            // ─── Intrinsic tools (on the belt from spawn, always owned) ───────────
            ["pickaxe"] = new() { Owned = () => true, Use = c => DoMine(c, MiningTool.Pickaxe) },
            ["blocks"]  = new() { Owned = () => true, Use = c => _run.Player.TryPlace(_run.Planet, _run.Physics, c) },
            ["bullets"] = new() { Weapon = true, Owned = () => true, NeedsCooldown = true, Use = FireBullet },

            // ─── Crafted tools ────────────────────────────────────────────────────
            ["drill"] = new()
            {
                Owned = () => _run.Player.HasDrill,
                Use = c => DoMine(c, MiningTool.Drill),
                OnCraft = Own("drill", () => _run.Player.HasDrill = true),
            },
            ["hammer"] = new()
            {
                Owned = () => _run.Player.HasHammer,
                Use = c => DoMine(c, MiningTool.Hammer),
                OnCraft = Own("hammer", () => _run.Player.HasHammer = true),
            },
            ["core_drill"] = new()
            {
                Owned = () => _run.Player.HasCoreDrill,
                Use = _ => TryCoreDrill(),
                OnCraft = Own("core_drill", () => _run.Player.HasCoreDrill = true),
            },

            // ─── Firearms ─────────────────────────────────────────────────────────
            ["pistol"] = new()
            {
                Weapon = true, NeedsCooldown = true,
                Owned = () => _run.Player.HasPistol, Use = FirePistol,
                OnCraft = Own("pistol", () => _run.Player.HasPistol = true),
            },
            ["machine_gun"] = new()
            {
                Weapon = true, NeedsCooldown = true,
                Owned = () => _run.Player.HasMachineGun, Use = FireMachineGun,
                OnCraft = Own("machine_gun", () => _run.Player.HasMachineGun = true),
            },
            ["laser"] = new()
            {
                Weapon = true, NeedsCooldown = true,
                Owned = () => _run.Player.HasLaser, Use = FireLaser,
                OnCraft = Own("laser", () => _run.Player.HasLaser = true),
            },
            ["laser_cannon"] = new()
            {
                Weapon = true, NeedsCooldown = true,
                Owned = () => _run.Player.HasLaserCannon, Use = FireLaserCannon,
                OnCraft = Own("laser_cannon", () => _run.Player.HasLaserCannon = true),
            },
            ["rocket_launcher"] = new()
            {
                Weapon = true, NeedsCooldown = true, Ammo = "rocket",
                Owned = () => _run.Player.HasRocketLauncher, Use = FireRocket,
                OnCraft = Own("rocket_launcher", () => _run.Player.HasRocketLauncher = true),
            },
            ["cannon"] = new()
            {
                Weapon = true, NeedsCooldown = true,
                Owned = () => _run.HasCannon, Use = FireCannon,
                OnCraft = Own("cannon", () => _run.HasCannon = true),
            },
            ["rocket"] = new() { OnCraft = Stock("rocket", 3) },

            // ─── Throwables (stackable weapons — god mode throws for free) ────────
            ["dynamite"] = new() { Weapon = true, NeedsCooldown = true, Ammo = "dynamite", Use = FireDynamite },
            ["tnt"]      = new() { Weapon = true, NeedsCooldown = true, Ammo = "tnt",      Use = FireTnt },
            ["harpoon"]  = new() { Weapon = true, NeedsCooldown = true, Ammo = "harpoon",  Use = FireHarpoon },
            ["nuke"]     = new() { Weapon = true, NeedsCooldown = true, Ammo = "nuke",     Use = FireNuke },

            // ─── Consumables ──────────────────────────────────────────────────────
            ["poultice"] = new() { Ammo = "poultice", Use = _ => UseHealPotion() },
            ["feast"]    = new() { Ammo = "feast",    Use = _ => UseFeast() },
            ["sentry"]   = new() { Ammo = "sentry",   Use = _ => PlaceSentryAtFeet() },

            // ─── Placeable build tiles ────────────────────────────────────────────
            ["support"]            = new() { Use = Place("support") },
            ["reinforced_support"] = new() { Use = Place("reinforced_support") },
            ["glowshroom"]         = new() { Use = Place("glowshroom") },
            ["beacon"]             = new() { Use = Place("beacon") },
            // Ladder/rail craft in fives — matches their "(5×)" recipe labels.
            ["ladder"] = new() { Use = Place("ladder"), OnCraft = Stock("ladder", 5) },
            ["rail"]   = new() { Use = Place("rail"),   OnCraft = Stock("rail", 5) },

            // ─── Recipe-only upgrades (no belt slot of their own) ─────────────────
            // Pickaxe tiers step sequentially: each is "owned" at or past its tier and
            // blocked until the previous tier is held.
            ["pickaxe_ii"]  = new() { Owned = () => _run.Player.PickaxeTier >= 2, OnCraft = () => _run.Player.PickaxeTier = 2 },
            ["pickaxe_iii"] = new()
            {
                Owned = () => _run.Player.PickaxeTier >= 3,
                Blocked = () => _run.Player.PickaxeTier < 2,
                OnCraft = () => _run.Player.PickaxeTier = 3,
            },
            ["pickaxe_iv"] = new()
            {
                Owned = () => _run.Player.PickaxeTier >= 4,
                Blocked = () => _run.Player.PickaxeTier < 3,
                OnCraft = () => _run.Player.PickaxeTier = 4,
            },
            ["lantern"]      = new() { Owned = () => _run.Player.HasLantern, OnCraft = () => _run.Player.HasLantern = true },
            ["armor"]        = new() { Owned = () => _run.Player.HasArmor,   OnCraft = () => _run.Player.HasArmor = true },
            ["chitin_armor"] = new() { Owned = () => _run.Player.HasArmor,   OnCraft = () => _run.Player.HasArmor = true },
            // Air tank tops the supply to the new (doubled) ceiling on craft, so it's an
            // immediate breather as well as a permanent capacity bump.
            ["air_tank"] = new()
            {
                Owned = () => _run.Player.HasAirTank,
                OnCraft = () => { _run.Player.HasAirTank = true; _run.Player.Oxygen = _run.Player.EffectiveMaxOxygen; },
            },

            // ─── Ship build chain (see PlaceLaunchPad / InstallShipStage) ─────────
            ["launch_pad"] = new()
            {
                Owned = () => _run.PadPos is not null,
                Blocked = () => !OpenToSky(_run.Player.Position),
                OnCraft = PlaceLaunchPad,
            },
            ["ship_hull"] = new()
            {
                Owned = () => _run.ShipStage >= 1,
                Blocked = () => _run.ShipStage != 0 || !NearPad(),
                OnCraft = InstallShipStage,
            },
            ["ship_engine"] = new()
            {
                Owned = () => _run.ShipStage >= 2,
                Blocked = () => _run.ShipStage != 1 || !NearPad(),
                OnCraft = InstallShipStage,
            },
            ["ship_nav"] = new()
            {
                Owned = () => _run.ShipStage >= 3,
                Blocked = () => _run.ShipStage != 2 || !NearPad(),
                OnCraft = InstallShipStage,
            },
        };
    }

    /// <summary>Dispatch the currently-selected toolbelt slot to its in-world action, called
    /// every frame LMB is held. Gate order: ownership (god bypasses for weapons) → firing
    /// cooldown → ammo (god fires weapons free) → act.</summary>
    private void UseSelectedSlot(Vector2 worldCursor)
    {
        var id = _run.Player.Toolbelt.Current;
        if (id is null || !_items.TryGetValue(id, out var def) || def.Use is null) return;

        var god = _run.Player.FlyMode;
        if (def.Owned is { } owned && !owned() && !(god && def.Weapon)) return;
        if (def.NeedsCooldown && _run.Player.ShootCooldown > 0) return;
        if (def.Ammo is { } ammo && !(god && def.Weapon)
            && !_run.Player.Inventory.TryConsume(ammo, 1)) return;

        def.Use(worldCursor);
    }

    /// <summary>Spend a recipe's cost and apply its output. One-time upgrades refuse when
    /// already owned; Blocked covers sequencing gates (pickaxe tiers, ship chain). Unlisted
    /// ids take the stackable default: stock 1 and auto-equip.</summary>
    private void ApplyCraft(Recipe r)
    {
        _items.TryGetValue(r.Id, out var def);
        if (def?.Owned?.Invoke() == true) return;
        if (def?.Blocked?.Invoke() == true) return;

        if (!Crafting.TryPay(r, _run.Player.Inventory)) return;

        if (def?.OnCraft is { } output)
        {
            output();
        }
        else
        {
            _run.Player.Inventory.Add(r.Id, 1);
            _run.Player.Toolbelt.AutoEquip(r.Id);
        }
    }

    /// <summary>Crafting-menu dim state — permanent items read their ownership probe;
    /// stackables are never "owned".</summary>
    private bool IsOwned(string id) =>
        _items.TryGetValue(id, out var def) && (def.Owned?.Invoke() ?? false);

    /// <summary>Belt ids the Q/E weapon-cycle steps through — anything that shoots or throws.</summary>
    private bool IsWeaponId(string id) =>
        _items.TryGetValue(id, out var def) && def.Weapon;

    /// <summary>True when this belt id is a god-mode loaner the player doesn't actually own —
    /// the ones to sweep off the belt when god mode ends. Stackable weapons count as owned
    /// while any are in stock.</summary>
    private bool IsGodLoanerWeapon(string id) =>
        _items.TryGetValue(id, out var def) && def.Weapon
        && !(def.Owned?.Invoke() ?? _run.Player.Inventory.Count(id) > 0);
}
