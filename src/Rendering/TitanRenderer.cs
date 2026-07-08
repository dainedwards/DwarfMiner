using System;
using DwarfMiner.Entities;
using DwarfMiner.World;
using Microsoft.Xna.Framework;

namespace DwarfMiner.Rendering;

/// <summary>
/// Draws the boss — a genuinely different procedural skeleton per <see cref="TitanKind"/>
/// rather than a re-tint of one body: an upright, dorsal-finned Godzilla; an angular
/// laser-mawed Mecha; a legless three-headed Sandworm; a big-armed Kong. Also handles the egg
/// and the burrow mound. Called in world space between <c>BeginEntities</c>/<c>EndEntities</c>;
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
            case TitanKind.Godzilla: DrawGodzilla(r, t, planet, playerPos, f, time); break;
            case TitanKind.Mecha:    DrawMecha(r, t, planet, playerPos, f, time); break;
            case TitanKind.Sandworm:    DrawSandworm(r, t, planet, playerPos, f, time); break;
            case TitanKind.Kong:     DrawKong(r, t, planet, playerPos, f, time); break;
        }
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

        // Eyes.
        var head = HeadPos(t, f);
        r.AddLight(head, 20f + 18f * f.Anger, EyeColor(t.Kind, f.Anger));

        // Attack telegraphs / muzzles.
        var mouth = MouthPos(t, f);
        switch (t.Kind)
        {
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

        // Hind legs (both drawn; they read as the stance behind/around the torso).
        foreach (var leg in t.Legs)
            DrawLeg(r, t, f, leg, 26f, 20f);

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

        // Forearms — short, clawed, held forward.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 26f) + f.Up * 6f;
            var hand = shoulder + f.Right * (f.Face * 34f) - f.Up * (20f + MathF.Sin(f.Pulse + s) * 4f);
            Seg(r, shoulder, hand, 12f, f.HideDark);
            Seg(r, hand, hand + f.Right * (f.Face * 12f) - f.Up * 6f, 7f, f.Hide);
            for (var c = -1; c <= 1; c++)
                Seg(r, hand + f.Right * (f.Face * 8f), hand + f.Right * (f.Face * 16f) + f.Up * (c * 5f), 2.5f, f.Chitin);
        }

        // Neck + head, thrust forward and up.
        var neck = chest + f.Up * 18f + f.Right * (f.Face * 24f);
        var head = neck + f.Up * 14f + f.Right * (f.Face * 34f);
        DrawTaper(r, chest + f.Up * 10f, neck, 44f, 34f, f.HideDark, f.Rot);
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

        foreach (var leg in t.Legs)
            DrawLeg(r, t, f, leg, 24f, 18f, mech: true);

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
            // Arm.
            var hand = sh + f.Right * (f.Face * 20f) - f.Up * (34f + MathF.Sin(f.Pulse + s) * 4f);
            Seg(r, sh, hand, 12f, steelDark);
            r.DrawRect(hand, new Vector2(12f, 12f), steel, f.Rot);   // fist
        }

        // Head: a wedge with a single glowing visor and a mouth cannon.
        var neck = chest + f.Up * 16f + f.Right * (f.Face * 22f);
        var head = neck + f.Up * 10f + f.Right * (f.Face * 30f);
        r.DrawRect(neck, new Vector2(26f, 22f), panel, f.Rot);
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

    // ── Sandworm: legless three-headed serpent ─────────────────────────────────────

    private static void DrawSandworm(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        // Body — the long verlet chain, thick scaled segments hugging the ground.
        var nodes = t.TailNodes;
        for (var i = 1; i < nodes.Length; i++)
        {
            var fr = i / (float)(nodes.Length - 1);
            var thick = MathHelper.Lerp(34f, 9f, fr);
            Seg(r, nodes[i - 1], nodes[i], thick, Color.Lerp(f.Hide, f.HideDark, fr));
            Seg(r, nodes[i - 1], nodes[i], thick * 0.5f, Color.Lerp(f.HideLight, f.Hide, fr));
            // Dorsal scales.
            var mid = (nodes[i] + nodes[i - 1]) * 0.5f;
            r.DrawCircle(mid + planet.UpAt(mid) * thick * 0.4f, thick * 0.22f, f.Chitin);
        }

        // Three necks rise from the body's front (near Position), each swaying on its own phase.
        for (var h = -1; h <= 1; h++)
        {
            var baseP = f.Tp + f.Right * (f.Face * 6f) + f.Right * (h * 22f);
            var sway = MathF.Sin(time * 2.2f + h * 1.9f) * 26f;
            var reared = t.SpecialState <= 0f ? 1f : 0.4f;   // heads lower a touch while burrow-cooling
            var neckTop = baseP + f.Up * (96f * reared) + f.Right * (f.Face * 26f + sway);
            // Neck as a couple of tapering segments.
            var mid = Vector2.Lerp(baseP, neckTop, 0.55f) + f.Right * (sway * 0.4f);
            Seg(r, baseP, mid, 20f, f.HideDark);
            Seg(r, mid, neckTop, 15f, f.Hide);
            // Head.
            var head = neckTop + f.Right * (f.Face * 16f) + f.Up * 4f;
            r.DrawRect(head, new Vector2(34f, 26f), f.HideDark, f.Rot);
            r.DrawRect(head + f.Up * 3f, new Vector2(26f, 18f), f.Hide, f.Rot);
            var snout = head + f.Right * (f.Face * 18f) - f.Up * 2f;
            r.DrawRect(snout, new Vector2(20f, 12f), f.HideDark, f.Rot);
            // Jaw + fangs.
            r.DrawRect(snout - f.Up * 8f, new Vector2(18f, 5f), f.Chitin, f.Rot);
            // Glowing serpent eyes.
            var eyeCol = EyeColor(t.Kind, f.Anger);
            for (var e = -1; e <= 1; e += 2)
                r.DrawCircle(head + f.Right * (f.Face * 4f) + f.Up * 4f + f.Right * (e * 6f), 3.2f, eyeCol);
        }
    }

    // ── Kong: big-armed ape ─────────────────────────────────────────────────────

    private static void DrawKong(Renderer r, Titan t, Planet planet, Vector2 playerPos, Frame f, float time)
    {
        var fur = f.Hide;
        var furDark = f.HideDark;
        var stone = new Color(90, 88, 96);

        // Legs (short, stout).
        foreach (var leg in t.Legs)
            DrawLeg(r, t, f, leg, 30f, 24f);

        // Broad torso.
        var breath = MathF.Sin(f.Pulse) * 2f;
        var chest = f.Tp + f.Up * (70f + breath);
        DrawTaper(r, f.Tp + f.Up * 16f, chest, 120f, 104f, furDark, f.Rot);
        DrawTaper(r, f.Tp + f.Up * 24f, chest, 100f, 84f, fur, f.Rot);
        // Stone plates over the shoulders/back.
        for (var s = -1; s <= 1; s += 2)
            r.DrawRect(chest + f.Right * (s * 44f) + f.Up * 6f, new Vector2(34f, 22f), stone, f.Rot + s * 0.2f);
        r.DrawRect(chest - f.Right * (f.Face * 40f) + f.Up * 4f, new Vector2(30f, 40f), stone, f.Rot);

        // Massive arms: raised overhead mid-leap, planted like columns otherwise.
        for (var s = -1; s <= 1; s += 2)
        {
            var shoulder = chest + f.Right * (s * 54f) + f.Up * 10f;
            Vector2 fist;
            if (t.Leaping)
                fist = shoulder + f.Up * 60f + f.Right * (f.Face * 18f);          // reaching to slam
            else
                fist = shoulder + f.Right * (s * 10f) - f.Up * (66f + MathF.Sin(f.Pulse + s) * 5f);  // knuckle stance
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

    // ── shared primitives ───────────────────────────────────────────────────────

    /// <summary>2-bone leg with knee, foot pad + claws; stretches when the foot is far. Shared
    /// by all legged variants (thicker/rocky for Kong, plated for Mecha).</summary>
    private static void DrawLeg(Renderer r, Titan t, Frame f, TitanLeg leg, float thigh, float shin, bool mech = false)
    {
        var hip = f.Tp + f.Right * leg.HipForward + f.Up * leg.HipUp;
        var foot = leg.FootPos;
        var toFoot = foot - hip;
        var dist = toFoot.Length();
        const float l = 78f;
        Vector2 knee;
        if (dist < 0.5f) knee = hip - f.Up * 40f;
        else if (dist >= 2 * l) knee = hip + toFoot * 0.5f;
        else
        {
            var dir = toFoot / dist;
            var perp = new Vector2(-dir.Y, dir.X);
            if (Vector2.Dot(perp, f.Right * leg.Side) < 0) perp = -perp;
            var half = dist * 0.5f;
            var bend = MathF.Sqrt(MathF.Max(0f, l * l - half * half));
            // Digitigrade (knee kicks back) for the mech; plantigrade otherwise.
            knee = hip + dir * half + perp * bend * (mech ? -1f : 1f);
        }
        Seg(r, hip, knee, thigh, f.HideDark);
        Seg(r, knee, foot, shin, f.Hide);
        r.DrawCircle(knee, thigh * 0.5f, f.Chitin);
        r.DrawCircle(foot, shin * 0.7f, f.Chitin);
        var tangent = new Vector2(-f.Up.Y, f.Up.X) * leg.Side;
        for (var c = -1; c <= 1; c++)
            Seg(r, foot, foot + (tangent + f.Up * (c * 0.5f - 0.3f)) * 14f, 4f, f.Chitin);
    }

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
        var up = planet.UpAt(t.Position);
        r.DrawCircle(t.Position, 17f, new Color(92, 70, 50));
        r.DrawCircle(t.Position + up * 4f, 11f, new Color(116, 88, 62));
    }

    // ── palette + helpers ───────────────────────────────────────────────────────

    private static Vector2 HeadPos(Titan t, Frame f) => t.Kind switch
    {
        TitanKind.Sandworm => f.Tp + f.Up * 96f,
        TitanKind.Kong  => f.Tp + f.Up * 92f + f.Right * (f.Face * 8f),
        _               => f.Tp + f.Up * 110f + f.Right * (f.Face * 60f),
    };

    private static Vector2 MouthPos(Titan t, Frame f) => t.Kind switch
    {
        TitanKind.Mecha => f.Tp + f.Up * 90f + f.Right * (f.Face * 78f),
        _               => f.Tp + f.Up * 106f + f.Right * (f.Face * 92f),
    };

    private static Color EyeColor(TitanKind k, float anger) => k switch
    {
        TitanKind.Mecha => Color.Lerp(new Color(120, 230, 255), new Color(255, 90, 60), anger),
        TitanKind.Sandworm => Color.Lerp(new Color(180, 255, 120), new Color(255, 210, 60), anger),
        TitanKind.Kong  => Color.Lerp(new Color(255, 210, 120), new Color(255, 90, 40), anger),
        _               => Color.Lerp(new Color(255, 220, 100), new Color(255, 70, 40), anger),
    };

    /// <summary>Calm→angry hide colours and spine-glow colours per variant.</summary>
    public static (Color hideCalm, Color hideAngry, Color glowCalm, Color glowAngry) Palette(TitanKind k) => k switch
    {
        TitanKind.Mecha => (new Color(120, 128, 145), new Color(160, 130, 130), new Color(90, 200, 255), new Color(130, 235, 255)),
        TitanKind.Sandworm => (new Color(38, 82, 58), new Color(96, 122, 52), new Color(120, 220, 140), new Color(185, 240, 90)),
        TitanKind.Kong  => (new Color(78, 60, 46), new Color(120, 80, 50), new Color(150, 110, 70), new Color(215, 150, 80)),
        _               => (new Color(52, 62, 56), new Color(120, 60, 50), new Color(80, 150, 230), new Color(255, 90, 60)),
    };

    private static Color Add(Color c, int d) => new(
        Math.Clamp(c.R + d, 0, 255), Math.Clamp(c.G + d, 0, 255), Math.Clamp(c.B + d, 0, 255));
}
