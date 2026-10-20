using System.Collections;
using UnityEngine;

/// The mist that closes in behind a harvest lane for the length of the interval.
///
/// ── WHY, WHEN THE LANE IS ALREADY AIMED AT CLEAR GROUND ─────────────────────
/// HarvestClearance guarantees nothing is standing *in* the corridor. It cannot
/// do anything about what is standing 8 m past the end of it, and a hillside of
/// NZ bush behind a 20 cm note is a busy, high-contrast background to read a
/// fast-moving target against. This is the same job a rhythm game does by
/// putting the player in a black room; here the clearing the player is already
/// standing in becomes the room instead, which costs no teleport and no loss of
/// place — you can still see the plant, the trail and the sky.
///
/// ── WHY A CURTAIN AND NOT FOG OR AN AMBIENT DIP ─────────────────────────────
/// RenderSettings.fog is off in this scene, and switching it on mid-play asks
/// URP for fog shader variants that may have been stripped from the Android
/// build — a hitch or a pink world, discovered on the headset. Dropping ambient
/// is free but barely moves under a strong directional light. A curtain is the
/// one option whose cost is known in advance and bounded: one ~200-triangle
/// mesh in a single transparent draw, alive only during an interval, subtending
/// maybe a third of the view rather than overdrawing the whole frame.
///
/// Technique is TorchGateMist's, shared rather than copied: vertex alpha for
/// softness, one scrolling tileable noise texture for movement, no particles.
public class HarvestMist : MonoBehaviour
{
    // ── Shape ────────────────────────────────────────────────────────────────
    /// Set back from the plant so the plant reads as a silhouette in front of
    /// the mist rather than being swallowed by it. Notes spawn at the plant, so
    /// they are in front of the curtain for their whole flight.
    const float StandoffBehindPlant = 1.6f;
    const float HalfAngle = 34f;    // of arc, either side of the lane
    const float Height    = 5.0f;
    const float SinkDepth = 1.2f;   // base pushed under the ground, so no gap
    const int   Columns   = 12;
    const int   Rows      = 4;
    const float MetresPerTile = 5f;

    /// Deliberately short of opaque. The point is to drop the *contrast* of the
    /// background, not to delete the hillside — a player who cannot see where
    /// they are has been teleported to a room after all, just an ugly one.
    const float Density = 0.72f;

    /// Vertical alpha profile, bottom → top: densest across the note field and
    /// gone by the top edge so the curtain has no visible upper border.
    static readonly float[] RowAlpha    = { 0.62f, 1.00f, 0.70f, 0.00f };
    static readonly float[] RowHeight01 = { 0.00f, 0.38f, 0.72f, 1.00f };

    static readonly Color MistTint = new Color(0.74f, 0.80f, 0.86f);

    Material mat;
    Renderer rend;
    Terrain  terrain;
    float    alpha;       // current global multiplier, 0-1
    bool     closing;

    /// Raises a curtain centred on `plantPos`, arced around `stance` so it
    /// faces the player square-on wherever they are standing.
    public static HarvestMist Create(Vector3 stance, Vector3 plantPos, float fadeInSeconds)
    {
        var go = new GameObject("HarvestMist");
        var m  = go.AddComponent<HarvestMist>();
        m.Build(stance, plantPos);
        m.StartCoroutine(m.FadeTo(1f, Mathf.Max(0.1f, fadeInSeconds)));
        return m;
    }

    void Build(Vector3 stance, Vector3 plantPos)
    {
        terrain = Terrain.activeTerrain;

        Vector3 flat = plantPos - stance;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;

        float radius = flat.magnitude + StandoffBehindPlant;
        Vector3 dir  = flat.normalized;

        // Centred on the player, so every column of the arc is the same
        // distance from the eye and the curtain has no near corner to catch the
        // light differently from its middle.
        transform.position = new Vector3(stance.x, 0f, stance.z);

        int vertCount = (Columns + 1) * Rows;
        var verts = new Vector3[vertCount];
        var uvs   = new Vector2[vertCount];
        var cols  = new Color[vertCount];
        var norms = new Vector3[vertCount];

        for (int c = 0; c <= Columns; c++)
        {
            float u   = (float)c / Columns;
            float deg = Mathf.Lerp(-HalfAngle, HalfAngle, u);

            Vector3 outward = Quaternion.AngleAxis(deg, Vector3.up) * dir;
            Vector3 basePt  = transform.position + outward * radius;
            float   groundY = GroundY(basePt);

            // Fade to nothing at both ends so the arc has no hard side edge —
            // it should read as the clearing closing in, not as a billboard.
            float edge01   = Mathf.Min(u, 1f - u) / 0.22f;
            float colAlpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge01));

