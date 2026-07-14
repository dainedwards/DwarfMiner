using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Terraria-style propagated lighting. A view-local cartesian grid at tile resolution:
/// each cell holds an RGB light value and an attenuation class (air passes most light,
/// solid rock eats it), sunlight seeds every open-to-sky cell, entity/cell lights seed
/// their cells through Renderer.AddLight, and chamfer sweeps flood the light outward.
/// The result is the defining underground look: caves are genuinely dark until lit,
/// torchlight hugs tunnel walls and bends around corners instead of punching through
/// rock, and daylight bleeds a few tiles into cave mouths before dying.
///
/// The grid is square (side covers the view diagonal, so camera rotation never uncovers
/// unlit cells) and world-axis aligned, its origin snapped to whole cells so the light
/// field doesn't swim as the camera pans. Zoomed far out (orbit, descent) the cell size
/// coarsens to keep the cell count bounded — coarse light is invisible at those scales
/// because the sunlit surface dominates the frame.
///
/// Perf: the field recomputes every OTHER frame (30 Hz light, imperceptible) while the
/// lightmap rasterizes from the kept texture every frame so camera motion stays smooth.
/// On the off frames Seed() is a no-op, so the AddLight call sites stay unconditional.
/// </summary>
public sealed class LightGrid
{
    // --- Tuning -------------------------------------------------------------------
    /// <summary>Per-cell decay in open air. Sets the reach of every light: a strength-1
    /// seed drops below ~5% after ln(0.05)/ln(AttAir) ≈ 36 cells (~145 world px).</summary>
    private const float AttAir = 0.92f;
    /// <summary>Per-cell decay through solid tiles — light dies within a few tiles of
    /// rock, which is the whole Terraria trick. Cells are 4 px, so this must be far
    /// gentler than Terraria's per-16px-tile factor: 0.78 shades a rock face to black
    /// across ~12 cells (~48 px), a readable gradient instead of a hard silhouette.</summary>
    private const float AttSolid = 0.78f;
    private static readonly float AttAirDiag = MathF.Pow(AttAir, 1.41421f);
    private static readonly float AttSolidDiag = MathF.Pow(AttSolid, 1.41421f);
    /// <summary>Seed strength bounds. The cap limits how far a huge source reaches; the
    /// floor keeps small glints (bullets, gem speckles) from vanishing entirely.</summary>
    private const float SeedCap = 2.4f;
    private const float SeedFloor = 0.10f;
    /// <summary>Grid side length cap — beyond this the cell size coarsens instead.</summary>
    private const int MaxSide = 200;
    /// <summary>Extra cells beyond the view diagonal so off-screen sources still cast in.</summary>
    private const int MarginCells = 12;
    private static readonly Color SunColor = new(255, 250, 238);

    private float[] _r = Array.Empty<float>();
    private float[] _g = Array.Empty<float>();
    private float[] _b = Array.Empty<float>();
    private bool[] _solid = Array.Empty<bool>();
    private Color[] _pix = Array.Empty<Color>();
    private int _side;
    private Vector2 _origin;
    private float _cell;
    /// <summary>Double-buffered upload textures: SetData into a texture the GPU may still
    /// be sampling from forces a full pipeline sync (the driver waits for the previous
    /// frame's draws) — alternating two textures keeps the upload stall-free.</summary>
    private readonly Texture2D?[] _tex = new Texture2D?[2];
    private int _front;
    /// <summary>False on the skipped frames of the 30 Hz cadence — Seed/Propagate/Upload
    /// no-op and the previous texture keeps serving.</summary>
    private bool _active;
    private int _frame;

    /// <summary>Per-bearing "first open sky" radius (global ring units): a radial descent
    /// from space down to the first solid tile, sampled once per planet and refreshed a few
    /// bearings per frame so player digging updates it within a couple of seconds. Unlike
    /// Planet.SurfaceProfile (the smooth terrain line), this sees carved lake basins,
    /// craters, and city towers — a beach at the bottom of a bowl is still under open sky
    /// and must be sunlit.</summary>
    private float[]? _skyR;
    private int _skyCursor;
    private const int SkyBearings = 2048;
    private Planet? _profilePlanet;
    private float _skyMin, _skyMax;

    public Vector2 Origin => _origin;
    public float CellSize => _cell;
    public Texture2D? Texture => _tex[_front];

