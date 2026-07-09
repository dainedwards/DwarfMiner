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
        // Planet size scales with the def (0.7× dwarf worlds up to 1.8× giants). The sky
        // headroom stays fixed, so the baseline surface (planet.SurfaceRing) scales with it.
        var rings = Math.Max(120, (int)MathF.Round(Planet.StandardRings * def.SizeScale));
        var planet = new Planet(new Vector2(2400, 2400), rings);
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
            // LakeScale > 1 (ocean worlds) makes every basin wider and deeper — at 3× with
            // 8+ lakes the shoreline is mostly sea and dry land the exception.
            lakes[i] = (
                ang,
                depth: (3.5f + (float)rng.NextDouble() * 3.5f) * def.LakeScale,  // 3.5–7 tiles at scale 1
                w: (0.055f + (float)rng.NextDouble() * 0.05f) * def.LakeScale);  // ≈ 3°–6° half-width at scale 1
        }

        // Surface acid pools (acid worlds): carved exactly like lakes but filled with acid
        // cells — open corrosive ponds to bridge or detour around, feeding the same cell
        // sim as the underground pockets.
        var acidPools = new (float ang, float depth, float w)[def.AcidPools];
        for (var i = 0; i < acidPools.Length; i++)
        {
            float ang;
            var tries = 0;
            do
            {
                ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
                tries++;
            } while (tries < 40 && NearMountain(mountains, ang, 0.14f));
            acidPools[i] = (
                ang,
                depth: 3f + (float)rng.NextDouble() * 3f,
                w: 0.05f + (float)rng.NextDouble() * 0.045f);
        }

        var baselineR = planet.SurfaceRing;

        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
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

                    // Acid pool basin — same carve, corrosive fill.
                    var acidDepth = 0f;
                    foreach (var a in acidPools)
                    {
                        var angDiff = MathF.Abs(ang - a.ang);
                        if (angDiff > MathF.PI) angDiff = MathHelper.TwoPi - angDiff;
                        if (angDiff < a.w)
                        {
                            var f = angDiff / a.w;
                            var d = (1f - f * f) * a.depth;
                            if (d > acidDepth) acidDepth = d;
                        }
                    }
                    if (acidDepth > 0.5f && depth < acidDepth)
                    {
                        planet.SetWall(r, t, TileKind.Stone);
                        planet.Set(r, t, TileKind.Sky);
                        if (depth >= 1f) planet.AcidSeeds.Add((r, t));
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
                    if (def.HasWater && depth > 10f && depth < 44f
                        && SampleNoise(waterNoise, wx * 0.05f, wy * 0.05f) > 0.62f)
                    {
                        planet.WaterSeeds.Add((r, t));
                    }

                    // Hazard pockets (planet-gated). Gas collects in the deeper, hotter caves
                    // near the lava zone; acid pools in the mid-crust. Decorrelated noise
                    // channels so a cave isn't both at once. Never overlaps a water reservoir.
                    var isReservoir = planet.WaterSeeds.Count > 0
                        && planet.WaterSeeds[^1] == (r, t);
                    if (!isReservoir)
                    {
                        if (def.SeedsGas && depth > 34f
                            && SampleNoise(pocketNoise, wx * 0.06f + 21f, wy * 0.06f + 21f) > 0.80f)
                            planet.GasSeeds.Add((r, t));
                        else if (def.SeedsAcid && depth > 18f && depth < 78f
                            && SampleNoise(oreNoise, wx * 0.06f + 31f, wy * 0.06f + 31f) > 0.82f)
                            planet.AcidSeeds.Add((r, t));
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
                    // The planet def's OreBias lowers thresholds further for signature ores.
                    var boost = MathHelper.Clamp((depth - 6f) / 110f, 0f, 1f) * 0.10f;
                    float Bias(TileKind ore)
                    {
                        foreach (var (o, b) in def.OreBias) if (o == ore) return b;
                        return 0f;
                    }

                    if (oreN > 0.88f - boost - Bias(TileKind.CoalOre) && depth > 6f) k = TileKind.CoalOre;
                    // Fuel ore sits in a broad shallow band — you need a good stockpile to launch,
                    // so it's common enough to gather without a deep dive.
                    if (oreN > 0.905f - boost - Bias(TileKind.FuelOre) && depth > 12f) k = TileKind.FuelOre;
                    if (oreN > 0.92f - boost - Bias(TileKind.IronOre) && depth > 16f) k = TileKind.IronOre;
                    if (oreN > 0.94f - boost * 0.8f - Bias(TileKind.SilverOre) && depth > 30f) k = TileKind.SilverOre;
                    if (oreN > 0.955f - boost * 0.7f - Bias(TileKind.GoldOre) && depth > 40f) k = TileKind.GoldOre;
                    if (oreN > 0.965f - boost * 0.6f - Bias(TileKind.PlatinumOre) && depth > 55f) k = TileKind.PlatinumOre;
                    if (oreN > 0.972f - boost * 0.5f - Bias(TileKind.Ruby) && depth > 65f) k = TileKind.Ruby;
                    if (oreN > 0.978f - boost * 0.4f - Bias(TileKind.Sapphire) && depth > 75f) k = TileKind.Sapphire;
                    if (oreN > 0.984f - boost * 0.3f - Bias(TileKind.Crystal) && depth > 42f) k = TileKind.Crystal;
                    if (oreN > 0.989f - boost * 0.2f - Bias(TileKind.Diamond) && depth > 95f) k = TileKind.Diamond;
                    // Emerald seams sit deep on the living worlds (verdant/frost carry the
                    // bias). Voidstone's base threshold is unreachable — only the Rift's
                    // bias pulls it into existence, making it the campaign's endgame gem.
                    if (oreN > 0.986f - boost * 0.3f - Bias(TileKind.Emerald) && depth > 80f) k = TileKind.Emerald;
                    if (oreN > 1.05f - Bias(TileKind.Voidstone) && depth > 100f) k = TileKind.Voidstone;
                }

                planet.Set(r, t, k);
            }
        }

        SeedBiomePockets(planet, def, rng);

        return planet;
    }

    /// <summary>Underground biomes: hand-carved pockets stamped after the main pass.
    /// <b>Crystal caverns</b> — mid-depth cavities lined with crystal, a glittering find
    /// worth a detour. <b>Fungal groves</b> — shallow caves walled in moss with glowshrooms
    /// sprouting from the floor, natural light wells on the living worlds. Counts come from
    /// the planet def.</summary>
    private static void SeedBiomePockets(Planet planet, PlanetDef def, Random rng)
    {
        void Carve(int count, bool crystal)
        {
            for (var i = 0; i < count; i++)
            {
                var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
                // Crystal runs deep (rings 30-70 below baseline); groves stay shallow (8-30).
                var depth = crystal ? 30 + rng.Next(40) : 8 + rng.Next(22);
                var cr = BaselineSurfaceRing - depth;
                if (cr < 8) continue;
                var radius = crystal ? 4 + rng.Next(4) : 3 + rng.Next(3);
                var n = planet.TilesAt(cr);
                var ct = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                var centre = planet.TileToWorld(cr, ct);

                // Carve the cavity, then line the shell one tile beyond it.
                var lineR = (radius + 1) * Planet.TileSize;
                for (var dr = -radius - 1; dr <= radius + 1; dr++)
                {
                    var r = cr + dr;
                    if (r < 2 || r >= planet.Rings) continue;
                    var rn = planet.TilesAt(r);
                    var rt0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * rn);
                    var span = radius + 2;
                    for (var dt = -span; dt <= span; dt++)
                    {
                        var t = ((rt0 + dt) % rn + rn) % rn;
                        var pos = planet.TileToWorld(r, t);
                        var dist = (pos - centre).Length();
                        if (dist > lineR) continue;
                        var k = planet.Get(r, t);
                        if (Tiles.IsAnchored(k)) continue;
                        if (dist <= radius * Planet.TileSize)
                        {
                            planet.Set(r, t, TileKind.Sky);
                        }
                        else if (k != TileKind.Sky)
                        {
                            // Cavern shells line with crystal; grove shells with moss, and
                            // the inward (floor) side sprouts glowshrooms.
                            planet.Set(r, t, crystal ? TileKind.Crystal
                                : dr < 0 && rng.Next(3) == 0 ? TileKind.Glowshroom
                                : TileKind.MossStone);
                        }
                    }
                }
            }
        }

        Carve(def.CrystalPockets, crystal: true);
        Carve(def.FungalPockets, crystal: false);
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
