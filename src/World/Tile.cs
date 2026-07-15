using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

public enum TileKind : byte
{
    Sky = 0,
    Dirt = 1,
    Stone = 2,
    PlanetCore = 3,
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
    // Volatile mineral refined into rocket fuel — mined, not crafted.
    FuelOre = 27,
    // Phase-11 rare gems: emerald seams on the living worlds, voidstone only in the Rift.
    Emerald = 28,
    Voidstone = 29,
    /// <summary>Compacted debris: loose grains (sand/dirt/gravel/tile dust) that sat buried
    /// and undisturbed long enough re-form into this soft mixed rock. Its exact cell makeup
    /// lives in Planet's composition side table and spills back out when the tile breaks, so
    /// dust value is conserved through compaction. See Cells' compaction sweep.</summary>
    Conglomerate = 30,
    // City-world architecture: alien skyscraper hull plating and its glowing window glass.
    // Both are anchored (engineered structures don't cave in) but mineable, so a dwarf can
    // break into an apartment the hard way. Values append so RunSave's byte cast stays valid.
    AlienAlloy = 31,
    CityGlass = 32,
    // Carved masonry of the underground lizardmen cities — scaled sandstone brick.
    LizardBrick = 33,
    // Craftable pop-open door: Closed blocks and seals, Open is walk-through. City towers
    // are built with them and the aliens work the latch themselves.
    DoorClosed = 34,
    DoorOpen = 35,
    // Alien apartment furniture — decorative, a little funny, and satisfyingly smashable.
    AlienPlant = 36,   // tentacled houseplant in a pot
    HoverPod = 37,     // levitating egg-chair
    OrbLamp = 38,      // glowing lamp orb on a squiggle stand
    // Biome flora — surface plants unique to each world type, scattered by WorldGen. All
    // are anchored (so acid never eats them) and fire-proof (so the ember bloom survives
    // its own lava world). Decorative: passable, break to nothing.
    Fernleaf = 39,     // verdant/living — lush green fronds
    Frostcap = 40,     // frost — pale ice bloom
    Emberbloom = 41,   // ember — charred stalk with glowing fire buds (fire/lava-proof)
    Rustbramble = 42,  // slag — oxidised metal thorn bush
    Vitrilily = 43,    // acid — bulbous acid-adapted pod (acid-proof)
    Geobloom = 44,     // crystal — faceted crystalline flower
    // Crafted base-building blocks — neat "built" tiles (vs raw mined rock), for making
    // proper bases. Solid, mineable, drop themselves back.
    Brick = 45,        // tidy stone masonry
    Plating = 46,      // riveted iron wall panel
    GlassBlock = 47,   // clear pane — a window block
    Platform = 49,     // thin ledge you stand on but jump/drop through (one-way, anchored)
    // Alien trees & water plants — WorldGen scatters them on every world that grows life
    // (none on airless rock). Trunks are chopped for WOOD; canopies are passable foliage;
    // seafronds sway in the shallows.
    TreeTrunk = 50,    // chop it → wood (felling the base topples the crown to dust)
    TreeCanopy = 51,   // leafy foliage (passable, sheds foliage dust when felled)
    TreeCanopy2 = 52,  // a second canopy tone for variety / other biomes
    SeaFrond = 53,     // waving water plant rooted on the lakebed
    TreeRoot = 54,     // underground root — survives felling and regrows the tree (watered by rain)
    Chest = 55,        // lizard-warren treasure chest — press E to loot gold / rare materials
    ChestOpen = 56,    // an already-looted chest (lid thrown back, empty)
    LilyPad = 57,      // alien lily pad floating on a lake surface (anchored, passable flora)
    Rope = 58,         // deployed rope line — climbable like a ladder, hangs from an anchor
    // Player-crafted placeables.
    Ladder = 22,
    Rail = 23,
    ReinforcedSupport = 24,
    Glowshroom = 25,
    Beacon = 26,
}

public static class Tiles
{
    public static bool IsSolid(TileKind k) => k != TileKind.Sky;

