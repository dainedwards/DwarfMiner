using System;
using System.Collections.Generic;
using DwarfMiner.Rendering;
using DwarfMiner.Space;
using DwarfMiner.Systems;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner;

// The space screen: the flyable solar system between planet runs (replaced the point-and-click
// star map). SpaceSim owns the model; this partial owns input, camera, and rendering.
public sealed partial class DwarfMinerGame
{
    private SpaceSim _space = null!;
    private Texture2D _rocketTex = null!;

    /// <summary>Space-screen camera zoom, eased every frame toward <see cref="SpaceZoom"/> —
    /// entering space off a launch starts zoomed in at planet scale and pulls out.</summary>
    private float _spaceZoom = SpaceZoom;
    private const float SpaceZoom = 0.55f;
    /// <summary>The in-run zoom to restore when landing (captured at LoadContent, where
    /// DM_ZOOM may have overridden the default).</summary>
    private float _playZoom = 4.0f;

    /// <summary>Switch to the space screen with the ship parked at a planet. Launch handoffs
    /// pass an exit speed and start the camera at planet scale so leaving reads as one motion.</summary>
    private void EnterSpace(int planetIndex, float exitSpeed = 0f, bool zoomFromPlanet = false)
    {
        _space.PlaceShipAt(planetIndex, exitSpeed);
        _spaceZoom = zoomFromPlanet ? 2.2f : SpaceZoom;
        _screen = GameScreen.Space;
        _camera?.SnapTo(_space.ShipPos, 0f);
    }

