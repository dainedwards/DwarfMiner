using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace DwarfMiner.Systems;

/// <summary>
/// Procedural sound — no asset files. Each effect is synthesized into a 16-bit PCM buffer at
/// load (swept oscillators + filtered noise + envelopes) and wrapped in a MonoGame
/// <see cref="SoundEffect"/>. Everything audio-facing is wrapped in try/catch and gated on
/// <c>_ok</c>, so a machine with no audio device (or a headless run) simply plays nothing
/// instead of crashing. The <see cref="Synth"/> buffer generation is static and device-free,
/// so it's unit-testable in the headless sim test.
/// </summary>
public sealed class Sfx
{
    public const int Rate = 22050;

    private readonly Dictionary<string, SoundEffect> _fx = new();
    private readonly Dictionary<string, double> _lastPlay = new();
    private double _now;
    private bool _ok;

    /// <summary>Master multiplier on every play; 0 mutes. DM_MUTE=1 forces silence.</summary>
    public float Master = 0.55f;
    public bool Muted;

    /// <summary>All effect names — also the set the sim test sweeps.</summary>
    public static readonly string[] Names =
    {
        "dig", "break", "pickup", "shoot", "explode", "hurt", "collapse", "hatch", "ui", "launch",
        "creak",
        // Per-weapon shot voices — each gun/thrown weapon picks one via ItemDef.ShotSound;
        // anything without one falls back to the generic "shoot" pew above.
        "shoot_pistol", "shoot_mg", "shoot_laser", "shoot_beam", "shoot_rocket", "shoot_cannon",
        "throw", "harpoon",
        // Mothership: the ion-engine rumble is a short loopable burst re-triggered while
        // thrusting (min-gap keeps it continuous without stacking).
        "thrust",
    };

    public void Build()
    {
        Muted = Environment.GetEnvironmentVariable("DM_MUTE") is { Length: > 0 };
        try
        {
            foreach (var name in Names)
            {
                var samples = Synth(name);
                var bytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                _fx[name] = new SoundEffect(bytes, Rate, AudioChannels.Mono);
            }
            _ok = true;
        }
        catch
        {
            _ok = false;   // no audio device — run silent
        }
    }

    /// <summary>Advance the internal clock used for the per-effect min-gap throttle.</summary>
    public void Tick(float dt) => _now += dt;

    /// <summary>Play an effect. <paramref name="minGap"/> throttles rapid repeats of the same
    /// name (mining/pickup spam). Volume/pitch/pan are clamped to the SoundEffect ranges.</summary>
    public void Play(string name, float volume = 1f, float pitch = 0f, float pan = 0f, float minGap = 0f)
    {
        if (!_ok || Muted) return;
        var vol = MathHelper.Clamp(volume * Master, 0f, 1f);
        if (vol <= 0.002f) return;
        if (minGap > 0f && _lastPlay.TryGetValue(name, out var t) && _now - t < minGap) return;
        _lastPlay[name] = _now;
        try { _fx[name].Play(vol, MathHelper.Clamp(pitch, -1f, 1f), MathHelper.Clamp(pan, -1f, 1f)); }
        catch { /* transient audio hiccup — drop the sound, never crash */ }
    }

    // ── synthesis (static, device-free) ─────────────────────────────────────────

