using System;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>Which boss hatches from this planet's egg. Each has its own procedural skeleton
/// (see TitanRenderer) and a distinct attack: Godzilla breathes fire, Mecha fires a mouth
/// laser, the Sandworm burrows and breaches, Kong leaps and quakes. The kaiju wave (Pacific
/// Rim-inspired): Knifehead gores with a blade-crest charge, Otachi spits arcing acid that
/// pools in the cell sim, Leatherback detonates an EMP that fries the dwarf's tech, Raiju
/// lunges in rapid dash chains, Slattern — the category-5 apex — whips radial tail-spike
/// barrages and sonic pulses. The flyers: Pyrodactyl and Vitriodactyl are pterodactyl-built
/// wing kaiju that cruise above the surface and carpet-bomb the dwarf — one rains lava,
/// the other rains acid. CosmicOctopus — the Starspawn — is the Hollow asteroid's abyss
/// god: a weightless nebula-hided octopus whose egg is buried near the core; it swims
/// through solid rock, spits void-bolt volleys, and drags prey in with a gravity well.
/// New values append at the end so RunSave's int cast stays valid.</summary>
public enum TitanKind { Godzilla, Mecha, Sandworm, Kong, Knifehead, Otachi, Leatherback, Raiju, Slattern, Pyrodactyl, Vitriodactyl, CosmicOctopus }

/// <summary>
/// Static description of a planet archetype — the knobs WorldGen and Game1 previously kept as
/// constants, plus star-map presentation and the ship's per-planet resource demand. One def per
/// world on the overworld chain; defs are data only (no per-run state — that's Session).
/// </summary>
/// <param name="OreBias">Threshold reductions for ores this planet is rich in. Each entry
/// lowers that ore's world-gen noise threshold, so 0.02–0.04 turns "rare" into "signature".</param>
/// <param name="QuakeScale">Restless-crust rating — 0.6 and below flags the QUAKES hazard on
/// the star map. (Quake cadence itself now rides the shared disaster clock; see
/// AmbientDirector and <paramref name="Difficulty"/>.)</param>
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
    // Buries flammable oil sumps in the mid-crust caves (WorldGen) — inert pools that burn
    // when fire, lava, or an explosion reaches them.
    bool SeedsOil = false,
    TitanKind Titan = TitanKind.Godzilla,
    int CrystalPockets = 0,
    int FungalPockets = 0,
    TitanKind[]? TitanPool = null,
    // ── Size + new biome knobs ─────────────────────────────────────────────────
    // SizeScale multiplies the standard 200-ring planet (0.7 dwarf .. 1.8 giant); the
    // random-planet generator ties it to difficulty. LakeScale widens/deepens every lake
    // basin (ocean worlds run 8+ lakes at ~3×, so the surface is mostly sea). AcidPools
    // carves that many open acid ponds into the surface; AcidRain arms the toxic-cloud
    // ambient event (AmbientDirector) that periodically rains acid around the dwarf.
    float SizeScale = 1f,
    float LakeScale = 1f,
    int AcidPools = 0,
    bool AcidRain = false,
    // ── Volcano knobs ──────────────────────────────────────────────────────────
    // Volcanoes raises that many basalt cones on the surface, each with an open crater
    // pool and a primed throat running down to a deep magma chamber (WorldGen
    // .CarveVolcanoes). VolcanoScale sizes the cones (fire worlds run big, the rare
    // strays on other biomes small); VolcanoAcid switches the fluid to vitriol — the
    // acid worlds' volcanoes vent acid instead of lava.
    int Volcanoes = 0,
    float VolcanoScale = 1f,
    bool VolcanoAcid = false,
    // Non-null on banded worlds (the debug planet): the surface course cycles through these
    // tiles, one wedge per entry, instead of painting SurfaceTile everywhere. SurfaceTile
    // still drives the snow-cap and blizzard gates.
    TileKind[]? SurfaceBands = null,
    // ── Civilisation knobs ─────────────────────────────────────────────────────
    // CityLots raises that many alien skyscrapers on the surface (WorldGen.RaiseCity):
    // alloy-hulled towers with glowing window bands, floor slabs, a street-level doorway
    // and a beacon-tipped antenna. Civilians (neutral fauna) inhabit them. LizardCities
    // buries that many lizardman warrens underground (WorldGen.CarveLizardCities): brick
    // chamber halls joined by tunnel networks, with huts, glowshroom lighting, a treasure
    // vault, a surface entrance shaft — and evil lizardman warriors guarding it all.
    int CityLots = 0,
    int LizardCities = 0,
    // Which biome archetype stamped this world — keys the ambient wildlife roster
    // (SpawnDirector): every biome keeps its own signature neutral species. One of
    // verdant / frost / ember / slag / ocean / acid / crystal / city / rift / debug.
    string Biome = "verdant",
    // Where this world sits on the campaign ramp, 0 (gentlest) .. 1 (hardest). PlanetGen
    // stamps it from the slot index; the Classic chain hand-sets it. Drives the shared
    // disaster clock's spacing (AmbientDirector: ~7 min at 0 down to ~2 min at 1).
    float Difficulty = 0f,
    // ── Outer-belt asteroid knobs (the Hollow) ─────────────────────────────────
    // GravityScale multiplies surface gravity for the dwarf AND every creature (0.45 on
    // the mega-asteroid: huge floaty jumps, slow falls). Airless worlds have no atmosphere
    // at all — the mothership cannot make entry until the Vac Suit foundry upgrade is
    // installed (SpaceSim bounces the ship off, same as the locked Rift's storm wall).
    // Craters pock the surface with that many dry impact bowls (no rim, no fill — the
    // meteor-blasted look). GreatGeode buries one vast crystal-lined hollow at mid-depth
    // (WorldGen.CarveGreatGeode): the marquee cavern, studded with gem overlays.
    float GravityScale = 1f,
    bool Airless = false,
    int Craters = 0,
    bool GreatGeode = false,
    // Silhouette undulation in legacy tiles: 0 keeps the classic lathed-round planet;
    // the belt asteroid runs ~20, so its surface radius swings by whole lobes — a potato,
    // not a circle. WorldGen stamps the resulting terrain line into Planet.SurfaceProfile
    // so depth-below-surface (oxygen, exposure) follows the local ground.
    float Lumpiness = 0f,
    // Non-null on MOONS: the id of the planet this world orbits. SpaceSim hangs it on a
    // tight, fast satellite orbit around that parent instead of a solar orbit. Moons come
    // in two kinds: with an atmosphere (the ocean moon — lands like any planet) and
    // without (the cratered moon — vac-suit gated like the Hollow, via Airless).
    string? MoonOf = null);

