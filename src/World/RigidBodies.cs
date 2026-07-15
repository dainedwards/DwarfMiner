using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DwarfMiner.World;

/// <summary>
/// Noita-style rigid debris: a detached region of solid tiles leaves the grid and becomes a
/// tumbling body that keeps its shape — it falls, rotates, bounces off terrain, and when it
/// comes to rest its tiles stamp back into the planet, mass-conserved (cells that can't find
/// a free tile spill out as ordinary dust). The polar grid means bodies live in Cartesian
/// world space: each payload cell remembers its offset from the centre of mass at detach
/// time, and every grid interaction (collision sampling, hazard erosion, re-stamping) goes
/// through Planet.WorldToTile per cell, so band-halving boundaries and ring curvature never
/// need special cases.
///
/// Coupling with the rest of the sim:
///   • Physics.CollapseRegion routes qualifying stone regions here instead of dusting them
///     (the tremble telegraph still runs first — see Physics.PendingCollapse.Rigid).
///   • Explosions near a fresh detach launch it ballistically (see NoteBlast), so blasted
///     walls fly apart in chunks instead of politely slumping.
///   • Fire/lava/acid in the cell sim eat a live body's cells; losing cells can split the
///     body into separate pieces, each of which keeps tumbling on its own.
///   • Bodies collide with terrain tiles only — they sink through loose cell-sim dust
///     (heavy rock through powder) and crush flora tiles they land on.
/// </summary>
public sealed class RigidBodies
{
    /// <summary>Kill switch: DM_RIGID=0 reverts every detach back to the legacy dust crumble.</summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("DM_RIGID") != "0";

    /// <summary>Largest crust-backed region that converts to a single body. Regions between
    /// this and <see cref="MaxDetachTiles"/> split into several bodies (an underground shelf
    /// breaks into boulders, not one rotating cutout). Fully above-ground regions ignore
    /// both limits — see <see cref="TryDetach"/>.</summary>
    public const int MaxChunkTiles = 400;

    /// <summary>Ceiling for rigid conversion of regions that reach down to the crust — past
    /// it the legacy dust cascade runs. Regions floating entirely above the crust (severed
    /// skyscrapers, undercut mountains) are exempt: those topple whole, whatever their size,
    /// because half a structure raining down as sand while the rest tumbles reads wrong.</summary>
    public const int MaxDetachTiles = 2400;

    /// <summary>World px per partition cell when a big region splits into boulders (~18
    /// tiles across → chunks near MaxChunkTiles).</summary>
    private const float BucketSpan = 72f;

    /// <summary>Live-body budget. Over budget, new detaches fall back to dust rather than
    /// force-stamping an old body mid-air (which would leave floating terrain).</summary>
    public const int MaxBodies = 20;

    private const float Gravity = 300f;          // px/s² toward the core — reads heavy
    private const float Restitution = 0.12f;     // rock thuds, it doesn't bounce
    /// <summary>Impacts slower than this don't bounce at all — resting contact must be
    /// perfectly inelastic or the restitution + push-out cycle pumps micro-jitter forever
    /// and the body never sleeps (measured: ±20 px/s of noise, SleepTimer pinned at 0).</summary>
    private const float BounceThreshold = 60f;
    private const float Friction = 0.55f;
    private const float SleepSpeed = 18f;        // px/s below which the sleep timer runs
    private const float SleepSpin = 0.35f;       // rad/s
    private const float SleepAfter = 0.6f;       // seconds of stillness before stamping
    private const float MaxSpeed = 460f;
    private const float ErodeInterval = 0.22f;   // hazard sampling cadence per body
    /// <summary>How long after a blast a fresh detach still inherits its launch impulse —
    /// covers the settle tick (50ms) + tremble (0.35s) between the carve and the detach.</summary>
    private const float BlastMemory = 0.6f;

    public struct BodyCell
    {
        public Vector2 Local;   // offset from centre of mass in the body frame (angle 0)
        public TileKind Kind;
        public bool Surface;    // has a missing lattice neighbour → contact/erosion point
        /// <summary>Original grid coords — keep the tile's atlas variant + colour jitter
        /// stable, so a chunk in flight looks exactly like the wall it broke out of.</summary>
        public int R, T;
        /// <summary>4-bit missing-neighbour mask at detach (outer=1 inner=2 left=4 right=8,
        /// the terrain renderer's exposure convention) — selects the baked ragged-erosion
        /// atlas frame so the chunk silhouette matches eroded terrain.</summary>
        public byte Expose;
        /// <summary>The tile's radial art rotation at detach (tile angle + 90°). Drawn at
        /// BaseRot + the body's current Angle, the art never pops when the chunk shears off.</summary>
        public float BaseRot;
        /// <summary>Burn state carried OFF the grid: 0 = not burning; otherwise the tile's
        /// BurningTiles clock + 1 at detach (offset so 0 stays "none"). Rides the cell
        /// through splits and re-ignites via Cells.IgniteTile when the body stamps —
        /// fire persists on a chunk that breaks free and falls.</summary>
        public float Burn;
    }