    // Tiles that never fall, even when unsupported.
    public static bool IsAnchored(TileKind k) =>
        k is TileKind.PlanetCore or TileKind.Core or TileKind.Support
          or TileKind.ReinforcedSupport or TileKind.Ladder or TileKind.Rope or TileKind.Rail
          or TileKind.Glowshroom or TileKind.Beacon
          // Built architecture: skyscraper hulls and lizard-city masonry never crumble —
          // mining one wall must not condemn the tower above it (they also shrug off acid,
          // which corrodes any non-anchored tile).
          or TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick
          or TileKind.DoorClosed or TileKind.DoorOpen
          or TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp
          // Placed building blocks are architecture like platforms: a small free-standing
          // cluster must not trip the connectivity flood and rain down as dust mid-build.
          or TileKind.Brick or TileKind.Plating or TileKind.GlassBlock
          // A placed platform is a fixed ledge — it never caves in.
          or TileKind.Platform
          // Treasure chests sit solid in the warren vault — they never crumble; you loot them.
          or TileKind.Chest or TileKind.ChestOpen
          // Lily pads float on the water — with no rock under them they must never "fall".
          or TileKind.LilyPad;
          // NOTE: trees & water plants are NOT anchored — an anchored plant reads as an
          // immovable wall to a walking titan (it walled the boss out of its dig shaft), so
          // they stay crushable. Hazard-immunity comes from IsFlora instead.

    /// <summary>Architecture that FALLS when structurally severed, even though it is
    /// anchored. IsAnchored still shields these kinds from every hazard gate (acid rain,
    /// meteor/blast craters, quake crumble, titan jaws chip-don't-vaporise) — but the
    /// cave-in connectivity flood treats them as ordinary load-bearing tiles, so a tower
    /// section cut free topples as rigid debris instead of hanging in the sky. Ladders are
    /// included ONLY so a city tower's centre ladder can't anchor the floors above a
    /// breach, and Beacons ONLY so a spire's beacon-tipped antenna mast can't anchor the
    /// whole skyscraper; placeables that exist to hold things up (Support/ReinforcedSupport/
    /// Platform/Rail/Glowshroom), loot chests, and lily pads keep full anchor semantics.</summary>
    public static bool Topples(TileKind k) =>
        k is TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick
          or TileKind.Brick or TileKind.Plating or TileKind.GlassBlock
          or TileKind.DoorClosed or TileKind.DoorOpen
          or TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp
          or TileKind.Ladder or TileKind.Beacon;

    /// <summary>A tile the cave-in physics may move: solid, not flora (those crush, never
    /// fall), and not anchored — except toppling architecture, which is anchored against
    /// hazards yet still falls when severed. THE predicate for every collapse check.</summary>
    public static bool CanFall(TileKind k) =>
        IsSolid(k) && !IsFlora(k) && (!IsAnchored(k) || Topples(k));

    /// <summary>Biome flora — decorative surface plants. NOT anchored (so a walking titan
    /// tramples them and settling terrain drops them naturally), but hazard-immune via the
    /// explicit checks in Cells (acid/lava/fire skip them) so the ember bloom survives its
    /// lava world and the vitriol lily its acid one. Grouped so those rules and the renderer
    /// can treat them as one family.</summary>
    public static bool IsFlora(TileKind k) =>
        k is TileKind.Fernleaf or TileKind.Frostcap or TileKind.Emberbloom
          or TileKind.Rustbramble or TileKind.Vitrilily or TileKind.Geobloom
          or TileKind.TreeTrunk or TileKind.TreeCanopy or TileKind.TreeCanopy2
          or TileKind.SeaFrond or TileKind.LilyPad;

