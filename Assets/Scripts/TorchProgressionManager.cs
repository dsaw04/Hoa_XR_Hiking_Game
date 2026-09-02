using System.Collections.Generic;
using UnityEngine;

/// Linear progression gating for the loop trail.
///
/// Runtime singleton in the same shape as MovementModeManager (static Instance,
/// GetOrCreate, Awake dedupe, System.Action events) — this project's established
/// manager pattern, minus DontDestroyOnLoad; see the note on Awake.
///
/// ── WHY THE PROGRESSION AXIS IS ANGULAR, NOT RADIAL ──────────────────────────
/// WalkingTrackGenerator carves the trail as a circle at a CONSTANT radius
/// (LOOP_RADIUS = 0.14 normalised ≈ 140 m). The terrain's distance-from-centre
/// bands — vegetation at 70/130/200/340 m, textures at 45/100/220 m — are
/// concentric rings lying *perpendicular* to the direction of travel, so a
/// walker on the loop never crosses one. Gating on radius would therefore be
/// either permanently open or permanently shut for the whole hike.
///
/// Progress around a loop is angular, so that is what this tracks. The loop is
/// cut into one SECTOR per torch, and a torch will only light once the player
/// has gathered PlantCollection.Required plant samples from inside the sector
/// leading up to it — see SectorIndexOf.
///
/// ── WHAT ACTUALLY HOLDS THE PLAYER ───────────────────────────────────────────
/// The fog walls do, physically: TorchGateMist stands a line of box colliders
/// from the crater centre out past the far corner of the terrain at every unlit
/// gate, which seals the player into a wedge that widens by one sector per torch
/// lit. There is no momentum-refusal gate any more; the collider is the whole
/// mechanism, and HikingPlayerController simply drops momentum on contact so the
/// player is not left grinding against it.
///
/// Progress is still accumulated here as a signed, unwrapped angle (it can
/// exceed 360° and go negative) because it is a useful readout and because
/// SectorIndexOf needs the spawn bearing it is built on. It only accrues while
/// the player is within a radial band around the trail — otherwise cutting
/// straight across the crater would sweep ~180° of angle in a few seconds.
[DefaultExecutionOrder(-50)]
public class TorchProgressionManager : MonoBehaviour
{
    public static TorchProgressionManager Instance { get; private set; }

    /// Mirrors WalkingTrackGenerator.LOOP_RADIUS. Duplicated rather than shared
    /// because that generator lives in the Editor assembly and runtime code
    /// cannot reference it — keep the two in step if the trail is re-cut.
    public const float LoopRadiusNormalised = 0.14f;

    // ── Layout ───────────────────────────────────────────────────────────────
    [Header("Torch Layout")]
    [Tooltip("Number of torches spaced around the loop.")]
    [SerializeField] int torchCount = 5;

    [Tooltip("Optional explicit gate angles (deg of loop travelled from spawn). " +
             "Leave empty to space `torchCount` torches evenly, ending short of " +
             "a full lap so the last torch is not sitting on the spawn point.")]
    [SerializeField] float[] gateAnglesDeg = new float[0];

    [Tooltip("+1 walks the loop anticlockwise from spawn, -1 clockwise.")]
    [SerializeField] int loopDirection = 1;

    [Tooltip("Metres outward from the trail centreline to stand each torch, so " +
             "it marks the edge of the path rather than blocking it.")]
    [SerializeField] float torchTrailOffset = 6f;

    [Tooltip("Metres back along the trail from the gate to stand the torch. The " +
             "fog wall runs through the gate angle itself, so a torch placed " +
             "exactly on it would be buried in fog and walled off from the " +
             "player who is meant to reach out and light it.")]
    [SerializeField] float torchGateSetback = 3.5f;

    // ── Gating ───────────────────────────────────────────────────────────────
    [Header("Gating")]
    [Tooltip("Half-width (m) of the radial band around the trail in which " +
             "angular progress counts. Stops the player shortcutting across the crater.")]
    [SerializeField] float trackBandMetres = 60f;

