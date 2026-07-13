using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Procedural campaign generator: every new run rolls 7 planets from a seed (persisted in
/// MetaSave.WorldSeed so the chain survives restarts and only rerolls when a campaign is
/// completed). Each planet is stamped from a biome archetype — the hand-tuned Classic defs
/// plus the ocean and acid-world biomes — with randomized names, palettes and terrain, and a
/// difficulty ramp tied to the slot index (= orbit distance from the start): farther worlds
/// are BIGGER (SizeScale 0.7 → 1.8), thinner-aired, more hazardous, and demand deeper ores.
/// The four classic titans stay farmable (their souls price the foundry) by always landing
/// on four of the seven slots; the other three slots roll the new kaiju and flyers. The Rift
/// is appended unchanged as the fixed finale.
/// </summary>
public static class PlanetGen
{
    private enum Biome { Verdant, Frost, Ember, Slag, Ocean, Acid, Crystal }

    public static PlanetDef[] Campaign(int seed)
    {
        var rng = new Random(seed);
        var chain = new PlanetDef[8];

        // Biome per slot: a gentle start, then one guaranteed ocean world in the early-mid
        // band and one guaranteed acid world in the mid-late band (the two new biomes are
        // always somewhere in a campaign); everything else rolls difficulty-banded.
        var biomes = new Biome[7];
        biomes[0] = rng.Next(2) == 0 ? Biome.Verdant : Biome.Ocean;
        var easy = new[] { Biome.Verdant, Biome.Frost, Biome.Ocean };
        var mid = new[] { Biome.Frost, Biome.Ember, Biome.Slag, Biome.Ocean };
        var hard = new[] { Biome.Ember, Biome.Slag, Biome.Acid, Biome.Crystal };
        for (var i = 1; i < 7; i++)
            biomes[i] = (i <= 2 ? easy : i <= 4 ? mid : hard)[rng.Next(i <= 2 ? easy.Length : i <= 4 ? mid.Length : hard.Length)];
        if (Array.IndexOf(biomes, Biome.Ocean) < 0) biomes[1 + rng.Next(2)] = Biome.Ocean;
        if (Array.IndexOf(biomes, Biome.Acid) < 0) biomes[4 + rng.Next(3)] = Biome.Acid;

        // Titans: the four classic soul kinds land on four random slots (farming stays
        // possible every campaign); the rest roll the kaiju wave + flyers. The acid world
        // prefers the Vitriodactyl and volcanic worlds the Pyrodactyl when they're free.
        var slots = new List<int> { 0, 1, 2, 3, 4, 5, 6 };
        Shuffle(rng, slots);
        var titans = new TitanKind[7];
        var classics = new[] { TitanKind.Kong, TitanKind.Sandworm, TitanKind.Godzilla, TitanKind.Mecha };
        for (var i = 0; i < 4; i++) titans[slots[i]] = classics[i];
        var wave = new List<TitanKind>
        {
            TitanKind.Knifehead, TitanKind.Otachi, TitanKind.Leatherback, TitanKind.Raiju,
            TitanKind.Pyrodactyl, TitanKind.Vitriodactyl,
        };
        for (var i = 4; i < 7; i++)
        {
            var slot = slots[i];
            var pick = biomes[slot] switch
            {
                Biome.Acid when wave.Contains(TitanKind.Vitriodactyl) => TitanKind.Vitriodactyl,
                Biome.Ember when wave.Contains(TitanKind.Pyrodactyl) => TitanKind.Pyrodactyl,
                _ => wave[rng.Next(wave.Count)],
            };
            wave.Remove(pick);
            titans[slot] = pick;
        }

        // Ore demand ramps with the slot — and gates each ore's spawn depth, which the
        // size ramp guarantees exists (deep gems only appear on the big far worlds).
        var shipOre = new[] { "gold", "gold", "sapphire", "ruby", "platinum", "diamond", "diamond" };
        var shipOreCount = new[] { 3, 4, 4, 4, 5, 5, 6 };

        // Rare-metal chart: gold and silver only exist on worlds that carry a vein bias
        // (WorldGen's base thresholds for them are unreachable, like voidstone's). The two
        // gold-signature starts always have gold; beyond that each world rolls its own
        // chart, with at least one silver world guaranteed per campaign, and the frosty and
        // metal-rich biomes favouring silver and gold respectively (rolled in Stamp).
        var goldVein = new bool[7];
        var silverVein = new bool[7];
        for (var i = 0; i < 7; i++)
        {
            goldVein[i] = shipOre[i] == "gold" || rng.Next(4) == 0;
            silverVein[i] = rng.Next(3) == 0;
        }
        silverVein[rng.Next(7)] = true;

        var usedNames = new HashSet<string>();
        for (var i = 0; i < 7; i++)
        {
            var difficulty = i / 6f;
            // Size is difficulty: 30% smaller at the start of the chain to 80% bigger at
            // the end, with a little jitter so no two campaigns ramp identically.
            var size = MathHelper.Clamp(
                MathHelper.Lerp(0.7f, 1.8f, difficulty) + ((float)rng.NextDouble() - 0.5f) * 0.12f,
                0.7f, 1.8f);
            chain[i] = Stamp(rng, biomes[i], i, difficulty, size, titans[i],
                shipOre[i], shipOreCount[i], NewName(rng, usedNames),
                goldVein[i], silverVein[i]);
        }

        // The finale is constant: the warp-locked Rift, exactly as hand-tuned.
        chain[7] = PlanetDefs.Classic[^1];
        return chain;
    }

    /// <summary>Build one planet from its biome archetype + ramp inputs.</summary>
    private static PlanetDef Stamp(Random rng, Biome biome, int slot, float difficulty,
        float size, TitanKind titan, string shipOre, int shipOreCount, string name)
    {
        var id = $"gen{slot}-{name.ToLowerInvariant()}";
        float J(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
        Color Jit(Color c, int amt) => new(
            Math.Clamp(c.R + rng.Next(-amt, amt + 1), 0, 255),
            Math.Clamp(c.G + rng.Next(-amt, amt + 1), 0, 255),
            Math.Clamp(c.B + rng.Next(-amt, amt + 1), 0, 255));
        var oxy = MathHelper.Lerp(1.0f, 2.0f, difficulty) * J(0.9f, 1.1f);
        var caveCap = 12 + (int)(difficulty * 14) + rng.Next(4);
        var quake = MathHelper.Lerp(1.0f, 0.45f, difficulty);
        // Signature-gem bias so the slot's ship ore is actually findable in quantity.
        var oreKind = shipOre switch
        {
            "gold" => TileKind.GoldOre, "sapphire" => TileKind.Sapphire, "ruby" => TileKind.Ruby,
            "platinum" => TileKind.PlatinumOre, _ => TileKind.Diamond,
        };

        // Volcanism: fire worlds always run 2-3 big cones, acid worlds vent vitriol from
        // 1-2 of theirs, and every other biome has a 1-in-4 shot at a lone small one.
        var strayVolcano = rng.Next(4) == 0 ? 1 : 0;
        var strayScale = J(0.5f, 0.7f);

        return biome switch
        {
            Biome.Ocean => new(id, name,
                "Ocean world - the land is the exception, pack for the crossings",
                Jit(new Color(52, 96, 150), 18), Jit(new Color(120, 190, 230), 18),
                TileKind.Grass,
                LakeMin: 8, LakeExtra: 3, MountainMin: 3, MountainExtra: 2,
                MountainHeightScale: J(0.8f, 1.1f), LavaFillFrac: 0.35f, HasWater: true,
                OreBias: new[] { (oreKind, 0.028f), (TileKind.IronOre, 0.015f), (TileKind.Emerald, 0.018f) },
                QuakeScale: quake, CaveSpawnCap: caveCap,
                ShipOre: shipOre, ShipOreCount: shipOreCount, OxygenDrainScale: oxy * 0.95f,
                Titan: titan, FungalPockets: 2 + rng.Next(3),
                SizeScale: size, LakeScale: J(2.6f, 3.4f),
                Volcanoes: strayVolcano, VolcanoScale: strayScale, Biome: "ocean", Difficulty: difficulty),

            Biome.Acid => new(id, name,
                "Acid world - open vitriol pools, and the clouds rain worse",
                Jit(new Color(96, 120, 44), 16), Jit(new Color(170, 220, 70), 16),
                TileKind.Dirt,
                LakeMin: 1, LakeExtra: 1, MountainMin: 6, MountainExtra: 3,
                MountainHeightScale: J(1.0f, 1.3f), LavaFillFrac: 0.42f, HasWater: false,
                OreBias: new[] { (oreKind, 0.028f), (TileKind.CoalOre, 0.015f) },
                QuakeScale: quake, CaveSpawnCap: caveCap + 3,
                ShipOre: shipOre, ShipOreCount: shipOreCount,
                OxygenDrainScale: oxy * 1.15f, SeedsAcid: true, Titan: titan,
                CrystalPockets: 1 + rng.Next(2),
                SizeScale: size, AcidPools: 4 + rng.Next(3), AcidRain: true,
                Volcanoes: 1 + rng.Next(2), VolcanoScale: J(0.75f, 1.05f), VolcanoAcid: true,
                Biome: "acid", Difficulty: difficulty),

            Biome.Frost => new(id, name,
                "Frozen world - blizzards bite anyone caught on the ice",
                Jit(new Color(140, 160, 190), 16), Jit(new Color(220, 235, 255), 12),
                TileKind.Snow,
                LakeMin: 2 + rng.Next(2), LakeExtra: 1, MountainMin: 7, MountainExtra: 3,
                MountainHeightScale: J(1.1f, 1.4f), LavaFillFrac: 0.38f, HasWater: true,
                OreBias: new[] { (oreKind, 0.028f), (TileKind.SilverOre, 0.02f), (TileKind.Emerald, 0.015f) },
                QuakeScale: quake, CaveSpawnCap: caveCap,
                ShipOre: shipOre, ShipOreCount: shipOreCount, OxygenDrainScale: oxy * 1.05f,
                Titan: titan, CrystalPockets: 1 + rng.Next(2), FungalPockets: rng.Next(3),
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Biome: "frost",
                Difficulty: difficulty),

            Biome.Ember => new(id, name,
                "Volcanic world - lava at the door and gas in the deeps",
                Jit(new Color(150, 80, 50), 18), Jit(new Color(240, 140, 80), 18),
                TileKind.Basalt,
                LakeMin: 0, LakeExtra: 1, MountainMin: 8, MountainExtra: 4,
                MountainHeightScale: J(1.2f, 1.5f), LavaFillFrac: J(0.52f, 0.62f), HasWater: false,
                OreBias: new[] { (oreKind, 0.028f), (TileKind.CoalOre, 0.02f) },
                QuakeScale: quake * 0.8f, CaveSpawnCap: caveCap + 2,
                ShipOre: shipOre, ShipOreCount: shipOreCount,
                OxygenDrainScale: oxy * 1.2f, SeedsGas: true, Titan: titan,
                CrystalPockets: rng.Next(3), SizeScale: size,
                Volcanoes: 2 + rng.Next(2), VolcanoScale: J(1.0f, 1.3f), Biome: "ember",
                Difficulty: difficulty),

            Biome.Slag => new(id, name,
                "Dead metal world - thin air, meteor-scarred, rich veins",
                Jit(new Color(110, 105, 118), 14), Jit(new Color(180, 172, 190), 14),
                TileKind.Gravel,
                LakeMin: 0, LakeExtra: 0, MountainMin: 9, MountainExtra: 4,
                MountainHeightScale: J(1.1f, 1.4f), LavaFillFrac: 0.40f, HasWater: false,
                OreBias: new[] { (oreKind, 0.03f), (TileKind.IronOre, 0.025f), (TileKind.PlatinumOre, 0.012f) },
                QuakeScale: quake, CaveSpawnCap: caveCap + 2,
                ShipOre: shipOre, ShipOreCount: shipOreCount,
                OxygenDrainScale: oxy * 1.35f, SeedsAcid: rng.Next(2) == 0, Titan: titan,
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Biome: "slag",
                Difficulty: difficulty),

            Biome.Crystal => new(id, name,
                "Crystalline world - glittering, swarming, and deeply unkind",
                Jit(new Color(120, 70, 160), 18), Jit(new Color(220, 150, 255), 18),
                TileKind.Basalt,
                LakeMin: 1, LakeExtra: 1, MountainMin: 10, MountainExtra: 4,
                MountainHeightScale: J(1.3f, 1.6f), LavaFillFrac: J(0.5f, 0.6f), HasWater: true,
                OreBias: new[] { (oreKind, 0.03f), (TileKind.Crystal, 0.025f), (TileKind.Ruby, 0.015f) },
                QuakeScale: quake * 0.7f, CaveSpawnCap: caveCap + 5,
                ShipOre: shipOre, ShipOreCount: shipOreCount,
                OxygenDrainScale: oxy * 1.25f, SeedsGas: true, SeedsAcid: true, Titan: titan,
                CrystalPockets: 3 + rng.Next(2), FungalPockets: rng.Next(2),
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Biome: "crystal",
                Difficulty: difficulty),

            _ => new(id, name,
                "Living world - gentle green, lakes, moss, iron",
                Jit(new Color(90, 150, 80), 16), Jit(new Color(150, 210, 130), 16),
                TileKind.Grass,
                LakeMin: 3, LakeExtra: 1, MountainMin: 6, MountainExtra: 3,
                MountainHeightScale: J(0.9f, 1.1f), LavaFillFrac: 0.45f, HasWater: true,
                OreBias: new[] { (oreKind, 0.025f), (TileKind.IronOre, 0.015f), (TileKind.Emerald, 0.02f) },
                QuakeScale: quake, CaveSpawnCap: caveCap,
                ShipOre: shipOre, ShipOreCount: shipOreCount, OxygenDrainScale: oxy,
                Titan: titan, CrystalPockets: 1, FungalPockets: 3 + rng.Next(3),
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Difficulty: difficulty),
        };
    }

    /// <summary>Syllable-mash planet names, unique within a campaign. Sticks to A-Z so the
    /// pixel font renders every glyph.</summary>
    private static string NewName(Random rng, HashSet<string> used)
    {
        string[] head = { "Kar", "Vor", "Zel", "Nyx", "Tha", "Grim", "Dur", "Mor", "Ulv", "Bra", "Sol", "Fen", "Ost", "Vel", "Qor" };
        string[] mid = { "a", "o", "u", "e", "ar", "or", "il", "un", "ath", "em" };
        string[] tail = { "ia", "os", "eth", "une", "ara", "ix", "on", "eim", "ur", "is" };
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var name = head[rng.Next(head.Length)]
                + (rng.Next(2) == 0 ? mid[rng.Next(mid.Length)] : "")
                + tail[rng.Next(tail.Length)];
            if (used.Add(name)) return name;
        }
        return $"World{used.Count}";
    }

    private static void Shuffle<T>(Random rng, IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
