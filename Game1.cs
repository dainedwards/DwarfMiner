using System;
using System.Collections.Generic;
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
    private Planet _planet = null!;
    private Physics _physics = null!;
    private Player _player = null!;
    private Titan _titan = null!;
    private MetaSave _meta = null!;
    private Cells _cells = null!;

    private readonly List<Creature> _creatures = new();
    private readonly List<Projectile> _projectiles = new();
    private readonly List<FallingBoulder> _boulders = new();
    private readonly List<RockChunk> _rockChunks = new();
    private readonly Particles _particles = new();

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private float _earthquakeTimer;
    private float _spawnTimer;
    private float _shake;
    private bool _gameOver;
    private string _gameOverReason = "";
    private float _runTime;
    private bool _hasCannon;

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
        _physics = new Physics(_planet, _cells) { ChunkSink = _rockChunks };
        // Lava seeding: any cave (Sky tile) within 45% of the planet radius gets filled with
        // lava. So the inner half of the planet — from roughly the middle layer inward — has
        // lava pooled in any cavities, with the densest lava near the Core.
        _cells.FillSkyTilesWithin(_planet.Radius * 0.45f, Material.Lava);

        // Spawn the dwarf on top of whatever mountain is at angle -π/2 — walk down from
        // far above until the first solid tile, then float a few pixels above it.
        var surfacePos = FindSurfaceSpawn(-MathF.PI / 2f, _planet.Radius);
        _player = new Player(surfacePos)
        {
            // World-test loadout: ghost flight + super pickaxe + long arm. Toggle fly with G.
            PickaxePower = 50,
            MineRange = 200f,
            FlyMode = true,
        };
        _hasCannon = _meta.StartWithCannon;
        _titan = new Titan(_planet, MathF.PI * 0.6f);
        _creatures.Clear();
        _projectiles.Clear();
        _boulders.Clear();
        _rockChunks.Clear();
        _earthquakeTimer = 25f;
        _spawnTimer = 6f;
        _shake = 0;
        _gameOver = false;
        _gameOverReason = "";
        _runTime = 0;
    }

    protected override void LoadContent()
    {
        _renderer = new Renderer(GraphicsDevice);
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

        if (_gameOver)
        {
            if (Pressed(keys, _prevKeys, Keys.R)) StartNewRun();
            _prevKeys = keys; _prevMouse = mouse;
            base.Update(gameTime);
            return;
        }

        _runTime += dt;

        // Movement input — A/D or arrows along the player's local tangent.
        var moveAxis = 0;
        if (keys.IsKeyDown(Keys.A) || keys.IsKeyDown(Keys.Left)) moveAxis -= 1;
        if (keys.IsKeyDown(Keys.D) || keys.IsKeyDown(Keys.Right)) moveAxis += 1;
        var jumpPressed = Pressed(keys, _prevKeys, Keys.Space) || Pressed(keys, _prevKeys, Keys.W) || Pressed(keys, _prevKeys, Keys.Up);

        // Vertical input for fly mode: W/Up = ascend, S/Down = descend along local up.
        var verticalAxis = 0;
        if (keys.IsKeyDown(Keys.W) || keys.IsKeyDown(Keys.Up)) verticalAxis += 1;
        if (keys.IsKeyDown(Keys.S) || keys.IsKeyDown(Keys.Down)) verticalAxis -= 1;

        // G toggles fly mode for world testing.
        if (Pressed(keys, _prevKeys, Keys.G)) _player.FlyMode = !_player.FlyMode;

        _player.Update(dt, _planet, moveAxis, jumpPressed, verticalAxis);

        // Camera follows player, rotating so up = away from planet center.
        var up = _planet.UpAt(_player.Position);
        _camera.Follow(_player.Position, up, dt);

        // Mouse → world cursor. Account for screen shake.
        var screenMouse = new Vector2(mouse.X, mouse.Y);
        var worldCursor = _camera.ScreenToWorld(screenMouse);

        // Mining (left-click held) and shooting (right-click).
        if (mouse.LeftButton == ButtonState.Pressed)
        {
            var broken = _player.TryMine(_planet, _physics, worldCursor);
            if (broken is { } bk)
            {
                if (Tiles.Drop(bk) is not null) _meta.TotalOreMined++;
                var depth = _planet.Radius - (int)((_player.Position - _planet.Center).Length() / Planet.TileSize);
                if (depth > _meta.DeepestDepth) _meta.DeepestDepth = depth;
                var (btx, bty) = _planet.WorldToTile(worldCursor);
                _particles.EmitChips(_planet.TileToWorld(btx, bty), bk);
                // Loose ground tiles crumble into falling cells (Noita-feel pile-up).
                var loose = Materials.LooseFor(bk);
                if (loose != Material.Empty)
                    _cells.SpawnInTile(btx, bty, loose, Cells.Density * Cells.Density / 2);
            }
        }

        if (mouse.RightButton == ButtonState.Pressed && _player.ShootCooldown <= 0)
        {
            FireWeapon(worldCursor);
        }

        // Held Q places a block from inventory at the cursor (sky tile, in mining range).
        if (keys.IsKeyDown(Keys.Q))
        {
            _player.TryPlace(_planet, _physics, worldCursor);
        }

        // Crafting hotkeys.
        if (Pressed(keys, _prevKeys, Keys.D1)) TryCraft("pickaxe_ii", () => _player.PickaxePower++);
        if (Pressed(keys, _prevKeys, Keys.D2)) TryCraft("cannon", () => _hasCannon = true);
        if (Pressed(keys, _prevKeys, Keys.D3)) TryCraft("support", () => PlaceSupportAtFeet());
        if (Pressed(keys, _prevKeys, Keys.D4)) TryCraft("rocket_part", () => { /* counted via inventory["rocket_part"] */ });
        if (Pressed(keys, _prevKeys, Keys.D5)) TryCraft("nuke", () => { /* nuke held in inventory */ });

        // Fire nuke with F (consumes one).
        if (Pressed(keys, _prevKeys, Keys.F) && _player.Inventory.TryConsume("nuke", 1))
        {
            FireNuke(worldCursor);
        }

        // Launch rocket with L if 5 parts held and player on the surface.
        if (Pressed(keys, _prevKeys, Keys.L)) TryLaunchRocket();

        // Physics + particles + cells update.
        _physics.Update(dt);
        _particles.Update(dt, _planet);
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

        // Spawn creatures inside caves.
        _spawnTimer -= dt;
        if (_spawnTimer <= 0 && _creatures.Count < 14)
        {
            TrySpawnCreature();
            _spawnTimer = 5f + (float)Random.Shared.NextDouble() * 4f;
        }

        // Update entities.
        for (var i = _creatures.Count - 1; i >= 0; i--)
        {
            var c = _creatures[i];
            c.Update(dt, _planet, _player);
            if (c.Health <= 0) _creatures.RemoveAt(i);
        }

        for (var i = _projectiles.Count - 1; i >= 0; i--)
        {
            var p = _projectiles[i];
            p.Update(dt, _planet);
            // Hit creatures.
            for (var j = _creatures.Count - 1; j >= 0; j--)
            {
                var c = _creatures[j];
                if ((c.Position - p.Position).Length() < c.Radius + p.Radius)
                {
                    c.Health -= p.Damage;
                    c.HitFlash = 0.15f;
                    if (p.Kind == ProjectileKind.Bullet) p.Dead = true;
                }
            }
            // Hit titan.
            if (!p.Dead && (_titan.Position - p.Position).Length() < _titan.Radius + p.Radius)
            {
                _titan.Health -= p.Damage;
                _titan.HitFlash = 0.15f;
                if (p.Kind == ProjectileKind.Bullet) p.Dead = true;
            }
            if (p.Dead)
            {
                _particles.EmitImpact(p.Position, p.Kind);
                _projectiles.RemoveAt(i);
            }
        }

        _titan.Update(dt, _planet, _physics, _player.Position, _boulders);
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

        for (var i = _rockChunks.Count - 1; i >= 0; i--)
        {
            var c = _rockChunks[i];
            c.Update(dt, _planet, _physics, _player);
            if (c.Dead)
            {
                _particles.EmitChips(c.Position, c.Kind);
                _particles.EmitDust(c.Position, 4f);
                _shake = MathF.Max(_shake, 0.25f);
                _rockChunks.RemoveAt(i);
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

    private void FireWeapon(Vector2 worldCursor)
    {
        var dir = worldCursor - _player.Position;
        if (dir.LengthSquared() < 0.01f) return;
        dir.Normalize();
        if (_hasCannon)
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 320f, 25f, 1.6f, ProjectileKind.Cannon));
            _player.ShootCooldown = 0.55f;
        }
        else
        {
            _projectiles.Add(new Projectile(_player.Position + dir * 6f, dir * 420f, 6f, 1.4f, ProjectileKind.Bullet));
            _player.ShootCooldown = 0.18f;
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

    private void TryCraft(string id, Action onSuccess)
    {
        var recipe = Crafting.All.FirstOrDefaultRecipe(id);
        if (recipe is null) return;
        if (Crafting.TryPay(recipe, _player.Inventory))
        {
            // For inventory-tracked outputs, deposit into the inventory.
            if (id == "rocket_part") _player.Inventory.Add("rocket_part", 1);
            else if (id == "nuke") _player.Inventory.Add("nuke", 1);
            onSuccess();
        }
    }

    private void PlaceSupportAtFeet()
    {
        // Place a support tile right at the player's feet (1 tile inward along gravity).
        var gravDir = _planet.GravityAt(_player.Position);
        var target = _player.Position + gravDir * (Planet.TileSize * 0.8f);
        var (x, y) = _planet.WorldToTile(target);
        if (_planet.Get(x, y) == TileKind.Sky)
            _planet.Set(x, y, TileKind.Support);
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

    private void TrySpawnCreature()
    {
        // Pick a random polar tile (ring, angle) that is sky (cave) and far enough from surface.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var r = Random.Shared.Next(Planet.RingCount);
            var t = Random.Shared.Next(Planet.TilesAt(r));
            if (_planet.Get(r, t) != TileKind.Sky) continue;
            var pos = _planet.TileToWorld(r, t);
            var fromCenter = (pos - _planet.Center).Length();
            var surface = _planet.Radius * Planet.TileSize;
            if (fromCenter > surface - Planet.TileSize * 8f) continue;
            if ((pos - _player.Position).Length() < 100f) continue;
            _creatures.Add(new Creature(pos));
            return;
        }
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

        _renderer.DrawWorld(_planet, _camera);

        _renderer.BeginEntities(_camera);

        // Cells (sand/water/lava/smoke) draw above tiles but below entities so the dwarf walks
        // in front of his own debris pile.
        _cells.Draw(_renderer);

        // Pixel-art dwarf sprite — drawn rotated to align local-up with planet's outward radial.
        // Sprite head-at-top, feet-at-bottom; the rotation maps sprite-up to world-up.
        var up = _planet.UpAt(_player.Position);
        var rot = MathF.Atan2(up.X, -up.Y);
        const float spriteScale = 0.6f; // world units per sprite pixel
        _renderer.Batch.Draw(_dwarfTex, _player.Position, null, Color.White, rot,
            new Vector2(_dwarfTex.Width * 0.5f, _dwarfTex.Height * 0.5f),
            spriteScale, SpriteEffects.None, 0f);

        // Reticle.
        var mouse = Mouse.GetState();
        var worldCursor = _camera.ScreenToWorld(new Vector2(mouse.X, mouse.Y));
        _renderer.DrawCircle(worldCursor, 3f, new Color(255, 255, 255, 180));

        // Creatures.
        foreach (var c in _creatures)
        {
            var col = c.HitFlash > 0 ? Color.White : new Color(180, 60, 80);
            _renderer.DrawCircle(c.Position, c.Radius, col);
            _renderer.DrawCircle(c.Position - new Vector2(0, 1f), 1.5f, Color.Black);
        }

        // Projectiles.
        foreach (var p in _projectiles)
        {
            var col = p.Kind switch
            {
                ProjectileKind.Bullet => new Color(255, 230, 120),
                ProjectileKind.Cannon => new Color(255, 140, 60),
                ProjectileKind.Nuke => new Color(255, 80, 200),
                _ => Color.White,
            };
            _renderer.DrawCircle(p.Position, p.Radius, col);
        }

        // Boulders.
        foreach (var b in _boulders)
        {
            _renderer.DrawCircle(b.Position, b.Radius, new Color(80, 70, 60));
        }

        // Falling rock chunks from stone collapses — drawn as tumbling squares of the original
        // tile colour with per-chunk jitter so a 50-chunk collapse reads as fragments, not a
        // uniform stamp. During the tremble warning, the chunk is at its tile spot oscillating.
        foreach (var c in _rockChunks)
        {
            var col = Tiles.BaseColor(c.Kind);
            var jit = (int)(c.ColorJitter * 12f);
            col = new Color(
                Math.Clamp(col.R + jit, 0, 255),
                Math.Clamp(col.G + jit, 0, 255),
                Math.Clamp(col.B + jit, 0, 255));
            _renderer.DrawRect(c.Position, new Vector2(c.Radius * 1.8f, c.Radius * 1.8f), col, c.Rotation);
        }

        // Titan: six-legged creature. Body is a hulking chitinous oval; legs are procedural —
        // each foot's world position is set by Titan.UpdateLegs from terrain probes, and the
        // hip→knee→foot chain is solved here per frame with 2-bone IK that allows stretching
        // when the foot is far from the hip (so legs visibly elongate over peaks/pits).
        {
            var tup = _planet.UpAt(_titan.Position);
            var tright = new Vector2(-tup.Y, tup.X);
            var trot = MathF.Atan2(tup.X, -tup.Y);
            var tp = _titan.Position;
            var anger01 = MathHelper.Clamp(_titan.Anger / 100f, 0f, 1f);
            var skin = _titan.HitFlash > 0
                ? Color.White
                : Color.Lerp(new Color(50, 38, 58), new Color(170, 55, 45), anger01);
            var skinDark = new Color(skin.R / 2, skin.G / 2, skin.B / 2);
            var skinLight = new Color(
                Math.Clamp(skin.R + 32, 0, 255),
                Math.Clamp(skin.G + 32, 0, 255),
                Math.Clamp(skin.B + 32, 0, 255));
            var chitin = new Color(22, 16, 22);

            // === Legs first, so the body draws over the hips ===
            // Per-leg pulse adds a tiny knee-bend wobble synced to the global Pulse so the
            // creature looks alive even when standing still.
            for (var li = 0; li < _titan.Legs.Length; li++)
            {
                var leg = _titan.Legs[li];
                var hipWorld = tp + tright * leg.HipForward + tup * leg.HipUp;
                var foot = leg.FootPos;
                var hipToFoot = foot - hipWorld;
                var dist = hipToFoot.Length();
                // Two-bone IK with stretchy fallback. L1 + L2 = nominal max; if |hip-foot| is
                // greater, knee snaps to a colinear midpoint so segments visibly stretch.
                const float L1 = 86f;
                const float L2 = 86f;
                Vector2 knee;
                if (dist < 0.5f)
                {
                    knee = hipWorld + (tright * leg.Side + tup) * 30f;
                }
                else if (dist >= L1 + L2)
                {
                    // Stretched: knee on the line at L1/(L1+L2). Both segments visually long.
                    knee = hipWorld + hipToFoot * (L1 / (L1 + L2));
                }
                else
                {
                    var dir = hipToFoot / dist;
                    var perp = new Vector2(-dir.Y, dir.X);
                    // Force knees to bend "outward + up" relative to body, so the silhouette
                    // looks like a spider/crab — knees jut up and away from the body.
                    var preferred = tright * leg.Side + tup * 0.7f;
                    if (Vector2.Dot(perp, preferred) < 0) perp = -perp;
                    var half = dist * 0.5f;
                    var bend = MathF.Sqrt(MathF.Max(0f, L1 * L1 - half * half));
                    // Tiny breathing wobble in the knee bend, scaled down when foot is mid-step.
                    var breath = MathF.Sin(_titan.Pulse + leg.Phase * 6.28f) * 2.5f;
                    var stepBlend = leg.StepT >= 1f ? 1f : 0.4f;
                    knee = hipWorld + dir * half + perp * (bend + breath * stepBlend);
                }

                // Mid-step legs render slightly brighter so the swinging leg "pops" — sells
                // the locomotion to the eye.
                var stepLift = leg.StepT >= 1f ? 0f : MathF.Sin(leg.StepT * MathF.PI);
                var thigh = stepLift > 0.05f ? skinDark : new Color(skinDark.R - 6, skinDark.G - 6, skinDark.B - 6);
                var shin = stepLift > 0.05f ? skin : skinDark;

                DrawLegSegment(hipWorld, knee, 14f, thigh);
                DrawLegSegment(knee, foot, 11f, shin);
                // Joint cap and foot — small dark beads to imply chitinous joints.
                _renderer.DrawCircle(knee, 7.5f, chitin);
                _renderer.DrawCircle(knee, 4f, skinLight);
                _renderer.DrawCircle(foot, 9f, chitin);
                _renderer.DrawCircle(foot, 5f, new Color(skinDark.R + 14, skinDark.G + 14, skinDark.B + 14));
            }

            // === Body: a wide chitinous oval, sat low so the legs splay around it ===
            // Carapace plates: stack of progressively narrower rects above the underbelly.
            // The pulse drives a subtle vertical breath so the body inflates/deflates.
            var breath2 = MathF.Sin(_titan.Pulse) * 1.4f;
            _renderer.DrawRect(tp + tup * (10f + breath2), new Vector2(220f, 90f), skinDark, trot);
            _renderer.DrawRect(tp + tup * (24f + breath2), new Vector2(200f, 70f), skin, trot);
            _renderer.DrawRect(tp + tup * (40f + breath2), new Vector2(168f, 44f), skinLight, trot);
            // Underbelly seam — strong dark line just under the body, helps sell volume.
            _renderer.DrawRect(tp + tup * -28f, new Vector2(196f, 4f), chitin, trot);
            // Back ridge: 5 spinal plates running tangentially across the carapace.
            for (var s = -2; s <= 2; s++)
            {
                var plateW = 18f - MathF.Abs(s) * 2f;
                var plateH = 14f - MathF.Abs(s) * 2f;
                _renderer.DrawRect(tp + tup * (52f + breath2) + tright * (s * 30f),
                    new Vector2(plateW, plateH), chitin, trot);
                _renderer.DrawRect(tp + tup * (54f + breath2) + tright * (s * 30f),
                    new Vector2(plateW - 6f, 2f), skinLight, trot);
            }

            // === Head: a forward bulb on the body's facing side, with mandibles + a single eye ===
            // Facing is smoothed -1..+1; lerp the head offset along the tangent so it slides
            // smoothly when the creature changes direction.
            var face = _titan.Facing;
            var headBase = tp + tup * 18f + tright * (face * 92f);
            _renderer.DrawRect(headBase + tup * 10f, new Vector2(70f, 56f), skinDark, trot);
            _renderer.DrawRect(headBase + tup * 18f, new Vector2(58f, 40f), skin, trot);
            _renderer.DrawRect(headBase + tup * 28f, new Vector2(40f, 18f), skinLight, trot);
            // Mandibles — two short curved stubs flanking the front of the head, opening with
            // anger. Idle: closed; angry: spread apart and twitch on the pulse.
            var mandSpread = 14f + anger01 * 8f + MathF.Sin(_titan.Pulse * 2.2f) * (1f + anger01 * 2.5f);
            var mandOut = face * 30f;
            _renderer.DrawRect(headBase + tup * -2f + tright * (mandOut + face * 6f) + tup * 2f,
                new Vector2(28f, 8f), chitin, trot + face * 0.35f);
            _renderer.DrawRect(headBase + tup * -2f - tright * (-mandOut + face * 6f),
                new Vector2(28f, 8f), chitin, trot - face * 0.35f);
            _ = mandSpread; // reserved for future per-mandible animation if needed

            // Single glowing eye, tracks the player. Project the look direction onto the head's
            // local right/up basis so the pupil slides within the eye socket.
            var lookDir = _player.Position - headBase;
            if (lookDir.LengthSquared() < 0.001f) lookDir = tright * face;
            else lookDir.Normalize();
            var eyeCenter = headBase + tup * 22f;
            var lookRight = Vector2.Dot(lookDir, tright);
            var lookUp = Vector2.Dot(lookDir, tup);
            var eyePos = eyeCenter + tright * lookRight * 6f + tup * lookUp * 4f;
            _renderer.DrawCircle(eyeCenter, 16f, chitin);
            _renderer.DrawCircle(eyePos, 12f, Color.Lerp(new Color(255, 230, 100), new Color(255, 100, 60), anger01));
            _renderer.DrawCircle(eyePos, 4.5f, Color.Black);
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

        // Player aura — four nested radials stacked together for a soft, wide glow.
        _renderer.AddLight(_player.Position, 200f, new Color(85, 70, 50));
        _renderer.AddLight(_player.Position, 140f, new Color(140, 115, 80));
        _renderer.AddLight(_player.Position, 90f,  new Color(200, 170, 120));
        _renderer.AddLight(_player.Position, 50f,  new Color(245, 215, 165));

        // Core: molten heart of the planet.
        _renderer.AddLight(_planet.Center, 90f, new Color(255, 90, 30));

        // Visible ores within a tile-radius of the player so we don't scan the whole map.
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
                    default: continue;
                }
                var op = _planet.TileToWorld(ptx + dx, pty + dy);
                _renderer.AddLight(op, r, glow);
            }
        }

        // Projectiles in flight glow by kind.
        foreach (var p in _projectiles)
        {
            var (col, r) = p.Kind switch
            {
                ProjectileKind.Bullet => (new Color(255, 220, 110), 8f),
                ProjectileKind.Cannon => (new Color(255, 130, 50),  16f),
                ProjectileKind.Nuke   => (new Color(255, 80, 220),  28f),
                _ => (Color.White, 6f),
            };
            _renderer.AddLight(p.Position, r, col);
        }

        // Titan eye: gold → red as anger climbs, scales up too. The eye sits on the creature's
        // forward-facing head bulb (tup * 18 + tright * facing * 92, plus tup * 22 for the eye
        // socket within the head), so the lit position must match the renderer.
        if (_titan.Health > 0)
        {
            var anger01 = MathHelper.Clamp(_titan.Anger / 100f, 0f, 1f);
            var tup = _planet.UpAt(_titan.Position);
            var tright = new Vector2(-tup.Y, tup.X);
            var headBase = _titan.Position + tup * 18f + tright * (_titan.Facing * 92f);
            var lookDir = _player.Position - headBase;
            if (lookDir.LengthSquared() < 0.001f) lookDir = tright * _titan.Facing; else lookDir.Normalize();
            var eyeCenter = headBase + tup * 22f;
            var eyePos = eyeCenter + tright * Vector2.Dot(lookDir, tright) * 6f + tup * Vector2.Dot(lookDir, tup) * 4f;
            _renderer.AddLight(eyePos, 32f + 28f * anger01,
                Color.Lerp(new Color(255, 220, 100), new Color(255, 80, 40), anger01));
        }

        // Glowing particles (ore flecks, projectile sparks, explosion embers) feed back into
        // the lightmap so they actually illuminate the cave wall behind them.
        _particles.AddLights(_renderer);
        // Lava cells along their pool surface light up the cave roof.
        _cells.AddLights(_renderer);

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
        var status = $"DEPTH {depth}   COAL {inv.Count("coal")}  IRON {inv.Count("iron")}  GOLD {inv.Count("gold")}  CRYSTAL {inv.Count("crystal")}\nROCKET PARTS {inv.Count("rocket_part")}/5   NUKES {inv.Count("nuke")}   TITAN HP {(int)_titan.Health}/{(int)_titan.MaxHealth}\nMETA: ESCAPES {_meta.Escapes}  KILLS {_meta.TitansDefeated}  DEEPEST {_meta.DeepestDepth}";
        var controls = "WASD/ARROWS MOVE   SPACE JUMP   LMB MINE   Q PLACE   RMB SHOOT   F NUKE   L LAUNCH ROCKET\n1 PICKAXE+   2 CANNON   3 SUPPORT BEAM   4 ROCKET PART   5 NUKE";
        _renderer.DrawHudBars(VirtualWidth, VirtualHeight, _player, (int)_titan.Anger, status, controls);

        if (_gameOver) DrawGameOverOverlay();

        base.Draw(gameTime);
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
    /// titan's procedural leg bones (hip→knee, knee→foot). The rect's long axis aligns with
    /// b−a, so the segment looks like a beam connecting the joints regardless of orientation.</summary>
    private void DrawLegSegment(Vector2 a, Vector2 b, float thickness, Color color)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 0.5f) return;
        var mid = (a + b) * 0.5f;
        var rot = MathF.Atan2(d.Y, d.X);
        _renderer.DrawRect(mid, new Vector2(len, thickness), color, rot);
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
