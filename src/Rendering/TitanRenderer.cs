using System;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Rendering;

/// <summary>
/// Draws the boss — a genuinely different procedural skeleton per <see cref="TitanKind"/>
/// rather than a re-tint of one body: an upright, dorsal-finned Godzilla; an angular
/// laser-mawed Mecha; a legless Dune sandworm with a round toothed maw; a big-armed Kong. Also
/// handles the egg and the burrow mound. Called in world space between
/// <c>BeginEntities</c>/<c>EndEntities</c>;
/// <see cref="AddLights"/> runs in the lighting pass.
/// </summary>
public static class TitanRenderer
{
    // ── entry points ──────────────────────────────────────────────────────────

    public static void Draw(Renderer r, Titan t, Planet planet, Vector2 playerPos, float time)
    {
        if (!t.Hatched) { DrawEgg(r, t, planet, time); return; }
        if (t.Submerged) { DrawMound(r, t, planet); return; }

        var f = Frame.For(t, planet);
        switch (t.Kind)
        {
            case TitanKind.Godzilla:    DrawGodzilla(r, t, planet, playerPos, f, time); break;
            case TitanKind.Mecha:       DrawMecha(r, t, planet, playerPos, f, time); break;
            case TitanKind.Sandworm:    DrawSandworm(r, t, planet, playerPos, f, time); break;
            case TitanKind.Kong:        DrawKong(r, t, planet, playerPos, f, time); break;
            case TitanKind.Knifehead:   DrawKnifehead(r, t, planet, playerPos, f, time); break;
            case TitanKind.Otachi:      DrawOtachi(r, t, planet, playerPos, f, time); break;
            case TitanKind.Leatherback: DrawLeatherback(r, t, planet, playerPos, f, time); break;
            case TitanKind.Raiju:       DrawRaiju(r, t, planet, playerPos, f, time); break;
            case TitanKind.Slattern:    DrawSlattern(r, t, planet, playerPos, f, time); break;
            case TitanKind.Pyrodactyl:
            case TitanKind.Vitriodactyl: DrawPterodactyl(r, t, planet, playerPos, f, time); break;
            case TitanKind.CosmicOctopus: DrawStarspawn(r, t, planet, playerPos, f, time); break;
        }

        // Shootable weakpoints — pulsing soft-spots riding the hide (Combat pays triple
        // damage inside them). A landed weakpoint hit flashes them white.
        foreach (var wp in t.WeakpointsWorld())
        {
            var pulse = 0.5f + 0.5f * MathF.Sin(time * 5f);
            var flash = t.WeakpointFlash > 0f;
            r.DrawCircle(wp, 7f + pulse * 2.5f, (flash ? Color.White : new Color(255, 150, 60)) * 0.5f);
            r.DrawCircle(wp, 3.5f + pulse * 1.2f, flash ? Color.White : new Color(255, 220, 120));
        }
    }

    /// <summary>Drive one arm's hand through the shared smash swing: rest → reared high →
    /// hammered onto <see cref="Titan.SmashTarget"/> (arriving exactly on the impact beat) →
    /// buried follow-through. Every arm kind (<see cref="Titan.HasArms"/>) renders its swing
    /// through this, so the whole roster works a skyline with its fists, not just Kong.</summary>
    private static Vector2 SmashFist(Titan t, Frame f, int side, Vector2 shoulder, Vector2 rest)
    {
        if (t.SmashTimer <= 0f || side != t.SmashHand) return rest;
        var elapsed = Titan.SmashDuration - t.SmashTimer;
        var raised = shoulder + f.Up * 74f + f.Right * (f.Face * 26f);
        const float raiseEnd = 0.35f;
        const float hammerEnd = Titan.SmashDuration - Titan.SmashImpactAt;
        if (elapsed < raiseEnd) return Vector2.Lerp(rest, raised, elapsed / raiseEnd);
        if (elapsed < hammerEnd)
        {
            var q = (elapsed - raiseEnd) / (hammerEnd - raiseEnd);
            return Vector2.Lerp(raised, t.SmashTarget, q * q);   // accelerating swing
        }
        return t.SmashTarget;
    }

    public static void AddLights(Renderer r, Titan t, Planet planet, Vector2 playerPos, float time)
    {
        if (!t.Hatched)
        {
            var (_, occ, _, _) = Palette(t.Kind);
            r.AddLight(t.Position + planet.UpAt(t.Position) * 40f, 30f, occ);
            return;
        }
        if (t.Submerged) return;

        var f = Frame.For(t, planet);
        var (_, _, glowCalm, glowAngry) = Palette(t.Kind);
        var glow = Color.Lerp(glowCalm, glowAngry, f.Anger);

        // Eyes — skipped for the eyeless worm, which gets a maw glow instead.
        if (t.Kind != TitanKind.Sandworm)
            r.AddLight(HeadPos(t, f), 20f + 18f * f.Anger, EyeColor(t.Kind, f.Anger));

        // Attack telegraphs / muzzles.
        var mouth = MouthPos(t, f);
        switch (t.Kind)
        {
            case TitanKind.Sandworm:
            {
                // Warm glow from deep in the gullet, riding the head node so it tracks the maw.
                var maw = t.TailNodes[0] + f.Right * (f.Face * 16f);
                r.AddLight(maw, 26f + 14f * f.Anger, new Color(190, 70, 40));
                break;
            }
            case TitanKind.Godzilla:
                if (t.SpecialState > 0f)
                {
                    // Dorsal spines and maw flare brighter as the breath winds up and fires.
                    var charge = MathHelper.Clamp(t.SpecialState / Titan.FireBreathWindup, 0f, 1f);
                    r.AddLight(mouth, 24f + 40f * (1f - charge), new Color(120, 210, 255));
                    for (var i = 0; i < t.TailNodes.Length; i += 2)
                        r.AddLight(t.TailNodes[i], 14f, new Color(120, 200, 255));
                }
                break;
            case TitanKind.Mecha:
                if (t.SpecialState > 0f)
                {
                    var prog = 1f - t.SpecialState / Titan.LaserChargeWindup;
                    r.AddLight(mouth, 8f + 26f * prog, new Color(140, 240, 255));
                }
                if (t.BeamTimer > 0f)
                    for (var d = 20f; d < 520f; d += 26f)
                        r.AddLight(mouth + t.BeamDir * d, 16f, new Color(150, 240, 255));
                r.AddLight(f.Tp + f.Up * 44f, 16f, glow);   // chest reactor
                break;
            case TitanKind.Knifehead:
                // The blade crest shimmers through the gore windup and burns during the sprint.
                if (t.SpecialState > 0f)
                    r.AddLight(f.Tp + f.Up * 120f + f.Right * (f.Face * 90f),
                        t.Charging ? 34f : 20f, new Color(120, 255, 230));
                break;
            case TitanKind.Otachi:
                // Acid sacs along the throat glow while the spray winds up and fires.
                if (t.SpecialState > 0f)
                    r.AddLight(mouth, 26f, new Color(170, 250, 70));
                r.AddLight(f.Tp + f.Up * 60f, 12f + 10f * f.Anger, new Color(120, 200, 50));
                break;
            case TitanKind.Leatherback:
                // The back turbine builds to a blinding crackle before the EMP detonates.
                var hump = f.Tp + f.Up * 96f - f.Right * (f.Face * 30f);
                if (t.SpecialState > 0f)
                {
                    var prog = 1f - t.SpecialState / Titan.EmpWindup;
                    r.AddLight(hump, 20f + 70f * prog, new Color(140, 200, 255));
                }
                else
                    r.AddLight(hump, 16f + 12f * f.Anger, new Color(90, 160, 255));
                break;
            case TitanKind.Raiju:
                // Streaking hood glow — bright while it dashes.
                r.AddLight(f.Tp + f.Up * 60f + f.Right * (f.Face * 50f),
                    t.Charging ? 40f : 16f, new Color(170, 230, 255));
                break;
            case TitanKind.Slattern:
                // Triple crest burns hotter with anger; the tail tip glows where barrages fling from.
                r.AddLight(f.Tp + f.Up * 150f, 24f + 26f * f.Anger, glow);
                r.AddLight(t.TailNodes[^1], 14f, glow);
                break;
            case TitanKind.Pyrodactyl:
            case TitanKind.Vitriodactyl:
                // Payload sacs glow along the belly; the beak flares while it rains.
                r.AddLight(f.Tp - f.Up * 14f, 18f + 12f * f.Anger, glow);
                if (t.SpecialState > 0f)
                    r.AddLight(mouth, 30f, glow);
                break;
            case TitanKind.CosmicOctopus:
                // The galaxy in the mantle lights its own abyss — the boss is the lantern
                // of its deep arena. Windups flare it hard so both specials telegraph even
                // with the body half-buried in rock.
                r.AddLight(f.Tp + f.Up * 30f, 55f + 20f * f.Anger
                    + (t.SpecialState > 0f ? 40f * (1f - t.SpecialState / Titan.GravityWellWindup) : 0f), glow);
                if (t.SpecialState > 0f && !t.OctoPulseNext)
                    r.AddLight(mouth, 26f, glow);
                break;
        }

        // Beam-tip / hurl glow already handled by shots elsewhere.
    }

    // ── shared frame ──────────────────────────────────────────────────────────