    public sealed class Body
    {
        public Vector2 Position;      // world-space centre of mass
        public Vector2 Velocity;
        public float Angle;
        public float Spin;            // rad/s
        public readonly List<BodyCell> Cells = new();
        public float InvMass;
        public float InvInertia;
        public float BoundRadius;     // world-space bounding circle around Position
        public float SleepTimer;
        /// <summary>Seconds since terrain contact. A resting body alternates contact/free
        /// every few frames (the push-out separates it, gravity re-engages), so sleep and
        /// damping decisions use "touched recently", never "touching this exact frame".</summary>
        public float SinceContact = 10f;
        public float ErodeTimer;
        public bool Dead;

        /// <summary>Indices of the surface cells, rebuilt with the mass properties. The
        /// contact solver runs per substep and the actor-overlap probe per creature — a
        /// whole-structure body carries tens of thousands of cells, so those hot paths walk
        /// this short list instead of scanning the full payload for the Surface flag.</summary>
        public readonly List<int> SurfaceIdx = new();

        /// <summary>Rebuild mass/inertia/bounds (and the surface index) from the current
        /// payload — called at spawn and again whenever erosion removes cells, always after
        /// surface flags are final. Unit mass per cell.</summary>
        public void RecomputeMass()
        {
            var n = Cells.Count;
            if (n == 0) { Dead = true; return; }
            var com = Vector2.Zero;
            foreach (var c in Cells) com += c.Local;
            com /= n;
            if (com.LengthSquared() > 0.01f)
            {
                // Re-centre so Position stays the true centre of mass after cell loss —
                // otherwise an eroded body spins about a phantom point.
                Position += Rotate(com, Angle);
                for (var i = 0; i < Cells.Count; i++)
                {
                    var c = Cells[i];
                    c.Local -= com;
                    Cells[i] = c;
                }
            }
            var inertia = 0f;
            var maxSq = 0f;
            SurfaceIdx.Clear();
            for (var i = 0; i < Cells.Count; i++)
            {
                var c = Cells[i];
                var d = c.Local.LengthSquared();
                inertia += d + Planet.TileSize * Planet.TileSize / 6f;
                maxSq = MathF.Max(maxSq, d);
                if (c.Surface) SurfaceIdx.Add(i);
            }
            InvMass = 1f / n;
            InvInertia = 1f / MathF.Max(1f, inertia);
            BoundRadius = MathF.Sqrt(maxSq) + Planet.TileSize;
        }
    }

    private readonly Planet _planet;
    private readonly Cells _cells;
    private readonly Physics _physics;
    public readonly List<Body> Bodies = new();

    /// <summary>Hard landings this tick: (position, impact speed × mass). Game1 drains these
    /// for shake/dust/sound, same pattern as Cells.PendingGemDrops.</summary>
    public readonly List<(Vector2 pos, float force)> Impacts = new();

    private readonly List<(Vector2 pos, float radius, float power, float age)> _blasts = new();

    // Scratch buffers for detach/stamp/split — reused so a cascade of detaches doesn't churn
    // the heap mid-disaster.
    private readonly HashSet<int> _regionSet = new();
    private readonly List<Vector2> _contacts = new();
    private readonly List<Vector2> _normals = new();

    /// <summary>Body-frame spatial hash over payload cells, rebuilt per split/surface pass.
    /// Whole-structure detaches carry tens of thousands of cells, so the all-pairs distance
    /// scans the split and surface passes used to run are O(n²) cliffs — a toppling mountain
    /// froze the frame. Bucket span 2 tiles: the 1.5-tile adjacency reach never crosses more
    /// than one bucket, so a 3×3 bucket probe sees every candidate neighbour.</summary>
    private readonly Dictionary<long, List<int>> _cellHash = new();
    private const float HashSpan = Planet.TileSize * 2f;

    private static long HashKey(Vector2 local) =>
        ((long)(int)MathF.Floor(local.X / HashSpan) << 32)
        ^ (uint)(int)MathF.Floor(local.Y / HashSpan);

