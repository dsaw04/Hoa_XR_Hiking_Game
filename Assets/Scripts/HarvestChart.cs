using System;
using System.Collections.Generic;

/// Which punch a note asks for. The three are chosen because they recruit
/// different muscle groups — a set built from all three cannot be worked
/// through with one arm motion, which is the point of the exercise.
public enum PunchType { Straight, Hook, Uppercut }

/// One note in a chart: when it should be struck, by which hand, with which
/// punch, and where in the 3x3 lane grid it sits.
///
/// Slots are indices, not metres. The grid's physical extent scales with
/// MovementMode.ReachScale, but its topology never changes — so a Gentle-mode
/// player gets the same choreography compressed into a smaller volume.
public struct ChartNote
{
    /// Beats from the start of the note stream (not from the lead-in).
    public int       Beat;
    public bool      IsLeft;
    public PunchType Punch;
    /// 0 = player's left, 1 = centre, 2 = player's right.
    public int       SlotX;
    /// 0 = low, 1 = middle, 2 = high.
    public int       SlotY;
}

/// Builds a note chart for one harvest interval.
///
/// ── WHY A PHRASE LIBRARY AND NOT A RANDOM GENERATOR ──────────────────────────
/// Rolling position and direction independently produces notes nobody can hit:
/// a right hook far right, then a right uppercut low left half a beat later is
/// the same hand covering ~1.2 m in 250 ms. That does not read as "hard", it
/// reads as broken. Preventing it in a random generator means a reachability
/// check on every candidate against the previous note for that hand — fiddly to
/// tune, and wrong in ways that only surface in playtest. A phrase library
/// encodes feasibility once, by hand, and *cannot* emit an invalid pair inside a
/// phrase; the seam check below only has to cover phrase boundaries.
///
/// The second reason is that combos are the fitness content. Real combinations
/// (jab–cross, jab–jab–hook, cross–hook–uppercut) exist because they chain
/// biomechanically — each punch's recovery loads the next one's rotation. That
/// chaining is what turns arm movement into coordinated torso motion, and it is
/// also what makes the game learnable: after ten harvests a player starts
/// recognising phrases and anticipating them. Note salad never does that.
///
/// ── DELIBERATELY UNITY-FREE ──────────────────────────────────────────────────
/// No UnityEngine types anywhere in this file. The sequencer is the most
/// bug-prone piece of the harvest design, and keeping it pure means it compiles
/// and runs standalone, without the editor in the loop.
public static class HarvestChart
{
    /// Slot conventions the phrases below obey, so a note is always something a
    /// body can actually be in position for:
    ///   • Left hand never reaches past centre (SlotX 0-1); right never past it (1-2).
    ///   • Hooks sit wide on their own hand's side — the arm swings inward
    ///     *through* the note, so it starts further out than the note is.
    ///   • Uppercuts are always low (SlotY 0). Nothing else is.
    ///   • Straights sit at middle or high (SlotY 1-2).
    sealed class Phrase
    {
        public readonly string      Name;
        /// Length in beats, including the trailing beat of its last note.
        public readonly int         Beats;
        /// 0 = sparse and simple, 2 = dense. Filtered against occupancy.
        public readonly int         Tier;
        public readonly ChartNote[] Notes;

        public Phrase(string name, int beats, int tier, ChartNote[] notes)
        {
            Name = name; Beats = beats; Tier = tier; Notes = notes;
        }

        public ChartNote First => Notes[0];
        public ChartNote Last  => Notes[Notes.Length - 1];
    }

    static ChartNote N(int beat, bool left, PunchType punch, int x, int y) =>
        new ChartNote { Beat = beat, IsLeft = left, Punch = punch, SlotX = x, SlotY = y };

    const bool L = true, R = false;

    static readonly Phrase[] k_phrases =
    {
        new Phrase("Single cross", 2, 0, new[]
        {
            N(0, R, PunchType.Straight, 1, 2),
        }),

        new Phrase("Jab-cross", 2, 0, new[]
        {
            N(0, L, PunchType.Straight, 1, 2),
            N(1, R, PunchType.Straight, 1, 2),
        }),

        new Phrase("Double jab", 2, 0, new[]
        {
            N(0, L, PunchType.Straight, 0, 2),
            N(1, L, PunchType.Straight, 1, 2),
        }),

        new Phrase("Body-head", 2, 0, new[]
        {
            N(0, L, PunchType.Uppercut, 0, 0),
            N(1, R, PunchType.Straight, 1, 2),
        }),

        new Phrase("Hook pair", 2, 1, new[]
        {
            N(0, L, PunchType.Hook, 0, 1),
            N(1, R, PunchType.Hook, 2, 1),
        }),

        new Phrase("Uppercut pair", 2, 1, new[]
        {
            N(0, L, PunchType.Uppercut, 0, 0),
            N(1, R, PunchType.Uppercut, 2, 0),
        }),

        new Phrase("Jab-cross-hook", 3, 1, new[]
        {
            N(0, L, PunchType.Straight, 1, 2),
            N(1, R, PunchType.Straight, 1, 2),
            N(2, L, PunchType.Hook,     0, 1),
        }),

        new Phrase("Cross-hook-uppercut", 3, 1, new[]
        {
            N(0, R, PunchType.Straight, 1, 2),
            N(1, L, PunchType.Hook,     0, 1),
            N(2, R, PunchType.Uppercut, 2, 0),
        }),

        new Phrase("Uppercut ladder", 3, 1, new[]
        {
            N(0, L, PunchType.Uppercut, 0, 0),
            N(1, R, PunchType.Uppercut, 2, 0),
            N(2, L, PunchType.Straight, 1, 2),
        }),

        new Phrase("Flurry", 4, 2, new[]
        {
            N(0, L, PunchType.Straight, 1, 2),
            N(1, R, PunchType.Straight, 1, 2),
            N(2, L, PunchType.Hook,     0, 2),
            N(3, R, PunchType.Uppercut, 2, 0),
        }),

        // The cross sits at middle height rather than high so the right hand's
        // travel into the following hook is one slot, not two: at 120 BPM it has
        // half a second to get there. Reads as a body cross into a hook, which
        // is a real combination anyway.
        new Phrase("Jab-jab-cross-hook", 4, 2, new[]
        {
            N(0, L, PunchType.Straight, 0, 2),
            N(1, L, PunchType.Straight, 1, 2),
            N(2, R, PunchType.Straight, 1, 1),
            N(3, R, PunchType.Hook,     2, 1),
        }),
    };

