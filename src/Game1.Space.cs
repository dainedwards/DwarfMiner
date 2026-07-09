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

// The space screen: the flyable solar system between planet visits (replaced the point-and-
// click star map). You fly the MOTHERSHIP here — dodging/shooting asteroids, docking rockets
// that return from planets, and spending souls + cargo in the upgrade foundry (U). SpaceSim
// owns the model; this partial owns input, camera, and rendering. See PLAN.md §0.
public sealed partial class DwarfMinerGame
{
    private SpaceSim _space = null!;
    private Texture2D _mothershipTex = null!;

    /// <summary>Foundry overlay state. DM_UPGRADES=1 opens it at boot so tooling can
    /// screenshot the menu without input access; DM_SURVEY=1 likewise for the survey.</summary>
    private bool _upgradesOpen = Environment.GetEnvironmentVariable("DM_UPGRADES") is { Length: > 0 };
    private int _upgradeCursor;
    private bool _surveyOpen = Environment.GetEnvironmentVariable("DM_SURVEY") is { Length: > 0 };

    /// <summary>Muzzle-flash timer for the autocannon.</summary>
    private float _muzzle;

    /// <summary>Space-screen camera zoom, eased every frame toward <see cref="SpaceZoom"/> —
    /// entering space off a launch starts zoomed in at planet scale and pulls out.</summary>
    private float _spaceZoom = SpaceZoom;
    private const float SpaceZoom = 0.55f;
    /// <summary>The in-run zoom to restore when landing (captured at LoadContent, where
    /// DM_ZOOM may have overridden the default).</summary>
    private float _playZoom = 4.0f;

    /// <summary>Switch to the space screen with the mothership parked at a planet. Launch
    /// handoffs pass an exit speed and start the camera at planet scale so docking reads as
    /// one motion.</summary>
    private void EnterSpace(int planetIndex, float exitSpeed = 0f, bool zoomFromPlanet = false)
    {
        _space.PlaceShipAt(planetIndex, exitSpeed);
        ApplyShipTiers();
        _spaceZoom = zoomFromPlanet ? 2.2f : SpaceZoom;
        _screen = GameScreen.Space;
        _camera?.SnapTo(_space.ShipPos, 0f);
    }

