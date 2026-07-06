using System;
using System.Collections.Generic;
using System.IO;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.Systems;
using DwarfMiner.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace DwarfMiner;

public sealed class DwarfMinerGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private Renderer _renderer = null!;
    private Camera _camera = null!;
    private Texture2D _dwarfTex = null!;
    private PlayerSprite? _playerSprite;
    private float _playerFacing = 1f;
    private Planet _planet = null!;
    private Physics _physics = null!;
    private Player _player = null!;
    private Titan _titan = null!;
    private MetaSave _meta = null!;
    private Cells _cells = null!;

    private readonly List<Creature> _creatures = new();
    private readonly List<Projectile> _projectiles = new();
    private readonly List<FallingBoulder> _boulders = new();
    private readonly List<Sentry> _sentries = new();
    private readonly Particles _particles = new();

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private float _earthquakeTimer;
    private float _spawnTimer;
    private float _faunaTimer;
    private float _shake;
    private bool _gameOver;
    private string _gameOverReason = "";
    private float _runTime;
    private bool _hasCannon;
    private bool _screenshotPending;
    private float _autoShotAt =
        float.TryParse(Environment.GetEnvironmentVariable("DM_AUTOSHOT"), out var s)
            ? s : float.PositiveInfinity;

    /// <summary>Crafting menu state. While open, mouse/movement input still drives the world
    /// but key events route to the menu (up/down to scroll, Enter to craft, C/Esc to close).
    /// The cursor is the index into Crafting.All; only recipes whose visibility predicate
    /// passes (e.g. pickaxe_iv hidden until pickaxe_iii is built) are shown.</summary>
    private bool _craftingOpen;
    private int _craftingCursor;

    /// <summary>Drag-and-drop carry state for the inventory/toolbelt UI. While non-null, the
    /// player has picked up an item: <c>Id</c> is the inventory id; <c>FromSlot</c> is the
    /// toolbelt slot it came from (-1 if from the inventory panel). Dropping clears this.
    /// Click-outside cancels and returns the item to its source.</summary>
    private (string Id, int FromSlot)? _carry;

    /// <summary>Cached screen-space rectangles for inventory + toolbelt hit-testing. Rebuilt
    /// every Draw and consumed by HandleInventoryUi on the next Update — the layout doesn't
    /// move per frame so 1-frame staleness is invisible.</summary>
    private readonly Dictionary<string, Rectangle> _invHitTest = new();
    private readonly Rectangle[] _toolbeltHitTest = new Rectangle[Toolbelt.SlotCount];

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
    }

    protected override void Initialize()
    {
        _meta = MetaSave.Load();
        StartNewRun();
        base.Initialize();
    }

    private void StartNewRun()
    {
        var seed = (int)DateTime.Now.Ticks;
        _planet = WorldGen.Generate(seed);
        _cells = new Cells(_planet);
        _physics = new Physics(_planet, _cells);
        // Lava seeding: any cave (Sky tile) within 45% of the planet radius gets filled with
        // lava. So the inner half of the planet — from roughly the middle layer inward — has
        // lava pooled in any cavities, with the densest lava near the Core.
        _cells.FillSkyTilesWithin(_planet.Radius * 0.45f, Material.Lava);

        // Water seeding: world gen recorded lake-basin and reservoir tiles; pour the cells in
        // now. Water is always sim cells (never solid tiles), so it settles, flows into player
        // tunnels, and quenches lava per the cell rules.
        foreach (var (wsx, wsy) in _planet.WaterSeeds)
            _cells.FillTile(wsx, wsy, Material.Water);

        // Pre-settle the seeded liquids during load: the first ~2s of cell ticks carry every
        // seeded cell awake (tens of ms per tick at Density 8). Burning them here turns a
        // visible gameplay stutter into a slightly longer world-gen pause; after settling,
        // hemmed pool interiors sleep and the steady-state tick is cheap.
        for (var i = 0; i < 120; i++) _cells.Update(1f / 60f);

        // Spawn the dwarf on top of whatever mountain is at angle -π/2 — walk down from
        // far above until the first solid tile, then float a few pixels above it.
        var surfacePos = FindSurfaceSpawn(-MathF.PI / 2f, _planet.Radius);
        _player = new Player(surfacePos)
        {
            // Start in god-mode for testing; G toggles it off (and on again) in-game. The
            // toggle drives ghost flight, super-pickaxe power, and extended mine range as a
            // single bundle — see Player.EffectivePickaxePower / EffectiveMineRange.
            FlyMode = true,
            // Apply meta-progress: a player who has previously escaped starts with a
            // higher-tier pickaxe so subsequent runs are slightly easier.
            PickaxeTier = Math.Max(1, _meta.StartingPickaxePower),
        };
        _hasCannon = _meta.StartWithCannon;
        _titan = new Titan(_planet, MathF.PI * 0.6f);
        _creatures.Clear();
        _projectiles.Clear();
        _boulders.Clear();
        _sentries.Clear();
        SpawnInitialFauna();
        _earthquakeTimer = 25f;
        _spawnTimer = 6f;
        _faunaTimer = 8f;
        _shake = 0;
        _gameOver = false;
        _gameOverReason = "";
        _runTime = 0;
        _craftingOpen = false;
        _craftingCursor = 0;
    }

    protected override void LoadContent()
    {
        _renderer = new Renderer(GraphicsDevice);
        Icons.Build(GraphicsDevice);
        _camera = new Camera
        {
            ViewportSize = new Point(VirtualWidth, VirtualHeight),
            Zoom = 4.0f,
        };
        _camera.SnapTo(_player.Position, 0f);

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

        if (keys.IsKeyDown(Keys.Escape)) Exit();

        // F12 → defer one-shot screenshot until end of next Draw, where the backbuffer
        // holds the fully composited frame (post lighting/bloom/vignette).
        if (Pressed(keys, _prevKeys, Keys.F12)) _screenshotPending = true;

        // Headless verification hook: DM_AUTOSHOT=<seconds> takes a screenshot at that run
        // time and every 5s after — lets tooling capture frames without input access.
        if (_autoShotAt <= _runTime)
        {
            _screenshotPending = true;
            _autoShotAt += 5f;
        }

        if (_gameOver)
        {
            if (Pressed(keys, _prevKeys, Keys.R)) StartNewRun();
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        // Crafting menu intercepts most input — world keeps simulating but movement/mining
        // stops so the player isn't fighting the game while shopping for upgrades.
        if (_craftingOpen)
        {
            UpdateCraftingMenu(keys);
            _physics.Update(dt);
            _particles.Update(dt, _planet);
            _cells.Update(dt);
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }
        if (Pressed(keys, _prevKeys, Keys.C)) { _craftingOpen = true; _craftingCursor = 0; _prevKeys = keys; _prevMouse = mouse; base.Update(gameTime); return; }

        _runTime += dt;

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
        if (Pressed(keys, _prevKeys, Keys.G)) _player.FlyMode = !_player.FlyMode;

        _player.Update(dt, _planet, moveAxis, jumpHeld, verticalAxis);

        // Camera follows player, rotating so up = away from planet center.
        var up = _planet.UpAt(_player.Position);
        _camera.Follow(_player.Position, up, dt);

        // Mouse → world cursor. Account for screen shake.
        var screenMouse = new Vector2(mouse.X, mouse.Y);
        var worldCursor = _camera.ScreenToWorld(screenMouse);

        // Number-row 1..9 selects a toolbelt slot. Holding the slot key (or LMB on the slot in
        // the HUD) drives the selected slot's action. Selection is sticky across frames.
        for (var s = 0; s < Toolbelt.SlotCount; s++)
        {
            var k = Keys.D1 + s;
            if (Pressed(keys, _prevKeys, k)) _player.Toolbelt.Selected = s;
        }
        // Mouse-wheel cycles selection — handy for fast tool swaps without leaving the home row.
        if (mouse.ScrollWheelValue != _prevMouse.ScrollWheelValue)
        {
            var dir = mouse.ScrollWheelValue > _prevMouse.ScrollWheelValue ? -1 : 1;
            _player.Toolbelt.Selected = (_player.Toolbelt.Selected + dir + Toolbelt.SlotCount) % Toolbelt.SlotCount;
        }

        // Drag-and-drop UI input runs on click edges and updates the carry state. If the click
        // landed on a UI element, we swallow it so the world doesn't also receive it as an LMB
        // dispatch this frame.
        var screenPos = new Vector2(mouse.X, mouse.Y);
        var lmbPressed = mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton != ButtonState.Pressed;
        var rmbPressed = mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton != ButtonState.Pressed;
        var clickConsumed = HandleInventoryUi(screenPos, lmbPressed, rmbPressed);

        // Held LMB activates the selected slot's action. UseSelectedSlot is the single place
        // that maps id → in-world action (mine / shoot / place / throw / heal / …).
        if (mouse.LeftButton == ButtonState.Pressed && !clickConsumed && _carry is null)
        {
            UseSelectedSlot(worldCursor);
        }

        // T recalls to the last placed Beacon — kept as a key because recall is a unique
        // verb that doesn't fit the "use selected slot" pattern (the beacon slot already
        // does *placement*; recall is a different action on the same data).
        if (Pressed(keys, _prevKeys, Keys.T) && _player.BeaconWorld is { } bp)
        {
            BeaconRecall(bp);
        }

        // Launch rocket with L if 5 parts held and player on the surface.
        if (Pressed(keys, _prevKeys, Keys.L)) TryLaunchRocket();

        // Physics + particles + cells update.
        _physics.Update(dt);
        _particles.Update(dt, _planet);

        // Sweep dust within a body's reach into the inventory. Each cell carries a fractional
        // resource amount tied to its source TileKind; Cells handles the per-id accumulator and
        // hands back whole units once they cross 1. Done before the cells tick so the
        // collection's WakeNeighbors calls land in `_next`, which the upcoming Update swaps into
        // `_active` and processes immediately — dust above a collected cell falls the same frame
        // instead of one frame later.
        var picked = _cells.CollectInRadius(_player.Position, _player.Radius + 4f);
        if (picked is not null)
            foreach (var (id, count) in picked)
                _player.Inventory.Add(id, count);

        _cells.Update(dt);
        if (_physics.CollapsesThisTick > 0) _shake = MathF.Max(_shake, MathHelper.Clamp(_physics.CollapsesThisTick / 80f, 0f, 1.5f));

        // Earthquakes — global shake every so often.
        _earthquakeTimer -= dt;
        if (_earthquakeTimer <= 0)
        {
            var anglesAround = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var pos = _planet.Center + new Vector2(MathF.Cos(anglesAround), MathF.Sin(anglesAround)) * _planet.Radius * Planet.TileSize * 0.5f;
            _physics.Earthquake(pos, 200f, 2);
            _earthquakeTimer = 30f + (float)Random.Shared.NextDouble() * 20f;
            _shake = MathF.Max(_shake, 1.0f);
        }

        // Spawn creatures inside caves. Cap counts cave dwellers only — surface/sky fauna
        // have their own population loop below.
        _spawnTimer -= dt;
        if (_spawnTimer <= 0 && CountKindsNear(550f, cave: true) < 14)
        {
            TrySpawnCreature();
            _spawnTimer = 2.3f + (float)Random.Shared.NextDouble() * 1.7f;
        }

        // Fauna upkeep: keep the surface grazed and the sky populated. Slow cadence — these
        // are ambience, not a threat escalation.
        _faunaTimer -= dt;
        if (_faunaTimer <= 0)
        {
            _faunaTimer = 6f + (float)Random.Shared.NextDouble() * 4f;
            if (CountKindsNear(700f, surface: true) < 7) TrySpawnSurfaceAnimal();
            if (CountKindsNear(700f, sky: true) < 6) TrySpawnSkyAnimal();
        }

        // Update entities. Creatures that have drifted far outside the player's neighbourhood
        // are recycled — they'd never be met again, they eat sim time, and every recycled
        // body frees local-population budget so the spawner keeps the area around the player
        // stocked as they travel.
        for (var i = _creatures.Count - 1; i >= 0; i--)
        {
            var c = _creatures[i];
            c.Update(dt, _planet, _physics, _cells, _player);
            if (c.Health <= 0
                || (c.Position - _player.Position).LengthSquared() > 1000f * 1000f)
            {
                _creatures.RemoveAt(i);
            }
        }

        for (var i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            p.Update(dt, _planet);

            // Hit creatures. Pierce policy: -1 = unlimited (AoE explosives), N = up to N hits.
            for (var j = _creatures.Count - 1; j >= 0; j--)
            {
                var c = _creatures[j];
                if ((c.Position - p.Position).LengthSquared() < (c.Radius + p.Radius) * (c.Radius + p.Radius))
                {
                    c.Health -= p.Damage;
                    c.HitFlash = 0.15f;
                    if (p.BurnSeconds > 0f) c.BurnSeconds = MathF.Max(c.BurnSeconds, p.BurnSeconds);
                    if (p.FreezeSeconds > 0f) c.FreezeSeconds = MathF.Max(c.FreezeSeconds, p.FreezeSeconds);

                    if (p.CreaturePierces > 0)
                    {
                        p.CreaturePierces--;
                        if (p.CreaturePierces <= 0) { p.Dead = true; break; }
                    }
                    // CreaturePierces == -1 → projectile keeps going through the swarm.
                }
            }

            // Hit titan. Harpoon hits big — its damage stat is already large; just register
            // the strike. Single titan, no list iteration needed.
            if (!p.Dead && (_titan.Position - p.Position).Length() < _titan.Radius + p.Radius)
            {
                _titan.Health -= p.Damage;
                _titan.HitFlash = 0.15f;
                _titan.OnDamage();   // wakes the kaiju up and resets its 10s aggro timer
                if (p.CreaturePierces == 1) p.Dead = true;
            }

            // Hit sentries (creature collisions chew through one over time, projectiles don't
            // hit them). We instead handle creature contact damage to sentries down below in
            // the sentry-update block.

            if (p.Dead)
            {
                _particles.EmitImpact(p.Position, p.Kind);
                // Apply explosion AoE damage at impact for any projectile that has a non-zero
                // ExplosionRadius. The crater-tiles dig is already done by Projectile itself.
                if (p.ExplosionRadius > 0f) ApplyExplosionDamage(p);
                _projectiles.RemoveAt(i);
            }
        }

        // Sentries — auto-fire at nearby creatures, take contact damage, expire when dead.
        for (var i = _sentries.Count - 1; i >= 0; i--)
        {
            var s = _sentries[i];
            s.Update(dt, _planet, _creatures, FireSentryShot);
            // Creatures that touch a sentry chew it down. Each contact frame deals
            // 8 dmg/sec → ~3.7s to kill a fresh sentry.
            foreach (var c in _creatures)
            {
                if ((c.Position - s.Position).LengthSquared() < (c.Radius + s.Radius) * (c.Radius + s.Radius))
                {
                    s.TakeHit(8f * dt);
                }
            }
            if (s.Dead)
            {
                _particles.EmitDust(s.Position, 8f);
                _shake = MathF.Max(_shake, 0.3f);
                _sentries.RemoveAt(i);
            }
        }

        _titan.Update(dt, _planet, _physics, _cells, _player.Position, _boulders);
        if (_titan.Health <= 0)
        {
            _meta.TitansDefeated++;
            _meta.Save();
            _gameOver = true;
            _gameOverReason = $"You felled the Titan! Run time: {_runTime:0.0}s. Press R to play again.";
        }

        for (var i = _boulders.Count - 1; i >= 0; i--)
        {
            var b = _boulders[i];
            b.Update(dt, _planet, _physics, _player);
            if (b.Dead)
            {
                _particles.EmitDust(b.Position, 14f);
                _shake = MathF.Max(_shake, 0.6f);
                _boulders.RemoveAt(i);
            }
        }

        if (_player.Health <= 0)
        {
            _meta.Deaths++;
            _meta.Save();
            _gameOver = true;
            _gameOverReason = "You died. Press R to try again.";
        }

        // Apply shake decay to camera.
        _shake = MathF.Max(0, _shake - dt * 1.4f);

        _prevKeys = keys;
        _prevMouse = mouse;
        base.Update(gameTime);
    }

    /// <summary>Dispatch the currently-selected toolbelt slot to its in-world action. This
    /// is the single mapping table from slot id → behaviour, called every frame LMB is held
    /// (most slots are continuous: mining drills, sweeping ladders, etc.). Stackable
    /// consumables short-circuit if their inventory is empty.</summary>
    private void UseSelectedSlot(Vector2 worldCursor)
    {
        var id = _player.Toolbelt.Current;
        if (id is null) return;

        switch (id)
        {
            case "pickaxe": DoMine(worldCursor, MiningTool.Pickaxe); break;
            case "drill":   if (_player.HasDrill)  DoMine(worldCursor, MiningTool.Drill); break;
            case "hammer":  if (_player.HasHammer) DoMine(worldCursor, MiningTool.Hammer); break;
            case "bullets": if (_player.ShootCooldown <= 0) FireBullet(worldCursor); break;
            case "cannon":  if (_hasCannon && _player.ShootCooldown <= 0) FireCannon(worldCursor); break;
            case "blocks":  _player.TryPlace(_planet, _physics, worldCursor); break;
            case "nuke":      if (_player.Inventory.TryConsume("nuke", 1))     FireNuke(worldCursor); break;
            case "harpoon":   if (_player.Inventory.TryConsume("harpoon", 1))  FireHarpoon(worldCursor); break;
            case "dynamite":  if (_player.Inventory.TryConsume("dynamite", 1)) FireDynamite(worldCursor); break;
            case "poultice":  if (_player.Inventory.TryConsume("poultice", 1)) UseHealPotion(); break;
            case "core_drill": if (_player.HasCoreDrill) TryCoreDrill(); break;
            case "sentry":    if (_player.Inventory.TryConsume("sentry", 1))   PlaceSentryAtFeet(); break;
            // Placeable build tiles — go through Player.TryPlaceBuildId so the passable / range
            // / inventory rules stay in one place.
            case "support":
            case "reinforced_support":
            case "ladder":
            case "rail":
            case "glowshroom":
            case "beacon":
                _player.TryPlaceBuildId(_planet, _physics, worldCursor, id);
                break;
        }
    }

    /// <summary>Mining wrapper for UseSelectedSlot — handles the per-tool chip / hammer-shard /
    /// drill-jet effects that used to live inline in Update. Pulled out so the dispatcher
    /// stays one-liner-per-case.</summary>
    private void DoMine(Vector2 worldCursor, MiningTool tool)
    {
        // Continuous drill chip-stream every frame the drill is held — fires during cooldown
        // so the swing reads as continuous.
        if (tool == MiningTool.Drill && !_player.FlyMode)
        {
            var (ctx, cty) = _planet.WorldToTile(worldCursor);
            if (_planet.Get(ctx, cty) != TileKind.Sky)
            {
                var tilePos = _planet.TileToWorld(ctx, cty);
                var dir = tilePos - _player.Position;
                if (dir.LengthSquared() > 0.001f) dir.Normalize();
                _particles.EmitDrillChips(tilePos, dir, _planet.Get(ctx, cty));
            }
        }

        var broken = _player.TryMine(_planet, _physics, worldCursor, tool);
        if (broken is { } bk)
        {
            if (Tiles.Drop(bk) is not null) _meta.TotalOreMined++;
            var depth = _planet.Radius - (int)((_player.Position - _planet.Center).Length() / Planet.TileSize);
            if (depth > _meta.DeepestDepth) _meta.DeepestDepth = depth;
            var (btx, bty) = _planet.WorldToTile(worldCursor);
            if (tool == MiningTool.Hammer && Tiles.Hardness(bk) >= 4)
            {
                _particles.EmitHammerImpact(_planet.TileToWorld(btx, bty), bk);
                _shake = MathF.Max(_shake, 0.4f);
            }
            else
            {
                _particles.EmitChips(_planet.TileToWorld(btx, bty), bk);
            }
            _cells.SpawnDustInTile(btx, bty, bk);
        }
    }

    /// <summary>Drag-and-drop UI handler. Returns true iff the click landed on a UI element
    /// (so the world doesn't also receive it as an LMB world-action this frame). Click on
    /// inventory/toolbelt → pick up; click on toolbelt slot while carrying → drop. Right-click
    /// a toolbelt slot → unequip back to inventory (stackables only). Click outside any UI
    /// while carrying → cancel.</summary>
    private bool HandleInventoryUi(Vector2 screenPos, bool lmbPressed, bool rmbPressed)
    {
        if (!lmbPressed && !rmbPressed) return false;

        // RMB on a toolbelt slot: unequip stackable items back to inventory. Permanent slots
        // stay put — there's nowhere else for them to live.
        if (rmbPressed)
        {
            for (var s = 0; s < Toolbelt.SlotCount; s++)
            {
                if (!_toolbeltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
                var id = _player.Toolbelt.Slots[s];
                if (id is null) return true;
                if (Toolbelt.IsPermanent(id)) return true;
                _player.Toolbelt.Slots[s] = null;
                return true;
            }
            return false;
        }

        // LMB: pick-up vs. drop based on whether we're already carrying.
        if (_carry is null)
        {
            // Click on a toolbelt slot just selects it. Rearranging is done via RMB-unequip +
            // inventory-drag — picking up directly from a slot would conflict with select.
            for (var s = 0; s < Toolbelt.SlotCount; s++)
            {
                if (!_toolbeltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
                _player.Toolbelt.Selected = s;
                return true;
            }
            // Click on an inventory row picks up that id (drag begins). The pickup is non-
            // destructive — inventory count stays the same; dropping just installs a slot
            // pointer to the same inventory entry.
            foreach (var (id, rect) in _invHitTest)
            {
                if (rect.Contains((int)screenPos.X, (int)screenPos.Y))
                {
                    _carry = (id, -1);
                    return true;
                }
            }
            return false;
        }

        // Carrying — drop on a toolbelt slot, or cancel by clicking elsewhere.
        var carry = _carry.Value;
        for (var s = 0; s < Toolbelt.SlotCount; s++)
        {
            if (!_toolbeltHitTest[s].Contains((int)screenPos.X, (int)screenPos.Y)) continue;
            var prev = _player.Toolbelt.Slots[s];
            _player.Toolbelt.Slots[s] = carry.Id;
            // If the destination held a permanent tool, push it to the first empty slot so
            // the player doesn't lose it. Stackable displacement is fine — it lives in the
            // inventory regardless of belt presence.
            if (prev is not null && Toolbelt.IsPermanent(prev))
            {
                var empty = _player.Toolbelt.FirstEmpty();
                if (empty >= 0) _player.Toolbelt.Slots[empty] = prev;
            }
            _player.Toolbelt.Selected = s;
            _carry = null;
            return true;
        }
        // Click outside any toolbelt slot → cancel the drag. Source state is unchanged.
        _carry = null;
        return true;
    }

    /// <summary>Fire a basic bullet from the "bullets" slot. Intrinsic — always available;
    /// no cannon needed. Low damage, fast cadence.</summary>
    private void FireBullet(Vector2 worldCursor)
    {
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 420f, 6f, 1.4f, ProjectileKind.Bullet));
        _player.ShootCooldown = 0.18f;
    }

    /// <summary>Fire the cannon. Consumes the highest-tier shell in inventory before falling
    /// back to the regular cannon round. Per-shell stats are kept here so the dispatch stays
    /// declarative.</summary>
    private void FireCannon(Vector2 worldCursor)
    {
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        if (_player.Inventory.TryConsume("ammo_diamond", 1))
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 300f, 80f, 1.8f, ProjectileKind.CannonDiamond));
            _player.ShootCooldown = 0.7f;
            _shake = MathF.Max(_shake, 0.5f);
        }
        else if (_player.Inventory.TryConsume("ammo_sapphire", 1))
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 320f, 35f, 1.7f, ProjectileKind.CannonSapphire));
            _player.ShootCooldown = 0.55f;
        }
        else if (_player.Inventory.TryConsume("ammo_ruby", 1))
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 320f, 32f, 1.7f, ProjectileKind.CannonRuby));
            _player.ShootCooldown = 0.55f;
        }
        else if (_player.Inventory.TryConsume("ammo_silver", 1))
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 380f, 22f, 1.6f, ProjectileKind.CannonSilver));
            _player.ShootCooldown = 0.45f;
        }
        else
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 320f, 25f, 1.6f, ProjectileKind.Cannon));
            _player.ShootCooldown = 0.55f;
        }
    }

    private void FireNuke(Vector2 worldCursor)
    {
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 240f, 1500f, 3f, ProjectileKind.Nuke));
        _player.ShootCooldown = 0.6f;
    }

    private void FireDynamite(Vector2 worldCursor)
    {
        // Dynamite is lobbed like a grenade — gravity arcs it. The launch speed is calibrated
        // so a click within mining range lands close to the cursor; far cursors overshoot
        // because gravity drags the shorter-flight throws.
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        var dist = dir.Length();
        dir /= dist;
        // Lob speed scales with throw distance up to a cap. Plus a small upward kick along
        // planet-up so the stick arcs visibly instead of skimming the floor.
        var speed = MathHelper.Clamp(110f + dist * 0.8f, 110f, 220f);
        var up = _planet.UpAt(_player.Position);
        var velocity = dir * speed + up * 60f;
        _projectiles.Add(new Projectile(_player.Position + dir * 6f, velocity, 50f, 2.0f, ProjectileKind.Dynamite));
        _player.ShootCooldown = 0.3f;
    }

    private void FireHarpoon(Vector2 worldCursor)
    {
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 520f, 600f, 2.2f, ProjectileKind.Harpoon));
        _player.ShootCooldown = 0.8f;
        _shake = MathF.Max(_shake, 0.5f);
    }

    /// <summary>Sentry-emitted bullet. Lower damage and speed than the player's bullet so the
    /// turrets feel like supplementary support, not an instant clear button.</summary>
    private void FireSentryShot(Vector2 muzzle, Vector2 dir)
    {
        _projectiles.Add(new Projectile(muzzle, dir * Sentry.BulletSpeed, Sentry.BulletDamage, 1.0f, ProjectileKind.Bullet));
    }

    /// <summary>Apply radial damage from an explosive projectile that just died. The crater
    /// (terrain dig) is handled inside Projectile; this is the creature/Titan damage step
    /// so AoE shells can hurt a swarm even after passing through them.</summary>
    private void ApplyExplosionDamage(Projectile p)
    {
        var rSq = p.ExplosionRadius * p.ExplosionRadius;
        foreach (var c in _creatures)
        {
            if ((c.Position - p.Position).LengthSquared() < rSq)
            {
                c.Health -= p.Damage * 0.5f;   // half of direct hit (already took full on contact)
                c.HitFlash = 0.15f;
                if (p.BurnSeconds > 0f) c.BurnSeconds = MathF.Max(c.BurnSeconds, p.BurnSeconds);
                if (p.FreezeSeconds > 0f) c.FreezeSeconds = MathF.Max(c.FreezeSeconds, p.FreezeSeconds);
            }
        }
        if ((_titan.Position - p.Position).LengthSquared() < (rSq + _titan.Radius * _titan.Radius))
        {
            _titan.Health -= p.Damage * 0.4f;
            _titan.HitFlash = 0.15f;
            _titan.OnDamage();
        }
    }

    /// <summary>Beacon recall — instant teleport to the most recent placed beacon. Visual
    /// feedback: dust burst at the depart point, sparkles at the arrival point, brief shake.</summary>
    private void BeaconRecall(Vector2 dest)
    {
        _particles.EmitDust(_player.Position, 8f);
        _player.Position = dest + _planet.UpAt(dest) * (_player.Radius + 2f);
        _player.Velocity = Vector2.Zero;
        _particles.EmitDust(_player.Position, 8f);
        _shake = MathF.Max(_shake, 0.4f);
    }

    private void UseHealPotion()
    {
        _player.Health = MathF.Min(_player.MaxHealth, _player.Health + 30f);
        // Visual: a small green sparkle puff anchored to the player.
        _particles.EmitDust(_player.Position, 6f);
    }

    /// <summary>Core drill — usable only when the player is within the innermost rings of
    /// the planet (i.e. effectively standing on the Core's threshold). The Core is a
    /// synthetic tile (Planet.Get returns it for x &lt; 0; it's not stored in the array), so
    /// "drilling" it is a distance-to-centre check rather than a tile mutation. On success,
    /// triggers the planet-pierce victory ending — biggest possible escalation.</summary>
    private void TryCoreDrill()
    {
        var distFromCentre = (_player.Position - _planet.Center).Length() / Planet.TileSize;
        // Innermost ~3 rings count as "at the core". Tunable; keeping it tight rewards the
        // player for actually digging all the way to the bottom.
        if (distFromCentre > Planet.RingMin + 3f) return;

        _particles.EmitImpact(_planet.Center, ProjectileKind.Nuke);
        _shake = MathF.Max(_shake, 1.5f);
        _meta.Save();
        _gameOver = true;
        _gameOverReason = $"You pierced the core. Run time: {_runTime:0.0}s. Press R to play again.";
    }

    /// <summary>Crafting menu input — opens with C, scrolls with up/down, crafts with Enter,
    /// closes with C/Esc. Recipes are dispatched through <see cref="ApplyCraft"/>; affordability
    /// is checked there too. Shift modifies the cursor to skip/jump in 5s for fast scroll.</summary>
    private void UpdateCraftingMenu(KeyboardState keys)
    {
        if (Pressed(keys, _prevKeys, Keys.C) || Pressed(keys, _prevKeys, Keys.Escape))
        {
            _craftingOpen = false;
            return;
        }
        var step = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift) ? 5 : 1;
        if (Pressed(keys, _prevKeys, Keys.Down) || Pressed(keys, _prevKeys, Keys.S))
            _craftingCursor = (_craftingCursor + step) % Crafting.All.Count;
        if (Pressed(keys, _prevKeys, Keys.Up) || Pressed(keys, _prevKeys, Keys.W))
            _craftingCursor = (_craftingCursor - step + Crafting.All.Count) % Crafting.All.Count;
        if (Pressed(keys, _prevKeys, Keys.Enter) || Pressed(keys, _prevKeys, Keys.Space))
        {
            var recipe = Crafting.All[_craftingCursor];
            ApplyCraft(recipe);
        }
    }

    /// <summary>Spend a recipe's cost and apply its output. Permanent upgrades flip a flag on
    /// the player; placeable / consumable / ammo recipes deposit the matching id into the
    /// inventory. Returns silently if the player can't afford or the upgrade is already owned.</summary>
    private void ApplyCraft(Recipe r)
    {
        // Block double-craft of one-time upgrades. Pickaxe tiers are gated by the *current*
        // tier so you can only step up sequentially: II requires tier 1, III requires tier 2, etc.
        switch (r.Id)
        {
            case "pickaxe_ii":   if (_player.PickaxeTier >= 2) return; break;
            case "pickaxe_iii":  if (_player.PickaxeTier >= 3 || _player.PickaxeTier < 2) return; break;
            case "pickaxe_iv":   if (_player.PickaxeTier >= 4 || _player.PickaxeTier < 3) return; break;
            case "drill":        if (_player.HasDrill)   return; break;
            case "hammer":       if (_player.HasHammer)  return; break;
            case "lantern":      if (_player.HasLantern) return; break;
            case "armor":        if (_player.HasArmor)   return; break;
            case "core_drill":   if (_player.HasCoreDrill) return; break;
            case "cannon":       if (_hasCannon)         return; break;
        }

        if (!Crafting.TryPay(r, _player.Inventory)) return;

        switch (r.Id)
        {
            // Pickaxe tiers — there's only one pickaxe slot ("pickaxe"), already on the belt
            // from spawn. Crafting just bumps the tier; the icon updates automatically because
            // Icons.GetForSlot keys on PickaxeTier.
            case "pickaxe_ii":  _player.PickaxeTier = 2; break;
            case "pickaxe_iii": _player.PickaxeTier = 3; break;
            case "pickaxe_iv":  _player.PickaxeTier = 4; break;
            // Permanent active tools — flip the flag, auto-equip a slot, and stash a marker
            // entry in inventory so the panel surfaces them when not on the belt. The marker
            // is only displayed (filtered out by DrawInventoryPanel when the belt holds it)
            // and never consumed by TryConsume — count stays at 1 for the run.
            case "drill":       _player.HasDrill     = true; _player.Inventory.Add("drill", 1);      _player.Toolbelt.AutoEquip("drill");      break;
            case "hammer":      _player.HasHammer    = true; _player.Inventory.Add("hammer", 1);     _player.Toolbelt.AutoEquip("hammer");     break;
            case "core_drill":  _player.HasCoreDrill = true; _player.Inventory.Add("core_drill", 1); _player.Toolbelt.AutoEquip("core_drill"); break;
            case "cannon":      _hasCannon           = true; _player.Inventory.Add("cannon", 1);     _player.Toolbelt.AutoEquip("cannon");     break;
            // Passive permanent upgrades — no slot, no inventory entry. Just a flag.
            case "lantern":     _player.HasLantern = true; break;
            case "armor":       _player.HasArmor   = true; break;
            // Everything else is a stockable item: the recipe id is the inventory id. The
            // first-craft also auto-equips an empty slot so the player can use it immediately
            // — subsequent crafts just stock more.
            default:
                _player.Inventory.Add(r.Id, 1);
                _player.Toolbelt.AutoEquip(r.Id);
                break;
        }
    }

    /// <summary>Drop a sentry just above the player's feet so the dwarf doesn't end up
    /// standing inside it. The position is along planet-up so the turret lands on the
    /// surface rather than burying into a slope.</summary>
    private void PlaceSentryAtFeet()
    {
        var up = _planet.UpAt(_player.Position);
        var pos = _player.Position - up * (_player.Radius + 1f);
        _sentries.Add(new Sentry(pos));
        _particles.EmitDust(pos, 4f);
    }

    /// <summary>Render the toolbelt strip across the bottom of the screen — 9 slot squares
    /// with icons, count badges, and a highlighted active slot. Hit-test rectangles are
    /// cached in <see cref="_toolbeltHitTest"/> so the next frame's input pass can drag-drop.
    ///
    /// Layout: each slot is 36×36 with a 4-px gap; the strip is centred horizontally and
    /// pinned to the bottom with a 16-px bottom margin. The active slot is drawn with a
    /// brighter inner ring and a number-key tag above it. Empty slots are dim outlines.</summary>
    private void DrawToolbelt()
    {
        var sb = _renderer.Batch;
        const int slotSize = 36;
        const int slotGap = 4;
        const int rowH = slotSize + 18;   // includes label tag above
        var totalW = Toolbelt.SlotCount * slotSize + (Toolbelt.SlotCount - 1) * slotGap;
        var x0 = (VirtualWidth - totalW) / 2;
        var y0 = VirtualHeight - rowH - 8;

        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            _toolbeltHitTest[i] = new Rectangle(sx, sy, slotSize, slotSize);

            var isActive = i == _player.Toolbelt.Selected;
            var bg = isActive ? new Color(60, 70, 90, 240) : new Color(20, 22, 32, 220);
            var border = isActive ? new Color(255, 220, 120) : new Color(110, 115, 130);
            sb.Draw(_renderer.Pixel, new Rectangle(sx, sy, slotSize, slotSize), bg);
            sb.Draw(_renderer.Pixel, new Rectangle(sx, sy, slotSize, 1), border);
            sb.Draw(_renderer.Pixel, new Rectangle(sx, sy + slotSize - 1, slotSize, 1), border);
            sb.Draw(_renderer.Pixel, new Rectangle(sx, sy, 1, slotSize), border);
            sb.Draw(_renderer.Pixel, new Rectangle(sx + slotSize - 1, sy, 1, slotSize), border);
        }
        sb.End();

        // Pass 2: icon + count badge + slot number, with PointClamp sampling so the 16×16
        // pixel-art icons stay crisp at 2× scale (32×32 inside the 36-px slot).
        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            var id = _player.Toolbelt.Slots[i];

            // Slot number above each cell.
            var numStr = (i + 1).ToString();
            _renderer.Batch.End();   // brief flip so DrawDebugLabel can begin its own
            _renderer.DrawDebugLabel(numStr,
                new Vector2(sx + 4, sy - 12),
                i == _player.Toolbelt.Selected ? Color.White : new Color(150, 150, 160));
            sb.Begin(samplerState: SamplerState.PointClamp);

            if (id is null) continue;

            var tex = Icons.GetForSlot(id, _player.PickaxeTier);
            if (tex is not null)
            {
                sb.Draw(tex, new Rectangle(sx + 2, sy + 2, slotSize - 4, slotSize - 4), Color.White);
            }
            else
            {
                // Fallback: solid swatch in the resource colour. Means an unknown id, useful
                // while wiring new recipes before authoring an icon.
                sb.Draw(_renderer.Pixel, new Rectangle(sx + 8, sy + 8, slotSize - 16, slotSize - 16),
                    Tiles.ResourceColor(id));
            }
        }
        sb.End();

        // Pass 3: count badges (bottom-right corner of each slot for stackable items).
        for (var i = 0; i < Toolbelt.SlotCount; i++)
        {
            var sx = x0 + i * (slotSize + slotGap);
            var sy = y0 + 14;
            var id = _player.Toolbelt.Slots[i];
            if (id is null) continue;
            if (Toolbelt.IsPermanent(id)) continue;
            var count = _player.Inventory.Count(id);
            if (count <= 0) continue;
            _renderer.DrawDebugLabel(count.ToString(),
                new Vector2(sx + slotSize - 14, sy + slotSize - 12),
                count > 0 ? Color.White : new Color(255, 120, 120));
        }
    }

    /// <summary>Custom inventory panel that supports click-to-pick-up. Replaces the old
    /// renderer-side panel. Each row is hit-test-recorded so HandleInventoryUi can detect
    /// which inventory id was clicked. Tool ids that live exclusively as toolbelt slots
    /// (intrinsic stones, etc.) are skipped — drag-and-drop is for stackable items.</summary>
    private void DrawInventoryPanel(Inventory inv)
    {
        _invHitTest.Clear();

        var rows = new List<(string id, int count)>();
        foreach (var id in Tiles.ResourceOrder)
        {
            var c = inv.Count(id);
            if (c > 0 && ShouldShowInInventory(id)) rows.Add((id, c));
        }
        foreach (var (id, count) in inv.Items)
        {
            if (count <= 0) continue;
            var known = false;
            foreach (var k in Tiles.ResourceOrder) if (k == id) { known = true; break; }
            if (!known && ShouldShowInInventory(id)) rows.Add((id, count));
        }
        if (rows.Count == 0) return;

        const int swatchSize = 14;
        const int rowHeight = 18;
        const int padX = 8;
        const int padY = 6;

        // Wider panel than before to fit icon + label + count cleanly.
        const int panelW = 200;
        var panelH = padY + rows.Count * rowHeight + padY;
        var panelX = VirtualWidth - panelW - 12;
        var panelY = 12;

        var sb = _renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(0, 0, 0, 170));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(255, 255, 255, 60));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(255, 255, 255, 60));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(255, 255, 255, 60));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(255, 255, 255, 60));

        for (var i = 0; i < rows.Count; i++)
        {
            var (id, count) = rows[i];
            var rowY = panelY + padY + i * rowHeight;
            var rowRect = new Rectangle(panelX + 2, rowY - 1, panelW - 4, rowHeight);
            _invHitTest[id] = rowRect;

            // Hover highlight — handy feedback without a full hover system.
            var mouse = Mouse.GetState();
            if (rowRect.Contains(mouse.X, mouse.Y))
                sb.Draw(_renderer.Pixel, rowRect, new Color(120, 130, 200, 60));

            // Icon takes priority over swatch — looks like a real tool entry. If the id
            // doesn't have an icon, fall back to the resource colour swatch (raw mats).
            var iconX = panelX + padX;
            var iconY = rowY + 1;
            var tex = Icons.GetForSlot(id, _player.PickaxeTier);
            if (tex is not null)
            {
                sb.Draw(tex, new Rectangle(iconX - 1, iconY - 2, swatchSize + 4, swatchSize + 4), Color.White);
            }
            else
            {
                sb.Draw(_renderer.Pixel, new Rectangle(iconX - 1, iconY - 1, swatchSize + 2, swatchSize + 2), new Color(0, 0, 0, 200));
                sb.Draw(_renderer.Pixel, new Rectangle(iconX, iconY, swatchSize, swatchSize), Tiles.ResourceColor(id));
            }
        }
        sb.End();

        // Labels in a separate pass — DrawDebugLabel handles its own Begin/End.
        for (var i = 0; i < rows.Count; i++)
        {
            var (id, count) = rows[i];
            var rowY = panelY + padY + i * rowHeight;
            var line = $"{Tiles.ResourceLabel(id)}  {count}";
            _renderer.DrawDebugLabel(line, new Vector2(panelX + padX + swatchSize + 8, rowY + 2), Color.White);
        }
    }

    /// <summary>Filter for the inventory panel: permanent tool markers (drill / hammer /
    /// cannon / core_drill) are visible only when *not* currently on the toolbelt. Stackable
    /// items always show — the toolbelt slot is a *shortcut* to the inventory stack, not a
    /// separate cache, so the count is meaningfully visible in both places at once.</summary>
    private bool ShouldShowInInventory(string id)
    {
        if (Toolbelt.IsPermanent(id) && _player.Toolbelt.Contains(id)) return false;
        return true;
    }

    /// <summary>Render the icon currently being carried (drag-and-drop) at the cursor — gives
    /// the player a clear visual that they have something in hand. Uses the same icon lookup
    /// the slot/inventory uses so the appearance matches.</summary>
    private void DrawCarry(string id)
    {
        var mouse = Mouse.GetState();
        var sb = _renderer.Batch;
        sb.Begin(samplerState: SamplerState.PointClamp);
        var tex = Icons.GetForSlot(id, _player.PickaxeTier);
        if (tex is not null)
            sb.Draw(tex, new Rectangle(mouse.X - 16, mouse.Y - 16, 32, 32), new Color(255, 255, 255, 220));
        else
            sb.Draw(_renderer.Pixel, new Rectangle(mouse.X - 8, mouse.Y - 8, 16, 16), Tiles.ResourceColor(id));
        sb.End();
    }

    /// <summary>Render the crafting menu overlay. Recipes scroll vertically with the cursor
    /// row highlighted; cost is colour-coded per item (green if affordable, red if not).
    /// Recipes the player already owns / can't yet craft are dimmed but still listed for
    /// reference. Closes when ApplyCraft completes — toggled by C/Esc in UpdateCraftingMenu.</summary>
    private void DrawCraftingMenu()
    {
        var sb = _renderer.Batch;
        sb.Begin();
        // Dim backdrop so the world reads as paused even though it's still ticking.
        sb.Draw(_renderer.Pixel, new Rectangle(0, 0, VirtualWidth, VirtualHeight), new Color(0, 0, 0, 170));
        sb.End();

        // Layout: a centred panel with header + scrollable list. Cursor stays on the same row
        // index but the visible window scrolls so a long recipe list still fits.
        const int panelW = 540;
        const int rowH = 16;
        const int visibleRows = 18;
        var panelH = 60 + visibleRows * rowH;
        var panelX = (VirtualWidth - panelW) / 2;
        var panelY = (VirtualHeight - panelH) / 2;

        sb.Begin();
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, panelW, panelH), new Color(15, 15, 25, 230));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, panelW, 1), new Color(140, 130, 200));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY + panelH - 1, panelW, 1), new Color(140, 130, 200));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX, panelY, 1, panelH), new Color(140, 130, 200));
        sb.Draw(_renderer.Pixel, new Rectangle(panelX + panelW - 1, panelY, 1, panelH), new Color(140, 130, 200));
        sb.End();

        _renderer.DrawDebugLabel("CRAFTING — Up/Down to scroll, Enter to craft, C/Esc to close",
            new Vector2(panelX + 12, panelY + 12), Color.White);

        // Scroll the visible window so the cursor stays in view. Cursor at the top half of
        // the visible block? Show from 0; near the end? Lock the bottom; otherwise centre.
        var total = Crafting.All.Count;
        var firstVisible = MathHelper.Clamp(_craftingCursor - visibleRows / 2, 0, Math.Max(0, total - visibleRows));

        for (var i = 0; i < visibleRows && firstVisible + i < total; i++)
        {
            var idx = firstVisible + i;
            var r = Crafting.All[idx];
            var rowY = panelY + 44 + i * rowH;

            var owned = IsOwned(r.Id);
            var afford = Crafting.CanAfford(r, _player.Inventory) && !owned;

            // Row highlight bar
            if (idx == _craftingCursor)
            {
                _renderer.Batch.Begin();
                _renderer.Batch.Draw(_renderer.Pixel,
                    new Rectangle(panelX + 4, rowY - 1, panelW - 8, rowH),
                    afford ? new Color(60, 90, 50, 200) : new Color(70, 50, 50, 200));
                _renderer.Batch.End();
            }

            // Name column. Dimmed (60% grey) for owned recipes; normal for affordable; red
            // for can't-afford. Shows recipe name + cost line under the name.
            var nameCol = owned ? new Color(120, 120, 120)
                       : afford ? new Color(220, 240, 200)
                                : new Color(230, 170, 170);
            var costStr = BuildCostString(r);
            _renderer.DrawDebugLabel(r.Name, new Vector2(panelX + 16, rowY), nameCol);
            _renderer.DrawDebugLabel(costStr, new Vector2(panelX + 280, rowY),
                owned ? new Color(110, 110, 110) : afford ? new Color(180, 220, 160) : new Color(220, 130, 130));
        }
    }

    private bool IsOwned(string id) => id switch
    {
        "pickaxe_ii"  => _player.PickaxeTier >= 2,
        "pickaxe_iii" => _player.PickaxeTier >= 3,
        "pickaxe_iv"  => _player.PickaxeTier >= 4,
        "drill"       => _player.HasDrill,
        "hammer"      => _player.HasHammer,
        "lantern"     => _player.HasLantern,
        "armor"       => _player.HasArmor,
        "cannon"      => _hasCannon,
        "core_drill"  => _player.HasCoreDrill,
        _ => false,
    };

    private static string BuildCostString(Recipe r)
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var (id, count) in r.Cost)
        {
            if (!first) sb.Append(' ');
            sb.Append(count).Append('×').Append(Tiles.ResourceLabel(id));
            first = false;
        }
        return sb.ToString();
    }

    private void TryLaunchRocket()
    {
        if (_player.Inventory.Count("rocket_part") < 5) return;
        // Must be near the surface — within 5 tiles of the surface ring.
        var fromCenter = (_player.Position - _planet.Center).Length();
        var surface = _planet.Radius * Planet.TileSize;
        if (surface - fromCenter > Planet.TileSize * 5f && fromCenter < surface + Planet.TileSize * 6f)
            return;

        _meta.Escapes++;
        if (_meta.Escapes >= 1) _meta.StartingPickaxePower = Math.Max(_meta.StartingPickaxePower, 2);
        if (_meta.Escapes >= 3) _meta.StartWithCannon = true;
        _meta.Save();
        _gameOver = true;
        _gameOverReason = $"Escape! Rocket launched. Run time: {_runTime:0.0}s. Press R to play again.";
    }

    private Vector2 FindSurfaceSpawn(float angle, int radius)
    {
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        // Start well above the highest possible peak and step inward until we hit solid.
        for (var d = radius + 30; d > 10; d--)
        {
            var p = _planet.Center + dir * (d * Planet.TileSize);
            if (_planet.IsSolidAt(p))
                return p - dir * (Planet.TileSize * 1.5f);
        }
        return _planet.Center + dir * ((radius + 12) * Planet.TileSize);
    }

    /// <summary>Carve out any solid tiles overlapping a freshly spawned body so nothing starts
    /// life embedded in rock — an embedded spawn forces the collider's inside-tile escape
    /// push on its first frame, which reads as teleporting/clipping through the world.
    /// Overlap uses the same polar-rect math as the collider; anchored tiles are left alone
    /// and physics is dirty-marked so any overhang this opens up settles normally.</summary>
    private void ClearSpawnSpace(Vector2 pos, float radius)
    {
        var (tx, _) = _planet.WorldToTile(pos);
        var relC = pos - _planet.Center;
        var ang = MathF.Atan2(relC.Y, relC.X);
        if (ang < 0) ang += MathHelper.TwoPi;
        var rSq = (radius + 0.5f) * (radius + 0.5f);
        for (var dx = -2; dx <= 2; dx++)
        {
            var x = tx + dx;
            if (x < 0 || x >= Planet.RingCount) continue;
            // Per-ring column from the true world angle — ring tile counts differ, so a
            // shared ty index would drift near the angle wrap and miss overlapped tiles.
            var nRing = Planet.TilesAt(x);
            var ty0 = (int)(ang / MathHelper.TwoPi * nRing);
            for (var dy = -2; dy <= 2; dy++)
            {
                var y = ty0 + dy;
                var k = _planet.Get(x, y);
                if (!Tiles.IsSolid(k) || Tiles.IsAnchored(k)) continue;

                var centre = _planet.TileToWorld(x, y);
                var up = _planet.UpAt(centre);
                var right = new Vector2(-up.Y, up.X);
                var rel = pos - centre;
                var lx = Vector2.Dot(rel, right);
                var ly = Vector2.Dot(rel, up);
                var ringRadius = (Planet.RingMin + x + 0.5f) * Planet.TileSize;
                var halfX = MathHelper.TwoPi * ringRadius / Planet.TilesAt(x) * 0.5f;
                var halfY = Planet.TileSize * 0.5f;
                var ox = lx - MathHelper.Clamp(lx, -halfX, halfX);
                var oy = ly - MathHelper.Clamp(ly, -halfY, halfY);
                if (ox * ox + oy * oy >= rSq) continue;

                _planet.Set(x, y, TileKind.Sky);
                _physics.MarkDirty(x, y);
            }
        }
    }

    /// <summary>Population count within a radius of the player. Spawning is budgeted on the
    /// *local* population, not the planet-wide one — a planet-wide cap fills up with
    /// creatures scattered across ~150k underground tiles and the player never meets any.</summary>
    private int CountKindsNear(float radius, bool cave = false, bool surface = false, bool sky = false)
    {
        var rSq = radius * radius;
        var n = 0;
        foreach (var c in _creatures)
            if ((cave && c.IsCaveKind) || (surface && c.IsSurfaceKind) || (sky && c.IsSkyKind))
                if ((c.Position - _player.Position).LengthSquared() < rSq)
                    n++;
        return n;
    }

    private void TrySpawnCreature()
    {
        // Pick a cave tile (Sky foreground with a rock wall behind it) in a donut around the
        // player: close enough to be met within a minute of wandering, far enough to never
        // pop in on-screen.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var a = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;
            var d = 200f + (float)Random.Shared.NextDouble() * 250f;
            var candidate = _player.Position + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            var (r, t) = _planet.WorldToTile(candidate);
            if (r < 0 || r >= Planet.RingCount) continue;
            if (_planet.Get(r, t) != TileKind.Sky) continue;
            if (_planet.GetWall(r, t) == TileKind.Sky) continue;
            var pos = _planet.TileToWorld(r, t);
            if ((pos - _player.Position).Length() < 180f) continue;

            // Roster shifts with depth: moles and skitterers riddle the upper crust, the
            // mid-band belongs to the diggers (delvers, centipedes, borers, moles), and the
            // lava zone is magma slugs plus hardened delver war-parties.
            var fromCenter = (pos - _planet.Center).Length() / Planet.TileSize;
            var depth = 129f - (fromCenter - Planet.RingMin); // tiles below the baseline surface
            var roll = Random.Shared.NextDouble();
            CreatureKind kind;
            if (depth > 45f)
            {
                kind = roll < 0.35 ? CreatureKind.MagmaSlug
                     : roll < 0.55 ? CreatureKind.HornedDelver
                     : roll < 0.70 ? CreatureKind.Centipede
                     : roll < 0.85 ? CreatureKind.CaveEye
                     : CreatureKind.Grub;
            }
            else if (depth > 20f)
            {
                kind = roll < 0.20 ? CreatureKind.HornedDelver
                     : roll < 0.35 ? CreatureKind.Centipede
                     : roll < 0.50 ? CreatureKind.Borer
                     : roll < 0.65 ? CreatureKind.MoleBeast
                     : roll < 0.78 ? CreatureKind.Grub
                     : roll < 0.90 ? CreatureKind.CaveEye
                     : CreatureKind.Skitterer;
            }
            else
            {
                kind = roll < 0.30 ? CreatureKind.Skitterer
                     : roll < 0.55 ? CreatureKind.MoleBeast
                     : roll < 0.70 ? CreatureKind.Grub
                     : roll < 0.85 ? CreatureKind.CaveEye
                     : CreatureKind.HornedDelver;
            }
            var c = new Creature(pos, kind);
            ClearSpawnSpace(pos, c.Radius);
            _creatures.Add(c);
            return;
        }
    }

    /// <summary>Populate the fresh planet with ambient life: herds on the surface, flyers in
    /// the sky. Run-start only; the fauna timer keeps numbers topped up afterwards.</summary>
    private void SpawnInitialFauna()
    {
        // All spawn helpers place relative to the player, so this stocks the starting
        // neighbourhood: herds on the nearby surface, flyers overhead, cave dwellers below.
        for (var i = 0; i < 7; i++) TrySpawnSurfaceAnimal();
        for (var i = 0; i < 6; i++) TrySpawnSkyAnimal();
        for (var i = 0; i < 12; i++) TrySpawnCreature();
    }

    /// <summary>Angle a few hundred px along the surface from the player — fauna spawns in
    /// the player's neighbourhood (just off-screen), not at a random point on the planet.</summary>
    private float NearbySurfaceAngle(float minArc, float maxArc)
    {
        var rel = _player.Position - _planet.Center;
        var playerAng = MathF.Atan2(rel.Y, rel.X);
        var surfaceRadius = MathF.Max(rel.Length(), Planet.RingMin * Planet.TileSize);
        var arc = minArc + (float)Random.Shared.NextDouble() * (maxArc - minArc);
        var sign = Random.Shared.Next(2) == 0 ? 1f : -1f;
        return playerAng + sign * (arc / surfaceRadius);
    }

    private void TrySpawnSurfaceAnimal()
    {
        var angle = NearbySurfaceAngle(220f, 550f);
        var pos = FindSurfaceSpawn(angle, _planet.Radius);
        if ((pos - _player.Position).Length() < 160f) return; // don't pop in on-screen
        var kind = Random.Shared.Next(2) == 0 ? CreatureKind.Grazer : CreatureKind.Hopper;
        var c = new Creature(pos, kind);
        ClearSpawnSpace(pos, c.Radius);
        _creatures.Add(c);
    }

    private void TrySpawnSkyAnimal()
    {
        var angle = NearbySurfaceAngle(220f, 550f);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var ground = FindSurfaceSpawn(angle, _planet.Radius);
        // 60–200 px above the local terrain, capped inside the tile grid so the flyer's
        // collision probes stay in-bounds.
        var alt = (ground - _planet.Center).Length() + 60f + (float)Random.Shared.NextDouble() * 140f;
        alt = MathF.Min(alt, (_planet.Radius - 6) * Planet.TileSize);
        var pos = _planet.Center + dir * alt;
        if ((pos - _player.Position).Length() < 160f) return;
        var kind = Random.Shared.NextDouble() < 0.65 ? CreatureKind.SkyMoth : CreatureKind.SkyStinger;
        var c = new Creature(pos, kind);
        ClearSpawnSpace(pos, c.Radius); // altitude is above local ground, but a mountain flank can still clip the band
        _creatures.Add(c);
    }

    protected override void Draw(GameTime gameTime)
    {
        // Feed the renderer the current wall-clock so animated decoration (waving grass,
        // hanging vines) advances with the game time rather than the frame index.
        _renderer.Time = (float)gameTime.TotalGameTime.TotalSeconds;

        // Apply shake by perturbing the camera target.
        var shakeX = (float)(Random.Shared.NextDouble() - 0.5) * _shake * 6f;
        var shakeY = (float)(Random.Shared.NextDouble() - 0.5) * _shake * 6f;
        var oldTarget = _camera.Target;
        _camera.Target = oldTarget + new Vector2(shakeX, shakeY);

        // Re-wired every frame because _physics is recreated on restart while _renderer persists.
        _renderer.TrembleTiles = _physics.TremblingTiles;
        _renderer.DrawWorld(_planet, _camera);

        _renderer.BeginEntities(_camera);

        // View circle for the culled cell passes: centre + a radius that covers the screen
        // corners at any camera rotation. The cell grid is far too fine to draw planet-wide.
        var viewCentre = _camera.ScreenToWorld(new Vector2(VirtualWidth / 2f, VirtualHeight / 2f));
        var viewRadius = (viewCentre - _camera.ScreenToWorld(Vector2.Zero)).Length() + Planet.TileSize * 2f;

        // Cells (sand/water/lava/smoke) draw above tiles but below entities so the dwarf walks
        // in front of his own debris pile.
        _cells.Draw(_renderer, viewCentre, viewRadius);

        // Pixel-art dwarf sprite — drawn rotated to align local-up with planet's outward radial.
        // Sprite head-at-top, feet-at-bottom; the rotation maps sprite-up to world-up.
        // Anchor offset: the sprite is 12 px tall × 0.6 scale so it extends 3.6 px below center,
        // but the collision Radius is only 2.6 px. Without the offset the visual feet sit 1 px
        // *inside* the tile while the collision body is correctly perched on top — looks like
        // the dwarf is sunk into the ground. Shifting the sprite up by exactly that gap aligns
        // visual feet with the collision bottom.
        var up = _planet.UpAt(_player.Position);
        var rot = MathF.Atan2(up.X, -up.Y);
        const float spriteScale = 0.6f;          // world units per sprite pixel
        const float spriteFeetOffset = 1.0f;     // = (sprite_half_height * scale) − Radius
        _renderer.Batch.Draw(_dwarfTex, _player.Position + up * spriteFeetOffset, null, Color.White, rot,
            new Vector2(_dwarfTex.Width * 0.5f, _dwarfTex.Height * 0.5f),
            spriteScale, SpriteEffects.None, 0f);

        // Reticle.
        var mouse = Mouse.GetState();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        _renderer.DrawCircle(worldCursor, 3f, new Color(255, 255, 255, 180));

        // Creatures — each kind draws its own procedural sprite, including the burn/freeze
        // status tinting and the burning-ember flicker.
        foreach (var c in _creatures)
        {
            c.Draw(_renderer, _planet, _player);
        }

        // Sentries — chunky pixel-art turret. Base is a stout iron plinth, barrel is a long
        // rectangle that rotates with the sentry's Aim. Tinted red on hit-flash; the glowing
        // muzzle ring tracks the cooldown so a charging sentry shows visible "ready" feedback.
        foreach (var s in _sentries)
        {
            var sup = _planet.UpAt(s.Position);
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
        foreach (var p in _projectiles)
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
                    var fuseFlicker = (MathF.Sin(_runTime * 50f) * 0.5f + 0.5f);
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
                default:
                    _renderer.DrawCircle(p.Position, p.Radius, Color.White);
                    break;
            }
        }

        // Boulders.
        foreach (var b in _boulders)
        {
            _renderer.DrawCircle(b.Position, b.Radius, new Color(80, 70, 60));
        }

        // Kaiju visibility cull. The kaiju's render block does 100+ draw calls (4 legs × IK +
        // 7-node tail + dorsal spines + head + claws), so skipping it when off-screen is a
        // large win. Camera viewport is 1280×720 at zoom 4 → ~320×180 world units, so the
        // visible radius from the camera target is ~200 px; bump to 400 for the kaiju's
        // silhouette (it's huge — body+legs span ~280 px) and a small margin so legs sweeping
        // into view from off-screen aren't suddenly popped in.
        var kaijuVisible = _titan.Health > 0
            && (_titan.Position - _camera.Target).LengthSquared() < 400f * 400f;

        // Kaiju boss. Body is a hulking scaled hulk on 4 procedural quadruped legs; tail is a
        // verlet chain dragging behind; head is a snouted bulb in front. Legs use 2-bone IK
        // that allows stretching when the foot is far from the hip — so the kaiju visibly
        // elongates its limbs to crest mountains and compress them on flat ground. Foot
        // positions, leg step state, and tail nodes are all simulated in Titan.Update; this
        // block is rendering only.
        if (kaijuVisible)
        {
            var tup = _planet.UpAt(_titan.Position);
            var tright = new Vector2(-tup.Y, tup.X);
            var trot = MathF.Atan2(tup.X, -tup.Y);
            var tp = _titan.Position;
            var face = _titan.Facing;
            var anger01 = MathHelper.Clamp(_titan.Anger / 100f, 0f, 1f);
            var hide = _titan.HitFlash > 0
                ? Color.White
                : Color.Lerp(new Color(48, 56, 50), new Color(120, 60, 50), anger01);   // dark mossy green → angry rust
            var hideDark = new Color(hide.R / 2, hide.G / 2, hide.B / 2);
            var hideLight = new Color(
                Math.Clamp(hide.R + 38, 0, 255),
                Math.Clamp(hide.G + 38, 0, 255),
                Math.Clamp(hide.B + 38, 0, 255));
            var underBelly = new Color(
                Math.Clamp(hide.R + 70, 0, 255),
                Math.Clamp(hide.G + 60, 0, 255),
                Math.Clamp(hide.B + 50, 0, 255));   // pale cream/yellow underbelly
            var chitin = new Color(18, 14, 22);
            var spineGlow = Color.Lerp(new Color(80, 130, 220), new Color(255, 90, 60), anger01);

            // === 1. Tail (drawn first, so the body covers its root) ===
            // Verlet chain; thickness tapers from base to tip. Trailing tip lights up as anger
            // climbs so it reads like a kaiju "atomic" tail.
            var tailNodes = _titan.TailNodes;
            for (var ti = 1; ti < tailNodes.Length; ti++)
            {
                var fr = ti / (float)(tailNodes.Length - 1);   // 0 at root, 1 at tip
                var thick = MathHelper.Lerp(28f, 8f, fr);
                var tailCol = Color.Lerp(hideDark, Color.Lerp(hide, spineGlow, anger01 * 0.45f), 1f - fr * 0.6f);
                DrawLegSegment(tailNodes[ti - 1], tailNodes[ti], thick, tailCol);
                // Mid-segment scales/spines along the tail
                if (ti < tailNodes.Length - 1)
                {
                    var mid = (tailNodes[ti] + tailNodes[ti - 1]) * 0.5f;
                    _renderer.DrawCircle(mid, thick * 0.45f, chitin);
                }
            }
            // Tail tip glow — a small radial bead on the last node, brighter when angry.
            _renderer.DrawCircle(tailNodes[^1], 7f + anger01 * 5f, spineGlow);
            _renderer.DrawCircle(tailNodes[^1], 3f, Color.White);

            // === 2. Hind legs (behind the body) — render the two with HipForward > 0 first ===
            for (var li = 0; li < _titan.Legs.Length; li++)
            {
                var leg = _titan.Legs[li];
                if (leg.HipForward * face >= 0) continue; // hind legs are on the side away from facing
                DrawTitanLeg(leg, tp, tup, tright, hide, hideDark, hideLight, chitin, _titan.Pulse);
            }

            // === 3. Body — a wide chitinous mass, breathing with the pulse ===
            var breath2 = MathF.Sin(_titan.Pulse) * 1.6f;
            // Underbelly slab (low, lighter)
            _renderer.DrawRect(tp + tup * (-18f + breath2), new Vector2(240f, 56f), underBelly, trot);
            _renderer.DrawRect(tp + tup * (-32f + breath2), new Vector2(220f, 4f), chitin, trot);
            // Belly scutes — horizontal banding
            for (var s = -2; s <= 2; s++)
            {
                _renderer.DrawRect(tp + tup * (-22f + breath2) + tright * (s * 38f),
                    new Vector2(28f, 14f), new Color(underBelly.R - 24, underBelly.G - 24, underBelly.B - 24), trot);
            }
            // Main body (hide-colored mass on top of underbelly)
            _renderer.DrawRect(tp + tup * (10f + breath2), new Vector2(260f, 90f), hideDark, trot);
            _renderer.DrawRect(tp + tup * (20f + breath2), new Vector2(244f, 70f), hide, trot);
            _renderer.DrawRect(tp + tup * (38f + breath2), new Vector2(212f, 38f), hideLight, trot);
            // Scaled skin texture: pseudo-random stable speckle. Use position-based hash so the
            // scales don't shimmer between frames.
            var hashSeed = (int)(tp.X * 0.13f) ^ (int)(tp.Y * 0.17f);
            for (var sc = 0; sc < 18; sc++)
            {
                var h = (hashSeed * 1664525 + sc * 1013904223) & 0x7fffffff;
                var sx = ((h >> 3) & 0xFF) / 255f * 220f - 110f;
                var sy = ((h >> 11) & 0x3F) / 63f * 50f - 5f;
                var ssz = 3f + ((h >> 19) & 3);
                _renderer.DrawRect(tp + tright * sx + tup * (sy + breath2),
                    new Vector2(ssz, ssz),
                    (h & 1) == 0 ? hideDark : chitin, trot);
            }

            // === 4. Dorsal spines — Godzilla-style fin row along the back ===
            // Each spine is a stacked pyramid of progressively narrower rects, glowing on the
            // tip when angry. Three rows of 5 spines each — center row is tallest.
            for (var s = -2; s <= 2; s++)
            {
                var sFr = MathF.Abs(s) / 2f;
                var height = 32f - sFr * 12f;
                var width = 22f - sFr * 6f;
                var basePos = tp + tup * (54f + breath2) + tright * (s * 44f);
                // Plate base
                _renderer.DrawRect(basePos, new Vector2(width, 8f), chitin, trot);
                // Plate body
                _renderer.DrawRect(basePos + tup * (height * 0.35f), new Vector2(width * 0.8f, height * 0.55f), hideDark, trot);
                // Plate spine tip
                _renderer.DrawRect(basePos + tup * (height * 0.7f), new Vector2(width * 0.45f, height * 0.45f), chitin, trot);
                // Glowing edge — anger-tinted
                _renderer.DrawRect(basePos + tup * (height * 0.85f), new Vector2(width * 0.3f, 4f), spineGlow, trot);
            }

            // === 5. Front legs (in front of the body) ===
            for (var li = 0; li < _titan.Legs.Length; li++)
            {
                var leg = _titan.Legs[li];
                if (leg.HipForward * face < 0) continue; // skip hind legs (already drawn)
                DrawTitanLeg(leg, tp, tup, tright, hide, hideDark, hideLight, chitin, _titan.Pulse);
            }

            // === 6. Head — a snouted bulb in front of the body ===
            var headBase = tp + tup * 26f + tright * (face * 110f);
            // Neck connection (a small dark patch where head meets body)
            _renderer.DrawRect(tp + tup * 22f + tright * (face * 60f), new Vector2(54f, 46f), hideDark, trot);
            // Skull
            _renderer.DrawRect(headBase + tup * 4f, new Vector2(86f, 64f), hideDark, trot);
            _renderer.DrawRect(headBase + tup * 12f, new Vector2(72f, 48f), hide, trot);
            _renderer.DrawRect(headBase + tup * 24f, new Vector2(54f, 22f), hideLight, trot);
            // Brow ridge — a strong horizontal dark bar over the eyes
            _renderer.DrawRect(headBase + tup * 18f, new Vector2(78f, 5f), chitin, trot);
            // Snout — extends further forward, narrower and lower than the skull
            var snoutBase = headBase + tright * (face * 38f) + tup * -4f;
            _renderer.DrawRect(snoutBase, new Vector2(56f, 36f), hideDark, trot);
            _renderer.DrawRect(snoutBase + tup * 6f, new Vector2(44f, 22f), hide, trot);
            // Nostril
            _renderer.DrawCircle(snoutBase + tright * (face * 18f) + tup * 8f, 3.5f, chitin);
            // Jaw — a darker rect under the snout, with a tooth row
            var jawPos = snoutBase + tup * -16f;
            _renderer.DrawRect(jawPos, new Vector2(60f, 12f), chitin, trot);
            // Teeth row — small white pixels along the jaw seam
            for (var ti2 = -2; ti2 <= 2; ti2++)
            {
                _renderer.DrawRect(jawPos + tright * (ti2 * 10f) + tup * 4f,
                    new Vector2(3f, 5f), Color.White, trot);
            }
            // Side horns — two crested ridges flaring back from the skull (kaiju cresting)
            _renderer.DrawRect(headBase + tright * (face * -16f) + tup * 32f,
                new Vector2(20f, 8f), chitin, trot + face * 0.5f);
            _renderer.DrawRect(headBase + tright * (face * -16f) + tup * 24f,
                new Vector2(28f, 6f), chitin, trot + face * 0.3f);

            // === 7. Eyes — two glowing slits, anger-tinted, tracking the player ===
            var lookDir = _player.Position - headBase;
            if (lookDir.LengthSquared() < 0.001f) lookDir = tright * face;
            else lookDir.Normalize();
            var lookRight = Vector2.Dot(lookDir, tright);
            var lookUp = Vector2.Dot(lookDir, tup);
            var eyeCol = Color.Lerp(new Color(255, 230, 100), new Color(255, 70, 50), anger01);
            for (var ei = -1; ei <= 1; ei += 2)
            {
                var socket = headBase + tup * 8f + tright * (ei * 14f);
                _renderer.DrawCircle(socket, 9f, chitin);
                var pupil = socket + tright * lookRight * 3f + tup * lookUp * 2f;
                _renderer.DrawCircle(pupil, 6f, eyeCol);
                _renderer.DrawCircle(pupil, 2.5f, Color.Black);
            }
        }

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
        var lanternMul = _player.HasLantern ? 1.55f : 1.0f;
        _renderer.AddLight(_player.Position, 200f * lanternMul, new Color(85, 70, 50));
        _renderer.AddLight(_player.Position, 140f * lanternMul, new Color(140, 115, 80));
        _renderer.AddLight(_player.Position, 90f * lanternMul,  new Color(200, 170, 120));
        _renderer.AddLight(_player.Position, 50f * lanternMul,  new Color(245, 215, 165));
        if (_player.PickaxeTier >= 4)
            _renderer.AddLight(_player.Position, 28f, new Color(180, 220, 255));

        // Core: molten heart of the planet.
        _renderer.AddLight(_planet.Center, 90f, new Color(255, 90, 30));

        // Visible ores within a tile-radius of the player so we don't scan the whole map.
        // Same pass picks up player-placed lights (Glowshroom, Beacon) so a torch-lit room
        // illuminates correctly without us having to track each placeable separately.
        var (ptx, pty) = _planet.WorldToTile(_player.Position);
        const int oreScanR = 14;
        for (var dy = -oreScanR; dy <= oreScanR; dy++)
        {
            for (var dx = -oreScanR; dx <= oreScanR; dx++)
            {
                if (dx * dx + dy * dy > oreScanR * oreScanR) continue;
                var k = _planet.Get(ptx + dx, pty + dy);
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
                var op = _planet.TileToWorld(ptx + dx, pty + dy);
                _renderer.AddLight(op, r, glow);
            }
        }

        // Projectiles in flight glow by kind. Special-ammo glows match their element so the
        // player can see at a glance which shell is in flight — ruby = warm orange, sapphire =
        // cool cyan, diamond = bright white, harpoon = warm metallic, dynamite = orange fuse.
        foreach (var p in _projectiles)
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
                _ => (Color.White, 6f),
            };
            _renderer.AddLight(p.Position, r, col);
        }

        // Sentry muzzle glow: a small pre-flash that ramps with cooldown — about-to-fire
        // turrets pulse, idle ones are nearly dark. Helps the player see active overwatch in
        // a dim cave at a glance.
        foreach (var s in _sentries)
        {
            var ready = MathHelper.Clamp(1f - s.Cooldown / Sentry.FireRate, 0f, 1f);
            if (ready > 0.05f)
            {
                var dir = new Vector2(MathF.Cos(s.Aim), MathF.Sin(s.Aim));
                _renderer.AddLight(s.Position + dir * 9f, 4f + 6f * ready, new Color(255, 200, 120));
            }
        }

        // Kaiju eyes: gold → red as anger climbs. Two sockets, both lit. Position must mirror
        // the renderer (headBase = body + tup*26 + tright*facing*110, sockets at tup*8 ± 14).
        // Same visibility cull as the body — no point lighting an off-screen kaiju.
        if (kaijuVisible)
        {
            var anger01 = MathHelper.Clamp(_titan.Anger / 100f, 0f, 1f);
            var tup = _planet.UpAt(_titan.Position);
            var tright = new Vector2(-tup.Y, tup.X);
            var headBase = _titan.Position + tup * 26f + tright * (_titan.Facing * 110f);
            var lookDir = _player.Position - headBase;
            if (lookDir.LengthSquared() < 0.001f) lookDir = tright * _titan.Facing; else lookDir.Normalize();
            var lookRight = Vector2.Dot(lookDir, tright);
            var lookUp = Vector2.Dot(lookDir, tup);
            var eyeCol = Color.Lerp(new Color(255, 220, 100), new Color(255, 80, 40), anger01);
            for (var ei = -1; ei <= 1; ei += 2)
            {
                var socket = headBase + tup * 8f + tright * (ei * 14f);
                var pupil = socket + tright * lookRight * 3f + tup * lookUp * 2f;
                _renderer.AddLight(pupil, 22f + 20f * anger01, eyeCol);
            }
            // Tail-tip glow as a bonus light source — sells the verlet drag and the kaiju's
            // "atomic tail" look. Brightness ramps with anger.
            var tip = _titan.TailNodes[^1];
            _renderer.AddLight(tip, 18f + 22f * anger01,
                Color.Lerp(new Color(80, 130, 220), new Color(255, 90, 60), anger01));
        }

        // Glowing creatures — magma slugs read as drifting coals, cave eyes as a faint gleam.
        foreach (var c in _creatures) c.AddLight(_renderer);

        // Glowing particles (ore flecks, projectile sparks, explosion embers) feed back into
        // the lightmap so they actually illuminate the cave wall behind them.
        _particles.AddLights(_renderer);
        // Lava cells along their pool surface light up the cave roof (view-culled).
        _cells.AddLights(_renderer, viewCentre, viewRadius);

        // Depth darkness: subtract a radial gradient centred at the planet so deep tiles
        // dim toward black while the surface stays at full ambient. Radius = surfaceRadius
        // means the gradient tapers to zero exactly at the surface; falloff is quadratic
        // inward so most of the dimming concentrates near the inner half.
        var surfaceRadius = _planet.Radius * Planet.TileSize;
        _renderer.Darken(_planet.Center, surfaceRadius, new Color(170, 165, 185));

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

        var depth = _planet.Radius - (int)((_player.Position - _planet.Center).Length() / Planet.TileSize);
        var inv = _player.Inventory;
        // Top-left status: depth, titan HP, run meta. The toolbelt at the bottom of the
        // screen now carries the per-tool readout, so we don't duplicate it here.
        var status = $"DEPTH {depth}   TITAN HP {(int)_titan.Health}/{(int)_titan.MaxHealth}\n" +
                     $"META: ESCAPES {_meta.Escapes}  KILLS {_meta.TitansDefeated}  DEEPEST {_meta.DeepestDepth}";
        var controls = "WASD MOVE  SPACE JUMP  1-9 TOOLBELT  LMB USE  WHEEL CYCLE\n" +
                       "C CRAFT  T BEACON RECALL  L LAUNCH ROCKET  G GOD MODE  R RESTART";
        _renderer.DrawHudBars(VirtualWidth, VirtualHeight, _player, (int)_titan.Anger, status, controls);
        DrawInventoryPanel(inv);
        DrawToolbelt();
        if (_carry is { } carry) DrawCarry(carry.Id);

        DrawHoverDebugLabel();

        if (_craftingOpen) DrawCraftingMenu();

        if (_gameOver) DrawGameOverOverlay();

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
        var (cx, cy) = _cells.WorldToCell(worldCursor);
        var mat = _cells.Get(cx, cy);
        if (mat != Material.Empty)
        {
            label = $"MATERIAL: {mat}";
        }
        else
        {
            var (tx, ty) = _planet.WorldToTile(worldCursor);
            var tile = _planet.Get(tx, ty);
            if (tile == TileKind.Sky)
            {
                var wall = _planet.GetWall(tx, ty);
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

    private static bool Pressed(KeyboardState now, KeyboardState prev, Keys k)
        => now.IsKeyDown(k) && !prev.IsKeyDown(k);

    /// <summary>Draw a thick line segment from a→b as a single rotated rect. Used for the
    /// kaiju's procedural leg bones (hip→knee, knee→foot) and tail links. The rect's long
    /// axis aligns with b−a, so the segment connects the endpoints regardless of orientation.</summary>
    private void DrawLegSegment(Vector2 a, Vector2 b, float thickness, Color color)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 0.5f) return;
        var mid = (a + b) * 0.5f;
        var rot = MathF.Atan2(d.Y, d.X);
        _renderer.DrawRect(mid, new Vector2(len, thickness), color, rot);
    }

    /// <summary>Render a single procedural leg with 2-bone IK. The knee is computed from hip+foot
    /// using the law of cosines; if the foot is farther than the bones can normally reach, the
    /// knee snaps to a colinear midpoint so segments visibly stretch (the "leg reaching over a
    /// mountain" look). The knee is biased to bend outward+up so the silhouette reads as a
    /// quadruped rather than a stick figure.</summary>
    private void DrawTitanLeg(Entities.TitanLeg leg, Vector2 bodyPos, Vector2 bodyUp, Vector2 bodyRight,
                              Color hide, Color hideDark, Color hideLight, Color chitin, float pulse)
    {
        var hipWorld = bodyPos + bodyRight * leg.HipForward + bodyUp * leg.HipUp;
        var foot = leg.FootPos;
        var hipToFoot = foot - hipWorld;
        var dist = hipToFoot.Length();
        const float L1 = 92f;
        const float L2 = 92f;
        Vector2 knee;
        if (dist < 0.5f)
        {
            knee = hipWorld + (bodyRight * leg.Side + bodyUp) * 30f;
        }
        else if (dist >= L1 + L2)
        {
            // Stretched: legs visibly elongate when the foot is farther than the bone reach.
            knee = hipWorld + hipToFoot * (L1 / (L1 + L2));
        }
        else
        {
            var dir = hipToFoot / dist;
            var perp = new Vector2(-dir.Y, dir.X);
            var preferred = bodyRight * leg.Side + bodyUp * 0.6f;
            if (Vector2.Dot(perp, preferred) < 0) perp = -perp;
            var half = dist * 0.5f;
            var bend = MathF.Sqrt(MathF.Max(0f, L1 * L1 - half * half));
            var breath = MathF.Sin(pulse + leg.Phase * 6.28f) * 2f;
            var stepBlend = leg.StepT >= 1f ? 1f : 0.4f;
            knee = hipWorld + dir * half + perp * (bend + breath * stepBlend);
        }

        // Mid-step legs render slightly brighter so the swinging leg pops.
        var stepLift = leg.StepT >= 1f ? 0f : MathF.Sin(leg.StepT * MathF.PI);
        var thigh = stepLift > 0.05f ? hideDark : new Color(hideDark.R - 6, hideDark.G - 6, hideDark.B - 6);
        var shin = stepLift > 0.05f ? hide : hideDark;

        DrawLegSegment(hipWorld, knee, 22f, thigh);
        DrawLegSegment(knee, foot, 18f, shin);
        // Joint cap and clawed foot.
        _renderer.DrawCircle(knee, 11f, chitin);
        _renderer.DrawCircle(knee, 5.5f, hideLight);
        // Clawed foot — a wide chitin pad with three claw spikes radiating outward along the
        // ground tangent. Claws point in the direction the leg extends from the body.
        _renderer.DrawCircle(foot, 13f, chitin);
        var groundTangent = new Vector2(-bodyUp.Y, bodyUp.X) * leg.Side; // outward along surface
        for (var c = -1; c <= 1; c++)
        {
            var clawDir = groundTangent + bodyUp * (c * 0.6f - 0.3f);
            if (clawDir.LengthSquared() > 0.0001f) clawDir = Vector2.Normalize(clawDir);
            DrawLegSegment(foot, foot + clawDir * 14f, 4f, chitin);
        }
    }
}

internal static class CraftingExtensions
{
    public static Recipe? FirstOrDefaultRecipe(this IReadOnlyList<Recipe> all, string id)
    {
        foreach (var r in all) if (r.Id == id) return r;
        return null;
    }
}
