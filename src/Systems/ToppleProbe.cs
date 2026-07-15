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
        var ticks = 0;
        while (cRigid.Bodies.Count == 0 && ticks++ < 240)
        {
            cPhysics.Update(1f / 60f);
            cRigid.Update(1f / 60f);
        }
        Console.WriteLine($"bodies {cRigid.Bodies.Count}, cells {cRigid.CellCount}, ticks {ticks}");
    }
}
