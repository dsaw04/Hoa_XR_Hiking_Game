using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// How a finished harvest went. Passed to the caller so it can bank the sample
/// (or not) and show the player what they scored.
public readonly struct HarvestResult
{
    public readonly NatureInteractable Plant;
    public readonly bool  Passed;
    public readonly int   Hit;
    public readonly int   Spawned;
    public readonly int   Total;
    public readonly int   BestCombo;
    /// Hits over notes spawned, 0-1.
    public readonly float Accuracy;

    public HarvestResult(NatureInteractable plant, bool passed, int hit, int spawned,
                         int total, int bestCombo)
    {
        Plant     = plant;
        Passed    = passed;
        Hit       = hit;
        Spawned   = spawned;
        Total     = total;
        BestCombo = bestCombo;
        Accuracy  = spawned > 0 ? (float)hit / spawned : 0f;
    }

    /// Provisional tiers — the design doc lists these boundaries as an open
    /// item, to be set once the interval has been felt in a headset.
    public string Rank =>
        !Passed         ? "—"      :
        Accuracy >= 0.93f ? "Gold"   :
        Accuracy >= 0.80f ? "Silver" :
                            "Bronze";
}

/// The HIIT work interval that gathers one plant sample.
///
/// A stream of directional punch notes travels from the plant to a strike plane
/// just in front of the player, on a metronomic beat grid, for a fixed duration.
/// Clear 65% of them and the sample banks; fall short and the plant survives for
/// another attempt. Only one harvest runs at a time (`Active`).
///
/// ── WHY A TIMED INTERVAL AND NOT A SET OF ORBS ──────────────────────────────
/// This game is an exergame, and its objectives are meant to read as HIIT work
/// intervals. A count-based set is not an interval: it lets the player control
/// the duration, so "do four orbs" can be three seconds or three minutes. A
/// fixed duration puts the clock on the game's side and makes the player's only
/// variable how hard they work inside it. It is also what finally gives
/// MovementModeManager.SessionMinutes something to mean.
///
/// ── WHY NOTES APPROACH RATHER THAN SITTING STILL ────────────────────────────
/// This replaces an earlier version that spawned a static ring of orbs. A target
/// that already exists at a fixed point in space rewards the *smallest* motion
/// that reaches it — MovementTarget only checks proximity plus a speed floor, so
/// a Gentle-mode player could clear the old ring with six-inch twitches. A note
/// travelling toward you has to be intercepted: you extend into it or it passes.
/// The exercise ends up enforced by the geometry instead of by a threshold that
/// can be gamed.
///
/// ── WHAT SCALES WITH WHAT ───────────────────────────────────────────────────
/// MovementMode is the *shape* of each rep (how far you reach, how hard you have
/// to punch); MovementIntensity is the *volume* of reps (how dense the chart,
/// how long the interval). This deliberately reverses the previous version,
/// which used EffortScale for orb count and left Intensity doing nothing — the
/// reason Gentle mode used to read as a shorter game rather than a lighter one.
/// Note travel speed is fixed for everybody: reaction time is vision and
/// cognition, not fitness, and scaling it would hand a player who asked for big
/// arm movements a twitch test they never asked for.
public class PlantHarvest : MonoBehaviour
{
    public enum Phase { LeadIn, Notes, Settling, Done }

    /// The harvest currently in progress, or null. Read by NatureInfoUI (to
    /// suppress the proximity prompt) and DesktopHikeSimulator.
    public static PlantHarvest Active { get; private set; }

    // ── Timing ───────────────────────────────────────────────────────────────
    /// 120 BPM. Notes land on quarter notes at most — one punch per beat is
    /// already brisk to hold for forty-five seconds.
    public  const float BeatSeconds    = 0.5f;
    /// Long enough to fade the lane in, establish the tempo, and let the player
    /// set their stance. Also the free-abort window: see Cancel().
    const float LeadInSeconds  = 4f;
    const float SettleSeconds  = 0.6f;
    const float LingerSeconds  = 0.6f;
    /// Fade back up from the stance blink. Long enough not to read as a glitch,
    /// short enough that it is over well inside the lead-in.
    const float BlinkSeconds   = 0.28f;