    /// <summary>Build the 16-bit PCM buffer for an effect name. Percussive game sounds: short
    /// swept tones and noise bursts shaped by exponential-decay envelopes, mixed and clipped.</summary>
    public static short[] Synth(string name)
    {
        switch (name)
        {
            case "dig":
                // Dry granular tick — filtered noise, very short.
                return Render(0.06, b => Noise(b, 0.5, Decay(60), lp: 0.35));
            case "break":
                // Rock shatter — noise crack over a low thump.
                return Render(0.16, b =>
                {
                    Noise(b, 0.6, Decay(26), lp: 0.6);
                    Tone(b, 150, 70, 0.5, Decay(22), Wave.Sine);
                });
            case "pickup":
                // Bright two-step rising blip.
                return Render(0.11, b =>
                {
                    Tone(b, 680, 680, 0.35, Ar(0.01, 40), Wave.Square, until: 0.05);
                    Tone(b, 1040, 1040, 0.32, Ar(0.01, 40), Wave.Square, from: 0.05);
                });
            case "shoot":
                // Snappy pew — falling square with a noise transient.
                return Render(0.09, b =>
                {
                    Tone(b, 440, 130, 0.4, Decay(34), Wave.Square);
                    Noise(b, 0.25, Decay(80), lp: 0.5);
                });
            case "shoot_pistol":
                // Sharp high crack — tighter and brighter than the default pew.
                return Render(0.10, b =>
                {
                    Tone(b, 620, 160, 0.45, Decay(40), Wave.Square);
                    Noise(b, 0.35, Decay(90), lp: 0.35);
                });
            case "shoot_mg":
                // Dry punchy tap — the per-round voice of a fast repeater.
                return Render(0.07, b =>
                {
                    Tone(b, 300, 110, 0.4, Decay(55), Wave.Saw);
                    Noise(b, 0.5, Decay(115), lp: 0.55);
                });
            case "shoot_laser":
                // Zap — a fast falling square with a shimmering sine overtone.
                return Render(0.14, b =>
                {
                    Tone(b, 1500, 260, 0.4, Decay(26), Wave.Square);
                    Tone(b, 2200, 500, 0.15, Decay(30), Wave.Sine);
                });
            case "shoot_beam":
                // Heavy sustained beam — a deep saw sweep with a growling low body.
                return Render(0.28, b =>
                {
                    Tone(b, 700, 120, 0.5, Decay(11), Wave.Saw);
                    Noise(b, 0.25, Decay(9), lp: 0.7);
                    Tone(b, 90, 60, 0.4, Decay(8), Wave.Sine);
                });
            case "shoot_rocket":
                // Ignition whoosh — swelling noise over a falling exhaust rumble.
                return Render(0.4, b =>
                {
                    Noise(b, 0.6, Ar(0.05, 8), lp: 0.75);
                    Tone(b, 200, 90, 0.4, Decay(7), Wave.Saw);
                });
            case "shoot_cannon":
                // Big deep report — a low boom under a broadband crack.
                return Render(0.35, b =>
                {
                    Tone(b, 160, 45, 0.7, Decay(12), Wave.Sine);
                    Noise(b, 0.55, Decay(16), lp: 0.8);
                });
            case "throw":
                // Light airy toss — a rising-then-falling whoosh, no percussive crack.
                return Render(0.16, b =>
                {
                    Noise(b, 0.4, Ar(0.04, 16), lp: 0.6);
                    Tone(b, 260, 420, 0.2, Decay(14), Wave.Sine);
                });
            case "harpoon":
                // Mechanical twang — metallic square ping over a thunk and a tiny hiss.
                return Render(0.16, b =>
                {
                    Tone(b, 900, 300, 0.4, Decay(30), Wave.Square);
                    Tone(b, 180, 90, 0.4, Decay(24), Wave.Sine);
                    Noise(b, 0.2, Decay(70), lp: 0.3);
                });
            case "explode":
                // Boom — long noise tail over a deep falling sine.
                return Render(0.5, b =>
                {
                    Noise(b, 0.85, Decay(7), lp: 0.85);
                    Tone(b, 90, 38, 0.7, Decay(6), Wave.Sine);
                });
            case "hurt":
                // Pained descending buzz.
                return Render(0.18, b => Tone(b, 320, 120, 0.5, Decay(16), Wave.Saw));
            case "collapse":
                // Low rumble — heavily low-passed noise with a slow tail.
                return Render(0.7, b =>
                {
                    Noise(b, 0.8, Decay(4.5), lp: 0.94);
                    Tone(b, 60, 42, 0.5, Decay(4), Wave.Sine);
                });
            case "creak":
                // Groaning rock strain — a wavering low tone that warns of imminent collapse,
                // sounded the moment a region is condemned (before the "collapse" boom).
                return Render(0.55, b =>
                {
                    Tone(b, 74, 52, 0.5, Ar(0.06, 3.2), Wave.Saw);
                    Tone(b, 150, 118, 0.22, Ar(0.06, 3.2), Wave.Sine);
                    Noise(b, 0.16, Ar(0.05, 2.6), lp: 0.92);
                });
            case "hatch":
                // Rising monster growl — swept saw + snarling noise.
                return Render(0.7, b =>
                {
                    Tone(b, 130, 420, 0.6, Ar(0.08, 4), Wave.Saw);
                    Noise(b, 0.35, Ar(0.1, 4), lp: 0.8);
                });
            case "ui":
                // Soft confirm blip.
                return Render(0.06, b => Tone(b, 880, 940, 0.3, Decay(45), Wave.Sine));
            case "launch":
                // Sustained rising roar for liftoff.
                return Render(1.3, b =>
                {
                    Noise(b, 0.7, Ar(0.2, 1.4), lp: 0.9);
                    Tone(b, 90, 260, 0.5, Ar(0.2, 1.2), Wave.Saw);
                });
            default:
                return Render(0.05, b => Tone(b, 440, 440, 0.3, Decay(40), Wave.Sine));
        }
    }

