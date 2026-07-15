using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Polar world generation. The planet's natural outer edge is essentially circular (very mild
/// elev variation only). Mountains are placed as a small set of explicit massifs at random
/// angles — a main peak flanked by shoulder peaks, with a high-frequency ridge noise cragging
/// the silhouette — so ranges read as jagged ridgelines with foothills rather than lone
/// spikes; tall peaks on wet worlds carry snow caps over a granite-veined body. Volcanic
/// worlds additionally raise basalt cones whose crater pools connect through a primed throat
/// to a deep magma chamber (see <see cref="CarveVolcanoes"/>). Underground: deep dirt layer,
/// then stone with sparse caves, dirt seams, gravel pockets, and ores, running all the way
/// down to the core ball the renderer draws inside the innermost ring.
/// </summary>
public static class WorldGen
{
    /// <summary>Surface flora/trees/water-plants scatter. On by default; the headless SimTest
    /// turns it OFF so the titan/defense mechanic tests run on a clean, tree-free surface
    /// (decorative vegetation isn't part of what those tests exercise).</summary>
    public static bool ScatterVegetation = true;

    /// <summary>Legacy overload — the starter planet's tuning (used by SimTest).</summary>
    public static Planet Generate(int seed) => Generate(seed, PlanetDefs.All[0]);

    /// <summary>Feature sizes below are authored in legacy 8-px tile units; S converts them
    /// to today's finer rings wherever a value meets ring indices. Depth thresholds instead
    /// divide the ring depth back into legacy units (see <c>depth</c>), and noise is sampled
    /// at 8-px world pitch — so the generated worlds are geometrically identical to the old
    /// coarse grid, just carved at 4× the tile resolution.</summary>
    private const float S = Planet.LegacyTileScale;

    public static Planet Generate(int seed, PlanetDef def)
    {
        // Planet size scales with the def (0.7× dwarf worlds up to 1.8× giants). The sky
        // headroom stays fixed, so the baseline surface (planet.SurfaceRing) scales with it.
        var planet = new Planet(new Vector2(2400, 2400), Planet.RingsFor(def.SizeScale))
        {
            GravityScale = def.GravityScale,
            Airless = def.Airless,
        };
        var rng = new Random(seed);

        // Subtle surface elevation noise — kept very low so the planet reads as a smooth
        // round circle except where mountain spikes rise. The ridge channel is much finer
        // (≈0.7° per sample) and crags the mountain profiles below.
        var surfA = MakeAngularNoise(rng, 8);
        var surfC = MakeAngularNoise(rng, 128);
        var ridge = MakeAngularNoise(rng, 512);

        // Asteroid lumps: big low-frequency lobes plus a mid-frequency dent channel, so the
        // silhouette reads as a battered potato rather than a lathed sphere. Gated on the
        // def knob so ordinary worlds consume no extra RNG draws (their gen streams — and
        // thus every downstream placement — stay bit-identical).
        float[]? lumpLobes = null, lumpDents = null;
        if (def.Lumpiness > 0f)
        {
            lumpLobes = MakeAngularNoise(rng, 6);
            lumpDents = MakeAngularNoise(rng, 16);
        }
        // Downward-biased on purpose: crests share the fixed sky headroom with mountains,
        // but valleys have the whole crust below them — so the potato shape comes mostly
        // from deep scalloped dips, with modest rises between them.
        float LumpAt(float a) => lumpLobes is null ? 0f
            : (AngularSample(lumpLobes, a) * 1.5f + AngularSample(lumpDents!, a) * 0.5f - 0.3f)
              * def.Lumpiness * S;

        // Explicit mountain placements — each roll is a massif: a main peak flanked by 1-3
        // shoulder peaks at offset angles and reduced heights, so ranges read as ridgelines
        // stepping down into foothills rather than isolated spikes. Peaks flatten into one
        // list; profile sampling takes the max contribution across all of them.
        var massifCount = def.MountainMin + rng.Next(def.MountainExtra + 1);
        var peaks = new List<(float ang, float h, float w)>();
        for (var i = 0; i < massifCount; i++)
        {
            // Per-massif height scale 0.5–1.5 multiplied against a 28–46 base — final
            // heights span ~14–69 legacy tiles (×S rings), a mix of short and tall ranges.
            var baseH = (28f + (float)rng.NextDouble() * 18f) * def.MountainHeightScale * S;
            var mainH = baseH * (0.5f + (float)rng.NextDouble() * 1.0f);
            var mainW = 0.09f + (float)rng.NextDouble() * 0.075f;         // ≈ 5.2°..9.5°
            var ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
            peaks.Add((ang, mainH, mainW));
            var shoulders = 1 + rng.Next(3);
            for (var s = 0; s < shoulders; s++)
            {
                var side = rng.Next(2) == 0 ? -1f : 1f;
                var off = mainW * (0.6f + (float)rng.NextDouble() * 0.9f) * side;
                peaks.Add((
                    ang + off,
                    mainH * (0.3f + (float)rng.NextDouble() * 0.4f),
                    mainW * (0.45f + (float)rng.NextDouble() * 0.4f)));
            }
        }
        var mountains = peaks.ToArray();

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
            // 8+ lakes the shoreline is mostly sea and dry land the exception. Ocean worlds
            // (LakeScale > 2.5) get an extra ×1.6 DEPTH so the seas plunge into real deep
            // water — a genuine dive, home to the big sea monsters — without flooding more
            // of the surface (width still scales only with LakeScale).
            var deepMul = def.LakeScale > 2.5f ? 1.6f : 1f;
            lakes[i] = (
                ang,
                depth: (3.5f + (float)rng.NextDouble() * 3.5f) * def.LakeScale * deepMul,  // 3.5–7 tiles at scale 1
                w: (0.055f + (float)rng.NextDouble() * 0.05f) * def.LakeScale);  // ≈ 3°–6° half-width at scale 1
        }

        // Surface acid pools (acid worlds): carved exactly like lakes but filled with acid
        // cells — open corrosive ponds to bridge or detour around. These (plus acid volcanoes
        // and acid-rain storms) are the ONLY acid a world gets now: the old underground cave
        // seeps were removed because acid, being non-depleting, poured out of an open cave and
        // dissolved the whole crust (melting + frame-rate death).
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

        // Impact craters (the belt asteroid): dry bowls scooped out of the surface exactly
        // like lake basins, but left empty — the meteor-blasted look. Wider and shallower
        // than lakes so they read as scars, not pits.
        var craters = new (float ang, float depth, float w)[def.Craters];
        for (var i = 0; i < craters.Length; i++)
        {
            float ang;
            var tries = 0;
            do
            {
                ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
                tries++;
            } while (tries < 40 && NearMountain(mountains, ang, 0.14f));
            craters[i] = (
                ang,
                depth: 3f + (float)rng.NextDouble() * 4f,
                w: 0.07f + (float)rng.NextDouble() * 0.07f);
        }

        var baselineR = planet.SurfaceRing;

        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
            for (var t = 0; t < n; t++)
            {
                var ang = (t + 0.5f) / n * MathHelper.TwoPi;

                // Very subtle surface variation — at most ±1.5 legacy tiles — plus, on
                // lumpy worlds, the asteroid lobes (whole-percent radius swings).
                var elev = AngularSample(surfA, ang) * 2f * S + LumpAt(ang);

                // Mountain height at this angle: max contribution across all peaks. The
                // pow-1.7 falloff keeps a sharp summit but flares wider at the base than
                // the old parabola, and the ridge noise crags the slope (±22%) so the
                // silhouette steps down in jagged shelves instead of a clean curve.
                var mountainHeight = 0f;
                foreach (var m in mountains)
                {
                    var angDiff = MathF.Abs(ang - m.ang);
                    if (angDiff > MathF.PI) angDiff = MathHelper.TwoPi - angDiff;
                    if (angDiff < m.w)
                    {
                        var contribution = MathF.Pow(1f - angDiff / m.w, 1.7f) * m.h;
                        if (contribution > mountainHeight) mountainHeight = contribution;
                    }
                }
                if (mountainHeight > 0.5f)
                    mountainHeight *= 1f + AngularSample(ridge, ang) * 0.45f;

                var surfaceR = baselineR + elev;
                var peakR = surfaceR + mountainHeight;

                // Above the mountain peak (or above the smooth surface): Sky.
                if (r > peakR)
                {
                    planet.Set(r, t, TileKind.Sky);
                    continue;
                }

                // Mountain body — rock above the planet's natural surface: a stone shell
                // over noise-veined granite, and a snow cap on the outer skin above the
                // snow line wherever the world is wet enough to hold one (frost peaks are
                // white all the way down; hot/dead worlds stay bare crag).
                if (r > surfaceR)
                {
                    var mpos = planet.TileToWorld(r, t);
                    var body = SampleNoise(pocketNoise,
                        (mpos.X - planet.Center.X) / (Planet.TileSize * S) * 0.11f,
                        (mpos.Y - planet.Center.Y) / (Planet.TileSize * S) * 0.11f);
                    var mk = body > 0.62f ? TileKind.Granite : TileKind.Stone;
                    var snowy = def.SurfaceTile == TileKind.Snow
                        || (def.HasWater && r - surfaceR > 20f * S * def.MountainHeightScale);
                    if (snowy && r > peakR - 2.5f * S) mk = TileKind.Snow;
                    planet.SetWall(r, t, TileKind.Stone);
                    planet.Set(r, t, mk);
                    continue;
                }

                // Below the surface: layered ground. Depth is measured in legacy 8-px tile
                // units so every threshold below keeps its original world-space meaning.
                var depth = (surfaceR - r) / S;

                // How deep the acid pool centred nearest this bearing reaches here (0 = none).
                // Hoisted out of the basin carve so the cave pass below can leave a solid buffer
                // under and around every pool — an acid pond that connects to the cave network
                // floods the whole crust (acid is non-depleting), so pools must stay sealed bowls.
                var acidDepth = 0f;
                foreach (var a in acidPools)
                {
                    var ad = MathF.Abs(ang - a.ang);
                    if (ad > MathF.PI) ad = MathHelper.TwoPi - ad;
                    if (ad < a.w)
                    {
                        var f = ad / a.w;
                        var d = (1f - f * f) * a.depth;
                        if (d > acidDepth) acidDepth = d;
                    }
                }

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
                    if (acidDepth > 0.5f && depth < acidDepth)
                    {
                        planet.SetWall(r, t, TileKind.Stone);
                        planet.Set(r, t, TileKind.Sky);
                        if (depth >= 1f) planet.AcidSeeds.Add((r, t));
                        continue;
                    }

                    // Impact crater — same carve again, but a dry bowl: nothing pours in.
                    var craterDepth = 0f;
                    foreach (var c in craters)
                    {
                        var cd = MathF.Abs(ang - c.ang);
                        if (cd > MathF.PI) cd = MathHelper.TwoPi - cd;
                        if (cd < c.w)
                        {
                            var f = cd / c.w;
                            var d = (1f - f * f) * c.depth;
                            if (d > craterDepth) craterDepth = d;
                        }
                    }
                    if (craterDepth > 0.5f && depth < craterDepth)
                    {
                        planet.SetWall(r, t, TileKind.Stone);
                        planet.Set(r, t, TileKind.Sky);
                        continue;
                    }
                }