    /// <summary>Rebuild the grid for this frame: size/position from the camera, occlusion
    /// and sunlight from the planet. Light seeds come afterwards via Seed().</summary>
    public void Begin(Planet planet, Camera cam)
    {
        _active = ++_frame % 2 == 0 || _tex[_front] is null;
        if (!_active) return;

        if (!ReferenceEquals(_profilePlanet, planet))
        {
            _profilePlanet = planet;
            _skyR = new float[SkyBearings];
            for (var b = 0; b < SkyBearings; b++) ScanSkyBearing(planet, b);
            RecalcSkyBounds();
        }
        else if (_skyR is not null)
        {
            // Round-robin refresh: 24 bearings per recompute ≈ a full sweep every ~3s, so
            // a pit dug into the surface starts catching daylight within moments.
            for (var i = 0; i < 24; i++)
            {
                ScanSkyBearing(planet, _skyCursor);
                _skyCursor = (_skyCursor + 1) % SkyBearings;
            }
            if (_skyCursor < 24) RecalcSkyBounds();
        }

        var halfW = cam.ViewportSize.X / cam.Zoom * 0.5f;
        var halfH = cam.ViewportSize.Y / cam.Zoom * 0.5f;
        var halfDiag = MathF.Sqrt(halfW * halfW + halfH * halfH);

        _cell = Planet.TileSize;
        var needed = (int)(halfDiag * 2f / _cell) + MarginCells * 2;
        if (needed > MaxSide)
        {
            _cell = (halfDiag * 2f) / (MaxSide - MarginCells * 2);
            needed = MaxSide;
        }
        if (_side != needed)
        {
            _side = needed;
            var n = _side * _side;
            _r = new float[n]; _g = new float[n]; _b = new float[n];
            _solid = new bool[n];
            _pix = new Color[n];
            _tex[0]?.Dispose(); _tex[1]?.Dispose();
            _tex[0] = _tex[1] = null;   // recreated at Upload
        }

        // Snap the origin to whole cells so texels stay put in world space while the
        // camera pans — otherwise the interpolated light field visibly swims.
        _origin = new Vector2(
            MathF.Floor((cam.Target.X - _side * _cell * 0.5f) / _cell) * _cell,
            MathF.Floor((cam.Target.Y - _side * _cell * 0.5f) / _cell) * _cell);

        var sunR = SunColor.R / 255f;
        var sunG = SunColor.G / 255f;
        var sunB = SunColor.B / 255f;
        for (var y = 0; y < _side; y++)
        {
            var wy = _origin.Y + (y + 0.5f) * _cell;
            for (var x = 0; x < _side; x++)
            {
                var i = y * _side + x;
                var wp = new Vector2(_origin.X + (x + 0.5f) * _cell, wy);
                var rel = wp - planet.Center;
                var distTiles = rel.Length() / Planet.TileSize;
                // Beyond the tile grid entirely (planet.Radius, above even skyscrapers):
                // open space, full sun, no tile lookup.
                if (distTiles >= planet.Radius)
                {
                    _solid[i] = false;
                    _r[i] = sunR; _g[i] = sunG; _b[i] = sunB;
                    continue;
                }
                var solid = planet.IsSolidAt(wp);
                _solid[i] = solid;
                // Sunlight: an air cell at or above the sky heightmap for its bearing is
                // under open sky — including basin floors and crater bowls. Cells below the
                // map's global minimum skip the bearing lookup (that's every cell when the
                // view is underground).
                if (!solid && distTiles >= _skyMin - 0.5f
                    && distTiles >= SkyRadiusAt(MathF.Atan2(rel.Y, rel.X)) - 0.5f)
                {
                    _r[i] = sunR; _g[i] = sunG; _b[i] = sunB;
                }
                else
                {
                    _r[i] = 0f; _g[i] = 0f; _b[i] = 0f;
                }
            }
        }
    }

    /// <summary>Radial descent for one sky-heightmap bearing: from just above the tile grid
    /// down to the first solid tile. Water/acid cells don't stop it — a lake floor counts
    /// as open sky and the seeded light dims through the liquid via propagation.</summary>
    private void ScanSkyBearing(Planet planet, int b)
    {
        var a = (b + 0.5f) / SkyBearings * MathHelper.TwoPi;
        var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        var lowStop = MathF.Max(1f, Planet.RingMin * 0.5f);
        var d = (float)planet.Radius;
        while (d > lowStop)
        {
            if (planet.IsSolidAt(planet.Center + dir * (d * Planet.TileSize))) break;
            d -= 1f;
        }
        _skyR![b] = d + 1f;
    }

    private void RecalcSkyBounds()
    {
        _skyMin = float.MaxValue; _skyMax = float.MinValue;
        foreach (var v in _skyR!)
        {
            if (v < _skyMin) _skyMin = v;
            if (v > _skyMax) _skyMax = v;
        }
    }

    private float SkyRadiusAt(float angle)
    {
        var t = (angle / MathHelper.TwoPi + 1f) % 1f * SkyBearings;
        var i = (int)t;
        var frac = t - i;
        var a = _skyR![i % SkyBearings];
        var b = _skyR[(i + 1) % SkyBearings];
        // A cliff edge between neighbouring bearings shouldn't smear darkness sideways:
        // take the lower (sunnier) side when the two disagree wildly.
        return MathF.Abs(a - b) > 3f ? MathF.Min(a, b) : a + (b - a) * frac;
    }