/// <summary>The overworld chain, in unlock order. Escaping planet i unlocks planet i+1.
/// <see cref="All"/> is the ACTIVE chain: at boot Game1 swaps in a procedurally generated
/// 7-planet campaign (see PlanetGen) seeded from MetaSave.WorldSeed, with the Rift appended
/// as the fixed finale. <see cref="Classic"/> keeps the hand-tuned archetypes — they're the
/// generator's templates, the default when nothing was activated (headless sim tests), and
/// the fallback for id lookups from tooling hooks like DM_AUTOSTART=verdant.</summary>
public static class PlanetDefs
{
    public static PlanetDef[] All { get; private set; } = null!;

    /// <summary>Dev switch, ON by default for now (DM_DEBUG=0 turns it off): Activate appends
    /// <see cref="DebugWorld"/> — the kitchen-sink QA planet — to whatever chain the game
    /// rolls, parked on a close-in orbit near the sun (see SpaceSim).</summary>
    public static bool DebugMode { get; } = Environment.GetEnvironmentVariable("DM_DEBUG") != "0";

    /// <summary>Swap in a generated campaign chain (call before anything caches planets).
    /// The Hollow — the landable mega-asteroid in the outer belt — rides along on every
    /// chain: it's a fixed bonus destination like the Rift, not a campaign slot.</summary>
    public static void Activate(PlanetDef[] chain)
    {
        var extras = DebugMode ? 2 : 1;
        var withExtras = new PlanetDef[chain.Length + extras];
        chain.CopyTo(withExtras, 0);
        withExtras[chain.Length] = HollowWorld;
        if (DebugMode) withExtras[^1] = DebugWorld;
        All = withExtras;
    }

