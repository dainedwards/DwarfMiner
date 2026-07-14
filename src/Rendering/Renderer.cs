using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

public sealed class Renderer
{
    private readonly GraphicsDevice _gd;
    private readonly SpriteBatch _sb;
    private readonly Texture2D _pixel;
    private readonly Texture2D _circle;
    // High-res disc for the planet-core ball — the 32px _circle scaled to core size would
    // show visible stair-stepping.
    private readonly Texture2D _coreTex;
    private readonly PixelFont _font;
    private readonly Lighting _lighting;
    private readonly Texture2D _tileAtlas;
    private readonly Texture2D _stars;
    private readonly Texture2D _atmoTex;
    private readonly Texture2D _wispTex;

    public Renderer(GraphicsDevice gd)
    {
        _gd = gd;
        _sb = new SpriteBatch(gd);
        _pixel = new Texture2D(gd, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _circle = MakeCircle(gd, 32);
        _coreTex = MakeCircle(gd, 256);
        _font = new PixelFont(gd);
        _lighting = new Lighting(gd);
        TileAtlas.Build(gd);
        _tileAtlas = TileAtlas.Texture;
        _stars = MakeStarfield(gd, 256);
        _atmoTex = MakeAtmosphere(gd, 512);
        _wispTex = MakeSoftBlob(gd, 64);
    }

    /// <summary>Kinds whose tile art stays hand-authored + animated in DrawWorld (supports,
    /// ladders, rails, glowing placeables) rather than sampled from the texture atlas.</summary>
    private static bool UsesAuthoredArt(TileKind k) => k is
        TileKind.Core or TileKind.Support or TileKind.ReinforcedSupport or
        TileKind.Ladder or TileKind.Rail or TileKind.Glowshroom or TileKind.Beacon or
        TileKind.DoorClosed or TileKind.DoorOpen or
        TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp;

    public Texture2D Pixel => _pixel;
    public SpriteBatch Batch => _sb;

    /// <summary>Wall-clock seconds, fed in once per frame by the game. Used by the world
    /// renderer to drive sub-tile animation (waving grass, hanging vines).</summary>
    public float Time { get; set; }

    /// <summary>Where the lighting passes rebind after their own RT work — the virtual
    /// scene target the whole frame renders into. See Lighting.SceneTarget.</summary>
    public RenderTarget2D? SceneTarget { set => _lighting.SceneTarget = value; }

    /// <summary>The frame's active light grid — every AddLight call seeds it. Game1 owns
    /// the grid and calls its Begin (occlusion + sun) before the seeding pass, then
    /// RenderLightGrid to propagate/upload/rasterize into the lightmap.</summary>
    public LightGrid? Grid;

    /// <summary>Seed a light into the propagated grid. Radius keeps its historical
    /// meaning (intended reach in world px); occlusion now comes for free — light stops
    /// at rock instead of shining through it.</summary>
    public void AddLight(Vector2 worldPos, float radius, Color color) => Grid?.Seed(worldPos, radius, color);

    /// <summary>This frame's ray-cast hero lights (carried lamp, explosion cores, muzzle
    /// flashes) — collected here, drawn and cleared in RenderLightGrid. See
    /// Lighting.RenderHeroLights.</summary>
    private readonly List<(Vector2 pos, float radius, Color color)> _heroLights = new();
    public void AddHeroLight(Vector2 worldPos, float radius, Color color)
    {
        if (radius > 2f) _heroLights.Add((worldPos, radius, color));
    }

    /// <summary>Propagate the seeded grid, rasterize it into the lightmap RT, then cut the
    /// hero lights' shadow fans over it — ready for CompositeLighting/BloomLighting.</summary>
    public void RenderLightGrid(Camera cam, Planet planet)
    {
        if (Grid is null) return;
        _lightPerfSw.Restart();
        var sw = _lightPerfSw;
        Grid.Propagate();
        var tProp = sw.Elapsed.TotalMilliseconds;
        Grid.Upload(_gd);
        var tUp = sw.Elapsed.TotalMilliseconds - tProp;
        _lighting.RenderGrid(cam, Grid);
        _lighting.RenderHeroLights(_heroLights, planet);
        _heroLights.Clear();
        var tRas = sw.Elapsed.TotalMilliseconds - tUp - tProp;
        if (Environment.GetEnvironmentVariable("DM_LIGHTPERF") is { Length: > 0 } && ++_lightPerfN % 60 == 0)
            Console.WriteLine($"[lightperf] begin {LightGridBeginMs:0.00} prop {tProp:0.00} upload {tUp:0.00} raster {tRas:0.00}");
    }
    private int _lightPerfN;
    private readonly System.Diagnostics.Stopwatch _lightPerfSw = new();
    /// <summary>Set by Game1 around LightGrid.Begin — DM_LIGHTPERF diagnostic only.</summary>
    public double LightGridBeginMs;

    public void CompositeLighting(Point screenSize) => _lighting.Composite(_sb, screenSize);
    public void BloomLighting(Point screenSize, Color tint) => _lighting.Bloom(_sb, screenSize, tint);
    public void VignetteScene(Point screenSize) => _lighting.Vignette(_sb, screenSize);
    public void GradeScene(Point screenSize, Color tint) => _lighting.Grade(_sb, _pixel, screenSize, tint);

    /// <summary>Planet indices of tiles trembling before a cave-in — Game1 wires this to
    /// Physics.TremblingTiles. Tiles in the set are drawn with a per-frame position jitter.</summary>
    public HashSet<int>? TrembleTiles;

    public void DrawWorld(Planet planet, Camera cam)
    {
        var view = cam.View;
        var inv = Matrix.Invert(view);
        var corners = new[]
        {
            Vector2.Transform(Vector2.Zero, inv),
            Vector2.Transform(new Vector2(cam.ViewportSize.X, 0), inv),
            Vector2.Transform(new Vector2(0, cam.ViewportSize.Y), inv),
            Vector2.Transform(new Vector2(cam.ViewportSize.X, cam.ViewportSize.Y), inv),
        };
        var minX = float.MaxValue; var minY = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue;
        foreach (var c in corners)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X > maxX) maxX = c.X;
            if (c.Y > maxY) maxY = c.Y;
        }

        // Use the camera-target distance ± the viewport's half-diagonal (in world units) to bound
        // the visible ring range. This always fully encloses the rectangular viewport regardless
        // of camera rotation, so we never miss tiles that should be visible.
        var camDist = (cam.Target - planet.Center).Length();
        var halfW = cam.ViewportSize.X / cam.Zoom * 0.5f;
        var halfH = cam.ViewportSize.Y / cam.Zoom * 0.5f;
        var halfDiag = MathF.Sqrt(halfW * halfW + halfH * halfH) + Planet.TileSize;
        var minDistTight = MathF.Max(0f, camDist - halfDiag);
        var maxDistTight = camDist + halfDiag;

        var minRing = Math.Max(0, (int)(minDistTight / Planet.TileSize) - Planet.RingMin - 1);
        var maxRing = Math.Min(planet.Rings - 1, (int)(maxDistTight / Planet.TileSize) - Planet.RingMin + 1);

        // Zoomed way out (orbit view, high descent/ascent) a tile is ~3 screen px: the atlas
        // texture, erosion rims, grass wraps, and decor are sub-pixel noise, and the view
        // sees 5-10× more tiles than ground play. Low-detail mode draws one flat quad per
        // tile and skips the deep interior (buried under opaque rock; the odd shaft reads
        // as a dark speck at this scale) — this is what keeps the orbital shot at 60 fps.
        var lowDetail = cam.Zoom < 0.9f;
        if (lowDetail) minRing = Math.Max(minRing, 120);

