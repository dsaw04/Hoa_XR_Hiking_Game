using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// Controller ray pointer for the menu panels.
///
/// Draws a laser from each tracked controller, hit-tests it against the
/// registered VRButtons (see VRButton for why the EventSystem isn't used), and
/// fires the button under the beam on an index-trigger press with a haptic tick.
///
/// Both hands are live at once, FitXR-style — whichever you raise works. In the
/// editor with no headset connected, the mouse drives a third ray cast from the
/// main camera so the menu stays testable without putting the Quest on (same
/// intent as DesktopHikeSimulator does for locomotion).
///
/// Created at runtime by MenuSceneBootstrap; no scene wiring.
public class VRPointer : MonoBehaviour
{
    /// Beam length when the ray isn't over anything (m).
    const float IdleLength = 2.5f;
    const float BeamWidthNear = 0.006f;
    const float BeamWidthFar  = 0.0015f;
    const float DotSize       = 0.016f;

    /// Quest controllers are held at a slight downward tilt relative to the
    /// pose Unity reports, so the raw anchor forward aims a little high.
    /// Positive pitches the beam down. Tunable live in the inspector.
    [SerializeField] float rayPitchOffset = 12f;

    [SerializeField] float hapticAmplitude = 0.35f;
    [SerializeField] float hapticDuration  = 0.04f;

    // One beam per input source (left hand, right hand, editor mouse).
    class Beam
    {
        public Transform            anchor;      // null → editor mouse ray
        public OVRInput.Controller  controller;
        public LineRenderer         laser;
        public Transform            dot;
        public VRButton             hover;
    }

    readonly List<Beam> beams = new List<Beam>();
    readonly HashSet<VRButton> hoveredNow  = new HashSet<VRButton>();
    readonly HashSet<VRButton> hoveredLast = new HashSet<VRButton>();

    Transform head;
    Material  beamMaterial;

    public static VRPointer Create(OVRCameraRig rig, Transform head)
    {
        var go = new GameObject("VRPointer");
        var p  = go.AddComponent<VRPointer>();
        p.head = head;

        p.beamMaterial = BuildBeamMaterial();

        if (rig != null)
        {
            p.beams.Add(p.NewBeam("LeftBeam",  rig.leftHandAnchor,  OVRInput.Controller.LTouch));
            p.beams.Add(p.NewBeam("RightBeam", rig.rightHandAnchor, OVRInput.Controller.RTouch));
        }
#if UNITY_EDITOR
        // Headset-free testing: mouse ray through the main camera.
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
            p.beams.Add(p.NewBeam("MouseBeam", null, OVRInput.Controller.None));
#endif
        return p;
    }

    Beam NewBeam(string name, Transform anchor, OVRInput.Controller controller)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        var lr = go.AddComponent<LineRenderer>();
        lr.material       = beamMaterial;
        lr.useWorldSpace  = true;
        lr.positionCount  = 2;
        lr.numCapVertices = 4;
        lr.textureMode    = LineTextureMode.Stretch;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.widthCurve     = AnimationCurve.Linear(0f, BeamWidthNear, 1f, BeamWidthFar);
        lr.startColor     = new Color(VRPanel.Accent.r, VRPanel.Accent.g, VRPanel.Accent.b, 0.85f);
        lr.endColor       = new Color(VRPanel.Accent.r, VRPanel.Accent.g, VRPanel.Accent.b, 0.10f);

        // Reticle at the hit point — a small accent disc, billboarded to the head.
        var dotGo = new GameObject("Dot");
        dotGo.transform.SetParent(transform, false);
        var sr = dotGo.AddComponent<SpriteRenderer>();
        sr.sprite = UIShapes.Disc(64);
        sr.color  = VRPanel.Accent;
        dotGo.transform.localScale = Vector3.one * DotSize;