    [Header("Presentation")]
    [Tooltip("Stand a fog wall across the map at every unlit gate.")]
    [SerializeField] bool showFogWalls = true;

    [Tooltip("How far out (m) the fog walls run from the crater centre. Leave at " +
             "0 to derive it from the terrain — it must clear the far corner, or " +
             "the player can simply walk round the outer end.")]
    [SerializeField] float wallOuterRadius = 0f;

    // ── Persistence ──────────────────────────────────────────────────────────
    [Header("Persistence")]
    [Tooltip("Off by default: progression resets each launch, exactly as " +
             "MovementModeManager.SessionConfirmed does. Flip on to carry lit " +
             "torches across sessions — Capture/RestoreState already handle it.")]
    [SerializeField] bool persistAcrossSessions = false;

    const string PrefState = "torch.progress1";

    // ── Events ───────────────────────────────────────────────────────────────
    /// Fired when a torch is lit, with the torch and its index. Subscribe for
    /// mist dissipation, UI, audio stingers, achievements…
    public System.Action<Torch, int> OnTorchLit;

    /// Fired when the final torch is lit and the loop is fully open.
    public System.Action OnAllTorchesLit;

    // ── State ────────────────────────────────────────────────────────────────
    readonly List<Torch> torches = new List<Torch>();

    Vector3   centre;              // terrain centre, XZ only
    float     loopRadius = 140f;
    float     spawnAngleDeg;       // world bearing the player started at
    float     progressDeg;         // unwrapped angle travelled along the loop
    float     lastAngleDeg;
    bool      hasLastAngle;
    bool      initialised;

    Transform player, head;
    Terrain   terrain;

    /// One per torch; entry is null once that torch's wall has been dropped.
    TorchGateMist[] walls;
    TorchPrompt     prompt;
    PlantCollection collection;

    // ── Read-only API ────────────────────────────────────────────────────────
    public IReadOnlyList<Torch> Torches   => torches;
    public float ProgressDeg              => progressDeg;
    public int   LitCount                 { get; private set; }
    public int   TorchCount               => torches.Count;
    public bool  AllLit                   => torches.Count > 0 && LitCount >= torches.Count;
    /// True once Initialise has placed the torches; until then the loop geometry
    /// is meaningless and SectorIndexOf must not be trusted.
    public bool  Ready                    => initialised;

    /// Index of the next torch that must be lit, or TorchCount when all are lit.
    public int NextTorchIndex => Mathf.Min(LitCount, torches.Count);

    /// Loop angle (deg from spawn) the active gate sits at; +∞ once all lit.
    public float NextGateAngleDeg =>
        NextTorchIndex < torches.Count ? torches[NextTorchIndex].GateAngleDeg
                                       : float.PositiveInfinity;

    /// Metres of trail left before the active gate. Negative once past it.
    public float ArcToGateMetres
    {
        get
        {
            if (AllLit || torches.Count == 0) return float.PositiveInfinity;
            return (NextGateAngleDeg - progressDeg) * Mathf.Deg2Rad * loopRadius;
        }
    }

    // ── Setup ────────────────────────────────────────────────────────────────
    public static TorchProgressionManager GetOrCreate()
    {
        if (Instance == null)
        {
            var go = new GameObject(nameof(TorchProgressionManager));
            Instance = go.AddComponent<TorchProgressionManager>();
        }
        return Instance;
    }

    // Deliberately NOT DontDestroyOnLoad, unlike MovementModeManager. This
    // manager owns scene objects — the torches and the mist curtain — so
    // surviving a scene load would leave it holding destroyed references and
    // reporting progress for torches that no longer exist. Anything that needs
    // to outlive the scene travels through Capture/RestoreState instead.
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        // The prompt is a root object like the torches and the walls, so it does
        // not go with the manager automatically — take it down explicitly rather
        // than leaving a sign floating over a torch that no longer has an owner.
        if (prompt != null) Destroy(prompt.gameObject);

