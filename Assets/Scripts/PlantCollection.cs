using System.Collections.Generic;
using UnityEngine;

/// How a given plant relates to the quota right now.
public enum PlantState
{
    /// In the active wedge and not yet gathered — worth doing the workout for.
    Collectable,
    AlreadyGathered,
    /// Enough samples are already in hand; the plant is still identifiable.
    QuotaMet,
    /// Still recovering from the last work interval — either not enough ground
    /// covered since it, or the short rest before retrying that same plant has
    /// not run out. The plant is still identifiable.
    Recovering,
    /// Behind a torch the player has already lit, so it no longer counts.
    OutOfSector,
    /// No progression running (menu scene, misconfigured scene, loop complete).
    Unavailable,
}

/// Tracks the plant samples gathered toward lighting the next torch.
///
/// ── WHY SAMPLES ARE LOCKED TO A SECTOR ───────────────────────────────────────
/// Around 1340 plants are scattered over the map — roughly 200 in every 60°
/// sector — so without a locality rule a player could stand at the trailhead,
/// work through five torches' worth of plants without walking anywhere, and then
/// light every torch on a single lap with no further effort. A sample therefore
/// only counts when the plant lies in the wedge the player has most recently
/// been let into: past the torch they last lit, short of the one they are
/// working toward. TorchProgressionManager.SectorIndexOf does that arithmetic.
///
/// ── WHY THE QUOTA IS FLAT AND THE WORKOUT ISN'T ──────────────────────────────
/// The number of plants per torch is a fixed count, so the shape of the hike is
/// the same for everyone. What scales with the player's Body-Adaptive choice is
/// the workout *inside* each plant — the density of the chart, the length of the
/// interval, the reach and the punch effort (see PlantHarvest). That keeps
/// Gentle mode a genuinely lighter set rather than a shorter game.
///
/// ── WHY THERE IS A REST GATE ─────────────────────────────────────────────────
/// The sector rule stops a player farming the trailhead, but it does not stop
/// them harvesting five bushes within ten metres of each other — there are ~200
/// plants in the active wedge. Under the old untimed orb ring that was merely
/// dull. Now that a harvest is a HIIT work interval it is actively harmful:
/// five back-to-back max-effort intervals with no recovery is how people get
/// hurt and how the training stimulus collapses.
///
/// So a harvest costs ground. The rest phase does not need building, because
/// punch locomotion already *is* textbook active recovery — lower intensity,
/// continuous, keeping the heart rate up rather than letting it crash. Walking
/// to the next plant is the rest interval, which makes the work:rest ratio the
/// level design. A cooldown timer was rejected for the opposite reason: it would
/// teach the player that the correct action is to stop moving, which is the last
/// thing a fitness app should reward.
///
/// Runtime singleton in this project's established manager shape (static
/// Instance, GetOrCreate, Awake dedupe, System.Action events); see
/// MovementModeManager. Deliberately NOT DontDestroyOnLoad, for the same reason
/// TorchProgressionManager isn't: it is bound to scene torches.
[DefaultExecutionOrder(-45)]
public class PlantCollection : MonoBehaviour
{
    public static PlantCollection Instance { get; private set; }

    [Header("Quota")]
    [Tooltip("Plant samples that must be gathered before the next torch will light.")]
    [SerializeField] int plantsPerTorch = 5;

    [Header("Recovery")]
    [Tooltip("Ground that must be covered between harvests of different plants (m). " +
             "Roughly 30-60 s of punch-walking, so work:rest lands near 1:1-1:2.")]
    [SerializeField] float recoveryMetres = 50f;

    [Tooltip("Rest before the same plant can be re-attempted after a failed harvest (s). " +
             "Shorter than the distance gate: walking 50 m back after falling just " +
             "short is a punishment loop, and failing already cost an interval.")]
    [SerializeField] float retryRestSeconds = 20f;

    /// Fired as (gathered, required) whenever the count moves — including the
    /// reset back to zero when a torch is lit.
    public System.Action<int, int> OnChanged;

    // Identity rather than position, so re-harvesting the same bush is closed
    // off even though hundreds of its neighbours are still fair game.
    readonly HashSet<NatureInteractable> gathered = new HashSet<NatureInteractable>();

    int count;
    TorchProgressionManager progression;

    // ── Recovery gate ────────────────────────────────────────────────────────
    // Starts "ready" so the very first plant of a session is not gated behind a
    // walk the player has had no chance to do.
    float travelSinceHarvest = float.MaxValue;
    float restRemaining;
    NatureInteractable lastAttempt;

    public int   Required   => Mathf.Max(1, plantsPerTorch);
    public int   Count      => count;
    public bool  QuotaMet   => count >= Required;
    public float Progress01 => Mathf.Clamp01((float)count / Required);

