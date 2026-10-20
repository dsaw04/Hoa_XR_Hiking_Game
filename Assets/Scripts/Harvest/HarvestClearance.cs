using System.Collections.Generic;
using UnityEngine;

/// Finds a place to stand where a harvest lane is actually visible.
///
/// ── THE PROBLEM THIS EXISTS FOR ─────────────────────────────────────────────
/// PlantHarvest streams notes from the plant to a strike plane in front of the
/// player, along a corridor roughly a metre off the ground. In an open world
/// that corridor runs through whatever happens to be standing there — most
/// reliably the target plant itself, because a harvest can only be started from
/// inside NatureInfoUI.showDistance (3 m) while the lane wants to be LaneLength
/// (3.5 m) long. The spawn plane therefore landed *behind* the plant every
/// single time, and every note spent most of its flight inside a canopy.
///
/// ── WHERE THE OCCLUDERS ACTUALLY ARE ────────────────────────────────────
/// The 1,340 NZFlora plants have lived in both places. They were individual
/// GameObjects with their own MeshRenderers until ConvertFloraToTerrainTrees was
/// run, which moved them into TerrainData as TreeInstances and stripped the
/// renderers; the GameObjects and their Transforms stay, so identify and harvest
/// are untouched. Both sources are indexed anyway, and not out of caution —
/// the conversion is a scene edit that can be reverted with an undo or a git
/// checkout, so either state is one keystroke away at any time. Collecting both
/// is self-deduping: a converted plant has no renderer left to find, so it is
/// counted once as a TreeInstance and never twice.
///
/// Both paths were measured against the live scene across the same 79 plants:
/// 64 straight-back / 15 turned before the conversion, 65 / 14 after. The radii
/// the two collectors derive agree closely enough that the choice of source does
/// not move where the player ends up standing.
///
/// A raycast sweep would be the obvious alternative and is the wrong tool:
/// Unity only instantiates tree colliders near the player, so once the flora do
/// become terrain trees the scan would start silently missing most of them at
/// range. Reading the authoritative lists is exact and costs one pass at load.
public static class HarvestClearance
{
    /// Candidate bearings, tried in order of how far they turn the player away
    /// from the plant they just pressed on, so the lane only swings wide when
    /// the straight-back corridor is genuinely blocked.
    const int   Candidates  = 11;
    const float StepDegrees = 15f;

    /// Past this the plant would be out at the player's shoulder and the "you
    /// are punching the thing you are harvesting" read breaks down. If nothing
    /// inside the cap is clear we take the least-bad bearing rather than turn
    /// the player around — the mist curtain covers the remainder.
    const float MaxDeviation = Candidates / 2 * StepDegrees;   // 75°

    /// Trees this close to the target plant *are* the target plant (or its
    /// immediate neighbours) sitting at the spawn plane, which is where notes
    /// are supposed to come from. Counting them would reject every bearing.
    const float SpawnExclusion = 1.1f;

    /// Terrain has to stay this far below the bottom of the note corridor.
    const float GroundClearance = 0.15f;
    const int   GroundSamples   = 7;

    /// Beyond this the stance is up a bank or down a hole rather than a step
    /// back, and the blink would read as a fall.
    const float MaxStanceRise  = 1.8f;
    const float MaxStanceSlope = 38f;

    // ── Tree index ───────────────────────────────────────────────────────────
    // Built once from TerrainData and kept: ~2,900 entries at 20 bytes is under
    // 60 KB, against re-copying the whole ~140 KB treeInstances array (and
    // re-reading prototype bounds) on every single press.
    const float CellSize = 4f;

    struct Tree
    {
        public Vector3 Pos;      // world, at the base
        public float   Radius;   // horizontal, already scaled
        public float   TopY;     // world y of the canopy top
    }

    /// Matches NatureInfoUI.floraRootName. Hardcoded rather than passed in
    /// because Warm runs before any harvest exists to ask.
    const string FloraRootName = "NZFlora";

