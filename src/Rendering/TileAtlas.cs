using System;
using System.Collections.Generic;
using System.IO;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Hybrid tile texture atlas. Detail comes from CC0 texture-pack art under assets/tiles
/// (see assets/CREDITS.md) when present; colour identity stays the game's: each source
/// pixel's luminance is remapped onto <see cref="Tiles.BaseColor"/> (ore nuggets onto
/// <see cref="Tiles.OreSpeckle"/>), so depth shading, lighting and the minimap keep reading
/// the same palette they always did. Kinds without an external source — and every kind when
/// the assets folder is missing — fall back to the original procedural generator, so the
/// game still runs asset-free. Each TileKind gets <see cref="VariantCount"/> 16×16 patterns;
/// DrawWorld picks a variant by tile hash and maps it onto the rotated polar tile quad. At
/// 2× the tile's world resolution (16px art on an 8px tile) the ground reads as textured
/// pixel art instead of flat jittered quads.
///
/// Row 0 of each pattern is the outer, sky-facing edge after rotation (same convention as
/// the old DrawDeco 8×8 reference coords), so the baked top-lit gradient replaces the old
/// dynamic top/bottom shade bands. Adjacency-dependent art (edge rims, AO, damage cracks)
/// and animated decoration (grass tufts, vines, beacon pulse) stay dynamic in DrawWorld.
/// </summary>
public static class TileAtlas
{
    public const int Res = 16;          // atlas pixels per tile edge (2× world resolution)
    public const int VariantCount = 4;
    /// <summary>Erosion-mask combinations per variant: a 4-bit cardinal air-exposure mask
    /// (bit 0 = outer, 1 = inner, 2 = left, 3 = right). Mask 0 is the untouched interior;
    /// exposed edges are baked with ragged alpha cut-outs and rounded corners, so block
    /// silhouettes read organic instead of razor-square — erosion, not edge paint.</summary>
    public const int MaskCount = 16;

    public static Texture2D Texture { get; private set; } = null!;

    public static Rectangle Source(TileKind k, int variant, int mask = 0)
    {
        var v = ((variant % VariantCount) + VariantCount) % VariantCount;
        return new Rectangle((v * MaskCount + (mask & 15)) * Res, (int)k * Res, Res, Res);
    }

    public static void Build(GraphicsDevice gd)
    {
        var kinds = Enum.GetValues<TileKind>();
        var rows = 0;
        foreach (var k in kinds) rows = Math.Max(rows, (int)k + 1);
        var w = VariantCount * MaskCount * Res;
        var h = rows * Res;
        var px = new Color[w * h];

        var external = LoadExternalSources(gd);
        _externalKinds.Clear();
        var tile = new Color[Res * Res];
        foreach (var k in kinds)
        {
            if (k == TileKind.Sky) continue;
            for (var v = 0; v < VariantCount; v++)
            {
                Array.Clear(tile);
                if (external.TryGetValue(k, out var src))
                {
                    // Ores draw their background detail from the stone source so a vein sits
                    // seamlessly in the surrounding rock instead of standing out as a square
                    // patch of different stone texture.
                    var bg = Tiles.IsOre(k) && external.TryGetValue(TileKind.Stone, out var stoneSrc)
                        ? stoneSrc
                        : src;
                    ComposeHybrid(tile, Res, 0, 0, k, src, bg, v);
                    _externalKinds.Add(k);
                }
                else
                {
                    GenerateTile(tile, Res, 0, 0, k, new Random((int)k * 97 + v * 13 + 1));
                }
                for (var mask = 0; mask < MaskCount; mask++)
                    BlitEroded(px, w, (v * MaskCount + mask) * Res, (int)k * Res, tile, k, v, mask);
            }
        }

        Texture = new Texture2D(gd, w, h);
        Texture.SetData(px);
    }

