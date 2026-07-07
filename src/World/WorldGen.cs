using System;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Polar world generation. The planet's natural outer edge is essentially circular (very mild
/// elev variation only). Mountains are placed as a small set of explicit narrow spikes at
/// random angles, so each peak is a pointy stone column rising sharply from the otherwise
/// smooth grass surface. Underground: deep dirt layer, then stone with sparse caves, dirt
/// seams, gravel pockets, and ores, running all the way down to the core ball the renderer
/// draws inside the innermost ring.
/// </summary>
public static class WorldGen
{
    /// <summary>Legacy overload — the starter planet's tuning (used by SimTest).</summary>
    public static Planet Generate(int seed) => Generate(seed, PlanetDefs.All[0]);

    public static Planet Generate(int seed, PlanetDef def)
    {
        var planet = new Planet(new Vector2(2400, 2400));
        var rng = new Random(seed);

        // Subtle surface elevation noise — kept very low so the planet reads as a smooth
        // round circle except where mountain spikes rise.
        var surfA = MakeAngularNoise(rng, 8);
        var surfC = MakeAngularNoise(rng, 128);

        // Explicit mountain placements — 4-6 narrow spikes at random angles, each with its
        // own height and angular width. Drives a much narrower silhouette than what angular
        // noise interpolation could produce, since width is a per-mountain parameter.
        var mountainCount = def.MountainMin + rng.Next(def.MountainExtra + 1);
        var mountains = new (float ang, float h, float w)[mountainCount];
        for (var i = 0; i < mountainCount; i++)
        {
            // Per-mountain height scale 0.5–1.5 multiplied against a 28–46 base — final
            // heights span ~14–69 tiles, so the planet has a mix of short and tall peaks.
            var baseH = (28f + (float)rng.NextDouble() * 18f) * def.MountainHeightScale;
            var scaleH = 0.5f + (float)rng.NextDouble() * 1.0f;
            mountains[i] = (
                ang: (float)(rng.NextDouble() * MathHelper.TwoPi),
                h: baseH * scaleH,
                w: 0.09f + (float)rng.NextDouble() * 0.075f               // 50% wider: ≈ 5.2°..9.5°
            );
        }

        var bigCave = new float[64, 64];
        var smallCave = new float[64, 64];
        var oreNoise = new float[64, 64];
        var pocketNoise = new float[64, 64];
        var waterNoise = new float[64, 64];
        for (var j = 0; j < 64; j++)
            for (var i = 0; i < 64; i++)
            {
                bigCave[i, j] = (float)rng.NextDouble();
                smallCave[i, j] = (float)rng.NextDouble();
                oreNoise[i, j] = (float)rng.NextDouble();
                pocketNoise[i, j] = (float)rng.NextDouble();
                waterNoise[i, j] = (float)rng.NextDouble();
            }

        // Surface lakes — 3-4 shallow basins scooped out of the smooth surface, placed by
        // rejection sampling so they never overlap a mountain's footprint. The basin profile
        // is a rounded dome (1-(d/w)²) rather than the mountains' pointy parabola. The carved
        // tiles are recorded as WaterSeeds; the water itself is poured in as sim cells later.
        var lakeCount = def.HasWater ? def.LakeMin + rng.Next(def.LakeExtra + 1) : 0;
        var lakes = new (float ang, float depth, float w)[lakeCount];
        for (var i = 0; i < lakeCount; i++)
        {
            float ang;
            var tries = 0;
            do
            {
                ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
                tries++;
            } while (tries < 40 && NearMountain(mountains, ang, 0.14f));
            lakes[i] = (
                ang,
                depth: 3.5f + (float)rng.NextDouble() * 3.5f,   // 3.5–7 tiles deep at centre
                w: 0.055f + (float)rng.NextDouble() * 0.05f);   // ≈ 3°–6° half-width
        }

        const int baselineR = 129;

        for (var r = 0; r < Planet.RingCount; r++)
        {
            var n = Planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                var ang = (t + 0.5f) / n * MathHelper.TwoPi;

                // Very subtle surface variation — at most ±1.5 tiles. The planet stays round.
                var elev = AngularSample(surfA, ang) * 2f;

                // Mountain height at this angle: take the max contribution across all spikes.
                // Squared distance falloff (1-d/w)² gives a sharp parabolic point.
                var mountainHeight = 0f;
                foreach (var m in mountains)
                {
                    var angDiff = MathF.Abs(ang - m.ang);
                    if (angDiff > MathF.PI) angDiff = MathHelper.TwoPi - angDiff;
                    if (angDiff < m.w)
                    {
                        var dt = 1f - (angDiff / m.w);
                        var contribution = dt * dt * m.h;
                        if (contribution > mountainHeight) mountainHeight = contribution;
                    }
                }

                var surfaceR = baselineR + elev;
                var peakR = surfaceR + mountainHeight;

                // Above the mountain peak (or above the smooth surface): Sky.
                if (r > peakR)
                {
                    planet.Set(r, t, TileKind.Sky);
                    continue;
                }

                // Mountain body — solid stone above the planet's natural surface.
                if (r > surfaceR)
                {
                    planet.SetWall(r, t, TileKind.Stone);
                    planet.Set(r, t, TileKind.Stone);
                    continue;
                }

                // Below the surface: layered ground.
                var depth = surfaceR - r;

                // Lake basin: carve the bowl out of the surface and seed it with water. The
                // top course (depth < 1) stays air so the waterline sits just below the shore.
                if (mountainHeight <= 0.5f)
                {
                    var lakeDepth = 0f;
                    foreach (var l in lakes)
                    {
                        var angDiff = MathF.Abs(ang - l.ang);
                        if (angDiff > MathF.PI) angDiff = MathHelper.TwoPi - angDiff;
                        if (angDiff < l.w)
                        {
                            var f = angDiff / l.w;
                            var d = (1f - f * f) * l.depth;
                            if (d > lakeDepth) lakeDepth = d;
                        }
                    }
                    if (lakeDepth > 0.5f && depth < lakeDepth)
                    {
                        planet.SetWall(r, t, TileKind.Dirt);
                        planet.Set(r, t, TileKind.Sky);
                        if (depth >= 1f) planet.WaterSeeds.Add((r, t));
                        continue;
                    }
                }

                var pos = planet.TileToWorld(r, t);
                var wx = (pos.X - planet.Center.X) / Planet.TileSize;
                var wy = (pos.Y - planet.Center.Y) / Planet.TileSize;

                TileKind k;
                if (depth < 1f)
                {
                    k = def.SurfaceTile;
                }
                else if (depth < 10f + AngularSample(surfC, ang) * 2f)
                {
                    k = TileKind.Dirt;
                }
                else
                {
                    // Depth-graded base rock: Stone in the upper crust grades through Granite
                    // and Basalt into Obsidian as you approach the core. A second noise mixes
                    // adjacent layers so band edges are jagged rather than perfectly circular.
                    var bN = SampleNoise(pocketNoise, wx * 0.07f, wy * 0.07f);
                    if (depth > 90f)
                        k = bN > 0.32f ? TileKind.Obsidian : TileKind.Basalt;
                    else if (depth > 60f)
                        k = bN > 0.74f ? TileKind.Obsidian : TileKind.Basalt;
                    else if (depth > 40f)
                    {
                        if (bN > 0.66f) k = TileKind.Basalt;
                        else if (bN > 0.42f) k = TileKind.Granite;
                        else k = TileKind.Stone;
                    }
                    else if (depth > 22f)
                        k = bN > 0.60f ? TileKind.Granite : TileKind.Stone;
                    else
                        k = TileKind.Stone;
                }

                // Mountain base footing: replace the surface grass tile with stone where a
                // mountain rises, so the spike rests on stone, not grass.
                if (depth < 1f && mountainHeight > 0.5f)
                {
                    k = TileKind.Stone;
                }

                // Buried dirt seams + gravel pockets in the mid-stone band.
                if (k == TileKind.Stone && depth > 8f && depth < 30f)
                {
                    var pN = SampleNoise(pocketNoise, wx * 0.13f, wy * 0.13f);
                    if (pN > 0.84f) k = TileKind.Dirt;
                    else if (pN > 0.78f) k = TileKind.Gravel;
                }

                // Wall captures the structural material before caves/ores override the foreground.
                planet.SetWall(r, t, k);

                var bigN = SampleNoise(bigCave, wx * 0.05f, wy * 0.05f);
                var smallN = SampleNoise(smallCave, wx * 0.18f, wy * 0.18f);
                if (depth > 5f && ((bigN > 0.84f && depth > 8f) || smallN > 0.88f))
                {
                    k = TileKind.Sky;
                    // Reservoirs: a slow water-noise channel floods whole cave pockets in the
                    // crust band (below the dirt, above the lava zone that Game1 fills at
                    // ~45% radius) so some caverns are found brimming rather than dry. Water
                    // is seeded as cells and settles to each pocket's floor on its own.
                    if (depth > 10f && depth < 44f
                        && SampleNoise(waterNoise, wx * 0.05f, wy * 0.05f) > 0.62f)
                    {
                        planet.WaterSeeds.Add((r, t));
                    }
                }

                if (k == TileKind.Stone && depth > 12f)
                {
                    var mN = SampleNoise(oreNoise, wx * 0.09f, wy * 0.09f);
                    if (mN > 0.70f && mN < 0.78f) k = TileKind.MossStone;
                }

                if (IsOreHost(k))
                {
                    var oreN = SampleNoise(oreNoise, wx * 0.31f, wy * 0.31f);
                    // Depth bonus lowers thresholds so deeper tiles produce more ore overall.
                    // Rarer tiers (gems) get a smaller bonus to keep them genuinely rare.
                    var boost = MathHelper.Clamp((depth - 6f) / 110f, 0f, 1f) * 0.10f;

                    if (oreN > 0.88f - boost && depth > 6f) k = TileKind.CoalOre;
                    if (oreN > 0.92f - boost && depth > 16f) k = TileKind.IronOre;
                    if (oreN > 0.94f - boost * 0.8f && depth > 30f) k = TileKind.SilverOre;
                    if (oreN > 0.955f - boost * 0.7f && depth > 40f) k = TileKind.GoldOre;
                    if (oreN > 0.965f - boost * 0.6f && depth > 55f) k = TileKind.PlatinumOre;
                    if (oreN > 0.972f - boost * 0.5f && depth > 65f) k = TileKind.Ruby;
                    if (oreN > 0.978f - boost * 0.4f && depth > 75f) k = TileKind.Sapphire;
                    if (oreN > 0.984f - boost * 0.3f && depth > 42f) k = TileKind.Crystal;
                    if (oreN > 0.989f - boost * 0.2f && depth > 95f) k = TileKind.Diamond;
                }

                planet.Set(r, t, k);
            }
        }