    private void BuildCellHash(Body b)
    {
        _cellHash.Clear();
        for (var i = 0; i < b.Cells.Count; i++)
        {
            var key = HashKey(b.Cells[i].Local);
            if (!_cellHash.TryGetValue(key, out var list)) _cellHash[key] = list = new List<int>(8);
            list.Add(i);
        }
    }

    public RigidBodies(Planet planet, Cells cells, Physics physics)
    {
        _planet = planet;
        _cells = cells;
        _physics = physics;
    }

    public static Vector2 Rotate(Vector2 v, float a)
    {
        var c = MathF.Cos(a);
        var s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>Remember an explosion so a region it just carved free launches outward when
    /// Physics detaches it a tick or two later (see BlastMemory).</summary>
    public void NoteBlast(Vector2 pos, float radius, float power)
    {
        _blasts.Add((pos, MathF.Max(radius, 24f), power, 0f));
        // Bodies already in flight take the impulse immediately.
        foreach (var b in Bodies) ApplyBlast(b, pos, MathF.Max(radius, 24f), power);
    }

    private static void ApplyBlast(Body b, Vector2 pos, float radius, float power)
    {
        var d = b.Position - pos;
        var dist = d.Length();
        if (dist > radius + b.BoundRadius) return;
        var dir = dist > 0.5f ? d / dist : new Vector2(0, -1);
        var falloff = 1f - MathHelper.Clamp(dist / (radius + b.BoundRadius), 0f, 1f);
        // Heft: chunks up to boulder size take the full launch (tuned there), but a whole
        // skyscraper or mountain barely shrugs — the same charge can't hurl both alike.
        var heft = MathF.Min(1f, MaxChunkTiles * b.InvMass);
        b.Velocity += dir * power * falloff * heft;
        // Off-centre kick: torque from the blast reaching one side of the body first.
        b.Spin += (dir.X * d.Y - dir.Y * d.X) * power * falloff * b.InvInertia * 0.5f;
        b.SleepTimer = 0f;
    }

    /// <summary>A titan's kick or fist meeting toppled debris: every body cell inside the
    /// radius is spilled straight to dust — the monster grinds fallen masonry to powder
    /// instead of batting it around. What survives is re-surfaced, split, and killed off
    /// exactly like hazard erosion (<see cref="Erode"/>) so partial hits stay Noita-true.</summary>
    public void Pulverize(Vector2 pos, float radius)
    {
        var rSq = radius * radius;
        for (var bi = Bodies.Count - 1; bi >= 0; bi--)
        {
            var b = Bodies[bi];
            if ((b.Position - pos).Length() > radius + b.BoundRadius) continue;
            var removed = false;
            for (var i = b.Cells.Count - 1; i >= 0; i--)
            {
                var wp = b.Position + Rotate(b.Cells[i].Local, b.Angle);
                if ((wp - pos).LengthSquared() > rSq) continue;
                var (tx, ty) = _planet.WorldToTile(wp);
                _cells.SpawnDustFraction(tx, ty, b.Cells[i].Kind, 0.6f);
                b.Cells.RemoveAt(i);
                removed = true;
            }
            if (!removed) continue;
            if (b.Cells.Count < 3)
            {
                foreach (var c in b.Cells)
                {
                    var wp = b.Position + Rotate(c.Local, b.Angle);
                    var (tx, ty) = _planet.WorldToTile(wp);
                    _cells.SpawnDustFraction(tx, ty, c.Kind, 0.5f);
                }
                b.Dead = true;
                Bodies.RemoveAt(bi);
                continue;
            }
            RebuildSurface(b);
            SplitIfDisconnected(b);
            b.RecomputeMass();
        }
    }

    private readonly List<int> _detach = new();

    /// <summary>
    /// Detach a condemned region into rigid bodies. Tiles are removed from the grid here.
    /// A <paramref name="sky"/> region — floating entirely above the crust, i.e. a severed
    /// skyscraper section or an undercut mountain — always converts WHOLE as one body, no
    /// size cap: above-ground structures topple in one piece. Crust-backed regions keep the
    /// graded treatment: up to MaxChunkTiles one body; up to MaxDetachTiles several boulders
    /// (world-space buckets, connectivity-split); bigger declines. Returns false (grid
    /// untouched) when disabled or over the body budget — caller runs the legacy crumble.
    /// </summary>
    public bool TryDetach(List<int> regionTiles, bool sky)
    {
        if (!Enabled || Bodies.Count >= MaxBodies) return false;
        if (!sky && regionTiles.Count > MaxDetachTiles) return false;

        // Gather the still-valid tiles (some may have been mined/melted during the tremble).
        _regionSet.Clear();
        _detach.Clear();
        foreach (var idx in regionTiles)
        {
            var (x, y) = _planet.UnIndex(idx);
            if (!Tiles.CanFall(_planet.Get(x, y))) continue;
            _regionSet.Add(idx);
            _detach.Add(idx);
        }
        if (_detach.Count < 4) return false;

        if (sky || _detach.Count <= MaxChunkTiles)
        {
            SpawnBody(_detach);
            return true;
        }

        // Mountain-scale region: bucket by world position so it breaks into boulder-sized
        // pieces along a rough grid. Expose masks still come from the FULL region set, so
        // the outer silhouette keeps its ragged erosion while the bucket-to-bucket cut
        // faces stay clean — fresh fracture surfaces.
        var buckets = new Dictionary<(int, int), List<int>>();
        foreach (var idx in _detach)
        {
            var (x, y) = _planet.UnIndex(idx);
            var wp = _planet.TileToWorld(x, y);
            var key = ((int)MathF.Floor(wp.X / BucketSpan), (int)MathF.Floor(wp.Y / BucketSpan));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
            list.Add(idx);
        }
        // Biggest buckets claim body slots first; the rest (and any crumbs) dust in place.
        var ordered = new List<List<int>>(buckets.Values);
        ordered.Sort((a, b) => b.Count - a.Count);
        foreach (var bucket in ordered)
        {
            if (Bodies.Count < MaxBodies && bucket.Count >= 4)
            {
                SpawnBody(bucket);
                continue;
            }
            foreach (var idx in bucket)
            {
                var (x, y) = _planet.UnIndex(idx);
                var k = _planet.Get(x, y);
                _planet.Set(x, y, TileKind.Sky);
                _planet.ClearStructureWall(x, y);
                _cells.SpawnDustInTile(x, y, k);
                _physics.MarkDirty(x, y);
            }
        }
        return true;
    }

    /// <summary>Lift one group of tiles (membership tested against <see cref="_regionSet"/>
    /// for exposure) out of the grid as a body: capture each cell's kind, grid coords, art
    /// rotation and erosion mask, clear the tiles, seed a sliver of spin, inherit any recent
    /// blast, and connectivity-split in case the group holds separate lumps.</summary>
    private void SpawnBody(List<int> tiles, Vector2 initialVel = default, float? initialSpin = null)
    {
        var com = Vector2.Zero;
        foreach (var idx in tiles)
        {
            var (x, y) = _planet.UnIndex(idx);
            com += _planet.TileToWorld(x, y);
        }
        com /= tiles.Count;

        var body = new Body { Position = com };
        foreach (var idx in tiles)
        {
            var (x, y) = _planet.UnIndex(idx);
            var wp = _planet.TileToWorld(x, y);
            var expose = ExposeMask(x, y);
            var rel = wp - _planet.Center;
            body.Cells.Add(new BodyCell
            {
                Local = wp - com,
                Kind = _planet.Get(x, y),
                Surface = expose != 0,
                R = x,
                T = y,
                Expose = expose,
                BaseRot = MathF.Atan2(rel.Y, rel.X) + MathHelper.PiOver2,
            });
        }
        foreach (var idx in tiles)
        {
            var (x, y) = _planet.UnIndex(idx);
            _planet.Set(x, y, TileKind.Sky);
            _planet.ClearStructureWall(x, y);
            _physics.MarkDirty(x, y);
        }
        body.RecomputeMass();
        // A sliver of spin so even a clean vertical drop tumbles a little (callers with a
        // real tip direction — felled logs — pass their own).
        body.Velocity = initialVel;
        body.Spin = initialSpin ?? ((float)Random.Shared.NextDouble() - 0.5f) * 0.6f;

        // A blast that just carved this region free hurls it outward.
        foreach (var (bp, br, pw, age) in _blasts)
            if (age <= BlastMemory) ApplyBlast(body, bp, br, pw);

        // Motion is set before the split so any separate lumps inherit it.
        Bodies.Add(body);
        SplitIfDisconnected(body);
        body.RecomputeMass();
    }

    /// <summary>Spawn a body directly from explicit tiles (tree logs). Removes them from the
    /// grid itself; returns false untouched when disabled or over budget.</summary>
    public bool SpawnFromTiles(List<(int x, int y)> tiles, Vector2 initialVel, float initialSpin)
    {
        if (!Enabled || Bodies.Count >= MaxBodies || tiles.Count < 3) return false;
        _regionSet.Clear();
        _detach.Clear();
        foreach (var (x, y) in tiles)
        {
            var idx = _planet.Index(x, y);
            _regionSet.Add(idx);
            _detach.Add(idx);
        }
        SpawnBody(_detach, initialVel, initialSpin);
        return true;
    }

    /// <summary>Missing-neighbour mask at detach time, on the original polar adjacency, in
    /// the terrain renderer's exposure convention (outer=1 inner=2 left=4 right=8). Non-zero
    /// means surface: a contact point for the solver and a ragged-erosion atlas frame for
    /// the renderer. Interior cells can never touch terrain first, so the solver skips them.</summary>
    private byte ExposeMask(int x, int y)
    {
        var mask = 0;
        var oc = _planet.OuterNeighbourCount(x, y);
        for (var i = 0; i < oc; i++)
        {
            var (or_, ot_) = _planet.OuterNeighbour(x, y, i);
            if (or_ >= _planet.Rings || !_regionSet.Contains(_planet.Index(or_, ot_))) { mask |= 1; break; }
        }
        var (ir, it) = _planet.InnerNeighbour(x, y);
        if (ir < 0 || !_regionSet.Contains(_planet.Index(ir, it))) mask |= 2;
        if (!_regionSet.Contains(_planet.Index(x, y - 1))) mask |= 4;
        if (!_regionSet.Contains(_planet.Index(x, y + 1))) mask |= 8;
        return (byte)mask;
    }

    public void Update(float dt)
    {
        Impacts.Clear();
        for (var i = _blasts.Count - 1; i >= 0; i--)
        {
            var b = _blasts[i];
            b.age += dt;
            if (b.age > BlastMemory) _blasts.RemoveAt(i);
            else _blasts[i] = b;
        }

        for (var i = Bodies.Count - 1; i >= 0; i--)
        {
            var b = Bodies[i];
            Step(b, dt);
            if (b.Dead) Bodies.RemoveAt(i);
        }
    }

    private void Step(Body b, float dt)
    {
        b.ErodeTimer += dt;
        if (b.ErodeTimer >= ErodeInterval)
        {
            b.ErodeTimer = 0f;
            Erode(b);
            if (b.Dead) return;
        }

        var grav = _planet.GravityAt(b.Position) * Gravity;
        b.Velocity += grav * dt;
        var speed = b.Velocity.Length();
        if (speed > MaxSpeed) b.Velocity *= MaxSpeed / speed;

        // Substep so fast bodies can't tunnel a 4-px tile in one integration.
        var steps = Math.Clamp((int)MathF.Ceiling((speed * dt + MathF.Abs(b.Spin) * b.BoundRadius * dt) / 2f), 1, 4);
        var sub = dt / steps;
        var contacted = false;
        for (var s = 0; s < steps && !b.Dead; s++)
        {
            b.Position += b.Velocity * sub;
            b.Angle += b.Spin * sub;
            contacted |= ResolveContacts(b);
        }
        b.SinceContact = contacted ? 0f : b.SinceContact + dt;

        // Settle assist: a grounded body crawling along is trying to come to rest — bleed
        // the crawl hard so the slope-rocking instability (slow slide → impulse burst →
        // opposite slide) can't rebuild. Real motion (falls, blast launches) is far above
        // this window and untouched.
        var grounded = b.SinceContact < 0.12f;
        if (grounded && b.Velocity.LengthSquared() < 30f * 30f)
        {
            b.Velocity *= 0.88f;
            b.Spin *= 0.86f;
        }

        // Sleep only while grounded — a body coasting at apogee is slow but not done.
        var slow = b.Velocity.LengthSquared() < SleepSpeed * SleepSpeed && MathF.Abs(b.Spin) < SleepSpin;
        b.SleepTimer = grounded && slow ? b.SleepTimer + dt : 0f;
        if (b.SleepTimer >= SleepAfter) Stamp(b);
    }

    /// <summary>Sample every surface cell against the tile grid, then resolve the deepest
    /// contacts with impulses + a positional push-out. Two Gauss-Seidel passes over the
    /// contact set settle a resting body without a proper LCP solver.</summary>
    private bool ResolveContacts(Body b)
    {
        _contacts.Clear();
        _normals.Clear();
        foreach (var si in b.SurfaceIdx)
        {
            var c = b.Cells[si];
            var wp = b.Position + Rotate(c.Local, b.Angle);
            var (tx, ty) = _planet.WorldToTile(wp);
            var k = _planet.Get(tx, ty);
            if (!Tiles.IsSolid(k) || Tiles.IsPassable(k)) continue;
            if (Tiles.IsFlora(k))
            {
                // Debris crushes plants: the trampled tile sheds a wisp of dust and the
                // body keeps moving — a felled log shouldn't perch on a fern.
                _planet.Set(tx, ty, TileKind.Sky);
                _cells.SpawnDustFraction(tx, ty, k, 0.3f);
                _physics.MarkDirty(tx, ty);
                continue;
            }
            var n = ContactNormal(wp);
            if (n == Vector2.Zero) n = _planet.UpAt(wp);
            _contacts.Add(wp);
            _normals.Add(n);
        }
        if (_contacts.Count == 0) return false;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < _contacts.Count; i++)
            {
                var n = _normals[i];
                var r = _contacts[i] - b.Position;
                // Velocity of this contact point (v + ω × r in 2D).
                var vp = b.Velocity + new Vector2(-b.Spin * r.Y, b.Spin * r.X);
                var vn = Vector2.Dot(vp, n);
                if (vn >= 0f) continue;

                var rn = r.X * n.Y - r.Y * n.X;
                var invEff = b.InvMass + rn * rn * b.InvInertia;
                var e = vn < -BounceThreshold ? Restitution : 0f;
                var j = -(1f + e) * vn / invEff;
                b.Velocity += n * (j * b.InvMass);
                b.Spin += rn * j * b.InvInertia;

                // Coulomb friction along the tangent, clamped by the normal impulse.
                var t = new Vector2(-n.Y, n.X);
                vp = b.Velocity + new Vector2(-b.Spin * r.Y, b.Spin * r.X);
                var vt = Vector2.Dot(vp, t);
                var rt = r.X * t.Y - r.Y * t.X;
                var invEffT = b.InvMass + rt * rt * b.InvInertia;
                var jt = MathHelper.Clamp(-vt / invEffT, -Friction * j, Friction * j);
                b.Velocity += t * (jt * b.InvMass);
                b.Spin += rt * jt * b.InvInertia;

                if (pass == 0 && j * b.InvMass > 55f)
                    Impacts.Add((_contacts[i], j));
            }
        }

