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
    /// Shader-free bloom: re-draw the lightmap on top of the backbuffer additively, with
    /// linear filtering during the upscale so the small RT smears into a soft glow. Bright
    /// regions of the lightmap (lights, lava, projectiles) contribute the most; ambient is
    /// dim enough that the additive contribution is barely perceptible.
    /// </summary>
    public void Bloom(SpriteBatch target, Point screenSize, Color tint)
    {
        if (_rt is null) return;
        target.Begin(blendState: ColorAdditive, samplerState: SamplerState.LinearClamp);
        target.Draw(_rt, new Rectangle(0, 0, screenSize.X, screenSize.Y), tint);
        target.End();
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
