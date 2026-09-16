#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

/// Editor-only stand-in for the headset and controllers so the game can be
/// tested without hardware. Added automatically by HikingPlayerController
/// when play mode starts with no XR device active.
///
///   Hold RMB + move mouse — look around
///   J / K                 — punch left / right target
///   Space                 — punch targets alternately (simulates a stride)
///   Hold Left Shift       — punch at full strength
///   E                     — investigate the plant you are next to (NatureInfoUI)
///   F                     — light a torch you are standing next to (Torch.cs)
///   P                     — cycle harvest auto-play: off → 50% → 80% → off
///
/// ── AUTO-PLAY IS THE REAL HARNESS ────────────────────────────────────────────
/// Harvest notes now have a direction and a ~200 ms window, so the old "route
/// J/K to whichever orb is left" trick cannot express what a punch even is: a
/// keyboard has no punch direction, and it cannot tell you whether 4.3 m/s is
/// too fast or the cone too tight. Those need arms.
///
/// What a keyboard *can* verify is the state machine, which is where the bugs
/// are — does a pass bank the sample and a fail not, does the retry rest start,
/// does the distance gate clear, does aborting after the first note cost an
/// attempt. Auto-play lands a set fraction of notes, so setting it either side
/// of the 65% threshold walks both branches in seconds. Charts are seeded from
/// the plant rather than the clock, so a run reproduces exactly.
///
/// J/K still resolve the nearest note for that hand as a clean hit, which is
/// useful for eyeballing chart pacing and nothing else.
public class DesktopHikeSimulator : MonoBehaviour
{
    [SerializeField] float lookSensitivity = 0.15f;   // degrees per mouse pixel
    [SerializeField] float softPunchSpeed  = 1.6f;    // m/s, a light jab
    [SerializeField] float hardPunchSpeed  = 3.5f;    // m/s, full-strength punch

    HikingPlayerController controller;
    Transform centerEye;
    float pitch;
    bool  nextAltIsLeft = true;

    void Start()
    {
        controller = GetComponent<HikingPlayerController>();
        var rig    = GetComponent<OVRCameraRig>();
        centerEye  = rig.centerEyeAnchor;

        // Without a headset the rig resets its anchors to identity every
        // frame, which would fight the simulated camera — switch it off,
        // along with OVRManager's device management.
        rig.enabled = false;
        var mgr = FindAnyObjectByType<OVRManager>();
        if (mgr != null) mgr.enabled = false;

        // The scene may store a stale tracked pose on the anchor (normally
        // overwritten by the headset every frame) — reset so the simulated
        // camera starts aligned with the rig's facing.
        centerEye.localPosition = Vector3.zero;
        centerEye.localRotation = Quaternion.identity;

        Debug.Log("[DesktopHikeSimulator] No XR device — desktop simulation active. " +
                  "Hold RMB to look. J/K = punch left/right, Space = alternate, Shift = hard punch.");
    }