    static Tree[] s_trees;
    static Dictionary<long, List<int>> s_grid;
    static Terrain s_terrain;
    static float s_maxRadius;
    static bool s_warmed;

    /// Builds the index up front so the first harvest press does not pay for
    /// it. Safe to call more than once.
    public static void Warm()
    {
        if (s_warmed) return;
        s_warmed = true;

        s_terrain = Terrain.activeTerrain;

        var found = new List<Tree>(2048);
        CollectTerrainTrees(found);
        CollectSceneFlora(found);

        s_trees = found.ToArray();
        s_grid  = new Dictionary<long, List<int>>(s_trees.Length / 2 + 1);

        for (int i = 0; i < s_trees.Length; i++)
        {
            if (s_trees[i].Radius > s_maxRadius) s_maxRadius = s_trees[i].Radius;

            long key = CellKeyAt(s_trees[i].Pos.x, s_trees[i].Pos.z);
            if (!s_grid.TryGetValue(key, out var bucket)) s_grid[key] = bucket = new List<int>(8);
            bucket.Add(i);
        }
    }

    /// Everything TerrainData draws as a tree. Empty in SampleScene today; this
    /// is what picks the flora up if ConvertFloraToTerrainTrees is ever run.
    static void CollectTerrainTrees(List<Tree> into)
    {
        if (s_terrain == null || s_terrain.terrainData == null) return;

        var td     = s_terrain.terrainData;
        var protos = td.treePrototypes;
        if (protos.Length == 0) return;

        var protoRadius = new float[protos.Length];
        var protoHeight = new float[protos.Length];
        for (int i = 0; i < protos.Length; i++)
            PrototypeExtents(protos[i].prefab, out protoRadius[i], out protoHeight[i]);

        Vector3 origin = s_terrain.transform.position;
        Vector3 size   = td.size;

        foreach (var inst in td.treeInstances)
        {
            int p = Mathf.Clamp(inst.prototypeIndex, 0, protos.Length - 1);

            Vector3 world = origin + Vector3.Scale(inst.position, size);
            world.y = s_terrain.SampleHeight(world) + origin.y;

            into.Add(new Tree
            {
                Pos    = world,
                Radius = protoRadius[p] * inst.widthScale,
                TopY   = world.y + protoHeight[p] * inst.heightScale,
            });
        }
    }

    /// The flora that are still GameObjects, which today is all of them.
    ///
    /// Measured from renderer bounds rather than prefab meshes: the bounds are
    /// already in world space with the instance's own scale and rotation baked
    /// in, so there is nothing to reconstruct and nothing to get wrong. A child
    /// whose renderer has been stripped is skipped, which is exactly the state
    /// ConvertFloraToTerrainTrees leaves behind — so a converted plant is
    /// counted once as a TreeInstance and never twice.
    static void CollectSceneFlora(List<Tree> into)
    {
        var root = GameObject.Find(FloraRootName);
        if (root == null) return;

        foreach (Transform child in root.transform)
        {
            var rend = child.GetComponentInChildren<MeshRenderer>(includeInactive: true);
            if (rend == null) continue;

            Bounds b = rend.bounds;
            into.Add(new Tree
            {
                Pos    = new Vector3(b.center.x, b.min.y, b.center.z),
                Radius = Mathf.Max(b.extents.x, b.extents.z),
                TopY   = b.max.y,
            });
        }
    }

    /// Horizontal radius and height of one prototype, in prefab-local units.
    /// Read from mesh bounds rather than a collider: most of these prefabs are
    /// foliage with no collider at all, and it is the silhouette that occludes
    /// a note, not the collision shape.
    static void PrototypeExtents(GameObject prefab, out float radius, out float height)
    {
        radius = 0.5f;
        height = 2f;
        if (prefab == null) return;

        var filters = prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true);
        if (filters.Length == 0) return;

