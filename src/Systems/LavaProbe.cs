using System;
using DwarfMiner.World;

namespace DwarfMiner.Systems;

/// <summary>
/// Diagnostic (invoked via `--lavaprobe`): builds a full session world (the same
/// BuildSessionWorld path the game uses, shell pass included) and audits every lava body's
/// jacket — for each tile that holds lava, every solid tile within the 2-tile shell reach
/// must be LavaRock or one of the deliberately-spared kinds (obsidian seals, ores/gems,
/// architecture, flora, anchors). Any "naked" convertible tile is a shell hole; the probe
/// prints where it is and which direction from the lava (below/beside/above), which is
/// exactly the report needed to chase "the bottom of the tubes isn't jacketed".
/// </summary>
public static class LavaProbe
{
    public static void Run()
    {
        foreach (var id in new[] { "debug", "ember" })
        {
            var def = PlanetDefs.ById(id);
            var run = DwarfMinerGame.BuildSessionWorld(def);
            var planet = run.Planet;
            var cells = run.Cells;

            var lavaTiles = 0;
            var rockTiles = 0;
            var naked = 0;
            var nakedBelow = 0;
            var samples = new System.Collections.Generic.List<string>();

            bool IsLavaTile(int r, int t)
            {
                if (r < 0 || r >= planet.Rings) return false;
                var n = planet.TilesAt(r);
                t = (t % n + n) % n;
                if (planet.Get(r, t) != TileKind.Sky) return false;
                return cells.Get(t * Cells.Density + Cells.Density / 2,
                    r * Cells.Density + Cells.Density / 2) == Material.Lava;
            }

            for (var r = 0; r < planet.Rings; r++)
            {
                var n = planet.TilesAt(r);
                for (var t = 0; t < n; t++)
                {
                    if (planet.Get(r, t) == TileKind.LavaRock) rockTiles++;
                    if (!IsLavaTile(r, t)) continue;
                    lavaTiles++;
                    for (var dr = -2; dr <= 2; dr++)
                    {
                        var r2 = r + dr;
                        if (r2 < 0 || r2 >= planet.Rings) continue;
                        var n2 = planet.TilesAt(r2);
                        var t2c = (int)((t + 0.5f) / n * n2);
                        for (var dt = -2; dt <= 2; dt++)
                        {
                            var t2 = ((t2c + dt) % n2 + n2) % n2;
                            var k = planet.Get(r2, t2);
                            // The kinds the shell pass converts — if one still borders
                            // lava after the pass, the jacket has a hole there.
                            if (k is TileKind.Dirt or TileKind.Grass or TileKind.Stone
                                or TileKind.Gravel or TileKind.MossStone or TileKind.Granite
                                or TileKind.Basalt or TileKind.Snow or TileKind.Conglomerate)
                            {
                                naked++;
                                if (dr < 0) nakedBelow++;
                                if (samples.Count < 12)
                                    samples.Add($"lava({r},{t}) -> {k}({r2},{t2}) dr={dr} dt={dt}");
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"--- {id}: lavaTiles {lavaTiles}, lavaRockTiles {rockTiles}, " +
                              $"naked convertibles in shell reach {naked} (below: {nakedBelow})");
            foreach (var s in samples) Console.WriteLine("    " + s);
            Console.WriteLine(naked == 0 ? "    OK: every lava body fully jacketed"
                                         : "    FAIL: shell has holes");
        }
    }
}
