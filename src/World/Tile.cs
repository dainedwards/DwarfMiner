using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

public enum TileKind : byte
{
    Sky = 0,
    Dirt = 1,
    Stone = 2,
    HardStone = 3,
    CoalOre = 4,
    IronOre = 5,
    GoldOre = 6,
    Crystal = 7,
    Core = 8,
    Support = 9,
    Grass = 10,
    Snow = 11,
    Gravel = 12,
    MossStone = 13,
    Granite = 14,
    Basalt = 15,
    Obsidian = 16,
    SilverOre = 17,
    PlatinumOre = 18,
    Ruby = 19,
    Sapphire = 20,
    Diamond = 21,
}

public static class Tiles
{
    public static bool IsSolid(TileKind k) => k != TileKind.Sky;

    // Tiles that never fall, even when unsupported.
    public static bool IsAnchored(TileKind k) =>
        k is TileKind.HardStone or TileKind.Core or TileKind.Support or TileKind.Obsidian;

    public static bool IsOre(TileKind k) =>
        k is TileKind.CoalOre or TileKind.IronOre or TileKind.GoldOre or TileKind.Crystal
          or TileKind.SilverOre or TileKind.PlatinumOre
          or TileKind.Ruby or TileKind.Sapphire or TileKind.Diamond;

    public static int Hardness(TileKind k) => k switch
    {
        TileKind.Dirt => 1,
        TileKind.Grass => 1,
        TileKind.Snow => 1,
        TileKind.Gravel => 2,
        TileKind.Stone => 2,
        TileKind.MossStone => 2,
        TileKind.Granite => 3,
        TileKind.Basalt => 4,
        TileKind.Obsidian => 6,
        TileKind.CoalOre => 2,
        TileKind.IronOre => 3,
        TileKind.SilverOre => 4,
        TileKind.GoldOre => 4,
        TileKind.PlatinumOre => 5,
        TileKind.Ruby => 5,
        TileKind.Sapphire => 5,
        TileKind.Diamond => 6,
        TileKind.Crystal => 5,
        TileKind.HardStone => 99,
        TileKind.Core => 999,
        TileKind.Support => 99,
        _ => 0,
    };

    public static Color BaseColor(TileKind k) => k switch
    {
        TileKind.Sky => new Color(20, 24, 38),
        TileKind.Dirt => new Color(110, 70, 40),
        TileKind.Grass => new Color(70, 110, 55),
        TileKind.Snow => new Color(220, 230, 240),
        TileKind.Gravel => new Color(120, 115, 105),
        TileKind.Stone => new Color(95, 95, 105),
        TileKind.MossStone => new Color(80, 100, 80),
        TileKind.Granite => new Color(140, 110, 110),
        TileKind.Basalt => new Color(60, 58, 70),
        TileKind.Obsidian => new Color(28, 24, 38),
        TileKind.HardStone => new Color(60, 60, 70),
        TileKind.CoalOre => new Color(55, 55, 62),
        TileKind.IronOre => new Color(150, 110, 90),
        TileKind.SilverOre => new Color(180, 185, 200),
        TileKind.GoldOre => new Color(170, 140, 70),
        TileKind.PlatinumOre => new Color(200, 215, 220),
        TileKind.Ruby => new Color(160, 40, 60),
        TileKind.Sapphire => new Color(50, 70, 170),
        TileKind.Diamond => new Color(180, 220, 230),
        TileKind.Crystal => new Color(130, 80, 170),
        TileKind.Core => new Color(255, 90, 40),
        TileKind.Support => new Color(150, 110, 70),
        _ => Color.Magenta,
    };

    /// <summary>Bright accent flecks for ore tiles, drawn as small sub-tile speckles.</summary>
    public static Color OreSpeckle(TileKind k) => k switch
    {
        TileKind.CoalOre => new Color(20, 20, 24),
        TileKind.IronOre => new Color(230, 200, 170),
        TileKind.SilverOre => new Color(245, 248, 255),
        TileKind.GoldOre => new Color(255, 230, 110),
        TileKind.PlatinumOre => new Color(255, 255, 255),
        TileKind.Ruby => new Color(255, 120, 140),
        TileKind.Sapphire => new Color(140, 180, 255),
        TileKind.Diamond => new Color(255, 255, 255),
        TileKind.Crystal => new Color(230, 180, 255),
        _ => Color.White,
    };

    // Loot dropped when mined (item id, count). Returns null for nothing.
    public static (string id, int count)? Drop(TileKind k) => k switch
    {
        TileKind.Dirt => ("dirt", 1),
        TileKind.Grass => ("dirt", 1),
        TileKind.Snow => ("snow", 1),
        TileKind.Gravel => ("stone", 1),
        TileKind.Stone => ("stone", 1),
        TileKind.MossStone => ("stone", 1),
        TileKind.Granite => ("stone", 1),
        TileKind.Basalt => ("stone", 1),
        TileKind.Obsidian => ("stone", 2),
        TileKind.HardStone => ("stone", 3),
        TileKind.Support => ("stone", 2),
        TileKind.CoalOre => ("coal", 1),
        TileKind.IronOre => ("iron", 1),
        TileKind.SilverOre => ("silver", 1),
        TileKind.GoldOre => ("gold", 1),
        TileKind.PlatinumOre => ("platinum", 1),
        TileKind.Ruby => ("ruby", 1),
        TileKind.Sapphire => ("sapphire", 1),
        TileKind.Diamond => ("diamond", 1),
        TileKind.Crystal => ("crystal", 1),
        _ => null,
    };
}