    private void UpdateSpace(KeyboardState keys, MouseState mouse, float dt)
    {
        _toastTimer -= dt;

        // The foundry overlay captures input; the system keeps drifting behind it.
        if (Pressed(keys, _prevKeys, Keys.U)) _upgradesOpen = !_upgradesOpen;
        if (_upgradesOpen)
        {
            UpdateUpgradeMenu(keys);
            _space.Update(dt, 0f, thrust: false, brake: false);
            TickSpaceCameraAndBreach(dt);
            return;
        }

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

        // Autocannon — held fire, rate-limited by the sim's cooldown.
        _muzzle -= dt;
        if ((keys.IsKeyDown(Keys.Space) || mouse.LeftButton == ButtonState.Pressed)
            && _space.TryFire())
        {
            _muzzle = 0.06f;
            _sfx.Play("shoot", 0.35f, pitch: -0.2f, pan: 0f, minGap: 0.05f);
        }

        TickSpaceCameraAndBreach(dt);

        // Landing: Enter beside an unlocked planet drops a rover there; locked worlds refuse.
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
                _toast = $"UNCHARTED - ESCAPE {PlanetDefs.All[idx - 1].Name.ToUpperInvariant()} TO CHART A COURSE";
                _toastTimer = 2.5f;
            }
        }
    }

    /// <summary>Per-frame space camera easing, plus the hull-breach consequence: an
    /// emergency dock at the nearest charted world with the hull patched up. Losing the
    /// fight to the rocks costs you your position, not the game.</summary>
    private void TickSpaceCameraAndBreach(float dt)
    {
        _spaceZoom = MathHelper.Lerp(_spaceZoom, SpaceZoom, MathHelper.Clamp(dt * 2.2f, 0f, 1f));
        _camera.Zoom = _spaceZoom;
        _camera.Rotation = 0f;
        _camera.SmoothRotation = 0f;
        _camera.Target = Vector2.Lerp(_camera.Target, _space.ShipPos, MathHelper.Clamp(dt * 8f, 0f, 1f));

        if (!_space.HullBreached) return;
        _space.HullBreached = false;
        var unlocked = Math.Min(_meta.PlanetsUnlocked, PlanetDefs.All.Length);
        var nearest = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i < unlocked; i++)
        {
            var d = (_space.Planets[i].Pos - _space.ShipPos).LengthSquared();
            if (d < bestD) { bestD = d; nearest = i; }
        }
        _space.PlaceShipAt(nearest);
        _space.Hull = SpaceSim.MaxHull;
        _space.Asteroids.Clear();
        _camera.SnapTo(_space.ShipPos, 0f);
        _sfx.Play("explode", 0.8f, pitch: -0.3f);
        _toast = $"HULL BREACH - EMERGENCY DOCK AT {_space.Planets[nearest].Def.Name.ToUpperInvariant()}";
        _toastTimer = 3.5f;
    }

    private void UpdateUpgradeMenu(KeyboardState keys)
    {
        if (Pressed(keys, _prevKeys, Keys.Escape)) _upgradesOpen = false;
        var count = Upgrades.All.Length;
        if (Pressed(keys, _prevKeys, Keys.Up) || Pressed(keys, _prevKeys, Keys.W))
            _upgradeCursor = (_upgradeCursor - 1 + count) % count;
        if (Pressed(keys, _prevKeys, Keys.Down) || Pressed(keys, _prevKeys, Keys.S))
            _upgradeCursor = (_upgradeCursor + 1) % count;
        if (Pressed(keys, _prevKeys, Keys.Enter))
        {
            var def = Upgrades.All[_upgradeCursor];
            if (Upgrades.Owned(_meta, def.Id))
            {
                _toast = "ALREADY INSTALLED";
            }
            else if (Upgrades.TryBuy(_meta, def))
            {
                // Ship tiers apply immediately; dwarf gear applies on the next rover drop.
                _space.GunTier = Upgrades.Owned(_meta, "gun2") ? 2 : 1;
                _space.EngineTier = Upgrades.Owned(_meta, "engine2") ? 2 : 1;
                _sfx.Play("pickup", 0.8f);
                _toast = $"{def.Name.ToUpperInvariant()} INSTALLED";
            }
            else
            {
                _toast = "CAN'T AFFORD - SOULS + CARGO REQUIRED";
            }
            _toastTimer = 2.5f;
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

        // Asteroids — cratered grey rocks; a rotating surface pock sells the spin.
        foreach (var a in _space.Asteroids)
        {
            FillCircleWorld(sb, a.Pos, a.Radius, new Color(105, 98, 92));
            FillCircleWorld(sb, a.Pos + new Vector2(a.Radius * 0.25f, a.Radius * 0.2f),
                a.Radius * 0.62f, new Color(84, 78, 74));
            var pock = a.Pos + new Vector2(MathF.Cos(a.Rot), MathF.Sin(a.Rot)) * a.Radius * 0.45f;
            FillCircleWorld(sb, pock, a.Radius * 0.2f, new Color(66, 61, 58));
        }

        // Autocannon bolts.
        foreach (var s in _space.Shots)
        {
            sb.Draw(_renderer.Pixel, new Rectangle((int)s.Pos.X - 2, (int)s.Pos.Y - 2, 4, 4),
                new Color(255, 240, 160));
            var tail = s.Pos - Vector2.Normalize(s.Vel) * 8f;
            sb.Draw(_renderer.Pixel, new Rectangle((int)tail.X - 1, (int)tail.Y - 1, 2, 2),
                new Color(255, 170, 70) * 0.7f);
        }

        // Exhaust flame under the engines while thrusting — flickering wedge of rects.
        if (_space.Thrusting)
        {
            var back = -_space.ShipDir;
            for (var i = 0; i < 4; i++)
            {
                var d = 30f + i * 11f + (float)Random.Shared.NextDouble() * 6f;
                var w = 14 - i * 3;
                var pos = _space.ShipPos + back * d;
                var col = i < 2 ? new Color(255, 220, 120) : new Color(255, 140, 60);
                sb.Draw(_renderer.Pixel,
                    new Rectangle((int)pos.X - w / 2, (int)pos.Y - w / 2, w, w),
                    col * (0.9f - i * 0.15f));
            }
        }

        // The mothership — sprite art points up, so heading needs the +π/2 correction.
        // Recent asteroid hits flash the hull red for the invulnerability window.
        var tint = _space.HitTimer > 0f && MathF.Sin(_totalTime * 40f) > 0f
            ? new Color(255, 120, 110) : Color.White;
        sb.Draw(_mothershipTex, _space.ShipPos, null, tint,
            _space.ShipHeading + MathF.PI / 2f,
            new Vector2(_mothershipTex.Width / 2f, _mothershipTex.Height / 2f), 3f, SpriteEffects.None, 0f);

        if (_muzzle > 0f)
        {
            var nose = _space.ShipPos + _space.ShipDir * (SpaceSim.ShipRadius + 8f);
            FillCircleWorld(sb, nose, 7f, new Color(255, 240, 170) * 0.9f);
        }

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
                ? $"ENTER  DEPLOY ROVER TO {cand.Def.Name.ToUpperInvariant()}"
                : "UNCHARTED WORLD - YOUR NAV CORE CAN'T CHART IT YET";
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

        // Ship status block, bottom-left: hull pips, tank, currencies.
        var hullY = VirtualHeight - 96;
        _renderer.DrawText("HULL", new Vector2(24, hullY), new Color(150, 155, 175));
        for (var i = 0; i < SpaceSim.MaxHull; i++)
        {
            var lit = i < _space.Hull;
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(_renderer.Pixel, new Rectangle(70 + i * 16, hullY - 1, 12, 10),
                lit ? new Color(120, 220, 130) : new Color(52, 56, 68));
            sb.End();
        }
        var cargoTotal = 0;
        foreach (var (_, c) in _meta.ShipCargo) cargoTotal += c;
        _renderer.DrawText($"FUEL {_meta.MotherFuel}   SOULS {_meta.TotalSouls()}   CARGO {cargoTotal}",
            new Vector2(24, hullY + 18), new Color(150, 155, 175));

        var controls = "A/D TURN   W THRUST   S BRAKE   SPACE FIRE   ENTER LAND   U FOUNDRY";
        _renderer.DrawText(controls,
            new Vector2((VirtualWidth - _renderer.MeasureText(controls)) / 2f, VirtualHeight - 58),
            new Color(150, 155, 175));
        var meta = $"ESCAPES {_meta.Escapes}   TITAN KILLS {_meta.TitansDefeated}   DEEPEST {_meta.DeepestDepth}   DEATHS {_meta.Deaths}";
        _renderer.DrawText(meta,
            new Vector2((VirtualWidth - _renderer.MeasureText(meta)) / 2f, VirtualHeight - 34),
            new Color(120, 125, 145));

        if (_upgradesOpen) DrawUpgradeMenu(sb);
    }

    /// <summary>The foundry overlay: one line per upgrade with soul + cargo price, cursor
    /// selection, and a wallet readout. Purchases persist in MetaSave.ShipUpgrades.</summary>
    private void DrawUpgradeMenu(SpriteBatch sb)
    {
        const int w = 620, h = 330;
        var x = (VirtualWidth - w) / 2;
        var y = (VirtualHeight - h) / 2;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, h), new Color(10, 12, 22, 235));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, 2), new Color(255, 225, 140));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y + h - 2, w, 2), new Color(255, 225, 140));
        sb.End();

        _renderer.DrawText("MOTHERSHIP FOUNDRY",
            new Vector2(x + (w - _renderer.MeasureText("MOTHERSHIP FOUNDRY", 3)) / 2f, y + 18), Color.White, 3);
        _renderer.DrawText($"SOULS {_meta.TotalSouls()}    CARGO HOLD: {CargoSummary()}",
            new Vector2(x + 24, y + 54), new Color(150, 155, 175));

        for (var i = 0; i < Upgrades.All.Length; i++)
        {
            var def = Upgrades.All[i];
            var rowY = y + 92 + i * 62;
            var owned = Upgrades.Owned(_meta, def.Id);
            var afford = Upgrades.CanAfford(_meta, def);
            if (i == _upgradeCursor)
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                sb.Draw(_renderer.Pixel, new Rectangle(x + 12, rowY - 6, w - 24, 54), new Color(45, 50, 70, 180));
                sb.End();
            }
            var nameCol = owned ? new Color(140, 220, 140) : afford ? Color.White : new Color(130, 132, 145);
            var cost = owned ? "INSTALLED" : CostLabel(def);
            _renderer.DrawText(def.Name.ToUpperInvariant(), new Vector2(x + 24, rowY), nameCol, 2);
            _renderer.DrawText(cost,
                new Vector2(x + w - 24 - _renderer.MeasureText(cost), rowY + 4),
                owned ? new Color(140, 220, 140) : afford ? new Color(255, 225, 140) : new Color(160, 120, 110));
            _renderer.DrawText(def.Desc.ToUpperInvariant(), new Vector2(x + 24, rowY + 24), new Color(150, 155, 175));
        }

        _renderer.DrawText("UP/DOWN SELECT   ENTER INSTALL   U/ESC CLOSE",
            new Vector2(x + (w - _renderer.MeasureText("UP/DOWN SELECT   ENTER INSTALL   U/ESC CLOSE")) / 2f, y + h - 28),
            new Color(150, 155, 175));
    }

    private string CargoSummary()
    {
        if (_meta.ShipCargo.Count == 0) return "EMPTY";
        var parts = new List<string>();
        foreach (var (id, c) in _meta.ShipCargo)
        {
            parts.Add($"{c} {Tiles.ResourceLabel(id)}");
            if (parts.Count == 4) { parts.Add("…"); break; }
        }
        return string.Join("  ", parts);
    }

    private static string CostLabel(UpgradeDef def)
    {
        var s = $"{def.Souls} SOUL{(def.Souls == 1 ? "" : "S")}";
        foreach (var (id, n) in def.Mats) s += $" + {n} {Tiles.ResourceLabel(id)}";
        return s;
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

    /// <summary>The mothership — a broad twin-nacelle cruiser (the little rocket from the
    /// planet escape docks with this). Art points up; the draw call rotates by heading.</summary>
    private Texture2D BuildMothershipTexture() => Renderer.BuildSprite(GraphicsDevice, new[]
    {
        "......WW......",
        ".....WCCW.....",
        ".....CCCC.....",
        "....SCggCS....",
        "....SCggCS....",
        "...SSCCCCSS...",
        "...SSSSSSSS...",
        "..dSSSSSSSSd..",
        ".ddSSyySSyySd.",
        ".dSSSSSSSSSSd.",
        "ddSSSSggSSSSdd",
        "dSSSSSSSSSSSSd",
        "dSSdSSSSSSdSSd",
        "NNdSSSSSSSSdNN",
        "NNddSSSSSSddNN",
        "NN..dNNNNd..NN",
        "....dNNNNd....",
        "....dNNNNd....",
    }, new Dictionary<char, Color>
    {
        ['.'] = Color.Transparent,
        ['W'] = new Color(235, 235, 240),  // nose tip
        ['C'] = new Color(150, 60, 55),    // command module red
        ['g'] = new Color(140, 210, 235),  // viewport glass
        ['S'] = new Color(185, 190, 205),  // hull steel
        ['d'] = new Color(115, 120, 138),  // hull shadow / trim
        ['y'] = new Color(230, 190, 70),   // warning stripes
        ['N'] = new Color(70, 72, 85),     // engine nacelles
    });
}
