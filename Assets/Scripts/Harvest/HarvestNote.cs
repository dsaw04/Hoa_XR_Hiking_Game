using System.Collections;
using UnityEngine;

/// A single punch note in a plant harvest.
///
/// Spawns at the plant end of the lane, travels toward the player at a fixed
/// speed, and resolves when a hand intercepts it or when it crosses the strike
/// plane untouched.
///
/// ── WHY THIS IS NOT MovementTarget ───────────────────────────────────────────
/// A locomotion orb lives forever, is punched hundreds of times, follows the
/// head, and only ever looks at how *fast* a hand is moving. A note lives 0.7 s,
/// resolves once, travels on its own, and has to know which *way* the hand is
/// moving. `cooldown` is load-bearing for one and meaningless for the other.
/// Punch locomotion is the game — if it regresses nothing else is playable — so
/// none of this lives in the class the locomotion orbs run through.
///
/// ── VELOCITY IS IN TRACKING SPACE ────────────────────────────────────────────
/// OVRInput.GetLocalControllerVelocity returns a vector in tracking space, not
/// world space. MovementTarget only ever takes .magnitude, which is
/// rotation-invariant, so nobody has had to care. The moment direction matters
/// the vector must be pushed through rig.trackingSpace or every punch is
/// validated against a rotated frame — a failure that looks like broken hit
/// detection and is very hard to trace.
public class HarvestNote : MonoBehaviour
{
    public enum Resolution
    {
        /// Right hand, right direction, hard enough.
        Hit,
        /// Right hand, hard enough, wrong way. Consumed.
        WrongDirection,
        /// Crossed the strike plane untouched.
        Passed,
    }

    // ── Tuning ───────────────────────────────────────────────────────────────

    /// Half-angle of the cone a punch has to land inside.
    ///
    /// The design doc argued for 60°, on the grounds that this is exercise and
    /// not a precision instrument. 45° is used instead for one concrete reason:
    /// the required directions for Straight (forward) and Uppercut (up) are 90°
    /// apart, and two 60° cones whose axes are 90° apart *overlap* — a single
    /// diagonal up-and-forward punch would satisfy both. Since roughly two
    /// thirds of chart notes are straights and uppercuts, that hands the player
    /// a way to clear ~66% of an interval with one repeated motion, which is
    /// over the 65% pass threshold and deletes the entire reason for having
    /// three punch types. At 45° the two cones are disjoint and the hole closes.
    /// Still generous: you have to punch roughly the right way, not precisely.
    public const float ConeHalfAngle = 45f;

    static readonly Color LeftColor  = new Color(0.25f, 0.80f, 0.90f);   // cyan
    static readonly Color RightColor = new Color(0.94f, 0.58f, 0.23f);   // orange
    static readonly Color ArrowColor = new Color(0.97f, 0.98f, 0.98f);
    static readonly Color WrongColor = new Color(0.90f, 0.28f, 0.20f);

    /// Left/right are separated by luminance as well as hue. Relying on colour
    /// alone for hand assignment is the most-cited accessibility complaint about
    /// Beat Saber, and this project's whole Body-Adaptive premise is that it
    /// does not do that to people. Silhouette and arrow carry the same
    /// information independently.

    public ChartNote Spec { get; private set; }
    public bool      IsLeft => Spec.IsLeft;
    public bool      Resolved { get; private set; }

    /// Fired once, with how the note ended.
    public System.Action<HarvestNote, Resolution> OnResolved;

    // ── Lane frame ───────────────────────────────────────────────────────────
    Vector3 laneDir;        // unit, plant → player: the direction of travel
    Vector3 strikeOrigin;   // a point on the strike plane
    Vector3 punchForward;   // unit, horizontal, player → plant
    Vector3 punchRight;     // unit, horizontal, the player's right
    float   speed;

    // ── Hit test ─────────────────────────────────────────────────────────────
    Transform hand;
    Transform trackingSpace;
    Transform head;
    OVRInput.Controller controller;
    float minHitSpeed;
    float hitRadius;

    // ── Visuals ──────────────────────────────────────────────────────────────
    Transform body, arrow;
    Material  bodyMat, arrowMat;
    float     diameter;

    /// True once auto-play has decided this note's fate, so it is only rolled
    /// for once. Editor-only concern, but the flag is harmless at runtime.
    internal bool AutoDecided;

