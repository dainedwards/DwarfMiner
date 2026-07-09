using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// The Geo Scanner's tile search: nearest tile of a kind within a radius of a world point.
/// Walks only the rings and angular spans the radius can reach (the same culling shape the
/// renderer uses), so a 600 px sweep touches ~20k tiles — cheap enough to run on a timer.
/// Pure and static for the headless tests.
/// </summary>
public static class Scanner
{
    /// <summary>The tile kind a resource id maps to — what the scanner hunts for a planet's
    /// signature (nav-core) ore.</summary>
    public static TileKind OreTileFor(string resourceId) => resourceId switch
    {
        "gold"     => TileKind.GoldOre,
        "silver"   => TileKind.SilverOre,
        "platinum" => TileKind.PlatinumOre,
        "ruby"     => TileKind.Ruby,
        "sapphire" => TileKind.Sapphire,
        "diamond"  => TileKind.Diamond,
        "crystal"  => TileKind.Crystal,
        "iron"     => TileKind.IronOre,
        "coal"     => TileKind.CoalOre,
        _          => TileKind.FuelOre,
    };

    public static Vector2? FindNearest(Planet planet, Vector2 from, TileKind kind, float maxRadius)
    {
        var (pr, _) = planet.WorldToTile(from);
        var fromAngle = MathF.Atan2(from.Y - planet.Center.Y, from.X - planet.Center.X);
        var ringSpan = (int)(maxRadius / Planet.TileSize) + 1;
        Vector2? best = null;
        var bestD2 = maxRadius * maxRadius;

        for (var dr = -ringSpan; dr <= ringSpan; dr++)
        {
            var r = pr + dr;
            if (r < 0 || r >= Planet.RingCount) continue;
            var n = Planet.TilesAt(r);
            var ringRadius = (Planet.RingMin + r + 0.5f) * Planet.TileSize;
            // Angular reach of the radius on this ring, in tile steps.
            var span = Math.Min(n / 2, (int)(maxRadius / (MathHelper.TwoPi * ringRadius) * n) + 1);
            var t0 = (int)((fromAngle / MathHelper.TwoPi + 1f) % 1f * n);
            for (var dt = -span; dt <= span; dt++)
            {
                var t = ((t0 + dt) % n + n) % n;
                if (planet.Get(r, t) != kind) continue;
                var pos = planet.TileToWorld(r, t);
                var d2 = (pos - from).LengthSquared();
                if (d2 >= bestD2) continue;
                bestD2 = d2;
                best = pos;
            }
        }
        return best;
    }
}
