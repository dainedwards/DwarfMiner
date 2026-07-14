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

        /// <summary>Sfx name played once per shot (see the central fire-sound hook in Update).
        /// Null falls back to the generic "shoot" pew — lets each weapon have its own voice
        /// without touching the individual Fire* methods.</summary>
        public string? ShotSound { get; init; }

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
            // Weapons/mining tools also land on the character doll if a fitting slot is free.
            _run.Player.Equipment.AutoEquip(id);
        };
        // Worn gear (armor pieces, carried lights): stock 1 and put it straight on the doll,
        // replacing whatever occupied that slot — the replaced piece stays in the backpack.
        Action Wear(string id, EquipSlot slot, Action? setFlag = null) => () =>
        {
            setFlag?.Invoke();
            _run.Player.Inventory.Add(id, 1);
            _run.Player.Equipment.Set(slot, id);
        };
        // Accessories: stock 1 and take the first free trinket slot (AutoEquip no-ops when
        // both are full — unlike Wear, a new trinket shouldn't silently displace a chosen one).
        Action Trinket(string id) => () =>
        {
            _run.Player.Inventory.Add(id, 1);
            _run.Player.Equipment.AutoEquip(id);
        };
        // Multi-count stackables (the "5×" recipes) — stock N, then equip like the default.
        Action Stock(string id, int count) => () =>
        {
            _run.Player.Inventory.Add(id, count);
            _run.Player.Toolbelt.AutoEquip(id);
        };
        Action<Vector2> Place(string id) => c =>
            _run.Player.TryPlaceBuildId(_run.Planet, _run.Physics, c, id, _frameDt);

        var items = new Dictionary<string, ItemDef>
        {
            // ─── Intrinsic tools (on the belt from spawn, always owned) ───────────
            ["pickaxe"] = new() { Owned = () => true, Use = c => DoMine(c, MiningTool.Pickaxe) },
            ["blocks"]  = new() { Owned = () => true, Use = c => _run.Player.TryPlace(_run.Planet, _run.Physics, c, _frameDt) },
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
            ["mining_laser"] = new()
            {
                Owned = () => _run.Player.HasMiningLaser,
                Use = c => DoMine(c, MiningTool.MiningLaser),
                OnCraft = Own("mining_laser", () => _run.Player.HasMiningLaser = true),
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
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_pistol",
                Owned = () => _run.Player.HasPistol, Use = FirePistol,
                OnCraft = Own("pistol", () => _run.Player.HasPistol = true),
            },
            ["machine_gun"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_mg",
                Owned = () => _run.Player.HasMachineGun, Use = FireMachineGun,
                OnCraft = Own("machine_gun", () => _run.Player.HasMachineGun = true),
            },
            ["laser"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_laser",
                Owned = () => _run.Player.HasLaser, Use = FireLaser,
                OnCraft = Own("laser", () => _run.Player.HasLaser = true),
            },
            ["laser_cannon"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_beam",
                Owned = () => _run.Player.HasLaserCannon, Use = FireLaserCannon,
                OnCraft = Own("laser_cannon", () => _run.Player.HasLaserCannon = true),
            },
            ["rocket_launcher"] = new()
            {
                Weapon = true, NeedsCooldown = true, Ammo = "rocket", ShotSound = "shoot_rocket",
                Owned = () => _run.Player.HasRocketLauncher, Use = FireRocket,
                OnCraft = Own("rocket_launcher", () => _run.Player.HasRocketLauncher = true),
            },
            // ─── Elemental arms — they fire the cell sim itself (fire/acid) or arc between
            //     bodies (lightning), so they play by material rules, not ballistics. ──────
            ["flamethrower"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_flame",
                Owned = () => _run.Player.HasFlamethrower, Use = FireFlamethrower,
                OnCraft = Own("flamethrower", () => _run.Player.HasFlamethrower = true),
            },
            ["acid_spewer"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_spew",
                Owned = () => _run.Player.HasAcidSpewer, Use = FireAcidSpewer,
                OnCraft = Own("acid_spewer", () => _run.Player.HasAcidSpewer = true),
            },
            ["lightning_gun"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_zap",
                Owned = () => _run.Player.HasLightningGun, Use = FireLightningGun,
                OnCraft = Own("lightning_gun", () => _run.Player.HasLightningGun = true),
            },
            ["cannon"] = new()
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "shoot_cannon",
                Owned = () => _run.HasCannon, Use = FireCannon,
                OnCraft = Own("cannon", () => _run.HasCannon = true),
            },
            ["rocket"] = new() { OnCraft = Stock("rocket", 3) },

            // ─── Throwables (stackable weapons — god mode throws for free) ────────
            ["dynamite"] = new() { Weapon = true, NeedsCooldown = true, Ammo = "dynamite", ShotSound = "throw",        Use = FireDynamite },
            ["tnt"]      = new() { Weapon = true, NeedsCooldown = true, Ammo = "tnt",      ShotSound = "throw",        Use = FireTnt },
            ["tnt_pack"] = new() { Weapon = true, NeedsCooldown = true, Ammo = "tnt_pack", ShotSound = "throw",        Use = FireTntPack },
            ["harpoon"]  = new() { Weapon = true, NeedsCooldown = true, Ammo = "harpoon",  ShotSound = "harpoon",      Use = FireHarpoon },
            ["nuke"]     = new() { Weapon = true, NeedsCooldown = true, Ammo = "nuke",     ShotSound = "shoot_rocket", Use = FireNuke },

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
            ["door"]   = new() { Use = Place("door"),   OnCraft = Stock("door", 2) },

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
            // Carried-light ladder — steps sequentially like pickaxe tiers. Each light is a
            // real item now: it lands in the backpack and the doll's torch slot, and only
            // sheds light while equipped there (LightTier stays the crafted-rung gate).
            // Torches are stackable AND throwable: LMB lobs one that sticks where it hits
            // and burns there — the classic light-the-shaft-as-you-descend loop.
            ["torch"] = new()
            {
                NeedsCooldown = true, Ammo = "torch", ShotSound = "throw", Use = ThrowTorch,
                OnCraft = () =>
                {
                    _run.Player.Inventory.Add("torch", 1);
                    if (_run.Player.LightTier < 1) _run.Player.LightTier = 1;
                    if (_run.Player.Equipment.Get(EquipSlot.Torch) is null)
                        _run.Player.Equipment.Set(EquipSlot.Torch, "torch");
                    _run.Player.Toolbelt.AutoEquip("torch");
                },
            },
            ["lantern"] = new()
            {
                Owned = () => _run.Player.LightTier >= 2,
                Blocked = () => _run.Player.LightTier < 1,
                OnCraft = Wear("lantern", EquipSlot.Torch, () => _run.Player.LightTier = 2),
            },
            ["helm_lamp"] = new()
            {
                Owned = () => _run.Player.LightTier >= 3,
                Blocked = () => _run.Player.LightTier < 2,
                OnCraft = Wear("helm_lamp", EquipSlot.Torch, () => _run.Player.LightTier = 3),
            },
            ["sun_crystal"] = new()
            {
                Owned = () => _run.Player.LightTier >= 4,
                Blocked = () => _run.Player.LightTier < 3,
                OnCraft = Wear("sun_crystal", EquipSlot.Torch, () => _run.Player.LightTier = 4),
            },
            // Headlamp upgrade rungs — recipe-only (the worn helm_lamp item itself doesn't
            // change, it just shines harder per rung; see Player.LightMul).
            ["headlamp_ii"] = new()
            {
                Owned = () => _run.Player.HeadlampTier >= 2,
                Blocked = () => _run.Player.LightTier < 3,
                OnCraft = () => _run.Player.HeadlampTier = 2,
            },
            ["headlamp_iii"] = new()
            {
                Owned = () => _run.Player.HeadlampTier >= 3,
                Blocked = () => _run.Player.LightTier < 3 || _run.Player.HeadlampTier < 2,
                OnCraft = () => _run.Player.HeadlampTier = 3,
            },
            ["headlamp_iv"] = new()
            {
                Owned = () => _run.Player.HeadlampTier >= 4,
                Blocked = () => _run.Player.LightTier < 3 || _run.Player.HeadlampTier < 3,
                OnCraft = () => _run.Player.HeadlampTier = 4,
            },
            // Armor — one craft per piece, worn on the doll. Iron and chitin chest plates are
            // separately ownable now that the character screen can swap between sets.
            ["armor"]           = new() { Owned = () => _run.Player.Inventory.Count("armor") > 0,           OnCraft = Wear("armor", EquipSlot.Chest, () => _run.Player.HasArmor = true) },
            ["chitin_armor"]    = new() { Owned = () => _run.Player.Inventory.Count("chitin_armor") > 0,    OnCraft = Wear("chitin_armor", EquipSlot.Chest, () => _run.Player.HasArmor = true) },
            ["iron_helmet"]     = new() { Owned = () => _run.Player.Inventory.Count("iron_helmet") > 0,     OnCraft = Wear("iron_helmet", EquipSlot.Head) },
            ["iron_leggings"]   = new() { Owned = () => _run.Player.Inventory.Count("iron_leggings") > 0,   OnCraft = Wear("iron_leggings", EquipSlot.Legs) },
            ["iron_boots"]      = new() { Owned = () => _run.Player.Inventory.Count("iron_boots") > 0,      OnCraft = Wear("iron_boots", EquipSlot.Feet) },
            ["chitin_helmet"]   = new() { Owned = () => _run.Player.Inventory.Count("chitin_helmet") > 0,   OnCraft = Wear("chitin_helmet", EquipSlot.Head) },
            ["chitin_leggings"] = new() { Owned = () => _run.Player.Inventory.Count("chitin_leggings") > 0, OnCraft = Wear("chitin_leggings", EquipSlot.Legs) },
            ["chitin_boots"]    = new() { Owned = () => _run.Player.Inventory.Count("chitin_boots") > 0,    OnCraft = Wear("chitin_boots", EquipSlot.Feet) },
            ["leather_gloves"]  = new() { Owned = () => _run.Player.Inventory.Count("leather_gloves") > 0,  OnCraft = Wear("leather_gloves", EquipSlot.Gloves) },
            ["iron_gauntlets"]  = new() { Owned = () => _run.Player.Inventory.Count("iron_gauntlets") > 0,  OnCraft = Wear("iron_gauntlets", EquipSlot.Gloves) },
            // Accessories — stock 1 and slip into the first free trinket slot; if both are
            // occupied it waits in the backpack for a manual swap on the character screen.
            ["band_regen"]    = new() { Owned = () => _run.Player.Inventory.Count("band_regen") > 0,    OnCraft = Trinket("band_regen") },
            ["magnet_ring"]   = new() { Owned = () => _run.Player.Inventory.Count("magnet_ring") > 0,   OnCraft = Trinket("magnet_ring") },
            ["miners_charm"]  = new() { Owned = () => _run.Player.Inventory.Count("miners_charm") > 0,  OnCraft = Trinket("miners_charm") },
            ["aegis_pendant"] = new() { Owned = () => _run.Player.Inventory.Count("aegis_pendant") > 0, OnCraft = Trinket("aegis_pendant") },
            // Air tank tops the supply to the new (doubled) ceiling on craft, so it's an
            // immediate breather as well as a permanent capacity bump.
            ["air_tank"] = new()
            {
                Owned = () => _run.Player.HasAirTank,
                OnCraft = () => { _run.Player.HasAirTank = true; _run.Player.Oxygen = _run.Player.EffectiveMaxOxygen; },
            },

            // ─── Surface base ────────────────────────────────────────────────────
            ["storage_depot"] = new()
            {
                Owned = () => _run.DepotPos is not null,   // one depot per run
                Blocked = () => !OpenToSky(_run.Player.Position),
                OnCraft = PlaceDepot,
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

        // ─── Melee arsenal — generated rows: the weapon itself plus its "hone" upgrade
        // (craftable three times, rungs II-IV; rung IV is the energy edge). ─────────────
        foreach (var mid in Toolbelt.MeleeIds)
        {
            var id = mid;   // capture per iteration
            items[id] = new ItemDef
            {
                Weapon = true, NeedsCooldown = true, ShotSound = "throw",
                Owned = () => _run.Player.MeleeTiers.ContainsKey(id),
                Use = c => MeleeAttack(id, c),
                OnCraft = Own(id, () =>
                    _run.Player.MeleeTiers[id] = Math.Max(1, _run.Player.MeleeTiers.GetValueOrDefault(id))),
            };
            items[$"{id}_up"] = new ItemDef
            {
                Owned = () => _run.Player.MeleeTiers.GetValueOrDefault(id) >= 4,
                Blocked = () => !_run.Player.MeleeTiers.ContainsKey(id),
                OnCraft = () => _run.Player.MeleeTiers[id] =
                    Math.Min(4, _run.Player.MeleeTiers.GetValueOrDefault(id, 1) + 1),
            };
        }

        // Jetpack tiers craftable in-run — permanent purchases banked to the meta save
        // (identical effect to the mothership foundry route).
        void JetTier(string craftId, string metaId, Action apply, Func<bool> owned, Func<bool> blocked)
        {
            items[craftId] = new ItemDef
            {
                Owned = owned,
                Blocked = blocked,
                OnCraft = () =>
                {
                    apply();
                    if (!_meta.ShipUpgrades.Contains(metaId)) _meta.ShipUpgrades.Add(metaId);
                    _meta.Save();
                },
            };
        }
        JetTier("jetpack_ii", "jetpack2", () => _run.Player.JetTier2 = true,
            () => _run.Player.JetTier2, () => !_run.Player.HasJetpack);
        JetTier("jetpack_iii", "jetpack3", () => _run.Player.JetTier3 = true,
            () => _run.Player.JetTier3, () => !_run.Player.JetTier2);
        JetTier("jetpack_iv", "jetpack4", () => _run.Player.JetTier4 = true,
            () => _run.Player.JetTier4, () => !_run.Player.JetTier3);

        return items;
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
        // Leatherback's EMP fries anything beam-powered — ballistics and muscle still work.
        if (_run.Player.EmpTimer > 0f && id is "laser" or "laser_cannon" or "mining_laser") return;
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
        _sfx.Play("ui", 0.6f, 0.1f);

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
