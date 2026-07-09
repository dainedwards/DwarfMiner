using System.Collections.Generic;
using DwarfMiner.World;

namespace DwarfMiner.Space;

/// <summary>
/// The mothership's long-range mineral survey: approximate per-planet ore quantities for the
/// M-key system readout. "Approximate" is honest — each planet is generated once with a fixed
/// survey seed and its ore tiles counted, so the numbers carry real world-gen variance against
/// the randomly-seeded world you actually land on. Generation costs a beat per planet, so
/// results are cached for the session (first menu open takes the hit).
/// </summary>
public static class Survey
{
    private static readonly Dictionary<string, (string label, int count)[]> _cache = new();

    // True minable deposits only — bulk terrain like obsidian would drown the list.
    private static readonly (TileKind kind, string label)[] OreKinds =
    {
        (TileKind.CoalOre, "COAL"), (TileKind.IronOre, "IRON"), (TileKind.GoldOre, "GOLD"),
        (TileKind.SilverOre, "SILVER"), (TileKind.PlatinumOre, "PLATINUM"),
        (TileKind.Crystal, "CRYSTAL"), (TileKind.Ruby, "RUBY"), (TileKind.Sapphire, "SAPPHIRE"),
        (TileKind.Diamond, "DIAMOND"), (TileKind.FuelOre, "FUEL"),
    };

    /// <summary>Top ore deposits on this world, biggest first (at most <paramref name="top"/>
    /// entries, zero-count ores omitted).</summary>
    public static (string label, int count)[] For(PlanetDef def, int top = 5)
    {
        if (_cache.TryGetValue(def.Id, out var hit)) return hit;

        var planet = WorldGen.Generate(4242 + PlanetDefs.IndexOf(def), def);
        var counts = new int[OreKinds.Length];
        for (var r = 0; r < Planet.RingCount; r++)
        {
            var n = Planet.TilesAt(r);
            for (var y = 0; y < n; y++)
            {
                var k = planet.Get(r, y);
                for (var i = 0; i < OreKinds.Length; i++)
                    if (OreKinds[i].kind == k) { counts[i]++; break; }
            }
        }

        var found = new List<(string label, int count)>();
        for (var i = 0; i < OreKinds.Length; i++)
            if (counts[i] > 0) found.Add((OreKinds[i].label, counts[i]));
        found.Sort((a, b) => b.count.CompareTo(a.count));
        // The nav core's demanded ore is the planet's signature — always report it, even
        // when commoner deposits would crowd it out of the top slots.
        var signature = def.ShipOre.ToUpperInvariant();
        var sigIdx = found.FindIndex(f => f.label == signature);
        if (found.Count > top)
        {
            var sig = sigIdx >= top ? found[sigIdx] : default;
            found.RemoveRange(top, found.Count - top);
            if (sig.label is not null) found[top - 1] = sig;
        }

        var result = found.ToArray();
        _cache[def.Id] = result;
        return result;
    }
}