    private void UpdateSpace(KeyboardState keys, float dt)
    {
        // R resumes the suspended run, if any — same affordance the star map had.
        if (Pressed(keys, _prevKeys, Keys.R) && RunSave.Exists)
        {
            ResumeRun();
            return;
        }

        var turn = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                 + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        var thrust = keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up);
        var brake = keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down);
        _space.Update(dt, turn, thrust, brake);

        _spaceZoom = MathHelper.Lerp(_spaceZoom, SpaceZoom, MathHelper.Clamp(dt * 2.2f, 0f, 1f));
        _camera.Zoom = _spaceZoom;
        _camera.Rotation = 0f;
        _camera.SmoothRotation = 0f;
        _camera.Target = Vector2.Lerp(_camera.Target, _space.ShipPos, MathHelper.Clamp(dt * 8f, 0f, 1f));

        _toastTimer -= dt;

        // Landing: Enter beside an unlocked planet starts a run there; locked worlds refuse.
        if (_space.LandingCandidate() is { } cand
            && (Pressed(keys, _prevKeys, Keys.Enter) || Pressed(keys, _prevKeys, Keys.E)))
        {
            var idx = PlanetDefs.IndexOf(cand.Def);
            var unlocked = Math.Min(_meta.PlanetsUnlocked, PlanetDefs.All.Length);
            if (idx < unlocked)
            {
                StartNewRun(cand.Def);
            }
            else
            {
                _toast = $"UNCHARTED — ESCAPE {PlanetDefs.All[idx - 1].Name.ToUpperInvariant()} TO CHART A COURSE";
                _toastTimer = 2.5f;
            }
        }
    }

    private void DrawSpace()
    {
        GraphicsDevice.Clear(new Color(4, 5, 12));
        var sb = _renderer.Batch;
        var view = _camera.View;
        var unlocked = Math.Min(_meta.PlanetsUnlocked, PlanetDefs.All.Length);

        // Parallax starfield: hashed screen positions scrolled against the camera at two
        // depths and wrapped, so flying reads as motion even with nothing else on screen.
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var layer = 0; layer < 2; layer++)
        {
            var factor = layer == 0 ? 0.04f : 0.11f;
            var n = layer == 0 ? 130 : 70;
            for (var i = 0; i < n; i++)
            {
                var h = ((i + layer * 977) * 1013904223 + 1664525) & 0x7fffffff;
                var x = Mod((h >> 7) % VirtualWidth - _camera.Target.X * factor, VirtualWidth);
                var y = Mod((h >> 17) % VirtualHeight - _camera.Target.Y * factor, VirtualHeight);
                var tw = MathF.Sin(_totalTime * (0.6f + (h & 3) * 0.5f) + i) * 0.5f + 0.5f;
                var bright = (0.2f + tw * 0.5f) * (layer == 0 ? 0.7f : 1f);
                var size = (h & 15) == 0 ? 2 : 1;
                sb.Draw(_renderer.Pixel, new Rectangle((int)x, (int)y, size, size),
                    new Color(200, 210, 235) * bright);
            }
        }
        sb.End();

        // World pass — everything in space coordinates under the camera transform.
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: view);

        // Orbit rings: dotted circles, dimmer past the unlock frontier.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var col = i < unlocked ? new Color(70, 76, 100) : new Color(34, 37, 48);
            var dots = (int)(p.OrbitRadius / 28f);
            for (var d = 0; d < dots; d++)
            {
                var a = d * MathHelper.TwoPi / dots;
                var pos = new Vector2(MathF.Cos(a), MathF.Sin(a)) * p.OrbitRadius;
                sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 4, 4), col);
            }
        }

        // The sun: layered corona glow with a slow breathing flicker, then the body.
        var flick = 1f + MathF.Sin(_totalTime * 1.7f) * 0.04f;
        FillCircleWorld(sb, Vector2.Zero, (SpaceSim.SunRadius + 150f) * flick, new Color(255, 160, 50, 14));
        FillCircleWorld(sb, Vector2.Zero, (SpaceSim.SunRadius + 70f) * flick, new Color(255, 180, 60, 30));
        FillCircleWorld(sb, Vector2.Zero, SpaceSim.SunRadius, new Color(255, 190, 80));
        FillCircleWorld(sb, Vector2.Zero, SpaceSim.SunRadius * 0.7f, new Color(255, 235, 170));

        // Planets — same disc treatment the star map used (body, terminator, polar highlight).
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var locked = i >= unlocked;
            var body = locked ? new Color(40, 42, 52) : p.Def.MapColor;
            var accent = locked ? new Color(60, 62, 72) : p.Def.MapAccent;
            var c = p.Pos;
            FillCircleWorld(sb, c, p.BodyRadius, body);
            // Terminator: the shadowed side faces away from the sun.
            var sunward = c.LengthSquared() > 1f ? Vector2.Normalize(c) : Vector2.UnitY;
            FillCircleWorld(sb, c + sunward * (p.BodyRadius * 0.34f), p.BodyRadius * 0.72f,
                new Color(body.R / 2, body.G / 2, body.B / 2));
            FillCircleWorld(sb, c - sunward * (p.BodyRadius * 0.3f), p.BodyRadius * 0.32f, accent);
        }

        // Exhaust flame under the nozzle while thrusting — flickering wedge of rects.
        if (_space.Thrusting)
        {
            var back = -_space.ShipDir;
            for (var i = 0; i < 4; i++)
            {
                var d = 16f + i * 9f + (float)Random.Shared.NextDouble() * 6f;
                var w = 10 - i * 2;
                var pos = _space.ShipPos + back * d;
                var col = i < 2 ? new Color(255, 220, 120) : new Color(255, 140, 60);
                sb.Draw(_renderer.Pixel,
                    new Rectangle((int)pos.X - w / 2, (int)pos.Y - w / 2, w, w),
                    col * (0.9f - i * 0.15f));
            }
        }

        // The rocket — sprite art points up, so heading needs the +π/2 correction.
        sb.Draw(_rocketTex, _space.ShipPos, null, Color.White,
            _space.ShipHeading + MathF.PI / 2f,
            new Vector2(_rocketTex.Width / 2f, _rocketTex.Height / 2f), 2.2f, SpriteEffects.None, 0f);

        sb.End();

        // Text pass — screen space; planet labels project through the camera.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var locked = i >= unlocked;
            var name = locked ? "???" : p.Def.Name.ToUpperInvariant();
            var screen = Vector2.Transform(p.Pos + new Vector2(0f, -(p.BodyRadius + 46f)), view);
            _renderer.DrawText(name,
                new Vector2(screen.X - _renderer.MeasureText(name, 2) / 2f, screen.Y),
                locked ? new Color(110, 112, 125) : Color.White, 2);
            if (_meta.PlanetsEscaped.Contains(p.Def.Id))
            {
                var esc = Vector2.Transform(p.Pos + new Vector2(0f, p.BodyRadius + 30f), view);
                _renderer.DrawText("ESCAPED",
                    new Vector2(esc.X - _renderer.MeasureText("ESCAPED") / 2f, esc.Y),
                    new Color(140, 220, 140));
            }
        }

        // Landing prompt for the planet under the ship.
        if (_space.LandingCandidate() is { } cand)
        {
            var idx = PlanetDefs.IndexOf(cand.Def);
            var prompt = idx < unlocked
                ? $"ENTER  LAND ON {cand.Def.Name.ToUpperInvariant()}"
                : "UNCHARTED WORLD — YOUR NAV CORE CAN'T CHART IT YET";
            var col = idx < unlocked ? new Color(255, 225, 140) : new Color(200, 130, 120);
            _renderer.DrawText(prompt,
                new Vector2((VirtualWidth - _renderer.MeasureText(prompt, 2)) / 2f, VirtualHeight - 140), col, 2);
            if (idx < unlocked)
            {
                var line2 = $"{cand.Def.Tagline.ToUpperInvariant()}   NAV CORE: {cand.Def.ShipOreCount} {Tiles.ResourceLabel(cand.Def.ShipOre)}";
                _renderer.DrawText(line2,
                    new Vector2((VirtualWidth - _renderer.MeasureText(line2)) / 2f, VirtualHeight - 112),
                    new Color(190, 195, 215));
            }
        }

        if (RunSave.Exists)
        {
            const string resume = "R  RESUME SAVED RUN";
            _renderer.DrawText(resume,
                new Vector2((VirtualWidth - _renderer.MeasureText(resume, 2)) / 2f, 46),
                new Color(140, 220, 140), 2);
        }

        if (_toastTimer > 0f && _toast.Length > 0)
            _renderer.DrawText(_toast,
                new Vector2((VirtualWidth - _renderer.MeasureText(_toast, 2)) / 2f, 84),
                new Color(255, 225, 140), 2);

        const string controls = "A/D TURN   W THRUST   S BRAKE   ENTER LAND";
        _renderer.DrawText(controls,
            new Vector2((VirtualWidth - _renderer.MeasureText(controls)) / 2f, VirtualHeight - 58),
            new Color(150, 155, 175));
        var meta = $"ESCAPES {_meta.Escapes}   TITAN KILLS {_meta.TitansDefeated}   DEEPEST {_meta.DeepestDepth}   DEATHS {_meta.Deaths}";
        _renderer.DrawText(meta,
            new Vector2((VirtualWidth - _renderer.MeasureText(meta)) / 2f, VirtualHeight - 34),
            new Color(120, 125, 145));
    }

    /// <summary>Filled circle drawn as horizontal strips — coordinates go through whatever
    /// transform the current batch was begun with, so this works in both screen and space
    /// coordinates.</summary>
    private void FillCircleWorld(SpriteBatch sb, Vector2 centre, float radius, Color color)
    {
        var r = (int)radius;
        for (var dy = -r; dy <= r; dy++)
        {
            var half = (int)MathF.Sqrt(radius * radius - dy * dy);
            sb.Draw(_renderer.Pixel,
                new Rectangle((int)centre.X - half, (int)centre.Y + dy, half * 2, 1), color);
        }
    }

    private static float Mod(float v, float m) => v - MathF.Floor(v / m) * m;

    private Texture2D BuildRocketTexture() => Renderer.BuildSprite(GraphicsDevice, new[]
    {
        "....WW....",
        "...WRRW...",
        "...RRRR...",
        "..RRRRRR..",
        "..RRggRR..",
        "..RRggRR..",
        "..RRRRRR..",
        "..SSSSSS..",
        "..SSSSSS..",
        ".FSSSSSSF.",
        ".FSSSSSSF.",
        "FFSSSSSSFF",
        "FF.NNNN.FF",
        "...NNNN...",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(235, 235, 240),  // nose tip
        ['R'] = new Color(190, 60, 50),    // nose cone red
        ['g'] = new Color(140, 210, 235),  // porthole glass
        ['S'] = new Color(185, 190, 205),  // hull steel
        ['F'] = new Color(150, 55, 45),    // fins
        ['N'] = new Color(80, 82, 95),     // nozzle
    });
}
