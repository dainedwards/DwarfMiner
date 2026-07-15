using System;
using System.IO;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Systems;

/// <summary>
/// Single-slot in-run save. Serializes the whole Session — planet tiles, cell sim, player,
/// ship progress, titan, sentries — so a long ship-building run survives quitting the game.
/// Creatures/corpses/projectiles are deliberately dropped: SpawnDirector restocks wildlife
/// naturally. The save is deleted on any run ending (death or victory), so it's a suspend
/// file, not a save-scumming tool.
/// </summary>
public static class RunSave
{
    // Bump when the format or the planet/cell geometry changes — old saves are discarded.
    // v8: 4-px tiles (doubled ring geometry) + Conglomerate composition table.
<<<<<<< HEAD
    private const int Version = 24;  // 24: Density-8 cell grid — cell state dims changed (23 reserved by the titan-siege pass on noita-sim)
=======
    private const int Version = 23;  // 23: player HasGrapple (grappling hook)
>>>>>>> noita-sim
                                     // 22: skyscraper facade frames (straight-tower lattice)
                                     // 21: titan carcass + soul-claim flag
                                     // 9: planet gem-overlay section
                                     // 20: player ScannerTier (craftable geo-scanner)
                                     // 10: city civilian spawn sites + lizardman dens in planet state
                                     // 11: toolbelt widened 13 → 24 slots (belt block length changed)
                                     // 12: surface-profile section in planet state (lumpy asteroid)
                                     // 13: player LightTier int replaces HasLantern bool
                                     // 16: elemental-arms weapon bools
                                     // 17: Back equip slot (paper doll widened 11 → 12)
                                     // 14: equipment (paper-doll) slot block after the toolbelt
                                     // 15: gloves + 2 accessory slots (equipment block 8 → 11)
    private const uint Magic = 0x444D5253; // "DMRS"

    private static string SavePath => Path.Combine(SaveSlots.Dir(SaveSlots.Active), "run.sav");

    public static bool Exists => File.Exists(SavePath);

    /// <summary>Whether a given slot holds a suspended run — the title screen's
    /// "RUN IN PROGRESS" tag, without switching slots.</summary>
    public static bool ExistsIn(int slot) =>
        File.Exists(Path.Combine(SaveSlots.Dir(slot), "run.sav"));

    public static void Delete()
    {
        try { File.Delete(SavePath); }
        catch { /* best-effort — a stale file just re-offers resume */ }
    }