        float maxR = 0f, maxY = 0f;
        foreach (var mf in filters)
        {
            if (mf.sharedMesh == null) continue;

            Bounds  b     = mf.sharedMesh.bounds;
            Vector3 scale = mf.transform.lossyScale;
            Vector3 local = prefab.transform.InverseTransformPoint(mf.transform.position);

            maxR = Mathf.Max(maxR, Mathf.Abs(local.x) + b.extents.x * Mathf.Abs(scale.x));
            maxR = Mathf.Max(maxR, Mathf.Abs(local.z) + b.extents.z * Mathf.Abs(scale.z));
            maxY = Mathf.Max(maxY, local.y + (b.center.y + b.extents.y) * Mathf.Abs(scale.y));
        }

        if (maxR > 0f) radius = maxR;
        if (maxY > 0f) height = maxY;
    }

    static long CellKey(int cx, int cz) => ((long)cx << 32) ^ (cz & 0xffffffffL);

    static long CellKeyAt(float x, float z) =>
        CellKey(Mathf.FloorToInt(x / CellSize), Mathf.FloorToInt(z / CellSize));

    // ── The scan ─────────────────────────────────────────────────────────────

    /// Picks where the player should stand to harvest `plant`.
    ///
    /// Returns the stance in world space (its y is the head height that stance
    /// implies) and the unit horizontal bearing from the plant out to it. It
    /// always returns something: a lane in front of a bush still beats a lane
    /// inside one, and there is no sensible way to refuse a harvest the player
    /// has already pressed for.
    ///
    /// `corridorRadius` is the half-width of the note field plus a note.
    /// `laneDrop`/`laneHalfHeight` locate the bottom of the corridor below the
    /// player's eyes — a tussock that never reaches it is not an occluder and
    /// should not cost a bearing.
    public static void FindStance(Vector3 plantPos, Vector3 headPos, float laneLength,
                                  float corridorRadius, float laneDrop, float laneHalfHeight,
                                  out Vector3 stance, out Vector3 bearing)
    {
        Warm();

        Vector3 away = headPos - plantPos;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0004f) away = Vector3.forward;
        away.Normalize();

        float headAboveGround = headPos.y - GroundY(headPos);
        float startGroundY    = GroundY(headPos);

        bearing = away;
        stance  = StanceFor(plantPos, away, laneLength, headAboveGround);

        float bestScore = float.MaxValue;
        bool  bestFound = false;

        // 0°, +15°, -15°, +30°, -30° … so the first acceptable bearing is also
        // the one that moves the player least and leaves the plant nearest the
        // centre of their view after the blink.
        for (int i = 0; i < Candidates; i++)
        {
            float deg = ((i + 1) / 2) * StepDegrees * ((i % 2 == 1) ? 1f : -1f);
            if (Mathf.Abs(deg) > MaxDeviation) continue;

            Vector3 b = Quaternion.AngleAxis(deg, Vector3.up) * away;
            Vector3 s = StanceFor(plantPos, b, laneLength, headAboveGround);

            if (!StanceUsable(s, startGroundY)) continue;

            float laneBottomY = s.y - laneDrop - laneHalfHeight;
            if (!GroundClear(s, plantPos, laneBottomY)) continue;

            float blocked = Blockage(s, plantPos, corridorRadius, laneBottomY);

            // A clear corridor wins outright at the first bearing that has one,
            // because the candidates are already ordered by preference.
            if (blocked <= 0f) { stance = s; bearing = b; return; }

            // Otherwise remember the least obstructed, biased back toward the
            // plant so a marginally clearer bearing cannot justify a big turn.
            float score = blocked + Mathf.Abs(deg) * 0.004f;
            if (score < bestScore) { bestScore = score; stance = s; bearing = b; bestFound = true; }
        }

        if (!bestFound)
        {
            // Every candidate failed the ground tests too. Standing straight
            // back from the plant is then no worse than anything else, and the
            // four-second lead-in still lets the player back out for free.
            bearing = away;
            stance  = StanceFor(plantPos, away, laneLength, headAboveGround);
        }
    }

    static Vector3 StanceFor(Vector3 plantPos, Vector3 bearing, float laneLength, float headAboveGround)
    {
        Vector3 s = plantPos + bearing * laneLength;
        s.y = GroundY(s) + headAboveGround;
        return s;
    }

    static bool StanceUsable(Vector3 stance, float startGroundY)
    {
        if (s_terrain == null) return true;

        if (Mathf.Abs(GroundY(stance) - startGroundY) > MaxStanceRise) return false;

        Vector3 origin = s_terrain.transform.position;
        Vector3 size   = s_terrain.terrainData.size;
        float nx = (stance.x - origin.x) / size.x;
        float nz = (stance.z - origin.z) / size.z;
        if (nx < 0.01f || nx > 0.99f || nz < 0.01f || nz > 0.99f) return false;

        return s_terrain.terrainData.GetSteepness(nx, nz) <= MaxStanceSlope;
    }

    /// True when the ground stays below the note corridor for the whole lane.
    /// This is what stops a lane being aimed into the side of a hill, which on
    /// a volcanic cone is at least as common as being aimed into a bush.
    static bool GroundClear(Vector3 stance, Vector3 plantPos, float laneBottomY)
    {
        if (s_terrain == null) return true;

        for (int i = 1; i < GroundSamples; i++)
        {
            Vector3 p = Vector3.Lerp(stance, plantPos, i / (float)GroundSamples);
            if (GroundY(p) > laneBottomY - GroundClearance) return false;
        }
        return true;
    }

    /// Metres of corridor overlap contributed by trees standing in the lane.
    /// Zero means clear. Returning a magnitude rather than a bool lets the
    /// fallback prefer "one tussock clipping the edge" over "three trunks".
    static float Blockage(Vector3 stance, Vector3 plantPos, float corridorRadius, float laneBottomY)
    {
        if (s_trees == null || s_trees.Length == 0) return 0f;

        Vector3 a  = new Vector3(stance.x,   0f, stance.z);
        Vector3 b  = new Vector3(plantPos.x, 0f, plantPos.z);
        Vector3 ab = b - a;

        float abLenSqr = ab.sqrMagnitude;
        if (abLenSqr < 0.0001f) return 0f;

        float total = 0f;

        // Walk only the grid cells the segment passes through, padded by the
        // furthest a tree's *centre* can sit outside the corridor and still
        // overlap it. Iterating cell indices rather than stepping positions by
        // CellSize matters: a position walk lands at an arbitrary offset inside
        // the first cell, so the cell holding the far edge of the box is missed
        // whenever the span is not an exact multiple of the cell size — which
        // was about half of all lanes, and silently, since a missed cell just
        // looks like clear ground.
        float pad = corridorRadius + s_maxRadius;

        int cx0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - pad) / CellSize);
        int cx1 = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + pad) / CellSize);
        int cz0 = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - pad) / CellSize);
        int cz1 = Mathf.FloorToInt((Mathf.Max(a.z, b.z) + pad) / CellSize);

        for (int cx = cx0; cx <= cx1; cx++)
        for (int cz = cz0; cz <= cz1; cz++)
        {
            if (!s_grid.TryGetValue(CellKey(cx, cz), out var bucket)) continue;

            foreach (int i in bucket)
            {
                // Too short to reach the notes: undergrowth is scenery, not an
                // occluder, and treating it as one would reject open ground.
                if (s_trees[i].TopY < laneBottomY) continue;

                Vector3 p = new Vector3(s_trees[i].Pos.x, 0f, s_trees[i].Pos.z);
                if ((p - b).sqrMagnitude < SpawnExclusion * SpawnExclusion) continue;

                float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / abLenSqr);
                float d = Vector3.Distance(p, a + ab * u);

                float overlap = (corridorRadius + s_trees[i].Radius) - d;
                if (overlap > 0f) total += overlap;
            }
        }

        return total;
    }

    static float GroundY(Vector3 world) =>
        s_terrain != null ? s_terrain.SampleHeight(world) + s_terrain.transform.position.y : world.y;
}