    /// <summary>Tiles the player walks through (climb / pass through) instead of colliding with.
    /// Ladders are passable so the dwarf can climb; small placed lights are passable too so the
    /// player doesn't bonk on torches in tight corridors.</summary>
    public static bool IsPassable(TileKind k) =>
        k is TileKind.Ladder or TileKind.Rope or TileKind.Glowshroom or TileKind.Beacon
          // Open doors are doorways; furniture is stepped over/through (but still mineable
          // and smashable because it stays "solid" to everything but the walk check).
          or TileKind.DoorOpen
          or TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp
          // Surface flora is walked through, like tall grass.
          or TileKind.Fernleaf or TileKind.Frostcap or TileKind.Emberbloom
          or TileKind.Rustbramble or TileKind.Vitrilily or TileKind.Geobloom
          // Tree canopy and water plants are pushed through; the solid trunk is not.
          or TileKind.TreeCanopy or TileKind.TreeCanopy2 or TileKind.SeaFrond
          or TileKind.LilyPad;

    /// <summary>Tiles that should block-place but allow the player's collision body to pass —
    /// equivalent to "non-solid" for player physics, while staying solid for rendering and
    /// physics-anchor purposes.</summary>
    public static bool BlocksPlayer(TileKind k) => IsSolid(k) && !IsPassable(k);

    /// <summary>Ladder-class climbable tile: while the player overlaps one, gravity is reduced
    /// and W/S directly drive vertical motion.</summary>
    public static bool IsClimbable(TileKind k) => k is TileKind.Ladder or TileKind.Rope;

    /// <summary>Gem-class minerals: shattering one pops a physical <c>Pickup</c> the player
    /// grabs by touch, instead of crumbling to vacuumable dust like ordinary tiles — and the
    /// renderer draws them as a bright gem embedded in whatever rock they sit in.</summary>
    public static bool IsGem(TileKind k) =>
        k is TileKind.Ruby or TileKind.Sapphire or TileKind.Diamond
          or TileKind.Emerald or TileKind.Crystal or TileKind.Voidstone;

    public static bool IsOre(TileKind k) =>
        k is TileKind.CoalOre or TileKind.IronOre or TileKind.GoldOre or TileKind.Crystal
          or TileKind.SilverOre or TileKind.PlatinumOre
          or TileKind.Ruby or TileKind.Sapphire or TileKind.Diamond
          or TileKind.Emerald or TileKind.Voidstone
          or TileKind.FuelOre;

    public static int Hardness(TileKind k) => k switch
    {
        TileKind.Dirt => 1,
        TileKind.Grass => 1,
        TileKind.Snow => 1,
        TileKind.Conglomerate => 1,
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
        TileKind.Emerald => 5,
        TileKind.Voidstone => 6,
        TileKind.Crystal => 5,
        TileKind.FuelOre => 3,
        TileKind.AlienAlloy => 6,
        TileKind.CityGlass => 1,
        TileKind.LizardBrick => 3,
        TileKind.PlanetCore => 99,
        TileKind.Core => 999,
        TileKind.Support => 99,
        TileKind.ReinforcedSupport => 99,
        TileKind.Ladder => 1,
        TileKind.Rope => 1,
        TileKind.Rail => 2,
        TileKind.Glowshroom => 1,
        TileKind.Beacon => 3,
        TileKind.DoorClosed => 2,
        TileKind.DoorOpen => 2,
        TileKind.AlienPlant => 1,
        TileKind.HoverPod => 1,
        TileKind.OrbLamp => 1,
        TileKind.Fernleaf => 1,
        TileKind.Frostcap => 1,
        TileKind.Emberbloom => 1,
        TileKind.Rustbramble => 1,
        TileKind.Vitrilily => 1,
        TileKind.Geobloom => 1,
        TileKind.Brick => 2,
        TileKind.Plating => 4,
        TileKind.GlassBlock => 1,
        TileKind.Platform => 1,
        TileKind.TreeTrunk => 4,       // tough bole — you chop through the whole trunk to fell it
                                       // (Planet.Mine scales this up further with the tree's height)
        TileKind.TreeCanopy => 1,
        TileKind.TreeCanopy2 => 1,
        TileKind.SeaFrond => 1,
        TileKind.LilyPad => 1,
        TileKind.TreeRoot => 3,        // grubbing a root out takes real digging (kills regrowth)
        _ => 0,
    };

