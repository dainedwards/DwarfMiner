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

    private void ApplyShipTiers()
    {
        _space.GunTier = Upgrades.Owned(_meta, "gun3") ? 3 : Upgrades.Owned(_meta, "gun2") ? 2 : 1;
        _space.EngineTier = Upgrades.Owned(_meta, "engine3") ? 3 : Upgrades.Owned(_meta, "engine2") ? 2 : 1;
        _space.HullTier = Upgrades.Owned(_meta, "hull2") ? 2 : 1;
        _space.HasShield = Upgrades.Owned(_meta, "shield");
    }

    /// <summary>Boot-time restore of the persisted mothership: park where you left it (with
    /// the hull you left with) or fall back to a planet for fresh installs.</summary>
    private void RestoreShipState()
    {
        ApplyShipTiers();
        if (!_meta.ShipStateSaved)
        {
            _space.PlaceShipAt(Math.Min(_meta.PlanetsUnlocked, PlanetDefs.All.Length) - 1);
            return;
        }
        _space.ShipPos = new Vector2(_meta.ShipPosX, _meta.ShipPosY);
        _space.ShipHeading = _meta.ShipHeadingSave;
        if (_meta.ShipHull > 0) _space.Hull = Math.Min(_meta.ShipHull, _space.HullMax);
    }

    /// <summary>Snapshot the mothership into MetaSave — called wherever meta already saves
    /// (landing, quitting), so the ship is wherever you left it next boot.</summary>
    private void CaptureShipState()
    {
        if (_space is null) return;
        _meta.ShipStateSaved = true;
        _meta.ShipPosX = _space.ShipPos.X;
        _meta.ShipPosY = _space.ShipPos.Y;
        _meta.ShipHeadingSave = _space.ShipHeading;
        _meta.ShipHull = _space.Hull;
    }

    private void UpdateSpace(KeyboardState keys, MouseState mouse, float dt)
    {
        _toastTimer -= dt;

        // Overlays capture input while the system keeps drifting behind them; opening one
        // closes the other.
        if (Pressed(keys, _prevKeys, Keys.U)) { _upgradesOpen = !_upgradesOpen; _surveyOpen = false; }
        if (Pressed(keys, _prevKeys, Keys.M)) { _surveyOpen = !_surveyOpen; _upgradesOpen = false; }
        if (_upgradesOpen || _surveyOpen)
        {
            if (_upgradesOpen) UpdateUpgradeMenu(keys);
            else if (Pressed(keys, _prevKeys, Keys.Escape)) _surveyOpen = false;
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
        var prevHull = _space.Hull;
        _space.Update(dt, turn, thrust, brake);

        // Engine rumble: a short loopable burst re-triggered while the burn is held.
        if (thrust) _sfx.Play("thrust", _space.HasFuel ? 0.4f : 0.18f, pitch: -0.2f, minGap: 0.22f);
        // Positional shatter for any rock that died this tick; a thud when one of them was us.
        if (_space.LastRockShattered is { } rock)
        {
            _space.LastRockShattered = null;
            PlayAt("break", rock, 0.8f, pitch: -0.3f, minGap: 0.05f);
        }
        if (_space.Hull < prevHull) _sfx.Play("hurt", 0.7f, pitch: -0.4f);

        // Autocannon — held fire, rate-limited by the sim's cooldown.
        _muzzle -= dt;
        if ((keys.IsKeyDown(Keys.Space) || mouse.LeftButton == ButtonState.Pressed)
            && _space.TryFire())
        {
            _muzzle = 0.06f;
            _sfx.Play("shoot", 0.35f, pitch: -0.2f, pan: 0f, minGap: 0.05f);
        }

        TickSpaceCameraAndBreach(dt);

        // Warp jump: all five core shards let the mothership fold space to the Rift.
        if (Pressed(keys, _prevKeys, Keys.J))
        {
            var needed = PlanetDefs.WarpShardsNeeded;
            if (_meta.CoreShards.Count >= needed)
            {
                var rift = _space.Planets.FindIndex(p => p.Def.Id == "rift");
                _space.PlaceShipAt(rift);
                _space.Asteroids.Clear();
                _spaceZoom = 1.6f;   // snap in, ease back out — sells the fold
                _camera.SnapTo(_space.ShipPos, 0f);
                _sfx.Play("launch", 0.8f, pitch: 0.4f);
                _toast = "WARP JUMP COMPLETE - THE RIFT LOOMS";
            }
            else
            {
                _toast = $"WARP DRIVE COLD - {needed - _meta.CoreShards.Count} MORE CORE SHARDS NEEDED";
            }
            _toastTimer = 3f;
        }

        // Landing: Enter beside any planet drops a rover there (fuel range is the travel
        // gate now, not an unlock chain). No rovers left = emergency drop pod: you still
        // get down, but the crash costs half your health. The Rift refuses landings until
        // the warp drive is shard-complete — its storms shred an unshielded descent.
        if (_space.LandingCandidate() is { } cand
            && (Pressed(keys, _prevKeys, Keys.Enter) || Pressed(keys, _prevKeys, Keys.E)))
        {
            if (cand.Def.Id == "rift" && _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded)
            {
                _toast = $"THE RIFT REJECTS YOU - {PlanetDefs.WarpShardsNeeded} CORE SHARDS REQUIRED TO BREACH ITS STORMS";
                _toastTimer = 3f;
                return;
            }
            var podDrop = _meta.Rovers <= 0;
            if (!podDrop) _meta.Rovers--;
            CaptureShipState();
            _meta.Save();
            StartNewRun(cand.Def, descend: true);
            if (podDrop)
            {
                // Pod Dampeners turn the crash landing into a soft one.
                if (Upgrades.Owned(_meta, "dampeners"))
                {
                    _toast = "NO ROVERS - DROP POD DOWN SOFT ON THE DAMPENERS";
                }
                else
                {
                    _run.Player.Health *= 0.5f;
                    _toast = "NO ROVERS - EMERGENCY DROP POD! SUIT DAMAGED";
                }
                _toastTimer = 4f;
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

        // Fuel plumbing: the sim accumulates burn, the meta tank pays it in whole units.
        // A dry tank drops the engines to reserve power (35%) — slow, never stuck.
        while (_space.FuelUsed >= 1f)
        {
            _space.FuelUsed -= 1f;
            if (_meta.MotherFuel > 0) _meta.MotherFuel--;
        }
        _space.HasFuel = _meta.MotherFuel > 0;

        if (!_space.HullBreached) return;
        _space.HullBreached = false;
        var nearest = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var d = (_space.Planets[i].Pos - _space.ShipPos).LengthSquared();
            if (d < bestD) { bestD = d; nearest = i; }
        }
        _space.PlaceShipAt(nearest);
        _space.Hull = _space.HullMax;
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
            if (!def.Repeatable && Upgrades.Owned(_meta, def.Id))
            {
                _toast = "ALREADY INSTALLED";
            }
            else if (Upgrades.Locked(_meta, def))
            {
                var req = Array.Find(Upgrades.All, u => u.Id == def.Requires)!;
                _toast = $"REQUIRES {req.Name.ToUpperInvariant()} FIRST";
            }
            else if (Upgrades.TryBuy(_meta, def))
            {
                // Ship tiers apply immediately; dwarf gear applies on the next rover drop.
                ApplyShipTiers();
                _sfx.Play("pickup", 0.8f);
                _toast = def.Repeatable
                    ? $"{def.Name.ToUpperInvariant()} BUILT ({_meta.Rovers} ABOARD)"
                    : $"{def.Name.ToUpperInvariant()} INSTALLED";
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

        // Orbit rings: dotted circles.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var col = new Color(70, 76, 100);
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
            var body = p.Def.MapColor;
            var accent = p.Def.MapAccent;
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
            var name = p.Def.Name.ToUpperInvariant();
            var warpLocked = p.Def.Id == "rift" && _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded;
            if (warpLocked) name += " [WARP LOCKED]";
            var screen = Vector2.Transform(p.Pos + new Vector2(0f, -(p.BodyRadius + 46f)), view);
            _renderer.DrawText(name,
                new Vector2(screen.X - _renderer.MeasureText(name, 2) / 2f, screen.Y),
                warpLocked ? new Color(255, 110, 90) : Color.White, 2);
            if (_meta.PlanetsEscaped.Contains(p.Def.Id))
            {
                var esc = Vector2.Transform(p.Pos + new Vector2(0f, p.BodyRadius + 30f), view);
                _renderer.DrawText("ESCAPED",
                    new Vector2(esc.X - _renderer.MeasureText("ESCAPED") / 2f, esc.Y),
                    new Color(140, 220, 140));
            }
        }

        // Landing prompt for the planet under the ship. Roverless drops still work but warn;
        // the Rift shows its shard gate instead.
        if (_space.LandingCandidate() is { } cand)
        {
            var riftLocked = cand.Def.Id == "rift" && _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded;
            var prompt = riftLocked
                ? $"THE RIFT'S STORMS REPEL YOU - {PlanetDefs.WarpShardsNeeded - _meta.CoreShards.Count} MORE CORE SHARDS NEEDED"
                : _meta.Rovers > 0
                    ? $"ENTER  DEPLOY ROVER TO {cand.Def.Name.ToUpperInvariant()} ({_meta.Rovers} LEFT)"
                    : $"ENTER  EMERGENCY DROP POD TO {cand.Def.Name.ToUpperInvariant()} (NO ROVERS - HALF HEALTH!)";
            var col = riftLocked ? new Color(255, 110, 90)
                : _meta.Rovers > 0 ? new Color(255, 225, 140) : new Color(230, 150, 110);
            _renderer.DrawText(prompt,
                new Vector2((VirtualWidth - _renderer.MeasureText(prompt, 2)) / 2f, VirtualHeight - 140), col, 2);
            var line2 = $"{cand.Def.Tagline.ToUpperInvariant()}   NAV CORE: {cand.Def.ShipOreCount} {Tiles.ResourceLabel(cand.Def.ShipOre)}";
            _renderer.DrawText(line2,
                new Vector2((VirtualWidth - _renderer.MeasureText(line2)) / 2f, VirtualHeight - 112),
                new Color(190, 195, 215));
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

        // Ship status block, bottom-left: hull pips, tank, currencies, rovers.
        var hullY = VirtualHeight - 96;
        _renderer.DrawText("HULL", new Vector2(24, hullY), new Color(150, 155, 175));
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < _space.HullMax; i++)
        {
            var lit = i < _space.Hull;
            sb.Draw(_renderer.Pixel, new Rectangle(70 + i * 16, hullY - 1, 12, 10),
                lit ? new Color(120, 220, 130) : new Color(52, 56, 68));
        }
        sb.End();
        var cargoTotal = 0;
        foreach (var (_, c) in _meta.ShipCargo) cargoTotal += c;
        var fuelCol = _meta.MotherFuel > 0 ? new Color(150, 155, 175) : new Color(230, 140, 110);
        _renderer.DrawText(
            $"FUEL {_meta.MotherFuel}{(_meta.MotherFuel > 0 ? "" : " [RESERVE POWER]")}   ROVERS {_meta.Rovers}   SOULS {_meta.TotalSouls()}   CARGO {cargoTotal}",
            new Vector2(24, hullY + 18), fuelCol);
        var shardCol = _meta.CoreShards.Count >= PlanetDefs.WarpShardsNeeded
            ? new Color(150, 230, 255) : new Color(150, 155, 175);
        _renderer.DrawText(
            $"CORE SHARDS {_meta.CoreShards.Count}/{PlanetDefs.WarpShardsNeeded}{(_meta.CoreShards.Count >= PlanetDefs.WarpShardsNeeded ? "  [WARP READY - PRESS J]" : "")}",
            new Vector2(24, hullY + 36), shardCol);

        var controls = "A/D TURN   W THRUST   S BRAKE   SPACE FIRE   ENTER LAND   U FOUNDRY   M SURVEY   J WARP";
        _renderer.DrawText(controls,
            new Vector2((VirtualWidth - _renderer.MeasureText(controls)) / 2f, VirtualHeight - 58),
            new Color(150, 155, 175));
        var meta = $"ESCAPES {_meta.Escapes}   TITAN KILLS {_meta.TitansDefeated}   DEEPEST {_meta.DeepestDepth}   DEATHS {_meta.Deaths}";
        _renderer.DrawText(meta,
            new Vector2((VirtualWidth - _renderer.MeasureText(meta)) / 2f, VirtualHeight - 34),
            new Color(120, 125, 145));

        if (_upgradesOpen) DrawUpgradeMenu(sb);
        if (_surveyOpen) DrawSurveyMenu(sb);
    }

    /// <summary>The M-key long-range survey: every planet's titan (with souls banked of that
    /// kind) and its biggest ore deposits with approximate counts — see Space.Survey for why
    /// "approximate" is honest. First open generates each world once (a beat of hitch).</summary>
    private void DrawSurveyMenu(SpriteBatch sb)
    {
        const int w = 860;
        var count = PlanetDefs.All.Length;
        var h = 120 + count * 66;
        var x = (VirtualWidth - w) / 2;
        var y = (VirtualHeight - h) / 2;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, h), new Color(10, 12, 22, 235));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, 2), new Color(140, 200, 255));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y + h - 2, w, 2), new Color(140, 200, 255));
        sb.End();

        _renderer.DrawText("SYSTEM SURVEY",
            new Vector2(x + (w - _renderer.MeasureText("SYSTEM SURVEY", 3)) / 2f, y + 16), Color.White, 3);

        for (var i = 0; i < count; i++)
        {
            var def = PlanetDefs.All[i];
            var rowY = y + 62 + i * 66;
            var souls = _meta.SoulsOf(def.Titan.ToString());
            var slain = souls > 0 ? $"SOULS {souls}" : "NO SOULS YET";
            _renderer.DrawText(def.Name.ToUpperInvariant(), new Vector2(x + 24, rowY), Color.White, 2);
            _renderer.DrawText($"TITAN: {TitanName(def.Titan).ToUpperInvariant()}  [{slain}]",
                new Vector2(x + 250, rowY + 4),
                souls > 0 ? new Color(140, 220, 140) : new Color(200, 160, 120));
            if (def.Id != "rift")
                _renderer.DrawText(_meta.CoreShards.Contains(def.Id) ? "SHARD SECURED" : "SHARD IN CORE",
                    new Vector2(x + w - 24 - _renderer.MeasureText(_meta.CoreShards.Contains(def.Id) ? "SHARD SECURED" : "SHARD IN CORE"), rowY + 4),
                    _meta.CoreShards.Contains(def.Id) ? new Color(150, 230, 255) : new Color(120, 125, 145));
            var deposits = "";
            foreach (var (label, n) in Survey.For(def))
                deposits += $"{label} {FormatCount(n)}   ";
            _renderer.DrawText(deposits.TrimEnd(),
                new Vector2(x + 24, rowY + 28), new Color(150, 155, 175));
        }

        _renderer.DrawText("M/ESC CLOSE",
            new Vector2(x + (w - _renderer.MeasureText("M/ESC CLOSE")) / 2f, y + h - 26),
            new Color(150, 155, 175));
    }

    /// <summary>Approximate-count formatting for the survey ("1.4K" style) — the rounding is
    /// the point: the real world is seeded per-visit, so exact digits would be a lie.</summary>
    private static string FormatCount(int n) =>
        n >= 1000 ? $"{n / 1000f:0.0}K" : n >= 100 ? $"{n / 10 * 10}+" : n.ToString();

    /// <summary>Rows visible at once in the foundry — the catalogue outgrew the screen, so
    /// the list scrolls with the cursor.</summary>
    private const int FoundryVisibleRows = 8;
    private int _upgradeScroll;

    /// <summary>The foundry overlay: one line per upgrade with soul + cargo price, cursor
    /// selection (scrolling window), and a wallet readout. Purchases persist in
    /// MetaSave.ShipUpgrades.</summary>
    private void DrawUpgradeMenu(SpriteBatch sb)
    {
        const int w = 680;
        var visible = Math.Min(FoundryVisibleRows, Upgrades.All.Length);
        var h = 140 + visible * 52;
        var x = (VirtualWidth - w) / 2;
        var y = (VirtualHeight - h) / 2;

        // Keep the cursor inside the window.
        if (_upgradeCursor < _upgradeScroll) _upgradeScroll = _upgradeCursor;
        if (_upgradeCursor >= _upgradeScroll + visible) _upgradeScroll = _upgradeCursor - visible + 1;

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
            var rowY = y + 88 + i * 52;
            var owned = !def.Repeatable && Upgrades.Owned(_meta, def.Id);
            var afford = Upgrades.CanAfford(_meta, def);
            if (i == _upgradeCursor)
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                sb.Draw(_renderer.Pixel, new Rectangle(x + 12, rowY - 6, w - 24, 46), new Color(45, 50, 70, 180));
                sb.End();
            }
            var nameCol = owned ? new Color(140, 220, 140) : afford ? Color.White : new Color(130, 132, 145);
            var name = def.Id == "rover" ? $"ROVER ({_meta.Rovers} ABOARD)" : def.Name.ToUpperInvariant();
            var cost = owned ? "INSTALLED" : CostLabel(def);
            _renderer.DrawText(name, new Vector2(x + 24, rowY), nameCol, 2);
            _renderer.DrawText(cost,
                new Vector2(x + w - 24 - _renderer.MeasureText(cost), rowY + 4),
                owned ? new Color(140, 220, 140) : afford ? new Color(255, 225, 140) : new Color(160, 120, 110));
            _renderer.DrawText(def.Desc.ToUpperInvariant(), new Vector2(x + 24, rowY + 22), new Color(150, 155, 175));
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

    /// <summary>"1 KONG SOUL + 4 GOLD + 6 IRON" — the kind prefix tells the player which
    /// boss to hunt for this line.</summary>
    private string CostLabel(UpgradeDef def)
    {
        var s = "";
        if (def.Souls > 0)
        {
            var kind = def.SoulKind is { } k
                ? TitanName(Enum.Parse<TitanKind>(k)).ToUpperInvariant() + " "
                : "";
            s = $"{def.Souls} {kind}SOUL{(def.Souls == 1 ? "" : "S")}";
        }
        foreach (var (id, n) in def.Mats)
            s += (s.Length > 0 ? " + " : "") + $"{n} {Tiles.ResourceLabel(id)}";
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
