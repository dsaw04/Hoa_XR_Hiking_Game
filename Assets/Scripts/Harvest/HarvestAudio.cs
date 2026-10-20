using UnityEngine;

/// Procedurally generated audio for the harvest interval.
///
/// Same rule as TorchAudio, and for the same reasons: the project ships no
/// imported audio, and a short synthesised clip is cheaper than shipping a
/// compressed asset the Quest's audio thread then has to decode.
///
/// ── WHY THERE IS A TICK AT ALL ───────────────────────────────────────────────
/// The harvest deliberately has no backing music, so without a hit sound the
/// interval is silent — and silence reads as unresponsiveness, not as calm. The
/// tick is pitched up with the combo streak, which makes the rising pitch *be*
/// the combo meter: audible, needs no display, and rewards flow directly. There
/// is deliberately no miss sound; at a 35% miss rate that would nag the player
/// fifteen times in thirty seconds, and the absence of the tick already says it.
public static class HarvestAudio
{
    const int SampleRate = 44100;

    static AudioClip s_tick;
    static AudioClip s_pulse;
    static AudioClip s_secured;

    /// Short woodblock-ish blip: a fast sine with a touch of noise under a very
    /// sharp decay. Kept dry and organic rather than electronic so it sits in an
    /// alpine soundscape instead of on top of it.
    public static AudioClip Tick()
    {
        if (s_tick != null) return s_tick;

        const float duration = 0.075f;
        int   count = Mathf.RoundToInt(SampleRate * duration);
        var   data  = new float[count];
        var   rng   = new System.Random(4417);

        for (int i = 0; i < count; i++)
        {
            float t   = (float)i / SampleRate;
            float env = Mathf.Exp(-t * 64f);
            float tone = Mathf.Sin(2f * Mathf.PI * 880f * t) * 0.7f
                       + Mathf.Sin(2f * Mathf.PI * 1320f * t) * 0.2f;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.12f;
            data[i] = (tone + noise) * env * 0.5f;
        }

        s_tick = AudioClip.Create("HarvestTick", count, 1, SampleRate, false);
        s_tick.SetData(data, 0);
        return s_tick;
    }

    /// Soft low pulse marking a beat during the lead-in, so the tempo is
    /// established before it is tested. A player who has not internalised the
    /// tempo misses the whole first phrase, which is a real slice of a 65%
    /// budget spent on not knowing what tempo they are at.
    public static AudioClip Pulse()
    {
        if (s_pulse != null) return s_pulse;

        const float duration = 0.16f;
        int count = Mathf.RoundToInt(SampleRate * duration);
        var data  = new float[count];

        for (int i = 0; i < count; i++)
        {
            float t    = (float)i / SampleRate;
            float env  = Mathf.Exp(-t * 22f);
            // Pitch drops slightly across the hit — reads as a struck body
            // rather than a beep.
            float freq = Mathf.Lerp(190f, 138f, Mathf.Clamp01(t * 12f));
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.42f;
        }

        s_pulse = AudioClip.Create("HarvestPulse", count, 1, SampleRate, false);
        s_pulse.SetData(data, 0);
        return s_pulse;
    }

    /// The moment the pass threshold is crossed. This is the one event in the
    /// interval that deserves punctuation — it turns a generous but invisible
    /// rule into something the player feels, and everything after it is upside.
    public static AudioClip Secured()
    {
        if (s_secured != null) return s_secured;

        const float duration = 0.55f;
        int count = Mathf.RoundToInt(SampleRate * duration);
        var data  = new float[count];

        for (int i = 0; i < count; i++)
        {
            float t   = (float)i / SampleRate;
            float env = Mathf.Exp(-t * 5.5f) * Mathf.Clamp01(t * 90f);
            // A rising open fifth: unmistakably "got it" without a fanfare.
            float a = Mathf.Sin(2f * Mathf.PI * 523.25f * t);
            float b = Mathf.Sin(2f * Mathf.PI * 783.99f * t) * Mathf.Clamp01((t - 0.06f) * 14f);
            data[i] = (a * 0.5f + b * 0.5f) * env * 0.4f;
        }

        s_secured = AudioClip.Create("HarvestSecured", count, 1, SampleRate, false);
        s_secured.SetData(data, 0);
        return s_secured;
    }
}
