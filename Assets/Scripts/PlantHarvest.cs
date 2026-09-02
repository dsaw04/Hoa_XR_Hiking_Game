using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// The mini workout that gathers one plant sample.
///
/// A ring of punch orbs appears between the player and the plant; clearing all
/// of them banks the sample. Only one harvest runs at a time (`Active`).
///
/// ── WHY PUNCH ORBS AND NOT A NEW MECHANIC ────────────────────────────────────
/// Punching is the one physical verb this project has — MovementModeManager
/// documents that locomotion is 100% upper-body and that no squat/jump/duck
/// mechanics exist anywhere. Building the workout from orbs means it works
/// seated and in a small play space, and it inherits the Body-Adaptive tuning
/// for free: the mode scales how many orbs appear, how far the player reaches
/// for them and how hard each punch has to be. A Gentle-mode player still
/// gathers five plants per torch, just with a lighter set on each.
///
/// ── WHY THE RING SITS IN FRONT OF THE PLANT, NOT ON IT ───────────────────────
/// The flora are GameObject prefabs scaled 1.2–12×, and the large ones are
/// metres across and may carry colliders. A ring centred on the plant's own
/// transform would put orbs inside a bush the player physically cannot walk
/// into. The ring is therefore struck on the player's side of the plant, on the
/// line between the two, at the moment the harvest starts.
public class PlantHarvest : MonoBehaviour
{
    /// The harvest currently in progress, or null. Read by NatureInfoUI (to
    /// suppress the proximity prompt) and DesktopHikeSimulator.
    public static PlantHarvest Active { get; private set; }

    // ── Tuning ───────────────────────────────────────────────────────────────
    const int   BaseOrbs      = 4;      // at Steady; scaled by EffortScale
    const float BaseRadius    = 0.42f;  // ring radius (m) at Steady; scaled by ReachScale
    const float BaseHitSpeed  = 1.1f;   // matches HikingPlayerController.minHitSpeed
    const float StandOff      = 0.70f;  // m from the plant back toward the player
    const float DropBelowEyes = 0.45f;  // ring centre relative to eye height
    const float AbortDistance = 4.5f;   // matches NatureInfoUI.hideDistance
    const float OrbSize       = 0.20f;
    const float OrbHitRadius  = 0.22f;
    const float LingerSeconds = 0.5f;   // keeps pop/haptic coroutines alive past Finish

    public NatureInteractable Plant { get; private set; }
    /// Orbs this harvest started with, and how many are left to clear.
    public int Total     { get; private set; }
    public int Remaining => orbs.Count;

    readonly List<MovementTarget> orbs = new List<MovementTarget>();

    Transform head;
    System.Action<NatureInteractable> onGathered;

    /// Starts a harvest, cancelling any other in progress. Returns null when the
    /// rig has no hand anchors to punch with, so callers can fall back to simply
    /// identifying the plant.
    public static PlantHarvest Begin(NatureInteractable plant, Transform head,
                                     OVRCameraRig rig,
                                     System.Action<NatureInteractable> onGathered)
    {
        if (plant == null || head == null || rig == null) return null;
        if (Active != null) Active.Cancel();

        var go = new GameObject("PlantHarvest");
        go.transform.position = plant.transform.position;

        var h = go.AddComponent<PlantHarvest>();
        h.Plant      = plant;
        h.head       = head;
        h.onGathered = onGathered;
        h.BuildOrbs(rig);

        Active = h;
        return h;
    }