    void Update()
    {
        var kb    = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (mouse.rightButton.isPressed)
        {
            Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
            transform.Rotate(0f, d.x, 0f, Space.World);
            pitch = Mathf.Clamp(pitch - d.y, -80f, 80f);
            centerEye.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        float speed = kb.leftShiftKey.isPressed ? hardPunchSpeed : softPunchSpeed;

        if (kb.jKey.wasPressedThisFrame) Punch(isLeft: true,  speed);
        if (kb.kKey.wasPressedThisFrame) Punch(isLeft: false, speed);
        if (kb.spaceKey.wasPressedThisFrame)
        {
            Punch(nextAltIsLeft, speed);
            nextAltIsLeft = !nextAltIsLeft;
        }

        if (kb.pKey.wasPressedThisFrame) CycleAutoPlay();
    }

    // Off, then a rate that fails the 65% threshold, then one that passes it.
    static readonly float[] AutoPlayRates = { -1f, 0.5f, 0.8f };
    int autoPlayIndex;

    void CycleAutoPlay()
    {
        autoPlayIndex = (autoPlayIndex + 1) % AutoPlayRates.Length;
        PlantHarvest.AutoPlayRate = AutoPlayRates[autoPlayIndex];
    }

    void OnDisable()
    {
        // Otherwise a domain reload leaves auto-play armed and the next play
        // session harvests itself.
        PlantHarvest.AutoPlayRate = -1f;
    }

    void Punch(bool isLeft, float speed)
    {
        // A harvest takes priority: its notes are what the player is punching at.
        var harvest = PlantHarvest.Active;
        if (harvest != null) { harvest.SimulatePunch(isLeft, speed); return; }

        var target = isLeft ? controller.TargetL : controller.TargetR;
        if (target != null) target.SimulateHit(speed);
    }

    void OnGUI()
    {
        var v = controller.Velocity; v.y = 0f;
        GUI.Box(new Rect(10, 10, 380, 250), GUIContent.none);
        GUILayout.BeginArea(new Rect(18, 16, 368, 240));
        GUILayout.Label("<b>DESKTOP SIM</b>  —  RMB look · J/K punch L/R · Space alternate · Shift hard · E investigate · F light torch · P auto-play",
            new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        GUILayout.Label($"Momentum {controller.Speed01:P0}    Speed {v.magnitude:F2} m/s");

        // Progression readout — the fog is only drawn within ~45 m and the
        // sector rule is invisible, so testing this without numbers is guesswork.
        var p = controller.Progression;
        if (p != null && p.TorchCount > 0)
        {
            GUILayout.Label($"Torches {p.LitCount}/{p.TorchCount}    Loop {p.ProgressDeg:F0}°");
            GUILayout.Label(p.AllLit
                ? "Fog: all walls dropped"
                : $"Fog wall: {p.ArcToGateMetres:F0} m to torch {p.NextTorchIndex + 1}");
        }

        var rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };

        var pc = PlantCollection.Instance;
        if (pc != null)
        {
            string gate = pc.MetresToGo > 0f ? $"   ·   walk {pc.MetresToGo:F0} m"
                        : pc.RestRemaining > 0f ? $"   ·   rest {pc.RestRemaining:F0} s"
                        : "   ·   ready";
            GUILayout.Label($"Samples {pc.Count}/{pc.Required}"
                          + (pc.QuotaMet ? "  — torch ready" : "") + gate);
        }

        GUILayout.Label(PlantHarvest.AutoPlayRate < 0f
            ? "Auto-play <b>off</b>  (P to cycle)"
            : $"Auto-play <b>{PlantHarvest.AutoPlayRate:P0}</b>  "
              + (PlantHarvest.AutoPlayRate >= PlantHarvest.PassThreshold ? "→ should pass" : "→ should fail"),
            rich);

        var harvest = PlantHarvest.Active;
        if (harvest != null)
        {
            // Plain null check, not ?., so a destroyed plant reads as null here
            // rather than sailing through Unity's fake-null and throwing in OnGUI.
            string plant = harvest.Plant != null ? harvest.Plant.displayName : "plant";

            GUILayout.Label($"<b>Harvesting</b> {plant} — {harvest.CurrentPhase}"
                          + (harvest.Committed ? "  (committed)" : "  (free abort)"), rich);
            GUILayout.Label($"Hit {harvest.Hit}/{harvest.Spawned} of {harvest.Total}"
                          + $"   ·   {harvest.Accuracy01:P0}"
                          + $"   ·   combo {harvest.Combo} (best {harvest.BestCombo})");
            GUILayout.Label(harvest.Secured
                ? $"<b>SECURED</b>   ·   {harvest.SecondsLeft:F1} s left"
                : $"Progress {harvest.Progress01:P0} to threshold   ·   {harvest.SecondsLeft:F1} s left", rich);
        }
        GUILayout.EndArea();
    }
}
#endif