    /// <summary>Copy one composed tile into the atlas, cutting ragged transparency into
    /// each air-exposed edge (per <paramref name="mask"/> bit) and chamfering corners where
    /// two exposed edges meet. Depths are hash-stable per (kind, variant, edge, position):
    /// mostly 0–1 art px with occasional 2, corners ~3 — at 2× world resolution that is a
    /// sub-pixel-to-1.5-px nibble, rounding the silhouette without visibly gnawing it.
    /// Cut pixels are fully transparent, so whatever is behind (sky, dim back-wall, water)
    /// shows through — a shape change, never a colour band.</summary>
    private static void BlitEroded(Color[] px, int stride, int ox, int oy,
        Color[] tile, TileKind k, int variant, int mask)
    {
        // Engineered structures (alien hull plating, its window glass, lizard-city masonry)
        // are machined, not geological — their walls must read as dead-straight. Force the
        // interior (mask 0) frame so they never grow the ragged silhouette erosion or corner
        // chamfers that the natural rock kinds use; a mined breach in a wall stays crisp.
        if (k is TileKind.AlienAlloy or TileKind.CityGlass or TileKind.LizardBrick) mask = 0;

        Span<int> top = stackalloc int[Res];
        Span<int> bot = stackalloc int[Res];
        Span<int> lft = stackalloc int[Res];
        Span<int> rgt = stackalloc int[Res];
        var seed = (int)k * 733 + variant * 149;
        for (var i = 0; i < Res; i++)
        {
            top[i] = (mask & 1) != 0 ? EdgeDepth(seed, 0, i) : 0;
            bot[i] = (mask & 2) != 0 ? EdgeDepth(seed, 1, i) : 0;
            lft[i] = (mask & 4) != 0 ? EdgeDepth(seed, 2, i) : 0;
            rgt[i] = (mask & 8) != 0 ? EdgeDepth(seed, 3, i) : 0;
        }
        // Corner chamfers where two exposed edges meet (diagonal cut of ~3 px, hash ±1).
        var tl = (mask & 5) == 5 ? 2 + EdgeDepth(seed, 4, 0) : 0;
        var tr = (mask & 9) == 9 ? 2 + EdgeDepth(seed, 4, 1) : 0;
        var bl = (mask & 6) == 6 ? 2 + EdgeDepth(seed, 4, 2) : 0;
        var br = (mask & 10) == 10 ? 2 + EdgeDepth(seed, 4, 3) : 0;

        for (var y = 0; y < Res; y++)
        {
            for (var x = 0; x < Res; x++)
            {
                var cut = y < top[x] || y >= Res - bot[x]
                       || x < lft[y] || x >= Res - rgt[y]
                       || x + y < tl
                       || Res - 1 - x + y < tr
                       || x + Res - 1 - y < bl
                       || Res - 1 - x + Res - 1 - y < br;
                px[(oy + y) * stride + ox + x] = cut ? Color.Transparent : tile[y * Res + x];
            }
        }
    }

    /// <summary>Ragged erosion depth for one position along one edge: 0 half the time,
    /// 1 a third, 2 the rest — hash-stable so the same tile always erodes the same way.</summary>
    private static int EdgeDepth(int seed, int edge, int i)
    {
        unchecked
        {
            var h = seed + edge * 8887 + i * (int)2654435761;
            h ^= h >> 13;
            h *= 1274126177;
            h ^= h >> 16;
            return ((h & 0x7FFFFFFF) % 6) switch { 0 or 1 or 2 => 0, 3 or 4 => 1, _ => 2 };
        }
    }

    private static readonly HashSet<TileKind> _externalKinds = new();

    /// <summary>True when this kind's atlas rows came from external pack art. Those variants
    /// are positional half-rolls meant to be picked by tile parity for cross-tile pattern
    /// continuity; procedural rows are independent patterns better picked by hash.</summary>
    public static bool HasExternal(TileKind k) => _externalKinds.Contains(k);