    private readonly struct Frame
    {
        public readonly Vector2 Tp, Up, Right;
        public readonly float Rot, Face, Anger, Pulse;
        public readonly bool Flash;
        public readonly Color Hide, HideDark, HideLight, Belly, Chitin, Glow;

        private Frame(Titan t, Planet planet)
        {
            Tp = t.Position;
            Up = planet.UpAt(Tp);
            Right = new Vector2(-Up.Y, Up.X);
            Rot = MathF.Atan2(Up.X, -Up.Y);
            Face = t.Facing;
            Anger = MathHelper.Clamp(t.Anger / 100f, 0f, 1f);
            Pulse = t.Pulse;
            Flash = t.HitFlash > 0f;
            var (calm, angry, glowCalm, glowAngry) = Palette(t.Kind);
            Hide = Flash ? Color.White : Color.Lerp(calm, angry, Anger);
            HideDark = new Color(Hide.R / 2, Hide.G / 2, Hide.B / 2);
            HideLight = Add(Hide, 40);
            Belly = Add(Hide, 74);
            Chitin = t.Kind == TitanKind.Mecha ? new Color(30, 34, 44) : new Color(18, 14, 22);
            Glow = Color.Lerp(glowCalm, glowAngry, Anger);
        }

        public static Frame For(Titan t, Planet planet) => new(t, planet);
    }

    // ── Godzilla: upright biped, dorsal fins, atomic breath ─────────────────────

    private static void DrawGodzilla(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var breath = MathF.Sin(f.Pulse) * 2f;

        // Tail — verlet chain, thick at the root, dorsal ridge running along it.
        DrawSpineChain(r, t, f, 30f, 7f, ridge: true);

        // Hind legs — far leg shaded behind the near one.
        DrawLegs(r, t, f, 26f, 20f);

        // Torso — a barrel rising from the pelvis (tp) to the shoulders, leaning into Facing.
        var lean = f.Right * (f.Face * 14f);
        var pelvis = f.Tp;
        var chest = f.Tp + f.Up * (78f + breath) + lean;
        DrawTaper(r, pelvis + f.Up * 20f, chest, 96f, 74f, f.HideDark, f.Rot);
        DrawTaper(r, pelvis + f.Up * 26f, chest + f.Up * 4f, 78f, 58f, f.Hide, f.Rot);
        // Pale segmented belly.
        for (var i = 0; i < 4; i++)
            r.DrawRect(Vector2.Lerp(pelvis + f.Up * 24f, chest, i / 3f) + f.Right * (f.Face * 20f),
                new Vector2(34f - i * 3f, 12f), f.Belly, f.Rot);

        // Forearms — short, clawed, held forward; the committed arm hammers a smash.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 26f) + f.Up * 6f;
            var hand = shoulder + f.Right * (f.Face * 34f) - f.Up * (20f + MathF.Sin(f.Pulse + s) * 4f);
            hand = SmashFist(t, f, s, shoulder, hand);
            Seg(r, shoulder, hand, 12f, f.HideDark);
            Seg(r, hand, hand + f.Right * (f.Face * 12f) - f.Up * 6f, 7f, f.Hide);
            for (var c = -1; c <= 1; c++)
                Seg(r, hand + f.Right * (f.Face * 8f), hand + f.Right * (f.Face * 16f) + f.Up * (c * 5f), 2.5f, f.Chitin);
        }

        // Neck + head, thrust forward and up. The neck is drawn as a continuous column that
        // physically spans from deep in the shoulders to the base of the skull, so it can never
        // read as detached no matter how the body leans or which way it faces.
        var neck = chest + f.Up * 18f + f.Right * (f.Face * 24f);
        var head = neck + f.Up * 14f + f.Right * (f.Face * 34f);
        var neckBase = chest + f.Up * 4f;             // rooted inside the shoulders
        Seg(r, neckBase, head, 46f, f.HideDark);      // dark under-neck bridging chest → skull
        Seg(r, neckBase, head, 34f, f.Hide);          // lit throat
        r.DrawRect(head, new Vector2(56f, 46f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 6f, new Vector2(46f, 34f), f.Hide, f.Rot);
        // Snout.
        var snout = head + f.Right * (f.Face * 34f) - f.Up * 2f;
        r.DrawRect(snout, new Vector2(40f, 26f), f.HideDark, f.Rot);
        r.DrawRect(snout + f.Up * 4f, new Vector2(30f, 16f), f.Hide, f.Rot);

        // Atomic-breath maw: opens and glows white-hot while breathing (post-charge).
        var breathing = t.SpecialState > 0f && t.SpecialState < Titan.FireBreathWindup - 0.35f;
        var charging = t.SpecialState >= Titan.FireBreathWindup - 0.35f;
        if (breathing || charging)
        {
            var maw = snout + f.Right * (f.Face * 16f);
            var heat = breathing ? 1f : 1f - (t.SpecialState - (Titan.FireBreathWindup - 0.35f)) / 0.35f;
            r.DrawCircle(maw, 8f + heat * 6f, new Color(255, 180, 90));
            r.DrawCircle(maw, 4f + heat * 4f, Color.White);
        }
        else
        {
            var jaw = snout + f.Right * (f.Face * 4f) - f.Up * 14f;
            r.DrawRect(jaw, new Vector2(34f, 8f), f.Chitin, f.Rot);
            for (var ti = -1; ti <= 1; ti++)
                r.DrawRect(jaw + f.Right * (ti * 9f) + f.Up * 3f, new Vector2(3f, 5f), Color.White, f.Rot);
        }

        // Eyes.
        DrawEyes(r, t, f, head + f.Up * 10f, playerPos, 12f, 7f);

        // Brow horns swept back.
        for (var s = -1; s <= 1; s += 2)
            Seg(r, head + f.Up * 20f + f.Right * (s * 10f),
                head + f.Up * 30f - f.Right * (f.Face * 20f) + f.Right * (s * 6f), 5f, f.Chitin);
    }

    // ── Mecha: angular robot, charging maw laser ────────────────────────────────

    private static void DrawMecha(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var steel = f.Hide;
        var steelDark = f.HideDark;
        var panel = new Color(20, 24, 32);

        // Short segmented tail.
        DrawSpineChain(r, t, f, 22f, 6f, ridge: false, plated: true);

        DrawLegs(r, t, f, 24f, 18f, mech: true);

        // Boxy torso with panel seams + a glowing chest reactor.
        var chest = f.Tp + f.Up * 74f + f.Right * (f.Face * 8f);
        r.DrawRect(f.Tp + f.Up * 40f, new Vector2(104f, 96f), steelDark, f.Rot);
        r.DrawRect(f.Tp + f.Up * 44f, new Vector2(88f, 84f), steel, f.Rot);
        for (var i = -1; i <= 1; i++)
            r.DrawRect(f.Tp + f.Up * (44f + i * 24f), new Vector2(88f, 2f), panel, f.Rot);   // horizontal seams
        r.DrawRect(f.Tp + f.Up * 40f, new Vector2(2f, 92f), panel, f.Rot);
        var reactorPulse = MathF.Sin(time * 6f) * 0.5f + 0.5f;
        r.DrawCircle(f.Tp + f.Up * 44f, 10f, f.Glow);
        r.DrawCircle(f.Tp + f.Up * 44f, 5f + reactorPulse * 2f, Color.White);

        // Angular shoulder pauldrons.
        for (var s = -1; s <= 1; s += 2)
        {
            var sh = chest + f.Right * (s * 52f) + f.Up * 8f;
            r.DrawRect(sh, new Vector2(30f, 26f), steelDark, f.Rot + s * 0.4f);
            r.DrawRect(sh, new Vector2(20f, 16f), steel, f.Rot + s * 0.4f);
            // Arm — hammers when the smash state machine commits this side.
            var hand = sh + f.Right * (f.Face * 20f) - f.Up * (34f + MathF.Sin(f.Pulse + s) * 4f);
            hand = SmashFist(t, f, s, sh, hand);
            Seg(r, sh, hand, 12f, steelDark);
            r.DrawRect(hand, new Vector2(12f, 12f), steel, f.Rot);   // fist
        }

        // Head: a wedge with a single glowing visor and a mouth cannon.
        var neck = chest + f.Up * 16f + f.Right * (f.Face * 22f);
        var head = neck + f.Up * 10f + f.Right * (f.Face * 30f);
        // Continuous armoured neck strut from the chest to the skull so the head stays bolted on.
        Seg(r, chest + f.Up * 2f, head, 30f, steelDark);
        Seg(r, chest + f.Up * 2f, head, 20f, panel);
        r.DrawRect(head, new Vector2(52f, 40f), steelDark, f.Rot);
        r.DrawRect(head + f.Up * 4f, new Vector2(40f, 28f), steel, f.Rot);
        // Visor band.
        var visor = Color.Lerp(new Color(90, 200, 255), new Color(255, 90, 60), f.Anger);
        r.DrawRect(head + f.Up * 8f + f.Right * (f.Face * 6f), new Vector2(34f, 6f), visor, f.Rot);
        // Fin crest.
        Seg(r, head + f.Up * 20f, head + f.Up * 34f - f.Right * (f.Face * 10f), 5f, steelDark);

        var mouth = MouthPos(t, f);
        // Charge orb — grows and brightens over the windup, with a tracking telegraph line.
        if (t.SpecialState > 0f)
        {
            var prog = MathHelper.Clamp(1f - t.SpecialState / Titan.LaserChargeWindup, 0f, 1f);
            var aim = playerPos - mouth;
            if (aim.LengthSquared() > 1f)
            {
                aim.Normalize();
                for (var d = 24f; d < 460f; d += 22f)   // dashed targeting line
                    r.DrawCircle(mouth + aim * d, 1.5f, new Color(255, 120, 120) * (0.3f + 0.5f * prog));
            }
            r.DrawCircle(mouth, 3f + prog * 12f, new Color(120, 230, 255));
            r.DrawCircle(mouth, 1.5f + prog * 6f, Color.White);
        }
        // Firing beam — a thick lance along the committed direction.
        if (t.BeamTimer > 0f)
        {
            var end = mouth + t.BeamDir * 520f;
            Seg(r, mouth, end, 12f, new Color(150, 240, 255, 210));
            Seg(r, mouth, end, 6f, Color.White);
            r.DrawCircle(mouth, 14f, new Color(200, 245, 255));
        }

        DrawEyes(r, t, f, head + f.Up * 8f, playerPos, 0f, 0f, mech: true);
    }