    /// Builds the chart for one interval.
    ///
    /// `seed` comes from the plant's identity rather than the clock, matching
    /// TorchAudio's fixed seed and WalkingTrackGenerator's `new Random(2025)`.
    /// The payoff is that retrying a failed harvest on the same plant deals the
    /// same chart, so the player gets to learn the phrase they fluffed instead
    /// of being handed a fresh random one — and auto-play test runs reproduce.
    ///
    /// `occupancy` is the fraction of beat slots that carry a note (roughly);
    /// it is the density dial driven by MovementIntensity.
    public static List<ChartNote> Build(int seed, float durationSeconds,
                                        float beatSeconds, float occupancy)
    {
        var notes = new List<ChartNote>();
        if (durationSeconds <= 0f || beatSeconds <= 0f) return notes;

        occupancy = Clamp(occupancy, 0.15f, 1f);
        int totalBeats = (int)(durationSeconds / beatSeconds);
        if (totalBeats <= 0) return notes;

        var rng = new Random(seed);

        Phrase previous = null;
        bool   havePrev = false;
        ChartNote prevNote = default;
        int    prevNoteBeat = int.MinValue;
        int    cursor = 0;

        while (cursor < totalBeats)
        {
            Phrase p = Pick(rng, previous, havePrev, prevNote, occupancy);

            // ── Seam reachability ────────────────────────────────────────────
            // Inside a phrase, transitions are hand-authored and safe. Across a
            // boundary they are not: if the same hand has to cross more than one
            // slot, it needs two beats (a full second at 120 BPM) to get there,
            // or the note is one no arm can reach and reads as a bug rather than
            // as difficulty.
            //
            // Note this pushes the cursor until the gap is actually big enough.
            // Advancing by a single beat is not sufficient — from a gap of zero
            // that still lands one beat short.
            if (havePrev && p.First.IsLeft == prevNote.IsLeft)
            {
                int span = Math.Abs(p.First.SlotX - prevNote.SlotX)
                         + Math.Abs(p.First.SlotY - prevNote.SlotY);

                if (span > 1)
                {
                    int shortfall = 2 - ((cursor + p.First.Beat) - prevNoteBeat);
                    if (shortfall > 0) cursor += shortfall;
                }
            }

            foreach (var n in p.Notes)
            {
                int beat = cursor + n.Beat;
                if (beat >= totalBeats) continue;

                var placed = n;
                placed.Beat = beat;
                notes.Add(placed);

                prevNote     = placed;
                prevNoteBeat = beat;
                havePrev     = true;
            }

            cursor  += p.Beats + Gap(p, occupancy);
            previous = p;
        }

        return notes;
    }

    /// Rest beats inserted after a phrase so the chart lands near the requested
    /// occupancy. A phrase is (near enough) one note per beat, so the gap that
    /// dilutes it to `occupancy` is beats * (1/occupancy - 1).
    static int Gap(Phrase p, float occupancy)
    {
        int gap = (int)Math.Round(p.Beats * (1.0 / occupancy - 1.0));
        return gap < 0 ? 0 : gap > 8 ? 8 : gap;
    }

    static Phrase Pick(Random rng, Phrase previous, bool havePrev,
                       ChartNote prevNote, float occupancy)
    {
        // Dense charts drop the sparsest phrases and sparse charts drop the
        // busiest, so occupancy changes what the interval *feels* like and not
        // only how much silence sits between phrases.
        int maxTier = occupancy < 0.6f ? 1 : 2;
        int minTier = occupancy > 0.85f ? 1 : 0;

        var pool      = new List<Phrase>();
        var alternate = new List<Phrase>();

        foreach (var p in k_phrases)
        {
            if (p.Tier < minTier || p.Tier > maxTier) continue;
            if (p == previous) continue;                 // never twice in a row
            pool.Add(p);

            // HikingPlayerController already pays an altBonus for alternating
            // hands and a sameArmPenalty for repeating, so locomotion has taught
            // the player that alternating is correct. The choreography should
            // agree with that rather than contradict it.
            if (havePrev && p.First.IsLeft != prevNote.IsLeft) alternate.Add(p);
        }

        if (alternate.Count > 0) return alternate[rng.Next(alternate.Count)];
        if (pool.Count > 0)      return pool[rng.Next(pool.Count)];
        return k_phrases[rng.Next(k_phrases.Length)];
    }

    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    /// Stable per-plant seed. Uses the name and position rather than the
    /// instance so the same bush deals the same chart across a scene reload.
    public static int SeedFrom(string name, float x, float z)
    {
        unchecked
        {
            int h = 17;
            if (!string.IsNullOrEmpty(name))
                foreach (char c in name) h = h * 31 + c;
            h = h * 31 + (int)(x * 16f);
            h = h * 31 + (int)(z * 16f);
            return h;
        }
    }
}
