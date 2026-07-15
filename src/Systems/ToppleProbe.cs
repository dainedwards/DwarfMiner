using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>Diagnostic for the street-level tower topple (`--toppleprobe`): reproduces the
/// SimTest cut on the same seed and reports what keeps the severed section standing when it
/// refuses to detach — remaining tiles in the cut band, region size, and body results.</summary>
public static class ToppleProbe
{
    public static void Run()
    {
        WorldGen.ScatterVegetation = false;
        var city = WorldGen.Generate(11, PlanetDefs.ById("city"));
        var cCells = new Cells(city);
        var cPhysics = new Physics(city, cCells);
        var cRigid = new RigidBodies(city, cCells, cPhysics);
        cPhysics.DetachToRigid = cRigid.TryDetach;
        Console.WriteLine($"rigid enabled: {RigidBodies.Enabled}");

        var (rd, td) = city.CitySpawns[0];
        Console.WriteLine($"doorway spawn at ring {rd} tile {td} (surface {city.SurfaceRing})");
        var n0 = city.TilesAt(rd);
        for (var r = city.SurfaceRing + 1; r <= city.SurfaceRing + 4; r++)
        {
            var n = city.TilesAt(r);
            var tC = (int)MathF.Round((td + 0.5f) / n0 * n - 0.5f);
            var cutBand = new List<string>();
            for (var dtc = -24; dtc <= 24; dtc++)
            {
                var t = ((tC + dtc) % n + n) % n;
                var k = city.Get(r, t);
                if (k == TileKind.Sky) continue;
                cutBand.Add($"{dtc}:{k}");
                city.Set(r, t, TileKind.Sky);
                cPhysics.MarkDirty(r, t);
            }
            Console.WriteLine($"ring {r}: cut {cutBand.Count} tiles");
        }
        // What still stands just past the cut band's ends and just above it?
        for (var r = city.SurfaceRing + 1; r <= city.SurfaceRing + 6; r++)
        {
            var n = city.TilesAt(r);
            var tC = (int)MathF.Round((td + 0.5f) / n0 * n - 0.5f);
            var solids = 0;
            var kinds = new HashSet<TileKind>();
            for (var dtc = -30; dtc <= 30; dtc++)
            {
                var k = city.Get(r, ((tC + dtc) % n + n) % n);
                if (k == TileKind.Sky) continue;
                solids++;
                kinds.Add(k);
            }
            Console.WriteLine($"ring {r}: ±30 band has {solids} solid ({string.Join(",", kinds)})");
        }
        // Trace the severed section's own connectivity: flood 4-connected solid from a tile
        // just above the cut and report how low it reaches (crust contact = why it stands).
        {
            var startR = city.SurfaceRing + 5;
            var nS = city.TilesAt(startR);
            var tS = (int)MathF.Round((td + 0.5f) / n0 * nS - 0.5f);
            var seed = -1;
            for (var dtc = -14; dtc <= 14 && seed < 0; dtc++)
            {
                var t = ((tS + dtc) % nS + nS) % nS;
                if (Tiles.IsSolid(city.Get(startR, t))) seed = t;
            }
            Console.WriteLine($"flood seed ring {startR} tile {seed}");
            if (seed >= 0)
            {
                var seen = new HashSet<(int, int)>();
                var stack = new Stack<(int, int)>();
                stack.Push((startR, seed));
                var minRing = startR;
                (int r, int t) lowTile = (startR, seed);
                var count = 0;
                while (stack.Count > 0 && count < 90000)
                {
                    var (r, t) = stack.Pop();
                    var n = city.TilesAt(r);
                    t = ((t % n) + n) % n;
                    if (!seen.Add((r, t))) continue;
                    var k = city.Get(r, t);
                    if (!Tiles.IsSolid(k)) continue;
                    if (Tiles.IsAnchored(k) && !Tiles.Topples(k))
                    {
                        Console.WriteLine($"  hard anchor: {k} at ring {r} tile {t}");
                        continue;
                    }
                    count++;
                    if (r < minRing) { minRing = r; lowTile = (r, t); }
                    var (ir, it) = city.InnerNeighbour(r, t);
                    if (ir >= 0) stack.Push((ir, it));
                    var oc = city.OuterNeighbourCount(r, t);
                    for (var i = 0; i < oc; i++)
                    {
                        var (orr, ott) = city.OuterNeighbour(r, t, i);
                        if (orr < city.Rings) stack.Push((orr, ott));
                    }
                    stack.Push((r, t - 1));
                    stack.Push((r, t + 1));
                }
                Console.WriteLine($"  region {count} tiles, lowest ring {minRing} "
                    + $"(crust line {city.SurfaceRing + 4}) low tile {lowTile.r}/{lowTile.t} "
                    + $"kind {city.Get(lowTile.r, lowTile.t)}");
            }
        }
        var ticks = 0;
        while (cRigid.Bodies.Count == 0 && ticks++ < 240)
        {
            cPhysics.Update(1f / 60f);
            cRigid.Update(1f / 60f);
            if (cPhysics.NewlyCondemnedThisTick > 0 || cPhysics.RigidDetachesThisTick > 0
                || cPhysics.CollapsesThisTick > 0)
                Console.WriteLine($"  tick {ticks}: condemned {cPhysics.NewlyCondemnedThisTick} "
                    + $"rigid {cPhysics.RigidDetachesThisTick} crumbled {cPhysics.CollapsesThisTick}");
        }
        Console.WriteLine($"bodies {cRigid.Bodies.Count}, cells {cRigid.CellCount}, ticks {ticks}");
    }
}
