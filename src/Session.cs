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
    public readonly List<Projectile> Projectiles = new();
    public readonly List<FallingBoulder> Boulders = new();
    public readonly List<TitanProjectile> TitanShots = new();
    public readonly List<Meteor> Meteors = new();
    public readonly List<Sentry> Sentries = new();

    /// <summary>Ambient-event timers (meteor strikes, magma surges) — see AmbientDirector.</summary>
    public float MeteorTimer;
    public float SurgeTimer;

    public float EarthquakeTimer;
    public float SpawnTimer;
    public float FaunaTimer;
    public float Shake;
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
    public const float OrbitAltitude = 480f;

    /// <summary>The mothership's world position in this planet's coordinate space.</summary>
    public Vector2 StationPos =>
        Planet.Center + new Vector2(MathF.Cos(MothershipAngle), MathF.Sin(MothershipAngle))
            * (Planet.Radius * World.Planet.TileSize + OrbitAltitude);

    public Session(PlanetDef def) => Def = def;
}
