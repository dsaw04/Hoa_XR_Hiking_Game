using UnityEngine;

/// Procedurally generated audio for the trail torches.
///
/// The project ships no imported audio (there is not a single .wav/.ogg outside
/// the Oculus SDK), and the same code-first rule that produced UIShapes applies
/// here: a torch needs an ignition whoosh and a fire loop, so both are
/// synthesised once at runtime and shared by every torch.
///
/// Both clips are built from a fixed seed, so a given build always sounds the
/// same — same reasoning as WalkingTrackGenerator's `new System.Random(2025)`.
/// Clips are mono and short; the pair costs well under 500 KB of memory, which
/// is far cheaper than shipping compressed assets we would then have to decode
/// on the Quest's audio thread.
public static class TorchAudio
{
    const int SampleRate = 44100;

    static AudioClip s_ignite;
    static AudioClip s_crackle;

    /// One-shot "fwoomph" as the torch catches. Filtered noise under a fast
    /// attack / exponential decay envelope, with the lowpass cutoff sweeping
    /// open and then closed again — that sweep is what reads as air catching
    /// rather than as a burst of static.
    public static AudioClip Ignite()
    {
        // AudioClip is a UnityEngine.Object, so the ??= operator would sidestep
        // the overloaded null check and hand back a destroyed clip.
        if (s_ignite != null) return s_ignite;

        const float duration = 0.90f;
        int   count = Mathf.RoundToInt(SampleRate * duration);
        var   data  = new float[count];
        var   rng   = new System.Random(7301);
        float lp    = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float u = t / duration;

            // Fast attack (~17 ms) then exponential tail.
            float env = Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 4.5f);

            // Cutoff opens over the first third, then closes — the "whoosh".
            float sweep = Mathf.Sin(Mathf.Clamp01(u * 1.5f) * Mathf.PI);
            float k     = Mathf.Lerp(0.020f, 0.320f, sweep);

            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp += (noise - lp) * k;

            // A little low sine adds body so it does not sound thin on the
            // Quest's speakers, which roll off hard below ~150 Hz.
            float body = Mathf.Sin(t * Mathf.PI * 2f * 90f) * 0.25f * Mathf.Exp(-t * 7f);

            data[i] = Mathf.Clamp(lp * 1.6f + body, -1f, 1f) * env * 0.85f;
        }

        s_ignite = AudioClip.Create("TorchIgnite", count, 1, SampleRate, false);
        s_ignite.SetData(data, 0);
        return s_ignite;
    }

    /// Seamless fire-loop bed: quiet filtered noise with occasional sharp pops.
    /// The tail is crossfaded into the head so the loop point is inaudible —
    /// without that, the discontinuity clicks once per cycle, which is very
    /// obvious on a sound the player stands next to.
    public static AudioClip Crackle()
    {
        if (s_crackle != null) return s_crackle;

        const float duration = 2.20f;
        int   count = Mathf.RoundToInt(SampleRate * duration);
        var   data  = new float[count];
        var   rng   = new System.Random(9145);
        float lp    = 0f;

        // Sustained bed.
        for (int i = 0; i < count; i++)
        {
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            lp += (noise - lp) * 0.045f;      // heavy lowpass → a soft roar
            data[i] = lp * 2.2f * 0.16f;
        }

        // Sputters and pops on top, each a short decaying transient.
        int pops = 26;
        for (int p = 0; p < pops; p++)
        {
            int   start  = rng.Next(0, count);
            float amp    = 0.10f + (float)rng.NextDouble() * 0.22f;
            float decay  = 220f + (float)rng.NextDouble() * 500f;
            int   length = Mathf.RoundToInt(SampleRate * 0.05f);

            for (int i = 0; i < length; i++)
            {
                int idx = start + i;
                if (idx >= count) break;      // crossfade below wraps the tail
                float t = (float)i / SampleRate;
                float e = Mathf.Exp(-t * decay);
                data[idx] += (float)(rng.NextDouble() * 2.0 - 1.0) * amp * e;
            }
        }

        // Crossfade the last `fade` samples over the first `fade` so the join
        // is continuous in both value and slope.
        int fadeLen = Mathf.RoundToInt(SampleRate * 0.12f);
        for (int i = 0; i < fadeLen; i++)
        {
            float w   = (float)i / fadeLen;            // 0 → 1 across the fade
            int   tail = count - fadeLen + i;
            data[i] = Mathf.Lerp(data[tail], data[i], w);
        }
        System.Array.Resize(ref data, count - fadeLen);

        s_crackle = AudioClip.Create("TorchCrackle", data.Length, 1, SampleRate, false);
        s_crackle.SetData(data, 0);
        return s_crackle;
    }

    /// The gameplay scene has no AudioListener (it was built around a silent
    /// prototype), so spatial audio would be inaudible until one exists. Called
    /// by TorchProgressionManager during setup.
    public static void EnsureListener(Transform head)
    {
        if (head == null) return;
        if (Object.FindAnyObjectByType<AudioListener>() != null) return;
        head.gameObject.AddComponent<AudioListener>();
    }
}