                // Solid buffer wrapping every acid pool: no cave may open under or beside a pool
                // (a shallow bowl needs only a few tiles of rock beneath it), so the acid can
                // never find a cave to pour down into. LineAcidReservoirs skins this buffer in
                // obsidian afterwards.
                var acidBuffer = acidDepth > 0.5f && depth < acidDepth + 12f;

                var pos = planet.TileToWorld(r, t);
                var wx = (pos.X - planet.Center.X) / (Planet.TileSize * S);
                var wy = (pos.Y - planet.Center.Y) / (Planet.TileSize * S);

                TileKind k;
                if (depth < 1f)
                {
                    // Banded worlds (the debug planet) cycle their ground cover through every
                    // biome's surface tile, one wedge per band, so all of them are walkable
                    // on a single world; ordinary worlds paint one tile everywhere.
                    k = def.SurfaceBands is { Length: > 0 } bands
                        ? bands[(int)(ang / MathHelper.TwoPi * bands.Length) % bands.Length]
                        : def.SurfaceTile;
                }
                else if (depth < 10f + AngularSample(surfC, ang) * 2f)
                {
                    // Airless rock has no soil: the belt asteroid's and the cratered moon's
                    // sub-surface band is loose regolith (gravel), not dirt — nothing ever
                    // lived or rotted here.
                    k = def.Biome is "belt" or "moon" ? TileKind.Gravel : TileKind.Dirt;
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

                // Buried dirt seams + gravel pockets in the mid-stone band (all-gravel on
                // the dirtless airless worlds).
                if (k == TileKind.Stone && depth > 8f && depth < 30f)
                {
                    var pN = SampleNoise(pocketNoise, wx * 0.13f, wy * 0.13f);
                    if (pN > 0.84f) k = def.Biome is "belt" or "moon" ? TileKind.Gravel : TileKind.Dirt;
                    else if (pN > 0.78f) k = TileKind.Gravel;
                }

                // Wall captures the structural material before caves/ores override the foreground.
                planet.SetWall(r, t, k);

                var bigN = SampleNoise(bigCave, wx * 0.05f, wy * 0.05f);
                var smallN = SampleNoise(smallCave, wx * 0.18f, wy * 0.18f);
                if (!acidBuffer && depth > 5f && ((bigN > 0.84f && depth > 8f) || smallN > 0.88f))
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

                    // Gas collects in the deeper, hotter caves near the lava zone. (Underground
                    // acid pockets are gone: a seep in an open cave floods the whole crust
                    // — acid is non-depleting — so acid worlds keep their acid to the sealed
                    // surface pools, the acid volcano's plumbing, and acid-rain storms.)
                    var isReservoir = planet.WaterSeeds.Count > 0
                        && planet.WaterSeeds[^1] == (r, t);
                    if (!isReservoir && def.SeedsGas && depth > 34f
                        && SampleNoise(pocketNoise, wx * 0.06f + 21f, wy * 0.06f + 21f) > 0.80f)
                        planet.GasSeeds.Add((r, t));

                    // Oil sumps: mid-crust black pools, shallower than the gas band so a
                    // burning tunnel doesn't automatically chain both. Inert until lit.
                    var isGasPocket = planet.GasSeeds.Count > 0 && planet.GasSeeds[^1] == (r, t);
                    if (!isReservoir && !isGasPocket && def.SeedsOil && depth > 14f && depth < 42f
                        && SampleNoise(pocketNoise, wx * 0.055f - 33f, wy * 0.055f - 33f) > 0.815f)
                        planet.OilSeeds.Add((r, t));
                }

                if (k == TileKind.Stone && depth > 12f)
                {
                    var mN = SampleNoise(oreNoise, wx * 0.09f, wy * 0.09f);
                    if (mN > 0.70f && mN < 0.78f) k = TileKind.MossStone;
                }

                if (IsOreHost(k))
                {
                    var baseRock = k;
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
                    // Depth gradient for the RARE finds (precious metals + gems): 0 at the
                    // surface, 1 in the deep crust. A shallow tile pays a threshold PENALTY, so
                    // these are common deep, present through the mid layers, and only very rarely
                    // near the top — a smooth gradient instead of a hard depth cutoff, so the
                    // lower depths are where the real treasure is.
                    var depthFrac = MathHelper.Clamp((depth - 8f) / 140f, 0f, 1f);
                    var shallowMetal = (1f - depthFrac) * 0.075f;
                    var shallowGem = (1f - depthFrac) * 0.06f;

                    if (oreN > 0.88f - boost - Bias(TileKind.CoalOre) && depth > 6f) k = TileKind.CoalOre;
                    // Fuel ore sits in a broad shallow band — you need a good stockpile to launch,
                    // so it's common enough to gather without a deep dive.
                    if (oreN > 0.905f - boost - Bias(TileKind.FuelOre) && depth > 12f) k = TileKind.FuelOre;
                    if (oreN > 0.92f - boost - Bias(TileKind.IronOre) && depth > 16f) k = TileKind.IronOre;
                    // Silver and gold are precious but no longer confined to bias worlds — every
                    // world now carries thin deep seams of them (so the alien homeworld has them
                    // too), just RARER than iron and the other common ores. A coarse selector
                    // noise splits the deep crust into gold country vs silver country, so a given
                    // region tends to hold one metal, not both — gold and silver rarely mix. The
                    // def bias still fattens the signature worlds' veins on top of this baseline.
                    // Precious metals are DEEP finds: higher base thresholds + a deeper minimum
                    // depth keep them scarce in the upper layers, while a stronger depth boost
                    // keeps them properly findable down in the crust — so gold/silver reward
                    // digging rather than littering the shallows.
                    var metalSel = SampleNoise(oreNoise, wx * 0.031f, wy * 0.031f);
                    if (metalSel < 0.5f && oreN > 0.955f - boost * 0.85f - Bias(TileKind.SilverOre) && depth > 46f) k = TileKind.SilverOre;
                    if (metalSel >= 0.5f && oreN > 0.962f - boost * 0.85f - Bias(TileKind.GoldOre) && depth > 56f) k = TileKind.GoldOre;
                    // Platinum rides its OWN noise field (offset + different frequency) so its
                    // veins are decorrelated from the common-ore noise — platinum no longer
                    // shares a seam with iron/gold/silver, it forms its own isolated pockets.
                    var platN = SampleNoise(oreNoise, wx * 0.53f + 1234.5f, wy * 0.53f - 987.6f);
                    if (platN > 0.978f - boost * 0.5f - Bias(TileKind.PlatinumOre) && depth > 55f) k = TileKind.PlatinumOre;

                    // Gems are not blocks: a gem site keeps its host tile (whatever common
                    // rock/ore the cascade above chose) and seats a gem overlay inside it —
                    // except the precious metals, which stay pure veins (a gem never rides
                    // silver/gold/platinum; the host reverts to plain rock instead). Gems are
                    // now MUCH rarer — the thresholds are pushed high and only every 8th
                    // qualifying site actually seats one (below) — and CRYSTALS are rarer still
                    // than the cut gems, the scarcest thing in the crust bar voidstone.
                    var gem = TileKind.Sky;
                    if (oreN > 0.985f - boost * 0.4f - Bias(TileKind.Ruby) && depth > 65f) gem = TileKind.Ruby;
                    if (oreN > 0.988f - boost * 0.35f - Bias(TileKind.Sapphire) && depth > 75f) gem = TileKind.Sapphire;
                    if (oreN > 0.990f - boost * 0.3f - Bias(TileKind.Emerald) && depth > 80f) gem = TileKind.Emerald;
                    if (oreN > 0.992f - boost * 0.25f - Bias(TileKind.Diamond) && depth > 95f) gem = TileKind.Diamond;
                    // Crystal is now the rarest of the ambient gems — its threshold sits above
                    // every cut gem, so a raw crystal is a scarcer find than a diamond.
                    if (oreN > 0.994f - boost * 0.2f - Bias(TileKind.Crystal) && depth > 60f) gem = TileKind.Crystal;
                    // Voidstone's base threshold is unreachable — only the Rift's bias pulls it
                    // into existence, making it the campaign's endgame gem.
                    if (oreN > 1.05f - Bias(TileKind.Voidstone) && depth > 100f) gem = TileKind.Voidstone;
                    if (gem != TileKind.Sky && (((r * 73856093) ^ (t * 19349663)) & 7) == 0)
                    {
                        if (k is TileKind.SilverOre or TileKind.GoldOre or TileKind.PlatinumOre)
                            k = baseRock;
                        planet.SetGem(r, t, gem);
                    }
                }