    // ── Sandworm (Shai-Hulud): Dune worm — segmented tube, round toothed maw ─────

    private static void DrawSandworm(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        // Body — the verlet chain as a fat, near-uniform ringed tube (only tapering toward the
        // tail). Each node gets a banding ring so it reads as a segmented worm, not a snake.
        var nodes = t.TailNodes;
        for (var i = 1; i < nodes.Length; i++)
        {
            var fr = i / (float)(nodes.Length - 1);
            var thick = MathHelper.Lerp(40f, 12f, fr * fr);   // stays fat most of its length
            Seg(r, nodes[i - 1], nodes[i], thick, f.HideDark);
            Seg(r, nodes[i - 1], nodes[i], thick * 0.72f, f.Hide);
            Seg(r, nodes[i - 1], nodes[i], thick * 0.34f, f.HideLight);   // pale spine highlight
            // Segment ring at each joint.
            var seg = nodes[i] - nodes[i - 1];
            if (seg.LengthSquared() > 1f)
            {
                var n = Vector2.Normalize(new Vector2(-seg.Y, seg.X));
                r.DrawRect(nodes[i], new Vector2(thick, thick * 0.16f), f.Chitin,
                    MathF.Atan2(n.Y, n.X));
            }
        }

        // The maw rides on node 0 — the head of the chain — so it stays welded to the body no
        // matter how the worm weaves through the ground. A solid head lump bridges the leading
        // body segment into the maw; then a round aperture ringed with inward-curving crystalline
        // teeth, pointing the way the worm travels.
        var head = nodes[0];
        var mouthDir = f.Right * f.Face;
        r.DrawCircle(head, 24f, f.HideDark);
        r.DrawCircle(head, 18f, f.Hide);
        var mouthCenter = head + mouthDir * 16f;
        var openPulse = 0.72f + MathF.Sin(time * 3f) * 0.06f;
        var rOuter = 44f * openPulse;

        // Fleshy outer lip ring.
        r.DrawCircle(mouthCenter, rOuter, f.HideDark);
        r.DrawCircle(mouthCenter, rOuter * 0.82f, f.Hide);
        // Dark gullet.
        var gullet = mouthCenter + mouthDir * (rOuter * 0.12f);
        r.DrawCircle(gullet, rOuter * 0.6f, new Color(30, 18, 14));
        r.DrawCircle(gullet + mouthDir * 6f, rOuter * 0.34f, new Color(70, 20, 16));   // glowing throat
        // Ring of teeth around the rim, each pointing inward toward the gullet.
        var teeth = 12;
        var ivory = new Color(226, 214, 188);
        for (var k = 0; k < teeth; k++)
        {
            var a = k / (float)teeth * MathHelper.TwoPi;
            var dirOut = f.Right * MathF.Cos(a) + f.Up * MathF.Sin(a);
            var rimBase = mouthCenter + dirOut * (rOuter * 0.78f);
            var tip = mouthCenter + dirOut * (rOuter * 0.34f);   // points inward
            Seg(r, rimBase, tip, 6f, ivory);
            r.DrawCircle(tip, 2.4f, Color.White);
        }
        // A couple of oversized mandible fangs framing the maw.
        for (var s = -1; s <= 1; s += 2)
        {
            var side = new Vector2(-mouthDir.Y, mouthDir.X) * s;
            var baseP = mouthCenter + side * rOuter + mouthDir * 4f;
            Seg(r, baseP, baseP + mouthDir * 26f - side * 6f, 8f, ivory);
        }
    }

    // ── Starspawn: the Hollow's cosmic octopus ──────────────────────────────────

    /// <summary>A weightless abyss god: a towering dome mantle with a galaxy inside it —
    /// twinkling star-flecks drift across the nebula hide — over two huge staring eyes and
    /// eight sine-curled tentacles studded with glowing suckers. The gravity-well windup
    /// spins a collapsing swirl of motes into the body; the void-volley windup flares the
    /// under-mantle beak instead.</summary>
    private static void DrawStarspawn(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var breath = MathF.Sin(f.Pulse) * 3f;

        // Tentacles first (behind the mantle): eight arms rooted around the mantle skirt,
        // each a chain of segments curling on its own phase. Far arms draw darker so the
        // bundle reads with depth instead of as a flat broom.
        for (var arm = 0; arm < 8; arm++)
        {
            var spread = (arm / 7f - 0.5f) * 2f;             // -1 .. 1 across the skirt
            var back = (arm & 1) == 0;                        // alternate near/far arms
            var armCol = back ? f.HideDark : f.Hide;
            var p = f.Tp - f.Up * 24f + f.Right * (spread * 52f);
            var d = -f.Up + f.Right * (spread * 0.85f + f.Face * 0.15f);
            d.Normalize();
            var thick = 17f;
            for (var s = 0; s < 7; s++)
            {
                var curl = MathF.Sin(f.Pulse * 1.5f + arm * 0.9f + s * 0.6f) * 0.30f
                         + spread * 0.05f;
                d = Rot(d, curl);
                var q = p + d * 25f;
                Seg(r, p, q, thick, (s & 1) == 0 ? armCol : Shade(armCol, 0.85f));
                // Sucker pips down the inner face, glowing faintly.
                if ((s & 1) == 1)
                {
                    var perp = new Vector2(-d.Y, d.X);
                    r.DrawCircle((p + q) * 0.5f + perp * (thick * 0.34f), 2.6f, f.Glow * 0.8f);
                }
                p = q;
                thick *= 0.78f;
            }
            r.DrawCircle(p, 3f, f.Glow);   // luminous arm tip
        }

        // Mantle: a tall dome, dark rim over nebula hide, breathing with the pulse.
        var top = f.Tp + f.Up * (96f + breath);
        DrawTaper(r, f.Tp - f.Up * 20f, top, 124f, 42f, f.HideDark, f.Rot);
        DrawTaper(r, f.Tp - f.Up * 14f, top - f.Up * 6f, 104f, 34f, f.Hide, f.Rot);
        r.DrawCircle(top, 20f, f.Hide);

        // The galaxy inside: hashed star-flecks drifting slowly across the mantle,
        // twinkling on their own phases — the hide is a window into somewhere else.
        for (var i = 0; i < 14; i++)
        {
            var h = (i * 1013904223 + 17) & 0x7fffffff;
            var sx = (((h >> 5) & 0x7F) - 64) / 64f;          // -1 .. 1
            var sy = ((h >> 13) & 0x7F) / 127f;               //  0 .. 1
            var drift = MathF.Sin(time * 0.4f + i) * 6f;
            var fleck = f.Tp + f.Up * (2f + sy * 82f)
                       + f.Right * (sx * (86f - sy * 52f) + drift);
            var tw = MathF.Sin(time * (2f + (h & 3)) + i) * 0.5f + 0.5f;
            r.DrawCircle(fleck, 1.4f + tw * 1.2f, Color.Lerp(f.Glow, Color.White, tw) * (0.5f + tw * 0.5f));
        }

        // Two vast eyes on the mantle front — bigger sockets than anything else alive.
        DrawEyes(r, t, f, f.Tp + f.Up * 34f, playerPos, 11f, 7.5f);

        // Under-mantle beak (the muzzle — keep in sync with Titan.Mouth()).
        var beak = f.Tp - f.Up * 26f + f.Right * (f.Face * 30f);
        r.DrawCircle(beak, 10f, f.Chitin);
        r.DrawCircle(beak + f.Right * (f.Face * 5f), 6f, new Color(240, 232, 214));

        // Windup telegraphs: the gravity well spirals collapsing motes INTO the body; the
        // void volley charges a growing null-orb at the beak.
        if (t.SpecialState > 0f)
        {
            var prog = 1f - t.SpecialState / Titan.GravityWellWindup;
            if (t.OctoPulseNext)
            {
                for (var i = 0; i < 10; i++)
                {
                    var a = time * 3f + i * (MathHelper.TwoPi / 10f);
                    var rad = MathHelper.Lerp(200f, 30f, (prog + i * 0.04f) % 1f);
                    var mote = f.Tp + f.Up * 30f
                             + new Vector2(MathF.Cos(a), MathF.Sin(a)) * rad;
                    r.DrawCircle(mote, 3f, f.Glow * 0.9f);
                }
            }
            else
            {
                r.DrawCircle(beak, 4f + prog * 10f, new Color(40, 20, 60));
                r.DrawCircle(beak, 2f + prog * 5f, f.Glow);
            }
        }
    }

