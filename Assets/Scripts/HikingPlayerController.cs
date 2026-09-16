using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// Target-hit locomotion for Meta Quest 3 hiking game.
/// Two floating orbs hover in front of the player (Beat Saber-style);
/// punching one adds momentum and the player walks forward in the
/// direction they are facing. Uses a CharacterController so the player
/// walks up slopes naturally and cannot clip through terrain.
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(OVRCameraRig))]
public class HikingPlayerController : MonoBehaviour
{
    [Header("Camera Height")]
    [Tooltip("Eye height above terrain (m). Drives the CharacterController capsule position.")]
    [SerializeField] float eyeHeight = 1.7f;

    [Header("Speed")]
    [SerializeField] float maxSpeed      = 5f;
    [SerializeField] float momentumDecay = 2.2f;

    [Header("Targets")]
    [Tooltip("Forward distance from the head to the targets (m).")]
    [SerializeField] float targetDistance = 0.55f;
    [Tooltip("Sideways offset of each target from head centre (m).")]
    [SerializeField] float targetSpacing  = 0.28f;
    [Tooltip("How far below eye level the targets float (m).")]
    [SerializeField] float targetDrop     = 0.35f;
    [Tooltip("How quickly targets follow head movement/turning.")]
    [SerializeField] float targetFollow   = 8f;

    [Header("Hit Tuning")]
    [Tooltip("Controller speed (m/s) at which a punch counts as full strength.")]
    [SerializeField] float peakHitSpeed = 3.5f;
    [SerializeField] float minHitSpeed  = 1.1f;

    [Header("Alternation")]
    [SerializeField] float altBonus       = 1.3f;
    [SerializeField] float sameArmPenalty = 0.55f;

    [Header("Haptics")]
    [SerializeField] float hapticBase      = 0.25f;
    [SerializeField] float hapticIntensity = 0.45f;
    [SerializeField] float hapticDuration  = 0.055f;

    float momentum;
    const float MAX_MOMENTUM = 10f;
    const float HITS_TO_PEAK = 5f;

    bool lastWasLeft;
    bool firstHit  = true;
    int  altStreak = 0;

    float verticalVelocity;
    const float Gravity     = -20f;
    const float GroundedVel = -2f;    // small downward push to stay stuck to slopes
    const float TerminalVel = -30f;

    CharacterController cc;
    OVRCameraRig        ovrRig;
    Transform           centerEye;
    Terrain             terrain;
    MovementTarget      targetL, targetR;
    TorchProgressionManager progression;
    Collider            lastHitCollider;
    bool                lastHitWasFog;

    void Awake()
    {
        ovrRig  = GetComponent<OVRCameraRig>();
        cc      = GetComponent<CharacterController>();
        terrain = Terrain.activeTerrain;
        ConfigureCC();
    }

    void Start()
    {
        centerEye = ovrRig.centerEyeAnchor;

        if (OVRManager.instance != null)
            OVRManager.instance.trackingOriginType = OVRManager.TrackingOrigin.EyeLevel;

        // Fixed foveated rendering: the periphery of a dense NZ-bush hillside is
        // exactly the fill-rate cost FFR is built to hide, and it costs nothing
        // to the centre of vision where the trail and the plants actually are.
        OVRManager.useDynamicFoveatedRendering = true;
        OVRManager.foveatedRenderingLevel      = OVRManager.FoveatedRenderingLevel.HighTop;

        // Drop the player onto the trail before anything else reads their pose.
        SpawnOnTrail();

#if UNITY_EDITOR
        // No headset connected — attach the keyboard/mouse simulator so the
        // game stays testable in the editor.
        if (!UnityEngine.XR.XRSettings.isDeviceActive)
            gameObject.AddComponent<DesktopHikeSimulator>();
#endif

        // Body-Adaptive Movement Control: the player affirms a movement mode
        // that fits their body before locomotion is enabled. That check-in now
        // lives in the menu scene, so by the time this scene loads the session
        // is normally already confirmed and we go straight into the hike.
        var modeManager = MovementModeManager.GetOrCreate();
        if (!modeManager.SessionConfirmed)
        {
            // Someone pressed Play on this scene directly. Send them to the
            // menu rather than duplicating a check-in here.
            if (MenuSceneBootstrap.MenuSceneAvailable)
            {
                SceneManager.LoadScene(MenuSceneBootstrap.MenuScene);
                return;
            }

            // Menu scene isn't in Build Settings — fall back to the last saved
            // choice so the hike is still playable instead of locked forever.
            Debug.LogWarning($"[{nameof(HikingPlayerController)}] " +
                $"'{MenuSceneBootstrap.MenuScene}' is not in Build Settings; " +
                "starting with the last saved movement mode. " +
                "Run Tools/Build Menu Scene to set it up.");
            modeManager.ConfirmSession();
        }

        BeginSession();
    }

