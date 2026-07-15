using System;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>A drifting weather cloud. It fades in, wanders downwind across the sky, and now and
/// then opens up and rains over the band beneath it before thinning back out. The rain kind is
/// the planet's (water / thin acid / ember-rain).</summary>
public sealed class Cloud
{
    public float Angle;        // bearing of the cloud centre
    public float HalfWidth;    // angular half-width of the bank
    public float Alt;          // height above the surface, world units
    public float Drift;        // angular drift per second (downwind)
    public float Life;         // seconds before it dissipates
    public float Grow;         // 0..1 fade-in / fade-out
    public float RainTimer;    // >0 while actively raining
    public float RainCooldown; // gap before it can rain again
    public float Phase;        // random phase for puff bob
}

/// <summary>The sky half of the living ecosystem. Clouds form on every world, drift on the wind,
/// and shed rain that waters the trees below (via <see cref="TreeEcology.WaterBand"/>). What
/// falls depends on the biome: plain water on temperate worlds; a thin acid drizzle on acid
/// worlds that only puffs off a little gas (never enough to eat the soil); an ember-rain on the
/// burning worlds that gutters out a wisp of smoke. Perf-cheap: a handful of clouds, rain
/// emitted as a few throttled particles plus the odd gas/smoke cell.</summary>
public static class Weather
{
    private const int MaxClouds = 4;

    public static void Update(float dt, Session run, Particles particles)
    {
        var planet = run.Planet;
        if (planet is null) return;
        var kind = TreeEcology.RainFor(run.Def);
        var rng = Random.Shared;

        // Form new clouds on a slow clock, biased to drift into view near the player so the
        // weather reads as a moving system, not a fixed backdrop.
        run.CloudTimer -= dt;
        if (run.CloudTimer <= 0f)
        {
            run.CloudTimer = 14f + (float)rng.NextDouble() * 22f;
            if (run.Clouds.Count < MaxClouds)
            {
                var rel = run.Player.Position - planet.Center;
                var near = MathF.Atan2(rel.Y, rel.X);
                var cAng = near + ((float)rng.NextDouble() - 0.5f) * 1.6f;
                var cHalf = 0.10f + (float)rng.NextDouble() * 0.14f;
                run.Clouds.Add(new Cloud
                {
                    Angle = cAng,
                    HalfWidth = cHalf,
                    // Sit well clear of the tallest thing beneath the band (peaks, giant trees)
                    // plus a random extra height, so a cloud never spawns inside terrain.
                    Alt = TargetAlt(planet, cAng, cHalf) + (float)rng.NextDouble() * 120f,
                    Drift = ((float)rng.NextDouble() - 0.5f) * 0.05f,
                    Life = 45f + (float)rng.NextDouble() * 60f,
                    RainCooldown = 4f + (float)rng.NextDouble() * 10f,
                    Phase = (float)rng.NextDouble() * MathHelper.TwoPi,
                });
            }
        }

        for (var i = run.Clouds.Count - 1; i >= 0; i--)
        {
            var c = run.Clouds[i];
            c.Life -= dt;
            c.Angle += c.Drift * dt;
            // Ride at a clearance above whatever's tallest beneath the band right now — as the
            // cloud drifts over a mountain or a stand of giant trees it rises to clear them, and
            // settles back down over open ground. Eased so it glides rather than snapping.
            c.Alt = MathHelper.Lerp(c.Alt, TargetAlt(planet, c.Angle, c.HalfWidth), dt * 0.7f);
            // Fade in over the life; fade back out in the last few seconds.
            var target = c.Life > 6f ? 1f : MathHelper.Clamp(c.Life / 6f, 0f, 1f);
            c.Grow = MathHelper.Lerp(c.Grow, target, dt * 0.5f);
            if (c.Life <= 0f && c.Grow < 0.03f) { run.Clouds.RemoveAt(i); continue; }

            // Only a well-formed cloud rains. Between showers it rests on a cooldown.
            if (c.RainTimer > 0f)
            {
                c.RainTimer -= dt;
                Rain(dt, run, c, kind, particles);
                if (c.RainTimer <= 0f) c.RainCooldown = 10f + (float)rng.NextDouble() * 22f;
            }
            else if (c.Grow > 0.7f && c.Life > 10f)
            {
                c.RainCooldown -= dt;
                if (c.RainCooldown <= 0f) c.RainTimer = 6f + (float)rng.NextDouble() * 10f;
            }
        }
    }