    // ── Lane ─────────────────────────────────────────────────────────────────
    /// Distance from the head to the spawn plane, which is where the plant is.
    ///
    /// This is a *requirement on where the player stands*, not a description of
    /// where they happen to be. A harvest can only be started from inside
    /// NatureInfoUI.showDistance (3 m), so taking it as a description — which
    /// this constant's original comment did — put the spawn plane behind the
    /// plant on every single harvest, and every note flew out through a canopy.
    /// Setup now moves the player to satisfy it; see the stance block there.
    const float LaneLength     = 3.5f;   // m from the head to the plant
    const float StrikeDistance = 0.5f;   // m from the head
    const float NoteSpeed      = 4.3f;   // m/s, fixed for every player
    const float LaneCentreDrop = 0.35f;  // lane mid-height below eye level
    const float BaseHalfWidth  = 0.42f;  // × ReachScale
    const float BaseHalfHeight = 0.38f;  // × ReachScale
    const float AbortDrift     = 2.0f;   // m of horizontal drift from the anchor

    // ── Notes ────────────────────────────────────────────────────────────────
    const float NoteDiameter   = 0.20f;
    const float HitRadius      = 0.22f;
    const float BaseHitSpeed   = 1.1f;   // matches HikingPlayerController.minHitSpeed

    /// Generous on purpose. The interval is the workout; the threshold exists to
    /// stop someone standing still, not to demand precision. It is measured
    /// against notes *spawned*, never an absolute count, so a Gentle player
    /// hitting 70% of 20 notes and an Energy Boost player hitting 70% of 90 both
    /// pass — everyone graded against their own prescription.
    public const float PassThreshold = 0.65f;

    /// Seconds of note travel. Notes are spawned this far *ahead* of their beat
    /// so they ARRIVE on it. Spawning on the beat instead is the classic
    /// rhythm-game mistake: every note lands late by its flight time and the
    /// whole thing feels unresponsive.
    const float FlightSeconds = (LaneLength - StrikeDistance) / NoteSpeed;

    // ── State ────────────────────────────────────────────────────────────────
    public NatureInteractable Plant { get; private set; }
    public Phase CurrentPhase { get; private set; } = Phase.LeadIn;
    public int   Total     { get; private set; }
    public int   Spawned   { get; private set; }
    public int   Hit       { get; private set; }
    public int   Combo     { get; private set; }
    public int   BestCombo { get; private set; }
    public bool  Secured   { get; private set; }
    /// True once the player is past the free-abort window.
    public bool  Committed { get; private set; }

    public float Accuracy01   => Spawned > 0 ? (float)Hit / Spawned : 0f;
    /// 0-1 toward the pass threshold, which is what the plant's glow shows.
    public float Progress01   => hitsToSecure > 0 ? Mathf.Clamp01((float)Hit / hitsToSecure) : 0f;
    public float SecondsLeft  => Mathf.Max(0f, endTime - elapsed);

    Transform head;
    OVRCameraRig rig;
    System.Action<HarvestResult> onFinished;

    List<ChartNote> chart;
    readonly List<HarvestNote> live = new List<HarvestNote>();

    Vector3 anchor;        // head position when the interval started
    Vector3 toPlant;       // unit, horizontal, player → plant
    Vector3 laneDir;       // unit, horizontal, plant → player
    Vector3 laneRight;     // unit, horizontal, the player's right
    Vector3 strikeOrigin;
    float   halfWidth, halfHeight, requiredSpeed;

    float elapsed;
    float endTime;
    int   nextNote;
    int   hitsToSecure;
    int   lastPulseBeat = -1;
    bool  finished;