    public float RecoveryMetres => Mathf.Max(1f, recoveryMetres);
    /// Metres still to cover before a different plant can be worked. 0 when ready.
    public float MetresToGo    => Mathf.Max(0f, RecoveryMetres - travelSinceHarvest);
    /// Seconds left on the retry rest for the last attempted plant. 0 when ready.
    public float RestRemaining => Mathf.Max(0f, restRemaining);

    /// True when this is the plant the player just worked, so its gate is the
    /// short retry rest rather than the distance walk. Lets the prompt say which
    /// of the two the player is actually waiting on.
    public bool IsRetryOf(NatureInteractable plant) => plant != null && plant == lastAttempt;

    public static PlantCollection GetOrCreate()
    {
        if (Instance == null)
        {
            var go = new GameObject(nameof(PlantCollection));
            Instance = go.AddComponent<PlantCollection>();
        }
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (progression != null) progression.OnTorchLit -= HandleTorchLit;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (restRemaining > 0f) restRemaining -= Time.deltaTime;
    }

    /// Ground covered on foot, pushed in by HikingPlayerController each frame.
    ///
    /// Path length, not displacement from the last harvest point: displacement
    /// would read a 25 m walk out and back as zero, and players would quickly
    /// learn to pace in circles rather than actually go anywhere.
    public void AddTravel(float metres)
    {
        if (metres <= 0f) return;
        if (travelSinceHarvest < RecoveryMetres) travelSinceHarvest += metres;
    }

    /// Banks the sample if the interval was passed, then opens the rest gate.
    /// Returns true when a sample was actually added to the quota.
    ///
    /// A failed interval deliberately does NOT consume the plant — it stays
    /// Collectable, so the cost of falling short is the time it takes to try
    /// again. In a fitness app that is not a punishment, it is the product.
    public bool CompleteHarvest(NatureInteractable plant, bool passed)
    {
        bool banked = passed && TryGather(plant);

        lastAttempt        = plant;
        travelSinceHarvest = 0f;
        restRemaining      = Mathf.Max(0f, retryRestSeconds);

        return banked;
    }

    /// Attaches to the progression manager that owns the torches. Called from
    /// TorchProgressionManager.Initialise, so there is no scene wiring.
    public void Bind(TorchProgressionManager manager)
    {
        if (progression == manager) return;
        if (progression != null) progression.OnTorchLit -= HandleTorchLit;

        progression = manager;
        if (progression != null) progression.OnTorchLit += HandleTorchLit;
    }

    // Each torch asks for its own fresh set of samples. Note that `gathered` is
    // NOT cleared: those plants are behind the player now, and forgetting them
    // would let a lap back down the trail refill the quota.
    void HandleTorchLit(Torch torch, int index)
    {
        count = 0;
        OnChanged?.Invoke(count, Required);
    }

    public bool IsGathered(NatureInteractable plant) =>
        plant != null && gathered.Contains(plant);

    public PlantState Evaluate(NatureInteractable plant)
    {
        if (plant == null) return PlantState.Unavailable;
        if (gathered.Contains(plant)) return PlantState.AlreadyGathered;
        if (QuotaMet) return PlantState.QuotaMet;

        if (progression == null || !progression.Ready) return PlantState.Unavailable;
        if (progression.AllLit) return PlantState.QuotaMet;   // nothing left to gather for

        if (progression.SectorIndexOf(plant.transform.position) != progression.NextTorchIndex)
            return PlantState.OutOfSector;

        // Rest gate. Retrying the plant you just fell short on is held by a
        // short timer; moving on to a different one costs ground.
        if (plant == lastAttempt)
        {
            if (restRemaining > 0f) return PlantState.Recovering;
        }
        else if (travelSinceHarvest < RecoveryMetres)
        {
            return PlantState.Recovering;
        }

        return PlantState.Collectable;
    }

    /// Banks a sample. Re-checks eligibility rather than trusting the caller, so
    /// a harvest that started legitimately and finished after the player crossed
    /// a gate still cannot double-count.
    public bool TryGather(NatureInteractable plant)
    {
        if (Evaluate(plant) != PlantState.Collectable) return false;

        gathered.Add(plant);
        count++;
        OnChanged?.Invoke(count, Required);
        return true;
    }

    /// Clears everything. Pairs with TorchProgressionManager.ResetProgression.
    public void ResetAll()
    {
        gathered.Clear();
        count = 0;

        travelSinceHarvest = float.MaxValue;   // ready, as at session start
        restRemaining      = 0f;
        lastAttempt        = null;

        OnChanged?.Invoke(count, Required);
    }
}
