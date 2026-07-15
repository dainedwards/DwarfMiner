using System;
using DwarfMiner.Rendering;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Entities;

/// <summary>
/// The felled kaiju's carcass. Slaying a titan no longer banks its soul on the spot — the
/// mountain of a body keels over where it died, and the dwarf must stand at it and carve
/// for <see cref="HarvestTime"/> seconds (the body visibly disintegrating as the work runs)
/// to claim the soul plus a butcher's bounty. It never decays: the carcass waits.
/// </summary>
public sealed class TitanCorpse
{
    public const float HarvestTime = 7f;

    public Vector2 Position;
    public Vector2 Velocity;
    public readonly TitanKind Kind;
    /// <summary>Carcass scale — the titan's BodyRadius, driving silhouette + harvest reach.</summary>
    public readonly float Radius;
    public float Progress;
    public bool Claimed;
    public float Dissolve => MathHelper.Clamp(Progress / HarvestTime, 0f, 1f);
    /// <summary>Which way the head fell. Fixed at death so the carcass never flips.</summary>
    public readonly int Facing = Random.Shared.Next(2) == 0 ? -1 : 1;

    public TitanCorpse(Vector2 pos, TitanKind kind, float radius)
    {
        Position = pos;
        Kind = kind;
        Radius = radius;
    }

    /// <summary>Settle: the carcass just slumps to the ground under gravity — no ragdoll
    /// theatrics at this scale, a dead kaiju is terrain with a face.</summary>
    public void Update(float dt, Planet planet)
    {
        var g = planet.GravityAt(Position);
        if (planet.IsSolidAt(Position + g * (Radius * 0.35f)))
        {
            Velocity = Vector2.Zero;
            return;
        }
        Velocity += g * 220f * dt;
        var speed = Velocity.Length();
        if (speed > 200f) Velocity *= 200f / speed;
        Position += Velocity * dt;
    }

    /// <summary>The carcass silhouette: the titan's own hide palette drained toward grey,
    /// lying flat — a chain of body mounds, a slumped head with a cross-scratched dead eye,
    /// dorsal nubs along the spine and stubby rigor-stiff legs poking up. Fades as the
    /// harvest dissolves it. Draw twice around a Renderer outline pass for the rim.</summary>
    public void Draw(Renderer r, Planet planet)
    {
        var up = planet.UpAt(Position);
        var right = new Vector2(-up.Y, up.X);
        var rot = MathF.Atan2(up.X, -up.Y);
        var (hide, _, _, _) = TitanRenderer.Palette(Kind);
        var grey = (byte)((hide.R + hide.G + hide.B) / 3);
        var body = Color.Lerp(hide, new Color(grey, grey, grey), 0.5f);
        var dark = Color.Lerp(body, Color.Black, 0.35f);
        var pale = Color.Lerp(body, Color.White, 0.22f);
        var L = Radius * 1.5f;          // half-length of the slumped body

        // Body mounds, tallest amidships.
        for (var i = -2; i <= 2; i++)
        {
            var f = 1f - MathF.Abs(i) * 0.28f;
            r.DrawCircle(Position + right * (Facing * i * L * 0.35f) + up * (Radius * 0.16f * f),
                Radius * 0.42f * f, body);
        }
        // Belly line hugging the ground.
        r.DrawRect(Position - up * (Radius * 0.05f), new Vector2(L * 1.5f, Radius * 0.16f), dark, rot);
        // Head slumped at the leading end, snout on the dirt.
        var head = Position + right * (Facing * L * 0.95f) - up * (Radius * 0.02f);
        r.DrawCircle(head, Radius * 0.34f, body);
        r.DrawRect(head + right * (Facing * Radius * 0.3f) - up * (Radius * 0.08f),
            new Vector2(Radius * 0.34f, Radius * 0.18f), body, rot);
        // Dead eye: a pale cross scratched over a dark socket.
        var eye = head + up * (Radius * 0.08f) + right * (Facing * Radius * 0.12f);
        r.DrawCircle(eye, Radius * 0.07f, dark);
        r.DrawRect(eye, new Vector2(Radius * 0.16f, Radius * 0.03f), pale, rot + 0.785f);
        r.DrawRect(eye, new Vector2(Radius * 0.16f, Radius * 0.03f), pale, rot - 0.785f);
        // Dorsal nubs along the spine.
        for (var i = -1; i <= 1; i++)
            r.DrawRect(Position + right * (Facing * i * L * 0.4f) + up * (Radius * 0.5f - MathF.Abs(i) * Radius * 0.12f),
                new Vector2(Radius * 0.14f, Radius * 0.26f), dark, rot + i * 0.2f);
        // Rigor-stiff legs poking up from the far side.
        for (var i = 0; i < 2; i++)
            r.DrawRect(Position - right * (Facing * L * (0.25f + i * 0.3f)) + up * (Radius * 0.42f),
                new Vector2(Radius * 0.1f, Radius * 0.4f), body, rot + (i == 0 ? 0.25f : -0.15f));
    }
}