        // Elevation-layered skybox: backdrop and stars belong to different altitude bands.
        // Underground reads near-black, the lower atmosphere is a moody dusk blue with no
        // stars at all, and the starfield only fades in through the upper atmosphere,
        // reaching full strength in open space — climbing a peak or flying up literally
        // ascends through the sky layers. (The radial atmosphere shell and the orbiting
        // haze wisps drawn below are world-space, so they stay glued to their own bands.)
        var planetR = planet.Radius * Planet.TileSize;
        var elev = camDist / planetR;                 // 0 = core … 1 = outermost ring
        var caveBg = new Color(7, 9, 15);
        var duskBg = new Color(34, 48, 82);
        var spaceBg = new Color(9, 11, 20);
        // Airless worlds (the Hollow) have no dusk band at all: step out of the caves and
        // the sky is space, with the starfield burning at full strength from the surface —
        // standing on the ground already IS standing in space.
        var backdrop = planet.Airless
            ? elev switch
            {
                < 0.60f => caveBg,
                < 0.70f => Color.Lerp(caveBg, spaceBg, (elev - 0.60f) / 0.10f),
                _ => spaceBg,
            }
            : elev switch
            {
                < 0.60f => caveBg,
                < 0.70f => Color.Lerp(caveBg, duskBg, (elev - 0.60f) / 0.10f),
                < 0.80f => duskBg,
                < 0.95f => Color.Lerp(duskBg, spaceBg, (elev - 0.80f) / 0.15f),
                _ => spaceBg,
            };
        var starAlpha = planet.Airless
            ? MathHelper.Clamp((elev - 0.62f) / 0.08f, 0f, 1f)
            : MathHelper.Clamp((elev - 0.82f) / 0.14f, 0f, 1f);

        _gd.Clear(backdrop);

        // Pixelated starfield, Terraria-style: tileable, drawn screen-space at world-pixel
        // chunkiness (PointWrap + integer-zoom upscale) with slow parallax. Faded by the
        // elevation band above — invisible from the surface, dense once you're in space.
        if (starAlpha > 0f)
        {
            var starScale = Math.Max(1, (int)cam.Zoom);
            var parallax = cam.Target * 0.15f + new Vector2(Time * 1.2f, Time * 0.3f);
            _sb.Begin(samplerState: SamplerState.PointWrap);
            _sb.Draw(_stars, new Rectangle(0, 0, cam.ViewportSize.X, cam.ViewportSize.Y),
                new Rectangle((int)parallax.X, (int)parallax.Y,
                    cam.ViewportSize.X / starScale, cam.ViewportSize.Y / starScale),
                Color.White * starAlpha);
            _sb.End();
        }

