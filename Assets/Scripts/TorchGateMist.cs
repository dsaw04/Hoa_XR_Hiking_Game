using System.Collections;
using UnityEngine;

/// The fog wall standing across the map at an unlit torch's gate.
///
/// The fog IS the gate: a radial line of box colliders runs from the crater
/// centre out past the far corner of the terrain, so there is no walking round
/// either end of it. Lighting the gate's torch drops the wall.
///
/// ── WHY EVERY UNLIT GATE IS WALLED ───────────────────────────────────────────
/// The trail is a closed circle, so a single wall across it blocks nothing —
/// the player can turn round and walk the long way to its far side. With a wall
/// at every unlit gate the player is sealed into a wedge, 120° wide at the
/// trailhead and widening by one 60° sector per torch lit, and the "walk
/// backwards into the final sector" exploit is closed by geometry instead of by
/// bookkeeping. The rear of that wedge is the last torch's wall, which is why
/// the way back stays shut until the loop is finished.
///
/// ── WHY THE VISIBLE FOG IS A SLIDING WINDOW ──────────────────────────────────
/// The barrier is ~730 m long and Quest 3 is fill-rate bound; drawing 730 m of
/// transparent fog would be unaffordable, and a particle curtain that long
/// absurdly so. Only a ~44 m window of curtain mesh is ever built — one
/// ~200-triangle mesh in one draw call — recentred on the player's position
/// along the wall when they drift far enough, and enabled only while they are
/// within DrawDistance of the wall plane. The colliders, which cost nothing to
/// leave switched on, run the whole length. Softness comes from vertex alpha and
/// a scrolling tileable noise texture rather than from particle count.
[DisallowMultipleComponent]
public class TorchGateMist : MonoBehaviour
{
    // ── Visible curtain ──────────────────────────────────────────────────────
    const float Width        = 44f;   // width of the drawn window, along the wall
    const float Height       = 7.5f;
    const float SinkDepth    = 1.5f;  // push the base under the ground so no gap shows
    const int   Columns      = 18;
    const int   Rows         = 4;
    // Was 45 m. One of these exists per unlit gate (up to torchCount, minus
    // whichever are already lit), each polling this every frame — a smaller
    // radius means fewer of them are drawing at any given moment on the hike.
    const float DrawDistance = 25f;
    const float RebuildDrift = 8f;    // metres of travel along the wall before recentring
    const float FadeSeconds  = 2.5f;
    const float MetresPerTile = 6f;   // noise texture repeat along the wall

    // ── Physical barrier ─────────────────────────────────────────────────────
    const int   Segments      = 14;
    const float SegmentOverlap = 1f;  // so consecutive boxes cannot leave a seam
    const float Thickness      = 1.2f;
    const float Overhead       = 12f; // above the highest ground the segment covers
    const float Underfoot      = 8f;  // below the lowest, so no gap on a slope
    const int   HeightSamples  = 9;

    /// Vertical alpha profile, bottom → top. Densest just above the ground and
    /// gone by the top edge, so the curtain has no visible upper border.
    static readonly float[] RowAlpha    = { 0.55f, 1.00f, 0.62f, 0.00f };
    static readonly float[] RowHeight01 = { 0.00f, 0.34f, 0.70f, 1.00f };

    static readonly Color MistTint = new Color(0.78f, 0.84f, 0.88f);   // cool, slightly blue

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId     = Shader.PropertyToID("_Color");
    static readonly int BaseMapId   = Shader.PropertyToID("_BaseMap");
    static readonly int MainTexId   = Shader.PropertyToID("_MainTex");

    Material  mat;
    Renderer  rend;
    Transform curtain;
    Mesh      mesh;
    Transform head;
    Terrain   terrain;

    Vector3 mapCentre;      // y is forced to 0; all heights are absolute
    Vector3 outward;        // horizontal, along the wall
    Vector3 normal;         // horizontal, across the wall
    float   outerRadius;

    BoxCollider[] barrier;

    Vector3[] verts;
    Vector2[] uvs;
    Color[]   cols;
    Vector3[] norms;
    int[]     tris;

    float windowCentre;
    bool  hasWindow;
    bool  dissipating;

    /// `outward` is the horizontal direction the wall runs in from the map
    /// centre; `outerRadius` must clear the far corner of the terrain.
    public static TorchGateMist Create(Vector3 mapCentre, Vector3 outward, float outerRadius,
                                       Terrain terrain, Transform head)
    {
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f) outward = Vector3.right;
        outward.Normalize();

        mapCentre.y = 0f;   // heights come from the terrain, never from the root

        var go = new GameObject("TorchGateFog");
        go.transform.position = mapCentre;

        var m = go.AddComponent<TorchGateMist>();
        m.head        = head;
        m.terrain     = terrain;
        m.mapCentre   = mapCentre;
        m.outward     = outward;
        m.normal      = Vector3.Cross(Vector3.up, outward).normalized;
        m.outerRadius = Mathf.Max(10f, outerRadius);