    /// <summary>The kitchen-sink QA world: max size, every disaster armed (quakes, meteors,
    /// flares, blizzards, acid rain, magma surges, eruptions, gas + acid pockets, cave-ins),
    /// every biome feature (banded surface, lakes, acid pools, crystal caverns, fungal
    /// groves, volcanoes), every ore findable, and the standard cave enemies. SurfaceTile is
    /// Snow because the blizzard and snow-cap gates key off it; the actual ground cycles
    /// through SurfaceBands. Its core holds no shard — it's a test rig, not a campaign world.</summary>
    public static readonly PlanetDef DebugWorld = new("debug", "Debug",
        "QA test rig - every biome and every disaster at once",
        new Color(225, 110, 200), new Color(255, 190, 245),
        TileKind.Snow,
        LakeMin: 4, LakeExtra: 1, MountainMin: 10, MountainExtra: 3,
        MountainHeightScale: 1.3f, LavaFillFrac: 0.55f, HasWater: true,
        OreBias: new[]
        {
            (TileKind.CoalOre, 0.03f), (TileKind.FuelOre, 0.02f), (TileKind.IronOre, 0.03f),
            // Gold/silver base thresholds are unreachable (charted-rarity metals), so the
            // QA world carries full vein biases — everything must be findable here.
            (TileKind.SilverOre, 0.16f), (TileKind.GoldOre, 0.16f), (TileKind.PlatinumOre, 0.03f),
            (TileKind.Ruby, 0.03f), (TileKind.Sapphire, 0.03f), (TileKind.Crystal, 0.03f),
            (TileKind.Emerald, 0.03f), (TileKind.Diamond, 0.03f), (TileKind.Voidstone, 0.105f),
        },
        QuakeScale: 0.45f, CaveSpawnCap: 20,
        ShipOre: "gold", ShipOreCount: 3,
        OxygenDrainScale: 1.5f, SeedsGas: true, SeedsAcid: true, SeedsOil: true,
        Titan: TitanKind.Godzilla,
        CrystalPockets: 3, FungalPockets: 3,
        SizeScale: 1.8f, LakeScale: 1.4f,
        AcidPools: 3, AcidRain: true,
        Volcanoes: 3, VolcanoScale: 1.1f,
        // One civilisation per planet holds on the QA rig too: it keeps the tower district
        // (city QA), and warren QA runs on slag/core via DM_AUTOSTART + DM_WARREN.
        CityLots: 4,
        SurfaceBands: new[] { TileKind.Grass, TileKind.Snow, TileKind.Gravel, TileKind.Dirt, TileKind.Basalt },
        Biome: "debug", Difficulty: 1f);

    /// <summary>The Hollow: a mega-asteroid on the belt at the edge of the system, big
    /// enough to land on and mine like a planet — but truly airless: no atmosphere in the
    /// sky (space-black from the ground, stars at noon), no air to breathe anywhere (the
    /// suit recycler only tops up slowly under the open sky), and no entry at all until the
    /// Vac Suit is installed (see Upgrades "vacsuit" + SpaceSim's bounce). Quarter gravity,
    /// a meteor-pocked cratered surface, no dirt/lava/water — just cold regolith over rock,
    /// the richest rare-metal chart in the system (platinum signature, gold AND silver
    /// veins, voidstone outside the Rift), and the Great Geode: a vast crystal-lined hollow
    /// at its heart. It holds no core shard — the hoard is the prize — and NOTHING ordinary
    /// lives here: the caves and vacuum sky belong only to the belt natives (Moonlet,
    /// VacLeech, Glimmermaw, VoidBarnacle, StarJelly). The Starspawn — a cosmic octopus —
    /// incubates in an egg buried near the core, guarding the deepest riches.</summary>
    public static readonly PlanetDef HollowWorld = new("hollow", "The Hollow",
        "Mega-asteroid in the outer belt - no air, no gravity to speak of, absurdly rich",
        new Color(138, 132, 122), new Color(212, 202, 182),
        TileKind.Gravel,
        // Small crags only — the Lumpiness lobes ARE this world's topography, and the two
        // must share the fixed sky headroom (lump crest + peak may not clear the grid top).
        LakeMin: 0, LakeExtra: 0, MountainMin: 5, MountainExtra: 3,
        MountainHeightScale: 0.35f, LavaFillFrac: 0f, HasWater: false,
        OreBias: new[]
        {
            (TileKind.PlatinumOre, 0.035f), (TileKind.GoldOre, 0.13f), (TileKind.SilverOre, 0.13f),
            (TileKind.IronOre, 0.03f), (TileKind.Diamond, 0.02f), (TileKind.Emerald, 0.018f),
            (TileKind.Voidstone, 0.105f),
        },
        QuakeScale: 1.0f, CaveSpawnCap: 18,
        ShipOre: "platinum", ShipOreCount: 5,
        // Airless drains have NO surface grace band (see OxygenRules), so the scale itself
        // stays moderate — the vacuum is the hazard, not a doubled multiplier on top of it.
        OxygenDrainScale: 1.5f,
        Titan: TitanKind.CosmicOctopus,
        CrystalPockets: 3,
        SizeScale: 1.5f,
        Biome: "belt", Difficulty: 0.85f,
        GravityScale: 0.25f, Airless: true, Craters: 8, GreatGeode: true, Lumpiness: 40f);