        _sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: view);

        // Noita-style layered backdrop, back to front: starfield (screen-space, above) →
        // atmosphere shell → drifting haze wisps → terrain. The atmosphere is a radial
        // gradient hugging the surface — dusky teal at the horizon shading into violet and
        // thinning to space by ~0.93 planet radii, so the world reads as a planet wrapped
        // in air, with mountain peaks poking out of the haze and stars dimming behind it.
        // Airless rock wears no shell and no haze — hard vacuum right down to the regolith.
        if (!planet.Airless)
        {
            var atmoScale = planet.Radius * Planet.TileSize * 2f / _atmoTex.Width;
            _sb.Draw(_atmoTex, planet.Center, null, Color.White, 0f,
                new Vector2(_atmoTex.Width / 2f, _atmoTex.Height / 2f), atmoScale, SpriteEffects.None, 0f);
            DrawHazeWisps(planet);
        }

        // Solid PlanetCore ball filling the space inside the innermost tile ring, so the
        // planet centre reads as a sphere the rock layers butt against, not a hole. A hot
        // inner disc marks the drillable Core itself. Edge tucks 0.6 tiles under ring 0.
        if (minRing == 0)
        {
            var origin = new Vector2(_coreTex.Width / 2f, _coreTex.Height / 2f);
            var outerR = (Planet.RingMin + 0.6f) * Planet.TileSize;
            _sb.Draw(_coreTex, planet.Center, null, Tiles.BaseColor(TileKind.PlanetCore), 0f,
                origin, outerR * 2f / _coreTex.Width, SpriteEffects.None, 0f);
            var innerR = Planet.RingMin * Planet.TileSize * 0.55f;
            _sb.Draw(_coreTex, planet.Center, null, Tiles.BaseColor(TileKind.Core), 0f,
                origin, innerR * 2f / _coreTex.Width, SpriteEffects.None, 0f);
        }

        var camAngle = MathF.Atan2(cam.Target.Y - planet.Center.Y, cam.Target.X - planet.Center.X);

        // Orbital stride: at planet-scale zooms a tile is ~1.8 screen px, and the full limb
        // is ~190k tile iterations — sample every 2nd/3rd ring+tile and draw scaled-up
        // quads (visually identical at that scale, a fraction of the quads and loop work).
        var tileStep = cam.Zoom < 0.48f ? 3 : cam.Zoom < 0.55f ? 2 : 1;

        for (var r = minRing; r <= maxRing; r += tileStep)
        {
            var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
            var tpr = planet.TilesAt(r);
            var chord = MathHelper.TwoPi * ringRadius / tpr;

            // Visible angular slice: from the law of cosines, a tile at angle Δθ from the
            // camera direction is within `halfDiag` of the camera target iff cos(Δθ) ≥ k where
            // k = (R² + camDist² − halfDiag²) / (2 · R · camDist). This is the *correct*
            // visibility test (works whether the player is inside, on, or outside the ring),
            // unlike the old atan2 approximation.
            int t0, t1;
            if (camDist < 1f || ringRadius < 1f)
            {
                t0 = 0; t1 = tpr;
            }
            else
            {
                var k = (ringRadius * ringRadius + camDist * camDist - halfDiag * halfDiag)
                      / (2f * ringRadius * camDist);
                if (k <= -1f)
                {
                    t0 = 0; t1 = tpr; // full ring visible
                }
                else if (k >= 1f)
                {
                    continue; // ring is entirely outside the viewport
                }
                else
                {
                    var halfArc = MathF.Acos(k) + 0.05f;
                    var span = (int)(halfArc / MathHelper.TwoPi * tpr) + 2;
                    var centreT = (int)((camAngle / MathHelper.TwoPi + 1f) % 1f * tpr);
                    t0 = centreT - span;
                    t1 = centreT + span;
                }
            }

            for (var ti = t0; ti < t1; ti += tileStep)
            {
                var t = ((ti % tpr) + tpr) % tpr;
                var k = planet.Get(r, t);

                // Analytic polar transform: the loop index fixes the tile's angle, so centre,
                // up and rotation all come from one cos+sin — no TileToWorld re-wrap, no UpAt
                // normalize, no Atan2 per visible tile (rotation of a radial quad is just the
                // tile angle + 90°).
                var angle = (t + 0.5f) / tpr * MathHelper.TwoPi;
                var up = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var centre = planet.Center + up * ringRadius;
                // Cull tiles whose centre is too far from the viewport rect (cheap secondary check).
                if (centre.X < minX - chord || centre.X > maxX + chord ||
                    centre.Y < minY - chord || centre.Y > maxY + chord)
                    continue;

                var rotation = angle + MathHelper.PiOver2;
                var right = new Vector2(-up.Y, up.X);
                // +1 px overlap kills sub-pixel seams; the stride widens the quad to cover
                // its skipped neighbours.
                var size = new Vector2(chord * tileStep + 1f, Planet.TileSize * tileStep + 1f);
                var hash = (r * 73856093) ^ (t * 19349663);

                // Sky tile: nothing to draw. Back-walls are gone — dug-out openings and the
                // horizon both show the pixelated space skybox drawn under the world, so
                // excavations read as holes into the night rather than blocky dim rock.
                if (k == TileKind.Sky) continue;

                // Condemned tiles oscillate before crumbling — shift the whole tile (base
                // quad, shades and rims all draw from `centre`) by a small random offset.
                if (TrembleTiles is { Count: > 0 } && TrembleTiles.Contains(planet.Index(r, t)))
                    centre += right * ((Random.Shared.NextSingle() - 0.5f) * 1.4f)
                            + up * ((Random.Shared.NextSingle() - 0.5f) * 1.4f);

                var jitter = ((hash >> 4) & 31) - 16;
                var col = Tiles.BaseColor(k);
                // Conglomerate tiles carry a per-tile tint blended from the exact grains that
                // compacted into them — a sandy pile presses into sandy rock, an ore-dust
                // pile keeps its glint. Applied as a multiply factor over the near-neutral
                // atlas art (and directly here for the low-detail quad).
                var tintF = Vector3.One;
                if (k == TileKind.Conglomerate && planet.GetComposition(r, t) is { } comp)
                {
                    tintF = new Vector3(
                        comp.Tint.R / (float)Math.Max(1, (int)col.R),
                        comp.Tint.G / (float)Math.Max(1, (int)col.G),
                        comp.Tint.B / (float)Math.Max(1, (int)col.B));
                    col = comp.Tint;
                }
                col = new Color(
                    Math.Clamp(col.R + jitter / 4, 0, 255),
                    Math.Clamp(col.G + jitter / 4, 0, 255),
                    Math.Clamp(col.B + jitter / 4, 0, 255));

                // Low-detail: the jittered base colour IS the tile at this scale.
                if (lowDetail)
                {
                    _sb.Draw(_pixel, centre, null, col, rotation,
                        new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
                    continue;
                }

                // Neighbour kinds drive the erosion mask, the grass wrap and the material
                // bite-rows. Outer band boundaries can have two neighbours: any sky counts
                // as exposed, and the first solid one supplies the merge colour.
                var outerK = TileKind.Sky;
                var outerSky = false;
                var outerCount = planet.OuterNeighbourCount(r, t);
                for (var oi = 0; oi < outerCount; oi++)
                {
                    var (or_, ot_) = planet.OuterNeighbour(r, t, oi);
                    var onk = planet.Get(or_, ot_);
                    if (onk == TileKind.Sky) outerSky = true;
                    else if (outerK == TileKind.Sky) outerK = onk;
                }
                var (ir_, it_) = planet.InnerNeighbour(r, t);
                var innerK = planet.Get(ir_, it_);
                var leftK  = planet.Get(r, t - 1);
                var rightK = planet.Get(r, t + 1);
                var innerSky = innerK == TileKind.Sky;
                var leftSky  = leftK == TileKind.Sky;
                var rightSky = rightK == TileKind.Sky;

                if (UsesAuthoredArt(k))
                {
                    // Placeables keep their hand-authored DrawDeco art below; flat base +
                    // dynamic shade bands, as before.
                    _sb.Draw(_pixel, centre, null, col, rotation,
                        new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
                    var topShade = new Color(
                        Math.Clamp(col.R + 16, 0, 255),
                        Math.Clamp(col.G + 16, 0, 255),
                        Math.Clamp(col.B + 16, 0, 255));
                    var botShade = new Color(
                        Math.Clamp(col.R - 16, 0, 255),
                        Math.Clamp(col.G - 16, 0, 255),
                        Math.Clamp(col.B - 16, 0, 255));
                    DrawDeco(centre, right, up, rotation, chord, 0, 0, 8, 2, topShade);
                    DrawDeco(centre, right, up, rotation, chord, 0, 6, 8, 2, botShade);
                }
                else
                {
                    // Natural materials sample the 16×16 atlas — 2× the tile's world
                    // resolution, so ground reads as textured pixel art. Pack-art kinds pick
                    // their variant from tile parity so the seamless pattern continues across
                    // neighbours; procedural kinds keep the hash-stable random pick. The
                    // exposure mask selects the frame whose air-facing edges are baked with
                    // ragged alpha erosion — silhouettes round off with no edge paint at all.
                    var exposeMask = (outerSky ? 1 : 0) | (innerSky ? 2 : 0)
                                   | (leftSky ? 4 : 0) | (rightSky ? 8 : 0);
                    var shade = (int)(255 * BlobShade(centre));
                    var drawCol = tintF == Vector3.One
                        ? new Color(shade, shade, shade)
                        : new Color(
                            (int)Math.Clamp(shade * tintF.X, 0f, 255f),
                            (int)Math.Clamp(shade * tintF.Y, 0f, 255f),
                            (int)Math.Clamp(shade * tintF.Z, 0f, 255f));
                    // Legacy gem *tiles* (old worlds — gems are overlays now) draw as the
                    // rock their solid neighbours are made of, so they read as embedded too.
                    var atlasKind = k;
                    if (Tiles.IsGem(k))
                        atlasKind = HostRockFor(outerK, innerK, leftK, rightK);
                    _sb.Draw(_tileAtlas, centre,
                        TileAtlas.Source(atlasKind, VariantFor(atlasKind, r, t, hash), exposeMask),
                        drawCol, rotation,
                        new Vector2(TileAtlas.Res * 0.5f, TileAtlas.Res * 0.5f),
                        new Vector2(size.X / TileAtlas.Res, size.Y / TileAtlas.Res),
                        SpriteEffects.None, 0f);
                    // An embedded gem draws on top of whatever host it sits in.
                    var overlayGem = planet.GemAt(r, t);
                    if (overlayGem != TileKind.Sky)
                        DrawGemCrystal(centre, hash, overlayGem);
                    else if (Tiles.IsGem(k))
                        DrawGemCrystal(centre, hash, k);
                    // Precious metals catch the light: gold/silver/platinum tiles carry a
                    // slow travelling glint — each tile flashes a bright speck for a moment
                    // on its own hash phase, so a vein twinkles as you pan past it.
                    else if (k is TileKind.GoldOre or TileKind.SilverOre or TileKind.PlatinumOre)
                    {
                        var phase = (Time * 0.8f + (hash & 0xFF) / 255f) % 1f;
                        if (phase < 0.16f)
                        {
                            var gx = (hash >> 8) & 7;
                            var gy = (hash >> 11) & 7;
                            var bright = phase < 0.08f ? Color.White : Tiles.OreSpeckle(k);
                            DrawDeco(centre, right, up, rotation, chord, gx, gy, 1, 1, bright);
                            if (phase is > 0.04f and < 0.12f) // cross-flare at the peak
                            {
                                DrawDeco(centre, right, up, rotation, chord, Math.Max(0, gx - 1), gy, 1, 1, bright * 0.6f);
                                DrawDeco(centre, right, up, rotation, chord, Math.Min(7, gx + 1), gy, 1, 1, bright * 0.6f);
                            }
                        }
                    }
                }

                // Grass hugs exposed edges, Terraria-style: the green wraps down exposed
                // sides and along a dug-out underside instead of showing bare dirt in
                // cross-section.
                if (k == TileKind.Grass)
                {
                    var wrap = new Color(95, 145, 65);
                    if (leftSky)  DrawDeco(centre, right, up, rotation, chord, 0, 0, 1, 8, wrap);
                    if (rightSky) DrawDeco(centre, right, up, rotation, chord, 7, 0, 1, 8, wrap);
                    if (innerSky) DrawDeco(centre, right, up, rotation, chord, 0, 7, 8, 1, wrap);
                }
                // Crumb accretion: a pinch of the tile's own material scattered on exposed
                // top surfaces — scree on stone ledges, soil on dirt runs. Material-coloured
                // single grains at hash positions (many tiles get none), so flat runs lose
                // their ruler line without any edge strip. Grass grows tufts instead.
                else if (outerSky)
                {
                    var crumbs = (hash >> 9) & 3;
                    for (var ci = 0; ci < crumbs; ci++)
                    {
                        var lx = (hash >> (11 + ci * 5)) & 7;
                        var cj = (((hash >> (14 + ci * 3)) & 7) - 3) * 4;
                        var cc = new Color(
                            Math.Clamp(col.R + cj, 0, 255),
                            Math.Clamp(col.G + cj, 0, 255),
                            Math.Clamp(col.B + cj, 0, 255));
                        DrawDeco(centre, right, up, rotation, chord, lx, -1, 1, 1, cc);
                    }
                }

                // Material separation — no painted boundary line. Where two families meet,
                // the softer material's colour bites a ragged tooth-row into the harder
                // tile's edge (dirt fingers into stone, snow over rock), so the join reads
                // as the materials interlocking — worldgen-style jaggedness — rather than
                // any drawn edge. Each tile only paints inside its own bounds: teeth pushed
                // into a neighbour's area would be overdrawn when that neighbour renders.
                var mg = MergeGroup(k);
                if (mg > 0)
                {
                    if (!outerSky && Merges(mg, outerK) && Bites(outerK, k))
                        BiteEdge(centre, right, up, rotation, chord, hash, Tiles.BaseColor(outerK), horizontal: true, edge: 0);
                    if (!innerSky && Merges(mg, innerK) && Bites(innerK, k))
                        BiteEdge(centre, right, up, rotation, chord, hash ^ 0x2D, Tiles.BaseColor(innerK), horizontal: true, edge: 7);
                    if (!leftSky && Merges(mg, leftK) && Bites(leftK, k))
                        BiteEdge(centre, right, up, rotation, chord, hash ^ 0x53, Tiles.BaseColor(leftK), horizontal: false, edge: 0);
                    if (!rightSky && Merges(mg, rightK) && Bites(rightK, k))
                        BiteEdge(centre, right, up, rotation, chord, hash ^ 0x71, Tiles.BaseColor(rightK), horizontal: false, edge: 7);
                }

                // 8-neighbour ambient occlusion. Sample the 4 diagonal cells; if a corner has
                // both adjacent rims solid but the diagonal cell is sky, paint a 1-px AO dot at
                // that corner. This gives the soft inner-corner shading that makes Terraria's
                // tiles look hand-painted rather than gridded.
                var (olR, olT) = planet.OuterNeighbour(r, t - 1, 0);
                var (orR, orT) = planet.OuterNeighbour(r, t + 1, 0);
                var (ilR, ilT) = planet.InnerNeighbour(r, t - 1);
                var (irR, irT) = planet.InnerNeighbour(r, t + 1);
                var olSky = planet.Get(olR, olT) == TileKind.Sky;
                var orSky = planet.Get(orR, orT) == TileKind.Sky;
                var ilSky = planet.Get(ilR, ilT) == TileKind.Sky;
                var irSky = planet.Get(irR, irT) == TileKind.Sky;
                var ao = new Color(
                    Math.Clamp(col.R - 38, 0, 255),
                    Math.Clamp(col.G - 38, 0, 255),
                    Math.Clamp(col.B - 38, 0, 255));
                if (!outerSky && !leftSky  && olSky) DrawDeco(centre, right, up, rotation, chord, 0, 0, 1, 1, ao);
                if (!outerSky && !rightSky && orSky) DrawDeco(centre, right, up, rotation, chord, 7, 0, 1, 1, ao);
                if (!innerSky && !leftSky  && ilSky) DrawDeco(centre, right, up, rotation, chord, 0, 7, 1, 1, ao);
                if (!innerSky && !rightSky && irSky) DrawDeco(centre, right, up, rotation, chord, 7, 7, 1, 1, ao);

                // Per-kind decoration — static material texture now lives in the atlas; what
                // remains here is either animated (grass tufts, vines, beacon pulse) or the
                // hand-authored art of the placeable kinds. Sub-rects are in 8×8 reference
                // coords; DrawDeco scales X by chord/8.
                switch (k)
                {
                    case TileKind.Grass:
                    {
                        // Animated tufts — only on grass tiles whose outer (sky-facing) edge is
                        // exposed. Each tuft is a vertical pixel column above the tile that
                        // bends with sin(time + hash); bend grows quadratically up the tuft so
                        // tips travel further than bases. Drawn into the sky cell above (which
                        // is already cleared, no draw conflict).
                        if (outerSky)
                        {
                            var tipCol = new Color(140, 195, 95);
                            var midCol = new Color(95, 145, 65);
                            for (var ti2 = 0; ti2 < 3; ti2++)
                            {
                                var baseX = 1 + ((hash >> (3 + ti2 * 5)) & 5);
                                var phase = ((hash >> (ti2 * 7)) & 0xFF) * 0.025f;
                                var sway  = MathF.Sin(Time * 1.6f + phase) * 0.9f;
                                var hgt   = 2 + ((hash >> (ti2 * 4)) & 1);
                                for (var hy = 0; hy < hgt; hy++)
                                {
                                    var fr  = (hy + 1f) / hgt;
                                    var bx  = baseX + (int)MathF.Round(sway * fr * fr);
                                    var lyy = -1 - hy;
                                    var c   = hy == hgt - 1 ? tipCol : midCol;
                                    DrawDeco(centre, right, up, rotation, chord, bx, lyy, 1, 1, c);
                                }
                            }
                        }
                        break;
                    }
                    case TileKind.MossStone:
                    {
                        // Hanging vine — when the inner (cave-facing) edge is exposed, drop a
                        // short pixel column down into the cave that sways slowly. A 4px vine
                        // is enough to read as foliage without crowding the rim.
                        if (innerSky)
                        {
                            var vine = new Color(60, 105, 55);
                            var vineTip = new Color(95, 150, 85);
                            var phase = (hash & 0xFF) * 0.025f;
                            var sway  = MathF.Sin(Time * 1.0f + phase) * 0.7f;
                            var baseX = 2 + ((hash >> 5) & 3);
                            for (var hy = 0; hy < 4; hy++)
                            {
                                var fr = (hy + 1f) / 4f;
                                var bx = baseX + (int)MathF.Round(sway * fr * fr);
                                DrawDeco(centre, right, up, rotation, chord, bx, 8 + hy, 1, 1,
                                    hy == 3 ? vineTip : vine);
                            }
                        }
                        break;
                    }
                    case TileKind.Core:
                    {
                        DrawDeco(centre, right, up, rotation, chord, 2, 2, 4, 4, new Color(255, 180, 80));
                        DrawDeco(centre, right, up, rotation, chord, 3, 3, 2, 2, new Color(255, 235, 190));
                        break;
                    }
                    case TileKind.Support:
                    {
                        var grain = new Color(115, 78, 45);
                        DrawDeco(centre, right, up, rotation, chord, 1, 2, 6, 1, grain);
                        DrawDeco(centre, right, up, rotation, chord, 1, 5, 6, 1, grain);
                        DrawDeco(centre, right, up, rotation, chord, 3, 1, 2, 6, new Color(75, 60, 45));
                        break;
                    }
                    case TileKind.ReinforcedSupport:
                    {
                        // Iron-banded beam: vertical wood post with two riveted iron straps and
                        // a corner gusset plate at each end. Reads as a much sturdier brother
                        // of the regular Support tile — same silhouette, more bracing.
                        var wood = new Color(120, 82, 50);
                        var ironMid = new Color(160, 168, 178);
                        var ironDark = new Color(90, 96, 106);
                        var rivet = new Color(40, 40, 48);
                        DrawDeco(centre, right, up, rotation, chord, 3, 1, 2, 6, wood);
                        // Iron straps (horizontal)
                        DrawDeco(centre, right, up, rotation, chord, 1, 2, 6, 1, ironMid);
                        DrawDeco(centre, right, up, rotation, chord, 1, 5, 6, 1, ironMid);
                        DrawDeco(centre, right, up, rotation, chord, 1, 2, 6, 1, ironDark);   // bottom shadow
                        // Rivets at strap ends
                        DrawDeco(centre, right, up, rotation, chord, 1, 2, 1, 1, rivet);
                        DrawDeco(centre, right, up, rotation, chord, 6, 2, 1, 1, rivet);
                        DrawDeco(centre, right, up, rotation, chord, 1, 5, 1, 1, rivet);
                        DrawDeco(centre, right, up, rotation, chord, 6, 5, 1, 1, rivet);
                        break;
                    }
                    case TileKind.Ladder:
                    {
                        // Two side rails with rungs every other row. Wood colour, brighter than
                        // Support so the climbable tile reads as distinct ironwork-free wood.
                        var rail2 = new Color(180, 130, 75);
                        var rung = new Color(140, 95, 55);
                        DrawDeco(centre, right, up, rotation, chord, 1, 0, 1, 8, rail2);
                        DrawDeco(centre, right, up, rotation, chord, 6, 0, 1, 8, rail2);
                        DrawDeco(centre, right, up, rotation, chord, 2, 1, 4, 1, rung);
                        DrawDeco(centre, right, up, rotation, chord, 2, 4, 4, 1, rung);
                        break;
                    }
                    case TileKind.Rail:
                    {
                        // Wooden ties under two iron rails. Ties run tangentially; rails are
                        // two thin horizontal strips. Looks like a top-down minecart track at
                        // tile scale.
                        var tie = new Color(95, 65, 45);
                        var ironRail = new Color(180, 185, 195);
                        var ironRailDark = new Color(95, 100, 115);
                        DrawDeco(centre, right, up, rotation, chord, 0, 3, 8, 1, tie);
                        DrawDeco(centre, right, up, rotation, chord, 0, 5, 8, 1, tie);
                        DrawDeco(centre, right, up, rotation, chord, 0, 2, 8, 1, ironRail);
                        DrawDeco(centre, right, up, rotation, chord, 0, 6, 8, 1, ironRail);
                        DrawDeco(centre, right, up, rotation, chord, 0, 2, 1, 1, ironRailDark);
                        DrawDeco(centre, right, up, rotation, chord, 7, 6, 1, 1, ironRailDark);
                        break;
                    }
                    case TileKind.Glowshroom:
                    {
                        // Cluster of 2-3 mushrooms with bright caps. The cap colour is the
                        // light source itself (composed multiplicatively in the lighting pass);
                        // the bright pixels act as bloom seeds so the tile reads as luminous.
                        var stem = new Color(220, 220, 200);
                        var capDark = new Color(90, 180, 120);
                        var capBright = new Color(180, 255, 200);
                        var spot = new Color(255, 255, 230);
                        // Big mushroom centred
                        DrawDeco(centre, right, up, rotation, chord, 3, 4, 2, 3, stem);
                        DrawDeco(centre, right, up, rotation, chord, 2, 3, 4, 2, capDark);
                        DrawDeco(centre, right, up, rotation, chord, 2, 2, 4, 1, capBright);
                        DrawDeco(centre, right, up, rotation, chord, 3, 2, 1, 1, spot);
                        DrawDeco(centre, right, up, rotation, chord, 5, 2, 1, 1, spot);
                        // Small side mushroom
                        DrawDeco(centre, right, up, rotation, chord, 1, 5, 1, 2, stem);
                        DrawDeco(centre, right, up, rotation, chord, 0, 4, 3, 1, capDark);
                        DrawDeco(centre, right, up, rotation, chord, 0, 4, 1, 1, capBright);
                        break;
                    }
                    case TileKind.Beacon:
                    {
                        // Crystal pillar on a dark plinth with a bright pulsing core. Pulse is
                        // driven by the renderer's wall-clock so a row of beacons all pulse
                        // together — feels like a synchronised network of waypoints.
                        var plinth = new Color(40, 32, 50);
                        var crystalDk = new Color(85, 50, 130);
                        var crystalLt = new Color(170, 110, 230);
                        var pulse = (MathF.Sin(Time * 3f + (hash & 0x7F) * 0.05f) * 0.5f + 0.5f);
                        var coreCol = Color.Lerp(crystalLt, new Color(255, 230, 255), pulse);
                        DrawDeco(centre, right, up, rotation, chord, 1, 6, 6, 2, plinth);
                        DrawDeco(centre, right, up, rotation, chord, 3, 1, 2, 5, crystalDk);
                        DrawDeco(centre, right, up, rotation, chord, 3, 2, 2, 3, crystalLt);
                        DrawDeco(centre, right, up, rotation, chord, 3, 3, 2, 1, coreCol);
                        DrawDeco(centre, right, up, rotation, chord, 2, 5, 1, 1, crystalDk);
                        DrawDeco(centre, right, up, rotation, chord, 5, 5, 1, 1, crystalDk);
                        break;
                    }
                }

                // (Ore veins and sparkles are baked into the atlas patterns.)

                // Progressive cracks: one connected, jagged fracture line that lengthens
                // from the impact point as damage accumulates, with a branch splitting off
                // past half damage — a pixel staircase like Terraria's crack overlay, not
                // scattered flecks. Start point, heading and wobble all come off the tile
                // hash so no two blocks fracture along the same lines.
                var dmg = planet.Damage(r, t);
                if (dmg > 0)
                {
                    var cc = Color.White * MathHelper.Min(0.4f + dmg / 500f, 0.75f);
                    (float x, float y) Walk(int seed, float x, float y, int dirX, int dirY, int steps)
                    {
                        for (var s = 0; s < steps; s++)
                        {
                            var bits = seed >> (s & 15);
                            var len = 0.4f + (bits & 3) * 0.15f;
                            if (((s + ((bits >> 2) & 1)) & 1) == 0)
                            {
                                var nx = x + dirX * len;
                                if (nx < 0.1f || nx > Planet.TileSize - 0.4f) { dirX = -dirX; nx = x + dirX * len; }
                                DrawDeco(centre, right, up, rotation, chord,
                                    MathF.Min(x, nx), y, MathF.Abs(nx - x), 0.3f, cc);
                                x = nx;
                            }
                            else
                            {
                                var ny = y + dirY * len;
                                if (ny < 0.1f || ny > Planet.TileSize - 0.4f) { dirY = -dirY; ny = y + dirY * len; }
                                DrawDeco(centre, right, up, rotation, chord,
                                    x, MathF.Min(y, ny), 0.3f, MathF.Abs(ny - y), cc);
                                y = ny;
                            }
                        }
                        return (x, y);
                    }
                    var sx = 1.4f + ((hash >> 2) & 3) * 0.3f;
                    var sy = 1.4f + ((hash >> 4) & 3) * 0.3f;
                    var dx = ((hash >> 6) & 1) != 0 ? 1 : -1;
                    var dy = ((hash >> 7) & 1) != 0 ? 1 : -1;
                    // Main fracture: a nick on the first hit, edge to edge near breaking.
                    var (ex, ey) = Walk(hash, sx, sy, dx, dy, 2 + dmg / 32);
                    // A branch forks from the fracture's midpoint, veering off sideways.
                    if (dmg > 128)
                        Walk(hash * 31, (sx + ex) * 0.5f, (sy + ey) * 0.5f, -dx, dy, 1 + (dmg - 128) / 32);
                }
            }
        }
        _sb.End();
    }

    /// <summary>
    /// Draw a sub-rect within a polar tile, given the tile's centre, local axes (right, up),
    /// rotation, and arc width (chord). Sub-rect is authored in 8×8 reference coords with
    /// (0,0) at the outer-tangent-left corner; X scales by chord/8 so wide surface tiles
    /// spread their decoration proportionally.
    /// </summary>
    /// <summary>Atlas variant for a tile. Pack-art kinds bake their variants as half-size
    /// rolls of one seamless texture, so picking by tile parity makes a 2×2 tile block span
    /// the source exactly once — the pattern flows across tile boundaries and terrain reads
    /// as continuous mass. Procedural kinds have four unrelated patterns; parity would tile
    /// them with an obvious 2-tile period, so they keep the hash pick.</summary>
    /// <summary>The embedded-gem marker: a small faceted crystal shard in the gem's colour —
    /// a dark-rimmed elongated body with diamond tips, a paler facet stripe, and a glint
    /// pixel. Each sits at its own hash-stable random angle and slight off-centre jitter, so
    /// a seam of gems reads as shards seated every which way in the rock rather than a row
    /// of identical uniform markers. Fits inside its 4-px host tile. The soft light it sheds
    /// comes from the lighting pass (Game1's ore scan), not here.</summary>
    private void DrawGemCrystal(Vector2 centre, int hash, TileKind gem)
    {
        var body = Tiles.BaseColor(gem);
        var edge = new Color(body.R / 2, body.G / 2, body.B / 2);
        var facet = Tiles.OreSpeckle(gem);
        var ang = ((hash >> 5) & 63) / 64f * MathF.Tau;               // seat angle, hash-stable
        var axis = new Vector2(-MathF.Sin(ang), MathF.Cos(ang));      // long axis of the shard
        var perp = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        var pos = centre + perp * ((((hash >> 11) & 3) - 1.5f) * 0.3f)
                         + axis * ((((hash >> 13) & 3) - 1.5f) * 0.3f);
        // Dark rim first (slightly larger silhouette), then the bright body over it.
        DrawRect(pos, new Vector2(2.0f, 3.6f), edge, ang);
        DrawRect(pos + axis * 1.9f, new Vector2(1.5f, 1.5f), edge, ang + MathF.PI / 4f);
        DrawRect(pos - axis * 1.9f, new Vector2(1.5f, 1.5f), edge, ang + MathF.PI / 4f);
        DrawRect(pos, new Vector2(1.3f, 3.0f), body, ang);
        DrawRect(pos + axis * 1.7f, new Vector2(1.0f, 1.0f), body, ang + MathF.PI / 4f);
        DrawRect(pos - axis * 1.7f, new Vector2(1.0f, 1.0f), body, ang + MathF.PI / 4f);
        DrawRect(pos + perp * 0.35f, new Vector2(0.5f, 2.0f), facet, ang);   // facet stripe
        DrawRect(pos + axis * 0.7f - perp * 0.3f, new Vector2(0.6f, 0.6f), Color.White, ang);
    }

    /// <summary>The rock a gem tile appears embedded in: the most common of its solid,
    /// non-gem cardinal neighbours. A gem surrounded only by other gems (cluster interior)
    /// or open air falls back to Stone.</summary>
    private static TileKind HostRockFor(TileKind outerK, TileKind innerK,
        TileKind leftK, TileKind rightK)
    {
        Span<TileKind> hosts = stackalloc TileKind[4];
        var n = 0;
        Span<TileKind> candidates = stackalloc[] { innerK, outerK, leftK, rightK };
        foreach (var c in candidates)
            if (c != TileKind.Sky && !Tiles.IsGem(c)) hosts[n++] = c;
        if (n == 0) return TileKind.Stone;
        // Majority of ≤4, first-listed wins ties (inner neighbour first — the bed it sits on).
        var best = hosts[0];
        var bestCount = 0;
        for (var i = 0; i < n; i++)
        {
            var count = 0;
            for (var j = 0; j < n; j++) if (hosts[j] == hosts[i]) count++;
            if (count > bestCount) { bestCount = count; best = hosts[i]; }
        }
        return best;
    }

    private static int VariantFor(TileKind k, int r, int t, int hash) =>
        TileAtlas.HasExternal(k) ? (t & 1) | ((r & 1) << 1) : (hash >> 6) & 3;

    /// <summary>Large, soft brightness blobs over world space (wavelengths ≈ 7 and 3 tiles),
    /// multiplied onto the atlas draw. The pack art's seamless pattern repeats every 2 tiles;
    /// this low-frequency modulation is what keeps a big rock face from reading as wallpaper —
    /// the same trick as Terraria's broad light/dark patches inside otherwise uniform stone.
    /// Darkens only (up to ~12%), so the palette never blows out.</summary>
    private static float BlobShade(Vector2 world) =>
        1f - 0.12f * (ValueNoise(world * (1f / 56f)) * 0.7f + ValueNoise(world * (1f / 23f)) * 0.3f);

    /// <summary>Smooth bilinear value noise in [0,1] from a hashed integer lattice.</summary>
    private static float ValueNoise(Vector2 p)
    {
        var x0 = (int)MathF.Floor(p.X);
        var y0 = (int)MathF.Floor(p.Y);
        var fx = p.X - x0;
        var fy = p.Y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        static float H(int x, int y)
        {
            unchecked
            {
                var h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
            }
        }
        var a = H(x0, y0);
        var b = H(x0 + 1, y0);
        var c = H(x0, y0 + 1);
        var d = H(x0 + 1, y0 + 1);
        var top = a + (b - a) * fx;
        var bot = c + (d - c) * fx;
        return top + (bot - top) * fy;
    }

    /// <summary>Material families for merge dithering. Boundaries between different families
    /// get stippled; same-family boundaries (stone↔granite, ore veins in rock) stay clean.
    /// 0 = never merges (sky, placeables, core).</summary>
    private static int MergeGroup(TileKind k) => k switch
    {
        TileKind.Dirt or TileKind.Grass => 1,
        TileKind.Snow => 2,
        TileKind.Gravel => 3,
        TileKind.Stone or TileKind.MossStone or TileKind.Granite or TileKind.Basalt
            or TileKind.Obsidian or TileKind.PlanetCore => 4,
        _ when Tiles.IsOre(k) => 4,
        _ => 0,
    };

    private static bool Merges(int myGroup, TileKind neighbour)
    {
        var ng = MergeGroup(neighbour);
        return ng > 0 && ng != myGroup;
    }

    /// <summary>True when material a visually overlaps material b at a shared boundary:
    /// the softer material's texture bites into the harder one (dirt into stone, snow over
    /// rock), with enum order as the tie-break so the choice is deterministic — exactly one
    /// side of any mergeable boundary grows teeth.</summary>
    private static bool Bites(TileKind a, TileKind b)
    {
        var ha = Tiles.Hardness(a);
        var hb = Tiles.Hardness(b);
        return ha != hb ? ha < hb : (int)a < (int)b;
    }

    /// <summary>Paint a ragged tooth-row of the softer neighbouring material's colour along
    /// this tile's edge (8×8 reference coords): roughly two thirds of the positions get a
    /// 1-px tooth, the occasional one runs 2 px deep, all hash-stable with a slight colour
    /// jitter per tooth. The neighbour appears to finger into this tile, so the boundary
    /// reads as interlocking materials with no outline or brightness change.</summary>
    private void BiteEdge(Vector2 centre, Vector2 right, Vector2 up, float rotation,
        float chord, int hash, Color c, bool horizontal, int edge)
    {
        var inward = edge == 0 ? 1 : -1;
        for (var i = 0; i < 8; i++)
        {
            var bits = (hash >> (i * 3)) & 7;
            if (bits < 3) continue;          // gap — keeps the row ragged
            var len = bits == 7 ? 2 : 1;     // occasional deeper finger
            var jit = (((hash >> (i * 2 + 5)) & 7) - 3) * 2;
            var tooth = new Color(
                Math.Clamp(c.R + jit, 0, 255),
                Math.Clamp(c.G + jit, 0, 255),
                Math.Clamp(c.B + jit, 0, 255));
            for (var l = 0; l < len; l++)
            {
                var off = edge + inward * l;
                if (horizontal) DrawDeco(centre, right, up, rotation, chord, i, off, 1, 1, tooth);
                else DrawDeco(centre, right, up, rotation, chord, off, i, 1, 1, tooth);
            }
        }
    }

    private void DrawDeco(Vector2 tileCentre, Vector2 right, Vector2 up, float rotation, float chord,
                          float lx, float ly, float lw, float lh, Color color)
    {
        var scaleX = chord / Planet.TileSize;
        var offX = (lx + lw * 0.5f - Planet.TileSize * 0.5f) * scaleX;
        var offY = (ly + lh * 0.5f - Planet.TileSize * 0.5f);
        var world = tileCentre + right * offX - up * offY;
        var size = new Vector2(lw * scaleX + 0.3f, lh + 0.3f);
        _sb.Draw(_pixel, world, null, color, rotation, new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
    }

    public void BeginEntities(Camera cam)
    {
        _sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: cam.View);
    }

    public void EndEntities() => _sb.End();

    public void DrawRect(Vector2 worldPos, Vector2 size, Color color, float rotation = 0f)
    {
        _sb.Draw(_pixel, worldPos, null, color, rotation, new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
    }

    public void DrawCircle(Vector2 worldPos, float radius, Color color)
    {
        _sb.Draw(_circle, worldPos, null, color, 0f,
            new Vector2(_circle.Width / 2f, _circle.Height / 2f),
            radius * 2f / _circle.Width, SpriteEffects.None, 0f);
    }

    public void DrawHudBars(int viewportWidth, int viewportHeight, Player player, int titanAnger, string status, string controls)
    {
        _sb.Begin(samplerState: SamplerState.PointClamp);
        var barW = 220; var barH = 14;

        // Health bar
        _sb.Draw(_pixel, new Rectangle(12, 12, barW, barH), new Color(40, 10, 10));
        var hp = (int)(barW * MathHelper.Clamp(player.Health / player.MaxHealth, 0f, 1f));
        _sb.Draw(_pixel, new Rectangle(12, 12, hp, barH), new Color(220, 60, 60));
        _sb.Draw(_pixel, new Rectangle(12, 12, barW, 1), Color.Black);
        _sb.Draw(_pixel, new Rectangle(12, 12 + barH - 1, barW, 1), Color.Black);
        _font.Draw(_sb, "HP", new Vector2(barW + 18, 13), Color.White, scale: 1);

        // Oxygen bar — cyan, flashing to a warning red as the supply runs low.
        var oxFrac = MathHelper.Clamp(player.Oxygen / player.EffectiveMaxOxygen, 0f, 1f);
        var oxFill = (int)(barW * oxFrac);
        var oxLow = oxFrac < 0.25f;
        var oxColor = oxLow
            ? Color.Lerp(new Color(120, 180, 210), new Color(230, 70, 60), MathF.Sin(Time * 8f) * 0.5f + 0.5f)
            : new Color(90, 190, 220);
        _sb.Draw(_pixel, new Rectangle(12, 32, barW, barH), new Color(12, 30, 38));
        _sb.Draw(_pixel, new Rectangle(12, 32, oxFill, barH), oxColor);
        _sb.Draw(_pixel, new Rectangle(12, 32, barW, 1), Color.Black);
        _sb.Draw(_pixel, new Rectangle(12, 32 + barH - 1, barW, 1), Color.Black);
        _font.Draw(_sb, "AIR", new Vector2(barW + 18, 33), Color.White, scale: 1);

        // Anger bar
        _sb.Draw(_pixel, new Rectangle(12, 52, barW, barH), new Color(40, 30, 10));
        var ang = (int)(barW * MathHelper.Clamp(titanAnger / 100f, 0f, 1f));
        _sb.Draw(_pixel, new Rectangle(12, 52, ang, barH), new Color(240, 140, 40));
        _sb.Draw(_pixel, new Rectangle(12, 52, barW, 1), Color.Black);
        _sb.Draw(_pixel, new Rectangle(12, 52 + barH - 1, barW, 1), Color.Black);
        _font.Draw(_sb, "TITAN ANGER", new Vector2(barW + 18, 53), Color.White, scale: 1);

        // Breath bar — only while diving (or catching breath after): deep blue draining to
        // a red drowning flash. Gill-grafted dwarves never see it drop.
        var statusY = 76;
        if (player.Breath < player.EffectiveMaxBreath - 0.01f || player.HeadInWater)
        {
            var brFrac = MathHelper.Clamp(player.Breath / player.EffectiveMaxBreath, 0f, 1f);
            var brLow = brFrac < 0.25f;
            var brColor = brLow
                ? Color.Lerp(new Color(60, 110, 230), new Color(230, 70, 60), MathF.Sin(Time * 9f) * 0.5f + 0.5f)
                : new Color(70, 130, 235);
            _sb.Draw(_pixel, new Rectangle(12, 72, barW, barH), new Color(10, 18, 40));
            _sb.Draw(_pixel, new Rectangle(12, 72, (int)(barW * brFrac), barH), brColor);
            _sb.Draw(_pixel, new Rectangle(12, 72, barW, 1), Color.Black);
            _sb.Draw(_pixel, new Rectangle(12, 72 + barH - 1, barW, 1), Color.Black);
            _font.Draw(_sb, "BREATH", new Vector2(barW + 18, 73), Color.White, scale: 1);
            statusY = 96;
        }

        _font.Draw(_sb, status, new Vector2(12, statusY), Color.White, scale: 1);
        _font.Draw(_sb, controls, new Vector2(12, viewportHeight - 56), new Color(200, 200, 220), scale: 1);
        _sb.End();
    }

    /// <summary>
    /// Top-right resource panel. One row per known resource with a non-zero count: a colour
    /// swatch (sourced from <see cref="Tiles.ResourceColor"/>), the uppercase label, and the
    /// count. Rows are rendered in <see cref="Tiles.ResourceOrder"/> with anything else (custom
    /// crafted items future-added) appended at the end. The panel auto-sizes to its visible rows
    /// — collapses to nothing when the inventory is empty.
    /// </summary>
    public void DrawInventoryPanel(int viewportWidth, int viewportHeight, Inventory inv)
    {
        var rows = new System.Collections.Generic.List<(string id, int count)>();
        foreach (var id in Tiles.ResourceOrder)
        {
            var c = inv.Count(id);
            if (c > 0) rows.Add((id, c));
        }
        // Append anything in the inventory not in the canonical order — future-proof for custom ids.
        foreach (var (id, count) in inv.Items)
        {
            if (count <= 0) continue;
            var known = false;
            foreach (var k in Tiles.ResourceOrder) if (k == id) { known = true; break; }
            if (!known) rows.Add((id, count));
        }

        if (rows.Count == 0) return;

        const int swatchSize = 8;
        const int rowHeight = 11;
        const int padX = 8;
        const int padY = 6;
        const int textScale = 1;

        // Compute panel width from the longest row.
        var maxTextW = 0;
        foreach (var (id, count) in rows)
        {
            var line = $"{Tiles.ResourceLabel(id)} {count}";
            var w = _font.Measure(line, textScale);
            if (w > maxTextW) maxTextW = w;
        }
        var panelW = padX + swatchSize + 6 + maxTextW + padX;
        var panelH = padY + rows.Count * rowHeight + padY;
        var panelX = viewportWidth - panelW - 12;
        var panelY = 12;

        _sb.Begin(samplerState: SamplerState.PointClamp);
        // Backdrop + 1px frame.
        _sb.Draw(_pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(0, 0, 0, 170));
        _sb.Draw(_pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(255, 255, 255, 60));
        _sb.Draw(_pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(255, 255, 255, 60));
        _sb.Draw(_pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(255, 255, 255, 60));
        _sb.Draw(_pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(255, 255, 255, 60));

        for (var i = 0; i < rows.Count; i++)
        {
            var (id, count) = rows[i];
            var rowY = panelY + padY + i * rowHeight;
            // Swatch with a 1px dark border so dim swatches still pop against the dark backdrop.
            var sx = panelX + padX;
            _sb.Draw(_pixel, new Rectangle(sx - 1, rowY - 1, swatchSize + 2, swatchSize + 2), new Color(0, 0, 0, 200));
            _sb.Draw(_pixel, new Rectangle(sx, rowY, swatchSize, swatchSize), Tiles.ResourceColor(id));
            var line = $"{Tiles.ResourceLabel(id)} {count}";
            _font.Draw(_sb, line, new Vector2(sx + swatchSize + 6, rowY + 1), Color.White, textScale);
        }
        _sb.End();
    }

    public void DrawDebugLabel(string text, Vector2 screenPos, Color color)
    {
        var w = _font.Measure(text, scale: 1) + 4;
        var h = _font.LineHeight + 4;
        _sb.Begin(samplerState: SamplerState.PointClamp);
        _sb.Draw(_pixel, new Rectangle((int)screenPos.X - 2, (int)screenPos.Y - 2, w, h), new Color(0, 0, 0, 180));
        _font.Draw(_sb, text, screenPos, color, scale: 1);
        _sb.End();
    }

    /// <summary>Screen-space text at an arbitrary position and scale — no backing box.
    /// Used by full-screen UI (star map) where DrawDebugLabel's black plate would clutter.</summary>
    public void DrawText(string text, Vector2 screenPos, Color color, int scale = 1)
    {
        _sb.Begin(samplerState: SamplerState.PointClamp);
        _font.Draw(_sb, text, screenPos, color, scale);
        _sb.End();
    }

    /// <summary>Pixel width of <paramref name="text"/> at <paramref name="scale"/> — for centring.</summary>
    public int MeasureText(string text, int scale = 1) => _font.Measure(text, scale);

    public void DrawCenteredText(string text, int viewportWidth, int viewportHeight, Color color, int scale = 3)
    {
        var w = _font.Measure(text, scale);
        _sb.Begin(samplerState: SamplerState.PointClamp);
        _font.Draw(_sb, text, new Vector2((viewportWidth - w) / 2f, viewportHeight / 2f - _font.LineHeight * scale / 2f), color, scale);
        _sb.End();
    }

    /// <summary>Build a small pixel-art Texture2D from a string layout + char→Color palette.
    /// Unrecognised characters become Color.Transparent.</summary>
    public static Texture2D BuildSprite(GraphicsDevice gd, string[] rows, System.Collections.Generic.Dictionary<char, Color> palette)
    {
        var h = rows.Length;
        var w = rows[0].Length;
        var data = new Color[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                data[y * w + x] = palette.TryGetValue(rows[y][x], out var c) ? c : Color.Transparent;
        var tex = new Texture2D(gd, w, h);
        tex.SetData(data);
        return tex;
    }

    private struct Wisp
    {
        public float Angle, RadiusFrac, Len, Thick, Speed;
        public Color Col;
    }

    private Wisp[]? _wisps;

    /// <summary>The moving middle layer of the backdrop — Noita's parallax sheets, adapted
    /// to a round world: translucent haze wisps orbiting slowly through the atmosphere band
    /// at staggered radii, speeds and tints (pale mist / dusky violet). Each is a soft blob
    /// stretched along its orbit tangent so it reads as drifting fog, not a sprite.</summary>
    private void DrawHazeWisps(Planet planet)
    {
        if (_wisps is null)
        {
            var rng = new Random(4242);
            _wisps = new Wisp[44];
            for (var i = 0; i < _wisps.Length; i++)
            {
                var pale = rng.Next(3) > 0;
                var baseCol = pale ? new Color(150, 175, 205) : new Color(96, 82, 138);
                _wisps[i] = new Wisp
                {
                    Angle = (float)(rng.NextDouble() * MathHelper.TwoPi),
                    RadiusFrac = 0.695f + (float)rng.NextDouble() * 0.16f,
                    Len = 60f + (float)rng.NextDouble() * 140f,
                    Thick = 5f + (float)rng.NextDouble() * 9f,
                    Speed = 0.004f + (float)rng.NextDouble() * 0.010f,
                    Col = baseCol * (0.10f + (float)rng.NextDouble() * 0.12f),
                };
            }
        }

        var rad = planet.Radius * Planet.TileSize;
        var origin = new Vector2(_wispTex.Width / 2f, _wispTex.Height / 2f);
        foreach (var w in _wisps)
        {
            var ang = w.Angle + Time * w.Speed;
            var pos = planet.Center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (w.RadiusFrac * rad);
            _sb.Draw(_wispTex, pos, null, w.Col, ang + MathHelper.PiOver2, origin,
                new Vector2(w.Len / _wispTex.Width, w.Thick / _wispTex.Width), SpriteEffects.None, 0f);
        }
    }

    /// <summary>Radial atmosphere shell baked as a premultiplied RGBA disc; the texture
    /// edge maps to the planet's outermost ring. Alpha is zero through the crust (caves
    /// keep the space backdrop), rises to a peak just above the mean surface (~0.68 of
    /// planet radius) and thins to nothing by ~0.93, so the tallest mountains break out of
    /// the haze. Colour grades dusky teal at the horizon into deep violet aloft.</summary>
    private static Texture2D MakeAtmosphere(GraphicsDevice gd, int size)
    {
        var data = new Color[size * size];
        var half = size / 2f;
        var horizon = new Vector3(64, 100, 138);
        var aloft = new Vector3(46, 32, 86);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - half + 0.5f;
                var dy = y - half + 0.5f;
                var d = MathF.Sqrt(dx * dx + dy * dy) / half;
                float band;
                if (d < 0.60f || d > 0.93f) band = 0f;
                else if (d < 0.68f) band = Smooth((d - 0.60f) / 0.08f);
                else band = 1f - Smooth((d - 0.68f) / 0.25f);
                if (band <= 0f)
                {
                    data[y * size + x] = Color.Transparent;
                    continue;
                }
                var t = MathHelper.Clamp((d - 0.64f) / 0.26f, 0f, 1f);
                var c = Vector3.Lerp(horizon, aloft, t);
                var alpha = band * 0.55f;
                data[y * size + x] = new Color(
                    (int)(c.X * alpha), (int)(c.Y * alpha), (int)(c.Z * alpha), (int)(alpha * 255));
            }
        }
        var tex = new Texture2D(gd, size, size);
        tex.SetData(data);
        return tex;
    }

    private static float Smooth(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Soft radial blob (quadratic falloff to transparent) for haze wisps.</summary>
    private static Texture2D MakeSoftBlob(GraphicsDevice gd, int size)
    {
        var data = new Color[size * size];
        var half = size / 2f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x - half + 0.5f) / half;
                var dy = (y - half + 0.5f) / half;
                var t = Math.Clamp(1f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                t *= t;
                var b = (byte)(t * 255);
                data[y * size + x] = new Color(b, b, b, b);
            }
        }
        var tex = new Texture2D(gd, size, size);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Tileable pixel-art night sky: deep space base, a few barely-there nebula
    /// washes (toroidal so the tile wraps seamlessly), and three brightness tiers of
    /// single-pixel stars — the brightest tier gets tiny cross glints. Drawn with PointWrap
    /// at integer zoom, so it stays chunky like the rest of the world.</summary>
    private static Texture2D MakeStarfield(GraphicsDevice gd, int size)
    {
        var rng = new Random(929);
        var data = new Color[size * size];
        var baseCol = new Color(9, 11, 20);
        for (var i = 0; i < data.Length; i++) data[i] = baseCol;

        // Nebula washes: soft toroidal blobs a handful of shades above the base, some
        // nudged violet, some teal. Enough to keep big sky areas from reading flat.
        for (var n = 0; n < 26; n++)
        {
            var cx = rng.Next(size);
            var cy = rng.Next(size);
            var rad = 10 + rng.Next(22);
            var violet = rng.Next(2) == 0;
            for (var dy = -rad; dy <= rad; dy++)
            {
                for (var dx = -rad; dx <= rad; dx++)
                {
                    var dSq = dx * dx + dy * dy;
                    if (dSq > rad * rad) continue;
                    var f = 1f - (float)Math.Sqrt(dSq) / rad;
                    var amp = (int)(f * f * 7f);
                    if (amp <= 0) continue;
                    var idx = ((cy + dy + size) % size) * size + (cx + dx + size) % size;
                    var c = data[idx];
                    data[idx] = violet
                        ? new Color(Math.Min(255, c.R + amp), c.G, Math.Min(255, c.B + amp + 2))
                        : new Color(c.R, Math.Min(255, c.G + amp), Math.Min(255, c.B + amp + 2));
                }
            }
        }

        void Set(int x, int y, Color c) => data[((y + size) % size) * size + (x + size) % size] = c;
        for (var s = 0; s < 150; s++)
        {
            var x = rng.Next(size);
            var y = rng.Next(size);
            var tier = rng.Next(10);
            if (tier < 6)
            {
                Set(x, y, new Color(74, 80, 108));
            }
            else if (tier < 9)
            {
                Set(x, y, new Color(140, 150, 182));
            }
            else
            {
                Set(x, y, new Color(224, 228, 244));
                var glint = new Color(96, 102, 134);
                Set(x + 1, y, glint);
                Set(x - 1, y, glint);
                Set(x, y + 1, glint);
                Set(x, y - 1, glint);
            }
        }

        var tex = new Texture2D(gd, size, size);
        tex.SetData(data);
        return tex;
    }

    private static Texture2D MakeCircle(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        var r = size / 2f - 0.5f;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - r; var dy = y - r;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                data[y * size + x] = d <= r ? Color.White : Color.Transparent;
            }
        }
        tex.SetData(data);
        return tex;
    }
}