    AudioSource audioSource;
    Light       glow;
    HarvestMist mist;
    /// True between blacking the screen for the stance blink and finishing the
    /// fade back up, so teardown can tell our own black screen from somebody
    /// else's and only clear the one it put there.
    bool        blinking;
    LineRenderer ring;
    Material    ringMat;
    // Fixed once per harvest (see BuildFeedback): the lane is hard-locked to
    // the head position when the interval started, so the ring's centre never
    // moves and UpdateFeedback has no reason to recompute it every frame.
    Vector3     ringCentre;
    float       ringY;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// Starts a harvest, cancelling any other in progress. Returns null when the
    /// rig has no hand anchors to punch with, so callers can fall back to simply
    /// identifying the plant.
    public static PlantHarvest Begin(NatureInteractable plant, Transform head, OVRCameraRig rig,
                                     System.Action<HarvestResult> onFinished)
    {
        if (plant == null || head == null || rig == null) return null;
        if (rig.leftHandAnchor == null && rig.rightHandAnchor == null) return null;
        if (Active != null) Active.Cancel();

        var go = new GameObject("PlantHarvest");
        go.transform.position = plant.transform.position;

        var h = go.AddComponent<PlantHarvest>();
        h.Plant      = plant;
        h.head       = head;
        h.rig        = rig;
        h.onFinished = onFinished;

        if (!h.Setup())
        {
            Destroy(go);
            return null;
        }

        Active = h;
        return h;
    }

    bool Setup()
    {
        var mm      = MovementModeManager.Instance;
        float reach  = mm != null ? mm.ReachScale  : 1f;
        float effort = mm != null ? mm.EffortScale : 1f;
        var intensity = mm != null ? mm.Intensity : MovementIntensity.Medium;

        halfWidth     = BaseHalfWidth  * reach;
        halfHeight    = BaseHalfHeight * reach;
        requiredSpeed = BaseHitSpeed   * effort;

        // ── Chart ────────────────────────────────────────────────────────────
        // Built before anything with a side effect, because it is the only step
        // that can fail. Everything below this either moves the player or puts
        // geometry in the world, and a half-applied harvest that then returned
        // false would leave them standing somewhere they did not ask to be.
        float duration  = DurationFor(intensity);
        float occupancy = OccupancyFor(intensity);
        int   seed      = HarvestChart.SeedFrom(Plant.name,
                                                Plant.transform.position.x,
                                                Plant.transform.position.z);

        chart = HarvestChart.Build(seed, duration, BeatSeconds, occupancy);
        if (chart == null || chart.Count == 0) return false;

        Total        = chart.Count;
        hitsToSecure = Mathf.Max(1, Mathf.CeilToInt(Total * PassThreshold));

        float lastArrival = LeadInSeconds + chart[chart.Count - 1].Beat * BeatSeconds;
        endTime = lastArrival + SettleSeconds;

        TakeStance();

        // ── Lane frame, struck once and then hard-locked ─────────────────────
        // The lane does not follow the head. You are harvesting *that* plant, so
        // facing it is the correct posture and the instinct to face what you are
        // punching does the work — no nagging, and no mid-interval wandering.
        // TakeStance is what makes that instinct pay off instead of punishing
        // it: it leaves the plant at the far end of a corridor with nothing in
        // it, which is where the head is already turned.
        anchor = head.position;

        Vector3 flat = Plant.transform.position - anchor;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f)
            flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;

        toPlant   = flat.normalized;
        laneDir   = -toPlant;
        laneRight = Vector3.Cross(Vector3.up, toPlant).normalized;

        strikeOrigin = anchor + toPlant * StrikeDistance;

