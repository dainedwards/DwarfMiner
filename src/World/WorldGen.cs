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
        };
        var rng = new Random(seed);

        // Subtle surface elevation noise — kept very low so the planet reads as a smooth
        // round circle except where mountain spikes rise. The ridge channel is much finer
        // (≈0.7° per sample) and crags the mountain profiles below.
        var surfA = MakeAngularNoise(rng, 8);
        var surfC = MakeAngularNoise(rng, 128);
        var ridge = MakeAngularNoise(rng, 512);

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
            // 8+ lakes the shoreline is mostly sea and dry land the exception.
            lakes[i] = (
                ang,
                depth: (3.5f + (float)rng.NextDouble() * 3.5f) * def.LakeScale,  // 3.5–7 tiles at scale 1
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

                // Very subtle surface variation — at most ±1.5 legacy tiles. The planet stays round.
                var elev = AngularSample(surfA, ang) * 2f * S;

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

                    if (oreN > 0.88f - boost - Bias(TileKind.CoalOre) && depth > 6f) k = TileKind.CoalOre;
                    // Fuel ore sits in a broad shallow band — you need a good stockpile to launch,
                    // so it's common enough to gather without a deep dive.
                    if (oreN > 0.905f - boost - Bias(TileKind.FuelOre) && depth > 12f) k = TileKind.FuelOre;
                    if (oreN > 0.92f - boost - Bias(TileKind.IronOre) && depth > 16f) k = TileKind.IronOre;
                    // Silver and gold are charted rarities: their base thresholds are
                    // unreachable (like voidstone's), so veins exist only on worlds whose def
                    // carries the bias — the star map's RARE FINDS line is the prospecting map.
                    if (oreN > 1.05f - Bias(TileKind.SilverOre) && depth > 30f) k = TileKind.SilverOre;
                    if (oreN > 1.05f - Bias(TileKind.GoldOre) && depth > 40f) k = TileKind.GoldOre;
                    if (oreN > 0.965f - boost * 0.6f - Bias(TileKind.PlatinumOre) && depth > 55f) k = TileKind.PlatinumOre;

                    // Gems are not blocks: a gem site keeps its host tile (whatever common
                    // rock/ore the cascade above chose) and seats a gem overlay inside it —
                    // except the precious metals, which stay pure veins (a gem never rides
                    // silver/gold/platinum; the host reverts to plain rock instead). Only
                    // every 4th qualifying site gets a gem: each embedded gem pays out one
                    // whole drop, so the thinning keeps a vein's total yield matched to the
                    // old 4-fine-tiles-per-drop dust economy.
                    var gem = TileKind.Sky;
                    if (oreN > 0.972f - boost * 0.5f - Bias(TileKind.Ruby) && depth > 65f) gem = TileKind.Ruby;
                    if (oreN > 0.978f - boost * 0.4f - Bias(TileKind.Sapphire) && depth > 75f) gem = TileKind.Sapphire;
                    if (oreN > 0.984f - boost * 0.3f - Bias(TileKind.Crystal) && depth > 42f) gem = TileKind.Crystal;
                    if (oreN > 0.989f - boost * 0.2f - Bias(TileKind.Diamond) && depth > 95f) gem = TileKind.Diamond;
                    // Emerald seams sit deep on the living worlds (verdant/frost carry the
                    // bias). Voidstone's base threshold is unreachable — only the Rift's
                    // bias pulls it into existence, making it the campaign's endgame gem.
                    if (oreN > 0.986f - boost * 0.3f - Bias(TileKind.Emerald) && depth > 80f) gem = TileKind.Emerald;
                    if (oreN > 1.05f - Bias(TileKind.Voidstone) && depth > 100f) gem = TileKind.Voidstone;
                    if (gem != TileKind.Sky && (((r * 73856093) ^ (t * 19349663)) & 3) == 0)
                    {
                        if (k is TileKind.SilverOre or TileKind.GoldOre or TileKind.PlatinumOre)
                            k = baseRock;
                        planet.SetGem(r, t, gem);
                    }
                }

                planet.Set(r, t, k);
            }
        }

        SeedBiomePockets(planet, def, rng);

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
            var doorSide = rng.Next(2) == 0 ? -1 : 1;
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
                        if (storey < doorH && Math.Sign(dt) == doorSide)
                            planet.Set(r, t, TileKind.Sky);
                        else if (storey == doorH && Math.Sign(dt) == doorSide)
                            planet.Set(r, t, TileKind.CityGlass);
                        else
                            planet.Set(r, t, window ? TileKind.CityGlass : TileKind.AlienAlloy);
                        continue;
                    }

                    // Interior: floor slab at the base of every storey above the ground
                    // floor (the plinth is street level's floor), with a stair gap hugging
                    // alternating walls so the shaft zig-zags up the tower.
                    var slab = slabRow;
                    if (slab)
                    {
                        var gapSide = storey / floorEvery % 2 == 0 ? 1 : -1;
                        slab = dt * gapSide < span - 4;
                    }
                    planet.Set(r, t, slab ? TileKind.AlienAlloy : TileKind.Sky);
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

            // Tunnels: brick-walled bores linking each hall to the next.
            for (var i = 0; i + 1 < centres.Count; i++)
                CarveTunnel(planet, centres[i], centres[i + 1]);

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
                    planet.Set(fr - 1, ft, i == 0 ? TileKind.Ruby : TileKind.GoldOre);
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
