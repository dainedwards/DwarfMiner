using System;
using System.Collections.Generic;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Lightmap pass. The propagated LightGrid is stretched into a RenderTarget at
/// world-pixel scale (zoom = 1), then the whole RT is composited back over the scene
/// with multiplicative blending — dark lightmap pixels darken the scene, bright ones
/// leave it alone (or tint it warm/cold via colored lights). Bloom/vignette/grade
/// post-passes run off the same RT.
/// </summary>
public sealed class Lighting
{
    private readonly GraphicsDevice _gd;
    private readonly SpriteBatch _sb;
    private RenderTarget2D? _rt;
    private Point _rtSize;
    private Matrix _viewMatrix;

    /// <summary>The scene render target the composited frame lives in. The lightmap/bloom
    /// passes bind their own RTs mid-frame and must return to this (not the backbuffer)
    /// when done — Game1 renders everything into a fixed virtual-resolution target and
    /// scales it to the window at present time.</summary>
    public RenderTarget2D? SceneTarget;

    // Bloom ping-pong RTs (half-res of the lightmap) for multi-tap separable Gaussian blur.
    private RenderTarget2D? _bloomA;
    private RenderTarget2D? _bloomB;
    private Point _bloomSize;
    // Cached vignette (radial darkening) — generated once at a fixed res and stretched.
    private Texture2D? _vignette;

    /// <summary>RGB-multiplicative blend for compositing the lightmap over the backbuffer.</summary>
    public static readonly BlendState Multiply = new()
    {
        ColorSourceBlend = Blend.DestinationColor,
        ColorDestinationBlend = Blend.Zero,
        AlphaSourceBlend = Blend.DestinationAlpha,
        AlphaDestinationBlend = Blend.Zero,
    };