    public static Color BaseColor(TileKind k) => k switch
    {
        TileKind.Sky => new Color(20, 24, 38),
        TileKind.Dirt => new Color(110, 70, 40),
        TileKind.Grass => new Color(70, 110, 55),
        TileKind.Snow => new Color(220, 230, 240),
        TileKind.Gravel => new Color(120, 115, 105),
        TileKind.Conglomerate => new Color(140, 120, 95),
        TileKind.Stone => new Color(95, 95, 105),
        TileKind.MossStone => new Color(80, 100, 80),
        TileKind.Granite => new Color(140, 110, 110),
        TileKind.Basalt => new Color(60, 58, 70),
        TileKind.Obsidian => new Color(28, 24, 38),
        TileKind.PlanetCore => new Color(60, 60, 70),
        TileKind.CoalOre => new Color(55, 55, 62),
        TileKind.IronOre => new Color(150, 110, 90),
        // Precious metals read bright and saturated — they should pop out of the rock
        // (the renderer adds a live glint on top).
        TileKind.SilverOre => new Color(202, 208, 224),
        TileKind.GoldOre => new Color(198, 160, 74),
        TileKind.PlatinumOre => new Color(206, 198, 172),   // warm champagne-pearl — NOT silver's blue-white
        TileKind.Ruby => new Color(160, 40, 60),
        TileKind.Sapphire => new Color(50, 70, 170),
        TileKind.Diamond => new Color(180, 220, 230),
        TileKind.Crystal => new Color(130, 80, 170),
        TileKind.Emerald => new Color(35, 120, 70),
        TileKind.Voidstone => new Color(40, 20, 55),
        TileKind.FuelOre => new Color(45, 80, 70),
        TileKind.AlienAlloy => new Color(96, 108, 128),
        TileKind.CityGlass => new Color(140, 200, 210),
        TileKind.LizardBrick => new Color(126, 110, 72),
        TileKind.Core => new Color(255, 90, 40),
        TileKind.Support => new Color(150, 110, 70),
        TileKind.ReinforcedSupport => new Color(120, 105, 95),
        TileKind.Ladder => new Color(140, 95, 55),
        TileKind.Rope => new Color(196, 160, 96),   // hemp line
        TileKind.DoorClosed => new Color(88, 122, 132),
        TileKind.DoorOpen => new Color(50, 66, 74),
        TileKind.AlienPlant => new Color(90, 160, 110),
        TileKind.HoverPod => new Color(170, 120, 190),
        TileKind.OrbLamp => new Color(230, 200, 130),
        TileKind.Fernleaf => new Color(70, 150, 70),
        TileKind.Frostcap => new Color(170, 210, 235),
        TileKind.Emberbloom => new Color(200, 90, 45),
        TileKind.Rustbramble => new Color(150, 95, 60),
        TileKind.Vitrilily => new Color(140, 190, 70),
        TileKind.Geobloom => new Color(150, 120, 210),
        TileKind.Brick => new Color(120, 96, 88),
        TileKind.Plating => new Color(120, 132, 150),
        TileKind.GlassBlock => new Color(150, 200, 220),
        TileKind.Platform => new Color(140, 110, 74),
        TileKind.TreeTrunk => new Color(96, 70, 92),      // alien mauve bark
        TileKind.TreeCanopy => new Color(78, 150, 130),   // teal foliage
        TileKind.TreeCanopy2 => new Color(150, 120, 190), // violet foliage
        TileKind.SeaFrond => new Color(70, 160, 140),
        TileKind.LilyPad => new Color(88, 178, 120),      // alien pad-green afloat on the lake
        TileKind.TreeRoot => new Color(74, 54, 60),       // dark woody root in the soil
        TileKind.Chest => new Color(150, 100, 52),        // banded wood + brass treasure chest
        TileKind.ChestOpen => new Color(96, 66, 40),      // looted, lid thrown back
        TileKind.Rail => new Color(70, 60, 55),
        TileKind.Glowshroom => new Color(60, 110, 70),
        TileKind.Beacon => new Color(100, 60, 150),
        _ => Color.Magenta,
    };

