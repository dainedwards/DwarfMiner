using System;
using DwarfMiner.Entities;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Temporary diagnostic (`dotnet run -- --smokeprobe`): verifies the effects-as-materials
/// path (DM_CELLFX). A shell detonated against the surface must stamp real Smoke cells
/// that rise out of the crater as a plume and gutter out on their own; an airburst far
/// from terrain must leave a small epicentre puff; and cinders emitted over open ground
/// must hand off real Fire cells where they land. Prints cell counts over sim time.
/// </summary>
public static class SmokeProbe
{
    public static void Run()
    {
        var def = PlanetDefs.ById("verdant");
        var planet = WorldGen.Generate(42, def);
        var cells = new Cells(planet);
        var physics = new Physics(planet, cells);
        const float dt = 1f / 60f;

        var surface = SpawnDirector.FindSurfaceSpawn(planet, -MathF.PI / 2f, planet.Radius);
        var up = planet.UpAt(surface);

        // --- 1. Ground burst: cannon shell into the dirt. ---
        var shell = new Projectile(surface - up * 4f, Vector2.Zero, 0f, 1f, ProjectileKind.Cannon);
        shell.Explode(planet, physics, cells, particles: null);
        var smoke0 = cells.CountNear(surface, 80f, Material.Smoke);
        Console.WriteLine($"[smokeprobe] ground burst: {smoke0} smoke cells stamped");
        // Track the plume: it should RISE (mean height above the blast grows) then decay.
        var peakAlt = 0f;
        var t180 = 0;
        for (var step = 1; step <= 60 * 8; step++)
        {
            cells.Update(dt);
            if (step % 60 != 0) continue;
            var count = cells.CountNear(surface, 200f, Material.Smoke);
            var alt = MeanSmokeAltitude(cells, planet, surface, up);
            peakAlt = MathF.Max(peakAlt, alt);
            if (step == 180) t180 = count;
            Console.WriteLine($"[smokeprobe]   t={step / 60}s smoke={count} meanAlt={alt:F1}px");
        }
        var smokeEnd = cells.CountNear(surface, 200f, Material.Smoke);
        Check(smoke0 >= 20, $"ground burst stamps a real plume (got {smoke0}, want >=20)");
        Check(peakAlt > 6f, $"plume rises (peak mean altitude {peakAlt:F1}px, want >6)");
        Check(smokeEnd < Math.Max(1, t180), $"plume decays ({t180} at 3s -> {smokeEnd} at 8s)");

        // --- 2. Airburst: same shell detonated high above the surface. Counted as a DELTA
        // over what already drifted there — the ground burst's plume has had 8 sim-seconds
        // to rise into this patch of sky, and counting it flaked the bound. ---
        var airPos = surface + up * 120f;
        var airBase = cells.CountNear(airPos, 30f, Material.Smoke);
        var air = new Projectile(airPos, Vector2.Zero, 0f, 1f, ProjectileKind.Cannon);
        air.Explode(planet, physics, cells, particles: null);
        var airSmoke = cells.CountNear(airPos, 30f, Material.Smoke) - airBase;
        Console.WriteLine($"[smokeprobe] airburst: +{airSmoke} smoke cells at epicentre (bg {airBase})");
        Check(airSmoke is > 0 and <= 10, $"airburst leaves a small puff (got +{airSmoke}, want 1..10)");

        // --- 3. Cinder handoff: coals scattered over open ground stamp Fire cells. ---
        var particles = new Particles();
        for (var burst = 0; burst < 10; burst++)
            particles.EmitCinders(surface + up * 30f, Vector2.Zero, 10, scatter: 50f);
        var firePeak = 0;
        for (var step = 1; step <= 60 * 4; step++)
        {
            particles.Update(dt, planet, cells);
            cells.Update(dt);
            if (step % 30 == 0)
                firePeak = Math.Max(firePeak, cells.CountNear(surface, 150f, Material.Fire));
        }
        Console.WriteLine($"[smokeprobe] cinder handoff: peak {firePeak} fire cells on the ground");
        Check(firePeak >= 3, $"landed cinders stamp real fire (peak {firePeak}, want >=3)");

        Console.WriteLine("[smokeprobe] PASS");
    }

    /// <summary>Mean radial height (px) of all smoke within 200px of the blast, measured
    /// above the blast point — crude but enough to prove the plume rises.</summary>
    private static float MeanSmokeAltitude(Cells cells, Planet planet, Vector2 origin, Vector2 up)
    {
        // CountNear can't report positions, so sample expanding shells: weight each ring's
        // population by its offset along up.
        float sum = 0; var n = 0;
        for (var h = -20; h <= 120; h += 8)
        {
            var c = cells.CountNear(origin + up * h, 4.5f, Material.Smoke);
            sum += c * h;
            n += c;
        }
        return n == 0 ? 0f : sum / n;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"[smokeprobe] {(ok ? "ok" : "FAIL")}: {what}");
        if (!ok) Environment.Exit(1);
    }
}