    private static Vector2 Rot(Vector2 v, float a)
    {
        var c = MathF.Cos(a);
        var s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    // ── Kong: big-armed ape ─────────────────────────────────────────────────────

    private static void DrawKong(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var fur = f.Hide;
        var furDark = f.HideDark;
        var stone = new Color(90, 88, 96);

        // Legs (short, stout) — far leg shaded behind the near one.
        DrawLegs(r, t, f, 30f, 24f);

        // Broad torso.
        var breath = MathF.Sin(f.Pulse) * 2f;
        var chest = f.Tp + f.Up * (70f + breath);
        DrawTaper(r, f.Tp + f.Up * 16f, chest, 120f, 104f, furDark, f.Rot);
        DrawTaper(r, f.Tp + f.Up * 24f, chest, 100f, 84f, fur, f.Rot);
        // Stone plates over the shoulders/back.
        for (var s = -1; s <= 1; s += 2)
            r.DrawRect(chest + f.Right * (s * 44f) + f.Up * 6f, new Vector2(34f, 22f), stone, f.Rot + s * 0.2f);
        r.DrawRect(chest - f.Right * (f.Face * 40f) + f.Up * 4f, new Vector2(30f, 40f), stone, f.Rot);

        // Massive arms: raised overhead mid-leap, hammering during a hand smash, planted
        // like columns otherwise.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 54f) + f.Up * 10f;
            var stance = shoulder + f.Right * (s * 10f) - f.Up * (66f + MathF.Sin(f.Pulse + s) * 5f);  // knuckle stance
            Vector2 fist;
            if (t.SmashTimer > 0f && s == t.SmashHand)
            {
                // The committed arm: rear the fist high, hammer it onto the smash target so
                // it arrives exactly at the impact moment, then leave it buried in the rubble
                // for the follow-through.
                var elapsed = Titan.SmashDuration - t.SmashTimer;
                var raised = shoulder + f.Up * 74f + f.Right * (f.Face * 26f);
                const float raiseEnd = 0.35f;
                const float hammerEnd = Titan.SmashDuration - Titan.SmashImpactAt;
                if (elapsed < raiseEnd)
                    fist = Vector2.Lerp(stance, raised, elapsed / raiseEnd);
                else if (elapsed < hammerEnd)
                {
                    var q = (elapsed - raiseEnd) / (hammerEnd - raiseEnd);
                    fist = Vector2.Lerp(raised, t.SmashTarget, q * q);   // accelerating swing
                }
                else
                    fist = t.SmashTarget;
            }
            else if (t.Leaping)
                fist = shoulder + f.Up * 60f + f.Right * (f.Face * 18f);          // reaching to slam
            else
                fist = stance;
            var elbow = Vector2.Lerp(shoulder, fist, 0.5f) + f.Right * (s * 16f);
            Seg(r, shoulder, elbow, 26f, furDark);
            Seg(r, elbow, fist, 22f, fur);
            r.DrawCircle(elbow, 13f, furDark);
            r.DrawCircle(fist, 16f, stone);   // rocky knuckles
        }

        // Head — small, heavy brow.
        var head = chest + f.Up * 22f + f.Right * (f.Face * 8f);
        r.DrawRect(head, new Vector2(48f, 40f), furDark, f.Rot);
        r.DrawRect(head + f.Up * 2f, new Vector2(36f, 28f), fur, f.Rot);
        r.DrawRect(head + f.Up * 10f, new Vector2(42f, 8f), f.Chitin, f.Rot);   // brow ridge
        // Muzzle.
        r.DrawRect(head + f.Right * (f.Face * 16f) - f.Up * 4f, new Vector2(24f, 18f), furDark, f.Rot);
        DrawEyes(r, t, f, head + f.Up * 2f, playerPos, 9f, 5f);
    }

    // ── Knifehead: pale carapace biped, huge blade crest, goring charge ─────────

    private static void DrawKnifehead(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        DrawSpineChain(r, t, f, 24f, 5f, ridge: false);
        DrawLegs(r, t, f, 24f, 19f);

        // Lean hard into the sprint; crouch low through the windup.
        var winding = t.SpecialState > 0f && !t.Charging;
        var lean = f.Right * (f.Face * (t.Charging ? 34f : 12f));
        var crouch = winding ? -14f : 0f;
        var breath = MathF.Sin(f.Pulse) * 2f;
        var pelvis = f.Tp + f.Up * crouch;
        var chest = pelvis + f.Up * (72f + breath) + lean;
        DrawTaper(r, pelvis + f.Up * 18f, chest, 88f, 62f, f.HideDark, f.Rot);
        DrawTaper(r, pelvis + f.Up * 24f, chest + f.Up * 4f, 70f, 50f, f.Hide, f.Rot);
        // Pale plated belly seams.
        for (var i = 0; i < 3; i++)
            r.DrawRect(Vector2.Lerp(pelvis + f.Up * 24f, chest, i / 2f) + f.Right * (f.Face * 16f),
                new Vector2(30f - i * 4f, 10f), f.Belly, f.Rot);

        // Stubby hooked forearms tucked against the chest.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 22f);
            var hand = shoulder + f.Right * (f.Face * 26f) - f.Up * 18f;
            Seg(r, shoulder, hand, 10f, f.HideDark);
            Seg(r, hand, hand + f.Right * (f.Face * 10f) - f.Up * 8f, 4f, f.Chitin);
        }