    // Applies the chosen movement mode and starts the punch locomotion. Called
    // once the check-in is confirmed (or straight away if it already was).
    void BeginSession()
    {
        var mm = MovementModeManager.Instance;
        if (mm != null)
        {
            mm.OnSessionConfirmed -= BeginSession;
            ApplyMovementMode(mm);
        }

        targetL = MovementTarget.Create(ovrRig.leftHandAnchor,  isLeft: true);
        targetR = MovementTarget.Create(ovrRig.rightHandAnchor, isLeft: false);
        targetL.OnHit = OnTargetHit;
        targetR.OnHit = OnTargetHit;
        targetL.SetRequiredSpeed(minHitSpeed);
        targetR.SetRequiredSpeed(minHitSpeed);

        SnapTargets();

        // Torch progression: places the torches around the loop and stands the
        // fog walls that hold the player in the current sector until they have
        // gathered enough plant samples to light the next one. Created here for
        // the same reason as everything else in this scene — no prefabs, no
        // scene wiring.
        progression = TorchProgressionManager.GetOrCreate();
        progression.Initialise(terrain, transform, centerEye, ovrRig);
    }

    // Scales the punch-locomotion tuning to the selected mode + intensity.
    // This project has no squat/jump/duck/twist mechanics to toggle, so the
    // modes differentiate by reach, punch effort, speed and pacing.
    // mm.AllowLowerBody is the gating hook for when such mechanics are added.
    void ApplyMovementMode(MovementModeManager mm)
    {
        targetDistance *= mm.ReachScale;
        targetSpacing  *= mm.ReachScale;
        targetDrop     *= mm.ReachScale;
        peakHitSpeed   *= mm.EffortScale;
        minHitSpeed    *= mm.EffortScale;
        maxSpeed       *= mm.SpeedScale;
        momentumDecay  *= mm.PaceScale;
    }

    // Hooks for DesktopHikeSimulator (editor-only, headset-free testing)
    internal MovementTarget TargetL  => targetL;
    internal MovementTarget TargetR  => targetR;
    internal float          Speed01  => Mathf.Clamp01(momentum / MAX_MOMENTUM);
    internal Vector3        Velocity => cc.velocity;
    internal TorchProgressionManager Progression => progression;

    void Update()
    {
        momentum = Mathf.Max(0f, momentum - momentumDecay * Time.deltaTime);

        // Horizontal movement in head-facing direction.
        float speed = maxSpeed * Mathf.Clamp01(momentum / MAX_MOMENTUM);
        Vector3 move = Vector3.zero;
        if (speed > 0.02f)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(centerEye.forward, Vector3.up);
            if (fwd.sqrMagnitude > 0.001f)
                move = fwd.normalized * speed;
        }

        // Gravity — small constant when grounded so CC stays pressed against slopes
        if (cc.isGrounded)
            verticalVelocity = GroundedVel;
        else
            verticalVelocity = Mathf.Max(verticalVelocity + Gravity * Time.deltaTime, TerminalVel);

        move.y = verticalVelocity;
        cc.Move(move * Time.deltaTime);

