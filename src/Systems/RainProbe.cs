using System;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `--rainprobe`): sweeps every angle a cloud could shower over and
/// drives the real rain-landing path (FindSurfaceSpawn → Cells.SpawnRainWater) at each one.
///
/// This exists because the landing path used to crash the whole game: FindSurfaceSpawn falls
/// back to `radius + 24` tiles when a column holds no ground, which is ABOVE the cell grid's
/// outermost row, and SettleToSurface walked that row off the end of `_cellsAt`. A cloud only
/// had to drift over one groundless column to abort the process, so it read as a random crash
/// minutes into a session. Sweeping all angles makes it deterministic: the probe reports the
/// groundless columns (the drops that take the fallback) and must never throw.
/// </summary>
public static class RainProbe
{
    public static void Run()
    {
        foreach (var id in new[] { "debug", "verdant", "ocean" })
        {
            var def = PlanetDefs.ById(id);
            if (def is null) { Console.WriteLine($"--- {id}: no such planet, skipped"); continue; }
            var run = DwarfMinerGame.BuildSessionWorld(def);
            var planet = run.Planet;
            var cells = run.Cells;
            var topRow = cells.Height - 1;

            Console.WriteLine($"--- {id}: rings {planet.Rings}, radius {planet.Radius}t, " +
                              $"cell rows {cells.Height}");

            var steps = planet.Rings * 4;   // finer than a cloud is wide: no column is skipped
            var groundless = 0; var aboveGrid = 0; var placed = 0; var maxCy = int.MinValue;
            for (var i = 0; i < steps; i++)
            {
                var ang = MathHelper.TwoPi * i / steps;
                var ground = SpawnDirector.FindSurfaceSpawn(planet, ang, planet.Radius);

                // What Weather.Rain hands the cell sim, and where that lands in the grid.
                var (_, cy) = cells.WorldToCell(ground);
                maxCy = Math.Max(maxCy, cy);
                var dist = (ground - planet.Center).Length() / Planet.TileSize;
                if (dist >= planet.Radius) groundless++;
                if (cy > topRow) aboveGrid++;

                // The crash path itself: must survive every column.
                var before = cells.CountMaterial(Material.Water);
                cells.SpawnRainWater(ground);
                if (cells.CountMaterial(Material.Water) > before) placed++;
            }

            Console.WriteLine($"    swept {steps} angles: {groundless} groundless columns " +
                              $"(FindSurfaceSpawn fallback), {aboveGrid} of them above the top " +
                              $"cell row {topRow} (these were the crash), highest cy {maxCy}");
            Console.WriteLine($"    rain cells placed: {placed}/{steps}");
            Console.WriteLine(aboveGrid == 0
                ? "    OK: every drop landed inside the grid"
                : "    OK: no throw — out-of-grid drops were clamped to the top row and settled");
        }
    }
}
