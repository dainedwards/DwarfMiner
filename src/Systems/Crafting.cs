using System.Collections.Generic;
using DwarfMiner.Entities;

namespace DwarfMiner.Systems;

public sealed record Recipe(string Id, string Name, IReadOnlyDictionary<string, int> Cost);

public static class Crafting
{
    public static readonly IReadOnlyList<Recipe> All = new List<Recipe>
    {
        new("pickaxe_ii", "Pickaxe II (+1 mining)",
            new Dictionary<string, int> { ["iron"] = 5 }),
        new("cannon", "Cannon (right-click upgrade)",
            new Dictionary<string, int> { ["iron"] = 3, ["coal"] = 5 }),
        new("support", "Place support beam",
            new Dictionary<string, int> { ["stone"] = 2 }),
        new("rocket_part", "Rocket part",
            new Dictionary<string, int> { ["iron"] = 5, ["gold"] = 3 }),
        new("nuke", "Titan-killing nuke",
            new Dictionary<string, int> { ["crystal"] = 3, ["gold"] = 10 }),
    };

    public static bool CanAfford(Recipe r, Inventory inv)
    {
        foreach (var (id, count) in r.Cost)
            if (inv.Count(id) < count) return false;
        return true;
    }

    public static bool TryPay(Recipe r, Inventory inv)
    {
        if (!CanAfford(r, inv)) return false;
        foreach (var (id, count) in r.Cost) inv.TryConsume(id, count);
        return true;
    }
}
