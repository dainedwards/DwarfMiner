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

        // Surface elevation noise. surfA is the old broad, subtle channel; surfB is the
        // ROLLING-HILL channel (Noita-style worldgen relief): ~5–11° swells of ±3-4 legacy
        // tiles, in real tiles, so flat runs become gently rolling ground that the sim,
        // collision and sand all agree on — the organic large-scale shape the edge shader's
        // 1-px carve budget can't provide. Downward-biased like the asteroid lumps (valleys
        // have the whole crust below; crests share the fixed sky headroom with mountains).
        // surfB draws from an ISOLATED rng (the worms/veins/flora pattern): a draw on the
        // shared stream would relocate every downstream placement — mountains, lakes,
        // districts, cave corridors — and the layout-sensitive SimTest scenarios with them.
        var surfA = MakeAngularNoise(rng, 8);
        var surfB = MakeAngularNoise(new Random(seed ^ 0x0511), 64);
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

        // City worlds grade their land: a civilization that raises 1/3-of-the-surface
        // megacities has flattened the hills first, so the rolling channel runs at 30%
        // there — towers keep their footing and streets stay walkable (this also keeps the
        // debug QA world near-flat, which the SimTest defense scenarios rely on).
        var hillScale = def.CityLots > 0 ? 0.3f : 1f;

        // Local terrain height at a bearing — the ONE definition of the ground line, shared
        // by the tile loop and the SurfaceProfile stamp so oxygen depth / sun heightmap /
        // disc preview all follow the same rolling ground.
        float ElevAt(float a) => AngularSample(surfA, a) * 2f * S
            + (AngularSample(surfB, a) - 0.1f) * 7f * S * hillScale
            + LumpAt(a);

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

        // QA-rig lake trio (def.LakeTrio): the first two basins and the first acid pool
        // re-place as a UNIT — water, lava, acid in a row, each rim a narrow land strip
        // from the next. LAVA takes the middle seat so it borders BOTH of the others: one
        // walk gives you every liquid pair at once (quench/steam on one rim, lava-vs-acid
        // on the other). Runs after the acid array is built because the trio's spacing is
        // derived from all three basins' widths.
        if (def.LakeTrio && lakeCount >= 2 && acidPools.Length >= 1)
        {
            // The trio are the rig's SHOWCASE basins: 3× the width and depth of a normal
            // one, so each is a body of fluid you swim, bridge or drain rather than a
            // puddle you step over. Scaled BEFORE the gaps are measured — the strips
            // between the rims stay proportional, so the row grows without merging. The
            // widths feed the `blocked` list below, so the volcano/city/warren siting
            // pass already routes around the trio at its true size.
            const float trioScale = 3f;
            lakes[0].depth *= trioScale; lakes[0].w *= trioScale;
            lakes[1].depth *= trioScale; lakes[1].w *= trioScale;
            acidPools[0].depth *= trioScale; acidPools[0].w *= trioScale;

            // Centre-to-centre = 1.3 × the two half-widths, so rims fall a 0.3-strip apart.
            var gapWaterLava = (lakes[0].w + lakes[1].w) * 1.3f;
            var gapLavaAcid = (lakes[1].w + acidPools[0].w) * 1.3f;
            float trioAng;
            var trioTries = 0;
            do
            {
                trioAng = (float)(rng.NextDouble() * MathHelper.TwoPi);
                trioTries++;
            } while (trioTries < 60 && (NearMountain(mountains, trioAng, 0.14f)
                     || NearMountain(mountains, (trioAng + gapWaterLava) % MathHelper.TwoPi, 0.14f)
                     || NearMountain(mountains, (trioAng + gapWaterLava + gapLavaAcid) % MathHelper.TwoPi, 0.14f)));
            lakes[0].ang = trioAng;
            lakes[1].ang = (trioAng + gapWaterLava) % MathHelper.TwoPi;
            acidPools[0].ang = (trioAng + gapWaterLava + gapLavaAcid) % MathHelper.TwoPi;
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

        // Cave-strata seams: solid shells where NO noise cave may open, so each stratum
        // stays sealed from the next (gameplay: the player mines between layers; perf: the
        // lava sea can never drain into the dry caves below it). See CaveStrata.
        var (strataSeams, _, seaFloorTiles) = CaveStrata(planet, def);

        // BASIN DEPTH CAP — a bowl must always have a floor under it, and every basin depth
        // above was rolled in ABSOLUTE legacy tiles with no idea how thick this world's crust
        // actually is. On the shrunken QA rig (SizeScale 0.49) the crust is only ~27 legacy
        // tiles from surface to core, while the LakeTrio's 3× showcase bowls roll 15-29 — so
        // the water lake was carved straight through the bottom of the world and its "bed"
        // was whatever the innermost ring left behind. Two floors to respect:
        //   * the core: keep 45% of the crust under the deepest point, so a basin reads as a
        //     bowl in the ground rather than a hole in the planet;
        //   * the topmost strata seam: a bowl that reaches one is re-plugged by SealSeams
        //     (the seam contract is absolute) — which quite literally cuts the lake's bottom
        //     off, and leaves the pour sitting on a slab that was never meant to be a bed.
        // Clamped here rather than at the roll so the rng stream — and every seeded layout —
        // stays byte-identical; only the depths change, and only where they overran.
        {
            var maxDepth = baselineR / S * 0.55f;
            foreach (var (_, hi) in strataSeams)
            {
                // `hi` is the seam band's OUTER radius — the first thing a bowl coming down
                // from the surface would meet. Stop 3 legacy tiles clear of it.
                var toSeam = (baselineR + Planet.RingMin - hi) / S - 3f;
                if (toSeam < maxDepth) maxDepth = toSeam;
            }
            maxDepth = MathF.Max(maxDepth, 2f);
            for (var i = 0; i < lakes.Length; i++)
                lakes[i].depth = MathF.Min(lakes[i].depth, maxDepth);
            for (var i = 0; i < acidPools.Length; i++)
                acidPools[i].depth = MathF.Min(acidPools[i].depth, maxDepth);
            for (var i = 0; i < craters.Length; i++)
                craters[i].depth = MathF.Min(craters[i].depth, maxDepth);
        }

        // Top of the lava flood (BuildSessionWorld fills Sky tiles inside this radius).
        // The liquid/gas pocket bands below are authored as ABSOLUTE depths, which sat
        // safely above the lava on full-size worlds — but on small worlds (the 0.7× QA
        // rig) the same depths reach INTO the flood band, and every water/oil pocket
        // seeded there ignites against the sea on load: a quench-and-burn front that woke
        // ~40k lava cells and simmered for minutes. All pocket seeding stays above this.
        var lavaTopTiles = def.LavaFillFrac > 0f ? planet.Radius * def.LavaFillFrac : 0f;

        // Carved-but-unseeded basin air (a lava/acid basin's freeboard and rim courses),
        // per fluid: PlugFluidBreaches keeps this lid open as the pool's own surface but
        // WALLS ITS EDGES — the settle slosh crests one course into the lid, so the lid's
        // rim is part of the containment, not the shore.
        var lavaLid = new HashSet<long>();
        var acidLid = new HashSet<long>();
        var waterLid = new HashSet<long>();

        for (var r = 0; r < planet.Rings; r++)
        {
            var n = planet.TilesAt(r);
            // Seams are purely radial, so one check per ring covers every tile in it.
            var radTiles = Planet.RingMin + r + 0.5f;
            var inSeam = false;
            foreach (var (lo, hi) in strataSeams)
                if (radTiles >= lo && radTiles < hi) { inSeam = true; break; }
            for (var t = 0; t < n; t++)
            {
                var ang = (t + 0.5f) / n * MathHelper.TwoPi;

                // Rolling ground: broad subtle variation + the hill channel + (on lumpy
                // worlds) the asteroid lobes. See ElevAt.
                var elev = ElevAt(ang);

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

                // How deep the sea reaches at this bearing (0 = dry land). Hoisted out of the
                // basin carve (like acidDepth) so the layered-ground pass below can armour the
                // seabed on ocean worlds: an obsidian shell plus a cave-free buffer under every
                // deep basin, keeping the under-sea cave network dry and the sea overhead.
                var lakeDepth = 0f;
                var lakeIdx = -1;
                for (var li = 0; li < lakes.Length; li++)
                {
                    var l = lakes[li];
                    var angDiff = MathF.Abs(ang - l.ang);
                    if (angDiff > MathF.PI) angDiff = MathHelper.TwoPi - angDiff;
                    if (angDiff < l.w)
                    {
                        var f = angDiff / l.w;
                        var d = (1f - f * f) * l.depth;
                        if (d > lakeDepth) { lakeDepth = d; lakeIdx = li; }
                    }
                }

                // Lake basin: carve the bowl out of the surface and seed it with water. The
                // top course (depth < 1) stays air so the waterline sits just below the shore.
                if (mountainHeight <= 0.5f)
                {
                    if (lakeDepth > 0.5f && depth < lakeDepth)
                    {
                        // The trio's second basin is the LAVA lake: basalt-walled crucible,
                        // seeded through LavaSeeds — the same fill path as volcano plumbing.
                        // Lava gets a course of FREEBOARD (fill starts at depth 2, not 1):
                        // the settle slosh crests one course over the fill line, and at
                        // shore level that ran along the jacket top and drained down the
                        // first roofed shaft it met — the crater pool survives the same
                        // slosh only because its pool line sits tiles below the rim.
                        var lavaLake = def.LakeTrio && lakeIdx == 1;
                        planet.SetWall(r, t, lavaLake ? TileKind.Basalt : TileKind.Dirt);
                        planet.Set(r, t, TileKind.Sky);
                        if (depth >= (lavaLake ? 2f : 1f))
                        {
                            if (lavaLake) planet.LavaSeeds.Add((r, t));
                            else { planet.WaterSeeds.Add((r, t)); planet.LakeBasinSeeds.Add((r, t)); }
                        }
                        else if (lavaLake) lavaLid.Add(Planet.TileKey(r, t));
                        else waterLid.Add(Planet.TileKey(r, t));
                        continue;
                    }

                    // Acid pool basin — same carve, corrosive fill.
                    if (acidDepth > 0.5f && depth < acidDepth)
                    {
                        planet.SetWall(r, t, TileKind.Stone);
                        planet.Set(r, t, TileKind.Sky);
                        if (depth >= 1f) planet.AcidSeeds.Add((r, t));
                        else acidLid.Add(Planet.TileKey(r, t));
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

                // The LAVA lake gets the same solid buffer (acid pools always had one; the
                // crucible never did): every probe-caught lake drain was a noise cave or
                // shaft in the crust under/beside the basin that the pour then found. The
                // jacket + plug passes seal what touches the fill, but a cave a few tiles
                // out only needs one melted or crumbled tile to connect — solid rock there
                // ends the class.
                //
                // WATER basins now get it too. They used to keep their caves on purpose (the
                // flooded-grotto idea), but a bowl carved straight onto an open cave roof
                // isn't a grotto — the pour is down the hole before you ever see the lake,
                // which is exactly what hollowed out the debug rig's basin. A lake you can
                // still breach by mining is the gameplay; one that arrives pre-drained is a
                // bug. Ocean worlds keep their own (thicker, shell-backed) seaBuffer below.
                var lakeBuffer = lakeDepth > 0.5f && depth < lakeDepth + 12f;
                var lavaBuffer = def.LakeTrio && lakeIdx == 1 && lakeBuffer;
                var waterBuffer = !lavaBuffer && lakeBuffer;

                // Ocean worlds (LakeScale > 2.5, same marker as the deep-water bonus above)
                // armour their seabeds: an obsidian SHELL under every real basin (nothing
                // upper-crust may bite obsidian — worms detour, noise caves are suppressed
                // by the buffer), and a solid cave-free BUFFER a few tiles thicker, so the
                // sea can never find a passage into the dry cave network below. Gameplay
                // contract: the caves under the ocean stay dry, and flooding them takes
                // deliberate obsidian mining — you can't casually drown what lives there.
                // The shell starts a shade inside the bowl edge (lakeDepth > 1.5) so island
                // beaches keep a diggable sand-and-dirt shoreline.
                var oceanWorld = def.LakeScale > 2.5f;
                var seaShell = oceanWorld && lakeDepth > 1.5f
                    && depth >= lakeDepth && depth < lakeDepth + 5f;
                var seaBuffer = oceanWorld && lakeDepth > 0.5f && depth < lakeDepth + 11f;

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

                // The seabed shell wins over every softer layer choice above: hardness 6
                // glass rock between the sea and the caves — mineable with intent, never
                // crumbled through by accident (and no worm can bite it).
                if (seaShell) k = TileKind.Obsidian;

                // Wall captures the structural material before caves/ores override the foreground.
                planet.SetWall(r, t, k);

                var bigN = SampleNoise(bigCave, wx * 0.05f, wy * 0.05f);
                var smallN = SampleNoise(smallCave, wx * 0.18f, wy * 0.18f);
                if (!acidBuffer && !lavaBuffer && !waterBuffer && !seaBuffer && !inSeam && depth > 5f && ((bigN > 0.84f && depth > 8f) || smallN > 0.88f))
                {
                    k = TileKind.Sky;
                    // Reservoirs: a slow water-noise channel floods whole cave pockets in the
                    // crust band (below the dirt, above the lava zone that Game1 fills at
                    // ~45% radius) so some caverns are found brimming rather than dry. Water
                    // is seeded as cells and settles to each pocket's floor on its own.
                    // Ocean worlds skip them: their design promise is DRY caves under a wet
                    // surface — all the water lives in the seas above the obsidian floor.
                    if (def.HasWater && !oceanWorld && depth > 10f && depth < 44f
                        && radTiles > lavaTopTiles + 4f * S
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
                    // Gas keeps to its authored home — the hot caves near (above) the lava —
                    // and stays OUT of the new deep strata below the sea floor: thousands of
                    // fresh dry cave tiles down there would otherwise all roll gas seeds,
                    // and wandering gas is exactly the kind of never-sleeping cell load the
                    // strata design promises not to add.
                    // Gas lives deep (the classic >34 band) OR in the hot shell just above
                    // the lava top — the latter is what keeps high-lava worlds gassed at
                    // all: on ember the flood reaches ~12 legacy tiles below the surface,
                    // so its ENTIRE deep band is drowned (historically those pockets were
                    // seeded INTO the lava and burned off during the load settle — churn,
                    // not gameplay). Either way one legacy tile of clearance above the
                    // flood, so no pocket is born in contact with the sea.
                    var gasBand = depth > 34f
                        || (lavaTopTiles > 0f && depth > 10f && radTiles < lavaTopTiles + 12f * S);
                    if (!isReservoir && def.SeedsGas && gasBand
                        && radTiles > MathF.Max(seaFloorTiles, lavaTopTiles + S)
                        && SampleNoise(pocketNoise, wx * 0.06f + 21f, wy * 0.06f + 21f) > 0.80f)
                        planet.GasSeeds.Add((r, t));

                    // Oil sumps: mid-crust black pools, shallower than the gas band so a
                    // burning tunnel doesn't automatically chain both. Inert until lit.
                    var isGasPocket = planet.GasSeeds.Count > 0 && planet.GasSeeds[^1] == (r, t);
                    if (!isReservoir && !isGasPocket && def.SeedsOil && depth > 14f && depth < 42f
                        && radTiles > lavaTopTiles + 4f * S
                        && SampleNoise(pocketNoise, wx * 0.055f - 33f, wy * 0.055f - 33f) > 0.815f)
                        planet.OilSeeds.Add((r, t));
                }

                if (k == TileKind.Stone && depth > 12f)
                {
                    var mN = SampleNoise(oreNoise, wx * 0.09f, wy * 0.09f);
                    if (mN > 0.70f && mN < 0.78f) k = TileKind.MossStone;
                }

                // No ore seams inside the seabed shell: an ore tile is softer than obsidian,
                // and one soft tile in the armour is all a flooding shortcut needs.
                if (IsOreHost(k) && !seaShell)
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
                    // Precious metals: mostly a DEEP find (rich down in the crust), some through
                    // the mid layers, and only very rarely in the shallows — the shallow penalty
                    // is what thins them out up top rather than a hard depth wall.
                    var metalSel = SampleNoise(oreNoise, wx * 0.031f, wy * 0.031f);
                    if (metalSel < 0.5f && oreN > 0.948f + shallowMetal - boost * 0.4f - Bias(TileKind.SilverOre) && depth > 12f) k = TileKind.SilverOre;
                    if (metalSel >= 0.5f && oreN > 0.955f + shallowMetal - boost * 0.4f - Bias(TileKind.GoldOre) && depth > 14f) k = TileKind.GoldOre;
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
                    // Same depth gradient as the metals: gems are rich deep, occasional in the
                    // mid crust, and only very rarely in the upper layers (the shallow penalty),
                    // so the deeper you dig the better the finds.
                    var gem = TileKind.Sky;
                    if (oreN > 0.982f + shallowGem - boost * 0.3f - Bias(TileKind.Ruby) && depth > 14f) gem = TileKind.Ruby;
                    if (oreN > 0.986f + shallowGem - boost * 0.3f - Bias(TileKind.Sapphire) && depth > 16f) gem = TileKind.Sapphire;
                    if (oreN > 0.988f + shallowGem - boost * 0.3f - Bias(TileKind.Emerald) && depth > 18f) gem = TileKind.Emerald;
                    if (oreN > 0.991f + shallowGem - boost * 0.25f - Bias(TileKind.Diamond) && depth > 20f) gem = TileKind.Diamond;
                    // Crystal is still the rarest of the ambient gems — its threshold sits above
                    // every cut gem, so a raw crystal is a scarcer find than a diamond.
                    if (oreN > 0.994f + shallowGem - boost * 0.2f - Bias(TileKind.Crystal) && depth > 18f) gem = TileKind.Crystal;
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
                profile[i] = baselineR + ElevAt(a);
            }
            planet.SurfaceProfile = profile;
        }

        SeedBiomePockets(planet, def, rng, lakes, acidPools);
        if (def.GreatGeode) CarveGreatGeode(planet, rng);

        // Volcanoes stamp last so their plumbing (throat lining, chamber shell) wins over
        // any cave or pocket it crosses. Keep them off the lake/pool basins. Each stamping
        // pass appends its own footprints to the shared avoid list, so towers keep off the
        // volcano flanks and lizard-city shafts keep off both.
        var blocked = new List<(float ang, float w)>();
        foreach (var l in lakes) blocked.Add((l.ang, l.w));
        foreach (var a in acidPools) blocked.Add((a.ang, a.w));
        CarveVolcanoes(planet, def, rng, mountains, blocked);

        // Every lava/acid seed is now recorded (lake basins from the tile pass, volcano
        // plumbing just above) — freeze their 2-tile jacket reach as the fluid keep-out
        // before ANY tunnel carver runs. A worm or corridor biting inside this halo opens
        // a drain mouth the pour then empties through (the lava-lake bed sat inside worm
        // range; the worms had no idea it was there).
        BuildFluidKeepOut(planet, def);

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

        // Island grotto mouths (ocean worlds): a few natural cave entrances opening on dry
        // land, winding down into the worm band — the under-sea network is meant to be
        // EXPLORED, so the islands offer a way in that isn't mining blind through the
        // beach. Isolated rng (ocean-only anyway) for the usual stream-stability reason.
        if (def.LakeScale > 2.5f)
            CarveIslandGrottoes(planet, new Random(seed ^ 0x0CEA), lakes, mountains);

        // Seams enforced LAST, as a hard pass over the final tile state: every carver above
        // (noise caves, worms, biome pockets, the geode) is also seam-aware or band-clamped,
        // but a disk radius poking one tile over a boundary is enough to let the lava sea
        // drain into the deep strata — so anything that still opened a hole inside a seam is
        // plugged back with its structural wall material here. The seam contract must hold
        // ABSOLUTELY (perf + the mine-between-layers design), not just probabilistically.
        SealSeams(planet, def);

        // The pours' own absolute contract, same spirit as the seams: whatever any carver
        // (noise caves included — they run in the tile pass, before any keep-out exists)
        // opened against a to-be-poured lava/acid body is plugged back with the barrier
        // material. Runs LAST so no later carve can undo it.
        PlugFluidBreaches(planet, lavaLid, acidLid, waterLid, plugWater: def.LakeScale <= 2.5f);

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

            // Gentle worlds grow OASES: a few spots of tightly packed vegetation — a huddle
            // of trees with undergrowth carpeting every gap — little green sanctuaries you
            // stumble on while crossing otherwise ordinary terrain. Isolated rng as above.
            ScatterOases(planet, def, new Random(seed ^ 0x0A51));
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
        "ocean"   => TileKind.Fernleaf,   // lush shore growth between the seas
        "frost"   => TileKind.Frostcap,
        "ember"   => TileKind.Emberbloom,
        "slag"    => TileKind.Rustbramble,
        "acid"    => TileKind.Vitrilily,
        "crystal" => TileKind.Geobloom,
        _         => TileKind.Sky,   // city/rift/belt/moon/debug: no scattered flora
    };

    /// <summary>Scatter the biome's signature plant across the open surface: for a spread of
    /// bearings, drop from the sky to the first solid ground tile and, if it's walkable soil
    /// under open sky (not a mountain wall, lake, or building), seat a plant in the air tile
    /// just above it. The plants are anchored + fire/lava-proof + acid-proof (see the tile
    /// flags), so the ember bloom survives its lava world and the vitriol lily its acid one —
    /// the "give the plants resistance on hostile worlds" ask.</summary>
    /// <summary>Key set of every to-be-poured fluid tile (water, lava, acid) — the LAND
    /// flora scatters skip any bearing whose "ground" is really a basin bed under one of
    /// these (the topmost-solid walk can't tell a lake bed from a hilltop: the fill is
    /// cells, poured after gen). The sea gets its own flora via ScatterWaterPlants.</summary>
    private static HashSet<long> FluidFillSet(Planet planet)
    {
        var set = new HashSet<long>();
        foreach (var (r, t) in planet.WaterSeeds) set.Add(Planet.TileKey(r, t));
        foreach (var (r, t) in planet.LavaSeeds) set.Add(Planet.TileKey(r, t));
        foreach (var (r, t) in planet.AcidSeeds) set.Add(Planet.TileKey(r, t));
        return set;
    }

    private static void ScatterBiomeFlora(Planet planet, PlanetDef def, Random rng)
    {
        var flora = FloraFor(def.Biome);
        if (flora == TileKind.Sky) return;
        var wet = FluidFillSet(planet);

        // One roll per bearing across the whole circumference; density ~48% so the surface
        // reads as generously planted without being carpeted.
        var bearings = 360 + rng.Next(120);
        for (var b = 0; b < bearings; b++)
        {
            if (rng.Next(100) >= 48) continue;
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
            // Land plants never sprout under a pour — this "ground" is a lake bed.
            if (wet.Contains(Planet.TileKey(groundR + 1, at))) continue;
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
            "verdant" => (34, TileKind.TreeCanopy),
            "ocean"   => (30, TileKind.TreeCanopy),              // lush shorelines
            "frost"   => (16, TileKind.TreeCanopy2),
            "crystal" => (16, TileKind.TreeCanopy2),
            "acid"    => (10, TileKind.TreeCanopy2),
            "ember"   => (7, TileKind.TreeCanopy2),
            "slag"    => (6, TileKind.TreeCanopy),               // barren: far less wood
            "city"    => (14, TileKind.TreeCanopy),
            _         => (16, TileKind.TreeCanopy),
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
        var wet = FluidFillSet(planet);
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
            // Never on a basin bed: the "ground" under a lake (Dirt) or the lava lake
            // (Basalt) passes the soil test, but the column above it pours full of fluid.
            {
                var an = planet.TilesAt(groundR + 1);
                var at = (int)((ang / MathHelper.TwoPi + 1f) % 1f * an);
                if (wet.Contains(Planet.TileKey(groundR + 1, at))) continue;
            }
            // Don't crowd trunks: keep a couple of tiles between neighbours.
            if (lastRing == groundR && Math.Abs(gt - lastT) < 3) continue;

            // Species drives height and silhouette, and is chosen per BIOME so each world type
            // grows its own kind of tree (frost = spires, verdant = broad/weeping, etc). Tall
            // and thin across the board — even the "short" broad trees stand several tiles,
            // spires/umbrellas soar, and roughly one in six of any species is a GIANT (a big
            // height bonus) so the canopy line is ragged with the odd towering old-growth alien.
            var species = TreeSpeciesFor(def.Biome, rng);
            // Terraria proportions: a LONG straight bole under the crown — even the short
            // species stand well over head height, and spires genuinely tower.
            var trunkH = species switch
            {
                0 => 16 + rng.Next(12),  // spire   — the tallest, a thin plume on top
                2 => 14 + rng.Next(10),  // umbrella — long bare bole under a flat cap
                3 => 12 + rng.Next(8),   // weeping  — tall, draping crown
                _ => 10 + rng.Next(7),   // broad    — shortest, still a proper trunk
            };
            if (rng.Next(6) == 0) trunkH += 10 + rng.Next(12);  // the occasional giant
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

    /// <summary>Oases on the GENTLE worlds (low difficulty, breathable): a few spots where
    /// vegetation packs in tight — a huddle of trees planted nearly shoulder to shoulder with
    /// undergrowth carpeting every ground tile between them. The harsher a world, the fewer
    /// (hostile and airless worlds grow none), so an oasis reads as a found sanctuary.</summary>
    private static void ScatterOases(Planet planet, PlanetDef def, Random rng)
    {
        if (def.Airless || def.Difficulty > 0.35f) return;
        var (_, canopy) = TreePlanFor(def);
        var flora = FloraFor(def.Biome);
        if (flora == TileKind.Sky) flora = TileKind.Fernleaf;   // an oasis always has undergrowth
        var wet = FluidFillSet(planet);
        var surfRadiusPx = (Planet.RingMin + planet.SurfaceRing) * Planet.TileSize;
        var oases = 2 + rng.Next(3);
        for (var o = 0; o < oases; o++)
        {
            var centreAng = (float)rng.NextDouble() * MathHelper.TwoPi;
            // Walk the site tile by tile: trees every ~3 tiles, undergrowth on everything else.
            var halfSpanTiles = 6 + rng.Next(5);
            for (var dt = -halfSpanTiles; dt <= halfSpanTiles; dt++)
            {
                var ang = centreAng + dt * Planet.TileSize / surfRadiusPx;
                // Ground on this bearing (topmost solid).
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
                if (planet.Get(groundR, gt) is not (TileKind.Grass or TileKind.Dirt or TileKind.Snow
                    or TileKind.MossStone or TileKind.Gravel or TileKind.Basalt)) continue;
                var an = planet.TilesAt(groundR + 1);
                var at = (int)((ang / MathHelper.TwoPi + 1f) % 1f * an);
                if (planet.Get(groundR + 1, at) != TileKind.Sky) continue;
                // Never on a basin bed — see ScatterTrees.
                if (wet.Contains(Planet.TileKey(groundR + 1, at))) continue;

                // A tree every ~3rd tile (tight but not fused), undergrowth everywhere else.
                if (((dt + halfSpanTiles) % 3) == 1)
                {
                    var trunkH = Math.Min(9 + rng.Next(8), planet.Rings - 6 - groundR);
                    if (trunkH < 4) continue;
                    var clear = true;
                    for (var h = 1; h <= trunkH && clear; h++)
                    {
                        var rr = groundR + h;
                        if (rr >= planet.Rings - 1) { clear = false; break; }
                        var nn = planet.TilesAt(rr);
                        if (planet.Get(rr, (int)((ang / MathHelper.TwoPi + 1f) % 1f * nn)) != TileKind.Sky)
                            clear = false;
                    }
                    if (!clear) { planet.Set(groundR + 1, at, flora); continue; }
                    var site = new TreeSite
                    {
                        Angle = ang,
                        GroundR = groundR,
                        Species = TreeSpeciesFor(def.Biome, rng),
                        Height = (byte)trunkH,
                        Canopy = rng.Next(4) == 0
                            ? (canopy == TileKind.TreeCanopy ? TileKind.TreeCanopy2 : TileKind.TreeCanopy)
                            : canopy,
                    };
                    Systems.TreeEcology.Plant(planet, site);
                    planet.Trees.Add(site);
                }
                else
                {
                    planet.Set(groundR + 1, at, flora);
                }
            }
        }
    }

    /// <summary>Scatter waving water plants (SeaFrond) on the shallow lakebeds of any world
    /// that has water — rooted on the solid floor just under the surface of a pool.</summary>
    private static void ScatterWaterPlants(Planet planet, PlanetDef def, Random rng)
    {
        if (!def.HasWater || planet.WaterSeeds.Count == 0) return;
        var basin = new System.Collections.Generic.HashSet<(int, int)>(planet.WaterSeeds);
        // Ocean worlds are LUSH underwater — dense kelp beds and lily-padded surfaces; other
        // wet worlds get a lighter dressing of both.
        var ocean = def.Biome == "ocean";
        var frondPct = ocean ? 24 : 12;
        var padPct = ocean ? 22 : 10;
        foreach (var (x, y) in planet.WaterSeeds)
        {
            // Seabed: root a seaweed stalk on the lakebed floor — 1-3 fronds STACKED into a
            // swaying kelp column (taller stands on the ocean world).
            if (planet.Get(x, y) == TileKind.Sky
                && !basin.Contains((x - 1, y)) && Tiles.IsSolid(planet.Get(x - 1, y))
                && rng.Next(100) < frondPct)
            {
                var stalk = 1 + rng.Next(ocean ? 3 : 2);
                for (var h = 0; h < stalk; h++)
                {
                    var rr = x + h;
                    var n = planet.TilesAt(rr);
                    var t = (int)((y + 0.5f) / planet.TilesAt(x) * n);
                    if (planet.Get(rr, t) != TileKind.Sky) break;
                    if (h > 0 && !basin.Contains((rr, t))) break;   // stay under the waterline
                    planet.Set(rr, t, TileKind.SeaFrond);
                }
            }
            // Surface: an alien lily pad floating on the topmost water row (a basin tile with
            // open sky above it), blossoms and all.
            if (!basin.Contains((x + 1, y)) && rng.Next(100) < padPct)
            {
                var n = planet.TilesAt(x + 1);
                var t = (int)((y + 0.5f) / planet.TilesAt(x) * n);
                if (x + 1 < planet.Rings - 1 && planet.Get(x + 1, t) == TileKind.Sky)
                    planet.Set(x + 1, t, TileKind.LilyPad);
            }
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

    /// <summary>Radial layout of the underground cave strata, shared by three consumers:
    /// the noise-cave pass (suppresses carving inside <c>seams</c>), the deep worm pass
    /// (each entry in <c>bands</c> gets its own sealed network), and Game1's lava flood
    /// (<c>seaFloorTiles</c> turns the old fill-everything-below flood into a lava SEA with
    /// a dry, explorable zone beneath). All values are radii in tiles-from-centre.
    ///
    /// The seams do two jobs at once. Gameplay: deep strata are deliberately NOT connected
    /// to each other or to the crust network above — the player mines the last stretch
    /// between layers. Perf: a stratum that never touches the lava sea can never become
    /// lava plumbing (sealed lava sleeps; flowing lava keeps the cell sim awake — the
    /// constraint that used to forbid ALL deep caves).</summary>
    internal static (List<(float lo, float hi)> seams, List<(float lo, float hi)> bands, float seaFloorTiles)
        CaveStrata(Planet planet, PlanetDef def)
    {
        var seams = new List<(float, float)>();
        var bands = new List<(float, float)>();
        float radius = planet.Radius;
        // The lava sea keeps its historical TOP (LavaFillFrac×radius) but gains a floor —
        // thick enough (14% of radius) to stay a real barrier layer you must cross.
        var seaFloor = def.LavaFillFrac > 0f
            ? MathF.Max(radius * (def.LavaFillFrac - 0.14f), Planet.RingMin + 30f)
            : 0f;
        // Ceiling of the deep zone: under the sea when the world has one, otherwise
        // directly under the upper worm network's floor (same 0.38 as CarveWormTunnels).
        var deepCeil = def.LavaFillFrac > 0f ? seaFloor : radius * 0.38f;

        // …but `radius` counts the SKY: Planet.Radius is RingMin + Rings, and Rings carries
        // a FIXED 142-ring sky headroom regardless of world size. On a standard world the
        // crust dwarfs that and 0.38×radius lands ~130 tiles down — fine. On the shrunken QA
        // rig (SizeScale 0.49) the sky is two thirds of the radius, so the same fraction put
        // the deep-strata ceiling — and its 8-tile seam — FOUR tiles under the grass: every
        // lake bowl was carved through a seam, SealSeams honoured its absolute contract and
        // slabbed the bowl back up, and that slab is the "lake with its bottom cut off". The
        // upper worm band was squeezed into the same 4 tiles, below its own hard floor.
        // So measure against the CRUST (surface radius down to RingMin) instead and take
        // whichever is deeper: the deep zone always starts in the lower half of the rock.
        // Sea worlds are exempt — their ceiling is the sea floor, which Game1's flood shares.
        if (def.LavaFillFrac <= 0f)
            deepCeil = MathF.Min(deepCeil, Planet.RingMin + planet.SurfaceRing * 0.55f);
        const float seamThick = 8f;              // 32 px of guaranteed solid rock per seam
        var coreTop = Planet.RingMin + 2f;       // strata may reach the core shell face
        seams.Add((deepCeil - seamThick, deepCeil));
        var top = deepCeil - seamThick;
        if (top - coreTop < 24f) return (seams, bands, seaFloor);   // too small for a deep zone
        if (top - coreTop > 110f)
        {
            // Thick deep zones split into TWO strata around a mid seam — two mining
            // frontiers on the way down instead of one.
            var mid = coreTop + (top - coreTop) * 0.5f;
            bands.Add((mid + seamThick * 0.5f, top));
            seams.Add((mid - seamThick * 0.5f, mid + seamThick * 0.5f));
            bands.Add((coreTop, mid - seamThick * 0.5f));
        }
        else
        {
            bands.Add((coreTop, top));
        }
        return (seams, bands, seaFloor);
    }

    /// <summary>Noita-style interconnecting tunnels: a couple dozen "perlin worms" wander
    /// the crust carving narrow winding corridors, each with a chance to fork once. The
    /// noise caves give chambers; the worms give the paths BETWEEN them — the difference
    /// between isolated pockets you mine into and a cave system you can travel. Worms stay
    /// below the dirt band (the surface keeps its skin), never carve anchored tiles or
    /// obsidian (acid/volcano linings stay sealed), and simply leave those tiles standing
    /// as natural dead-ends when they meet one. Below the upper network, CarveDeepStrata
    /// adds the sealed deep layers.</summary>
    private static void CarveWormTunnels(Planet planet, PlanetDef def, Random rng)
    {
        // High-lava worlds skip the UPPER worms: their habitable band is a thin shell
        // squeezed between the flood line and the surface (the warrens carry the
        // underground feel there), and every tunnel that grazes the lava zone becomes
        // permanent plumbing that keeps the cell sim awake — measured at 3× the steady
        // update budget. They still get the deep strata below the sea.
        if (def.LavaFillFrac <= 0.5f)
        {
            // Dense enough that neighbouring walks intersect constantly — the underground
            // should read as one connected Noita warren, not isolated corridors.
            var worms = 30 + rng.Next(11);
            // Ocean worlds honeycomb the crust harder: the surface is nearly all sea, so
            // the explorable real estate lives underground — extra walks below, plus the
            // vaulted chambers after the loop. The obsidian seabed shell (stamped in the
            // tile pass, unbiteable by these worms) is what keeps all of it dry.
            var ocean = def.LakeScale > 2.5f;
            if (ocean) worms += 20;
            // Stay above THIS world's lava zone (crossing the flood line turns tunnels into
            // permanent lava plumbing) and below the dirt band. The hard floor passed to
            // CarveWorm stops drifting walks from ever biting the stratum seam below.
            // Ocean worlds run the band DEEPER (0.30 vs 0.38): their seas plunge far below
            // the ordinary worm floor, and the quiet 0.22 lava fill leaves the room — the
            // +0.08 flood-line margin still holds.
            var minFrac = MathF.Max(ocean ? 0.30f : 0.38f, def.LavaFillFrac + 0.08f);
            var (upperSeams, _, _) = CaveStrata(planet, def);
            var hardFloorPx = upperSeams[0].lo * Planet.TileSize;
            var (floorTiles, ceilTiles, driftCeilTiles) = WormBand(planet, def, minFrac, upperSeams);
            for (var i = 0; i < worms; i++)
            {
                var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
                var radiusTiles = MathHelper.Lerp(floorTiles, ceilTiles, (float)rng.NextDouble());
                var start = planet.Center
                    + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radiusTiles * Planet.TileSize;
                CarveWorm(planet, rng, start, (float)rng.NextDouble() * MathHelper.TwoPi,
                    260 + rng.Next(300), branchBudget: 2, floorTiles, driftCeilTiles, hardFloorPx,
                    localCeiling: ocean);
            }

            // Ocean chambers: vaulted halls strung along the worm band — the destination
            // rooms the corridors run between (ore walls, spawn floors, room to fight).
            // Rolled AFTER the worm loop so the extra draws can't shift worm layouts on
            // other-world seeds; ocean-only, so non-ocean streams are untouched anyway.
            if (ocean)
            {
                var chambers = 9 + rng.Next(5);
                for (var i = 0; i < chambers; i++)
                {
                    var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
                    // Mid-crust band, held clear of both the stratum seam below and the
                    // deepest possible seabed above (a chamber may nudge the shell — it
                    // can't bite obsidian — but shouldn't waste half its volume on it).
                    var radiusTiles = MathHelper.Lerp(planet.Radius * minFrac + 8f,
                        maxTiles - 14f, (float)rng.NextDouble());
                    if (radiusTiles <= planet.Radius * minFrac + 8f) continue;
                    var centre = planet.Center + new Vector2(MathF.Cos(ang), MathF.Sin(ang))
                        * radiusTiles * Planet.TileSize;
                    if (NearDenOrCity(planet, centre)) continue;
                    // Local-surface clearance: the global cap can't see valleys — a hall
                    // vaulting up under a low-lying shallow shore would breach it.
                    if (radiusTiles > planet.SurfaceRadiusAt(centre) - 36f) continue;
                    // A hall is a clutch of overlapping bites around the centre.
                    var lobes = 3 + rng.Next(3);
                    for (var b = 0; b < lobes; b++)
                    {
                        var off = new Vector2(
                            ((float)rng.NextDouble() - 0.5f) * 26f,
                            ((float)rng.NextDouble() - 0.5f) * 18f);
                        CarveWormDisk(planet, centre + off, 10f + (float)rng.NextDouble() * 5f);
                    }
                    // Every hall sends out a connector worm: a hall nobody's corridors
                    // happen to cross is a sealed pocket, and the whole point down here
                    // is one travellable network.
                    CarveWorm(planet, rng, centre, (float)rng.NextDouble() * MathHelper.TwoPi,
                        130 + rng.Next(90), branchBudget: 1, minFrac, hardFloorPx,
                        localCeiling: true);
                }
            }
        }

        CarveDeepStrata(planet, def, rng);
    }

    /// <summary>Squared distance from a point to a segment — capsule test for the volcano
    /// plumbing keep-out (a chamber disc is a zero-length segment).</summary>
    private static float DistPointSegSq(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len2 = ab.LengthSquared();
        var t = len2 < 1e-4f ? 0f : MathHelper.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    /// <summary>Plug every Sky tile inside a stratum seam back to solid rock — the final,
    /// authoritative enforcement of the seam contract (see the call site in Generate). The
    /// wall layer captured the structural material before any carver ran, so the plug is
    /// whatever rock genuinely belonged there.</summary>
    private static void SealSeams(Planet planet, PlanetDef def)
    {
        var (seams, _, _) = CaveStrata(planet, def);
        foreach (var (lo, hi) in seams)
        {
            var r0 = Math.Max(0, (int)(lo - Planet.RingMin));
            var r1 = Math.Min(planet.Rings - 1, (int)(hi - Planet.RingMin) + 1);
            for (var r = r0; r <= r1; r++)
            {
                var radTiles = Planet.RingMin + r + 0.5f;
                if (radTiles < lo || radTiles >= hi) continue;
                var n = planet.TilesAt(r);
                for (var t = 0; t < n; t++)
                {
                    if (planet.Get(r, t) != TileKind.Sky) continue;
                    var wall = planet.GetWall(r, t);
                    planet.Set(r, t, wall != TileKind.Sky ? wall : TileKind.Basalt);
                }
            }
        }
    }

    /// <summary>The deep cave strata: worm networks BELOW the lava sea (or below the upper
    /// network on lava-less worlds), reaching all the way down to the core shell. Each
    /// stratum is internally connected but sealed off from everything above by CaveStrata's
    /// solid seams — enforced here as HARD carve bands (a drifting worm keeps walking but
    /// bites nothing outside its stratum) and in Generate as noise-cave suppression. Deep
    /// rock is mostly obsidian/basalt, so these worms MAY bite obsidian — but they detour
    /// around volcano bearings (throat/chamber linings must stay sealed) and warren halls.</summary>
    private static void CarveDeepStrata(Planet planet, PlanetDef def, Random rng)
    {
        var (_, bands, _) = CaveStrata(planet, def);
        // Volcano plumbing bearings — an obsidian-biting worm must never breach a throat.
        var vents = new float[planet.VolcanoVents.Count];
        for (var i = 0; i < vents.Length; i++)
        {
            var (vx, vy, _) = planet.VolcanoVents[i];
            var rel = planet.TileToWorld(vx, vy) - planet.Center;
            vents[i] = MathF.Atan2(rel.Y, rel.X);
        }
        foreach (var (lo, hi) in bands)
        {
            // Deeper bands have less circumference, so scale count with band thickness.
            var worms = 8 + (int)((hi - lo) * 0.22f) + rng.Next(5);
            for (var i = 0; i < worms; i++)
            {
                var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
                var radiusTiles = MathHelper.Lerp(lo + 3f, hi - 3f, (float)rng.NextDouble());
                var start = planet.Center
                    + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radiusTiles * Planet.TileSize;
                CarveDeepWorm(planet, rng, start, (float)rng.NextDouble() * MathHelper.TwoPi,
                    120 + rng.Next(200), branchBudget: 2, lo, hi, vents);
            }
        }
    }

    private static void CarveDeepWorm(Planet planet, Random rng, Vector2 pos, float heading,
        int length, int branchBudget, float bandLoTiles, float bandHiTiles, float[] ventBearings)
    {
        // 4-tile inset: the walk point is clamped, but each bite is a disk of up to 10 px
        // (2.5 tiles) around it — the inset keeps the disk EDGE inside the band, so no bite
        // can nibble into a seam (SealSeams would plug it anyway; don't waste the carve).
        var minPx = (bandLoTiles + 4f) * Planet.TileSize;
        var maxPx = (bandHiTiles - 4f) * Planet.TileSize;
        if (maxPx <= minPx) return;
        for (var s = 0; s < length; s++)
        {
            heading += ((float)rng.NextDouble() - 0.5f) * 0.55f;
            var rel = pos - planet.Center;
            var dist = rel.Length();
            if (dist > maxPx || dist < minPx)
            {
                var radial = MathF.Atan2(rel.Y, rel.X);
                var desired = dist > maxPx ? radial + MathF.PI : radial;
                heading += MathHelper.WrapAngle(desired - heading) * 0.25f;
            }
            pos += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * Planet.TileSize;
            // HARD band clamp — steering is soft and drifts, but carving is what must never
            // cross a stratum seam, so a worm outside its band walks without biting.
            rel = pos - planet.Center;
            dist = rel.Length();
            var nearVent = false;
            if (ventBearings.Length > 0)
            {
                var posAng = MathF.Atan2(rel.Y, rel.X);
                // Coarse bearing skip only — the authoritative plumbing keep-out (chamber
                // disc + throat capsule, which this margin can never fully cover at
                // chamber depth) lives in CarveWormDisk, the chokepoint every bite from
                // every worm family passes through.
                foreach (var v in ventBearings)
                    if (MathF.Abs(MathHelper.WrapAngle(posAng - v)) < 0.07f) { nearVent = true; break; }
            }
            if (dist >= minPx && dist <= maxPx && !nearVent && !NearDenOrCity(planet, pos))
                CarveWormDisk(planet, pos, rng.Next(3) == 0 ? 10f : 7f, biteObsidian: true);
            if (branchBudget > 0 && s > length / 4 && rng.Next(50) == 0)
            {
                branchBudget--;
                CarveDeepWorm(planet, rng, pos,
                    heading + (rng.Next(2) == 0 ? 1f : -1f) * (0.8f + (float)rng.NextDouble()),
                    length / 2, length > 80 ? 1 : 0, bandLoTiles, bandHiTiles, ventBearings);
            }
        }
    }

    /// <param name="localCeiling">Ocean worlds: bites must also stay 14 legacy tiles below
    /// the LOCAL surface line (SurfaceProfile), not just the global cap — the rolling
    /// channel digs valleys deep enough that a globally-legal bite can surface inside a
    /// shallow shore basin and turn the sea into a drain. Other worlds keep the historical
    /// global-only rule so their layouts stay byte-identical.</param>
    private static void CarveWorm(Planet planet, Random rng, Vector2 pos, float heading,
        int length, int branchBudget, float minFrac, float hardFloorPx,
        bool localCeiling = false)
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
            // natural plug where it crossed. The hard floor is the top of the first stratum
            // seam: soft steering lets a walk DRIFT below its band, which was harmless when
            // everything below was solid, but must never puncture the seam now that sealed
            // strata live under it.
            // NOTE: the radius roll stays INSIDE the guarded call — on non-ocean worlds the
            // guard set is unchanged, so the rng stream (and every seeded layout) is too.
            if ((pos - planet.Center).LengthSquared() >= hardFloorPx * hardFloorPx
                && !NearDenOrCity(planet, pos)
                && (!localCeiling || (pos - planet.Center).Length()
                    <= (planet.SurfaceRadiusAt(pos) - 28f) * Planet.TileSize))
                CarveWormDisk(planet, pos, rng.Next(3) == 0 ? 11f : 8f);
            if (branchBudget > 0 && s > length / 4 && rng.Next(55) == 0)
            {
                branchBudget--;
                // Branches fork once more themselves — second-generation forks are what
                // stitch neighbouring worm systems into one continuous warren.
                CarveWorm(planet, rng, pos,
                    heading + (rng.Next(2) == 0 ? 1f : -1f) * (0.8f + (float)rng.NextDouble()),
                    length / 2, length > 80 ? 1 : 0, minFrac, hardFloorPx, localCeiling);
            }
        }
    }

    /// <summary>Ocean worlds: open a few natural grotto mouths on the islands — winding
    /// entrance shafts from the surface down into the worm band, so the dry under-sea cave
    /// network has walk-in doors on dry land. Bearings must be genuinely dry (any sea
    /// coverage disqualifies — a mouth on a seabed is just a drain) and keep off the
    /// mountain cores; mouths spread out so different islands get different doors.</summary>
    private static void CarveIslandGrottoes(Planet planet, Random rng,
        (float ang, float depth, float w)[] lakes, (float ang, float h, float w)[] mountains)
    {
        var want = 3 + rng.Next(2);
        var mouths = new List<float>();
        for (var attempt = 0; attempt < 240 && mouths.Count < want; attempt++)
        {
            var ang = (float)rng.NextDouble() * MathHelper.TwoPi;
            // The +0.06 margin covers the shaft's own sideways drift: a mouth a hair past
            // a lake's rim could otherwise wander under the bowl and become a drain.
            var wet = false;
            foreach (var l in lakes)
                if (MathF.Abs(MathHelper.WrapAngle(ang - l.ang)) < l.w + 0.06f) { wet = true; break; }
            if (wet) continue;
            if (NearMountain(mountains, ang, 0.03f)) continue;
            var apart = true;
            foreach (var m in mouths)
                if (MathF.Abs(MathHelper.WrapAngle(ang - m)) < 0.35f) { apart = false; break; }
            if (!apart) continue;
            mouths.Add(ang);

            // Winding descent: open at the local ground line, then bite mostly inward with
            // a lazy side-sway until the shaft reaches worm-band depth (~35-48 tiles) — a
            // grotto, not a drilled bore.
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            var pos = planet.Center + dir
                * (planet.SurfaceRadiusAt(planet.Center + dir) * Planet.TileSize);
            var heading = MathF.Atan2(-dir.Y, -dir.X) + ((float)rng.NextDouble() - 0.5f) * 0.5f;
            var steps = 34 + rng.Next(14);
            for (var s = 0; s < steps; s++)
            {
                var toCore = planet.Center - pos;
                var inward = MathF.Atan2(toCore.Y, toCore.X);
                heading += MathHelper.WrapAngle(inward - heading) * 0.22f
                    + ((float)rng.NextDouble() - 0.5f) * 0.5f;
                pos += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * Planet.TileSize;
                if (!NearDenOrCity(planet, pos))
                    CarveWormDisk(planet, pos, s < 4 ? 6f : 7.5f);
            }
        }
    }

    /// <summary>Plug every open drain a carver left against a to-be-poured lava/acid body:
    /// any Sky tile in the 2-tile halo AT OR BELOW a seed's ring that is neither part of
    /// the fill nor the basin's own carved air (its freeboard/rim courses, recorded during
    /// the basin carve) gets sealed with the barrier material — 2 tiles to match the
    /// jacket contract. Fluid never climbs, so the halo's outward rows stay untouched
    /// (crater mouths, lake surfaces, bowl air). This is the absolute half of the tunnel
    /// guards: the keep-out stops the worms, this stops what was carved before the seeds
    /// existed (noise caves) or slipped a diagonal. Lava plugs with its jacket rock, acid
    /// with the only kind it can't eat. The basin's carved-but-unseeded LID (freeboard/rim
    /// courses) stays open as the pool's own surface — but its tiles are walled like the
    /// seeds themselves: the settle slosh crests a course into the lid, so a shaft or
    /// shoreline gap at lid level is a drain exactly like one at fill level.</summary>
    private static void PlugFluidBreaches(Planet planet, HashSet<long> lavaLid,
        HashSet<long> acidLid, HashSet<long> waterLid, bool plugWater)
    {
        // The same soft kinds ShellLavaBodies hardens: a MELTABLE tile is no barrier —
        // the crest ate straight through a grass-and-sand shoreline and drained anyway,
        // so the halo converts them to the barrier rock, not just the open Sky.
        static bool Soft(TileKind k) => k is
            TileKind.Sky or TileKind.Dirt or TileKind.Grass or TileKind.Stone
            or TileKind.Gravel or TileKind.MossStone or TileKind.Granite
            or TileKind.Basalt or TileKind.Snow or TileKind.Conglomerate;

        // `melts`: lava and acid eat their way through a soft tile, so for them a meltable
        // solid in the halo is no barrier at all and gets converted outright. Water eats
        // nothing — it only needs the OPEN tiles closed, and rewriting live rock as lake
        // bed would recolour the crust around every basin for no containment gain.
        void Plug(List<(int x, int y)> seeds, HashSet<long> lid, TileKind barrier, bool melts)
        {
            var open = new HashSet<long>(lid);
            foreach (var (r, t) in seeds) open.Add(Planet.TileKey(r, t));
            void Halo(int r, int t)
            {
                var n = planet.TilesAt(r);
                for (var dr = -2; dr <= 0; dr++)
                {
                    var r2 = r + dr;
                    if (r2 < 0) continue;
                    var n2 = planet.TilesAt(r2);
                    var t2c = (int)((t + 0.5f) / n * n2);
                    for (var dt = -2; dt <= 2; dt++)
                    {
                        var t2 = ((t2c + dt) % n2 + n2) % n2;
                        if (open.Contains(Planet.TileKey(r2, t2))) continue;
                        var k = planet.Get(r2, t2);
                        if (melts ? Soft(k) : k == TileKind.Sky)
                        {
                            // A water plug takes the structural wall the tile was cut from,
                            // so a sealed hole reads as the crust around it rather than a
                            // dirt scar sitting in a granite band.
                            var w = planet.GetWall(r2, t2);
                            planet.Set(r2, t2, melts || !Tiles.IsSolid(w) ? barrier : w);
                        }
                    }
                }
            }
            foreach (var (r, t) in seeds) Halo(r, t);
            foreach (var key in lid) Halo((int)(key / 4_000_000L), (int)(key % 4_000_000L));
        }
        Plug(planet.LavaSeeds, lavaLid, TileKind.LavaRock, melts: true);
        Plug(planet.AcidSeeds, acidLid, TileKind.Obsidian, melts: true);
        // Water last: it only fills Sky, so it can never overwrite the jackets above. Only
        // the surface BASINS — a crust reservoir's open neighbours are its own cave — and
        // only on lake worlds. An ocean's sea is 70%+ of the bearings, and its containment
        // is already a different, stronger design: an obsidian shell under the seabed with a
        // cave-free buffer under that. Running the plug over it as well buys nothing and
        // walls up the island grottoes near the shore, which are the one sanctioned way into
        // the under-sea network.
        if (plugWater) Plug(planet.LakeBasinSeeds, waterLid, TileKind.Dirt, melts: false);
    }

    /// <summary>Expand every fluid seed tile by the 2-tile jacket reach into
    /// <see cref="Planet.FluidKeepOut"/> — the halo the tunnel carvers refuse to bite.
    /// 2 tiles matches the shell contract: what survives between a tunnel and the fluid is
    /// exactly the wall ShellLavaBodies hardens to LavaRock (or LineAcidReservoirs skins
    /// in obsidian) at load. Water and oil are in here as well as the hazard fluids: a worm
    /// undercutting a lake bed drains the lake into the tunnel network at load, and a worm
    /// through an oil sump empties it the same way — the pockets are meant to be FOUND, so
    /// the only thing that should open one is a pickaxe.</summary>
    private static void BuildFluidKeepOut(Planet planet, PlanetDef def)
    {
        void Halo(List<(int x, int y)> seeds)
        {
            foreach (var (r, t) in seeds)
            {
                var n = planet.TilesAt(r);
                for (var dr = -2; dr <= 2; dr++)
                {
                    var r2 = r + dr;
                    if (r2 < 0 || r2 >= planet.Rings) continue;
                    var n2 = planet.TilesAt(r2);
                    var t2c = (int)((t + 0.5f) / n * n2);
                    for (var dt = -2; dt <= 2; dt++)
                        planet.FluidKeepOut.Add(Planet.TileKey(r2, ((t2c + dt) % n2 + n2) % n2));
                }
            }
        }
        Halo(planet.LavaSeeds);
        Halo(planet.AcidSeeds);
        Halo(planet.OilSeeds);
        // Ocean seas are exempt, as they are from the plug pass: an ocean world's water is
        // held out of the caves by the obsidian seabed shell (which no worm can bite) over a
        // cave-free buffer, and its WaterSeeds are that whole sea — 70%+ of the bearings.
        // Haloing it walls off the island grottoes' descent, and the shafts are the design's
        // one way into the under-sea network. Lake worlds have no shell, so they need this.
        if (def.LakeScale <= 2.5f) Halo(planet.WaterSeeds);
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

    /// <summary>One worm step's bite: a small disk of soft tiles → Sky. Anchored tiles
    /// always stay (city foundations, the core). Obsidian stays too UNLESS
    /// <paramref name="biteObsidian"/> — the deep strata run mostly through obsidian/basalt
    /// bedrock and would carve nothing without it; their volcano-bearing detour is what
    /// protects the plumbing linings down there instead.</summary>
    private static void CarveWormDisk(Planet planet, Vector2 centre, float radius,
        bool biteObsidian = false)
    {
        // Volcano plumbing keep-out: refuse any bite whose disk reaches a chamber shell or
        // throat sleeve. Enforced HERE — the single chokepoint every worm family funnels
        // through (upper network, deep strata, ocean halls, connectors) — because a breach
        // of a primed column drains the whole volcano to the core: the upper worms had no
        // vent awareness at all, and the deep worms' bearing margin never covered a
        // chamber's angular width. 6px slack on top of the disk radius.
        foreach (var (za, zb, zr) in planet.PlumbingZones)
            if (DistPointSegSq(centre, za, zb) < (zr + radius + 6f) * (zr + radius + 6f))
                return;
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
                // LavaRock is spared in BOTH modes: it only ever exists as a lava body's
                // jacket (crater lining, chamber shell) — never as biteable bedrock — so
                // even the deep-strata worms must leave it. The fluid keep-out is the
                // same contract for jackets that don't exist yet: the pour comes after
                // every carver, so the halo around each seed is the barrier's footprint.
                if (k == TileKind.Sky || Tiles.IsAnchored(k) || k == TileKind.LavaRock
                    || (!biteObsidian && k == TileKind.Obsidian)
                    || planet.FluidKeepOut.Contains(Planet.TileKey(r, t))) continue;
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
    /// pool that connects through a primed 3-tile lava-rock tube down to a shallow lava
    /// GEYSER well — the tube ends just below the first crust layer under the cone, and the
    /// well periodically pumps the column up the tube to overflow the rim (see the eruption
    /// tick in Game1). Drilling into the column anywhere lets it flow out through the breach.
    /// Acid worlds (def.VolcanoAcid)
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
            // Resting fill: 80% of the bowl — a visible fifth of the crater wall stands dry
            // above the pool. Eruptions RAISE the level from here to 110–130% of the rim so
            // the magma visibly climbs and bubbles over the sides; the eruption tick derives
            // this exact geometry back from the vent ring (poolTop = 0.91·coneH — keep the
            // two in sync or the runtime level math drifts).
            var poolTop = coneH - craterDepth * 0.2f;
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
                        // Lava craters line with LAVA ROCK, not obsidian — the whole lava
                        // jacket contract is one material (obsidian stays the ACID seal;
                        // lava rock is corrodible, so the acid bowl above keeps obsidian).
                        // Same rng draw as the legacy mix so downstream placement is stable.
                        k = rng.Next(3) == 0 ? TileKind.LavaRock : TileKind.Basalt;
                    else if (f > 0.55f && above > h - 1.5f * S)
                        k = TileKind.Gravel;                       // ash skirt
                    planet.SetWall(r, t, TileKind.Basalt);
                    planet.Set(r, t, k);
                }
            }

            // The lava GEYSER well: a shelled molten pit at the FOOT of the tube, sunk just
            // below the first crust layer under the cone (not the old near-core chamber).
            // Lava wells wear a LAVA-ROCK shell (melt/burn-proof — the same jacket every lava
            // body gets); acid wells keep OBSIDIAN, the only kind acid can't corrode. The
            // well holds a primed column that eruptions pump up the tube and over the rim.
            // HALF the old well: a tight bulb just big enough to hold the geyser node and
            // its molten bath. (Same single rng draw — downstream stamp order is sacred.)
            var chamberRad = Math.Max((int)(1.5f * S),
                (int)(((3 + rng.Next(2)) * 0.5f + scale * 0.5f) * S));
            // End the tube below the first underground layer: past the ~10-tile dirt band
            // and a little into the stone crust, so the geyser sits in rock but stays a
            // shallow, reachable conduit rather than a deep-core reservoir.
            var chamberR = surfaceR - (int)((16 + rng.Next(8)) * S) - chamberRad;
            if (def.VolcanoAcid)
            {
                // Keep acid plumbing above the global lava flood so the two never mix.
                var lavaTop = (int)(planet.Radius * def.LavaFillFrac) - Planet.RingMin;
                chamberR = Math.Max(chamberR, lavaTop + chamberRad + (int)(4 * S));
            }
            chamberR = Math.Max(chamberR, (int)(14 * S) + chamberRad);

            var nC = planet.TilesAt(chamberR);
            var centre = planet.TileToWorld(chamberR, (int)((ang / MathHelper.TwoPi + 1f) % 1f * nC));
            // TWO-tile shell: honours the every-lava-body 2-block jacket contract, and a
            // 1-tile Euclidean annulus on the polar grid had diagonal pinholes at the
            // circle's shoulders — cell flow crosses tile diagonals at ring remaps.
            var shellR = (chamberRad + 2) * Planet.TileSize;
            // Chamber keep-out disc for every worm carver — see Planet.PlumbingZones.
            planet.PlumbingZones.Add((centre, centre, shellR));
            for (var dr = -chamberRad - 2; dr <= chamberRad + 2; dr++)
            {
                var r = chamberR + dr;
                if (r < 2 || r >= planet.Rings) continue;
                var rn = planet.TilesAt(r);
                var rt0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * rn);
                for (var dt = -chamberRad - 3; dt <= chamberRad + 3; dt++)
                {
                    var t = ((rt0 + dt) % rn + rn) % rn;
                    var dist = (planet.TileToWorld(r, t) - centre).Length();
                    if (dist > shellR) continue;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (dist <= chamberRad * Planet.TileSize)
                    {
                        planet.SetWall(r, t, TileKind.Basalt);
                        // The GEYSER node: a solid molten heart at the bulb's centre. It is
                        // the pump behind every eruption — mine it out and the volcano falls
                        // silent for good (Game1.HandleGeyserBroken removes the vent), paying
                        // out a lava core. Solid, so it takes no lava seed.
                        if (dist <= 1.6f * Planet.TileSize)
                        {
                            planet.Set(r, t, TileKind.Geyser);
                            continue;
                        }
                        planet.Set(r, t, TileKind.Sky);
                        seeds.Add((r, t));
                    }
                    else
                    {
                        var shell = def.VolcanoAcid ? TileKind.Obsidian : TileKind.LavaRock;
                        planet.SetWall(r, t, shell);
                        planet.Set(r, t, shell);
                    }
                }
            }

            // The throat: a primed LAVA-ROCK tube from the geyser roof up to the crater
            // floor, so it connects the well straight through the bottom of the top bowl.
            // Open bore is 3 tiles wide (dt −1..1); a 2-tile lava-rock lining each side
            // (obsidian for acid) sleeves it so the soft dirt band it crosses can't slump
            // in, and the rising column can't dissolve its own walls.
            const int throatOpen = 1;                   // half-width of open bore → 3 tiles
            const int throatLine = 2;                   // lining courses each side
            var throatSpan = throatOpen + throatLine;   // total half-span carved + lined
            // Throat keep-out capsule (geyser → crater floor) — see Planet.PlumbingZones.
            var throatTop = planet.Center + new Vector2(MathF.Cos(ang), MathF.Sin(ang))
                * (Planet.RingMin + floorR + 1) * Planet.TileSize;
            planet.PlumbingZones.Add((centre, throatTop, (throatSpan + 0.5f) * Planet.TileSize));
            // The carve runs PAST floorR: the bowl's floor profile rises away from the exact
            // centre bearing (h dips only at f=0), so stopping at floorR left a solid basalt
            // WEDGE over the bore — a tube that never actually reached the bowl. Above floorR
            // the loop only converts tiles that are still solid (bowl air is left alone, so
            // no lining pillars poke up into the crater), cutting the bore through the wedge
            // until every column has broken into the open pool above.
            var throatTopR = Math.Min(planet.Rings - 1, floorR + (int)(craterDepth * 0.6f));
            for (var r = chamberR + chamberRad - 1; r <= throatTopR; r++)
            {
                if (r < 2 || r >= planet.Rings) continue;
                var n = planet.TilesAt(r);
                var t0 = (int)((ang / MathHelper.TwoPi + 1f) % 1f * n);
                for (var dt = -throatSpan; dt <= throatSpan; dt++)
                {
                    var t = ((t0 + dt) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (r > floorR && planet.Get(r, t) == TileKind.Sky) continue;
                    planet.SetWall(r, t, TileKind.Basalt);
                    if (Math.Abs(dt) > throatOpen)
                    {
                        // Lining: LAVA ROCK for lava tubes (the every-lava-body jacket),
                        // OBSIDIAN for acid (the only kind vitriol can't corrode) so the
                        // rising column can't eat its way out through the crust.
                        planet.Set(r, t, def.VolcanoAcid ? TileKind.Obsidian : TileKind.LavaRock);
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
        // wide mutual spacing keeping the towns from merging into its edges. Lot shares
        // are planned up front so each district can demand mountain clearance for its
        // WHOLE row — the wider towers make rows long enough that a mountain mid-row
        // used to split the capital into fragments. If no bearing offers the full-row
        // clearance, the district settles for the old centre-only margin.
        var districtCount = def.CityLots >= 24 ? 2 + rng.Next(2) : 2;
        var lotsPlan = new int[districtCount];
        lotsPlan[0] = districtCount == 1 ? def.CityLots : (int)(def.CityLots * 0.6f);
        for (var i = 0; i < def.CityLots - lotsPlan[0]; i++)
            lotsPlan[1 + i % Math.Max(1, districtCount - 1)]++;

        var centres = new List<float>();
        var lotsOf = new List<int>();
        for (var d = 0; d < districtCount; d++)
        {
            // ~96 px per lot (hull + street) — the row's angular half-length.
            var rowHalfAng = MathF.Min(0.6f, lotsPlan[d] * 96f * 0.5f / surfRadiusPx);
            var cAng = 0f;
            var ok = false;
            for (var tries = 0; tries < 180 && !ok; tries++)
            {
                // First half of the tries insists the whole row clears the mountains,
                // the rover drop AND every stamped landmark — anything mid-row skips
                // lots and splits the district. The fallback half relaxes to the old
                // centre-only margins.
                var rowPad = tries < 90 ? rowHalfAng : 0f;
                cAng = (float)(rng.NextDouble() * MathHelper.TwoPi);
                ok = !NearMountain(mountains, cAng, rowPad + (tries < 90 ? 0.04f : 0.16f))
                     && AngDist(cAng, MathF.PI * 1.5f) > 0.3f + rowPad;
                for (var i = 0; ok && i < avoid.Count; i++)
                    ok = AngDist(cAng, avoid[i].ang) > avoid[i].w + 0.14f + rowPad;
                for (var i = 0; ok && i < centres.Count; i++)
                    ok = AngDist(cAng, centres[i]) > 0.95f;
            }
            if (!ok) continue;
            centres.Add(cAng);
            lotsOf.Add(lotsPlan[d]);
        }
        if (centres.Count == 0) return;

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
                // Widths sized so interiors hold real ROOMS: past the 3-wide ladder shaft
                // and the wall columns, every class keeps several tiles of open floor per
                // side for furniture and pacing civilians (the old widths left mid-rises
                // ~2 usable tiles a side — the apartments read as empty corridors).
                if (classRoll < 0.18)         // small building: a squat shopfront
                {
                    halfWidthPx = 30f + (float)rng.NextDouble() * 12f;
                    height = (int)((6f + (float)rng.NextDouble() * 6f) * S);
                }
                else if (classRoll < 0.60)    // mid-rise block
                {
                    halfWidthPx = 30f + (float)rng.NextDouble() * 16f;
                    height = (int)((18f + (float)rng.NextDouble() * 16f) * S);
                }
                else                          // spire: thinner and tall
                {
                    halfWidthPx = 22f + (float)rng.NextDouble() * 10f;
                    height = (int)((30f + (float)rng.NextDouble() * 26f) * S);
                }
                height = Math.Min(height, (int)(Planet.SkyHeadroom - 16 * S));
                // A narrow street between hulls — never under 16px: each hull quantizes up
                // to a whole column (+≤2px per side) and slab rows carry a 1-tile exterior
                // ledge, so a 12px street could leave neighbouring LEDGES angularly adjacent
                // — structurally fusing the towers, so a tower severed at the street hung
                // off its neighbour instead of toppling.
                var gapPx = 16f + (float)rng.NextDouble() * 12f;
                specs.Add((classRoll, halfWidthPx, height, gapPx));
                rowPx += halfWidthPx * 2f + gapPx;
            }

            var cursor = centres[d] - rowPx / surfRadiusPx * 0.5f;
            var rowTowers = new List<(float ang, float halfW)>();   // this row, west-to-east
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

                BuildTower(ang, classRoll, halfWidthPx, height, rowTowers);
            }

            // Street bridges: an anchored alloy deck at the baseline surface ring spanning
            // every street gap between neighbouring hulls (and the odd skipped lot), with
            // standing headroom cleared above. The ground between towers rolls and dips,
            // which stranded citizens (and the dwarf) in the hollows — and because the
            // deck is anchored alloy, meteors, acid and quakes can't disintegrate the
            // crossing. Doors, hulls and furniture are all anchored too, so the clearing
            // pass can't harm them.
            for (var i = 1; i < rowTowers.Count; i++)
            {
                var (a0, w0) = rowTowers[i - 1];
                var (a1, w1) = rowTowers[i];
                var e0 = a0 + w0 / surfRadiusPx;
                var e1 = a1 - w1 / surfRadiusPx;
                var gapWorld = (e1 - e0) * surfRadiusPx;
                if (gapWorld <= 0f || gapWorld > 120f) continue;
                BuildBridge(e0, e1);
            }
        }

        avoid.AddRange(placed);
        return;

        void BuildBridge(float a0, float a1)
        {
            // Deck at the baseline surface ring (its top face is the towers' door
            // threshold level), padded just over half a tile into each hull so it meets
            // the pilings without a seam; the rings above are cleared for headroom.
            var deckR = surfaceR;
            var topClear = Math.Min(planet.Rings - 2, deckR + (int)(4 * S));
            for (var r = deckR; r <= topClear; r++)
            {
                var n = planet.TilesAt(r);
                var chord = MathHelper.TwoPi / n;
                var t0 = (int)MathF.Floor((a0 - chord * 0.6f) / chord);
                var t1 = (int)MathF.Ceiling((a1 + chord * 0.6f) / chord);
                for (var ti = t0; ti <= t1; ti++)
                {
                    var t = (ti % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    if (r == deckR)
                    {
                        planet.Set(r, t, TileKind.AlienAlloy);
                        planet.SetWall(r, t, TileKind.AlienAlloy);
                    }
                    else
                    {
                        planet.Set(r, t, TileKind.Sky);
                    }
                }
            }
        }

        void BuildTower(float ang, double classRoll, float halfWidthPx, int height,
            List<(float ang, float halfW)> rowTowers)
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

            // COLUMN-LATTICE rasterization. The hull width quantises to a whole number of
            // tile columns, and every ring assigns the SAME logical column j (0..cols-1) to
            // the grid tile nearest that column's lateral position in the tower frame. Roles
            // (door, window, ladder, slab) key off j, so the structure is identical storey
            // to storey; the leftover ≤½-tile grid jitter is erased at draw time, where the
            // renderer snaps engineered tiles back onto this exact lattice (see
            // Planet.CityFacades / FacadeSnapAngle — the polar grid's tiles-per-ring drifts
            // every ring, which is what made towers render as leaning sawtooth staircases).
            var cols = Math.Max(6, (int)MathF.Round(halfWidthPx * 2f / Planet.TileSize));
            var halfW = cols * Planet.TileSize * 0.5f;
            var midJ = cols / 2;                     // ladder spine column
            rowTowers.Add((ang, halfW));             // the street-bridge pass spans these
            planet.CityFacades.Add((ang, halfW, footingR,
                Math.Min(planet.Rings - 1, topR + (int)(7 * S))));

            for (var r = footingR; r <= topR; r++)
            {
                var n = planet.TilesAt(r);
                var chordAng = MathHelper.TwoPi / n;
                var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                var storey = r - baseR;
                var slabRow = storey % floorEvery < 2 && storey >= floorEvery && storey < height - 2;
                // Slab rows carry a one-tile exterior ledge — horizontal ribs that break up
                // the hull. The foundation is a tile wider than the tower for the same
                // bump, so the plinth reads as a footing, not a buried wall.
                var ledge = slabRow || r <= surfaceR ? 1 : 0;
                var gapSide = storey / floorEvery % 2 == 0 ? 1 : -1;
                var prevT = int.MinValue;
                for (var j = -ledge; j < cols + ledge; j++)
                {
                    var lat = -halfW + (j + 0.5f) * Planet.TileSize;
                    var tj = (int)MathF.Round((ang + lat / ringRadius) / chordAng - 0.5f);
                    // Ring chords run a hair off TileSize, so the column map can very rarely
                    // skip a grid tile — backfill the gap so the hull stays airtight.
                    for (var ti = prevT == int.MinValue ? tj : Math.Min(prevT + 1, tj); ti <= tj; ti++)
                    {
                        var t = (ti % n + n) % n;
                        if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                        planet.SetWall(r, t, TileKind.AlienAlloy);

                        // Below the baseline surface: solid piling, whatever the local
                        // elevation noise did to the ground line.
                        if (r <= surfaceR) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                        // Roof cap.
                        if (storey >= height - 2) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                        var edge = j <= 1 || j >= cols - 2;
                        if (edge)
                        {
                            // Street-level doorway (with a glass transom over the lintel) on
                            // both sides; windows above the ground floor per the facade
                            // style; alloy hull everywhere else.
                            var window = curtainWall
                                ? storey >= floorEvery && !slabRow
                                : storey >= floorEvery && storey % floorEvery >= 3
                                                       && storey % floorEvery <= floorEvery - 3;
                            if (storey < doorH)
                                // The outer column is a real working door (closed at gen
                                // time — residents and the dwarf pop it open with E / by
                                // walking up), the inner column is open lobby behind it.
                                planet.Set(r, t, j <= 0 || j >= cols - 1 ? TileKind.DoorClosed
                                                                         : TileKind.Sky);
                            else if (storey == doorH)
                                planet.Set(r, t, TileKind.CityGlass);   // transom over both doors
                            else
                                planet.Set(r, t, window ? TileKind.CityGlass : TileKind.AlienAlloy);
                            continue;
                        }

                        // Interior: floor slab at the base of every storey above the ground
                        // floor (the plinth is street level's floor). The stair gap is a
                        // two-column hole in the slab BESIDE the climb channel, alternating
                        // sides per floor so the open shaft zig-zags — never against the
                        // wall: a wall-side gap severed the slab's outer strip from the hull,
                        // leaving every other floor a free-floating shelf that broke the
                        // tower into loose pieces the moment it toppled (or anything woke
                        // the settle physics under it).
                        var dt = j - midJ;
                        var span = cols / 2;
                        var inGap = slabRow && dt * gapSide is 2 or 3;
                        var slab = slabRow && !inGap;

                        // Climbing spine: a full-height ladder shaft dead-centre in the
                        // tower — always reachable from the ground floor, so every storey
                        // connects. The stair-gap beside it lets you step off each level.
                        if (dt == 0 && storey >= 0 && storey < height - 2)
                        {
                            planet.Set(r, t, TileKind.Ladder);
                            continue;
                        }
                        // The two columns flanking the ladder stay a clear climb channel
                        // (a 1-tile ladder hole through a 2-tile-thick slab used to wall
                        // you in) — but at slab rows they carry LADDER landings instead of
                        // open sky: still climb-through (ladders are passable), and they
                        // lace the spine to every floor slab, so the tower is one connected
                        // structure instead of a loose ladder strand between floating floors.
                        if (Math.Abs(dt) <= 1 && storey >= 0 && storey < height - 2)
                        {
                            planet.Set(r, t, slab ? TileKind.Ladder : TileKind.Sky);
                            continue;
                        }
                        if (slab) { planet.Set(r, t, TileKind.AlienAlloy); continue; }

                        // Apartment furniture on the row sitting directly on each slab:
                        // potted tentacle-plants, levitating egg-chairs, orb lamps — placed
                        // by a position hash (no rng draws, so downstream worldgen streams
                        // hold). Skipped over the stair gap — nothing floats over the hole.
                        if (storey >= floorEvery && storey % floorEvery == 2
                            && Math.Abs(dt) < span - 2 && dt * gapSide is not (2 or 3))
                        {
                            var h = (r * 7919 + t * 104729) & 1023;
                            if (h < 260)
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
                    prevT = tj;
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
                var mastLat = -halfW + (midJ + 0.5f) * Planet.TileSize;   // the spine column's slot
                for (var r = topR + 1; r <= mastTop; r++)
                {
                    var n = planet.TilesAt(r);
                    var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                    var t = ((int)MathF.Round((ang + mastLat / ringRadius) / (MathHelper.TwoPi / n) - 0.5f) % n + n) % n;
                    if (Tiles.IsAnchored(planet.Get(r, t))) continue;
                    planet.Set(r, t, r == mastTop ? TileKind.Beacon : TileKind.AlienAlloy);
                    planet.SetWall(r, t, TileKind.AlienAlloy);
                }
            }
            else if (roofStyle == 1)
            {
                // Stepped crown: two shrinking alloy tiers, a little ziggurat cap — laid on
                // the tower's own column lattice so the tiers snap straight with the hull.
                for (var step = 0; step < 2; step++)
                {
                    var stepW = halfWidthPx * (0.65f - step * 0.28f);
                    var swCols = Math.Max(1, (int)MathF.Round(stepW / Planet.TileSize));
                    for (var dr = 1 + step * (int)S; dr <= (1 + step) * (int)S; dr++)
                    {
                        var r = topR + dr;
                        if (r >= planet.Rings - 1) break;
                        var n = planet.TilesAt(r);
                        var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                        for (var j = midJ - swCols; j <= midJ + swCols; j++)
                        {
                            var lat = -halfW + (j + 0.5f) * Planet.TileSize;
                            var t = ((int)MathF.Round((ang + lat / ringRadius) / (MathHelper.TwoPi / n) - 0.5f) % n + n) % n;
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
                    var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                    var sw = Math.Max(1, (int)MathF.Round(domeW / Planet.TileSize));
                    for (var dt = -sw; dt <= sw; dt++)
                    {
                        var lat = -halfW + (midJ + dt + 0.5f) * Planet.TileSize;
                        var t = ((int)MathF.Round((ang + lat / ringRadius) / (MathHelper.TwoPi / n) - 0.5f) % n + n) % n;
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
                var tD = ((int)MathF.Round(ang / (MathHelper.TwoPi / nD) - 0.5f) % nD + nD) % nD;
                planet.CitySpawns.Add((baseR + 1, tD));
                for (var storey = floorEvery; storey < height - 4; storey += floorEvery * 2)
                {
                    var r = baseR + storey + 2;
                    var nA = planet.TilesAt(r);
                    var tA = ((int)MathF.Round(ang / (MathHelper.TwoPi / nA) - 0.5f) % nA + nA) % nA;
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
                    // Warren corridors honour the same barrier contract as the worms:
                    // never through an obsidian seal or lava-rock jacket, never inside a
                    // fluid seed's halo — a corridor into a reservoir floods the warren.
                    if (k is TileKind.Obsidian or TileKind.LavaRock
                        || planet.FluidKeepOut.Contains(Planet.TileKey(x, y))) continue;
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

    private static void SeedBiomePockets(Planet planet, PlanetDef def, Random rng,
        (float ang, float depth, float w)[] lakes, (float ang, float depth, float w)[] acidPools)
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
                // Every roll for this pocket happens BEFORE the wet skip below, so a skipped
                // bearing consumes exactly the same draws as a carved one and the shared rng
                // stream — with it every downstream carver's layout on every world — is
                // untouched. The ocean-only version of this skip sat above the radius draw
                // and quietly re-rolled the rest of the planet the moment it fired on a new
                // world; the seams-and-tunnels test noticed before anyone else would have.
                var radius = (int)((crystal ? 4 + rng.Next(4) : 3 + rng.Next(3)) * S);

                // Pockets keep out of any basin's SOLID BUFFER (the cave-free rock the tile
                // pass leaves under a lake/pool bed). This carve eats anything unanchored —
                // obsidian shell, buffer, jacket included — and it runs before the keep-out
                // exists, so a cavity that reaches a bed is a drain the pour finds at load:
                // a grove is what emptied the debug rig's water lake from below.
                //
                // WALK the pocket to a dry bearing rather than dropping it. The ocean-only
                // ancestor of this rule just skipped, which was affordable when it fired on
                // one world; applied to all of them it quietly deletes pockets from worlds
                // that only have four or five (verdant), and a grove is a destination, not
                // scenery. Rotating costs no rng draw, so the shared stream — and every
                // downstream carver's layout — stays put either way.
                var top = (depth - radius - 1) / S;         // shallowest point, legacy tiles
                // A bearing with no bowl over it has no bed to undercut — BasinDepthAt is 0
                // there, so the buffer test only applies where the tile pass actually carved
                // one (its own gate is the same `> 0.5`).
                bool Undercuts(float a)
                {
                    foreach (var l in lakes)
                    {
                        var d = BasinDepthAt(l, a);
                        if (d > 0.5f && top < d + 14f) return true;
                    }
                    foreach (var p in acidPools)
                    {
                        var d = BasinDepthAt(p, a);
                        if (d > 0.5f && top < d + 14f) return true;
                    }
                    return false;
                }
                var moved = 0;
                while (Undercuts(ang) && moved < 7)
                {
                    ang = (ang + 2.399963f) % MathHelper.TwoPi;   // golden angle: no orbiting
                    moved++;
                }
                if (Undercuts(ang)) continue;   // an ocean world can genuinely be all shore
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

    /// <summary>How deep a basin's bowl reaches at one bearing, in legacy tiles below the
    /// local surface (0 outside its footprint) — the same parabola the tile pass carves, so
    /// callers that must stay clear of a bed measure against the real thing.</summary>
    private static float BasinDepthAt((float ang, float depth, float w) basin, float ang)
    {
        var angDiff = MathF.Abs(MathHelper.WrapAngle(ang - basin.ang));
        if (angDiff >= basin.w) return 0f;
        var f = angDiff / basin.w;
        return (1f - f * f) * basin.depth;
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