        // Head is mostly blade: a low skull flowing into an enormous flat crest that runs
        // forward past the snout AND sweeps back over the shoulders — the silhouette IS the
        // weapon. It glows along its edge while the gore is armed.
        var head = chest + f.Up * 16f + f.Right * (f.Face * 30f);
        Seg(r, chest + f.Up * 2f, head, 34f, f.HideDark);
        r.DrawRect(head, new Vector2(44f, 30f), f.Hide, f.Rot);
        var bladeTip = head + f.Right * (f.Face * 110f) + f.Up * 10f;
        var bladeBack = head - f.Right * (f.Face * 58f) + f.Up * 26f;
        // Blade: dark core with a pale honed edge, tapering to both ends.
        Seg(r, bladeBack, bladeTip, 22f, f.HideDark);
        Seg(r, bladeBack + f.Up * 4f, bladeTip + f.Up * 3f, 12f, f.Hide);
        var edgeGlow = t.SpecialState > 0f
            ? (t.Charging ? new Color(140, 255, 235) : new Color(90, 200, 210))
            : f.Belly;
        Seg(r, bladeBack + f.Up * 10f, bladeTip + f.Up * 6f, 4f, edgeGlow);
        r.DrawCircle(bladeTip, 5f, edgeGlow);
        // Underslung jaw beneath the blade.
        var jaw = head + f.Right * (f.Face * 26f) - f.Up * 12f;
        r.DrawRect(jaw, new Vector2(30f, 10f), f.HideDark, f.Rot);
        for (var ti = -1; ti <= 1; ti++)
            r.DrawRect(jaw + f.Right * (ti * 8f) + f.Up * 4f, new Vector2(3f, 5f), Color.White, f.Rot);
        // Four small eyes tucked under the crest (kaiju cluster, not a friendly pair).
        for (var e = 0; e < 4; e++)
            r.DrawCircle(head + f.Right * (f.Face * (6f + e * 7f)) + f.Up * (e % 2 == 0 ? 8f : 12f),
                2.6f, EyeColor(t.Kind, f.Anger));
    }

    // ── Otachi: winged acid-spitter, pincered tail ──────────────────────────────

    private static void DrawOtachi(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        // Tail chain ends in a three-claw pincer.
        DrawSpineChain(r, t, f, 26f, 6f, ridge: false);
        var tip = t.TailNodes[^1];
        var tipDir = t.TailNodes[^1] - t.TailNodes[^2];
        if (tipDir.LengthSquared() > 1f)
        {
            tipDir.Normalize();
            var perp = new Vector2(-tipDir.Y, tipDir.X);
            for (var c = -1; c <= 1; c++)
                Seg(r, tip, tip + tipDir * 20f + perp * (c * 12f), 4.5f, f.Chitin);
        }

        DrawLegs(r, t, f, 25f, 20f);

        // Torso, leaner than Godzilla's, rearing up while it sprays.
        var spraying = t.SpecialState > 0f;
        var rear = spraying ? 16f : 0f;
        var breath = MathF.Sin(f.Pulse) * 2f;
        var chest = f.Tp + f.Up * (76f + rear + breath) + f.Right * (f.Face * 8f);
        DrawTaper(r, f.Tp + f.Up * 20f, chest, 86f, 60f, f.HideDark, f.Rot);
        DrawTaper(r, f.Tp + f.Up * 26f, chest + f.Up * 4f, 68f, 48f, f.Hide, f.Rot);
        // Glowing acid veins up the throat — brighter mid-spray.
        var vein = spraying ? new Color(190, 255, 90) : new Color(120, 190, 50);
        for (var i = 0; i < 3; i++)
            r.DrawRect(Vector2.Lerp(f.Tp + f.Up * 30f, chest, i / 2f) + f.Right * (f.Face * 18f),
                new Vector2(4f, 14f), vein, f.Rot);

        // Folded membrane wings swept back off the shoulders — the Otachi silhouette.
        for (var s = -1; s <= 1; s += 2)
        {
            var root = chest + f.Right * (s * 24f) + f.Up * 10f;
            var elbow = root - f.Right * (f.Face * 44f) + f.Up * (34f + s * 4f);
            var wtip = elbow - f.Right * (f.Face * 52f) - f.Up * 10f;
            var shade = s == (f.Face >= 0 ? 1 : -1) ? f.HideDark : Shade(f.HideDark, 0.6f);
            // Wing fingers + stretched membrane between them.
            Seg(r, root, elbow, 7f, shade);
            Seg(r, elbow, wtip, 5f, shade);
            Seg(r, Vector2.Lerp(root, elbow, 0.5f), Vector2.Lerp(elbow, wtip, 0.6f), 16f,
                Shade(f.Hide, s == (f.Face >= 0 ? 1 : -1) ? 0.8f : 0.5f));
        }

        // Long neck to a narrow head with splitting mandibles; maw glows when spraying.
        var neck = chest + f.Up * 14f + f.Right * (f.Face * 26f);
        var head = neck + f.Up * 12f + f.Right * (f.Face * 38f);
        Seg(r, chest + f.Up * 4f, head, 26f, f.HideDark);
        Seg(r, chest + f.Up * 4f, head, 18f, f.Hide);
        r.DrawRect(head, new Vector2(42f, 26f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 3f, new Vector2(32f, 18f), f.Hide, f.Rot);
        // Split mandibles — hinge open around the glowing gullet during the spray.
        var mAng = spraying ? 0.5f : 0.15f;
        var snout = head + f.Right * (f.Face * 22f);
        for (var s = -1; s <= 1; s += 2)
            Seg(r, snout, snout + f.Right * (f.Face * 22f) + f.Up * (s * 22f * mAng), 6f, f.Chitin);
        if (spraying)
        {
            r.DrawCircle(snout + f.Right * (f.Face * 8f), 8f, new Color(150, 240, 60));
            r.DrawCircle(snout + f.Right * (f.Face * 8f), 4f, new Color(230, 255, 150));
        }
        DrawEyes(r, t, f, head + f.Up * 8f, playerPos, 8f, 4.5f);
    }

    // ── Leatherback: barnacled tank ape with an EMP turbine hump ────────────────

    private static void DrawLeatherback(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        DrawLegs(r, t, f, 32f, 26f);

        // The widest torso in the roster — a storm-blue slab.
        var breath = MathF.Sin(f.Pulse) * 2f;
        var chest = f.Tp + f.Up * (66f + breath);
        DrawTaper(r, f.Tp + f.Up * 14f, chest, 132f, 116f, f.HideDark, f.Rot);
        DrawTaper(r, f.Tp + f.Up * 22f, chest, 112f, 94f, f.Hide, f.Rot);
        // Barnacle clusters stubbling the hide.
        var seed = (int)(f.Tp.X * 0.37f) ^ (int)(f.Tp.Y * 0.19f);
        for (var b = 0; b < 8; b++)
        {
            var h = (seed * 1664525 + b * 1013904223) & 0x7fffffff;
            var bx = ((h >> 3) & 0x7F) - 64;
            var by = ((h >> 11) & 0x3F) + 18;
            r.DrawCircle(f.Tp + f.Right * (bx * 0.72f) + f.Up * by, 3f + (h & 3), f.Belly);
        }

        // The EMP organ: a ribbed turbine hump on the back, arcing lightning as it charges.
        var hump = f.Tp + f.Up * 96f - f.Right * (f.Face * 30f);
        r.DrawCircle(hump, 34f, f.HideDark);
        r.DrawCircle(hump + f.Up * 4f, 26f, f.Chitin);
        for (var i = -2; i <= 2; i++)
            r.DrawRect(hump + f.Right * (i * 10f) + f.Up * 6f, new Vector2(4f, 22f), f.HideDark, f.Rot);
        var charging = t.SpecialState > 0f;
        var coreCol = charging
            ? Color.Lerp(new Color(90, 160, 255), Color.White, 1f - t.SpecialState / Titan.EmpWindup)
            : new Color(90, 160, 255);
        r.DrawCircle(hump + f.Up * 6f, charging ? 12f : 8f, coreCol);
        if (charging)
        {
            // Jagged arcs crawling off the turbine — pseudo-random per frame slice.
            var n = 2 + (int)((1f - t.SpecialState / Titan.EmpWindup) * 4f);
            for (var a = 0; a < n; a++)
            {
                var h = ((int)(time * 30f) * 92821 + a * 68917) & 0x7fffffff;
                var dir = f.Right * (((h & 0xFF) / 128f) - 1f) + f.Up * (((h >> 8) & 0xFF) / 255f);
                if (dir.LengthSquared() < 0.05f) dir = f.Up;
                dir.Normalize();
                var p0 = hump + f.Up * 6f;
                var p1 = p0 + dir * 22f + f.Right * ((((h >> 16) & 0xF) - 8) * 1.5f);
                var p2 = p1 + dir * 18f - f.Right * ((((h >> 20) & 0xF) - 8) * 1.8f);
                Seg(r, p0, p1, 2.2f, new Color(170, 220, 255));
                Seg(r, p1, p2, 1.6f, Color.White);
            }
        }

        // Column arms planted on rocky knuckles (broader than Kong's); they hammer too.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 60f) + f.Up * 12f;
            var fist = shoulder + f.Right * (s * 16f) - f.Up * (62f + MathF.Sin(f.Pulse + s) * 4f);
            fist = SmashFist(t, f, s, shoulder, fist);
            var elbow = Vector2.Lerp(shoulder, fist, 0.5f) + f.Right * (s * 18f);
            Seg(r, shoulder, elbow, 30f, f.HideDark);
            Seg(r, elbow, fist, 26f, f.Hide);
            r.DrawCircle(fist, 18f, f.Chitin);
            for (var c = -1; c <= 1; c++)
                r.DrawCircle(fist + f.Right * (c * 9f) - f.Up * 8f, 5f, f.Belly);
        }

        // Squat head sunk between the shoulders, heavy jaw.
        var head = chest + f.Up * 20f + f.Right * (f.Face * 14f);
        r.DrawRect(head, new Vector2(50f, 34f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 2f, new Vector2(38f, 24f), f.Hide, f.Rot);
        r.DrawRect(head + f.Right * (f.Face * 14f) - f.Up * 8f, new Vector2(26f, 14f), f.HideDark, f.Rot);
        DrawEyes(r, t, f, head + f.Up * 4f, playerPos, 8f, 4.5f);
    }

    // ── Raiju: low sleek sprinter, split hood crest ─────────────────────────────

    private static void DrawRaiju(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        // Fin-bladed tail.
        DrawSpineChain(r, t, f, 20f, 4f, ridge: false);

        // Digitigrade legs (backward knee) sell the sprinter stance.
        DrawLegs(r, t, f, 20f, 15f, mech: true);

        // Speed streaks while dashing — motion smeared out behind the body.
        if (t.Charging)
            for (var s = 0; s < 4; s++)
                Seg(r, f.Tp + f.Up * (20f + s * 14f) - f.Right * (f.Face * (30f + s * 22f)),
                    f.Tp + f.Up * (20f + s * 14f) - f.Right * (f.Face * (78f + s * 26f)),
                    3f, new Color(170, 230, 255) * 0.5f);

        // Long horizontal body — a torpedo riding low over the legs, nose down like a
        // crocodile mid-sprint. Built from a lying taper instead of the upright barrel.
        var lunge = t.Charging ? 12f : 0f;
        var rump = f.Tp + f.Up * 34f - f.Right * (f.Face * 40f);
        var prow = f.Tp + f.Up * (46f + MathF.Sin(f.Pulse) * 2f) + f.Right * (f.Face * (52f + lunge));
        Seg(r, rump, prow, 52f, f.HideDark);
        Seg(r, rump + f.Up * 4f, prow + f.Up * 4f, 38f, f.Hide);
        Seg(r, rump + f.Up * 12f, prow + f.Up * 10f, 14f, f.HideLight);
        // Charge-lit flank stripes.
        var stripe = t.Charging ? new Color(190, 240, 255) : Color.Lerp(f.Belly, new Color(150, 220, 255), f.Anger);
        for (var i = 0; i < 3; i++)
            r.DrawRect(Vector2.Lerp(rump, prow, 0.3f + i * 0.2f) - f.Up * 6f, new Vector2(5f, 16f), stripe, f.Rot + 0.5f * f.Face);

        // Crocodile head with the split hood — two swept crest plates that flare while dashing.
        var head = prow + f.Right * (f.Face * 26f) + f.Up * 4f;
        r.DrawRect(head, new Vector2(46f, 24f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 2f, new Vector2(36f, 16f), f.Hide, f.Rot);
        var snout = head + f.Right * (f.Face * 28f) - f.Up * 2f;
        r.DrawRect(snout, new Vector2(28f, 12f), f.HideDark, f.Rot);
        for (var ti = -1; ti <= 1; ti++)
            r.DrawRect(snout + f.Right * (ti * 7f) - f.Up * 4f, new Vector2(2.5f, 4f), Color.White, f.Rot);
        var flare = t.Charging ? 0.55f : 0.25f;
        for (var s = -1; s <= 1; s += 2)
        {
            var root = head + f.Up * 10f;
            var crest = root - f.Right * (f.Face * 30f) + f.Up * (26f * (1f + s * flare * 0.4f)) + f.Right * (s * 8f);
            Seg(r, root, crest, 7f, f.Chitin);
            Seg(r, root, crest, 3f, stripe);
        }
        DrawEyes(r, t, f, head + f.Up * 6f, playerPos, 7f, 4f);
    }

    // ── Slattern: the category-5 apex — triple crest, spiked lash ───────────────

    private static void DrawSlattern(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        // The lash: a long tail chain studded with thorn spikes — the barrage's launcher.
        var nodes = t.TailNodes;
        for (var i = 1; i < nodes.Length; i++)
        {
            var fr = i / (float)(nodes.Length - 1);
            var thick = MathHelper.Lerp(30f, 8f, fr);
            Seg(r, nodes[i - 1], nodes[i], thick, Color.Lerp(f.HideDark, f.Hide, 1f - fr * 0.5f));
            // Paired thorns at each joint, angled off the chain.
            var seg = nodes[i] - nodes[i - 1];
            if (seg.LengthSquared() > 1f)
            {
                var n = Vector2.Normalize(new Vector2(-seg.Y, seg.X));
                Seg(r, nodes[i], nodes[i] + n * (thick * 0.9f), 3.5f, f.Chitin);
                Seg(r, nodes[i], nodes[i] - n * (thick * 0.9f), 3.5f, f.Chitin);
            }
        }
        r.DrawCircle(nodes[^1], 7f, f.Glow);   // the glowing flinger tip

        DrawLegs(r, t, f, 30f, 24f);

        // Towering torso — head and shoulders over every other kaiju.
        var breath = MathF.Sin(f.Pulse) * 2.5f;
        var lean = f.Right * (f.Face * 12f);
        var chest = f.Tp + f.Up * (108f + breath) + lean;
        DrawTaper(r, f.Tp + f.Up * 22f, chest, 118f, 88f, f.HideDark, f.Rot);
        DrawTaper(r, f.Tp + f.Up * 30f, chest + f.Up * 4f, 96f, 70f, f.Hide, f.Rot);
        // Cracked magma seams down the chest — the apex runs hot.
        for (var i = 0; i < 4; i++)
            r.DrawRect(Vector2.Lerp(f.Tp + f.Up * 34f, chest, i / 3f) + f.Right * (f.Face * (14f - i * 3f)),
                new Vector2(5f, 16f), Color.Lerp(f.Glow, f.Belly, 0.4f), f.Rot + 0.2f);

        // Twin heavy arms with talon fans — the committed one hammers a smash.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 48f) + f.Up * 8f;
            var hand = shoulder + f.Right * (f.Face * 30f) - f.Up * (44f + MathF.Sin(f.Pulse + s) * 5f);
            hand = SmashFist(t, f, s, shoulder, hand);
            var elbow = Vector2.Lerp(shoulder, hand, 0.5f) + f.Right * (s * 14f);
            Seg(r, shoulder, elbow, 20f, f.HideDark);
            Seg(r, elbow, hand, 16f, f.Hide);
            for (var c = -1; c <= 1; c++)
                Seg(r, hand, hand + f.Right * (f.Face * 16f) + f.Up * (c * 8f - 6f), 3.5f, f.Chitin);
        }

        // Head crowned by the triple crest — three swept blades, lit from within.
        var head = chest + f.Up * 24f + f.Right * (f.Face * 20f);
        Seg(r, chest + f.Up * 6f, head, 38f, f.HideDark);
        r.DrawRect(head, new Vector2(52f, 38f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 4f, new Vector2(42f, 28f), f.Hide, f.Rot);
        for (var c = -1; c <= 1; c++)
        {
            var root = head + f.Up * 16f + f.Right * (c * 12f);
            var ctip = root + f.Up * (34f - MathF.Abs(c) * 8f) - f.Right * (f.Face * (18f + MathF.Abs(c) * 10f)) + f.Right * (c * 8f);
            Seg(r, root, ctip, 7f, f.Chitin);
            Seg(r, root, ctip, 3f, f.Glow);
            r.DrawCircle(ctip, 3f, f.Glow);
        }
        // Underbite jaw with a tooth row.
        var jaw = head + f.Right * (f.Face * 26f) - f.Up * 12f;
        r.DrawRect(jaw, new Vector2(34f, 12f), f.HideDark, f.Rot);
        for (var ti = -1; ti <= 1; ti++)
            r.DrawRect(jaw + f.Right * (ti * 9f) + f.Up * 5f, new Vector2(3f, 6f), Color.White, f.Rot);
        DrawEyes(r, t, f, head + f.Up * 8f, playerPos, 10f, 6f);
    }

    // ── Pyrodactyl / Vitriodactyl: winged bombers — pterodactyl, but meaner ─────

    private static void DrawPterodactyl(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var lava = t.Kind == TitanKind.Pyrodactyl;
        var payload = lava ? new Color(255, 140, 40) : new Color(160, 240, 60);
        var payloadHot = lava ? new Color(255, 220, 120) : new Color(220, 255, 150);

        // Thin whip tail with a diamond vane at the tip.
        DrawSpineChain(r, t, f, 14f, 3f, ridge: false);
        var vane = t.TailNodes[^1];
        var vaneDir = t.TailNodes[^1] - t.TailNodes[^2];
        if (vaneDir.LengthSquared() > 1f)
        {
            vaneDir.Normalize();
            r.DrawRect(vane + vaneDir * 8f, new Vector2(16f, 9f), f.Chitin,
                MathF.Atan2(vaneDir.Y, vaneDir.X) + 0.78f);
        }

        // Wing beat: slow soaring flap at cruise, folded tight into the dive while bombing.
        var flap = t.Bombing ? -0.35f : MathF.Sin(f.Pulse * 2.2f) * 0.8f;

        // Far wing first (shaded), then body, then near wing over it — reads as depth.
        for (var s = -1; s <= 1; s += 2)
        {
            var near = s == (f.Face >= 0 ? 1 : -1);
            if (near) continue;
            DrawWing(r, t, f, flap * 0.85f, shade: 0.55f, back: true);
        }

        // Fuselage — a lean horizontal body slung between the wings, chest keel below.
        var rump = f.Tp - f.Right * (f.Face * 36f) - f.Up * 2f;
        var prow = f.Tp + f.Right * (f.Face * 44f) + f.Up * 6f;
        Seg(r, rump, prow, 40f, f.HideDark);
        Seg(r, rump + f.Up * 4f, prow + f.Up * 4f, 28f, f.Hide);
        Seg(r, rump - f.Up * 10f, prow - f.Up * 8f, 12f, f.Belly);   // pale keel
        // Payload sacs glowing along the belly — the bomb bay.
        for (var i = 0; i < 3; i++)
            r.DrawCircle(Vector2.Lerp(rump, prow, 0.3f + i * 0.22f) - f.Up * 14f,
                4.5f, t.SpecialState > 0f ? payloadHot : payload);

        // Tucked talons hanging off the hips.
        for (var s = -1; s <= 1; s += 2)
        {
            var hip = f.Tp - f.Right * (f.Face * 14f) - f.Up * 16f + f.Right * (s * 6f);
            var claw = hip - f.Up * 14f + f.Right * (f.Face * 8f);
            Seg(r, hip, claw, 5f, f.HideDark);
            for (var c = 0; c < 2; c++)
                Seg(r, claw, claw + f.Right * (f.Face * 8f) - f.Up * (3f + c * 4f), 2.2f, f.Chitin);
        }

        // Neck arcing up and forward into the head.
        var head = prow + f.Right * (f.Face * 30f) + f.Up * 14f;
        Seg(r, prow, head, 18f, f.HideDark);
        Seg(r, prow, head, 12f, f.Hide);
        r.DrawRect(head, new Vector2(30f, 22f), f.HideDark, f.Rot);
        r.DrawRect(head + f.Up * 2f, new Vector2(24f, 15f), f.Hide, f.Rot);

        // The mean part: a long spear beak lined with snaggle teeth, and a swept skull
        // crest counterweighting it — the classic pterosaur silhouette with malice.
        var beakTip = head + f.Right * (f.Face * 52f) - f.Up * 2f;
        Seg(r, head + f.Right * (f.Face * 8f), beakTip, 12f, f.HideDark);
        Seg(r, head + f.Right * (f.Face * 8f) + f.Up * 3f, beakTip + f.Up * 2f, 6f, f.Hide);
        Seg(r, beakTip, beakTip + f.Right * (f.Face * 7f) - f.Up * 5f, 4f, f.Chitin);   // hooked tip
        for (var ti = 0; ti < 4; ti++)
            r.DrawRect(Vector2.Lerp(head + f.Right * (f.Face * 14f), beakTip, ti / 3.6f) - f.Up * 6f,
                new Vector2(2.5f, 5f), Color.White, f.Rot);
        var crestTip = head - f.Right * (f.Face * 34f) + f.Up * 22f;
        Seg(r, head + f.Up * 6f, crestTip, 10f, f.HideDark);
        Seg(r, head + f.Up * 8f, crestTip, 4f, t.SpecialState > 0f ? payloadHot : payload);

        // Maw glow while raining.
        if (t.SpecialState > 0f)
        {
            r.DrawCircle(beakTip - f.Up * 4f, 7f, payload);
            r.DrawCircle(beakTip - f.Up * 4f, 3.5f, payloadHot);
        }

        DrawEyes(r, t, f, head + f.Up * 5f, playerPos, 6.5f, 4f);

        // Near wing over the body.
        DrawWing(r, t, f, flap, shade: 1f, back: false);
    }

    /// <summary>One membrane wing: shoulder → wrist → tip, two bone fingers with the
    /// membrane stretched back to the hip. <paramref name="flap"/> swings the whole
    /// assembly around the shoulder; <paramref name="shade"/> darkens the far wing.</summary>
    private static void DrawWing(Renderer r, Titan t, Frame f, float flap, float shade, bool back)
    {
        var shoulder = f.Tp + f.Up * 12f + f.Right * (f.Face * 4f);
        // Wing sweeps back and up from the shoulder; flap rotates it about the tangent axis.
        var span = f.Up * (46f + flap * 60f) - f.Right * (f.Face * 30f);
        var wrist = shoulder + span;
        var tip = wrist + f.Up * (18f + flap * 46f) - f.Right * (f.Face * 62f);
        var hip = f.Tp - f.Right * (f.Face * 30f) - f.Up * 6f;

        var bone = Shade(f.HideDark, shade);
        var skin = Shade(f.Hide, shade * 0.82f);
        // Membrane: filled between the bone line and the hip anchor.
        Seg(r, Vector2.Lerp(shoulder, wrist, 0.5f), Vector2.Lerp(hip, tip, 0.45f), 30f, skin);
        Seg(r, Vector2.Lerp(wrist, tip, 0.5f), Vector2.Lerp(hip, tip, 0.8f), 18f, skin);
        // Leading-edge bones + finger.
        Seg(r, shoulder, wrist, 9f, bone);
        Seg(r, wrist, tip, 6f, bone);
        Seg(r, wrist, Vector2.Lerp(hip, tip, 0.7f), 3.5f, bone);   // strut finger
        r.DrawCircle(wrist, 5.5f, bone);
        // Wing claw at the wrist joint — the mean little hook pterosaurs actually had.
        Seg(r, wrist, wrist + f.Right * (f.Face * 10f) + f.Up * 4f, 2.5f, Shade(f.Chitin, shade));
    }

    // ── shared primitives ───────────────────────────────────────────────────────

    /// <summary>Draw both legs of a biped, far leg first. The leg on the side opposite the
    /// facing direction renders in shadow behind the near leg, so the pair reads as two
    /// distinct legs with depth instead of one overlapping blob.</summary>
    private static void DrawLegs(Renderer r, Titan t, Frame f, float thigh, float shin, bool mech = false)
    {
        var faceSign = f.Face >= 0f ? 1 : -1;
        foreach (var leg in t.Legs)
            if (leg.Side != faceSign) DrawLeg(r, t, f, leg, thigh, shin, mech, far: true);
        foreach (var leg in t.Legs)
            if (leg.Side == faceSign) DrawLeg(r, t, f, leg, thigh, shin, mech);
    }

    /// <summary>3-bone digitigrade leg: hip→knee (thigh), knee→ankle (shin), ankle→toe (foot).
    /// The knee bows toward the facing direction and the ankle kicks back behind the toes —
    /// the theropod silhouette (the mech reverses the knee for a robot's backward joint).
    /// Shares its hip socket, bone lengths and ankle geometry with the simulation
    /// (<see cref="Titan.HipWorld"/>/<see cref="Titan.LegBoneLen"/>/<see cref="Titan.AnkleLift"/>)
    /// so the drawn leg roots exactly where the anchor search thinks it does and can never
    /// out-reach it. Mid-swing the toes droop and the sole tilts so a lifted foot dangles
    /// instead of hovering flat. <paramref name="far"/> shades the whole leg for the away side.</summary>
    private static void DrawLeg(Renderer r, Titan t, Frame f, TitanLeg leg, float thigh, float shin,
        bool mech = false, bool far = false)
    {
        var hip = t.HipWorld(leg, f.Up, f.Right);
        var groundFwd = new Vector2(-f.Up.Y, f.Up.X) * (f.Face >= 0f ? 1f : -1f);
        var swing = leg.StepT < 1f ? MathF.Sin(leg.StepT * MathF.PI) : 0f;

        // Ankle joint: lifted above and pulled back from the toe contact point.
        var foot = leg.FootPos;
        var ankle = foot + f.Up * Titan.AnkleLift - groundFwd * Titan.AnkleBack;
        var toAnkle = ankle - hip;
        var dist = toAnkle.Length();
        const float l = Titan.LegBoneLen;
        // The sim clamps anchors to reach, but a mid-swing frame can momentarily exceed it —
        // pin the drawn ankle at full extension (foot follows) so the leg never rubber-bands.
        if (dist > 2f * l)
        {
            ankle = hip + toAnkle * (2f * l / dist);
            foot = ankle - f.Up * Titan.AnkleLift + groundFwd * Titan.AnkleBack;
            toAnkle = ankle - hip;
            dist = 2f * l;
        }

        var cDark = far ? Shade(f.HideDark, 0.62f) : f.HideDark;
        var cMid = far ? Shade(f.Hide, 0.62f) : f.Hide;
        var cLite = far ? Shade(f.HideLight, 0.62f) : f.HideLight;
        var cJoint = far ? Shade(f.Chitin, 0.7f) : f.Chitin;

        // Two-bone IK hip→ankle for the knee.
        Vector2 knee;
        if (dist < 0.5f) knee = hip - f.Up * l * 0.5f;
        else
        {
            var dir = toAnkle / dist;
            var perp = new Vector2(-dir.Y, dir.X);
            // Knee bows toward the facing direction (theropod), or away from it for the
            // reverse-jointed mech — both knees agree, instead of splaying outward.
            if (Vector2.Dot(perp, groundFwd) < 0) perp = -perp;
            var half = MathF.Min(dist * 0.5f, l);
            var bend = MathF.Sqrt(MathF.Max(0f, l * l - half * half));
            knee = hip + dir * half + perp * bend * (mech ? -1f : 1f);
        }

        // Haunch — a heavy thigh mass over the hip socket so the leg visibly roots into the
        // pelvis (mostly tucked under the torso, its lower arc shows as the top of the thigh).
        r.DrawCircle(hip, thigh * 0.85f, cDark);
        // Thigh: dark bone with a lit muscle core.
        Seg(r, hip, knee, thigh, cDark);
        Seg(r, hip, knee, thigh * 0.55f, Color.Lerp(cDark, cMid, 0.5f));
        // Knee cap.
        r.DrawCircle(knee, MathF.Max(thigh, shin) * 0.55f, cDark);
        r.DrawCircle(knee, MathF.Max(thigh, shin) * 0.3f, cJoint);
        // Shin: lighter, slimmer, with a highlight edge so the lower leg pops off the thigh.
        Seg(r, knee, ankle, shin, cMid);
        Seg(r, knee, ankle, shin * 0.42f, cLite);
        // Ankle — the visible backward-bending third joint above the heel.
        r.DrawCircle(ankle, shin * 0.6f, cMid);
        r.DrawCircle(ankle, shin * 0.32f, cJoint);

        // Foot bone from the ankle down to the toes, then the sole and claws. Planted it lies
        // flat along the ground; mid-swing the toes droop below the ball so the foot dangles.
        var toe = foot + groundFwd * (shin * 1.0f * (1f - 0.3f * swing)) - f.Up * (7f * swing);
        var heel = foot - groundFwd * (shin * 0.5f) + f.Up * (5f * swing);
        Seg(r, ankle, foot, shin * 0.8f, cMid);                  // metatarsal
        Seg(r, heel, toe, shin * 0.9f, cDark);                   // sole
        r.DrawCircle(heel, shin * 0.42f, cDark);                 // heel pad
        r.DrawCircle(foot, shin * 0.62f, cMid);                  // ball of the foot
        var clawLen = mech ? 8f : 13f;
        var clawThick = mech ? 3f : 4.5f;
        for (var c = -1; c <= 1; c++)
            Seg(r, toe, toe + groundFwd * clawLen + f.Up * (c * shin * 0.34f - 4f * swing),
                clawThick, cJoint);
    }

    private static Color Shade(Color c, float m) => new(
        (int)(c.R * m), (int)(c.G * m), (int)(c.B * m));

    /// <summary>Draw the verlet spine chain (tail or serpent body). <paramref name="ridge"/>
    /// adds Godzilla dorsal spikes; <paramref name="plated"/> adds mech plating.</summary>
    private static void DrawSpineChain(Renderer r, Titan t, Frame f, float rootThick, float tipThick,
        bool ridge, bool plated = false)
    {
        var nodes = t.TailNodes;
        // Spine glow travels tail→head while the atomic breath charges.
        var chargeGlow = t.Kind == TitanKind.Godzilla && t.SpecialState > 0f
            ? MathHelper.Clamp(1f - t.SpecialState / Titan.FireBreathWindup, 0f, 1f) : 0f;
        for (var i = 1; i < nodes.Length; i++)
        {
            var fr = i / (float)(nodes.Length - 1);
            var thick = MathHelper.Lerp(rootThick, tipThick, fr);
            Seg(r, nodes[i - 1], nodes[i], thick, Color.Lerp(f.HideDark, f.Hide, 1f - fr * 0.6f));
            if (plated)
                r.DrawCircle((nodes[i] + nodes[i - 1]) * 0.5f, thick * 0.4f, f.Chitin);
            if (ridge)
            {
                // A glowing dorsal fin at each node — lights up in sequence during the charge.
                var mid = (nodes[i] + nodes[i - 1]) * 0.5f;
                var up = mid - t.Position; if (up.LengthSquared() > 1f) up.Normalize();
                var lit = chargeGlow > 0f && (1f - fr) < chargeGlow + 0.2f;
                var finCol = lit ? new Color(150, 220, 255) : f.Chitin;
                r.DrawRect(mid + up * thick * 0.7f, new Vector2(thick * 0.7f, thick * 0.9f), finCol,
                    MathF.Atan2(up.X, -up.Y));
            }
        }
        if (ridge)
        {
            r.DrawCircle(nodes[^1], 6f + f.Anger * 4f, f.Glow);
        }
    }

    private static void DrawEyes(Renderer r, Titan t, Frame f, Vector2 center, Vector2 playerPos,
        float socket, float pupil, bool mech = false)
    {
        var look = playerPos - center;
        if (look.LengthSquared() < 0.001f) look = f.Right * f.Face; else look.Normalize();
        var lr = Vector2.Dot(look, f.Right);
        var lu = Vector2.Dot(look, f.Up);
        var col = EyeColor(t.Kind, f.Anger);
        for (var e = -1; e <= 1; e += 2)
        {
            var s = center + f.Right * (e * 14f);
            if (mech)
            {
                r.DrawRect(s, new Vector2(12f, 6f), col, f.Rot);   // rectangular sensor
                r.DrawRect(s, new Vector2(5f, 3f), Color.White, f.Rot);
                continue;
            }
            r.DrawCircle(s, socket, f.Chitin);
            var p = s + f.Right * lr * 3f + f.Up * lu * 2f;
            r.DrawCircle(p, pupil, col);
            r.DrawCircle(p, pupil * 0.4f, Color.Black);
        }
    }

    /// <summary>Rotated rect between two world points — the workhorse for limbs and spines.</summary>
    private static void Seg(Renderer r, Vector2 a, Vector2 b, float thick, Color col)
    {
        var d = b - a;
        var len = d.Length();
        if (len < 0.5f) return;
        r.DrawRect((a + b) * 0.5f, new Vector2(len, thick), col, MathF.Atan2(d.Y, d.X));
    }

    /// <summary>Tapered body segment — a stack of shrinking rects from a→b.</summary>
    private static void DrawTaper(Renderer r, Vector2 a, Vector2 b, float wA, float wB, Color col, float rot)
    {
        const int n = 4;
        for (var i = 0; i <= n; i++)
        {
            var tt = i / (float)n;
            r.DrawRect(Vector2.Lerp(a, b, tt), new Vector2(MathHelper.Lerp(wA, wB, tt), (b - a).Length() / n + 4f), col, rot);
        }
    }

    // ── egg + mound ─────────────────────────────────────────────────────────────

    private static void DrawEgg(Renderer r, Titan t, Planet planet, float time)
    {
        var up = planet.UpAt(t.Position);
        var right = new Vector2(-up.Y, up.X);
        var wob = MathF.Sin(t.Pulse) * 2.5f;
        var basePos = t.Position + right * wob;
        var (occCalm, occAngry, _, _) = Palette(t.Kind);
        var shell = t.HitFlash > 0f ? Color.White : new Color(224, 214, 196);
        var shellDark = new Color(165, 154, 132);

        r.DrawCircle(basePos + up * 26f, 42f, shellDark);
        r.DrawCircle(basePos + up * 30f, 37f, shell);
        r.DrawCircle(basePos + up * 56f, 31f, shellDark);
        r.DrawCircle(basePos + up * 60f, 26f, shell);
        r.DrawCircle(basePos + up * 80f, 17f, shell);

        var occ = Color.Lerp(occCalm, occAngry, 0.4f);
        var seed = (int)(basePos.X * 0.21f) ^ (int)(basePos.Y * 0.11f);
        for (var s = 0; s < 10; s++)
        {
            var h = (seed * 1664525 + s * 1013904223) & 0x7fffffff;
            var sx = ((h >> 4) & 0x3F) - 32;
            var sy = ((h >> 12) & 0x7F) + 8;
            r.DrawCircle(basePos + right * sx * 0.7f + up * sy, 3f + (h & 3), occ);
        }

        var dmg = MathHelper.Clamp(1f - t.EggHealth / t.EggMaxHealth, 0f, 1f);
        for (var c = 0; c < (int)(dmg * 6f); c++)
        {
            var a = c * 1.7f;
            var from = basePos + up * 44f;
            var to = from + (right * MathF.Cos(a) + up * MathF.Sin(a)) * (14f + dmg * 20f);
            Seg(r, from, to, 1.6f, new Color(40, 34, 30));
        }
    }

    private static void DrawMound(Renderer r, Titan t, Planet planet)
    {
        // A heaping sand wake shoved up over the buried worm — a broad ridge with a cresting
        // hump, tinted to the worm's sandy hide so it reads as "something huge is under here".
        var up = planet.UpAt(t.Position);
        var right = new Vector2(-up.Y, up.X);
        var (calm, _, _, _) = Palette(TitanKind.Sandworm);
        var sand = calm;
        var sandDark = new Color(sand.R * 3 / 4, sand.G * 3 / 4, sand.B * 3 / 4);
        var proj = t.Position + up * 6f;   // sit the wake on the surface it's pushing up
        for (var d = -3; d <= 3; d++)
        {
            var h = (3 - MathF.Abs(d)) / 3f;
            r.DrawCircle(proj + right * (d * 18f), 12f + h * 14f, d % 2 == 0 ? sand : sandDark);
        }
        r.DrawCircle(proj + up * 10f, 20f, sand);
    }

    // ── palette + helpers ───────────────────────────────────────────────────────

    private static Vector2 HeadPos(Titan t, Frame f) => t.Kind switch
    {
        TitanKind.Sandworm    => f.Tp + f.Up * 96f,
        TitanKind.Kong        => f.Tp + f.Up * 92f + f.Right * (f.Face * 8f),
        TitanKind.Leatherback => f.Tp + f.Up * 88f + f.Right * (f.Face * 14f),
        TitanKind.Raiju       => f.Tp + f.Up * 56f + f.Right * (f.Face * 78f),
        TitanKind.Slattern    => f.Tp + f.Up * 136f + f.Right * (f.Face * 32f),
        TitanKind.Pyrodactyl or TitanKind.Vitriodactyl
                              => f.Tp + f.Up * 24f + f.Right * (f.Face * 74f),
        TitanKind.CosmicOctopus => f.Tp + f.Up * 34f,
        _                     => f.Tp + f.Up * 110f + f.Right * (f.Face * 60f),
    };

    // Kept numerically identical to Titan.Mouth() so ranged attacks spawn/aim from exactly where
    // the muzzle is drawn — otherwise the beam is drawn from the head but hits along a line cast
    // from somewhere else.
    private static Vector2 MouthPos(Titan t, Frame f) => t.Kind switch
    {
        TitanKind.Mecha => f.Tp + f.Up * 90f + f.Right * (f.Face * 78f),
        TitanKind.Pyrodactyl or TitanKind.Vitriodactyl
                        => f.Tp + f.Up * 8f + f.Right * (f.Face * 84f),
        TitanKind.CosmicOctopus => f.Tp - f.Up * 26f + f.Right * (f.Face * 30f),
        _               => f.Tp + f.Up * 106f + f.Right * (f.Face * 92f),
    };

    private static Color EyeColor(TitanKind k, float anger) => k switch
    {
        TitanKind.Mecha       => Color.Lerp(new Color(120, 230, 255), new Color(255, 90, 60), anger),
        TitanKind.Sandworm    => Color.Lerp(new Color(180, 255, 120), new Color(255, 210, 60), anger),
        TitanKind.Kong        => Color.Lerp(new Color(255, 210, 120), new Color(255, 90, 40), anger),
        TitanKind.Knifehead   => Color.Lerp(new Color(140, 220, 255), new Color(120, 255, 220), anger),
        TitanKind.Otachi      => Color.Lerp(new Color(190, 255, 90), new Color(255, 250, 120), anger),
        TitanKind.Leatherback => Color.Lerp(new Color(110, 180, 255), new Color(200, 230, 255), anger),
        TitanKind.Raiju       => Color.Lerp(new Color(160, 220, 255), new Color(255, 255, 255), anger),
        TitanKind.Slattern    => Color.Lerp(new Color(255, 190, 80), new Color(255, 100, 40), anger),
        TitanKind.Pyrodactyl  => Color.Lerp(new Color(255, 170, 70), new Color(255, 90, 30), anger),
        TitanKind.Vitriodactyl => Color.Lerp(new Color(190, 255, 90), new Color(240, 255, 130), anger),
        TitanKind.CosmicOctopus => Color.Lerp(new Color(200, 160, 255), new Color(255, 255, 255), anger),
        _                     => Color.Lerp(new Color(255, 220, 100), new Color(255, 70, 40), anger),
    };

    /// <summary>Calm→angry hide colours and spine-glow colours per variant.</summary>
    public static (Color hideCalm, Color hideAngry, Color glowCalm, Color glowAngry) Palette(TitanKind k) => k switch
    {
        TitanKind.Mecha       => (new Color(120, 128, 145), new Color(160, 130, 130), new Color(90, 200, 255), new Color(130, 235, 255)),
        TitanKind.Sandworm    => (new Color(196, 168, 126), new Color(206, 132, 78), new Color(210, 120, 60), new Color(255, 150, 70)),
        TitanKind.Kong        => (new Color(78, 60, 46), new Color(120, 80, 50), new Color(150, 110, 70), new Color(215, 150, 80)),
        // The kaiju wave — bioluminescent Pacific Rim palettes: pale carapace Knifehead,
        // acid-veined Otachi, storm-blue Leatherback, electric Raiju, molten-crested Slattern.
        TitanKind.Knifehead   => (new Color(150, 160, 170), new Color(180, 150, 140), new Color(90, 210, 255), new Color(130, 255, 230)),
        TitanKind.Otachi      => (new Color(58, 76, 50), new Color(96, 118, 44), new Color(140, 230, 60), new Color(200, 255, 90)),
        TitanKind.Leatherback => (new Color(52, 68, 74), new Color(84, 78, 106), new Color(90, 160, 255), new Color(160, 220, 255)),
        TitanKind.Raiju       => (new Color(84, 104, 134), new Color(140, 116, 190), new Color(150, 220, 255), new Color(220, 245, 255)),
        TitanKind.Slattern    => (new Color(94, 58, 44), new Color(150, 66, 38), new Color(255, 150, 50), new Color(255, 200, 80)),
        // The flyers: charcoal hide over ember innards for the lava bomber, sickly olive
        // over acid-lit membranes for its corrosive twin.
        TitanKind.Pyrodactyl  => (new Color(64, 46, 44), new Color(120, 52, 36), new Color(255, 140, 40), new Color(255, 190, 80)),
        TitanKind.Vitriodactyl => (new Color(56, 68, 44), new Color(94, 112, 40), new Color(150, 230, 60), new Color(210, 255, 110)),
        // The Starspawn: void-violet nebula hide that pales as it rages; galaxy glow.
        TitanKind.CosmicOctopus => (new Color(48, 34, 72), new Color(96, 44, 118), new Color(170, 110, 255), new Color(230, 180, 255)),
        _                     => (new Color(52, 62, 56), new Color(120, 60, 50), new Color(80, 150, 230), new Color(255, 90, 60)),
    };

    private static Color Add(Color c, int d) => new(
        Math.Clamp(c.R + d, 0, 255), Math.Clamp(c.G + d, 0, 255), Math.Clamp(c.B + d, 0, 255));
}
