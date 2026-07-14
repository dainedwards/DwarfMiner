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
    private enum Biome { Verdant, Frost, Ember, Slag, Ocean, Acid, Crystal, City }

    public static PlanetDef[] Campaign(int seed)
    {
        var rng = new Random(seed);
        // 7 rolled worlds + the fixed Rift + the cratered moon (hung on a mid-chain host).
        var chain = new PlanetDef[9];

        // Biome per slot: a gentle start, then one guaranteed ocean world in the early-mid
        // band and one guaranteed acid world in the mid-late band (the two new biomes are
        // always somewhere in a campaign); everything else rolls difficulty-banded.
        var biomes = new Biome[7];
        biomes[0] = rng.Next(2) == 0 ? Biome.Verdant : Biome.Ocean;
        var easy = new[] { Biome.Verdant, Biome.Frost, Biome.Ocean };
        var mid = new[] { Biome.Frost, Biome.Ember, Biome.Slag, Biome.Ocean, Biome.City };
        var hard = new[] { Biome.Ember, Biome.Slag, Biome.Acid, Biome.Crystal };
        for (var i = 1; i < 7; i++)
            biomes[i] = (i <= 2 ? easy : i <= 4 ? mid : hard)[rng.Next(i <= 2 ? easy.Length : i <= 4 ? mid.Length : hard.Length)];
        if (Array.IndexOf(biomes, Biome.Ocean) < 0) biomes[1 + rng.Next(2)] = Biome.Ocean;
        if (Array.IndexOf(biomes, Biome.Acid) < 0) biomes[4 + rng.Next(3)] = Biome.Acid;
        // One alien metropolis per campaign. Slot 3 is the only mid slot the ocean (1-2)
        // and acid (4-6) guarantees can never claim, so the city can't stomp either.
        if (Array.IndexOf(biomes, Biome.City) < 0) biomes[3] = Biome.City;

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

        // The metropolis always gets a walking titan: a kaiju stomping through a skyline is
        // the set piece, and a worm slithering under the streets (or a flyer that never
        // lands) wastes it. If a city slot rolled a legless kind, swap with a walker from a
        // non-city slot — the worm/flyer just menaces that world instead. Three of the four
        // always-placed classics walk, so a swap partner is guaranteed.
        for (var slot = 0; slot < 7; slot++)
        {
            if (biomes[slot] != Biome.City || Walks(titans[slot])) continue;
            for (var j = 0; j < 7; j++)
            {
                if (biomes[j] == Biome.City || !Walks(titans[j])) continue;
                (titans[slot], titans[j]) = (titans[j], titans[slot]);
                break;
            }
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

        // Warren guarantee: every campaign hides lizardmen SOMEWHERE. If none of the
        // per-world rolls landed one, bury a warren under the first acid or lava world —
        // the acid guarantee above means one always exists.
        var anyWarren = false;
        foreach (var d in chain[..7]) anyWarren |= d is { LizardCities: > 0 };
        if (!anyWarren)
        {
            for (var slot = 0; slot < 7; slot++)
            {
                if (chain[slot].Biome is not ("acid" or "ember")) continue;
                chain[slot] = chain[slot] with { LizardCities = 1 };
                break;
            }
        }

        // The finale is constant: the warp-locked Rift, exactly as hand-tuned.
        chain[7] = PlanetDefs.Classic[^1];

        // ── Moons ────────────────────────────────────────────────────────────
        // Two kinds fly this system: moons WITH an atmosphere and moons WITHOUT.
        //
        // (1) Ocean moon (atmospheric): when the seed rolled a second water world, the
        // smaller of the two stops pretending to be a planet — it becomes a moon of the
        // biggest world in the chain, seas, air, shard and all. Slot 0 is exempt (the
        // campaign's first landing shouldn't orbit something scarier than itself).
        var oceanCount = 0;
        foreach (var d in chain[..7]) if (d.Biome == "ocean") oceanCount++;
        if (oceanCount >= 2)
        {
            var moonSlot = -1;
            for (var i = 1; i < 7; i++)
                if (chain[i].Biome == "ocean"
                    && (moonSlot < 0 || chain[i].SizeScale < chain[moonSlot].SizeScale))
                    moonSlot = i;
            if (moonSlot > 0)
            {
                var host = 0;
                for (var i = 0; i < 7; i++)
                    if (i != moonSlot && chain[i].SizeScale > chain[host].SizeScale) host = i;
                if (host == moonSlot) host = moonSlot == 0 ? 1 : 0;
                chain[moonSlot] = chain[moonSlot] with
                {
                    MoonOf = chain[host].Id,
                    SizeScale = MathF.Min(chain[moonSlot].SizeScale, 0.8f),
                    Tagline = $"Ocean moon of {chain[host].Name} - all sea, and it holds its air",
                };
            }
        }

        // (2) The cratered moon (airless): every system hangs one dead, meteor-hammered
        // moon on a mid-chain planet — vac-suit gated like the Hollow, low gravity, silver
        // in the rock, and only vacuum-native creatures in its caves. No core shard: like
        // the Hollow it's a bonus destination, not a campaign gate.
        var hostSlot = 1 + rng.Next(5);
        if (chain[hostSlot].MoonOf is not null) hostSlot = hostSlot == 1 ? 2 : 1;
        chain[8] = CraterMoon(rng, chain[hostSlot], NewName(rng, usedNames));
        return chain;
    }

    /// <summary>The airless cratered moon: a small dead satellite pocked by the meteors its
    /// missing atmosphere never burned up. Belt-school world: vac-suit gated, low gravity,
    /// dirtless regolith, space-black sky — and a closed roster of vacuum natives (biome
    /// "moon": moonlets and vac leeches from the belt, plus its own selenites and dust
    /// devils). Silver-signature so the trip pays even without a shard.</summary>
    private static PlanetDef CraterMoon(Random rng, PlanetDef host, string name)
    {
        float J(float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);
        return new PlanetDef("moon", name,
            $"Cratered moon of {host.Name} - dead, airless, hammered by meteors",
            new Color(196, 194, 200), new Color(240, 238, 244),
            TileKind.Gravel,
            LakeMin: 0, LakeExtra: 0, MountainMin: 4, MountainExtra: 2,
            MountainHeightScale: 0.5f, LavaFillFrac: 0f, HasWater: false,
            OreBias: new[]
            {
                (TileKind.SilverOre, 0.14f), (TileKind.IronOre, 0.03f),
                (TileKind.CoalOre, 0.02f), (TileKind.Crystal, 0.02f),
            },
            QuakeScale: 1.0f, CaveSpawnCap: 14,
            ShipOre: "silver", ShipOreCount: 4,
            OxygenDrainScale: 1.3f,   // airless = no grace band; the scale stays gentle
            Titan: TitanKind.Raiju,   // a dash kaiju on a quarter-g moon: blink and it's on you
            CrystalPockets: 2,
            SizeScale: J(0.65f, 0.75f),
            Biome: "moon", Difficulty: 0.5f,
            GravityScale: 0.4f, Airless: true, Craters: 12, Lumpiness: 8f,
            MoonOf: host.Id);
    }

    /// <summary>Build one planet from its biome archetype + ramp inputs.</summary>
    private static PlanetDef Stamp(Random rng, Biome biome, int slot, float difficulty,
        float size, TitanKind titan, string shipOre, int shipOreCount, string name,
        bool goldVein, bool silverVein)
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
        // Gold's base threshold is unreachable, so a gold signature needs the full vein
        // bias (~0.14 lands the effective threshold near where common veins used to sit).
        var oreKind = shipOre switch
        {
            "gold" => TileKind.GoldOre, "sapphire" => TileKind.Sapphire, "ruby" => TileKind.Ruby,
            "platinum" => TileKind.PlatinumOre, _ => TileKind.Diamond,
        };
        var sigBias = oreKind == TileKind.GoldOre ? 0.15f : 0.028f;

        // The rare-metal chart rolled in Campaign, flavoured by biome: frost worlds always
        // run silver in the ice, the slag world's "rich veins" tagline earns it gold odds.
        var rare = new List<(TileKind ore, float bias)>();
        if (goldVein || (biome == Biome.Slag && rng.Next(2) == 0))
            rare.Add((TileKind.GoldOre, J(0.11f, 0.14f)));
        if (silverVein || biome == Biome.Frost)
            rare.Add((TileKind.SilverOre, J(0.12f, 0.15f)));
        (TileKind, float)[] WithRare(params (TileKind ore, float bias)[] biases)
        {
            var all = new List<(TileKind, float)>(biases);
            foreach (var (o, b) in rare)
                if (o != oreKind) all.Add((o, b));
            return all.ToArray();
        }

        // Volcanism: fire worlds always run 2-3 big cones, acid worlds vent vitriol from
        // 1-2 of theirs, and every other biome has a 1-in-4 shot at a lone small one.
        var strayVolcano = rng.Next(4) == 0 ? 1 : 0;
        var strayScale = J(0.5f, 0.7f);

        var def = biome switch
        {
            Biome.City => new PlanetDef(id, name,
                "Alien metropolis - glowing towers above, old warrens below",
                Jit(new Color(70, 110, 150), 16), Jit(new Color(150, 230, 240), 16),
                TileKind.Gravel,
                LakeMin: 1, LakeExtra: 1, MountainMin: 3, MountainExtra: 2,
                MountainHeightScale: J(0.7f, 0.95f), LavaFillFrac: 0.40f, HasWater: true,
                OreBias: WithRare((oreKind, sigBias), (TileKind.IronOre, 0.03f), (TileKind.CoalOre, 0.02f)),
                QuakeScale: quake, CaveSpawnCap: caveCap,
                ShipOre: shipOre, ShipOreCount: shipOreCount, OxygenDrainScale: oxy * 0.95f,
                Titan: titan, CrystalPockets: rng.Next(2), FungalPockets: rng.Next(2),
                SizeScale: size,
                // Enough lots that the districts (towers + streets) span roughly a third of
                // the surface — scaled with planet size so giants get proportional sprawl.
                CityLots: (int)(32 * size) + rng.Next(5),
                Biome: "city", Difficulty: difficulty),

            Biome.Ocean => new(id, name,
                "Ocean world - the land is the exception, pack for the crossings",
                Jit(new Color(52, 96, 150), 18), Jit(new Color(120, 190, 230), 18),
                TileKind.Grass,
                LakeMin: 8, LakeExtra: 3, MountainMin: 3, MountainExtra: 2,
                MountainHeightScale: J(0.8f, 1.1f), LavaFillFrac: 0.35f, HasWater: true,
                OreBias: WithRare((oreKind, sigBias), (TileKind.IronOre, 0.015f), (TileKind.Emerald, 0.018f)),
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
                OreBias: WithRare((oreKind, sigBias), (TileKind.CoalOre, 0.015f)),
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
                OreBias: WithRare((oreKind, sigBias), (TileKind.Emerald, 0.015f)),
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
                OreBias: WithRare((oreKind, sigBias), (TileKind.CoalOre, 0.02f)),
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
                OreBias: WithRare((oreKind, sigBias), (TileKind.IronOre, 0.025f), (TileKind.PlatinumOre, 0.012f)),
                QuakeScale: quake, CaveSpawnCap: caveCap + 2,
                ShipOre: shipOre, ShipOreCount: shipOreCount,
                OxygenDrainScale: oxy * 1.35f, SeedsAcid: rng.Next(2) == 0, SeedsOil: true,
                Titan: titan,
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Biome: "slag",
                Difficulty: difficulty),

            Biome.Crystal => new(id, name,
                "Crystalline world - glittering, swarming, and deeply unkind",
                Jit(new Color(120, 70, 160), 18), Jit(new Color(220, 150, 255), 18),
                TileKind.Basalt,
                LakeMin: 1, LakeExtra: 1, MountainMin: 10, MountainExtra: 4,
                MountainHeightScale: J(1.3f, 1.6f), LavaFillFrac: J(0.5f, 0.6f), HasWater: true,
                OreBias: WithRare((oreKind, sigBias), (TileKind.Crystal, 0.025f), (TileKind.Ruby, 0.015f)),
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
                OreBias: WithRare((oreKind, sigBias), (TileKind.IronOre, 0.015f), (TileKind.Emerald, 0.02f)),
                QuakeScale: quake, CaveSpawnCap: caveCap,
                ShipOre: shipOre, ShipOreCount: shipOreCount, OxygenDrainScale: oxy,
                Titan: titan, CrystalPockets: 1, FungalPockets: 3 + rng.Next(3),
                SizeScale: size, Volcanoes: strayVolcano, VolcanoScale: strayScale, Difficulty: difficulty),
        };

        // Lizardman warrens are creatures of heat and vitriol: only the acid and lava
        // (ember) worlds hide them — never anywhere else, and never a city world (one
        // civilisation per planet). Campaign guarantees at least one warren per chain.
        if (biome is Biome.Acid or Biome.Ember && rng.Next(2) == 0)
            def = def with { LizardCities = 1 };
        return def;
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

    /// <summary>Legged kinds — everything except the slithering Sandworm and the two flyers.</summary>
    private static bool Walks(TitanKind k)
        => k is not (TitanKind.Sandworm or TitanKind.Pyrodactyl or TitanKind.Vitriodactyl);

    private static void Shuffle<T>(Random rng, IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
