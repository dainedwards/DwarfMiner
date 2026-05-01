using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Noita-style lightmap pass. The lightmap is rendered to a low-res RenderTarget at
/// world-pixel scale (zoom = 1) so light edges scale up chunky alongside the world.
/// Lights are additive into an ambient base, then the whole RT is composited back over
/// the scene with multiplicative blending — dark lightmap pixels darken the scene, bright
/// ones leave it alone (or tint it warm/cold via colored lights).
/// </summary>
public sealed class Lighting
{
    private readonly GraphicsDevice _gd;
    private readonly SpriteBatch _sb;
    private readonly Texture2D _lightTex;
    private RenderTarget2D? _rt;
    private Point _rtSize;
    private Matrix _viewMatrix;

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

    /// <summary>Reverse-subtract: result = dst − src. Used to subtract a radial darkness
    /// gradient from the lightmap (depth-based dimming).</summary>
    public static readonly BlendState ReverseSubtract = new()
    {
        ColorBlendFunction = BlendFunction.ReverseSubtract,
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaBlendFunction = BlendFunction.ReverseSubtract,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    public Lighting(GraphicsDevice gd)
    {
        _gd = gd;
        _sb = new SpriteBatch(gd);
        _lightTex = MakeRadialGradient(gd, 64);
    }

    public void Begin(Camera cam, Color ambient)
    {
        var w = Math.Max(1, (int)(cam.ViewportSize.X / cam.Zoom));
        var h = Math.Max(1, (int)(cam.ViewportSize.Y / cam.Zoom));
        if (_rt is null || _rtSize.X != w || _rtSize.Y != h)
        {
            _rt?.Dispose();
            _rt = new RenderTarget2D(_gd, w, h, false, SurfaceFormat.Color, DepthFormat.None);
            _rtSize = new Point(w, h);
        }
        _gd.SetRenderTarget(_rt);
        _gd.Clear(ambient);

        // Mirror the camera's translation+rotation but at zoom = 1 so each lightmap pixel
        // corresponds to a world pixel. Upscaling at composite time gives chunky pixel light.
        _viewMatrix = Matrix.CreateTranslation(-cam.Target.X, -cam.Target.Y, 0)
                    * Matrix.CreateRotationZ(-cam.SmoothRotation)
                    * Matrix.CreateTranslation(_rtSize.X * 0.5f, _rtSize.Y * 0.5f, 0);
        _sb.Begin(blendState: ColorAdditive, samplerState: SamplerState.PointClamp, transformMatrix: _viewMatrix);
    }

    public void AddPoint(Vector2 worldPos, float radius, Color color)
    {
        if (radius <= 0f) return;
        _sb.Draw(_lightTex, worldPos, null, color, 0f,
            new Vector2(_lightTex.Width / 2f, _lightTex.Height / 2f),
            (radius * 2f) / _lightTex.Width, SpriteEffects.None, 0f);
    }

    /// <summary>Subtract a radial darkness gradient from the lightmap (e.g. centre of planet).
    /// Brightest src pixels subtract the most; the gradient's quadratic falloff ensures the
    /// darkening tapers off at the radius boundary.</summary>
    public void Darken(Vector2 worldPos, float radius, Color color)
    {
        if (radius <= 0f) return;
        // SpriteBatch can't change blend mid-batch, so we end the additive pass, draw with
        // ReverseSubtract, and reopen the additive pass for any subsequent AddPoint calls.
        _sb.End();
        _sb.Begin(blendState: ReverseSubtract, samplerState: SamplerState.PointClamp, transformMatrix: _viewMatrix);
        _sb.Draw(_lightTex, worldPos, null, color, 0f,
            new Vector2(_lightTex.Width / 2f, _lightTex.Height / 2f),
            (radius * 2f) / _lightTex.Width, SpriteEffects.None, 0f);
        _sb.End();
        _sb.Begin(blendState: ColorAdditive, samplerState: SamplerState.PointClamp, transformMatrix: _viewMatrix);
    }

    public void End()
    {
        _sb.End();
        _gd.SetRenderTarget(null);
    }

    public void Composite(SpriteBatch target, Point screenSize)
    {
        if (_rt is null) return;
        target.Begin(blendState: Multiply, samplerState: SamplerState.PointClamp);
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

        _gd.SetRenderTarget(null);

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

    private static Texture2D MakeRadialGradient(GraphicsDevice gd, int size)
    {
        var tex = new Texture2D(gd, size, size);
        var data = new Color[size * size];
        var r = size / 2f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - r + 0.5f; var dy = y - r + 0.5f;
            var d = MathF.Sqrt(dx * dx + dy * dy) / r;
            var t = Math.Clamp(1f - d, 0f, 1f);
            t = t * t; // quadratic falloff — softer rim, hotter core
            var b = (byte)(t * 255);
            data[y * size + x] = new Color(b, b, b, b);
        }
        tex.SetData(data);
        return tex;
    }
}