    private enum Wave { Sine, Square, Saw }

    /// <summary>Allocate a float working buffer, let the recipe fill it, then clip to 16-bit.</summary>
    private static short[] Render(double seconds, Action<float[]> recipe)
    {
        var buf = new float[Math.Max(1, (int)(seconds * Rate))];
        recipe(buf);
        var outp = new short[buf.Length];
        for (var i = 0; i < buf.Length; i++)
            outp[i] = (short)(MathHelper.Clamp(buf[i], -1f, 1f) * 32000f);
        return outp;
    }

    /// <summary>Additively mix a swept oscillator into the buffer over an optional sub-window
    /// [from, until] (fractions of the buffer).</summary>
    private static void Tone(float[] buf, double f0, double f1, double amp, Func<double, double> env,
        Wave wave, double from = 0.0, double until = 1.0)
    {
        var n = buf.Length;
        var i0 = (int)(from * n);
        var i1 = (int)(until * n);
        double phase = 0;
        for (var i = i0; i < i1 && i < n; i++)
        {
            var tt = (i - i0) / (double)Rate;
            var span = (i1 - i0) / (double)n;
            var k = span > 0 ? (i - i0) / (double)(i1 - i0) : 0;
            var freq = f0 + (f1 - f0) * k;
            phase += freq / Rate;
            var ph = phase - Math.Floor(phase);
            double s = wave switch
            {
                Wave.Sine => Math.Sin(ph * 2 * Math.PI),
                Wave.Square => ph < 0.5 ? 1 : -1,
                _ => 2 * ph - 1,   // saw
            };
            buf[i] += (float)(s * amp * env(tt));
        }
    }

    /// <summary>Additively mix one-pole low-passed white noise. <paramref name="lp"/> 0 = raw,
    /// →1 = darker.</summary>
    private static void Noise(float[] buf, double amp, Func<double, double> env, double lp)
    {
        var rng = new Random(12345);
        double prev = 0;
        for (var i = 0; i < buf.Length; i++)
        {
            var tt = i / (double)Rate;
            var white = rng.NextDouble() * 2 - 1;
            prev = prev * lp + white * (1 - lp);
            buf[i] += (float)(prev * amp * env(tt));
        }
    }

    /// <summary>Exponential decay envelope, k per second.</summary>
    private static Func<double, double> Decay(double k) => t => Math.Exp(-k * t);

    /// <summary>Linear attack over <paramref name="a"/> seconds, then exponential release.</summary>
    private static Func<double, double> Ar(double a, double k) =>
        t => t < a ? t / a : Math.Exp(-k * (t - a));
}