    /// <summary>Bright accent flecks for ore tiles, drawn as small sub-tile speckles.</summary>
    public static Color OreSpeckle(TileKind k) => k switch
    {
        TileKind.CoalOre => new Color(20, 20, 24),
        TileKind.IronOre => new Color(230, 200, 170),
        TileKind.SilverOre => new Color(245, 248, 255),
        TileKind.GoldOre => new Color(255, 230, 110),
        TileKind.PlatinumOre => new Color(255, 246, 210),   // warm pearl sparkle
        TileKind.Ruby => new Color(255, 120, 140),
        TileKind.Sapphire => new Color(140, 180, 255),
        TileKind.Diamond => new Color(255, 255, 255),
        TileKind.Crystal => new Color(230, 180, 255),
        TileKind.Emerald => new Color(120, 255, 160),
        TileKind.Voidstone => new Color(200, 120, 255),
        TileKind.FuelOre => new Color(120, 255, 190),
        _ => Color.White,
    };

    /// <summary>
    /// Display order for the inventory panel — rare/expensive resources at the top, bulk
    /// materials at the bottom. Drives both the row order and which ids count as "known"
    /// resources to render even at zero. Anything not listed still appears at the end.
    /// </summary>
    public static readonly string[] ResourceOrder =
    {
        "voidstone", "emerald", "diamond", "ruby", "sapphire", "platinum", "gold", "silver",
        "crystal", "iron", "coal", "fuel", "wood",
        "nuke", "harpoon",
        "ammo_diamond", "ammo_sapphire", "ammo_ruby", "ammo_silver",
        "rocket", "dynamite", "dynamite_pack", "poultice",
        "ladder", "door", "platform", "brick", "plating", "glass_block",
        "rail", "reinforced_support", "glowshroom", "beacon", "sentry",
        "obsidian", "granite", "basalt", "moss_stone", "gravel",
        "stone", "dirt", "snow",
    };

    /// <summary>Raw mined/harvested materials — the ids a Storage Depot will bank. Crafted
    /// items (weapons, ammo, consumables, placeables) are deliberately excluded: the depot is
    /// a stockpile of dug resources, not a general vault.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _bankable = new()
    {
        "dirt", "stone", "gravel", "moss_stone", "granite", "basalt", "obsidian", "snow",
        "coal", "iron", "silver", "gold", "platinum", "crystal", "fuel",
        "ruby", "sapphire", "diamond", "emerald", "voidstone", "meat", "hide", "chitin", "wood",
    };

    public static bool IsBankable(string id) => _bankable.Contains(id);

