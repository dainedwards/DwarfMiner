using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DwarfMiner.Rendering;
using DwarfMiner.Space;
using DwarfMiner.Systems;
using DwarfMiner.UI;
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
    private Texture2D _stationTex = null!;
    private Texture2D _stationSideTex = null!;
    private Texture2D _arrowTex = null!;

    /// <summary>Pixel-art sidearms drawn in the dwarf's grip, keyed by toolbelt item id.
    /// All art points +X; the draw call rotates to the aim and flips below the horizontal.</summary>
    private readonly Dictionary<string, Texture2D> _weaponTex = new();

    private void BuildWeaponTextures()
    {
        var pal = new Dictionary<char, Color>
        {
            ['.'] = Color.Transparent,
            ['S'] = new Color(185, 190, 205),  // steel
            ['d'] = new Color(115, 120, 138),  // dark steel
            ['D'] = new Color(80, 62, 40),     // wood/grip
            ['m'] = new Color(255, 220, 120),  // muzzle bright
            ['C'] = new Color(120, 220, 255),  // energy cyan
            ['G'] = new Color(120, 255, 160),  // mining beam green
            ['R'] = new Color(190, 60, 50),    // warhead red
        };
        Texture2D T(params string[] rows) => Renderer.BuildSprite(GraphicsDevice, rows, pal);

        _weaponTex["pistol"] = T(
            "SSSSSSm.",
            "SSSSSSS.",
            ".DD.....",
            ".DD.....");
        _weaponTex["machine_gun"] = T(
            "..ddddddddd.",
            "DDSSSSSSSSSm",
            ".DDSSd......",
            "..DDd.......");
        _weaponTex["laser"] = T(
            ".dSSSSSSd.",
            "DSCCCCCCSm",
            ".dSSSSSSd.",
            "..DD......");
        _weaponTex["laser_cannon"] = T(
            ".ddSSSSSSSSd.",
            "DSSCCCCCCCCSm",
            "DSSCCCCCCCCSm",
            ".ddSSSSSSSSd.",
            "...DD........");
        _weaponTex["rocket_launcher"] = T(
            "SSSSSSSSSSSSR",
            "d...........R",
            "SSSSSSSSSSSSR",
            "...DD........");
        _weaponTex["cannon"] = T(
            ".dSSSSSSSSSd.",
            "dSSSSSSSSSSSm",
            "dSSSSSSSSSSSm",
            ".dSSSSSSSSSd.",
            "..DDD........");
        _weaponTex["harpoon"] = T(
            "dddddddddSSm",
            "DSSSSSSSSSS.",
            ".DD.........");
        _weaponTex["mining_laser"] = T(
            ".dSSSSSSd.",
            "DSGGGGGGSm",
            ".dSSSSSSd.",
            "..DD......");
        // Swung tools — handle along +X with the head at the far end, vertically symmetric
        // so the swing draw can rotate through the full arc without any flip bookkeeping.
        // The pick head is a thin ")" arc bowed away from the handle with twin tips curling
        // back toward it — the classic pickaxe silhouette, not a solid slab of steel.
        _weaponTex["pickaxe"] = T(
            "......SS.",
            "........S",
            "DDDDDDDdS",
            "........S",
            "......SS.");
        _weaponTex["hammer"] = T(
            "......ddd",
            "......dSS",
            "DDDDDDdSS",
            "......dSS",
            "......ddd");
    }

    /// <summary>A solid triangle pointing +X, for the edge-of-screen nav arrows.</summary>
    private Texture2D BuildArrowTexture()
    {
        const int s = 15;
        var data = new Color[s * s];
        for (var y = 0; y < s; y++)
            for (var x = 0; x < s; x++)
                if (MathF.Abs(y - s / 2f + 0.5f) <= (s - 1 - x) * 0.5f)
                    data[y * s + x] = Color.White;
        var tex = new Texture2D(GraphicsDevice, s, s);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Edge-of-screen pointer toward an off-frame world position — a stubby wide
    /// chevron hugging the screen edge, with an optional small label floated just inside
    /// it. On-screen targets draw nothing (they speak for themselves).</summary>
    private void DrawEdgeArrow(Vector2 worldPos, Color col, string? label = null)
    {
        var screen = Vector2.Transform(worldPos, _camera.View);
        const int margin = 14;
        if (screen.X > margin && screen.X < VirtualWidth - margin
            && screen.Y > margin && screen.Y < VirtualHeight - margin)
            return;

        var centre = new Vector2(VirtualWidth / 2f, VirtualHeight / 2f);
        var dir = screen - centre;
        if (dir.LengthSquared() < 1f) return;
        dir.Normalize();
        var pos = new Vector2(
            MathHelper.Clamp(screen.X, margin, VirtualWidth - margin),
            MathHelper.Clamp(screen.Y, margin, VirtualHeight - margin));

        var sb = _renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_arrowTex, pos, null, col,
            MathF.Atan2(dir.Y, dir.X), new Vector2(7.5f, 7.5f),
            new Vector2(0.7f, 1.3f), SpriteEffects.None, 0f);
        sb.End();
        if (label is not null)
        {
            var lp = pos - dir * 24f;
            _renderer.DrawText(label,
                new Vector2(
                    MathHelper.Clamp(lp.X - _renderer.MeasureText(label) / 2f, 4f,
                        VirtualWidth - 4f - _renderer.MeasureText(label)),
                    MathHelper.Clamp(lp.Y - 4f, 4f, VirtualHeight - 16f)),
                col);
        }
    }

    /// <summary>Fix on the orbiting mothership — mining runs deep, the rendezvous drifts,
    /// and the escape climb needs the docking target at any range. Off-frame it's an edge
    /// arrow with a range readout; in frame during the ascent the label floats under the
    /// station itself, so the indicator is up no matter how far away the ship is.</summary>
    private void DrawStationIndicator()
    {
        var col = new Color(150, 220, 255);
        var label = $"MOTHERSHIP {(_run.StationPos - _run.Player.Position).Length() / Planet.TileSize:0}M";
        var screen = Vector2.Transform(_run.StationPos, _camera.View);
        const int margin = 14;
        var onScreen = screen.X > margin && screen.X < VirtualWidth - margin
                    && screen.Y > margin && screen.Y < VirtualHeight - margin;
        if (!onScreen)
        {
            DrawEdgeArrow(_run.StationPos, col, label);
            return;
        }
        // On-screen the station speaks for itself in normal play, but during the ascent
        // keep the label pinned to it — the docking target should never be ambiguous.
        if (!_ascending) return;
        var w = _renderer.MeasureText(label);
        _renderer.DrawText(label,
            new Vector2(
                MathHelper.Clamp(screen.X - w / 2f, 4f, VirtualWidth - 4f - w),
                MathHelper.Clamp(screen.Y + 42f, 4f, VirtualHeight - 16f)),
            col);
    }

    /// <summary>The Geo Scanner's HUD fixes: nearest fuel deposit, nearest signature ore,
    /// and the titan, each with its range in tiles. Only while the upgrade is owned and the
    /// dwarf is actually down on (or dropping to) the planet.</summary>
    private void DrawScannerArrows()
    {
        if (_orbiting || !Upgrades.Owned(_meta, "scanner")) return;
        string Range(Vector2 target) =>
            $"{(target - _run.Player.Position).Length() / Planet.TileSize:0}M";
        if (_scanFuel is { } fuel)
            DrawEdgeArrow(fuel, new Color(255, 170, 60), $"FUEL {Range(fuel)}");
        if (_scanOre is { } ore)
            DrawEdgeArrow(ore, Tiles.ResourceColor(_run.Def.ShipOre),
                $"{Tiles.ResourceLabel(_run.Def.ShipOre)} {Range(ore)}");
        if (_run.Titan.Health > 0)
            DrawEdgeArrow(_run.Titan.Position, new Color(255, 110, 90),
                $"TITAN {Range(_run.Titan.Position)}");
    }

    /// <summary>Background world builds keyed by planet id. The aim predictor keeps the
    /// likely destination baking from the moment the ship points at it, and finished builds
    /// stay cached through course changes — so atmosphere contact almost always lands on a
    /// world that's already built. Each bake carries a token that cancels the liquid
    /// pre-settle (the slow half): atmosphere entry fires it and takes the world as soon as
    /// generation itself is done. BuildSessionWorld touches only fresh objects, so the
    /// thread hop is safe; the dictionary itself is only touched on the update thread.</summary>
    private readonly Dictionary<string, (Task<Session> Task, CancellationTokenSource SettleCts)> _prefetch = new();
    /// <summary>A cached Session retains ~100 MB, so hold at most two: the current target
    /// plus whatever finished before the last course change.</summary>
    private const int PrefetchCap = 2;

    /// <summary>Start a background world build for this planet unless one is already cached
    /// or in flight. At the cap, the finished build farthest from the ship is evicted;
    /// in-flight builds can't be cancelled, so if everything is still baking, pass.</summary>
    private void EnsurePrefetch(PlanetDef def)
    {
        if (def.Id == "rift" && _space.RiftLocked) return;
        if (def.Airless && _space.VacSuitLocked) return;
        if (_prefetch.ContainsKey(def.Id)) return;
        if (_prefetch.Count >= PrefetchCap)
        {
            string? evict = null;
            var worst = -1f;
            foreach (var (id, bake) in _prefetch)
            {
                if (!bake.Task.IsCompleted) continue;
                var planet = _space.Planets.Find(sp => sp.Def.Id == id);
                var d = planet is null ? float.MaxValue : (planet.Pos - _space.ShipPos).Length();
                if (d > worst) { worst = d; evict = id; }
            }
            if (evict is null) return;
            _prefetch.Remove(evict);
        }
        var cts = new CancellationTokenSource();
        _prefetch[def.Id] = (Task.Run(() => BuildSessionWorld(def, cts.Token)), cts);
    }

    /// <summary>Disc previews rasterized from each planet's survey world, so the system
    /// view shows real terrain — mountain silhouettes, lakes, lava — not a flat disc.
    /// Worlds generate on a background task at boot; textures build lazily on the main
    /// thread once their world is ready.</summary>
    private readonly Dictionary<string, Texture2D> _planetPreview = new();
    private bool _previewBuiltThisFrame;
    private const int PreviewSize = 200;

    /// <summary>Rasterize a planet's survey world into a disc texture: each pixel samples
    /// the tile at the matching polar coordinate. Sky above the LOCAL terrain line stays
    /// transparent (real mountain silhouettes on the limb — and on the lumpy asteroid, the
    /// whole potato outline); sky inside the crust (caves) reads as dark rock so the disc
    /// doesn't look moth-eaten.</summary>
    private Texture2D BuildPlanetPreview(Planet world)
    {
        var data = new Color[PreviewSize * PreviewSize];
        var half = PreviewSize / 2f;
        var worldRadius = world.Radius * Planet.TileSize;
        var cave = new Color(24, 21, 26);
        for (var py = 0; py < PreviewSize; py++)
            for (var px = 0; px < PreviewSize; px++)
            {
                var dx = (px + 0.5f - half) / half;
                var dy = (py + 0.5f - half) / half;
                var rr = MathF.Sqrt(dx * dx + dy * dy);
                if (rr > 1f) continue;
                var pos = world.Center + new Vector2(dx, dy) * worldRadius;
                var (tx, ty) = world.WorldToTile(pos);
                var kind = world.Get(tx, ty);
                var aboveGround = rr * worldRadius
                    > world.SurfaceRadiusAt(pos) * Planet.TileSize - Planet.TileSize;
                data[py * PreviewSize + px] = kind == TileKind.Sky
                    ? (aboveGround ? Color.Transparent : cave)
                    : Tiles.BaseColor(kind);
            }
        var tex = new Texture2D(GraphicsDevice, PreviewSize, PreviewSize);
        tex.SetData(data);
        return tex;
    }

    /// <summary>Claim a prefetched Session for this planet, waiting out any remaining build
    /// time (still faster than restarting). Null when this world was never prefetched —
    /// the caller builds synchronously.</summary>
    private Session? TakePrefetchedSession(PlanetDef def)
    {
        if (!_prefetch.Remove(def.Id, out var bake)) return null;
        try { return bake.Task.GetAwaiter().GetResult(); }
        catch { return null; }   // background build died — rebuild on the main thread
    }

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
        _space.HullTier = Upgrades.Owned(_meta, "hull3") ? 3 : Upgrades.Owned(_meta, "hull2") ? 2 : 1;
        _space.HasShield = Upgrades.Owned(_meta, "shield");
        _space.ShieldTier = Upgrades.Owned(_meta, "shield2") ? 2 : 1;
        _space.FreeThrust = Upgrades.Owned(_meta, "voidcore");
        _space.VacSuitLocked = !Upgrades.Owned(_meta, "vacsuit");
    }

    /// <summary>Boot-time restore of the persisted mothership: park where you left it (with
    /// the hull you left with) or fall back to a planet for fresh installs.</summary>
    private void RestoreShipState()
    {
        ApplyShipTiers();
        _space.RiftLocked = _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded;
        if (!_meta.ShipStateSaved)
        {
            _space.ParkShipTrailing(Math.Min(_meta.PlanetsUnlocked, PlanetDefs.All.Length) - 1);
        }
        else
        {
            _space.ShipPos = new Vector2(_meta.ShipPosX, _meta.ShipPosY);
            _space.ShipHeading = _meta.ShipHeadingSave;
            if (_meta.ShipHull > 0) _space.Hull = Math.Min(_meta.ShipHull, _space.HullMax);

            // The snapshot may date from the moment of atmosphere entry, and the planets
            // re-rack to their boot angles regardless of where they'd orbited to — either
            // way the saved point can boot inside (or skimming) a planet, forcing an
            // atmosphere entry before its world build has finished. Any spawn closer than
            // the boot-park shell re-parks trailing the planet on its own orbit ring: far
            // enough out that the build kicked below wins the race back in, and clear of
            // every body's orbital sweep so an idle ship stays parked.
            for (var i = 0; i < _space.Planets.Count; i++)
            {
                var p = _space.Planets[i];
                if ((_space.ShipPos - p.Pos).Length() - p.BodyRadius >= SpaceSim.BootParkDistance)
                    continue;
                _space.ParkShipTrailing(i);
                break;
            }
        }

        // Start the nearest world building now, during the rest of boot — by the time the
        // player has oriented and burned back across the park distance, entry is instant.
        var (nearP, _) = _space.NearestPlanet();
        if (nearP is not null) EnsurePrefetch(nearP.Def);
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

        // F9 — developer menu, space edition: godmode grants instead of boss spawns. The
        // system keeps drifting behind it, same as the other overlays.
        if (_debugMenu.Open)
        {
            _debugMenu.Update(keys, _prevKeys);
            _space.Update(dt, 0f, thrust: false, brake: false);
            TickSpaceCameraAndBreach(dt);
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.F9))
        {
            _debugMenu.SetEntries(BuildSpaceDebugEntries());
            _debugMenu.Toggle();
            return;
        }

        // Overlays capture input while the system keeps drifting behind them; opening one
        // closes the other.
        if (Pressed(keys, _prevKeys, Keys.U)) { _upgradesOpen = !_upgradesOpen; _surveyOpen = false; }
        if (Pressed(keys, _prevKeys, Keys.M)) { _surveyOpen = !_surveyOpen; _upgradesOpen = false; }
        if (_upgradesOpen || _surveyOpen)
        {
            if (_upgradesOpen) UpdateUpgradeMenu(keys);
            else
            {
                if (Pressed(keys, _prevKeys, Keys.Escape)) _surveyOpen = false;
                UpdateStarMap(mouse);
            }
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

        _space.RiftLocked = _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded;
        TickSpaceCameraAndBreach(dt);

        // Keep the likely destination baking long before contact: the planet the flight
        // vector points at (a build's worth of lead even on a dead-ahead burn), plus the
        // nearest planet as a fallback for slow drifts. Finished builds stay cached, so a
        // mid-flight course change doesn't throw a built world away.
        var (nearP, surfDist) = _space.NearestPlanet();
        if (_space.AimedPlanet() is { } aimed) EnsurePrefetch(aimed.Def);
        if (nearP is not null && surfDist < 4200f) EnsurePrefetch(nearP.Def);

        // Flying into the upper atmosphere IS the transition — no prompt, no keypress. The
        // bearing you flew in on becomes the bearing you arrive above.
        if (_space.AtmosphereContact() is { } entry)
        {
            // Contact with a world that was never prefetched (a hard swerve at the last
            // second): kick the build now so the hold below overlaps it.
            EnsurePrefetch(entry.Def);
            // Entry never waits for the liquid pre-settle — cancel whatever settle time is
            // left and take the world as soon as generation itself is done (~a quarter
            // second even on the biggest worlds). The leftover settling runs live in the
            // orbit frames instead: a briefly heavier framerate from orbit beats any hold
            // here. With normal prefetch lead the settle already finished and this is moot.
            if (_prefetch.TryGetValue(entry.Def.Id, out var bake) && !bake.Task.IsCompleted)
            {
                bake.SettleCts.Cancel();
                _space.ShipVel *= MathF.Exp(-6f * dt);
                _toast = $"ENTERING {entry.Def.Name.ToUpperInvariant()} ATMOSPHERE";
                _toastTimer = 0.5f;
                return;
            }
            CaptureShipState();
            _meta.Save();
            var toShip = _space.ShipPos - entry.Pos;
            EnterOrbit(entry.Def, MathF.Atan2(toShip.Y, toShip.X));
            return;
        }

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

        // Storm-wall feedback when the locked Rift bounces the ship.
        if (nearP is { Def.Id: "rift" } && _space.RiftLocked && surfDist < 60f && _toastTimer <= 0f)
        {
            _toast = $"THE RIFT STORMS REPEL YOU - {PlanetDefs.WarpShardsNeeded - _meta.CoreShards.Count} MORE CORE SHARDS NEEDED";
            _toastTimer = 2.5f;
        }

        // Hard-vacuum feedback when the ship grinds against an airless rock without the suit.
        if (nearP is { Def.Airless: true } && _space.VacSuitLocked && surfDist < 60f && _toastTimer <= 0f)
        {
            _toast = "NO ATMOSPHERE TO ENTER - BUILD THE VAC SUIT AT THE FOUNDRY (U)";
            _toastTimer = 2.5f;
        }

        // Corona feedback while the sun chews the hull (the sim applies the burn itself).
        if (_space.ShipPos.Length() < SpaceSim.SunRadius + 100f && _toastTimer <= 0f)
        {
            _toast = "SOLAR CORONA - HULL BURNING, PULL AWAY";
            _toastTimer = 1.5f;
        }
    }

    /// <summary>Per-frame space camera easing, plus the hull-breach consequence: an
    /// emergency dock at the nearest charted world with the hull patched up. Losing the
    /// fight to the rocks costs you your position, not the game.</summary>
    private void TickSpaceCameraAndBreach(float dt)
    {
        // Approach zoom, No Man's Sky style: cruising shows the system; closing on a planet
        // pulls the camera onto the ship until the atmosphere fills the screen at contact.
        var (nearPlanet, dist) = _space.NearestPlanet();
        var zoomTarget = SpaceZoom;
        if (nearPlanet is not null && dist < 650f)
            zoomTarget = MathHelper.Lerp(2.4f, SpaceZoom, MathHelper.Clamp(dist / 650f, 0f, 1f));
        _spaceZoom = MathHelper.Lerp(_spaceZoom, zoomTarget, MathHelper.Clamp(dt * 2.2f, 0f, 1f));
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

    /// <summary>The space screen's developer menu rows — godmode grants for testing the
    /// foundry, loadouts, and the warp gate without grinding. Mirrors the planet-side
    /// boss-spawn menu on the same F9 key.</summary>
    private DebugMenu.Entry[] BuildSpaceDebugEntries() => new DebugMenu.Entry[]
    {
        new("GODMODE — everything below at once", GrantGodmode),
        new("Fill cargo hold (9999 of every resource)", GrantAllMaterials),
        new("Secure all core shards (warp ready)", GrantCoreShards),
        new("Top up fuel + rovers", GrantFuelAndRovers),
    };

    /// <summary>Debug godmode: every resource in the hold, every titan soul banked, every
    /// core shard secured (warp-ready), and fuel/rovers/hull topped off — effectively
    /// unlimited materials. Re-run it from the menu whenever the pile runs low.</summary>
    private void GrantGodmode()
    {
        GrantAllMaterials();
        GrantCoreShards();
        GrantFuelAndRovers();
        foreach (var kind in Enum.GetNames<TitanKind>())
            _meta.TitanSouls[kind] = 99;
        _space.Hull = _space.HullMax;
        _meta.Save();
        _toast = "GODMODE - HOLD STOCKED, SHARDS SECURED, SOULS BANKED";
        _toastTimer = 3f;
    }

    /// <summary>Stock the mothership's hold with 9999 of every resource id the game knows —
    /// the inventory catalogue, the harvest drops, and the refined pure_ metals the foundry
    /// bills in — so nothing is ever short.</summary>
    private void GrantAllMaterials()
    {
        foreach (var id in Tiles.ResourceOrder)
            _meta.ShipCargo[id] = 9999;
        foreach (var id in new[] { "meat", "hide", "chitin" })
            _meta.ShipCargo[id] = 9999;
        foreach (var id in new[] { "iron", "coal", "silver", "gold", "platinum" })
            _meta.ShipCargo["pure_" + id] = 9999;
        _meta.Save();
        _toast = "CARGO HOLD STOCKED - 9999 OF EVERYTHING";
        _toastTimer = 3f;
    }

    /// <summary>Secure every planet's core shard — the warp-drive material — so the Rift
    /// unlocks and J warps immediately.</summary>
    private void GrantCoreShards()
    {
        foreach (var def in PlanetDefs.All)
            if (def.Id is not ("rift" or "debug" or "hollow") && !_meta.CoreShards.Contains(def.Id))
                _meta.CoreShards.Add(def.Id);
        _meta.Save();
        _toast = $"ALL CORE SHARDS SECURED ({_meta.CoreShards.Count}/{PlanetDefs.WarpShardsNeeded}) - WARP READY";
        _toastTimer = 3f;
    }

    private void GrantFuelAndRovers()
    {
        _meta.MotherFuel = 9999;
        _meta.Rovers = 99;
        _meta.Save();
        _toast = "TANK AND ROVER BAY FULL";
        _toastTimer = 3f;
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
                _toast = "NOT ENOUGH SOULS + CARGO";
            }
            _toastTimer = 2.5f;
        }
    }

    private void DrawSpace()
    {
        GraphicsDevice.Clear(new Color(4, 5, 12));
        var sb = _renderer.Batch;
        var view = _camera.View;
        _previewBuiltThisFrame = false;

        // Deep background: soft nebula blobs on the slowest parallax, then a three-layer
        // starfield with a scattering of tinted stars — hashed screen positions scrolled
        // against the camera and wrapped, so flying reads as motion everywhere.
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < 4; i++)
        {
            var h = (i * 2654435761u) & 0x7fffffff;
            var nx = Mod((h >> 6) % VirtualWidth - _camera.Target.X * 0.015f, VirtualWidth);
            var ny = Mod((h >> 16) % VirtualHeight - _camera.Target.Y * 0.015f, VirtualHeight);
            var nebCol = (i & 1) == 0 ? new Color(90, 60, 140) : new Color(40, 90, 120);
            for (var ring = 4; ring >= 1; ring--)
                FillCircleWorld(sb, new Vector2(nx, ny), 36f * ring,
                    nebCol * (0.016f * (5 - ring)));
        }
        for (var layer = 0; layer < 3; layer++)
        {
            var factor = layer switch { 0 => 0.03f, 1 => 0.07f, _ => 0.13f };
            var n = layer switch { 0 => 120, 1 => 90, _ => 55 };
            for (var i = 0; i < n; i++)
            {
                var h = ((i + layer * 977) * 1013904223 + 1664525) & 0x7fffffff;
                var x = Mod((h >> 7) % VirtualWidth - _camera.Target.X * factor, VirtualWidth);
                var y = Mod((h >> 17) % VirtualHeight - _camera.Target.Y * factor, VirtualHeight);
                var tw = MathF.Sin(_totalTime * (0.6f + (h & 3) * 0.5f) + i) * 0.5f + 0.5f;
                var bright = (0.2f + tw * 0.5f) * (0.55f + layer * 0.22f);
                var size = (h & 15) == 0 ? 2 : 1;
                // Most stars are white; a scattering runs warm or cool.
                var starCol = (h & 31) switch
                {
                    < 3 => new Color(255, 200, 150),
                    < 6 => new Color(150, 190, 255),
                    _ => new Color(200, 210, 235),
                };
                sb.Draw(_renderer.Pixel, new Rectangle((int)x, (int)y, size, size), starCol * bright);
            }
        }
        sb.End();

        // World pass — everything in space coordinates under the camera transform.
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: view);

        // Orbit rings: dotted circles — sun-centred for planets, parent-centred for moons
        // (a moon's ring rides along with its host).
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var col = new Color(70, 76, 100);
            var centre = p.OrbitCentre;
            var dots = Math.Max(20, (int)(p.OrbitRadius / 28f));
            for (var d = 0; d < dots; d++)
            {
                var a = d * MathHelper.TwoPi / dots;
                var pos = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * p.OrbitRadius;
                sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 4, 4), col);
            }
        }

        // The outer asteroid belt: a hashed scatter of dim rock-flecks along the Hollow's
        // annulus, drifting slowly prograde. Pure dressing — the real (collidable) belt
        // rocks are maintained by the sim's spawner — but it makes the ring readable from
        // clear across the system, so the "edge of the map" has a visible landmark.
        for (var i = 0; i < 900; i++)
        {
            var h = (uint)(i * 2246822519u + 3266489917u);
            var a = (h & 0xffff) / 65535f * MathHelper.TwoPi + _totalTime * 0.0035f;
            var radJit = (((h >> 16) & 0xff) / 255f + ((h >> 24) & 0xff) / 255f - 1f)
                         * SpaceSim.BeltHalfWidth;
            var pos = new Vector2(MathF.Cos(a), MathF.Sin(a)) * (SpaceSim.BeltOrbitRadius + radJit);
            var s = 2 + (int)(h % 3);
            sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - s / 2, (int)pos.Y - s / 2, s, s),
                new Color(120, 112, 102) * (0.35f + (h & 7) * 0.05f));
        }

        // The sun: layered corona with a slow breathing flicker, rotating flare rays, then
        // the body with a hot core.
        var flick = 1f + MathF.Sin(_totalTime * 1.7f) * 0.04f;
        FillCircleWorld(sb, Vector2.Zero, (SpaceSim.SunRadius + 150f) * flick, new Color(255, 160, 50, 14));
        FillCircleWorld(sb, Vector2.Zero, (SpaceSim.SunRadius + 70f) * flick, new Color(255, 180, 60, 30));
        for (var ray = 0; ray < 10; ray++)
        {
            var ra = _totalTime * 0.08f + ray * MathHelper.TwoPi / 10f;
            var rd = new Vector2(MathF.Cos(ra), MathF.Sin(ra));
            var len = 120f + MathF.Sin(_totalTime * 1.3f + ray * 1.7f) * 45f;
            for (var seg = 0f; seg < len; seg += 10f)
            {
                var pos = rd * (SpaceSim.SunRadius + 14f + seg);
                var fade = 1f - seg / len;
                sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 3, (int)pos.Y - 3, 6, 6),
                    new Color(255, 190, 90) * (0.28f * fade));
            }
        }
        FillCircleWorld(sb, Vector2.Zero, SpaceSim.SunRadius, new Color(255, 190, 80));
        FillCircleWorld(sb, Vector2.Zero, SpaceSim.SunRadius * 0.7f, new Color(255, 235, 170));
        FillCircleWorld(sb, Vector2.Zero, SpaceSim.SunRadius * 0.35f, new Color(255, 250, 225));

        // Planets — atmosphere halo, body, drifting surface blotches, a two-step terminator
        // (shadow faces away from the sun), and a polar highlight.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var body = p.Def.MapColor;
            var accent = p.Def.MapAccent;
            var c = p.Pos;
            var sunward = c.LengthSquared() > 1f ? Vector2.Normalize(c) : Vector2.UnitY;

            // Atmosphere: two translucent accent halos (the Rift's reads as a red storm).
            // Airless rock (the Hollow) gets none — bare regolith against black space.
            if (!p.Def.Airless)
            {
                FillCircleWorld(sb, c, p.BodyRadius + 16f, accent * 0.10f);
                FillCircleWorld(sb, c, p.BodyRadius + 7f, accent * 0.16f);
            }

            // The disc is the planet's real (survey) terrain once its preview is ready —
            // mountains on the limb, lakes and lava where they'll actually be. Until the
            // background world-gen delivers, a flat disc stands in. At most one preview
            // rasterizes per frame so six never spike the same frame.
            if (!_planetPreview.TryGetValue(p.Def.Id, out var preview)
                && !_previewBuiltThisFrame && Survey.TryWorld(p.Def) is { } world)
            {
                preview = BuildPlanetPreview(world);
                _planetPreview[p.Def.Id] = preview;
                _previewBuiltThisFrame = true;
            }
            if (preview is not null)
            {
                sb.Draw(preview, c, null, Color.White, _totalTime * 0.012f,
                    new Vector2(PreviewSize / 2f, PreviewSize / 2f),
                    p.BodyRadius * 2f / PreviewSize, SpriteEffects.None, 0f);
            }
            else
            {
                FillCircleWorld(sb, c, p.BodyRadius, body);
            }

            // Two-step terminator for a softer shadow gradient. (No fake polar highlight —
            // the real terrain carries the detail now.)
            FillCircleWorld(sb, c + sunward * (p.BodyRadius * 0.30f), p.BodyRadius * 0.76f,
                new Color(0, 0, 0, 60));
            FillCircleWorld(sb, c + sunward * (p.BodyRadius * 0.48f), p.BodyRadius * 0.56f,
                new Color(0, 0, 0, 80));
            if (preview is null)
                FillCircleWorld(sb, c - sunward * (p.BodyRadius * 0.3f), p.BodyRadius * 0.30f, accent * 0.8f);
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

        // Deflector shield: a faint cyan halo while charged, so its readiness reads at a
        // glance (it vanishes for the 8s recharge after eating a hit).
        if (_space.ShieldReady)
        {
            var ring = SpaceSim.ShipRadius + 14f + MathF.Sin(_totalTime * 3f) * 2f;
            const int dots = 26;
            for (var d = 0; d < dots; d++)
            {
                var a = d * MathHelper.TwoPi / dots + _totalTime * 0.6f;
                var pos = _space.ShipPos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * ring;
                sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 2, (int)pos.Y - 2, 4, 4),
                    new Color(120, 220, 255) * 0.7f);
            }
        }

        // The mothership — a ring station spinning slowly (its facing doesn't read from the
        // hull, so a nose lamp marks the thrust axis). Recent asteroid hits flash it red for
        // the invulnerability window.
        var tint = _space.HitTimer > 0f && MathF.Sin(_totalTime * 40f) > 0f
            ? new Color(255, 120, 110) : Color.White;
        sb.Draw(_stationTex, _space.ShipPos, null, tint,
            _totalTime * 0.12f,
            new Vector2(_stationTex.Width / 2f, _stationTex.Height / 2f), 1.5f, SpriteEffects.None, 0f);
        // Heading lamp: a bright dot on the ring along the nose vector.
        var lamp = _space.ShipPos + _space.ShipDir * (SpaceSim.ShipRadius - 3f);
        FillCircleWorld(sb, lamp, 4f, new Color(255, 240, 170));

        if (_muzzle > 0f)
        {
            var nose = _space.ShipPos + _space.ShipDir * (SpaceSim.ShipRadius + 8f);
            FillCircleWorld(sb, nose, 7f, new Color(255, 240, 170) * 0.9f);
        }

        sb.End();

        // Text pass — screen space. On-screen planets get a name + range label; off-screen
        // ones get an edge arrow with name + range, so every world is always navigable.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var name = p.Def.Name.ToUpperInvariant();
            var warpLocked = p.Def.Id == "rift" && _meta.CoreShards.Count < PlanetDefs.WarpShardsNeeded;
            var suitLocked = p.Def.Airless && _space.VacSuitLocked;
            if (warpLocked) name += " [WARP LOCKED]";
            if (suitLocked) name += " [VAC SUIT REQUIRED]";
            var col = warpLocked ? new Color(255, 110, 90)
                : suitLocked ? new Color(255, 190, 90) : Color.White;
            var range = MathF.Max(0f, (p.Pos - _space.ShipPos).Length() - p.BodyRadius);
            var rangeLabel = $"{range / 10f:0} KM";

            var discCentre = Vector2.Transform(p.Pos, view);
            var discR = p.BodyRadius * _camera.Zoom;
            var onScreen = discCentre.X > -discR && discCentre.X < VirtualWidth + discR
                        && discCentre.Y > -discR && discCentre.Y < VirtualHeight + discR;
            if (onScreen)
            {
                var screen = Vector2.Transform(p.Pos + new Vector2(0f, -(p.BodyRadius + 46f)), view);
                _renderer.DrawText(name,
                    new Vector2(screen.X - _renderer.MeasureText(name, 2) / 2f, screen.Y), col, 2);
                _renderer.DrawText(rangeLabel,
                    new Vector2(screen.X - _renderer.MeasureText(rangeLabel) / 2f, screen.Y - 16f),
                    new Color(150, 155, 175));
                if (_meta.PlanetsEscaped.Contains(p.Def.Id))
                {
                    var esc = Vector2.Transform(p.Pos + new Vector2(0f, p.BodyRadius + 30f), view);
                    _renderer.DrawText("ESCAPED",
                        new Vector2(esc.X - _renderer.MeasureText("ESCAPED") / 2f, esc.Y),
                        new Color(140, 220, 140));
                }
            }
            else
            {
                const int margin = 42;
                var centre = new Vector2(VirtualWidth / 2f, VirtualHeight / 2f);
                var dir = discCentre - centre;
                if (dir.LengthSquared() < 1f) continue;
                dir.Normalize();
                var pos = new Vector2(
                    MathHelper.Clamp(discCentre.X, margin, VirtualWidth - margin),
                    MathHelper.Clamp(discCentre.Y, margin, VirtualHeight - margin));
                sb.Begin(samplerState: SamplerState.PointClamp);
                sb.Draw(_arrowTex, pos, null, warpLocked ? new Color(255, 110, 90) : p.Def.MapAccent,
                    MathF.Atan2(dir.Y, dir.X), new Vector2(7.5f, 7.5f), 1.4f, SpriteEffects.None, 0f);
                sb.End();
                var label = $"{name} {rangeLabel}";
                var labelPos = pos - dir * 30f;
                _renderer.DrawText(label,
                    new Vector2(
                        MathHelper.Clamp(labelPos.X - _renderer.MeasureText(label) / 2f, 6f,
                            VirtualWidth - 6f - _renderer.MeasureText(label)),
                        MathHelper.Clamp(labelPos.Y - 4f, 6f, VirtualHeight - 20f)),
                    col);
            }
        }

        // Approach readout — informational only: entry happens by flying in, not a keypress.
        var (approaching, approachDist) = _space.NearestPlanet();
        if (approaching is not null && approachDist < 650f)
        {
            var riftLocked = approaching.Def.Id == "rift" && _space.RiftLocked;
            var suitLocked = approaching.Def.Airless && _space.VacSuitLocked;
            var line1 = riftLocked
                ? "STORM WALL AHEAD - THE RIFT IS WARP-LOCKED"
                : suitLocked
                ? "AIRLESS ROCK AHEAD - THE VAC SUIT IS REQUIRED TO LAND"
                : $"ENTERING {approaching.Def.Name.ToUpperInvariant()} APPROACH - FLY IN TO MAKE ORBIT";
            _renderer.DrawText(line1,
                new Vector2((VirtualWidth - _renderer.MeasureText(line1, 2)) / 2f, VirtualHeight - 140),
                riftLocked ? new Color(255, 110, 90)
                    : suitLocked ? new Color(255, 190, 90) : new Color(255, 225, 140), 2);
            var line2 = $"{approaching.Def.Tagline.ToUpperInvariant()}   NAV CORE: {approaching.Def.ShipOreCount} {Tiles.ResourceLabel(approaching.Def.ShipOre)}";
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

        var controls = "A/D TURN   W THRUST   S BRAKE   SPACE FIRE   U FOUNDRY   M STAR MAP   J WARP";
        _renderer.DrawText(controls,
            new Vector2((VirtualWidth - _renderer.MeasureText(controls)) / 2f, VirtualHeight - 58),
            new Color(150, 155, 175));
        var meta = $"ESCAPES {_meta.Escapes}   TITAN KILLS {_meta.TitansDefeated}   DEEPEST {_meta.DeepestDepth}   DEATHS {_meta.Deaths}";
        _renderer.DrawText(meta,
            new Vector2((VirtualWidth - _renderer.MeasureText(meta)) / 2f, VirtualHeight - 34),
            new Color(120, 125, 145));

        if (_upgradesOpen) DrawUpgradeMenu(sb);
        if (_surveyOpen) DrawStarMap(sb);
        if (_debugMenu.Open) _debugMenu.Draw(_renderer, VirtualWidth, VirtualHeight);
        DrawTransitionFlash();
    }

    // ── The M-key star map ──────────────────────────────────────────────────
    // A top-down chart of the whole system: the sun (a marked hazard) at the centre, every
    // orbit as a dotted ring, every planet at its live position, and the mothership marker.
    // Hovering a body opens the long-range survey tooltip — ore deposits (see Space.Survey
    // for why the counts are approximate), the titan and its soul kind, and the hazard
    // manifest read straight off the PlanetDef. In debug mode right-click warps the ship.

    /// <summary>Chart hover state, written by UpdateStarMap and read by DrawStarMap.</summary>
    private int _mapHoverPlanet = -1;
    private bool _mapHoverSun;

    /// <summary>DM_SURVEY's value when it names a body ("sun" or a planet id) — the forced-
    /// hover screenshot hook. "1" (the plain open-at-boot switch) resolves to null.</summary>
    private static readonly string? SurveyHoverHook =
        Environment.GetEnvironmentVariable("DM_SURVEY") is { Length: > 1 } s ? s : null;

    /// <summary>Pixel radius of the chart (the outermost orbit ring).</summary>
    private const float StarMapRadius = VirtualHeight / 2f - 84f;
    private static Vector2 StarMapCentre => new(VirtualWidth / 2f, VirtualHeight / 2f + 10f);

    /// <summary>The largest orbit the chart must contain (with a little breathing room).</summary>
    private float MapMaxOrbit()
    {
        var max = SpaceSim.SunRadius;
        foreach (var p in _space.Planets) max = MathF.Max(max, p.OrbitRadius);
        return max * 1.06f;
    }

    /// <summary>Projected chart radius for a space distance from the sun. Radial sqrt
    /// compression: the inner orbits spread out readably while the far-flung Rift still
    /// fits the panel. Angles pass through untouched, so orbits stay circles.</summary>
    private float MapProjectR(float spaceR) =>
        StarMapRadius * MathF.Sqrt(MathHelper.Clamp(spaceR / MapMaxOrbit(), 0f, 1f));

    private Vector2 MapProject(Vector2 spacePos)
    {
        var len = spacePos.Length();
        if (len < 1f) return StarMapCentre;
        return StarMapCentre + spacePos / len * MapProjectR(len);
    }

    /// <summary>Chart point back to space coordinates — the debug warp's landing math.</summary>
    private Vector2 MapUnproject(Vector2 screenPos)
    {
        var d = screenPos - StarMapCentre;
        var len = d.Length();
        if (len < 1f) return Vector2.Zero;
        var frac = MathF.Min(len / StarMapRadius, 1f);
        return d / len * (frac * frac * MapMaxOrbit());
    }

    /// <summary>Marker size on the chart — tracks the real body radius, clamped readable.</summary>
    private static float MapMarkerRadius(SpacePlanet p) =>
        MathHelper.Clamp(p.BodyRadius * 0.06f, 5f, 15f);

    private const float MapSunMarker = 16f;

    /// <summary>Star-map input: hover detection for the survey tooltip, and — debug mode
    /// only — a right-click warp that teleports the mothership across the system (onto a
    /// hovered planet's parking spot, or to the clicked point in open space).</summary>
    private void UpdateStarMap(MouseState mouse)
    {
        var m = new Vector2(mouse.X, mouse.Y);
        _mapHoverPlanet = -1;
        _mapHoverSun = false;
        var bestD = float.MaxValue;
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var d = (MapProject(_space.Planets[i].Pos) - m).Length();
            if (d < MapMarkerRadius(_space.Planets[i]) + 8f && d < bestD)
            {
                bestD = d;
                _mapHoverPlanet = i;
            }
        }
        if (_mapHoverPlanet < 0 && (StarMapCentre - m).Length() < MapSunMarker + 8f)
            _mapHoverSun = true;

        // DM_SURVEY=<planet id|sun> forces that body hovered (on top of opening the map at
        // boot), so tooling can screenshot the tooltip without mouse access. It overrides
        // the real mouse — headless tooling can't move the cursor off a marker it happens
        // to be parked on.
        if (SurveyHoverHook is { } forced)
        {
            var idx = _space.Planets.FindIndex(p => p.Def.Id == forced);
            if (forced == "sun" || idx >= 0)
            {
                _mapHoverSun = forced == "sun";
                _mapHoverPlanet = idx;
            }
        }

        if (!PlanetDefs.DebugMode) return;
        if (mouse.RightButton != ButtonState.Pressed
            || _prevMouse.RightButton == ButtonState.Pressed) return;
        if (_mapHoverPlanet >= 0)
        {
            // Park at the planet's standard spot (outside atmosphere-entry range), same as
            // launching off it — warping never force-feeds an entry.
            _space.PlaceShipAt(_mapHoverPlanet);
            _toast = $"DEBUG WARP - {_space.Planets[_mapHoverPlanet].Def.Name.ToUpperInvariant()}";
        }
        else
        {
            // Open-space warp to the clicked point, held outside the corona: the sun is a
            // hazard, not a destination.
            var target = MapUnproject(m);
            var minSun = SpaceSim.SunRadius + 110f;
            if (target.Length() < minSun)
                target = (target.LengthSquared() > 1f ? Vector2.Normalize(target) : -Vector2.UnitY) * minSun;
            _space.ShipPos = target;
            _space.ShipVel = Vector2.Zero;
            _toast = "DEBUG WARP";
        }
        _space.Asteroids.Clear();
        _spaceZoom = SpaceZoom;
        _camera.SnapTo(_space.ShipPos, 0f);
        _sfx.Play("launch", 0.6f, pitch: 0.5f);
        _toastTimer = 2.5f;
    }

    /// <summary>Ores whose deposits read as RARE FINDS in the survey tooltip. Gold and
    /// silver belong here now — their veins only exist on worlds whose def charts them, so
    /// this line is the prospecting map for where to mine them.</summary>
    private static readonly HashSet<string> RareOres = new()
        { "CRYSTAL", "RUBY", "SAPPHIRE", "EMERALD", "DIAMOND", "VOIDSTONE", "GOLD", "SILVER" };

    /// <summary>The hazard manifest for a world, read off the same PlanetDef knobs that arm
    /// each danger in worldgen and the ambient director.</summary>
    private static List<string> HazardsOf(PlanetDef def)
    {
        var list = new List<string>();
        if (def.Airless) list.Add("NO ATMOSPHERE - VAC SUIT ONLY");
        if (def.GravityScale < 0.8f) list.Add("LOW GRAVITY");
        if (def.LavaFillFrac >= 0.5f) list.Add("MAGMA SURGES");
        if (def.Volcanoes > 0) list.Add(def.VolcanoAcid ? "ACID VOLCANOES" : "VOLCANOES");
        if (def.SeedsGas) list.Add("TOXIC GAS");
        if (def.SeedsAcid) list.Add("ACID POCKETS");
        if (def.AcidPools > 0) list.Add("ACID POOLS");
        if (def.AcidRain) list.Add("ACID RAIN");
        if (def.QuakeScale <= 0.6f) list.Add("QUAKES");
        if (def.OxygenDrainScale >= 1.3f) list.Add("THIN AIR + METEORS");
        if (def.SurfaceTile == TileKind.Snow) list.Add("BLIZZARDS");
        if (def.CaveSpawnCap >= 20) list.Add("SWARMING CAVES");
        if (def.LizardCities > 0) list.Add("LIZARDMAN WARRENS");
        return list;
    }

    /// <summary>The star map itself: dim the live scene, then chart orbits, sun, planets,
    /// and the mothership; finish with the hover tooltip. First hover of a planet may
    /// generate its survey world (a beat of hitch) if the boot warm-up hasn't reached it.</summary>
    private void DrawStarMap(SpriteBatch sb)
    {
        var centre = StarMapCentre;
        var dotCol = new Color(70, 76, 100);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight),
            new Color(4, 6, 14, 222));

        // Orbit rings — dotted, one per world. Moons get a small dotted halo around their
        // parent's chart marker instead of a sun ring (the sqrt-compressed projection can't
        // draw a true off-centre circle, and the halo reads exactly right at chart scale).
        foreach (var p in _space.Planets)
        {
            if (p.Def.MoonOf is not null)
            {
                var parent = _space.Planets.Find(q => q.Def.Id == p.Def.MoonOf);
                if (parent is null) continue;
                var pc = MapProject(parent.Pos);
                var hr = MapMarkerRadius(parent) + 9f;
                for (var d = 0; d < 14; d++)
                {
                    var a = d * MathHelper.TwoPi / 14;
                    var pos = pc + new Vector2(MathF.Cos(a), MathF.Sin(a)) * hr;
                    sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), dotCol);
                }
                continue;
            }
            var rr = MapProjectR(p.OrbitRadius);
            var dots = Math.Max(28, (int)(rr * 0.5f));
            for (var d = 0; d < dots; d++)
            {
                var a = d * MathHelper.TwoPi / dots;
                var pos = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
                sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2), dotCol);
            }
        }

        // The outer belt on the chart: a grainy dust band around the Hollow's orbit.
        var beltR = MapProjectR(SpaceSim.BeltOrbitRadius);
        var beltSpread = MapProjectR(SpaceSim.BeltOrbitRadius + SpaceSim.BeltHalfWidth) - beltR;
        for (var i = 0; i < 170; i++)
        {
            var h = (uint)(i * 2654435761u + 97u);
            var a = (h & 0xffff) / 65535f * MathHelper.TwoPi;
            var rr = beltR + (((h >> 16) & 0xff) / 255f * 2f - 1f) * beltSpread;
            var pos = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr;
            sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2),
                new Color(150, 140, 125) * 0.5f);
        }

        // The corona no-go ring: the radius inside which the sun starts burning the hull.
        var burnR = MapProjectR(SpaceSim.SunRadius + 70f) + 6f;
        for (var d = 0; d < 40; d++)
        {
            var a = d * MathHelper.TwoPi / 40 + _totalTime * 0.25f;
            var pos = centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * burnR;
            sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2),
                new Color(255, 110, 70) * 0.55f);
        }

        // The sun, breathing slightly like the live one.
        var flick = 1f + MathF.Sin(_totalTime * 1.7f) * 0.05f;
        FillCircleWorld(sb, centre, MapSunMarker + 6f * flick, new Color(255, 170, 60, 40));
        FillCircleWorld(sb, centre, MapSunMarker, new Color(255, 190, 80));
        FillCircleWorld(sb, centre, MapSunMarker * 0.55f, new Color(255, 245, 200));

        // Planets at their live orbital positions.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var pos = MapProject(p.Pos);
            var r = MapMarkerRadius(p);
            if (i == _mapHoverPlanet)
                FillCircleWorld(sb, pos, r + 4f, p.Def.MapAccent * 0.5f);
            FillCircleWorld(sb, pos, r, p.Def.MapColor);
            FillCircleWorld(sb, pos - new Vector2(r * 0.3f, r * 0.3f), r * 0.35f, p.Def.MapAccent * 0.8f);
        }

        // The mothership: heading arrow with a slow-pulsing halo ring.
        var ship = MapProject(_space.ShipPos);
        var pulse = MapMarkerRadius(_space.Planets[0]) + 6f + MathF.Sin(_totalTime * 3f) * 2f;
        for (var d = 0; d < 18; d++)
        {
            var a = d * MathHelper.TwoPi / 18;
            var pos = ship + new Vector2(MathF.Cos(a), MathF.Sin(a)) * pulse;
            sb.Draw(_renderer.Pixel, new Rectangle((int)pos.X - 1, (int)pos.Y - 1, 2, 2),
                new Color(150, 220, 255) * 0.8f);
        }
        sb.Draw(_arrowTex, ship, null, Color.White, _space.ShipHeading,
            new Vector2(7.5f, 7.5f), 1.0f, SpriteEffects.None, 0f);
        sb.End();

        _renderer.DrawText("YOU",
            new Vector2(ship.X - _renderer.MeasureText("YOU") / 2f, ship.Y + pulse + 4f),
            new Color(150, 220, 255));

        // Labels — name under every world, sun caption, title, and the footer hints.
        for (var i = 0; i < _space.Planets.Count; i++)
        {
            var p = _space.Planets[i];
            var pos = MapProject(p.Pos);
            var warpLocked = p.Def.Id == "rift" && _space.RiftLocked;
            var name = p.Def.Name.ToUpperInvariant();
            _renderer.DrawText(name,
                new Vector2(pos.X - _renderer.MeasureText(name) / 2f, pos.Y + MapMarkerRadius(p) + 5f),
                warpLocked ? new Color(255, 110, 90)
                    : i == _mapHoverPlanet ? Color.White : new Color(185, 190, 210));
        }
        _renderer.DrawText("SUN",
            new Vector2(centre.X - _renderer.MeasureText("SUN") / 2f, centre.Y + MapSunMarker + 8f),
            new Color(255, 190, 80));

        _renderer.DrawText("SYSTEM STAR MAP",
            new Vector2((VirtualWidth - _renderer.MeasureText("SYSTEM STAR MAP", 3)) / 2f, 18),
            Color.White, 3);
        var footer = "HOVER A WORLD FOR SURVEY DETAIL   M/ESC CLOSE"
                   + (PlanetDefs.DebugMode ? "   RIGHT CLICK: DEBUG WARP" : "");
        _renderer.DrawText(footer,
            new Vector2((VirtualWidth - _renderer.MeasureText(footer)) / 2f, VirtualHeight - 26),
            new Color(150, 155, 175));

        if (_mapHoverPlanet >= 0) DrawMapPlanetTooltip(sb, _space.Planets[_mapHoverPlanet]);
        else if (_mapHoverSun) DrawMapSunTooltip(sb);
    }

    /// <summary>Greedy word-wrap for tooltip item runs: "PREFIX A 1.2K   B 900" split across
    /// as many lines as needed to stay under maxW pixels.</summary>
    private void AddWrapped(List<(string text, Color col, int scale)> lines, string prefix,
        IEnumerable<string> items, Color col, int maxW)
    {
        var line = prefix;
        var any = false;
        foreach (var item in items)
        {
            any = true;
            var candidate = line.Length == 0 ? "  " + item : line + "   " + item;
            if (line.Length > 0 && _renderer.MeasureText(candidate) > maxW)
            {
                lines.Add((line, col, 1));
                line = "  " + item;
            }
            else line = candidate;
        }
        if (any) lines.Add((line, col, 1));
    }

    /// <summary>The hover tooltip for a world: survey deposits (rare finds split out), the
    /// titan with its soul kind and banked count, hazards, shard status, and range.</summary>
    private void DrawMapPlanetTooltip(SpriteBatch sb, SpacePlanet p)
    {
        const int maxW = 470;
        var def = p.Def;
        var grey = new Color(150, 155, 175);
        var lines = new List<(string text, Color col, int scale)>
        {
            (def.Name.ToUpperInvariant()
                + (def.Id == "rift" && _space.RiftLocked ? "  [WARP LOCKED]" : ""), def.MapAccent, 2),
            (def.Tagline.ToUpperInvariant(), grey, 1),
            ($"RANGE {MathF.Max(0f, (p.Pos - _space.ShipPos).Length() - p.BodyRadius) / 10f:0} KM", grey, 1),
        };
        if (def.MoonOf is { } hostId)
            lines.Add(($"MOON OF {PlanetDefs.ById(hostId).Name.ToUpperInvariant()}"
                + (def.Airless ? "  -  AIRLESS" : "  -  HOLDS AN ATMOSPHERE"),
                new Color(200, 205, 220), 1));

        // Titan census: pooled worlds roll a fresh kaiju per visit, so no name ahead of time.
        if (def.TitanPool is { Length: > 0 })
            lines.Add(("TITAN: UNSTABLE - ROLLS EACH VISIT", new Color(220, 140, 220), 1));
        else
        {
            var souls = _meta.SoulsOf(def.Titan.ToString());
            lines.Add(($"TITAN: {TitanName(def.Titan).ToUpperInvariant()}"
                    + $"  ({TitanName(def.Titan).ToUpperInvariant()} SOUL{(souls > 0 ? $" - {souls} BANKED" : "")})",
                souls > 0 ? new Color(140, 220, 140) : new Color(200, 160, 120), 1));
        }

        // Deposits, biggest first, rare gems split onto their own line.
        var common = new List<string>();
        var rare = new List<string>();
        foreach (var (label, n) in Survey.For(def, 12))
            (RareOres.Contains(label) ? rare : common).Add($"{label} {FormatCount(n)}");
        if (common.Count > 0) AddWrapped(lines, "MATERIALS: ", common, new Color(200, 205, 220), maxW);
        if (rare.Count > 0) AddWrapped(lines, "RARE FINDS: ", rare, new Color(150, 230, 255), maxW);

        var hazards = HazardsOf(def);
        AddWrapped(lines, "HAZARDS: ",
            hazards.Count > 0 ? hazards : new List<string> { "NONE CHARTED" },
            hazards.Count > 0 ? new Color(255, 140, 110) : new Color(140, 220, 140), maxW);

        if (def.Id is not ("rift" or "debug" or "hollow"))
            lines.Add((_meta.CoreShards.Contains(def.Id) ? "CORE SHARD SECURED" : "CORE SHARD IN CORE",
                _meta.CoreShards.Contains(def.Id) ? new Color(150, 230, 255) : grey, 1));
        if (def.Id == "hollow")
            lines.Add((def.Airless && _space.VacSuitLocked
                    ? "VAC SUIT REQUIRED - SEE THE FOUNDRY (U)"
                    : "VAC SUIT ABOARD - LANDING CLEARED",
                def.Airless && _space.VacSuitLocked ? new Color(255, 190, 90) : new Color(140, 220, 140), 1));
        if (_meta.PlanetsEscaped.Contains(def.Id))
            lines.Add(("ESCAPED", new Color(140, 220, 140), 1));
        if (PlanetDefs.DebugMode)
            lines.Add(("RIGHT CLICK - WARP HERE", new Color(255, 225, 140), 1));

        DrawMapTooltipBox(sb, MapProject(p.Pos), MapMarkerRadius(p), def.MapAccent, lines);
    }

    /// <summary>The sun's hover card — it's a charted hazard, not a destination.</summary>
    private void DrawMapSunTooltip(SpriteBatch sb)
    {
        DrawMapTooltipBox(sb, StarMapCentre, MapSunMarker, new Color(255, 190, 80),
            new List<(string, Color, int)>
            {
                ("THE SUN", new Color(255, 190, 80), 2),
                ("NOT LANDABLE - THE CORONA REPELS ALL APPROACH", new Color(150, 155, 175), 1),
                ("HAZARD: CORONA CONTACT BURNS THE HULL", new Color(255, 140, 110), 1),
            });
    }

    /// <summary>Panel plumbing shared by the planet and sun tooltips: size to the widest
    /// line, float beside the hovered marker, clamp on-screen, draw with an accent rule.</summary>
    private void DrawMapTooltipBox(SpriteBatch sb, Vector2 anchor, float markerR, Color accent,
        List<(string text, Color col, int scale)> lines)
    {
        const int pad = 14;
        var w = 0;
        var h = pad * 2;
        foreach (var (text, _, scale) in lines)
        {
            w = Math.Max(w, (int)_renderer.MeasureText(text, scale));
            h += scale >= 2 ? 24 : 17;
        }
        w += pad * 2;

        // Prefer the right of the marker; flip left when that runs off-screen.
        var x = (int)(anchor.X + markerR + 18f);
        if (x + w > VirtualWidth - 8) x = (int)(anchor.X - markerR - 18f) - w;
        x = Math.Clamp(x, 8, VirtualWidth - 8 - w);
        var y = Math.Clamp((int)(anchor.Y - h / 2f), 8, VirtualHeight - 8 - h);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, h), new Color(10, 12, 22, 240));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, 2), accent);
        sb.Draw(_renderer.Pixel, new Rectangle(x, y + h - 2, w, 2), accent * 0.5f);
        sb.End();

        var ty = y + pad;
        foreach (var (text, col, scale) in lines)
        {
            _renderer.DrawText(text, new Vector2(x + pad, ty), col, scale);
            ty += scale >= 2 ? 24 : 17;
        }
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

        for (var row = 0; row < visible; row++)
        {
            var i = _upgradeScroll + row;
            if (i >= Upgrades.All.Length) break;
            var def = Upgrades.All[i];
            var rowY = y + 88 + row * 52;
            var owned = !def.Repeatable && Upgrades.Owned(_meta, def.Id);
            var locked = Upgrades.Locked(_meta, def);
            var afford = !locked && Upgrades.CanAfford(_meta, def);
            if (i == _upgradeCursor)
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                sb.Draw(_renderer.Pixel, new Rectangle(x + 12, rowY - 6, w - 24, 46), new Color(45, 50, 70, 180));
                sb.End();
            }
            var nameCol = owned ? new Color(140, 220, 140)
                : locked ? new Color(95, 98, 112)
                : afford ? Color.White : new Color(130, 132, 145);
            var name = def.Id == "rover" ? $"ROVER ({_meta.Rovers} ABOARD)" : def.Name.ToUpperInvariant();
            var cost = owned ? "INSTALLED"
                : locked ? $"REQUIRES {Array.Find(Upgrades.All, u => u.Id == def.Requires)!.Name.ToUpperInvariant()}"
                : CostLabel(def);
            _renderer.DrawText(name, new Vector2(x + 24, rowY), nameCol, 2);
            _renderer.DrawText(cost,
                new Vector2(x + w - 24 - _renderer.MeasureText(cost), rowY + 4),
                owned ? new Color(140, 220, 140)
                : locked ? new Color(95, 98, 112)
                : afford ? new Color(255, 225, 140) : new Color(160, 120, 110));
            _renderer.DrawText(def.Desc.ToUpperInvariant(), new Vector2(x + 24, rowY + 22), new Color(150, 155, 175));
        }

        // Scroll cues when the catalogue continues past the window.
        if (_upgradeScroll > 0)
            _renderer.DrawText("+ MORE", new Vector2(x + w - 24 - _renderer.MeasureText("+ MORE"), y + 56), new Color(150, 155, 175));
        if (_upgradeScroll + visible < Upgrades.All.Length)
            _renderer.DrawText("+ MORE", new Vector2(x + w - 24 - _renderer.MeasureText("+ MORE"), y + h - 28), new Color(150, 155, 175));

        _renderer.DrawText("UP/DOWN SELECT   ENTER INSTALL   U/ESC CLOSE",
            new Vector2(x + 24, y + h - 28),
            new Color(150, 155, 175));
    }

    /// <summary>The orbit loadout overlay: per-drop supply kits bought with cargo. Same
    /// visual language as the foundry, but everything here is a consumable manifest that
    /// pays out when the rover launches.</summary>
    private void DrawLoadoutMenu(SpriteBatch sb)
    {
        const int w = 620;
        var h = 150 + Loadouts.All.Length * 46;
        var x = (VirtualWidth - w) / 2;
        var y = (VirtualHeight - h) / 2;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, h), new Color(10, 12, 22, 235));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, w, 2), new Color(150, 220, 255));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y + h - 2, w, 2), new Color(150, 220, 255));
        sb.End();

        _renderer.DrawText("ROVER LOADOUT",
            new Vector2(x + (w - _renderer.MeasureText("ROVER LOADOUT", 3)) / 2f, y + 16), Color.White, 3);
        _renderer.DrawText($"CARGO HOLD: {CargoSummary()}",
            new Vector2(x + 24, y + 50), new Color(150, 155, 175));

        for (var i = 0; i < Loadouts.All.Length; i++)
        {
            var def = Loadouts.All[i];
            var rowY = y + 78 + i * 46;
            var afford = Loadouts.CanAfford(_meta, def);
            var packed = _pendingKits.GetValueOrDefault(def.Id);
            if (i == _loadoutCursor)
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                sb.Draw(_renderer.Pixel, new Rectangle(x + 12, rowY - 5, w - 24, 40), new Color(45, 50, 70, 180));
                sb.End();
            }
            var name = packed > 0 ? $"{def.Name.ToUpperInvariant()} X{packed}" : def.Name.ToUpperInvariant();
            _renderer.DrawText(name, new Vector2(x + 24, rowY),
                packed > 0 ? new Color(140, 220, 140) : afford ? Color.White : new Color(130, 132, 145), 2);
            var cost = "";
            foreach (var (id, n) in def.Mats)
                cost += (cost.Length > 0 ? " + " : "") + $"{n} {Tiles.ResourceLabel(id)}";
            _renderer.DrawText(cost,
                new Vector2(x + w - 24 - _renderer.MeasureText(cost), rowY + 4),
                afford ? new Color(255, 225, 140) : new Color(160, 120, 110));
            _renderer.DrawText(def.Desc.ToUpperInvariant(), new Vector2(x + 24, rowY + 20), new Color(150, 155, 175));
        }

        _renderer.DrawText("PACKS PAY OUT ON THE NEXT DROP   ENTER BUY   L/ESC CLOSE",
            new Vector2(x + 24, y + h - 26), new Color(150, 155, 175));
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

    /// <summary>The mothership — a circular ring station built procedurally: outer hull
    /// ring, four spokes, glazed central hub, warning stripes at the cardinal points, and a
    /// darker docking bay notch. Drawn spinning slowly in both views.</summary>
    private Texture2D BuildStationTexture()
    {
        const int size = 48;
        const float c = (size - 1) / 2f;
        var data = new Color[size * size];
        var hull = new Color(185, 190, 205);
        var shade = new Color(115, 120, 138);
        var dark = new Color(70, 72, 85);
        var glass = new Color(140, 210, 235);
        var stripe = new Color(230, 190, 70);
        for (var yPix = 0; yPix < size; yPix++)
            for (var xPix = 0; xPix < size; xPix++)
            {
                var dx = xPix - c;
                var dy = yPix - c;
                var r = MathF.Sqrt(dx * dx + dy * dy);
                var ang = MathF.Atan2(dy, dx);
                var col = Color.Transparent;

                // Outer hull ring with an inner shadow edge.
                if (r is >= 17f and <= 23f)
                {
                    col = r >= 21.6f ? shade : hull;
                    // Warning stripes at the four cardinal points.
                    var quad = MathF.Abs(MathF.IEEERemainder(ang, MathF.PI / 2f));
                    if (quad < 0.16f && r < 21.6f) col = stripe;
                    // Docking bay notch at the top of the ring.
                    if (MathF.Abs(MathF.IEEERemainder(ang + MathF.PI / 2f, MathF.PI * 2f)) < 0.24f)
                        col = dark;
                }
                // Four spokes from hub to ring.
                else if (r is > 8f and < 17f)
                {
                    var spoke = MathF.Abs(MathF.IEEERemainder(ang + MathF.PI / 4f, MathF.PI / 2f));
                    if (spoke < 0.13f) col = shade;
                }
                // Central hub: hull rim around a glass core.
                else if (r <= 8f)
                {
                    col = r <= 4.6f ? glass : r >= 7f ? shade : hull;
                }
                data[yPix * size + xPix] = col;
            }
        var tex = new Texture2D(GraphicsDevice, size, size);
        tex.SetData(data);
        return tex;
    }

    /// <summary>The station drawn inside a planet's world view (entity pass, world coords).
    /// The planet view is side-on, so this is the SIDE profile — deck hull, glass command
    /// dome, under-slung docking bay, masts — rotated so its keel faces local down. Blinking
    /// running lights along the deck. Big next to the ~36 px rocket.</summary>
    private void DrawStationInWorld(Vector2 pos)
    {
        var up = _run.Planet.UpAt(pos);
        var rot = MathF.Atan2(up.X, -up.Y);
        _renderer.Batch.Draw(_stationSideTex, pos, null, Color.White, rot,
            new Vector2(_stationSideTex.Width / 2f, _stationSideTex.Height / 2f),
            2.2f, SpriteEffects.None, 0f);
        // Running lights: blinkers spaced along the deck line.
        var right = new Vector2(-up.Y, up.X);
        for (var i = 0; i < 3; i++)
        {
            var lp = pos + right * ((i - 1) * 42f) + up * 8f;
            var on = MathF.Sin(_totalTime * 5f + i * 2.1f) > 0.2f;
            if (on) _renderer.DrawRect(lp, new Vector2(2.4f, 2.4f), new Color(255, 200, 120));
        }
    }

    /// <summary>Side profile of the mothership for the planet view: a long hull deck with a
    /// glazed command dome amidships, engine pods at both ends, antenna masts, warning
    /// stripes, and a docking bay notch on the keel where the rover drops and the rocket
    /// docks. Built procedurally like the ring.</summary>
    private Texture2D BuildStationSideTexture()
    {
        const int w = 56, h = 22;
        var data = new Color[w * h];
        var hull = new Color(185, 190, 205);
        var shade = new Color(115, 120, 138);
        var dark = new Color(70, 72, 85);
        var glass = new Color(140, 210, 235);
        var stripe = new Color(230, 190, 70);
        void Set(int x, int y, Color c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) data[y * w + x] = c;
        }
        // Main deck hull: rows 8..14, tapered at the ends.
        for (var y = 8; y <= 14; y++)
            for (var x = 2; x < w - 2; x++)
            {
                var taper = y is 8 or 14 ? 4 : 0;
                if (x < 2 + taper || x >= w - 2 - taper) continue;
                Set(x, y, y >= 13 ? shade : hull);
            }
        // Engine pods at both ends.
        for (var y = 9; y <= 13; y++)
            for (var x = 0; x < 4; x++) { Set(x, y, dark); Set(w - 1 - x, y, dark); }
        // Command dome amidships (rows 3..8), glass with a hull rim.
        for (var y = 3; y <= 8; y++)
            for (var x = 0; x < w; x++)
            {
                var dx = x - w / 2f + 0.5f;
                var dy = (y - 8) * 1.6f;
                var rr = MathF.Sqrt(dx * dx + dy * dy);
                if (rr <= 9f) Set(x, y, rr > 7.4f ? hull : glass);
            }
        // Warning stripes on the deck.
        for (var x = 8; x < w - 8; x += 12) { Set(x, 12, stripe); Set(x + 1, 12, stripe); }
        // Docking bay: a dark keel notch dead centre, where the lander leaves from.
        for (var y = 15; y <= 18; y++)
            for (var x = w / 2 - 5; x <= w / 2 + 4; x++)
                Set(x, y, y >= 17 ? Color.Transparent : dark);
        for (var x = w / 2 - 5; x <= w / 2 + 4; x += 9) { Set(x, 17, shade); Set(x, 18, shade); }
        // Antenna masts.
        for (var y = 0; y < 3; y++) { Set(10, y, shade); Set(w - 11, y, shade); }
        Set(10, 0, stripe); Set(w - 11, 0, stripe);

        var tex = new Texture2D(GraphicsDevice, w, h);
        tex.SetData(data);
        return tex;
    }

    /// <summary>The white blink masking the space↔planet coordinate swap, drawn over
    /// whatever screen is active.</summary>
    private void DrawTransitionFlash()
    {
        if (_transitionFlash <= 0f) return;
        var sb = _renderer.Batch;
        sb.Begin();
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight),
            Color.White * MathHelper.Clamp(_transitionFlash / 0.6f, 0f, 1f));
        sb.End();
    }
}