                planet.Set(r, t, k);
            }
        }

        // Stamp the terrain line (baseline + elevation/lumps, mountains excluded) so
        // depth-below-surface reads against the LOCAL ground — on a lumpy asteroid a
        // valley floor under open sky must count as the surface, not as underground.
        {
            const int samples = 720;
            var profile = new float[samples];
            for (var i = 0; i < samples; i++)
            {
                var a = (i + 0.5f) / samples * MathHelper.TwoPi;
                profile[i] = baselineR + AngularSample(surfA, a) * 2f * S + LumpAt(a);
            }
            planet.SurfaceProfile = profile;
        }

        SeedBiomePockets(planet, def, rng);
        if (def.GreatGeode) CarveGreatGeode(planet, rng);

        // Volcanoes stamp last so their plumbing (throat lining, chamber shell) wins over
        // any cave or pocket it crosses. Keep them off the lake/pool basins. Each stamping
        // pass appends its own footprints to the shared avoid list, so towers keep off the
        // volcano flanks and lizard-city shafts keep off both.
        var blocked = new List<(float ang, float w)>();
        foreach (var l in lakes) blocked.Add((l.ang, l.w));
        foreach (var a in acidPools) blocked.Add((a.ang, a.w));
        CarveVolcanoes(planet, def, rng, mountains, blocked);
        RaiseCity(planet, def, rng, mountains, blocked);
        // One civilisation per planet, enforced here regardless of what the def says: a
        // world with an alien city never also hides lizardman warrens — the lizardmen
        // survive only where nobody built over them.
        if (def.CityLots <= 0)
            CarveLizardCities(planet, def, rng, mountains, blocked);

        // Noita-style connectivity: winding worm tunnels stitch the noise caves into one
        // traversable network. Carved after every architecture stamp so the den/district
        // halos are known (worms leave plugs there), on an INDEPENDENT rng so the shared
        // stream — and with it every seeded world layout — stays byte-identical whether or
        // not a future change re-tunes the worms. LineAcidReservoirs runs after, re-skinning
        // anything a worm grazed.
        CarveWormTunnels(planet, def, new Random(seed ^ 0x5EED));

        // The prospector's jackpot: the odd RICH gold/silver vein — a solid ribbon of ore
        // far denser than the ambient scatter (which runs deliberately lean, gold most of
        // all). Isolated rng for the same stream-stability reason as the worms.
        StampRichVeins(planet, new Random(seed ^ 0x60D5));

        // Biome flora: dot the surface with the world's signature plant (fire/acid-proof on
        // the hostile worlds). Isolated rng, same stream-stability reason.
        if (ScatterVegetation)
        {
            ScatterBiomeFlora(planet, def, new Random(seed ^ 0xF10A));

            // Alien trees (harvest the trunks for WOOD) and shallow-water plants. Density is
            // by world: living/ocean worlds are forested, the hostile/dead worlds have sparse
            // groves, and truly airless rock (belt/moon) grows no trees at all. Isolated rng
            // for stream stability, like the flora above.
            ScatterTrees(planet, def, new Random(seed ^ 0x77EE));
            ScatterWaterPlants(planet, def, new Random(seed ^ 0x5EA1));
        }

        // Skin every acid reservoir (surface pools, volcano plumbing, and the scattered crust
        // seeps) in obsidian so the acid can't chew outward through the crust. Obsidian shrugs
        // off both acid and lava but is still mineable/blastable, so the pools stay contained
        // yet remain breachable — and, crucially, hemmed-in acid falls asleep once it settles
        // instead of corroding forever, which is what melts the whole acid world and tanks the
        // frame rate.
        LineAcidReservoirs(planet);

        return planet;
    }

    /// <summary>Obsidian-line the walls of every pocket of air that holds acid. A multi-source
    /// flood starts at every acid seed and spreads a few tiles through ENCLOSED air only —
    /// stopping at open atmosphere (a Sky background wall) so it never spills out of a pool's
    /// open mouth and skins the whole surface — converting each corrodible solid tile it borders
    /// into acid-proof (and lava-proof) obsidian. Anchored tiles and existing obsidian are left
    /// alone. The shallow depth budget keeps it hugging the reservoir instead of glassing entire
    /// cave systems that merely touch an acid seep.</summary>
    /// <summary>The signature surface plant for a biome, or Sky for worlds that grow none
    /// (airless rock, the city's paved districts, the rift's dead ground).</summary>
    private static TileKind FloraFor(string biome) => biome switch
    {
        "verdant" => TileKind.Fernleaf,
        "frost"   => TileKind.Frostcap,
        "ember"   => TileKind.Emberbloom,
        "slag"    => TileKind.Rustbramble,
        "acid"    => TileKind.Vitrilily,
        "crystal" => TileKind.Geobloom,
        _         => TileKind.Sky,   // ocean/city/rift/belt/moon/debug: no scattered flora
    };

    /// <summary>Scatter the biome's signature plant across the open surface: for a spread of
    /// bearings, drop from the sky to the first solid ground tile and, if it's walkable soil
    /// under open sky (not a mountain wall, lake, or building), seat a plant in the air tile
    /// just above it. The plants are anchored + fire/lava-proof + acid-proof (see the tile
    /// flags), so the ember bloom survives its lava world and the vitriol lily its acid one —
    /// the "give the plants resistance on hostile worlds" ask.</summary>
    private static void ScatterBiomeFlora(Planet planet, PlanetDef def, Random rng)
    {
        var flora = FloraFor(def.Biome);
        if (flora == TileKind.Sky) return;

        // One roll per bearing across the whole circumference; density ~35% so the surface
        // reads as dotted with plants, not carpeted.
        var bearings = 360 + rng.Next(120);
        for (var b = 0; b < bearings; b++)
        {
            if (rng.Next(100) >= 35) continue;
            var ang = (b + (float)rng.NextDouble()) / bearings * MathHelper.TwoPi;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            // Walk down from well above the surface to the first solid tile.
            var topR = planet.SurfaceRing + 30;
            TileKind ground = TileKind.Sky;
            var groundR = -1;
            for (var r = topR; r > planet.SurfaceRing - 24; r--)
            {
                var n = planet.TilesAt(r);
                var t = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                var k = planet.Get(r, t);
                if (k == TileKind.Sky) continue;
                ground = k; groundR = r;
                break;
            }
            if (groundR < 0) continue;
            // Only seat plants on natural walkable soil — never on rock walls, built tiles,
            // ore, obsidian linings, or anything already occupied.
            if (ground is not (TileKind.Grass or TileKind.Dirt or TileKind.Snow
                or TileKind.Gravel or TileKind.MossStone or TileKind.Basalt)) continue;
            var an = planet.TilesAt(groundR + 1);
            var at = (int)((ang / MathHelper.TwoPi + 1f) % 1f * an);
            if (planet.Get(groundR + 1, at) != TileKind.Sky) continue;
            planet.Set(groundR + 1, at, flora);
        }
    }

    /// <summary>Trees per world: how many of the ~500 bearings grow a tree (0-100 scale),
    /// and which canopy tone. Airless rock grows none; the dead/hostile worlds are sparse;
    /// the living and ocean worlds are properly forested.</summary>
    /// <summary>Which tree silhouettes a biome grows, as weights over the four species
    /// (0 spire, 1 broad, 2 umbrella, 3 weeping). Every world type gets a recognisably
    /// DIFFERENT forest: frost grows tall conifer-like spires, verdant lush broad crowns and
    /// weepers, the burnt/toxic worlds bare umbrella boles, the crystal world glassy spires.</summary>
    private static byte TreeSpeciesFor(string biome, Random rng)
    {
        var w = biome switch
        {
            "verdant" => new[] { 1, 4, 1, 3 },
            "ocean"   => new[] { 1, 3, 1, 4 },
            "frost"   => new[] { 6, 1, 1, 1 },
            "crystal" => new[] { 4, 1, 3, 1 },
            "acid"    => new[] { 1, 1, 5, 1 },
            "ember"   => new[] { 1, 1, 5, 1 },
            "slag"    => new[] { 1, 4, 2, 1 },
            "city"    => new[] { 1, 4, 1, 2 },
            _         => new[] { 1, 1, 1, 1 },
        };
        var total = w[0] + w[1] + w[2] + w[3];
        var roll = rng.Next(total);
        for (byte s = 0; s < 4; s++) { if (roll < w[s]) return s; roll -= w[s]; }
        return 1;
    }

    private static (int density, TileKind canopy) TreePlanFor(PlanetDef def)
    {
        if (def.Airless) return (0, TileKind.TreeCanopy);        // belt / moon: no wood
        return def.Biome switch
        {
            "verdant" => (26, TileKind.TreeCanopy),
            "ocean"   => (20, TileKind.TreeCanopy),
            "frost"   => (12, TileKind.TreeCanopy2),
            "crystal" => (12, TileKind.TreeCanopy2),
            "acid"    => (7, TileKind.TreeCanopy2),
            "ember"   => (5, TileKind.TreeCanopy2),
            "slag"    => (4, TileKind.TreeCanopy),               // barren: far less wood
            "city"    => (10, TileKind.TreeCanopy),
            _         => (12, TileKind.TreeCanopy),
        };
    }

    /// <summary>Scatter alien trees across the surface: a SLENDER single-tile bole rising tall
    /// (6-16 tiles) in one of four silhouettes — spire, broad, umbrella, weeping — topped with
    /// the species' canopy and anchored by roots threaded into the soil. The trunk drops WOOD
    /// when chopped; felling the base topples the crown to pick-up-able dust; the roots survive
    /// to regrow the tree. Density and canopy tone vary by world (airless rock grows none).
    /// Each planted tree is registered as a <see cref="TreeSite"/> so the ecosystem can tend it.</summary>
    private static void ScatterTrees(Planet planet, PlanetDef def, Random rng)
    {
        var (density, canopy) = TreePlanFor(def);
        if (density <= 0) return;
        var bearings = 500 + rng.Next(140);
        var lastT = -999;
        var lastRing = -1;
        for (var b = 0; b < bearings; b++)
        {
            if (rng.Next(100) >= density) continue;
            var ang = (b + (float)rng.NextDouble()) / bearings * MathHelper.TwoPi;
            // Find the ground (topmost solid) on this bearing.
            var groundR = -1;
            for (var r = planet.SurfaceRing + 30; r > planet.SurfaceRing - 24; r--)
            {
                var n = planet.TilesAt(r);
                var t = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                if (planet.Get(r, t) == TileKind.Sky) continue;
                groundR = r;
                break;
            }
            if (groundR < 0) continue;
            var gn = planet.TilesAt(groundR);
            var gt = (int)((ang / MathHelper.TwoPi + 1f) % 1f * gn);
            var ground = planet.Get(groundR, gt);
            if (ground is not (TileKind.Grass or TileKind.Dirt or TileKind.Snow
                or TileKind.MossStone or TileKind.Gravel or TileKind.Basalt)) continue;
            // Don't crowd trunks: keep a couple of tiles between neighbours.
            if (lastRing == groundR && Math.Abs(gt - lastT) < 3) continue;

            // Species drives height and silhouette, and is chosen per BIOME so each world type
            // grows its own kind of tree (frost = spires, verdant = broad/weeping, etc). Tall
            // and thin across the board — even the "short" broad trees stand several tiles,
            // spires/umbrellas soar, and roughly one in six of any species is a GIANT (a big
            // height bonus) so the canopy line is ragged with the odd towering old-growth alien.
            var species = TreeSpeciesFor(def.Biome, rng);
            var trunkH = species switch
            {
                0 => 12 + rng.Next(10),  // spire   — the tallest, a thin plume on top
                2 => 11 + rng.Next(8),   // umbrella — long bare bole under a flat cap
                3 => 9 + rng.Next(7),    // weeping  — tall, draping crown
                _ => 7 + rng.Next(6),    // broad    — shortest, still a proper trunk
            };
            if (rng.Next(6) == 0) trunkH += 8 + rng.Next(10);   // the occasional giant
            // Keep the crown (up to ~5 rings of canopy) inside the sky.
            trunkH = Math.Min(trunkH, planet.Rings - 6 - groundR);
            if (trunkH < 4) continue;

            // The trunk column must be clear sky all the way up (canopy may overlap terrain).
            var clear = true;
            for (var h = 1; h <= trunkH && clear; h++)
            {
                var rr = groundR + h;
                if (rr >= planet.Rings - 1) { clear = false; break; }
                var nn = planet.TilesAt(rr);
                if (planet.Get(rr, (int)((ang / MathHelper.TwoPi + 1f) % 1f * nn)) != TileKind.Sky)
                    clear = false;
            }
            if (!clear) continue;
            lastRing = groundR; lastT = gt;

            var site = new TreeSite
            {
                Angle = ang,
                GroundR = groundR,
                Species = species,
                Height = (byte)trunkH,
                // Mostly the biome canopy, with the occasional off-tone tree for variety.
                Canopy = rng.Next(4) == 0
                    ? (canopy == TileKind.TreeCanopy ? TileKind.TreeCanopy2 : TileKind.TreeCanopy)
                    : canopy,
            };
            Systems.TreeEcology.Plant(planet, site);
            planet.Trees.Add(site);
        }
    }

    /// <summary>Scatter waving water plants (SeaFrond) on the shallow lakebeds of any world
    /// that has water — rooted on the solid floor just under the surface of a pool.</summary>
    private static void ScatterWaterPlants(Planet planet, PlanetDef def, Random rng)
    {
        if (!def.HasWater || planet.WaterSeeds.Count == 0) return;
        var basin = new System.Collections.Generic.HashSet<(int, int)>(planet.WaterSeeds);
        // For each basin tile that sits on the floor (solid below, water/basin here), root a
        // frond with a small chance. Cheap: one pass over the seeds, no per-bearing walk.
        foreach (var (x, y) in planet.WaterSeeds)
        {
            if (rng.Next(100) >= 8) continue;                    // sparse
            if (planet.Get(x, y) != TileKind.Sky) continue;      // must be an open water column
            if (basin.Contains((x - 1, y))) continue;            // not on the very floor → skip
            if (Tiles.IsSolid(planet.Get(x - 1, y)))             // solid lakebed directly below
                planet.Set(x, y, TileKind.SeaFrond);
        }
    }

    /// <summary>Rich metal veins: every world has a coin-flip shot at ONE concentrated
    /// gold or silver ribbon (and a slim shot at a second) buried in the deep crust — a
    /// wandering walk that converts plain rock to solid ore in a narrow band. This is the
    /// jackpot that makes prospecting pay: the ambient charted scatter stays lean (gold
    /// especially), but striking a rich vein sets you up for a whole flight home. Never
    /// overwrites anchored tiles, ores, gems, or obsidian — only common rock.</summary>
    private static void StampRichVeins(Planet planet, Random rng)
    {
        var veins = rng.Next(2) + (rng.Next(5) == 0 ? 1 : 0);   // 0-1 usual, rarely 2
        for (var v = 0; v < veins; v++)
        {
            var ore = rng.Next(2) == 0 ? TileKind.GoldOre : TileKind.SilverOre;
            var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
            // Deep band: below the casual dig, above the core furniture.
            var frac = 0.55f + (float)rng.NextDouble() * 0.25f;
            var pos = planet.Center
                + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * planet.Radius * frac * Planet.TileSize;
            var heading = (float)rng.NextDouble() * MathHelper.TwoPi;
            var length = 26 + rng.Next(18);
            for (var s = 0; s < length; s++)
            {
                heading += ((float)rng.NextDouble() - 0.5f) * 0.5f;
                pos += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * Planet.TileSize;
                var rad = 4f + (float)rng.NextDouble() * 3f;
                var (cr, ct) = planet.WorldToTile(pos);
                var span = (int)(rad / Planet.TileSize) + 1;
                for (var dr = -span; dr <= span; dr++)
                {
                    var rr = cr + dr;
                    if (rr < 2 || rr >= planet.Rings - 1) continue;
                    var n = planet.TilesAt(rr);
                    var tt0 = (int)((float)ct / planet.TilesAt(Math.Clamp(cr, 0, planet.Rings - 1)) * n);
                    for (var dt2 = -span; dt2 <= span; dt2++)
                    {
                        var tt = ((tt0 + dt2) % n + n) % n;
                        if ((planet.TileToWorld(rr, tt) - pos).LengthSquared() > rad * rad) continue;
                        var k = planet.Get(rr, tt);
                        // Common rock only — never obsidian (acid/volcano linings must stay
                        // sealed), never existing ore/gem, never anchored architecture.
                        if (!IsOreHost(k) || Tiles.IsOre(k) || k == TileKind.Obsidian) continue;
                        if (planet.GemAt(rr, tt) != TileKind.Sky) continue;
                        planet.Set(rr, tt, ore);
                    }
                }
            }
        }
    }

    /// <summary>Noita-style interconnecting tunnels: a couple dozen "perlin worms" wander
    /// the crust carving narrow winding corridors, each with a chance to fork once. The
    /// noise caves give chambers; the worms give the paths BETWEEN them — the difference
    /// between isolated pockets you mine into and a cave system you can travel. Worms stay
    /// below the dirt band (the surface keeps its skin) and above the deep core, never
    /// carve anchored tiles or obsidian (acid/volcano linings stay sealed), and simply
    /// leave those tiles standing as natural dead-ends when they meet one.</summary>
    private static void CarveWormTunnels(Planet planet, PlanetDef def, Random rng)
    {
        // High-lava worlds skip the worms: their habitable band is a thin shell squeezed
        // between the flood line and the surface (the warrens carry the underground feel
        // there), and every tunnel that grazes the lava zone becomes permanent plumbing
        // that keeps the cell sim awake — measured at 3× the steady update budget.
        if (def.LavaFillFrac > 0.5f) return;
        // Dense enough that neighbouring walks intersect constantly — the underground
        // should read as one connected Noita warren, not isolated corridors.
        var worms = 30 + rng.Next(11);
        // Stay above THIS world's lava zone — StartNewRun floods every sky tile below
        // LavaFillFrac×radius (ember-class worlds run 0.55-0.70), and tunnels crossing
        // that line become permanent lava plumbing that wrecks the steady-state cell
        // budget — and below the dirt band.
        // Reach much deeper than before — the cave network now threads the LOWER crust too,
        // not just the upper shell — but still stop a safe margin ABOVE this world's lava fill
        // line (crossing it turns tunnels into permanent lava plumbing that wrecks the cell
        // budget). On a low-lava world that opens the deep half of the crust to caving.
        var minFrac = MathF.Max(0.30f, def.LavaFillFrac + 0.08f);
        var maxTiles = Planet.RingMin + planet.SurfaceRing - 16f * Planet.LegacyTileScale;
        for (var i = 0; i < worms; i++)
        {
            var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
            var radiusTiles = MathHelper.Lerp(planet.Radius * minFrac, maxTiles,
                (float)rng.NextDouble());
            var start = planet.Center
                + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radiusTiles * Planet.TileSize;
            CarveWorm(planet, rng, start, (float)rng.NextDouble() * MathHelper.TwoPi,
                260 + rng.Next(300), branchBudget: 2, minFrac);
        }
    }

    private static void CarveWorm(Planet planet, Random rng, Vector2 pos, float heading,
        int length, int branchBudget, float minFrac)
    {
        var minRad = planet.Radius * (minFrac - 0.02f) * Planet.TileSize;
        var maxRad = (Planet.RingMin + planet.SurfaceRing - 14f * Planet.LegacyTileScale)
                     * Planet.TileSize;
        for (var s = 0; s < length; s++)
        {
            // Meander, but steer back toward the band when drifting out of it — the pull
            // toward/away from the core is what keeps worms lateral (Noita's corridors
            // wind mostly sideways with dips, not straight radial bores).
            heading += ((float)rng.NextDouble() - 0.5f) * 0.55f;
            var rel = pos - planet.Center;
            var dist = rel.Length();
            if (dist > maxRad || dist < minRad)
            {
                var radial = MathF.Atan2(rel.Y, rel.X);
                var desired = dist > maxRad ? radial + MathF.PI : radial;
                heading += MathHelper.WrapAngle(desired - heading) * 0.18f;
            }
            pos += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * Planet.TileSize;
            // Hand-built places keep their architecture: no worm bites inside a warren
            // hall's halo or under a city district — the worm keeps walking and leaves a
            // natural plug where it crossed.
            if (!NearDenOrCity(planet, pos))
                CarveWormDisk(planet, pos, rng.Next(3) == 0 ? 11f : 8f);
            if (branchBudget > 0 && s > length / 4 && rng.Next(55) == 0)
            {
                branchBudget--;
                // Branches fork once more themselves — second-generation forks are what
                // stitch neighbouring worm systems into one continuous warren.
                CarveWorm(planet, rng, pos,
                    heading + (rng.Next(2) == 0 ? 1f : -1f) * (0.8f + (float)rng.NextDouble()),
                    length / 2, length > 80 ? 1 : 0, minFrac);
            }
        }
    }

    /// <summary>True near a lizard-warren hall or under a city district's bearing span —
    /// the two kinds of architecture a worm must not undermine.</summary>
    private static bool NearDenOrCity(Planet planet, Vector2 pos)
    {
        foreach (var (dr, dtIdx) in planet.LizardDens)
            if ((planet.TileToWorld(dr, dtIdx) - pos).LengthSquared() < 90f * 90f) return true;
        if (planet.CityDistricts.Count > 0)
        {
            var rel = pos - planet.Center;
            var a = MathF.Atan2(rel.Y, rel.X);
            foreach (var (ang, half) in planet.CityDistricts)
                if (MathF.Abs(MathHelper.WrapAngle(a - ang)) < half + 0.03f) return true;
        }
        return false;
    }

    /// <summary>One worm step's bite: a small disk of soft tiles → Sky. Anchored tiles and
    /// obsidian stay (containment linings, city foundations, the core).</summary>
    private static void CarveWormDisk(Planet planet, Vector2 centre, float radius)
    {
        var (er, _) = planet.WorldToTile(centre);
        var span = (int)(radius / Planet.TileSize) + 1;
        var rel = centre - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var radiusSq = radius * radius;
        for (var dr = -span; dr <= span; dr++)
        {
            var r = er + dr;
            if (r < 1 || r >= planet.Rings) continue;
            var n = planet.TilesAt(r);
            var t0 = (int)(ang / MathHelper.TwoPi * n);
            for (var dt = -span * 2; dt <= span * 2; dt++)
            {
                var t = ((t0 + dt) % n + n) % n;
                if ((planet.TileToWorld(r, t) - centre).LengthSquared() > radiusSq) continue;
                var k = planet.Get(r, t);
                if (k == TileKind.Sky || Tiles.IsAnchored(k) || k == TileKind.Obsidian) continue;
                planet.Set(r, t, TileKind.Sky);
            }
        }
    }

    private static void LineAcidReservoirs(Planet planet)
    {
        if (planet.AcidSeeds.Count == 0) return;
        const int maxDepth = 12;                 // air tiles to flood out from any acid seed
        var visited = new HashSet<long>();
        long Key(int r, int t) => (long)r * 4_000_000L + (uint)t;

        var frontier = new Queue<(int r, int t, int depth)>();
        foreach (var (r, t) in planet.AcidSeeds)
            if (visited.Add(Key(r, t))) frontier.Enqueue((r, t, 0));

        var nb = new List<(int x, int y)>(12);
        while (frontier.Count > 0)
        {
            var (r, t, d) = frontier.Dequeue();
            var n = planet.TilesAt(r);
            var left = ((t - 1) % n + n) % n;
            var right = (t + 1) % n;

            // Skin every solid tile this air tile — or its cells — can touch. Radial rings hold
            // different tile counts, so an air tile's cells spill into the inner/outer tiles of
            // its ANGULAR neighbours too; lining those as well closes the cell-straddle gaps
            // that otherwise leave a pool floor tile bare at the reservoir's edge.
            nb.Clear();
            foreach (var at in new[] { left, t, right })
            {
                nb.Add(planet.InnerNeighbour(r, at));
                var oc = planet.OuterNeighbourCount(r, at);
                for (var i = 0; i < oc; i++) nb.Add(planet.OuterNeighbour(r, at, i));
            }
            nb.Add((r, left));
            nb.Add((r, right));
            foreach (var (lr, lt) in nb)
            {
                if (lr < 0 || lr >= planet.Rings) continue;
                var k = planet.Get(lr, lt);
                if (Tiles.IsSolid(k) && !Tiles.IsAnchored(k) && k != TileKind.Obsidian)
                {
                    planet.Set(lr, lt, TileKind.Obsidian);
                    planet.SetWall(lr, lt, TileKind.Obsidian);
                }
            }

            // Flood on through enclosed air only (a rock wall behind it); an open-sky background
            // means we've reached the atmosphere, so stop before escaping the reservoir. (nb
            // already holds this tile's direct radial + angular neighbours.)
            if (d >= maxDepth) continue;
            foreach (var (ar, at) in nb)
            {
                if (ar < 0 || ar >= planet.Rings) continue;
                if (Tiles.IsSolid(planet.Get(ar, at))) continue;
                if (planet.GetWall(ar, at) == TileKind.Sky) continue;
                if (!visited.Add(Key(ar, at))) continue;
                frontier.Enqueue((ar, at, d + 1));
            }
        }
    }

    /// <summary>Volcanoes: basalt cones raised on the surface, each holding an open crater
    /// pool that connects through a primed two-tile throat to a deep obsidian-shelled magma
    /// chamber — the lava genuinely rises from a pool far underground, and drilling into the
    /// column anywhere lets it flow out through the breach. Acid worlds (def.VolcanoAcid)
    /// vent vitriol instead; their chambers stay above the global lava-fill line so the two
    /// fluids never share plumbing. Fluid sites are recorded as seeds (LavaSeeds/AcidSeeds)
    /// for Game1 to pour, and each crater registers a vent for periodic eruptions.</summary>
    private static void CarveVolcanoes(Planet planet, PlanetDef def, Random rng,
        (float ang, float h, float w)[] mountains, List<(float ang, float w)> avoid)
    {
        var surfaceR = planet.SurfaceRing;
        var seeds = def.VolcanoAcid ? planet.AcidSeeds : planet.LavaSeeds;
        var placed = new List<(float ang, float w)>();

        for (var v = 0; v < def.Volcanoes; v++)
        {
            var scale = def.VolcanoScale * (0.85f + (float)rng.NextDouble() * 0.3f);
            var coneH = MathF.Min((30f + (float)rng.NextDouble() * 16f) * scale * S, Planet.SkyHeadroom - 6f * S);
            var coneW = (0.13f + (float)rng.NextDouble() * 0.05f) * MathF.Sqrt(scale);

            // Placement: clear of mountains, lake/pool basins, other volcanoes — and the
            // spawn bearing (-π/2), so the rover never drops the dwarf into a crater.
            var ang = 0f;
            var ok = false;
            for (var tries = 0; tries < 80 && !ok; tries++)
            {
                ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
                ok = !NearMountain(mountains, ang, coneW + 0.05f)
                     && AngDist(ang, MathF.PI * 1.5f) > coneW + 0.25f;
                for (var i = 0; ok && i < avoid.Count; i++)
                    ok = AngDist(ang, avoid[i].ang) > coneW + avoid[i].w + 0.04f;
                for (var i = 0; ok && i < placed.Count; i++)
                    ok = AngDist(ang, placed[i].ang) > coneW + placed[i].w + 0.1f;
            }
            if (!ok) continue;
            placed.Add((ang, coneW));

            const float craterFrac = 0.30f;           // crater mouth as a fraction of the footprint
            var craterDepth = coneH * 0.45f;
            var poolTop = coneH - craterDepth * 0.25f; // fluid level — a few tiles below the rim,
                                                       // so eruptions visibly overflow the lip
            var floorR = surfaceR + (int)(coneH - craterDepth);

            // The cone. Profile: full height at the rim (f = craterFrac), a near-linear
            // flank falling to the footprint edge, and a bowl dipping to the crater floor
            // inside the mouth. Basalt body with obsidian around the throat and a loose
            // ash-gravel skin low on the flanks.
            var topR = Math.Min(planet.Rings - 1, surfaceR + (int)coneH + (int)(2 * S));
            for (var r = Math.Max(2, surfaceR - (int)(3 * S)); r <= topR; r++)
            {
                var n = planet.TilesAt(r);
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                var span = (int)(coneW / MathHelper.TwoPi * n) + 2;
                for (var dt = -span; dt <= span; dt++)
                {
                    var t = ((t0 + dt) % n + n) % n;
                    var f = AngDist((t + 0.5f) / n * MathHelper.TwoPi, ang) / coneW;
                    if (f > 1f) continue;
                    var h = f >= craterFrac
                        ? coneH * MathF.Pow((1f - f) / (1f - craterFrac), 1.15f)
                        : coneH - craterDepth * MathF.Pow(1f - f / craterFrac, 1.5f);
                    var above = r - surfaceR;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (above > h)
                    {
                        // Inside the bowl above the floor: open air, fluid up to the pool
                        // line. Above the pool the crater mouth stays open sky.
                        if (f < craterFrac && above <= poolTop)
                        {
                            planet.SetWall(r, t, TileKind.Basalt);
                            planet.Set(r, t, TileKind.Sky);
                            seeds.Add((r, t));
                        }
                        continue;
                    }
                    var k = TileKind.Basalt;
                    // Acid craters get an all-obsidian bowl + rim (a wider band): vitriol pooled
                    // — or slopped above the fill line — in the mouth can't corrode its way out
                    // through the cone the way it eats basalt. Lava craters keep the exact
                    // legacy basalt/obsidian mix (lava can't melt basalt anyway), so their RNG
                    // draw is untouched and downstream placement (warrens, cities) is stable.
                    if (def.VolcanoAcid)
                    {
                        if (f < craterFrac + 0.15f) k = TileKind.Obsidian;
                        else if (f > 0.55f && above > h - 1.5f * S) k = TileKind.Gravel;
                    }
                    else if (f < craterFrac + 0.1f)
                        k = rng.Next(3) == 0 ? TileKind.Obsidian : TileKind.Basalt;
                    else if (f > 0.55f && above > h - 1.5f * S)
                        k = TileKind.Gravel;                       // ash skirt
                    planet.SetWall(r, t, TileKind.Basalt);
                    planet.Set(r, t, k);
                }
            }

            // The magma chamber: an obsidian-shelled pool deep under the cone. Obsidian
            // resists both lava melt and acid corrosion, so the reservoir holds until the
            // player (or a cave-in) breaches it.
            var chamberRad = (int)((4 + rng.Next(3) + scale * 1.5f) * S);
            var chamberR = surfaceR - (int)((55 + rng.Next(26)) * S);
            if (def.VolcanoAcid)
            {
                // Keep acid plumbing above the global lava flood so the two never mix.
                var lavaTop = (int)(planet.Radius * def.LavaFillFrac) - Planet.RingMin;
                chamberR = Math.Max(chamberR, lavaTop + chamberRad + (int)(4 * S));
            }
            chamberR = Math.Max(chamberR, (int)(14 * S) + chamberRad);

            var nC = planet.TilesAt(chamberR);
            var centre = planet.TileToWorld(chamberR, (int)((ang / MathHelper.TwoPi + 1f) % 1f * nC));
            var shellR = (chamberRad + 1) * Planet.TileSize;
            for (var dr = -chamberRad - 1; dr <= chamberRad + 1; dr++)
            {
                var r = chamberR + dr;
                if (r < 2 || r >= planet.Rings) continue;
                var rn = planet.TilesAt(r);
                var rt0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * rn);
                for (var dt = -chamberRad - 2; dt <= chamberRad + 2; dt++)
                {
                    var t = ((rt0 + dt) % rn + rn) % rn;
                    var dist = (planet.TileToWorld(r, t) - centre).Length();
                    if (dist > shellR) continue;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (dist <= chamberRad * Planet.TileSize)
                    {
                        planet.SetWall(r, t, TileKind.Basalt);
                        planet.Set(r, t, TileKind.Sky);
                        seeds.Add((r, t));
                    }
                    else
                    {
                        planet.SetWall(r, t, TileKind.Obsidian);
                        planet.Set(r, t, TileKind.Obsidian);
                    }
                }
            }

            // The throat: a primed channel from the chamber roof up to the crater floor,
            // sleeved in basalt so the soft dirt band it crosses can't slump into it.
            // Span keeps the legacy world width: open channel ≈24 px, 8-px walls each side.
            var throatSpan = (int)(2 * S) + 1;
            for (var r = chamberR + chamberRad - 1; r <= floorR; r++)
            {
                if (r < 2 || r >= planet.Rings) continue;
                var n = planet.TilesAt(r);
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                for (var dt = -throatSpan; dt <= throatSpan; dt++)
                {
                    var t = ((t0 + dt) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    planet.SetWall(r, t, TileKind.Basalt);
                    if (Math.Abs(dt) >= throatSpan - 1)
                    {
                        // Acid throats are obsidian-sleeved so the rising column can't dissolve
                        // its own channel walls and drain into the surrounding crust.
                        planet.Set(r, t, def.VolcanoAcid ? TileKind.Obsidian : TileKind.Basalt);
                    }
                    else
                    {
                        planet.Set(r, t, TileKind.Sky);
                        seeds.Add((r, t));
                    }
                }
            }

            // Vent: just above the pool surface — where eruptions spawn fresh cells.
            var ventR = Math.Min(planet.Rings - 1, surfaceR + (int)poolTop + (int)(2 * S));
            var ventT = (int)((ang / MathHelper.TwoPi + 1f) % 1f * planet.TilesAt(ventR));
            planet.VolcanoVents.Add((ventR, ventT, def.VolcanoAcid));
        }

        // Later stamping passes (city towers, lizard-city shafts) must keep clear of the cones.
        avoid.AddRange(placed);
    }

    /// <summary>City worlds: raise <c>def.CityLots</c> alien skyscrapers on the surface,
    /// grouped as ONE CAPITAL METROPOLIS (~60% of the lots) plus one or two satellite
    /// towns — rows of towers standing shoulder to shoulder with narrow street gaps, not
    /// lone spires scattered around the globe. Each tower is an
    /// anchored alloy hull (buildings don't cave in) with straight px-width sides, storeys of
    /// floor slabs with an alternating stair gap, glowing window bands in the skin, a
    /// street-level doorway, an alloy piling footing, and per-class rooftop furniture.
    /// Doorway and apartment-floor tiles are recorded in <see cref="Planet.CitySpawns"/>
    /// so the spawn director can stock the towers with civilians.</summary>
    private static void RaiseCity(Planet planet, PlanetDef def, Random rng,
        (float ang, float h, float w)[] mountains, List<(float ang, float w)> avoid)
    {
        if (def.CityLots <= 0) return;
        var surfaceR = planet.SurfaceRing;
        var surfRadiusPx = (Planet.RingMin + surfaceR) * Planet.TileSize;
        var placed = new List<(float ang, float w)>();

        // District centres: bearings clear of mountains, basins, volcanoes, the rover
        // drop, and each other. Layout is ONE CAPITAL plus one or two satellite towns —
        // most of the skyline concentrates in a single sprawling metropolis, with the
        // wide mutual spacing keeping the towns from merging into its edges.
        var districtCount = def.CityLots >= 24 ? 2 + rng.Next(2) : 2;
        var centres = new List<float>();
        for (var d = 0; d < districtCount; d++)
        {
            var cAng = 0f;
            var ok = false;
            for (var tries = 0; tries < 90 && !ok; tries++)
            {
                cAng = (float)(rng.NextDouble() * MathHelper.TwoPi);
                ok = !NearMountain(mountains, cAng, 0.16f)
                     && AngDist(cAng, MathF.PI * 1.5f) > 0.3f;
                for (var i = 0; ok && i < avoid.Count; i++)
                    ok = AngDist(cAng, avoid[i].ang) > avoid[i].w + 0.14f;
                for (var i = 0; ok && i < centres.Count; i++)
                    ok = AngDist(cAng, centres[i]) > 0.95f;
            }
            if (ok) centres.Add(cAng);
        }
        if (centres.Count == 0) return;
        // The capital takes ~60% of every lot; the towns split the remainder.
        var lotsOf = new int[centres.Count];
        if (centres.Count == 1)
        {
            lotsOf[0] = def.CityLots;
        }
        else
        {
            lotsOf[0] = (int)(def.CityLots * 0.6f);
            var rest = def.CityLots - lotsOf[0];
            for (var i = 0; i < rest; i++) lotsOf[1 + i % (centres.Count - 1)]++;
        }

        for (var d = 0; d < centres.Count; d++)
        {
            // Roll the whole district's towers first so the row can be centred on the
            // district bearing, then walk a cursor west-to-east: tower, street gap, tower…
            var specs = new List<(double classRoll, float halfWidthPx, int height, float gapPx)>();
            var rowPx = 0f;
            for (var i = 0; i < lotsOf[d]; i++)
            {
                // Proportions are authored in pixels and converted to per-ring angles below,
                // so tower sides stay straight instead of flaring with radius like a
                // constant-angle wedge would. The mix is mostly skyscrapers: a few small
                // shopfront buildings, then mid-rise blocks and spires across a wide height
                // spread so no two neighbours match.
                var classRoll = rng.NextDouble();
                float halfWidthPx;
                int height;
                if (classRoll < 0.18)         // small building: a squat shopfront
                {
                    halfWidthPx = 22f + (float)rng.NextDouble() * 10f;
                    height = (int)((6f + (float)rng.NextDouble() * 6f) * S);
                }
                else if (classRoll < 0.60)    // mid-rise block
                {
                    halfWidthPx = 20f + (float)rng.NextDouble() * 12f;
                    height = (int)((18f + (float)rng.NextDouble() * 16f) * S);
                }
                else                          // spire: thin and tall
                {
                    halfWidthPx = 15f + (float)rng.NextDouble() * 9f;
                    height = (int)((30f + (float)rng.NextDouble() * 26f) * S);
                }
                height = Math.Min(height, (int)(Planet.SkyHeadroom - 16 * S));
                var gapPx = 12f + (float)rng.NextDouble() * 12f;   // a narrow street between hulls
                specs.Add((classRoll, halfWidthPx, height, gapPx));
                rowPx += halfWidthPx * 2f + gapPx;
            }

            var cursor = centres[d] - rowPx / surfRadiusPx * 0.5f;
            foreach (var (classRoll, halfWidthPx, height, gapPx) in specs)
            {
                var footAng = halfWidthPx / surfRadiusPx;
                var ang = cursor + footAng;
                cursor += footAng * 2f + gapPx / surfRadiusPx;

                // Row edges can still run into a mountain flank, a basin, the drop
                // bearing — or, with rows this wide, a neighbouring district's edge.
                // Skip that lot rather than clip anything.
                if (NearMountain(mountains, ang, footAng + 0.02f)) continue;
                if (AngDist(ang, MathF.PI * 1.5f) < footAng + 0.1f) continue;
                var clear = true;
                for (var i = 0; clear && i < avoid.Count; i++)
                    clear = AngDist(ang, avoid[i].ang) > footAng + avoid[i].w + 0.02f;
                for (var i = 0; clear && i < placed.Count; i++)
                    clear = AngDist(ang, placed[i].ang) > footAng + placed[i].w + 0.004f;
                if (!clear) continue;
                placed.Add((ang, footAng));

                BuildTower(ang, classRoll, halfWidthPx, height);
            }
        }

        avoid.AddRange(placed);
        return;

        void BuildTower(float ang, double classRoll, float halfWidthPx, int height)
        {
            var baseR = surfaceR + 1;
            var topR = Math.Min(planet.Rings - 2, baseR + height);
            var floorEvery = (int)(4 * S);            // one storey per 4 legacy tiles (32 px)
            var doorH = (int)(2.5f * S);              // street door: 20 px of headroom
            // Facade style: banded windows (spandrel rows between glass strips) or a sheer
            // curtain wall (glass all the way up, broken only by the slab lines).
            var curtainWall = rng.Next(3) == 0;
            // The foundation runs well past the dirt layer into bedrock (14 legacy tiles),
            // one tile wider than the hull — towers stand on pilings, not on topsoil.
            var footingR = Math.Max(2, surfaceR - (int)(14 * S));

            for (var r = footingR; r <= topR; r++)
            {
                var n = planet.TilesAt(r);
                var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                var halfAng = halfWidthPx / ringRadius;
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                var span = (int)(halfAng / MathHelper.TwoPi * n) + 1;
                var storey = r - baseR;
                var slabRow = storey % floorEvery < 2 && storey >= floorEvery && storey < height - 2;
                // Slab rows carry a one-tile exterior ledge — horizontal ribs that break up
                // the hull. The foundation is a tile wider than the tower for the same span
                // bump, so the plinth reads as a footing, not a buried wall.
                var spanHere = span + (slabRow || r <= surfaceR ? 1 : 0);
                for (var dt = -spanHere; dt <= spanHere; dt++)
                {
                    var t = ((t0 + dt) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    planet.SetWall(r, t, TileKind.AlienAlloy);

                    // Below the baseline surface: solid piling, whatever the local
                    // elevation noise did to the ground line.
                    if (r <= surfaceR) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                    // Roof cap.
                    if (storey >= height - 2) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                    var edge = Math.Abs(dt) >= spanHere - 1;
                    if (edge)
                    {
                        // Street-level doorway (with a glass transom over the lintel) on one
                        // side; windows above the ground floor per the facade style; alloy
                        // hull everywhere else.
                        var window = curtainWall
                            ? storey >= floorEvery && !slabRow
                            : storey >= floorEvery && storey % floorEvery >= 3
                                                   && storey % floorEvery <= floorEvery - 3;
                        if (storey < doorH)
                            // Doorway on BOTH sides at street level: the outer column is a
                            // real working door (closed at gen time — residents and the dwarf
                            // pop it open with E / by walking up), the inner column is open
                            // lobby behind it. Both left and right edges get one now.
                            planet.Set(r, t, Math.Abs(dt) >= spanHere ? TileKind.DoorClosed
                                                                      : TileKind.Sky);
                        else if (storey == doorH)
                            planet.Set(r, t, TileKind.CityGlass);   // glass transom over both doors
                        else
                            planet.Set(r, t, window ? TileKind.CityGlass : TileKind.AlienAlloy);
                        continue;
                    }

                    // Interior: floor slab at the base of every storey above the ground
                    // floor (the plinth is street level's floor), with a stair gap hugging
                    // alternating walls so the shaft zig-zags up the tower.
                    var gapSide = storey / floorEvery % 2 == 0 ? 1 : -1;
                    var slab = slabRow && dt * gapSide < span - 4;

                    // Climbing spine: a full-height ladder shaft dead-centre in the tower —
                    // always reachable from the ground floor and clear of the slab (it's
                    // placed before the slab fill and punches through every floor), so every
                    // storey connects. The stair-gap beside it lets you step off each level.
                    if (dt == 0 && storey >= 0 && storey < height - 2)
                    {
                        planet.Set(r, t, TileKind.Ladder);
                        continue;
                    }
                    // Keep the two columns flanking the ladder open its full height, so the
                    // climb shaft is a clean 3-wide channel: floor slabs stop short of it and
                    // never seal over the ladder, and the dwarf's body clears each storey.
                    // (A 1-tile ladder hole through a 2-tile-thick slab used to wall you in.)
                    if (Math.Abs(dt) <= 1 && storey >= 0 && storey < height - 2)
                    {
                        planet.Set(r, t, TileKind.Sky);
                        continue;
                    }
                    if (slab) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                    // Apartment furniture on the row sitting directly on each slab: potted
                    // tentacle-plants, levitating egg-chairs, orb lamps — placed by a
                    // position hash (no rng draws, so downstream worldgen streams hold).
                    if (storey >= floorEvery && storey % floorEvery == 2
                        && Math.Abs(dt) < span - 2 && dt * gapSide < span - 4)
                    {
                        var h = (r * 7919 + t * 104729) & 1023;
                        if (h < 150)
                        {
                            planet.Set(r, t, (h % 3) switch
                            {
                                0 => TileKind.AlienPlant,
                                1 => TileKind.HoverPod,
                                _ => TileKind.OrbLamp,
                            });
                            continue;
                        }
                    }
                    planet.Set(r, t, TileKind.Sky);
                }
            }

            // Rooftop furniture, by class: spires carry the beacon-tipped antenna mast,
            // low-rises a stepped crown or a glass conservatory dome, mid-rises any of the
            // three — so the skyline tops read varied instead of stamped.
            var roofStyle = classRoll >= 0.60 ? 0 : classRoll < 0.18 ? 1 + rng.Next(2) : rng.Next(3);
            if (roofStyle == 0)
            {
                // Antenna mast: a thin anchored spike off the roof with a glowing beacon tip.
                var mastTop = Math.Min(planet.Rings - 2, topR + (int)(3 * S) + rng.Next((int)(3 * S)));
                for (var r = topR + 1; r <= mastTop; r++)
                {
                    var t = (int)((ang / MathHelper.TwoPi + 1f) % 1f * planet.TilesAt(r));
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    planet.Set(r, t, r == mastTop ? TileKind.Beacon : TileKind.AlienAlloy);
                    planet.SetWall(r, t, TileKind.AlienAlloy);
                }
            }
            else if (roofStyle == 1)
            {
                // Stepped crown: two shrinking alloy tiers, a little ziggurat cap.
                for (var step = 0; step < 2; step++)
                {
                    var stepW = halfWidthPx * (0.65f - step * 0.28f);
                    for (var dr = 1 + step * (int)S; dr <= (1 + step) * (int)S; dr++)
                    {
                        var r = topR + dr;
                        if (r >= planet.Rings - 1) break;
                        var n = planet.TilesAt(r);
                        var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                        var sw = (int)(stepW / ((Planet.RingMin + r + 0.5f) * Planet.TileSize)
                                       / MathHelper.TwoPi * n) + 1;
                        for (var dt = -sw; dt <= sw; dt++)
                        {
                            var t = ((t0 + dt) % n + n) % n;
                            if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                            planet.Set(r, t, TileKind.AlienAlloy);
                            planet.SetWall(r, t, TileKind.AlienAlloy);
                        }
                    }
                }
            }
            else
            {
                // Conservatory dome: a hollow glass half-shell over the roof garden.
                var domeH = (int)(2.5f * S);
                for (var dr = 1; dr <= domeH; dr++)
                {
                    var r = topR + dr;
                    if (r >= planet.Rings - 1) break;
                    var f = dr / (float)domeH;
                    var domeW = halfWidthPx * 0.85f * MathF.Sqrt(MathF.Max(0.05f, 1f - f * f));
                    var n = planet.TilesAt(r);
                    var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                    var sw = (int)(domeW / ((Planet.RingMin + r + 0.5f) * Planet.TileSize)
                                   / MathHelper.TwoPi * n) + 1;
                    for (var dt = -sw; dt <= sw; dt++)
                    {
                        var t = ((t0 + dt) % n + n) % n;
                        if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                        var shell = Math.Abs(dt) >= sw - 1 || dr == domeH;
                        planet.Set(r, t, shell ? TileKind.CityGlass : TileKind.Sky);
                        planet.SetWall(r, t, TileKind.AlienAlloy);
                    }
                }
            }

            // Civilian spawn sites: the doorway, and one apartment per second storey.
            {
                var nD = planet.TilesAt(baseR + 1);
                var tD = (int)((ang / MathHelper.TwoPi + 1f) % 1f * nD);
                planet.CitySpawns.Add((baseR + 1, tD));
                for (var storey = floorEvery; storey < height - 4; storey += floorEvery * 2)
                {
                    var r = baseR + storey + 2;
                    var tA = (int)((ang / MathHelper.TwoPi + 1f) % 1f * planet.TilesAt(r));
                    planet.CitySpawns.Add((r, tA));
                }
            }
        }
    }

    /// <summary>Underground lizardman cities: <c>def.LizardCities</c> buried warrens. Each is
    /// a descending chain of brick-shelled chamber halls joined by carved tunnels, entered
    /// through a brick-lined shaft that breaks the surface. Chambers hold glowshroom lamps and
    /// brick huts; the deepest hall is the treasure vault, its floor studded with gold and a
    /// gem. Chamber hearts are recorded in <see cref="Planet.LizardDens"/> — the spawn
    /// director garrisons lizardman warriors around them. Everything stays above the lava
    /// flood line so the halls are found dry.</summary>
    private static void CarveLizardCities(Planet planet, PlanetDef def, Random rng,
        (float ang, float h, float w)[] mountains, List<(float ang, float w)> avoid)
    {
        var surfaceR = planet.SurfaceRing;
        // Ring index of the global lava flood's top (see Game1's FillSkyTilesWithin call),
        // plus margin — chambers and tunnels must stay above it or they generate flooded.
        var lavaTop = (int)(planet.Radius * def.LavaFillFrac) - Planet.RingMin;
        var minRing = Math.Max((int)(10 * S), lavaTop + (int)(7 * S));

        for (var city = 0; city < def.LizardCities; city++)
        {
            // Entrance bearing: clear of everything already stamped and of the rover drop.
            var ang = 0f;
            var ok = false;
            for (var tries = 0; tries < 90 && !ok; tries++)
            {
                ang = (float)(rng.NextDouble() * MathHelper.TwoPi);
                ok = !NearMountain(mountains, ang, 0.08f)
                     && AngDist(ang, MathF.PI * 1.5f) > 0.25f;
                for (var i = 0; ok && i < avoid.Count; i++)
                    ok = AngDist(ang, avoid[i].ang) > avoid[i].w + 0.06f;
            }
            if (!ok) continue;
            avoid.Add((ang, 0.1f));

            // Chamber chain: the entrance hall sits shallow, each later hall steps deeper
            // and drifts sideways, so the warren reads as a descending gallery network.
            var count = 5 + rng.Next(3);
            var centres = new List<Vector2>();
            var cAng = ang;
            var cr = surfaceR - (int)((14 + rng.Next(5)) * S);
            for (var i = 0; i < count; i++)
            {
                var halfH = (int)(4 * S) + rng.Next((int)(2 * S) + 1);       // 32–48 px tall
                var halfWpx = 39f + (float)rng.NextDouble() * 39f;           // 78–156 px wide
                // On lava-rich worlds (the ember homeland floods at 62%!) the dry band
                // between flood line and surface is thin: squash the hall to fit, and pin
                // its centre so the interior floor stays dry and the roof stays buried.
                halfH = Math.Min(halfH, Math.Max((int)(2 * S), (surfaceR - lavaTop) / 2 - (int)(3 * S)));
                var crLo = lavaTop + halfH + (int)(3 * S);
                var crHi = surfaceR - halfH - (int)(2 * S);
                cr = crLo <= crHi ? Math.Clamp(cr, crLo, crHi) : crHi;
                var vault = i == count - 1;
                CarveChamber(planet, rng, cr, cAng, halfH, halfWpx, vault);

                var nC = planet.TilesAt(cr);
                var ct = (int)((cAng / MathHelper.TwoPi + 1f) % 1f * nC);
                planet.LizardDens.Add((cr, ct));
                centres.Add(planet.TileToWorld(cr, ct));

                cAng += ((float)rng.NextDouble() * 0.12f + 0.05f) * (rng.Next(2) == 0 ? 1f : -1f);
                cr -= (int)((8 + rng.Next(7)) * S);
            }

            // Tunnels: brick-walled bores linking each hall to the next — plus a few
            // cross-links skipping a hall (i to i+2) so the warren reads as a CONNECTED
            // network of galleries with loops and shortcuts, not a single dead-end chain.
            for (var i = 0; i + 1 < centres.Count; i++)
                CarveTunnel(planet, centres[i], centres[i + 1]);
            for (var i = 0; i + 2 < centres.Count; i++)
                if (rng.Next(2) == 0) CarveTunnel(planet, centres[i], centres[i + 2]);
            // And a long spine tunnel from the entrance hall down to the deep vault, so there's
            // always a direct through-route linking the whole village end to end.
            if (centres.Count >= 3)
                CarveTunnel(planet, centres[0], centres[^1]);

            // Entrance shaft: from the first hall's roof straight up through the surface,
            // brick-lined so the soft dirt band can't slump into it.
            var entryR = surfaceR - (int)((14 + 4) * S);
            for (var r = Math.Max(minRing, entryR); r <= surfaceR + (int)S; r++)
            {
                if (r >= planet.Rings) break;
                var n = planet.TilesAt(r);
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                for (var dt = -3; dt <= 3; dt++)
                {
                    var t = ((t0 + dt) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (Math.Abs(dt) <= 1)
                    {
                        planet.SetWall(r, t, TileKind.LizardBrick);
                        planet.Set(r, t, TileKind.Sky);
                    }
                    else if (Tiles.IsSolid(planet.Get(r, t)))
                    {
                        planet.Set(r, t, TileKind.LizardBrick);
                        planet.SetWall(r, t, TileKind.LizardBrick);
                    }
                }
            }

            // Cave exits: from each hall, bore a short brick corridor OUT into the surrounding
            // cave-riddled crust, gated by a door at the mouth. Lizardmen open doors, so the
            // guards can slip out of the warren and range through the nearby caves — the
            // village isn't a sealed box, its people come and go.
            foreach (var hall in centres)
            {
                var hUp = planet.UpAt(hall);
                var hRight = new Vector2(-hUp.Y, hUp.X);
                var side = rng.Next(2) == 0 ? 1f : -1f;
                var reach = 120f + (float)rng.NextDouble() * 90f;
                CarveTunnel(planet, hall, hall + hRight * side * reach);
                // Hang a 3-tall door across the corridor mouth, ~30px out from the hall centre.
                var doorPos = hall + hRight * side * 30f;
                var rel = doorPos - planet.Center;
                var doorAng = MathF.Atan2(rel.Y, rel.X);
                var (doorR, _) = planet.WorldToTile(doorPos);
                for (var dr = -1; dr <= 1; dr++)
                {
                    var r = doorR + dr;
                    if (r < 2 || r >= planet.Rings) continue;
                    var n = planet.TilesAt(r);
                    var t = (int)((doorAng / MathHelper.TwoPi + 1f) % 1f * n);
                    if (planet.Get(r, t) == TileKind.Sky)
                    {
                        planet.Set(r, t, TileKind.DoorClosed);
                        planet.SetWall(r, t, TileKind.LizardBrick);
                    }
                }
            }
        }
    }

    /// <summary>One lizard-city hall: a brick-shelled rectangular chamber in polar space —
    /// interior air over brick backdrop, glowshroom lamps on the floor, a hut in the wider
    /// halls, and gold + a gem studding the vault's floor.</summary>
    private static void CarveChamber(Planet planet, Random rng, int cr, float cAng,
        int halfH, float halfWpx, bool vault)
    {
        var interiorFloor = new List<(int r, int t)>();
        for (var dr = -halfH - 2; dr <= halfH + 2; dr++)
        {
            var r = cr + dr;
            if (r < 2 || r >= planet.Rings) continue;
            var n = planet.TilesAt(r);
            var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
            var span = (int)(halfWpx / ringRadius / MathHelper.TwoPi * n) + 1;
            var t0 = (int)((cAng / MathHelper.TwoPi + 1f) % 1f * n);
            for (var dt = -span - 2; dt <= span + 2; dt++)
            {
                var t = ((t0 + dt) % n + n) % n;
                if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                var interior = Math.Abs(dr) <= halfH && Math.Abs(dt) <= span;
                if (interior)
                {
                    planet.SetWall(r, t, TileKind.LizardBrick);
                    planet.Set(r, t, TileKind.Sky);
                    if (dr == -halfH) interiorFloor.Add((r, t));
                }
                else if (Tiles.IsSolid(planet.Get(r, t)))
                {
                    planet.Set(r, t, TileKind.LizardBrick);
                    planet.SetWall(r, t, TileKind.LizardBrick);
                }
            }
        }

        // Glowshroom lamps: scattered along the floor so the hall glows from within.
        for (var i = 0; i < 3 + rng.Next(3) && interiorFloor.Count > 0; i++)
        {
            var (fr, ft) = interiorFloor[rng.Next(interiorFloor.Count)];
            planet.Set(fr, ft, TileKind.Glowshroom);
        }

        // The vault's floor is studded with the hoard — gold seams and a cut gem, set into
        // the brick course just under the interior.
        if (vault)
        {
            for (var i = 0; i < 4 + rng.Next(3) && interiorFloor.Count > 0; i++)
            {
                var (fr, ft) = interiorFloor[rng.Next(interiorFloor.Count)];
                if (fr - 1 >= 2 && planet.Get(fr - 1, ft) == TileKind.LizardBrick)
                {
                    // Gold seams set into the brick course, and the centrepiece ruby is EMBEDDED
                    // in a gold seam (a gem inside the material, not a solid ruby block).
                    planet.Set(fr - 1, ft, TileKind.GoldOre);
                    if (i == 0) planet.SetGem(fr - 1, ft, TileKind.Ruby);
                }
            }
            // The prize: one or two treasure chests standing on the vault floor. The dwarf
            // opens them with E for a pile of gold and a shot at a rare gem (see Game1's chest
            // interaction) — the reward for fighting down through the warren's guards.
            for (var i = 0; i < 1 + rng.Next(2) && interiorFloor.Count > 0; i++)
            {
                var (fr, ft) = interiorFloor[rng.Next(interiorFloor.Count)];
                if (planet.Get(fr, ft) == TileKind.Sky) planet.Set(fr, ft, TileKind.Chest);
            }
        }
        else if (halfWpx > 52f && interiorFloor.Count > 8)
        {
            // A brick hut leaning on the hall floor: hollow box, one open doorway.
            var hutHalfW = 3;
            var hutH = (int)(2 * S);
            var side = rng.Next(2) == 0 ? -1 : 1;
            var nF = planet.TilesAt(cr - halfH);
            var hutT0 = (int)((cAng / MathHelper.TwoPi + 1f) % 1f * nF)
                        + side * (int)(halfWpx / Planet.TileSize / 2f);
            for (var hr = 0; hr <= hutH; hr++)
            {
                var r = cr - halfH + hr;
                if (r < 2 || r >= planet.Rings) continue;
                var n = planet.TilesAt(r);
                for (var dt = -hutHalfW; dt <= hutHalfW; dt++)
                {
                    var t = ((hutT0 + dt) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    var wall = hr == hutH || Math.Abs(dt) == hutHalfW;
                    // Door: the lower two courses on the leeward side stay open.
                    if (wall && hr < hutH - 1 && dt == -side * hutHalfW) wall = false;
                    planet.Set(r, t, wall ? TileKind.LizardBrick : TileKind.Sky);
                    planet.SetWall(r, t, TileKind.LizardBrick);
                }
            }
        }
    }

    /// <summary>Bore a walkable tunnel between two world points: step along the line and
    /// clear a disc at each step, backing every cleared tile with brick so the bore reads
    /// carved rather than natural. Same polar-aware neighbourhood math as the colliders —
    /// angular indices are recomputed per ring.</summary>
    private static void CarveTunnel(Planet planet, Vector2 from, Vector2 to)
    {
        const float radius = 7f;
        var d = to - from;
        var len = d.Length();
        if (len < 1f) return;
        var step = d / len * 3f;
        var steps = (int)(len / 3f) + 1;
        var pos = from;
        for (var s = 0; s <= steps; s++, pos += step)
        {
            var (cx, _) = planet.WorldToTile(pos);
            var rel = pos - planet.Center;
            var pAng = MathF.Atan2(rel.Y, rel.X);
            if (pAng < 0) pAng += MathHelper.TwoPi;
            for (var dx = -2; dx <= 2; dx++)
            {
                var x = cx + dx;
                if (x < 2 || x >= planet.Rings) continue;
                var n = planet.TilesAt(x);
                var ty0 = (int)(pAng / MathHelper.TwoPi * n);
                for (var dy = -2; dy <= 2; dy++)
                {
                    var y = ((ty0 + dy) % n + n) % n;
                    var k = planet.Get(x, y);
                    if (Tiles.IsAnchored(k)) continue;
                    if ((planet.TileToWorld(x, y) - pos).LengthSquared() > radius * radius) continue;
                    planet.SetWall(x, y, TileKind.LizardBrick);
                    if (Tiles.IsSolid(k)) planet.Set(x, y, TileKind.Sky);
                }
            }
        }
    }

    /// <summary>Shortest angular distance between two bearings, in radians.</summary>
    private static float AngDist(float a, float b)
    {
        var d = MathF.Abs(a - b) % MathHelper.TwoPi;
        return d > MathF.PI ? MathHelper.TwoPi - d : d;
    }

    /// <summary>Underground biomes: hand-carved pockets stamped after the main pass.
    /// <b>Crystal caverns</b> — mid-depth cavities lined with crystal, a glittering find
    /// worth a detour. <b>Fungal groves</b> — shallow caves walled in moss with glowshrooms
    /// sprouting from the floor, natural light wells on the living worlds. Counts come from
    /// the planet def.</summary>
    /// <summary>The Hollow's namesake: one vast cavern buried at mid-depth — a geode the
    /// size of a town square, its shell lined with crystal and studded with embedded gem
    /// overlays (voidstone, diamond, emerald). A scaled-up cousin of SeedBiomePockets'
    /// crystal caverns; glowshrooms sprout from the floor shell so breaking in reads as
    /// stepping into a lit cathedral rather than another dark pocket.</summary>
    private static void CarveGreatGeode(Planet planet, Random rng)
    {
        var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
        // 55-70 legacy tiles down: below the mid-crust ore band, above the deepest gem band,
        // and comfortably clear of the core on the 1.5× asteroid.
        var depth = (int)((55 + rng.Next(16)) * S);
        var cr = planet.SurfaceRing - depth;
        var radius = (int)((13 + rng.Next(5)) * S);
        var n = planet.TilesAt(cr);
        var ct = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
        var centre = planet.TileToWorld(cr, ct);

        var lineR = (radius + 2) * Planet.TileSize;
        for (var dr = -radius - 2; dr <= radius + 2; dr++)
        {
            var r = cr + dr;
            if (r < 2 || r >= planet.Rings) continue;
            var rn = planet.TilesAt(r);
            var rt0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * rn);
            var span = radius + 3;
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
                else if (dist <= (radius + 1) * Planet.TileSize)
                {
                    // Inner shell: a rock face densely STUDDED with embedded crystals (they sit
                    // inside the wall rock as gem overlays, not solid crystal blocks), with
                    // glowshrooms sprouting from the floor side.
                    if (dr < 0 && rng.Next(4) == 0)
                        planet.Set(r, t, TileKind.Glowshroom);
                    else
                    {
                        planet.Set(r, t, TileKind.Stone);
                        planet.SetGem(r, t, TileKind.Crystal);
                    }
                }
                else if (k != TileKind.Sky)
                {
                    // Outer shell: the host rock stays, studded with embedded rare gems —
                    // the geode's treasure crust, paid out one whole pickup per gem.
                    if (rng.Next(4) == 0 && IsOreHost(k))
                        planet.SetGem(r, t, rng.Next(5) switch
                        {
                            0 => TileKind.Voidstone,
                            1 or 2 => TileKind.Diamond,
                            _ => TileKind.Emerald,
                        });
                }
            }
        }
    }

    private static void SeedBiomePockets(Planet planet, PlanetDef def, Random rng)
    {
        void Carve(int count, bool crystal)
        {
            for (var i = 0; i < count; i++)
            {
                var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
                // Crystal runs deep (legacy tiles 30-70 below baseline); groves stay shallow (8-30).
                var depth = (int)((crystal ? 30 + rng.Next(40) : 8 + rng.Next(22)) * S);
                var cr = planet.SurfaceRing - depth;
                if (cr < 8 * S) continue;
                var radius = (int)((crystal ? 4 + rng.Next(4) : 3 + rng.Next(3)) * S);
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
                            // Cavern shells are rock STUDDED with embedded crystals (inside the
                            // wall, not solid crystal blocks); grove shells are moss, and the
                            // inward (floor) side sprouts glowshrooms.
                            if (crystal)
                            {
                                planet.Set(r, t, TileKind.Stone);
                                planet.SetGem(r, t, TileKind.Crystal);
                            }
                            else
                            {
                                planet.Set(r, t, dr < 0 && rng.Next(3) == 0
                                    ? TileKind.Glowshroom : TileKind.MossStone);
                            }
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
