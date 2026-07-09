using System;
using System.Collections.Generic;
using System.IO;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.Space;
using DwarfMiner.Systems;
using DwarfMiner.UI;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner;

/// <summary>Top-level screen state. Space is the flyable solar system (entry screen and
/// post-run hub — see src/Game1.Space.cs); Playing is a live run; GameOver overlays the
/// frozen run until R returns you to your ship.</summary>
public enum GameScreen { Space, Playing, GameOver }

public sealed partial class DwarfMinerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private Renderer _renderer = null!;
    private Camera _camera = null!;
    private Texture2D _dwarfTex = null!;
    private PlayerSprite? _playerSprite;
    private float _playerFacing = 1f;
    private MetaSave _meta = null!;
    private readonly Particles _particles = new();
    private readonly Sfx _sfx = new();
    private float _prevPlayerHealth;
    private int _prevProjCount;
    /// <summary>Previous-frame titan health — the soul award fires on the >0 → ≤0 crossing,
    /// so a save resumed with an already-dead titan can never re-award.</summary>
    private float _prevTitanHealth;

    /// <summary>The current planet visit. Everything per-run lives here — swapped atomically
    /// when the player lands on a planet from space. Null only while flying in space before
    /// the first run; Playing/GameOver screens always have one.</summary>
    private Session _run = null!;

    private GameScreen _screen = GameScreen.Space;

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private string _gameOverReason = "";
    private bool _screenshotPending;

    /// <summary>Transient HUD toast ("RUN SAVED") — drawn top-centre while the timer runs.</summary>
    private string _toast = "";
    private float _toastTimer;

    /// <summary>F6 master-volume cycle. Step 0 is the classic default; the choice persists
    /// in MetaSave.VolumeStep.</summary>
    private static readonly float[] VolumeSteps = { 0.55f, 0.28f, 0.1f, 0f };
    private static readonly string[] VolumeNames = { "FULL", "LOW", "QUIET", "MUTED" };

    /// <summary>Frame-rate diagnostics, drawn top-right on every screen: real rendered FPS
    /// (counted at Draw), plus smoothed update/draw CPU times so a slow frame points at its
    /// culprit.</summary>
    private readonly System.Diagnostics.Stopwatch _updSw = new();
    private readonly System.Diagnostics.Stopwatch _drawSw = new();
    private float _updateMs, _drawMs;
    private int _fps, _fpsFrames;
    private long _fpsMark = Environment.TickCount64;

    /// <summary>Cave-in warning state: <c>_caveInWarn</c> counts down while condemned tiles hang
    /// over the dwarf (drives the flashing HUD banner); <c>_caveInDust</c> throttles the sifting
    /// debris telegraph emitted from those tiles.</summary>
    private float _caveInWarn;
    private float _caveInDust;

    /// <summary>Fuel units the ship must hold before it can lift off.</summary>
    public const int FuelToLaunch = 12;

    /// <summary>Rover descent state. While <see cref="_landing"/> is set, normal play is
    /// suspended: the pod departs the orbiting mothership and sinks along local gravity with
    /// A/D lateral steering (semi-controlled) until it touches the surface, where gameplay
    /// begins.</summary>
    private bool _landing;
    private Vector2 _landerPos;

    /// <summary>White flash masking the space↔planet coordinate swap — the one visual cut
    /// the seamless transitions can't avoid. Decays in Update, drawn over both screens.</summary>
    private float _transitionFlash;

    /// <summary>Manual orbital ascent: after the liftoff cinematic clears the pad, the player
    /// steers the climbing rocket (A/D) up to the mothership's parking orbit and docks —
    /// reaching orbit altitude engages an approach glide so the rendezvous always completes.</summary>
    private bool _ascending;

    /// <summary>Parked in orbit after an atmosphere entry: the world is live below, the
    /// station holds at its anchor, and the player decides when (and where — A/D shifts the
    /// orbit) to launch the rover, or breaks orbit back to space.</summary>
    private bool _orbiting;

    /// <summary>Geo Scanner state: cached nearest-target positions, refreshed on a timer
    /// (the tile sweep is cheap but not per-frame cheap).</summary>
    private float _scanTimer;
    private Vector2? _scanFuel;
    private Vector2? _scanOre;

    /// <summary>Rover loadout menu (L, in orbit): kits bought with cargo stack in the
    /// pending manifest and pay out into the pack on the next drop.</summary>
    private bool _loadoutOpen;
    private int _loadoutCursor;
    private readonly Dictionary<string, int> _pendingKits = new();

    private int PendingKitCount()
    {
        var n = 0;
        foreach (var (_, c) in _pendingKits) n += c;
        return n;
    }

    /// <summary>Liftoff cinematic state. While <see cref="_launching"/> is set, normal play is
    /// suspended: the rocket climbs along <see cref="_launchUp"/> under <see cref="_launchVel"/>,
    /// trailing exhaust, until the player takes the stick for the orbital ascent.</summary>
    private bool _launching;
    private float _launchElapsed;
    private float _launchVel;
    private Vector2 _launchShipPos;
    private Vector2 _launchUp;
    /// <summary>Wall-clock seconds since launch — drives the DM_AUTOSHOT capture schedule so
    /// tooling can screenshot any screen, including the star map before a run starts.</summary>
    private float _totalTime;
    private readonly bool _bossCam = Environment.GetEnvironmentVariable("DM_BOSSCAM") is { Length: > 0 };
    private float _autoShotAt =
        float.TryParse(Environment.GetEnvironmentVariable("DM_AUTOSHOT"), out var s)
            ? s : float.PositiveInfinity;
    private float _autoSaveAt =
        float.TryParse(Environment.GetEnvironmentVariable("DM_AUTOSAVE"), out var sv)
            ? sv : float.PositiveInfinity;

    /// <summary>The crafting overlay and the inventory/toolbelt drag-drop UI — see
    /// src/UI. While the menu is open, mouse/movement input still drives the world but key
    /// events route to the menu.</summary>
    private readonly CraftingMenu _craftingMenu = new();
    private readonly InventoryUi _invUi = new();
    private readonly DebugMenu _debugMenu = new();

    private const int VirtualWidth = 1280;
    private const int VirtualHeight = 720;

    public DwarfMinerGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = VirtualWidth,
            PreferredBackBufferHeight = VirtualHeight,
            SynchronizeWithVerticalRetrace = true,
        };
        // Default on the OpenGL backend is DiscardContents — switching to a RenderTarget
        // would wipe the backbuffer, leaving the lightmap composite to multiply against
        // black. PreserveContents keeps the world+entities pass intact across RT swaps.
        _graphics.PreparingDeviceSettings += (_, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
        Window.Title = "Dwarf Miner";
        Window.AllowUserResizing = false;
        // Item table lambdas capture `this` (they read _run at call time), so this can't be
        // a field initializer — C# forbids `this` there.
        _items = BuildItems();
        // Suspend-save on window close / Esc quit, so a long run survives quitting. Finished
        // runs never reach this in Playing — EndRun already deleted the save.
        Exiting += (_, _) =>
        {
            // A visit only suspends once the dwarf is actually down — quitting from the
            // parking orbit just discards the unentered world.
            if (_screen == GameScreen.Playing && !_orbiting && _run is not null) RunSave.Write(_run);
            // Fuel burned and flying done between the event-driven saves would otherwise be
            // lost — snapshot the mothership and flush meta on the way out.
            if (_screen == GameScreen.Space) CaptureShipState();
            _meta?.Save();
        };
    }

    protected override void Initialize()
    {
        _meta = MetaSave.Load();
        // Boot into space with the mothership wherever it was left (fresh installs park at
        // the farthest charted world), and the saved volume step applied.
        _space = new SpaceSim();
        RestoreShipState();
        _sfx.Master = VolumeSteps[Math.Clamp(_meta.VolumeStep, 0, VolumeSteps.Length - 1)];
        // Warm the survey-world cache in the background so the system view can rasterize
        // real terrain discs (and the M survey opens instantly) without a frame hitch.
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var def in PlanetDefs.All) Space.Survey.WorldFor(def);
        });
        // DM_AUTOSTART=<planet-id|resume> skips the space screen and jumps straight into a run
        // (or resumes the suspend save) — keeps DM_AUTOSHOT-driven gameplay verification
        // working without menu input.
        if (Environment.GetEnvironmentVariable("DM_AUTOSTART") is { Length: > 0 } auto)
        {
            if (auto == "resume") ResumeRun();
            else StartNewRun(PlanetDefs.ById(auto));
        }
        // DM_ORBIT=<planet-id> boots straight into the parking orbit — tooling can
        // screenshot the orbit state without flying there.
        else if (Environment.GetEnvironmentVariable("DM_ORBIT") is { Length: > 0 } orbit)
        {
            EnterOrbit(PlanetDefs.ById(orbit));
        }
        base.Initialize();
    }

    /// <summary>The heavy half of starting a run: world generation, cell seeding, and the
    /// liquid pre-settle. Static and touching only freshly built objects, so the space
    /// screen can run it on a background thread while the player is still flying — by the
    /// time they press Enter the world is usually already built (seamless landing).</summary>
    internal static Session BuildSessionWorld(PlanetDef def)
    {
        var seed = (int)DateTime.Now.Ticks;
        var run = new Session(def);
        run.Planet = WorldGen.Generate(seed, def);
        run.Cells = new Cells(run.Planet);
        run.Physics = new Physics(run.Planet, run.Cells);
        // Lava seeding: any cave (Sky tile) within the def's lava fraction of the planet
        // radius gets filled with lava — volcanic worlds flood far shallower cavities.
        if (def.LavaFillFrac > 0f)
            run.Cells.FillSkyTilesWithin(run.Planet.Radius * def.LavaFillFrac, Material.Lava);

        // Water seeding: world gen recorded lake-basin and reservoir tiles; pour the cells in
        // now. Water is always sim cells (never solid tiles), so it settles, flows into player
        // tunnels, and quenches lava per the cell rules.
        foreach (var (wsx, wsy) in run.Planet.WaterSeeds)
            run.Cells.FillTile(wsx, wsy, Material.Water);

        // Hazard cells: gas rises to the cave roofs, acid settles to the floors. Poured after
        // water so the pre-settle below carries them to rest alongside it.
        foreach (var (gx, gy) in run.Planet.GasSeeds)
            run.Cells.FillTile(gx, gy, Material.Gas);
        foreach (var (ax, ay) in run.Planet.AcidSeeds)
            run.Cells.FillTile(ax, ay, Material.Acid);

        // Pre-settle the seeded liquids during load: the first ~2s of cell ticks carry every
        // seeded cell awake (tens of ms per tick at Density 8). Burning them here turns a
        // visible gameplay stutter into a slightly longer world-gen pause; after settling,
        // hemmed pool interiors sleep and the steady-state tick is cheap.
        for (var i = 0; i < 120; i++) run.Cells.Update(1f / 60f);
        return run;
    }

    private void StartNewRun(PlanetDef def, bool descend = false)
    {
        // A prefetched world (built in the background while flying nearby) makes this
        // near-instant; otherwise build it here and eat the pause.
        _run = TakePrefetchedSession(def) ?? BuildSessionWorld(def);
        // Ship-stage recipes (nav core cost) vary per planet — rebuild the crafting table.
        Crafting.SetPlanet(def);

        // Spawn the dwarf on top of whatever mountain is at angle -π/2 — walk down from
        // far above until the first solid tile, then float a few pixels above it.
        var surfacePos = SpawnDirector.FindSurfaceSpawn(_run.Planet, -MathF.PI / 2f, _run.Planet.Radius);
        _run.Player = new Player(surfacePos)
        {
            // Survival by default; DM_GOD=1 starts runs in god mode for testing, and G
            // toggles it in-game either way. The toggle drives ghost flight, super-pickaxe
            // power, and extended mine range as a single bundle — see
            // Player.EffectivePickaxePower / EffectiveMineRange.
            FlyMode = Environment.GetEnvironmentVariable("DM_GOD") is { Length: > 0 },
            // Apply meta-progress: a player who has previously escaped starts with a
            // higher-tier pickaxe so subsequent runs are slightly easier.
            PickaxeTier = Math.Max(1, _meta.StartingPickaxePower),
        };
        _run.HasCannon = _meta.StartWithCannon;
        // God mode carries the full armoury — load every weapon onto the belt from frame
        // one (toggling god off strips the unowned loaners).
        if (_run.Player.FlyMode)
            foreach (var w in GodWeaponIds) _run.Player.Toolbelt.AutoEquip(w);
        // DM_BOSSCAM spawns the egg beside the dwarf (instead of across the planet) so tooling
        // can screenshot the boss without walking to it.
        var titanAngle = Environment.GetEnvironmentVariable("DM_BOSSCAM") is { Length: > 0 }
            ? -MathF.PI / 2f + 0.16f : MathF.PI * 0.6f;
        // Pooled planets (Coreheart) roll a fresh kaiju out of the breach every visit;
        // fixed planets always hatch their signature boss (soul farming stays plannable).
        // DM_BOSSCAM=<TitanKind name> forces a kind so tooling can screenshot each variant.
        var titanKind = def.TitanPool is { Length: > 0 } pool
            ? pool[Random.Shared.Next(pool.Length)]
            : def.Titan;
        if (Environment.GetEnvironmentVariable("DM_BOSSCAM") is { Length: > 0 } bossCam
            && char.IsLetter(bossCam[0])   // DM_BOSSCAM=1 is the plain follow-cam, not a kind
            && Enum.TryParse<TitanKind>(bossCam, true, out var forced))
            titanKind = forced;
        _run.Titan = new Titan(_run.Planet, titanAngle, titanKind);
        // DM_HATCH=<seconds> shortens the egg timer for testing (default 10 min).
        if (float.TryParse(Environment.GetEnvironmentVariable("DM_HATCH"), out var hatchAt))
            _run.Titan.EggTimer = hatchAt;
        _prevTitanHealth = _run.Titan.Health;
        // Foundry gear bought on the mothership rides down with every rover drop.
        _run.Player.HasJetpack = Upgrades.Owned(_meta, "jetpack");
        _run.Player.JetTier2 = Upgrades.Owned(_meta, "jetpack2");
        _run.Player.JetTier3 = Upgrades.Owned(_meta, "jetpack3");
        _run.Player.HasO2Recycler = Upgrades.Owned(_meta, "o2");
        _run.Player.O2Tier2 = Upgrades.Owned(_meta, "o22");
        _run.Player.HasMagnet = Upgrades.Owned(_meta, "magnet");
        _run.Player.MagnetTier2 = Upgrades.Owned(_meta, "magnet2");
        if (Upgrades.Owned(_meta, "drill")) _run.Player.PickaxeTier++;
        _run.Player.HasPlating = Upgrades.Owned(_meta, "plating");
        // Emerald Weave: a bigger health pool, filled from the start of every drop.
        if (Upgrades.Owned(_meta, "vitality"))
        {
            _run.Player.MaxHealth = 140f;
            _run.Player.Health = 140f;
        }
        // Rover Armory: every drop comes armed — sidearm plus a belt of rounds.
        if (Upgrades.Owned(_meta, "armory"))
        {
            _run.Player.HasPistol = true;
            _run.Player.Toolbelt.AutoEquip("pistol");
            _run.Player.Inventory.Add("bullets", 90);
        }
        // Supply Cache: field consumables in every rover.
        if (Upgrades.Owned(_meta, "supplies"))
        {
            _run.Player.Inventory.Add("poultice", 2);
            _run.Player.Inventory.Add("blocks", 40);
            _run.Player.Inventory.Add("sentry", 1);
            _run.Player.Toolbelt.AutoEquip("poultice");
        }
        _scanTimer = 0f;
        _scanFuel = null;
        _scanOre = null;
        SpawnDirector.SpawnInitialFauna(_run);
        // DM_FAUNA=1 parades the biome fauna beside the spawn so tooling can screenshot
        // creature art without hunting for natural spawns.
        if (Environment.GetEnvironmentVariable("DM_FAUNA") is { Length: > 0 })
        {
            var fUp = _run.Planet.UpAt(_run.Player.Position);
            var fRight = new Vector2(-fUp.Y, fUp.X);
            var kinds = new[] { CreatureKind.SporeBat, CreatureKind.CrystalCrawler, CreatureKind.VoidWraith };
            for (var i = 0; i < kinds.Length; i++)
                _run.Creatures.Add(new Creature(
                    _run.Player.Position + fRight * (26f + i * 22f) + fUp * 8f, kinds[i]));
        }
        _run.EarthquakeTimer = 25f * def.QuakeScale;
        _run.SpawnTimer = 6f;
        _run.FaunaTimer = 8f;
        // DM_METEOR=<s> overrides the first-strike delay for testing.
        _run.MeteorTimer = float.TryParse(Environment.GetEnvironmentVariable("DM_METEOR"), out var mt)
            ? mt : 16f + (float)Random.Shared.NextDouble() * 14f;   // first strike ~16-30s in
        _run.SurgeTimer = 22f;
        // Disasters: first flare well into the visit; DM_FLARE=<s> forces it for testing.
        _run.FlareTimer = float.TryParse(Environment.GetEnvironmentVariable("DM_FLARE"), out var ft)
            ? ft : 75f + (float)Random.Shared.NextDouble() * 60f;
        _run.BlizzardTimer = 50f + (float)Random.Shared.NextDouble() * 45f;
        // DM_ACIDRAIN=<seconds> forces the first toxic-cloud storm for tooling.
        _run.AcidRainTimer = float.TryParse(Environment.GetEnvironmentVariable("DM_ACIDRAIN"), out var art)
            ? art : 45f + (float)Random.Shared.NextDouble() * 50f;
        _gameOverReason = "";
        _craftingMenu.Reset();
        _invUi.Reset();
        _screen = GameScreen.Playing;
        _orbiting = false;
        // Test-hook starts (DM_AUTOSTART) spawn straight on the surface; DM_DESCEND=1
        // forces the rover descent for tooling; DM_LAUNCH=1 plants a fuelled rocket and
        // lifts off at once (screenshots the escape ascent). The real gameplay path goes
        // through EnterOrbit → LaunchRover instead of this method.
        _landing = descend || Environment.GetEnvironmentVariable("DM_DESCEND") is { Length: > 0 };
        if (_landing)
        {
            _landerPos = _run.StationPos;
            _run.Player.Position = _landerPos;
            _transitionFlash = 0.6f;
            _toast = "ROVER AWAY - A/D STEER";
            _toastTimer = 3.5f;
        }
        else if (Environment.GetEnvironmentVariable("DM_LAUNCH") is { Length: > 0 })
        {
            SpawnDebugShip(fuelled: true);
            if (_run.PadPos is { } pad) BeginLaunch(pad);
        }
        // Camera exists except when DM_AUTOSTART triggers a run during Initialize —
        // LoadContent snaps it then. Descents open zoomed out at the station and ease in
        // on the way down; surface starts restore the play zoom directly.
        if (_camera is not null)
        {
            _camera.Zoom = _landing ? 1.5f : _playZoom;
            _camera.SnapTo(_run.Player.Position, 0f);
        }
    }

    /// <summary>Flying into a planet's upper atmosphere from space: build (or claim the
    /// prefetched) world and arrive above the parking orbit at the same bearing you flew in
    /// on — the ship then settles itself down into orbit automatically. From there the
    /// player orbits left/right to pick a drop site, launches the lander, or leaves. No
    /// menus, no prompts on the way in.</summary>
    private void EnterOrbit(PlanetDef def, float entryBearing = -MathF.PI / 2f)
    {
        StartNewRun(def);
        _landing = false;
        _orbiting = true;
        _transitionFlash = 0.6f;
        _run.MothershipAngle = entryBearing;
        _run.OrbitEntryOffset = 420f;   // arrive high, glide down — the automatic fly-in
        // DM_LOADOUT=1 opens the loadout menu on arrival so tooling can screenshot it.
        _loadoutOpen = Environment.GetEnvironmentVariable("DM_LOADOUT") is { Length: > 0 };
        _run.Player.Position = _run.StationPos;
        _toast = $"ORBIT OF {def.Name.ToUpperInvariant()} ESTABLISHED";
        _toastTimer = 3f;
        // Camera is null only for the DM_ORBIT boot hook — LoadContent snaps it then.
        if (_camera is not null)
        {
            _camera.Zoom = 1.1f;
            _camera.SnapTo(_run.StationPos, 0f);
        }
    }

    /// <summary>One frame in the parking orbit: left/right slides the station around the
    /// planet (choosing the drop site), ENTER launches the lander, SPACE leaves the planet.
    /// The arrival glide settles first; the world lives underneath the whole time.</summary>
    private void UpdateOrbit(float dt, KeyboardState keys)
    {
        // The automatic fly-in: bleed off the arrival height until the ship sits in orbit.
        _run.OrbitEntryOffset = MathF.Max(0f, _run.OrbitEntryOffset - 340f * dt);

        var lat = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        var orbitRadius = _run.Planet.Radius * Planet.TileSize + Session.OrbitAltitude;
        _run.MothershipAngle += lat * (170f / orbitRadius) * dt;

        var station = _run.StationPos;
        _run.Player.Position = station;
        _run.Player.Velocity = Vector2.Zero;
        var up = _run.Planet.UpAt(station);
        // Frame both the station and the ground below it — the whole point of shifting the
        // orbit is picking a drop site you can see. (Surface baseline sits ~1270 px under
        // the raised parking orbit, so this is a genuinely wide shot.)
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, 0.44f, MathHelper.Clamp(dt * 2f, 0f, 1f));
        _camera.Follow(station - up * 630f, up, dt);

        _run.Physics.Update(dt);
        _run.Cells.Update(dt);
        _particles.Update(dt, _run.Planet);
        _toastTimer -= dt;

        // The loadout menu captures input while open; the orbit keeps drifting behind it.
        if (Pressed(keys, _prevKeys, Keys.L)) _loadoutOpen = !_loadoutOpen;
        if (_loadoutOpen)
        {
            UpdateLoadoutMenu(keys);
            return;
        }

        if (Pressed(keys, _prevKeys, Keys.Enter))
        {
            LaunchRover();
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.Space))
        {
            _orbiting = false;
            _transitionFlash = 0.6f;
            EnterSpace(PlanetDefs.IndexOf(_run.Def), exitSpeed: 260f, zoomFromPlanet: true);
        }
    }

    private void UpdateLoadoutMenu(KeyboardState keys)
    {
        if (Pressed(keys, _prevKeys, Keys.Escape)) _loadoutOpen = false;
        var count = Loadouts.All.Length;
        if (Pressed(keys, _prevKeys, Keys.Up) || Pressed(keys, _prevKeys, Keys.W))
            _loadoutCursor = (_loadoutCursor - 1 + count) % count;
        if (Pressed(keys, _prevKeys, Keys.Down) || Pressed(keys, _prevKeys, Keys.S))
            _loadoutCursor = (_loadoutCursor + 1) % count;
        if (Pressed(keys, _prevKeys, Keys.Enter))
        {
            var def = Loadouts.All[_loadoutCursor];
            if (Loadouts.TryBuy(_meta, def, _pendingKits))
            {
                _sfx.Play("pickup", 0.7f);
                _toast = $"{def.Name.ToUpperInvariant()} PACKED ({_pendingKits[def.Id]} ABOARD)";
            }
            else
            {
                _toast = "NOT ENOUGH CARGO";
            }
            _toastTimer = 2.5f;
        }
    }

    /// <summary>Deploy the rover from orbit — spends one (or takes the drop-pod penalty)
    /// and begins the steered descent from wherever the orbit was parked.</summary>
    private void LaunchRover()
    {
        var podDrop = _meta.Rovers <= 0;
        if (!podDrop) _meta.Rovers--;
        _meta.Save();
        // The pending loadout manifest pays out into the pack as the rover departs.
        foreach (var (kitId, kits) in _pendingKits)
        {
            var kit = Array.Find(Loadouts.All, l => l.Id == kitId);
            if (kit is null) continue;
            foreach (var (id, n) in kit.Grants) _run.Player.Inventory.Add(id, n * kits);
        }
        _pendingKits.Clear();
        _loadoutOpen = false;
        _orbiting = false;
        _landing = true;
        _landerPos = _run.StationPos;
        _run.Player.Position = _landerPos;
        if (podDrop)
        {
            if (!Upgrades.Owned(_meta, "dampeners")) _run.Player.Health *= 0.5f;
            _toast = Upgrades.Owned(_meta, "dampeners")
                ? "NO ROVERS - DROP POD DOWN SOFT ON THE DAMPENERS"
                : "NO ROVERS - EMERGENCY DROP POD! SUIT DAMAGED";
        }
        else
        {
            _toast = "ROVER AWAY - A/D STEER";
        }
        _toastTimer = 3.5f;
    }

    /// <summary>One frame of the rover descent: constant sink along local gravity, direct
    /// lateral drive from A/D (a guided pod, not a brick), world simulating underneath and
    /// the camera easing from orbit scale down to play zoom. Touchdown hands control to
    /// normal play exactly where the pod settled.</summary>
    private void UpdateLanding(float dt, KeyboardState keys)
    {
        var up = _run.Planet.UpAt(_landerPos);
        var right = new Vector2(-up.Y, up.X);
        var lat = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        _landerPos += (-up * 185f + right * (lat * 205f)) * dt;

        // Retro-thruster flame under the pod, throttled by the particle system itself.
        _particles.EmitRocketExhaust(_landerPos - up * 4f, -up);

        _run.Player.Position = _landerPos;
        _run.Player.Velocity = Vector2.Zero;
        // Altitude-driven zoom: stay wide while high (see the terrain you're steering at),
        // then close to play zoom only for the final stretch.
        var alt = (_landerPos - _run.Planet.Center).Length()
                  - (Planet.RingMin + _run.Planet.SurfaceRing) * Planet.TileSize;
        var zoomTarget = MathHelper.Lerp(_playZoom, 0.72f, MathHelper.Clamp(alt / 650f, 0f, 1f));
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, zoomTarget, MathHelper.Clamp(dt * 2.4f, 0f, 1f));
        _camera.Follow(_landerPos, up, dt);
        // The station keeps drifting while the pod falls.
        _run.MothershipAngle += Session.StationDriftRate * dt;

        _run.Physics.Update(dt);
        _run.Cells.Update(dt);
        _particles.Update(dt, _run.Planet);
        _run.RunTime += dt;
        _toastTimer -= dt;

        // Touchdown: feet-level rock below, or a cliff face clipped sideways. Nudge free of
        // any overlap so the dwarf never starts a run embedded.
        if (!_run.Planet.IsSolidAt(_landerPos - up * (_run.Player.Radius + 2f))
            && !_run.Planet.IsSolidAt(_landerPos))
            return;
        for (var i = 0; i < 60 && _run.Planet.IsSolidAt(_landerPos); i++) _landerPos += up * 2f;
        _landing = false;

        // The pod hits hard enough to gouge a small crater (soft tiles only — same rules as
        // a meteor strike, minus the ore) and what's left of it stays as wreckage: a
        // landmark marking where you came down.
        var (cx, cy) = _run.Planet.WorldToTile(_landerPos - up * (_run.Player.Radius + 4f));
        const int craterR = 3;
        for (var dy = -craterR; dy <= craterR; dy++)
            for (var dx = -craterR; dx <= craterR; dx++)
            {
                if (dx * dx + dy * dy > craterR * craterR) continue;
                var x = cx + dx; var y = cy + dy;
                var k = _run.Planet.Get(x, y);
                if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;
                _run.Planet.Set(x, y, TileKind.Sky);
                _run.Cells.SpawnDustInTile(x, y, k);
                _run.Physics.MarkDirty(x, y);
            }
        _run.RoverWreck = _landerPos - up * 2f;

        _run.Player.Position = _landerPos;
        _particles.EmitDust(_landerPos, 22f);
        _run.Shake = MathF.Max(_run.Shake, 0.7f);
        _sfx.Play("collapse", 0.55f, pitch: 0.1f);
        _toast = "TOUCHDOWN - ROVER EXPENDED";
        _toastTimer = 2.5f;
    }

    /// <summary>Resume the suspended run from disk — RunSave rebuilt the Session; this does
    /// the same post-worldgen wiring StartNewRun does. Wildlife starts empty and SpawnDirector
    /// restocks it over the first minute.</summary>
    private void ResumeRun()
    {
        var run = RunSave.TryRead();
        if (run is null) return;  // corrupt/stale save — the map hint disappears next frame
        _run = run;
        Crafting.SetPlanet(run.Def);
        _prevTitanHealth = run.Titan.Health;
        // Foundry gear isn't in the run save — it's meta state, re-applied on every entry.
        // (Drill Rig and the Armory/Supply kits aren't: the saved tier/inventory carry them.)
        run.Player.HasJetpack = Upgrades.Owned(_meta, "jetpack");
        run.Player.JetTier2 = Upgrades.Owned(_meta, "jetpack2");
        run.Player.JetTier3 = Upgrades.Owned(_meta, "jetpack3");
        run.Player.HasO2Recycler = Upgrades.Owned(_meta, "o2");
        run.Player.O2Tier2 = Upgrades.Owned(_meta, "o22");
        run.Player.HasMagnet = Upgrades.Owned(_meta, "magnet");
        run.Player.MagnetTier2 = Upgrades.Owned(_meta, "magnet2");
        run.Player.HasPlating = Upgrades.Owned(_meta, "plating");
        if (Upgrades.Owned(_meta, "vitality")) run.Player.MaxHealth = 140f;
        // Loading woke every cell; burn the resettle here like world gen's pre-settle pass.
        for (var i = 0; i < 45; i++) _run.Cells.Update(1f / 60f);
        _gameOverReason = "";
        _craftingMenu.Reset();
        _invUi.Reset();
        _screen = GameScreen.Playing;
        if (_camera is not null)
        {
            _camera.Zoom = _playZoom;
            _camera.SnapTo(_run.Player.Position, 0f);
        }
    }

    protected override void LoadContent()
    {
        _renderer = new Renderer(GraphicsDevice);
        _sfx.Build();
        Icons.Build(GraphicsDevice);
        _camera = new Camera
        {
            ViewportSize = new Point(VirtualWidth, VirtualHeight),
            // DM_ZOOM overrides the default zoom for testing (e.g. zoom out to frame a boss).
            Zoom = float.TryParse(Environment.GetEnvironmentVariable("DM_ZOOM"), out var z) ? z : 4.0f,
        };
        // The space screen drives Zoom itself every frame; remember the in-run zoom (incl. any
        // DM_ZOOM override) so landing on a planet restores it.
        _playZoom = _camera.Zoom;
        _stationTex = BuildStationTexture();
        _stationSideTex = BuildStationSideTexture();
        _arrowTex = BuildArrowTexture();
        BuildWeaponTextures();
        // No run yet when the game boots to space; StartNewRun snaps on planet entry.
        if (_run is not null) _camera.SnapTo(_run.Player.Position, 0f);
        else _camera.SnapTo(_space.ShipPos, 0f);

        // Animated CC0 sprite pack when assets/player is present; string-art dwarf otherwise.
        _playerSprite = PlayerSprite.TryLoad(GraphicsDevice);

        // Pixel-art dwarf — 8 wide × 12 tall. Drawn rotated so local-up always points at outward radial.
        _dwarfTex = Renderer.BuildSprite(GraphicsDevice, new[]
        {
            "..HHHH..",
            ".HhhhhH.",
            "HHHHHHHH",
            "DDDDDDDD",
            "..esse..",
            ".BBBBBB.",
            "BBBBBBBB",
            ".BBBBBB.",
            "RRRRRRRR",
            "KKKYKKKK",
            "PPPPPPPP",
            "FF..FF..",
        }, new Dictionary<char, Color>
        {
            ['.'] = Color.Transparent,
            ['H'] = new Color(170, 165, 60),   // helmet brass
            ['h'] = new Color(220, 215, 130),  // helmet highlight
            ['D'] = new Color(40, 35, 30),     // helmet brim
            ['s'] = new Color(230, 180, 140),  // skin
            ['e'] = new Color(20, 15, 10),     // eyes
            ['B'] = new Color(195, 130, 60),   // beard
            ['R'] = new Color(160, 50, 40),    // coat red
            ['K'] = new Color(40, 25, 20),     // belt
            ['Y'] = new Color(230, 190, 70),   // brass buckle
            ['P'] = new Color(95, 55, 35),     // pants
            ['F'] = new Color(35, 25, 18),     // boots
        });
    }

    protected override void Update(GameTime gameTime)
    {
        _updSw.Restart();
        UpdateFrame(gameTime);
        _updSw.Stop();
        _updateMs = _updateMs * 0.9f + (float)_updSw.Elapsed.TotalMilliseconds * 0.1f;
    }

    private void UpdateFrame(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        _totalTime += dt;
        _transitionFlash = MathF.Max(0f, _transitionFlash - dt * 1.6f);

        // Esc quits — except while the crafting menu is open, where the menu's own handler
        // consumes the same press edge to close itself (previously Esc quit the whole game
        // out from under the menu). Edge-triggered so the close-press doesn't also quit.
        if (Pressed(keys, _prevKeys, Keys.Escape)
            && !(_screen == GameScreen.Playing && (_craftingMenu.Open || _debugMenu.Open || _loadoutOpen))
            && !(_screen == GameScreen.Space && (_upgradesOpen || _surveyOpen)))
            Exit();

        // F12 → defer one-shot screenshot until end of next Draw, where the backbuffer
        // holds the fully composited frame (post lighting/bloom/vignette).
        if (Pressed(keys, _prevKeys, Keys.F12)) _screenshotPending = true;

        // F6 cycles the master volume on any screen; the step persists across sessions.
        if (Pressed(keys, _prevKeys, Keys.F6))
        {
            _meta.VolumeStep = (_meta.VolumeStep + 1) % VolumeSteps.Length;
            _sfx.Master = VolumeSteps[_meta.VolumeStep];
            _sfx.Play("ui", 1f);
            _toast = $"SOUND: {VolumeNames[_meta.VolumeStep]}";
            _toastTimer = 2f;
        }

        // Headless verification hook: DM_AUTOSHOT=<seconds> takes a screenshot at that wall
        // time and every 5s after — lets tooling capture frames without input access.
        if (_autoShotAt <= _totalTime)
        {
            _screenshotPending = true;
            _autoShotAt += 5f;
        }

        if (_screen == GameScreen.Space)
        {
            UpdateSpace(keys, mouse, dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (_screen == GameScreen.GameOver)
        {
            // R returns you to your ship, parked at the world the run ended on.
            if (Pressed(keys, _prevKeys, Keys.R)) EnterSpace(PlanetDefs.IndexOf(_run.Def));
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Parked in orbit: the player chooses when to drop, where to drop, or to leave.
        if (_orbiting)
        {
            UpdateOrbit(dt, keys);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Rover descent owns the frame: steer the pod, everything else waits for touchdown.
        if (_landing)
        {
            UpdateLanding(dt, keys);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Liftoff cinematic owns the frame: no player control, no crafting — just the climb.
        if (_launching)
        {
            UpdateLaunch(dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Orbital ascent: the player steers the climbing rocket to the mothership.
        if (_ascending)
        {
            UpdateAscent(dt, keys);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Crafting menu intercepts most input — world keeps simulating but movement/mining
        // stops so the player isn't fighting the game while shopping for upgrades.
        if (_craftingMenu.Open)
        {
            _craftingMenu.Update(keys, _prevKeys, ApplyCraft);
            _run.Physics.Update(dt);
            _particles.Update(dt, _run.Planet);
            _run.Cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.C)) { _craftingMenu.Show(); _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return; }

        // F9 — developer boss-spawn menu. While open it intercepts input (like crafting): the
        // world keeps ticking but player control is suspended until a boss is picked or it closes.
        if (_debugMenu.Open)
        {
            _debugMenu.Update(keys, _prevKeys);
            _run.Physics.Update(dt);
            _particles.Update(dt, _run.Planet);
            _run.Cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return;
        }
        if (Pressed(keys, _prevKeys, Keys.F9))
        {
            if (!_debugMenu.Open) _debugMenu.SetEntries(BuildDebugEntries());
            _debugMenu.Toggle();
            _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return;
        }

        // F5 — suspend-save in place (also happens automatically on quit). Resume from the
        // star map with R. DM_AUTOSAVE=<seconds> triggers the same save on a timer for
        // headless testing, since synthetic key input isn't available on this platform.
        if (Pressed(keys, _prevKeys, Keys.F5) || _autoSaveAt <= _run.RunTime)
        {
            _autoSaveAt = float.PositiveInfinity;
            RunSave.Write(_run);
            _toast = "RUN SAVED";
            _toastTimer = 2.5f;
        }
        _toastTimer -= dt;

        _run.RunTime += dt;
        _sfx.Tick(dt);

        // Movement input — A/D or arrows along the player's local tangent.
        var moveAxis = 0;
        if (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left)) moveAxis -= 1;
        if (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right)) moveAxis += 1;
        // Jump: pass the held state (continuous). Player.Update derives the press edge itself
        // and uses the held state for variable jump height (hold for full apex, tap for short).
        var jumpHeld = keys.IsKeyDown(Keys.Space) || keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up);

        // Vertical input for fly mode: W/Up = ascend, S/Down = descend along local up.
        var verticalAxis = 0;
        if (keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up)) verticalAxis += 1;
        if (keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down)) verticalAxis -= 1;

        // G toggles fly mode for world testing.
        if (Pressed(keys, _prevKeys, Keys.G))
        {
            _run.Player.FlyMode = !_run.Player.FlyMode;
            // God mode carries the full armoury: entering fills empty belt slots with every
            // weapon; leaving strips the ones the player doesn't actually own, so the loaner
            // guns vanish with the mode while crafted gear stays put.
            if (_run.Player.FlyMode)
            {
                foreach (var w in GodWeaponIds) _run.Player.Toolbelt.AutoEquip(w);
            }
            else
            {
                for (var s = 0; s < Toolbelt.SlotCount; s++)
                    if (_run.Player.Toolbelt.Slots[s] is { } wid && IsGodLoanerWeapon(wid))
                        _run.Player.Toolbelt.Slots[s] = null;
            }
        }

        _run.Player.Update(dt, _run.Planet, moveAxis, jumpHeld, verticalAxis);
        TickOxygen(dt);
        TickHazardContact(dt);

        // Camera follows player, rotating so up = away from planet center. DM_BOSSCAM frames
        // the boss instead (testing hook for screenshotting the variants).
        var camFocus = _bossCam && _run.Titan.Hatched ? _run.Titan.Position : _run.Player.Position;
        var up = _run.Planet.UpAt(camFocus);
        _camera.Follow(camFocus, up, dt);

        // Mouse → world cursor. Account for screen shake.
        var screenMouse = new Vector2(mouse.X, mouse.Y);
        var worldCursor = _camera.ScreenToWorld(screenMouse);

        // Number-row 1..9 selects the first nine toolbelt slots (the belt is wider than the
        // number row; slots past 9 are reached by wheel, Q/E, or clicking the HUD). Holding
        // the slot key (or LMB on the slot in the HUD) drives the selected slot's action.
        for (var s = 0; s < Math.Min(9, Toolbelt.SlotCount); s++)
        {
            var k = Keys.D1 + s;
            if (Pressed(keys, _prevKeys, k)) _run.Player.Toolbelt.Selected = s;
        }
        // Mouse-wheel cycles selection — handy for fast tool swaps without leaving the home row.
        if (mouse.ScrollWheelValue != _prevMouse.ScrollWheelValue)
        {
            var dir = mouse.ScrollWheelValue > _prevMouse.ScrollWheelValue ? -1 : 1;
            _run.Player.Toolbelt.Selected = (_run.Player.Toolbelt.Selected + dir + Toolbelt.SlotCount) % Toolbelt.SlotCount;
        }
        // Q/E cycle through *weapons only*, skipping tools and placeables — the fast way to
        // switch guns mid-fight now that the armoury outgrows the number row.
        if (Pressed(keys, _prevKeys, Keys.Q)) CycleWeapon(-1);
        if (Pressed(keys, _prevKeys, Keys.E)) CycleWeapon(+1);

        // Drag-and-drop UI input runs on click edges and updates the carry state. If the click
        // landed on a UI element, we swallow it so the world doesn't also receive it as an LMB
        // dispatch this frame.
        var screenPos = new Vector2(mouse.X, mouse.Y);
        var lmbPressed = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton != ButtonState.Pressed;
        var rmbPressed = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton != ButtonState.Pressed;
        var clickConsumed = _invUi.HandleClick(screenPos, lmbPressed, rmbPressed, _run.Player);

        // Held LMB activates the selected slot's action. UseSelectedSlot is the single place
        // that maps id → in-world action (mine / shoot / place / throw / heal / …).
        var shootCdBefore = _run.Player.ShootCooldown;
        if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed && !_invUi.Carrying)
        {
            UseSelectedSlot(worldCursor);
        }
        // A weapon fired iff the shoot cooldown jumped up this frame — one sound per shot,
        // covering every gun/thrown weapon without touching each fire method. Each weapon
        // picks its own voice via ItemDef.ShotSound (falling back to the generic pew).
        if (_run.Player.ShootCooldown > shootCdBefore + 0.001f)
        {
            var shot = "shoot";
            if (_run.Player.Toolbelt.Current is { } cur
                && _items.TryGetValue(cur, out var cdef) && cdef.ShotSound is { } snd)
                shot = snd;
            PlayAt(shot, _run.Player.Position, 0.5f,
                pitch: MathHelper.Clamp(0.4f - _run.Player.ShootCooldown, -0.3f, 0.4f), minGap: 0.03f);
        }

        // T recalls to the last placed Beacon — kept as a key because recall is a unique
        // verb that doesn't fit the "use selected slot" pattern (the beacon slot already
        // does *placement*; recall is a different action on the same data).
        if (Pressed(keys, _prevKeys, Keys.T) && _run.Player.BeaconWorld is { } bp)
        {
            BeaconRecall(bp);
        }

        // Launch the completed ship with L while standing at the pad.
        if (Pressed(keys, _prevKeys, Keys.L)) TryLaunchShip();

        // Storage depot: B banks raw mats, N withdraws the stash — when standing at the depot.
        if (NearDepot())
        {
            if (Pressed(keys, _prevKeys, Keys.B)) DepositToBank();
            if (Pressed(keys, _prevKeys, Keys.N)) WithdrawFromBank();
        }

        // Physics + particles + cells update.
        _run.Physics.Update(dt);
        _particles.Update(dt, _run.Planet);

        // Sweep dust within a body's reach into the inventory. Each cell carries a fractional
        // resource amount tied to its source TileKind; Cells handles the per-id accumulator and
        // hands back whole units once they cross 1. Done before the cells tick so the
        // collection's WakeNeighbors calls land in `_next`, which the upcoming Update swaps into
        // `_active` and processes immediately — dust above a collected cell falls the same frame
        // instead of one frame later.
        // The Ore Magnet tiers stretch the sweep the dwarf hoovers loose material from.
        var picked = _run.Cells.CollectInRadius(_run.Player.Position,
            _run.Player.Radius + _run.Player.PickupReach);
        if (picked is not null)
        {
            foreach (var (id, count) in picked)
                _run.Player.Inventory.Add(id, count);
            _sfx.Play("pickup", 0.4f, 0.2f, 0f, minGap: 0.11f);
        }

        _run.Cells.Update(dt);
        if (_run.Physics.CollapsesThisTick > 0)
        {
            _run.Shake = MathF.Max(_run.Shake, MathHelper.Clamp(_run.Physics.CollapsesThisTick / 80f, 0f, 1.5f));
            _sfx.Play("collapse", MathHelper.Clamp(_run.Physics.CollapsesThisTick / 40f, 0.25f, 1f), 0f, 0f, minGap: 0.4f);
        }
        UpdateCaveInWarning(dt);

        // Earthquakes — global shake every so often.
        _run.EarthquakeTimer -= dt;
        if (_run.EarthquakeTimer <= 0)
        {
            var anglesAround = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var pos = _run.Planet.Center + new Vector2(MathF.Cos(anglesAround), MathF.Sin(anglesAround)) * _run.Planet.Radius * Planet.TileSize * 0.5f;
            _run.Physics.Earthquake(pos, 200f, 2);
            _run.EarthquakeTimer = (30f + (float)Random.Shared.NextDouble() * 20f) * _run.Def.QuakeScale;
            _run.Shake = MathF.Max(_run.Shake, 1.0f);
        }

        // Population upkeep — cave dwellers, surface herds, sky flyers (see SpawnDirector).
        SpawnDirector.Update(dt, _run);

        // Ambient events — meteor strikes + magma surges (see AmbientDirector).
        var ambient = AmbientDirector.Update(dt, _run, _particles);
        if (ambient.Surge)
        {
            _run.Shake = MathF.Max(_run.Shake, 0.7f);
            PlayAt("collapse", ambient.SurgePos, 0.9f, pitch: -0.3f);
        }
        if (ambient.FlareWarned)
        {
            _toast = "! SOLAR FLARE INBOUND - GET UNDERGROUND !";
            _toastTimer = 6.5f;
            _sfx.Play("creak", 0.9f, pitch: 0.4f);
        }
        if (ambient.FlareStruck)
        {
            _toast = "SOLAR FLARE - THE SURFACE IS BURNING";
            _toastTimer = 3.5f;
            _sfx.Play("explode", 0.7f, pitch: -0.5f);
        }
        if (ambient.BlizzardStarted)
        {
            _toast = "BLIZZARD - GET OUT OF THE WIND";
            _toastTimer = 4f;
            _sfx.Play("creak", 0.8f, pitch: 0.6f);
        }
        if (ambient.AcidRainStarted)
        {
            _toast = "! TOXIC CLOUD - ACID RAIN. GET UNDER OBSIDIAN OR DIG DEEP !";
            _toastTimer = 5f;
            _sfx.Play("creak", 0.9f, pitch: -0.2f);
        }
        // Exposure: both disasters punish standing in surface air; underground is safe.
        var exposed = DepthBelowSurface() < OxygenRules.AirDepth;
        if (exposed && _run.FlareActive > 0f)
        {
            _run.Player.TakeDamage(7f * dt);
            if (Random.Shared.Next(4) == 0)
                _particles.EmitDust(_run.Player.Position + new Vector2(
                    (float)(Random.Shared.NextDouble() - 0.5) * 20f,
                    (float)(Random.Shared.NextDouble() - 0.5) * 20f), 1.5f);
        }
        if (exposed && _run.BlizzardActive > 0f)
            _run.Player.TakeDamage(2.5f * dt);
        for (var i = _run.Meteors.Count - 1; i >= 0; i--)
        {
            var m = _run.Meteors[i];
            m.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player, _particles);
            if (m.Dead)
            {
                _particles.EmitImpact(m.Position, ProjectileKind.Rocket);
                _run.Shake = MathF.Max(_run.Shake, 0.9f);
                PlayAt("explode", m.Position, 1f, pitch: -0.2f);
                _run.Meteors.RemoveAt(i);
            }
        }

        // Update entities. Creatures that have drifted far outside the player's neighbourhood
        // are recycled — they'd never be met again, they eat sim time, and every recycled
        // body frees local-population budget so the spawner keeps the area around the player
        // stocked as they travel.
        for (var i = _run.Creatures.Count - 1; i >= 0; i--)
        {
            var c = _run.Creatures[i];
            c.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player);
            if (c.Health <= 0)
            {
                // Killed — leave a harvestable corpse where it fell. Distance culls (below)
                // don't: those creatures just wandered out of the simulation bubble.
                _run.Corpses.Add(new Corpse(c.Position, c.Kind, c.Radius));
                _particles.EmitDust(c.Position, 5f);
                // Spore bats burst into a choking puff — kill them at arm's length.
                if (c.Kind == CreatureKind.SporeBat)
                {
                    var (sx, sy) = _run.Planet.WorldToTile(c.Position);
                    _run.Cells.SpawnInTile(sx, sy, Material.Gas, Cells.Density * 2);
                }
                _run.Creatures.RemoveAt(i);
            }
            else if ((c.Position - _run.Player.Position).LengthSquared() > 1000f * 1000f)
            {
                _run.Creatures.RemoveAt(i);
            }
        }

        // Corpses — settle under gravity, decay on a timer, and are harvested for materials
        // by walking over them (same sweep-up feel as dust collection).
        for (var i = _run.Corpses.Count - 1; i >= 0; i--)
        {
            var corpse = _run.Corpses[i];
            corpse.Update(dt, _run.Planet);
            var reach = _run.Player.Radius + corpse.Radius + 3f;
            if (!corpse.Harvested && (corpse.Position - _run.Player.Position).LengthSquared() < reach * reach)
            {
                foreach (var (id, count) in Corpse.DropsFor(corpse.Kind))
                    _run.Player.Inventory.Add(id, count);
                corpse.Harvested = true;
                _particles.EmitDust(corpse.Position, 6f);
            }
            if (corpse.Expired || (corpse.Position - _run.Player.Position).LengthSquared() > 1200f * 1200f)
                _run.Corpses.RemoveAt(i);
        }

        for (var i = _run.Projectiles.Count - 1; i >= 0; i--)
        {
            var p = _run.Projectiles[i];
            p.Update(dt, _run.Planet, _run.Physics, _run.Cells, _particles);
            _particles.EmitTrail(p);   // rocket exhaust, fuse sparks, beam motes, …

            // Body hits (creatures + titan): Combat sweeps the frame's travel segment so fast
            // rounds can't skip over bodies, lands hits in path order with pierce accounting,
            // detonates contact explosives on the first body struck, and applies blast AoE
            // when an explosive dies. Sentries stay projectile-transparent — creature contact
            // damage to them is handled in the sentry-update block below.
            Combat.ResolveHits(p, _run.Creatures, _run.Titan, _run.Planet, _run.Physics, _run.Cells, _particles);

            if (p.Dead)
            {
                _particles.EmitImpact(p.Position, p.Kind);
                if (p.ExplosionRadius > 0f)
                {
                    _run.Shake = MathF.Max(_run.Shake, MathF.Min(1.5f, p.ExplosionRadius / 60f));
                    PlayAt("explode", p.Position, MathHelper.Clamp(p.ExplosionRadius / 60f, 0.4f, 1f),
                        pitch: MathHelper.Clamp(0.3f - p.ExplosionRadius / 200f, -0.4f, 0.3f), minGap: 0.05f);
                }
                _run.Projectiles.RemoveAt(i);
            }
        }

        // Sentries — auto-fire at nearby creatures, take contact damage, expire when dead.
        for (var i = _run.Sentries.Count - 1; i >= 0; i--)
        {
            var s = _run.Sentries[i];
            s.Update(dt, _run.Planet, _run.Creatures, FireSentryShot);
            // Creatures that touch a sentry chew it down. Each contact frame deals
            // 8 dmg/sec → ~3.7s to kill a fresh sentry.
            foreach (var c in _run.Creatures)
            {
                if ((c.Position - s.Position).LengthSquared() < (c.Radius + s.Radius) * (c.Radius + s.Radius))
                {
                    s.TakeHit(8f * dt);
                }
            }
            if (s.Dead)
            {
                _particles.EmitDust(s.Position, 8f);
                _run.Shake = MathF.Max(_run.Shake, 0.3f);
                _run.Sentries.RemoveAt(i);
            }
        }

        if (_run.Titan.Health > 0)
            _run.Titan.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player.Position, _run.Boulders, _run.TitanShots);
        // Hatch feedback: a heavy shake + shell-burst the frame the egg cracks open.
        if (_run.Titan.JustHatched)
        {
            _run.Titan.JustHatched = false;
            _run.Shake = MathF.Max(_run.Shake, 1.4f);
            _particles.EmitDust(_run.Titan.Position, 30f);
            PlayAt("hatch", _run.Titan.Position, 1f);
        }
        // Melee shockwave from a Kong slam / Sandworm eruption — quake already fired inside the
        // Titan; here we knock back and hurt the player if they're inside the radius.
        if (_run.Titan.PendingShockwave is { } sw)
        {
            _run.Titan.PendingShockwave = null;
            _run.Shake = MathF.Max(_run.Shake, 1.0f);
            _particles.EmitDust(sw.pos, 24f);
            PlayAt("explode", sw.pos, 1f, pitch: -0.3f);
            var toPlayer = _run.Player.Position - sw.pos;
            var d = toPlayer.Length();
            if (d < sw.radius)
            {
                _run.Player.TakeDamage(sw.damage * (1f - d / sw.radius));
                if (d > 0.01f) _run.Player.Velocity += toPlayer / d * 260f;
            }
        }
        // Leatherback's EMP: inside the radius the dwarf's tech dies — jetpack and energy
        // weapons — for the pulse duration (Player.EmpTimer gates both). Damage came from
        // the pressure-wave shockwave above; this is the systems kill.
        if (_run.Titan.PendingEmp is { } emp)
        {
            _run.Titan.PendingEmp = null;
            _run.Shake = MathF.Max(_run.Shake, 1.2f);
            _particles.EmitDust(emp.pos, 34f);
            PlayAt("shoot_beam", emp.pos, 1f, pitch: -0.4f);
            if ((_run.Player.Position - emp.pos).Length() < emp.radius)
            {
                _run.Player.EmpTimer = emp.seconds;
                _toast = "EMP BURST - JETPACK + ENERGY WEAPONS OFFLINE";
                _toastTimer = 2.5f;
            }
        }
        // Slaying the titan no longer ends the visit — the rocket is the only way off-world.
        // The kill banks a titan soul (foundry currency aboard the mothership) exactly once,
        // on the frame health crosses zero; resumed saves of an already-dead titan can't
        // re-award because both sides of the crossing are non-positive after load.
        if (_run.Titan.Health <= 0 && _prevTitanHealth > 0)
        {
            _meta.TitansDefeated++;
            // Soul keyed off the titan actually fought — planets with a TitanPool roll a
            // different kaiju per visit, so the def's static kind can be wrong.
            var kindKey = _run.Titan.Kind.ToString();
            _meta.TitanSouls[kindKey] = _meta.TitanSouls.GetValueOrDefault(kindKey) + 1;
            _meta.Save();
            _run.Shake = MathF.Max(_run.Shake, 1.6f);
            _particles.EmitDust(_run.Titan.Position, 44f);
            PlayAt("explode", _run.Titan.Position, 1f, pitch: -0.4f);
            _toast = $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} SLAIN - SOUL CLAIMED";
            _toastTimer = 3.5f;
        }
        _prevTitanHealth = _run.Titan.Health;

        for (var i = _run.Boulders.Count - 1; i >= 0; i--)
        {
            var b = _run.Boulders[i];
            b.Update(dt, _run.Planet, _run.Physics, _run.Player);
            if (b.Dead)
            {
                _particles.EmitDust(b.Position, 14f);
                _run.Shake = MathF.Max(_run.Shake, 0.6f);
                _run.Boulders.RemoveAt(i);
            }
        }

        // Titan ranged attacks (Godzilla flame, Mecha laser) — self-contained enemy shots.
        for (var i = _run.TitanShots.Count - 1; i >= 0; i--)
        {
            var shot = _run.TitanShots[i];
            shot.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player);
            // Trailing smoke: only a fraction of flame grains puff each frame — emitting for
            // every grain every frame (with dozens live during a breath) was a real cost.
            if (shot.Kind == TitanShotKind.Flame && Random.Shared.Next(3) == 0)
                _particles.EmitDust(shot.Position, 2f);
            if (shot.Dead)
            {
                _particles.EmitImpact(shot.Position, ProjectileKind.Cannon);
                _run.TitanShots.RemoveAt(i);
            }
        }

        // Hurt sting whenever HP drops (any source), throttled so a fire-breath tick-stream
        // doesn't stack into a drone.
        if (_run.Player.Health < _prevPlayerHealth - 0.5f)
            _sfx.Play("hurt", 0.6f, 0f, 0f, minGap: 0.18f);
        _prevPlayerHealth = _run.Player.Health;

        if (_run.Player.Health <= 0)
        {
            _meta.Deaths++;
            _meta.Save();
            EndRun(_run.Player.Oxygen <= 0f
                ? "You suffocated in the deep. Press R to return to your ship."
                : "You died. Press R to return to your ship.");
        }

        // Apply shake decay to camera.
        _run.Shake = MathF.Max(0, _run.Shake - dt * 1.4f);

        // The mothership keeps sailing its orbit while you're on the ground — glance up (or
        // follow the HUD arrow) to see where the rendezvous will be.
        _run.MothershipAngle += Session.StationDriftRate * dt;

        // Geo Scanner sweep: refresh the nearest fuel / signature-ore fixes on a timer (the
        // ring-band search is cheap, but not run-it-every-frame cheap).
        if (Upgrades.Owned(_meta, "scanner"))
        {
            _scanTimer -= dt;
            if (_scanTimer <= 0f)
            {
                _scanTimer = 1.5f;
                _scanFuel = Scanner.FindNearest(_run.Planet, _run.Player.Position, TileKind.FuelOre, 620f);
                _scanOre = Scanner.FindNearest(_run.Planet, _run.Player.Position,
                    Scanner.OreTileFor(_run.Def.ShipOre), 620f);
            }
        }

        _prevKeys = keys;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    /// <summary>Weapon ids god mode loans out, in belt-fill order — the complete armoury.
    /// Ten entries — exactly the free slots left beside the three intrinsic tools.</summary>
    private static readonly string[] GodWeaponIds =
    {
        "pistol", "machine_gun", "laser", "laser_cannon", "rocket_launcher",
        "cannon", "dynamite", "tnt", "harpoon", "nuke",
    };

    /// <summary>Step the belt selection to the next/previous slot holding a weapon, wrapping
    /// around and skipping tools/placeables. No-op if no weapon is on the belt.</summary>
    private void CycleWeapon(int dir)
    {
        var belt = _run.Player.Toolbelt;
        for (var step = 1; step <= Toolbelt.SlotCount; step++)
        {
            var s = (((belt.Selected + dir * step) % Toolbelt.SlotCount) + Toolbelt.SlotCount) % Toolbelt.SlotCount;
            if (belt.Slots[s] is { } id && IsWeaponId(id))
            {
                belt.Selected = s;
                return;
            }
        }
    }

    /// <summary>Mining wrapper for UseSelectedSlot — handles the per-tool chip / hammer-shard /
    /// drill-jet effects that used to live inline in Update. Pulled out so the dispatcher
    /// stays one-liner-per-case.</summary>
    private void DoMine(Vector2 worldCursor, MiningTool tool)
    {
        // Where the strike actually lands — the swung tools ray out from the body toward the
        // aim, so effects must key off the resolved tile, not the raw cursor.
        var target = _run.Player.ResolveMineTarget(_run.Planet, worldCursor, tool);

        // Continuous drill chip-stream every frame the drill is held — fires during cooldown
        // so the swing reads as continuous.
        if (tool == MiningTool.Drill && !_run.Player.FlyMode && target is { } dt)
        {
            var tilePos = _run.Planet.TileToWorld(dt.X, dt.Y);
            var dir = tilePos - _run.Player.Position;
            if (dir.LengthSquared() > 0.001f) dir.Normalize();
            _particles.EmitDrillChips(tilePos, dir, _run.Planet.Get(dt.X, dt.Y));
        }

        // Mining laser: a continuous beam from the dwarf to the strike point (or full beam
        // length into the dark when aimed at nothing), emitted every held frame so it reads
        // as a stream rather than swings. Low steady hum instead of the pick-tick.
        if (tool == MiningTool.MiningLaser)
        {
            var aim = worldCursor - _run.Player.Position;
            if (aim.LengthSquared() > 0.001f)
            {
                aim.Normalize();
                var beamEnd = target is { } ht
                    ? _run.Planet.TileToWorld(ht.X, ht.Y)
                    : _run.Player.Position + aim * Player.MiningLaserRange;
                _particles.EmitMiningBeam(_run.Player.Position + aim * 4f, beamEnd, hitting: target is not null);
                PlayAt("shoot_laser", _run.Player.Position, 0.16f, pitch: -0.4f, minGap: 0.12f);
            }
        }
        else
        {
            // Pick-tick on each swing (throttled + pitch-jittered so it doesn't machine-gun).
            PlayAt("dig", worldCursor, 0.35f,
                pitch: 0.1f + (float)Random.Shared.NextDouble() * 0.3f, minGap: 0.09f);
        }

        var broken = _run.Player.TryMine(_run.Planet, _run.Physics, worldCursor, tool);
        if (broken is { } bk && target is { } bt)
        {
            if (Tiles.Drop(bk) is not null) _meta.TotalOreMined++;
            var depth = _run.Planet.Radius - (int)((_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize);
            if (depth > _meta.DeepestDepth) _meta.DeepestDepth = depth;
            var (btx, bty) = (bt.X, bt.Y);
            if (tool == MiningTool.Hammer && Tiles.Hardness(bk) >= 4)
            {
                _particles.EmitHammerImpact(_run.Planet.TileToWorld(btx, bty), bk);
                _run.Shake = MathF.Max(_run.Shake, 0.4f);
            }
            else
            {
                _particles.EmitChips(_run.Planet.TileToWorld(btx, bty), bk);
            }
            _run.Cells.SpawnDustInTile(btx, bty, bk);
            PlayAt("break", _run.Planet.TileToWorld(btx, bty), 0.6f,
                pitch: -0.1f + (float)Random.Shared.NextDouble() * 0.25f);
        }
    }

    /// <summary>Fire a basic bullet from the "bullets" slot. Intrinsic — always available;
    /// no cannon needed. Low damage, fast cadence.</summary>
    private void FireBullet(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 220, 110));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 420f, 6f, 1.4f, ProjectileKind.Bullet));
        _run.Player.ShootCooldown = 0.18f;
    }

    /// <summary>Pistol: the crafted sidearm — twice the bullet's punch at half the cadence.</summary>
    private void FirePistol(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 235, 160));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 480f, 14f, 1.5f, ProjectileKind.Pistol));
        _run.Player.ShootCooldown = 0.32f;
    }

    /// <summary>Machine gun: weak rounds at a blistering cadence with a small random spread,
    /// so held fire hoses an area rather than drilling one pixel.</summary>
    private void FireMachineGun(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var spread = ((float)Random.Shared.NextDouble() - 0.5f) * 0.14f;
        var c = MathF.Cos(spread);
        var s = MathF.Sin(spread);
        dir = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 210, 120));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 460f, 4f, 1.2f, ProjectileKind.MachineGun));
        _run.Player.ShootCooldown = 0.06f;
    }

    /// <summary>Laser: near-instant energy bolt that pierces up to three creatures. No
    /// crater — it cooks flesh, not rock.</summary>
    private void FireLaser(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 90, 90));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 900f, 18f, 0.6f, ProjectileKind.Laser));
        _run.Player.ShootCooldown = 0.14f;
    }

    /// <summary>Laser cannon: a heavy energy lance that drills straight through wall after
    /// wall and skewers whole columns of creatures, vaporising a thin tunnel as it goes.
    /// Slow cadence — one deliberate beam, not a stream.</summary>
    private void FireLaserCannon(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(120, 225, 255));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 800f, 40f, 1.0f, ProjectileKind.LaserCannon));
        _run.Player.ShootCooldown = 0.55f;
        _run.Shake = MathF.Max(_run.Shake, 0.25f);
    }

    /// <summary>Rocket: straight-flying launcher round; explodes on contact with a real
    /// crater. Ammo consumption is handled by the dispatcher (god mode fires free).</summary>
    private void FireRocket(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 160, 70));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 8f, dir * 250f, 60f, 2.5f, ProjectileKind.Rocket));
        _run.Player.ShootCooldown = 0.75f;
        _run.Shake = MathF.Max(_run.Shake, 0.3f);
    }

    /// <summary>TNT: a heavy satchel charge. Barely throwable — a short weighty lob with a
    /// long fuse — but the biggest non-nuke blast in the game. Placement tool, not artillery.</summary>
    private void FireTnt(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var up = _run.Planet.UpAt(_run.Player.Position);
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 5f, dir * 70f + up * 50f, 120f, 2.5f, ProjectileKind.Tnt));
        _run.Player.ShootCooldown = 0.6f;
    }

    /// <summary>Fire the cannon. Consumes the highest-tier shell in inventory before falling
    /// back to the regular cannon round. Per-shell stats are kept here so the dispatch stays
    /// declarative.</summary>
    private void FireCannon(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 130, 50));
        if (_run.Player.Inventory.TryConsume("ammo_diamond", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 300f, 80f, 1.8f, ProjectileKind.CannonDiamond));
            _run.Player.ShootCooldown = 0.7f;
            _run.Shake = MathF.Max(_run.Shake, 0.5f);
        }
        else if (_run.Player.Inventory.TryConsume("ammo_sapphire", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 320f, 35f, 1.7f, ProjectileKind.CannonSapphire));
            _run.Player.ShootCooldown = 0.55f;
        }
        else if (_run.Player.Inventory.TryConsume("ammo_ruby", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 320f, 32f, 1.7f, ProjectileKind.CannonRuby));
            _run.Player.ShootCooldown = 0.55f;
        }
        else if (_run.Player.Inventory.TryConsume("ammo_silver", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 380f, 22f, 1.6f, ProjectileKind.CannonSilver));
            _run.Player.ShootCooldown = 0.45f;
        }
        else
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 320f, 25f, 1.6f, ProjectileKind.Cannon));
            _run.Player.ShootCooldown = 0.55f;
        }
    }

    private void FireNuke(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 90, 230));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 240f, 1500f, 3f, ProjectileKind.Nuke));
        _run.Player.ShootCooldown = 0.6f;
    }

    private void FireDynamite(Vector2 worldCursor)
    {
        // Dynamite is lobbed like a grenade — gravity arcs it. The launch speed is calibrated
        // so a click within mining range lands close to the cursor; far cursors overshoot
        // because gravity drags the shorter-flight throws.
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        var dist = dir.Length();
        dir /= dist;
        // Lob speed scales with throw distance up to a cap. Plus a small upward kick along
        // planet-up so the stick arcs visibly instead of skimming the floor.
        var speed = MathHelper.Clamp(110f + dist * 0.8f, 110f, 220f);
        var up = _run.Planet.UpAt(_run.Player.Position);
        var velocity = dir * speed + up * 60f;
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, velocity, 50f, 2.0f, ProjectileKind.Dynamite));
        _run.Player.ShootCooldown = 0.3f;
    }

    private void FireHarpoon(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 200, 130));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 520f, 600f, 2.2f, ProjectileKind.Harpoon));
        _run.Player.ShootCooldown = 0.8f;
        _run.Shake = MathF.Max(_run.Shake, 0.5f);
    }

    /// <summary>Sentry-emitted bullet. Lower damage and speed than the player's bullet so the
    /// turrets feel like supplementary support, not an instant clear button.</summary>
    private void FireSentryShot(Vector2 muzzle, Vector2 dir)
    {
        _particles.EmitMuzzleFlash(muzzle, dir, new Color(255, 220, 110));
        _run.Projectiles.Add(new Projectile(muzzle, dir * Sentry.BulletSpeed, Sentry.BulletDamage, 1.0f, ProjectileKind.Bullet));
    }

    /// <summary>Beacon recall — instant teleport to the most recent placed beacon. Visual
    /// feedback: dust burst at the depart point, sparkles at the arrival point, brief shake.</summary>
    private void BeaconRecall(Vector2 dest)
    {
        _particles.EmitDust(_run.Player.Position, 8f);
        _run.Player.Position = dest + _run.Planet.UpAt(dest) * (_run.Player.Radius + 2f);
        _run.Player.Velocity = Vector2.Zero;
        _particles.EmitDust(_run.Player.Position, 8f);
        _run.Shake = MathF.Max(_run.Shake, 0.4f);
    }

    private void UseHealPotion()
    {
        _run.Player.Health = MathF.Min(_run.Player.MaxHealth, _run.Player.Health + 30f);
        // Visual: a small green sparkle puff anchored to the player.
        _particles.EmitDust(_run.Player.Position, 6f);
    }

    /// <summary>Hearty feast — the big heal cooked from harvested creature meat.</summary>
    private void UseFeast()
    {
        _run.Player.Health = MathF.Min(_run.Player.MaxHealth, _run.Player.Health + 60f);
        _particles.EmitDust(_run.Player.Position, 8f);
    }

    /// <summary>Core drill — usable only when the player is within the innermost rings of
    /// the planet (i.e. effectively standing on the Core's threshold). The Core is a
    /// synthetic tile (Planet.Get returns it for x &lt; 0; it's not stored in the array), so
    /// "drilling" it is a distance-to-centre check rather than a tile mutation. On success,
    /// triggers the planet-pierce victory ending — biggest possible escalation.</summary>
    private void TryCoreDrill()
    {
        var distFromCentre = (_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize;
        // Innermost ~3 rings count as "at the core". Tunable; keeping it tight rewards the
        // player for actually digging all the way to the bottom.
        if (distFromCentre > Planet.RingMin + 3f) return;

        _particles.EmitImpact(_run.Planet.Center, ProjectileKind.Nuke);
        _run.Shake = MathF.Max(_run.Shake, 1.5f);
        // Piercing the core no longer ends the run — it yields the planet's CORE SHARD, the
        // warp-drive material (one per world, straight into meta: shards are too important
        // to lose to a death on the climb back out). All five shards let the mothership warp
        // to the Rift.
        if (_run.Def.Id != "rift" && !_meta.CoreShards.Contains(_run.Def.Id))
        {
            _meta.CoreShards.Add(_run.Def.Id);
            _meta.Save();
            _toast = $"CORE SHARD SECURED ({_meta.CoreShards.Count}/{PlanetDefs.WarpShardsNeeded}) - NOW GET BACK UP";
        }
        else
        {
            _toast = _run.Def.Id == "rift"
                ? "THE RIFT'S CORE YIELDS NOTHING - SLAY ITS TITAN AND ESCAPE"
                : "CORE ALREADY PIERCED - THE SHARD IS ABOARD";
        }
        _toastTimer = 4f;
    }

    /// <summary>The developer spawn menu's rows — bosses plus the two rocket shortcuts. Rebuilt
    /// each time the menu opens so the delegates close over the current run.</summary>
    private DebugMenu.Entry[] BuildDebugEntries() => new DebugMenu.Entry[]
    {
        new("Cinderwyrm  (fire breath)",  () => SpawnDebugTitan(TitanKind.Godzilla)),
        new("Mecha-Titan (drill laser)",  () => SpawnDebugTitan(TitanKind.Mecha)),
        new("Shai-Hulud  (slither/bite)", () => SpawnDebugTitan(TitanKind.Sandworm)),
        new("Stone Ape   (leap slam)",    () => SpawnDebugTitan(TitanKind.Kong)),
        new("Knifehead   (gore charge)",  () => SpawnDebugTitan(TitanKind.Knifehead)),
        new("Otachi      (acid spray)",   () => SpawnDebugTitan(TitanKind.Otachi)),
        new("Leatherback (EMP burst)",    () => SpawnDebugTitan(TitanKind.Leatherback)),
        new("Raiju       (dash chain)",   () => SpawnDebugTitan(TitanKind.Raiju)),
        new("Slattern    (spike barrage)",() => SpawnDebugTitan(TitanKind.Slattern)),
        new("Pyrodactyl  (lava rain)",    () => SpawnDebugTitan(TitanKind.Pyrodactyl)),
        new("Vitriodactyl (acid rain)",   () => SpawnDebugTitan(TitanKind.Vitriodactyl)),
        new("Rocket — fuelled, launch-ready", () => SpawnDebugShip(fuelled: true)),
        new("Rocket — dry (mine fuel first)", () => SpawnDebugShip(fuelled: false)),
    };

    /// <summary>Debug-menu action: plant a fully-built, launch-ready ship at the player's feet,
    /// skipping the pad/hull/engine/nav-core craft chain. When <paramref name="fuelled"/> the
    /// tank is topped to launch spec so L lifts off at once; otherwise it's empty and you must
    /// mine "fuel" to fill it — the intended path, just with the build steps skipped.</summary>
    private void SpawnDebugShip(bool fuelled)
    {
        PlaceLaunchPad();
        _run.ShipStage = 3;
        _run.ShipFuel = fuelled ? FuelToLaunch : 0;
        _toast = fuelled ? "SPAWNED FUELLED ROCKET — L AT PAD TO LAUNCH"
                         : "SPAWNED DRY ROCKET — MINE FUEL, THEN L AT PAD";
        _toastTimer = 2.5f;
    }

    /// <summary>Debug-menu action: replace the current boss with a freshly-hatched one of the
    /// chosen kind, spawned a short arc from the player and already aggroed. Any in-flight boss
    /// ordnance from the previous boss is cleared so the new fight starts clean.</summary>
    private void SpawnDebugTitan(TitanKind kind)
    {
        var rel = _run.Player.Position - _run.Planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X) + 0.22f;
        _run.Titan = new Titan(_run.Planet, ang, kind);
        _run.Titan.Hatch();
        _run.TitanShots.Clear();
        _run.Boulders.Clear();
        _toast = $"SPAWNED {TitanName(kind).ToUpperInvariant()}";
        _toastTimer = 2.5f;
    }

    /// <summary>Display name for a boss variant — used in the victory line and the egg HUD.</summary>
    private static string TitanName(TitanKind kind) => kind switch
    {
        TitanKind.Godzilla    => "Cinderwyrm",
        TitanKind.Mecha       => "Mecha-Titan",
        TitanKind.Sandworm    => "Shai-Hulud",
        TitanKind.Kong        => "Stone Ape",
        TitanKind.Knifehead    => "Knifehead",
        TitanKind.Otachi       => "Otachi",
        TitanKind.Leatherback  => "Leatherback",
        TitanKind.Raiju        => "Raiju",
        TitanKind.Slattern     => "Slattern",
        TitanKind.Pyrodactyl   => "Pyrodactyl",
        TitanKind.Vitriodactyl => "Vitriodactyl",
        _                  => "Titan",
    };

    /// <summary>Every run ending funnels through here: show the game-over overlay and void
    /// the suspend save — a finished run can't be resumed.</summary>
    private void EndRun(string reason)
    {
        _gameOverReason = reason;
        _screen = GameScreen.GameOver;
        RunSave.Delete();
    }

    /// <summary>Install the next ship stage at the pad — the crafted stage goes straight
    /// into the world build site, never through the inventory.</summary>
    private void InstallShipStage()
    {
        _run.ShipStage++;
        if (_run.PadPos is { } sp) _particles.EmitDust(sp, 12f);
        _run.Shake = MathF.Max(_run.Shake, 0.35f);
    }

    /// <summary>The expended rover where it settled: a listing, scorched pod shell with a
    /// cracked dome, one sheared-off skid nearby, and a lazy smoke wisp — the crash site
    /// stays as a landmark for the whole visit.</summary>
    private void DrawRoverWreck(Vector2 pos)
    {
        var up = _run.Planet.UpAt(pos);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y) + 0.38f;   // listing to one side
        var shell = new Color(120, 124, 138);
        var scorch = new Color(62, 58, 60);
        _renderer.DrawRect(pos, new Vector2(9f, 7f), shell, rot);
        _renderer.DrawRect(pos - up * 2.4f, new Vector2(9f, 2.4f), scorch, rot);
        _renderer.DrawRect(pos + up * 3.6f + right * 1.2f, new Vector2(6f, 2.2f), new Color(90, 140, 160), rot);
        // Sheared skid a body-length away, flat on the ground.
        _renderer.DrawRect(pos + right * 9f - up * 2.5f, new Vector2(4.5f, 1.6f), scorch,
            MathF.Atan2(up.X, -up.Y));
        // A thin smoke wisp so the site reads from a distance.
        if (Random.Shared.Next(5) == 0) _particles.EmitDust(pos + up * 4f, 0.8f);
    }

    /// <summary>Drop a sentry just above the player's feet so the dwarf doesn't end up
    /// standing inside it. The position is along planet-up so the turret lands on the
    /// surface rather than burying into a slope.</summary>
    private void PlaceSentryAtFeet()
    {
        var up = _run.Planet.UpAt(_run.Player.Position);
        var pos = _run.Player.Position - up * (_run.Player.Radius + 1f);
        _run.Sentries.Add(new Sentry(pos));
        _particles.EmitDust(pos, 4f);
    }

    /// <summary>Tiles below the planet's baseline surface at the player's current radius —
    /// negative on mountain peaks, ~0 at sea level, rising toward the core. The shared depth
    /// metric for oxygen (here) and the HUD readout.</summary>
    private float DepthBelowSurface()
    {
        var ringsFromCenter = (_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize;
        var surfaceRings = Planet.RingMin + _run.Planet.SurfaceRing;
        return surfaceRings - ringsFromCenter;
    }

    /// <summary>Advance the air supply: refill near the surface, drain with depth (see
    /// <see cref="OxygenRules"/>), and bleed HP once it's empty. Skipped in god mode (kept
    /// topped up so re-entering survival is never an instant suffocation).</summary>
    /// <summary>Cave-in warnings. Sounds a groaning creak the instant a region is condemned
    /// (lead time before it crumbles), and — while condemned tiles hang over the dwarf — flashes
    /// a HUD banner and sifts dust from those tiles so there's a clear "move now" cue during the
    /// tremble window. In polar gravity "over" the dwarf means a larger ring index, so only rock
    /// that would actually fall onto them raises the alarm.</summary>
    private void UpdateCaveInWarning(float dt)
    {
        // Newly condemned this tick → creak, scaled by how much rock just gave way.
        if (_run.Physics.NewlyCondemnedThisTick > 0)
            _sfx.Play("creak",
                MathHelper.Clamp(_run.Physics.NewlyCondemnedThisTick / 24f, 0.3f, 0.9f),
                pitch: -0.15f, minGap: 0.5f);

        _caveInWarn = MathF.Max(0f, _caveInWarn - dt);
        _caveInDust -= dt;

        var pending = _run.Physics.TremblingTiles;
        if (pending.Count == 0) return;

        var pos = _run.Player.Position;
        var (pr, _) = _run.Planet.WorldToTile(pos);
        const float reach = 96f;              // overhead rock this close threatens the dwarf
        var reachSq = reach * reach;
        var sift = _caveInDust <= 0f;         // throttle the dust telegraph
        var threatened = false;

        foreach (var idx in pending)
        {
            var (tx, ty) = _run.Planet.UnIndex(idx);
            if (tx < pr) continue;            // inward of the dwarf — falls away, not onto them
            var tw = _run.Planet.TileToWorld(tx, ty);
            if (Vector2.DistanceSquared(tw, pos) > reachSq) continue;
            threatened = true;
            if (sift) _particles.EmitDust(tw, 2.5f);
            else break;                       // just need the flag once we've sifted this frame
        }

        if (threatened)
        {
            _caveInWarn = 0.5f;
            if (sift) _caveInDust = 0.1f;
        }
    }

    private void TickOxygen(float dt)
    {
        var p = _run.Player;
        var max = p.EffectiveMaxOxygen;
        if (p.FlyMode)
        {
            p.Oxygen = max;
            return;
        }

        var depth = DepthBelowSurface();
        if (OxygenRules.AtSurfaceAir(depth))
            p.Oxygen = MathF.Min(max, p.Oxygen + OxygenRules.RefillRate * dt);
        else
            p.Oxygen = MathF.Max(0f, p.Oxygen - OxygenRules.DrainPerSecond(depth, _run.Def.OxygenDrainScale) * dt);

        if (p.Oxygen <= 0f)
        {
            // Suffocation ignores armor — no plate stops you drowning in rock.
            p.Health -= OxygenRules.SuffocationDps * dt;
            // Occasional gasp puff so the cause of death reads on-screen.
            if (Random.Shared.NextDouble() < dt * 3f) _particles.EmitDust(p.Position, 3f);
        }
    }

    // Hazard-contact tuning (per second while the dwarf's body overlaps the cells).
    private const float LavaBurnDps = 42f;   // ~2.4s from full — a lava bath is near-instant death
    private const float AcidBurnDps = 20f;   // corrosive but survivable if you scramble out
    private const float GasChokeOxygen = 26f; // air burned by breathing gas, on top of depth drain

    /// <summary>Body-contact hazards from the cell sim: lava sears, acid corrodes (both bypass
    /// armor — no plate stops molten rock or acid), and gas chokes by burning air. God mode is
    /// immune. This is also the only place lava damages the dwarf at all — before hazard cells
    /// the dwarf could wade through it unharmed.</summary>
    private void TickHazardContact(float dt)
    {
        var p = _run.Player;
        if (p.FlyMode) return;

        var (lava, acid, gas) = _run.Cells.SampleHazardsNear(p.Position, p.Radius + 1.5f);
        if (lava > 0)
        {
            p.Health -= LavaBurnDps * dt;
            _run.Shake = MathF.Max(_run.Shake, 0.3f);
            if (Random.Shared.NextDouble() < dt * 20f) _particles.EmitImpact(p.Position, ProjectileKind.Cannon);
        }
        if (acid > 0)
        {
            p.Health -= AcidBurnDps * dt;
            if (Random.Shared.NextDouble() < dt * 12f) _particles.EmitDust(p.Position, 3f);
        }
        if (gas > 0)
            p.Oxygen = MathF.Max(0f, p.Oxygen - GasChokeOxygen * dt);
    }

    /// <summary>L at the finished pad: pour any mined fuel into the tanks, and once they're
    /// full, fire the liftoff cinematic. Needs all three stages installed and the dwarf
    /// standing at the pad. Fuel loads incrementally, so partial progress survives wandering
    /// off to mine more.</summary>
    private void TryLaunchShip()
    {
        if (_launching) return;
        if (_run.ShipStage < 3 || _run.PadPos is not { } pad) return;
        if ((_run.Player.Position - pad).Length() > 60f) return;

        // Top up the tanks from carried fuel, capped at the launch requirement.
        var need = FuelToLaunch - _run.ShipFuel;
        if (need > 0)
        {
            var have = _run.Player.Inventory.Count("fuel");
            var load = Math.Min(need, have);
            if (load > 0 && _run.Player.Inventory.TryConsume("fuel", load))
            {
                _run.ShipFuel += load;
                need -= load;
                _particles.EmitDust(pad, 6f);
            }
        }

        if (_run.ShipFuel < FuelToLaunch)
        {
            _toast = $"FUEL {_run.ShipFuel}/{FuelToLaunch} — MINE {FuelToLaunch - _run.ShipFuel} MORE";
            _toastTimer = 2.5f;
            return;
        }

        BeginLaunch(pad);
    }

    /// <summary>Kick off the liftoff cinematic from the pad. From here <see cref="UpdateLaunch"/>
    /// drives the climb; <see cref="FinishLaunch"/> banks the escape once the ship clears the sky.</summary>
    private void BeginLaunch(Vector2 pad)
    {
        _launching = true;
        _sfx.Play("launch", 1f);
        _launchElapsed = 0f;
        _launchVel = 0f;
        _launchShipPos = pad;
        _launchUp = _run.Planet.UpAt(pad);
    }

    /// <summary>One frame of the liftoff climb: build thrust, ride the dwarf up with the ship,
    /// spew exhaust, shake the screen — then hand the stick to the player for the orbital
    /// ascent once the rocket has cleared the pad.</summary>
    private void UpdateLaunch(float dt)
    {
        _launchElapsed += dt;
        // Thrust ramps in over the first moments, then holds — a slow, weighty liftoff
        // building into a real climb rather than an instant jump.
        _launchVel += 40f * dt;
        _launchShipPos += _launchUp * _launchVel * dt;

        // The dwarf rides the ship, so the camera tracks the ascent.
        _run.Player.Position = _launchShipPos + _launchUp * 8f;
        _run.Player.Velocity = Vector2.Zero;
        _camera.Follow(_run.Player.Position, _launchUp, dt);

        // Exhaust plume out of the nozzle, downward along local gravity.
        _particles.EmitRocketExhaust(_launchShipPos - _launchUp * 2f, -_launchUp);
        _run.Shake = MathF.Max(_run.Shake, 0.7f);

        // Keep the world alive underneath the rising ship.
        _run.Physics.Update(dt);
        _particles.Update(dt, _run.Planet);
        _run.Cells.Update(dt);
        _run.RunTime += dt;

        if (_launchElapsed > 2.2f)
        {
            _launching = false;
            _ascending = true;
            _toast = "CLIMB TO THE MOTHERSHIP - WASD/ARROWS STEER";
            _toastTimer = 3.5f;
        }
    }

    /// <summary>The manual half of the escape: the rocket climbs gently on its own, easing
    /// to a stop at the mothership's parking orbit, while the player steers freely in any
    /// direction (WASD/arrows) — nudge sideways to chase the drifting station, throttle up
    /// or ease back down. At orbit altitude an approach glide pulls the rocket the rest of
    /// the way to the station — the rendezvous always completes; steering just decides how
    /// direct the ride is. Docking = FinishLaunch.</summary>
    private void UpdateAscent(float dt, KeyboardState keys)
    {
        var up = _run.Planet.UpAt(_launchShipPos);
        var right = new Vector2(-up.Y, up.X);
        var lat = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        var vert = (keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up) ? 1f : 0f)
                 + (keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down) ? -1f : 0f);

        var alt = (_launchShipPos - _run.Planet.Center).Length() - _run.Planet.Radius * Planet.TileSize;
        // Climb rate eases to zero approaching the parking orbit — the rocket "arrives"
        // rather than shooting past the station (which is still drifting; the approach
        // glide below tracks it).
        _run.MothershipAngle += Session.StationDriftRate * dt;
        // Keep building thrust from where the liftoff cinematic left off, capped at a
        // leisurely cruise — the slow heave off the pad flows into the climb with no
        // velocity jump, and the low cap keeps the whole ride easy to steer.
        _launchVel = MathF.Min(100f, _launchVel + 35f * dt);
        var climb = MathHelper.Clamp((Session.OrbitAltitude - alt) * 1.4f, 0f, _launchVel);
        // Player thrust vector on top of the auto-climb: full 2D steering.
        var steer = right * lat + up * vert;
        if (steer != Vector2.Zero) steer.Normalize();
        _launchShipPos += (up * climb + steer * 170f) * dt;
        // Never let the rocket sink back into the crust — S throttles down to a hover.
        var minR = _run.Planet.Radius * Planet.TileSize + 60f;
        var fromCenter = _launchShipPos - _run.Planet.Center;
        if (fromCenter.Length() < minR)
            _launchShipPos = _run.Planet.Center + Vector2.Normalize(fromCenter) * minR;

        // Near orbit altitude the docking computer takes over the final approach.
        var station = _run.StationPos;
        if (alt > Session.OrbitAltitude - 70f)
        {
            var to = station - _launchShipPos;
            var d = to.Length();
            if (d > 1f) _launchShipPos += to / d * MathF.Min(380f * dt, d);
            if (d < 48f)
            {
                _ascending = false;
                _transitionFlash = 0.6f;
                FinishLaunch();
                return;
            }
        }

        _run.Player.Position = _launchShipPos + _launchUp * 8f;
        _run.Player.Velocity = Vector2.Zero;
        // Wide orbit-style framing: same zoom as the parking orbit, biased upward so the
        // planet limb, the climbing rocket, and the station all fit in frame.
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, 0.44f, MathHelper.Clamp(dt * 2.2f, 0f, 1f));
        _camera.Follow(_run.Player.Position + up * 260f, up, dt);
        _launchUp = up;

        if (climb > 20f || vert > 0f) _particles.EmitRocketExhaust(_launchShipPos - up * 2f, -up);
        _run.Physics.Update(dt);
        _particles.Update(dt, _run.Planet);
        _run.Cells.Update(dt);
        _run.RunTime += dt;
        _toastTimer -= dt;
    }

    /// <summary>Reached once the ship has flown clear: bank the escape (unlock the next world,
    /// meta bonuses), then hand the rocket to the player in space — manual flight from here,
    /// with the camera easing out from planet scale to system scale.</summary>
    private void FinishLaunch()
    {
        _launching = false;
        _meta.Escapes++;
        if (!_meta.PlanetsEscaped.Contains(_run.Def.Id)) _meta.PlanetsEscaped.Add(_run.Def.Id);
        var idx = PlanetDefs.IndexOf(_run.Def);
        _meta.PlanetsUnlocked = Math.Max(_meta.PlanetsUnlocked,
            Math.Min(PlanetDefs.All.Length, idx + 2));
        if (_meta.Escapes >= 1) _meta.StartingPickaxePower = Math.Max(_meta.StartingPickaxePower, 2);
        if (_meta.Escapes >= 3) _meta.StartWithCannon = true;
        // The base is left behind when you fly off — its vault won't be revisited.
        _meta.Bank.Remove(_run.Def.Id);
        // Docking: raw materials in the pack transfer to the mothership's cargo hold (the
        // foundry spends from it) and leftover mined fuel tops up the ship's tank. Gear
        // stays behind with the rover — only bankable raws ride up.
        var cargoMoved = 0;
        foreach (var (id, count) in _run.Player.Inventory.Items)
        {
            if (id == "fuel") { _meta.MotherFuel += count; cargoMoved += count; continue; }
            if (!Tiles.IsBankable(id)) continue;
            _meta.ShipCargo[id] = _meta.ShipCargo.GetValueOrDefault(id) + count;
            cargoMoved += count;
        }
        // The dock's refinery runs as the cargo comes aboard: raw metals smelt 4:1 into the
        // pure ingots the foundry actually spends; gems stay precious as-is.
        _meta.RefineCargo();
        _meta.Save();
        // The visit is over (a finished visit can't be resumed), but there's no game-over
        // screen: the rocket docks with the mothership and you have the stick.
        RunSave.Delete();

        // The campaign finale: escaping the Rift with its titan slain conquers the system.
        // Souls, upgrades, and the mothership endure into the next campaign; the warp home
        // burns the five core shards, so the worlds must be pierced anew.
        if (_run.Def.Id == "rift" && _run.Titan.Health <= 0)
        {
            _meta.RunsCompleted++;
            _meta.CoreShards.Clear();
            _meta.Save();
            EndRun("SYSTEM CONQUERED!\n" +
                   $"The Rift titan is slain and you flew out alive. Campaigns completed: {_meta.RunsCompleted}.\n" +
                   $"Escapes {_meta.Escapes}   Titan kills {_meta.TitansDefeated}   Deepest {_meta.DeepestDepth}   Deaths {_meta.Deaths}\n" +
                   "Souls and upgrades endure. The warp home burned your core shards -\n" +
                   "pierce the five worlds anew to reach the Rift again.\n" +
                   "Press R to return to your ship.");
            return;
        }

        _toast = _run.Def.Id == "rift"
            ? "DOCKED - BUT THE RIFT TITAN STILL LIVES. RETURN AND SLAY IT"
            : cargoMoved > 0
                ? $"DOCKED - {cargoMoved} CARGO HAULED IN AND REFINED"
                : $"ESCAPED {_run.Def.Name.ToUpperInvariant()} IN {_run.RunTime:0.0}S - YOU HAVE THE STICK";
        _toastTimer = 4f;
        EnterSpace(idx, exitSpeed: 320f, zoomFromPlanet: true);
    }

    /// <summary>True when nothing solid hangs above this position for a dozen tiles along
    /// local up — i.e. open sky. The pad-placement gate: valleys, plains, and mountain tops
    /// all qualify; caves and overhangs don't.</summary>
    private bool OpenToSky(Vector2 pos)
    {
        var up = _run.Planet.UpAt(pos);
        for (var i = 1; i <= 12; i++)
        {
            var probe = pos + up * (i * Planet.TileSize);
            var (x, y) = _run.Planet.WorldToTile(probe);
            if (x >= _run.Planet.Rings) return true;
            if (Tiles.IsSolid(_run.Planet.Get(x, y))) return false;
        }
        return true;
    }

    /// <summary>Plant the launch pad at the player's feet: walk down along local gravity to
    /// the first solid tile and sit the pad on top of it, so the ship rests on the ground
    /// rather than hovering at whatever height the dwarf was standing.</summary>
    private void PlaceLaunchPad()
    {
        var up = _run.Planet.UpAt(_run.Player.Position);
        var pos = _run.Player.Position;
        for (var i = 0; i < 8 * Planet.TileSize; i++)
        {
            var probe = pos - up * i;
            if (_run.Planet.IsSolidAt(probe))
            {
                pos = probe + up * (Planet.TileSize * 0.7f);
                break;
            }
        }
        _run.PadPos = pos;
        _particles.EmitDust(pos, 10f);
        _run.Shake = MathF.Max(_run.Shake, 0.3f);
    }

    /// <summary>Ship stages are crafted standing at the pad — the ship is a physical build
    /// site, not an inventory item.</summary>
    private bool NearPad() =>
        _run.PadPos is { } pad && (_run.Player.Position - pad).Length() < 120f;

    /// <summary>Plant the storage depot at the player's feet (same ground-seek as the pad).</summary>
    private void PlaceDepot()
    {
        var up = _run.Planet.UpAt(_run.Player.Position);
        var pos = _run.Player.Position;
        for (var i = 0; i < 8 * Planet.TileSize; i++)
        {
            var probe = pos - up * i;
            if (_run.Planet.IsSolidAt(probe))
            {
                pos = probe + up * (Planet.TileSize * 0.7f);
                break;
            }
        }
        _run.DepotPos = pos;
        _particles.EmitDust(pos, 8f);
    }

    private bool NearDepot() =>
        _run.DepotPos is { } d && (_run.Player.Position - d).Length() < 90f;

    /// <summary>Bank every raw material in the inventory into the planet's persistent stash.
    /// Crafted gear stays on the belt; only dug/harvested resources go in the vault.</summary>
    private void DepositToBank()
    {
        var bank = _meta.BankFor(_run.Def.Id);
        var moved = 0;
        // Snapshot first — TryConsume mutates the inventory we'd otherwise be iterating.
        var snapshot = new List<(string id, int count)>();
        foreach (var kv in _run.Player.Inventory.Items) snapshot.Add((kv.Key, kv.Value));
        foreach (var (id, count) in snapshot)
        {
            if (count <= 0 || !Tiles.IsBankable(id)) continue;
            if (_run.Player.Inventory.TryConsume(id, count))
            {
                bank[id] = bank.GetValueOrDefault(id) + count;
                moved += count;
            }
        }
        if (moved > 0)
        {
            _meta.Save();
            _toast = $"BANKED {moved} RESOURCES";
            _sfx.Play("ui", 0.7f);
        }
        else _toast = "NOTHING TO BANK";
        _toastTimer = 2.5f;
        _particles.EmitDust(_run.DepotPos ?? _run.Player.Position, 6f);
    }

    /// <summary>Pull the planet's whole banked stash back into the inventory.</summary>
    private void WithdrawFromBank()
    {
        var bank = _meta.BankFor(_run.Def.Id);
        var moved = 0;
        foreach (var (id, count) in new List<KeyValuePair<string, int>>(bank))
        {
            if (count <= 0) continue;
            _run.Player.Inventory.Add(id, count);
            moved += count;
        }
        bank.Clear();
        if (moved > 0)
        {
            _meta.Save();
            _toast = $"WITHDREW {moved} RESOURCES";
            _sfx.Play("ui", 0.7f, 0.15f);
        }
        else _toast = "VAULT EMPTY";
        _toastTimer = 2.5f;
        _particles.EmitDust(_run.DepotPos ?? _run.Player.Position, 6f);
    }

    /// <summary>Total items sitting in this planet's vault — for the HUD prompt.</summary>
    private int BankCount()
    {
        var n = 0;
        foreach (var (_, c) in _meta.BankFor(_run.Def.Id)) n += c;
        return n;
    }

    protected override void Draw(GameTime gameTime)
    {
        _drawSw.Restart();
        DrawFrame(gameTime);
        _drawSw.Stop();
        _drawMs = _drawMs * 0.9f + (float)_drawSw.Elapsed.TotalMilliseconds * 0.1f;

        base.Draw(gameTime);
    }

    /// <summary>Real rendered frame rate + smoothed CPU times, top-right on every screen.
    /// MonoGame drops Draws when a fixed-step frame overruns, so FPS here is the number
    /// that actually stutters; UPD/DRW attribute the blame.</summary>
    private void DrawFpsOverlay()
    {
        _fpsFrames++;
        var now = Environment.TickCount64;
        if (now - _fpsMark >= 1000)
        {
            _fps = (int)(_fpsFrames * 1000 / (now - _fpsMark));
            _fpsFrames = 0;
            _fpsMark = now;
        }
        var fpsText = $"FPS {_fps}  UPD {_updateMs:0.0}  DRW {_drawMs:0.0}";
        _renderer.DrawText(fpsText,
            new Vector2(VirtualWidth - 8 - _renderer.MeasureText(fpsText), 6),
            _fps >= 55 ? new Color(120, 220, 130) : _fps >= 30 ? new Color(255, 225, 140) : new Color(255, 110, 90));
    }

    private void DrawFrame(GameTime gameTime)
    {
        // Feed the renderer the current wall-clock so animated decoration (waving grass,
        // hanging vines) advances with the game time rather than the frame index.
        _renderer.Time = (float)gameTime.TotalGameTime.TotalSeconds;

        if (_screen == GameScreen.Space)
        {
            DrawSpace();
            DrawFpsOverlay();
            if (_screenshotPending)
            {
                _screenshotPending = false;
                SaveScreenshot();
            }
            return;
        }

        // Apply shake by perturbing the camera target.
        var shakeX = (float)(Random.Shared.NextDouble() - 0.5) * _run.Shake * 6f;
        var shakeY = (float)(Random.Shared.NextDouble() - 0.5) * _run.Shake * 6f;
        var oldTarget = _camera.Target;
        _camera.Target = oldTarget + new Vector2(shakeX, shakeY);

        // Re-wired every frame because _run.Physics is recreated on restart while _renderer persists.
        _renderer.TrembleTiles = _run.Physics.TremblingTiles;
        _renderer.DrawWorld(_run.Planet, _camera);

        _renderer.BeginEntities(_camera);

        // View circle for the culled cell passes: centre + a radius that covers the screen
        // corners at any camera rotation. The cell grid is far too fine to draw planet-wide.
        var viewCentre = _camera.ScreenToWorld(new Vector2(VirtualWidth / 2f, VirtualHeight / 2f));
        var viewRadius = (viewCentre - _camera.ScreenToWorld(Vector2.Zero)).Length() + Planet.TileSize * 2f;

        // Cells (sand/water/lava/smoke) draw above tiles but below entities so the dwarf walks
        // in front of his own debris pile.
        // Zoomed-out views (orbit/high descent) sample the cell grid at a stride — the full
        // scan is the single biggest cost at orbital view radii.
        var cellStride = _camera.Zoom < 0.55f ? 6 : _camera.Zoom < 0.9f ? 3 : 1;
        _run.Cells.Draw(_renderer, viewCentre, viewRadius, cellStride);

        // Pixel-art dwarf sprite — drawn rotated to align local-up with planet's outward radial.
        // Sprite head-at-top, feet-at-bottom; the rotation maps sprite-up to world-up.
        // Anchor offset: the sprite is 12 px tall × 0.6 scale so it extends 3.6 px below center,
        // but the collision Radius is only 2.6 px. Without the offset the visual feet sit 1 px
        // *inside* the tile while the collision body is correctly perched on top — looks like
        // the dwarf is sunk into the ground. Shifting the sprite up by exactly that gap aligns
        // visual feet with the collision bottom.
        var up = _run.Planet.UpAt(_run.Player.Position);
        var rot = MathF.Atan2(up.X, -up.Y);
        if (_playerSprite is { } ps)
        {
            // Animated pack sprite: pick a frame from grounded/tangent/radial motion, flip
            // toward the last direction the dwarf actually walked.
            var pRight = new Vector2(-up.Y, up.X);
            var vT = Vector2.Dot(_run.Player.Velocity, pRight);
            if (MathF.Abs(vT) > 8f) _playerFacing = MathF.Sign(vT);
            var frame = ps.Frame(_run.Player.Grounded, vT, Vector2.Dot(_run.Player.Velocity, up), _run.RunTime);
            _renderer.Batch.Draw(frame, _run.Player.Position + up * ps.FeetOffset(_run.Player.Radius), null,
                Color.White, rot, new Vector2(frame.Width * 0.5f, frame.Height * 0.5f), ps.Scale,
                _playerFacing < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }
        else
        {
            const float spriteScale = 0.6f;          // world units per sprite pixel
            const float spriteFeetOffset = 1.0f;     // = (sprite_half_height * scale) − Radius
            _renderer.Batch.Draw(_dwarfTex, _run.Player.Position + up * spriteFeetOffset, null, Color.White, rot,
                new Vector2(_dwarfTex.Width * 0.5f, _dwarfTex.Height * 0.5f),
                spriteScale, SpriteEffects.None, 0f);
        }

        // During the rover descent the dwarf rides inside the pod — draw the capsule over
        // him: steel shell, glass dome, landing skids splayed along local-down.
        if (_landing)
        {
            var lRight = new Vector2(-up.Y, up.X);
            _renderer.DrawRect(_landerPos, new Vector2(9f, 8f), new Color(165, 170, 185), rot);
            _renderer.DrawRect(_landerPos + up * 4.4f, new Vector2(7f, 2.4f), new Color(140, 210, 235), rot);
            _renderer.DrawRect(_landerPos - up * 4.4f - lRight * 3.4f, new Vector2(1.8f, 3.4f), new Color(90, 92, 105), rot);
            _renderer.DrawRect(_landerPos - up * 4.4f + lRight * 3.4f, new Vector2(1.8f, 3.4f), new Color(90, 92, 105), rot);
        }

        // Reticle.
        var mouse = Mouse.GetState();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        _renderer.DrawCircle(worldCursor, 3f, new Color(255, 255, 255, 180));

        // Held weapon: the selected toolbelt slot's sidearm drawn in the dwarf's grip,
        // rotated to the aim and flipped when facing left so it's never upside-down.
        if (!_landing && !_launching && !_ascending && !_orbiting
            && _run.Player.Toolbelt.Slots[_run.Player.Toolbelt.Selected] is { } heldId
            && _weaponTex.TryGetValue(heldId, out var heldTex))
        {
            var aim = worldCursor - _run.Player.Position;
            if (aim.LengthSquared() > 0.01f) aim.Normalize(); else aim = new Vector2(1f, 0f);
            var wrot = MathF.Atan2(aim.Y, aim.X);
            _renderer.Batch.Draw(heldTex, _run.Player.Position + aim * 3.2f, null, Color.White,
                wrot, new Vector2(1.5f, heldTex.Height / 2f), 0.55f,
                aim.X < 0f ? SpriteEffects.FlipVertically : SpriteEffects.None, 0f);
        }

        // Corpses — drawn under the living so a fresh kill layers naturally. A flattened
        // slab of the creature's (desaturated) body colour lying along the local tangent,
        // with a paler belly stripe. Blinks during the last seconds before decay.
        foreach (var corpse in _run.Corpses)
        {
            if (corpse.Life < Corpse.BlinkTime && (int)(corpse.Life * 6f) % 2 == 0) continue;
            var cup = _run.Planet.UpAt(corpse.Position);
            var crot = MathF.Atan2(cup.X, -cup.Y);
            var col = Corpse.BodyColor(corpse.Kind);
            _renderer.DrawRect(corpse.Position, new Vector2(corpse.Radius * 2.2f, corpse.Radius * 0.9f), col, crot);
            _renderer.DrawRect(corpse.Position - cup * corpse.Radius * 0.2f,
                new Vector2(corpse.Radius * 1.6f, corpse.Radius * 0.4f),
                Color.Lerp(col, Color.White, 0.18f), crot);
        }

        // Creatures — each kind draws its own procedural sprite, including the burn/freeze
        // status tinting and the burning-ember flicker.
        foreach (var c in _run.Creatures)
        {
            c.Draw(_renderer, _run.Planet, _run.Player);
        }

        // Sentries — chunky pixel-art turret. Base is a stout iron plinth, barrel is a long
        // rectangle that rotates with the sentry's Aim. Tinted red on hit-flash; the glowing
        // muzzle ring tracks the cooldown so a charging sentry shows visible "ready" feedback.
        foreach (var s in _run.Sentries)
        {
            var sup = _run.Planet.UpAt(s.Position);
            var srot = MathF.Atan2(sup.X, -sup.Y);
            var bodyCol = s.HitFlash > 0 ? Color.White : new Color(120, 110, 95);
            var rivetCol = new Color(50, 45, 40);
            // Base plinth (slightly inset) and a darker mount cap on top.
            _renderer.DrawRect(s.Position - sup * 1.5f, new Vector2(11f, 4f), rivetCol, srot);
            _renderer.DrawRect(s.Position, new Vector2(10f, 6f), bodyCol, srot);
            _renderer.DrawRect(s.Position + sup * 1.0f, new Vector2(6f, 3f), rivetCol, srot);
            // Barrel — drawn rotated to the aim angle.
            var barrelDir = new Vector2(MathF.Cos(s.Aim), MathF.Sin(s.Aim));
            var barrelMid = s.Position + barrelDir * 5f;
            _renderer.DrawRect(barrelMid, new Vector2(8f, 2.5f), bodyCol, s.Aim);
            // Muzzle ring — pulses brighter as cooldown nears 0 (about-to-fire feel).
            var ready = MathHelper.Clamp(1f - s.Cooldown / Sentry.FireRate, 0f, 1f);
            var muzzleCol = Color.Lerp(new Color(120, 80, 40), new Color(255, 200, 100), ready);
            _renderer.DrawCircle(s.Position + barrelDir * 9f, 1.3f, muzzleCol);
            // HP pip — small bar above the body when damaged.
            if (s.Health < s.MaxHealth)
            {
                var fr = MathHelper.Clamp(s.Health / s.MaxHealth, 0f, 1f);
                _renderer.DrawRect(s.Position + sup * 6f, new Vector2(8f, 1.2f), new Color(40, 10, 10), srot);
                _renderer.DrawRect(s.Position + sup * 6f - new Vector2((1f - fr) * 4f, 0f),
                    new Vector2(8f * fr, 1.2f), new Color(220, 60, 60), srot);
            }
        }

        // Projectiles. Each kind gets its own pixel motif: bullets are tiny dots, cannon shells
        // are bigger discs, dynamite is a brown stick with a red fuse-tip, harpoon is a long
        // pointed shaft. Special ammo glows in its element colour.
        foreach (var p in _run.Projectiles)
        {
            switch (p.Kind)
            {
                case ProjectileKind.Bullet:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 230, 120));
                    break;
                case ProjectileKind.Cannon:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 140, 60));
                    break;
                case ProjectileKind.Nuke:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 80, 200));
                    break;
                case ProjectileKind.CannonSilver:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(220, 230, 250));
                    _renderer.DrawCircle(p.Position, p.Radius * 0.5f, Color.White);
                    break;
                case ProjectileKind.CannonRuby:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 100, 90));
                    _renderer.DrawCircle(p.Position, p.Radius * 0.5f, new Color(255, 220, 200));
                    break;
                case ProjectileKind.CannonSapphire:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(140, 180, 255));
                    _renderer.DrawCircle(p.Position, p.Radius * 0.5f, new Color(220, 240, 255));
                    break;
                case ProjectileKind.CannonDiamond:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(220, 240, 255));
                    _renderer.DrawCircle(p.Position, p.Radius * 0.6f, Color.White);
                    break;
                case ProjectileKind.Dynamite:
                {
                    // Stick-of-dynamite sprite oriented along velocity. Fuse tip flickers.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(7f, 2.5f), new Color(180, 40, 50), ang);
                    _renderer.DrawRect(p.Position, new Vector2(7f, 1f), new Color(120, 20, 30), ang);
                    var fuseFlicker = (MathF.Sin(_run.RunTime * 50f) * 0.5f + 0.5f);
                    var fuseTip = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 4f;
                    _renderer.DrawCircle(fuseTip, 1.2f + fuseFlicker * 0.6f, new Color(255, 200, 100));
                    break;
                }
                case ProjectileKind.Harpoon:
                {
                    // Long pointed shaft with a metallic head. Drawn rotated along velocity.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(14f, 2f), new Color(140, 110, 80), ang);
                    var head = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 6f;
                    _renderer.DrawRect(head, new Vector2(5f, 3f), new Color(220, 220, 220), ang);
                    break;
                }
                case ProjectileKind.Pistol:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 240, 180));
                    break;
                case ProjectileKind.MachineGun:
                {
                    // Short tracer streak so held fire reads as a stream.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(5f, 1.2f), new Color(255, 210, 120), ang);
                    break;
                }
                case ProjectileKind.Laser:
                {
                    // Energy beam segment: long red bolt with a white-hot core.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(18f, 2.2f), new Color(255, 70, 70, 200), ang);
                    _renderer.DrawRect(p.Position, new Vector2(14f, 1f), new Color(255, 210, 210), ang);
                    break;
                }
                case ProjectileKind.LaserCannon:
                {
                    // Heavy lance: a longer, thicker cyan beam with a blinding core — reads
                    // as a drill of light punching through terrain.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(26f, 4f), new Color(80, 220, 255, 190), ang);
                    _renderer.DrawRect(p.Position, new Vector2(20f, 1.8f), new Color(230, 250, 255), ang);
                    break;
                }
                case ProjectileKind.Rocket:
                {
                    // Finned round with a flickering exhaust flame at the tail.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    var fwd = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                    _renderer.DrawRect(p.Position, new Vector2(9f, 3f), new Color(180, 185, 200), ang);
                    _renderer.DrawRect(p.Position + fwd * 4.5f, new Vector2(3f, 2f), new Color(200, 80, 60), ang);
                    var flick = MathF.Sin(_run.RunTime * 40f) * 0.5f + 0.5f;
                    _renderer.DrawCircle(p.Position - fwd * 5.5f, 1.6f + flick * 1.2f, new Color(255, 170, 70));
                    break;
                }
                case ProjectileKind.Tnt:
                {
                    // Strapped bundle of three sticks with a sparking fuse.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(6f, 5f), new Color(180, 45, 45), ang);
                    _renderer.DrawRect(p.Position, new Vector2(6f, 1f), new Color(120, 25, 25), ang);
                    _renderer.DrawRect(p.Position, new Vector2(1.4f, 5f), new Color(90, 70, 45), ang);
                    var spark = MathF.Sin(_run.RunTime * 60f) * 0.5f + 0.5f;
                    var up2 = _run.Planet.UpAt(p.Position);
                    _renderer.DrawCircle(p.Position + up2 * 3.5f, 1f + spark * 0.8f, new Color(255, 230, 130));
                    break;
                }
                default:
                    _renderer.DrawCircle(p.Position, p.Radius, Color.White);
                    break;
            }
        }

        // Boulders.
        foreach (var b in _run.Boulders)
        {
            _renderer.DrawCircle(b.Position, b.Radius, new Color(80, 70, 60));
        }

        // Toxic cloud — the acid-rain storm's source, a roiling olive bank parked above the
        // surface at the storm bearing. The falling acid cells themselves are drawn by the
        // cell renderer; this is the thing to run away from.
        if (_run.AcidRainActive > 0f)
        {
            var cAng = _run.AcidRainAngle;
            var cDir = new Vector2(MathF.Cos(cAng), MathF.Sin(cAng));
            var cUp = cDir;
            var cRight = new Vector2(-cUp.Y, cUp.X);
            var ground = SpawnDirector.FindSurfaceSpawn(_run.Planet, cAng, _run.Planet.Radius);
            var cloud = ground + cUp * 300f;
            var dark = new Color(52, 66, 40);
            var mid = new Color(72, 92, 50);
            for (var i = -3; i <= 3; i++)
            {
                var bob = MathF.Sin(_renderer.Time * 1.3f + i) * 6f;
                var h = (3f - MathF.Abs(i)) / 3f;
                var p = cloud + cRight * (i * 42f) + cUp * bob;
                _renderer.DrawCircle(p, 26f + h * 22f, i % 2 == 0 ? dark : mid);
                _renderer.DrawCircle(p - cUp * 12f, 18f + h * 14f, new Color(96, 128, 56));
            }
        }

        // Meteors — a molten rock (dark core, hot rim) plus a pulsing warning reticle on the
        // ground it's aimed at, so the strike is telegraphed and dodgeable.
        foreach (var m in _run.Meteors)
        {
            var gUp = _run.Planet.UpAt(m.Target);
            var pulse = MathF.Sin(_renderer.Time * 10f) * 0.5f + 0.5f;
            _renderer.DrawCircle(m.Target, 10f + pulse * 5f, new Color(255, 80, 40, 130));
            _renderer.DrawRect(m.Target, new Vector2(22f, 2f), new Color(255, 120, 60), MathF.Atan2(gUp.X, -gUp.Y));

            _renderer.DrawCircle(m.Position, m.Radius + 3f, new Color(255, 150, 60));
            _renderer.DrawCircle(m.Position, m.Radius, new Color(60, 30, 24));
            _renderer.DrawCircle(m.Position, m.Radius * 0.5f, new Color(255, 220, 140));
        }

        // Titan ranged shots — Godzilla flame (layered orange/yellow flicker), Otachi acid
        // (wobbling green glob), Slattern spikes (bone-pale darts), Mecha laser (cyan bolt
        // with a white core drawn along its velocity).
        foreach (var shot in _run.TitanShots)
        {
            switch (shot.Kind)
            {
                case TitanShotKind.Flame:
                {
                    var flick = (float)Random.Shared.NextDouble() * 1.6f;
                    _renderer.DrawCircle(shot.Position, shot.Radius + flick, new Color(230, 90, 30));
                    _renderer.DrawCircle(shot.Position, shot.Radius * 0.6f, new Color(255, 210, 90));
                    break;
                }
                case TitanShotKind.Acid:
                {
                    var wob = (float)Random.Shared.NextDouble() * 1.2f;
                    _renderer.DrawCircle(shot.Position, shot.Radius + wob, new Color(90, 160, 30));
                    _renderer.DrawCircle(shot.Position, shot.Radius * 0.55f, new Color(180, 240, 80));
                    break;
                }
                case TitanShotKind.Lava:
                {
                    var wob = (float)Random.Shared.NextDouble() * 1.4f;
                    _renderer.DrawCircle(shot.Position, shot.Radius + wob, new Color(200, 70, 20));
                    _renderer.DrawCircle(shot.Position, shot.Radius * 0.55f, new Color(255, 190, 80));
                    break;
                }
                case TitanShotKind.Spike:
                {
                    var sAng = MathF.Atan2(shot.Velocity.Y, shot.Velocity.X);
                    _renderer.DrawRect(shot.Position, new Vector2(16f, 4.5f), new Color(200, 190, 165), sAng);
                    _renderer.DrawRect(shot.Position + Vector2.Normalize(shot.Velocity) * 6f,
                        new Vector2(6f, 2f), Color.White, sAng);
                    break;
                }
                default:   // Laser
                {
                    var ang = MathF.Atan2(shot.Velocity.Y, shot.Velocity.X);
                    _renderer.DrawRect(shot.Position, new Vector2(20f, 3.5f), new Color(120, 230, 255, 200), ang);
                    _renderer.DrawRect(shot.Position, new Vector2(15f, 1.6f), Color.White, ang);
                    break;
                }
            }
        }

        // The mothership hangs at its parking orbit for the whole visit — the rover departs
        // from it and the escape rocket docks with it. Culled when far off-screen.
        if ((_run.StationPos - _camera.Target).LengthSquared() < 1400f * 1400f)
            DrawStationInWorld(_run.StationPos);

        // Spaceship build site — the pad plus however many stages are installed. Drawn as
        // world-space rects rotated to local-up, same as every other surface structure. During
        // liftoff the ship is drawn at its climbing position instead of on the pad.
        if (_launching || _ascending) DrawShip(_launchShipPos, _run.ShipStage);
        else if (_run.PadPos is { } shipPos) DrawShip(shipPos, _run.ShipStage);

        // Storage depot — a squat vault the dwarf banks resources at.
        if (_run.DepotPos is { } depotPos) DrawDepot(depotPos);

        // The spent rover, broken where it came down — the arrival landmark.
        if (_run.RoverWreck is { } wreck) DrawRoverWreck(wreck);

        // Kaiju visibility cull. The kaiju's render block does 100+ draw calls (4 legs × IK +
        // 7-node tail + dorsal spines + head + claws), so skipping it when off-screen is a
        // large win. Camera viewport is 1280×720 at zoom 4 → ~320×180 world units, so the
        // visible radius from the camera target is ~200 px; bump to 400 for the kaiju's
        // silhouette (it's huge — body+legs span ~280 px) and a small margin so legs sweeping
        // into view from off-screen aren't suddenly popped in.
        // Boss — a distinct procedural skeleton per variant (upright Godzilla, angular Mecha,
        // legless Sandworm, big-armed Kong) plus the egg / burrow mound, all in TitanRenderer.
        // Culled off-screen: the biggest bodies span ~300 px.
        var titanOnScreen = _run.Titan.Health > 0
            && (_run.Titan.Position - _camera.Target).LengthSquared() < 400f * 400f;
        if (titanOnScreen)
            TitanRenderer.Draw(_renderer, _run.Titan, _run.Planet, _run.Player.Position, _renderer.Time);

        // Particles drawn last so chips and sparks pop over creatures and the player.
        _particles.Draw(_renderer);

        _renderer.EndEntities();

        // === Lighting pass: build a low-res lightmap, then composite it multiplicatively
        // over the scene. Bright ambient so the surface is uniformly daylight regardless of
        // angle (no day/night). A subtractive darkness disk centred at the planet then dims
        // the inner half of the planet so caves still feel dark. Player aura helps near the
        // dwarf in the dim zone. ===
        _renderer.BeginLighting(_camera, new Color(180, 175, 195));

        // Player aura — four nested radials stacked together for a soft, wide glow. Lantern
        // upgrade makes every ring bigger and brighter — visible improvement to dark-cave
        // navigation. Tier IV pickaxe (diamond) adds a faint icy-white sheen so the player
        // sprite reads as freshly tooled.
        var lanternMul = _run.Player.HasLantern ? 1.55f : 1.0f;
        _renderer.AddLight(_run.Player.Position, 200f * lanternMul, new Color(85, 70, 50));
        _renderer.AddLight(_run.Player.Position, 140f * lanternMul, new Color(140, 115, 80));
        _renderer.AddLight(_run.Player.Position, 90f * lanternMul,  new Color(200, 170, 120));
        _renderer.AddLight(_run.Player.Position, 50f * lanternMul,  new Color(245, 215, 165));
        if (_run.Player.PickaxeTier >= 4)
            _renderer.AddLight(_run.Player.Position, 28f, new Color(180, 220, 255));

        // Core: molten heart of the planet.
        _renderer.AddLight(_run.Planet.Center, 90f, new Color(255, 90, 30));

        // Visible ores within a tile-radius of the player so we don't scan the whole map.
        // Same pass picks up player-placed lights (Glowshroom, Beacon) so a torch-lit room
        // illuminates correctly without us having to track each placeable separately.
        var (ptx, pty) = _run.Planet.WorldToTile(_run.Player.Position);
        const int oreScanR = 14;
        for (var dy = -oreScanR; dy <= oreScanR; dy++)
        {
            for (var dx = -oreScanR; dx <= oreScanR; dx++)
            {
                if (dx * dx + dy * dy > oreScanR * oreScanR) continue;
                var k = _run.Planet.Get(ptx + dx, pty + dy);
                Color glow; float r;
                switch (k)
                {
                    case TileKind.GoldOre: glow = new Color(255, 180, 60);  r = 9f; break;
                    case TileKind.Crystal: glow = new Color(180, 110, 230); r = 11f; break;
                    case TileKind.IronOre: glow = new Color(220, 150, 110); r = 5f; break;
                    case TileKind.Glowshroom:
                        glow = new Color(110, 220, 130);
                        // Soft pulse so a row of mushrooms breathes together with a low frequency.
                        r = 26f + MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 1.6f + dx * 0.3f) * 3f;
                        break;
                    case TileKind.Beacon:
                        glow = new Color(190, 130, 255);
                        // Sharper pulse for the beacon — matches the renderer's pixel pulse so
                        // the tile core, the light, and the bloom all peak together.
                        r = 32f + MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 3.0f + dx * 0.4f) * 5f;
                        break;
                    default: continue;
                }
                var op = _run.Planet.TileToWorld(ptx + dx, pty + dy);
                _renderer.AddLight(op, r, glow);
            }
        }

        // Projectiles in flight glow by kind. Special-ammo glows match their element so the
        // player can see at a glance which shell is in flight — ruby = warm orange, sapphire =
        // cool cyan, diamond = bright white, harpoon = warm metallic, dynamite = orange fuse.
        foreach (var p in _run.Projectiles)
        {
            var (col, r) = p.Kind switch
            {
                ProjectileKind.Bullet         => (new Color(255, 220, 110), 8f),
                ProjectileKind.Cannon         => (new Color(255, 130, 50),  16f),
                ProjectileKind.Nuke           => (new Color(255, 80, 220),  28f),
                ProjectileKind.CannonSilver   => (new Color(220, 230, 255), 14f),
                ProjectileKind.CannonRuby     => (new Color(255, 110, 80),  20f),
                ProjectileKind.CannonSapphire => (new Color(140, 200, 255), 20f),
                ProjectileKind.CannonDiamond  => (new Color(230, 245, 255), 26f),
                ProjectileKind.Dynamite       => (new Color(255, 180, 80),  10f),
                ProjectileKind.Harpoon        => (new Color(255, 200, 130), 14f),
                ProjectileKind.Pistol         => (new Color(255, 235, 160), 8f),
                ProjectileKind.MachineGun     => (new Color(255, 210, 120), 6f),
                ProjectileKind.Laser          => (new Color(255, 90, 90),   14f),
                ProjectileKind.LaserCannon    => (new Color(120, 225, 255), 22f),
                ProjectileKind.Rocket         => (new Color(255, 160, 70),  16f),
                ProjectileKind.Tnt            => (new Color(255, 210, 110), 10f),
                _ => (Color.White, 6f),
            };
            _renderer.AddLight(p.Position, r, col);
        }

        // Sentry muzzle glow: a small pre-flash that ramps with cooldown — about-to-fire
        // turrets pulse, idle ones are nearly dark. Helps the player see active overwatch in
        // a dim cave at a glance.
        foreach (var s in _run.Sentries)
        {
            var ready = MathHelper.Clamp(1f - s.Cooldown / Sentry.FireRate, 0f, 1f);
            if (ready > 0.05f)
            {
                var dir = new Vector2(MathF.Cos(s.Aim), MathF.Sin(s.Aim));
                _renderer.AddLight(s.Position + dir * 9f, 4f + 6f * ready, new Color(255, 200, 120));
            }
        }

        // Completed ship's beacon light — a slow warm pulse at the nose so the finished
        // ride home is visible from a ridge away.
        if (_run.PadPos is { } beaconPad && _run.ShipStage >= 3)
        {
            var bUp = _run.Planet.UpAt(beaconPad);
            var pulse = MathF.Sin(_run.RunTime * 2.5f) * 0.5f + 0.5f;
            _renderer.AddLight(beaconPad + bUp * 34f, 18f + pulse * 10f, new Color(255, 190, 90));
        }

        // Boss light sources — eyes, attack telegraphs, egg glow — all per-variant in the
        // renderer. Same off-screen cull as the body.
        if (titanOnScreen)
            TitanRenderer.AddLights(_renderer, _run.Titan, _run.Planet, _run.Player.Position, _renderer.Time);

        // Titan shots light their surroundings — flame warm, acid toxic green, spikes dim
        // bone-glint, laser cyan.
        foreach (var shot in _run.TitanShots)
        {
            var (lr, lc) = shot.Kind switch
            {
                TitanShotKind.Flame => (16f, new Color(255, 150, 50)),
                TitanShotKind.Acid  => (14f, new Color(140, 230, 60)),
                TitanShotKind.Lava  => (16f, new Color(255, 130, 40)),
                TitanShotKind.Spike => (8f, new Color(210, 200, 170)),
                _                   => (18f, new Color(120, 230, 255)),
            };
            _renderer.AddLight(shot.Position, lr, lc);
        }

        // The toxic cloud sheds a sickly green underglow onto the ground it's raining on.
        if (_run.AcidRainActive > 0f)
        {
            var cAng = _run.AcidRainAngle;
            var cUp = new Vector2(MathF.Cos(cAng), MathF.Sin(cAng));
            var ground = SpawnDirector.FindSurfaceSpawn(_run.Planet, cAng, _run.Planet.Radius);
            _renderer.AddLight(ground + cUp * 240f, 60f, new Color(110, 170, 60));
        }

        // Falling meteors blaze a warm light as they streak in.
        foreach (var m in _run.Meteors)
            _renderer.AddLight(m.Position, 26f, new Color(255, 150, 60));

        // Glowing creatures — magma slugs read as drifting coals, cave eyes as a faint gleam.
        foreach (var c in _run.Creatures) c.AddLight(_renderer);

        // Glowing particles (ore flecks, projectile sparks, explosion embers) feed back into
        // the lightmap so they actually illuminate the cave wall behind them.
        _particles.AddLights(_renderer);
        // Lava cells along their pool surface light up the cave roof (view-culled).
        _run.Cells.AddLights(_renderer, viewCentre, viewRadius,
            _camera.Zoom < 0.55f ? 6 : _camera.Zoom < 0.9f ? 3 : 1);

        // Depth darkness: subtract a radial gradient centred at the planet so deep tiles
        // dim toward black while the surface stays at full ambient. Radius = surfaceRadius
        // means the gradient tapers to zero exactly at the surface; falloff is quadratic
        // inward so most of the dimming concentrates near the inner half.
        var surfaceRadius = _run.Planet.Radius * Planet.TileSize;
        _renderer.Darken(_run.Planet.Center, surfaceRadius, new Color(170, 165, 185));

        _renderer.EndLighting();
        _renderer.CompositeLighting(new Point(VirtualWidth, VirtualHeight));
        // Multi-tap separable Gaussian bloom — bright spots (lava, projectiles, headlamp core)
        // bleed a soft glow over the scene through real downsample + 9-tap blur passes.
        _renderer.BloomLighting(new Point(VirtualWidth, VirtualHeight), new Color(70, 65, 85));
        // Cinematic vignette + subtle cool grade — pushes shadows slightly blue, keeps the
        // surface readable but adds depth at the screen edges.
        _renderer.VignetteScene(new Point(VirtualWidth, VirtualHeight));
        _renderer.GradeScene(new Point(VirtualWidth, VirtualHeight), new Color(245, 245, 255));

        _camera.Target = oldTarget;

        var depth = _run.Planet.Radius - (int)((_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize);
        // Top-left status: planet, ship progress, depth, titan HP, run meta. The toolbelt at
        // the bottom of the screen carries the per-tool readout, so we don't duplicate it here.
        var ship = _run.PadPos is null ? "NO PAD" : _run.ShipStage switch
        {
            0 => "PAD READY",
            1 => "HULL BUILT",
            2 => "ENGINE IN",
            _ => $"FUEL {_run.ShipFuel}/{FuelToLaunch} - L AT PAD TO FUEL/LAUNCH",
        };
        string titanStatus;
        if (_run.Titan.Health <= 0)
        {
            titanStatus = $"{TitanName(_run.Def.Titan).ToUpperInvariant()} SLAIN - SOUL CLAIMED";
        }
        else if (!_run.Titan.Hatched)
        {
            var secs = MathF.Max(0f, _run.Titan.EggTimer);
            titanStatus = $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} EGG  HATCH {(int)(secs / 60):0}:{(int)(secs % 60):00}  (ATTACK TO HATCH: {(int)_run.Titan.EggHealth} HP)";
        }
        else
        {
            titanStatus = $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} HP {(int)_run.Titan.Health}/{(int)_run.Titan.MaxHealth}";
        }
        var status = $"{_run.Def.Name.ToUpperInvariant()}   DEPTH {depth}   SHIP: {ship}   {titanStatus}\n" +
                     $"META: ESCAPES {_meta.Escapes}  KILLS {_meta.TitansDefeated}  DEEPEST {_meta.DeepestDepth}";
        var controls = "WASD MOVE  SPACE JUMP  1-9 TOOLBELT  LMB USE  WHEEL CYCLE  Q/E WEAPONS\n" +
                       "C CRAFT  T BEACON  L LAUNCH  B/N DEPOT BANK  F5 SAVE  G GOD MODE";
        if (_orbiting)
            controls = $"LEFT/RIGHT ORBIT THE PLANET   ENTER LAUNCH LANDER ({_meta.Rovers} ABOARD{(_meta.Rovers <= 0 ? " - DROP POD!" : "")})   SPACE LEAVE PLANET\n" +
                       $"L LOADOUT{(PendingKitCount() > 0 ? $" ({PendingKitCount()} KITS PACKED)" : "")}   PICK YOUR DROP SITE - THE LANDER FALLS FROM THE SHIP";
        else if (_landing)
            controls = "A/D STEER THE ROVER\nTOUCHDOWN WHERE YOU AIM";
        else if (_ascending)
            controls = "WASD/ARROWS STEER THE ROCKET - ANY DIRECTION\nCLIMB TO THE MOTHERSHIP AND DOCK";
        _renderer.DrawHudBars(VirtualWidth, VirtualHeight, _run.Player, (int)_run.Titan.Anger, status, controls);

        // Depot prompt: at the depot, show deposit/withdraw; otherwise, if a stash is waiting on
        // this planet and no depot is built yet, nudge the player to build one to reclaim it.
        var banked = BankCount();
        if (NearDepot())
        {
            var prompt = $"B DEPOSIT   N WITHDRAW   VAULT: {banked}";
            _renderer.DrawText(prompt, new Vector2((VirtualWidth - _renderer.MeasureText(prompt, 2)) / 2f, 92), new Color(200, 220, 150), 2);
        }
        else if (banked > 0 && _run.DepotPos is null)
        {
            var hint = $"BANKED STASH: {banked} - BUILD A STORAGE DEPOT TO WITHDRAW";
            _renderer.DrawText(hint, new Vector2((VirtualWidth - _renderer.MeasureText(hint)) / 2f, 92), new Color(170, 190, 140));
        }

        if (_toastTimer > 0)
            _renderer.DrawText(_toast,
                new Vector2((VirtualWidth - _renderer.MeasureText(_toast, 2)) / 2f, 64), new Color(160, 235, 160), 2);

        // EMP banner — flashes while Leatherback's pulse has the dwarf's tech fried.
        if (_run.Player.EmpTimer > 0f && ((int)(_run.RunTime * 4f) & 1) == 0)
        {
            var empWarn = $"EMP - SYSTEMS OFFLINE {_run.Player.EmpTimer:0.0}S";
            _renderer.DrawText(empWarn, new Vector2((VirtualWidth - _renderer.MeasureText(empWarn, 2)) / 2f, 132),
                new Color(120, 190, 255), 2);
        }

        // Cave-in banner — flashes while condemned rock hangs over the dwarf (see UpdateCaveInWarning).
        if (_caveInWarn > 0f && ((int)(_run.RunTime * 6f) & 1) == 0)
        {
            const string warn = "! CAVE-IN !";
            _renderer.DrawText(warn, new Vector2((VirtualWidth - _renderer.MeasureText(warn, 2)) / 2f, 112),
                new Color(240, 90, 70), 2);
        }
        _invUi.DrawInventoryPanel(_renderer, _run.Player, VirtualWidth);
        _invUi.DrawToolbelt(_renderer, _run.Player, VirtualWidth, VirtualHeight);
        _invUi.DrawCarry(_renderer, _run.Player);

        DrawHoverDebugLabel();

        if (_craftingMenu.Open)
            _craftingMenu.Draw(_renderer, _run.Player.Inventory, IsOwned, VirtualWidth, VirtualHeight);
        if (_debugMenu.Open)
            _debugMenu.Draw(_renderer, VirtualWidth, VirtualHeight);

        DrawStationIndicator();
        DrawScannerArrows();
        if (_orbiting && _loadoutOpen) DrawLoadoutMenu(_renderer.Batch);
        if (_screen == GameScreen.GameOver) DrawGameOverOverlay();
        DrawTransitionFlash();
        DrawFpsOverlay();

        if (_screenshotPending)
        {
            _screenshotPending = false;
            SaveScreenshot();
        }
    }

    private void SaveScreenshot()
    {
        var pp = GraphicsDevice.PresentationParameters;
        var w = pp.BackBufferWidth;
        var h = pp.BackBufferHeight;
        var data = new Color[w * h];
        GraphicsDevice.GetBackBufferData(data);

        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

        using var tex = new Texture2D(GraphicsDevice, w, h);
        tex.SetData(data);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, w, h);

        Console.WriteLine($"[screenshot] {path}");
    }

    private void DrawHoverDebugLabel()
    {
        var mouse = Mouse.GetState();
        var screenPos = new Vector2(mouse.X, mouse.Y);
        if (screenPos.X < 0 || screenPos.Y < 0 || screenPos.X >= VirtualWidth || screenPos.Y >= VirtualHeight)
            return;
        var worldCursor = _camera.ScreenToWorld(screenPos);

        // Cell-grid material wins over the underlying tile, so hovering a sand pile shows
        // "SAND" rather than the dirt tile beneath it.
        string label;
        var (cx, cy) = _run.Cells.WorldToCell(worldCursor);
        var mat = _run.Cells.Get(cx, cy);
        if (mat != Material.Empty)
        {
            label = $"MATERIAL: {mat}";
        }
        else
        {
            var (tx, ty) = _run.Planet.WorldToTile(worldCursor);
            var tile = _run.Planet.Get(tx, ty);
            if (tile == TileKind.Sky)
            {
                var wall = _run.Planet.GetWall(tx, ty);
                label = wall == TileKind.Sky ? "SKY" : $"SKY (WALL: {wall})";
            }
            else
            {
                label = $"TILE: {tile}";
            }
        }

        _renderer.DrawDebugLabel(label, screenPos + new Vector2(12, 12), Color.White);
    }

    /// <summary>Dim the frozen run and stack the reason lines centred. Single-sentence
    /// deaths render big; the multi-line campaign-victory summary steps down so every line
    /// fits the viewport.</summary>
    private void DrawGameOverOverlay()
    {
        var sb = _renderer.Batch;
        sb.Begin();
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 180));
        sb.End();

        var lines = _gameOverReason.Split('\n');
        var y = VirtualHeight / 2f - lines.Length * 20f;
        for (var i = 0; i < lines.Length; i++)
        {
            // Headline big when it fits, body lines smaller.
            var scale = i == 0 ? 3 : 2;
            if (_renderer.MeasureText(lines[i], scale) > VirtualWidth - 60) scale--;
            _renderer.DrawText(lines[i],
                new Vector2((VirtualWidth - _renderer.MeasureText(lines[i], scale)) / 2f, y),
                i == 0 ? Color.White : new Color(200, 205, 220), scale);
            y += 22f + scale * 6f;
        }
    }

    /// <summary>Render the ship build site: the pad slab always, then hull / engine / nose
    /// as stages install. All rects are rotated to local-up so the ship stands on the planet
    /// like everything else. Proportions: pad ~30 px wide, finished ship ~36 px tall — a
    /// landmark next to the ~7 px dwarf without dwarfing the mountains.</summary>
    /// <summary>Draw the storage depot: a stout riveted vault crate with a hatch, standing on
    /// the surface. Lights up with a small lamp when the player is close enough to use it.</summary>
    private void DrawDepot(Vector2 pos)
    {
        var up = _run.Planet.UpAt(pos);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y);
        var body = new Color(120, 95, 60);
        var bodyDark = new Color(80, 62, 40);
        var band = new Color(60, 46, 30);
        var metal = new Color(150, 155, 165);

        // Crate body + base.
        _renderer.DrawRect(pos + up * 2f, new Vector2(34f, 6f), band, rot);
        _renderer.DrawRect(pos + up * 12f, new Vector2(30f, 22f), bodyDark, rot);
        _renderer.DrawRect(pos + up * 12f, new Vector2(24f, 18f), body, rot);
        // Reinforcing bands + corner rivets.
        _renderer.DrawRect(pos + up * 12f, new Vector2(30f, 3f), band, rot);
        for (var s = -1; s <= 1; s += 2)
            _renderer.DrawRect(pos + up * 12f + right * (s * 13f), new Vector2(3f, 22f), band, rot);
        // Hatch + latch.
        _renderer.DrawRect(pos + up * 11f, new Vector2(12f, 12f), bodyDark, rot);
        _renderer.DrawRect(pos + up * 11f, new Vector2(4f, 4f), metal, rot);
        // Lit indicator lamp when in range, dim otherwise.
        var lamp = NearDepot() ? new Color(120, 230, 150) : new Color(70, 90, 70);
        _renderer.DrawCircle(pos + up * 24f + right * 10f, 2.4f, lamp);
    }

    private void DrawShip(Vector2 pos, int stage)
    {
        var up = _run.Planet.UpAt(pos);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y);
        var steel = new Color(150, 155, 170);
        var steelDark = new Color(95, 100, 115);
        var frame = new Color(70, 60, 50);

        // Pad: a broad slab on two squat legs, with hazard-stripe ends.
        _renderer.DrawRect(pos - up * 3f + right * 10f, new Vector2(3f, 4f), frame, rot);
        _renderer.DrawRect(pos - up * 3f - right * 10f, new Vector2(3f, 4f), frame, rot);
        _renderer.DrawRect(pos, new Vector2(30f, 3f), steelDark, rot);
        _renderer.DrawRect(pos + right * 13f, new Vector2(4f, 3f), new Color(200, 170, 60), rot);
        _renderer.DrawRect(pos - right * 13f, new Vector2(4f, 3f), new Color(200, 170, 60), rot);

        if (stage >= 1)
        {
            // Hull: the main fuselage cylinder with a lighter wall highlight.
            _renderer.DrawRect(pos + up * 13f, new Vector2(13f, 20f), steelDark, rot);
            _renderer.DrawRect(pos + up * 13f - right * 2f, new Vector2(7f, 18f), steel, rot);
        }
        if (stage >= 2)
        {
            // Engine: nozzle bell under the hull plus two swept tail fins.
            _renderer.DrawRect(pos + up * 2.5f, new Vector2(9f, 4f), frame, rot);
            _renderer.DrawRect(pos + up * 1f, new Vector2(12f, 2.5f), new Color(50, 45, 42), rot);
            _renderer.DrawRect(pos + up * 6f + right * 8f, new Vector2(6f, 8f), steelDark, rot + 0.5f);
            _renderer.DrawRect(pos + up * 6f - right * 8f, new Vector2(6f, 8f), steelDark, rot - 0.5f);
        }
        if (stage >= 3)
        {
            // Nav core: nose cone, porthole, and the blinking launch-ready beacon.
            _renderer.DrawRect(pos + up * 25f, new Vector2(10f, 5f), new Color(180, 80, 60), rot);
            _renderer.DrawRect(pos + up * 29f, new Vector2(6f, 4f), new Color(180, 80, 60), rot);
            _renderer.DrawRect(pos + up * 32f, new Vector2(3f, 3f), new Color(220, 110, 80), rot);
            _renderer.DrawCircle(pos + up * 16f - right * 1f, 2.4f, new Color(140, 200, 230));
            var blink = MathF.Sin(_run.RunTime * 2.5f) * 0.5f + 0.5f;
            _renderer.DrawCircle(pos + up * 34f, 1.2f + blink, new Color(255, 190, 90));
        }
    }

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && !prev.IsKeyDown(k);

    /// <summary>Play a positioned sound: pan follows the world point's screen-x (rotation-aware
    /// via the camera matrix) and volume falls off with distance, so a far-off explosion is a
    /// quiet thud on the correct side.</summary>
    private void PlayAt(string name, Vector2 worldPos, float vol = 1f, float pitch = 0f, float minGap = 0f)
    {
        var screen = Vector2.Transform(worldPos, _camera.View);
        var halfW = VirtualWidth * 0.5f;
        var pan = MathHelper.Clamp((screen.X - halfW) / halfW, -1f, 1f);
        var range = halfW / MathF.Max(_camera.Zoom, 0.01f) * 2.4f;
        var att = MathHelper.Clamp(1f - (worldPos - _camera.Target).Length() / range, 0f, 1f);
        _sfx.Play(name, vol * att, pitch, pan, minGap);
    }
}
