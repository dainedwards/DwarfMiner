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

    public static Texture2D Texture { get; private set; } = null!;

    public static Rectangle Source(TileKind k, int variant)
    {
        var v = ((variant % VariantCount) + VariantCount) % VariantCount;
        return new Rectangle(v * Res, (int)k * Res, Res, Res);
    }

    public static void Build(GraphicsDevice gd)
    {
        var kinds = Enum.GetValues<TileKind>();
        var rows = 0;
        foreach (var k in kinds) rows = Math.Max(rows, (int)k + 1);
        var w = VariantCount * Res;
        var h = rows * Res;
        var px = new Color[w * h];

        var external = LoadExternalSources(gd);
        foreach (var k in kinds)
        {
            if (k == TileKind.Sky) continue;
            for (var v = 0; v < VariantCount; v++)
            {
                var rng = new Random((int)k * 97 + v * 13 + 1);
                if (external.TryGetValue(k, out var src))
                    ComposeHybrid(px, w, v * Res, (int)k * Res, k, src, v, rng);
                else
                    GenerateTile(px, w, v * Res, (int)k * Res, k, rng);
            }
        }

        Texture = new Texture2D(gd, w, h);
        Texture.SetData(px);
    }

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
    /// supplies the colour. The pack tiles seamlessly, so variants sample at wrapped offsets
    /// (odd ones mirrored) to differ without extra art; Grass rolls only sideways so its
    /// sky-edge blade strip stays on row 0. The baked top-lit gradient and a light grain
    /// match the procedural tiles' conventions.</summary>
    private static void ComposeHybrid(Color[] px, int stride, int ox, int oy, TileKind k,
        (Color[] pix, float meanLum) src, int variant, Random rng)
    {
        var baseCol = Tiles.BaseColor(k);
        var isOre = Tiles.IsOre(k);
        var dirtCol = Tiles.BaseColor(TileKind.Dirt);
        var rollX = variant * 5;
        var rollY = k == TileKind.Grass ? 0 : variant * 3;
        var mirror = variant % 2 == 1;

        for (var y = 0; y < Res; y++)
        {
            for (var x = 0; x < Res; x++)
            {
                var sx = ((mirror ? Res - 1 - x : x) + rollX) % Res;
                var sy = (y + rollY) % Res;
                var s = src.pix[sy * Res + sx];
                var lum = (s.R * 30 + s.G * 59 + s.B * 11) / 100;
                var sat = Math.Max(s.R, Math.Max(s.G, s.B)) - Math.Min(s.R, Math.Min(s.G, s.B));
                var green = s.G > s.R + 10 && s.G > s.B + 10;

                Color target;
                if (isOre && sat > 45) target = Tiles.OreSpeckle(k);          // nugget/crystal pixels
                else if (k == TileKind.Grass && !green) target = dirtCol;      // root-line dirt
                else if (k == TileKind.MossStone && green) target = new Color(70, 120, 75);
                else target = baseCol;

                var d = (int)((lum - src.meanLum) * 0.85f)
                        + 12 - 24 * y / (Res - 1)
                        + rng.Next(-4, 5);
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
            {
                Strata(1, -20);
                Vein(Tiles.OreSpeckle(k), sparkle: k != TileKind.IronOre);
                break;
            }
            // Player-built kinds (Support, Ladder, Rail, Glowshroom, Beacon, Core…) keep
            // their authored DrawDeco art in DrawWorld; their atlas rows are just base fill.
        }
    }
}
