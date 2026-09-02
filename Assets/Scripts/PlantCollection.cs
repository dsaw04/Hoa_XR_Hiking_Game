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
/// the workout *inside* each plant — how many orbs, how far the reach, how hard
/// the punch (see PlantHarvest). That keeps Gentle mode a genuinely lighter set
/// rather than a shorter game.
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

    /// Fired as (gathered, required) whenever the count moves — including the
    /// reset back to zero when a torch is lit.
    public System.Action<int, int> OnChanged;

    // Identity rather than position, so re-harvesting the same bush is closed
    // off even though hundreds of its neighbours are still fair game.
    readonly HashSet<NatureInteractable> gathered = new HashSet<NatureInteractable>();

    int count;
    TorchProgressionManager progression;

    public int   Required   => Mathf.Max(1, plantsPerTorch);
    public int   Count      => count;
    public bool  QuotaMet   => count >= Required;
    public float Progress01 => Mathf.Clamp01((float)count / Required);

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

        return progression.SectorIndexOf(plant.transform.position) == progression.NextTorchIndex
            ? PlantState.Collectable
            : PlantState.OutOfSector;
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
        OnChanged?.Invoke(count, Required);
    }
}