            for (int row = 0; row < Rows; row++)
            {
                int i = c * Rows + row;

                Vector3 p = basePt;
                p.y = groundY - SinkDepth + RowHeight01[row] * Height;

                verts[i] = p - transform.position;
                // U from arc length rather than column index, so the texture is
                // pinned to the world and stays put as the player's head moves.
                uvs[i]   = new Vector2(deg * Mathf.Deg2Rad * radius / MetresPerTile, RowHeight01[row]);
                cols[i]  = new Color(MistTint.r, MistTint.g, MistTint.b, colAlpha * RowAlpha[row]);
                norms[i] = -outward;
            }
        }

        var mesh = new Mesh { name = "HarvestMist" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(cols);
        mesh.SetNormals(norms);
        mesh.SetTriangles(BuildTriangles(), 0);
        mesh.RecalculateBounds();

        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;

        rend = gameObject.AddComponent<MeshRenderer>();
        mat  = TorchGateMist.BuildMistMaterial("HarvestMist");
        rend.material                  = mat;
        rend.shadowCastingMode         = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows            = false;
        rend.lightProbeUsage           = UnityEngine.Rendering.LightProbeUsage.Off;
        rend.reflectionProbeUsage      = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        rend.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

        SetAlpha(0f);
    }

    /// Single-sided. Unlike the gate wall the player never gets behind this one
    /// — drifting AbortDrift metres cancels the harvest first — so the back
    /// faces would be paying fill rate for a view nobody reaches.
    static int[] BuildTriangles()
    {
        var t = new int[Columns * (Rows - 1) * 6];
        int n = 0;

        for (int c = 0; c < Columns; c++)
        for (int r = 0; r < Rows - 1; r++)
        {
            int bl =  c      * Rows + r;
            int tl =  c      * Rows + r + 1;
            int br = (c + 1) * Rows + r;
            int tr = (c + 1) * Rows + r + 1;

            t[n++] = bl; t[n++] = tl; t[n++] = br;
            t[n++] = br; t[n++] = tl; t[n++] = tr;
        }
        return t;
    }

    void Update()
    {
        if (mat == null) return;

        // Slow drift, so the curtain reads as moving air rather than a sheet.
        TorchGateMist.SetOffset(mat, new Vector2(Time.time * 0.030f, Time.time * 0.010f));
    }

    /// Thins the mist out and takes the object with it. Called whether the
    /// interval passed, failed or was abandoned — the curtain is a reading aid,
    /// not a reward, so it should never linger to mark a result.
    public void Dismiss(float seconds = 0.8f)
    {
        if (closing) return;
        closing = true;
        StartCoroutine(Close(Mathf.Max(0.05f, seconds)));
    }

    IEnumerator FadeTo(float target, float seconds)
    {
        float from = alpha;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            if (closing) yield break;
            SetAlpha(Mathf.Lerp(from, target, t / seconds));
            yield return null;
        }
        if (!closing) SetAlpha(target);
    }

    IEnumerator Close(float seconds)
    {
        float from = alpha;
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            float k = t / seconds;
            SetAlpha(Mathf.Lerp(from, 0f, k));
            // Stretch vertically as it goes, so it burns off upward instead of
            // simply being switched off — same trick as the gate fog.
            TorchGateMist.SetTiling(mat, new Vector2(1f, Mathf.Lerp(1f, 0.6f, k)));
            yield return null;
        }
        Destroy(gameObject);
    }

    void SetAlpha(float a)
    {
        alpha = Mathf.Clamp01(a);
        if (mat != null) TorchGateMist.SetTint(mat, new Color(1f, 1f, 1f, alpha * Density));
        if (rend != null) rend.enabled = alpha > 0.01f;
    }

    float GroundY(Vector3 world) =>
        terrain != null ? terrain.SampleHeight(world) + terrain.transform.position.y : 0f;

    void OnDestroy()
    {
        if (mat != null) Destroy(mat);
    }
}
