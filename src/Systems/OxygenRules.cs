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
    // Above AirDepth (a few tiles into the ground) surface air refills the supply; below it the
    // drain ramps linearly to MaxDrain at DeepDepth. Tuned so a base 100-air dwarf can reach
    // the mid-crust ore band and return, while the deep gem/diamond bands need the air tank.
    public const float AirDepth = 6f;
    public const float DeepDepth = 120f;
    public const float RefillRate = 45f;
    public const float MaxDrain = 7.5f;
    public const float SuffocationDps = 6f;

    /// <summary>True when <paramref name="depthBelowSurface"/> is shallow enough to breathe —
    /// the air refills rather than drains.</summary>
    public static bool AtSurfaceAir(float depthBelowSurface) => depthBelowSurface <= AirDepth;

    /// <summary>Air units drained per second at this depth on this planet. Zero at/above the
    /// surface-air line, ramping to <see cref="MaxDrain"/> × scale at <see cref="DeepDepth"/>.</summary>
    public static float DrainPerSecond(float depthBelowSurface, float drainScale)
    {
        if (depthBelowSurface <= AirDepth) return 0f;
        var t = MathHelper.Clamp((depthBelowSurface - AirDepth) / (DeepDepth - AirDepth), 0f, 1f);
        return MaxDrain * t * drainScale;
    }
}
