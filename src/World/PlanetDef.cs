using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>Which boss hatches from this planet's egg. Each has a distinct special attack:
/// Godzilla breathes fire, Mecha fires a mouth laser, Hydra burrows and erupts, Kong leaps
/// and quakes. The shared quadruped chassis is re-tinted per kind.</summary>
public enum TitanKind { Godzilla, Mecha, Hydra, Kong }

/// <summary>
/// Static description of a planet archetype — the knobs WorldGen and Game1 previously kept as
/// constants, plus star-map presentation and the ship's per-planet resource demand. One def per
/// world on the overworld chain; defs are data only (no per-run state — that's Session).
/// </summary>
/// <param name="OreBias">Threshold reductions for ores this planet is rich in. Each entry
/// lowers that ore's world-gen noise threshold, so 0.02–0.04 turns "rare" into "signature".</param>
/// <param name="QuakeScale">Multiplies the earthquake interval — below 1 means more quakes.</param>
/// <param name="ShipOre">Extra resource id demanded by the ship's nav core on this planet —
/// chosen to force a deep dive into the planet's signature ore before you can leave.</param>
/// <param name="OxygenDrainScale">Multiplies how fast the dwarf's air supply depletes at
/// depth — thin-atmosphere worlds (dead metal, volcanic) burn air faster, so deep dives on
/// them demand the air-tank upgrade sooner.</param>
public sealed record PlanetDef(
    string Id,
    string Name,
    string Tagline,
    Color MapColor,
    Color MapAccent,
    TileKind SurfaceTile,
    int LakeMin, int LakeExtra,
    int MountainMin, int MountainExtra,
    float MountainHeightScale,
    float LavaFillFrac,
    bool HasWater,
    (TileKind ore, float bias)[] OreBias,
    float QuakeScale,
    int CaveSpawnCap,
    string ShipOre, int ShipOreCount,
    float OxygenDrainScale = 1f,
    bool SeedsGas = false,
    bool SeedsAcid = false,
    TitanKind Titan = TitanKind.Godzilla);

/// <summary>The overworld chain, in unlock order. Escaping planet i unlocks planet i+1.</summary>
public static class PlanetDefs
{
    public static readonly PlanetDef[] All =
    {
        new("verdant", "Verdant", "Gentle green start, lakes, moss, iron",
            new Color(90, 150, 80), new Color(150, 210, 130),
            TileKind.Grass,
            LakeMin: 3, LakeExtra: 1, MountainMin: 6, MountainExtra: 3,
            MountainHeightScale: 1.0f, LavaFillFrac: 0.45f, HasWater: true,
            OreBias: new[] { (TileKind.IronOre, 0.015f), (TileKind.CoalOre, 0.015f) },
            QuakeScale: 1.0f, CaveSpawnCap: 14,
            ShipOre: "gold", ShipOreCount: 3),

        new("frost", "Frost", "Frozen wastes, deep water, sapphire seams",
            new Color(150, 180, 220), new Color(220, 235, 255),
            TileKind.Snow,
            LakeMin: 4, LakeExtra: 2, MountainMin: 8, MountainExtra: 3,
            MountainHeightScale: 1.25f, LavaFillFrac: 0.30f, HasWater: true,
            OreBias: new[] { (TileKind.Sapphire, 0.030f), (TileKind.SilverOre, 0.020f) },
            QuakeScale: 1.2f, CaveSpawnCap: 16,
            ShipOre: "sapphire", ShipOreCount: 4, OxygenDrainScale: 1.1f),

        new("ember", "Ember", "Volcanic furnace, lava high, rubies below",
            new Color(190, 90, 50), new Color(255, 170, 90),
            TileKind.Gravel,
            LakeMin: 0, LakeExtra: 0, MountainMin: 9, MountainExtra: 4,
            MountainHeightScale: 1.4f, LavaFillFrac: 0.62f, HasWater: false,
            OreBias: new[] { (TileKind.Ruby, 0.030f), (TileKind.CoalOre, 0.030f), (TileKind.Obsidian, 0f) },
            QuakeScale: 0.55f, CaveSpawnCap: 18,
            ShipOre: "ruby", ShipOreCount: 4, OxygenDrainScale: 1.35f, SeedsGas: true),

        new("slag", "Slag", "Dead metal world, platinum veins, restless crust",
            new Color(130, 125, 140), new Color(200, 205, 220),
            TileKind.Gravel,
            LakeMin: 0, LakeExtra: 0, MountainMin: 4, MountainExtra: 2,
            MountainHeightScale: 0.7f, LavaFillFrac: 0.35f, HasWater: false,
            OreBias: new[] { (TileKind.PlatinumOre, 0.030f), (TileKind.GoldOre, 0.020f), (TileKind.IronOre, 0.025f) },
            QuakeScale: 0.45f, CaveSpawnCap: 20,
            ShipOre: "platinum", ShipOreCount: 5, OxygenDrainScale: 1.7f, SeedsAcid: true),

        new("core", "Coreheart", "The finale, diamond-rich, swarming, lava at the door",
            new Color(120, 70, 160), new Color(220, 150, 255),
            TileKind.Basalt,
            LakeMin: 1, LakeExtra: 1, MountainMin: 10, MountainExtra: 4,
            MountainHeightScale: 1.5f, LavaFillFrac: 0.58f, HasWater: true,
            OreBias: new[] { (TileKind.Diamond, 0.030f), (TileKind.Crystal, 0.025f), (TileKind.Ruby, 0.015f) },
            QuakeScale: 0.5f, CaveSpawnCap: 24,
            ShipOre: "diamond", ShipOreCount: 5, OxygenDrainScale: 1.4f, SeedsGas: true, SeedsAcid: true),
    };

    public static PlanetDef ById(string id)
    {
        foreach (var d in All) if (d.Id == id) return d;
        return All[0];
    }

    public static int IndexOf(PlanetDef def)
    {
        for (var i = 0; i < All.Length; i++) if (All[i].Id == def.Id) return i;
        return 0;
    }
}