    /// <summary>Which external texture feeds which kind. Ores share one nugget/one crystal
    /// stone — the palette remap in ComposeHybrid recolours them per kind.</summary>
    private static readonly (TileKind kind, string file)[] ExternalMap =
    {
        (TileKind.Dirt, "dirt.png"),
        (TileKind.Grass, "grass.png"),
        (TileKind.Stone, "stone.png"),
        (TileKind.PlanetCore, "stone.png"),
        (TileKind.Granite, "granite.png"),
        (TileKind.Basalt, "basalt.png"),
        (TileKind.Obsidian, "obsidian.png"),
        (TileKind.Gravel, "gravel.png"),
        (TileKind.Snow, "snow.png"),
        (TileKind.MossStone, "mossstone.png"),
        (TileKind.CoalOre, "ore_nuggets.png"),
        (TileKind.IronOre, "ore_nuggets.png"),
        (TileKind.SilverOre, "ore_nuggets.png"),
        (TileKind.GoldOre, "ore_nuggets.png"),
        (TileKind.PlatinumOre, "ore_nuggets.png"),
        (TileKind.Ruby, "ore_crystal.png"),
        (TileKind.Sapphire, "ore_crystal.png"),
        (TileKind.Diamond, "ore_crystal.png"),
        (TileKind.Crystal, "ore_crystal.png"),
    };

    /// <summary>Locate assets/tiles next to the binary (or the working directory when run
    /// via `dotnet run`) and decode every mapped 16×16 PNG once. Missing folder, missing
    /// files, or wrong-sized art simply drop back to the procedural generator.</summary>
    private static Dictionary<TileKind, (Color[] pix, float meanLum)> LoadExternalSources(GraphicsDevice gd)
    {
        var result = new Dictionary<TileKind, (Color[], float)>();
        string? dir = null;
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = Path.Combine(root, "assets", "tiles");
            if (Directory.Exists(d)) { dir = d; break; }
        }
        if (dir is null) return result;