    /// <summary>Seed a light. <paramref name="radius"/> keeps the legacy AddLight meaning
    /// (world px of intended reach) and converts to a seed strength that decays to ~5%
    /// at that distance in open air — so existing call sites read the same.</summary>
    public void Seed(Vector2 worldPos, float radius, Color color)
    {
        if (!_active) return;
        var x = (int)((worldPos.X - _origin.X) / _cell);
        var y = (int)((worldPos.Y - _origin.Y) / _cell);
        if (x < 0 || y < 0 || x >= _side || y >= _side) return;
        var s = Math.Clamp(0.05f * MathF.Pow(AttAir, -radius / _cell), SeedFloor, SeedCap);
        var i = y * _side + x;
        // Max, not add: overlapping seeds (stacked auras, lava neighbours) brighten to the
        // strongest source instead of blowing out.
        _r[i] = MathF.Max(_r[i], color.R / 255f * s);
        _g[i] = MathF.Max(_g[i], color.G / 255f * s);
        _b[i] = MathF.Max(_b[i], color.B / 255f * s);
    }

    /// <summary>Flood the seeded light through the grid: two rounds of forward/backward
    /// chamfer sweeps (straight + diagonal taps), each cell taking the max of its own
    /// value and every already-visited neighbour times this cell's attenuation.</summary>
    public void Propagate()
    {
        if (!_active) return;
        // Manually inlined taps into locals — this loop runs hot every recompute and the
        // Debug JIT inlines nothing, so a Take() helper costs a real method call per tap.
        var r = _r; var g = _g; var b = _b; var solid = _solid;
        var side = _side;
        for (var round = 0; round < 2; round++)
        {
            // Forward sweep: left / up / up-left / up-right are already final-ish.
            for (var y = 0; y < side; y++)
            {
                var row = y * side;
                for (var x = 0; x < side; x++)
                {
                    var i = row + x;
                    float a, d;
                    if (solid[i]) { a = AttSolid; d = AttSolidDiag; }
                    else { a = AttAir; d = AttAirDiag; }
                    float mr = r[i], mg = g[i], mb = b[i];
                    float v; int j;
                    if (x > 0)
                    {
                        j = i - 1;
                        v = r[j] * a; if (v > mr) mr = v;
                        v = g[j] * a; if (v > mg) mg = v;
                        v = b[j] * a; if (v > mb) mb = v;
                    }
                    if (y > 0)
                    {
                        j = i - side;
                        v = r[j] * a; if (v > mr) mr = v;
                        v = g[j] * a; if (v > mg) mg = v;
                        v = b[j] * a; if (v > mb) mb = v;
                        if (x > 0)
                        {
                            j = i - side - 1;
                            v = r[j] * d; if (v > mr) mr = v;
                            v = g[j] * d; if (v > mg) mg = v;
                            v = b[j] * d; if (v > mb) mb = v;
                        }
                        if (x < side - 1)
                        {
                            j = i - side + 1;
                            v = r[j] * d; if (v > mr) mr = v;
                            v = g[j] * d; if (v > mg) mg = v;
                            v = b[j] * d; if (v > mb) mb = v;
                        }
                    }
                    r[i] = mr; g[i] = mg; b[i] = mb;
                }
            }
            // Backward sweep: right / down / down-right / down-left.
            for (var y = side - 1; y >= 0; y--)
            {
                var row = y * side;
                for (var x = side - 1; x >= 0; x--)
                {
                    var i = row + x;
                    float a, d;
                    if (solid[i]) { a = AttSolid; d = AttSolidDiag; }
                    else { a = AttAir; d = AttAirDiag; }
                    float mr = r[i], mg = g[i], mb = b[i];
                    float v; int j;
                    if (x < side - 1)
                    {
                        j = i + 1;
                        v = r[j] * a; if (v > mr) mr = v;
                        v = g[j] * a; if (v > mg) mg = v;
                        v = b[j] * a; if (v > mb) mb = v;
                    }
                    if (y < side - 1)
                    {
                        j = i + side;
                        v = r[j] * a; if (v > mr) mr = v;
                        v = g[j] * a; if (v > mg) mg = v;
                        v = b[j] * a; if (v > mb) mb = v;
                        if (x < side - 1)
                        {
                            j = i + side + 1;
                            v = r[j] * d; if (v > mr) mr = v;
                            v = g[j] * d; if (v > mg) mg = v;
                            v = b[j] * d; if (v > mb) mb = v;
                        }
                        if (x > 0)
                        {
                            j = i + side - 1;
                            v = r[j] * d; if (v > mr) mr = v;
                            v = g[j] * d; if (v > mg) mg = v;
                            v = b[j] * d; if (v > mb) mb = v;
                        }
                    }
                    r[i] = mr; g[i] = mg; b[i] = mb;
                }
            }
        }
    }

    /// <summary>Pack the light field into the texture the lighting pass stretches over
    /// the lightmap (linear-filtered — Terraria "smooth lighting").</summary>
    public void Upload(GraphicsDevice gd)
    {
        if (!_active && _tex[_front] is not null) return;
        var back = 1 - _front;
        if (_tex[back] is null || _tex[back]!.Width != _side)
            _tex[back] = new Texture2D(gd, _side, _side, false, SurfaceFormat.Color);
        for (var i = 0; i < _pix.Length; i++)
        {
            _pix[i] = new Color(
                Math.Clamp(_r[i], 0f, 1f),
                Math.Clamp(_g[i], 0f, 1f),
                Math.Clamp(_b[i], 0f, 1f));
        }
        _tex[back]!.SetData(_pix);
        _front = back;
    }
}
