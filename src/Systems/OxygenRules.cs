using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Pure air-supply rules, factored out of Game1 so they're unit-testable headlessly. The
/// dwarf breathes freely near the surface and burns air faster the deeper they dig; a planet's
/// <see cref="World.PlanetDef.OxygenDrainScale"/> multiplies the drain so thin-atmosphere
/// worlds punish deep dives harder.
/// </summary>
public static class OxygenRules
{
    // Above AirDepth (a few rings into the ground) surface air refills the supply; below it the
    // drain ramps linearly to MaxDrain at DeepDepth. Tuned so a base 100-air dwarf can reach
    // the mid-crust ore band and return, while the deep gem/diamond bands need the air tank.
    public const float AirDepth = 12f;
    public const float DeepDepth = 240f;
    public const float RefillRate = 45f;
    public const float MaxDrain = 7.5f;
    public const float SuffocationDps = 6f;

    /// <summary>On airless worlds the suit's starlight recycler is the only top-up: it works
    /// at this fraction of the normal surface refill, and only under the open sky.</summary>
    public const float AirlessRefillFrac = 0.25f;

    /// <summary>True when <paramref name="depthBelowSurface"/> is shallow enough to breathe —
    /// the air refills rather than drains. On an airless world there is nothing to breathe;
    /// only the open-sky band still refills (slowly — the suit recycler, not the air).</summary>
    public static bool AtSurfaceAir(float depthBelowSurface, bool airless = false)
        => depthBelowSurface <= (airless ? AirDepth * 0.5f : AirDepth);

    /// <summary>Air units drained per second at this depth on this planet. Zero at/above the
    /// surface-air line, ramping to <see cref="MaxDrain"/> × scale at <see cref="DeepDepth"/>.
    /// Airless worlds get no grace band at all — the tank starts paying from the first
    /// tile underground, because there is no atmosphere seeping down the shaft.</summary>
    public static float DrainPerSecond(float depthBelowSurface, float drainScale, bool airless = false)
    {
        var grace = airless ? 0f : AirDepth;
        if (depthBelowSurface <= grace) return 0f;
        var t = MathHelper.Clamp((depthBelowSurface - grace) / (DeepDepth - grace), 0f, 1f);
        return MaxDrain * t * drainScale;
    }
}