    /// <summary>Plain RGB-additive: result = src + dst. Independent of source alpha so the
    /// gradient texture's authored falloff isn't squared by SrcAlpha-based blending.</summary>
    public static readonly BlendState ColorAdditive = new()
    {
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    public Lighting(GraphicsDevice gd)
    {
        _gd = gd;
        _sb = new SpriteBatch(gd);
    }

    /// <summary>Render the propagated light grid into the lightmap RT: the grid texture
    /// (one texel per grid cell) stretched with linear filtering — Terraria's "smooth
    /// lighting" — under the camera's translation+rotation at zoom = 1, so each lightmap
    /// pixel corresponds to a world pixel. Everything outside the grid clears to black:
    /// unlit is genuinely dark now, there is no ambient base.</summary>
    public void RenderGrid(Camera cam, LightGrid grid)
    {
        if (grid.Texture is null) return;
        var w = Math.Max(1, (int)(cam.ViewportSize.X / cam.Zoom));
        var h = Math.Max(1, (int)(cam.ViewportSize.Y / cam.Zoom));
        if (_rt is null || _rtSize.X != w || _rtSize.Y != h)
        {
            _rt?.Dispose();
            // PreserveContents: the hero-light pass rebinds this RT after the grid raster —
            // the default DiscardContents would wipe the grid on that rebind.
            _rt = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None,
                0, RenderTargetUsage.PreserveContents);
            _rtSize = new Point(w, h);
        }
        _gd.SetRenderTarget(_rt);
        _gd.Clear(Color.Black);

        _viewMatrix = Matrix.CreateTranslation(-cam.Target.X, -cam.Target.Y, 0)
                    * Matrix.CreateRotationZ(-cam.SmoothRotation)
                    * Matrix.CreateTranslation(_rtSize.X * 0.5f, _rtSize.Y * 0.5f, 0);
        _sb.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp,
            transformMatrix: _viewMatrix);
        _sb.Draw(grid.Texture, grid.Origin, null, Color.White, 0f, Vector2.Zero,
            grid.CellSize, SpriteEffects.None, 0f);
        _sb.End();
        _gd.SetRenderTarget(SceneTarget);
    }

    // ── Noita-style ray-cast hero lights ────────────────────────────────────────
    // A handful of lights per frame (the carried lamp, explosion cores, muzzle flashes)
    // get real 2D shadow-casting: rays march out from the source against the tile grid
    // and the lit region is drawn as an additive triangle fan into the lightmap ON TOP of
    // the soft propagated field. Occluders cut crisp shadows; the grid still provides the
    // gentle bounce-light everywhere else.

    private const int HeroRays = 96;
    private const int MaxHeroLights = 10;
    private BasicEffect? _heroFx;
    private VertexPositionColor[] _heroVerts = new VertexPositionColor[HeroRays * 3];

    /// <summary>Draw the frame's hero lights into the lightmap RT (which must still be the
    /// pass's target — call between RenderGrid's raster and the composite).</summary>
    public void RenderHeroLights(List<(Vector2 pos, float radius, Color color)> lights, Planet planet)
    {
        if (lights.Count == 0 || _rt is null) return;
        _gd.SetRenderTarget(_rt);

        _heroFx ??= new BasicEffect(_gd) { VertexColorEnabled = true, TextureEnabled = false };
        _heroFx.World = Matrix.Identity;
        _heroFx.View = _viewMatrix;
        _heroFx.Projection = Matrix.CreateOrthographicOffCenter(0, _rtSize.X, _rtSize.Y, 0, 0, 1);

        _gd.BlendState = BlendState.Additive;
        _gd.RasterizerState = RasterizerState.CullNone;
        _gd.DepthStencilState = DepthStencilState.None;

        var n = Math.Min(lights.Count, MaxHeroLights);
        for (var li = 0; li < n; li++)
        {
            var (pos, radius, color) = lights[li];
            if (radius <= 2f) continue;

            // March each ray against the tile grid; the fan edge stops just inside the
            // first solid hit (small bleed so the struck wall face itself catches light).
            Span<float> hitDist = stackalloc float[HeroRays];
            for (var r = 0; r < HeroRays; r++)
            {
                var ang = r * MathHelper.TwoPi / HeroRays;
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                var d = 3f;
                while (d < radius)
                {
                    if (planet.IsSolidAt(pos + dir * d)) { d += 2.5f; break; }
                    d += 3f;
                }
                hitDist[r] = MathF.Min(d, radius);
            }

            // Fan → triangle list: centre at full colour, rim transparent (linear falloff;
            // the bloom pass rounds it off).
            var centre = new VertexPositionColor(new Vector3(pos, 0f), color);
            var vi = 0;
            for (var r = 0; r < HeroRays; r++)
            {
                var a0 = r * MathHelper.TwoPi / HeroRays;
                var a1 = (r + 1) % HeroRays * MathHelper.TwoPi / HeroRays;
                var p0 = pos + new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * hitDist[r];
                var p1 = pos + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * hitDist[(r + 1) % HeroRays];
                _heroVerts[vi++] = centre;
                _heroVerts[vi++] = new VertexPositionColor(new Vector3(p0, 0f), Color.Transparent);
                _heroVerts[vi++] = new VertexPositionColor(new Vector3(p1, 0f), Color.Transparent);
            }
            foreach (var pass in _heroFx.CurrentTechnique.Passes)
            {
                pass.Apply();
                _gd.DrawUserPrimitives(PrimitiveType.TriangleList, _heroVerts, 0, HeroRays);
            }
        }
        _gd.SetRenderTarget(SceneTarget);
    }

    public void Composite(SpriteBatch target, Point screenSize)
    {
        if (_rt is null) return;
        // Linear upscale — the propagated grid is already smooth; point sampling would
        // re-pixelate the gradients at world-pixel steps.
        target.Begin(blendState: Multiply, samplerState: SamplerState.LinearClamp);
        target.Draw(_rt, new Rectangle(0, 0, screenSize.X, screenSize.Y), Color.White);
        target.End();
    }

    /// <summary>
    /// Shader-free bloom with a real separable Gaussian blur. The lightmap is downsampled to
    /// a half-res ping-pong RT, blurred horizontally then vertically with a 9-tap kernel
    /// (5 unique weights mirrored), then composited additively over the backbuffer. The
    /// downsample + linear upscale at composite time gives a much softer falloff than the
    /// previous single-pass linear-filter trick — bright spots actually bloom into the scene
    /// instead of just feathering at their edges.
    /// </summary>
    public void Bloom(SpriteBatch target, Point screenSize, Color tint)
    {
        if (_rt is null) return;
        EnsureBloomRts();

        // Downsample: lightmap → bloomA at half res. Linear filter pre-smooths for free.
        _gd.SetRenderTarget(_bloomA);
        _gd.Clear(Color.Transparent);
        _sb.Begin(blendState: BlendState.Opaque, samplerState: SamplerState.LinearClamp);
        _sb.Draw(_rt!, new Rectangle(0, 0, _bloomSize.X, _bloomSize.Y), Color.White);
        _sb.End();

        // Separable Gaussian: horizontal then vertical. Two passes ping-pong between A and B.
        SeparableBlur(_bloomA!, _bloomB!, horizontal: true);
        SeparableBlur(_bloomB!, _bloomA!, horizontal: false);

        _gd.SetRenderTarget(SceneTarget);

        // Composite blurred bloom additively at full screen, tinted slightly cool so the
        // overall mood stays a touch blue rather than washing the scene yellow.
        target.Begin(blendState: ColorAdditive, samplerState: SamplerState.LinearClamp);
        target.Draw(_bloomA!, new Rectangle(0, 0, screenSize.X, screenSize.Y), tint);
        target.End();
    }

    /// <summary>Cinematic vignette — multiplicative full-screen darkening that's strongest at
    /// the corners and tapers to identity at the centre. Sells the cave depth more than a
    /// flat ambient does, especially at the surface where the sky is full-bright.</summary>
    public void Vignette(SpriteBatch target, Point screenSize)
    {
        EnsureVignette();
        target.Begin(blendState: Multiply, samplerState: SamplerState.LinearClamp);
        target.Draw(_vignette!, new Rectangle(0, 0, screenSize.X, screenSize.Y), Color.White);
        target.End();
    }

    /// <summary>Cheap colour grade: a single multiplicative tint over the whole composited
    /// frame. Used to push shadows slightly cool/desaturated, which is most of what a real
    /// LUT-based grade would buy at this fidelity. Pass white (255,255,255) to skip.</summary>
    public void Grade(SpriteBatch target, Texture2D pixel, Point screenSize, Color tint)
    {
        target.Begin(blendState: Multiply, samplerState: SamplerState.PointClamp);
        target.Draw(pixel, new Rectangle(0, 0, screenSize.X, screenSize.Y), tint);
        target.End();
    }

    private void EnsureBloomRts()
    {
        if (_rt is null) return;
        var w = Math.Max(1, _rtSize.X / 2);
        var h = Math.Max(1, _rtSize.Y / 2);
        if (_bloomA is not null && _bloomSize.X == w && _bloomSize.Y == h) return;
        _bloomA?.Dispose();
        _bloomB?.Dispose();
        _bloomA = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        _bloomB = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
        _bloomSize = new Point(w, h);
    }

    private void SeparableBlur(RenderTarget2D src, RenderTarget2D dst, bool horizontal)
    {
        // 9-tap Gaussian σ≈2: weights mirrored around centre. Sums to ~1.0.
        // Offsets are in pixels; on the half-res bloom RT each step covers 2 source-pixels.
        ReadOnlySpan<float> weights = stackalloc float[5] { 0.227f, 0.194f, 0.121f, 0.054f, 0.016f };
        ReadOnlySpan<int>   offsets = stackalloc int[5]   { 0, 1, 2, 3, 4 };

        _gd.SetRenderTarget(dst);
        _gd.Clear(Color.Transparent);
        _sb.Begin(blendState: ColorAdditive, samplerState: SamplerState.LinearClamp);
        for (var i = 0; i < 5; i++)
        {
            var w = weights[i];
            var o = offsets[i];
            var dx = horizontal ? o : 0;
            var dy = horizontal ? 0 : o;
            // For i==0 the centre tap is drawn once. For i>0 we draw once on each side
            // of the centre to mirror the kernel.
            var col = new Color(w, w, w, w);
            _sb.Draw(src, new Vector2(dx, dy), col);
            if (i > 0)
                _sb.Draw(src, new Vector2(-dx, -dy), col);
        }
        _sb.End();
    }

    private void EnsureVignette()
    {
        if (_vignette is not null) return;
        const int s = 256;
        var data = new Color[s * s];
        var half = s / 2f;
        for (var y = 0; y < s; y++)
        for (var x = 0; x < s; x++)
        {
            var dx = (x - half) / half;
            var dy = (y - half) / half;
            var d = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));
            // Multiplicative: 1.0 at centre → ~0.55 at corners. Quadratic for soft midfield.
            var v = 1f - d * d * 0.45f;
            var b = (byte)(v * 255);
            data[y * s + x] = new Color(b, b, b, (byte)255);
        }
        _vignette = new Texture2D(_gd, s, s);
        _vignette.SetData(data);
    }
}