    public static HarvestNote Create(ChartNote spec, Vector3 spawnPos, float noteDiameter)
    {
        var go = new GameObject($"Note_{(spec.IsLeft ? "L" : "R")}_{spec.Punch}");
        go.transform.position = spawnPos;

        var n = go.AddComponent<HarvestNote>();
        n.Spec     = spec;
        n.diameter = noteDiameter;
        n.BuildVisuals();
        return n;
    }

    /// Everything the note needs to move and to judge a punch. Split from
    /// Create so the argument list stays readable.
    public void Configure(Vector3 laneDirection, Vector3 strikePlanePoint, float travelSpeed,
                          Transform handAnchor, Transform rigTrackingSpace, Transform headAnchor,
                          float requiredSpeed, float radius)
    {
        laneDir       = laneDirection.normalized;
        strikeOrigin  = strikePlanePoint;
        speed         = travelSpeed;
        hand          = handAnchor;
        trackingSpace = rigTrackingSpace;
        head          = headAnchor;
        minHitSpeed   = requiredSpeed;
        hitRadius     = radius;
        controller    = Spec.IsLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        punchForward = Vector3.ProjectOnPlane(-laneDir, Vector3.up);
        if (punchForward.sqrMagnitude < 0.0001f) punchForward = Vector3.forward;
        punchForward.Normalize();
        punchRight = Vector3.Cross(Vector3.up, punchForward).normalized;
    }

    // ── Visuals ──────────────────────────────────────────────────────────────

    /// Three cues carry the same two facts, because they resolve at different
    /// ranges and in different parts of the visual field:
    ///   • colour → hand, readable instantly and in peripheral vision
    ///   • silhouette → punch type, likewise ("the long axis is the way you
    ///     swing; a ball means straight")
    ///   • arrow → direction and polarity, but only legible from ~1.5 m, so it
    ///     confirms late what the silhouette suggested early
    void BuildVisuals()
    {
        Color tint = Spec.IsLeft ? LeftColor : RightColor;

        bodyMat = PunchOrb.UnlitMaterial(tint);
        var bodyGo = PunchOrb.Sphere("Body", bodyMat);
        body = bodyGo.transform;
        body.SetParent(transform, worldPositionStays: false);
        body.localPosition = Vector3.zero;
        body.localScale    = SilhouetteScale();

        if (Spec.Punch == PunchType.Straight)
        {
            // A straight comes head-on, so an arrow would point at the player
            // and collapse to a dot. The concentric inner disc reads as
            // "through the middle" instead, and it is the same shape language.
            arrowMat = PunchOrb.UnlitMaterial(ArrowColor);
            var pip = PunchOrb.Sphere("Pip", arrowMat);
            arrow = pip.transform;
            arrow.SetParent(transform, worldPositionStays: false);
            arrow.localScale = Vector3.one * (diameter * 0.42f);
        }
        else
        {
            arrowMat = PunchOrb.UnlitMaterial(ArrowColor);
            var go = new GameObject("Arrow");
            go.AddComponent<MeshFilter>().sharedMesh = PunchOrb.ArrowMesh();
            go.AddComponent<MeshRenderer>().material = arrowMat;
            arrow = go.transform;
            arrow.SetParent(transform, worldPositionStays: false);
            arrow.localScale = Vector3.one * (diameter * 0.95f);
        }
    }

    Vector3 SilhouetteScale()
    {
        // Applied in the billboard frame, so "wide" and "tall" mean wide and
        // tall on screen rather than in world axes.
        switch (Spec.Punch)
        {
            case PunchType.Hook:     return new Vector3(1.95f, 0.80f, 0.80f) * diameter;
            case PunchType.Uppercut: return new Vector3(0.80f, 1.95f, 0.80f) * diameter;
            default:                 return Vector3.one * diameter;
        }
    }

    /// World direction the punch has to be travelling in.
    public Vector3 RequiredDirection()
    {
        switch (Spec.Punch)
        {
            // A hook swings inward across the body: the left hand travels to the
            // player's right, and vice versa. The note sits wide on its own
            // hand's side, so the arm passes through it on the way in.
            case PunchType.Hook:     return Spec.IsLeft ? punchRight : -punchRight;
            case PunchType.Uppercut: return Vector3.up;
            default:                 return punchForward;
        }
    }