        m.BuildBarrier();
        m.BuildCurtain();
        return m;
    }

    // ── Physical barrier ─────────────────────────────────────────────────────

    /// One box per segment, each only as tall as the ground it spans plus
    /// clearance. A single box for the whole 730 m would have to be hundreds of
    /// metres tall to cover the volcanic cone, and would sit absurdly far above
    /// the lowland at either end.
    void BuildBarrier()
    {
        float segLength = outerRadius / Segments;
        barrier = new BoxCollider[Segments];

        for (int i = 0; i < Segments; i++)
        {
            float r0 = i * segLength;
            float r1 = r0 + segLength;

            float minY = float.MaxValue, maxY = float.MinValue;
            for (int s = 0; s < HeightSamples; s++)
            {
                float r = Mathf.Lerp(r0, r1, (float)s / (HeightSamples - 1));
                float y = GroundY(mapCentre + outward * r);
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            float top    = maxY + Overhead;
            float bottom = minY - Underfoot;

            var seg = new GameObject($"FogBarrier_{i:00}");
            seg.transform.SetPositionAndRotation(
                mapCentre + outward * ((r0 + r1) * 0.5f) + Vector3.up * ((top + bottom) * 0.5f),
                Quaternion.LookRotation(outward, Vector3.up));
            seg.transform.SetParent(transform, worldPositionStays: true);

            var box = seg.AddComponent<BoxCollider>();
            box.size = new Vector3(Thickness, top - bottom, segLength + SegmentOverlap);
            barrier[i] = box;
        }
    }

    void DisableBarrier()
    {
        if (barrier == null) return;
        foreach (var box in barrier)
            if (box != null) box.enabled = false;
    }

    float GroundY(Vector3 world) =>
        terrain != null ? terrain.SampleHeight(world) + terrain.transform.position.y : 0f;

    // ── Visible curtain ──────────────────────────────────────────────────────
    void BuildCurtain()
    {
        var go = new GameObject("FogCurtain");
        go.transform.SetParent(transform, false);
        curtain = go.transform;

        mesh = new Mesh { name = "GateFog" };
        mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        rend = go.AddComponent<MeshRenderer>();
        mat  = BuildMistMaterial();
        rend.material                     = mat;
        rend.shadowCastingMode            = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows                = false;
        rend.lightProbeUsage               = UnityEngine.Rendering.LightProbeUsage.Off;
        rend.reflectionProbeUsage          = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        rend.motionVectorGenerationMode    = MotionVectorGenerationMode.ForceNoMotion;
        rend.enabled                       = false;
    }

    /// Rebuilds the drawn window centred `centreRadius` metres out along the
    /// wall, with its base following the terrain so the fog sits on uneven
    /// ground instead of hovering or sinking through it.
    void RebuildWindow(float centreRadius)
    {
        int vertCount = (Columns + 1) * Rows;
        if (verts == null)
        {
            verts = new Vector3[vertCount];
            uvs   = new Vector2[vertCount];
            cols  = new Color[vertCount];
            norms = new Vector3[vertCount];
            tris  = BuildTriangles();
        }

        Vector3 origin = transform.position;

        for (int c = 0; c <= Columns; c++)
        {
            float u = (float)c / Columns;
            float r = centreRadius + (u - 0.5f) * Width;

            Vector3 basePoint = mapCentre + outward * r;
            float groundY = GroundY(basePoint);

            // Fade to nothing at both ends of the window so it has no hard side
            // edge — the wall carries on, this is just where we stop drawing it.
            float edge01   = Mathf.Min(u, 1f - u) / 0.18f;
            float colAlpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(edge01));

            for (int row = 0; row < Rows; row++)
            {
                int i = c * Rows + row;

                Vector3 p = basePoint;
                p.y = groundY - SinkDepth + RowHeight01[row] * Height;

                verts[i] = p - origin;
                // U comes from the world radius, not from the column index, so
                // the texture stays pinned to the ground when the window
                // recentres instead of swimming along the wall.
                uvs[i]   = new Vector2(r / MetresPerTile, RowHeight01[row]);
                cols[i]  = new Color(MistTint.r, MistTint.g, MistTint.b, colAlpha * RowAlpha[row]);
                norms[i] = normal;
            }
        }

        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(cols);
        mesh.SetNormals(norms);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        windowCentre = centreRadius;
        hasWindow    = true;
    }

    /// Two-sided: the player walks up to the fog from either side of it, and a
    /// single-sided curtain vanishes from the back.
    static int[] BuildTriangles()
    {
        var t = new int[Columns * (Rows - 1) * 12];
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

            t[n++] = br; t[n++] = tl; t[n++] = bl;   // reversed winding
            t[n++] = tr; t[n++] = tl; t[n++] = br;
        }
        return t;
    }

    // Particles/Unlit rather than plain Unlit: it is the URP shader that
    // multiplies in vertex colour, which is where all of the softness lives.
    static Material BuildMistMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                  ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { name = "GateFog" };

        var tex = NoiseTexture();
        mat.mainTexture = tex;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);

        SetTint(mat, new Color(1f, 1f, 1f, 1f));

        if (mat.HasProperty("_Surface"))  mat.SetFloat("_Surface", 1f);   // transparent
        if (mat.HasProperty("_Blend"))    mat.SetFloat("_Blend",   0f);   // alpha
        if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_ZWrite"))   mat.SetInt("_ZWrite", 0);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        // Tiling stays 1:1 — the repeat is baked into the UVs so it tracks world
        // position. FadeOut still stretches it vertically as the fog thins.
        SetTiling(mat, Vector2.one);
        return mat;
    }

    static Texture2D s_noise;

    /// Two octaves of *tileable* Perlin noise. Mathf.PerlinNoise does not wrap,
    /// so each octave blends its four wrapped corners — without that the
    /// scrolling curtain shows a hard vertical seam every cycle.
    static Texture2D NoiseTexture()
    {
        if (s_noise != null) return s_noise;

        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            name     = "GateFogNoise",
        };

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = Tileable(x, y, size, 3f) * 0.65f
                    + Tileable(x, y, size, 7f) * 0.35f;

            // Bias toward the middle of the range: pure noise reads as a flat
            // grey haze, whereas a contrast curve gives distinct wisps.
            float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((n - 0.28f) / 0.44f));
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        s_noise = tex;
        return tex;
    }

    static float Tileable(int x, int y, int size, float scale)
    {
        float fx = (float)x / size, fy = (float)y / size;
        float sx = fx * scale,      sy = fy * scale;

        return Mathf.PerlinNoise(sx,         sy)         * (1f - fx) * (1f - fy)
             + Mathf.PerlinNoise(sx - scale, sy)         *       fx  * (1f - fy)
             + Mathf.PerlinNoise(sx,         sy - scale) * (1f - fx) *       fy
             + Mathf.PerlinNoise(sx - scale, sy - scale) *       fx  *       fy;
    }

    void Update()
    {
        if (mat == null || rend == null) return;

        if (dissipating)
        {
            // Keep scrolling through the fade so the burn-off still reads as
            // moving air, not a sheet that stopped and then faded.
            SetOffset(mat, new Vector2(Time.time * 0.035f, Time.time * 0.012f));
            return;
        }

        if (head == null) return;

        Vector3 d     = head.position - mapCentre;
        float   along = Vector3.Dot(d, outward);
        float   perp  = Mathf.Abs(Vector3.Dot(d, normal));

        bool near = perp < DrawDistance
                 && along > -DrawDistance
                 && along < outerRadius + DrawDistance;

        rend.enabled = near;
        if (!near) return;

        // Scroll sideways with a slow upward drift so the fog reads as moving
        // air rather than as a textured sheet — only while it is actually
        // drawn, since up to Segments of these curtains can exist at once and
        // most of them are off-screen most of the time.
        SetOffset(mat, new Vector2(Time.time * 0.035f, Time.time * 0.012f));

        float want = Mathf.Clamp(along, 0f, outerRadius);
        if (!hasWindow || Mathf.Abs(want - windowCentre) > RebuildDrift)
            RebuildWindow(want);
    }

    /// Fades the fog out and destroys the wall. The barrier opens immediately —
    /// the player has earned the way through and should not be held by an
    /// invisible collider for the length of the fade.
    public void Dissipate()
    {
        if (dissipating) return;
        dissipating = true;
        DisableBarrier();

        if (rend != null && rend.enabled) StartCoroutine(FadeOut());
        else Destroy(gameObject);
    }

    /// Removes the wall with no fade — for restoring a saved game, where five
    /// simultaneous dissolves would be nonsense.
    public void DestroyNow()
    {
        dissipating = true;
        DisableBarrier();
        Destroy(gameObject);
    }

    IEnumerator FadeOut()
    {
        Vector3 start = curtain.localPosition;

        for (float t = 0f; t < FadeSeconds; t += Time.deltaTime)
        {
            float k = Mathf.Clamp01(t / FadeSeconds);

            SetTint(mat, new Color(1f, 1f, 1f, 1f - k));

            // Lift and thin out as it goes — fog burning off rather than a sheet
            // being switched off.
            curtain.localPosition = start + Vector3.up * (k * 2f);
            SetTiling(mat, new Vector2(1f, Mathf.Lerp(1f, 0.55f, k)));

            yield return null;
        }
        Destroy(gameObject);
    }

    // URP's Particles/Unlit uses _BaseMap/_BaseColor; the Sprites/Default
    // fallback uses _MainTex/_Color. Write whichever the material actually has.
    static void SetTint(Material m, Color c)
    {
        if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
        if (m.HasProperty(ColorId))     m.SetColor(ColorId, c);
    }

    static void SetTiling(Material m, Vector2 tiling)
    {
        if (m.HasProperty(BaseMapId)) m.SetTextureScale(BaseMapId, tiling);
        if (m.HasProperty(MainTexId)) m.SetTextureScale(MainTexId, tiling);
    }

    static void SetOffset(Material m, Vector2 offset)
    {
        if (m.HasProperty(BaseMapId)) m.SetTextureOffset(BaseMapId, offset);
        if (m.HasProperty(MainTexId)) m.SetTextureOffset(MainTexId, offset);
    }

    void OnDestroy()
    {
        if (mat != null)  Destroy(mat);
        if (mesh != null) Destroy(mesh);
    }
}
