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
///
/// While a plant harvest is running the punch keys are routed to its orbs
/// instead of the locomotion pair, so a set can be worked through from the
/// keyboard without walking off mid-workout.
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
    }

    void Punch(bool isLeft, float speed)
    {
        // A harvest takes priority: its orbs are what the player is punching at.
        var harvest = PlantHarvest.Active;
        if (harvest != null) { harvest.SimulatePunch(isLeft, speed); return; }

        var target = isLeft ? controller.TargetL : controller.TargetR;
        if (target != null) target.SimulateHit(speed);
    }

    void OnGUI()
    {
        var v = controller.Velocity; v.y = 0f;
        GUI.Box(new Rect(10, 10, 360, 176), GUIContent.none);
        GUILayout.BeginArea(new Rect(18, 16, 348, 166));
        GUILayout.Label("<b>DESKTOP SIM</b>  —  RMB look · J/K punch L/R · Space alternate · Shift hard · E investigate · F light torch",
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

        var pc = PlantCollection.Instance;
        if (pc != null)
        {
            GUILayout.Label($"Samples {pc.Count}/{pc.Required}"
                          + (pc.QuotaMet ? "  — torch ready" : ""));
        }

        var harvest = PlantHarvest.Active;
        if (harvest != null)
        {
            // Plain null check, not ?., so a destroyed plant reads as null here
            // rather than sailing through Unity's fake-null and throwing in OnGUI.
            string plant = harvest.Plant != null ? harvest.Plant.displayName : "plant";
            GUILayout.Label($"<b>Harvesting</b> {plant} — "
                          + $"{harvest.Total - harvest.Remaining}/{harvest.Total} orbs (J/K)",
                new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
        }
        GUILayout.EndArea();
    }
}
#endif