        return new Beam
        {
            anchor     = anchor,
            controller = controller,
            laser      = lr,
            dot        = dotGo.transform,
        };
    }

    // URP has no built-in line shader; Unlit is the URP equivalent, with the
    // legacy sprite shader as a fallback so this still draws under other
    // pipelines rather than rendering magenta.
    static Material BuildBeamMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        mat.color = Color.white;                       // LineRenderer tints per-vertex
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
        if (mat.HasProperty("_Surface"))   mat.SetFloat("_Surface", 1f);   // transparent
        return mat;
    }

    void Update()
    {
        hoveredNow.Clear();

        foreach (var beam in beams)
        {
            if (!TryGetRay(beam, out Ray ray))
            {
                SetBeamVisible(beam, false);
                beam.hover = null;
                continue;
            }

            SetBeamVisible(beam, true);
            beam.hover = Pick(ray, out float distance);
            if (beam.hover != null) hoveredNow.Add(beam.hover);

            float length = beam.hover != null ? distance : IdleLength;
            beam.laser.SetPosition(0, ray.origin);
            beam.laser.SetPosition(1, ray.GetPoint(length));

            beam.dot.gameObject.SetActive(beam.hover != null);
            if (beam.hover != null)
            {
                // Nudge toward the shooter so the dot doesn't z-fight the panel.
                beam.dot.position = ray.GetPoint(length - 0.004f);
                if (head != null) beam.dot.rotation = Quaternion.LookRotation(beam.dot.position - head.position);
            }

            if (beam.hover != null && ClickDown(beam))
            {
                beam.hover.Click();
                Pulse(beam.controller);
            }
        }

        ApplyHoverChanges();
    }

    bool TryGetRay(Beam beam, out Ray ray)
    {
        ray = default;

        if (beam.anchor == null)
        {
            var cam = Camera.main;
            if (cam == null || Mouse.current == null) return false;
            ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
            return true;
        }

        if (!OVRInput.IsControllerConnected(beam.controller)) return false;

        Vector3 forward = Quaternion.AngleAxis(rayPitchOffset, beam.anchor.right) * beam.anchor.forward;
        ray = new Ray(beam.anchor.position, forward);
        return true;
    }

    bool ClickDown(Beam beam)
    {
        if (beam.anchor == null)
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

        return OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, beam.controller)
            || OVRInput.GetDown(OVRInput.Button.One,                 beam.controller);
    }

    /// Nearest registered button along the ray.
    static VRButton Pick(Ray ray, out float distance)
    {
        VRButton best = null;
        distance = float.MaxValue;

        // Indexed loop: a Click() handler can rebuild the panel (tab switch),
        // and the list is mutated by OnEnable/OnDisable as that happens.
        for (int i = 0; i < VRButton.All.Count; i++)
        {
            var b = VRButton.All[i];
            if (b == null) continue;
            if (b.Raycast(ray, out float d) && d < distance) { distance = d; best = b; }
        }

        if (best == null) distance = 0f;
        return best;
    }

    void SetBeamVisible(Beam beam, bool visible)
    {
        beam.laser.enabled = visible;
        if (!visible) beam.dot.gameObject.SetActive(false);
    }

    // Hover is per-button, not per-hand: a card lit by either hand stays lit
    // until no beam is on it.
    void ApplyHoverChanges()
    {
        foreach (var b in hoveredLast)
            if (b != null && !hoveredNow.Contains(b)) b.SetHover(false);

        foreach (var b in hoveredNow)
            if (b != null) b.SetHover(true);

        hoveredLast.Clear();
        foreach (var b in hoveredNow) hoveredLast.Add(b);
    }

    void Pulse(OVRInput.Controller controller)
    {
        if (controller == OVRInput.Controller.None) return;
        StartCoroutine(HapticPulse(controller));
    }

    IEnumerator HapticPulse(OVRInput.Controller controller)
    {
        OVRInput.SetControllerVibration(0.3f, hapticAmplitude, controller);
        yield return new WaitForSecondsRealtime(hapticDuration);
        OVRInput.SetControllerVibration(0f, 0f, controller);
    }
}