    public static void Write(Session run)
    {
        try
        {
            // Rigid debris isn't serialized (kinetics drop, same policy as flying cells) —
            // but a chunk's tiles already left the grid, so unlike a few grains of dust it
            // would be real missing terrain. Stamp every live body into the grid first; a
            // mid-air chunk simply re-condemns and resumes falling after the save.
            run.Rigid?.StampAll();

            // Gzip shrinks the save ~50×: the payload is dominated by the cell grid, which
            // is overwhelmingly zero bytes.
            using var w = new BinaryWriter(new System.IO.Compression.GZipStream(
                File.Create(SavePath), System.IO.Compression.CompressionLevel.Fastest));
            w.Write(Magic);
            w.Write(Version);
            w.Write(run.Def.Id);

            w.Write(run.RunTime);
            w.Write(run.HasCannon);
            w.Write(run.ShipStage);
            w.Write(run.ShipFuel);
            w.Write(run.PadPos.HasValue);
            if (run.PadPos is { } pad) { w.Write(pad.X); w.Write(pad.Y); }
            w.Write(run.DepotPos.HasValue);
            if (run.DepotPos is { } depot) { w.Write(depot.X); w.Write(depot.Y); }

            run.Planet.WriteState(w);
            run.Cells.WriteState(w);

            var p = run.Player;
            w.Write(p.Position.X); w.Write(p.Position.Y);
            w.Write(p.Velocity.X); w.Write(p.Velocity.Y);
            w.Write(p.Health);
            w.Write(p.Oxygen);
            w.Write(p.HasAirTank);
            w.Write(p.PickaxeTier);
            w.Write(p.HasDrill); w.Write(p.HasHammer); w.Write(p.LightTier);
            w.Write(p.HasArmor); w.Write(p.HasCoreDrill);
            w.Write(p.HasPistol); w.Write(p.HasMachineGun); w.Write(p.HasLaser);
            w.Write(p.HasLaserCannon); w.Write(p.HasRocketLauncher); w.Write(p.HasMiningLaser);
            // v16: the elemental arms (flamethrower / acid spewer / lightning gun).
            w.Write(p.HasFlamethrower); w.Write(p.HasAcidSpewer); w.Write(p.HasLightningGun);
            w.Write(p.HasGrapple);    // v23
            w.Write(p.ScannerTier);   // v20
            w.Write(p.FlyMode);
            w.Write(p.BeaconWorld.HasValue);
            if (p.BeaconWorld is { } b) { w.Write(b.X); w.Write(b.Y); }

            w.Write(p.Toolbelt.Selected);
            for (var s = 0; s < Toolbelt.SlotCount; s++)
                w.Write(p.Toolbelt.Slots[s] ?? "");

            for (var s = 0; s < Equipment.SlotCount; s++)
                w.Write(p.Equipment.Slots[s] ?? "");

            w.Write(p.Inventory.Items.Count);
            foreach (var (id, count) in p.Inventory.Items)
            {
                w.Write(id);
                w.Write(count);
            }

            w.Write(run.Titan.Position.X); w.Write(run.Titan.Position.Y);
            w.Write(run.Titan.Health);
            w.Write(run.Titan.Anger);
            // Boss variant + egg state. The kind comes from the planet def but is saved too so
            // a future def re-map doesn't retroactively change an in-progress run.
            w.Write((int)run.Titan.Kind);
            w.Write(run.Titan.Hatched);
            w.Write(run.Titan.EggTimer);
            w.Write(run.Titan.EggHealth);

            w.Write(run.Sentries.Count);
            foreach (var s in run.Sentries)
            {
                w.Write(s.Position.X); w.Write(s.Position.Y);
                w.Write(s.Health);
            }

            // v18: planted torches (in-flight ones just drop — they were airborne for
            // half a second at most) + the headlamp upgrade rung.
            var stuck = run.Torches.FindAll(t => t.Stuck);
            w.Write(stuck.Count);
            foreach (var t in stuck)
            {
                w.Write(t.Position.X); w.Write(t.Position.Y);
                w.Write(t.BaseAngle);
            }
            w.Write(run.Player.HeadlampTier);

            // v19: melee arsenal (id → upgrade rung).
            w.Write(run.Player.MeleeTiers.Count);
            foreach (var (mid, mtier) in run.Player.MeleeTiers)
            {
                w.Write(mid);
                w.Write(mtier);
            }

            // v21: the titan-soul harvest. Quitting between kill and carve must not void the
            // soul, so the carcass (position + carve progress) and the claimed flag persist.
            w.Write(run.SoulClaimed);
            w.Write(run.TitanCarcass is { Claimed: false });
            if (run.TitanCarcass is { Claimed: false } tcc)
            {
                w.Write(tcc.Position.X); w.Write(tcc.Position.Y);
                w.Write(tcc.Progress);
            }
        }
        catch (Exception ex)
        {
            // Best-effort persistence — never crash the game on disk errors.
            if (Environment.GetEnvironmentVariable("DM_SAVEDEBUG") is { Length: > 0 })
                Console.WriteLine($"[runsave] write failed: {ex}");
        }
    }

