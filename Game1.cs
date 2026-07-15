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

/// <summary>Top-level screen state. Title is the save-slot menu (new game / continue,
/// three profiles); Loading covers the per-slot survey warm-up (world generation for
/// every planet's preview disc) so the space screen starts smooth instead of stuttering;
/// Space is the flyable solar system (entry screen and post-run hub — see
/// src/Game1.Space.cs); Playing is a live run; GameOver overlays the frozen run until R
/// returns you to your ship.</summary>
public enum GameScreen { Title, Loading, Space, Playing, GameOver }

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
    /// <summary>Live gravity-well pull from the Starspawn's pulse (consumed off
    /// Titan.PendingGravityWell): while the timer runs, the dwarf is dragged toward the
    /// well point with force ramping up as they get closer.</summary>
    private Vector2 _gravityWell;
    private float _gravityWellTimer;
    private float _gravityWellRadius;

    /// <summary>Titan-climbing: while riding, the dwarf clings to a bearing on the monster's
    /// body circle and moves with it — A/D walks around the hull, Space jumps off, and the
    /// monster's shake-off thrash flings the rider (see TickTitanRiding).</summary>
    private bool _riding;
    private float _rideAngle;

    /// <summary>Grappling-hook line: latched to terrain (<see cref="_grapAnchor"/>) or to the
    /// titan's hide (<see cref="_grapOnTitan"/> + body-local offset). While latched the rope
    /// is a hard length constraint — hold LMB to reel in, S pays line out, W cuts it.</summary>
    private Vector2? _grapAnchor;
    private bool _grapOnTitan;
    private Vector2 _grapLocal;
    private float _ropeLen;

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
    private readonly System.Diagnostics.Stopwatch _lightSw = new();
    private float _updateMs, _drawMs;
    private int _fps, _fpsFrames;
    private long _fpsMark = Environment.TickCount64;
    private int _gc0, _gc2;   // last-second GC collection counts for the DM_PERF [cnt] line

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

    /// <summary>Manual escape flight: the rocket is the player's from the instant it leaves
    /// the pad — A/D swing the nose, SPACE burns along it, E steps out. Flying close to
    /// the mothership engages an approach glide that completes the docking.</summary>
    private bool _ascending;

    /// <summary>Set once the rocket has been flown off the pad and set down somewhere else
    /// on the planet (E mid-flight). While parked, the hull draws at
    /// <see cref="_launchShipPos"/> and E within reach boards it again. Not persisted — a
    /// reloaded run finds the rocket back on its pad.</summary>
    private bool _shipParked;

    /// <summary>Parked in orbit after an atmosphere entry: the world is live below, the
    /// station holds at its anchor, and the player decides when (and where — A/D shifts the
    /// orbit) to launch the rover, or breaks orbit back to space.</summary>
    private bool _orbiting;

    /// <summary>Geo Scanner state: cached nearest-target positions, refreshed on a timer
    /// (the tile sweep is cheap but not per-frame cheap).</summary>
    private float _scanTimer;
    private Vector2? _scanFuel;
    private Vector2? _scanOre;

    /// <summary>Crafted geo-scanner state: the geo-scanner is ACTIVATED now — selecting it and
    /// firing emits a scan pulse that snapshots the deposits in a radius (tier-scaled) and
    /// marks them for a tier-scaled lifetime (up to 60s). Each hit carries its expiry; the
    /// draw skips the stale ones. Arrows radiate from the current player position, spaced so
    /// they never overlap.</summary>
    private readonly List<(TileKind kind, Vector2 pos, float expiry)> _geoScanHits = new();
    private float _scanCooldown;   // seconds until the scanner can pulse again
    private float _scanPulseT;     // time since the last pulse (drives the expanding ring)

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

    /// <summary>Escape-flight ship state: <see cref="_launchShipPos"/>/<see cref="_ascentVel"/>
    /// track the rocket, <see cref="_launchUp"/> the local vertical for the camera and the
    /// riding dwarf, <see cref="_ascentHeading"/> the way the nose points — upright at
    /// liftoff, then entirely the player's to swing with A/D.</summary>
    private Vector2 _launchShipPos;
    private Vector2 _launchUp;
    private Vector2 _ascentVel;
    private Vector2 _ascentHeading;
    /// <summary>Wall-clock seconds since launch — drives the DM_AUTOSHOT capture schedule so
    /// tooling can screenshot any screen, including the star map before a run starts.</summary>
    private float _totalTime;
    /// <summary>This frame's dt, captured at the top of UpdateFrame — for the item-table
    /// actions (Action&lt;Vector2&gt; lambdas) that need real time, e.g. held-build placement.</summary>
    private float _frameDt;
    /// <summary>Flamethrower / acid spewer: how long fire has been held (seconds, capped), and
    /// the run-time of the last stream tick. The stream starts short and grows to full reach the
    /// longer it's held; a gap in firing resets it. Shared by both hoses (only one fires at once).</summary>
    private float _streamHold;
    private float _streamLast;
    private const float StreamHoldMax = 0.735f;  // REAL seconds now: StreamReach adds _frameDt per call and hoses fire per-frame — 0.245 at the old every-3rd-frame cadence = this
    private readonly bool _bossCam = Environment.GetEnvironmentVariable("DM_BOSSCAM") is { Length: > 0 };
    private readonly bool _rigidDbg = Environment.GetEnvironmentVariable("DM_RIGIDDBG") is { Length: > 0 };
    private int _prevRigidCount;
    // Live harvest channel this frame (corpse or titan carcass) — drives the carving-knife
    // + progress-bar overlay in the draw pass. Null when nothing is being carved.
    private Vector2? _harvestFxPos;
    private float _harvestFxFrac;
    private float _harvestFxRadius;
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
    private readonly CharacterScreen _charScreen = new();
    private readonly InventoryUi _invUi = new();
    private readonly DebugMenu _debugMenu = new();

    private const int VirtualWidth = 1280;
    private const int VirtualHeight = 720;

    /// <summary>The fixed 1280×720 scene target every frame renders into; presented scaled
    /// (aspect-fit, letterboxed) to whatever the real window/display size is. All UI code
    /// keeps its hardcoded virtual coordinates; Screen maps the mouse back.</summary>
    private RenderTarget2D _sceneRt = null!;
    /// <summary>Pixel-grid world target (DM_PIXELRT=0 disables): the whole world layer —
    /// tiles, cells, entities, particles — renders into this low-res target at one texel
    /// per HALF world pixel (one texel per sim cell at Density 8), then reaches the scene
    /// through a single point-sampled INTEGER upscale. That is Noita's discipline: every
    /// grain quantises to the same uniform screen grid instead of rasterising at its own
    /// sub-pixel phase/rotation, which is what made dynamic matter read as ragged unequal
    /// squares. Sized per zoom step (gameplay zoom is locked to even factors); cinematic
    /// zooms (landing 1.5×, orbit) fall back to the direct full-res path.</summary>
    private RenderTarget2D? _worldRt;
    /// <summary>Liquid pass target (DM_LIQRT=0 disables): water/acid/oil cells rasterize
    /// in here each frame, then composite over the world in ONE alpha blend (with the
    /// metaball threshold + rim shader when available) — pools read as a single body of
    /// liquid instead of overlapping translucent quads. Matches the active world target's
    /// resolution (low-res on the pixel-grid path, virtual-res on the direct path).</summary>
    private RenderTarget2D? _liquidRt;
    /// <summary>Flame-stream coverage target — the flamethrower's metaball body. Separate
    /// from _liquidRt so fire can never threshold-fuse with water/acid it crosses.</summary>
    private RenderTarget2D? _flameRt;
    /// <summary>Integer upscale factor of the pixel-grid path this frame; 0 = direct path.</summary>
    private int _pixelK;
    /// <summary>Reentrancy guard for the ClientSizeChanged → ApplyChanges round-trip.</summary>
    private bool _resizing;

    /// <summary>Terraria-style propagated light field — rebuilt every playing frame from
    /// the view. See src/Rendering/LightGrid.cs.</summary>
    private readonly LightGrid _lightGrid = new();

    /// <summary>Boot-time survey warm-up (one world generated per planet, for the system
    /// view's terrain discs and the M-key ore census). The load screen holds until it
    /// completes so the generation never contends with live play.</summary>
    private System.Threading.Tasks.Task? _warmTask;
    private System.Threading.CancellationTokenSource? _warmCts;
    private int _warmDone;
    private int _warmTotal;
    private volatile string? _warmName;
    /// <summary>Wall time the Loading screen was (re-)entered — its gates are relative to
    /// this, since a slot switch can enter Loading long after boot.</summary>
    private float _loadingSince;

    /// <summary>Title screen state: highlighted slot + a cached summary per slot
    /// (meta progress or null = empty, plus whether a suspended run exists).</summary>
    private int _titleCursor;
    private readonly (MetaSave? Meta, bool HasRun)[] _slotInfo = new (MetaSave?, bool)[SaveSlots.Count];
    /// <summary>Hit rects cached by the draw passes for mouse hover/click (1-frame stale,
    /// same pattern as the toolbelt).</summary>
    private readonly Rectangle[] _titleCardRects = new Rectangle[SaveSlots.Count];
    private readonly Rectangle[] _pauseOptionRects = new Rectangle[3];
    /// <summary>The title screen's living pixel vista, generated once at load and drawn at
    /// 2× point-sampled: a static sky layer and a static land layer sandwich the animated
    /// elements (setting sun, rotating gas giant, ambient fauna). _titleSurfY is the crust
    /// lip per column so the surface critters can walk the actual ground line.</summary>
    private Texture2D _titleSkyTex = null!;
    private Texture2D _titleLandTex = null!;
    private Texture2D _titlePlanetMap = null!;
    private Texture2D _titleRingTex = null!;
    private Texture2D _titleSunTex = null!;
    private Texture2D _titleMoonTex = null!;
    /// <summary>Baked volcano summit positions (640-space) — the live eruption plumes
    /// anchor here.</summary>
    private Point _titleVolcanoA, _titleVolcanoB;
    private int[] _titleSurfY = System.Array.Empty<int>();
    /// <summary>Esc on the title asks before quitting.</summary>
    private bool _titleQuitConfirm;
    private Rectangle _titleConfirmYes, _titleConfirmNo;

    /// <summary>Pause menu (Esc during play/space). While open the whole sim freezes.</summary>
    private bool _pauseOpen;
    private int _pauseCursor;
    /// <summary>Half-rate cell-sim toggle while parked in orbit.</summary>
    private bool _orbitCellTick;

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
        // Variable timestep + our own precise 60 Hz limiter (see EndDraw). Two macOS
        // findings forced this: (1) SynchronizeWithVerticalRetrace never actually engages
        // here (uncapped runs hit 78-80 fps), so nothing paced the loop but MonoGame's
        // fixed-step sleep; (2) that sleep OVERSHOOTS (Thread.Sleep granularity), so
        // paradoxically the CHEAPER the frame the worse the rate — light scenes latched
        // IsRunningSlowly at 22-34 fps while heavy ones held a clean 60. With the precise
        // limiter dt arrives a steady ~16.7 ms, so per-tick probability rates in the sim
        // keep their authored 60 Hz meaning; UpdateFrame clamps dt on real hitches.
        // DM_FIXEDSTEP=1 restores the old MonoGame fixed-step loop for A/B comparison.
        // NOTE if it ever returns: leave MaxElapsedTime at its 500 ms default — capping
        // it at 50 ms locked the fixed-step loop at a permanent 30 fps.
        IsFixedTimeStep = Environment.GetEnvironmentVariable("DM_FIXEDSTEP") is { Length: > 0 };
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
        // MonoGame injects a 20 ms Thread.Sleep into EVERY tick while the window isn't
        // frontmost (InactiveSleepTime) — that's an instant 24-33 fps whenever focus sits
        // elsewhere, which read as random "the game is lagging" states (and it poisoned
        // every backgrounded perf capture this session). The world keeps simulating when
        // unfocused, so it should keep rendering honestly too; our EndDraw limiter is the
        // one and only pacer.
        InactiveSleepTime = TimeSpan.Zero;
        Window.Title = "Dwarf Miner";
        // The scene renders at the fixed virtual resolution and scales to the window, so
        // the window itself is free to be any size (drag-resize or F11 fullscreen).
        Window.AllowUserResizing = true;
        Window.ClientSizeChanged += (_, _) =>
        {
            if (_resizing || _graphics.IsFullScreen) return;
            _resizing = true;
            var b = Window.ClientBounds;
            if (b.Width > 0 && b.Height > 0)
            {
                _graphics.PreferredBackBufferWidth = b.Width;
                _graphics.PreferredBackBufferHeight = b.Height;
                _graphics.ApplyChanges();
            }
            _resizing = false;
        };
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
        // Adopt any pre-slot save as slot 1 BEFORE anything reads (and thereby creates)
        // slot files — a fresh meta materialising first would block the migration and
        // orphan the legacy campaign.
        SaveSlots.MigrateLegacy();
        _meta = MetaSave.Load();
        // Roll (or restore) the campaign's 7 procedurally generated planets before anything
        // touches PlanetDefs.All — the seed persists in the meta save so the same system
        // greets you across restarts, and only a completed campaign rerolls it.
        if (_meta.WorldSeed == 0)
        {
            _meta.WorldSeed = Random.Shared.Next(1, int.MaxValue);
            _meta.Save();
        }
        PlanetDefs.Activate(PlanetGen.Campaign(_meta.WorldSeed));
        // Boot into space with the mothership wherever it was left (fresh installs park at
        // the farthest charted world), and the saved volume step applied.
        _space = new SpaceSim();
        RestoreShipState();
        _sfx.Master = VolumeSteps[Math.Clamp(_meta.VolumeStep, 0, VolumeSteps.Length - 1)];
        // The saved display mode (F11) applies before the first frame.
        if (_meta.Fullscreen) SetFullscreen(true);
        // Warm the survey-world cache in the background so the system view can rasterize
        // real terrain discs (and the M survey opens instantly) without a frame hitch.
        // The load screen holds on this task: generating a full world per planet used to
        // run underneath the space screen and stutter the first seconds of play.
        StartSurveyWarm();
        // DM_AUTOSTART=<planet-id|resume> skips the space screen and jumps straight into a run
        // (or resumes the suspend save) — keeps DM_AUTOSHOT-driven gameplay verification
        // working without menu input.
        if (Environment.GetEnvironmentVariable("DM_AUTOSTART") is { Length: > 0 } auto)
        {
            if (auto == "resume") ResumeRun();
            // Generated planet ids vary by seed, so the new biomes get stable aliases:
            // "ocean"/"acidworld" start the campaign's guaranteed instance of each.
            else if (auto == "ocean") StartNewRun(FirstDef(d => d.LakeScale > 2f));
            else if (auto == "acidworld") StartNewRun(FirstDef(d => d.AcidRain));
            else if (auto == "city") StartNewRun(FirstDef(d => d.Biome == "city"));
            else StartNewRun(PlanetDefs.ById(auto));
        }
        // DM_ORBIT=<planet-id> boots straight into the parking orbit — tooling can
        // screenshot the orbit state without flying there.
        else if (Environment.GetEnvironmentVariable("DM_ORBIT") is { Length: > 0 } orbit)
        {
            EnterOrbit(PlanetDefs.ById(orbit));
        }
        // Normal boot (no test-hook jump-in): land on the title screen — the save-slot
        // choice then (re)loads that profile behind the load screen. Test hooks skip
        // straight in on slot 1 (DM_ENTRYTEST needs the space screen, so it skips the
        // title and goes through the load screen directly).
        if (_screen == GameScreen.Space)
        {
            if (Environment.GetEnvironmentVariable("DM_ENTRYTEST") is { Length: > 0 })
            {
                _loadingSince = 0f;
                _screen = GameScreen.Loading;
            }
            else
            {
                RefreshSlotSummaries();
                _screen = GameScreen.Title;
            }
        }
        base.Initialize();
    }

    /// <summary>(Re)start the background survey warm-up for the ACTIVE campaign. Any
    /// previous slot's warm-up is cancelled first — its cache entries were cleared and
    /// letting it keep writing would repopulate them with the wrong worlds.</summary>
    private void StartSurveyWarm()
    {
        // DM_NOWARM=1 skips the background survey warm-up entirely — perf measurement
        // hook: DM_AUTOSTART jumps past the load screen that normally absorbs the warm-up,
        // so without this the first ~8s of any autostarted FPS reading is polluted by
        // full world gens contending on background threads.
        if (Environment.GetEnvironmentVariable("DM_NOWARM") is { Length: > 0 })
        {
            _warmTotal = 0;
            return;
        }
        _warmCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _warmCts = cts;
        _warmDone = 0;
        var defs = PlanetDefs.All;
        _warmTotal = defs.Length;
        _warmTask = System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var def in defs)
            {
                if (cts.IsCancellationRequested) return;
                _warmName = def.Name;
                Space.Survey.WorldFor(def);
                System.Threading.Interlocked.Increment(ref _warmDone);
            }
        });
    }

    /// <summary>Re-read each slot's summary for the title screen.</summary>
    private void RefreshSlotSummaries()
    {
        for (var i = 0; i < SaveSlots.Count; i++)
            _slotInfo[i] = (MetaSave.Peek(i + 1), RunSave.ExistsIn(i + 1));
    }

    /// <summary>Commit to a save slot from the title screen: load (or freshly create) its
    /// profile, roll/restore its campaign, reset every per-campaign cache, and hand over
    /// to the load screen while its survey warm-up runs.</summary>
    private void SelectSlot(int slot)
    {
        SaveSlots.Active = slot;
        _meta = MetaSave.Load();
        if (_meta.WorldSeed == 0)
        {
            _meta.WorldSeed = Random.Shared.Next(1, int.MaxValue);
            _meta.Save();
        }
        PlanetDefs.Activate(PlanetGen.Campaign(_meta.WorldSeed));
        // Per-campaign caches: survey worlds, disc previews, and prefetched sessions all
        // key by planet id, and ids collide across campaigns ("moon", biome aliases).
        Space.Survey.Reset();
        foreach (var tex in _planetPreview.Values) tex.Dispose();
        _planetPreview.Clear();
        _prefetch.Clear();
        _space = new SpaceSim();
        RestoreShipState();
        _sfx.Master = VolumeSteps[Math.Clamp(_meta.VolumeStep, 0, VolumeSteps.Length - 1)];
        StartSurveyWarm();
        _loadingSince = _totalTime;
        _screen = GameScreen.Loading;
        _camera?.SnapTo(_space.ShipPos, 0f);
    }

    /// <summary>Title-screen input: hover highlights a slot card, click or Enter commits
    /// (new game on an empty slot, continue otherwise); arrows / 1-3 drive the cursor from
    /// the keyboard. Esc here quits — the title IS the quit screen.</summary>
    private void UpdateTitle(KeyboardState keys, MouseState mouse)
    {
        var confirmClick = mouse.LeftButton == ButtonState.Pressed
                        && _prevMouse.LeftButton != ButtonState.Pressed;
        // Quit needs a second opinion: Esc opens the confirm dialog; Y/Enter/click-YES
        // quits, anything dismissive cancels.
        if (_titleQuitConfirm)
        {
            if (Pressed(keys, _prevKeys, Keys.Escape) || Pressed(keys, _prevKeys, Keys.N)
                || (confirmClick && _titleConfirmNo.Contains(mouse.X, mouse.Y)))
                _titleQuitConfirm = false;
            else if (Pressed(keys, _prevKeys, Keys.Enter) || Pressed(keys, _prevKeys, Keys.Y)
                || (confirmClick && _titleConfirmYes.Contains(mouse.X, mouse.Y)))
                Exit();
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.Escape))
        {
            _titleQuitConfirm = true;
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.Up) || Pressed(keys, _prevKeys, Keys.W))
            _titleCursor = (_titleCursor - 1 + SaveSlots.Count) % SaveSlots.Count;
        if (Pressed(keys, _prevKeys, Keys.Down) || Pressed(keys, _prevKeys, Keys.S))
            _titleCursor = (_titleCursor + 1) % SaveSlots.Count;
        if (Pressed(keys, _prevKeys, Keys.D1)) _titleCursor = 0;
        if (Pressed(keys, _prevKeys, Keys.D2)) _titleCursor = 1;
        if (Pressed(keys, _prevKeys, Keys.D3)) _titleCursor = 2;

        var clicked = mouse.LeftButton == ButtonState.Pressed
                   && _prevMouse.LeftButton != ButtonState.Pressed;
        var hoverAny = false;
        for (var i = 0; i < SaveSlots.Count; i++)
        {
            if (!_titleCardRects[i].Contains(mouse.X, mouse.Y)) continue;
            _titleCursor = i;
            hoverAny = true;
        }
        if (Pressed(keys, _prevKeys, Keys.Enter) || (clicked && hoverAny))
        {
            _sfx.Play("ui", 0.7f);
            SelectSlot(_titleCursor + 1);
        }
    }

    /// <summary>Pause-menu input: hover + click, or arrows + Enter — resume / return to the
    /// title (saving first) / quit.</summary>
    private void UpdatePauseMenu(KeyboardState keys, MouseState mouse)
    {
        if (Pressed(keys, _prevKeys, Keys.Escape)) { _pauseOpen = false; return; }
        if (Pressed(keys, _prevKeys, Keys.Up) || Pressed(keys, _prevKeys, Keys.W))
            _pauseCursor = (_pauseCursor + 2) % 3;
        if (Pressed(keys, _prevKeys, Keys.Down) || Pressed(keys, _prevKeys, Keys.S))
            _pauseCursor = (_pauseCursor + 1) % 3;

        var clicked = mouse.LeftButton == ButtonState.Pressed
                   && _prevMouse.LeftButton != ButtonState.Pressed;
        var hoverAny = false;
        for (var i = 0; i < _pauseOptionRects.Length; i++)
        {
            if (!_pauseOptionRects[i].Contains(mouse.X, mouse.Y)) continue;
            _pauseCursor = i;
            hoverAny = true;
        }
        if (!Pressed(keys, _prevKeys, Keys.Enter) && !(clicked && hoverAny)) return;
        _sfx.Play("ui", 0.7f);
        switch (_pauseCursor)
        {
            case 0:
                _pauseOpen = false;
                break;
            case 1:
                SaveAndReturnToTitle();
                break;
            default:
                Exit();   // the Exiting handler suspend-saves on the way out
                break;
        }
    }

    /// <summary>Bank everything and go back to the title's slot menu — same saves the quit
    /// path writes, so switching profiles never loses progress.</summary>
    private void SaveAndReturnToTitle()
    {
        if (_screen == GameScreen.Playing && !_orbiting && _run is not null) RunSave.Write(_run);
        if (_screen == GameScreen.Space) CaptureShipState();
        _meta?.Save();
        _pauseOpen = false;
        _entryDef = null;
        RefreshSlotSummaries();
        _screen = GameScreen.Title;
    }

    /// <summary>Apply the display mode: borderless fullscreen at the desktop resolution, or
    /// a plain window back at the virtual size. The scene itself always renders at 1280×720
    /// and scales, so no other system notices the change.</summary>
    private void SetFullscreen(bool on)
    {
        _graphics.HardwareModeSwitch = false;   // borderless — no display-mode switch flicker
        _graphics.IsFullScreen = on;
        if (on)
        {
            _graphics.PreferredBackBufferWidth = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
            _graphics.PreferredBackBufferHeight = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;
        }
        else
        {
            _graphics.PreferredBackBufferWidth = VirtualWidth;
            _graphics.PreferredBackBufferHeight = VirtualHeight;
        }
        _graphics.ApplyChanges();
    }

    /// <summary>The heavy half of starting a run: world generation, cell seeding, and the
    /// liquid pre-settle. Static and touching only freshly built objects, so the space
    /// screen can run it on a background thread while the player is still flying — by the
    /// time they press Enter the world is usually already built (seamless landing).</summary>
    internal static Session BuildSessionWorld(PlanetDef def, System.Threading.CancellationToken settleToken = default)
    {
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var seed = (int)DateTime.Now.Ticks;
        var run = new Session(def);
        run.Planet = WorldGen.Generate(seed, def);
        var tGen = loadSw.ElapsedMilliseconds;
        run.Cells = new Cells(run.Planet);
        run.Physics = new Physics(run.Planet, run.Cells);
        // Lava seeding: caves (Sky tiles) inside the lava SEA band fill with lava —
        // volcanic worlds flood far shallower cavities. The sea has a floor now
        // (WorldGen.CaveStrata): below it lie the sealed dry strata, deep cave layers the
        // player mines into, kept from ever plumbing into the sea by solid seams.
        if (def.LavaFillFrac > 0f)
        {
            var (_, _, seaFloor) = WorldGen.CaveStrata(run.Planet, def);
            run.Cells.FillSkyTilesWithin(run.Planet.Radius * def.LavaFillFrac, Material.Lava, seaFloor);
        }

        // Volcano priming: the crater pool, throat, and magma chamber recorded by world gen
        // fill with lava so every volcano stands primed — a continuous column from the deep
        // chamber up to the crater. (Acid volcanoes seed via AcidSeeds below instead.)
        foreach (var (lx, ly) in run.Planet.LavaSeeds)
            run.Cells.FillTileSilent(lx, ly, Material.Lava);

        // Water seeding: world gen recorded lake-basin and reservoir tiles; pour the cells in
        // now. Water is always sim cells (never solid tiles), so it settles, flows into player
        // tunnels, and quenches lava per the cell rules.
        foreach (var (wsx, wsy) in run.Planet.WaterSeeds)
            run.Cells.FillTileSilent(wsx, wsy, Material.Water);

        // Hazard cells: gas rises to the cave roofs, acid settles to the floors. Poured after
        // water so the pre-settle below carries them to rest alongside it.
        foreach (var (gx, gy) in run.Planet.GasSeeds)
            run.Cells.FillTileSilent(gx, gy, Material.Gas);
        foreach (var (ax, ay) in run.Planet.AcidSeeds)
            run.Cells.FillTileSilent(ax, ay, Material.Acid);
        // Oil sumps: inert black pools in the mid-crust caves — dead weight until a stray
        // flame, a lava tongue, or a careless explosion finds them.
        foreach (var (ox, oy) in run.Planet.OilSeeds)
            run.Cells.FillTileSilent(ox, oy, Material.Oil);
        // One boundary-wake pass for everything seeded silently above: only free surfaces
        // start awake (a lake's skin, a gas pocket's roof); hemmed interiors are born
        // asleep — which is their steady state anyway — instead of parading millions of
        // cells through the first sim ticks. See Cells.WakeFreeSurfaces.
        run.Cells.WakeFreeSurfaces(0, run.Cells.Height - 1);
        var tSeed = loadSw.ElapsedMilliseconds;

        // The planet-wide population census (creatures + spawners) also runs on the build
        // thread: its spawn-space carving marks physics dirty at ~85 sites across the
        // planet, and paying that on arrival meant several seconds of collapse-churn
        // framerate right as the player landed.
        SpawnDirector.SpawnInitialFauna(run);
        run.Populated = true;
        // Digest the census's physics dirt UNCONDITIONALLY (not under the cancellable
        // settle): a last-second atmosphere entry that cancels the settle must not dump
        // planet-wide collapse checks into the first live frames. A NoFauna world ran no
        // census, so there is nothing to digest.
        if (!def.NoFauna)
            for (var i = 0; i < 30; i++) run.Physics.Update(1f / 60f);
        var tCensus = loadSw.ElapsedMilliseconds;

        // Pre-settle the seeded liquids during load: the first ~2s of cell ticks carry every
        // seeded cell awake (tens of ms per tick at Density 8). Burning them here turns a
        // visible gameplay stutter into a slightly longer world-gen pause; after settling,
        // hemmed pool interiors sleep and the steady-state tick is cheap. Physics ticks
        // alongside so the census's dirty tiles are digested here too. The settle is by
        // far the heavy half (the generation above is ~200 ms; a big world settles for many
        // seconds), so it's cancellable: atmosphere entry takes the world the moment the
        // token fires and the leftover settling runs live in the orbit/descent frames — a
        // few heavy frames from orbit beat any hold at the atmosphere.
        // The far-field throttle runs during the settle too (focus = the surface spawn
        // point), and the loop stops early the moment a tick comes back CHEAP — the whole
        // point is to burn the wake storm, and once a tick is a frame-budget rounding
        // error the storm is burnt. Worlds that never settle (the QA rig's simmering lava
        // sea) instead hit the wall-clock budget rather than holding the load hostage:
        // their churn is steady-state, so no amount of pre-settling would help anyway.
        run.Cells.SimFocus = SpawnDirector.FindSurfaceSpawn(run.Planet, -MathF.PI / 2f, run.Planet.Radius);
        var settleSw = System.Diagnostics.Stopwatch.StartNew();
        var settleTicks = 0;
        var bestTick = double.MaxValue;
        var sinceBest = 0;
        for (var i = 0; i < 120 && !settleToken.IsCancellationRequested; i++)
        {
            var tickStart = settleSw.Elapsed.TotalMilliseconds;
            run.Cells.Update(1f / 60f);
            run.Physics.Update(1f / 60f);
            settleTicks++;
            var cost = settleSw.Elapsed.TotalMilliseconds - tickStart;
            if (cost < 0.6) break;                                             // settled
            // Plateau: ticks stopped getting cheaper, so what's left isn't a wake storm
            // burning down — it's the world's steady simmer (the QA rig's lava sea, an
            // ocean's shoreline), which no amount of pre-settling retires. Hand it to the
            // live frames, where the far-field throttle already owns it.
            if (cost < bestTick * 0.95) { bestTick = cost; sinceBest = 0; }
            else if (++sinceBest >= 12) break;
            if (settleSw.ElapsedMilliseconds > 700) break;                     // budget
        }
        run.Cells.SimFocus = null;   // live frames re-set it every tick (or leave headless null)
        // Rigid debris comes online only now: the census/settle churn above must digest as
        // plain dust (bodies spawned during a build nobody is watching would just hang in
        // the air waiting for the first live frame, and could exhaust the body budget).
        run.Rigid = new RigidBodies(run.Planet, run.Cells, run.Physics);
        run.Physics.DetachToRigid = run.Rigid.TryDetach;
        Console.WriteLine($"[load] {def.Id}: gen {tGen}ms, seed {tSeed - tGen}ms, " +
            $"census {tCensus - tSeed}ms, settle {loadSw.ElapsedMilliseconds - tCensus}ms " +
            $"({settleTicks} ticks), total {loadSw.ElapsedMilliseconds}ms");
        return run;
    }

    private void StartNewRun(PlanetDef def, bool descend = false)
    {
        // A prefetched world (built in the background while flying nearby) makes this
        // near-instant; otherwise build it here and eat the pause.
        _run = TakePrefetchedSession(def) ?? BuildSessionWorld(def);
        // Sky heightmap for the lighting pass builds now, in the load, instead of lazily
        // on the already-overloaded first live frame.
        _lightGrid.PrewarmSky(_run.Planet);
        // Ship-stage recipes (nav core cost) vary per planet — rebuild the crafting table.
        Crafting.SetPlanet(def);

        // Spawn the dwarf on top of whatever mountain is at angle -π/2 — walk down from
        // far above until the first solid tile, then float a few pixels above it.
        var spawnAngle = -MathF.PI / 2f;
        // DM_VOLCANO=1 spawns beside the first volcano vent instead, so tooling can
        // screenshot craters and eruptions without hiking there (pairs with DM_ERUPT).
        if (Environment.GetEnvironmentVariable("DM_VOLCANO") is { Length: > 0 }
            && _run.Planet.VolcanoVents.Count > 0)
        {
            var (vvx, vvy, _) = _run.Planet.VolcanoVents[0];
            var vRel = _run.Planet.TileToWorld(vvx, vvy) - _run.Planet.Center;
            spawnAngle = MathF.Atan2(vRel.Y, vRel.X) + 0.09f;   // offset onto the flank
        }
        var surfacePos = SpawnDirector.FindSurfaceSpawn(_run.Planet, spawnAngle, _run.Planet.Radius);
        // DM_WARREN=1 drops the dwarf straight into the first lizard-city hall so tooling
        // can screenshot the warren without spelunking to it (pairs with a warren world:
        // acid/lava biomes only, e.g. DM_AUTOSTART=ember — city worlds have no warrens).
        if (Environment.GetEnvironmentVariable("DM_WARREN") is { Length: > 0 }
            && _run.Planet.LizardDens.Count > 0)
        {
            var (ldr, ldt) = _run.Planet.LizardDens[0];
            surfacePos = _run.Planet.TileToWorld(ldr, ldt);
        }
        // DM_CITY=1 drops the dwarf at the first city district's bearing, so tooling can
        // screenshot the skyline (doors, ladders, furniture, patrols) without the hike.
        if (Environment.GetEnvironmentVariable("DM_CITY") is { Length: > 0 }
            && _run.Planet.CityDistricts.Count > 0)
        {
            surfacePos = SpawnDirector.FindSurfaceSpawn(
                _run.Planet, _run.Planet.CityDistricts[0].ang, _run.Planet.Radius);
        }
        // DM_SWIM=1 drops the dwarf into the first lake found, so tooling can screenshot
        // swimming, the breath meter, and the aquatic fauna without hiking to water.
        if (Environment.GetEnvironmentVariable("DM_SWIM") is { Length: > 0 })
        {
            for (var a = 0f; a < MathHelper.TwoPi; a += 0.05f)
            {
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                var hit = false;
                for (var d = _run.Planet.Radius + 40; d > 20 && !hit; d--)
                {
                    var pos = _run.Planet.Center + dir * (d * Planet.TileSize);
                    if (_run.Planet.IsSolidAt(pos)) break;
                    if (_run.Cells.CountWaterNear(pos, 3f) >= 3)
                    {
                        surfacePos = pos - dir * 8f;
                        hit = true;
                    }
                }
                if (hit) break;
            }
        }
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
            // Low-gravity worlds (the Hollow asteroid): everything falls gently and the
            // same jump impulse carries more than twice as high.
            Gravity = 320f * def.GravityScale,
        };
        _run.HasCannon = _meta.StartWithCannon;
        // God mode grants everything PERMANENTLY from frame one — every item at max tier
        // plus unlimited crafting materials (see GrantGodmodeItems).
        if (_run.Player.FlyMode)
        {
            GrantGodmodeItems();
            GrantGodmodeMaterials();
            foreach (var w in GodWeaponIds) _run.Player.Toolbelt.AutoEquip(w);
        }
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
        _run.Player.JetTier4 = Upgrades.Owned(_meta, "jetpack4");
        // The pack is a real worn item now: it rides down in the backpack and straps into
        // the paper doll's Back slot (it only burns while equipped there).
        if (_run.Player.HasJetpack)
        {
            _run.Player.Inventory.Add("jetpack", 1);
            _run.Player.Equipment.AutoEquip("jetpack");
        }
        _run.Player.HasO2Recycler = Upgrades.Owned(_meta, "o2");
        _run.Player.O2Tier2 = Upgrades.Owned(_meta, "o22");
        if (Upgrades.Owned(_meta, "drill")) _run.Player.PickaxeTier++;
        _run.Player.HasPlating = Upgrades.Owned(_meta, "plating");
        _run.Player.HasFins = Upgrades.Owned(_meta, "fins");
        _run.Player.LungTier = Upgrades.Owned(_meta, "lungs2") ? 2 : Upgrades.Owned(_meta, "lungs") ? 1 : 0;
        _run.Player.HasGills = Upgrades.Owned(_meta, "gills");
        // The vacsuit is a full pressure suit with a sealed helmet — it lets you breathe on
        // airless worlds (the air meter only drains there without it).
        _run.Player.HasHelmet = Upgrades.Owned(_meta, "vacsuit");
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
        _gravityWellTimer = 0f;
        // The Starspawn's egg is buried near the core — carve its nest cavern so the shell
        // rests in a real chamber the player breaks INTO, not a ghost drawn inside rock.
        if (_run.Titan.Kind == TitanKind.CosmicOctopus)
        {
            CarveTitanNest();
            // DM_BOSSCAM can't reach an egg buried by the core: surface the boss hatched
            // beside the dwarf instead, so tooling can actually frame it.
            if (Environment.GetEnvironmentVariable("DM_BOSSCAM") is { Length: > 0 })
            {
                var camUp = _run.Planet.UpAt(_run.Player.Position);
                _run.Titan.Position = _run.Player.Position + camUp * 130f;
                _run.Titan.Hatch();
            }
        }
        // Prefetched sessions arrive already populated (and settled) from the build thread.
        if (!_run.Populated) SpawnDirector.SpawnInitialFauna(_run);
        // DM_FAUNA=1 parades the biome fauna beside the spawn so tooling can screenshot
        // creature art without hunting for natural spawns.
        if (Environment.GetEnvironmentVariable("DM_FAUNA") is { Length: > 0 })
        {
            var fUp = _run.Planet.UpAt(_run.Player.Position);
            var fRight = new Vector2(-fUp.Y, fUp.X);
            var kinds = new[] { CreatureKind.SporeBat, CreatureKind.CrystalCrawler, CreatureKind.VoidWraith,
                                CreatureKind.CaveSlime, CreatureKind.AcidSpitter, CreatureKind.BomberBeetle,
                                CreatureKind.SnapperVine, CreatureKind.RockMimic,
                                CreatureKind.Civilian, CreatureKind.Lizardman,
                                CreatureKind.Peacekeeper, CreatureKind.Saucer,
                                CreatureKind.AlienWhale, CreatureKind.AlienCrab,
                                CreatureKind.Moonlet, CreatureKind.VacLeech, CreatureKind.Glimmermaw,
                                CreatureKind.StarJelly, CreatureKind.VoidBarnacle,
                                CreatureKind.Selenite, CreatureKind.DustDevil,
                                CreatureKind.Kraken };
            for (var i = 0; i < kinds.Length; i++)
                _run.Creatures.Add(new Creature(
                    _run.Player.Position + fRight * (26f + i * 22f) + fUp * 8f, kinds[i]));
        }
        // DM_RIGIDTEST=1 hangs a few free-floating stone slabs in the sky beside the spawn —
        // they condemn on the first settle pass, shear off as rigid chunks, and tumble down
        // in front of the tester (pairs with DM_RIGID=0 to compare the legacy dust path).
        if (Environment.GetEnvironmentVariable("DM_RIGIDTEST") is { Length: > 0 })
        {
            var (sx, sy) = _run.Planet.WorldToTile(_run.Player.Position);
            for (var slab = 0; slab < 3; slab++)
            {
                // Parked beside the spawn column, not over it — the demo shouldn't open
                // with 112 tiles of rock landing on the tester's head.
                var baseR = Math.Min(sx + 22 + slab * 14, _run.Planet.Rings - 12);
                var baseT = sy + 12 + slab * 20;
                for (var dr = 0; dr < 7; dr++)
                    for (var dtc = 0; dtc < 16; dtc++)
                    {
                        _run.Planet.Set(baseR + dr, baseT + dtc,
                            slab == 1 ? TileKind.Granite : TileKind.Stone);
                        _run.Physics.MarkDirty(baseR + dr, baseT + dtc);
                    }
            }
        }
        _run.SpawnTimer = 6f;
        _run.FaunaTimer = 8f;
        // DM_METEOR=<s> overrides the first-strike delay for testing.
        _run.MeteorTimer = float.TryParse(Environment.GetEnvironmentVariable("DM_METEOR"), out var mt)
            ? mt : 16f + (float)Random.Shared.NextDouble() * 14f;   // first strike ~16-30s in
        // Disasters share one clock (AmbientDirector): one at a time, spaced by planet
        // difficulty (~7 min gentle → ~2 min brutal). First one lands about mid-spacing.
        // DM_FLARE / DM_ACIDRAIN / DM_ERUPT =<seconds> force that kind then, for tooling.
        _run.DisasterTimer = AmbientDirector.NextInterval(def) * 0.5f;
        if (float.TryParse(Environment.GetEnvironmentVariable("DM_FLARE"), out var ft))
            { _run.DisasterTimer = ft; _run.NextDisaster = DisasterKind.Flare; }
        if (float.TryParse(Environment.GetEnvironmentVariable("DM_ACIDRAIN"), out var art))
            { _run.DisasterTimer = art; _run.NextDisaster = DisasterKind.AcidRain; }
        if (float.TryParse(Environment.GetEnvironmentVariable("DM_ERUPT"), out var vt))
            { _run.DisasterTimer = vt; _run.NextDisaster = DisasterKind.Eruption; }
        _gameOverReason = "";
        _craftingMenu.Reset();
        _charScreen.Reset();
        // DM_CHARSCREEN=1 opens the character screen on run start — visual-verification
        // hook, since synthetic key input isn't available headless (same as DM_AUTOSAVE).
        if (Environment.GetEnvironmentVariable("DM_CHARSCREEN") is { Length: > 0 })
            _charScreen.Show();
        // DM_CRAFT=1 opens the crafting menu on run start (headless layout check).
        if (Environment.GetEnvironmentVariable("DM_CRAFT") is { Length: > 0 })
            _craftingMenu.Show();
        _invUi.Reset();
        _screen = GameScreen.Playing;
        _orbiting = false;
        _ascending = false;
        _shipParked = false;
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
            _toast = "ROVER AWAY - A/D STEER, S DIVE";
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
        // DM_DEEP=1 teleports straight to ore depth (~45% radius) and carves a small pocket
        // so tooling can screenshot underground features (scanner arrows, veins, gems)
        // without riding the rover down and mining a shaft.
        if (Environment.GetEnvironmentVariable("DM_DEEP") is { Length: > 0 } && !_landing)
        {
            var up = _run.Planet.UpAt(_run.Player.Position);
            var ang = MathF.Atan2(_run.Player.Position.Y - _run.Planet.Center.Y,
                                  _run.Player.Position.X - _run.Planet.Center.X);
            var deep = _run.Planet.Center
                + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * _run.Planet.Radius * 0.45f * Planet.TileSize;
            var (dr, dtc) = _run.Planet.WorldToTile(deep);
            for (var rr = dr - 4; rr <= dr + 4; rr++)
                for (var tt = dtc - 4; tt <= dtc + 4; tt++)
                    if (rr >= 0 && rr < _run.Planet.Rings)
                    {
                        var n = _run.Planet.TilesAt(rr);
                        var t = ((tt % n) + n) % n;
                        if (!Tiles.IsAnchored(_run.Planet.Get(rr, t)))
                            _run.Planet.Set(rr, t, TileKind.Sky);
                    }
            _run.Player.Position = deep;
        }

        if (_camera is not null)
        {
            _camera.Zoom = _landing ? 1.5f : _playZoom;
            _camera.SnapTo(_run.Player.Position, 0f);
        }
    }

    /// <summary>Open the Starspawn's nest: a spherical cavern around the buried egg (plus a
    /// little headroom above it), so the deep boss sits in a chamber the player mines into
    /// rather than a sprite entombed in solid rock. Anchored tiles (the core itself) stay.</summary>
    private void CarveTitanNest()
    {
        var planet = _run.Planet;
        var egg = _run.Titan.Position;
        var up = planet.UpAt(egg);
        const float radius = 64f;
        var centre = egg + up * 18f;   // bias the dome upward so the egg rests on the floor
        var (er, _) = planet.WorldToTile(centre);
        var span = (int)(radius / Planet.TileSize) + 2;
        var rel = centre - planet.Center;
        var ang = MathF.Atan2(rel.Y, rel.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        for (var dr = -span; dr <= span; dr++)
        {
            var r = er + dr;
            if (r < 1 || r >= planet.Rings) continue;
            var n = planet.TilesAt(r);
            var t0 = (int)(ang / MathHelper.TwoPi * n);
            for (var dt2 = -span * 3; dt2 <= span * 3; dt2++)
            {
                var t = ((t0 + dt2) % n + n) % n;
                var pos = planet.TileToWorld(r, t);
                if ((pos - centre).Length() > radius) continue;
                var k = planet.Get(r, t);
                if (k == TileKind.Sky || Tiles.IsAnchored(k)) continue;
                planet.Set(r, t, TileKind.Sky);
                _run.Physics.MarkDirty(r, t);
            }
        }
    }

    /// <summary>First active-chain planet matching the probe (DM_AUTOSTART biome aliases).</summary>
    private static PlanetDef FirstDef(Func<PlanetDef, bool> probe)
    {
        foreach (var d in PlanetDefs.All) if (probe(d)) return d;
        return PlanetDefs.All[0];
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
        _run.MothershipAngle += lat * (680f / orbitRadius) * dt;

        var station = _run.StationPos;
        _run.Player.Position = station;
        _run.Player.Velocity = Vector2.Zero;
        var up = _run.Planet.UpAt(station);
        // Frame both the station and the ground below it — the whole point of shifting the
        // orbit is picking a drop site you can see. (Surface baseline sits ~1270 px under
        // the raised parking orbit, so this is a genuinely wide shot.)
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, 0.44f, MathHelper.Clamp(dt * 2f, 0f, 1f));
        _camera.Follow(station - up * 630f, up, dt);

        var tPerf = FramePerf.Now();
        _run.Physics.Update(dt);
        FramePerf.Add("phys", tPerf);
        // From the parking orbit individual cells are sub-pixel — tick the material sim at
        // half rate (double dt every other frame keeps flow speeds right). On lava/ocean
        // worlds this is several ms a frame, exactly what pushed the orbit over the vsync
        // budget while the player picks a drop site. The station is also the far-field
        // throttle's focus, so the planet's own churn runs at quarter rate besides.
        _run.Cells.SimFocus = station;
        tPerf = FramePerf.Now();
        if ((_orbitCellTick = !_orbitCellTick))
            _run.Cells.Update(dt * 2f);
        FramePerf.Add("cells", tPerf);
        _particles.Update(dt, _run.Planet, _run.Cells);
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
            _toast = "ROVER AWAY - A/D STEER, S DIVE";
        }
        _toastTimer = 3.5f;
    }

    /// <summary>One frame of the rover descent: constant sink along local gravity (S/Down
    /// holds a fast dive), direct lateral drive from A/D (a guided pod, not a brick), world
    /// simulating underneath and the camera easing from orbit scale down to play zoom.
    /// Touchdown hands control to normal play exactly where the pod settled.</summary>
    private void UpdateLanding(float dt, KeyboardState keys)
    {
        var up = _run.Planet.UpAt(_landerPos);
        var right = new Vector2(-up.Y, up.X);
        var lat = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        var dive = keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down);
        _landerPos += (-up * (dive ? 440f : 185f) + right * (lat * 205f)) * dt;

        // Retro-thruster flame under the pod, throttled by the particle system itself.
        _particles.EmitRocketExhaust(_landerPos - up * 4f, -up);

        _run.Player.Position = _landerPos;
        _run.Player.Velocity = Vector2.Zero;
        // Altitude-driven zoom: stay wide while high (see the terrain you're steering at),
        // then close to play zoom only for the final stretch. Clearance is probed against
        // the actual ground under the pod, not the baseline surface ring — touching down on
        // a mountain top must still arrive at full play zoom, not orbit scale.
        var clearance = 900f;
        for (var probe = 12f; probe < 900f; probe += 12f)
            if (_run.Planet.IsSolidAt(_landerPos - up * probe)) { clearance = probe; break; }
        var zoomTarget = MathHelper.Lerp(_playZoom, 0.72f, MathHelper.Clamp(clearance / 650f, 0f, 1f));
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, zoomTarget, MathHelper.Clamp(dt * 2.4f, 0f, 1f));
        _camera.Follow(_landerPos, up, dt);
        // The station keeps drifting while the pod falls.
        _run.MothershipAngle += Session.StationDriftRate * dt;

        var tPerf = FramePerf.Now();
        _run.Physics.Update(dt);
        FramePerf.Add("phys", tPerf);
        // The falling pod is the far-field throttle's focus during the descent.
        _run.Cells.SimFocus = _landerPos;
        tPerf = FramePerf.Now();
        _run.Cells.Update(dt);
        FramePerf.Add("cells", tPerf);
        _particles.Update(dt, _run.Planet, _run.Cells);
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
        // The terrain-probed zoom above has all but converged by now; close the last sliver
        // so play never starts wide (a fast dive onto a peak can outrun the ease).
        _camera.Zoom = _playZoom;
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
        // Rigid debris system (not serialized — bodies were stamped into the grid at save).
        run.Rigid = new RigidBodies(run.Planet, run.Cells, run.Physics);
        run.Physics.DetachToRigid = run.Rigid.TryDetach;
        // Foundry gear isn't in the run save — it's meta state, re-applied on every entry.
        // (Drill Rig and the Armory/Supply kits aren't: the saved tier/inventory carry them.)
        run.Player.HasJetpack = Upgrades.Owned(_meta, "jetpack");
        run.Player.JetTier2 = Upgrades.Owned(_meta, "jetpack2");
        run.Player.JetTier3 = Upgrades.Owned(_meta, "jetpack3");
        run.Player.JetTier4 = Upgrades.Owned(_meta, "jetpack4");
        // Worn-pack grant, idempotent against the reloaded save: the saved inventory and
        // Back slot usually already carry it — only a fresh foundry purchase adds it here.
        if (run.Player.HasJetpack)
        {
            if (run.Player.Inventory.Count("jetpack") == 0) run.Player.Inventory.Add("jetpack", 1);
            run.Player.Equipment.AutoEquip("jetpack");
        }
        run.Player.HasO2Recycler = Upgrades.Owned(_meta, "o2");
        run.Player.O2Tier2 = Upgrades.Owned(_meta, "o22");
        run.Player.HasPlating = Upgrades.Owned(_meta, "plating");
        run.Player.HasFins = Upgrades.Owned(_meta, "fins");
        run.Player.LungTier = Upgrades.Owned(_meta, "lungs2") ? 2 : Upgrades.Owned(_meta, "lungs") ? 1 : 0;
        run.Player.HasGills = Upgrades.Owned(_meta, "gills");
        run.Player.HasHelmet = Upgrades.Owned(_meta, "vacsuit");
        if (Upgrades.Owned(_meta, "vitality")) run.Player.MaxHealth = 140f;
        // Gravity isn't in the run save — it's def-derived, like the foundry gear above.
        run.Player.Gravity = 320f * run.Def.GravityScale;
        // Loading woke every cell; burn the resettle here like world gen's pre-settle pass.
        for (var i = 0; i < 45; i++) _run.Cells.Update(1f / 60f);
        _lightGrid.PrewarmSky(_run.Planet);
        // Trees live in the saved tile grid, but their ecosystem sites aren't serialized — walk
        // the grid for TreeRoot columns and rebuild the sites so regrowth and rain-watering
        // keep working on a resumed world.
        TreeEcology.RebuildSites(_run);
        // Creatures aren't saved — re-seed the planet-wide resident census (cities staffed,
        // warrens garrisoned, lakes stocked) so a resumed world isn't a ghost town.
        SpawnDirector.PopulateWorld(_run);
        _gameOverReason = "";
        _craftingMenu.Reset();
        _charScreen.Reset();
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
        // PreserveContents: the lighting/bloom passes bind their own RTs mid-frame and the
        // composited scene must survive the swap (same reason as the backbuffer setting in
        // the constructor, which the scene target now stands in for).
        _sceneRt = new RenderTarget2D(GraphicsDevice, VirtualWidth, VirtualHeight, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        _renderer.SceneTarget = _sceneRt;
        _renderer.Grid = _lightGrid;
        // Pre-warm the liquid pass: create both coverage targets at their descent-time size
        // and push one composite through the metaball shader so the driver compiles its
        // GLSL now. Untouched, all of that happened lazily on the FIRST frame the descent
        // camera reached close-up LOD — an ~80 ms hitch right at the "ground rushing up"
        // moment of every landing.
        _liquidRt = new RenderTarget2D(GraphicsDevice, VirtualWidth, VirtualHeight, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        _flameRt = new RenderTarget2D(GraphicsDevice, VirtualWidth, VirtualHeight, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        GraphicsDevice.SetRenderTarget(_liquidRt);
        GraphicsDevice.Clear(Color.Transparent);
        // One blob through the fill blend so the driver also builds that pipeline state.
        _renderer.Batch.Begin(SpriteSortMode.Deferred, Renderer.LiquidFillBlend, SamplerState.LinearClamp);
        _renderer.Batch.Draw(_renderer.LiquidBlob, Vector2.Zero, Color.White);
        _renderer.Batch.End();
        GraphicsDevice.SetRenderTarget(_sceneRt);
        _renderer.CompositeLiquids(_liquidRt);
        GraphicsDevice.SetRenderTarget(null);
        _sfx.Build();
        Icons.Build(GraphicsDevice);
        // Slot icons for the melee arsenal escalate with the live run's upgrade rungs.
        Icons.MeleeTierOf = id => _run?.Player?.MeleeTiers.GetValueOrDefault(id, 1) ?? 1;
        // Character-screen item context menu: upgrade probe + actions wired to the same
        // crafting/apply/drop paths the rest of the game uses.
        _charScreen.UpgradeInfo = id =>
        {
            if (UpgradeRecipeFor(id) is not { } recipe)
                return HasUpgradePath(id) ? ("UPGRADE: MAXED", false) : ((string, bool)?)null;
            var cost = string.Join(" ", System.Linq.Enumerable.Select(recipe.Cost,
                kv => $"{kv.Value}x{Tiles.ResourceLabel(kv.Key)}"));
            return ($"UPGRADE: {cost}", Crafting.CanAfford(recipe, _run.Player.Inventory));
        };
        _charScreen.DoUpgrade = id =>
        {
            if (UpgradeRecipeFor(id) is { } recipe) ApplyCraft(recipe);
        };
        _charScreen.DropAction = DropItem;
        BuildTitleBackdrop(GraphicsDevice);
        _camera = new Camera
        {
            ViewportSize = new Point(VirtualWidth, VirtualHeight),
            // DM_ZOOM overrides the default zoom for testing (e.g. zoom out to frame a boss).
            Zoom = float.TryParse(Environment.GetEnvironmentVariable("DM_ZOOM"), out var z) ? z : 5.6f,
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

    /// <summary>Fixed-step catch-up diagnostics: updates MonoGame ran since the last drawn
    /// frame, and whether the loop reported falling behind. 2+ updates per draw sustained =
    /// the catch-up spiral — FPS collapses while the per-call CPU timers still look cheap.</summary>
    private int _updatesSinceDraw, _updatesPerDrawMax;
    private bool _runningSlowly;

    protected override void Update(GameTime gameTime)
    {
        _updSw.Restart();
        UpdateFrame(gameTime);
        _updSw.Stop();
        _updateMs = _updateMs * 0.9f + (float)_updSw.Elapsed.TotalMilliseconds * 0.1f;
        _updatesSinceDraw++;
        if (gameTime.IsRunningSlowly) _runningSlowly = true;
    }

    private void UpdateFrame(GameTime gameTime)
    {
        // Hitch clamp: under variable timestep a stalled frame must not hand the sim a
        // huge dt (tunnelling, oversized integration steps). 30 Hz floor = worst case the
        // world runs briefly in slow motion, exactly like the fixed-step catch-up cap.
        var dt = MathF.Min((float)gameTime.ElapsedGameTime.TotalSeconds, 1f / 30f);
        _frameDt = dt;   // for per-frame item actions dispatched by id (see BuildItems)
        var keys = Keyboard.GetState();
        var mouse = Screen.Mouse();
        _totalTime += dt;
        _transitionFlash = MathF.Max(0f, _transitionFlash - dt * 1.6f);

        // The OS pointer only shows where there is something to point AT — menus, pause,
        // and overlay screens. In raw play the drawn reticle is the cursor.
        IsMouseVisible = _pauseOpen
            || _screen is not GameScreen.Playing
            || _craftingMenu.Open || _debugMenu.Open || _loadoutOpen || _charScreen.Open;

        // Esc never quits the game directly any more: open menus consume their own Esc
        // edge to close (crafting, character screen, loadout, foundry, survey, debug);
        // with nothing open, Esc toggles the PAUSE menu — quitting lives there.
        var pauseToggled = false;
        if (Pressed(keys, _prevKeys, Keys.Escape)
            && _screen is GameScreen.Playing or GameScreen.Space or GameScreen.GameOver
            && !(_screen == GameScreen.Playing
                 && (_craftingMenu.Open || _debugMenu.Open || _loadoutOpen || _charScreen.Open))
            && !(_screen == GameScreen.Space && (_upgradesOpen || _surveyOpen || _debugMenu.Open)))
        {
            _pauseOpen = !_pauseOpen;
            _pauseCursor = 0;
            pauseToggled = true;
        }
        if (_pauseOpen)
        {
            // True pause: nothing below runs — the whole sim freezes under the overlay.
            if (!pauseToggled) UpdatePauseMenu(keys, mouse);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // F12 → defer one-shot screenshot until end of next Draw, where the backbuffer
        // holds the fully composited frame (post lighting/bloom/vignette).
        if (Pressed(keys, _prevKeys, Keys.F12)) _screenshotPending = true;

        // F11 toggles borderless fullscreen on any screen; the choice persists.
        if (Pressed(keys, _prevKeys, Keys.F11))
        {
            _meta.Fullscreen = !_graphics.IsFullScreen;
            SetFullscreen(_meta.Fullscreen);
            _meta.Save();
            _toast = _meta.Fullscreen ? "FULLSCREEN (F11)" : "WINDOWED (F11)";
            _toastTimer = 2f;
        }

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

        if (_screen == GameScreen.Title)
        {
            UpdateTitle(keys, mouse);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (_screen == GameScreen.Loading)
        {
            // Hand over once the survey warm-up lands (with a floor so the screen never
            // strobes, and a ceiling so a pathological world can't hold the game hostage —
            // any leftover generation just runs in the background like it used to). Gates
            // are relative to when Loading was entered — a slot switch re-enters it long
            // after boot.
            var since = _totalTime - _loadingSince;
            if ((_warmTask is not { IsCompleted: false } && since > 0.4f) || since > 15f)
                _screen = GameScreen.Space;
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
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

        // Escape flight: the player flies the rocket freely around the planet and up to
        // the mothership.
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
            _craftingMenu.Update(keys, _prevKeys, mouse, _prevMouse, IsOwned,
                id => CanAffordId(id), ApplyCraft);
            _run.Physics.Update(dt);
            _particles.Update(dt, _run.Planet, _run.Cells);
            _run.Cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }
        // Character screen (I) — same interception contract as crafting: the world keeps
        // simulating while the player shuffles gear between backpack and doll.
        if (_charScreen.Open)
        {
            _charScreen.Update(keys, _prevKeys, mouse, _prevMouse, _run.Player);
            _run.Physics.Update(dt);
            _particles.Update(dt, _run.Planet, _run.Cells);
            _run.Cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.I)) { _charScreen.Show(); _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return; }
        if (Pressed(keys, _prevKeys, Keys.C)) { _craftingMenu.Show(); _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return; }

        // F9 — developer boss-spawn menu. While open it intercepts input (like crafting): the
        // world keeps ticking but player control is suspended until a boss is picked or it closes.
        if (_debugMenu.Open)
        {
            _debugMenu.Update(keys, _prevKeys, mouse, _prevMouse);
            _run.Physics.Update(dt);
            _particles.Update(dt, _run.Planet, _run.Cells);
            _run.Cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return;
        }
        if (Pressed(keys, _prevKeys, Keys.F9))
        {
            if (!_debugMenu.Open) _debugMenu.SetTabs(BuildDebugTabs());
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
        // Space is the one flight key, Noita-style: tap to jump, keep holding and the
        // jetpack lights (Player.Update gates the burn on hold time so a tap never sputters
        // the pack; an airborne press hovers immediately). W/Up no longer jump — they only
        // climb ladders and steer fly mode via verticalAxis below.
        var jumpHeld = keys.IsKeyDown(Keys.Space);

        // DM_JETTEST=<1-4>: tooling hook — grants+equips that jetpack tier and holds jump
        // forever, so headless runs can screenshot the hover physics and each tier's
        // exhaust colour without input access.
        if (Environment.GetEnvironmentVariable("DM_JETTEST") is { Length: > 0 } jetTest
            && int.TryParse(jetTest, out var jetTier))
        {
            var p = _run.Player;
            p.HasJetpack = true;
            p.JetTier2 = jetTier >= 2; p.JetTier3 = jetTier >= 3; p.JetTier4 = jetTier >= 4;
            if (p.Inventory.Count("jetpack") == 0) p.Inventory.Add("jetpack", 1);
            p.Equipment.AutoEquip("jetpack");
            // Hold jump continuously: the press edge hops off the ground and the sustained
            // hold lights the pack once past the tap window — so the headless hover test
            // both leaves the ground and burns the jet.
            jumpHeld = true;
        }

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
                // Everything is granted PERMANENTLY: ownership flags flip for real (so
                // nothing is swept when god mode ends), plus 9999 of every material.
                GrantGodmodeItems();
                GrantGodmodeMaterials();
                foreach (var w in GodWeaponIds) _run.Player.Toolbelt.AutoEquip(w);
                _toast = "GOD MODE - FLIGHT, EVERY ITEM, AND 9999 OF EVERY MATERIAL";
                _toastTimer = 3f;
            }
        }

        // Immersion probes feed the swim model and the breath meter: body-centre water for
        // the movement flip, a head-height sample for the drowning clock. Sampled here
        // because Player.Update deliberately has no Cells reference.
        {
            var p = _run.Player;
            var pUp = _run.Planet.UpAt(p.Position);
            p.InWater = _run.Cells.CountWaterNear(p.Position, p.Radius + 1.5f) >= 3;
            p.HeadInWater = p.InWater
                && _run.Cells.CountWaterNear(p.Position + pUp * (p.Radius + 1f), 2f) >= 2;
        }
        // Shield guard: raised (halving damage) while a shield is the selected belt item.
        _run.Player.GuardMul = _run.Player.Toolbelt.Current switch
        {
            "shield" => 0.55f,
            "tower_shield" => 0.40f,
            _ => 1f,
        };
        _meleeAnim = MathF.Max(0f, _meleeAnim - dt);

        _run.Player.Update(dt, _run.Planet, moveAxis, jumpHeld, verticalAxis);

        // Jetpack exhaust: a tier-coloured jet stream from the worn pack's nozzles — red
        // stub burner up through orange and yellow to the tier-IV blue jet. The origin
        // matches the drawn pack (trailing side of the body, at its base).
        if (_run.Player.IsJetting)
        {
            var jetUp = _run.Planet.UpAt(_run.Player.Position);
            var jetRight = new Vector2(-jetUp.Y, jetUp.X);
            // Nozzle sits at the pack's base (pack centre +0.8 up, half-height ~3): root
            // the flame at -2.0 so the teardrop visibly connects to the metal.
            _particles.EmitJetExhaust(
                _run.Player.Position - jetRight * _playerFacing * 2.6f - jetUp * 2.0f,
                -jetUp, _run.Player.JetTier);
        }
        TickSwing(dt);
        TickAir(dt);
        TickHazardContact(dt);

        // Zoom control: "-" steps the camera out, "+"/"=" steps it in (numpad +/- too).
        // Steps are locked to EVEN zoom factors (2/4/6/8) so the pixel-grid world target
        // (see DrawFrame) upscales by an exact integer — every world pixel stays an
        // identical crisp block. The chosen zoom sticks as the play zoom.
        var zoomOutKey = keys.IsKeyDown(Keys.OemMinus) || keys.IsKeyDown(Keys.Subtract);
        var zoomInKey = keys.IsKeyDown(Keys.OemPlus) || keys.IsKeyDown(Keys.Add);
        if (zoomOutKey && !(_prevKeys.IsKeyDown(Keys.OemMinus) || _prevKeys.IsKeyDown(Keys.Subtract)))
            _playZoom = MathF.Max(2f, _playZoom - 2f);
        if (zoomInKey && !(_prevKeys.IsKeyDown(Keys.OemPlus) || _prevKeys.IsKeyDown(Keys.Add)))
            _playZoom = MathF.Min(8f, _playZoom + 2f);
        // Kaiju-scale framing: fighting near the titan eases the camera out so the whole
        // monster fits the fight, then eases back in once the player breaks away. The blend
        // is smoothed so crossing the range boundary never pops the zoom. While the blend
        // is active the zoom is fractional, so the pixel-grid world target hands off to
        // the direct render path for the fight and re-engages when the camera settles
        // back onto its even step.
        {
            var tt = _run.Titan;
            var titanNear = tt.Hatched && tt.Health > 0 && tt.Targetable
                && (tt.Position - _run.Player.Position).Length() < 760f;
            _fightZoomBlend = MathHelper.Clamp(
                _fightZoomBlend + (titanNear ? dt * 1.6f : -dt * 1.1f), 0f, 1f);
            var fightZoom = MathF.Max(2f, _playZoom * 0.68f);
            _camera.Zoom = MathHelper.Lerp(_playZoom, fightZoom, _fightZoomBlend);
        }

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
        // The "0" key selects the tenth slot (index 9), sitting after 1-9 on the number row.
        if (Toolbelt.SlotCount > 9 && Pressed(keys, _prevKeys, Keys.D0))
            _run.Player.Toolbelt.Selected = 9;
        // Mouse-wheel cycles selection — handy for fast tool swaps without leaving the home row.
        if (mouse.ScrollWheelValue != _prevMouse.ScrollWheelValue)
        {
            var dir = mouse.ScrollWheelValue > _prevMouse.ScrollWheelValue ? -1 : 1;
            _run.Player.Toolbelt.Selected = (_run.Player.Toolbelt.Selected + dir + Toolbelt.SlotCount) % Toolbelt.SlotCount;
        }
        // Q/E cycle through *weapons only*, skipping tools and placeables — the fast way to
        // switch guns mid-fight now that the armoury outgrows the number row. Standing at
        // the finished rocket, E boards it instead.
        if (Pressed(keys, _prevKeys, Keys.Q)) CycleWeapon(-1);
        if (Pressed(keys, _prevKeys, Keys.E))
        {
            if (NearShip()) TryLaunchShip();
            else if (!TryOpenChest() && !TryToggleDoor(worldCursor)) CycleWeapon(+1);
        }

        // Drag-and-drop UI input runs on click edges and updates the carry state. If the click
        // landed on a UI element, we swallow it so the world doesn't also receive it as an LMB
        // dispatch this frame.
        var screenPos = new Vector2(mouse.X, mouse.Y);
        var lmbPressed = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton != ButtonState.Pressed;
        var rmbPressed = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton != ButtonState.Pressed;
        var clickConsumed = _invUi.HandleClick(screenPos, lmbPressed, rmbPressed, _run.Player, DropItem);

        // Held LMB activates the selected slot's action. UseSelectedSlot is the single place
        // that maps id → in-world action (mine / shoot / place / throw / heal / …).
        var shootCdBefore = _run.Player.ShootCooldown;
        var selectedId = _run.Player.Toolbelt.Current;
        var throwable = selectedId is not null && IsThrowable(selectedId) && !_invUi.Carrying;
        var isEnergyBall = selectedId == "nuke" && !_invUi.Carrying;
        if (isEnergyBall)
        {
            // The energy ball CHARGES: hold LMB to wind it up (a growing orb at the muzzle
            // and a rising hum), release to fire. Non-linear power — released early it's
            // weak, only a full charge hits max.
            if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed
                && CanThrowSelected("nuke") && _run.Player.ShootCooldown <= 0f)
            {
                _energyCharging = true;
                _energyCharge = MathF.Min(1f, _energyCharge + dt / EnergyChargeTime);
                _energyHumT -= dt;
                if (_energyHumT <= 0f)
                {
                    _energyHumT = 0.14f;
                    PlayAt("shoot_beam", _run.Player.Position, 0.25f,
                        pitch: -0.5f + _energyCharge * 1.1f, minGap: 0.05f);
                }
            }
            else if (_energyCharging)
            {
                _energyCharging = false;
                if (_energyCharge > 0.05f) UseSelectedSlot(worldCursor);  // FireEnergyBall reads _energyCharge
                _energyCharge = 0f;
            }
        }
        else if (throwable)
        {
            _energyCharging = false; _energyCharge = 0f;
            // Charge while held (only when a throw is actually available), throw on release.
            if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed)
            {
                if (CanThrowSelected(selectedId!))
                {
                    _throwCharging = true;
                    _throwCharge = MathF.Min(1f, _throwCharge + dt / ThrowChargeTime);
                }
            }
            else if (_throwCharging)
            {
                _throwCharging = false;
                UseSelectedSlot(worldCursor);   // Fire*/ThrowTorch read _throwCharge
                _throwCharge = 0f;
            }
        }
        else
        {
            _throwCharging = false;
            _throwCharge = 0f;
            _energyCharging = false;
            _energyCharge = 0f;
            if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed && !_invUi.Carrying)
                UseSelectedSlot(worldCursor);
        }
        // DM_THROWTEST=1: ramp the throw-charge gauge (no input needed) so tooling can
        // screenshot the reticle gauge. Loops so the fill animates.
        if (Environment.GetEnvironmentVariable("DM_THROWTEST") is { Length: > 0 })
            _throwCharge = (_throwCharge + dt * 0.35f) % 1f;
        // A weapon fired iff the shoot cooldown jumped up this frame — one sound per shot,
        // covering every gun/thrown weapon without touching each fire method. Each weapon
        // picks its own voice via ItemDef.ShotSound (falling back to the generic pew).
        if (_run.Player.ShootCooldown > shootCdBefore + 0.001f)
        {
            var shot = "shoot";
            var cur = _run.Player.Toolbelt.Current;
            if (cur is not null && _items.TryGetValue(cur, out var cdef) && cdef.ShotSound is { } snd)
                shot = snd;
            // The hoses (flamethrower / acid spewer) fire ~20×/s to keep a smooth stream — but a
            // per-shot sound at that rate rattles like a machine gun. Play their voice on a big
            // minGap instead so it reads as ONE sustained low roar; other guns keep the snappy
            // per-shot report.
            var isHose = cur is "flamethrower" or "acid_spewer";
            PlayAt(shot, _run.Player.Position, isHose ? 0.4f : 0.5f,
                pitch: isHose ? -0.2f : MathHelper.Clamp(0.4f - _run.Player.ShootCooldown, -0.3f, 0.4f),
                minGap: isHose ? 0.32f : 0.03f);
        }

        // DM_AUTOFIRE=<belt id>: tooling hook — equips that weapon and holds fire along the
        // local tangent every frame, so headless runs can screenshot firing effects
        // (flame cones, lightning arcs, muzzle strobes) without input access. Pair with
        // DM_GOD=1 so ownership/ammo gates don't block the shot.
        if (Environment.GetEnvironmentVariable("DM_AUTOFIRE") is { Length: > 0 } autoFire)
        {
            _run.Player.Toolbelt.AutoEquip(autoFire);
            for (var i = 0; i < Toolbelt.SlotCount; i++)
                if (_run.Player.Toolbelt.Slots[i] == autoFire) { _run.Player.Toolbelt.Selected = i; break; }
            var afUp = _run.Planet.UpAt(_run.Player.Position);
            var afRight = new Vector2(-afUp.Y, afUp.X);
            UseSelectedSlot(_run.Player.Position + afRight * 55f + afUp * 6f);
        }

        // T recalls to the last placed Beacon — kept as a key because recall is a unique
        // verb that doesn't fit the "use selected slot" pattern (the beacon slot already
        // does *placement*; recall is a different action on the same data).
        if (Pressed(keys, _prevKeys, Keys.T) && _run.Player.BeaconWorld is { } bp)
        {
            BeaconRecall(bp);
        }

        // Board the completed ship with L (or E, above) while standing at it.
        if (Pressed(keys, _prevKeys, Keys.L)) TryLaunchShip();

        // Storage depot: B banks raw mats, N withdraws the stash — when standing at the depot.
        if (NearDepot())
        {
            if (Pressed(keys, _prevKeys, Keys.B)) DepositToBank();
            if (Pressed(keys, _prevKeys, Keys.N)) WithdrawFromBank();
        }

        // Physics + particles + cells update.
        var tPerf = FramePerf.Now();
        _run.Physics.Update(dt);
        FramePerf.Add("phys", tPerf);
        // A slab shearing free gets its own crack — deeper than the dust-crumble boom.
        if (_run.Physics.RigidDetachesThisTick > 0)
            _sfx.Play("collapse", MathHelper.Clamp(_run.Physics.RigidDetachesThisTick / 120f, 0.3f, 0.9f),
                pitch: -0.45f, pan: 0f, minGap: 0.25f);
        tPerf = FramePerf.Now();
        UpdateRigidBodies(dt);
        FramePerf.Add("rigid", tPerf);
        tPerf = FramePerf.Now();
        _particles.Update(dt, _run.Planet, _run.Cells);
        FramePerf.Add("parts", tPerf);

        // Thrown torches: fly, stick, and stay. A soft thunk marks the plant.
        foreach (var torch in _run.Torches)
            if (torch.Update(dt, _run.Planet))
                PlayAt("dig", torch.Position, 0.3f, pitch: -0.25f, minGap: 0.08f);

        // Sweep dust within a body's reach into the inventory. Each cell carries a fractional
        // resource amount tied to its source TileKind; Cells handles the per-id accumulator and
        // hands back whole units once they cross 1. Done before the cells tick so the
        // collection's WakeNeighbors calls land in `_next`, which the upcoming Update swaps into
        // `_active` and processes immediately — dust above a collected cell falls the same frame
        // instead of one frame later.
        var picked = _run.Cells.CollectInRadius(_run.Player.Position,
            _run.Player.Radius + _run.Player.PickupReach);
        if (picked is not null)
        {
            foreach (var (id, count) in picked)
                _run.Player.Inventory.Add(id, count);
            _sfx.Play("pickup", 0.4f, 0.2f, 0f, minGap: 0.11f);
        }

        // Keep the compaction sweep away from the dwarf — never solidify the pile they're
        // standing in / vacuuming (headless contexts leave this null and compact freely).
        _run.Cells.CompactionExclusion = _run.Player.Position;
        _run.Cells.SimFocus = _run.Player.Position;
        tPerf = FramePerf.Now();
        _run.Cells.Update(dt);
        FramePerf.Add("cells", tPerf);
        if (_run.Physics.CollapsesThisTick > 0)
        {
            _run.Shake = MathF.Max(_run.Shake, MathHelper.Clamp(_run.Physics.CollapsesThisTick / 80f, 0f, 1.5f));
            _sfx.Play("collapse", MathHelper.Clamp(_run.Physics.CollapsesThisTick / 40f, 0.25f, 1f), 0f, 0f, minGap: 0.4f);
        }
        UpdateCaveInWarning(dt);

        // Eruption in progress (started by the disaster clock, or the debug menu): the vent
        // spawns fresh cells from its deep conduit just above the crater pool until it
        // overflows the rim and runs down the flanks.
        if (_run.EruptionLeft > 0f && _run.EruptionVent >= 0
            && _run.EruptionVent < _run.Planet.VolcanoVents.Count)
        {
            _run.EruptionLeft -= dt;
            var (vx, vy, vAcid) = _run.Planet.VolcanoVents[_run.EruptionVent];
            var mat = vAcid ? Material.Acid : Material.Lava;
            var ventPos = _run.Planet.TileToWorld(vx, vy);
            var ventUp = _run.Planet.UpAt(ventPos);
            var ventRight = new Vector2(-ventUp.Y, ventUp.X);

            // Magma bumbles up from the deep conduit — a fat surge that fills the crater and
            // overflows the rim to run down the flanks (far more than the old 12-cell trickle).
            for (var i = 0; i < 3; i++)
                _run.Cells.SpawnInTile(
                    Math.Clamp(vx + Random.Shared.Next(-1, 2), 1, _run.Planet.Rings - 1),
                    vy + Random.Shared.Next(-1, 2), mat, Cells.Density * Cells.Density);

            // ...and SPEWS: a fountain of molten gobs hurled up and out of the crater mouth,
            // arcing over and raining lava down the slopes.
            var gobs = 9 + Random.Shared.Next(7);
            for (var i = 0; i < gobs; i++)
            {
                var spread = (float)(Random.Shared.NextDouble() - 0.5) * 1.0f;
                var dir = ventUp * MathF.Cos(spread) + ventRight * MathF.Sin(spread);
                var speed = 150f + (float)Random.Shared.NextDouble() * 60f;
                _run.Cells.LaunchAtWorld(ventPos + ventUp * 6f, dir * speed, mat);
            }

            // Ash plume, glowing cinders spat from the fountain, and a rolling rumble.
            if (Random.Shared.Next(2) == 0)
                _run.Cells.SpawnInTile(Math.Min(vx + 4, _run.Planet.Rings - 1), vy, Material.Smoke, 8);
            _particles.EmitCinders(ventPos + ventUp * 10f, ventUp * 70f, 5, 110f);
            _run.Shake = MathF.Max(_run.Shake, 0.4f);
            PlayAt("collapse", ventPos, 0.4f, pitch: -0.5f, minGap: 0.5f);
        }

        // Population upkeep — cave dwellers, surface herds, sky flyers (see SpawnDirector).
        tPerf = FramePerf.Now();
        SpawnDirector.Update(dt, _run);

        // Ambient events — meteors on their own cadence, disasters off the shared clock
        // (see AmbientDirector).
        ApplyAmbient(AmbientDirector.Update(dt, _run, _particles));
        // Living ecosystem — drifting clouds shed biome rain, and felled trees regrow from
        // their roots the more that rain (or a nearby pool) waters them.
        Weather.Update(dt, _run, _particles);
        TreeEcology.Update(dt, _run);
        FramePerf.Add("dir", tPerf);
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
                _run.Rigid?.NoteBlast(m.Position, 90f, 200f);
                KickCorpses(m.Position, 90f, 200f);
                _run.Meteors.RemoveAt(i);
            }
        }

        // City disaster response: while any disaster is live, the citizens and their militia
        // take cover in the towers (set per-creature below). The buildings shrug the disaster
        // off (anchored, non-meltable, non-corrodible tiles) and the saucers keep patrolling —
        // only the on-foot aliens run for shelter.
        var cityCover = _run.Def.Biome == "city" && AmbientDirector.DisasterActive(_run);
        // The city's grudge cools slowly on its own; a wrathful city keeps its civilians
        // indoors the same way a disaster does.
        _run.CityWrath = MathF.Max(0f, _run.CityWrath - 1.2f * dt);
        var cityWrathful = _run.CityWrath >= 50f;

        // Update entities. Creatures that have drifted far outside the player's neighbourhood
        // are recycled — they'd never be met again, they eat sim time, and every recycled
        // body frees local-population budget so the spawner keeps the area around the player
        // stocked as they travel.
        tPerf = FramePerf.Now();
        for (var i = _run.Creatures.Count - 1; i >= 0; i--)
        {
            var c = _run.Creatures[i];
            // Far residents FREEZE instead of culling: the planet-wide census (city crowds,
            // warren garrisons, lake fauna) exists before the player arrives anywhere, but
            // it costs nothing until they walk into range and it wakes.
            if (c.Resident
                && (c.Position - _run.Player.Position).LengthSquared() > 900f * 900f)
                continue;
            c.TakeCover = (cityCover || cityWrathful)
                && c.Kind is CreatureKind.Civilian or CreatureKind.Peacekeeper;
            c.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player, _run.TitanShots);
            if (c.Health <= 0)
            {
                // Killed — leave a harvestable corpse where it fell. Distance culls (below)
                // don't: those creatures just wandered out of the simulation bubble.
                // (Bomber beetles leave a crater instead of a corpse.)
                if (c.Kind != CreatureKind.BomberBeetle)
                {
                    // The corpse inherits the death momentum (combat knockback is already in
                    // the creature's velocity, capped so a blast kill doesn't hurl the body
                    // out of the county) and tumbles from it — a shot grazer keels over and
                    // rolls instead of freezing in place. It carries a dead clone of the
                    // creature as its display body, so what hits the ground IS the animal.
                    var cUp = _run.Planet.UpAt(c.Position);
                    var deathVel = c.Velocity.Length() > 240f
                        ? Vector2.Normalize(c.Velocity) * 240f : c.Velocity;
                    _run.Corpses.Add(new Corpse(c.Position, c.Kind, c.Radius)
                    {
                        Velocity = deathVel,
                        Angle = MathF.Atan2(cUp.X, -cUp.Y),
                        Spin = ((float)Random.Shared.NextDouble() - 0.5f) * 4f
                            * MathHelper.Clamp(c.Velocity.Length() / 100f, 0.3f, 1.5f),
                        Body = new Creature(c.Position, c.Kind),
                    });
                }
                _particles.EmitDust(c.Position, 5f);
                // Killing ANY alien turns the whole city on you at once — a single dead
                // civilian, guard, saucer, or lizardman crosses the wrath threshold (unless
                // the titan is rampaging right there and takes the blame).
                if (c.Kind is CreatureKind.Civilian or CreatureKind.Peacekeeper or CreatureKind.Saucer
                        or CreatureKind.Lizardman or CreatureKind.BigSaucer
                    && (!_run.Titan.Hatched || _run.Titan.Health <= 0
                        || (_run.Titan.Position - c.Position).LengthSquared() > 380f * 380f))
                    AddCityWrath(60f);
                switch (c.Kind)
                {
                    // Spore bats burst into a choking puff — kill them at arm's length.
                    case CreatureKind.SporeBat:
                    {
                        var (sx, sy) = _run.Planet.WorldToTile(c.Position);
                        _run.Cells.SpawnInTile(sx, sy, Material.Gas, Cells.Density * 2);
                        break;
                    }
                    // Magma slugs are living coals — the hide bursts into live lava where
                    // they die, so meleeing one down is a commitment.
                    case CreatureKind.MagmaSlug:
                    {
                        var (lx, ly) = _run.Planet.WorldToTile(c.Position);
                        _run.Cells.SpawnInTile(lx, ly, Material.Lava, Cells.Density);
                        break;
                    }
                    // Bomber beetles detonate on any death — fuse-out or gunned down. The
                    // instant-fuse dynamite carves the crater and applies creature AoE via
                    // the normal projectile path (so bombers chain each other); the player
                    // blast damage is applied here with the same linear falloff.
                    case CreatureKind.BomberBeetle:
                    {
                        _run.Projectiles.Add(new Projectile(c.Position, Vector2.Zero, 16f, 0.02f,
                            ProjectileKind.Dynamite));
                        var pd = (_run.Player.Position - c.Position).Length();
                        if (pd < 50f) _run.Player.TakeDamage(24f * (1f - 0.6f * pd / 50f));
                        break;
                    }
                    // Terraria rules: a dead slime is two smaller ones. Slimelets pop off
                    // sideways so the split reads, and they don't split further.
                    case CreatureKind.CaveSlime:
                    {
                        var slUp = _run.Planet.UpAt(c.Position);
                        var slRight = new Vector2(-slUp.Y, slUp.X);
                        for (var s = -1; s <= 1; s += 2)
                        {
                            var lite = new Creature(c.Position + slRight * (s * 3f), CreatureKind.Slimelet)
                            {
                                Velocity = slRight * (s * 55f) + slUp * 90f,
                            };
                            _run.Creatures.Add(lite);
                        }
                        break;
                    }
                }
                _run.Creatures.RemoveAt(i);
            }
            // Residents are exempt from the distance cull — they're the standing population
            // of a place (a city, a warren, a lake), not bubble wildlife. They froze above.
            else if (!c.Resident
                && (c.Position - _run.Player.Position).LengthSquared() > 1000f * 1000f)
            {
                _run.Creatures.RemoveAt(i);
            }
        }

        // City militia: each peacekeeper picks the nearest hostile invader in guard range —
        // or the rampaging titan when the streets are otherwise clear — and peppers it with
        // low-damage civic bolts. Targeting and firing live here because Game1 owns both the
        // creature and projectile lists; the creature tick just walks to engage. The bolts
        // are friendly-to-neutrals (Combat skips civilians, and a bolt-stung titan doesn't
        // re-aggro onto the player), so the city defends itself without ever drafting the
        // dwarf into its crossfire.
        foreach (var pk in _run.Creatures)
        {
            if (pk.Kind is not (CreatureKind.Peacekeeper or CreatureKind.Saucer or CreatureKind.BigSaucer)
                || pk.Health <= 0)
                continue;
            var big = pk.Kind == CreatureKind.BigSaucer;
            // Saucers watch a wider circle from altitude and fire from further out; the
            // command ship sees and reaches the furthest of all.
            var scanR = big ? 420f : pk.Kind == CreatureKind.Saucer ? 320f : 240f;
            var fireR = big ? 380f : pk.Kind == CreatureKind.Saucer ? 280f : 230f;
            pk.GuardFireCd -= dt;
            Vector2? threat = null;
            var threatIsPlayer = false;
            var bestSq = scanR * scanR;
            foreach (var other in _run.Creatures)
            {
                if (!other.Hostile || other.Health <= 0) continue;
                var dSq = (other.Position - pk.Position).LengthSquared();
                if (dSq < bestSq) { bestSq = dSq; threat = other.Position; }
            }
            // The city remembers: enough dead residents or wrecked floors and the dwarf
            // IS the invader — militia and saucers add the player to the threat scan.
            if (cityWrathful && _run.Player.Health > 0)
            {
                var pSq = (_run.Player.Position - pk.Position).LengthSquared();
                if (pSq < bestSq) { bestSq = pSq; threat = _run.Player.Position; threatIsPlayer = true; }
            }
            if (threat is null && _run.Titan.Hatched && _run.Titan.Health > 0
                && (_run.Titan.Position - pk.Position).LengthSquared() < 380f * 380f)
                threat = _run.Titan.Position;
            pk.GuardTarget = threat;
            // Command-ship tractor beam: while it holds the PLAYER as its target within
            // reach, it drags them toward it and heavily slows them — a constant pull, no
            // cooldown. (Ordinary saucers don't have one.)
            if (big && threatIsPlayer && threat is { } beamAt
                && (beamAt - pk.Position).Length() < fireR)
            {
                var toShip = pk.Position - _run.Player.Position;
                var bd = toShip.Length();
                if (bd > 1f)
                {
                    _run.Player.Velocity += toShip / bd * 120f * dt;    // reel in
                    _run.Player.Velocity *= 1f - MathF.Min(0.85f, 3.0f * dt); // and bog down
                }
            }
            if (threat is { } tp && pk.GuardFireCd <= 0f)
            {
                var diff = tp - pk.Position;
                var d = diff.Length();
                if (d > 10f && d < fireR)
                {
                    var dir = diff / d;
                    // The command ship fires a FAN of lasers; peacekeepers/saucers a single
                    // round. Player-seeking shots ride the self-contained titan-shot path
                    // (civic bolts are hard-coded friendly to the dwarf).
                    var shots = big ? 3 : 1;
                    for (var s = 0; s < shots; s++)
                    {
                        var spread = big ? (s - 1) * 0.16f : 0f;
                        var sd = new Vector2(
                            dir.X * MathF.Cos(spread) - dir.Y * MathF.Sin(spread),
                            dir.X * MathF.Sin(spread) + dir.Y * MathF.Cos(spread));
                        if (threatIsPlayer)
                            _run.TitanShots.Add(new TitanProjectile(
                                pk.Position + sd * (pk.Radius + 3f), sd * (big ? 320f : 270f),
                                big ? TitanShotKind.Laser : TitanShotKind.Slug,
                                damage: big ? 14f : 7f));
                        else
                            _run.Projectiles.Add(new Projectile(
                                pk.Position + sd * (pk.Radius + 3f), sd * 260f, 3f, 1.1f,
                                ProjectileKind.CivicBolt));
                    }
                    pk.GuardFireCd = big ? 0.9f + (float)Random.Shared.NextDouble() * 0.4f
                                         : 1.1f + (float)Random.Shared.NextDouble() * 0.5f;
                    pk.GuardMuzzleFlash();
                    PlayAt("shoot", pk.Position, big ? 0.6f : 0.35f, pitch: big ? -0.2f : 0.45f, minGap: 0.1f);
                }
            }
        }

        // Lizardman war-cry: the first guard to sight prey shrieks, and every lizardman in
        // a wide radius picks up the hunt — aggro one and the warren answers together.
        foreach (var lz in _run.Creatures)
        {
            if (lz.Kind != CreatureKind.Lizardman || !lz.CallingBackup) continue;
            lz.CallingBackup = false;
            foreach (var ally in _run.Creatures)
                if (!ReferenceEquals(ally, lz) && ally.Kind == CreatureKind.Lizardman
                    && ally.Health > 0
                    && (ally.Position - lz.Position).LengthSquared() < 800f * 800f)
                    ally.RallyToWar();
            // A guttural down-pitched cry so the pile-on is telegraphed, not a mugging.
            PlayAt("hurt", lz.Position, 0.7f, pitch: -0.4f, minGap: 0.25f);
        }

        // Corpses — settle under gravity, decay on a timer, and are harvested for materials
        // by walking over them (same sweep-up feel as dust collection).
        // Corpse harvest is a CHANNEL now, not a walk-over: stand at the body and carve for
        // HarvestTime seconds (bigger animals take longer), the corpse disintegrating as
        // the work runs. Progress holds if the dwarf steps away mid-carve.
        _harvestFxPos = null;
        for (var i = _run.Corpses.Count - 1; i >= 0; i--)
        {
            var corpse = _run.Corpses[i];
            corpse.Update(dt, _run.Planet);
            var reach = _run.Player.Radius + corpse.Radius + 4f;
            if (!corpse.Harvested && (corpse.Position - _run.Player.Position).LengthSquared() < reach * reach)
            {
                var beatBefore = (int)(corpse.HarvestProgress * 5f);
                corpse.HarvestProgress += dt;
                // Only one carving overlay even standing between two bodies.
                if (_harvestFxPos is null)
                {
                    _harvestFxPos = corpse.Position;
                    _harvestFxFrac = corpse.Dissolve;
                    _harvestFxRadius = corpse.Radius;
                }
                // Work beats: flecks of the carcass come away with a wet chop.
                if ((int)(corpse.HarvestProgress * 5f) != beatBefore)
                {
                    _particles.EmitDust(corpse.Position, 3f);
                    PlayAt("dig", corpse.Position, 0.28f, pitch: 0.45f, minGap: 0.1f);
                }
                if (corpse.HarvestProgress >= corpse.HarvestTime)
                {
                    foreach (var (id, count) in Corpse.DropsFor(corpse.Kind))
                        _run.Player.Inventory.Add(id, count);
                    corpse.Harvested = true;
                    _particles.EmitDust(corpse.Position, 8f);
                    _sfx.Play("pickup", 0.5f, 0.2f, 0f, minGap: 0.1f);
                }
            }
            if (corpse.Expired || (corpse.Position - _run.Player.Position).LengthSquared() > 1200f * 1200f)
                _run.Corpses.RemoveAt(i);
        }

        // Gem drops: drain the cell sim's shatter queue into physical pickups. Every shatter
        // site — embedded gem popping out of its host, or a gem tile breaking — pops its
        // whole drop right where it broke, so a mined crystal never silently vanishes.
        if (_run.Cells.PendingGemDrops.Count > 0)
        {
            foreach (var (gpos, gkind) in _run.Cells.PendingGemDrops)
            {
                if (Tiles.Drop(gkind) is not { } gd) continue;
                for (var n = 0; n < gd.count; n++)
                {
                    var gemUp = _run.Planet.UpAt(gpos);
                    var kick = gemUp * (55f + (float)Random.Shared.NextDouble() * 30f)
                             + new Vector2(-gemUp.Y, gemUp.X) * (((float)Random.Shared.NextDouble() - 0.5f) * 50f);
                    _run.Pickups.Add(new Pickup(gpos, gkind, kick));
                }
            }
            _run.Cells.PendingGemDrops.Clear();
        }

        // Bubble sites: quench boils and drowned fire gouts queue positions in the cell sim
        // (it has no particle access); each becomes a small train of rising bubbles.
        if (_run.Cells.PendingBubbles.Count > 0)
        {
            foreach (var bpos in _run.Cells.PendingBubbles)
            {
                var bup = _run.Planet.UpAt(bpos);
                for (var b = 0; b < 3; b++) _particles.EmitBubble(bpos, bup);
            }
            _run.Cells.PendingBubbles.Clear();
        }

        // Licking-flame sites: every burning cell the sim sampled this tick grows a
        // rising flame tongue — the standing fire the jet leaves reads as actual flame,
        // scaling with how long the jet dwelt there and spreading with the fire itself.
        if (_run.Cells.PendingFlames.Count > 0)
        {
            foreach (var (fpos, ffuse) in _run.Cells.PendingFlames)
                _particles.EmitLickingFlame(fpos, _run.Planet.UpAt(fpos), ffuse);
            _run.Cells.PendingFlames.Clear();
        }

        // Pickups — settle where they fell and collect by walk-over (no magnet: the player
        // goes to the gem). No decay and no distance cull: a dropped gem is exactly the
        // thing the player came down here for.
        for (var i = _run.Pickups.Count - 1; i >= 0; i--)
        {
            var g = _run.Pickups[i];
            g.Update(dt, _run.Planet, _run.Cells);
            var reach = _run.Player.Radius + 3.5f;
            if ((g.Position - _run.Player.Position).LengthSquared() < reach * reach)
            {
                if (Tiles.Drop(g.Kind) is { } gd) _run.Player.Inventory.Add(gd.id, gd.count);
                _particles.EmitDust(g.Position, 4f);
                PlayAt("ui", g.Position, 0.5f, pitch: 0.35f, minGap: 0.06f);
                _run.Pickups.RemoveAt(i);
            }
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
                    // Rigid debris: chunks already in flight get kicked, and any region this
                    // blast just carved free launches outward when it detaches a tick later.
                    _run.Rigid?.NoteBlast(p.Position, p.ExplosionRadius * 1.6f,
                        MathF.Min(260f, p.ExplosionRadius * 2.4f));
                    KickCorpses(p.Position, p.ExplosionRadius * 1.6f,
                        MathF.Min(260f, p.ExplosionRadius * 2.4f));
                    // Explosions do not care whose they are: the dwarf standing inside the
                    // blast eats real damage with the same center-weighted falloff creatures
                    // get. Own bombs are lethal at arm's length now — take cover or take the hit.
                    var pd = (_run.Player.Position - p.Position).Length();
                    var blastR = p.ExplosionRadius + _run.Player.Radius;
                    if (pd < blastR)
                    {
                        var falloff = Combat.BlastFalloff(pd / p.ExplosionRadius);
                        _run.Player.TakeDamage(p.Damage * falloff * 0.75f);
                        if (pd > 0.5f)
                            _run.Player.Velocity += (_run.Player.Position - p.Position) / pd
                                * MathF.Min(220f, p.ExplosionRadius * 2.4f) * falloff;
                    }
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
        FramePerf.Add("crit", tPerf);
        // Hatch feedback: a heavy shake + shell-burst the frame the egg cracks open.
        if (_run.Titan.JustHatched)
        {
            _run.Titan.JustHatched = false;
            _run.Shake = MathF.Max(_run.Shake, 1.4f);
            _particles.EmitDust(_run.Titan.Position, 30f);
            PlayAt("hatch", _run.Titan.Position, 1f);
        }
        // Melee shockwave from a Kong slam/fist-smash / Sandworm eruption — quake already fired
        // inside the Titan; here we knock back and hurt the player and any creature inside the
        // radius (a titan's fist doesn't care whether it lands on the dwarf or the militia).
        if (_run.Titan.PendingShockwave is { } sw)
        {
            _run.Titan.PendingShockwave = null;
            _run.Shake = MathF.Max(_run.Shake, 1.0f);
            _particles.EmitDust(sw.pos, 24f);
            PlayAt("explode", sw.pos, 1f, pitch: -0.3f);
            // A slam under a loose overhang sends the shards flying, not slumping.
            _run.Rigid?.NoteBlast(sw.pos, sw.radius, 170f);
            KickCorpses(sw.pos, sw.radius, 170f);
            var toPlayer = _run.Player.Position - sw.pos;
            var d = toPlayer.Length();
            if (d < sw.radius)
            {
                _run.Player.TakeDamage(sw.damage * (1f - d / sw.radius));
                if (d > 0.01f) _run.Player.Velocity += toPlayer / d * 260f;
            }
            foreach (var c in _run.Creatures)
            {
                var toC = c.Position - sw.pos;
                var dc = toC.Length();
                if (dc >= sw.radius + c.Radius) continue;
                c.Health -= sw.damage * (1f - MathHelper.Clamp(dc / sw.radius, 0f, 1f));
                c.HitFlash = 0.15f;
                if (dc > 0.01f) c.Velocity += toC / dc * 260f;
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
        // The Starspawn's gravity well: arm the pull window, then drag the dwarf toward the
        // maw every frame it runs — inside the radius the floor stops being safety, and the
        // only counter is thrust (jetpack / sprinting away) or breaking line of pull with
        // distance. Mirrors the pending hand-off pattern above.
        if (_run.Titan.PendingGravityWell is { } well)
        {
            _run.Titan.PendingGravityWell = null;
            _gravityWell = well.pos;
            _gravityWellTimer = well.seconds;
            _gravityWellRadius = well.radius;
            PlayAt("shoot_beam", well.pos, 1f, pitch: -0.6f);
            if ((_run.Player.Position - well.pos).Length() < well.radius)
            {
                _toast = "GRAVITY WELL - IT IS PULLING YOU IN";
                _toastTimer = 2f;
            }
        }
        if (_gravityWellTimer > 0f)
        {
            _gravityWellTimer -= dt;
            var toWell = _gravityWell - _run.Player.Position;
            var wd = toWell.Length();
            if (wd < _gravityWellRadius && wd > 1f)
            {
                // Stronger the closer you are — escape at the rim is possible, at the maw it isn't.
                var pull = MathHelper.Lerp(620f, 160f, wd / _gravityWellRadius);
                _run.Player.Velocity += toWell / wd * pull * dt;
                if (Random.Shared.NextDouble() < dt * 20f)
                    _particles.EmitDust(_run.Player.Position - toWell / wd * 8f, 2f);
            }
        }
        // Kaiju voice — attack windups queue a movie-monster bellow/screech; play it at the
        // body with a little screen weight so the roar lands physically too.
        if (_run.Titan.PendingRoar is { } roar)
        {
            _run.Titan.PendingRoar = null;
            var rpitch = _run.Titan.Kind switch
            {
                TitanKind.Slattern or TitanKind.Leatherback => -0.25f,
                TitanKind.Raiju => 0.2f,
                _ => 0f,
            };
            PlayAt(roar, _run.Titan.Position, 1f, pitch: rpitch, minGap: 0.7f);
            _run.Shake = MathF.Max(_run.Shake, 0.3f);
        }
        // A kick or fist meeting toppled debris grinds it straight to dust and punches any
        // corpses away; the thud is the impact's bass note under the shockwave's boom.
        if (_run.Titan.PendingPulverize is { } pulv)
        {
            _run.Titan.PendingPulverize = null;
            _run.Rigid?.Pulverize(pulv.pos, pulv.radius);
            KickCorpses(pulv.pos, pulv.radius, 190f);
            _particles.EmitDust(pulv.pos, 16f);
            PlayAt("thud", pulv.pos, 1f, minGap: 0.15f);
        }
        // Shake-off: the mid-thrash fling throws a clinging dwarf — riding the hide or
        // grapple-latched to it — clear of the monster.
        if (_run.Titan.PendingShakeOff)
        {
            _run.Titan.PendingShakeOff = false;
            if (_riding || _grapOnTitan)
            {
                var tUp = _run.Planet.UpAt(_run.Titan.Position);
                var tRight = new Vector2(-tUp.Y, tUp.X);
                var fling = Random.Shared.Next(2) == 0 ? -1f : 1f;
                _riding = false;
                ReleaseGrapple();
                _run.Player.Velocity = tUp * 240f + tRight * (fling * 300f);
                _run.Player.TakeDamage(6f);
                _toast = "SHAKEN OFF!";
                _toastTimer = 2f;
            }
        }
        TickTitanRiding(dt, keys);
        TickGrapple(dt, keys, mouse);
        // Slaying the titan no longer ends the visit — the rocket is the only way off-world.
        // Nor does it bank the soul any more: the kill drops a CARCASS where the kaiju fell,
        // and the soul is claimed by carving it for 7 seconds (see the harvest block below).
        // The crossing fires exactly once; resumed saves of an already-dead titan can't
        // re-trigger because both sides of the crossing are non-positive after load.
        if (_run.Titan.Health <= 0 && _prevTitanHealth > 0)
        {
            _run.TitanCarcass = new TitanCorpse(_run.Titan.Position, _run.Titan.Kind,
                _run.Titan.BodyRadius);
            _run.Shake = MathF.Max(_run.Shake, 1.6f);
            _particles.EmitDust(_run.Titan.Position, 44f);
            PlayAt("explode", _run.Titan.Position, 1f, pitch: -0.4f);
            _toast = $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} FELLED - HARVEST THE CARCASS FOR ITS SOUL";
            _toastTimer = 4f;
        }
        _prevTitanHealth = _run.Titan.Health;

        // Carcass harvest: stand at the fallen kaiju and carve. Seven seconds of work — the
        // body visibly disintegrating — banks the soul (foundry currency) plus a butcher's
        // bounty. Progress holds if the dwarf steps away; the carcass never decays.
        if (_run.TitanCarcass is { Claimed: false } carc)
        {
            carc.Update(dt, _run.Planet);
            var creach = _run.Player.Radius + carc.Radius * 0.7f + 8f;
            if ((carc.Position - _run.Player.Position).LengthSquared() < creach * creach)
            {
                var beatBefore = (int)(carc.Progress * 5f);
                carc.Progress += dt;
                _harvestFxPos = carc.Position;
                _harvestFxFrac = carc.Dissolve;
                _harvestFxRadius = carc.Radius * 0.6f;
                if ((int)(carc.Progress * 5f) != beatBefore)
                {
                    _particles.EmitDust(carc.Position + new Vector2(
                        (float)(Random.Shared.NextDouble() - 0.5) * carc.Radius,
                        (float)(Random.Shared.NextDouble() - 0.5) * carc.Radius * 0.4f), 4f);
                    PlayAt("dig", carc.Position, 0.35f, pitch: 0.3f, minGap: 0.12f);
                }
                if (carc.Progress >= TitanCorpse.HarvestTime)
                {
                    carc.Claimed = true;
                    _run.SoulClaimed = true;
                    _meta.TitansDefeated++;
                    // Soul keyed off the titan actually fought — planets with a TitanPool
                    // roll a different kaiju per visit, so the def's static kind can be wrong.
                    var kindKey = _run.Titan.Kind.ToString();
                    _meta.TitanSouls[kindKey] = _meta.TitanSouls.GetValueOrDefault(kindKey) + 1;
                    _meta.Save();
                    // The butcher's bounty: a kaiju is a lot of animal.
                    _run.Player.Inventory.Add("meat", 8);
                    _run.Player.Inventory.Add("hide", 4);
                    _particles.EmitDust(carc.Position, 30f);
                    _run.Shake = MathF.Max(_run.Shake, 0.8f);
                    PlayAt("pickup", carc.Position, 1f, pitch: -0.3f);
                    _toast = $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} SOUL CLAIMED  +8 MEAT +4 HIDE";
                    _toastTimer = 3.5f;
                    _run.TitanCarcass = null;
                }
            }
        }

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
                // Small shots — darts, slugs, bone spikes — land with a tiny puff, NOT the
                // cannon-blast burst the heavy titan ordnance (flame/acid/lava/void) throws.
                var impact = shot.Kind is TitanShotKind.Dart or TitanShotKind.Slug or TitanShotKind.Spike
                    ? ProjectileKind.Bullet : ProjectileKind.Cannon;
                _particles.EmitImpact(shot.Position, impact);
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
        // ring-band search is cheap, but not run-it-every-frame cheap). The ship-upgrade
        // scanner drives the fuel/signature edge arrows; the CRAFTED geo-scanner drives the
        // radiating material arrows (its own rung-gated deposit list).
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
        // Geo-scanner upkeep: cool the pulse down, advance the ring animation, and prune the
        // marks whose lifetime has run out (the scanner is activated now, not passive).
        if (_scanCooldown > 0f) _scanCooldown -= dt;
        _scanPulseT += dt;
        for (var i = _geoScanHits.Count - 1; i >= 0; i--)
            if (_run.RunTime >= _geoScanHits[i].expiry) _geoScanHits.RemoveAt(i);

        _prevKeys = keys;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    /// <summary>Weapon ids god mode loans out, in belt-fill order — derived straight from the
    /// item registry so every <c>Weapon</c> row is loaned automatically. Adding a new weapon is
    /// now just its <see cref="ItemDef"/>; there's no armoury list to keep in sync. "bullets" is
    /// intrinsic (always on the belt from spawn) so it's skipped here.</summary>
    private IEnumerable<string> GodWeaponIds
    {
        get
        {
            foreach (var (id, def) in _items)
                if (def.Weapon && id != "bullets") yield return id;
        }
    }

    /// <summary>God mode's unlimited crafting stock: top the pack up to 9999 of every known
    /// resource, every creature part, and every id any active recipe bills in (which sweeps
    /// in planet-specific ship ores automatically). Mirrors the mothership debug menu's
    /// "fill cargo hold" grant, planet-side.</summary>
    /// <summary>Drop items out of the pack onto the ground (right-click context menu).
    /// Gems fall as physical pickups you can grab back; raw materials scatter as real dust
    /// cells (up to a visual cap — dumping a 9999 stack discards the rest); permanent
    /// tools can't be dropped at all.</summary>
    private void DropItem(string id, int count)
    {
        if (_items.TryGetValue(id, out var def) && def.Owned is not null)
        {
            _toast = "CAN'T DROP TOOLS OR WORN GEAR";
            _toastTimer = 2f;
            return;
        }
        count = Math.Min(count, _run.Player.Inventory.Count(id));
        if (count <= 0 || !_run.Player.Inventory.TryConsume(id, count)) return;

        var up = _run.Planet.UpAt(_run.Player.Position);
        var right = new Vector2(-up.Y, up.X);
        // Which tile kind does this id come from? (Reverse of Tiles.Drop.)
        TileKind srcKind = TileKind.Sky;
        foreach (var k in Enum.GetValues<TileKind>())
            if (Tiles.Drop(k) is { } d && d.id == id) { srcKind = k; break; }

        if (srcKind is TileKind.Ruby or TileKind.Sapphire or TileKind.Diamond
            or TileKind.Emerald or TileKind.Voidstone or TileKind.Crystal)
        {
            // Gems drop whole — pickups that bounce and can be re-collected.
            for (var i = 0; i < Math.Min(count, 20); i++)
                _run.Pickups.Add(new Pickup(_run.Player.Position + up * 3f, srcKind,
                    right * (((float)Random.Shared.NextDouble() - 0.5f) * 90f) + up * 70f));
        }
        else if (srcKind != TileKind.Sky)
        {
            for (var i = 0; i < Math.Min(count, 60); i++)
                _run.Cells.LaunchAtWorld(_run.Player.Position + up * 2f,
                    right * (((float)Random.Shared.NextDouble() - 0.5f) * 110f)
                    + up * (50f + (float)Random.Shared.NextDouble() * 60f),
                    Material.Dust, srcKind);
        }
        _particles.EmitDust(_run.Player.Position, 5f);
        _toast = $"DROPPED {count} {Tiles.ResourceLabel(id)}";
        _toastTimer = 2f;
    }

    /// <summary>God mode's permanent item grant: every tool, weapon, light, armor piece,
    /// accessory, and pack at max tier — ownership flags flip for real, so it all stays
    /// when god mode is toggled off.</summary>
    private void GrantGodmodeItems()
    {
        var p = _run.Player;
        p.HasDrill = p.HasHammer = p.HasMiningLaser = p.HasCoreDrill = true;
        p.HasPistol = p.HasMachineGun = p.HasLaser = p.HasLaserCannon = p.HasRocketLauncher = true;
        p.HasFlamethrower = p.HasAcidSpewer = p.HasLightningGun = true;
        p.HasGrapple = true;
        _run.HasCannon = true;
        p.PickaxeTier = 4;
        p.ScannerTier = 4;
        if (p.Inventory.Count("scanner") == 0) p.Inventory.Add("scanner", 1);
        p.Toolbelt.AutoEquip("scanner");
        p.LightTier = 4;
        p.HeadlampTier = 4;
        p.HasJetpack = p.JetTier2 = p.JetTier3 = p.JetTier4 = true;
        p.HasAirTank = p.HasArmor = true;
        foreach (var mid in Toolbelt.MeleeIds) p.MeleeTiers[mid] = 4;
        foreach (var id in new[]
        {
            "torch", "lantern", "helm_lamp", "sun_crystal", "jetpack",
            "armor", "iron_helmet", "iron_leggings", "iron_boots", "iron_gauntlets",
            "chitin_armor", "chitin_helmet", "chitin_leggings", "chitin_boots", "leather_gloves",
            "band_regen", "magnet_ring", "miners_charm", "aegis_pendant",
            "sword", "mace", "warhammer", "shield",
            "great_sword", "great_mace", "great_hammer", "tower_shield",
            "grapple",
        })
            if (p.Inventory.Count(id) == 0) p.Inventory.Add(id, 1);
        if (p.Inventory.Count("rope") < 8) p.Inventory.Add("rope", 8);
        p.Toolbelt.AutoEquip("grapple");
        p.Toolbelt.AutoEquip("rope");
        p.Equipment.AutoEquip("jetpack");
        if (p.Equipment.Get(EquipSlot.Torch) is null) p.Equipment.Set(EquipSlot.Torch, "sun_crystal");
    }

    /// <summary>Whether an item id has an upgrade ladder at all (for the "MAXED" label).</summary>
    private static bool HasUpgradePath(string id) =>
        id is "pickaxe" or "helm_lamp" or "jetpack" || Array.IndexOf(Toolbelt.MeleeIds, id) >= 0;

    /// <summary>The next rung's crafting recipe for an upgradeable item, given the live
    /// run's current tier — null when maxed or the id has no ladder.</summary>
    private Recipe? UpgradeRecipeFor(string id)
    {
        var next = id switch
        {
            "pickaxe" => _run.Player.PickaxeTier switch
            {
                1 => "pickaxe_ii", 2 => "pickaxe_iii", 3 => "pickaxe_iv", _ => null,
            },
            "helm_lamp" => _run.Player.HeadlampTier switch
            {
                1 => "headlamp_ii", 2 => "headlamp_iii", 3 => "headlamp_iv", _ => null,
            },
            "jetpack" => !_run.Player.JetTier2 ? "jetpack_ii"
                : !_run.Player.JetTier3 ? "jetpack_iii"
                : !_run.Player.JetTier4 ? "jetpack_iv" : null,
            _ when Array.IndexOf(Toolbelt.MeleeIds, id) >= 0 =>
                _run.Player.MeleeTiers.GetValueOrDefault(id, 1) < 4 ? $"{id}_up" : null,
            _ => null,
        };
        if (next is null) return null;
        foreach (var r in Crafting.All)
            if (r.Id == next) return r;
        return null;
    }

    private void GrantGodmodeMaterials()
    {
        var inv = _run.Player.Inventory;
        void Top(string id)
        {
            var have = inv.Count(id);
            if (have < 9999) inv.Add(id, 9999 - have);
        }
        foreach (var id in Tiles.ResourceOrder) Top(id);
        foreach (var id in new[] { "meat", "hide", "chitin" }) Top(id);
        foreach (var r in Crafting.All)
            foreach (var (id, _) in r.Cost) Top(id);
    }

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
        // Pickaxe and hammer are physical swings now: LMB starts the sweep and the strike
        // resolves in TickSwing where the blade actually contacts rock — not here. Fly mode
        // keeps instant cursor mining (dev tool).
        if (!_run.Player.FlyMode && tool is MiningTool.Pickaxe or MiningTool.Hammer)
        {
            _run.Player.TryStartSwing(worldCursor, tool, _run.Planet.UpAt(_run.Player.Position));
            return;
        }

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

        // A strike clears a 2×2 footprint of the fine 4-px tiles; handle every tile it broke.
        if (_run.Player.TryMine(_run.Planet, _run.Physics, worldCursor, tool) is not null)
            foreach (var (bx, by, bk) in _run.Player.LastBroken)
                OnTileBroken(bx, by, bk, tool);
    }

    /// <summary>Everything that happens when a mined tile shatters — ore/depth meta stats,
    /// hammer quake vs chip burst, the collectable dust pile, and the break sound. Shared by
    /// the cursor tools (DoMine) and the physical swing (TickSwing).</summary>
    private void OnTileBroken(int x, int y, TileKind bk, MiningTool tool)
    {
        if (Tiles.Drop(bk) is not null) _meta.TotalOreMined++;
        // Wrecking city architecture is noticed — each broken tower tile stokes the city's
        // anger a little; keep chewing through apartments and the militia turns on you.
        if (bk is TileKind.AlienAlloy or TileKind.CityGlass
            or TileKind.AlienPlant or TileKind.HoverPod or TileKind.OrbLamp)
            AddCityWrath(2.5f);
        var depth = _run.Planet.Radius - (int)((_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize);
        if (depth > _meta.DeepestDepth) _meta.DeepestDepth = depth;
        if (tool == MiningTool.Hammer && Tiles.Hardness(bk) >= 4)
        {
            _particles.EmitHammerImpact(_run.Planet.TileToWorld(x, y), bk);
            _run.Shake = MathF.Max(_run.Shake, 0.4f);
        }
        else
        {
            _particles.EmitChips(_run.Planet.TileToWorld(x, y), bk);
        }
        _run.Cells.SpawnDustInTile(x, y, bk);
        // Fell a trunk and the whole crown above the cut collapses into pick-up-able dust —
        // wood from the trunk, a wispy 30%-of-a-tile foliage puff from the leaves — while the
        // underground roots survive to regrow it.
        if (bk == TileKind.TreeTrunk) ToppleTree(x, y);
        // Dig spray: a couple of the fresh dust grains kick out of the hole toward the dwarf
        // — real material in ballistic flight, not just cosmetic chips. They land nearby and
        // are vacuumable like the rest of the pile.
        var brokeAt = _run.Planet.TileToWorld(x, y);
        var toDwarf = _run.Player.Position - brokeAt;
        if (toDwarf.LengthSquared() > 1f)
            _run.Cells.EjectFromTile(x, y, Vector2.Normalize(toDwarf), 90f, 2);
        // minGap: one strike now shatters up to 4 fine tiles in the same frame — one crack, not a burst.
        PlayAt("break", _run.Planet.TileToWorld(x, y), 0.6f,
            pitch: -0.1f + (float)Random.Shared.NextDouble() * 0.25f, minGap: 0.05f);
    }

    /// <summary>Shove every corpse in a blast radius — dead bodies fly with the explosion
    /// (same falloff as the player knockback) instead of lying rigor-still inside it.</summary>
    private void KickCorpses(Vector2 pos, float radius, float power)
    {
        foreach (var corpse in _run.Corpses)
        {
            var d = corpse.Position - pos;
            var dist = d.Length();
            if (dist > radius + corpse.Radius) continue;
            var dir = dist > 0.5f ? d / dist : new Vector2(0f, -1f);
            corpse.Kick(dir * power * (1f - MathHelper.Clamp(dist / (radius + corpse.Radius), 0f, 1f)));
        }
    }

    /// <summary>Tick the rigid debris and couple it to the actors. Hard landings shake the
    /// screen and puff dust; a chunk moving fast enough clobbers the dwarf or a creature
    /// (scaled by closing speed and chunk size), while a slow one just shoulders them aside.</summary>
    private void UpdateRigidBodies(float dt)
    {
        if (_run.Rigid is not { } rigid) return;
        rigid.Update(dt);
        // DM_RIGIDDBG=1: one console line per lifecycle change (detach/stamp), so headless
        // tooling can confirm the detach→fly→stamp round trip without frame-perfect shots.
        if (_rigidDbg && rigid.Bodies.Count != _prevRigidCount)
        {
            Console.WriteLine($"[rigid] bodies {_prevRigidCount} -> {rigid.Bodies.Count}, cells {rigid.CellCount}");
            _prevRigidCount = rigid.Bodies.Count;
        }

        foreach (var (pos, force) in rigid.Impacts)
        {
            _particles.EmitDust(pos, MathF.Min(14f, 4f + force / 30f));
            _run.Shake = MathF.Max(_run.Shake, MathF.Min(0.8f, force / 450f));
            PlayAt("collapse", pos, MathHelper.Clamp(force / 400f, 0.2f, 0.8f),
                pitch: -0.15f, minGap: 0.15f);
        }

        foreach (var b in rigid.Bodies)
        {
            // The dwarf: push-out plus a real hit when the closing speed says "falling slab",
            // scaled by the chunk's size — a pebble stings, a wall section crushes.
            if (RigidBodies.Overlap(b, _run.Player.Position, _run.Player.Radius, out var n, out var cv))
            {
                _run.Player.Position += n * 1.5f;
                var closing = Vector2.Dot(cv - _run.Player.Velocity, n);
                if (closing > 80f)
                {
                    var heft = MathHelper.Clamp(b.Cells.Count / 60f, 0.4f, 2f);
                    _run.Player.TakeDamage(MathF.Min(40f, (closing - 80f) * 0.12f * heft));
                    _run.Player.Velocity += n * MathF.Min(260f, closing);
                }
                else if (closing > 0f)
                {
                    _run.Player.Velocity += n * closing * 0.5f;
                }
            }
            // Creatures: falling debris is indiscriminate — militia under a toppling wall
            // section fare no better than the dwarf would.
            foreach (var c in _run.Creatures)
            {
                if (!RigidBodies.Overlap(b, c.Position, c.Radius, out var cn, out var ccv)) continue;
                c.Position += cn * 1.5f;
                var closing = Vector2.Dot(ccv - c.Velocity, cn);
                if (closing > 80f)
                {
                    c.Health -= MathF.Min(60f, (closing - 80f) * 0.2f
                        * MathHelper.Clamp(b.Cells.Count / 60f, 0.4f, 2f));
                    c.Velocity += cn * MathF.Min(260f, closing);
                }
            }
        }
    }

    /// <summary>Fell a tree from a cut trunk tile: the trunk above the break shears off as a
    /// rigid log that topples sideways and tumbles down the slope (chop it where it lands for
    /// the wood), while the airy canopy puffs into foliage dust. Falls back to the legacy
    /// all-dust fell when the body budget is full. The stump and the roots are left standing,
    /// and the matching <see cref="TreeSite"/> is marked felled so it regrows.</summary>
    private void ToppleTree(int baseRing, int baseCol)
    {
        var planet = _run.Planet;
        var centerFrac = (baseCol + 0.5f) / planet.TilesAt(baseRing);
        // Flood-fill the connected tree above the cut instead of scanning a fixed ±4-column
        // window per ring: wide crowns and leaning regrowth spilled past the window and left
        // canopy chunks hanging in the sky. Everything trunk/canopy reachable from the cut
        // (strictly above it — the stump and roots stay) comes down in one fell.
        var trunk = new List<(int x, int y)>();
        var canopy = new List<(int x, int y, TileKind k)>();
        var seen = new HashSet<int>();
        var flood = new Stack<(int x, int y)>();
        void Visit(int vx, int vy)
        {
            if (vx <= baseRing || vx >= planet.Rings) return;
            var vi = planet.Index(vx, vy);
            if (!seen.Add(vi)) return;
            var vk = planet.Get(vx, vy);
            if (vk is not (TileKind.TreeTrunk or TileKind.TreeCanopy or TileKind.TreeCanopy2)) return;
            flood.Push((vx, vy));
        }
        // Seed just above the cut, a couple of columns wide (trunks can be 2 tiles thick).
        {
            var n1 = planet.TilesAt(baseRing + 1);
            var c1 = (int)(centerFrac * n1);
            for (var dt = -2; dt <= 2; dt++) Visit(baseRing + 1, ((c1 + dt) % n1 + n1) % n1);
        }
        while (flood.Count > 0 && seen.Count < 4000)
        {
            var (rr, tt) = flood.Pop();
            var k = planet.Get(rr, tt);
            if (k == TileKind.TreeTrunk) trunk.Add((rr, tt));
            else canopy.Add((rr, tt, k));
            var oc = planet.OuterNeighbourCount(rr, tt);
            for (var oi = 0; oi < oc; oi++)
            {
                var (or_, ot_) = planet.OuterNeighbour(rr, tt, oi);
                Visit(or_, ot_);
            }
            var (ir_, it_) = planet.InnerNeighbour(rr, tt);
            Visit(ir_, it_);
            Visit(rr, tt - 1);
            Visit(rr, tt + 1);
        }
        foreach (var (rr, tt, ck) in canopy)
        {
            planet.Set(rr, tt, TileKind.Sky);
            _run.Cells.SpawnDustFraction(rr, tt, ck, 0.3f);   // airy foliage: 30% of a tile
        }
        // Tip the log over: a sideways shove at the base plus matching spin, so it keels
        // over and rolls rather than dropping straight down its own shaft.
        var tipSide = Random.Shared.Next(2) == 0 ? -1f : 1f;
        var baseWorld = planet.TileToWorld(baseRing, baseCol);
        var up = planet.UpAt(baseWorld);
        var side = new Vector2(-up.Y, up.X) * tipSide;
        if (_run.Rigid is null || !_run.Rigid.SpawnFromTiles(trunk, side * 30f + up * 8f, tipSide * 1.1f))
        {
            foreach (var (rr, tt) in trunk)
            {
                planet.Set(rr, tt, TileKind.Sky);
                _run.Cells.SpawnDustInTile(rr, tt, TileKind.TreeTrunk);
            }
        }
        // Mark the felled site so the ecosystem regrows it from the surviving roots. Growth
        // resumes from whatever trunk is left standing below the cut.
        var ang = centerFrac * MathHelper.TwoPi;
        foreach (var s in _run.Trees)
        {
            if (MathF.Abs(MathHelper.WrapAngle(s.Angle - ang)) > 0.03f) continue;
            if (baseRing < s.GroundR || baseRing > s.GroundR + s.Height + 1) continue;
            s.Standing = false;
            s.Growth = MathHelper.Clamp((baseRing - 1 - s.GroundR) / (float)Math.Max(1, (int)s.Height), 0f, 1f);
            break;
        }
    }

    /// <summary>Draw the ambient weather clouds — soft drifting banks of puffs parked at each
    /// cloud's altitude, tinted by the biome's rain (grey water, olive acid, ember fire) and
    /// darkening while they rain. Falling rain is drawn by the particle system.</summary>
    private void DrawWeather()
    {
        if (_run.Clouds.Count == 0) return;
        var kind = TreeEcology.RainFor(_run.Def);
        var body = kind switch
        {
            RainKind.Acid => new Color(96, 120, 70),
            RainKind.Fire => new Color(122, 84, 72),
            RainKind.Snow => new Color(150, 160, 178),
            _             => new Color(120, 130, 150),
        };
        var bodyLt = kind switch
        {
            RainKind.Acid => new Color(150, 176, 104),
            RainKind.Fire => new Color(178, 128, 100),
            RainKind.Snow => new Color(226, 234, 246),
            _             => new Color(182, 192, 210),
        };
        foreach (var c in _run.Clouds)
        {
            if (c.Grow < 0.03f) continue;
            var raining = c.RainTimer > 0f;
            var bodyCol = raining ? body : Color.Lerp(body, bodyLt, 0.35f);
            // A slim connected bank: many small puffs packed tightly along the ARC at the
            // cloud's one fixed radius from the planet centre. The sine envelope is skewed
            // and re-lumped per cloud (Shape) so no two banks are the same symmetric
            // almond, and a slow Time billow breathes the tops while every puff's
            // UNDERSIDE stays pinned to the cruise altitude (the radial shift matches the
            // radius growth, so the flat base never bobs). Fade rides alpha instead of
            // size — forming condenses outward from the thick core, dissipating sheds the
            // thin tips first (envelope-vs-Grow gate) rather than deflating like a balloon
            // into a row of beads. A translucent halo under each puff softens the rim.
            var arcLen = c.HalfWidth * 2f * c.Alt;
            var puffs = Math.Max(4, (int)(arcLen / 11f));
            var skew = 0.65f + c.Shape * 0.7f; // envelope exponent: shifts the fat part off-centre
            var alpha = MathHelper.Clamp(c.Grow * 1.15f, 0f, 1f);
            var haloCol = bodyCol * (0.3f * alpha);
            for (var i = 0; i <= puffs; i++)
            {
                var fi = i / (float)puffs;
                var envelope = MathF.Sin(MathF.Pow(fi, skew) * MathF.PI)
                             * (0.74f + 0.26f * MathF.Sin(fi * 7.3f + c.Phase * 5f));
                if (envelope < (1f - c.Grow) * 0.9f) continue;
                var wob = MathF.Sin(c.Phase + i * 2.17f);
                var breathe = (0.5f + 0.5f * MathF.Sin(c.Phase + i * 1.31f + _renderer.Time * 0.3f))
                            * 1.8f * envelope;
                var rad = 6f + envelope * 11f + wob * 1.5f + breathe;
                if (rad < 1.5f) continue;
                var a = c.Angle + (fi - 0.5f) * 2f * c.HalfWidth;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                var p = _run.Planet.Center + dir * (c.Alt + wob * 1.2f + breathe);
                _renderer.DrawCircle(p, rad * 1.45f, haloCol);
                _renderer.DrawCircle(p, rad, bodyCol * alpha);
                // The highlight puff sits on the sunlit top of the bank.
                _renderer.DrawCircle(p + dir * (rad * 0.45f), rad * 0.62f, bodyLt * alpha);
            }
        }
    }

    /// <summary>Advance an in-flight pickaxe/hammer swing and land its strike. Runs every
    /// frame (not just while LMB is held) so a started swing always completes. Contact that
    /// only damages — or clinks off something unbreakable — still gets the pick-tick and a
    /// tiny chip puff, so every landed blow reads.</summary>
    private void TickSwing(float dt)
    {
        if (_run.Player.UpdateSwing(_run.Planet, _run.Physics, dt) is not { } strike) return;
        var hitPos = _run.Planet.TileToWorld(strike.X, strike.Y);
        PlayAt("dig", hitPos, 0.35f,
            pitch: 0.1f + (float)Random.Shared.NextDouble() * 0.3f, minGap: 0.09f);
        if (strike.Broken is not null)
        {
            foreach (var (bx, by, bk) in _run.Player.LastBroken)
                OnTileBroken(bx, by, bk, _run.Player.SwingTool);
        }
        else
        {
            _particles.EmitMiningTick(hitPos, strike.Kind);
        }
    }

    /// <summary>Draw the held pickaxe/hammer as a physical object in the dwarf's grip.
    /// Mid-swing it rotates through Player.SwingAngle — the identical angle the mining
    /// hitbox samples, so the strike lands exactly where the head is drawn. At rest it
    /// hangs where the last swing ended (which is also where the next one starts, so the
    /// chopping pendulum never teleports). Tier IV's icy diamond sheen shows as a tint.</summary>
    private void DrawSwungTool(Texture2D tex, Vector2 aim)
    {
        var p = _run.Player;
        float ang;
        if (p.SwingActive)
        {
            ang = p.SwingAngle;
        }
        else
        {
            var theta = MathF.Atan2(aim.Y, aim.X);
            ang = theta - p.SwingFlip * Player.SwingArc * 0.5f;
        }
        var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        // Length and grip offset come from Player so the drawn blade and the swing's
        // mining reach (Player.SwingReach) stay one and the same object.
        var scale = p.SwingToolLen / tex.Width;
        var tint = p.PickaxeTier >= 4 ? new Color(200, 235, 255)
                 : p.PickaxeTier == 3 ? new Color(235, 235, 245)
                 : Color.White;
        _renderer.Batch.Draw(tex, p.Position + dir * Player.SwingHandOffset, null, tint,
            ang, new Vector2(0.5f, tex.Height / 2f), scale, SpriteEffects.None, 0f);
    }

    /// <summary>Fire a basic bullet from the "bullets" slot. Intrinsic — always available;
    /// no cannon needed. Low damage, fast cadence.</summary>
    private void FireBullet(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 220, 110));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 300f, 6f, 1.4f, ProjectileKind.Bullet));
        _run.Player.ShootCooldown = 0.26f;
    }

    /// <summary>Pistol: the crafted sidearm — twice the bullet's punch at half the cadence.</summary>
    private void FirePistol(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir, new Color(255, 235, 160));
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 340f, 14f, 1.5f, ProjectileKind.Pistol));
        _run.Player.ShootCooldown = 0.44f;
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
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 330f, 4f, 1.2f, ProjectileKind.MachineGun));
        _run.Player.ShootCooldown = 0.10f;
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

    // ── Throw-charge system ───────────────────────────────────────────────────────────
    // Thrown items (dynamite, dynamite pack, TNT, TNT pack, torches) charge a strength gauge
    // while LMB is held and release the throw on button-up. _throwCharge (0..1) drives the
    // launch speed via ThrowSpeed and the gauge ring on the reticle.
    private float _throwCharge;
    private bool _throwCharging;
    private const float ThrowChargeTime = 0.7f;   // seconds of hold to reach full power

    /// <summary>True for the belt ids that use the charge-up throw (gauge on the reticle).</summary>
    private static bool IsThrowable(string id) =>
        id is "dynamite" or "dynamite_pack" or "tnt" or "tnt_pack" or "torch";

    /// <summary>Launch speed for a thrown item: lerps from a soft lob to a hard throw across
    /// the current charge. Read by every Fire*/ThrowTorch method so they share one gauge.</summary>
    private float ThrowSpeed(float min, float max) => MathHelper.Lerp(min, max, _throwCharge);

    /// <summary>True if the selected throwable can actually be thrown right now (ammo in
    /// stock, or god mode) — so charging only starts when a throw would land.</summary>
    private bool CanThrowSelected(string id)
    {
        if (_run.Player.FlyMode) return true;
        if (_items.TryGetValue(id, out var def) && def.Ammo is { } ammo)
            return _run.Player.Inventory.Count(ammo) > 0;
        return true;
    }

    /// <summary>TNT: a heavy satchel charge. Barely throwable — a short weighty lob with a
    /// long fuse — but the biggest non-nuke blast in the game. Placement tool, not artillery.</summary>
    private void FireTnt(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var up = _run.Planet.UpAt(_run.Player.Position);
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 5f,
            dir * ThrowSpeed(70f, 210f) + up * 50f, 120f, 3.0f, ProjectileKind.Tnt));
        _run.Player.ShootCooldown = 0.6f;
    }

    /// <summary>TNT pack: the sticky charge. A real throw (it has to reach a ceiling), and
    /// it cements to the first wall it touches, burning the same fuse as the satchel.</summary>
    private void FireTntPack(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 5f,
            dir * ThrowSpeed(150f, 320f), 120f, 3.0f, ProjectileKind.TntPack));
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
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 220f, 80f, 1.8f, ProjectileKind.CannonDiamond));
            _run.Player.ShootCooldown = 0.88f;
            _run.Shake = MathF.Max(_run.Shake, 0.5f);
        }
        else if (_run.Player.Inventory.TryConsume("ammo_sapphire", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 235f, 35f, 1.7f, ProjectileKind.CannonSapphire));
            _run.Player.ShootCooldown = 0.72f;
        }
        else if (_run.Player.Inventory.TryConsume("ammo_ruby", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 235f, 32f, 1.7f, ProjectileKind.CannonRuby));
            _run.Player.ShootCooldown = 0.72f;
        }
        else if (_run.Player.Inventory.TryConsume("ammo_silver", 1))
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 275f, 22f, 1.6f, ProjectileKind.CannonSilver));
            _run.Player.ShootCooldown = 0.6f;
        }
        else
        {
            _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, dir * 235f, 25f, 1.6f, ProjectileKind.Cannon));
            _run.Player.ShootCooldown = 0.72f;
        }
    }

    // ── Energy ball (the old "nuke") — a charge-up alien cannon ─────────────────────────
    private float _energyCharge;      // 0..1 while winding up
    private bool _energyCharging;
    private float _energyHumT;        // throttle for the rising charge hum
    private const float EnergyChargeTime = 1.6f;   // seconds to full charge

    /// <summary>Non-linear charge → damage/size factor: a half charge is only ~30% power, a
    /// full charge is 100%. Pow(charge, 1.74) hits 0.30 at 0.5 and 1.0 at 1.0.</summary>
    private static float EnergyPower(float charge) => MathF.Pow(MathHelper.Clamp(charge, 0f, 1f), 1.74f);

    /// <summary>Fire the energy ball at its current charge. Reads _energyCharge (set by the
    /// hold-to-charge input). The orb's size, blast, damage, and alien-metal bite all scale
    /// non-linearly with the charge — a tapped shot is a firecracker, a full charge levels a
    /// block.</summary>
    private void FireEnergyBall(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var f = EnergyPower(_energyCharge);
        _particles.EmitMuzzleFlash(_run.Player.Position + dir * 7f, dir,
            Color.Lerp(new Color(150, 120, 255), new Color(255, 120, 240), f));
        var p = new Projectile(_run.Player.Position + dir * 6f, dir * 240f, 1500f * f, 3f, ProjectileKind.Nuke);
        p.Radius *= (0.6f + 0.8f * f) * 0.35f;              // 35% of the old ball size, at every charge stage
        p.ExplosionRadius *= 0.45f + 0.55f * f;
        p.CraterTiles = Math.Max(2, (int)(p.CraterTiles * (0.4f + 0.6f * f)));
        p.AlloyMinePower = Math.Max(2, (int)(12 * f));      // full charge = 12 (breaks alien metal ~4 hits)
        _run.Projectiles.Add(p);
        _run.Player.ShootCooldown = 0.6f;
        _run.Shake = MathF.Max(_run.Shake, 0.3f + 0.5f * f);
        PlayAt("explode", _run.Player.Position, 0.4f + 0.4f * f, pitch: 0.3f - 0.5f * f);
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
        var speed = ThrowSpeed(110f, 240f);
        var up = _run.Planet.UpAt(_run.Player.Position);
        var velocity = dir * speed + up * 60f;
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, velocity, 50f, 3.0f, ProjectileKind.Dynamite));
        _run.Player.ShootCooldown = 0.3f;
    }

    /// <summary>Dynamite pack: the bundled charge — throws like a stick but with 3× the
    /// blast. Same 3-second fuse and bounce.</summary>
    private void FireDynamitePack(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var up = _run.Planet.UpAt(_run.Player.Position);
        var velocity = dir * ThrowSpeed(100f, 220f) + up * 60f;
        _run.Projectiles.Add(new Projectile(_run.Player.Position + dir * 6f, velocity, 60f, 3.0f,
            ProjectileKind.DynamitePack));
        _run.Player.ShootCooldown = 0.35f;
    }

    /// <summary>Climbing the monster: an airborne dwarf touching the titan's hull latches to
    /// a bearing on its body circle and moves WITH it — the platform is the monster. A/D walk
    /// around the hide (all the way over the back, to hold aim on a weakpoint), a Space tap
    /// hops off, lighting the jetpack pulls free, and the shake-off thrash flings the rider.</summary>
    private void TickTitanRiding(float dt, KeyboardState keys)
    {
        var t = _run.Titan;
        var p = _run.Player;
        if (!t.Hatched || t.Health <= 0 || !t.Targetable || p.FlyMode) { _riding = false; return; }

        var surfR = t.BodyRadius + p.Radius + 2f;
        if (!_riding)
        {
            // Latch on hull contact — airborne only, so the monster walking into a dwarf
            // standing on the ground doesn't glue them to it.
            var rel = p.Position - t.Position;
            if (p.Grounded || p.IsJetting || rel.Length() > t.BodyRadius + p.Radius + 5f) return;
            _riding = true;
            _rideAngle = MathF.Atan2(rel.Y, rel.X);
        }

        if (Pressed(keys, _prevKeys, Keys.Space) || p.IsJetting)
        {
            _riding = false;
            var off = new Vector2(MathF.Cos(_rideAngle), MathF.Sin(_rideAngle));
            p.Velocity = t.Velocity + off * 90f + _run.Planet.UpAt(p.Position) * 120f;
            return;
        }

        var move = 0;
        if (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left)) move -= 1;
        if (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right)) move += 1;
        _rideAngle += move * dt * (48f / surfR);
        var dir = new Vector2(MathF.Cos(_rideAngle), MathF.Sin(_rideAngle));
        p.Position = t.Position + dir * surfR;
        p.Velocity = t.Velocity;
        // The monster feels the rider and thrashes once its patience runs out (2×dt beats
        // the titan's own 1×dt decay); mid-shake the grip visibly rattles.
        t.RiderTime += 2f * dt;
        if (t.ShakeTimer > 0f) p.Position += dir * (MathF.Sin(t.ShakeTimer * 40f) * 2f);
    }

    /// <summary>The grapple line as a hard length constraint on the player: swing inside it,
    /// never stretch past it. Hold LMB (with the hook selected) to reel in, S to pay line
    /// out, Space to cut it. A line latched to the titan rides its hide — the boarding route —
    /// and counts as clinging for the monster's shake-off patience.</summary>
    private void TickGrapple(float dt, KeyboardState keys, MouseState mouse)
    {
        if (_grapAnchor is null && !_grapOnTitan) return;
        var t = _run.Titan;
        var p = _run.Player;
        if (_grapOnTitan && (t.Health <= 0 || !t.Targetable)) { ReleaseGrapple(); return; }
        if (_riding) { ReleaseGrapple(); return; }   // boarded — the line's done its job
        var anchor = _grapOnTitan ? t.Position + _grapLocal : _grapAnchor!.Value;
        if (!_grapOnTitan)
        {
            // The anchor tile got mined/blown away — the hook falls free.
            var (ax, ay) = _run.Planet.WorldToTile(anchor);
            if (!Tiles.IsSolid(_run.Planet.Get(ax, ay))) { ReleaseGrapple(); return; }
        }
        if (Pressed(keys, _prevKeys, Keys.Space)) { ReleaseGrapple(); return; }
        if (p.Toolbelt.Current == "grapple" && mouse.LeftButton == ButtonState.Pressed)
            _ropeLen = MathF.Max(10f, _ropeLen - 130f * dt);
        if (keys.IsKeyDown(Keys.S))
            _ropeLen = MathF.Min(420f, _ropeLen + 100f * dt);

        var d = p.Position - anchor;
        var len = d.Length();
        if (len > 480f) { ReleaseGrapple(); return; }
        if (len > _ropeLen && len > 0.01f)
        {
            var n = d / len;
            p.Position = anchor + n * _ropeLen;
            var vOut = Vector2.Dot(p.Velocity, n);
            if (vOut > 0f) p.Velocity -= n * vOut;   // taut line: keep the swing, kill the stretch
        }
        if (_grapOnTitan) t.RiderTime += 2f * dt;
    }

    private void ReleaseGrapple()
    {
        _grapAnchor = null;
        _grapOnTitan = false;
    }

    /// <summary>Cast the grappling hook at the cursor: an instant line march that latches to
    /// the first player-blocking tile — or the titan's hide, the climbing-aid route onto the
    /// monster. One line at a time; W cuts it (see TickGrapple).</summary>
    private void FireGrapple(Vector2 worldCursor)
    {
        if (_grapAnchor is not null || _grapOnTitan) return;
        var from = _run.Player.Position;
        var dir = worldCursor - from;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _run.Player.ShootCooldown = 0.45f;
        var t = _run.Titan;
        for (var d = 8f; d <= 400f; d += 4f)
        {
            var probe = from + dir * d;
            if (t.Hatched && t.Health > 0 && t.Targetable
                && (probe - t.Position).Length() < t.BodyRadius + 4f)
            {
                _grapOnTitan = true;
                _grapLocal = probe - t.Position;
                _ropeLen = d;
                PlayAt("harpoon", from, 0.8f);
                return;
            }
            var (px, py) = _run.Planet.WorldToTile(probe);
            if (Tiles.BlocksPlayer(_run.Planet.Get(px, py)))   // no purchase on foliage
            {
                _grapAnchor = probe;
                _ropeLen = d;
                PlayAt("harpoon", from, 0.8f);
                return;
            }
        }
        PlayAt("ui", from, 0.3f, pitch: -0.4f);   // dry cast — nothing in range
    }

    /// <summary>Deploy a rope coil at the cursor: anchored under any non-sky tile, it unrolls
    /// straight down as climbable Rope segments (10 max) until it meets ground — the cheap
    /// way up a tower flank, a cliff, or into a shaft. One coil per use.</summary>
    private void PlaceRope(Vector2 worldCursor)
    {
        var p = _run.Player;
        if (p.Inventory.Count("rope") <= 0 && !p.FlyMode) return;
        if ((worldCursor - p.Position).Length() > 70f) return;
        var (cx, cy) = _run.Planet.WorldToTile(worldCursor);
        if (_run.Planet.Get(cx, cy) != TileKind.Sky) return;
        // Needs something to hang from directly above the top segment.
        var up = _run.Planet.UpAt(worldCursor);
        var (ax, ay) = _run.Planet.WorldToTile(worldCursor + up * Planet.TileSize);
        if (_run.Planet.Get(ax, ay) == TileKind.Sky) return;

        var placed = 0;
        var pos = worldCursor;
        for (var i = 0; i < 10; i++)
        {
            var (tx, ty) = _run.Planet.WorldToTile(pos);
            if (_run.Planet.Get(tx, ty) != TileKind.Sky) break;
            _run.Planet.Set(tx, ty, TileKind.Rope);
            placed++;
            pos -= _run.Planet.UpAt(pos) * Planet.TileSize;   // per-step up: follow curvature
        }
        if (placed == 0) return;
        if (!p.FlyMode) p.Inventory.TryConsume("rope", 1);
        p.ShootCooldown = 0.3f;
        PlayAt("throw", worldCursor, 0.5f);
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

    /// <summary>Swing timer for the held melee weapon's arc animation (counts down;
    /// duration in _meleeAnimDur).</summary>
    private float _meleeAnim;
    private float _meleeAnimDur = 1f;

    /// <summary>Per-weapon melee tuning. Damage scales +40% per upgrade rung; rung 4 gains
    /// mine power — the energy edge carves terrain in the swing arc.</summary>
    private static (float dmg, float reach, float cd, string family) MeleeBase(string id) => id switch
    {
        // Reaches match the drawn blades (weapons render 50% longer now); cadences are
        // deliberate — a full arcing swing, not a poke.
        "sword"        => (14f, 29f, 0.38f, "sword"),
        "mace"         => (18f, 26f, 0.52f, "mace"),
        "warhammer"    => (24f, 26f, 0.68f, "hammer"),
        "shield"       => (6f, 18f, 0.60f, "shield"),
        "great_sword"  => (26f, 37f, 0.68f, "sword"),
        "great_mace"   => (32f, 34f, 0.86f, "mace"),
        "great_hammer" => (40f, 34f, 1.05f, "hammer"),
        _              => (10f, 21f, 0.72f, "shield"),   // tower_shield bash
    };

    /// <summary>Rung-4 energy edge colour, by weapon family — the "lightsaber" glow.</summary>
    private static Color MeleeGlow(string id) => MeleeBase(id).family switch
    {
        "sword"  => new Color(120, 225, 255),
        "mace"   => new Color(255, 120, 230),
        "hammer" => new Color(255, 170, 80),
        _        => new Color(140, 255, 160),
    };

    /// <summary>One melee swing: damages and knocks back every creature in the aim arc
    /// (the titan too), and at rung 4 the energy edge carves soft terrain along the arc.
    /// The swing animation is driven by _meleeAnim in the held-weapon draw.</summary>
    /// <summary>Stoke the city's anger at the dwarf. Crossing the tipping point announces
    /// the turn once (the toast) — below it the city just remembers quietly.</summary>
    private void AddCityWrath(float amount)
    {
        if (_run.Planet.CityDistricts.Count == 0) return;
        var before = _run.CityWrath;
        _run.CityWrath = MathF.Min(100f, _run.CityWrath + amount);
        if (before < 50f && _run.CityWrath >= 50f)
        {
            _toast = "! THE CITY HAS TURNED ON YOU !";
            _toastTimer = 5f;
            _sfx.Play("creak", 0.9f, pitch: -0.3f);
        }
    }

    /// <summary>E on/near a door pops it open or shut. Checks the cursor tile first, then a
    /// small ring around the player, so standing in a doorway and mashing E always works.
    /// Both leaves of a two-tall door toggle together (doors place/generate as vertical
    /// pairs) — half-open doors read as broken, not ajar.</summary>
    /// <summary>E at a warren treasure chest: loot it for a pile of gold and a good shot at a
    /// rare gem, then leave the lid thrown back. Scans a short radius around the dwarf so you
    /// just walk up and press E. Returns true if a chest was opened (so E doesn't also cycle
    /// weapons that frame).</summary>
    private bool TryOpenChest()
    {
        var planet = _run.Planet;
        var pos = _run.Player.Position;
        for (var a = 0; a < 12; a++)
        {
            var off = new Vector2(MathF.Cos(a * MathF.Tau / 12f), MathF.Sin(a * MathF.Tau / 12f));
            for (var d = 0f; d <= 22f; d += 4f)
            {
                var (tx, ty) = planet.WorldToTile(pos + off * d);
                if (tx < 0 || tx >= planet.Rings) continue;
                if (planet.Get(tx, ty) != TileKind.Chest) continue;
                OpenChest(tx, ty);
                return true;
            }
        }
        return false;
    }

    private void OpenChest(int tx, int ty)
    {
        var planet = _run.Planet;
        planet.Set(tx, ty, TileKind.ChestOpen);
        var at = planet.TileToWorld(tx, ty);

        // A hoard of gold, plus a strong chance of a rare gem — the warren vault pays out.
        var gold = 10 + Random.Shared.Next(12);
        _run.Player.Inventory.Add("gold", gold);
        var roll = Random.Shared.NextDouble();
        var (bonusId, bonusN) =
              roll < 0.28 ? ("diamond", 1)
            : roll < 0.52 ? ("ruby", 1 + Random.Shared.Next(2))
            : roll < 0.74 ? ("sapphire", 1 + Random.Shared.Next(2))
            : roll < 0.90 ? ("emerald", 1)
            : ("silver", 4 + Random.Shared.Next(6));
        _run.Player.Inventory.Add(bonusId, bonusN);

        _sfx.Play("pickup", 0.9f);
        _particles.EmitDust(at, 12f);
        _renderer.AddLight(at, 26f, new Color(255, 220, 120));
        _toast = $"CHEST LOOTED: +{gold} GOLD, +{bonusN} {Tiles.ResourceLabel(bonusId)}";
        _toastTimer = 3f;
    }

    private bool TryToggleDoor(Vector2 worldCursor)
    {
        bool TryAt(Vector2 at)
        {
            var (tx, ty) = _run.Planet.WorldToTile(at);
            var k = _run.Planet.Get(tx, ty);
            if (k is not (TileKind.DoorClosed or TileKind.DoorOpen)) return false;
            var to = k == TileKind.DoorClosed ? TileKind.DoorOpen : TileKind.DoorClosed;
            // The whole leaf swings together — Planet.SetDoorRun walks the run ring by
            // ring with column slack, so the drifted upper tiles come along too.
            _run.Planet.SetDoorRun(tx, ty, to);
            PlayAt("creak", at, 0.5f, pitch: to == TileKind.DoorOpen ? 0.3f : -0.2f);
            return true;
        }

        if ((worldCursor - _run.Player.Position).Length() < 40f && TryAt(worldCursor)) return true;
        // Fallback sweep: the player's own tile plus its neighbours.
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (TryAt(_run.Player.Position + new Vector2(dx, dy) * Planet.TileSize))
                    return true;
        return false;
    }

    private void MeleeAttack(string id, Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        var tier = Math.Clamp(_run.Player.MeleeTiers.GetValueOrDefault(id, 1), 1, 4);
        var (baseDmg, reach, cd, _) = MeleeBase(id);
        var dmg = baseDmg * (1f + 0.4f * (tier - 1));
        var up = _run.Planet.UpAt(_run.Player.Position);

        foreach (var c in _run.Creatures)
        {
            var to = c.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist > reach + c.Radius || dist < 0.5f) continue;
            if (Vector2.Dot(to / dist, dir) < 0.35f) continue;
            c.Health -= dmg;
            c.HitFlash = 0.15f;
            c.Velocity += dir * 150f + up * 50f;
            _particles.EmitDust(c.Position, 4f);
        }
        if (_run.Titan.Health > 0 && _run.Titan.Targetable)
        {
            var to = _run.Titan.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist < reach + 28f && dist > 0.5f && Vector2.Dot(to / dist, dir) > 0.3f)
            {
                _run.Titan.Health -= dmg * 0.9f;
                _run.Titan.HitFlash = 0.15f;
            }
        }

        // Rung 4: the energy edge shears terrain along the swing path. Each sample angle
        // raymarches OUT from the player and bites the FIRST solid tile it meets — the
        // blade stops on the near wall instead of teleporting past it to carve at some
        // fixed radius (which used to skip adjacent blocks while cutting distant ones).
        if (tier >= 4)
        {
            var power = id.StartsWith("great") ? 6 : 4;
            var aimAng = MathF.Atan2(dir.Y, dir.X);
            var chips = 4;
            for (var i = 0; i <= 9; i++)
            {
                var a = aimAng + MathHelper.Lerp(-1.0f, 0.75f, i / 9f);
                var ray = new Vector2(MathF.Cos(a), MathF.Sin(a));
                for (var step = Planet.TileSize * 0.75f; step <= reach * 0.95f;
                     step += Planet.TileSize * 0.5f)
                {
                    var at = _run.Player.Position + ray * step;
                    var (mx, my) = _run.Planet.WorldToTile(at);
                    if (!Tiles.IsSolid(_run.Planet.Get(mx, my))) continue;
                    if (_run.Planet.Mine(mx, my, power) is { } cut)
                    {
                        _run.Cells.SpawnDustInTile(mx, my, cut);
                        _run.Physics.MarkDirty(mx, my);
                        if (chips-- > 0) _particles.EmitChips(_run.Planet.TileToWorld(mx, my), cut);
                        if (cut is TileKind.AlienAlloy or TileKind.CityGlass) AddCityWrath(2.5f);
                    }
                    break; // first solid tile along the ray — hit or bounce, the swing stops here
                }
            }
        }

        _run.Player.ShootCooldown = cd;
        // The swing animation covers most of the cadence — a long deliberate arc.
        _meleeAnimDur = MathF.Min(0.38f, cd * 0.85f);
        _meleeAnim = _meleeAnimDur;
        if (id.StartsWith("great")) _run.Shake = MathF.Max(_run.Shake, 0.15f);
    }

    /// <summary>Lob a torch: it arcs under gravity, sticks to whatever solid it hits, and
    /// burns there — dangling slightly — as a persistent planted light. Throw distance
    /// scales with how far the cursor is, like dynamite.</summary>
    private void ThrowTorch(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        var dist = dir.Length();
        dir /= dist;
        var up = _run.Planet.UpAt(_run.Player.Position);
        var speed = ThrowSpeed(120f, 260f);
        _run.Torches.Add(new ThrownTorch(_run.Player.Position + dir * 5f, dir * speed + up * 50f));
        // Threw the last one straight out of the light slot → the dwarf is empty-handed.
        if (_run.Player.Inventory.Count("torch") == 0
            && _run.Player.Equipment.Get(EquipSlot.Torch) == "torch")
            _run.Player.Equipment.Set(EquipSlot.Torch, null);
        _run.Player.ShootCooldown = 0.3f;
    }

    /// <summary>Advance the hose's held-length ramp and return the current stream reach. Both
    /// hoses share it (you fire one at a time): each fire tick nudges the ramp up, and a &gt;0.15s
    /// gap resets it, so the stream starts SHORT and grows to full reach the longer you hold
    /// fire, then snaps back short when you let go.</summary>
    private float StreamReach()
    {
        if (_run.RunTime - _streamLast > 0.15f) _streamHold = 0f;
        _streamLast = _run.RunTime;
        _streamHold = MathF.Min(_streamHold + _frameDt, StreamHoldMax);
        return MathHelper.Lerp(59f, 185f, _streamHold / StreamHoldMax);
    }

    /// <summary>Flamethrower: a steady, TIGHT tongue of REAL burning fuel — Fire cells launched
    /// down the aim that COLLIDE with tiles, so the flame stops at walls instead of pouring
    /// through them. The stream starts short and grows to full reach the longer fire is held;
    /// creatures caught in it catch fire. A continuous low roar, not a rattling machine gun.</summary>
    private void FireFlamethrower(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        // Muzzle at the DRAWN gun tip: grip anchor 3.2 + sprite length × the 0.39 held
        // scale (see the held-weapon draw) — the flame is born at the barrel mouth.
        var muzzle = _run.Player.Position + dir * (_weaponTex.TryGetValue("flamethrower", out var ftx)
            ? 3.2f + (ftx.Width - 1.5f) * 0.39f : 9f);

        // Underwater (or with the muzzle pressed into a pool), the burner can't light — it
        // just belches steam bubbles and does nothing else.
        var (mcx, mcy) = _run.Cells.WorldToCell(muzzle);
        if (_run.Cells.Get(mcx, mcy) == Material.Water)
        {
            _run.Cells.PlaceAtWorld(muzzle, Material.Smoke);
            _run.Player.ShootCooldown = 0.12f;
            return;
        }
        var reach = StreamReach();

        // NO launched fire cells any more: the flying payload obeyed different physics
        // (FlyMaxOutward, its own launch jitter) and kept reading as stray streaks off
        // the stream's arc no matter how the two were matched — removed per user. The
        // burn is delivered by the visible grains themselves now: every flame grain
        // stamps a real Fire cell where it lands (Particles.LandMat), and the cone
        // ignition below burns creatures directly.
        _particles.EmitFlameJet(muzzle, dir, reach, _run.Planet.UpAt(muzzle), _run.Player.Velocity);
        // Near-cone ignition out to the current reach — a tight cone.
        foreach (var c in _run.Creatures)
        {
            var to = c.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist > reach + 12f || dist < 1f) continue;
            if (Vector2.Dot(to / dist, dir) < 0.9f) continue;
            if (c.ImmuneTo(Material.Fire)) continue;
            c.BurnSeconds = MathF.Max(c.BurnSeconds, 3f);
            c.Health -= 14f * _frameDt;
            c.HitFlash = 0.1f;
        }
        // The hose roasts titans too — except the fire-blooded (Godzilla's breath, the
        // Pyrodactyl's lava): you can't burn what already burns.
        if (_run.Titan.Health > 0 && _run.Titan.Targetable
            && _run.Titan.Kind is not (TitanKind.Godzilla or TitanKind.Pyrodactyl))
        {
            var to = _run.Titan.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist is > 1f && dist < reach + 24f && Vector2.Dot(to / dist, dir) > 0.8f)
            {
                _run.Titan.Health -= 21f * _frameDt;
                _run.Titan.HitFlash = 0.1f;
            }
        }
        _run.Player.ShootCooldown = 0f;   // per-frame: the stream is continuous, damage is dt-scaled /3 to hold the old dps
    }

    /// <summary>Acid spewer: sprays REAL Acid cells that pool where they land and eat
    /// through soft rock (at the halved corrosion rate) — a mining tool as much as a
    /// weapon. The caustic splash sickens creatures in the near cone.</summary>
    private void FireAcidSpewer(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        // Muzzle at the DRAWN gun tip — same convention as the flamethrower.
        var muzzle = _run.Player.Position + dir * (_weaponTex.TryGetValue("acid_spewer", out var atx)
            ? 3.2f + (atx.Width - 1.5f) * 0.39f : 9f);
        var reach = StreamReach();
        // NO launched acid cells any more — same treatment as the flamethrower: the
        // launched payload rode its own physics and read as streaks off the stream's
        // arc. Every visible droplet stamps a real Acid cell where it lands
        // (Particles.LandMat), which IS the corrosion mechanic; the near cone below
        // sickens creatures directly.
        _particles.EmitAcidJet(muzzle, dir, reach, _run.Planet.UpAt(muzzle), _run.Player.Velocity);
        foreach (var c in _run.Creatures)
        {
            var to = c.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist > reach + 10f || dist < 1f) continue;
            if (Vector2.Dot(to / dist, dir) < 0.9f) continue;
            if (c.ImmuneTo(Material.Acid)) continue;
            c.Health -= 17f * _frameDt;
            c.HitFlash = 0.1f;
        }
        // Caustic against titans too — except the acid-blooded (Otachi's spit, the
        // Vitriodactyl's rain), which the spray just washes over.
        if (_run.Titan.Health > 0 && _run.Titan.Targetable
            && _run.Titan.Kind is not (TitanKind.Otachi or TitanKind.Vitriodactyl))
        {
            var to = _run.Titan.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist is > 1f && dist < reach + 22f && Vector2.Dot(to / dist, dir) > 0.8f)
            {
                _run.Titan.Health -= 23f * _frameDt;
                _run.Titan.HitFlash = 0.1f;
            }
        }
        _run.Player.ShootCooldown = 0f;   // per-frame: the stream is continuous, damage is dt-scaled /3 to hold the old dps
    }

    /// <summary>Lightning gun: instant chain arc. The bolt seeks the closest creature (or
    /// the Titan) near the aim line and forks to further victims within reach of the last;
    /// with no target it grounds out on terrain at the aim point. Each arc renders as a
    /// jagged strobing bolt with hero-lit endpoints.</summary>
    private void FireLightningGun(Vector2 worldCursor)
    {
        var dir = worldCursor - _run.Player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        const float range = 160f;
        const float chainReach = 70f;

        // Conduction: a strike that lands in water electrifies the WHOLE connected pool for
        // a beat — the cell grid flood-fills and flash-tints it (Cells.ZapWater), arcs
        // skitter across the surface, and everything submerged takes the hit, the dwarf
        // included. This is the classic "never stand in the pool you shoot" rule.
        var conducted = false;
        void Conduct(Vector2 at)
        {
            if (conducted) return;
            var arcs = _run.Cells.ZapWater(at);
            if (arcs.Count == 0) return;
            conducted = true;
            for (var a = 0; a < arcs.Count && a < 4; a++) _particles.EmitLightning(at, arcs[a]);
            foreach (var c in _run.Creatures)
                if (_run.Cells.ZappedAt(c.Position))
                {
                    c.Health -= 16f;
                    c.HitFlash = 0.15f;
                }
            if (_run.Cells.ZappedAt(_run.Player.Position)) _run.Player.TakeDamage(10f);
            _run.Shake = MathF.Max(_run.Shake, 0.25f);
        }

        // Does the beam meet open water before anything else? March the aim line; rock
        // shadows the pool, and a closer body target still wins below.
        Vector2? waterHit = null;
        var waterDist = range;
        for (var d = 10f; d < range; d += 5f)
        {
            var probe = _run.Player.Position + dir * d;
            if (_run.Planet.IsSolidAt(probe)) break;
            if (_run.Cells.CountWaterNear(probe, 2.5f) >= 2)
            {
                waterHit = probe;
                waterDist = d;
                break;
            }
        }

        // First victim: nearest creature roughly along the aim; the titan competes too.
        var from = _run.Player.Position + dir * 6f;
        Creature? victim = null;
        var bestDist = range;
        foreach (var c in _run.Creatures)
        {
            var to = c.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist >= bestDist || dist < 1f) continue;
            if (Vector2.Dot(to / dist, dir) < 0.7f) continue;
            victim = c;
            bestDist = dist;
        }
        var titanFirst = false;
        if (_run.Titan.Health > 0 && _run.Titan.Targetable)
        {
            var to = _run.Titan.Position - _run.Player.Position;
            var dist = to.Length();
            if (dist < bestDist && dist > 1f && Vector2.Dot(to / dist, dir) > 0.7f)
                titanFirst = true;
        }

        var damage = 30f;
        if (titanFirst)
        {
            _particles.EmitLightning(from, _run.Titan.Position);
            _run.Titan.Health -= damage;
            _run.Titan.HitFlash = 0.15f;
        }
        else if (victim is null || (waterHit is { } wh0 && waterDist < bestDist))
        {
            if (waterHit is { } wh && (victim is null || waterDist < bestDist))
            {
                // The pool is the nearest conductor: the arc dives in and fans out.
                _particles.EmitLightning(from, wh);
                Conduct(wh);
            }
            else
            {
                // No body in reach: the arc grounds out on the first rock along the aim.
                var end = _run.Player.Position + dir * range;
                for (var d = 8f; d < range; d += 4f)
                {
                    var probe = _run.Player.Position + dir * d;
                    if (_run.Planet.IsSolidAt(probe)) { end = probe; break; }
                }
                _particles.EmitLightning(from, end);
            }
        }
        else
        {
            // Chain: hit, then fork from the last victim to the nearest fresh body, up to
            // three jumps, each arc weaker than the last.
            var hit = new HashSet<Creature>();
            var source = from;
            for (var jump = 0; jump < 4 && victim is not null; jump++)
            {
                _particles.EmitLightning(source, victim.Position);
                victim.Health -= damage;
                victim.HitFlash = 0.15f;
                hit.Add(victim);
                // A victim standing in water dumps the arc into its pool — one conduction
                // per shot, and the chain keeps forking as normal on top of it.
                if (!conducted && _run.Cells.CountWaterNear(victim.Position, 3f) >= 2)
                    Conduct(victim.Position);
                damage *= 0.7f;
                source = victim.Position;
                Creature? next = null;
                var nextDist = chainReach;
                foreach (var c in _run.Creatures)
                {
                    if (hit.Contains(c)) continue;
                    var dist = (c.Position - source).Length();
                    if (dist < nextDist) { next = c; nextDist = dist; }
                }
                victim = next;
            }
        }
        _run.Player.ShootCooldown = 0.4f;
        _run.Shake = MathF.Max(_run.Shake, 0.2f);
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
        // Innermost ~6 rings (3 legacy tiles) count as "at the core". Tunable; keeping it
        // tight rewards the player for actually digging all the way to the bottom.
        if (distFromCentre > Planet.RingMin + 6f) return;

        _particles.EmitImpact(_run.Planet.Center, ProjectileKind.Nuke);
        _run.Shake = MathF.Max(_run.Shake, 1.5f);
        // Piercing the core no longer ends the run — it yields the planet's CORE SHARD, the
        // warp-drive material (one per world, straight into meta: shards are too important
        // to lose to a death on the climb back out). All five shards let the mothership warp
        // to the Rift.
        if (_run.Def.Id is not ("rift" or "debug" or "hollow" or "moon") && !_meta.CoreShards.Contains(_run.Def.Id))
        {
            _meta.CoreShards.Add(_run.Def.Id);
            _meta.Save();
            _toast = $"CORE SHARD SECURED ({_meta.CoreShards.Count}/{PlanetDefs.WarpShardsNeeded}) - NOW GET BACK UP";
        }
        else
        {
            _toast = _run.Def.Id switch
            {
                "rift" => "THE RIFT'S CORE YIELDS NOTHING - SLAY ITS TITAN AND ESCAPE",
                "hollow" => "THE HOLLOW HAS NO CORE SHARD - ITS RICHES ARE THE PRIZE",
                "moon" => "A DEAD MOON HAS NO CORE SHARD - THE SILVER IS THE PRIZE",
                "debug" => "THE DEBUG CORE YIELDS NOTHING - THIS WORLD IS A TEST RIG",
                _ => "CORE ALREADY PIERCED - THE SHARD IS ABOARD",
            };
        }
        _toastTimer = 4f;
    }

    /// <summary>The developer spawn menu's rows — bosses plus the two rocket shortcuts. Rebuilt
    /// each time the menu opens so the delegates close over the current run.</summary>
    private (string, DebugMenu.Entry[])[] BuildDebugTabs() => new (string, DebugMenu.Entry[])[]
    {
        ("TITANS", new DebugMenu.Entry[]
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
            new("Starspawn   (void volley/gravity well)", () => SpawnDebugTitan(TitanKind.CosmicOctopus)),
        }),
        ("CREATURES", BuildCreatureEntries()),
        ("EVENTS", new DebugMenu.Entry[]
        {
            new("Disaster — solar flare",   () => TriggerDebugDisaster(DisasterKind.Flare)),
            new("Disaster — blizzard",      () => TriggerDebugDisaster(DisasterKind.Blizzard)),
            new("Disaster — acid rain",     () => TriggerDebugDisaster(DisasterKind.AcidRain)),
            new("Disaster — magma surge",   () => TriggerDebugDisaster(DisasterKind.MagmaSurge)),
            new("Disaster — eruption",      () => TriggerDebugDisaster(DisasterKind.Eruption)),
            new("Disaster — earthquake",    () => TriggerDebugDisaster(DisasterKind.Earthquake)),
            new("Meteor strike (ambient)",  () => AmbientDirector.SpawnMeteor(_run)),
            new("Cloud — rain shower (starts now)", () => Weather.SpawnDebugCloud(_run, RainKind.Water)),
            new("Cloud — acid drizzle (starts now)", () => Weather.SpawnDebugCloud(_run, RainKind.Acid)),
        }),
        ("MISC", new DebugMenu.Entry[]
        {
            new("Rocket — fuelled, launch-ready", () => SpawnDebugShip(fuelled: true)),
            new("Rocket — dry (mine fuel first)", () => SpawnDebugShip(fuelled: false)),
            new("Toggle fullbright (light up the underground)", () => _fullbright = !_fullbright),
            new("Return to mothership (suspends run)", DebugReturnToShip),
        }),
    };

    /// <summary>One row per CreatureKind, straight off the enum so new species appear here
    /// without upkeep. Spawns at the mouse cursor (the debug world carries no census, so
    /// this tab is how test subjects get into it).</summary>
    private DebugMenu.Entry[] BuildCreatureEntries()
    {
        var kinds = Enum.GetValues<CreatureKind>();
        var entries = new DebugMenu.Entry[kinds.Length];
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            entries[i] = new DebugMenu.Entry(kind.ToString(), () => SpawnDebugCreature(kind));
        }
        return entries;
    }

    /// <summary>Debug: spawn one creature at the mouse cursor, nudged up out of solid rock
    /// and given the same carved spawn pocket a census spawn gets. No hazard veto — if the
    /// tester points a shark at dry land, that's their experiment to run.</summary>
    private void SpawnDebugCreature(CreatureKind kind)
    {
        var mouse = Screen.Mouse();
        var pos = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        if ((pos - _run.Planet.Center).LengthSquared() < 1f)
            pos = _run.Player.Position + _run.Planet.UpAt(_run.Player.Position) * 24f;
        var up = _run.Planet.UpAt(pos);
        for (var i = 0; i < 120 && _run.Planet.IsSolidAt(pos); i++) pos += up * 2f;
        var c = new Creature(pos, kind);
        SpawnDirector.ClearSpawnSpace(_run, pos, c.Radius);
        _run.Creatures.Add(c);
        _toast = $"SPAWNED {kind.ToString().ToUpperInvariant()}";
        _toastTimer = 2f;
    }

    /// <summary>Debug: hop straight back aboard the mothership. The run is suspend-saved
    /// exactly like the quit path, so it stays resumable from the star map.</summary>
    private void DebugReturnToShip()
    {
        if (_run is null || _screen != GameScreen.Playing) return;
        if (!_orbiting) RunSave.Write(_run);
        _orbiting = false;
        _landing = false;
        _ascending = false;
        _transitionFlash = 0.6f;
        EnterSpace(PlanetDefs.IndexOf(_run.Def), zoomFromPlanet: true);
        _toast = "DEBUG: BACK ABOARD THE MOTHERSHIP";
        _toastTimer = 3f;
    }

    /// <summary>Debug: render with no lighting pass at all — the whole underground reads
    /// as fully lit. Toggled from the debug menu.</summary>
    private bool _fullbright;

    /// <summary>Feedback for AmbientDirector results — toasts, shake, and sound for whatever
    /// began this tick. Shared by the normal update and the debug-menu disaster trigger.</summary>
    private void ApplyAmbient(in AmbientDirector.Result ambient)
    {
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
        if (ambient.EruptionStarted)
        {
            _toast = "VOLCANIC ERUPTION";
            _toastTimer = 4f;
            _run.Shake = MathF.Max(_run.Shake, 0.8f);
            PlayAt("collapse", ambient.EruptionPos, 0.9f, pitch: -0.5f);
        }
        if (ambient.QuakeStruck)
            _run.Shake = MathF.Max(_run.Shake, 1.0f);
    }

    /// <summary>Debug-menu action: force a disaster right now. Any disaster already playing
    /// out is cut short first (the shared clock allows only one at a time), and the clock is
    /// re-rolled so the natural spacing resumes after the forced one ends.</summary>
    private void TriggerDebugDisaster(DisasterKind kind)
    {
        _run.FlareWarn = 0f;
        _run.FlareActive = 0f;
        _run.BlizzardActive = 0f;
        _run.AcidRainActive = 0f;
        _run.EruptionLeft = 0f;
        var result = new AmbientDirector.Result();
        if (!AmbientDirector.TryBegin(kind, _run, _particles, ref result))
        {
            _toast = kind == DisasterKind.Eruption ? "NO VOLCANO VENTS ON THIS WORLD"
                                                   : "NO SITE FOUND FOR THAT DISASTER";
            _toastTimer = 2.5f;
            return;
        }
        _run.DisasterTimer = AmbientDirector.NextInterval(_run.Def);
        ApplyAmbient(result);
    }

    /// <summary>Debug-menu action: plant a fully-built, launch-ready ship at the player's feet,
    /// skipping the pad/hull/engine/nav-core craft chain. When <paramref name="fuelled"/> the
    /// tank is topped to launch spec so L lifts off at once; otherwise it's empty and you must
    /// mine "fuel" to fill it — the intended path, just with the build steps skipped.</summary>
    private void SpawnDebugShip(bool fuelled)
    {
        PlaceLaunchPad();
        _run.ShipStage = 3;
        _run.ShipFuel = fuelled ? FuelToLaunch : 0;
        _toast = fuelled ? "SPAWNED FUELLED ROCKET — E AT PAD TO BOARD"
                         : "SPAWNED DRY ROCKET — MINE FUEL, THEN E AT PAD";
        _toastTimer = 2.5f;
    }

    /// <summary>Debug-menu action: replace the current boss with a freshly-hatched one of the
    /// chosen kind, spawned a short arc from the player and already aggroed. Any in-flight boss
    /// ordnance from the previous boss is cleared so the new fight starts clean.</summary>
    private void SpawnDebugTitan(TitanKind kind)
    {
        // Spawn at the mouse cursor's bearing around the planet (the constructor snaps the egg to
        // the surface / core at that angle), so the tester drops the boss exactly where they're
        // pointing. Falls back to just off the player if the cursor sits dead-on the planet centre.
        var mouse = Screen.Mouse();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        var rel = worldCursor - _run.Planet.Center;
        var ang = rel.LengthSquared() > 1f
            ? MathF.Atan2(rel.Y, rel.X)
            : MathF.Atan2(_run.Player.Position.Y - _run.Planet.Center.Y,
                          _run.Player.Position.X - _run.Planet.Center.X) + 0.22f;
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
        TitanKind.CosmicOctopus => "Starspawn",
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
        // A turret is a heavy deploy — a real cooldown (gated through NeedsCooldown before
        // ammo is spent) so they can't be machine-gunned down all at once.
        _run.Player.ShootCooldown = 0.9f;
    }

    /// <summary>Tiles below the planet's baseline surface at the player's current radius —
    /// negative on mountain peaks, ~0 at sea level, rising toward the core. The shared depth
    /// metric for oxygen (here) and the HUD readout.</summary>
    private float DepthBelowSurface()
    {
        var ringsFromCenter = (_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize;
        // Measured against the LOCAL terrain line (Planet.SurfaceProfile), not the flat
        // baseline — on the lumpy asteroid a lobe-valley floor under open sky is the
        // surface, not a 20-tile-deep mine. Round worlds' profiles barely deviate.
        return _run.Planet.SurfaceRadiusAt(_run.Player.Position) - ringsFromCenter;
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

    /// <summary>The unified AIR meter. One bar drains in the two situations you can't breathe:
    /// head underwater (unless the gill graft breathes water), or standing on an airless
    /// world (no atmosphere) without the vacsuit's sealed helmet. Everywhere else — an
    /// atmosphere world at any depth, or an airless world with the helmet — it refills.
    /// Empty = suffocation HP bleed (bypasses armor). God mode stays topped up. Gas pockets
    /// still choke via the gas hazard's direct Oxygen drain, which outpaces the refill.</summary>
    private void TickAir(float dt)
    {
        var p = _run.Player;
        var max = p.EffectiveMaxOxygen;
        if (p.FlyMode)
        {
            p.Oxygen = max;
            return;
        }

        var underwater = p.HeadInWater && !p.HasGills;
        var airlessNoHelmet = _run.Def.Airless && !p.HasHelmet;

        if (underwater || airlessNoHelmet)
        {
            p.Oxygen = MathF.Max(0f, p.Oxygen - AirDrainPerSecond * dt);
            // Exhaled air: a thin bubble trail from the head while it's under — the AIR
            // bar's visual twin, and the classic "someone is down there" tell.
            if (underwater && Random.Shared.NextDouble() < dt * 2.5f)
            {
                var bubUp = _run.Planet.UpAt(p.Position);
                _particles.EmitBubble(p.Position + bubUp * (p.Radius + 1f), bubUp);
            }
            if (p.Oxygen <= 0f)
            {
                // Suffocation/drowning ignores armor — no plate stops you running out of air.
                p.Health -= OxygenRules.SuffocationDps * dt;
                // Occasional gasp puff so the cause of death reads on-screen.
                if (Random.Shared.NextDouble() < dt * 4f) _particles.EmitDust(p.Position, 3f);
            }
        }
        else
        {
            p.Oxygen = MathF.Min(max, p.Oxygen + OxygenRules.RefillRate * dt);
        }
    }

    /// <summary>Air burned per second while underwater or airless-without-helmet — a base
    /// 100 reserve lasts ~12.5s, matching the old breath meter (lung/tank upgrades extend
    /// it by raising the ceiling).</summary>
    private const float AirDrainPerSecond = 8f;

    // Hazard-contact tuning (per second while the dwarf's body overlaps the cells).
    private const float LavaBurnDps = 42f;   // ~2.4s from full — a lava bath is near-instant death
    private const float AcidBurnDps = 20f;   // corrosive but survivable if you scramble out
    private const float GasChokeOxygen = 26f; // air burned by breathing gas, on top of depth drain
    private const float FireBurnDps = 14f;   // open flame — painful, but fire is patchy and brief

    /// <summary>Body-contact hazards from the cell sim: lava sears, acid corrodes (both bypass
    /// armor — no plate stops molten rock or acid), and gas chokes by burning air. God mode is
    /// immune. This is also the only place lava damages the dwarf at all — before hazard cells
    /// the dwarf could wade through it unharmed.</summary>
    private void TickHazardContact(float dt)
    {
        var p = _run.Player;
        if (p.FlyMode) return;

        var (lava, acid, gas, fire) = _run.Cells.SampleHazardsNear(p.Position, p.Radius + 1.5f);
        if (lava > 0)
        {
            p.Health -= LavaBurnDps * dt;
            _run.Shake = MathF.Max(_run.Shake, 0.3f);
            p.HurtFlash = MathF.Max(p.HurtFlash, 0.25f);   // the damage read, not a spark storm
            // Only the rare cinder now — the old shower of sparks was 99% cut; the hurt flash
            // and shake carry the "you're burning" signal instead.
            if (Random.Shared.NextDouble() < dt * 0.2f) _particles.EmitImpact(p.Position, ProjectileKind.Cannon);
        }
        if (acid > 0)
        {
            p.Health -= AcidBurnDps * dt;
            if (Random.Shared.NextDouble() < dt * 12f) _particles.EmitDust(p.Position, 3f);
        }
        if (gas > 0)
            p.Oxygen = MathF.Max(0f, p.Oxygen - GasChokeOxygen * dt);
        if (fire > 0)
        {
            p.Health -= FireBurnDps * dt;
            if (Random.Shared.NextDouble() < dt * 8f) _particles.EmitImpact(p.Position, ProjectileKind.Bullet);
        }
    }

    /// <summary>L at the finished pad: pour any mined fuel into the tanks, and once they're
    /// full, fire the liftoff cinematic. Needs all three stages installed and the dwarf
    /// standing at the pad. Fuel loads incrementally, so partial progress survives wandering
    /// off to mine more.</summary>
    /// <summary>Where the finished rocket currently sits: wherever it was last parked, or
    /// its build pad if it has never flown. Null until all three stages are installed.</summary>
    private Vector2? ShipAt()
        => _run.ShipStage < 3 ? null : _shipParked ? _launchShipPos : _run.PadPos;

    private bool NearShip()
        => ShipAt() is { } at && (_run.Player.Position - at).Length() <= 60f;

    private void TryLaunchShip()
    {
        if (_ascending) return;
        if (ShipAt() is not { } ship) return;
        if ((_run.Player.Position - ship).Length() > 60f) return;

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
                _particles.EmitDust(ship, 6f);
            }
        }

        if (_run.ShipFuel < FuelToLaunch)
        {
            _toast = $"FUEL {_run.ShipFuel}/{FuelToLaunch} — MINE {FuelToLaunch - _run.ShipFuel} MORE";
            _toastTimer = 2.5f;
            return;
        }

        BeginLaunch(ship);
    }

    /// <summary>Board and fire up the rocket: a gentle hop clears the ground and the stick
    /// is live immediately — <see cref="UpdateAscent"/> flies every frame from here until
    /// the player docks at the mothership (<see cref="FinishLaunch"/>) or steps back out
    /// (<see cref="ExitShip"/>).</summary>
    private void BeginLaunch(Vector2 pad)
    {
        _sfx.Play("launch", 1f);
        _launchShipPos = pad;
        _launchUp = _run.Planet.UpAt(pad);
        _ascentVel = _launchUp * 60f;
        _ascentHeading = _launchUp;
        _ascending = true;
        _toast = "ROCKET IS YOURS - A/D TURN, SPACE BURN OR GRAVITY WINS, E STEP OUT";
        _toastTimer = 3.5f;
    }

    /// <summary>E mid-flight: set the rocket down where it is and step out. The hull stays
    /// parked at this spot (E within reach boards it again) and normal play resumes with
    /// the dwarf beside it — high-altitude exits are the player's own skydive to make.</summary>
    private void ExitShip(Vector2 up)
    {
        _ascending = false;
        _shipParked = true;
        // Settle onto the ground when it's just below — a low hover parks as a landing;
        // a genuinely high exit leaves the hull floating where the player bailed.
        for (var i = 0; i < 12 && !_run.Planet.IsSolidAt(_launchShipPos - up * 5f); i++)
            _launchShipPos -= up * 2f;
        var pos = _launchShipPos + up * 6f;
        for (var i = 0; i < 60 && _run.Planet.IsSolidAt(pos); i++) pos += up * 2f;
        _run.Player.Position = pos;
        _run.Player.Velocity = Vector2.Zero;
        _camera.Zoom = _playZoom;
        _toast = "E AT THE ROCKET TO BOARD AGAIN";
        _toastTimer = 2.5f;
    }

    /// <summary>The rocket is flown by hand, Asteroids-style: A/D (or arrows) swing the
    /// nose — it points wherever the player leaves it, nothing rights it — and SPACE (or
    /// W/up) burns along it. Flight is ballistic: gravity pulls toward the planet core
    /// the whole way up, momentum carries through a coast, and cutting the jets means
    /// arcing over and falling back to the surface. The rocket can't sink into the crust
    /// or wander past the station's orbit into deep space. E sets it down and steps out
    /// (ExitShip); flying close to the mothership engages a short approach glide that
    /// completes the docking = FinishLaunch.</summary>
    private void UpdateAscent(float dt, KeyboardState keys)
    {
        var up = _run.Planet.UpAt(_launchShipPos);

        _run.MothershipAngle += Session.StationDriftRate * dt;

        if (Pressed(keys, _prevKeys, Keys.E))
        {
            ExitShip(up);
            return;
        }

        var turn = (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left) ? -1f : 0f)
                 + (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right) ? 1f : 0f);
        var thrusting = keys.IsKeyDown(Keys.Space)
                     || keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up);
        var heading = MathF.Atan2(_ascentHeading.Y, _ascentHeading.X) + turn * 2.8f * dt;
        _ascentHeading = new Vector2(MathF.Cos(heading), MathF.Sin(heading));

        // Real ballistics: the burn accelerates along the nose, gravity drags the whole
        // flight back toward the core, and coasting keeps its momentum — only a whisper
        // of drag caps runaway speed. Cut the jets and the rocket arcs over and falls
        // home; the burn has to outmuscle the planet the whole way up.
        const float thrust = 430f;
        const float gravity = 175f;
        const float maxSpeed = 300f;
        if (thrusting) _ascentVel += _ascentHeading * thrust * dt;
        _ascentVel += _run.Planet.GravityAt(_launchShipPos) * gravity * dt;
        _ascentVel *= 1f - MathF.Min(1f, 0.12f * dt);
        var speed = _ascentVel.Length();
        if (speed > maxSpeed) _ascentVel *= maxSpeed / speed;
        _launchShipPos += _ascentVel * dt;

        // The ground stays solid on the way up, same as it was on the way down: the rocket
        // rides just clear of terrain (mountainsides included) rather than sinking in.
        if (_run.Planet.IsSolidAt(_launchShipPos - up * 5f) || _run.Planet.IsSolidAt(_launchShipPos))
        {
            for (var i = 0; i < 60 && _run.Planet.IsSolidAt(_launchShipPos); i++) _launchShipPos += up * 2f;
            var sink = Vector2.Dot(_ascentVel, up);
            if (sink < 0f) _ascentVel -= up * sink;
            // Grounded: the fins bite, so a rocket that fell back down settles where it
            // landed instead of skating sideways across the crust under gravity.
            var right = new Vector2(-up.Y, up.X);
            var slide = Vector2.Dot(_ascentVel, right);
            _ascentVel -= right * (slide * MathF.Min(1f, 6f * dt));
        }

        // Ceiling just past the station's orbit — no drifting off into deep space.
        var fromCenter = _launchShipPos - _run.Planet.Center;
        var r = fromCenter.Length();
        var maxR = _run.Planet.Radius * Planet.TileSize + Session.OrbitAltitude + 120f;
        if (r > maxR)
        {
            var radial = fromCenter / r;
            _launchShipPos = _run.Planet.Center + radial * maxR;
            var outward = Vector2.Dot(_ascentVel, radial);
            if (outward > 0f) _ascentVel -= radial * outward;
        }

        // Fly near the mothership and the docking computer glides in the last stretch —
        // it also bleeds off momentum so gravity can't yank the rocket back out of the
        // approach mid-glide.
        var to = _run.StationPos - _launchShipPos;
        var d = to.Length();
        if (d < 150f)
        {
            _ascentVel *= 1f - MathF.Min(1f, 3f * dt);
            _launchShipPos += to / d * MathF.Min(200f * dt, d);
            if (d < 48f)
            {
                _ascending = false;
                _transitionFlash = 0.6f;
                FinishLaunch();
                return;
            }
        }

        _run.Player.Position = _launchShipPos + _ascentHeading * 8f;
        _run.Player.Velocity = Vector2.Zero;
        // The rover descent in reverse: altitude above the baseline surface drives the zoom,
        // easing from play scale at the pad out to descent-wide by high sky — the terrain
        // shrinks away below exactly the way it grew on the way down.
        var alt = r - (Planet.RingMin + _run.Planet.SurfaceRing) * Planet.TileSize;
        var zoomTarget = MathHelper.Lerp(_playZoom, 0.72f, MathHelper.Clamp(alt / 650f, 0f, 1f));
        _camera.Zoom = MathHelper.Lerp(_camera.Zoom, zoomTarget, MathHelper.Clamp(dt * 2.4f, 0f, 1f));
        _camera.Follow(_launchShipPos, up, dt);
        _launchUp = up;

        if (thrusting)
        {
            _particles.EmitRocketExhaust(_launchShipPos - _ascentHeading * 2f, -_ascentHeading);
            _run.Shake = MathF.Max(_run.Shake, 0.25f);
        }
        _run.Physics.Update(dt);
        _particles.Update(dt, _run.Planet, _run.Cells);
        _run.Cells.Update(dt);
        _run.RunTime += dt;
        _toastTimer -= dt;
    }

    /// <summary>Reached once the ship has flown clear: bank the escape (unlock the next world,
    /// meta bonuses), then hand the rocket to the player in space — manual flight from here,
    /// with the camera easing out from planet scale to system scale.</summary>
    private void FinishLaunch()
    {
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
            // A conquered system is left behind: reroll the campaign seed so the next run
            // gets seven freshly generated worlds, and clear the progression that named
            // the old ones (escapes, banks, unlock depth). Souls/upgrades/ship endure.
            _meta.WorldSeed = Random.Shared.Next(1, int.MaxValue);
            _meta.PlanetsEscaped.Clear();
            _meta.Bank.Clear();
            _meta.PlanetsUnlocked = 1;
            _meta.Save();
            PlanetDefs.Activate(PlanetGen.Campaign(_meta.WorldSeed));
            _space = new SpaceSim();   // rebuild the system view around the new planets
            EndRun("SYSTEM CONQUERED!\n" +
                   $"The Rift titan is slain and you flew out alive. Campaigns completed: {_meta.RunsCompleted}.\n" +
                   $"Escapes {_meta.Escapes}   Titan kills {_meta.TitansDefeated}   Deepest {_meta.DeepestDepth}   Deaths {_meta.Deaths}\n" +
                   "Souls and upgrades endure. The warp home burned your core shards -\n" +
                   "and flung you into an UNCHARTED SYSTEM: seven new worlds to pierce.\n" +
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

    /// <summary>Next-frame deadline for the manual limiter, in Stopwatch ticks.</summary>
    private long _nextFrameAt;
    /// <summary>Measured worst-recent cost of a Thread.Sleep(1), in Stopwatch ticks —
    /// starts at the nominal ~2 ms and adapts (see the limiter in EndDraw).</summary>
    private long _sleepCost = System.Diagnostics.Stopwatch.Frequency / 500;

    /// <summary>Times the platform Present (the backbuffer swap) — GPU saturation and
    /// driver sync stalls surface HERE, invisible to the Update/Draw CPU timers ("swap"
    /// phase in the DM_PERF report). Then paces the loop to 60 Hz: coarse Thread.Sleep
    /// down to the last ~3 ms, spin the remainder — the precision MonoGame's fixed-step
    /// sleep lacks (see the timestep note in the constructor).</summary>
    protected override void EndDraw()
    {
        var t0 = FramePerf.Now();
        base.EndDraw();
        FramePerf.Add("swap", t0);
        if (IsFixedTimeStep) return;   // DM_FIXEDSTEP A/B: MonoGame paces itself
        var step = System.Diagnostics.Stopwatch.Frequency / 60;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        // Re-anchor after a stall instead of racing to repay it — the dt clamp in
        // UpdateFrame already turned the stall into slow-motion.
        if (_nextFrameAt == 0 || now > _nextFrameAt + step * 3) _nextFrameAt = now;
        _nextFrameAt += step;
        while (true)
        {
            var remain = _nextFrameAt - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remain <= 0) break;
            // Sleep only while the measured cost of a Sleep(1) comfortably fits in the
            // remaining wait; spin otherwise. The cost is TRACKED, not assumed: macOS
            // timer coalescing (App Nap on an occluded window) silently stretches
            // Sleep(1) to 25+ ms, which pinned the paced loop at 24 fps — when that
            // happens the tracked cost balloons and the limiter shifts to pure spinning
            // (correct pacing, one busy core), then drifts back once sleeps recover.
            if (remain > _sleepCost * 2)
            {
                var s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                System.Threading.Thread.Sleep(1);
                var took = System.Diagnostics.Stopwatch.GetTimestamp() - s0;
                _sleepCost = Math.Max(took, _sleepCost - step / 100);
            }
            else
            {
                System.Threading.Thread.SpinWait(64);
            }
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_updatesSinceDraw > _updatesPerDrawMax) _updatesPerDrawMax = _updatesSinceDraw;
        _updatesSinceDraw = 0;
        _drawSw.Restart();
        // Everything renders into the fixed virtual-resolution scene target, then scales
        // to the real window in one letterboxed blit — resolution independence without a
        // single UI coordinate changing. DM_NORT=1 draws straight to the backbuffer
        // instead (frame-pacing diagnostic; breaks resize/fullscreen scaling).
        var noRt = Environment.GetEnvironmentVariable("DM_NORT") is { Length: > 0 };
        if (noRt)
        {
            GraphicsDevice.SetRenderTarget(null);
            _renderer.SceneTarget = null;
            DrawFrame(gameTime);
        }
        else
        {
            GraphicsDevice.SetRenderTarget(_sceneRt);
            DrawFrame(gameTime);
            var tPresent = FramePerf.Now();
            PresentScene();
            FramePerf.Add("present", tPresent);
        }
        _drawSw.Stop();
        _drawMs = _drawMs * 0.9f + (float)_drawSw.Elapsed.TotalMilliseconds * 0.1f;

        base.Draw(gameTime);
    }

    /// <summary>Blit the finished scene target to the backbuffer, aspect-fit and centred.
    /// Integer scale factors stay point-sampled so the pixel art keeps hard edges; anything
    /// fractional smooths instead of shimmering. Also refreshes the Screen mouse mapping.</summary>
    private void PresentScene()
    {
        var gd = GraphicsDevice;
        gd.SetRenderTarget(null);
        var pp = gd.PresentationParameters;
        var scale = MathF.Min(pp.BackBufferWidth / (float)VirtualWidth,
                              pp.BackBufferHeight / (float)VirtualHeight);
        var w = Math.Max(1, (int)(VirtualWidth * scale));
        var h = Math.Max(1, (int)(VirtualHeight * scale));
        var dest = new Rectangle((pp.BackBufferWidth - w) / 2, (pp.BackBufferHeight - h) / 2, w, h);
        Screen.Scale = scale;
        Screen.Offset = dest.Location;
        gd.Clear(Color.Black);
        var sampler = MathF.Abs(scale - MathF.Round(scale)) < 0.01f
            ? SamplerState.PointClamp : SamplerState.LinearClamp;
        _renderer.Batch.Begin(samplerState: sampler);
        _renderer.Batch.Draw(_sceneRt, dest, Color.White);
        _renderer.Batch.End();
    }

    /// <summary>Generate the title vista layers once. Sky (gradient, stars, moon) and
    /// land (five haze-faded ridges over the alien crust — turf lip, wavy strata, crystal
    /// clusters, glow-shrooms, ore glints; land is transparent above the ridge line) are
    /// static textures; the gas giant becomes a scrolling cylinder map + shade overlay so
    /// it can visibly rotate, and the sun is its own sprite so it can set. All drawn at 2×
    /// with point sampling.</summary>
    private void BuildTitleBackdrop(GraphicsDevice gd)
    {
        const int w = 640, h = 360;
        const int horizonY = 292;
        var rng = new Random(74921);

        // ── Sky layer ────────────────────────────────────────────────────────────────
        var sky = new Color[w * h];
        for (var y = 0; y < h; y++)
        {
            var t = MathF.Pow(Math.Clamp(y / (float)horizonY, 0f, 1f), 1.5f);
            var c = Color.Lerp(new Color(5, 6, 14), new Color(52, 34, 66), t);
            var glow = MathHelper.Clamp((y - (horizonY - 70)) / 70f, 0f, 1f);
            c = Color.Lerp(c, new Color(96, 52, 70), glow * glow * 0.8f);
            for (var x = 0; x < w; x++) sky[y * w + x] = c;
        }
        for (var i = 0; i < 380; i++)
        {
            var x = rng.Next(w);
            var y = rng.Next((int)(h * 0.62f));
            var b = 80 + rng.Next(160);
            var c = rng.Next(12) switch
            {
                0 => new Color(b, (int)(b * 0.8f), (int)(b * 0.6f)),
                1 => new Color((int)(b * 0.7f), (int)(b * 0.85f), b),
                _ => new Color(b, b, b),
            };
            sky[y * w + x] = c;
            if (b > 210 && rng.Next(3) == 0 && x > 0 && y > 0 && x < w - 1 && y < h - 1)
            {
                var dim = new Color(b / 2, b / 2, b / 2);
                sky[y * w + x - 1] = dim; sky[y * w + x + 1] = dim;
                sky[(y - 1) * w + x] = dim; sky[(y + 1) * w + x] = dim;
            }
        }
        void SkyDisc(int cx, int cy, int r, Func<int, int, float, Color?> shade)
        {
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                {
                    var d = MathF.Sqrt(dx * dx + dy * dy) / r;
                    if (d > 1f) continue;
                    var x = cx + dx; var y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    if (shade(dx, dy, d) is { } c) sky[y * w + x] = c;
                }
        }
        _titleSkyTex = new Texture2D(gd, w, h);
        _titleSkyTex.SetData(sky);

        // The moon is its own sprite now — it drifts across the sky live.
        var moon = new Color[33 * 33];
        for (var dy = -16; dy <= 16; dy++)
            for (var dx = -16; dx <= 16; dx++)
            {
                var d = MathF.Sqrt(dx * dx + dy * dy) / 16f;
                if (d > 1f) continue;
                var g = (int)(165 - d * 62);
                moon[(dy + 16) * 33 + dx + 16] = new Color(g, g, (int)(g * 1.08f));
            }
        void Crater(int cx, int cy, int r, Color c)
        {
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r && cx + dx is >= 0 and < 33 && cy + dy is >= 0 and < 33
                        && moon[(cy + dy) * 33 + cx + dx].A > 0)
                        moon[(cy + dy) * 33 + cx + dx] = c;
        }
        Crater(12, 12, 3, new Color(104, 104, 118));
        Crater(21, 20, 2, new Color(96, 96, 110));
        Crater(9, 22, 2, new Color(110, 110, 124));
        _titleMoonTex = new Texture2D(gd, 33, 33);
        _titleMoonTex.SetData(moon);

        // ── Land layer (transparent sky above the ridge line) ───────────────────────
        var px = new Color[w * h];   // starts fully transparent
        void Ridge(int baseY, int amp, Color col, Color haze, int jaggedness)
        {
            var yr = baseY + rng.Next(-amp / 2, amp / 2);
            for (var x = 0; x < w; x++)
            {
                yr += rng.Next(3) - 1;
                if (rng.Next(jaggedness) == 0) yr += rng.Next(9) - 4;
                yr = Math.Clamp(yr, baseY - amp, baseY + amp);
                for (var y = yr; y < h; y++)
                {
                    var depth = MathHelper.Clamp((y - yr) / 46f, 0f, 1f);
                    px[y * w + x] = Color.Lerp(col, haze, MathF.Pow(depth, 1.6f) * 0.55f);
                }
            }
        }
        var skyAtHorizon = new Color(96, 52, 70);
        Ridge(196, 14, Color.Lerp(new Color(58, 40, 70), skyAtHorizon, 0.55f),
            Color.Lerp(new Color(70, 44, 72), skyAtHorizon, 0.6f), 10);
        Ridge(216, 16, Color.Lerp(new Color(52, 36, 64), skyAtHorizon, 0.38f),
            Color.Lerp(new Color(64, 40, 66), skyAtHorizon, 0.45f), 8);
        // Two distant volcanoes stand behind the nearer ridges — cones stamped between
        // ridge passes so each successive ridge buries a little more of their flanks
        // (classic painted depth). Their summits poke above the skyline; the live
        // eruption plumes in DrawTitleVista anchor there.
        void Volcano(int cx, int summitY, int baseY, int halfW, Color rock, Color dark)
        {
            // A painted mountain, not a triangle: concave volcanic flanks (steep at the
            // crater, flaring into a skirt), hash-wobbled edges, occasional shoulder
            // ledges, a sun-side lit flank, and a faint warm crater rim. Colours arrive
            // pre-hazed toward the horizon so the cone melts into the ridge stack.
            var height = baseY - summitY;
            var lit = Color.Lerp(rock, skyAtHorizon, 0.28f);
            var rim = Color.Lerp(new Color(150, 84, 52), rock, 0.45f);
            for (var y = summitY; y < baseY; y++)
            {
                var f = (y - summitY) / (float)height;
                var hwF = 2f + halfW * MathF.Pow(f, 1.5f);
                var rowHash = (y * 668265263) >> 3;
                hwF += ((rowHash & 3) - 1.5f) * 0.8f;                 // ragged edge wobble
                if ((rowHash & 15) == 0) hwF += 2.2f;                 // a shoulder ledge
                var hw = Math.Max(2, (int)hwF);
                // The crater is a real bowl: a V-notch three rows deep.
                var notch = y - summitY < 3 ? 2 + (y - summitY) : 0;
                for (var x = cx - hw; x <= cx + hw; x++)
                {
                    if (x < 0 || x >= w) continue;
                    if (notch > 0 && Math.Abs(x - cx) < notch - 1) continue;
                    var hash = ((x * 73856093) ^ (y * 19349663)) & 15;
                    Color c;
                    if (notch > 0 && Math.Abs(x - cx) == notch - 1) c = rim;   // crater lip ember
                    else if (Math.Abs(x - cx) > hw - 2) c = dark;              // silhouette edge
                    else if (x < cx - hw / 3) c = hash < 3 ? rock : lit;       // sunset-lit west flank
                    else c = hash == 0 ? dark : rock;
                    px[y * w + x] = c;
                }
            }
        }
        // Volcano A: modest cone on the left, hazed hard so it sits WITH the far ridges.
        Volcano(100, 176, 226, 22,
            Color.Lerp(new Color(48, 34, 58), skyAtHorizon, 0.45f),
            Color.Lerp(new Color(36, 26, 46), skyAtHorizon, 0.38f));
        _titleVolcanoA = new Point(100, 176);
        Ridge(238, 18, Color.Lerp(new Color(44, 30, 56), skyAtHorizon, 0.22f),
            Color.Lerp(new Color(56, 36, 58), skyAtHorizon, 0.3f), 7);
        // Volcano B: smaller still, off toward the RIGHT of the screen, one ridge nearer.
        Volcano(505, 204, 252, 16,
            Color.Lerp(new Color(40, 28, 48), skyAtHorizon, 0.32f),
            Color.Lerp(new Color(28, 20, 36), skyAtHorizon, 0.26f));
        _titleVolcanoB = new Point(505, 204);
        Ridge(262, 16, new Color(34, 24, 44), new Color(46, 30, 48), 6);
        Ridge(282, 12, new Color(20, 15, 26), new Color(30, 20, 32), 6);

        _titleSurfY = new int[w];
        var sy = horizonY + 6;
        for (var x = 0; x < w; x++)
        {
            sy += rng.Next(3) - 1;
            sy = Math.Clamp(sy, horizonY + 2, horizonY + 12);
            _titleSurfY[x] = sy;
        }
        for (var x = 0; x < w; x++)
        {
            for (var y = _titleSurfY[x]; y < h; y++)
            {
                var depth = y - _titleSurfY[x];
                Color c;
                if (depth < 3)
                {
                    c = depth == 0 ? new Color(96, 200, 172) : new Color(52, 128, 116);
                }
                else
                {
                    var wave = MathF.Sin(x * 0.021f + depth * 0.05f) * 5f;
                    var band = (int)((depth + wave) / 11f) % 4;
                    c = band switch
                    {
                        0 => new Color(74, 56, 84),
                        1 => new Color(58, 62, 88),
                        2 => new Color(88, 62, 58),
                        _ => new Color(48, 42, 62),
                    };
                    var hash = ((x * 73856093) ^ (y * 19349663)) & 1023;
                    var j = (hash & 15) - 8;
                    c = new Color(
                        Math.Clamp(c.R + j, 0, 255),
                        Math.Clamp(c.G + j, 0, 255),
                        Math.Clamp(c.B + j, 0, 255));
                }
                px[y * w + x] = c;
            }
            if (rng.Next(5) == 0 && _titleSurfY[x] > 2)
            {
                var th = 1 + rng.Next(3);
                for (var k = 1; k <= th; k++)
                    px[(_titleSurfY[x] - k) * w + x] = new Color(96, 200, 172);
            }
        }
        // Glow-shrooms dotting the surface.
        for (var i = 0; i < 9; i++)
        {
            var x = 8 + rng.Next(w - 16);
            var baseYy = _titleSurfY[x];
            var stalk = 2 + rng.Next(2);
            for (var k = 1; k <= stalk; k++) px[(baseYy - k) * w + x] = new Color(150, 235, 160);
            var capY = baseYy - stalk - 1;
            for (var ox = -1; ox <= 1; ox++) px[capY * w + x + ox] = new Color(120, 255, 140);
            px[(capY - 1) * w + x] = new Color(200, 255, 200);
        }
        // Crystal clusters growing out of the rock.
        for (var i = 0; i < 10; i++)
        {
            var x = 4 + rng.Next(w - 8);
            var y = horizonY + 20 + rng.Next(h - horizonY - 30);
            var cyan = rng.Next(2) == 0;
            var body = cyan ? new Color(90, 200, 220) : new Color(200, 110, 220);
            var core = cyan ? new Color(190, 245, 255) : new Color(250, 200, 255);
            var tall = 2 + rng.Next(3);
            for (var k = 0; k <= tall; k++)
            {
                px[(y - k) * w + x] = body;
                if (k < tall) px[(y - k) * w + x + (k % 2 == 0 ? 1 : -1)] = body;
            }
            px[(y - tall) * w + x] = core;
        }
        // Ore glints through the strata.
        var glints = new[]
        {
            new Color(255, 205, 90), new Color(255, 110, 90),
            new Color(140, 190, 255), new Color(235, 250, 255), new Color(120, 230, 130),
        };
        for (var i = 0; i < 60; i++)
        {
            var x = rng.Next(w); var y = horizonY + 14 + rng.Next(h - horizonY - 20);
            px[y * w + x] = glints[rng.Next(glints.Length)];
            if (rng.Next(3) == 0 && x + 1 < w) px[y * w + x + 1] = glints[rng.Next(glints.Length)];
        }
        _titleLandTex = new Texture2D(gd, w, h);
        _titleLandTex.SetData(px);

        // ── Gas-giant cylinder map: pixel-art yellow/tan bands with dithered boundaries
        // and a pale storm oval. Built at DOUBLE resolution (period 320, 108 tall) and drawn
        // at 2× so the pixels match the sun/moon's finer grain rather than the old chunky 4×.
        // The last 108 columns repeat the first so a scrolling window never wraps mid-draw.
        // NO shading/shadows — flat, evenly-lit bands. ──
        const int mapPeriod = 320, mapW = mapPeriod + 108, mapH = 108;
        var map = new Color[mapW * mapH];
        var bandCols = new[]
        {
            new Color(206, 148, 100), new Color(158, 102, 82),
            new Color(224, 176, 128), new Color(172, 118, 88),
        };
        for (var y = 0; y < mapH; y++)
        {
            // Clean solid bands — no dithered/jagged edge between colours, just a straight
            // colour change at each band boundary.
            var band = (int)(y / 13.5f);
            var c = bandCols[band % bandCols.Length];
            for (var x = 0; x < mapPeriod; x++)
                map[y * mapW + x] = c;
        }
        // Storm oval — a pale swirl in the southern band, solid (no dithered rim).
        for (var dy = -6; dy <= 6; dy++)
            for (var dx = -10; dx <= 10; dx++)
            {
                var dd = dx * dx / 100f + dy * dy / 36f;
                if (dd > 1f) continue;
                var x = (70 + dx + mapPeriod) % mapPeriod;
                var y = 70 + dy;
                map[y * mapW + x] = dd < 0.4f ? new Color(238, 208, 170) : new Color(216, 168, 128);
            }
        // Copy the leading 108 columns to the tail for wrap-free windows.
        for (var y = 0; y < mapH; y++)
            for (var x = 0; x < 108; x++)
                map[y * mapW + mapPeriod + x] = map[y * mapW + x];
        _titlePlanetMap = new Texture2D(gd, mapW, mapH);
        _titlePlanetMap.SetData(map);

        // Ring system: a flat ellipse annulus, SOLID now — two clean bands split by a dark
        // gap, no grain speckle and no dithered edge. At double resolution, drawn in two
        // halves around the tilted planet: far side behind, near side in front.
        const int ringW = 172, ringH = 48;
        var ring = new Color[ringW * ringH];
        for (var y = 0; y < ringH; y++)
            for (var x = 0; x < ringW; x++)
            {
                var nx = (x - ringW / 2f) / 80f;
                var ny = (y - ringH / 2f) / 21f;
                var rr = MathF.Sqrt(nx * nx + ny * ny);
                if (rr is < 0.64f or > 1f) continue;
                if (rr is > 0.82f and < 0.86f) continue;             // the dark division
                var bright = rr < 0.82f ? 0.9f : 0.7f;               // inner band brighter
                ring[y * ringW + x] = new Color(
                    (int)(214 * bright), (int)(188 * bright), (int)(148 * bright), 255);
            }
        _titleRingTex = new Texture2D(gd, ringW, ringH);
        _titleRingTex.SetData(ring);

        // Sun sprite: white-hot core through orange to a soft halo.
        const int sunR = 22;
        var sun = new Color[sunR * 2 * sunR * 2];
        for (var dy = -sunR; dy < sunR; dy++)
            for (var dx = -sunR; dx < sunR; dx++)
            {
                var d = MathF.Sqrt(dx * dx + dy * dy) / sunR;
                if (d > 1f) continue;
                var idx = (dy + sunR) * sunR * 2 + dx + sunR;
                sun[idx] = d < 0.34f ? new Color(255, 244, 214)
                    : d < 0.5f ? new Color(255, 208, 130)
                    : Color.Lerp(new Color(255, 150, 70), Color.Transparent, (d - 0.5f) / 0.5f);
            }
        _titleSunTex = new Texture2D(gd, sunR * 2, sunR * 2);
        _titleSunTex.SetData(sun);
    }

    /// <summary>The living parts of the vista, drawn between sky and land layers or over
    /// the crust: the slowly setting sun, the rotating gas giant (row-sliced scrolling
    /// cylinder under a static terminator), and a handful of ambient critters — grazers
    /// and a hopper on the turf, moths in the dusk sky.</summary>
    private void DrawTitleVista()
    {
        var sb = _renderer.Batch;
        var time = _renderer.Time;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_titleSkyTex, new Rectangle(0, 0, VirtualWidth, VirtualHeight), Color.White);
        // Twinkling stars — a good scatter of them.
        for (var i = 0; i < 26; i++)
        {
            var hsh = (uint)(i * 2654435761);
            var tx = (int)(hsh % 640) * 2;
            var ty = (int)((hsh >> 10) % 200) * 2;
            var tw = 0.5f + 0.5f * MathF.Sin(time * (1.2f + i * 0.37f) + i * 2.1f);
            sb.Draw(_renderer.Pixel, new Rectangle(tx, ty, 2, 2), Color.White * (0.25f + 0.55f * tw));
        }
        // Shooting stars: brief bright streaks on independent clocks, each with a fading
        // tail, at a hashed spot per appearance.
        for (var i = 0; i < 2; i++)
        {
            var period = 7.5f + i * 4.3f;
            var tt = (time + i * 3.7f) % period;
            if (tt >= 0.7f) continue;
            var p2 = tt / 0.7f;
            var seed = (uint)((int)((time + i * 3.7f) / period) * 2654435761 + i * 97);
            var sx = seed % 480 + 40 + p2 * 130f;
            var syy = (seed >> 9) % 130 + 8 + p2 * 62f;
            for (var k = 0; k < 7; k++)
                sb.Draw(_renderer.Pixel,
                    new Rectangle((int)(sx - k * 2.4f) * 2, (int)(syy - k * 1.1f) * 2, 2, 2),
                    Color.White * ((1f - p2) * (1f - k / 7f)));
        }
        // The moon drifts slowly across the upper sky (wraps after it leaves the frame).
        var moonX = (128f + time * 0.7f) % 720f - 40f;
        var moonY = 66f - 26f * MathF.Sin(MathHelper.Clamp(moonX / 640f, 0f, 1f) * MathF.PI);
        sb.Draw(_titleMoonTex, new Rectangle((int)(moonX - 16) * 2, (int)(moonY - 16) * 2, 66, 66),
            Color.White);
        // The sun: a slow arc that sinks behind the ridge line and rises again (~150 s
        // cycle). Land is drawn after, so the set happens BEHIND the mountains.
        var setP = 0.5f + 0.5f * MathF.Sin(time * MathHelper.TwoPi / 150f - MathHelper.PiOver2);
        var sunY = MathHelper.Lerp(140f, 310f, setP);
        var sunX = 70f + MathF.Sin(time * MathHelper.TwoPi / 150f) * 22f;   // clear of the slot cards
        sb.Draw(_titleSunTex, new Rectangle((int)(sunX - 22) * 2, (int)(sunY - 22) * 2, 88, 88),
            Color.White);
        // Low sun floods the horizon warm.
        var lowGlow = MathHelper.Clamp((sunY - 230f) / 80f, 0f, 1f) * 0.35f;
        if (lowGlow > 0.01f)
            sb.Draw(_renderer.Pixel, new Rectangle(0, 440, VirtualWidth, 160),
                new Color(255, 140, 70) * lowGlow);

        // Rotating gas giant, tilted 30°: each disc row samples a scrolling window of the
        // cylinder map (period 320) — bands and storm drift across the face — and every row
        // is drawn rotated about the planet's centre (origin trick), so the whole assembly
        // leans without losing the row-scroll rotation. Built at double resolution and drawn
        // at 2× (matching the sun/moon's finer pixels — no more chunky 4×). Its ring system
        // splits around it: far half behind the disc, near half in front, drawn 30% larger
        // than the disc. No shade overlay (shading/shadows removed — flat bands).
        const int pcx = 504, pcy = 84, pr = 54;   // full-res disc drawn at 2× = ~same size, finer pixels
        var tilt = MathHelper.ToRadians(30f);
        const float ringScale = 2f * 1.3f;         // rings 30% bigger than the disc's 2×
        var planetCentre = new Vector2(pcx * 2, pcy * 2);
        sb.Draw(_titleRingTex, planetCentre, new Rectangle(0, 0, 172, 24), Color.White,
            tilt, new Vector2(86f, 24f), ringScale, SpriteEffects.None, 0f);
        var scroll = time * 5f;
        for (var row = 0; row < pr * 2; row++)
        {
            var dy = row - pr;
            var halfSq = pr * pr - dy * dy;
            if (halfSq <= 0) continue;
            var half = MathF.Sqrt(halfSq);
            var rowW = Math.Max(1, (int)(half * 2f));
            var srcX = (int)(scroll % 320);
            sb.Draw(_titlePlanetMap, planetCentre,
                new Rectangle(srcX, row, rowW, 1), Color.White,
                tilt, new Vector2(rowW / 2f, pr - row), 2f, SpriteEffects.None, 0f);
        }
        sb.Draw(_titleRingTex, planetCentre, new Rectangle(0, 24, 172, 24), Color.White,
            tilt, new Vector2(86f, 0f), ringScale, SpriteEffects.None, 0f);

        // The titans: every so often a colossal silhouette rises past the far skyline and
        // lumbers off — drawn BEFORE the land layer, so only the head and shoulders show
        // above the ridge line, a shadow on the horizon. TWO different walkers on their own
        // clocks (different periods, so they drift in and out of sync): a spiny long-tailed
        // kaiju crossing right→left, and a broad hunched ape-biped crossing left→right.
        var dark = new Color(9, 7, 13) * 0.92f;
        void TBlock(float x, float y, int wPx, int hPx) =>
            sb.Draw(_renderer.Pixel, new Rectangle((int)x * 2, (int)y * 2, wPx * 2, hPx * 2), dark);
        {
            var cyc = (time + 20f) % 47f;
            if (cyc < 13f)
            {
                var tp = cyc / 13f;
                var tx2 = MathHelper.Lerp(700f, -80f, tp);
                var bob = MathF.Sin(time * 2.2f) * 1.4f;
                var ty2 = 196f + bob;
                TBlock(tx2 - 14, ty2 - 22, 28, 40);           // torso (lower half hides behind land)
                TBlock(tx2 - 22, ty2 - 34, 10, 9);            // head, leading the walk
                TBlock(tx2 - 18, ty2 - 27, 8, 6);             // jaw/neck
                for (var sp = 0; sp < 3; sp++)                // back spines
                    TBlock(tx2 - 4 + sp * 7, ty2 - 28 - sp * 2, 4, 7 + sp * 2);
                TBlock(tx2 + 14, ty2 - 12, 12, 6);            // tail root
            }
        }
        {
            // Second walker: a broad, hunched ape-biped (no tail/spines) plodding the other
            // way — heavy shoulders, a domed brow, and long arms swinging by its sides.
            var cyc = (time + 6f) % 61f;
            if (cyc < 15f)
            {
                var tp = cyc / 15f;
                var tx3 = MathHelper.Lerp(-90f, 760f, tp);
                var sway = MathF.Sin(time * 1.7f) * 1.6f;
                var ty3 = 198f + sway;
                var arm = MathF.Sin(time * 1.7f) * 4f;        // arms swing with the gait
                TBlock(tx3 - 16, ty3 - 22, 34, 42);           // hunched torso
                TBlock(tx3 - 22, ty3 - 24, 46, 9);            // broad shoulders
                TBlock(tx3 + 2, ty3 - 35, 15, 12);            // domed head, leading the walk
                TBlock(tx3 + 1, ty3 - 37, 18, 4);             // heavy brow ridge
                TBlock(tx3 - 25, ty3 - 16 + arm, 8, 28);      // trailing arm
                TBlock(tx3 + 19, ty3 - 16 - arm, 8, 28);      // leading arm
            }
        }

        // Land over the sky/sun/planet (and over the titan's legs).
        sb.Draw(_titleLandTex, new Rectangle(0, 0, VirtualWidth, VirtualHeight), Color.White);

        // Volcano eruptions: slow, occasional, far away. Each summit runs its own clock;
        // during a window the crater glows and a column of embers-then-ash climbs a few
        // pixels a second and drifts.
        void Erupt(Point summit, float period, float phase)
        {
            var cyc = (time + phase) % period;
            if (cyc >= 11f) return;
            var swell = MathF.Sin(cyc / 11f * MathF.PI);
            sb.Draw(_renderer.Pixel,
                new Rectangle((summit.X - 2) * 2, summit.Y * 2, 8, 4),
                new Color(255, 140, 50) * (0.55f * swell));
            for (var k = 0; k < 9; k++)
            {
                var age = cyc - k * 1.05f;
                if (age is < 0f or > 7f) continue;
                var hsh = (uint)((k * 73856093) ^ ((int)((time + phase) / period) * 19349663));
                var drift = ((int)(hsh % 9) - 4) * 0.32f;
                var bx2 = summit.X + drift * age;
                var by2 = summit.Y - age * (2.6f + hsh % 3);
                var hot = age < 1.8f;
                var size = 1 + (int)(age * 0.7f);
                var col = hot
                    ? new Color(255, 150, 60) * 0.95f
                    : Color.Lerp(new Color(120, 95, 90), new Color(70, 60, 72), age / 7f) * (1f - age / 8f);
                sb.Draw(_renderer.Pixel,
                    new Rectangle((int)(bx2 - size / 2f) * 2, (int)by2 * 2, size * 2, size * 2), col);
            }
        }
        Erupt(_titleVolcanoA, 34f, 6f);
        Erupt(_titleVolcanoB, 47f, 21f);

        // Ambient critters (all in 640-space, drawn as 2-px blocks). Grazers amble the
        // turf, a hopper bounces, moths stitch figure-eights through the dusk.
        void Block(float x, float y, int wPx, int hPx, Color c) =>
            sb.Draw(_renderer.Pixel, new Rectangle((int)x * 2, (int)y * 2, wPx * 2, hPx * 2), c);
        for (var i = 0; i < 3; i++)
        {
            var speed = 4.5f + i * 2.1f;
            var dir = i % 2 == 0 ? 1f : -1f;
            var gx = ((i * 211f + time * speed * dir) % 640f + 640f) % 640f;
            var xi = Math.Clamp((int)gx, 0, 639);
            var gy = _titleSurfY[xi] - 2;
            var bob = MathF.Sin(time * 5f + i * 2f) > 0f ? 0 : 1;
            var body = new Color(186, 158, 118);
            Block(gx - 1, gy - 1, 3, 2, body);                       // body
            Block(gx + (dir > 0 ? 2 : -2), gy - 2, 1, 2, body);      // head, grazing dip
            Block(gx - 1, gy + 1 - bob, 1, 1, new Color(120, 98, 70));   // legs shuffle
            Block(gx + 1, gy + bob, 1, 1, new Color(120, 98, 70));
        }
        {
            var hx = ((time * 16f) % 700f) - 30f;
            if (hx is > 0f and < 640f)
            {
                var xi = Math.Clamp((int)hx, 0, 639);
                var hop = MathF.Abs(MathF.Sin(time * 4.2f)) * 6f;
                var hy = _titleSurfY[xi] - 2 - hop;
                Block(hx, hy - 1, 2, 2, new Color(126, 196, 120));
                Block(hx + 1, hy - 2, 1, 1, new Color(126, 196, 120));
            }
        }
        for (var i = 0; i < 2; i++)
        {
            var mx = ((i * 320f + time * (9f + i * 3f)) % 640f);
            var my = 150f + MathF.Sin(time * 0.8f + i * 2.6f) * 26f + MathF.Sin(time * 3.1f + i) * 5f;
            var flap = (int)(time * 9f + i) % 2 == 0;
            var moth = new Color(196, 186, 210);
            Block(mx, my, 1, 1, moth);
            Block(mx - 1, my - (flap ? 1 : 0), 1, 1, moth * 0.8f);
            Block(mx + 1, my - (flap ? 0 : 1), 1, 1, moth * 0.8f);
        }
        sb.End();
    }

    /// <summary>The title screen: game name over three save-slot cards — empty slots offer
    /// NEW GAME, played ones summarise the campaign and flag a suspended run.</summary>
    private void DrawTitle()
    {
        GraphicsDevice.Clear(new Color(7, 9, 18));
        var sb = _renderer.Batch;

        DrawTitleVista();

        const string title = "DWARF MINER";
        // Drop shadow keeps the wordmark crisp over the vista.
        _renderer.DrawText(title,
            new Vector2((VirtualWidth - _renderer.MeasureText(title, 5)) / 2f + 4, 114),
            new Color(20, 12, 8), 5);
        _renderer.DrawText(title,
            new Vector2((VirtualWidth - _renderer.MeasureText(title, 5)) / 2f, 110),
            new Color(235, 205, 120), 5);

        const int cardW = 460, cardH = 64, gap = 14;
        var x0 = (VirtualWidth - cardW) / 2;
        var y0 = 250;
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < SaveSlots.Count; i++)
        {
            var y = y0 + i * (cardH + gap);
            _titleCardRects[i] = new Rectangle(x0, y, cardW, cardH);
            var hot = i == _titleCursor;
            sb.Draw(_renderer.Pixel, new Rectangle(x0, y, cardW, cardH),
                hot ? new Color(45, 52, 76, 240) : new Color(20, 23, 34, 220));
            var border = hot ? new Color(255, 220, 120) : new Color(95, 100, 118);
            sb.Draw(_renderer.Pixel, new Rectangle(x0, y, cardW, 2), border);
            sb.Draw(_renderer.Pixel, new Rectangle(x0, y + cardH - 2, cardW, 2), border);
            sb.Draw(_renderer.Pixel, new Rectangle(x0, y, 2, cardH), border);
            sb.Draw(_renderer.Pixel, new Rectangle(x0 + cardW - 2, y, 2, cardH), border);
        }
        sb.End();

        for (var i = 0; i < SaveSlots.Count; i++)
        {
            var y = y0 + i * (cardH + gap);
            var (meta, hasRun) = _slotInfo[i];
            var hot = i == _titleCursor;
            _renderer.DrawText($"SLOT {i + 1}", new Vector2(x0 + 16, y + 10),
                hot ? Color.White : new Color(170, 175, 190), 2);
            if (meta is null)
            {
                _renderer.DrawText("NEW GAME", new Vector2(x0 + 16, y + 38),
                    new Color(140, 210, 150));
            }
            else
            {
                var line = $"CONTINUE - {meta.Escapes} ESCAPES, {meta.TitansDefeated} TITANS, {meta.TotalSouls()} SOULS";
                _renderer.DrawText(line, new Vector2(x0 + 16, y + 38), new Color(190, 200, 220));
                if (hasRun)
                    _renderer.DrawText("RUN IN PROGRESS",
                        new Vector2(x0 + cardW - 16 - _renderer.MeasureText("RUN IN PROGRESS"), y + 10),
                        new Color(255, 220, 120));
            }
        }

        var hint = "CLICK A SLOT OR UP/DOWN + ENTER   ESC QUIT";
        _renderer.DrawText(hint,
            new Vector2((VirtualWidth - _renderer.MeasureText(hint)) / 2f, y0 + 3 * (cardH + gap) + 24),
            new Color(150, 165, 200));

        // Quit confirmation dialog — Esc opened it; nothing leaves without a yes.
        if (_titleQuitConfirm)
        {
            const int dw = 300, dh = 110;
            var dx = (VirtualWidth - dw) / 2;
            var dy = (VirtualHeight - dh) / 2;
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 140));
            sb.Draw(_renderer.Pixel, new Rectangle(dx, dy, dw, dh), new Color(18, 21, 32, 250));
            sb.Draw(_renderer.Pixel, new Rectangle(dx, dy, dw, 2), new Color(255, 220, 120));
            sb.Draw(_renderer.Pixel, new Rectangle(dx, dy + dh - 2, dw, 2), new Color(255, 220, 120));
            _titleConfirmYes = new Rectangle(dx + 28, dy + 62, 100, 26);
            _titleConfirmNo = new Rectangle(dx + dw - 128, dy + 62, 100, 26);
            var mouse = Screen.Mouse();
            foreach (var (rect, hot) in new[]
            {
                (_titleConfirmYes, _titleConfirmYes.Contains(mouse.X, mouse.Y)),
                (_titleConfirmNo, _titleConfirmNo.Contains(mouse.X, mouse.Y)),
            })
                sb.Draw(_renderer.Pixel, rect, hot ? new Color(70, 78, 108) : new Color(34, 38, 54));
            sb.End();
            var q = "QUIT THE GAME?";
            _renderer.DrawText(q, new Vector2((VirtualWidth - _renderer.MeasureText(q, 2)) / 2f, dy + 18),
                new Color(255, 220, 150), 2);
            _renderer.DrawText("YES",
                new Vector2(_titleConfirmYes.Center.X - _renderer.MeasureText("YES") / 2f, dy + 68), Color.White);
            _renderer.DrawText("NO (ESC)",
                new Vector2(_titleConfirmNo.Center.X - _renderer.MeasureText("NO (ESC)") / 2f, dy + 68), Color.White);
        }
    }

    /// <summary>The pause overlay (Esc): dimmed frozen frame with resume / return to the
    /// title / quit. Drawn atop both the space screen and live runs.</summary>
    private void DrawPauseMenu()
    {
        if (!_pauseOpen) return;
        var sb = _renderer.Batch;
        const int boxW = 300, boxH = 150;
        var bx = (VirtualWidth - boxW) / 2;
        var by = (VirtualHeight - boxH) / 2;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 150));
        sb.Draw(_renderer.Pixel, new Rectangle(bx, by, boxW, boxH), new Color(18, 21, 32, 245));
        sb.Draw(_renderer.Pixel, new Rectangle(bx, by, boxW, 2), new Color(255, 220, 120));
        sb.Draw(_renderer.Pixel, new Rectangle(bx, by + boxH - 2, boxW, 2), new Color(255, 220, 120));
        sb.End();
        _renderer.DrawText("PAUSED", new Vector2((VirtualWidth - _renderer.MeasureText("PAUSED", 2)) / 2f, by + 14),
            new Color(255, 220, 120), 2);
        var options = new[] { "RESUME", "RETURN TO MENU", "QUIT GAME" };
        for (var i = 0; i < options.Length; i++)
        {
            var hot = i == _pauseCursor;
            var label = hot ? "> " + options[i] : options[i];
            var oy = by + 52 + i * 26;
            // Generous hit band the full box width — mouse hover/click drives the cursor.
            _pauseOptionRects[i] = new Rectangle(bx, oy - 5, boxW, 24);
            _renderer.DrawText(label,
                new Vector2((VirtualWidth - _renderer.MeasureText(label)) / 2f, oy),
                hot ? Color.White : new Color(170, 175, 190));
        }
    }

    /// <summary>The boot load screen: title, a progress bar fed by the survey warm-up, and
    /// the name of the world currently generating. Short by design — it exists to soak up
    /// the per-planet world generation that otherwise stutters the first seconds of play.</summary>
    private void DrawLoading()
    {
        GraphicsDevice.Clear(new Color(7, 9, 18));

        const string title = "DWARF MINER";
        _renderer.DrawText(title,
            new Vector2((VirtualWidth - _renderer.MeasureText(title, 5)) / 2f, VirtualHeight / 2f - 110),
            new Color(235, 205, 120), 5);

        var done = _warmDone;
        var total = Math.Max(1, _warmTotal);
        var frac = MathHelper.Clamp(done / (float)total, 0f, 1f);
        const int barW = 420, barH = 10;
        var bx = (VirtualWidth - barW) / 2;
        var by = VirtualHeight / 2 + 14;
        var sb = _renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(bx - 2, by - 2, barW + 4, barH + 4), new Color(58, 62, 88));
        sb.Draw(_renderer.Pixel, new Rectangle(bx, by, barW, barH), new Color(14, 17, 28));
        sb.Draw(_renderer.Pixel, new Rectangle(bx, by, (int)(barW * frac), barH), new Color(160, 205, 125));
        sb.End();

        var dots = new string('.', 1 + (int)(_totalTime * 2.5f) % 3);
        var label = _warmName is { } nm && done < total
            ? $"SURVEYING {nm.ToUpperInvariant()}{dots}"
            : $"CHARTING SYSTEM{dots}";
        _renderer.DrawText(label,
            new Vector2((VirtualWidth - _renderer.MeasureText(label)) / 2f, by + 24),
            new Color(150, 165, 200));
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
            // DM_FPSLOG=1 → per-second console FPS line, for headless perf comparisons.
            // updx/slow expose the fixed-step catch-up state (see _updatesSinceDraw).
            if (Environment.GetEnvironmentVariable("DM_FPSLOG") is { Length: > 0 })
                Console.WriteLine($"[fps] {_fps}  upd {_updateMs:0.0}  drw {_drawMs:0.0}" +
                    $"  updx {_updatesPerDrawMax}{(_runningSlowly ? " SLOW" : "")}");
            _updatesPerDrawMax = 0;
            _runningSlowly = false;
            // DM_PERF=1 → companion per-phase attribution line (mean/worst ms per system),
            // plus population counts and GC activity — a slow-decaying active-cell count or
            // a gen2 collection lining up with a worst-frame spike is the usual smoking gun.
            FramePerf.Report();
            if (FramePerf.On && _run is not null)
            {
                var g0 = GC.CollectionCount(0);
                var g2 = GC.CollectionCount(2);
                Console.WriteLine($"[cnt] active {_run.Cells.ActiveCellCount}  fly {_run.Cells.FlyingCellCount}" +
                    $"  parts {_particles.Count}  crit {_run.Creatures.Count}  gc0 {g0 - _gc0}  gc2 {g2 - _gc2}" +
                    $"  [{_run.Cells.ActiveBreakdown()}]");
                _gc0 = g0; _gc2 = g2;
            }
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

        if (_screen == GameScreen.Title)
        {
            DrawTitle();
            if (_screenshotPending)
            {
                _screenshotPending = false;
                SaveScreenshot();
            }
            return;
        }

        if (_screen == GameScreen.Loading)
        {
            DrawLoading();
            if (_screenshotPending)
            {
                _screenshotPending = false;
                SaveScreenshot();
            }
            return;
        }

        if (_screen == GameScreen.Space)
        {
            DrawSpace();
            DrawPauseMenu();
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
        _renderer.TreeBiome = _run.Def.Biome;   // trees paint in this world's own palette
        _renderer.WorldCells = _run.Cells;      // damp-ground probe (water on a tile face)

        // Engage the pixel-grid world target (see _worldRt) when the zoom sits exactly on
        // an even step: the world renders at camera zoom 2 (one texel per half world px)
        // into a target 1/k the scene size, and the leftover ×k magnification happens in
        // one point-sampled blit after EndEntities. DM_NORT (direct-to-backbuffer
        // diagnostic) and non-step zooms (landing/orbit cinematics) skip it.
        _pixelK = 0;
        var pxK = (int)MathF.Round(_camera.Zoom * 0.5f);
        if (pxK >= 1 && MathF.Abs(_camera.Zoom - pxK * 2f) < 0.01f
            && Environment.GetEnvironmentVariable("DM_PIXELRT") != "0"
            && Environment.GetEnvironmentVariable("DM_NORT") is not { Length: > 0 })
        {
            _pixelK = pxK;
            var rw = (VirtualWidth + pxK - 1) / pxK;
            var rh = (VirtualHeight + pxK - 1) / pxK;
            if (_worldRt == null || _worldRt.Width != rw || _worldRt.Height != rh)
            {
                _worldRt?.Dispose();
                _worldRt = new RenderTarget2D(GraphicsDevice, rw, rh, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
            _camera.ViewportSize = new Point(rw, rh);
            _camera.Zoom = 2f;
        }

        // View circle for the culled cell passes: centre + a radius that covers the screen
        // corners at any camera rotation. The cell grid is far too fine to draw planet-wide.
        // Uses the camera's own viewport, which is the low-res target when the pixel-grid
        // path is engaged.
        var viewCentre = _camera.ScreenToWorld(new Vector2(_camera.ViewportSize.X / 2f, _camera.ViewportSize.Y / 2f));
        var viewRadius = (viewCentre - _camera.ScreenToWorld(Vector2.Zero)).Length() + Planet.TileSize * 2f;

        // Cells LOD: zoomed-out views (orbit/descent/landing) sample the cell grid at a
        // stride — the full scan is the single biggest cost at wide view radii. Stride is
        // derived from SCREEN px per cell so it tracks Density. The pixel-grid path always
        // runs stride 1 (one cell per target texel is the whole point); the DIRECT path —
        // every fractional zoom, i.e. the entire mothership-drop descent — strides as soon
        // as cells shrink below ~1.6 screen px, because mid-descent zooms with stride 1
        // scanned over a million candidates a frame (the pod-drop lag).
        var cellPx = _camera.Zoom * ((float)Planet.TileSize / Cells.Density);
        var cellStride = _pixelK > 0 || cellPx >= 1.6f
            ? 1 : Math.Min(10, (int)MathF.Ceiling(2.2f / cellPx));

        // Liquid RT pass (DM_LIQRT=0 reverts to in-batch liquid quads): water/acid/oil
        // rasterize into their own transparent target, which then composites over the
        // world in ONE alpha blend — see Renderer.CompositeLiquids. Filled FIRST, before
        // the world target engages, so no mid-frame target switch can discard scene
        // content. Close-up LOD only; the strided zoomed-out passes keep liquids in the
        // main cell draw where the grain is sub-pixel anyway.
        var liquidPass = cellStride == 1
            && Environment.GetEnvironmentVariable("DM_LIQRT") != "0"
            && Environment.GetEnvironmentVariable("DM_NORT") is not { Length: > 0 };
        var tDraw = FramePerf.Now();
        if (liquidPass)
        {
            var (lw, lh) = (_camera.ViewportSize.X, _camera.ViewportSize.Y);
            if (_liquidRt == null || _liquidRt.Width != lw || _liquidRt.Height != lh)
            {
                _liquidRt?.Dispose();
                _liquidRt = new RenderTarget2D(GraphicsDevice, lw, lh, false,
                    SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
            }
            GraphicsDevice.SetRenderTarget(_liquidRt);
            GraphicsDevice.Clear(Color.Transparent);
            // Shader mode fills soft coverage blobs (linear-sampled, colour replaces /
            // alpha accumulates); fallback fills hard quads with material alpha in the A
            // channel (opaque replace). Either way nothing here blends against the scene.
            var blobMode = _renderer.LiquidShaderOn;
            _renderer.Batch.Begin(SpriteSortMode.Deferred,
                blobMode ? Renderer.LiquidFillBlend : BlendState.Opaque,
                blobMode ? SamplerState.LinearClamp : SamplerState.PointClamp,
                null, null, null, _camera.View);
            _run.Cells.DrawLiquids(_renderer, viewCentre, viewRadius, blobMode);
            // Acid-spewer grains join the SAME coverage field: the stream and the pool
            // it feeds threshold into one connected body — the spray genuinely merges
            // into what it lands in.
            if (blobMode) _particles.DrawFluid(_renderer, Material.Acid);
            _renderer.Batch.End();

            // Flame stream in its OWN coverage field: fire must never metaball-fuse with
            // water/acid bodies it crosses. Same fill, same composite shader, fire inks.
            if (blobMode)
            {
                if (_flameRt == null || _flameRt.Width != lw || _flameRt.Height != lh)
                {
                    _flameRt?.Dispose();
                    _flameRt = new RenderTarget2D(GraphicsDevice, lw, lh, false,
                        SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
                }
                GraphicsDevice.SetRenderTarget(_flameRt);
                GraphicsDevice.Clear(Color.Transparent);
                _renderer.Batch.Begin(SpriteSortMode.Deferred, Renderer.LiquidFillBlend,
                    SamplerState.LinearClamp, null, null, null, _camera.View);
                _particles.DrawFluid(_renderer, Material.Fire);
                _renderer.Batch.End();
            }
        }
        FramePerf.Add("liqRT", tDraw);
        // Fluid-mode flag for the particle draw: while the metaball composite is live,
        // hose grains render as the fluid body above and skip their strand quads.
        _particles.FluidMode = liquidPass && _renderer.LiquidShaderOn;
        if (_pixelK > 0) GraphicsDevice.SetRenderTarget(_worldRt);
        else if (liquidPass) GraphicsDevice.SetRenderTarget(_sceneRt);

        tDraw = FramePerf.Now();
        _renderer.DrawWorld(_run.Planet, _camera);
        FramePerf.Add("world", tDraw);

        // Liquids sit above the terrain (and its crust) but below every entity — same
        // layer they occupied when they drew inside the cell batch. The flame stream
        // composites through the same metaball shader, right above the liquids.
        if (liquidPass) _renderer.CompositeLiquids(_liquidRt!);
        // Flame body: full opacity + a hot bright rim — the flame's sheath, not a pool's
        // translucent wet lip (which made the tongue read as glowing liquid).
        if (_particles.FluidMode)
            _renderer.CompositeLiquids(_flameRt!, 0.40f, 1f, 1.85f, 0.15f);

        _renderer.BeginEntities(_camera);

        // Cells (sand/lava/smoke — liquids too when the RT pass is off) draw above tiles
        // but below entities so the dwarf walks in front of his own debris pile.
        tDraw = FramePerf.Now();
        _run.Cells.Draw(_renderer, viewCentre, viewRadius, cellStride, skipLiquids: liquidPass);
        FramePerf.Add("cellsD", tDraw);
        tDraw = FramePerf.Now();

        // Pixel-art dwarf sprite — drawn rotated to align local-up with planet's outward radial.
        // Sprite head-at-top, feet-at-bottom; the rotation maps sprite-up to world-up.
        // Anchor offset: the sprite is 12 px tall × 0.6 scale so it extends 3.6 px below center,
        // but the collision Radius is only 2.6 px. Without the offset the visual feet sit 1 px
        // *inside* the tile while the collision body is correctly perched on top — looks like
        // the dwarf is sunk into the ground. Shifting the sprite up by exactly that gap aligns
        // visual feet with the collision bottom.
        var up = _run.Planet.UpAt(_run.Player.Position);
        var rot = MathF.Atan2(up.X, -up.Y);
        // Worn jetpack: drawn first (behind the body), offset to the trailing side so it
        // reads as strapped to the back, upright with the dwarf. The exhaust stream in
        // UpdateFrame emits from this same pack position.
        if (_run.Player.Equipment.Get(EquipSlot.Back) == "jetpack")
        {
            var packRight = new Vector2(-up.Y, up.X);
            _renderer.Batch.Draw(_jetpackTex,
                _run.Player.Position - packRight * _playerFacing * 2.6f + up * 0.8f, null,
                Color.White, rot, new Vector2(_jetpackTex.Width * 0.5f, _jetpackTex.Height * 0.5f),
                new Vector2(0.65f, 1f), SpriteEffects.None, 0f);
        }
        // Hurt flash: the dwarf sprite briefly washes red when he takes damage, so a hit
        // reads right on the character, not just at the screen edge.
        var hurtTint = _run.Player.HurtFlash > 0f
            ? Color.Lerp(Color.White, new Color(255, 45, 45),
                MathHelper.Clamp(_run.Player.HurtFlash, 0f, 1f) * 0.85f)
            : Color.White;
        if (_playerSprite is { } ps)
        {
            // Animated pack sprite: pick a frame from grounded/tangent/radial motion, flip
            // toward the last direction the dwarf actually walked.
            var pRight = new Vector2(-up.Y, up.X);
            var vT = Vector2.Dot(_run.Player.Velocity, pRight);
            if (MathF.Abs(vT) > 8f) _playerFacing = MathF.Sign(vT);
            var frame = ps.Frame(_run.Player.Grounded, vT, Vector2.Dot(_run.Player.Velocity, up), _run.RunTime);
            _renderer.Batch.Draw(frame, _run.Player.Position + up * ps.FeetOffset(_run.Player.Radius), null,
                hurtTint, rot, new Vector2(frame.Width * 0.5f, frame.Height * 0.5f), ps.Scale,
                _playerFacing < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }
        else
        {
            const float spriteScale = 0.6f;          // world units per sprite pixel
            const float spriteFeetOffset = 1.0f;     // = (sprite_half_height * scale) − Radius
            _renderer.Batch.Draw(_dwarfTex, _run.Player.Position + up * spriteFeetOffset, null, hurtTint, rot,
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

        // Reticle — a small dot; the OS cursor does the pointing, this just marks the
        // world-space aim.
        var mouse = Screen.Mouse();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        _renderer.DrawCircle(worldCursor, 1.1f, new Color(255, 255, 255, 200));

        // Geo-scanner pulse: an expanding dotted ring around the player for a beat after a
        // scan fires (world-space, so it draws here in the world pass).
        if (_scanPulseT < 0.9f && _run.Player.ScannerTier > 0)
        {
            var pr = _scanPulseT / 0.9f;
            var rad = pr * (200f + (_run.Player.ScannerTier - 1) * 120f);
            var col = new Color(120, 220, 160, (int)((1f - pr) * 160f));
            const int dots = 48;
            for (var i = 0; i < dots; i++)
            {
                var a = i / (float)dots * MathF.Tau;
                _renderer.DrawRect(_run.Player.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rad,
                    new Vector2(1.4f, 1.4f), col);
            }
        }

        // Throw-strength gauge: while charging a thrown item, a ring around the reticle
        // fills clockwise from the top and shifts green→orange→red, pulsing white at full
        // power — so the aim and the power read in one place.
        if (_throwCharge > 0.001f)
        {
            var frac = _throwCharge;
            var full = frac >= 0.999f;
            var pulse = full ? MathF.Sin(_totalTime * 18f) * 0.5f + 0.5f : 1f;
            var col = Color.Lerp(Color.Lerp(new Color(120, 235, 120), new Color(255, 210, 70), frac),
                new Color(255, 80, 55), MathF.Max(0f, frac - 0.5f) * 2f);
            if (full) col = Color.Lerp(col, Color.White, pulse);
            const int segs = 24;
            const float ring = 4.6f;
            var filled = (int)MathF.Ceiling(frac * segs);
            for (var i = 0; i < filled; i++)
            {
                var a = -MathF.PI / 2f + i / (float)segs * MathF.Tau;   // top, clockwise
                var p = worldCursor + new Vector2(MathF.Cos(a), MathF.Sin(a)) * ring;
                _renderer.DrawRect(p, new Vector2(1.2f, 1.2f), col);
            }
        }

        // Terraria-style placement preview: with blocks selected, ghost the tiles the next
        // click fills — white when legal, red when unsupported / out of range / no stock.
        if (!_landing && !_ascending && !_orbiting
            && _run.Player.Toolbelt.Current == "blocks"
            && _run.Player.PlacePreview(_run.Planet, worldCursor) is { } prev)
        {
            var ghost = prev.valid ? new Color(255, 255, 255, 110) : new Color(255, 90, 80, 100);
            foreach (var (gx2, gy2) in prev.stamp)
            {
                var centre = _run.Planet.TileToWorld(gx2, gy2);
                var gUp = _run.Planet.UpAt(centre);
                _renderer.DrawRect(centre, new Vector2(Planet.TileSize, Planet.TileSize),
                    ghost, MathF.Atan2(gUp.X, -gUp.Y));
            }
        }

        // Held weapon: the selected toolbelt slot's sidearm drawn in the dwarf's grip,
        // rotated to the aim and flipped when facing left so it's never upside-down.
        // Pickaxe and hammer are swung tools instead — drawn along the swing arc.
        if (!_landing && !_ascending && !_orbiting
            && _run.Player.Toolbelt.Slots[_run.Player.Toolbelt.Selected] is { } heldId)
        {
            // Melee weapons store one texture per upgrade rung ("sword_t3") — the look
            // escalates with each craft, up to the rung-4 energy edge.
            var isMelee = Array.IndexOf(Toolbelt.MeleeIds, heldId) >= 0;
            var texKey = isMelee
                ? $"{heldId}_t{Math.Clamp(_run.Player.MeleeTiers.GetValueOrDefault(heldId, 1), 1, 4)}"
                : heldId;
            if (_weaponTex.TryGetValue(texKey, out var heldTex))
            {
                var aim = worldCursor - _run.Player.Position;
                if (aim.LengthSquared() > 0.01f) aim.Normalize(); else aim = new Vector2(1f, 0f);
                if (heldId is "pickaxe" or "hammer")
                {
                    DrawSwungTool(heldTex, aim);
                }
                else
                {
                    var wrot = MathF.Atan2(aim.Y, aim.X);
                    // Melee is 30% smaller now. Swing side picked from local planet-up so the
                    // chop always starts sky-side and comes DOWN, whatever the facing.
                    var scale = isMelee ? new Vector2(0.96f, 0.64f) : new Vector2(0.39f, 0.39f);
                    var flip = aim.X < 0f ? SpriteEffects.FlipVertically : SpriteEffects.None;
                    var org = new Vector2(1.5f, heldTex.Height / 2f);
                    if (isMelee && _meleeAnim > 0f)
                    {
                        var upW = _run.Planet.UpAt(_run.Player.Position);
                        float StartUpness(float sgn)
                        {
                            var a = wrot + sgn * -2.1f;
                            return Vector2.Dot(new Vector2(MathF.Cos(a), MathF.Sin(a)), upW);
                        }
                        var side = StartUpness(1f) >= StartUpness(-1f) ? 1f : -1f;
                        // Eased progress: the blade whips through the strike fast (smootherstep)
                        // so the swipe reads as a real slash, not a slow pan.
                        var p = 1f - _meleeAnim / _meleeAnimDur;
                        float Arc(float pp)
                        {
                            var e = pp * pp * pp * (pp * (pp * 6f - 15f) + 10f);   // smootherstep
                            return wrot + side * MathHelper.Lerp(-2.1f, 1.4f, e);
                        }
                        // Motion-trail: a few fading afterimages along the arc already covered,
                        // so the swing leaves a clear crescent slash behind the blade.
                        for (var s = 3; s >= 1; s--)
                        {
                            var tp = MathF.Max(0f, p - s * 0.12f);
                            _renderer.Batch.Draw(heldTex, _run.Player.Position + aim * 3.2f, null,
                                new Color(255, 255, 255, 50 - s * 10),
                                Arc(tp), org, scale, flip, 0f);
                        }
                        wrot = Arc(p);
                    }
                    _renderer.Batch.Draw(heldTex, _run.Player.Position + aim * 3.2f, null, Color.White,
                        wrot, org, scale, flip, 0f);
                    // The dwarf's fist wrapped around the grip — a small skin-tone knuckle over
                    // the stock, so the weapon reads as held rather than floating at his hip.
                    _renderer.DrawRect(_run.Player.Position + aim * 3.4f, new Vector2(1.7f, 1.7f),
                        new Color(230, 180, 140), wrot);

                    // Energy ball charging orb: a violet sphere growing at the muzzle as the
                    // cannon winds up, brightest core when full — the visual power meter.
                    if (heldId == "nuke" && _energyCharge > 0.01f)
                    {
                        var muzzle = _run.Player.Position + aim * 9f;
                        var f = EnergyPower(_energyCharge);
                        var rad = (1.2f + f * 5.5f) * 0.35f;   // 35% of the old charging-orb size
                        var jit = MathF.Sin(_totalTime * 30f) * 0.4f * f;
                        _renderer.DrawCircle(muzzle, (rad + jit) * 1.5f, new Color(150, 90, 240, 90));
                        _renderer.DrawCircle(muzzle, rad + jit, Color.Lerp(new Color(150, 110, 235), new Color(255, 120, 240), f));
                        _renderer.DrawCircle(muzzle, (rad + jit) * 0.5f, new Color(240, 220, 255));
                        if (_energyCharge >= 0.999f)   // fully charged: a bright pulsing ring
                            _renderer.DrawCircle(muzzle, rad * (1.6f + MathF.Sin(_totalTime * 18f) * 0.2f),
                                new Color(255, 255, 255, 70));
                    }
                }
            }
        }

        // Physical spawners. Goo pile: a pulsing slime mound oozing on a cave floor.
        // Lizard door: a brick arch with a dark doorway in a warren hall. Alien home: a
        // warm-lit doorway plate on a city address.
        foreach (var sp in _run.Spawners)
        {
            if ((sp.Position - _run.Player.Position).LengthSquared() > 1400f * 1400f) continue;
            var sUp = _run.Planet.UpAt(sp.Position);
            var sRot = MathF.Atan2(sUp.X, -sUp.Y);
            switch (sp.Kind)
            {
                case SpawnerKind.GooPile:
                {
                    var pulse = 1f + MathF.Sin(_renderer.Time * 2.2f + sp.Phase) * 0.12f;
                    _renderer.DrawCircle(sp.Position + sUp * 1.5f, 4.4f * pulse, new Color(52, 110, 45));
                    _renderer.DrawCircle(sp.Position + sUp * 3.2f, 3.0f * pulse, new Color(85, 170, 70));
                    _renderer.DrawCircle(sp.Position + sUp * 4.6f, 1.6f, new Color(150, 230, 110));
                    // A slow drip crawling down the mound face sells "alive".
                    var drip = (_renderer.Time * 0.5f + sp.Phase) % 1f;
                    _renderer.DrawRect(sp.Position + sUp * (4.5f - drip * 3.5f)
                        + new Vector2(-sUp.Y, sUp.X) * 2.4f, new Vector2(0.9f, 1.4f),
                        new Color(120, 210, 90), sRot);
                    break;
                }
                case SpawnerKind.LizardDoor:
                    _renderer.DrawRect(sp.Position + sUp * 4f, new Vector2(9f, 10f), new Color(96, 66, 44), sRot);
                    _renderer.DrawRect(sp.Position + sUp * 3.6f, new Vector2(6f, 8f), new Color(30, 22, 18), sRot);
                    _renderer.DrawRect(sp.Position + sUp * 8.6f, new Vector2(10f, 1.6f), new Color(120, 84, 55), sRot);
                    break;
                default:   // AlienHome — a lit household doorway
                    _renderer.DrawRect(sp.Position + sUp * 3.4f, new Vector2(5f, 7.5f), new Color(70, 76, 96), sRot);
                    _renderer.DrawRect(sp.Position + sUp * 3.2f, new Vector2(3.4f, 6f),
                        new Color(255, 214, 140), sRot);
                    break;
            }
        }

        // Planted (and flying) torches. A planted torch hangs BY ITS HILT from the contact
        // point: the stick extends from the anchor and the whole thing pendulums around it
        // — hard swing on impact, damping into the idle sway.
        foreach (var torch in _run.Torches)
        {
            var tRot = torch.Stuck
                ? torch.Swing(_renderer.Time)
                : MathF.Atan2(torch.Velocity.X, -torch.Velocity.Y);   // tumble nose-first
            var tUp = new Vector2(MathF.Sin(tRot), -MathF.Cos(tRot));
            _renderer.DrawRect(torch.Position + tUp * 2.1f, new Vector2(1.2f, 4.2f),
                new Color(115, 78, 45), tRot);
            var tip = torch.Position + tUp * 4.6f;
            _renderer.DrawRect(tip, new Vector2(2.0f, 1.8f), new Color(255, 165, 55), tRot);
            _renderer.DrawRect(tip + tUp * 0.5f, new Vector2(1.0f, 1.0f), new Color(255, 235, 150), tRot);
        }

        // Corpses — drawn under the living so a fresh kill layers naturally. A flattened
        // slab of the creature's (desaturated) body colour lying along the local tangent,
        // with a paler belly stripe. Blinks during the last seconds before decay.
        foreach (var corpse in _run.Corpses)
        {
            if (corpse.Life < Corpse.BlinkTime && (int)(corpse.Life * 6f) % 2 == 0) continue;
            if (corpse.Body is { } cbody)
            {
                // The corpse IS the creature: its dead clone drawn frozen and greyed, the
                // whole pose rotated by the ragdoll's tumble (±90° so it lies on its side),
                // fading out as the harvest carves it away. The entity-FX fade reaches the
                // raw-colour details (eyes, glints) the dead tint alone can't.
                cbody.Position = corpse.Position;
                var pose = corpse.Angle + corpse.PoseSide * MathF.PI / 2f;
                _renderer.SetEntityFx(1f - corpse.Dissolve * 0.9f, 0f);
                _renderer.BeginOutline(new Color(14, 11, 16));
                cbody.Draw(_renderer, _run.Planet, _run.Player, pose, corpse.Dissolve);
                _renderer.EndOutline();
                cbody.Draw(_renderer, _run.Planet, _run.Player, pose, corpse.Dissolve);
                _renderer.ClearEntityFx();
            }
            else
            {
                // Legacy slab (headless-spawned corpses with no display body).
                var crot = corpse.Angle;
                var bodyUp = new Vector2(MathF.Sin(crot), -MathF.Cos(crot));
                var col = Corpse.BodyColor(corpse.Kind);
                _renderer.DrawRect(corpse.Position, new Vector2(corpse.Radius * 2.2f, corpse.Radius * 0.9f), col, crot);
                _renderer.DrawRect(corpse.Position - bodyUp * corpse.Radius * 0.2f,
                    new Vector2(corpse.Radius * 1.6f, corpse.Radius * 0.4f),
                    Color.Lerp(col, Color.White, 0.18f), crot);
            }
        }

        // The titan carcass — drawn like the corpses (outline + dissolve fade), before the
        // living so anything walking past layers over it.
        if (_run.TitanCarcass is { } carcDraw
            && (carcDraw.Position - _camera.Target).LengthSquared() < 500f * 500f)
        {
            _renderer.SetEntityFx(1f - carcDraw.Dissolve * 0.85f, 0f);
            _renderer.BeginOutline(new Color(14, 11, 16));
            carcDraw.Draw(_renderer, _run.Planet);
            _renderer.EndOutline();
            carcDraw.Draw(_renderer, _run.Planet);
            _renderer.ClearEntityFx();
        }

        // Carving overlay while a harvest channel runs: progress bar over the body and the
        // dwarf's knife chopping at it — the "working on it" read the instant-pickup lacked.
        if (_harvestFxPos is { } hpos)
        {
            var hup = _run.Planet.UpAt(hpos);
            var hright = new Vector2(-hup.Y, hup.X);
            var hrot = MathF.Atan2(hup.X, -hup.Y);
            var barAt = hpos + hup * (_harvestFxRadius + 6f);
            _renderer.DrawRect(barAt, new Vector2(13f, 2.2f), new Color(24, 20, 20), hrot);
            _renderer.DrawRect(barAt - hright * (6.5f * (1f - _harvestFxFrac)),
                new Vector2(13f * _harvestFxFrac, 1.6f), new Color(235, 185, 90), hrot);
            // Knife: a pale blade rocking over the body from the dwarf's side, at the same
            // beat as the work particles.
            float hside = MathF.Sign(Vector2.Dot(_run.Player.Position - hpos, hright));
            if (hside == 0f) hside = 1f;
            var chop = MathF.Sin(_renderer.Time * 11f);
            var hand = hpos + hright * (hside * _harvestFxRadius * 0.5f)
                     + hup * (2.5f + MathF.Abs(chop) * 2.4f);
            _renderer.DrawRect(hand, new Vector2(1.3f, 4.6f), new Color(205, 210, 220),
                hrot + hside * (0.45f + chop * 0.5f));
        }

        // Gem pickups — sit upright along the local surface normal (no spin). A loose gem
        // reads as its own material (body colour + a brighter inner facet), cut with a
        // faceted silhouette, and carries a slight travelling shine highlight so a freshly
        // mined stone gleams. Crystals cut as a DIAMOND rhombus; the true gems as a jewel.
        foreach (var g in _run.Pickups)
        {
            var gup = _run.Planet.UpAt(g.Position);
            var gright = new Vector2(-gup.Y, gup.X);
            var grot = MathF.Atan2(gup.X, -gup.Y);
            var body = Tiles.BaseColor(g.Kind);
            var facet = Tiles.OreSpeckle(g.Kind);
            // THIN edge tone barely darker than the body — the old half-brightness rim drawn
            // 0.7px proud read as a black square backing behind the jewel when it sat on a
            // bright dust pile. Now it's just a sliver of outline.
            var edge = Color.Lerp(body, Color.Black, 0.35f);
            // A small, clean DIAMOND (rotated square) for every loose stone — no protruding
            // point pieces, smaller than before, so it reads as a neat cut jewel.
            var d45 = grot + MathF.PI / 4f;
            _renderer.DrawRect(g.Position, new Vector2(2.2f, 2.2f), edge, d45);   // thin outline
            _renderer.DrawRect(g.Position, new Vector2(1.9f, 1.9f), body, d45);   // bright body
            _renderer.DrawRect(g.Position - gright * 0.35f + gup * 0.35f,
                new Vector2(0.9f, 0.9f), facet, d45);                            // facet
            _renderer.DrawRect(g.Position + gright * 0.45f - gup * 0.1f,
                new Vector2(0.5f, 0.5f), Color.White, d45);                      // glint
            // Slight travelling shine: a small highlight that sweeps across the stone, plus
            // an occasional bright sparkle at a corner — the "just mined, still glinting"
            // read the user asked for.
            var shineT = (g.Age * 0.8f) % 1f;
            var shinePos = g.Position + gright * ((shineT * 2f - 1f) * 1.8f) + gup * 0.6f;
            _renderer.DrawRect(shinePos, new Vector2(0.9f, 0.9f), new Color(255, 255, 255, 200), grot);
            if (((int)(g.Age * 2.5f) & 7) == 0)
                _renderer.DrawRect(g.Position + gup * 1.6f + gright * 1.4f,
                    new Vector2(1f, 1f), Color.White);
        }

        // Creatures — each kind draws its own procedural sprite, including the burn/freeze
        // status tinting and the burning-ember flicker. The planet-wide resident census
        // makes the list long; anything far outside the view skips its sprite entirely.
        // Creatures draw twice: an enlarged dark-silhouette pass then the body — the chunky
        // Noita-style outline that separates every living thing from the terrain behind it.
        foreach (var c in _run.Creatures)
        {
            if ((c.Position - _run.Player.Position).LengthSquared() > 1400f * 1400f) continue;
            _renderer.BeginOutline(new Color(14, 11, 16));
            c.Draw(_renderer, _run.Planet, _run.Player);
            _renderer.EndOutline();
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
                {
                    // Small brass round flying nose-first — a stubby casing with a bright tip.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    var nose = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 1.4f;
                    _renderer.DrawRect(p.Position, new Vector2(3f, 1.1f), new Color(205, 170, 90), ang);
                    _renderer.DrawRect(nose, new Vector2(1.3f, 1.1f), new Color(245, 230, 170), ang);
                    break;
                }
                case ProjectileKind.Cannon:
                    _renderer.DrawCircle(p.Position, p.Radius, new Color(255, 140, 60));
                    break;
                case ProjectileKind.Nuke:
                {
                    // Energy ball: a pulsing violet orb with a white-hot core and a crackling
                    // corona — bigger the harder it was charged.
                    var rr = p.Radius;
                    var pulse = MathF.Sin(_run.RunTime * 22f) * 0.15f + 1f;
                    _renderer.DrawCircle(p.Position, rr * 1.5f * pulse, new Color(150, 90, 240, 90));
                    _renderer.DrawCircle(p.Position, rr, new Color(200, 120, 255));
                    _renderer.DrawCircle(p.Position, rr * 0.55f, new Color(240, 220, 255));
                    // A couple of orbiting sparks.
                    for (var s = 0; s < 3; s++)
                    {
                        var a = _run.RunTime * 9f + s * MathF.Tau / 3f;
                        _renderer.DrawRect(p.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rr * 1.3f,
                            new Vector2(1.4f, 1.4f), new Color(220, 180, 255));
                    }
                    break;
                }
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
                case ProjectileKind.DynamitePack:
                {
                    // Stick (or fatter bundle) oriented along velocity, fuse tip flickering
                    // faster as the 3s timer runs down so you can read the bang coming.
                    var pack = p.Kind == ProjectileKind.DynamitePack;
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    var w = pack ? 8f : 7f;
                    var h = pack ? 5f : 2.5f;
                    _renderer.DrawRect(p.Position, new Vector2(w, h), new Color(180, 40, 50), ang);
                    _renderer.DrawRect(p.Position, new Vector2(w, h * 0.4f), new Color(120, 20, 30), ang);
                    var strobe = 30f + MathF.Max(0f, 3f - p.Life) * 30f;
                    var fuseFlicker = MathF.Sin(_run.RunTime * strobe) * 0.5f + 0.5f;
                    var fuseTip = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (w * 0.55f);
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
                {
                    // Heavier gold round — a touch longer and fatter than the basic bullet.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    var nose = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 1.7f;
                    _renderer.DrawRect(p.Position, new Vector2(3.6f, 1.4f), new Color(228, 200, 120), ang);
                    _renderer.DrawRect(nose, new Vector2(1.5f, 1.4f), new Color(255, 245, 210), ang);
                    break;
                }
                case ProjectileKind.CivicBolt:
                {
                    // Militia stun-bolt: a short cyan dash, visibly not the player's ammo.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(4.5f, 1.3f), new Color(120, 225, 255), ang);
                    break;
                }
                case ProjectileKind.MachineGun:
                {
                    // Small, sharp copper round with a hot tip — reads as a fast little slug
                    // rather than a fat tracer streak now.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    var nose = p.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 1.2f;
                    _renderer.DrawRect(p.Position, new Vector2(2.6f, 0.95f), new Color(200, 130, 80), ang);
                    _renderer.DrawRect(nose, new Vector2(1.1f, 0.95f), new Color(255, 205, 130), ang);
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
                case ProjectileKind.TntPack:
                {
                    // Strapped bundle of three sticks with a sparking fuse. The fuse spark
                    // strobes faster as the timer runs down — you can read the bang coming.
                    var ang = MathF.Atan2(p.Velocity.Y, p.Velocity.X);
                    _renderer.DrawRect(p.Position, new Vector2(6f, 5f), new Color(180, 45, 45), ang);
                    _renderer.DrawRect(p.Position, new Vector2(6f, 1f), new Color(120, 25, 25), ang);
                    _renderer.DrawRect(p.Position, new Vector2(1.4f, 5f), new Color(90, 70, 45), ang);
                    var strobe = 30f + MathF.Max(0f, 2.5f - p.Life) * 40f;
                    var spark = MathF.Sin(_run.RunTime * strobe) * 0.5f + 0.5f;
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

        // Rigid debris — drawn with the terrain's own atlas art so chunks match the wall
        // they broke out of (see Renderer.DrawRigidBodies).
        if (_run.Rigid is { } rigidDraw) _renderer.DrawRigidBodies(rigidDraw);

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

        // Ambient weather clouds — the gentle ecosystem rain, drawn as soft drifting banks
        // tinted by the biome's rain. The falling drops themselves are particles.
        DrawWeather();

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
                case TitanShotKind.Void:
                {
                    // Null-energy orb: a black core wrapped in a violet corona, trailing a
                    // twinkle — reads as a little collapsed star drifting at you.
                    var wob = (float)Random.Shared.NextDouble() * 1.2f;
                    _renderer.DrawCircle(shot.Position, shot.Radius + 2f + wob, new Color(150, 90, 230));
                    _renderer.DrawCircle(shot.Position, shot.Radius, new Color(18, 10, 30));
                    if (Random.Shared.Next(3) == 0)
                        _renderer.DrawRect(shot.Position - Vector2.Normalize(shot.Velocity) * (shot.Radius + 3f),
                            new Vector2(1.6f, 1.6f), Color.White);
                    break;
                }
                case TitanShotKind.Slug:
                {
                    // Bandit bullet: a short lead tracer — smaller and duller than the
                    // player's own rounds, because it IS the budget version.
                    var sAng = MathF.Atan2(shot.Velocity.Y, shot.Velocity.X);
                    _renderer.DrawRect(shot.Position, new Vector2(7f, 2f), new Color(210, 200, 160), sAng);
                    _renderer.DrawRect(shot.Position + Vector2.Normalize(shot.Velocity) * 2.5f,
                        new Vector2(3f, 1.2f), Color.White, sAng);
                    break;
                }
                case TitanShotKind.Dart:
                {
                    // Blowdart: a thin reed shaft tipped with a dark point and a little tuft at
                    // the tail, turned to face the way it's travelling as it arcs.
                    var sAng = MathF.Atan2(shot.Velocity.Y, shot.Velocity.X);
                    var fwd = Vector2.Normalize(shot.Velocity);
                    _renderer.DrawRect(shot.Position, new Vector2(7f, 1.3f), new Color(196, 176, 120), sAng);
                    _renderer.DrawRect(shot.Position + fwd * 3.4f, new Vector2(2.2f, 1.8f), new Color(70, 90, 60), sAng); // dark point
                    _renderer.DrawRect(shot.Position - fwd * 3.2f, new Vector2(1.6f, 3f), new Color(230, 90, 90), sAng);  // fletch tuft
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
        if (_ascending)
        {
            // The pad stays behind on the ground; the rocket flies free on its own heading.
            if (_run.PadPos is { } padPos) DrawShip(padPos, 0);
            DrawShip(_launchShipPos, _run.ShipStage, _ascentHeading);
        }
        else if (_shipParked)
        {
            // Rocket set down away from home: bare pad at the build site, hull standing
            // upright wherever the player left it.
            if (_run.PadPos is { } padPos) DrawShip(padPos, 0);
            DrawShip(_launchShipPos, _run.ShipStage, _run.Planet.UpAt(_launchShipPos));
        }
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

        // Grapple line — a taut hemp strand from the dwarf to its hook (terrain point or a
        // spot on the titan's hide), with the hook drawn as a bright claw at the anchor.
        if (_grapAnchor is not null || _grapOnTitan)
        {
            var anchor = _grapOnTitan ? _run.Titan.Position + _grapLocal : _grapAnchor!.Value;
            var span = anchor - _run.Player.Position;
            var segLen = span.Length();
            if (segLen > 1f)
            {
                var mid = _run.Player.Position + span * 0.5f;
                var lineRot = MathF.Atan2(span.Y, span.X);
                _renderer.DrawRect(mid, new Vector2(segLen, 1.2f), new Color(196, 160, 96), lineRot);
                _renderer.DrawCircle(anchor, 2.2f, new Color(220, 220, 230));
            }
        }

        // Particles drawn last so chips and sparks pop over creatures and the player.
        _particles.Draw(_renderer);

        _renderer.EndEntities();

        // Pixel-grid pipeline: the finished world layer reaches the scene in ONE integer
        // point-sampled upscale, then the camera returns to native so the lighting pass
        // composites smooth light over the quantised world at full resolution.
        if (_pixelK > 0)
        {
            GraphicsDevice.SetRenderTarget(_sceneRt);
            _camera.ViewportSize = new Point(VirtualWidth, VirtualHeight);
            _camera.Zoom = _pixelK * 2f;
            _renderer.Batch.Begin(samplerState: SamplerState.PointClamp);
            _renderer.Batch.Draw(_worldRt, new Rectangle(0, 0,
                _worldRt!.Width * _pixelK, _worldRt.Height * _pixelK), Color.White);
            _renderer.Batch.End();
        }

        // === Lighting pass: Terraria-style propagated light. The grid samples occlusion
        // and sunlight from the planet (open sky = full daylight, bleeding a few tiles
        // into cave mouths before rock eats it), every AddLight below seeds it, and the
        // propagation floods light through air while walls block it. No ambient, no
        // darkness disks: unlit underground is genuinely black now. ===
        FramePerf.Add("entsD", tDraw);
        tDraw = FramePerf.Now();
        _lightSw.Restart();
        _lightGrid.Begin(_run.Planet, _camera);
        _renderer.LightGridBeginMs = _lightSw.Elapsed.TotalMilliseconds;
        FramePerf.Add("lgBeg", tDraw);
        tDraw = FramePerf.Now();

        // Player light — reach scales with the worn light-slot item (bare stub → torch →
        // lantern → headlamp I-IV → sunstone), which is what makes the light ladder feel
        // like real progression against the dark. No depth gating any more: on the surface
        // the propagated sunlight simply out-brightens the aura (seeds combine by max).
        var lightMul = _run.Player.LightMul;
        _renderer.AddLight(_run.Player.Position, 150f * lightMul, new Color(245, 215, 165));
        // The carried lamp is also a ray-cast hero light: a direct beam that cuts hard
        // Noita-style shadows behind pillars and creatures, layered over the soft grid
        // spill. Kept dimmer than the grid seed so it accents rather than dominates.
        _renderer.AddHeroLight(_run.Player.Position, 70f * lightMul, new Color(120, 100, 70));
        // Sunstone burns cold white at the core — reads as a different light source, not
        // just a bigger torch. Tier IV pickaxe (diamond) keeps its faint icy sheen.
        if (_run.Player.EffectiveLightTier >= 4)
            _renderer.AddLight(_run.Player.Position, 70f * lightMul, new Color(200, 215, 235));
        if (_run.Player.PickaxeTier >= 4)
            _renderer.AddLight(_run.Player.Position, 28f, new Color(180, 220, 255));
        // Rung-4 melee: the energy edge glows like a lightsaber — real light off the blade
        // whenever it's the held item.
        if (_run.Player.Toolbelt.Current is { } meleeHeld
            && Array.IndexOf(Toolbelt.MeleeIds, meleeHeld) >= 0
            && _run.Player.MeleeTiers.GetValueOrDefault(meleeHeld, 1) >= 4)
        {
            var glow = MeleeGlow(meleeHeld);
            _renderer.AddLight(_run.Player.Position, 60f, glow);
            _renderer.AddHeroLight(_run.Player.Position, 26f, glow * 0.5f);
        }

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
                // Embedded gems shed a slight glow in their own colour — a seam of them
                // twinkles down a dark tunnel before the tiles themselves resolve.
                var og = _run.Planet.GemAt(ptx + dx, pty + dy);
                if (og != TileKind.Sky)
                    _renderer.AddLight(_run.Planet.TileToWorld(ptx + dx, pty + dy), 10f,
                        Tiles.OreSpeckle(og));
                var k = _run.Planet.Get(ptx + dx, pty + dy);
                Color glow; float r;
                switch (k)
                {
                    case TileKind.GoldOre: glow = new Color(255, 190, 70);  r = 12f; break;
                    case TileKind.SilverOre: glow = new Color(210, 225, 255); r = 9f; break;
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
                    case TileKind.CityGlass:
                        // Lit apartment windows — the skyscraper bands glow warm from inside.
                        glow = new Color(255, 214, 140);
                        r = 11f;
                        break;
                    case TileKind.OrbLamp:
                        // Apartment lamp orb — a warm hearth-glow that breathes with the
                        // renderer's pixel pulse.
                        glow = new Color(255, 220, 150);
                        r = 24f + MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 1.4f + dx * 0.3f) * 3f;
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
                ProjectileKind.DynamitePack   => (new Color(255, 170, 70),  14f),
                ProjectileKind.Harpoon        => (new Color(255, 200, 130), 14f),
                ProjectileKind.Pistol         => (new Color(255, 235, 160), 8f),
                ProjectileKind.MachineGun     => (new Color(255, 210, 120), 6f),
                ProjectileKind.CivicBolt      => (new Color(130, 220, 255), 7f),
                ProjectileKind.Laser          => (new Color(255, 90, 90),   30f),
                ProjectileKind.LaserCannon    => (new Color(120, 225, 255), 44f),
                ProjectileKind.Rocket         => (new Color(255, 160, 70),  16f),
                ProjectileKind.Tnt            => (new Color(255, 210, 110), 10f),
                ProjectileKind.TntPack        => (new Color(255, 210, 110), 10f),
                _ => (Color.White, 6f),
            };
            _renderer.AddLight(p.Position, r, col);
        }

        // Gem pickups shed a soft glow in their own colour, so a popped ruby is findable
        // even when it bounces into an unlit corner of the dig.
        foreach (var g in _run.Pickups)
            _renderer.AddLight(g.Position, 9f, Tiles.OreSpeckle(g.Kind));

        // Spawners glow so the player can spot (and learn) them: goo sickly green, homes
        // warm; the lizard door stays dark — a warren mouth shouldn't advertise itself.
        foreach (var sp in _run.Spawners)
        {
            if (sp.Kind == SpawnerKind.GooPile)
                _renderer.AddLight(sp.Position, 24f, new Color(120, 220, 90));
            else if (sp.Kind == SpawnerKind.AlienHome)
                _renderer.AddLight(sp.Position, 20f, new Color(255, 210, 130));
        }

        // Planted torches burn with a soft per-torch flicker; in-flight ones carry their
        // flame with them. The light lives at the FLAME (the swinging tip), not the hilt,
        // and reaches far enough to genuinely light the player's way.
        foreach (var torch in _run.Torches)
        {
            if (torch.Stuck)
            {
                var swingRot = torch.Swing(_renderer.Time);
                var flameTip = torch.Position
                    + new Vector2(MathF.Sin(swingRot), -MathF.Cos(swingRot)) * 4.6f;
                _renderer.AddLight(flameTip,
                    88f + MathF.Sin(_renderer.Time * 6.5f + torch.Phase) * 9f,
                    new Color(255, 180, 90));
            }
            else
            {
                _renderer.AddLight(torch.Position, 60f, new Color(255, 180, 90));
            }
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
                TitanShotKind.Void  => (16f, new Color(160, 100, 240)),
                TitanShotKind.Dart  => (0f, new Color(0, 0, 0)),   // a wooden dart doesn't glow
                TitanShotKind.Slug  => (0f, new Color(0, 0, 0)),   // nor a lead slug
                _                   => (18f, new Color(120, 230, 255)),
            };
            if (lr > 0f) _renderer.AddLight(shot.Position, lr, lc);
        }

        // The live gravity well drowns its arena in violet while the pull runs.
        if (_gravityWellTimer > 0f)
            _renderer.AddLight(_gravityWell, 90f + MathF.Sin(_renderer.Time * 9f) * 12f,
                new Color(150, 90, 230));

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
        foreach (var c in _run.Creatures)
        {
            // Same far-gate as the sprite pass — no lighting math for the frozen census.
            if ((c.Position - _run.Player.Position).LengthSquared() > 1400f * 1400f) continue;
            c.AddLight(_renderer, _run.Planet);
        }

        // Glowing particles (ore flecks, projectile sparks, explosion embers) feed back into
        // the lightmap so they actually illuminate the cave wall behind them.
        _particles.AddLights(_renderer);
        // Lava cells along their pool surface light up the cave roof (view-culled). Floor
        // of 2 on the seeding stride even in close play: this pass re-scans the same cell
        // candidates as the draw, but light seeds combine by MAX into 4-px light cells —
        // every-2nd-cell seeding is visually identical and halves the second scan on both
        // axes (the draw + seed scans together were ~half the descent frame cost).
        _run.Cells.AddLights(_renderer, viewCentre, viewRadius, Math.Max(cellStride, 2));

        // Propagate the seeded grid, rasterize it into the lightmap, and cut the hero
        // lights' ray-cast shadow fans over it. Depth darkness is emergent now: rock
        // occludes, so anywhere the sun and the seeds can't reach is simply black.
        // DM_NOLIGHT=1 skips the whole pass (perf isolation diagnostic); the debug menu's
        // fullbright toggle does the same interactively — the underground renders as if lit.
        var noLight = _fullbright
            || Environment.GetEnvironmentVariable("DM_NOLIGHT") is { Length: > 0 };
        FramePerf.Add("lgSeed", tDraw);
        if (!noLight)
        {
            tDraw = FramePerf.Now();
            _renderer.RenderLightGrid(_camera, _run.Planet);
            FramePerf.Add("lgRender", tDraw);
            tDraw = FramePerf.Now();
            _renderer.CompositeLighting(new Point(VirtualWidth, VirtualHeight));
            FramePerf.Add("lgComp", tDraw);
        }
        tDraw = FramePerf.Now();
        // Multi-tap separable Gaussian bloom — bright spots (lava, projectiles, headlamp core)
        // bleed a soft glow over the scene through real downsample + 9-tap blur passes.
        if (!noLight && Environment.GetEnvironmentVariable("DM_NOBLOOM") is not { Length: > 0 })
            _renderer.BloomLighting(new Point(VirtualWidth, VirtualHeight), new Color(70, 65, 85));
        // Cinematic vignette + subtle cool grade — pushes shadows slightly blue, keeps the
        // surface readable but adds depth at the screen edges.
        _renderer.VignetteScene(new Point(VirtualWidth, VirtualHeight));
        _renderer.GradeScene(new Point(VirtualWidth, VirtualHeight), new Color(245, 245, 255));
        FramePerf.Add("post", tDraw);

        _camera.Target = oldTarget;

        var depth = _run.Planet.Radius - (int)((_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize);
        // Top-left status: planet, ship progress, depth, titan HP, run meta. The toolbelt at
        // the bottom of the screen carries the per-tool readout, so we don't duplicate it here.
        var ship = _run.PadPos is null ? "NO PAD" : _run.ShipStage switch
        {
            0 => "PAD READY",
            1 => "HULL BUILT",
            2 => "ENGINE IN",
            _ => _run.ShipFuel < FuelToLaunch
                ? $"FUEL {_run.ShipFuel}/{FuelToLaunch} - E AT ROCKET TO FUEL"
                : "FUELLED - E AT ROCKET TO BOARD",
        };
        string titanStatus;
        if (_run.Titan.Health <= 0)
        {
            titanStatus = _run.TitanCarcass is { Claimed: false }
                ? $"{TitanName(_run.Titan.Kind).ToUpperInvariant()} FELLED - CARVE THE CARCASS FOR ITS SOUL ({TitanCorpse.HarvestTime:0}s)"
                : $"{TitanName(_run.Def.Titan).ToUpperInvariant()} SLAIN - SOUL CLAIMED";
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
                       "C CRAFT  I GEAR  T BEACON  E/L BOARD ROCKET  B/N DEPOT BANK  F5 SAVE  G GOD MODE";
        if (_orbiting)
            controls = $"LEFT/RIGHT ORBIT THE PLANET   ENTER LAUNCH LANDER ({_meta.Rovers} ABOARD{(_meta.Rovers <= 0 ? " - DROP POD!" : "")})   SPACE LEAVE PLANET\n" +
                       $"L LOADOUT{(PendingKitCount() > 0 ? $" ({PendingKitCount()} KITS PACKED)" : "")}   PICK YOUR DROP SITE - THE LANDER FALLS FROM THE SHIP";
        else if (_landing)
            controls = "A/D STEER THE ROVER\nTOUCHDOWN WHERE YOU AIM";
        else if (_ascending)
            controls = "A/D TURN THE NOSE   SPACE BURN   E SET DOWN AND STEP OUT\nGET CLOSE TO THE MOTHERSHIP TO DOCK";
        // (No screen-edge hurt vignette — the damage read lives ON the dwarf now: the sprite
        // flashes red when hit, see the player draw's hurtTint.)
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
        _invUi.DrawContextMenu(_renderer, _run.Player);

        DrawHoverDebugLabel();
        DrawBuildProgress();

        if (_charScreen.Open)
            _charScreen.Draw(_renderer, _run.Player, VirtualWidth, VirtualHeight);
        if (_craftingMenu.Open)
            _craftingMenu.Draw(_renderer, _run.Player.Inventory, IsOwned,
                id => CanAffordId(id), Screen.Mouse(), VirtualWidth, VirtualHeight, _totalTime);
        if (_debugMenu.Open)
            _debugMenu.Draw(_renderer, VirtualWidth, VirtualHeight);

        DrawStationIndicator();
        DrawScannerArrows();
        if (_orbiting && _loadoutOpen) DrawLoadoutMenu(_renderer.Batch);
        if (_screen == GameScreen.GameOver) DrawGameOverOverlay();
        DrawTransitionFlash();
        DrawPauseMenu();
        DrawFpsOverlay();

        if (_screenshotPending)
        {
            _screenshotPending = false;
            SaveScreenshot();
        }
    }

    private void SaveScreenshot()
    {
        // The finished frame lives in the virtual scene target (the backbuffer only gets
        // the scaled blit at present time) — read it there, unbinding first so the copy
        // isn't from a live render target.
        GraphicsDevice.SetRenderTarget(null);
        const int w = VirtualWidth;
        const int h = VirtualHeight;
        var data = new Color[w * h];
        _sceneRt.GetData(data);

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
        var mouse = Screen.Mouse();
        var screenPos = new Vector2(mouse.X, mouse.Y);
        if (screenPos.X < 0 || screenPos.Y < 0 || screenPos.X >= VirtualWidth || screenPos.Y >= VirtualHeight)
            return;
        var worldCursor = _camera.ScreenToWorld(screenPos);

        // Named things first — creatures, gem pickups, embedded gems / crystal tiles get
        // their proper name; only unnamed ground falls through to the material/tile readout.
        if (HoverName(worldCursor) is { } name)
        {
            _renderer.DrawDebugLabel(name, screenPos + new Vector2(12, 12), new Color(255, 228, 150));
            return;
        }

        // Cell-grid material wins over the underlying tile, so hovering a sand pile shows
        // "SAND" rather than the dirt tile beneath it.
        string label;
        var (cx, cy) = _run.Cells.WorldToCell(worldCursor);
        var mat = _run.Cells.Get(cx, cy);
        if (mat == Material.Dust)
        {
            // "Dust" is just the loose state — name it for the material it came from.
            var src = _run.Cells.SrcTileAt(cx, cy);
            var srcName = Tiles.Drop(src) is { } d ? Tiles.ResourceLabel(d.id)
                        : src == TileKind.Sky ? "DEBRIS"
                        : src.ToString().ToUpperInvariant();
            label = $"MATERIAL: {srcName} (LOOSE)";
        }
        else if (mat != Material.Empty)
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

    /// <summary>Display name of the nameable thing under the cursor, or null: the nearest
    /// overlapped creature first (the living thing wins over whatever it stands on), then
    /// physical gem pickups, then a gem embedded in (or a crystal tile at) the hovered tile.</summary>
    private string? HoverName(Vector2 world)
    {
        Creature? bestCreature = null;
        var bestD2 = float.MaxValue;
        foreach (var c in _run.Creatures)
        {
            var r = MathF.Max(c.Radius, 3f) + 2f;   // small floor so tiny fauna are hoverable
            var d2 = (c.Position - world).LengthSquared();
            if (d2 < r * r && d2 < bestD2) { bestD2 = d2; bestCreature = c; }
        }
        if (bestCreature is not null) return Humanize(bestCreature.Kind.ToString());

        foreach (var g in _run.Pickups)
            if ((g.Position - world).LengthSquared() < 5f * 5f)
                return Humanize(g.Kind.ToString());

        var (tx, ty) = _run.Planet.WorldToTile(world);
        var gem = _run.Planet.GemAt(tx, ty);
        if (gem == TileKind.Sky && Tiles.IsGem(_run.Planet.Get(tx, ty)))
            gem = _run.Planet.Get(tx, ty);
        return gem == TileKind.Sky ? null : Humanize(gem.ToString());
    }

    /// <summary>PascalCase enum name → spaced caps for the HUD: "CaveEye" → "CAVE EYE".</summary>
    private static string Humanize(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(pascal[i]));
        }
        return sb.ToString();
    }

    /// <summary>Progress bar under the cursor while a placement is under construction —
    /// the visible cost of the build time (see Player.BuildTime).</summary>
    private void DrawBuildProgress()
    {
        var f = _run.Player.BuildFraction;
        if (f <= 0f) return;
        var mouse = Screen.Mouse();
        var x = mouse.X - 12;
        var y = mouse.Y + 16;
        var sb = _renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(x - 1, y - 1, 26, 5), new Color(0, 0, 0, 190));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, 24, 3), new Color(70, 62, 50));
        sb.Draw(_renderer.Pixel, new Rectangle(x, y, (int)(24 * f), 3), new Color(235, 205, 120));
        sb.End();
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

    /// <summary>Draw the pad-and-rocket assembly at a world position, oriented to the local
    /// vertical. In flight a <paramref name="heading"/> overrides the orientation — the hull
    /// aligns to the nose instead of planet-up, and the pad slab is left off (it stays on
    /// the ground where it was built).</summary>
    private void DrawShip(Vector2 pos, int stage, Vector2? heading = null)
    {
        var up = heading ?? _run.Planet.UpAt(pos);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y);
        var steel = new Color(150, 155, 170);
        var steelDark = new Color(95, 100, 115);
        var frame = new Color(70, 60, 50);

        if (heading is null)
        {
            // Pad: a broad slab on two squat legs, with hazard-stripe ends.
            _renderer.DrawRect(pos - up * 3f + right * 10f, new Vector2(3f, 4f), frame, rot);
            _renderer.DrawRect(pos - up * 3f - right * 10f, new Vector2(3f, 4f), frame, rot);
            _renderer.DrawRect(pos, new Vector2(30f, 3f), steelDark, rot);
            _renderer.DrawRect(pos + right * 13f, new Vector2(4f, 3f), new Color(200, 170, 60), rot);
            _renderer.DrawRect(pos - right * 13f, new Vector2(4f, 3f), new Color(200, 170, 60), rot);
        }

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