    public static readonly PlanetDef[] Classic =
    {
        new("verdant", "Verdant", "Gentle green start, lakes, moss, iron",
            new Color(90, 150, 80), new Color(150, 210, 130),
            TileKind.Grass,
            LakeMin: 3, LakeExtra: 1, MountainMin: 6, MountainExtra: 3,
            MountainHeightScale: 1.0f, LavaFillFrac: 0.45f, HasWater: true,
            // Gold vein bias: this world is gold-rich, but gold now has a reachable BASE
            // threshold on every world, so the bias only needs to be a modest bump on top —
            // a big bias here floods the crust with gold. Keep it a genuine-but-lean richness.
            OreBias: new[] { (TileKind.GoldOre, 0.05f), (TileKind.IronOre, 0.015f), (TileKind.CoalOre, 0.015f), (TileKind.Emerald, 0.020f) },
            QuakeScale: 1.0f, CaveSpawnCap: 14,
            ShipOre: "gold", ShipOreCount: 3, SeedsOil: true, Titan: TitanKind.Kong,
            CrystalPockets: 1, FungalPockets: 4, Difficulty: 0f),

        new("frost", "Frost", "Frozen wastes, deep water, sapphire seams",
            new Color(150, 180, 220), new Color(220, 235, 255),
            TileKind.Snow,
            LakeMin: 4, LakeExtra: 2, MountainMin: 8, MountainExtra: 3,
            MountainHeightScale: 1.25f, LavaFillFrac: 0.30f, HasWater: true,
            OreBias: new[] { (TileKind.Sapphire, 0.030f), (TileKind.SilverOre, 0.135f), (TileKind.Emerald, 0.015f) },
            QuakeScale: 1.2f, CaveSpawnCap: 16,
            ShipOre: "sapphire", ShipOreCount: 4, OxygenDrainScale: 1.1f, Titan: TitanKind.Sandworm,
            CrystalPockets: 3, FungalPockets: 2, Biome: "frost", Difficulty: 0.2f),

        new("ember", "Ember", "Volcanic furnace, lava high, rubies below",
            new Color(190, 90, 50), new Color(255, 170, 90),
            TileKind.Gravel,
            LakeMin: 0, LakeExtra: 0, MountainMin: 9, MountainExtra: 4,
            MountainHeightScale: 1.4f, LavaFillFrac: 0.62f, HasWater: false,
            OreBias: new[] { (TileKind.Ruby, 0.030f), (TileKind.CoalOre, 0.030f), (TileKind.Obsidian, 0f) },
            QuakeScale: 0.55f, CaveSpawnCap: 18,
            ShipOre: "ruby", ShipOreCount: 4, OxygenDrainScale: 1.35f, SeedsGas: true, Titan: TitanKind.Godzilla,
            // Lizardmen love the heat: the classic chain's warren lives under the lava world.
            CrystalPockets: 2, Volcanoes: 2, VolcanoScale: 1.15f, LizardCities: 1,
            Biome: "ember", Difficulty: 0.4f),

        new("slag", "Slag", "Dead metal world, platinum veins, restless crust",
            new Color(130, 125, 140), new Color(200, 205, 220),
            TileKind.Gravel,
            LakeMin: 0, LakeExtra: 0, MountainMin: 4, MountainExtra: 2,
            MountainHeightScale: 0.7f, LavaFillFrac: 0.35f, HasWater: false,
            OreBias: new[] { (TileKind.PlatinumOre, 0.030f), (TileKind.GoldOre, 0.125f), (TileKind.IronOre, 0.025f) },
            QuakeScale: 0.45f, CaveSpawnCap: 20,
            ShipOre: "platinum", ShipOreCount: 5, OxygenDrainScale: 1.7f, AcidPools: 3,
            SeedsOil: true, Titan: TitanKind.Mecha,
            CrystalPockets: 3, Biome: "slag", Difficulty: 0.6f),

        // The alien metropolis: flat, mild ground under a downtown of clustered alloy
        // towers, their window bands lit and their streets ambled by harmless civilians
        // and peacekeeper patrols. One civilisation per world: the citizens cleared their
        // planet's warrens long ago, so no lizardmen here (and no towers on warren worlds).
        new("city", "Neonspire", "Alien metropolis, glowing towers, timid citizens",
            new Color(70, 110, 150), new Color(150, 230, 240),
            TileKind.Gravel,
            LakeMin: 1, LakeExtra: 1, MountainMin: 3, MountainExtra: 2,
            MountainHeightScale: 0.8f, LavaFillFrac: 0.40f, HasWater: true,
            OreBias: new[] { (TileKind.IronOre, 0.030f), (TileKind.CoalOre, 0.020f), (TileKind.GoldOre, 0.015f) },
            QuakeScale: 1.0f, CaveSpawnCap: 16,
            ShipOre: "gold", ShipOreCount: 4, OxygenDrainScale: 1.05f, Titan: TitanKind.Mecha,
            CrystalPockets: 1, FungalPockets: 1,
            // Enough lots that the downtown districts span roughly a third of the surface.
            CityLots: 34, Biome: "city", Difficulty: 0.4f),

        new("core", "Coreheart", "The finale, diamond-rich, swarming, lava at the door",
            new Color(120, 70, 160), new Color(220, 150, 255),
            TileKind.Basalt,
            LakeMin: 1, LakeExtra: 1, MountainMin: 10, MountainExtra: 4,
            MountainHeightScale: 1.5f, LavaFillFrac: 0.58f, HasWater: true,
            OreBias: new[] { (TileKind.Diamond, 0.030f), (TileKind.Crystal, 0.025f), (TileKind.Ruby, 0.015f) },
            QuakeScale: 0.5f, CaveSpawnCap: 24,
            ShipOre: "diamond", ShipOreCount: 5, OxygenDrainScale: 1.4f, SeedsGas: true, SeedsAcid: true, Titan: TitanKind.Godzilla,
            CrystalPockets: 3, FungalPockets: 2,
            // The breach world: a fresh kaiju rolls out of the pool on every visit, so no two
            // Coreheart dives face the same monster. Titan above stays as the fallback label.
            TitanPool: new[] { TitanKind.Knifehead, TitanKind.Otachi, TitanKind.Leatherback, TitanKind.Raiju },
            Biome: "crystal", Difficulty: 0.8f),

        // The warp world — out of normal flight range, reachable only with all five core
        // shards. Everything is turned up: swarming caves, toxins, near-vacuum air, lava at
        // every depth, and the deadliest titan variant. Escaping it with the titan slain
        // completes the campaign.
        new("rift", "The Rift", "Warp-locked hellworld, everything here wants you dead",
            new Color(85, 25, 40), new Color(255, 90, 70),
            TileKind.Basalt,
            LakeMin: 0, LakeExtra: 0, MountainMin: 12, MountainExtra: 4,
            MountainHeightScale: 1.6f, LavaFillFrac: 0.70f, HasWater: false,
            OreBias: new[] { (TileKind.Diamond, 0.035f), (TileKind.Ruby, 0.030f), (TileKind.PlatinumOre, 0.030f), (TileKind.Voidstone, 0.105f) },
            QuakeScale: 0.35f, CaveSpawnCap: 30,
            ShipOre: "diamond", ShipOreCount: 6, OxygenDrainScale: 2.2f, SeedsGas: true, SeedsAcid: true, Titan: TitanKind.Slattern,
            CrystalPockets: 4, Volcanoes: 2, VolcanoScale: 1.25f, Biome: "rift", Difficulty: 1f),
    };

    static PlanetDefs() => All = Classic;

    /// <summary>Core shards needed to warp — one from every world except the Rift itself
    /// and the bonus destinations: the shard-less Hollow, the cratered moon, and the debug
    /// rig. (The ocean moon still counts — it's a campaign world that happens to orbit
    /// a planet, shard and all.)</summary>
    public static int WarpShardsNeeded
    {
        get
        {
            var n = 0;
            foreach (var d in All) if (d.Id is not ("rift" or "debug" or "hollow" or "moon")) n++;
            return n;
        }
    }

    public static PlanetDef ById(string id)
    {
        foreach (var d in All) if (d.Id == id) return d;
        // Classic ids (verdant, frost, …) stay resolvable while a generated campaign is
        // active — the DM_AUTOSTART/DM_ORBIT tooling hooks and sim tests name them directly.
        foreach (var d in Classic) if (d.Id == id) return d;
        // The fixed extras resolve even when nothing Activated (headless sim tests).
        if (id == "hollow") return HollowWorld;
        if (id == "debug") return DebugWorld;
        return All[0];
    }

    public static int IndexOf(PlanetDef def)
    {
        for (var i = 0; i < All.Length; i++) if (All[i].Id == def.Id) return i;
        return 0;
    }
}
