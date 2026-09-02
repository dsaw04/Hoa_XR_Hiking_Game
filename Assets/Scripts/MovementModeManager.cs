using UnityEngine;

// Ordered gentle → energetic; the check-in UI shows them left-to-right in
// this order. Gentle (small, close motions — suits a small play space),
// Steady (comfortable arm-powered default), Energy Boost (big, high energy).
public enum MovementMode { Gentle, Steady, EnergyBoost }
public enum MovementIntensity { Low, Medium, High }

/// Body-Adaptive Movement Control.
///
/// Stores the movement mode + intensity the player affirms in the pre-session
/// check-in and exposes derived tuning scalars to the locomotion/interaction
/// systems. The point is affirmative choice each session: players with knee/
/// back pain, fatigue, a small play space, or an upper-body-only preference set
/// their own boundaries BEFORE the hike, not mid-session.
///
/// Runtime singleton (DontDestroyOnLoad) — matches this project's all-code,
/// no-ScriptableObject-asset convention (see NatureInfoUI, MovementTarget).
/// The last choice persists to PlayerPrefs so returning players are pre-filled,
/// but the check-in still shows every launch rather than silently skipping.
///
/// NOTE: this project's locomotion is already 100% upper-body (arm-punch, see
/// HikingPlayerController + MovementTarget) — there are no squat/jump/duck/twist
/// mechanics to switch off. The modes therefore differentiate by *tuning* the
/// punch locomotion (reach, effort, speed, pacing). AllowLowerBody is the
/// forward-looking gate for when such mechanics are added.
public class MovementModeManager : MonoBehaviour
{
    public static MovementModeManager Instance { get; private set; }

    // "bam.mode2": the MovementMode enum was reordered (gentle → energetic)
    // so ints saved under the old key would map to the wrong mode.
    const string PrefMode      = "bam.mode2";
    const string PrefIntensity = "bam.intensity";
    const string PrefMinutes   = "bam.minutes";

    /// Session lengths offered by the check-in, in minutes.
    public static readonly int[] DurationOptions = { 10, 20, 30 };

    public MovementMode      CurrentMode { get; private set; } = MovementMode.EnergyBoost;
    public MovementIntensity Intensity   { get; private set; } = MovementIntensity.Medium;

    /// Target session length in minutes, chosen in the check-in. Not yet
    /// consumed by gameplay (same status as SessionLengthScale) — exposed for
    /// the future session/trail system.
    public int SessionMinutes { get; private set; } = 20;

    /// True once the player has confirmed the check-in this run. Locomotion
    /// waits on this before enabling. Persists across scene loads within a run
    /// (DontDestroyOnLoad) but resets on a fresh app launch, so the check-in
    /// re-appears every launch.
    public bool SessionConfirmed { get; private set; }

    /// Fired when the player confirms the check-in (the Start button).
    public System.Action OnSessionConfirmed;

    /// Fetches the manager, creating it if the scene has none. Called from
    /// HikingPlayerController.Start so no manual scene wiring is required.
    public static MovementModeManager GetOrCreate()
    {
        if (Instance == null)
        {
            var go = new GameObject("MovementModeManager");
            Instance = go.AddComponent<MovementModeManager>();
        }
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    // ── Selection (called live by the check-in UI as the player browses) ──
    public void SetMode(MovementMode mode)          => CurrentMode = mode;
    public void SetIntensity(MovementIntensity lvl) => Intensity   = lvl;
    public void SetDuration(int minutes)            => SessionMinutes = minutes;

    /// Persists the choice and unlocks locomotion for this session.
    public void ConfirmSession()
    {
        Save();
        SessionConfirmed = true;
        OnSessionConfirmed?.Invoke();
    }

    // ── Derived tuning the locomotion reads ──────────────────────────────────

    /// Whether lower-body mechanics (squat/jump/duck/twist) are permitted. None
    /// exist yet; this is the gating hook for when they're added. Only the
    /// full-body Energy Boost mode enables them.
    public bool AllowLowerBody => CurrentMode == MovementMode.EnergyBoost;

    /// Multiplies target reach (distance / sideways spacing / drop) — how far
    /// the player must extend an arm to punch a target. Small-space keeps
    /// everything close to the body; Energy Boost pushes it out for bigger swings.
    public float ReachScale => CurrentMode switch
    {
        MovementMode.Gentle      => 0.60f,
        MovementMode.EnergyBoost => 1.15f,
        _                        => 1.00f,   // Steady = comfortable default
    };

    /// Multiplies the controller speed needed to register / fully power a punch.
    /// Small-space accepts gentle jabs; Energy Boost demands committed punches.
    public float EffortScale => CurrentMode switch
    {
        MovementMode.Gentle      => 0.60f,
        MovementMode.EnergyBoost => 1.25f,
        _                        => 1.00f,
    };

    /// Multiplies walking speed. Combines the mode baseline with intensity.
    public float SpeedScale => ModeSpeed * IntensitySpeed;

    /// Multiplies momentum decay — how quickly the player slows when they stop
    /// punching. Higher intensity decays faster, so the player keeps moving to
    /// keep momentum up.
    public float PaceScale => Intensity switch
    {
        MovementIntensity.Low  => 0.80f,
        MovementIntensity.High => 1.30f,
        _                      => 1.00f,
    };

    /// Trail-length / session-duration multiplier — exposed for a session system
    /// to scale required distance or time. Independent of the movement mode.
    public float SessionLengthScale => Intensity switch
    {
        MovementIntensity.Low  => 0.70f,
        MovementIntensity.High => 1.40f,
        _                      => 1.00f,
    };

    float ModeSpeed => CurrentMode switch
    {
        MovementMode.Gentle      => 0.85f,
        MovementMode.EnergyBoost => 1.10f,
        _                        => 1.00f,
    };

    float IntensitySpeed => Intensity switch
    {
        MovementIntensity.Low  => 0.80f,
        MovementIntensity.High => 1.25f,
        _                      => 1.00f,
    };

    // ── Human-readable copy for the UI ───────────────────────────────────────
    public static string Title(MovementMode m) => m switch
    {
        MovementMode.Gentle      => "Gentle Hike",
        MovementMode.Steady      => "Steady Hike",
        MovementMode.EnergyBoost => "Energy Boost",
        _ => m.ToString(),
    };

    public static string Blurb(MovementMode m) => m switch
    {
        MovementMode.Gentle      => "Small, easy motions close to you — fits a small space.",
        MovementMode.Steady      => "Comfortable arm-powered walking. No squats or jumps.",
        MovementMode.EnergyBoost => "Full-body, high energy. Everything on.",
        _ => "",
    };

    // ── Persistence ──────────────────────────────────────────────────────────
    void Load()
    {
        CurrentMode    = (MovementMode)PlayerPrefs.GetInt(PrefMode, (int)MovementMode.EnergyBoost);
        Intensity      = (MovementIntensity)PlayerPrefs.GetInt(PrefIntensity, (int)MovementIntensity.Medium);
        SessionMinutes = PlayerPrefs.GetInt(PrefMinutes, 20);
    }

    void Save()
    {
        PlayerPrefs.SetInt(PrefMode,      (int)CurrentMode);
        PlayerPrefs.SetInt(PrefIntensity, (int)Intensity);
        PlayerPrefs.SetInt(PrefMinutes,   SessionMinutes);
        PlayerPrefs.Save();
    }
}
