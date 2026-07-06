using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace DwarfMiner.Rendering;

/// <summary>
/// Animated player sprite loaded from the CC0 pack under assets/player (see
/// assets/CREDITS.md): 3 idle, 6 run, plus jump and fall frames, all 16×16 and facing
/// right. Content bounds are measured at load so the feet line up with the collision
/// circle and the draw scale keeps the dwarf his usual on-screen height regardless of the
/// pack's transparent padding. When the folder is missing Game1 falls back to the built-in
/// string-art dwarf.
/// </summary>
public sealed class PlayerSprite
{
    private readonly Texture2D[] _idle;
    private readonly Texture2D[] _run;
    private readonly Texture2D _jump;
    private readonly Texture2D _fall;
    private readonly float _feetBelowCentre; // sprite px from frame centre down to ground contact

    /// <summary>World units per sprite pixel, chosen so the visible body is ~7.5 px tall —
    /// the same on-screen height as the original 12-row string-art dwarf at 0.6 scale.</summary>
    public float Scale { get; }

    /// <summary>Draw-position shift along planet-up so the sprite's feet sit exactly at the
    /// bottom of the collision circle (same correction the old fixed-art path hardcoded).</summary>
    public float FeetOffset(float radius) => _feetBelowCentre * Scale - radius;

    private PlayerSprite(Texture2D[] idle, Texture2D[] run, Texture2D jump, Texture2D fall)
    {
        _idle = idle;
        _run = run;
        _jump = jump;
        _fall = fall;

        // Grounded frames define the metrics: the lowest opaque row across the run cycle is
        // the ground-contact line, the opaque height drives the scale.
        int top = int.MaxValue, bottom = int.MinValue;
        foreach (var t in run)
        {
            var (t0, b0) = OpaqueRowBounds(t);
            top = Math.Min(top, t0);
            bottom = Math.Max(bottom, b0);
        }
        Scale = 7.5f / Math.Max(1, bottom - top + 1);
        _feetBelowCentre = bottom + 1 - run[0].Height / 2f;
    }

    /// <summary>Load all frames, or null if the asset folder (or any frame) is absent.</summary>
    public static PlayerSprite? TryLoad(GraphicsDevice gd)
    {
        string? dir = null;
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var d = Path.Combine(root, "assets", "player");
            if (Directory.Exists(d)) { dir = d; break; }
        }
        if (dir is null) return null;

        try
        {
            var idle = new Texture2D[3];
            for (var i = 0; i < 3; i++) idle[i] = Load(gd, Path.Combine(dir, $"idle{i + 1}.png"));
            var run = new Texture2D[6];
            for (var i = 0; i < 6; i++) run[i] = Load(gd, Path.Combine(dir, $"run{i + 1}.png"));
            return new PlayerSprite(idle, run,
                Load(gd, Path.Combine(dir, "jump.png")),
                Load(gd, Path.Combine(dir, "fall.png")));
        }
        catch (Exception)
        {
            return null; // incomplete pack — string-art fallback covers it
        }
    }

    /// <summary>Pick the frame for this tick. Airborne shows jump while rising and fall while
    /// dropping; grounded runs the 6-frame cycle when moving and the idle blink otherwise.</summary>
    public Texture2D Frame(bool grounded, float tangentSpeed, float upVelocity, float time)
    {
        if (!grounded) return upVelocity > 15f ? _jump : _fall;
        if (MathF.Abs(tangentSpeed) > 8f) return _run[(int)(time * 12f) % _run.Length];
        return _idle[(int)(time * 4f) % _idle.Length];
    }

    private static Texture2D Load(GraphicsDevice gd, string path)
    {
        using var fs = File.OpenRead(path);
        var tex = Texture2D.FromStream(gd, fs);
        // FromStream yields straight alpha; the batch expects premultiplied.
        var pix = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
        tex.GetData(pix);
        for (var i = 0; i < pix.Length; i++)
            pix[i] = Microsoft.Xna.Framework.Color.FromNonPremultiplied(
                pix[i].R, pix[i].G, pix[i].B, pix[i].A);
        tex.SetData(pix);
        return tex;
    }

    private static (int top, int bottom) OpaqueRowBounds(Texture2D tex)
    {
        var pix = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
        tex.GetData(pix);
        int top = 0, bottom = tex.Height - 1;
        bool RowOpaque(int y)
        {
            for (var x = 0; x < tex.Width; x++)
                if (pix[y * tex.Width + x].A > 16) return true;
            return false;
        }
        while (top < tex.Height - 1 && !RowOpaque(top)) top++;
        while (bottom > top && !RowOpaque(bottom)) bottom--;
        return (top, bottom);
    }
}