    void BuildOrbs(OVRCameraRig rig)
    {
        var mm     = MovementModeManager.Instance;
        float reach  = mm != null ? mm.ReachScale  : 1f;
        float effort = mm != null ? mm.EffortScale : 1f;

        // Clamped rather than raw so the set stays a workout at the gentle end
        // and stays finishable at the energetic one: 3 / 4 / 5 orbs.
        int   orbCount = Mathf.Clamp(Mathf.RoundToInt(BaseOrbs * effort), 3, 8);
        float radius   = BaseRadius   * reach;
        float speed    = BaseHitSpeed * effort;

        Vector3 plantPos = Plant.transform.position;

        Vector3 toPlant = plantPos - head.position;
        toPlant.y = 0f;
        // Standing directly on top of the plant leaves no line to work from;
        // fall back to whichever way the player happens to be looking.
        if (toPlant.sqrMagnitude < 0.01f)
            toPlant = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (toPlant.sqrMagnitude < 0.01f) toPlant = Vector3.forward;
        toPlant.Normalize();

        Vector3 centre = plantPos - toPlant * StandOff;
        centre.y = head.position.y - DropBelowEyes;

        // Player's right, matching HikingPlayerController.TargetAnchor.
        Vector3 axis = Vector3.Cross(Vector3.up, toPlant).normalized;

        Total = orbCount;
        for (int i = 0; i < orbCount; i++)
        {
            // Starting at 45° keeps orbs off the exact horizontal and vertical,
            // so an even count splits cleanly into high and low reaches rather
            // than sitting in a line.
            float a = (45f + i * (360f / orbCount)) * Mathf.Deg2Rad;
            Vector3 offset = axis * (Mathf.Cos(a) * radius) + Vector3.up * (Mathf.Sin(a) * radius);

            // Each orb is assigned to the hand that naturally covers its side of
            // the ring, so both arms get used rather than one doing all the work.
            bool isLeft = Vector3.Dot(offset, axis) < 0f;
            Transform hand = isLeft ? rig.leftHandAnchor : rig.rightHandAnchor;
            if (hand == null) hand = head;

            var orb = MovementTarget.Create(hand, isLeft);
            orb.name = $"HarvestOrb_{i + 1}";
            orb.transform.SetParent(transform, worldPositionStays: false);
            orb.transform.position = centre + offset;
            orb.SetSize(OrbSize);
            orb.SetHitRadius(OrbHitRadius);
            orb.SetRequiredSpeed(speed);
            orb.OnHit = OnOrbHit;
            orbs.Add(orb);
        }
    }

    void OnOrbHit(MovementTarget orb, float speed)
    {
        orbs.Remove(orb);
        orb.Pop();
        StartCoroutine(Buzz(orb.IsLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch));

        if (orbs.Count == 0) Complete();
    }

    void Complete()
    {
        var plant = Plant;
        var callback = onGathered;
        Finish();
        callback?.Invoke(plant);
    }

    /// Abandons the harvest with no credit. Walking away does this.
    public void Cancel() => Finish();

    void Finish()
    {
        if (Active == this) Active = null;

        foreach (var orb in orbs)
            if (orb != null) orb.Pop();
        orbs.Clear();

        enabled = false;
        // Delayed so the orb burst and the haptic pulse finish; destroying now
        // would kill both coroutines and leave a controller buzzing.
        Destroy(gameObject, LingerSeconds);
    }

    void Update()
    {
        if (Plant == null || head == null) { Cancel(); return; }

        float sqr = (head.position - Plant.transform.position).sqrMagnitude;
        if (sqr > AbortDistance * AbortDistance) Cancel();
    }

    IEnumerator Buzz(OVRInput.Controller ctrl)
    {
        OVRInput.SetControllerVibration(0.35f, 0.5f, ctrl);
        yield return new WaitForSecondsRealtime(0.06f);
        OVRInput.SetControllerVibration(0f, 0f, ctrl);
    }

#if UNITY_EDITOR
    /// Headset-free testing hook for DesktopHikeSimulator. Falls through to any
    /// remaining orb when that hand has none left, so J/K can always finish a
    /// set rather than deadlocking on an empty side of the ring.
    internal void SimulatePunch(bool isLeft, float speed)
    {
        MovementTarget pick = null;
        foreach (var orb in orbs)
            if (orb != null && orb.IsLeft == isLeft) { pick = orb; break; }

        if (pick == null && orbs.Count > 0) pick = orbs[0];
        if (pick != null) pick.SimulateHit(speed);
    }
#endif
}
