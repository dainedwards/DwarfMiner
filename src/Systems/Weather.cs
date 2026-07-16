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
    public float Alt;          // cruising RADIUS from the planet centre, world px — fixed for life
    public float Drift;        // angular drift per second — the planet's prevailing wind / Alt
    public float Life;         // seconds before it dissipates
    public float Grow;         // 0..1 fade-in / fade-out
    public float RainTimer;    // >0 while actively raining
    public float RainElapsed;  // seconds since this shower opened — gates the landing half
    public float RainCooldown; // gap before it can rain again
    public float Phase;        // random phase for per-puff outline wobble
    public float Shape;        // 0..1 silhouette hash — skews the bank so no two look alike
    public bool Dissipating;   // shredding against a peak/skyscraper — fading out for good
    public RainKind? KindOverride; // debug-spawned clouds rain THIS regardless of biome
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

    /// <summary>DM_RAIN=1 forces the weather on: clouds form fast and shower continuously —
    /// verification hook for rain pooling/splash work, useless in real play.</summary>
    private static readonly bool ForceRain =
        Environment.GetEnvironmentVariable("DM_RAIN") is { Length: > 0 };

    /// <summary>The planet's prevailing wind, signed world px/s. Every cloud on a world drifts
    /// the SAME way (±20% per-cloud speed spread at spawn) so the sky reads as one moving
    /// weather system instead of banks crossing each other at random. Derived from the def id
    /// so it's stable for the world's whole life with nothing to save; expressed as LINEAR
    /// speed and divided by each cloud's Alt at spawn, so clouds pace the same on a small moon
    /// and a giant world (a fixed angular rate would sprint at big-world altitudes).</summary>
    public static float WindFor(PlanetDef def)
    {
        var h = 17;
        foreach (var ch in def.Id) h = h * 31 + ch;
        var u = (uint)h;
        var speed = 6f + u % 997 / 997f * 12f; // 6..18 px/s
        return (u & 0x400) == 0 ? speed : -speed;
    }

    public static void Update(float dt, Session run, Particles particles)
    {
        var planet = run.Planet;
        if (planet is null) return;

        // No atmosphere, no weather: airless rock (the Hollow, the cratered moon) has no
        // air to hold a cloud up — the sky stays space-black and empty. Clear instead of
        // letting stragglers fade so nothing ever drifts over the regolith. (The rest of
        // this tick is moot too: airless worlds grow no trees and are never the frost
        // biome, so leaves and lying snow have nothing to do.)
        if (run.Def.Airless)
        {
            run.Clouds.Clear();
            return;
        }

        var kind = TreeEcology.RainFor(run.Def);
        var rng = Random.Shared;

        // Snow lies for good only on the ice world; everywhere else a landed flake slowly
        // sublimates (see the snow clause in Cells.TickSand). Refreshed here so the flag
        // always tracks the world the player is actually on.
        run.Cells.SnowPersists = run.Def.Biome == "frost";

        // Falling leaves: a standing tree near the player sheds the odd leaf in its own
        // canopy colour — the cheapest "the forest is alive" signal there is. Roll a few
        // times a second, pick a random tree, and only bother if it's within earshot.
        if (run.Trees.Count > 0 && rng.NextDouble() < dt * 2.5f)
        {
            var tree = run.Trees[rng.Next(run.Trees.Count)];
            var rel2 = run.Player.Position - planet.Center;
            var pAng = MathF.Atan2(rel2.Y, rel2.X);
            if (tree.Standing && MathF.Abs(MathHelper.WrapAngle(tree.Angle - pAng)) < 0.3f)
            {
                var ring = Math.Min(planet.Rings - 2, tree.GroundR + tree.Height + rng.Next(3) - 1);
                var rad = (Planet.RingMin + ring + 0.5f) * Planet.TileSize;
                var la = tree.Angle + (rng.Next(5) - 2) * (Planet.TileSize / rad);
                var lpos = planet.Center + new Vector2(MathF.Cos(la), MathF.Sin(la)) * rad;
                var (leaf, leafDk, _) = Renderer.TreeLeafFor(run.Def.Biome,
                    tree.Canopy == TileKind.TreeCanopy2);
                particles.EmitLeaf(lpos, -planet.UpAt(lpos), rng.Next(2) == 0 ? leaf : leafDk);
            }
        }

        // Form new clouds on a slow clock, biased UPWIND of the player so the prevailing
        // wind carries them into view — the weather reads as a moving system, not a fixed
        // backdrop that pops in overhead.
        run.CloudTimer -= dt;
        if (ForceRain && run.CloudTimer > 1.5f) run.CloudTimer = 1.5f;
        if (run.CloudTimer <= 0f)
        {
            run.CloudTimer = 14f + (float)rng.NextDouble() * 22f;
            if (run.Clouds.Count < MaxClouds)
            {
                var wind = WindFor(run.Def);
                var rel = run.Player.Position - planet.Center;
                var near = MathF.Atan2(rel.Y, rel.X);
                var cAng = near - MathF.Sign(wind) * 0.45f
                         + ((float)rng.NextDouble() - 0.5f) * 1.6f;
                var cHalf = 0.10f + (float)rng.NextDouble() * 0.14f;
                // Fixed cruising radius, set ONCE at spawn: well clear of the tallest thing
                // beneath the band (peaks, giant trees) plus a random extra height. The
                // cloud holds this altitude for its whole life — no terrain-tracking, no
                // bobbing — and if the drift carries it into something taller it shreds
                // and dissipates instead of hopping over it.
                var alt = BandTopRadius(planet, cAng, cHalf) + 110f + (float)rng.NextDouble() * 120f;
                run.Clouds.Add(new Cloud
                {
                    Angle = cAng,
                    HalfWidth = cHalf,
                    Alt = alt,
                    Drift = wind * (0.8f + (float)rng.NextDouble() * 0.4f) / alt,
                    Life = 45f + (float)rng.NextDouble() * 60f,
                    RainCooldown = 4f + (float)rng.NextDouble() * 10f,
                    Phase = (float)rng.NextDouble() * MathHelper.TwoPi,
                    Shape = (float)rng.NextDouble(),
                });
            }
        }

        for (var i = run.Clouds.Count - 1; i >= 0; i--)
        {
            var c = run.Clouds[i];
            c.Life -= dt;
            c.Angle += c.Drift * dt;
            // Constant altitude — the bank never bobs or terrain-hops. Instead, when the
            // drift has carried it into something that reaches up INTO its ride band (a
            // mountain flank, a skyscraper), it starts shredding against the obstacle and
            // slowly dissipates: the fade rides the existing last-seconds Grow ramp, so it
            // thins out over several seconds rather than popping.
            if (!c.Dissipating && BandTopRadius(planet, c.Angle, c.HalfWidth) + 24f > c.Alt)
            {
                c.Dissipating = true;
                c.RainTimer = 0f;
                c.Life = MathF.Min(c.Life, 5.5f);
            }
            // Fade in over the life; fade back out in the last few seconds.
            var target = c.Life > 6f ? 1f : MathHelper.Clamp(c.Life / 6f, 0f, 1f);
            c.Grow = MathHelper.Lerp(c.Grow, target, dt * 0.5f);
            if (c.Life <= 0f && c.Grow < 0.03f) { run.Clouds.RemoveAt(i); continue; }

            // Only a well-formed cloud rains. Between showers it rests on a cooldown.
            if (c.RainTimer > 0f)
            {
                c.RainTimer -= dt;
                Rain(dt, run, c, c.KindOverride ?? kind, particles);
                if (c.RainTimer <= 0f) c.RainCooldown = 10f + (float)rng.NextDouble() * 22f;
            }
            else if (c.Grow > 0.7f && c.Life > 10f && !c.Dissipating)
            {
                c.RainCooldown -= dt;
                if (ForceRain) c.RainCooldown = 0f;
                if (c.RainCooldown <= 0f)
                {
                    c.RainTimer = 6f + (float)rng.NextDouble() * 10f;
                    c.RainElapsed = 0f;
                }
            }
        }
    }

    /// <summary>Debug-menu hook: conjure a fully-formed cloud right over the player that
    /// opens up IMMEDIATELY, raining the given kind regardless of biome (acid cloud on a
    /// verdant world, plain rain on an acid world — per-cloud KindOverride). Skips the
    /// natural form/grow/cooldown ramp entirely; airless worlds still clear it next tick
    /// (no atmosphere, no cloud).</summary>
    public static void SpawnDebugCloud(Session run, RainKind kind)
    {
        var planet = run.Planet;
        if (planet is null) return;
        var rel = run.Player.Position - planet.Center;
        var cAng = MathF.Atan2(rel.Y, rel.X);
        const float cHalf = 0.14f;
        var alt = BandTopRadius(planet, cAng, cHalf) + 110f + (float)Random.Shared.NextDouble() * 60f;
        run.Clouds.Add(new Cloud
        {
            Angle = cAng,
            HalfWidth = cHalf,
            Alt = alt,
            Drift = WindFor(run.Def) * 0.9f / alt,
            Life = 50f,
            Grow = 1f,
            RainTimer = 16f,
            Phase = (float)Random.Shared.NextDouble() * MathHelper.TwoPi,
            Shape = (float)Random.Shared.NextDouble(),
            KindOverride = kind,
        });
    }

    /// <summary>Radius (world px from the planet centre) of the tallest object beneath the
    /// whole band — peaks, skyscrapers and giant trees included. Samples a few bearings
    /// across the band and takes the topmost non-sky tile. Used once at spawn to pick a
    /// clear cruising altitude, and per-frame to notice when the drift has run the bank
    /// into something tall enough to shred it.</summary>
    private static float BandTopRadius(Planet planet, float angle, float halfWidth)
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
        return maxTop;
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

        // The shower actually lands: REAL cells dropped just above the ground under the
        // band. Water rain pools in hollows and runs off slopes instead of passing through
        // the terrain (tagged in the cell sim so puddles evaporate and deep/lake-touching
        // pools convert — see Cells.SpawnRainWater); snowfall banks into drifts that lie
        // on the frost world and sublimate away everywhere else. Acid and ember rain stay
        // atmospheric per the brief. Water lands 3 cells/tick (~180/s per cloud — the old
        // 1-in-3 trickle was ~7s per TILE of water at Density 8, so a whole shower left
        // nothing you could see); snow keeps the trickle, its drifts have no evaporation
        // counterweight beyond the saturation cap.
        if (kind is RainKind.Water or RainKind.Snow)
        {
            var drops = kind == RainKind.Water ? 3 : rng.Next(3) == 0 ? 1 : 0;
            for (var d = 0; d < drops; d++)
            {
                var wAng = c.Angle + ((float)rng.NextDouble() - 0.5f) * 2f * c.HalfWidth;
                var wGround = SpawnDirector.FindSurfaceSpawn(planet, wAng, planet.Radius);
                var wUp = planet.UpAt(wGround);
                var drop = wGround + wUp * (4f + (float)rng.NextDouble() * 10f);
                if (kind == RainKind.Water) run.Cells.SpawnRainWater(drop);
                else run.Cells.SpawnSnow(drop);
            }
        }

        var color = kind switch
        {
            RainKind.Acid => new Color(150, 200, 90),
            RainKind.Fire => new Color(255, 150, 70),
            RainKind.Snow => new Color(235, 244, 255),
            _             => new Color(120, 170, 235),
        };
        // Plenty of small flakes/drops per tick, scattered across the band, falling from the
        // cloud. Snow drifts down slow and fluffy; everything else streaks. (Drops shrank to
        // ~1px in the Noita-grain pass, so the count carries the "raining hard" read.)
        for (var i = 0; i < 9; i++)
        {
            var ang = c.Angle + ((float)rng.NextDouble() - 0.5f) * 2f * c.HalfWidth;
            var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);
            var up = planet.UpAt(ground);
            // Alt is the cloud's radius from the planet centre — convert to height above
            // this bearing's ground. Drops spawn INSIDE the bank's lower half, just above
            // its bottom edge (the base puffs centre on Alt with ~6-19px radii), and
            // rain/snow render in the backdrop pass under the clouds — so the origin is
            // hidden behind the puffs and the shower streams out of the underside instead
            // of materialising on top of the bank.
            var altAbove = MathF.Max(30f, c.Alt - (ground - planet.Center).Length());
            var start = ground + up * (altAbove - 2f - (float)rng.NextDouble() * 5f);
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