        BuildFeedback();
        return true;
    }

    /// Steps the player back into their own note lane, behind a blink.
    ///
    /// Two things are wrong with harvesting from wherever the player happens to
    /// be standing when they press. The plant is inside the corridor rather
    /// than at the end of it (see LaneLength), and whatever else the open world
    /// has put in that direction — a pohutukawa, the side of the crater — is in
    /// there with it. HarvestClearance answers both at once: it picks the
    /// nearest bearing whose corridor is actually empty, and a stance one lane
    /// length back along it.
    ///
    /// ── WHY A BLINK AND NOT A WALK ──────────────────────────────────────────
    /// Asking the player to punch-walk backwards onto a mark would be more
    /// diegetic, but it means moving backwards in VR, which is where the worst
    /// of the discomfort lives, and it invents a failure state ("you did not
    /// reach the mark in time") for a game trying hard to be approachable. A
    /// blink costs neither. What makes a teleport uncomfortable is *watching*
    /// yourself translate, so the screen goes black on the frame of the move and
    /// fades back up — nobody sees the translation, and what is on the other
    /// side of it is the same world, two steps further from the same plant.
    ///
    /// Momentum is zeroed as part of the warp. A player who punch-ran up to a
    /// plant would otherwise keep drifting forward into their own lane for a
    /// second or so, and the first phrase would feel like it was rushing them.
    void TakeStance()
    {
        var controller = rig.GetComponent<HikingPlayerController>();
        if (controller == null) return;

        // Half a note has to clear the corridor wall, plus a little slack, or a
        // trunk grazing the edge still eats the notes in the outer slots.
        float corridorRadius = halfWidth + NoteDiameter * 0.5f + 0.15f;

        HarvestClearance.FindStance(Plant.transform.position, head.position, LaneLength,
                                    corridorRadius, LaneCentreDrop, halfHeight,
                                    out Vector3 stance, out _);

        var fade = OVRScreenFade.instance;
        if (fade != null) fade.SetExplicitFade(1f);

        controller.WarpHeadTo(stance);

        if (fade == null) return;
        blinking = true;
        StartCoroutine(BlinkUp(fade));
    }

    IEnumerator BlinkUp(OVRScreenFade fade)
    {
        for (float t = 0f; t < BlinkSeconds; t += Time.deltaTime)
        {
            if (fade == null) yield break;
            fade.SetExplicitFade(1f - t / BlinkSeconds);
            yield return null;
        }
        if (fade != null) fade.SetExplicitFade(0f);
        blinking = false;
    }

    /// Creates the screen fade ahead of time so the first harvest's blink
    /// actually blinks.
    ///
    /// Called from NatureInfoUI.Start alongside the clearance index warm-up,
    /// because both are one-off costs that have no business landing on the frame
    /// the player presses — and because OVRScreenFade builds its material in
    /// Start, which has not run yet for a component added mid-frame. Without
    /// this the first stance blink would be skipped, and the player would watch
    /// themselves slide backwards exactly once per session.
    public static void PrepareBlink(MonoBehaviour host, Transform head)
    {
        if (OVRScreenFade.instance != null) return;

        var cam = head != null ? head.GetComponent<Camera>() : null;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        if (cam.GetComponent<OVRScreenFade>() != null) return;

        // Its Start builds a material from this shader; if an Android build has
        // stripped it, adding the component would log an error every frame
        // instead of quietly doing nothing, and we would rather lose the blink.
        if (Shader.Find("Oculus/Unlit Transparent Color") == null) return;

        var fade = cam.gameObject.AddComponent<OVRScreenFade>();
        fade.fadeOnStart = false;

        if (host != null) host.StartCoroutine(ClearFadeQuad(fade));
    }

    /// Switches the fade quad off the frame after OVRScreenFade builds it.
    ///
    /// OVRScreenFade.Start adds a MeshRenderer and a full-view quad, but binds
    /// the material and sets renderer.enabled only inside SetMaterialAlpha —
    /// which nothing calls when fadeOnStart is off. The renderer is therefore
    /// left enabled with a null material, and an unmaterialed renderer draws
    /// with Unity's magenta error shader: a purple rectangle across the whole
    /// view, from scene load until the first blink happens to fix it.
    ///
    /// One explicit zero-fade binds the material and disables the renderer,
    /// which is the state the component should have started in. Deferred a
    /// frame because SetMaterialAlpha no-ops while fadeMaterial is still null,
    /// and the component we just added has not run its Start yet.
    static IEnumerator ClearFadeQuad(OVRScreenFade fade)
    {
        yield return null;
        if (fade != null) fade.SetExplicitFade(0f);
    }

    /// Interval length is the volume dial, so it comes from intensity — not from
    /// the movement mode, which sets the shape of each rep instead.
    static float DurationFor(MovementIntensity i) => i switch
    {
        MovementIntensity.Low  => 20f,
        MovementIntensity.High => 45f,
        _                      => 30f,
    };

    /// Fraction of beat slots carrying a note. Quantising to the beat grid is
    /// what stops a dense chart producing unreadable overlapping notes: two
    /// notes cannot be 40 ms apart because the grid has no slot there.
    static float OccupancyFor(MovementIntensity i) => i switch
    {
        MovementIntensity.Low  => 0.50f,
        MovementIntensity.High => 1.00f,
        _                      => 0.75f,
    };

    // ── Diegetic feedback ────────────────────────────────────────────────────

    /// No HUD and no text during the interval. The reason is not only occlusion
    /// (NatureInfoUI already found that the card sits in the punch corridor) —
    /// it is that text is unreadable to someone working at intensity. A player
    /// half a minute into a max-effort interval cannot parse "62% · 14/22 · 0:12";
    /// they can absolutely parse the plant glowing green and the ring running
    /// out. Every gym display that works is a colour and a bar.
    void BuildFeedback()
    {
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake  = false;
        audioSource.spatialBlend = 0f;    // these are feedback, not world sound
        audioSource.volume       = 0.85f;

        var lightGo = new GameObject("HarvestGlow");
        lightGo.transform.SetParent(transform, worldPositionStays: false);
        lightGo.transform.position = Plant.transform.position + Vector3.up * 0.6f;

        glow = lightGo.AddComponent<Light>();
        glow.type      = LightType.Point;
        glow.range     = 7f;
        glow.shadows   = LightShadows.None;
        glow.intensity = 0f;
        glow.color     = UnsecuredColor;

        // A ring on the ground under the lane, depleting as the clock runs. Sits
        // below the note field so it never occludes anything the player is
        // trying to read, and reads peripherally without being looked at.
        var ringGo = new GameObject("HarvestTimerRing");
        ringGo.transform.SetParent(transform, worldPositionStays: false);

        ringMat = PunchOrb.UnlitMaterial(UnsecuredColor);
        ring = ringGo.AddComponent<LineRenderer>();
        ring.material        = ringMat;
        ring.useWorldSpace   = true;
        ring.loop            = false;
        ring.widthMultiplier = 0.025f;
        ring.numCornerVertices = 2;
        ring.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        ring.receiveShadows    = false;
        ring.positionCount     = 0;

        ringCentre = anchor + toPlant * ((LaneLength + StrikeDistance) * 0.5f);
        ringY      = anchor.y - 1.35f;

        // The clearing closes in behind the plant for the length of the
        // interval. HarvestClearance has already guaranteed the corridor is
        // empty; this is about what is standing eight metres past the end of
        // it, because a hillside of NZ bush is a busy, high-contrast background
        // to read a 20 cm target against. Raised over the lead-in rather than
        // popped, so it reads as weather and not as a loading screen.
        mist = HarvestMist.Create(anchor, Plant.transform.position, LeadInSeconds);
    }

    static readonly Color UnsecuredColor = new Color(0.95f, 0.72f, 0.25f);
    static readonly Color SecuredColor   = new Color(0.36f, 0.90f, 0.52f);

    const int RingSegments = 72;
    const float RingRadius = 0.85f;

    // Unit-circle offsets for the ring, computed once for the process lifetime
    // — RingSegments/RingRadius never change, so there is no reason to call
    // Cos/Sin 72 times a frame for a shape that never rotates. UpdateFeedback
    // just adds the (fixed, per-harvest) centre and writes into the shared
    // buffer below, then pushes it in a single SetPositions call instead of
    // up to 72 individual SetPosition interop calls.
    static readonly Vector3[] s_ringOffsets = BuildRingOffsets();
    static readonly Vector3[] s_ringBuffer  = new Vector3[RingSegments];

    static Vector3[] BuildRingOffsets()
    {
        var offsets = new Vector3[RingSegments];
        for (int i = 0; i < RingSegments; i++)
        {
            float a = (i / (float)RingSegments) * Mathf.PI * 2f;
            offsets[i] = new Vector3(Mathf.Cos(a) * RingRadius, 0f, Mathf.Sin(a) * RingRadius);
        }
        return offsets;
    }

    void UpdateFeedback()
    {
        Color tint = Secured
            ? SecuredColor
            : Color.Lerp(new Color(0.55f, 0.42f, 0.20f), UnsecuredColor, Progress01);

        if (glow != null)
        {
            float baseIntensity;
            if (CurrentPhase == Phase.LeadIn)
            {
                // Pulse on the beat so the tempo is established before it is
                // tested rather than discovered halfway through the first phrase.
                float intoBeat = Mathf.Repeat(elapsed, BeatSeconds) / BeatSeconds;
                baseIntensity = Mathf.Lerp(2.4f, 0.5f, intoBeat);
            }
            else
            {
                baseIntensity = 1.1f + Progress01 * 2.6f + (Secured ? 1.4f : 0f);
            }

            glow.intensity = baseIntensity;
            glow.color     = tint;
        }

        if (ring == null) return;

        float remaining01 = CurrentPhase == Phase.LeadIn
            ? 1f
            : Mathf.Clamp01((endTime - elapsed) / Mathf.Max(0.01f, endTime - LeadInSeconds));

        int count = Mathf.Clamp(Mathf.CeilToInt(RingSegments * remaining01), 0, RingSegments);
        if (count != ring.positionCount) ring.positionCount = count;
        if (count == 0) return;

        for (int i = 0; i < count; i++)
        {
            Vector3 o = s_ringOffsets[i];
            s_ringBuffer[i] = new Vector3(ringCentre.x + o.x, ringY, ringCentre.z + o.z);
        }
        ring.SetPositions(s_ringBuffer);

        PunchOrb.SetColor(ringMat, tint);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    void Update()
    {
        if (finished) return;

        if (Plant == null || head == null) { Cancel(); return; }

        elapsed += Time.deltaTime;

        if (CurrentPhase == Phase.LeadIn && elapsed >= LeadInSeconds)
        {
            CurrentPhase = Phase.Notes;
            Committed    = true;
        }

        LeadInPulse();
        SpawnDueNotes();
        UpdateFeedback();

#if UNITY_EDITOR
        AutoPlayStep();
#endif

        // Drifting out of the lane abandons the attempt. Horizontal only —
        // terrain slope moves the player's head without them meaning to walk
        // away, and being failed by a hillside would be nonsense.
        Vector3 drift = head.position - anchor;
        drift.y = 0f;
        if (drift.sqrMagnitude > AbortDrift * AbortDrift) { Cancel(); return; }

        if (elapsed >= endTime && nextNote >= chart.Count && live.Count == 0)
            Finish(Hit >= hitsToSecure);
        else if (elapsed >= endTime + 1.5f)
            Finish(Hit >= hitsToSecure);   // safety net: never hang on a stuck note
    }

    void LeadInPulse()
    {
        if (CurrentPhase != Phase.LeadIn) return;

        int beat = Mathf.FloorToInt(elapsed / BeatSeconds);
        if (beat == lastPulseBeat) return;

        lastPulseBeat = beat;
        if (audioSource != null) audioSource.PlayOneShot(HarvestAudio.Pulse(), 0.5f);
    }

    void SpawnDueNotes()
    {
        while (nextNote < chart.Count)
        {
            var spec = chart[nextNote];
            float spawnAt = LeadInSeconds + spec.Beat * BeatSeconds - FlightSeconds;
            if (elapsed < spawnAt) break;

            SpawnNote(spec);
            nextNote++;
        }
    }

    void SpawnNote(ChartNote spec)
    {
        Transform hand = spec.IsLeft ? rig.leftHandAnchor : rig.rightHandAnchor;
        if (hand == null) hand = spec.IsLeft ? rig.rightHandAnchor : rig.leftHandAnchor;
        if (hand == null) hand = head;

        var note = HarvestNote.Create(spec, SlotPosition(spec), NoteDiameter);
        note.transform.SetParent(transform, worldPositionStays: true);
        note.Configure(laneDir, strikeOrigin, NoteSpeed, hand, rig.trackingSpace, head,
                       requiredSpeed, HitRadius);
        note.OnResolved = OnNoteResolved;

        live.Add(note);
        Spawned++;
    }

    /// Slot indices to metres. The grid's topology is fixed at 3x3 and only its
    /// extent scales, so every player gets the same choreography — a Gentle
    /// player just gets it compressed into a smaller volume.
    Vector3 SlotPosition(ChartNote spec)
    {
        float x = (spec.SlotX - 1) * halfWidth;
        float y = (spec.SlotY - 1) * halfHeight;

        Vector3 horizontal = anchor + toPlant * LaneLength + laneRight * x;
        return new Vector3(horizontal.x, anchor.y - LaneCentreDrop + y, horizontal.z);
    }

    void OnNoteResolved(HarvestNote note, HarvestNote.Resolution how)
    {
        live.Remove(note);

        if (how != HarvestNote.Resolution.Hit)
        {
            Combo = 0;
            return;
        }

        Hit++;
        Combo++;
        if (Combo > BestCombo) BestCombo = Combo;

        if (audioSource != null)
        {
            // Pitch rises with the streak, which makes the rising pitch the
            // combo meter — audible, and it needs no display.
            audioSource.pitch = Mathf.Min(1f + (Combo - 1) * 0.045f, 1.7f);
            audioSource.PlayOneShot(HarvestAudio.Tick(), 0.7f);
            audioSource.pitch = 1f;
        }

        Buzz(note.IsLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch, 0.35f, 0.06f);

        if (!Secured && Hit >= hitsToSecure) Secure();
    }

    /// The one moment in the interval that deserves punctuation: it turns a
    /// generous but invisible rule into something the player feels, and tells
    /// them everything from here is upside.
    void Secure()
    {
        Secured = true;
        if (audioSource != null) audioSource.PlayOneShot(HarvestAudio.Secured(), 0.9f);
        Buzz(OVRInput.Controller.LTouch, 0.5f, 0.14f);
        Buzz(OVRInput.Controller.RTouch, 0.5f, 0.14f);
    }

    // ── Ending ───────────────────────────────────────────────────────────────

    /// Abandons the attempt. Free during the lead-in; a failed attempt after it.
    ///
    /// Abort cannot be free throughout, because that bypasses the retry rest
    /// entirely: start, look at the first phrase, quit if it looks hard, restart.
    /// Making abandonment cost exactly what failing costs removes the incentive
    /// without inventing a new punishment — quitting and failing are the same
    /// outcome, so there is nothing to game. The free lead-in exists for the
    /// opposite reason: someone who pressed by accident, or who is facing the
    /// wrong way, has to be able to back out. A commit window, not a trap.
    public void Cancel()
    {
        if (Committed) Finish(passed: false);
        else           Finish(passed: false, report: false);
    }

    void Finish(bool passed, bool report = true)
    {
        if (finished) return;
        finished = true;

        if (Active == this) Active = null;
        CurrentPhase = Phase.Done;

        foreach (var note in live)
            if (note != null) note.Discard();
        live.Clear();

        if (ring != null) ring.positionCount = 0;
        if (glow != null) glow.intensity = 0f;

        // Thinned out whether the interval passed, failed or was abandoned: the
        // curtain is a reading aid, not a reward, and leaving it up to mark a
        // result would make the world itself part of the scoring.
        if (mist != null) { mist.Dismiss(); mist = null; }

        // Belt and braces: an interval can end mid-buzz, and a controller left
        // vibrating is the worst possible bug to ship to a headset.
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
        OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);

        var result   = new HarvestResult(Plant, passed, Hit, Spawned, Total, BestCombo);
        var callback = onFinished;
        onFinished = null;

        // Deliberately NOT `enabled = false` here. Disabling a MonoBehaviour
        // suspends its coroutines, so the delayed-destroy trick would strand any
        // in-flight haptic pulse before it got to switch the motor off. The
        // `finished` flag at the top of Update does the same job safely.
        Destroy(gameObject, LingerSeconds);

        if (report) callback?.Invoke(result);
    }

    void Buzz(OVRInput.Controller ctrl, float amplitude, float seconds)
        => StartCoroutine(BuzzRoutine(ctrl, amplitude, seconds));

    IEnumerator BuzzRoutine(OVRInput.Controller ctrl, float amplitude, float seconds)
    {
        OVRInput.SetControllerVibration(0.4f, amplitude, ctrl);
        yield return new WaitForSecondsRealtime(seconds);
        OVRInput.SetControllerVibration(0f, 0f, ctrl);
    }

    void OnDestroy()
    {
        if (Active == this) Active = null;
        if (ringMat != null) Destroy(ringMat);

        // The curtain outlives this object by its own fade, so it is not
        // parented here and has to be told to go if we are torn down without
        // Finish ever running — a scene change mid-interval, say.
        if (mist != null) mist.Dismiss();

        // Same reasoning as the vibration reset in Finish: torn down mid-blink
        // there is nothing left running to fade the screen back up, and the
        // player is left staring at black. Guarded on our own flag so this
        // cannot clear a fade some other system is in the middle of.
        if (blinking && OVRScreenFade.instance != null)
            OVRScreenFade.instance.SetExplicitFade(0f);
    }

