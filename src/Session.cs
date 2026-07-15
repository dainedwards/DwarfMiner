using System;
using System.Collections.Generic;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner;

/// <summary>
/// The per-run world bundle — everything owned by a single visit to a single planet. Game1
/// swaps the whole Session atomically when the player travels via the star map (or dies and
/// retries), so nothing from the previous planet can leak into the next. This is also exactly
/// the unit an in-run save/load would serialize.
/// </summary>
public sealed class Session
{
    public readonly PlanetDef Def;

    public Planet Planet = null!;
    public Cells Cells = null!;
    public Physics Physics = null!;
    public Player Player = null!;
    public Titan Titan = null!;

    public readonly List<Creature> Creatures = new();
    public readonly List<Corpse> Corpses = new();
    /// <summary>Physical gem drops lying in the world (see Entities.Pickup). Fed from
    /// Cells.PendingGemDrops, collected by touch.</summary>
    public readonly List<Pickup> Pickups = new();
    public readonly List<Projectile> Projectiles = new();
    public readonly List<FallingBoulder> Boulders = new();
    public readonly List<TitanProjectile> TitanShots = new();
    public readonly List<Meteor> Meteors = new();
    public readonly List<Sentry> Sentries = new();
    /// <summary>Thrown torches — in flight and planted. Stuck ones persist in the run save
    /// (a lit-up shaft should stay lit across a suspend).</summary>
    public readonly List<ThrownTorch> Torches = new();
    /// <summary>Physical enemy spawners (goo piles, lizard doors, alien homes) — placed by
    /// SpawnDirector.PopulateWorld at load, the only post-load source of new creatures.</summary>
    public readonly List<Spawner> Spawners = new();
    /// <summary>Set once PopulateWorld has run (normally on the background build thread —
    /// its spawn-space carving wakes physics planet-wide, which must be digested by the
    /// background settle, not by the first seconds of play).</summary>
    public bool Populated;

    /// <summary>Meteor-strike cadence — the frequent ambient dodge hazard, outside the
    /// disaster clock. See AmbientDirector.</summary>
    public float MeteorTimer;

    /// <summary>The shared disaster clock (see AmbientDirector): counts down to the next
    /// disaster while the world is quiet, holds while one is live (only one at a time), and
    /// its reset interval scales with <see cref="World.PlanetDef.Difficulty"/> — ~7 min on
    /// the gentlest worlds down to ~2 min on the hardest. <see cref="NextDisaster"/>, when
    /// set (DM_FLARE-style tooling hooks), forces which kind fires next.</summary>
    public float DisasterTimer = 240f;
    public Systems.DisasterKind? NextDisaster;

    /// <summary>Solar-flare phases: a warned get-underground window, then a scorching phase
    /// that burns anyone in surface air. Blizzards freeze exposed dwarves for their window.</summary>
    public float FlareWarn;
    public float FlareActive;
    public float BlizzardActive;

    /// <summary>Acid-rain storm state (acid worlds, PlanetDef.AcidRain): a toxic cloud
    /// parks over a bearing and rains live acid cells for the active window. The cloud
    /// drifts while it rains — see AmbientDirector.</summary>
    public float AcidRainActive;
    public float AcidRainAngle;

    /// <summary>Ambient weather: drifting clouds that gather and shed rain (water, thin acid,
    /// or ember-rain by biome). Gentler than the acid-rain disaster — it waters the tree
    /// ecosystem and adds atmosphere rather than threatening the dwarf. Driven by
    /// <see cref="Systems.Weather"/>. Transient (not saved).</summary>
    public readonly List<Systems.Cloud> Clouds = new();
    public float CloudTimer = 12f;

    /// <summary>Shortcut to this run's living trees (they live on the planet). Regrown and
    /// watered by <see cref="Systems.TreeEcology"/>.</summary>
    public List<TreeSite> Trees => Planet.Trees;

    /// <summary>Eruption in progress: which vent is erupting and how long it keeps spewing.
    /// Vent sites live on <see cref="World.Planet.VolcanoVents"/>.</summary>
    public float EruptionLeft;
    public int EruptionVent = -1;

    public float SpawnTimer;
    public float FaunaTimer;
    public float Shake;

    /// <summary>City anger at the dwarf, 0-100. Killing residents and smashing tower tiles
    /// pumps it; time bleeds it off. Past the tipping point the militia and air patrol turn
    /// their guns on the player and civilians take cover. Transient — a fresh landing (or
    /// enough good behaviour) resets the city's mood.</summary>
    public float CityWrath;
    public float RunTime;
    public bool HasCannon;

    /// <summary>Spaceship escape progress. <see cref="PadPos"/> anchors the build site once a
    /// launch pad is crafted; <see cref="ShipStage"/> counts installed stages (1 hull,
    /// 2 engine, 3 nav core = ready to launch).</summary>
    public Vector2? PadPos;
    public int ShipStage;

    /// <summary>Fuel units loaded into the ship. The rocket only lifts off once this reaches
    /// the launch requirement; refuelling pulls mined "fuel" out of the inventory.</summary>
    public int ShipFuel;

    /// <summary>Storage-depot build site (surface). While placed, the dwarf can bank raw
    /// resources here (persisted per-planet in MetaSave) so death doesn't wipe them.</summary>
    public Vector2? DepotPos;

    /// <summary>Bearing of the mothership's parking orbit — it hangs in the planet view at
    /// <see cref="OrbitAltitude"/> above the surface. The rover departs from it and the
    /// escape rocket must climb back up and dock with it. Defaults to the spawn bearing.</summary>
    public float MothershipAngle = -MathF.PI / 2f;

    /// <summary>How far above the planet surface (px) the mothership parks.</summary>
    public const float OrbitAltitude = 700f;

    /// <summary>Orbital drift (rad/s) once the rover has dropped — the station keeps moving
    /// around the planet while you mine, so the return rocket is a real rendezvous. Idle
    /// while parked in the pre-drop orbit (the player is steering it then).</summary>
    public const float StationDriftRate = 0.0035f;

    /// <summary>Where the spent rover came to rest — drawn as wreckage for the whole visit,
    /// a landmark marking your arrival point. Not persisted in the run save (cosmetic).</summary>
    public Vector2? RoverWreck;

    /// <summary>Extra height above the parking orbit — set on atmosphere entry and decayed
    /// by the orbit tick, so arriving reads as the ship flying itself down into orbit.</summary>
    public float OrbitEntryOffset;

    /// <summary>The mothership's world position in this planet's coordinate space.</summary>
    public Vector2 StationPos =>
        Planet.Center + new Vector2(MathF.Cos(MothershipAngle), MathF.Sin(MothershipAngle))
            * (Planet.Radius * World.Planet.TileSize + OrbitAltitude + OrbitEntryOffset);

    public Session(PlanetDef def) => Def = def;
}
