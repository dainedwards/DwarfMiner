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
    }

    /// <summary>Kinds whose tile art stays hand-authored + animated in DrawWorld (supports,
    /// ladders, rails, glowing placeables) rather than sampled from the texture atlas.</summary>
    private static bool UsesAuthoredArt(TileKind k) => k is
        TileKind.Core or TileKind.Support or TileKind.ReinforcedSupport or
        TileKind.Ladder or TileKind.Rail or TileKind.Glowshroom or TileKind.Beacon;

    public Texture2D Pixel => _pixel;
    public SpriteBatch Batch => _sb;

    /// <summary>Wall-clock seconds, fed in once per frame by the game. Used by the world
    /// renderer to drive sub-tile animation (waving grass, hanging vines).</summary>
    public float Time { get; set; }

    public void BeginLighting(Camera cam, Color ambient) => _lighting.Begin(cam, ambient);
    public void AddLight(Vector2 worldPos, float radius, Color color) => _lighting.AddPoint(worldPos, radius, color);
    public void EndLighting() => _lighting.End();
    public void CompositeLighting(Point screenSize) => _lighting.Composite(_sb, screenSize);
    public void BloomLighting(Point screenSize, Color tint) => _lighting.Bloom(_sb, screenSize, tint);
    public void Darken(Vector2 worldPos, float radius, Color color) => _lighting.Darken(worldPos, radius, color);
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
        var maxRing = Math.Min(Planet.RingCount - 1, (int)(maxDistTight / Planet.TileSize) - Planet.RingMin + 1);

        _gd.Clear(new Color(8, 10, 18));
        _sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: view);

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

        for (var r = minRing; r <= maxRing; r++)
        {
            var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
            var tpr = Planet.TilesAt(r);
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

            for (var ti = t0; ti < t1; ti++)
            {
                var t = ((ti % tpr) + tpr) % tpr;
                var k = planet.Get(r, t);

                var centre = planet.TileToWorld(r, t);
                // Cull tiles whose centre is too far from the viewport rect (cheap secondary check).
                if (centre.X < minX - chord || centre.X > maxX + chord ||
                    centre.Y < minY - chord || centre.Y > maxY + chord)
                    continue;

                var up = planet.UpAt(centre);
                var rotation = MathF.Atan2(up.X, -up.Y);
                var right = new Vector2(-up.Y, up.X);
                var size = new Vector2(chord + 1f, Planet.TileSize + 1f); // +1 px overlap kills sub-pixel seams between neighbours
                var hash = (r * 73856093) ^ (t * 19349663);

                // Sky tile: maybe draw the background wall (cave back-wall, Terraria style).
                // Multi-layer composite: a deep base, a mid-depth blocky strata pattern from a
                // coarser hash so adjacent tiles share the same big-block, and a foreground
                // speckle layer for surface noise. Tiles whose neighbour is solid get a faint
                // rim "lip" along the shared edge — sells the floor/wall meeting line.
                if (k == TileKind.Sky)
                {
                    var wallK = planet.GetWall(r, t);
                    if (wallK == TileKind.Sky) continue; // outside planet — no wall
                    var wb = Tiles.BaseColor(wallK);

                    // Back wall = the same atlas texture the material uses in the foreground,
                    // multiplied down to ~35% brightness. Keeps all the baked grain/strata
                    // detail while clearly reading as background rock.
                    _sb.Draw(_tileAtlas, centre, TileAtlas.Source(wallK, VariantFor(wallK, r, t, hash)),
                        new Color(88, 88, 100), rotation,
                        new Vector2(TileAtlas.Res * 0.5f, TileAtlas.Res * 0.5f),
                        new Vector2(size.X / TileAtlas.Res, size.Y / TileAtlas.Res),
                        SpriteEffects.None, 0f);

                    // Edge "lip" — when this back-wall tile is adjacent to a solid tile, draw
                    // a faint dark rim along the shared edge so the floor/wall transition reads
                    // as a corner rather than a flat colour change.
                    var lipCol = new Color(
                        Math.Clamp((int)(wb.R * 0.15f), 0, 255),
                        Math.Clamp((int)(wb.G * 0.15f), 0, 255),
                        Math.Clamp((int)(wb.B * 0.15f), 0, 255));
                    var ocnt = planet.OuterNeighbourCount(r, t);
                    var outerSolid = false;
                    for (var oi = 0; oi < ocnt; oi++)
                    {
                        var (o2r, o2t) = planet.OuterNeighbour(r, t, oi);
                        if (Tiles.IsSolid(planet.Get(o2r, o2t))) { outerSolid = true; break; }
                    }
                    var (i2r, i2t) = planet.InnerNeighbour(r, t);
                    var innerSolid = Tiles.IsSolid(planet.Get(i2r, i2t));
                    var leftSolid  = Tiles.IsSolid(planet.Get(r, t - 1));
                    var rightSolid = Tiles.IsSolid(planet.Get(r, t + 1));
                    if (outerSolid) DrawDeco(centre, right, up, rotation, chord, 0, 0, 8, 1, lipCol);
                    if (innerSolid) DrawDeco(centre, right, up, rotation, chord, 0, 7, 8, 1, lipCol);
                    if (leftSolid)  DrawDeco(centre, right, up, rotation, chord, 0, 0, 1, 8, lipCol);
                    if (rightSolid) DrawDeco(centre, right, up, rotation, chord, 7, 0, 1, 8, lipCol);
                    continue;
                }

                // Condemned tiles oscillate before crumbling — shift the whole tile (base
                // quad, shades and rims all draw from `centre`) by a small random offset.
                if (TrembleTiles is { Count: > 0 } && TrembleTiles.Contains(planet.Index(r, t)))
                    centre += right * ((Random.Shared.NextSingle() - 0.5f) * 1.4f)
                            + up * ((Random.Shared.NextSingle() - 0.5f) * 1.4f);

                var jitter = ((hash >> 4) & 31) - 16;
                var col = Tiles.BaseColor(k);
                col = new Color(
                    Math.Clamp(col.R + jitter / 4, 0, 255),
                    Math.Clamp(col.G + jitter / 4, 0, 255),
                    Math.Clamp(col.B + jitter / 4, 0, 255));

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
                    // neighbours; procedural kinds keep the hash-stable random pick.
                    _sb.Draw(_tileAtlas, centre, TileAtlas.Source(k, VariantFor(k, r, t, hash)),
                        Color.White, rotation,
                        new Vector2(TileAtlas.Res * 0.5f, TileAtlas.Res * 0.5f),
                        new Vector2(size.X / TileAtlas.Res, size.Y / TileAtlas.Res),
                        SpriteEffects.None, 0f);
                }

                // Edge rims — outer/inner radial + tangential to either side.
                var rim = new Color(
                    Math.Clamp(col.R + 44, 0, 255),
                    Math.Clamp(col.G + 44, 0, 255),
                    Math.Clamp(col.B + 44, 0, 255));
                var sh = new Color(
                    Math.Clamp(col.R - 36, 0, 255),
                    Math.Clamp(col.G - 36, 0, 255),
                    Math.Clamp(col.B - 36, 0, 255));
                var outerSky = false;
                var outerCount = planet.OuterNeighbourCount(r, t);
                for (var oi = 0; oi < outerCount; oi++)
                {
                    var (or_, ot_) = planet.OuterNeighbour(r, t, oi);
                    if (planet.Get(or_, ot_) == TileKind.Sky) { outerSky = true; break; }
                }
                var (ir_, it_) = planet.InnerNeighbour(r, t);
                var innerSky = planet.Get(ir_, it_) == TileKind.Sky;
                var leftSky  = planet.Get(r, t - 1) == TileKind.Sky;
                var rightSky = planet.Get(r, t + 1) == TileKind.Sky;
                if (outerSky) DrawDeco(centre, right, up, rotation, chord, 0, 0, 8, 1, rim);
                if (innerSky) DrawDeco(centre, right, up, rotation, chord, 0, 7, 8, 1, sh);
                if (leftSky)  DrawDeco(centre, right, up, rotation, chord, 0, 0, 1, 8, rim);
                if (rightSky) DrawDeco(centre, right, up, rotation, chord, 7, 0, 1, 8, sh);

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

                // Damage overlay + cracks once past the halfway mark.
                var dmg = planet.Damage(r, t);
                if (dmg > 0)
                {
                    var a = (byte)(dmg / 1.4f);
                    _sb.Draw(_pixel, centre, null, new Color((byte)0, (byte)0, (byte)0, a), rotation,
                        new Vector2(0.5f, 0.5f), size, SpriteEffects.None, 0f);
                    if (dmg > 140)
                    {
                        var cc = new Color((byte)0, (byte)0, (byte)0, (byte)200);
                        DrawDeco(centre, right, up, rotation, chord, 2, 3, 1, 2, cc);
                        DrawDeco(centre, right, up, rotation, chord, 4, 1, 2, 1, cc);
                        DrawDeco(centre, right, up, rotation, chord, 5, 4, 1, 2, cc);
                    }
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
    private static int VariantFor(TileKind k, int r, int t, int hash) =>
        TileAtlas.HasExternal(k) ? (t & 1) | ((r & 1) << 1) : (hash >> 6) & 3;

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
        // Health bar
        var barW = 220; var barH = 14;
        _sb.Draw(_pixel, new Rectangle(12, 12, barW, barH), new Color(40, 10, 10));
        var hp = (int)(barW * MathHelper.Clamp(player.Health / 100f, 0f, 1f));
        _sb.Draw(_pixel, new Rectangle(12, 12, hp, barH), new Color(220, 60, 60));
        _sb.Draw(_pixel, new Rectangle(12, 12, barW, 1), Color.Black);
        _sb.Draw(_pixel, new Rectangle(12, 12 + barH - 1, barW, 1), Color.Black);
        _font.Draw(_sb, "HP", new Vector2(barW + 18, 13), Color.White, scale: 1);

        // Anger bar
        _sb.Draw(_pixel, new Rectangle(12, 32, barW, barH), new Color(40, 30, 10));
        var ang = (int)(barW * MathHelper.Clamp(titanAnger / 100f, 0f, 1f));
        _sb.Draw(_pixel, new Rectangle(12, 32, ang, barH), new Color(240, 140, 40));
        _sb.Draw(_pixel, new Rectangle(12, 32, barW, 1), Color.Black);
        _sb.Draw(_pixel, new Rectangle(12, 32 + barH - 1, barW, 1), Color.Black);
        _font.Draw(_sb, "TITAN ANGER", new Vector2(barW + 18, 33), Color.White, scale: 1);

        _font.Draw(_sb, status, new Vector2(12, 56), Color.White, scale: 1);
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
