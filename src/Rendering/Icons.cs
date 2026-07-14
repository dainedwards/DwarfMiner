using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Pixel-art icon set for every craftable tool, weapon, and placeable. Built once at game
/// start (<see cref="Build"/>) and looked up by inventory id.
///
/// Each icon is 16×16 with a 1-px transparent border so the image sits cleanly in a 16-px
/// toolbelt slot. The art is deliberately blocky and high-contrast — at 32 px on screen
/// (toolbelt slot is rendered at 32×32) they read as silhouettes, not detailed scenes.
///
/// Pickaxe gets four tier-tinted variants ("pickaxe_t1" … "pickaxe_t4"); the toolbelt
/// indexes the right one based on Player.PickaxeTier so the icon visibly upgrades as the
/// pickaxe levels.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, Texture2D> _icons = new();

    /// <summary>Look up an icon by id. Returns null if the id has no registered icon (HUD
    /// then falls back to a coloured swatch).</summary>
    public static Texture2D? Get(string id) => _icons.TryGetValue(id, out var t) ? t : null;

    /// <summary>Lookup with pickaxe-tier substitution: "pickaxe" maps to "pickaxe_t<tier>"
    /// so the slot reflects the player's current pickaxe level.</summary>
    public static Texture2D? GetForSlot(string id, int pickaxeTier)
    {
        if (id == "pickaxe") return Get($"pickaxe_t{System.Math.Clamp(pickaxeTier, 1, 4)}");
        return Get(id);
    }

    public static void Build(GraphicsDevice gd)
    {
        // Per-tier pickaxe — wood handle stays the same, head colour escalates from iron
        // (T1) → silver (T2) → platinum (T3) → diamond (T4). Same silhouette so the "pickaxe"
        // identity is preserved while the upgrade reads at a glance.
        _icons["pickaxe_t1"] = BuildPickaxe(gd, new Color(170, 170, 180), new Color(110, 110, 120));
        _icons["pickaxe_t2"] = BuildPickaxe(gd, new Color(220, 225, 240), new Color(150, 155, 175));
        _icons["pickaxe_t3"] = BuildPickaxe(gd, new Color(230, 240, 250), new Color(170, 180, 200));
        _icons["pickaxe_t4"] = BuildPickaxe(gd, new Color(220, 245, 255), new Color(140, 200, 230));

        _icons["drill"]      = BuildDrill(gd);
        _icons["hammer"]     = BuildHammer(gd);
        _icons["cannon"]     = BuildCannon(gd);
        _icons["bullets"]    = BuildBullets(gd);
        _icons["blocks"]     = BuildBlocks(gd);
        _icons["nuke"]       = BuildNuke(gd);
        _icons["harpoon"]    = BuildHarpoon(gd);
        _icons["dynamite"]   = BuildDynamite(gd);
        _icons["poultice"]   = BuildPoultice(gd);
        _icons["feast"]      = BuildFeast(gd);
        _icons["core_drill"] = BuildCoreDrill(gd);
        _icons["sentry"]     = BuildSentry(gd);
        _icons["beacon"]     = BuildBeacon(gd);
        _icons["glowshroom"] = BuildGlowshroom(gd);
        _icons["ladder"]     = BuildLadder(gd);
        _icons["rail"]       = BuildRail(gd);
        _icons["support"]    = BuildSupport(gd);
        _icons["reinforced_support"] = BuildReinforcedSupport(gd);
        _icons["pistol"]          = BuildPistol(gd);
        _icons["machine_gun"]     = BuildMachineGun(gd);
        _icons["laser"]           = BuildLaser(gd);
        _icons["laser_cannon"]    = BuildLaserCannon(gd);
        _icons["mining_laser"]    = BuildMiningLaser(gd);
        _icons["rocket_launcher"] = BuildRocketLauncher(gd);
        _icons["rocket"]          = BuildRocket(gd);
        _icons["tnt"]             = BuildTnt(gd);
        _icons["ammo_silver"]   = BuildAmmo(gd, new Color(220, 225, 240), new Color(150, 155, 170));
        _icons["ammo_ruby"]     = BuildAmmo(gd, new Color(255, 110, 90), new Color(160, 30, 40));
        _icons["ammo_sapphire"] = BuildAmmo(gd, new Color(140, 180, 255), new Color(40, 70, 160));
        _icons["ammo_diamond"]  = BuildAmmo(gd, new Color(230, 245, 255), new Color(140, 200, 230));
        _icons["rocket_part"]   = BuildRocketPart(gd);

        // Worn gear for the character screen — armor pieces in iron (steel-grey) and chitin
        // (carapace-green) colourways, plus the carried-light ladder.
        var ironL = new Color(195, 200, 215); var ironD = new Color(120, 125, 145);
        var chitL = new Color(125, 150, 105); var chitD = new Color(70, 90, 60);
        _icons["armor"]           = BuildChestplate(gd, ironL, ironD);
        _icons["iron_helmet"]     = BuildHelmet(gd, ironL, ironD);
        _icons["iron_leggings"]   = BuildLeggings(gd, ironL, ironD);
        _icons["iron_boots"]      = BuildBoots(gd, ironL, ironD);
        _icons["chitin_armor"]    = BuildChestplate(gd, chitL, chitD);
        _icons["chitin_helmet"]   = BuildHelmet(gd, chitL, chitD);
        _icons["chitin_leggings"] = BuildLeggings(gd, chitL, chitD);
        _icons["chitin_boots"]    = BuildBoots(gd, chitL, chitD);
        _icons["torch"]       = BuildTorch(gd);
        _icons["lantern"]     = BuildLantern(gd);
        _icons["helm_lamp"]   = BuildHelmLamp(gd);
        _icons["sun_crystal"] = BuildSunCrystal(gd);
    }

    // ───────── worn-gear builders (character screen) ─────────

    private static Texture2D BuildHelmet(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "................",
            ".....SSSSSS.....",
            "....SSSSSSSS....",
            "...SSSSSSSSSS...",
            "...SSSSSSSSSS...",
            "...SSssssssSS...",
            "...SS......SS...",
            "...SS......SS...",
            "...ss......ss...",
            "................",
            "................",
            "................",
            "................",
            "................",
        }, new Dictionary<char, Color>
        { ['.'] = Color.Transparent, ['S'] = light, ['s'] = dark });

    private static Texture2D BuildChestplate(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "..ss........ss..",
            ".sSSs......sSSs.",
            ".sSSSSSSSSSSSSs.",
            ".sSSSSSSSSSSSSs.",
            "..sSSSssssSSSs..",
            "..sSSSSSSSSSSs..",
            "..sSSSSSSSSSSs..",
            "..sSSSssssSSSs..",
            "..sSSSSSSSSSSs..",
            "...sSSSSSSSSs...",
            "....ssssssss....",
            "................",
            "................",
            "................",
        }, new Dictionary<char, Color>
        { ['.'] = Color.Transparent, ['S'] = light, ['s'] = dark });

    private static Texture2D BuildLeggings(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "................",
            "...SSSSSSSSSS...",
            "...SSSSSSSSSS...",
            "...SSSssssSSS...",
            "...SSS....SSS...",
            "...SSS....SSS...",
            "...SSS....SSS...",
            "...SSS....SSS...",
            "...SSS....SSS...",
            "...sss....sss...",
            "................",
            "................",
            "................",
            "................",
        }, new Dictionary<char, Color>
        { ['.'] = Color.Transparent, ['S'] = light, ['s'] = dark });

    private static Texture2D BuildBoots(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "................",
            "................",
            "................",
            "...SS.....SS....",
            "...SS.....SS....",
            "...SS.....SS....",
            "...SS.....SS....",
            "...SSs....SSs...",
            "...SSSs...SSSs..",
            "...ssss...ssss..",
            "................",
            "................",
            "................",
            "................",
        }, new Dictionary<char, Color>
        { ['.'] = Color.Transparent, ['S'] = light, ['s'] = dark });

    private static Texture2D BuildTorch(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "......YY........",
        ".....YFFY.......",
        ".....FFOF.......",
        ".....OFFO.......",
        "......OO........",
        "......GG........",
        "......Gg........",
        "......Gg........",
        "......Gg........",
        "......Gg........",
        "......gg........",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['Y'] = new Color(255, 240, 150), ['F'] = new Color(255, 170, 60),
        ['O'] = new Color(220, 110, 40),
        ['G'] = new Color(120, 85, 50), ['g'] = new Color(80, 55, 32),
    });

    private static Texture2D BuildLantern(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......ss........",
        ".....s..s.......",
        ".....ssss.......",
        "....sSSSSs......",
        "....SYYYYS......",
        "....SYFFYS......",
        "....SYFFYS......",
        "....SYYYYS......",
        "....sSSSSs......",
        ".....ssss.......",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(150, 155, 170), ['s'] = new Color(95, 100, 115),
        ['Y'] = new Color(255, 230, 140), ['F'] = new Color(255, 180, 80),
    });

    private static Texture2D BuildHelmLamp(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "....SSSSSS......",
        "...SSSSSSSS.....",
        "...SSSYYSSS..bb.",
        "...SSSYYSSS.bbb.",
        "...SSSSSSSS..bb.",
        "...Sssssss......",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(200, 175, 90), ['s'] = new Color(130, 110, 60),
        ['Y'] = new Color(255, 250, 200), ['b'] = new Color(255, 245, 170, 160),
    });

    private static Texture2D BuildSunCrystal(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "......ww........",
        ".....wWWw.......",
        "....wWWWWw......",
        "...wWWYYWWw.....",
        "...wWWYYWWw.....",
        "....wWWWWw......",
        ".....wWWw.......",
        "......ww........",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(255, 250, 215), ['w'] = new Color(235, 210, 140),
        ['Y'] = new Color(255, 255, 255),
    });

    // ───────── per-icon builders ─────────

    private static Texture2D BuildPistol(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "................",
        "..SSSSSSSSSS....",
        "..SsssssssssS...",
        "..SSSSSSSSSS....",
        "......GGg.......",
        "......GGg.......",
        ".....GGg........",
        ".....GG.........",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(185, 190, 205),
        ['s'] = new Color(120, 125, 140),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildMachineGun(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "..SSSSSSSSSSSS..",
        "..SsssssssssssS.",
        "..SSSSSSSSSSSS..",
        "....GGg..MMm....",
        "....GGg..MMm....",
        "...GGg..........",
        "...GG...........",
        "................",
        "..y.y.y.........",
        ".y.y.y..........",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(150, 158, 175),
        ['s'] = new Color(95, 102, 118),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
        ['M'] = new Color(120, 126, 140),
        ['m'] = new Color(80, 85, 98),
        ['y'] = new Color(230, 195, 90),
    });

    private static Texture2D BuildLaser(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "..MMMMMMM.......",
        "..MmmmmmMCC.....",
        "..MMMMMMMCCrrrr.",
        "..MmmmmmMCC.....",
        "..MMMMMMM.......",
        ".....GGg........",
        ".....GGg........",
        "....GGg.........",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['M'] = new Color(170, 175, 195),
        ['m'] = new Color(110, 115, 135),
        ['C'] = new Color(255, 130, 130),
        ['r'] = new Color(255, 80, 80),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildFeast(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "....MMMM........",
        "...MMMMMM.......",
        "...MMmMMM.......",
        "...MMMMMM.......",
        "....MMMM........",
        "......BB........",
        ".......BB.......",
        "........BB......",
        ".........WW.....",
        "........WWWW....",
        ".........WW.....",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['M'] = new Color(205, 110, 80),
        ['m'] = new Color(160, 70, 55),
        ['B'] = new Color(225, 200, 160),
        ['W'] = new Color(245, 235, 220),
    });

    private static Texture2D BuildLaserCannon(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".MMMMMMMMMM.....",
        ".MmmmmmmmmMCC...",
        ".MmmCCCCmmMCCbbb",
        ".MmmmmmmmmMCC...",
        ".MMMMMMMMMM.....",
        ".....GGg........",
        ".....GGg........",
        "....GGg.........",
        "....GG..........",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['M'] = new Color(170, 185, 205),
        ['m'] = new Color(105, 120, 145),
        ['C'] = new Color(120, 225, 255),
        ['b'] = new Color(80, 200, 255),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildMiningLaser(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".MMMMMMMMM......",
        ".MmmmmmmmMOO....",
        ".MmmYYYmmMOOrrrw",
        ".MmmmmmmmMOO....",
        ".MMMMMMMMM......",
        ".....GGg........",
        ".....GGg........",
        "....GGg.........",
        "....GG..........",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['M'] = new Color(175, 170, 160),
        ['m'] = new Color(115, 110, 100),
        ['Y'] = new Color(230, 195, 90),
        ['O'] = new Color(255, 160, 60),
        ['r'] = new Color(255, 130, 30),
        ['w'] = new Color(255, 235, 190),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildRocketLauncher(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "..TTTTTTTTTTTT..",
        ".TtttttttttttTT.",
        ".TtttttttttttTT.",
        "..TTTTTTTTTTTT..",
        ".......GGg......",
        ".......GGg......",
        "......GGg.......",
        "......GG........",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['T'] = new Color(96, 110, 88),
        ['t'] = new Color(60, 72, 55),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildRocket(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "................",
        "......RR........",
        ".....RRRR.......",
        "....SSSSSS......",
        "....SssssS......",
        "....SssssS......",
        "....SSSSSS......",
        "...F.SSSS.F.....",
        "..FF..ff..FF....",
        "......ff........",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(200, 80, 60),
        ['S'] = new Color(185, 190, 205),
        ['s'] = new Color(125, 130, 148),
        ['F'] = new Color(150, 60, 45),
        ['f'] = new Color(255, 170, 70),
    });

    private static Texture2D BuildTnt(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......y.........",
        ".....y..........",
        "....ww..........",
        "...RRRRRRRRR....",
        "...RrrRrrRrr....",
        "...RRRRRRRRR....",
        "...BBBBBBBBB....",
        "...RRRRRRRRR....",
        "...RrrRrrRrr....",
        "...RRRRRRRRR....",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(190, 50, 45),
        ['r'] = new Color(130, 28, 26),
        ['B'] = new Color(95, 70, 45),
        ['w'] = new Color(200, 190, 170),
        ['y'] = new Color(255, 225, 120),
    });

    private static Texture2D BuildPickaxe(GraphicsDevice gd, Color head, Color headDark) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        ".....HH..HH.....",
        "....HHddddHH....",
        "....HddddddH....",
        "...HddddddddH...",
        "....HddddddH....",
        "......WWWW......",
        ".....WWww.......",
        "....WWww........",
        "...WWww.........",
        "..WWww..........",
        ".WWww...........",
        ".Www............",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['H'] = head,
        ['d'] = headDark,
        ['W'] = new Color(110, 75, 45),
        ['w'] = new Color(70, 45, 25),
    });

    private static Texture2D BuildDrill(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "....SSSSSS......",
        "...SyyyyySs.....",
        "...SyYYYySs.....",
        "...SyyyyySs.....",
        "....SSSSSs......",
        "......BB........",
        "....bBBBBb......",
        "...bBBBBBBb.....",
        "....BBBBBB......",
        ".....BBBB.......",
        "......BB........",
        ".......b........",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(220, 225, 230),
        ['s'] = new Color(110, 110, 115),
        ['Y'] = new Color(255, 220, 90),
        ['y'] = new Color(220, 170, 60),
        ['B'] = new Color(155, 155, 160),
        ['b'] = new Color(80, 80, 90),
    });

    private static Texture2D BuildHammer(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "...HHHHHHHH.....",
        "..HhhhhhhhhH....",
        "..HhhhhhhhhH....",
        "..HHHHHHHHHH....",
        "....WWww........",
        "....WWww........",
        "....WWww........",
        "....WWww........",
        "....WWww........",
        "....WWww........",
        "....WWww........",
        "...kWWwwk.......",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['H'] = new Color(180, 185, 195),
        ['h'] = new Color(220, 225, 235),
        ['W'] = new Color(110, 75, 45),
        ['w'] = new Color(70, 45, 25),
        ['k'] = new Color(40, 30, 20),
    });

    private static Texture2D BuildCannon(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".....BBBBBBBB...",
        "....BbbbbbbbBb..",
        "...BbbbbbbbbBb..",
        "...BbBBBBBBbBb..",
        "...BbBoooooBbb..",
        "...BbBBBBBBbBb..",
        "...BbbbbbbbbBb..",
        "....BbbbbbbbBb..",
        ".....BBBBBBBB...",
        "...kk.....kk....",
        "..kk.......kk...",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['B'] = new Color(80, 75, 70),
        ['b'] = new Color(50, 45, 40),
        ['o'] = new Color(20, 18, 18),
        ['k'] = new Color(110, 75, 45),
    });

    private static Texture2D BuildBullets(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "....YY..........",
        "...YYYy.........",
        "...YYYy.........",
        "....yy..........",
        "................",
        "..........YY....",
        ".........YYYy...",
        ".........YYYy...",
        "..........yy....",
        ".....YY.........",
        "....YYYy........",
        "....YYYy........",
        ".....yy.........",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['Y'] = new Color(255, 220, 110),
        ['y'] = new Color(180, 140, 40),
    });

    private static Texture2D BuildBlocks(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "..SSSSSSSSSS....",
        ".SsssssssssSb...",
        ".SshhsshhssSb...",
        ".SssssssssSbb...",
        ".SsshsshsssSb...",
        ".SshsssssshSb...",
        ".SssshssshssSb..",
        ".SssssssssssSb..",
        "..SSSSSSSSSSb...",
        "...bbbbbbbbb....",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(120, 120, 130),
        ['s'] = new Color(95, 95, 105),
        ['h'] = new Color(70, 70, 80),
        ['b'] = new Color(40, 40, 48),
    });

    private static Texture2D BuildNuke(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......PP........",
        ".....PpppP......",
        "....PpoooopP....",
        "...PpoMMMoopP...",
        "..PpoMMMMMoopP..",
        "..PpoMMMMMoopP..",
        "..PpoMMMMMoopP..",
        "..PpooooooopP...",
        "..PppppppppP....",
        "...PPPPPPPP.....",
        "....KKKKKK......",
        ".....KKKK.......",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['P'] = new Color(255, 80, 200),
        ['p'] = new Color(180, 40, 130),
        ['o'] = new Color(60, 25, 60),
        ['M'] = new Color(255, 180, 240),
        ['K'] = new Color(40, 30, 30),
    });

    private static Texture2D BuildHarpoon(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "..S.............",
        "..SS............",
        "..SSS...........",
        "..SSSSwwwwwwww..",
        "..SSSSwWWWWWWw..",
        "..SSSSwwwwwwww..",
        "..SSS...........",
        "..SS............",
        "..S.............",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(220, 220, 230),
        ['W'] = new Color(170, 130, 80),
        ['w'] = new Color(110, 80, 50),
    });

    private static Texture2D BuildDynamite(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".......YY.......",
        "......YyY.......",
        ".....YY.........",
        ".....YY.........",
        "....RRRRR.......",
        "....RrrrR.......",
        "....RrwrR.......",
        "....RrwrR.......",
        "....RrwrR.......",
        "....RrwrR.......",
        "....RrrrR.......",
        "....RRRRR.......",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(180, 50, 60),
        ['r'] = new Color(120, 30, 40),
        ['w'] = new Color(220, 220, 220),
        ['Y'] = new Color(255, 220, 90),
        ['y'] = new Color(255, 160, 50),
    });

    private static Texture2D BuildPoultice(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......WW........",
        "......WW........",
        ".....WGGW.......",
        "....WGGGGW......",
        "....WGgGgW......",
        "....WGGGGW......",
        "....WGgGgW......",
        "....WGGGGW......",
        "....WGgGgW......",
        "....WGGGGW......",
        "....WWWWWW......",
        ".....bbbb.......",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(220, 230, 220),
        ['G'] = new Color(110, 200, 110),
        ['g'] = new Color(70, 150, 80),
        ['b'] = new Color(80, 60, 40),
    });

    private static Texture2D BuildCoreDrill(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "...sSSSSSSss....",
        "..SyyyyyyyySs...",
        "..SyDDDDDDySs...",
        "..SyDDddDDySs...",
        "..SyDDddDDySs...",
        "..SyDDDDDDySs...",
        "..SyyyyyyyySs...",
        "...sSSSSSSss....",
        "....BBBBBBB.....",
        ".....BBBBB......",
        "......BBB.......",
        ".......B........",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(220, 240, 250),
        ['s'] = new Color(110, 140, 160),
        ['y'] = new Color(255, 220, 100),
        ['D'] = new Color(180, 220, 250),
        ['d'] = new Color(120, 180, 230),
        ['B'] = new Color(140, 110, 70),
    });

    private static Texture2D BuildSentry(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "......BBBB......",
        ".....BbbbbB.....",
        "....BBbbbbBB....",
        "....bb....bb....",
        "...bbbBBBBbbbb..",
        "...bb..yy....b..",
        "...bbbBBBBbbb...",
        "....bb....bb....",
        "....BBBBBBBB....",
        ".....bbbbbb.....",
        "....k........k..",
        "...kk........kk.",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['B'] = new Color(140, 130, 110),
        ['b'] = new Color(80, 70, 60),
        ['y'] = new Color(255, 200, 100),
        ['k'] = new Color(110, 75, 45),
    });

    private static Texture2D BuildBeacon(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        ".......PP.......",
        "......PpP.......",
        "......PpP.......",
        ".....PPpPP......",
        ".....PpppP......",
        ".....PpwpP......",
        ".....PpppP......",
        "......PpP.......",
        ".....KKKKKK.....",
        "....KkkkkkkK....",
        "....KkkkkkkK....",
        "....KKKKKKKK....",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['P'] = new Color(170, 110, 230),
        ['p'] = new Color(120, 70, 180),
        ['w'] = new Color(255, 230, 255),
        ['K'] = new Color(60, 50, 80),
        ['k'] = new Color(40, 30, 50),
    });

    private static Texture2D BuildGlowshroom(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "....GGGGGG......",
        "...GgggggGG.....",
        "..GgGggGggGG....",
        "..GggggggggG....",
        "...GggggggGG....",
        "....SSSSSS......",
        "....WWSSWWW.....",
        ".....WSSWW......",
        ".....WSSW.......",
        ".....bbbb.......",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['G'] = new Color(120, 220, 140),
        ['g'] = new Color(70, 160, 90),
        ['S'] = new Color(220, 220, 200),
        ['W'] = new Color(150, 150, 130),
        ['b'] = new Color(70, 60, 40),
    });

    private static Texture2D BuildLadder(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "...WW......WW...",
        "...Ww......Ww...",
        "...WwwwwwwwWw...",
        "...Ww......Ww...",
        "...Ww......Ww...",
        "...WwwwwwwwWw...",
        "...Ww......Ww...",
        "...Ww......Ww...",
        "...WwwwwwwwWw...",
        "...Ww......Ww...",
        "...Ww......Ww...",
        "...WwwwwwwwWw...",
        "...Ww......Ww...",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(180, 130, 75),
        ['w'] = new Color(140, 95, 55),
    });

    private static Texture2D BuildRail(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        ".SSSSSSSSSSSSSS.",
        ".sssssssssssss..",
        "................",
        "..wwwwwwwwwwww..",
        "..wwwwwwwwwwww..",
        "................",
        ".SSSSSSSSSSSSSS.",
        ".sssssssssssss..",
        "................",
        "..wwwwwwwwwwww..",
        "..wwwwwwwwwwww..",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(190, 195, 210),
        ['s'] = new Color(110, 115, 130),
        ['w'] = new Color(95, 65, 45),
    });

    private static Texture2D BuildSupport(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".....WWWWWW.....",
        ".WWWWWWWWWWWWWW.",
        ".WwwwWwwwwWwwwW.",
        ".WWWWWWWWWWWWWW.",
        ".....WwwwwW.....",
        ".....WWwwWW.....",
        ".....WwwwwW.....",
        ".....WwwwwW.....",
        ".....WwwwwW.....",
        ".WWWWWWWWWWWWWW.",
        ".WwwwwwwwwwwwwW.",
        ".WWWWWWWWWWWWWW.",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(150, 110, 70),
        ['w'] = new Color(95, 70, 45),
    });

    private static Texture2D BuildReinforcedSupport(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".....WWWWWW.....",
        ".IIIIIIIIIIIIII.",
        ".IiiiiiiiiiiiiI.",
        ".IIIIIIIIIIIIII.",
        ".....WwwwwW.....",
        ".....WWwwWW.....",
        ".....WwwwwW.....",
        ".....WwwwwW.....",
        ".....WwwwwW.....",
        ".IIIIIIIIIIIIII.",
        ".IiiiiiiiiiiiiI.",
        ".IIIIIIIIIIIIII.",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['I'] = new Color(170, 175, 185),
        ['i'] = new Color(95, 100, 110),
        ['W'] = new Color(150, 110, 70),
        ['w'] = new Color(95, 70, 45),
    });

    private static Texture2D BuildAmmo(GraphicsDevice gd, Color tip, Color tipDark) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "......TT........",
        ".....TttT.......",
        "....TtTTtT......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BbbbBb......",
        "....BBBBBb......",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['T'] = tip,
        ['t'] = tipDark,
        ['B'] = new Color(190, 160, 90),
        ['b'] = new Color(120, 95, 50),
    });

    private static Texture2D BuildRocketPart(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......WW........",
        ".....WWWW.......",
        "....WWWWWW......",
        "....WSSSSWW.....",
        "....WSooSSW.....",
        "....WSooSSW.....",
        "....WSSSSSW.....",
        "....WSSSSSW.....",
        "....WSSSSSW.....",
        "....WWWWWWW.....",
        "....RRRRRR......",
        "....rrrrrr......",
        "....rrrrrr......",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(190, 200, 220),
        ['S'] = new Color(140, 160, 200),
        ['o'] = new Color(60, 80, 130),
        ['R'] = new Color(255, 150, 80),
        ['r'] = new Color(180, 80, 40),
    });
}