    /// <summary>The altitude (offset above the baseline ground at <paramref name="angle"/>) a
    /// cloud should ride at to sit SEVERAL tiles clear of the tallest object beneath its whole
    /// band — peaks and giant trees included. Samples a few bearings across the band, finds the
    /// topmost non-sky tile at each, and clears the highest by a comfortable margin.</summary>
    private static float TargetAlt(Planet planet, float angle, float halfWidth)
    {
        var baseR = (SpawnDirector.FindSurfaceSpawn(planet, angle, planet.Radius) - planet.Center).Length();
        // Water is cells, not tiles, so over a lake/ocean the topmost-solid scan below finds
        // the LAKE BED — and a cloud 130px above a deep seabed sits underwater. Never let the
        // "tallest thing" fall below the baseline surface ring: basins fill to about that
        // level, so clamping to it keeps every cloud above the sea.
        var maxTop = MathF.Max(baseR, (Planet.RingMin + planet.SurfaceRing) * Planet.TileSize);
        // Scan from well above the tallest possible feature down to the first solid/flora tile.
        var hi = Math.Min(planet.Rings - 2, planet.SurfaceRing + 60);
        var lo = Math.Max(2, planet.SurfaceRing - 20);
        for (var i = -2; i <= 2; i++)
        {
            var a = angle + i / 2f * halfWidth;
            for (var r = hi; r > lo; r--)
            {
                var n = planet.TilesAt(r);
                var t = (int)((a / MathHelper.TwoPi + 1f) % 1f * n);
                if (planet.Get(r, t) != TileKind.Sky)
                {
                    var topR = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
                    if (topR > maxTop) maxTop = topR;
                    break;
                }
            }
        }
        return maxTop - baseR + 130f;   // several tiles of clear air above the tallest thing
    }

    /// <summary>One rain tick under a cloud: water the trees in its band, throw a few falling
    /// raindrop particles, and — on the harsh worlds — puff off the odd gas (acid) or smoke
    /// (fire) cell. Deliberately light on real cells so acid rain never dissolves the ground.</summary>
    private static void Rain(float dt, Session run, Cloud c, RainKind kind, Particles particles)
    {
        var planet = run.Planet;
        // Feed the roots under the whole band. Rate is per-second; the band-water helper clamps.
        TreeEcology.WaterBand(run, c.Angle, c.HalfWidth, kind, 0.22f * dt);

        var rng = Random.Shared;

        // The shower actually lands: a throttled trickle of REAL water cells dropped just
        // above the ground under the band, so rain pools in hollows and runs off slopes
        // instead of passing through the terrain. Rain water is tagged in the cell sim and
        // evaporates once it rests exposed near the surface (see Cells.SpawnRainWater), so a
        // shower leaves drying puddles, never a slow world-flood. Water rain only — acid and
        // ember rain stay atmospheric per the brief, and snow just dusts visually.
        if (kind == RainKind.Water && rng.Next(3) == 0)
        {
            var wAng = c.Angle + ((float)rng.NextDouble() - 0.5f) * 2f * c.HalfWidth;
            var wGround = SpawnDirector.FindSurfaceSpawn(planet, wAng, planet.Radius);
            var wUp = planet.UpAt(wGround);
            run.Cells.SpawnRainWater(wGround + wUp * (4f + (float)rng.NextDouble() * 10f));
        }

        var color = kind switch
        {
            RainKind.Acid => new Color(150, 200, 90),
            RainKind.Fire => new Color(255, 150, 70),
            RainKind.Snow => new Color(235, 244, 255),
            _             => new Color(120, 170, 235),
        };
        // A few flakes/drops per tick, scattered across the band, falling from the cloud. Snow
        // drifts down slow and fluffy; everything else streaks.
        for (var i = 0; i < 5; i++)
        {
            var ang = c.Angle + ((float)rng.NextDouble() - 0.5f) * 2f * c.HalfWidth;
            var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
            var up = planet.UpAt(ground);
            var start = ground + up * (c.Alt * (0.5f + (float)rng.NextDouble() * 0.6f));
            if (kind == RainKind.Snow) particles.EmitSnow(start, -up, color);
            else particles.EmitRain(start, -up, color);
        }

        // Harsh-world exhaust: a thin acid drizzle fizzes off a little gas; ember-rain gutters
        // to a wisp of smoke. Both are rare and land high, so nothing corrodes or ignites the
        // ground — it's atmosphere, per the brief. Water rain and snow stay clean.
        if ((kind == RainKind.Acid || kind == RainKind.Fire) && rng.Next(4) == 0)
        {
            var ang = c.Angle + ((float)rng.NextDouble() - 0.5f) * 2f * c.HalfWidth;
            var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
            var up = planet.UpAt(ground);
            var puff = ground + up * (30f + (float)rng.NextDouble() * 60f);
            var (tx, ty) = planet.WorldToTile(puff);
            if (tx >= 0 && tx < planet.Rings)
                run.Cells.SpawnInTile(tx, ty, kind == RainKind.Acid ? Material.Gas : Material.Smoke, 1);
        }
    }
}
