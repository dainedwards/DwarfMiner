using System;
using System.Collections.Generic;
using System.IO;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.Systems;
using DwarfMiner.UI;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner;

/// <summary>Top-level screen state. Overworld is the star map (entry screen and post-run
/// hub); Playing is a live run; GameOver overlays the frozen run until R returns to the map.</summary>
public enum GameScreen { Overworld, Playing, GameOver }

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

    /// <summary>The current planet visit. Everything per-run lives here — swapped atomically
    /// when the player picks a planet on the star map. Null only while on the star map before
    /// the first run; Playing/GameOver screens always have one.</summary>
    private Session _run = null!;

    private GameScreen _screen = GameScreen.Overworld;
    private int _overworldCursor;

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private string _gameOverReason = "";
    private bool _screenshotPending;

    /// <summary>Transient HUD toast ("RUN SAVED") — drawn top-centre while the timer runs.</summary>
    private string _toast = "";
    private float _toastTimer;

    /// <summary>Fuel units the ship must hold before it can lift off.</summary>
    public const int FuelToLaunch = 12;

    /// <summary>Liftoff cinematic state. While <see cref="_launching"/> is set, normal play is
    /// suspended: the rocket climbs along <see cref="_launchUp"/> under <see cref="_launchVel"/>,
    /// trailing exhaust, until it clears the sky and the run ends.</summary>
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
            if (_screen == GameScreen.Playing && _run is not null) RunSave.Write(_run);
        };
    }

    protected override void Initialize()
    {
        _meta = MetaSave.Load();
        // DM_AUTOSTART=<planet-id|resume> skips the star map and jumps straight into a run
        // (or resumes the suspend save) — keeps DM_AUTOSHOT-driven gameplay verification
        // working without menu input.
        if (Environment.GetEnvironmentVariable("DM_AUTOSTART") is { Length: > 0 } auto)
        {
            if (auto == "resume") ResumeRun();
            else StartNewRun(PlanetDefs.ById(auto));
        }
        base.Initialize();
    }

    private void StartNewRun(PlanetDef def)
    {
        var seed = (int)DateTime.Now.Ticks;
        _run = new Session(def);
        // Ship-stage recipes (nav core cost) vary per planet — rebuild the crafting table.
        Crafting.SetPlanet(def);
        _run.Planet = WorldGen.Generate(seed, def);
        _run.Cells = new Cells(_run.Planet);
        _run.Physics = new Physics(_run.Planet, _run.Cells);
        // Lava seeding: any cave (Sky tile) within the def's lava fraction of the planet
        // radius gets filled with lava — volcanic worlds flood far shallower cavities.
        if (def.LavaFillFrac > 0f)
            _run.Cells.FillSkyTilesWithin(_run.Planet.Radius * def.LavaFillFrac, Material.Lava);

        // Water seeding: world gen recorded lake-basin and reservoir tiles; pour the cells in
        // now. Water is always sim cells (never solid tiles), so it settles, flows into player
        // tunnels, and quenches lava per the cell rules.
        foreach (var (wsx, wsy) in _run.Planet.WaterSeeds)
            _run.Cells.FillTile(wsx, wsy, Material.Water);

        // Hazard cells: gas rises to the cave roofs, acid settles to the floors. Poured after
        // water so the pre-settle below carries them to rest alongside it.
        foreach (var (gx, gy) in _run.Planet.GasSeeds)
            _run.Cells.FillTile(gx, gy, Material.Gas);
        foreach (var (ax, ay) in _run.Planet.AcidSeeds)
            _run.Cells.FillTile(ax, ay, Material.Acid);

        // Pre-settle the seeded liquids during load: the first ~2s of cell ticks carry every
        // seeded cell awake (tens of ms per tick at Density 8). Burning them here turns a
        // visible gameplay stutter into a slightly longer world-gen pause; after settling,
        // hemmed pool interiors sleep and the steady-state tick is cheap.
        for (var i = 0; i < 120; i++) _run.Cells.Update(1f / 60f);

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
        _run.Titan = new Titan(_run.Planet, titanAngle, def.Titan);
        // DM_HATCH=<seconds> shortens the egg timer for testing (default 10 min).
        if (float.TryParse(Environment.GetEnvironmentVariable("DM_HATCH"), out var hatchAt))
            _run.Titan.EggTimer = hatchAt;
        SpawnDirector.SpawnInitialFauna(_run);
        _run.EarthquakeTimer = 25f * def.QuakeScale;
        _run.SpawnTimer = 6f;
        _run.FaunaTimer = 8f;
        _gameOverReason = "";
        _craftingMenu.Reset();
        _invUi.Reset();
        _screen = GameScreen.Playing;
        // Camera exists except when DM_AUTOSTART triggers a run during Initialize —
        // LoadContent snaps it then.
        _camera?.SnapTo(_run.Player.Position, 0f);
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
        // Loading woke every cell; burn the resettle here like world gen's pre-settle pass.
        for (var i = 0; i < 45; i++) _run.Cells.Update(1f / 60f);
        _gameOverReason = "";
        _craftingMenu.Reset();
        _invUi.Reset();
        _screen = GameScreen.Playing;
        _camera?.SnapTo(_run.Player.Position, 0f);
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
        // No run yet when the game boots to the star map; StartNewRun snaps on planet entry.
        if (_run is not null) _camera.SnapTo(_run.Player.Position, 0f);

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
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        var keys = Keyboard.GetState();
        var mouse = Mouse.GetState();
        _totalTime += dt;

        // Esc quits — except while the crafting menu is open, where the menu's own handler
        // consumes the same press edge to close itself (previously Esc quit the whole game
        // out from under the menu). Edge-triggered so the close-press doesn't also quit.
        if (Pressed(keys, _prevKeys, Keys.Escape)
            && !(_screen == GameScreen.Playing && _craftingMenu.Open))
            Exit();

        // F12 → defer one-shot screenshot until end of next Draw, where the backbuffer
        // holds the fully composited frame (post lighting/bloom/vignette).
        if (Pressed(keys, _prevKeys, Keys.F12)) _screenshotPending = true;

        // Headless verification hook: DM_AUTOSHOT=<seconds> takes a screenshot at that wall
        // time and every 5s after — lets tooling capture frames without input access.
        if (_autoShotAt <= _totalTime)
        {
            _screenshotPending = true;
            _autoShotAt += 5f;
        }

        if (_screen == GameScreen.Overworld)
        {
            UpdateOverworld(keys, mouse);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        if (_screen == GameScreen.GameOver)
        {
            // R returns to the star map — a fresh attempt (or the next planet) is picked there.
            if (Pressed(keys, _prevKeys, Keys.R)) _screen = GameScreen.Overworld;
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
        if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed && !_invUi.Carrying)
        {
            UseSelectedSlot(worldCursor);
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

        // God-mode cheat: P plants a fully built, fuelled, launch-ready ship at the player's
        // feet, skipping the pad/hull/engine/nav-core craft chain — then L lifts off as usual.
        if (_run.Player.FlyMode && Pressed(keys, _prevKeys, Keys.P))
        {
            PlaceLaunchPad();
            _run.ShipStage = 3;
            _run.ShipFuel = FuelToLaunch;
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
        var picked = _run.Cells.CollectInRadius(_run.Player.Position, _run.Player.Radius + 4f);
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
                    _run.Shake = MathF.Max(_run.Shake, MathF.Min(1.5f, p.ExplosionRadius / 60f));
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

        _run.Titan.Update(dt, _run.Planet, _run.Physics, _run.Cells, _run.Player.Position, _run.Boulders, _run.TitanShots);
        // Hatch feedback: a heavy shake + shell-burst the frame the egg cracks open.
        if (_run.Titan.JustHatched)
        {
            _run.Titan.JustHatched = false;
            _run.Shake = MathF.Max(_run.Shake, 1.4f);
            _particles.EmitDust(_run.Titan.Position, 30f);
        }
        // Melee shockwave from a Kong slam / Sandworm eruption — quake already fired inside the
        // Titan; here we knock back and hurt the player if they're inside the radius.
        if (_run.Titan.PendingShockwave is { } sw)
        {
            _run.Titan.PendingShockwave = null;
            _run.Shake = MathF.Max(_run.Shake, 1.0f);
            _particles.EmitDust(sw.pos, 24f);
            var toPlayer = _run.Player.Position - sw.pos;
            var d = toPlayer.Length();
            if (d < sw.radius)
            {
                _run.Player.TakeDamage(sw.damage * (1f - d / sw.radius));
                if (d > 0.01f) _run.Player.Velocity += toPlayer / d * 260f;
            }
        }
        if (_run.Titan.Health <= 0)
        {
            _meta.TitansDefeated++;
            _meta.Save();
            EndRun($"You felled the {TitanName(_run.Def.Titan)}! Run time: {_run.RunTime:0.0}s. Press R for the star map.");
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
            if (shot.Kind == TitanShotKind.Flame) _particles.EmitDust(shot.Position, 2f);
            if (shot.Dead)
            {
                _particles.EmitImpact(shot.Position, ProjectileKind.Cannon);
                _run.TitanShots.RemoveAt(i);
            }
        }

        if (_run.Player.Health <= 0)
        {
            _meta.Deaths++;
            _meta.Save();
            EndRun(_run.Player.Oxygen <= 0f
                ? "You suffocated in the deep. Press R for the star map."
                : "You died. Press R for the star map.");
        }

        // Apply shake decay to camera.
        _run.Shake = MathF.Max(0, _run.Shake - dt * 1.4f);

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
        // Continuous drill chip-stream every frame the drill is held — fires during cooldown
        // so the swing reads as continuous.
        if (tool == MiningTool.Drill && !_run.Player.FlyMode)
        {
            var (ctx, cty) = _run.Planet.WorldToTile(worldCursor);
            if (_run.Planet.Get(ctx, cty) != TileKind.Sky)
            {
                var tilePos = _run.Planet.TileToWorld(ctx, cty);
                var dir = tilePos - _run.Player.Position;
                if (dir.LengthSquared() > 0.001f) dir.Normalize();
                _particles.EmitDrillChips(tilePos, dir, _run.Planet.Get(ctx, cty));
            }
        }

        var broken = _run.Player.TryMine(_run.Planet, _run.Physics, worldCursor, tool);
        if (broken is { } bk)
        {
            if (Tiles.Drop(bk) is not null) _meta.TotalOreMined++;
            var depth = _run.Planet.Radius - (int)((_run.Player.Position - _run.Planet.Center).Length() / Planet.TileSize);
            if (depth > _meta.DeepestDepth) _meta.DeepestDepth = depth;
            var (btx, bty) = _run.Planet.WorldToTile(worldCursor);
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
        _meta.Save();
        EndRun($"You pierced the core. Run time: {_run.RunTime:0.0}s. Press R for the star map.");
    }

    /// <summary>Display name for a boss variant — used in the victory line and the egg HUD.</summary>
    private static string TitanName(TitanKind kind) => kind switch
    {
        TitanKind.Godzilla => "Cinderwyrm",
        TitanKind.Mecha    => "Mecha-Titan",
        TitanKind.Sandworm => "Shai-Hulud",
        TitanKind.Kong     => "Stone Ape",
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
        var surfaceRings = Planet.RingMin + WorldGen.BaselineSurfaceRing;
        return surfaceRings - ringsFromCenter;
    }

    /// <summary>Advance the air supply: refill near the surface, drain with depth (see
    /// <see cref="OxygenRules"/>), and bleed HP once it's empty. Skipped in god mode (kept
    /// topped up so re-entering survival is never an instant suffocation).</summary>
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
        _launchElapsed = 0f;
        _launchVel = 0f;
        _launchShipPos = pad;
        _launchUp = _run.Planet.UpAt(pad);
    }

    /// <summary>One frame of the liftoff climb: build thrust, ride the dwarf up with the ship,
    /// spew exhaust, shake the screen, and hand off to <see cref="FinishLaunch"/> once the
    /// rocket has cleared the surface.</summary>
    private void UpdateLaunch(float dt)
    {
        _launchElapsed += dt;
        // Thrust ramps in over the first moment, then holds — a slow, weighty liftoff building
        // into a real climb rather than an instant jump.
        _launchVel += 150f * dt;
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

        if (_launchElapsed > 3.4f) FinishLaunch();
    }

    /// <summary>The escape ending, reached once the ship has flown clear. Unlocks the next
    /// planet on the star-map chain and banks the same meta bonuses the old rocket escape
    /// granted.</summary>
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
        _meta.Save();
        EndRun($"Liftoff! You escaped {_run.Def.Name} in {_run.RunTime:0.0}s. Press R for the star map.");
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
            if (x >= Planet.RingCount) return true;
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
        // Feed the renderer the current wall-clock so animated decoration (waving grass,
        // hanging vines) advances with the game time rather than the frame index.
        _renderer.Time = (float)gameTime.TotalGameTime.TotalSeconds;

        if (_screen == GameScreen.Overworld)
        {
            DrawOverworld();
            if (_screenshotPending)
            {
                _screenshotPending = false;
                SaveScreenshot();
            }
            base.Draw(gameTime);
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
        _run.Cells.Draw(_renderer, viewCentre, viewRadius);

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

        // Reticle.
        var mouse = Mouse.GetState();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        _renderer.DrawCircle(worldCursor, 3f, new Color(255, 255, 255, 180));

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

        // Titan ranged shots — Godzilla flame (layered orange/yellow flicker) and Mecha laser
        // (cyan bolt with a white core drawn along its velocity).
        foreach (var shot in _run.TitanShots)
        {
            if (shot.Kind == TitanShotKind.Flame)
            {
                var flick = (float)Random.Shared.NextDouble() * 1.6f;
                _renderer.DrawCircle(shot.Position, shot.Radius + flick, new Color(230, 90, 30));
                _renderer.DrawCircle(shot.Position, shot.Radius * 0.6f, new Color(255, 210, 90));
            }
            else
            {
                var ang = MathF.Atan2(shot.Velocity.Y, shot.Velocity.X);
                _renderer.DrawRect(shot.Position, new Vector2(20f, 3.5f), new Color(120, 230, 255, 200), ang);
                _renderer.DrawRect(shot.Position, new Vector2(15f, 1.6f), Color.White, ang);
            }
        }

        // Spaceship build site — the pad plus however many stages are installed. Drawn as
        // world-space rects rotated to local-up, same as every other surface structure. During
        // liftoff the ship is drawn at its climbing position instead of on the pad.
        if (_launching) DrawShip(_launchShipPos, _run.ShipStage);
        else if (_run.PadPos is { } shipPos) DrawShip(shipPos, _run.ShipStage);

        // Storage depot — a squat vault the dwarf banks resources at.
        if (_run.DepotPos is { } depotPos) DrawDepot(depotPos);

        // Kaiju visibility cull. The kaiju's render block does 100+ draw calls (4 legs × IK +
        // 7-node tail + dorsal spines + head + claws), so skipping it when off-screen is a
        // large win. Camera viewport is 1280×720 at zoom 4 → ~320×180 world units, so the
        // visible radius from the camera target is ~200 px; bump to 400 for the kaiju's
        // silhouette (it's huge — body+legs span ~280 px) and a small margin so legs sweeping
        // into view from off-screen aren't suddenly popped in.
        // Boss — a distinct procedural skeleton per variant (upright Godzilla, angular Mecha,
        // legless Sandworm, big-armed Kong) plus the egg / burrow mound, all in TitanRenderer.
        // Culled off-screen: the biggest bodies span ~300 px.
        var titanOnScreen = (_run.Titan.Position - _camera.Target).LengthSquared() < 400f * 400f;
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

        // Titan shots light their surroundings — flame warm, laser cyan.
        foreach (var shot in _run.TitanShots)
            _renderer.AddLight(shot.Position, shot.Kind == TitanShotKind.Flame ? 16f : 18f,
                shot.Kind == TitanShotKind.Flame ? new Color(255, 150, 50) : new Color(120, 230, 255));

        // Glowing creatures — magma slugs read as drifting coals, cave eyes as a faint gleam.
        foreach (var c in _run.Creatures) c.AddLight(_renderer);

        // Glowing particles (ore flecks, projectile sparks, explosion embers) feed back into
        // the lightmap so they actually illuminate the cave wall behind them.
        _particles.AddLights(_renderer);
        // Lava cells along their pool surface light up the cave roof (view-culled).
        _run.Cells.AddLights(_renderer, viewCentre, viewRadius);

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
        if (!_run.Titan.Hatched)
        {
            var secs = MathF.Max(0f, _run.Titan.EggTimer);
            titanStatus = $"{TitanName(_run.Def.Titan).ToUpperInvariant()} EGG  HATCH {(int)(secs / 60):0}:{(int)(secs % 60):00}  (ATTACK TO HATCH: {(int)_run.Titan.EggHealth} HP)";
        }
        else
        {
            titanStatus = $"{TitanName(_run.Def.Titan).ToUpperInvariant()} HP {(int)_run.Titan.Health}/{(int)_run.Titan.MaxHealth}";
        }
        var status = $"{_run.Def.Name.ToUpperInvariant()}   DEPTH {depth}   SHIP: {ship}   {titanStatus}\n" +
                     $"META: ESCAPES {_meta.Escapes}  KILLS {_meta.TitansDefeated}  DEEPEST {_meta.DeepestDepth}";
        var controls = "WASD MOVE  SPACE JUMP  1-9 TOOLBELT  LMB USE  WHEEL CYCLE  Q/E WEAPONS\n" +
                       "C CRAFT  T BEACON  L LAUNCH  B/N DEPOT BANK  F5 SAVE  G GOD MODE";
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
        _invUi.DrawInventoryPanel(_renderer, _run.Player, VirtualWidth);
        _invUi.DrawToolbelt(_renderer, _run.Player, VirtualWidth, VirtualHeight);
        _invUi.DrawCarry(_renderer, _run.Player);

        DrawHoverDebugLabel();

        if (_craftingMenu.Open)
            _craftingMenu.Draw(_renderer, _run.Player.Inventory, IsOwned, VirtualWidth, VirtualHeight);

        if (_screen == GameScreen.GameOver) DrawGameOverOverlay();

        if (_screenshotPending)
        {
            _screenshotPending = false;
            SaveScreenshot();
        }

        base.Draw(gameTime);
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

    private void DrawGameOverOverlay()
    {
        var sb = _renderer.Batch;
        sb.Begin();
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 180));
        sb.End();
        _renderer.DrawCenteredText(_gameOverReason, VirtualWidth, VirtualHeight, Color.White, scale: 3);
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

    // ─── Star map (overworld) ─────────────────────────────────────────────────

    private void UpdateOverworld(KeyboardState keys, MouseState mouse)
    {
        var count = PlanetDefs.All.Length;
        var unlocked = Math.Min(_meta.PlanetsUnlocked, count);

        // R resumes the suspended run, if any.
        if (Pressed(keys, _prevKeys, Keys.R) && RunSave.Exists)
        {
            ResumeRun();
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.Left) || Pressed(keys, _prevKeys, Keys.A))
            _overworldCursor = (_overworldCursor - 1 + count) % count;
        if (Pressed(keys, _prevKeys, Keys.Right) || Pressed(keys, _prevKeys, Keys.D))
            _overworldCursor = (_overworldCursor + 1) % count;

        // Mouse: click a planet to select it; click the selected planet to depart.
        var clicked = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton != ButtonState.Pressed;
        var launch = Pressed(keys, _prevKeys, Keys.Enter) || Pressed(keys, _prevKeys, Keys.Space);
        if (clicked)
        {
            for (var i = 0; i < count; i++)
            {
                if ((new Vector2(mouse.X, mouse.Y) - PlanetMapCentre(i)).Length() > PlanetMapRadius + 10f) continue;
                if (i == _overworldCursor) launch = true;
                else _overworldCursor = i;
                break;
            }
        }

        if (launch && _overworldCursor < unlocked)
            StartNewRun(PlanetDefs.All[_overworldCursor]);
    }

    private const float PlanetMapRadius = 38f;

    /// <summary>Screen position of planet i on the star map — evenly spaced along a gentle
    /// sine arc so the chain reads as a route, not a menu row.</summary>
    private static Vector2 PlanetMapCentre(int i)
    {
        var count = PlanetDefs.All.Length;
        var x = (i + 1f) / (count + 1f) * VirtualWidth;
        var y = VirtualHeight * 0.42f + MathF.Sin(i * 1.9f) * 55f;
        return new Vector2(x, y);
    }

    private void DrawOverworld()
    {
        GraphicsDevice.Clear(new Color(6, 7, 14));
        var sb = _renderer.Batch;
        var count = PlanetDefs.All.Length;
        var unlocked = Math.Min(_meta.PlanetsUnlocked, count);

        sb.Begin(samplerState: SamplerState.PointClamp);

        // Starfield — hashed positions, twinkling with wall time.
        for (var i = 0; i < 170; i++)
        {
            var h = (i * 1013904223 + 1664525) & 0x7fffffff;
            var x = (h >> 7) % VirtualWidth;
            var y = (h >> 17) % VirtualHeight;
            var tw = MathF.Sin(_totalTime * (0.6f + (h & 3) * 0.5f) + i) * 0.5f + 0.5f;
            var bright = 0.25f + tw * 0.55f;
            var size = (h & 15) == 0 ? 2 : 1;
            sb.Draw(_renderer.Pixel, new Rectangle(x, y, size, size), new Color(200, 210, 235) * bright);
        }

        // Route line between stops — dotted, dimmer past the unlock frontier.
        for (var i = 0; i < count - 1; i++)
        {
            var a = PlanetMapCentre(i);
            var b = PlanetMapCentre(i + 1);
            var col = i < unlocked - 1 ? new Color(120, 130, 160) : new Color(45, 48, 62);
            for (var t = 0.12f; t < 0.9f; t += 0.07f)
            {
                var p = Vector2.Lerp(a, b, t);
                sb.Draw(_renderer.Pixel, new Rectangle((int)p.X, (int)p.Y, 2, 2), col);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var def = PlanetDefs.All[i];
            var c = PlanetMapCentre(i);
            var locked = i >= unlocked;

            // Selection halo behind the disc.
            if (i == _overworldCursor)
                FillCircleScreen(sb, c, PlanetMapRadius + 5f, new Color(255, 225, 140, 90));

            var body = locked ? new Color(40, 42, 52) : def.MapColor;
            var accent = locked ? new Color(60, 62, 72) : def.MapAccent;
            FillCircleScreen(sb, c, PlanetMapRadius, body);
            // Terminator shading: darker offset disc clipped by drawing it after the body.
            FillCircleScreen(sb, c + new Vector2(9, 7), PlanetMapRadius - 10f,
                new Color(body.R / 2, body.G / 2, body.B / 2));
            // Polar highlight.
            FillCircleScreen(sb, c - new Vector2(10, 9), PlanetMapRadius * 0.32f, accent);
        }
        sb.End();

        // Text pass — DrawText handles its own batches.
        var title = "STAR MAP";
        _renderer.DrawText(title, new Vector2((VirtualWidth - _renderer.MeasureText(title, 4)) / 2f, 46), Color.White, 4);
        var subtitle = "A/D SELECT   ENTER DEPART   BUILD A SHIP TO REACH THE NEXT WORLD";
        _renderer.DrawText(subtitle, new Vector2((VirtualWidth - _renderer.MeasureText(subtitle)) / 2f, 88), new Color(150, 155, 175));
        if (RunSave.Exists)
        {
            var resume = "R RESUME SAVED RUN";
            _renderer.DrawText(resume,
                new Vector2((VirtualWidth - _renderer.MeasureText(resume, 2)) / 2f, 110), new Color(140, 220, 140), 2);
        }

        for (var i = 0; i < count; i++)
        {
            var def = PlanetDefs.All[i];
            var c = PlanetMapCentre(i);
            var locked = i >= unlocked;
            var name = locked ? "???" : def.Name.ToUpperInvariant();
            _renderer.DrawText(name,
                new Vector2(c.X - _renderer.MeasureText(name, 2) / 2f, c.Y + PlanetMapRadius + 12), locked ? new Color(110, 112, 125) : Color.White, 2);
            if (_meta.PlanetsEscaped.Contains(def.Id))
                _renderer.DrawText("ESCAPED",
                    new Vector2(c.X - _renderer.MeasureText("ESCAPED") / 2f, c.Y + PlanetMapRadius + 34), new Color(140, 220, 140));
        }

        // Info panel for the selected planet.
        var sel = PlanetDefs.All[_overworldCursor];
        var selLocked = _overworldCursor >= unlocked;
        var infoY = VirtualHeight - 130f;
        if (selLocked)
        {
            var lockedMsg = "LOCKED: ESCAPE THE PREVIOUS WORLD TO CHART A COURSE HERE";
            _renderer.DrawText(lockedMsg,
                new Vector2((VirtualWidth - _renderer.MeasureText(lockedMsg, 2)) / 2f, infoY), new Color(200, 130, 120), 2);
        }
        else
        {
            var line1 = sel.Name.ToUpperInvariant() + ": " + sel.Tagline.ToUpperInvariant();
            _renderer.DrawText(line1,
                new Vector2((VirtualWidth - _renderer.MeasureText(line1, 2)) / 2f, infoY), Color.White, 2);
            var line2 = $"SHIP NAV CORE NEEDS: {sel.ShipOreCount} {Tiles.ResourceLabel(sel.ShipOre)} + 3 CRYSTAL";
            _renderer.DrawText(line2,
                new Vector2((VirtualWidth - _renderer.MeasureText(line2)) / 2f, infoY + 30), new Color(190, 195, 215));
            var hazard = sel.LavaFillFrac >= 0.55f ? "EXTREME" : sel.CaveSpawnCap >= 18 ? "HIGH" : "MODERATE";
            var line3 = $"HAZARD: {hazard}   WATER: {(sel.HasWater ? "YES" : "NONE")}   QUAKES: {(sel.QuakeScale < 0.7f ? "FREQUENT" : "OCCASIONAL")}";
            _renderer.DrawText(line3,
                new Vector2((VirtualWidth - _renderer.MeasureText(line3)) / 2f, infoY + 48), new Color(150, 155, 175));
            // Toxin warning line — only shown for worlds that seed gas/acid so it reads as a
            // genuine caution rather than boilerplate.
            var toxins = (sel.SeedsGas, sel.SeedsAcid) switch
            {
                (true, true)  => "TOXINS: FLAMMABLE GAS + CORROSIVE ACID",
                (true, false) => "TOXINS: FLAMMABLE GAS POCKETS",
                (false, true) => "TOXINS: CORROSIVE ACID POOLS",
                _             => "",
            };
            if (toxins.Length > 0)
                _renderer.DrawText(toxins,
                    new Vector2((VirtualWidth - _renderer.MeasureText(toxins)) / 2f, infoY + 66), new Color(180, 210, 120));
            if (sel.OxygenDrainScale >= 1.5f)
            {
                const string air = "THIN ATMOSPHERE: AIR BURNS FAST AT DEPTH";
                _renderer.DrawText(air,
                    new Vector2((VirtualWidth - _renderer.MeasureText(air)) / 2f, infoY + (toxins.Length > 0 ? 84 : 66)), new Color(200, 180, 130));
            }
        }

        var meta = $"ESCAPES {_meta.Escapes}   TITAN KILLS {_meta.TitansDefeated}   DEEPEST {_meta.DeepestDepth}   DEATHS {_meta.Deaths}";
        _renderer.DrawText(meta,
            new Vector2((VirtualWidth - _renderer.MeasureText(meta)) / 2f, VirtualHeight - 34), new Color(120, 125, 145));
    }

    /// <summary>Filled circle in screen space, one horizontal strip per row — the star map's
    /// only primitive (world-space DrawCircle goes through the camera transform, so it can't
    /// be reused here).</summary>
    private void FillCircleScreen(SpriteBatch sb, Vector2 centre, float radius, Color color)
    {
        var r = (int)radius;
        for (var dy = -r; dy <= r; dy++)
        {
            var half = (int)MathF.Sqrt(radius * radius - dy * dy);
            sb.Draw(_renderer.Pixel,
                new Rectangle((int)centre.X - half, (int)centre.Y + dy, half * 2, 1), color);
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