    /// <summary>Display swatch colour for a resource id. Pulls from the source tile's BaseColor
    /// where there's a clean 1:1 mapping; specials (rocket_part, nuke) use bespoke tints.</summary>
    public static Color ResourceColor(string id) => id switch
    {
        "dirt"        => BaseColor(TileKind.Dirt),
        "stone"       => BaseColor(TileKind.Stone),
        "gravel"      => BaseColor(TileKind.Gravel),
        "moss_stone"  => BaseColor(TileKind.MossStone),
        "granite"     => BaseColor(TileKind.Granite),
        "basalt"      => BaseColor(TileKind.Basalt),
        "obsidian"    => BaseColor(TileKind.Obsidian),
        "snow"        => BaseColor(TileKind.Snow),
        "coal"        => BaseColor(TileKind.CoalOre),
        "iron"        => BaseColor(TileKind.IronOre),
        "silver"      => BaseColor(TileKind.SilverOre),
        "gold"        => BaseColor(TileKind.GoldOre),
        "platinum"    => BaseColor(TileKind.PlatinumOre),
        "ruby"        => BaseColor(TileKind.Ruby),
        "sapphire"    => BaseColor(TileKind.Sapphire),
        "emerald"     => BaseColor(TileKind.Emerald),
        "voidstone"   => BaseColor(TileKind.Voidstone),
        "diamond"     => BaseColor(TileKind.Diamond),
        "crystal"     => BaseColor(TileKind.Crystal),
        "fuel"        => new Color(120, 255, 190),
        "wood"        => new Color(120, 88, 110),
        "rocket_part" => new Color(190, 200, 220),
        "nuke"        => new Color(255, 80, 200),
        "harpoon"     => new Color(220, 160, 100),
        "ammo_silver"   => new Color(220, 225, 235),
        "ammo_ruby"     => new Color(255, 100, 120),
        "ammo_sapphire" => new Color(120, 160, 255),
        "ammo_diamond"  => new Color(220, 245, 255),
        "dynamite"    => new Color(180, 50, 60),
        "tnt"         => new Color(205, 65, 50),
        "tnt_pack"    => new Color(190, 75, 45),
        "dynamite_pack" => new Color(200, 60, 55),
        "rocket"      => new Color(210, 130, 90),
        "pistol"          => new Color(200, 200, 215),
        "machine_gun"     => new Color(150, 160, 175),
        "laser"           => new Color(255, 110, 110),
        "rocket_launcher" => new Color(180, 150, 110),
        "poultice"    => new Color(120, 180, 110),
        "ladder"      => BaseColor(TileKind.Ladder),
        "door"        => BaseColor(TileKind.DoorClosed),
        "brick"       => BaseColor(TileKind.Brick),
        "plating"     => BaseColor(TileKind.Plating),
        "glass_block" => BaseColor(TileKind.GlassBlock),
        "platform"    => BaseColor(TileKind.Platform),
        "rail"        => BaseColor(TileKind.Rail),
        "reinforced_support" => BaseColor(TileKind.ReinforcedSupport),
        "glowshroom"  => BaseColor(TileKind.Glowshroom),
        "beacon"      => BaseColor(TileKind.Beacon),
        "sentry"      => new Color(160, 140, 90),
        "meat"        => new Color(205, 95, 95),
        "hide"        => new Color(165, 125, 85),
        "chitin"      => new Color(105, 125, 90),
        "feast"       => new Color(230, 150, 90),
        // Worn gear (character screen) — iron pieces read steel-grey, chitin pieces read
        // like the material they're carved from; lights get warm flame tints.
        "armor"           => new Color(185, 190, 205),
        "iron_helmet"     => new Color(185, 190, 205),
        "iron_leggings"   => new Color(170, 175, 190),
        "iron_boots"      => new Color(150, 155, 170),
        "chitin_armor"    => new Color(110, 135, 95),
        "chitin_helmet"   => new Color(110, 135, 95),
        "chitin_leggings" => new Color(100, 120, 85),
        "chitin_boots"    => new Color(90, 110, 80),
        "leather_gloves"  => new Color(165, 125, 85),
        "iron_gauntlets"  => new Color(185, 190, 205),
        "band_regen"      => new Color(210, 90, 100),
        "magnet_ring"     => new Color(200, 90, 80),
        "miners_charm"    => new Color(240, 205, 100),
        "aegis_pendant"   => new Color(140, 180, 255),
        "torch"           => new Color(240, 180, 90),
        "lantern"         => new Color(245, 205, 120),
        "helm_lamp"       => new Color(200, 220, 240),
        "sun_crystal"     => new Color(255, 240, 180),
        _             => Color.White,
    };