        var byFile = new Dictionary<string, (Color[], float)?>();
        foreach (var (kind, file) in ExternalMap)
        {
            if (!byFile.TryGetValue(file, out var loaded))
            {
                loaded = LoadSource(gd, Path.Combine(dir, file));
                byFile[file] = loaded;
            }
            if (loaded is { } src) result[kind] = src;
        }
        return result;
    }

    private static (Color[] pix, float meanLum)? LoadSource(GraphicsDevice gd, string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var tex = Texture2D.FromStream(gd, fs);
            if (tex.Width != Res || tex.Height != Res) return null;
            var pix = new Color[Res * Res];
            tex.GetData(pix);
            var sum = 0L;
            foreach (var c in pix) sum += (c.R * 30 + c.G * 59 + c.B * 11) / 100;
            return (pix, sum / (float)pix.Length);
        }
        catch (Exception)
        {
            return null; // unreadable file — procedural fallback covers it
        }
    }

    /// <summary>Compose one atlas variant from an external source: pack art supplies the
    /// per-pixel detail (as luminance deviation from the source's mean), the game palette
    /// supplies the colour.
    ///
    /// Variants are the four half-size rolls of the seamless source — (0,0), (8,0), (0,8),
    /// (8,8). DrawWorld picks the variant from tile parity ((t&amp;1) | (r&amp;1)&lt;&lt;1), and
    /// because the art is 2× the tile's world resolution, a 2×2 block of tiles then spans
    /// the source exactly once and the pattern flows continuously across tile boundaries —
    /// terrain reads as one rock mass, Terraria-style, instead of a wall of repeated blocks.
    /// Grass pins its sky-edge blade strip to row 0, so it only rolls horizontally.
    ///
    /// Deliberately no baked top-lit gradient or per-variant grain here (unlike the
    /// procedural fallback): a per-tile gradient repeats every tile and redraws the grid the
    /// positional rolls just erased; the dynamic edge rims and the lighting pass carry form
    /// instead.</summary>
    private static void ComposeHybrid(Color[] px, int stride, int ox, int oy, TileKind k,
        (Color[] pix, float meanLum) src, (Color[] pix, float meanLum) bg, int variant)
    {
        var baseCol = Tiles.BaseColor(k);
        var isOre = Tiles.IsOre(k);
        var dirtCol = Tiles.BaseColor(TileKind.Dirt);
        var rollX = (variant & 1) * (Res / 2);
        var rollY = k == TileKind.Grass ? 0 : (variant >> 1) * (Res / 2);

        for (var y = 0; y < Res; y++)
        {
            for (var x = 0; x < Res; x++)
            {
                var sx = (x + rollX) % Res;
                var sy = (y + rollY) % Res;
                var s = src.pix[sy * Res + sx];
                var lum = (s.R * 30 + s.G * 59 + s.B * 11) / 100;
                var mean = src.meanLum;
                var sat = Math.Max(s.R, Math.Max(s.G, s.B)) - Math.Min(s.R, Math.Min(s.G, s.B));
                var green = s.G > s.R + 10 && s.G > s.B + 10;

                Color target;
                if (isOre && sat > 45)
                {
                    target = Tiles.OreSpeckle(k); // nugget/crystal pixels
                }
                else if (isOre)
                {
                    // Non-nugget ore pixels take their detail from the *stone* source, so the
                    // vein's host rock is texture-continuous with the stone around the tile.
                    var b = bg.pix[sy * Res + sx];
                    lum = (b.R * 30 + b.G * 59 + b.B * 11) / 100;
                    mean = bg.meanLum;
                    target = baseCol;
                }
                else if (k == TileKind.Grass && !green) target = dirtCol;      // root-line dirt
                else if (k == TileKind.MossStone && green) target = new Color(70, 120, 75);
                else target = baseCol;

                // Half-strength detail: Terraria concentrates contrast at tile edges (the
                // dynamic outline/lip framing in DrawWorld), so interiors stay soft.
                var d = (int)((lum - mean) * 0.5f);
                px[(oy + y) * stride + ox + x] = new Color(
                    Math.Clamp(target.R + d, 0, 255),
                    Math.Clamp(target.G + d, 0, 255),
                    Math.Clamp(target.B + d, 0, 255));
            }
        }
    }

    private static void GenerateTile(Color[] px, int stride, int ox, int oy, TileKind k, Random rng)
    {
        var baseCol = Tiles.BaseColor(k);

        void Set(int x, int y, Color c)
        {
            if (x < 0 || x >= Res || y < 0 || y >= Res) return;
            px[(oy + y) * stride + ox + x] = c;
        }

        static Color Shade(Color c, int d) => new(
            Math.Clamp(c.R + d, 0, 255), Math.Clamp(c.G + d, 0, 255), Math.Clamp(c.B + d, 0, 255));
        static Color Tint(Color c, int dr, int dg, int db) => new(
            Math.Clamp(c.R + dr, 0, 255), Math.Clamp(c.G + dg, 0, 255), Math.Clamp(c.B + db, 0, 255));

        // Base fill: per-pixel grain plus a baked top-lit gradient (row 0 faces the sky).
        for (var y = 0; y < Res; y++)
            for (var x = 0; x < Res; x++)
                Set(x, y, Shade(baseCol, 12 - 24 * y / (Res - 1) + rng.Next(-9, 10)));

        // Wandering darker bedding line with breaks — shared by the rock family.
        void Strata(int count, int delta)
        {
            for (var l = 0; l < count; l++)
            {
                var y = 2 + rng.Next(Res - 4);
                for (var x = rng.Next(3); x < Res - rng.Next(3); x++)
                {
                    if (rng.Next(6) == 0) y = Math.Clamp(y + rng.Next(-1, 2), 1, Res - 2);
                    if (rng.Next(8) == 0) continue;
                    Set(x, y, Shade(baseCol, delta + rng.Next(-6, 7)));
                }
            }
        }

        // Rough elliptical patch (clods, pebbles, moss) with ragged edges.
        void Blob(int cx, int cy, int rw, int rh, Color c)
        {
            for (var y = -rh; y <= rh; y++)
                for (var x = -rw; x <= rw; x++)
                    if (x * x * rh * rh + y * y * rw * rw <= rw * rw * rh * rh && rng.Next(5) > 0)
                        Set(cx + x, cy + y, Shade(c, rng.Next(-8, 9)));
        }

        // Connected nugget cluster along a random walk — ore deposits read as one vein
        // rather than scattered confetti.
        void Vein(Color nugget, bool sparkle)
        {
            var n = 4 + rng.Next(4);
            float vx = 3 + rng.Next(Res - 6), vy = 3 + rng.Next(Res - 6);
            var ang = (float)(rng.NextDouble() * Math.PI * 2);
            for (var i = 0; i < n; i++)
            {
                var sz = 1 + rng.Next(2);
                for (var y = 0; y <= sz; y++)
                    for (var x = 0; x <= sz; x++)
                        Set((int)vx + x, (int)vy + y, Shade(nugget, rng.Next(-14, 15)));
                if (sparkle && rng.Next(3) == 0) Set((int)vx + 1, (int)vy, Color.White);
                ang += (float)(rng.NextDouble() - 0.5) * 1.2f;
                vx = Math.Clamp(vx + MathF.Cos(ang) * (1.5f + rng.Next(2)), 1, Res - 3);
                vy = Math.Clamp(vy + MathF.Sin(ang) * (1.5f + rng.Next(2)), 1, Res - 3);
            }
        }

        switch (k)
        {
            case TileKind.Stone:
            case TileKind.PlanetCore:
            {
                Strata(2, -24);
                for (var i = 0; i < 3; i++) Set(1 + rng.Next(Res - 2), 1 + rng.Next(Res - 2), Shade(baseCol, 20));
                for (var i = 0; i < 2; i++) Set(1 + rng.Next(Res - 2), 1 + rng.Next(Res - 2), Shade(baseCol, -22));
                break;
            }
            case TileKind.Granite:
            {
                // Dense mineral flecking — pink feldspar lights against dark mica.
                for (var i = 0; i < 9; i++) Set(rng.Next(Res), rng.Next(Res), Tint(baseCol, 30, 14, 14));
                for (var i = 0; i < 7; i++) Set(rng.Next(Res), rng.Next(Res), Shade(baseCol, -26));
                break;
            }
            case TileKind.Basalt:
            {
                // Angular fracture slashes running diagonally, with cold glassy highlights.
                for (var s = 0; s < 3; s++)
                {
                    int x = rng.Next(Res - 6), y = rng.Next(Res - 6);
                    var len = 4 + rng.Next(4);
                    for (var i = 0; i < len; i++)
                    {
                        Set(x + i, y + i / 2 + rng.Next(2), Shade(baseCol, -30));
                    }
                }
                for (var i = 0; i < 2; i++) Set(rng.Next(Res), rng.Next(Res), Tint(baseCol, 16, 16, 26));
                break;
            }
            case TileKind.Obsidian:
            {
                // Glassy black: conchoidal ripple arcs plus two hard glints.
                var arcY = 3 + rng.Next(Res - 8);
                for (var x = 2; x < Res - 2; x++)
                    if (rng.Next(3) > 0) Set(x, arcY + (x * x) / (Res * 2), Shade(baseCol, 16));
                for (var g = 0; g < 2; g++)
                {
                    var gx = 2 + rng.Next(Res - 4);
                    var gy = 2 + rng.Next(Res - 4);
                    Set(gx, gy, new Color(185, 195, 225));
                    Set(gx + 1, gy, new Color(120, 130, 170));
                }
                break;
            }
            case TileKind.Dirt:
            {
                for (var i = 0; i < 4; i++)
                    Blob(2 + rng.Next(Res - 4), 2 + rng.Next(Res - 4), 1 + rng.Next(2), 1, Shade(baseCol, -20));
                for (var i = 0; i < 4; i++) Set(rng.Next(Res), rng.Next(Res), Shade(baseCol, 16));
                break;
            }
            case TileKind.Gravel:
            {
                // High-contrast pebble mosaic with a shadow pixel under each stone.
                for (var i = 0; i < 6; i++)
                {
                    var cx = 2 + rng.Next(Res - 4);
                    var cy = 2 + rng.Next(Res - 4);
                    var shade = rng.Next(2) == 0 ? 22 : -22;
                    Blob(cx, cy, 1 + rng.Next(2), 1, Shade(baseCol, shade));
                    Set(cx, cy + 2, Shade(baseCol, -30));
                }
                break;
            }
            case TileKind.Conglomerate:
            {
                // Pressed-debris look: clasts of the loose materials (sand / dirt / gravel)
                // cemented in the base matrix. The renderer multiplies the whole tile by the
                // composition tint stored at compaction time, so keep the palette near-neutral
                // and let hue come from what actually got pressed in.
                var clasts = new[]
                {
                    new Color(190, 158, 92),   // sand
                    new Color(115, 75, 42),    // dirt
                    new Color(125, 120, 110),  // gravel
                    Shade(baseCol, -18),
                };
                for (var i = 0; i < 7; i++)
                {
                    var cx = 2 + rng.Next(Res - 4);
                    var cy = 2 + rng.Next(Res - 4);
                    Blob(cx, cy, 1 + rng.Next(2), 1 + rng.Next(2), clasts[rng.Next(clasts.Length)]);
                    Set(cx, cy + 2, Shade(baseCol, -26));   // seat shadow under each clast
                }
                break;
            }
            case TileKind.Grass:
            {
                // Bright blade mottling near the sky edge, fading into dirt at the root line.
                var dirtC = Tiles.BaseColor(TileKind.Dirt);
                for (var y = 11; y < Res; y++)
                    for (var x = 0; x < Res; x++)
                        Set(x, y, Shade(Color.Lerp(baseCol, dirtC, (y - 10) / 6f), rng.Next(-8, 9)));
                for (var y = 0; y < 3; y++)
                    for (var x = 0; x < Res; x++)
                        if (rng.Next(3) == 0)
                            Set(x, y, Tint(baseCol, 25, 40, 10));
                for (var i = 0; i < 5; i++) Set(rng.Next(Res), 3 + rng.Next(6), Shade(baseCol, -20));
                break;
            }
            case TileKind.Snow:
            {
                for (var i = 0; i < 5; i++)
                    Blob(2 + rng.Next(Res - 4), 2 + rng.Next(Res - 4), 1 + rng.Next(2), 1, new Color(202, 216, 236));
                for (var i = 0; i < 4; i++) Set(rng.Next(Res), rng.Next(Res), Color.White);
                break;
            }
            case TileKind.MossStone:
            {
                Strata(1, -20);
                for (var i = 0; i < 3; i++)
                    Blob(2 + rng.Next(Res - 4), 2 + rng.Next(Res - 4), 1 + rng.Next(3), 1 + rng.Next(2), new Color(70, 120, 75));
                break;
            }
            case TileKind.Crystal:
            {
                // Elongated diagonal shards with bright tips.
                for (var s = 0; s < 3; s++)
                {
                    int x = 2 + rng.Next(Res - 8), y = 2 + rng.Next(Res - 8);
                    var len = 4 + rng.Next(3);
                    var dir = rng.Next(2) == 0 ? 1 : -1;
                    for (var i = 0; i < len; i++)
                        Set(x + i, y + i * dir, Shade(Tiles.OreSpeckle(k), rng.Next(-16, 8)));
                    Set(x + len, y + len * dir, Color.White);
                }
                break;
            }
            case TileKind.CoalOre:
            {
                Strata(1, -22);
                Vein(new Color(24, 24, 30), sparkle: false);
                break;
            }
            case TileKind.IronOre:
            case TileKind.SilverOre:
            case TileKind.GoldOre:
            case TileKind.PlatinumOre:
            case TileKind.Ruby:
            case TileKind.Sapphire:
            case TileKind.Diamond:
            case TileKind.Emerald:
            case TileKind.Voidstone:
            {
                Strata(1, -20);
                Vein(Tiles.OreSpeckle(k), sparkle: k != TileKind.IronOre);
                break;
            }
            case TileKind.AlienAlloy:
            {
                // Riveted hull plating: panel seam lines with highlight bevels and dark
                // rivet dots at the seam crossings — engineered, not geological.
                var seamY = 5 + rng.Next(6);
                for (var x = 0; x < Res; x++)
                {
                    Set(x, seamY, Shade(baseCol, -26));
                    Set(x, seamY + 1, Shade(baseCol, 14));
                }
                var seamX = 4 + rng.Next(8);
                for (var y = 0; y < Res; y++)
                {
                    Set(seamX, y, Shade(baseCol, -22));
                    Set(seamX + 1, y, Shade(baseCol, 10));
                }
                Set(seamX, seamY, Shade(baseCol, -40));
                for (var i = 0; i < 4; i++)
                    Set(1 + rng.Next(Res - 2), 1 + rng.Next(Res - 2), Shade(baseCol, -30));
                for (var i = 0; i < 3; i++)
                    Set(1 + rng.Next(Res - 2), 1 + rng.Next(Res - 2), Tint(baseCol, 14, 22, 30));
                break;
            }
            case TileKind.CityGlass:
            {
                // Lit window pane: warm interior glow behind a cool glass sheen, a bright
                // diagonal reflection streak, and a dark frame rim.
                var warm = new Color(240, 205, 130);
                for (var y = 2; y < Res - 2; y++)
                    for (var x = 2; x < Res - 2; x++)
                        if (rng.Next(4) == 0) Set(x, y, Color.Lerp(baseCol, warm, 0.45f));
                var d0 = rng.Next(6);
                for (var i = 0; i < Res; i++)
                {
                    Set(i + d0 - 4, i, Tint(baseCol, 45, 45, 40));
                    Set(i + d0 - 3, i, Tint(baseCol, 25, 25, 22));
                }
                for (var i = 0; i < Res; i++)
                {
                    Set(i, 0, Shade(baseCol, -50)); Set(i, Res - 1, Shade(baseCol, -50));
                    Set(0, i, Shade(baseCol, -50)); Set(Res - 1, i, Shade(baseCol, -50));
                }
                break;
            }
            case TileKind.LizardBrick:
            {
                // Coursed masonry: offset brick rows split by dark mortar lines, the odd
                // brick lighter or mossy — carved, ancient, and slightly unmaintained.
                for (var row = 0; row < 4; row++)
                {
                    var y = row * 4;
                    for (var x = 0; x < Res; x++) Set(x, y, Shade(baseCol, -34));
                    var off = row % 2 == 0 ? 0 : 4;
                    for (var x = off; x < Res + 8; x += 8)
                        for (var yy = y + 1; yy < y + 4 && yy < Res; yy++)
                            Set(x % Res, yy, Shade(baseCol, -30));
                    // Per-brick tonal variety.
                    if (rng.Next(2) == 0)
                    {
                        var bx = (off + rng.Next(2) * 8) % Res;
                        var tintUp = rng.Next(3) > 0;
                        for (var yy = y + 1; yy < y + 4 && yy < Res; yy++)
                            for (var xx = bx + 1; xx < bx + 8 && xx < Res; xx++)
                                Set(xx, yy, tintUp ? Shade(baseCol, 12) : Tint(baseCol, -14, 2, -12));
                    }
                }
                break;
            }
            // Player-built kinds (Support, Ladder, Rail, Glowshroom, Beacon, Core…) keep
            // their authored DrawDeco art in DrawWorld; their atlas rows are just base fill.
        }
    }
}
