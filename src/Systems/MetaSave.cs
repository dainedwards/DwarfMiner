using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DwarfMiner.Systems;

/// <summary>Persisted across runs. Drives meta-progress: starting bonuses, unlocks, totals.</summary>
public sealed class MetaSave
{
    public int TitansDefeated { get; set; }
    public int Escapes { get; set; }
    public int Deaths { get; set; }
    public int TotalOreMined { get; set; }
    public int DeepestDepth { get; set; }

    // Permanent unlocks earned by clearing runs.
    public int StartingPickaxePower { get; set; } = 1;
    public bool StartWithCannon { get; set; }

    // Star-map progression: how many planets on the PlanetDefs chain are selectable, and
    // which ones have been escaped by ship (drives the map's ESCAPED badges).
    public int PlanetsUnlocked { get; set; } = 1;
    public List<string> PlanetsEscaped { get; set; } = new();

    /// <summary>Seed of the current procedurally generated 7-planet campaign (see
    /// PlanetGen.Campaign). 0 = not rolled yet — Game1 rolls and saves one at boot.
    /// Rerolled when a campaign completes, so every new run gets a fresh system.</summary>
    public int WorldSeed { get; set; }

    /// <summary>Per-planet storage depot stash (planet id → resource id → count). Resources
    /// deposited at a Storage Depot survive death — a new run of that planet can build a depot
    /// and withdraw them, so a long ship-build run isn't wiped by one bad dive. Cleared when
    /// the planet is escaped (the base is left behind).</summary>
    public Dictionary<string, Dictionary<string, int>> Bank { get; set; } = new();

    /// <summary>The (mutable) bank for a planet, created on first access.</summary>
    public Dictionary<string, int> BankFor(string planetId)
    {
        if (!Bank.TryGetValue(planetId, out var b)) { b = new(); Bank[planetId] = b; }
        return b;
    }

    // ── Mothership era (see PLAN.md §0) ──────────────────────────────────────

    /// <summary>Titan souls by TitanKind name ("Godzilla", "Kong", …) — one banked per kill
    /// of that boss type. The upgrade foundry's premium currency. Killing a titan no longer
    /// ends the visit, so souls live here rather than in the per-run inventory.</summary>
    public Dictionary<string, int> TitanSouls { get; set; } = new();

    /// <summary>The mothership's cargo hold (resource id → count). Raw materials still in
    /// the dwarf's pack when the rocket docks are transferred here; the foundry spends
    /// from it.</summary>
    public Dictionary<string, int> ShipCargo { get; set; } = new();

    /// <summary>Fuel units in the mothership's tank — thrusting in space burns it (dry tank
    /// = 35% reserve power), and leftover mined fuel transfers on docking. Starts with a
    /// courtesy tank so the first flight isn't a crawl.</summary>
    public int MotherFuel { get; set; } = 10;

    /// <summary>Purchased foundry upgrade ids (see Space.Upgrades). Permanent.</summary>
    public List<string> ShipUpgrades { get; set; } = new();

    /// <summary>Disposable descent rovers aboard the mothership. Landing consumes one; the
    /// foundry builds more from cargo. Landing with none is an emergency drop pod — you
    /// arrive at half health.</summary>
    public int Rovers { get; set; } = 3;

    /// <summary>Core shards secured (planet ids) — the warp-drive material pierced from near
    /// each planet's center with the core drill. One per world; all of them together let the
    /// mothership warp to the Rift.</summary>
    public List<string> CoreShards { get; set; } = new();

    /// <summary>Completed warp runs (escaped the Rift with its titan slain).</summary>
    public int RunsCompleted { get; set; }

    /// <summary>Mothership state persisted across app restarts: where you parked, which way
    /// the nose points, and the hull you left with. <see cref="ShipStateSaved"/> false =
    /// fresh install — the boot falls back to parking at a planet. Hull -1 = full. (No NaN
    /// sentinels: System.Text.Json refuses to serialize them.)</summary>
    public bool ShipStateSaved { get; set; }
    public float ShipPosX { get; set; }
    public float ShipPosY { get; set; }
    public float ShipHeadingSave { get; set; }
    public int ShipHull { get; set; } = -1;

    /// <summary>Master-volume step (0 full … 3 muted) — cycled with F6, applied at boot.</summary>
    public int VolumeStep { get; set; }

    /// <summary>Borderless-fullscreen preference — toggled with F11, applied at boot.</summary>
    public bool Fullscreen { get; set; }

    /// <summary>Raw metals refined N:1 into pure ingots at the dock. Gems and other rares
    /// (crystal, ruby, sapphire, diamond) are precious as-is and skip refining; bulk stone
    /// stays bulk. Remainders stay raw in the hold and join the next batch.</summary>
    public const int RefineRatio = 4;
    private static readonly string[] Refinable = { "iron", "coal", "silver", "gold", "platinum" };

    public void RefineCargo()
    {
        foreach (var id in Refinable)
        {
            if (!ShipCargo.TryGetValue(id, out var raw) || raw < RefineRatio) continue;
            var pure = raw / RefineRatio;
            ShipCargo["pure_" + id] = ShipCargo.GetValueOrDefault("pure_" + id) + pure;
            var rem = raw % RefineRatio;
            if (rem == 0) ShipCargo.Remove(id);
            else ShipCargo[id] = rem;
        }
    }

    public int SoulsOf(string kind) => TitanSouls.GetValueOrDefault(kind);

    /// <summary>Deduct souls of one specific titan kind. False untouched if short.</summary>
    public bool SpendSoulsOf(string kind, int n)
    {
        if (SoulsOf(kind) < n) return false;
        TitanSouls[kind] -= n;
        if (TitanSouls[kind] == 0) TitanSouls.Remove(kind);
        return true;
    }

    public int TotalSouls()
    {
        var n = 0;
        foreach (var (_, c) in TitanSouls) n += c;
        return n;
    }

    /// <summary>Deduct <paramref name="n"/> souls of any kind (largest stacks first).
    /// Returns false untouched if there aren't enough. Kind-specific costs are a phase 3
    /// upgrade-depth item.</summary>
    public bool SpendSouls(int n)
    {
        if (TotalSouls() < n) return false;
        while (n > 0)
        {
            string? biggest = null;
            var max = 0;
            foreach (var (kind, c) in TitanSouls)
                if (c > max) { max = c; biggest = kind; }
            if (biggest is null) return false;   // unreachable given the TotalSouls check
            var take = Math.Min(n, max);
            TitanSouls[biggest] -= take;
            if (TitanSouls[biggest] == 0) TitanSouls.Remove(biggest);
            n -= take;
        }
        return true;
    }

    private static string SavePath => Path.Combine(SaveSlots.Dir(SaveSlots.Active), "meta.json");

    /// <summary>Read a slot's meta without switching to it — the title screen's summary
    /// line. Null when the slot is empty (a fresh "NEW GAME" slot).</summary>
    public static MetaSave? Peek(int slot)
    {
        try
        {
            var path = Path.Combine(SaveSlots.Dir(slot), "meta.json");
            if (!File.Exists(path)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<MetaSave>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static MetaSave Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return new MetaSave();
            var json = File.ReadAllText(SavePath);
            return JsonSerializer.Deserialize<MetaSave>(json) ?? new MetaSave();
        }
        catch
        {
            return new MetaSave();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch { /* best-effort persistence; don't crash the game on disk errors */ }
    }
}
