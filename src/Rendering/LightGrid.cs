using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Terraria-style propagated lighting. A view-local cartesian grid at tile resolution:
/// each cell holds an RGB light value and an attenuation factor (air passes most light,
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
/// </summary>
public sealed class LightGrid
{
    // --- Tuning -------------------------------------------------------------------
    /// <summary>Per-cell decay in open air. Sets the reach of every light: a strength-1
    /// seed drops below ~5% after ln(0.05)/ln(AttAir) ≈ 36 cells (~145 world px).</summary>
    private const float AttAir = 0.92f;
    /// <summary>Per-cell decay through solid tiles — light dies within a few tiles of
    /// rock, which is the whole Terraria trick.</summary>
    private const float AttSolid = 0.60f;
    /// <summary>Seed strength bounds. The cap limits how far a huge source reaches; the
    /// floor keeps small glints (bullets, gem speckles) from vanishing entirely.</summary>
    private const float SeedCap = 2.4f;
    private const float SeedFloor = 0.10f;
    /// <summary>Grid side length cap — beyond this the cell size coarsens instead.</summary>
    private const int MaxSide = 200;
    /// <summary>Extra cells beyond the view diagonal so off-screen sources still cast in.</summary>
    private const int MarginCells = 18;
    private static readonly Color SunColor = new(255, 250, 238);

    private float[] _r = Array.Empty<float>();
    private float[] _g = Array.Empty<float>();
    private float[] _b = Array.Empty<float>();
    /// <summary>Straight-neighbour attenuation per cell, and the same raised to √2 for
    /// the diagonal taps (precomputed — pow per tap is what would make sweeps slow).</summary>
    private float[] _att = Array.Empty<float>();
    private float[] _attDiag = Array.Empty<float>();
    private Color[] _pix = Array.Empty<Color>();
    private int _side;
    private Vector2 _origin;
    private float _cell;
    private Texture2D? _tex;

    public Vector2 Origin => _origin;
    public float CellSize => _cell;
    public int Side => _side;
    public Texture2D? Texture => _tex;

    /// <summary>Rebuild the grid for this frame: size/position from the camera, occlusion
    /// and sunlight from the planet. Light seeds come afterwards via Seed().</summary>
    public void Begin(Planet planet, Camera cam)
    {
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
            _att = new float[n]; _attDiag = new float[n];
            _pix = new Color[n];
            _tex?.Dispose();
            _tex = null;   // recreated at Upload
        }

        // Snap the origin to whole cells so texels stay put in world space while the
        // camera pans — otherwise the interpolated light field visibly swims.
        _origin = new Vector2(
            MathF.Floor((cam.Target.X - _side * _cell * 0.5f) / _cell) * _cell,
            MathF.Floor((cam.Target.Y - _side * _cell * 0.5f) / _cell) * _cell);

        for (var y = 0; y < _side; y++)
        {
            for (var x = 0; x < _side; x++)
            {
                var i = y * _side + x;
                var wp = new Vector2(_origin.X + (x + 0.5f) * _cell, _origin.Y + (y + 0.5f) * _cell);
                var solid = planet.IsSolidAt(wp);
                var a = solid ? AttSolid : AttAir;
                _att[i] = a;
                _attDiag[i] = MathF.Pow(a, 1.41421f);

                // Sunlight: an air cell at or above the local terrain surface is open sky.
                // (No per-cell raycast — the surface profile is the authority, and the
                // propagation itself carries daylight down into dips and cave mouths.)
                if (!solid
                    && (wp - planet.Center).Length() / Planet.TileSize >= planet.SurfaceRadiusAt(wp) - 0.5f)
                {
                    _r[i] = SunColor.R / 255f;
                    _g[i] = SunColor.G / 255f;
                    _b[i] = SunColor.B / 255f;
                }
                else
                {
                    _r[i] = 0f; _g[i] = 0f; _b[i] = 0f;
                }
            }
        }
    }

    /// <summary>Seed a light. <paramref name="radius"/> keeps the legacy AddLight meaning
    /// (world px of intended reach) and converts to a seed strength that decays to ~5%
    /// at that distance in open air — so existing call sites read the same.</summary>
    public void Seed(Vector2 worldPos, float radius, Color color)
    {
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
        for (var round = 0; round < 2; round++)
        {
            // Forward sweep: left / up / up-left / up-right are already final-ish.
            for (var y = 0; y < _side; y++)
            {
                var row = y * _side;
                for (var x = 0; x < _side; x++)
                {
                    var i = row + x;
                    var a = _att[i]; var d = _attDiag[i];
                    if (x > 0) Take(i, i - 1, a);
                    if (y > 0)
                    {
                        Take(i, i - _side, a);
                        if (x > 0) Take(i, i - _side - 1, d);
                        if (x < _side - 1) Take(i, i - _side + 1, d);
                    }
                }
            }
            // Backward sweep: right / down / down-right / down-left.
            for (var y = _side - 1; y >= 0; y--)
            {
                var row = y * _side;
                for (var x = _side - 1; x >= 0; x--)
                {
                    var i = row + x;
                    var a = _att[i]; var d = _attDiag[i];
                    if (x < _side - 1) Take(i, i + 1, a);
                    if (y < _side - 1)
                    {
                        Take(i, i + _side, a);
                        if (x < _side - 1) Take(i, i + _side + 1, d);
                        if (x > 0) Take(i, i + _side - 1, d);
                    }
                }
            }
        }
    }

    private void Take(int i, int from, float att)
    {
        var v = _r[from] * att; if (v > _r[i]) _r[i] = v;
        v = _g[from] * att; if (v > _g[i]) _g[i] = v;
        v = _b[from] * att; if (v > _b[i]) _b[i] = v;
    }

    /// <summary>Pack the light field into the texture the lighting pass stretches over
    /// the lightmap (linear-filtered — Terraria "smooth lighting").</summary>
    public void Upload(GraphicsDevice gd)
    {
        if (_tex is null || _tex.Width != _side)
            _tex = new Texture2D(gd, _side, _side, false, SurfaceFormat.Color);
        for (var i = 0; i < _pix.Length; i++)
        {
            _pix[i] = new Color(
                Math.Clamp(_r[i], 0f, 1f),
                Math.Clamp(_g[i], 0f, 1f),
                Math.Clamp(_b[i], 0f, 1f));
        }
        _tex.SetData(_pix);
    }
}
