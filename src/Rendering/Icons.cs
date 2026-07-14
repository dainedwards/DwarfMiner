using System.Collections.Generic;
using DwarfMiner.Entities;
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

    /// <summary>Melee upgrade-rung probe, wired by Game1 at load (reads the live run's
    /// Player.MeleeTiers) — lets slot icons escalate with each upgrade craft.</summary>
    public static System.Func<string, int>? MeleeTierOf;

    /// <summary>Lookup with tier substitution: "pickaxe" maps to "pickaxe_t<tier>", and the
    /// melee ids map to their current upgrade rung's icon.</summary>
    public static Texture2D? GetForSlot(string id, int pickaxeTier)
    {
        if (id == "pickaxe") return Get($"pickaxe_t{System.Math.Clamp(pickaxeTier, 1, 4)}");
        if (System.Array.IndexOf(Toolbelt.MeleeIds, id) >= 0)
            return Get($"{id}_t{System.Math.Clamp(MeleeTierOf?.Invoke(id) ?? 1, 1, 4)}");
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
        _icons["door"]       = BuildDoor(gd);
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
        _icons["tnt_pack"]        = BuildTntPack(gd);
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
        _icons["leather_gloves"] = BuildGloves(gd, new Color(180, 140, 95), new Color(110, 80, 50));
        _icons["iron_gauntlets"] = BuildGloves(gd, ironL, ironD);
        _icons["band_regen"]    = BuildBandRegen(gd);
        _icons["magnet_ring"]   = BuildMagnetRing(gd);
        _icons["miners_charm"]  = BuildMinersCharm(gd);
        _icons["aegis_pendant"] = BuildAegisPendant(gd);
        _icons["flamethrower"]  = BuildFlamethrower(gd);
        _icons["acid_spewer"]   = BuildAcidSpewer(gd);
        _icons["lightning_gun"] = BuildLightningGun(gd);
        _icons["jetpack"]       = BuildJetpack(gd);

        // Melee arsenal: one icon per weapon per upgrade rung (iron → steel → gilded →
        // energy edge). The two-handed versions share their family's silhouette.
        var meleeTiers = new (Color L, Color D, Color E)[]
        {
            (new Color(150, 155, 170), new Color(95, 100, 115), new Color(185, 190, 205)),
            (new Color(205, 210, 225), new Color(135, 140, 155), new Color(240, 244, 252)),
            (new Color(235, 200, 95),  new Color(165, 130, 55),  new Color(255, 240, 170)),
            (new Color(150, 240, 255), new Color(80, 190, 225),  new Color(255, 255, 255)),
        };
        void MeleeIcon(string id, string[] rows)
        {
            for (var t = 0; t < 4; t++)
            {
                var (l, d, e) = meleeTiers[t];
                _icons[$"{id}_t{t + 1}"] = Renderer.BuildSprite(gd, rows, new Dictionary<char, Color>
                {
                    ['.'] = Color.Transparent,
                    ['w'] = new Color(120, 85, 55), ['k'] = new Color(80, 55, 32),
                    ['M'] = l, ['x'] = d, ['E'] = e,
                });
            }
        }
        MeleeIcon("sword", SwordIcon);
        MeleeIcon("great_sword", SwordIcon);
        MeleeIcon("mace", MaceIcon);
        MeleeIcon("great_mace", MaceIcon);
        MeleeIcon("warhammer", HammerIcon);
        MeleeIcon("great_hammer", HammerIcon);
        MeleeIcon("shield", ShieldIcon);
        MeleeIcon("tower_shield", ShieldIcon);

        // Terraria-style finish pass over the whole set: a dark contour outline hugging
        // every sprite plus a top-lit rim and under-shadow. Applied programmatically so
        // all ~60 icons share one consistent look without redrawing each by hand.
        foreach (var key in new List<string>(_icons.Keys))
            _icons[key] = Polish(gd, _icons[key]);

        // Aliases (post-polish so they share the finished texture): upgrade-recipe ids
        // show the item they improve.
        _icons["headlamp_ii"] = _icons["helm_lamp"];
        _icons["headlamp_iii"] = _icons["helm_lamp"];
        _icons["headlamp_iv"] = _icons["helm_lamp"];
        _icons["jetpack_ii"] = _icons["jetpack"];
        _icons["jetpack_iii"] = _icons["jetpack"];
        _icons["jetpack_iv"] = _icons["jetpack"];
        foreach (var mid in Toolbelt.MeleeIds)
        {
            _icons[mid] = _icons[$"{mid}_t1"];
            _icons[$"{mid}_up"] = _icons[$"{mid}_t2"];
        }
    }

    // ───────── melee icon shapes (shared by 1h/2h; palette carries the rung) ─────────

    private static readonly string[] SwordIcon =
    {
        "............E...",
        "...........EME..",
        "..........EMM...",
        ".........MMM....",
        "........MMM.....",
        ".......MMM......",
        "......MMM.......",
        ".....MMM........",
        "..x.MMM.........",
        "..xxMM..........",
        "..xxx...........",
        ".wwxxx..........",
        "www..x..........",
        "ww..............",
        "................",
        "................",
    };

    private static readonly string[] MaceIcon =
    {
        "................",
        "..........E.....",
        ".......E.MMM.E..",
        "........MMMMM...",
        ".......MMxMMM...",
        "........MMMMM...",
        ".......E.MMM.E..",
        "......ww..E.....",
        ".....ww.........",
        "....ww..........",
        "...ww...........",
        "..ww............",
        ".kw.............",
        "................",
        "................",
        "................",
    };

    private static readonly string[] HammerIcon =
    {
        "................",
        "........EMMMME..",
        "........MMMMMM..",
        "........MMxxMM..",
        "........MMMMMM..",
        "........EMMMME..",
        "......ww........",
        ".....ww.........",
        "....ww..........",
        "...ww...........",
        "..ww............",
        ".kw.............",
        "................",
        "................",
        "................",
        "................",
    };

    private static readonly string[] ShieldIcon =
    {
        "................",
        "....EEEEEEEE....",
        "...EMMMMMMMME...",
        "...EMMMMMMMME...",
        "...EMMxMMxMME...",
        "...EMMMMMMMME...",
        "...EMMMMMMMME...",
        "....EMMMMMME....",
        "....EMMxxMME....",
        ".....EMMMME.....",
        ".....EMMMME.....",
        "......EMME......",
        ".......EE.......",
        "................",
        "................",
        "................",
    };

    /// <summary>The high-fidelity finish: (1) 1-px outline in a darkened tint of the
    /// adjacent art wherever a transparent pixel borders an opaque one; (2) opaque pixels
    /// open to the sky (transparent above in the source) get a subtle top-light; (3) pixels
    /// with nothing below get a subtle under-shade. Together they give every icon the
    /// contour + directional-light read of Terraria's item sprites.</summary>
    private static Texture2D Polish(GraphicsDevice gd, Texture2D src)
    {
        var w = src.Width;
        var h = src.Height;
        var data = new Color[w * h];
        src.GetData(data);
        var outp = (Color[])data.Clone();

        bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && data[y * w + x].A > 40;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (data[i].A > 40)
                {
                    // Directional rims read against the fresh outline.
                    if (!Opaque(x, y - 1))
                        outp[i] = Color.Lerp(data[i], Color.White, 0.22f);
                    else if (!Opaque(x, y + 1))
                        outp[i] = Color.Lerp(data[i], Color.Black, 0.18f);
                    continue;
                }
                // Transparent pixel touching art → contour. Tinted from the neighbours so
                // warm sprites get warm-dark outlines, not one flat black everywhere.
                int rSum = 0, gSum = 0, bSum = 0, n = 0;
                void Tap(int tx, int ty)
                {
                    if (!Opaque(tx, ty)) return;
                    var c = data[ty * w + tx];
                    rSum += c.R; gSum += c.G; bSum += c.B; n++;
                }
                Tap(x - 1, y); Tap(x + 1, y); Tap(x, y - 1); Tap(x, y + 1);
                if (n > 0)
                    outp[i] = new Color(rSum / n / 4, gSum / n / 4, bSum / n / 4, 255);
            }
        }
        var tex = new Texture2D(gd, w, h);
        tex.SetData(outp);
        src.Dispose();
        return tex;
    }

    private static Texture2D BuildJetpack(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "....SS....SS....",
        "...SLLS..SLLS...",
        "...SLLS..SLLS...",
        "...SMMSssSMMS...",
        "...SMMS..SMMS...",
        "...SMDS..SMDS...",
        "...SDDS..SDDS...",
        "....nn....nn....",
        "....FF....FF....",
        "....fY....fY....",
        ".....Y.....Y....",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(150, 155, 170),   // twin tank shells
        ['L'] = new Color(210, 215, 230),   // top-lit
        ['M'] = new Color(120, 125, 140),
        ['D'] = new Color(85, 90, 105),
        ['s'] = new Color(100, 105, 120),   // strap bar
        ['n'] = new Color(70, 72, 82),      // nozzles
        ['F'] = new Color(255, 150, 50),    // flame
        ['f'] = new Color(235, 90, 45),
        ['Y'] = new Color(255, 230, 120),
    });

    private static Texture2D BuildFlamethrower(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "..........yY....",
        "..sSSSSSSSSy....",
        "..SsssssssSSF...",
        "..SsssssssSFF...",
        "..sSSSSSSSS.....",
        "...RRRR.GGg.....",
        "...RrrR.GGg.....",
        "...RrrR.Gg......",
        "...RRRR.G.......",
        "....hh..........",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(150, 148, 140),   // burner barrel
        ['s'] = new Color(95, 92, 85),
        ['R'] = new Color(190, 60, 45),     // fuel tank
        ['r'] = new Color(125, 35, 28),
        ['h'] = new Color(80, 78, 72),      // tank hose
        ['G'] = new Color(105, 75, 50),     // grip
        ['g'] = new Color(70, 48, 30),
        ['F'] = new Color(255, 150, 50),    // pilot flame
        ['y'] = new Color(255, 220, 110),
        ['Y'] = new Color(255, 245, 180),
    });

    private static Texture2D BuildAcidSpewer(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "................",
        "...gGGGGg.......",
        "..gGLLLGGgSSS...",
        "..gGLAAAGgsssA..",
        "..gGAAAAGgSSS...",
        "...gGAAGg....a..",
        "....gGGg.....A..",
        ".....DDd........",
        ".....DDd........",
        "....DDd.........",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['G'] = new Color(90, 150, 70),     // reservoir shell
        ['g'] = new Color(55, 95, 45),
        ['L'] = new Color(200, 255, 130),   // glass highlight
        ['A'] = new Color(140, 230, 60),    // acid
        ['a'] = new Color(95, 170, 40),     // drip
        ['S'] = new Color(150, 155, 145),   // spout
        ['s'] = new Color(95, 100, 92),
        ['D'] = new Color(105, 75, 50),     // grip
        ['d'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildLightningGun(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "............w...",
        "..CC.CC.CC.w....",
        ".SSSSSSSSSSw.w..",
        ".SsssssssssWw...",
        ".SSSSSSSSSSw.w..",
        "..CC.CC.CC..w...",
        "....GGg.....v...",
        "....GGg.........",
        "...GGg..........",
        "...GG...........",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['S'] = new Color(120, 130, 160),   // coil housing
        ['s'] = new Color(75, 82, 108),
        ['C'] = new Color(140, 170, 255),   // capacitor rings
        ['W'] = new Color(255, 255, 255),   // arc core
        ['w'] = new Color(190, 180, 255),   // crackling fork
        ['v'] = new Color(120, 100, 220),
        ['G'] = new Color(105, 75, 50),
        ['g'] = new Color(70, 48, 30),
    });

    private static Texture2D BuildGloves(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "..OOO....OOO....",
            ".OLLDO..OLLDO...",
            ".OLLDO..OLLDO...",
            ".OLMDO..OLMDO...",
            ".OLMMOO.OLMMOO..",
            ".OLMMMLO.OLMMLO.",  // thumb pokes out
            ".OLMMMMO.OLMMMO.",
            ".OMMMMDO.OMMMDO.",
            ".OMMMDDO.OMMDDO.",
            "..OOOOO...OOOO..",
            "................",
            "................",
            "................",
            "................",
        }, ArmorPalette(light, dark));

    private static Texture2D BuildBandRegen(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        ".....R..R.......",
        "....RRRRRR......",
        "....RRRRRR......",
        ".....RRRR.......",
        "....S.RR.S......",
        "...S......S.....",
        "...S......S.....",
        "...S......S.....",
        "....S....S......",
        ".....SSSS.......",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(230, 80, 95), ['S'] = new Color(200, 205, 220),
    });

    private static Texture2D BuildMagnetRing(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "................",
        "...WW.....WW....",
        "...WW.....WW....",
        "...RR.....RR....",
        "...RR.....RR....",
        "...RR.....RR....",
        "...RRR...RRR....",
        "....RRRRRRR.....",
        ".....RRRRR......",
        "................",
        "................",
        "................",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(210, 70, 60), ['W'] = new Color(230, 235, 245),
    });

    private static Texture2D BuildMinersCharm(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "....C......C....",
        "...C........C...",
        "...C........C...",
        "....C......C....",
        ".....C....C.....",
        "......CCCC......",
        ".....GGGGGG.....",
        "....GGGYYGGG....",
        "....GGYYYYGG....",
        "....GGYYYYGG....",
        "....GGGYYGGG....",
        ".....GGGGGG.....",
        "......GGGG......",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['C'] = new Color(120, 100, 60),
        ['G'] = new Color(235, 190, 80), ['Y'] = new Color(255, 245, 170),
    });

    private static Texture2D BuildAegisPendant(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "....C......C....",
        "...C........C...",
        "...C........C...",
        "....C......C....",
        ".....C....C.....",
        "......CCCC......",
        "......BBBB......",
        ".....BBLLBB.....",
        ".....BLLLLB.....",
        ".....BBLLBB.....",
        "......BBBB......",
        ".......BB.......",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['C'] = new Color(170, 175, 190),
        ['B'] = new Color(80, 120, 220), ['L'] = new Color(170, 200, 255),
    });

    // ───────── worn-gear builders (character screen) ─────────

    /// <summary>Shared 4-tone palette for the armor colourways: outline, light (top-lit),
    /// mid, and dark (shadow side) — light comes from the upper-left like the tool icons.</summary>
    private static Dictionary<char, Color> ArmorPalette(Color light, Color dark) => new()
    {
        ['.'] = Color.Transparent,
        ['O'] = new Color(dark.R / 2, dark.G / 2, dark.B / 2),
        ['L'] = light,
        ['M'] = Color.Lerp(light, dark, 0.45f),
        ['D'] = dark,
    };

    private static Texture2D BuildHelmet(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "....OOOOOOOO....",
            "...OLLLLLLMDO...",
            "..OLLLLLMMMDDO..",
            "..OLLLLMMMMDDO..",
            "..OLLMMMMMMDDO..",
            "..OMMMOOOOMMDO..",
            "..OMMO....OMDO..",
            "..OMMO.OO.OMDO..",
            "..OOO..OO..OOO..",
            "................",
            "................",
            "................",
            "................",
            "................",
        }, ArmorPalette(light, dark));

    private static Texture2D BuildChestplate(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            ".OO..........OO.",
            "OLLO..OOOO..OMDO",
            "OLLLOOLLMDOOMMDO",
            "OLLLLLOOMDMMMMDO",
            ".OLLLLLMMMMMMDO.",
            ".OLLLLMMMMMMDDO.",
            "..OLLMMOOMMDDO..",
            "..OLLMMMMMMDDO..",
            "..OLMMDMMDMMDO..",
            "..OLMMMMMMMMDO..",
            "...OMMMMMMMDO...",
            "....OOOOOOOO....",
            "................",
            "................",
            "................",
        }, ArmorPalette(light, dark));

    private static Texture2D BuildLeggings(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "..OOOOOOOOOOOO..",
            ".OLLLLLLLMMMDDO.",
            ".OLLLLLMMMMMDDO.",
            ".OLLMMOOOOMMDDO.",
            ".OLMMO....OMDDO.",
            ".OLMMO....OMDDO.",
            ".OLMDO....OMDDO.",
            ".OLMDO....ODDDO.",
            ".OLMDO....ODDDO.",
            ".OOOOO....OOOOO.",
            "................",
            "................",
            "................",
            "................",
        }, ArmorPalette(light, dark));

    private static Texture2D BuildBoots(GraphicsDevice gd, Color light, Color dark) =>
        Renderer.BuildSprite(gd, new[]
        {
            "................",
            "................",
            "................",
            "................",
            "..OOO.....OOO...",
            ".OLMDO...OLMDO..",
            ".OLMDO...OLMDO..",
            ".OLMDO...OLMDO..",
            ".OLMDO...OLMDO..",
            ".OLMDOO..OLMDOO.",
            ".OLMMMDO.OLMMDO.",
            ".OMMDDDO.OMMDDO.",
            ".OOOOOOO.OOOOOO.",
            "................",
            "................",
            "................",
        }, ArmorPalette(light, dark));

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

    private static Texture2D BuildTntPack(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "......y.........",
        ".....y..........",
        "....ww..........",
        "...RRRRRRRRR....",
        "...RrrRrrRrr....",
        "...GGGGGGGGG....",
        "...RRRRRRRRR....",
        "...GGGGGGGGG....",
        "...RrrRrrRrr....",
        "...RRRRRRRRR....",
        "....sssssss.....",
        "....s.....s.....",
        "................",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['R'] = new Color(190, 60, 45),
        ['r'] = new Color(130, 32, 26),
        ['G'] = new Color(120, 120, 100),   // resin straps — the sticky part
        ['s'] = new Color(165, 125, 85),    // carry sling
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

    private static Texture2D BuildDoor(GraphicsDevice gd) => Renderer.BuildSprite(gd, new[]
    {
        "................",
        "....TTTTTTTT....",
        "....TppppppT....",
        "....TpPPPPpT....",
        "....TpPddPpT....",
        "....TpPddPpT....",
        "....TpPPPPpT....",
        "....TppppppT....",
        "....TpPPPPpT....",
        "....TpPPPhpT....",
        "....TpPPPhpT....",
        "....TpPPPPpT....",
        "....TppppppT....",
        "....TTTTTTTT....",
        "................",
        "................",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['T'] = new Color(58, 78, 88),
        ['p'] = new Color(88, 122, 132),
        ['P'] = new Color(108, 146, 156),
        ['d'] = new Color(150, 200, 210),
        ['h'] = new Color(235, 210, 130),
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