        // Feed the harvest rest gate. Horizontal only — falling down a slope is
        // not recovery, and it should not buy the player a work interval.
        // Pushed from here rather than pulled by PlantCollection for the same
        // reason TorchProgressionManager.Initialise is called from BeginSession:
        // the managers do not reach back out into the scene for the player.
        var collection = PlantCollection.Instance;
        if (collection != null)
        {
            Vector3 travelled = cc.velocity;
            travelled.y = 0f;
            collection.AddTravel(travelled.magnitude * Time.deltaTime);
        }
    }

    /// Drops the player's walking momentum on the spot.
    ///
    /// Called when a harvest starts: momentum decays over about a second, so a
    /// player who punch-ran up to a plant would keep drifting forward into their
    /// own note lane and the first phrase would feel like it was rushing them.
    /// Same problem TorchGateMist solves by zeroing momentum against fog walls.
    internal void ZeroMomentum() => momentum = 0f;

    // Targets track the head after all movement for the frame is done.
    void LateUpdate()
    {
        if (targetL == null) return;
        float t = 1f - Mathf.Exp(-targetFollow * Time.deltaTime);
        targetL.transform.position = Vector3.Lerp(targetL.transform.position, TargetAnchor(isLeft: true),  t);
        targetR.transform.position = Vector3.Lerp(targetR.transform.position, TargetAnchor(isLeft: false), t);
    }

    // Desired world position of a target: in front of the head, offset
    // sideways, a little below eye level. Yaw-only so head pitch doesn't
    // push the targets into the ground.
    Vector3 TargetAnchor(bool isLeft)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(centerEye.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.ProjectOnPlane(centerEye.up, Vector3.up);
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        return centerEye.position
             + fwd   * targetDistance
             + right * (isLeft ? -targetSpacing : targetSpacing)
             + Vector3.up * -targetDrop;
    }

    void SnapTargets()
    {
        targetL.transform.position = TargetAnchor(isLeft: true);
        targetR.transform.position = TargetAnchor(isLeft: false);
    }

    // ── Hit handling ─────────────────────────────────────────────────────────
    void OnTargetHit(MovementTarget target, float hitSpeed)
    {
        var ctrl = target.IsLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        float intensity = Mathf.Clamp01(
            (hitSpeed - minHitSpeed) / (peakHitSpeed - minHitSpeed));

        float boost = (MAX_MOMENTUM / HITS_TO_PEAK) * (0.4f + intensity * 0.6f);

        bool alternates = firstHit || (target.IsLeft != lastWasLeft);
        if (alternates)
        {
            altStreak++;
            if (altStreak >= 2)
                boost *= altBonus;
        }
        else
        {
            altStreak = 1;
            boost    *= sameArmPenalty;
        }

        momentum    = Mathf.Min(momentum + boost, MAX_MOMENTUM);
        lastWasLeft = target.IsLeft;
        firstHit    = false;

        float amp = hapticBase + intensity * hapticIntensity;
        StartCoroutine(HapticPulse(ctrl, amp));
    }

    // ── Fog walls ────────────────────────────────────────────────────────────

    /// Torch gates are physical: TorchGateMist stands a line of box colliders
    /// across the map at every unlit gate. The CharacterController stops dead
    /// against one, but momentum would keep pushing until it decayed, leaving
    /// the player grinding into the fog. Zeroing it on contact makes walking
    /// away immediate — the fog is the whole gate, so nothing else here needs to
    /// know about progression.
    ///
    /// The component lookup is cached per collider because this fires every
    /// frame of contact, and the terrain is by far the most common hit.
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.collider != lastHitCollider)
        {
            lastHitCollider = hit.collider;
            lastHitWasFog   = hit.collider.GetComponentInParent<TorchGateMist>() != null;
        }

        if (lastHitWasFog) momentum = 0f;
    }

    // Capsule bottom = rig root Y − eyeHeight (i.e., foot level).
    // In EyeLevel mode the rig root ≈ eye world position.
    void ConfigureCC()
    {
        const float h = 1.0f;   // capsule height
        const float r = 0.3f;   // capsule radius
        cc.height          = h;
        cc.radius          = r;
        cc.center          = new Vector3(0f, -(eyeHeight - h * 0.5f), 0f);
        cc.stepOffset      = 0.3f;
        cc.slopeLimit      = 55f;
        cc.skinWidth       = 0.03f;
        cc.minMoveDistance = 0f;
    }

    // ── Haptics ──────────────────────────────────────────────────────────────
    IEnumerator HapticPulse(OVRInput.Controller ctrl, float amplitude)
    {
        OVRInput.SetControllerVibration(0.3f, amplitude, ctrl);
        yield return new WaitForSecondsRealtime(hapticDuration);
        OVRInput.SetControllerVibration(0f, 0f, ctrl);
    }

    // ── Spawn ─────────────────────────────────────────────────────────────────
    void SpawnOnTrail()
    {
        if (terrain == null) return;
        // East side of the circular trail: 140m from terrain centre (500,500)
        const float wx = 640f, wz = 500f;
        float terrainY = terrain.SampleHeight(new Vector3(wx, 0f, wz)) + terrain.transform.position.y;
        cc.enabled = false;
        transform.position = new Vector3(wx, terrainY + eyeHeight, wz);
        transform.rotation = Quaternion.Euler(0f, 270f, 0f); // face west toward crater
        cc.enabled = true;
        verticalVelocity = 0f;
    }
}