    /// <summary>Rebuild a Session from disk, or null if there's no save / it's unreadable
    /// (unreadable saves are deleted so the resume prompt doesn't dangle). The caller still
    /// owns post-load work: Crafting.SetPlanet, cell pre-settle, camera snap.</summary>
    public static Session? TryRead()
    {
        if (!Exists) return null;
        try
        {
            using var r = new BinaryReader(new System.IO.Compression.GZipStream(
                File.OpenRead(SavePath), System.IO.Compression.CompressionMode.Decompress));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version)
                throw new InvalidDataException("bad header");

            var def = PlanetDefs.ById(r.ReadString());
            var run = new Session(def)
            {
                RunTime = r.ReadSingle(),
                HasCannon = r.ReadBoolean(),
                ShipStage = r.ReadInt32(),
                ShipFuel = r.ReadInt32(),
            };
            if (r.ReadBoolean()) run.PadPos = new Vector2(r.ReadSingle(), r.ReadSingle());
            if (r.ReadBoolean()) run.DepotPos = new Vector2(r.ReadSingle(), r.ReadSingle());

            // Ring count must match the def's size scale or ReadState rejects the geometry —
            // generated campaign worlds are almost never the standard 200 rings.
            run.Planet = new Planet(new Vector2(2400, 2400), Planet.RingsFor(def.SizeScale))
            {
                GravityScale = def.GravityScale,   // def-derived, not in the save
                Airless = def.Airless,
            };
            run.Planet.ReadState(r);
            run.Cells = new Cells(run.Planet);
            run.Cells.ReadState(r);
            run.Physics = new Physics(run.Planet, run.Cells);

            var pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            var p = new Player(pos)
            {
                Velocity = new Vector2(r.ReadSingle(), r.ReadSingle()),
                Health = r.ReadSingle(),
                Oxygen = r.ReadSingle(),
                HasAirTank = r.ReadBoolean(),
                PickaxeTier = r.ReadInt32(),
                HasDrill = r.ReadBoolean(), HasHammer = r.ReadBoolean(), LightTier = r.ReadInt32(),
                HasArmor = r.ReadBoolean(), HasCoreDrill = r.ReadBoolean(),
                HasPistol = r.ReadBoolean(), HasMachineGun = r.ReadBoolean(), HasLaser = r.ReadBoolean(),
                HasLaserCannon = r.ReadBoolean(), HasRocketLauncher = r.ReadBoolean(),
                HasMiningLaser = r.ReadBoolean(),
                HasFlamethrower = r.ReadBoolean(), HasAcidSpewer = r.ReadBoolean(),
                HasLightningGun = r.ReadBoolean(),
                HasGrapple = r.ReadBoolean(),  // v23
                ScannerTier = r.ReadInt32(),   // v20
                FlyMode = r.ReadBoolean(),
            };
            if (r.ReadBoolean()) p.BeaconWorld = new Vector2(r.ReadSingle(), r.ReadSingle());

            p.Toolbelt.Selected = r.ReadInt32();
            for (var s = 0; s < Toolbelt.SlotCount; s++)
            {
                var id = r.ReadString();
                p.Toolbelt.Slots[s] = id.Length == 0 ? null : id;
            }

            for (var s = 0; s < Equipment.SlotCount; s++)
            {
                var id = r.ReadString();
                p.Equipment.Slots[s] = id.Length == 0 ? null : id;
            }

            var invCount = r.ReadInt32();
            for (var i = 0; i < invCount; i++)
            {
                var id = r.ReadString();
                p.Inventory.Add(id, r.ReadInt32());
            }
            run.Player = p;

            // Titan: construct on the surface at the saved bearing, then restore the exact
            // body state — legs/tail are verlet-simulated and re-plant within a few frames.
            var titanPos = new Vector2(r.ReadSingle(), r.ReadSingle());
            var rel = titanPos - run.Planet.Center;
            var titanHealth = r.ReadSingle();
            var titanAnger = r.ReadSingle();
            var titanKind = (TitanKind)r.ReadInt32();
            run.Titan = new Titan(run.Planet, MathF.Atan2(rel.Y, rel.X), titanKind)
            {
                Position = titanPos,
                Health = titanHealth,
                Anger = titanAnger,
                Hatched = r.ReadBoolean(),
                EggTimer = r.ReadSingle(),
                EggHealth = r.ReadSingle(),
            };

            var sentryCount = r.ReadInt32();
            for (var i = 0; i < sentryCount; i++)
            {
                var sPos = new Vector2(r.ReadSingle(), r.ReadSingle());
                run.Sentries.Add(new Sentry(sPos) { Health = r.ReadSingle() });
            }

            var torchCount = r.ReadInt32();
            for (var i = 0; i < torchCount; i++)
            {
                var tPos = new Vector2(r.ReadSingle(), r.ReadSingle());
                run.Torches.Add(new ThrownTorch(tPos, Vector2.Zero)
                {
                    Stuck = true,
                    BaseAngle = r.ReadSingle(),
                });
            }
            run.Player.HeadlampTier = r.ReadInt32();
            var meleeCount = r.ReadInt32();
            for (var i = 0; i < meleeCount; i++)
            {
                var mid = r.ReadString();
                run.Player.MeleeTiers[mid] = r.ReadInt32();
            }

            // v21: the titan-soul harvest state (see Write).
            run.SoulClaimed = r.ReadBoolean();
            if (r.ReadBoolean())
            {
                var cPos = new Vector2(r.ReadSingle(), r.ReadSingle());
                run.TitanCarcass = new TitanCorpse(cPos, run.Titan.Kind, run.Titan.BodyRadius)
                {
                    Progress = r.ReadSingle(),
                };
            }
            // Belt-and-braces: a dead titan whose soul was never claimed always offers its
            // carcass, even across saves from the crossing frame itself.
            else if (run.Titan.Health <= 0 && !run.SoulClaimed)
            {
                run.TitanCarcass = new TitanCorpse(run.Titan.Position, run.Titan.Kind,
                    run.Titan.BodyRadius);
            }

            // Timers restart at their run-start defaults — a beat of calm after resuming.
            run.DisasterTimer = AmbientDirector.NextInterval(def) * 0.5f;
            run.SpawnTimer = 6f;
            run.FaunaTimer = 8f;
            return run;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("DM_SAVEDEBUG") is { Length: > 0 })
                Console.WriteLine($"[runsave] load failed: {ex}");
            Delete();
            return null;
        }
    }
}