    /// <summary>HUD label for a resource id — uppercase, with underscores → spaces.</summary>
    public static string ResourceLabel(string id) => id switch
    {
        "rocket_part"        => "ROCKET PART",
        "nuke"               => "ENERGY BLASTER",
        "moss_stone"         => "MOSS STONE",
        "reinforced_support" => "REINFORCED SUPPORT",
        "ammo_silver"        => "SILVER SHELL",
        "ammo_ruby"          => "RUBY SHELL",
        "ammo_sapphire"      => "SAPPHIRE SHELL",
        "ammo_diamond"       => "DIAMOND SHELL",
        "machine_gun"        => "MACHINE GUN",
        "tnt_pack"           => "TNT PACK",
        "dynamite_pack"      => "DYNAMITE PACK",
        "glass_block"        => "GLASS BLOCK",
        "rocket_launcher"    => "ROCKET LAUNCHER",
        "chitin_armor"       => "CHITIN ARMOR",
        "armor"              => "IRON PLATE ARMOR",
        "iron_helmet"        => "IRON HELMET",
        "iron_leggings"      => "IRON LEGGINGS",
        "iron_boots"         => "IRON BOOTS",
        "chitin_helmet"      => "CHITIN HELMET",
        "chitin_leggings"    => "CHITIN LEGGINGS",
        "chitin_boots"       => "CHITIN BOOTS",
        "leather_gloves"     => "LEATHER GLOVES",
        "iron_gauntlets"     => "IRON GAUNTLETS",
        "band_regen"         => "BAND OF REGENERATION",
        "magnet_ring"        => "MAGNET RING",
        "miners_charm"       => "MINERS CHARM",
        "aegis_pendant"      => "AEGIS PENDANT",
        "helm_lamp"          => "MINERS HEADLAMP",
        "sun_crystal"        => "SUNSTONE CHARM",
        "mining_laser"       => "MINING LASER",
        "laser_cannon"       => "LASER CANNON",
        "core_drill"         => "CORE DRILL",
        "air_tank"           => "AIR TANK",
        // Mothership refinery output (the pixel font has no underscore glyph).
        "pure_iron"          => "PURE IRON",
        "pure_coal"          => "PURE COAL",
        "pure_silver"        => "PURE SILVER",
        "pure_gold"          => "PURE GOLD",
        "pure_platinum"      => "PURE PLATINUM",
        "core_shard"         => "CORE SHARD",
        _                    => id.ToUpperInvariant(),
    };

    // Loot dropped when mined (item id, count). Returns null for nothing.
    public static (string id, int count)? Drop(TileKind k) => k switch
    {
        TileKind.Dirt => ("dirt", 1),
        TileKind.Grass => ("dirt", 1),
        TileKind.Snow => ("snow", 1),
        TileKind.Gravel => ("gravel", 1),
        TileKind.Stone => ("stone", 1),
        TileKind.MossStone => ("moss_stone", 1),
        TileKind.Granite => ("granite", 1),
        TileKind.Basalt => ("basalt", 1),
        TileKind.Obsidian => ("obsidian", 1),
        TileKind.PlanetCore => ("stone", 3),
        TileKind.Support => ("stone", 2),
        TileKind.CoalOre => ("coal", 1),
        TileKind.IronOre => ("iron", 1),
        TileKind.SilverOre => ("silver", 1),
        TileKind.GoldOre => ("gold", 1),
        TileKind.PlatinumOre => ("platinum", 1),
        TileKind.Ruby => ("ruby", 1),
        TileKind.Sapphire => ("sapphire", 1),
        TileKind.Emerald => ("emerald", 1),
        TileKind.Voidstone => ("voidstone", 1),
        TileKind.Diamond => ("diamond", 1),
        TileKind.Crystal => ("crystal", 1),
        TileKind.FuelOre => ("fuel", 1),
        // City salvage: alloy plating strips down to iron; lizard masonry breaks to stone.
        // Glass just shatters (no drop).
        TileKind.AlienAlloy => ("iron", 1),
        TileKind.LizardBrick => ("stone", 1),
        // Player-built tiles drop their craft input back when mined — lets you reposition
        // a misplaced ladder / torch without losing the resource.
        TileKind.Ladder => ("ladder", 1),
        TileKind.Rope => ("rope", 1),
        TileKind.DoorClosed => ("door", 1),
        TileKind.DoorOpen => ("door", 1),
        TileKind.Brick => ("brick", 1),
        TileKind.Plating => ("plating", 1),
        TileKind.GlassBlock => ("glass_block", 1),
        TileKind.Platform => ("platform", 1),
        TileKind.TreeTrunk => ("wood", 2),   // chopping a trunk yields wood
        TileKind.TreeCanopy => ("wood", 1),  // foliage dust still pays a little wood on pickup
        TileKind.TreeCanopy2 => ("wood", 1),
        TileKind.TreeRoot => ("wood", 1),    // grubbed-out roots give a scrap of wood
        TileKind.Rail => ("rail", 1),
        TileKind.ReinforcedSupport => ("reinforced_support", 1),
        TileKind.Glowshroom => ("glowshroom", 1),
        TileKind.Beacon => ("beacon", 1),
        _ => null,
    };
}