    /// Keeps the silhouette and the arrow square to the player. Without this a
    /// hook seen from an angle is just a sphere and an arrow seen edge-on is
    /// invisible — a silent failure of the whole cueing scheme.
    void Billboard()
    {
        if (head == null) return;

        Vector3 toViewer = head.position - transform.position;
        if (toViewer.sqrMagnitude < 0.0001f) return;
        toViewer.Normalize();

        if (body != null)
            body.rotation = Quaternion.LookRotation(toViewer, Vector3.up);

        if (arrow == null) return;

        if (Spec.Punch == PunchType.Straight)
        {
            // The pip sits slightly proud of the body so it is never z-fighting
            // with the sphere it is centred in.
            arrow.position = transform.position + toViewer * (diameter * 0.40f);
            return;
        }

        // Point the arrow along the punch direction as projected into the plane
        // the player is looking at.
        Vector3 screenDir = Vector3.ProjectOnPlane(RequiredDirection(), toViewer);
        if (screenDir.sqrMagnitude < 0.0001f) screenDir = Vector3.up;

        arrow.rotation = Quaternion.LookRotation(toViewer, screenDir.normalized);
        arrow.position = transform.position + toViewer * (diameter * 0.62f);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    void Update()
    {
        transform.position += laneDir * speed * Time.deltaTime;
        Billboard();

        if (Resolved) return;

        // Crossing the strike plane untouched is the fail unit. Distance to the
        // plane is also the timing error, which is what makes accuracy here an
        // objective number rather than a judgement call.
        if (Vector3.Dot(transform.position - strikeOrigin, laneDir) >= 0f)
        {
            Resolve(Resolution.Passed);
            return;
        }

        if (hand == null) return;

        // Speed first, then proximity — same ordering as MovementTarget, and for
        // the same reason: a hand resting inside the lane must not consume notes
        // by loitering in them.
        Vector3 local = OVRInput.GetLocalControllerVelocity(controller);
        Vector3 world = trackingSpace != null ? trackingSpace.TransformDirection(local) : local;

        if (world.magnitude < minHitSpeed) return;
        if ((hand.position - transform.position).sqrMagnitude > hitRadius * hitRadius) return;

        Resolve(Vector3.Angle(world, RequiredDirection()) <= ConeHalfAngle
            ? Resolution.Hit
            : Resolution.WrongDirection);
    }

    /// Resolves the note and starts its burst. Notes only ever listen to their
    /// own hand — the off-hand is legitimately busy crossing the grid on its way
    /// to its own note, and letting it consume notes produces misses that feel
    /// like broken hit detection.
    void Resolve(Resolution how)
    {
        if (Resolved) return;
        Resolved = true;

        var cb = OnResolved;
        OnResolved = null;
        cb?.Invoke(this, how);

        // A wrong-direction consume has to look different from a timing miss:
        // both are misses in the tally, but "I was late" and "I threw the wrong
        // punch" need completely different corrections from the player.
        if (how == Resolution.WrongDirection)
        {
            PunchOrb.SetColor(bodyMat, WrongColor);
            PunchOrb.SetColor(arrowMat, WrongColor);
        }

        StartCoroutine(Burst(how == Resolution.Passed ? 0.10f : 0.16f));
    }

    IEnumerator Burst(float duration)
    {
        yield return PunchOrb.PopOut(transform, duration);
        Destroy(gameObject);
    }

    /// Cancels the note with no callback — used when the whole harvest is torn
    /// down and nothing should be scored.
    public void Discard()
    {
        Resolved   = true;
        OnResolved = null;
        Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (bodyMat  != null) Destroy(bodyMat);
        if (arrowMat != null) Destroy(arrowMat);
    }

    /// How far the note still has to travel before it crosses the strike plane.
    public float DistanceToPlane =>
        -Vector3.Dot(transform.position - strikeOrigin, laneDir);

#if UNITY_EDITOR
    /// Headset-free hook for DesktopHikeSimulator's auto-play. Resolves the note
    /// as if a hand had (or had not) arrived correctly, without faking a
    /// controller velocity.
    internal void SimulateResolve(bool hit)
    {
        Resolve(hit ? Resolution.Hit : Resolution.WrongDirection);
    }
#endif
}