        // Contact damping: while touching ground, bleed linear + angular energy hard. The
        // sequential impulses above aren't a real LCP solve, and whatever error they leave
        // each substep would otherwise accumulate as the micro-jitter that keeps a landed
        // body "awake" forever. Airborne flight is untouched.
        b.Velocity *= 0.96f;
        b.Spin *= 0.94f;

        // Positional correction: push out along the average contact normal, gently — just
        // enough that a resting body doesn't sink while its sleep timer runs. Overdoing
        // this (or scaling it by contact count) pumps the body back into the air.
        var push = Vector2.Zero;
        foreach (var n in _normals) push += n;
        if (push.LengthSquared() > 0.001f)
            b.Position += Vector2.Normalize(push) * MathF.Min(0.8f, 0.15f * _contacts.Count);
        return true;
    }

    /// <summary>Estimate the terrain normal at a penetrating point by averaging the open
    /// directions in a one-tile ring — the classic falling-sand gradient probe. Returns
    /// zero deep inside rock (caller substitutes radial up).</summary>
    private Vector2 ContactNormal(Vector2 wp)
    {
        var n = Vector2.Zero;
        const float step = Planet.TileSize;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var off = new Vector2(dx * step, dy * step);
                var (tx, ty) = _planet.WorldToTile(wp + off);
                var k = _planet.Get(tx, ty);
                if (!Tiles.IsSolid(k) || Tiles.IsPassable(k) || Tiles.IsFlora(k))
                    n += off;
            }
        }
        return n.LengthSquared() > 0.001f ? Vector2.Normalize(n) : Vector2.Zero;
    }

    /// <summary>Fire/lava/acid in the cell sim gnaw at a body's surface cells (wood burns,
    /// rock melts into the lava that swallowed it). Removing cells can split the body —
    /// each connected piece keeps flying separately, Noita-style.</summary>
    private void Erode(Body b)
    {
        var removed = false;
        for (var i = b.Cells.Count - 1; i >= 0; i--)
        {
            var c = b.Cells[i];
            if (!c.Surface) continue;
            var wp = b.Position + Rotate(c.Local, b.Angle);
            var (cx, cy) = _cells.WorldToCell(wp);
            var m = _cells.Get(cx, cy);
            var burns = m is Material.Lava or Material.Fire && !Tiles.IsAnchored(c.Kind);
            // Acid follows the tile-world corrosion rule: anchored architecture (even the
            // toppling kinds), obsidian, and acid-adapted flora resist in flight too.
            var corrodes = m is Material.Acid && !Tiles.IsAnchored(c.Kind)
                && c.Kind != TileKind.Obsidian && !Tiles.IsFlora(c.Kind);
            if (!burns && !corrodes) continue;
            b.Cells.RemoveAt(i);
            removed = true;
        }
        if (!removed) return;
        if (b.Cells.Count < 3)
        {
            // Too little left to be a body — the remainder spills as dust where it is.
            foreach (var c in b.Cells)
            {
                var wp = b.Position + Rotate(c.Local, b.Angle);
                var (tx, ty) = _planet.WorldToTile(wp);
                _cells.SpawnDustFraction(tx, ty, c.Kind, 0.5f);
            }
            b.Dead = true;
            return;
        }
        // Interior cells uncovered by the erosion become the new surface.
        RebuildSurface(b);
        SplitIfDisconnected(b);
        b.RecomputeMass();
    }

    /// <summary>Recompute surface flags from local-frame adjacency: a cell is surface when
    /// any of its four lattice neighbours (one tile away in the body frame) is missing. The
    /// polar detach lattice isn't perfectly square, so neighbours match by distance —
    /// candidates come from the spatial hash so whole-structure bodies stay O(n).</summary>
    private void RebuildSurface(Body b)
    {
        const float adjSq = (Planet.TileSize * 1.5f) * (Planet.TileSize * 1.5f);
        BuildCellHash(b);
        for (var i = 0; i < b.Cells.Count; i++)
        {
            var ci = b.Cells[i];
            var neighbours = 0;
            var bx = (int)MathF.Floor(ci.Local.X / HashSpan);
            var by = (int)MathF.Floor(ci.Local.Y / HashSpan);
            for (var dy = -1; dy <= 1 && neighbours < 4; dy++)
                for (var dx = -1; dx <= 1 && neighbours < 4; dx++)
                {
                    if (!_cellHash.TryGetValue(((long)(bx + dx) << 32) ^ (uint)(by + dy), out var list))
                        continue;
                    foreach (var j in list)
                    {
                        if (i == j) continue;
                        if ((b.Cells[j].Local - ci.Local).LengthSquared() <= adjSq
                            && ++neighbours >= 4) break;
                    }
                }
            ci.Surface = neighbours < 4;
            b.Cells[i] = ci;
        }
    }

    /// <summary>Flood the payload over local-frame adjacency (spatial-hash candidates, so a
    /// whole toppled skyscraper floods in O(n)); if more than one component remains, the
    /// largest keeps this body and the rest respawn as their own bodies.</summary>
    private void SplitIfDisconnected(Body b)
    {
        var n = b.Cells.Count;
        var comp = new int[n];
        for (var i = 0; i < n; i++) comp[i] = -1;
        const float adjSq = (Planet.TileSize * 1.5f) * (Planet.TileSize * 1.5f);
        BuildCellHash(b);
        var compCount = 0;
        var stack = new Stack<int>();
        for (var seed = 0; seed < n; seed++)
        {
            if (comp[seed] >= 0) continue;
            stack.Push(seed);
            comp[seed] = compCount;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var bx = (int)MathF.Floor(b.Cells[i].Local.X / HashSpan);
                var by = (int)MathF.Floor(b.Cells[i].Local.Y / HashSpan);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (!_cellHash.TryGetValue(((long)(bx + dx) << 32) ^ (uint)(by + dy), out var list))
                            continue;
                        foreach (var j in list)
                        {
                            if (comp[j] >= 0) continue;
                            if ((b.Cells[j].Local - b.Cells[i].Local).LengthSquared() <= adjSq)
                            {
                                comp[j] = compCount;
                                stack.Push(j);
                            }
                        }
                    }
            }
            compCount++;
        }
        if (compCount <= 1) return;

        // Count members; the biggest component stays, others peel off into new bodies that
        // inherit this body's motion (they were one piece until a moment ago).
        var sizes = new int[compCount];
        for (var i = 0; i < n; i++) sizes[comp[i]]++;
        var keep = 0;
        for (var c = 1; c < compCount; c++) if (sizes[c] > sizes[keep]) keep = c;

        for (var c = 0; c < compCount; c++)
        {
            if (c == keep) continue;
            // Crumbs under 3 cells (or pieces past the body budget, below) spill as dust
            // where they are — split conserves matter the same way stamping does.
            if (sizes[c] < 3 || Bodies.Count >= MaxBodies)
            {
                for (var i = 0; i < n; i++)
                {
                    if (comp[i] != c) continue;
                    var wp = b.Position + Rotate(b.Cells[i].Local, b.Angle);
                    var (tx, ty) = _planet.WorldToTile(wp);
                    _cells.SpawnDustFraction(tx, ty, b.Cells[i].Kind, 0.5f);
                }
                continue;
            }
            var piece = new Body
            {
                Position = b.Position,
                Velocity = b.Velocity,
                Angle = b.Angle,
                Spin = b.Spin,
            };
            for (var i = 0; i < n; i++)
                if (comp[i] == c) piece.Cells.Add(b.Cells[i]);
            piece.RecomputeMass();
            Bodies.Add(piece);
        }
        for (var i = n - 1; i >= 0; i--)
            if (comp[i] != keep) b.Cells.RemoveAt(i);
    }

    /// <summary>Stamp a resting body back into the tile grid, mass-conserved: each cell takes
    /// the tile under it if free, then tries the surrounding ring, and failing that spills as
    /// dust of its kind — nothing is created, nothing vanishes. Every written tile wakes the
    /// settle physics so a bad perch re-condemns naturally.</summary>
    public void Stamp(Body b)
    {
        b.Dead = true;
        foreach (var c in b.Cells)
        {
            var wp = b.Position + Rotate(c.Local, b.Angle);
            var (tx, ty) = _planet.WorldToTile(wp);
            if (TryStampAt(tx, ty, c.Kind)) continue;
            var placed = false;
            for (var dy = -1; dy <= 1 && !placed; dy++)
                for (var dx = -1; dx <= 1 && !placed; dx++)
                    placed = TryStampAt(tx + dx, ty + dy, c.Kind);
            if (!placed) _cells.SpawnDustInTile(tx, ty, c.Kind);
        }
    }

    private bool TryStampAt(int tx, int ty, TileKind k)
    {
        if (!_planet.InBounds(tx, ty)) return false;
        if (_planet.Get(tx, ty) != TileKind.Sky) return false;
        _planet.Set(tx, ty, k);
        _physics.MarkDirty(tx, ty);
        return true;
    }

    /// <summary>Force every live body into the grid — called before a run save so no matter
    /// leaves the world (kinetics aren't persisted, same policy as flying cells). A mid-air
    /// chunk stamps where it is and simply re-condemns after the save.</summary>
    public void StampAll()
    {
        foreach (var b in Bodies)
            if (!b.Dead) Stamp(b);
        Bodies.Clear();
    }

    /// <summary>Overlap test between a body and an actor circle (dwarf, creature). Finds the
    /// nearest surface cell; when it's within reach, returns the push-out normal (body → actor)
    /// and the body's velocity at that contact point, so the caller can shoulder the actor
    /// aside gently or clobber them on a fast hit.</summary>
    public static bool Overlap(Body b, Vector2 pos, float radius, out Vector2 normal, out Vector2 contactVel)
    {
        normal = default;
        contactVel = default;
        var d = pos - b.Position;
        var reach = radius + Planet.TileSize * 0.7f;
        if (d.LengthSquared() > (b.BoundRadius + radius) * (b.BoundRadius + radius)) return false;
        var bestSq = float.MaxValue;
        var bestWp = Vector2.Zero;
        foreach (var si in b.SurfaceIdx)
        {
            var wp = b.Position + Rotate(b.Cells[si].Local, b.Angle);
            var dsq = (pos - wp).LengthSquared();
            if (dsq < bestSq) { bestSq = dsq; bestWp = wp; }
        }
        if (bestSq > reach * reach) return false;
        var n = pos - bestWp;
        normal = n.LengthSquared() > 0.01f ? Vector2.Normalize(n) : new Vector2(0f, -1f);
        var r = bestWp - b.Position;
        contactVel = b.Velocity + new Vector2(-b.Spin * r.Y, b.Spin * r.X);
        return true;
    }

    /// <summary>Total payload cells across live bodies (perf counters / tests).</summary>
    public int CellCount
    {
        get
        {
            var n = 0;
            foreach (var b in Bodies) n += b.Cells.Count;
            return n;
        }
    }
}