        if (Instance == this) Instance = null;
    }

    /// +1 / −1, never 0 — guards against loopDirection being zeroed in the
    /// inspector, which would otherwise freeze all progress silently.
    float Dir => loopDirection < 0 ? -1f : 1f;

    /// Places the torches and starts tracking. Safe to call more than once.
    /// Called from HikingPlayerController.BeginSession, so no scene wiring.
    public void Initialise(Terrain terrain, Transform player, Transform head, OVRCameraRig rig)
    {
        if (initialised) return;

        this.terrain = terrain;
        this.player  = player;
        this.head    = head;

        if (terrain == null || player == null)
        {
            Debug.LogWarning($"[{nameof(TorchProgressionManager)}] No terrain or player — " +
                             "progression gating disabled for this session.");
            return;
        }

        Vector3 tPos  = terrain.transform.position;
        Vector3 tSize = terrain.terrainData.size;
        centre     = new Vector3(tPos.x + tSize.x * 0.5f, 0f, tPos.z + tSize.z * 0.5f);
        loopRadius = tSize.x * LoopRadiusNormalised;

        spawnAngleDeg = AngleOf(player.position);
        lastAngleDeg  = spawnAngleDeg;
        hasLastAngle  = true;

        TorchAudio.EnsureListener(head);

        // Before the torches, so the first torch's bowl has a quota to report
        // the moment it is built.
        collection = PlantCollection.GetOrCreate();
        collection.Bind(this);

        BuildTorches(rig);

        if (persistAcrossSessions) Load();

        initialised = true;

        walls = new TorchGateMist[torches.Count];
        SyncWalls(instant: true);
        prompt = TorchPrompt.Create(head);
    }

    void BuildTorches(OVRCameraRig rig)
    {
        float[] angles = ResolveGateAngles();

        // The torch stands on the APPROACH side of its own fog wall. It keeps
        // the gate's angle as its GateAngleDeg — that is where the wall goes —
        // but is placed a few metres short of it so the player can walk up to
        // it, see it, and reach the bowl without the barrier in between.
        float setbackDeg = torchGateSetback / Mathf.Max(1f, loopRadius) * Mathf.Rad2Deg;

        for (int i = 0; i < angles.Length; i++)
        {
            Vector3 pos = PointOnLoop(angles[i] - setbackDeg, loopRadius + torchTrailOffset);
            torches.Add(Torch.Create(i, angles[i], pos, head, rig));
        }

        Debug.Log($"[{nameof(TorchProgressionManager)}] Placed {torches.Count} torches " +
                  $"at {string.Join("°, ", angles)}° around a {loopRadius:F0} m loop " +
                  $"({2f * Mathf.PI * loopRadius:F0} m circumference).");
    }

    /// Explicit angles win; otherwise space `torchCount` evenly with one spare
    /// segment at the end, so lighting the last torch still leaves a short walk
    /// home and no torch spawns on top of the player.
    float[] ResolveGateAngles()
    {
        if (gateAnglesDeg != null && gateAnglesDeg.Length > 0)
        {
            var copy = (float[])gateAnglesDeg.Clone();
            System.Array.Sort(copy);
            return copy;
        }

        int count  = Mathf.Max(1, torchCount);
        float step = 360f / (count + 1);
        var angles = new float[count];
        for (int i = 0; i < count; i++) angles[i] = step * (i + 1);
        return angles;
    }

    // ── Per-frame tracking ───────────────────────────────────────────────────
    void Update()
    {
        if (!initialised || player == null) return;

        Vector3 p = player.position;
        float r   = RadiusOf(p);

        // Angle is meaningless at the exact centre; skip rather than emit NaN.
        if (r > 1f)
        {
            float a = AngleOf(p);
            if (!hasLastAngle)
            {
                lastAngleDeg = a;
                hasLastAngle = true;
            }
            else
            {
                float delta = Mathf.DeltaAngle(lastAngleDeg, a) * Dir;
                lastAngleDeg = a;

                // Off the trail band (crater floor, outer lowland): keep tracking
                // the bearing so re-entry does not jump, but bank no progress.
                bool onTrackBand = Mathf.Abs(r - loopRadius) <= trackBandMetres;
                if (onTrackBand)
                    progressDeg = Mathf.Clamp(progressDeg + delta, -180f, 400f);
            }
        }
    }

    // ── Sectors ──────────────────────────────────────────────────────────────

    /// Loop angle of the rearmost wall — the last torch's gate, expressed as a
    /// negative angle because that wall sits *behind* the trailhead. It anchors
    /// the angular domain: every point on the map maps to exactly one sector.
    public float RearAngleDeg =>
        torches.Count > 0 ? torches[torches.Count - 1].GateAngleDeg - 360f : -180f;

    /// Loop-travel angle of a world point, in degrees from the spawn bearing,
    /// wrapped into (RearAngleDeg, RearAngleDeg + 360].
    ///
    /// Note this is an ABSOLUTE position on the loop, unlike progressDeg which
    /// is the unwrapped distance the player has actually walked. Sector
    /// membership is a property of where a plant *is*, not of how the player
    /// got there.
    public float LoopAngleOf(Vector3 world)
    {
        float d = Mathf.DeltaAngle(spawnAngleDeg, AngleOf(world)) * Dir;   // (-180, 180]
        if (d <= RearAngleDeg) d += 360f;
        return d;
    }

    /// Which torch's sector a world point falls in. Sector i runs from gate
    /// i−1 up to gate i, with sector 0 reaching back to the rear wall — so the
    /// starting wedge is two segments wide and the rest are one each.
    public int SectorIndexOf(Vector3 world)
    {
        if (torches.Count == 0) return 0;

        float a = LoopAngleOf(world);
        for (int i = 0; i < torches.Count; i++)
            if (a <= torches[i].GateAngleDeg) return i;

        return torches.Count - 1;   // exactly on the far side of the last gate
    }

    // ── Lighting ─────────────────────────────────────────────────────────────

    /// Single authority for lighting a torch. Enforces sequential order (so
    /// order holds even if a player somehow reaches a later torch off-trail) and
    /// the plant-sample quota.
    public bool TryLight(Torch torch)
    {
        if (torch == null || torch.IsLit) return false;
        if (torch.Index != NextTorchIndex) return false;
        if (collection != null && !collection.QuotaMet) return false;

        torch.SetLit(true);
        LitCount = CountLit();

        DropWall(torch.Index);

        OnTorchLit?.Invoke(torch, torch.Index);
        if (AllLit) OnAllTorchesLit?.Invoke();

        if (persistAcrossSessions) Save();
        return true;
    }

    int CountLit()
    {
        int n = 0;
        foreach (var t in torches) if (t.IsLit) n++;
        return n;
    }

    // ── Fog walls ────────────────────────────────────────────────────────────

    /// Every unlit gate is walled at once, not just the active one.
    ///
    /// The trail is a closed circle, so one wall blocks nothing — a player can
    /// walk the other way round and arrive behind it. All five walls together
    /// seal a wedge that widens by one sector per torch. This is affordable
    /// because a wall is a line of box colliders plus a single small mesh that
    /// is only drawn when the player is within ~45 m of it (see TorchGateMist).
    void SyncWalls(bool instant)
    {
        if (!showFogWalls || walls == null) return;

        float outer = wallOuterRadius > 1f ? wallOuterRadius : AutoWallRadius();

        for (int i = 0; i < torches.Count && i < walls.Length; i++)
        {
            bool wanted = !torches[i].IsLit;

            if (wanted && walls[i] == null)
            {
                walls[i] = TorchGateMist.Create(
                    mapCentre: centre,
                    outward: RadialDirection(torches[i].GateAngleDeg),
                    outerRadius: outer,
                    terrain: terrain,
                    head: head);
            }
            else if (!wanted && walls[i] != null)
            {
                if (instant) walls[i].DestroyNow();
                else         walls[i].Dissipate();
                walls[i] = null;
            }
        }
    }

    void DropWall(int index)
    {
        if (walls == null || index < 0 || index >= walls.Length) return;
        if (walls[index] == null) return;

        walls[index].Dissipate();
        walls[index] = null;
    }

    /// Far enough to clear the corner of a square terrain from its centre,
    /// plus margin. Anything shorter leaves a gap to walk round at the outer end.
    float AutoWallRadius()
    {
        if (terrain == null) return 750f;

        Vector3 size = terrain.terrainData.size;
        return Mathf.Sqrt(size.x * size.x + size.z * size.z) * 0.5f + 25f;
    }

    // ── Loop geometry ────────────────────────────────────────────────────────
    // WalkingTrackGenerator parameterises the loop as (cx + r·cos a, cz + r·sin a),
    // so x maps to cosine and z to sine. Keep these in the same convention.

    float AngleOf(Vector3 world) =>
        Mathf.Atan2(world.z - centre.z, world.x - centre.x) * Mathf.Rad2Deg;

    float RadiusOf(Vector3 world)
    {
        float dx = world.x - centre.x, dz = world.z - centre.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// Outward (radial) direction at a given loop angle — the axis a gate spans.
    Vector3 RadialDirection(float loopAngleDeg)
    {
        float rad = (spawnAngleDeg + loopAngleDeg * Dir) * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
    }

    /// World point at `loopAngleDeg` of travel from spawn, dropped onto terrain.
    Vector3 PointOnLoop(float loopAngleDeg, float radius)
    {
        Vector3 p = centre + RadialDirection(loopAngleDeg) * radius;
        if (terrain != null)
            p.y = terrain.SampleHeight(p) + terrain.transform.position.y;
        return p;
    }

    // ── State snapshot ───────────────────────────────────────────────────────

    /// Serialisable progression snapshot. Persistence is off by default, but the
    /// capture/restore path is complete so enabling it is a single flag.
    [System.Serializable]
    public class State
    {
        public int    version = 1;
        public bool[] lit;
        public float  progressDeg;
    }

    public State CaptureState()
    {
        var s = new State { lit = new bool[torches.Count], progressDeg = progressDeg };
        for (int i = 0; i < torches.Count; i++) s.lit[i] = torches[i].IsLit;
        return s;
    }

    /// Applies a snapshot. Torches restore without the ignition whoosh, so a
    /// resumed session does not fire five bursts of audio on load.
    public void RestoreState(State s)
    {
        if (s?.lit == null) return;

        // Progression is strictly sequential, so a valid snapshot is always a
        // run of lit torches followed by unlit ones. Restoring only that prefix
        // means a corrupted or hand-edited save degrades to "less progress"
        // rather than to a state where NextTorchIndex points at a lit torch and
        // the player can never advance again.
        for (int i = 0; i < torches.Count; i++)
        {
            bool lit = i < s.lit.Length && s.lit[i];
            if (lit && i > 0 && !torches[i - 1].IsLit) lit = false;
            torches[i].SetLit(lit, instant: true);
        }

        LitCount    = CountLit();
        progressDeg = s.progressDeg;

        // No fade: five simultaneous dissolves on load would be nonsense.
        SyncWalls(instant: true);
    }

    void Save()
    {
        PlayerPrefs.SetString(PrefState, JsonUtility.ToJson(CaptureState()));
        PlayerPrefs.Save();
    }

    void Load()
    {
        string json = PlayerPrefs.GetString(PrefState, "");
        if (string.IsNullOrEmpty(json)) return;

        var s = JsonUtility.FromJson<State>(json);
        if (s != null && s.version == 1) RestoreState(s);
    }

    /// Clears saved progression. Useful from a future "restart hike" button.
    public void ResetProgression()
    {
        foreach (var t in torches) t.SetLit(false, instant: true);
        LitCount     = 0;
        progressDeg  = 0f;
        hasLastAngle = false;

        if (collection != null) collection.ResetAll();

        PlayerPrefs.DeleteKey(PrefState);
        SyncWalls(instant: true);
    }
}