#if UNITY_EDITOR
    // ── Headset-free testing ─────────────────────────────────────────────────
    //
    // Game feel is unverifiable on a keyboard — whether a hook reads, whether
    // 4.3 m/s is too fast, whether the cone is too tight, all need arms. What is
    // verifiable is the state machine, and that is where the bugs are: does a
    // pass bank and a fail not, does the retry rest start, does the distance
    // gate clear, does aborting after the first note cost an attempt. Auto-play
    // exercises all of it deterministically, because the chart is seeded from
    // the plant rather than the clock.

    /// Fraction of notes auto-play lands, or negative to leave notes alone.
    internal static float AutoPlayRate = -1f;
    static System.Random s_autoRng = new System.Random(90210);

    void AutoPlayStep()
    {
        if (AutoPlayRate < 0f) return;

        // Resolve just before the strike plane, so timing behaves the way a real
        // punch would rather than notes vanishing at spawn.
        float window = NoteSpeed * 0.12f;

        for (int i = live.Count - 1; i >= 0; i--)
        {
            var note = live[i];
            if (note == null || note.AutoDecided) continue;
            if (note.DistanceToPlane > window) continue;

            note.AutoDecided = true;
            if (s_autoRng.NextDouble() < AutoPlayRate) note.SimulateResolve(hit: true);
            // Otherwise leave it: it crosses the plane on its own and counts as
            // a timing miss, which is the more common real failure.
        }
    }

    /// Manual keyboard fallback: resolves the nearest live note for that hand as
    /// a clean hit. It cannot express direction, so it tests chart pacing and
    /// scoring rather than the punch itself — auto-play is the real harness.
    internal void SimulatePunch(bool isLeft, float speed)
    {
        HarvestNote best = null;
        float bestDist = float.MaxValue;

        foreach (var note in live)
        {
            if (note == null || note.Resolved || note.IsLeft != isLeft) continue;
            if (note.DistanceToPlane < bestDist) { bestDist = note.DistanceToPlane; best = note; }
        }

        best?.SimulateResolve(hit: true);
    }
#endif
}