        return planet;
    }

    /// <summary>True if <paramref name="ang"/> falls within <paramref name="margin"/> radians
    /// of any mountain's footprint — used to keep lakes off the peaks.</summary>
    private static bool NearMountain((float ang, float h, float w)[] mountains, float ang, float margin)
    {
        foreach (var m in mountains)
        {
            var d = MathF.Abs(ang - m.ang);
            if (d > MathF.PI) d = MathHelper.TwoPi - d;
            if (d < m.w + margin) return true;
        }
        return false;
    }

    /// <summary>Base rocks that ore deposits can replace during world gen.</summary>
    private static bool IsOreHost(TileKind k) => k is
        TileKind.Stone or TileKind.MossStone or
        TileKind.Granite or TileKind.Basalt or TileKind.Obsidian;

    private static float[] MakeAngularNoise(Random rng, int samples)
    {
        var arr = new float[samples];
        for (var i = 0; i < samples; i++) arr[i] = (float)rng.NextDouble() - 0.5f;
        return arr;
    }

    private static float AngularSample(float[] arr, float angle)
    {
        var t = (angle / (MathF.PI * 2f) + 1f) % 1f;
        var f = t * arr.Length;
        var i = (int)f;
        var frac = f - i;
        var a = arr[i % arr.Length];
        var b = arr[(i + 1) % arr.Length];
        return a + (b - a) * frac;
    }

    private static float SampleNoise(float[,] arr, float x, float y)
    {
        var w = arr.GetLength(0);
        var h = arr.GetLength(1);
        x = (x % w + w) % w;
        y = (y % h + h) % h;
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = (x0 + 1) % w;
        var y1 = (y0 + 1) % h;
        var fx = x - x0;
        var fy = y - y0;
        var a = arr[x0, y0] + (arr[x1, y0] - arr[x0, y0]) * fx;
        var b = arr[x0, y1] + (arr[x1, y1] - arr[x0, y1]) * fx;
        return a + (b - a) * fy;
    }
}
