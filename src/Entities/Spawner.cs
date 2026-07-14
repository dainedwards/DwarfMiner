using System;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

public enum SpawnerKind
{
    /// <summary>A dripping slime mound on a cave floor — bubbles out cave slimes.</summary>
    GooPile,
    /// <summary>A brick doorway in a warren hall — lizardmen trickle out at a LOW rate.</summary>
    LizardDoor,
    /// <summary>A marked address in a city — its household of civilians steps out over time.</summary>
    AlienHome,
}

/// <summary>
/// A physical enemy spawner. With the world's population now rolled once at load, spawners
/// are the ONLY source of new creatures afterwards — visible structures the player can
/// learn, avoid, or camp. Each ticks only while the player is in its activity band (close
/// enough to matter, far enough not to see the pop-in), and stops while its brood cap is
/// alive nearby. Recreated by PopulateWorld on resume, like the creatures themselves.
/// </summary>
public sealed class Spawner
{
    public Vector2 Position;
    public SpawnerKind Kind;
    /// <summary>Seconds until the next spawn attempt. Starts staggered so a freshly loaded
    /// world doesn't fire every spawner on the same frame.</summary>
    public float Timer;
    /// <summary>Animation phase (goo pulse, door glow) so neighbours don't sync.</summary>
    public readonly float Phase = (float)Random.Shared.NextDouble() * MathHelper.TwoPi;

    public Spawner(Vector2 pos, SpawnerKind kind)
    {
        Position = pos;
        Kind = kind;
        Timer = 6f + (float)Random.Shared.NextDouble() * 12f;
    }
}
