using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/// Generates a Mt Eden / Maungawhau-inspired volcanic cone hiking environment:
///   - Scoria cone with a bowl crater and crater lake
///   - Circular walking trail around the upper slopes
///   - Dense NZ native bush (pohutukawa, kowhai, ponga tree ferns, flax, manuka)
///   - Terrain textures: lush grass, mossy rock, volcanic scoria, muddy crater floor
///   - Dramatic late-afternoon Auckland sun angle
public static class WalkingTrackGenerator
{
    // ── Path ─────────────────────────────────────────────────────────────────
    const float CX          = 0.5f;
    const float CZ          = 0.5f;
    const float LOOP_RADIUS = 0.14f;   // 140 m from cone centre — upper-slope trail
    const int   PATH_SAMPLES = 1800;

    // ── Terrain shape ─────────────────────────────────────────────────────────
    // Cone height  : 0.22 norm × 600 m = 132 m (close to Mt Eden's 196 m at ~⅔ scale)
    // Crater sigma : 0.060 norm = 60 m  → bowl diameter ~180 m
    // Crater depth : 0.13 norm × 600 m = 78 m (Mt Eden crater is ~50 m deep)
    const float CONE_AMP    = 0.22f;
    const float CONE_SIGMA  = 0.28f;
    const float CRATER_AMP  = 0.13f;
    const float CRATER_SIGMA = 0.062f;

    // ── Menu entry ────────────────────────────────────────────────────────────
    [MenuItem("Tools/Generate Mt Eden Hiking Trail")]
    public static void GenerateTrack()
    {
        Terrain terrain = Object.FindAnyObjectByType<Terrain>();
        if (terrain == null) { Debug.LogError("No Terrain in scene."); return; }
        TerrainData td = terrain.terrainData;

        Undo.RecordObject(td, "Generate Mt Eden Trail");

        Vector2[] path = BuildLoopPoints();
        BuildHeightmap(td, path);
        PaintTextures(td, path);
        PlaceVegetation(td, path);
        CreateCraterLake(terrain);
        PositionPlayer(terrain, path);
        SetLighting();

        EditorUtility.SetDirty(td);
        AssetDatabase.SaveAssets();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        Debug.Log("Mt Eden hiking trail generated.");
    }

    /// Repaints the terrain alphamap only — same path, same PaintTextures, none
    /// of GenerateTrack's other steps (heightmap, vegetation, crater lake,
    /// player spawn, lighting). For picking up a PaintTextures change (e.g. the
    /// 6→4 layer merge below) without regenerating everything else the full
    /// menu item touches. BuildLoopPoints is a pure function of PATH_SAMPLES —
    /// no seeded RNG in it — so this reproduces the exact same path the terrain
    /// was already carved against.
    [MenuItem("Tools/Repaint Mt Eden Textures Only")]
    public static void RepaintTexturesOnly()
    {
        Terrain terrain = Object.FindAnyObjectByType<Terrain>();

        // A human running this from the menu already has SampleScene open —
        // reloading it here would be a silent way to discard whatever else
        // they have unsaved. Only fall back to opening it for a headless
        // invocation (-executeMethod with no scene loaded), where there is
        // nothing unsaved to lose.
        if (terrain == null && Application.isBatchMode)
        {
            EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
            terrain = Object.FindAnyObjectByType<Terrain>();
        }

        if (terrain == null) { Debug.LogError("No Terrain in scene."); return; }
        TerrainData td = terrain.terrainData;

        Undo.RecordObject(td, "Repaint Mt Eden Textures");

        PaintTextures(td, BuildLoopPoints());

        EditorUtility.SetDirty(td);
        AssetDatabase.SaveAssets();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        Debug.Log("Mt Eden terrain textures repainted.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PATH POINTS
    // ════════════════════════════════════════════════════════════════════════
    static Vector2[] BuildLoopPoints()
    {
        var pts = new Vector2[PATH_SAMPLES];
        for (int i = 0; i < PATH_SAMPLES; i++)
        {
            float a = (float)i / PATH_SAMPLES * Mathf.PI * 2f;
            float noise = Mathf.PerlinNoise(Mathf.Cos(a) * 2f + 40f,
                                            Mathf.Sin(a) * 2f + 40f);
            float r = LOOP_RADIUS + (noise - 0.5f) * 0.014f;
            pts[i] = new Vector2(CX + r * Mathf.Cos(a), CZ + r * Mathf.Sin(a));
        }
        return pts;
    }

    // ════════════════════════════════════════════════════════════════════════
    // HEIGHTMAP  — volcanic scoria cone with bowl crater
    // ════════════════════════════════════════════════════════════════════════
    static void BuildHeightmap(TerrainData td, Vector2[] path)
    {
        int res = td.heightmapResolution;
        float[,] h = new float[res, res];

        for (int zi = 0; zi < res; zi++)
        {
            for (int xi = 0; xi < res; xi++)
            {
                float nx = (float)xi / (res - 1);
                float nz = (float)zi / (res - 1);
                float dx = nx - CX, dz = nz - CZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                // --- Main volcanic cone (broad Gaussian) ---
                float cone = CONE_AMP * Mathf.Exp(-(dist * dist) / (CONE_SIGMA * CONE_SIGMA));

                // --- Crater bowl (subtracted from centre) ---
                float crater = CRATER_AMP * Mathf.Exp(-(dist * dist) / (CRATER_SIGMA * CRATER_SIGMA));

                // --- Surrounding Auckland lowland hills ---
                float hills = 0f;
                hills += Mathf.PerlinNoise(nx * 2.5f + 73f, nz * 2.5f + 91f) * 0.040f;
                hills += Mathf.PerlinNoise(nx * 6f   + 37f, nz * 6f   + 55f) * 0.016f;
                hills += Mathf.PerlinNoise(nx * 14f  + 12f, nz * 14f  + 28f) * 0.005f;

                // Fade outer hills smoothly to prevent hard terrain edges
                float edgeFade = Mathf.Clamp01(Mathf.Min(nx, nz, 1f - nx, 1f - nz) * 7f);
                hills *= edgeFade;

                // --- Scoria ridges radiating out from cone (subtle) ---
                float ridgeAngle  = Mathf.Atan2(dz, dx);
                float ridgeNoise  = Mathf.PerlinNoise(Mathf.Cos(ridgeAngle * 4f) * 3f + 99f,
                                                       Mathf.Sin(ridgeAngle * 4f) * 3f + 99f);
                float ridgeFactor = Mathf.Exp(-(dist * dist) / (0.20f * 0.20f));
                float ridges      = ridgeNoise * ridgeFactor * 0.018f;

                h[zi, xi] = Mathf.Clamp01(cone - crater + hills + ridges + 0.012f);
            }
        }

        // --- Carve walking trail ---
        float[] rawH  = SamplePathHeights(h, path, res);
        float[] pathH = SmoothCircular(rawH, 70);

        const float HALF_W = 0.009f;  // ~9 m flat path surface
        const float BLEND  = 0.028f;  // shoulder softening
        const float DIP    = 0.005f;  // path slightly recessed

        for (int zi = 0; zi < res; zi++)
        {
            for (int xi = 0; xi < res; xi++)
            {
                float nx = (float)xi / (res - 1);
                float nz = (float)zi / (res - 1);
                float minD; int nearI;
                ClosestOnLoop(new Vector2(nx, nz), path, out minD, out nearI);

                if (minD < BLEND)
                {
                    float target = pathH[nearI] - DIP;
                    float blend  = minD <= HALF_W ? 1f
                        : 1f - Mathf.SmoothStep(0f, 1f, (minD - HALF_W) / (BLEND - HALF_W));
                    h[zi, xi] = Mathf.Lerp(h[zi, xi], target, blend);
                }
            }
        }

        td.SetHeights(0, 0, h);
    }

    // ════════════════════════════════════════════════════════════════════════
    // TEXTURE PAINTING  — NZ volcanic palette
    //   0 Grass_A      bright NZ green on lower/middle slopes, and — folded in
    //                  below — the damp mossy variation that used to be its
    //                  own Grass_Moss layer
    //   1 Pebbles_B    walking trail surface, and the shoulder that used to
    //                  fade into a separate Soil_Rocks layer
    //   2 Rock         exposed volcanic rock on steep faces
    //   3 Muddy        wet crater floor (dark volcanic earth)
    //
    // Was 6 layers (Grass_A, Pebbles_B, Rock, Grass_Moss, Muddy, Soil_Rocks).
    // URP's terrain shader fits 4 layers in one pass; a 5th or 6th forces a
    // second full pass over the whole terrain every frame. Grass_Moss and
    // Soil_Rocks were the two safest to fold away: Muddy and Rock are hard
    // colour breaks that would look wrong if softened into their neighbours
    // (a green crater floor, a grey trail through solid rock), where moss and
    // shoulder-soil were always a soft blend layered under a wider area —
    // real texture, but the more affordable one to give up for a rendering
    // pass. The blend math below (noise, distance falloff) is unchanged; only
    // the channel each result lands in has moved.
    // ════════════════════════════════════════════════════════════════════════
    static void PaintTextures(TerrainData td, Vector2[] path)
    {
        var layers = new[]
        {
            LoadLayer("Grass_A_TerrainLayer"),
            LoadLayer("Pebbles_B_TerrainLayer"),
            LoadLayer("Rock_TerrainLayer"),
            LoadLayer("Muddy_TerrainLayer"),
        };
        foreach (var l in layers)
            if (l == null) { Debug.LogWarning("Terrain layer missing — skipping paint."); return; }

        td.terrainLayers = layers;

        int res = td.alphamapResolution;
        float[,,] m = new float[res, res, 4];

        for (int zi = 0; zi < res; zi++)
        {
            for (int xi = 0; xi < res; xi++)
            {
                float nx   = (float)xi / (res - 1);
                float nz   = (float)zi / (res - 1);
                float dx   = nx - CX, dz = nz - CZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                float slope = td.GetSteepness(nx, nz) / 90f;

                float minD; int nearI;
                ClosestOnLoop(new Vector2(nx, nz), path, out minD, out nearI);

                float wG = 0, wP = 0, wR = 0, wM = 0, wMud = 0, wS = 0;

                // Rock on all steep volcanic faces
                wR = Mathf.Clamp01((slope - 0.18f) * 6f);

                if (dist < 0.045f)
                {
                    // Deep crater: dark muddy volcanic earth + wet moss
                    float t = dist / 0.045f;
                    wMud = Mathf.SmoothStep(1f, 0f, t) * 0.85f;
                    wM   = Mathf.SmoothStep(0f, 1f, t) * 0.60f;
                    wG   = Mathf.Max(0f, 1f - wMud - wM);
                }
                else if (dist < 0.10f)
                {
                    // Upper crater rim: mossy, damp
                    float t = (dist - 0.045f) / 0.055f;
                    wM = Mathf.Lerp(0.70f, 0.40f, t);
                    wG = 1f - wM;
                }
                else if (minD <= 0.009f)
                {
                    // Trail core
                    wP = 1f;
                }
                else if (minD <= 0.022f)
                {
                    // Trail shoulder: pebbles fade to soil
                    float f = (minD - 0.009f) / 0.013f;
                    wP = Mathf.SmoothStep(1f, 0f, f);
                    wS = 1f - wP;
                }
                else if (dist < 0.22f)
                {
                    // Mid-slopes: rich green NZ grass with moss patches
                    float noise = Mathf.PerlinNoise(nx * 9f + 5f, nz * 9f + 7f);
                    wM = noise * 0.30f;
                    wG = 1f - wM;
                }
                else
                {
                    // Outer lowland: mostly grass, subtle variation
                    float noise = Mathf.PerlinNoise(nx * 6f + 20f, nz * 6f + 20f);
                    wM = noise * 0.15f;
                    wG = 1f - wM;
                }

                // Rock overrides on steep areas
                if (wR > 0.05f)
                {
                    float rf = Mathf.SmoothStep(0f, 1f, wR);
                    wG *= 1f - rf; wM *= 1f - rf;
                    wS *= 1f - rf; wP *= 1f - rf; wMud *= 1f - rf;
                }

                // wM (moss) and wS (shoulder soil) were always computed as the
                // complement of wG/wP within their own branch above — never an
                // independently-varying blend — so folding them straight into
                // wG/wP here reproduces exactly what they always resolved to
                // once Grass_Moss/Soil_Rocks stop being separate textures.
                wG += wM;
                wP += wS;

                float tot = wG + wP + wR + wMud;
                if (tot < 1e-4f) tot = 1f;
                m[zi, xi, 0] = wG   / tot;
                m[zi, xi, 1] = wP   / tot;
                m[zi, xi, 2] = wR   / tot;
                m[zi, xi, 3] = wMud / tot;
            }
        }

        td.SetAlphamaps(0, 0, m);
    }

    // ════════════════════════════════════════════════════════════════════════
    // VEGETATION  — New Zealand native bush
    //
    //  Bush_A / Bush_B    → Pōhutukawa / Kōwhai (iconic NZ trees)
    //  Fern_A/B/C         → Ponga tree ferns (silver fern)
    //  Plant_A–D          → Harakeke (flax) / Tī kōuka (cabbage tree) / natives
    //  Heather_A/B        → Mānuka / Kānuka scrub
    //  GrassDry_A/B       → Tussock grass on open slopes
    // ════════════════════════════════════════════════════════════════════════
    static void PlaceVegetation(TerrainData td, Vector2[] path)
    {
        // Define species list
        var speciesNames = new[]
        {
            "Bush_A", "Bush_B",                              // 0,1  tall trees
            "Fern_A", "Fern_B", "Fern_C",                  // 2,3,4 ponga ferns
            "Plant_A", "Plant_B", "Plant_C", "Plant_D",    // 5,6,7,8 flax/native plants
            "Heather_A", "Heather_B",                       // 9,10 manuka scrub
            "GrassDry_A", "GrassDry_B",                     // 11,12 tussock
        };

        var protos   = new List<TreePrototype>();
        var idxMap   = new Dictionary<string, int>(); // species name → proto index
        foreach (var name in speciesNames)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/TerrainSampleAssets/Prefabs/{name}.prefab");
            if (go == null) { Debug.LogWarning($"Prefab missing: {name}"); continue; }
            idxMap[name] = protos.Count;
            protos.Add(new TreePrototype { prefab = go, bendFactor = 0.35f });
        }

        td.treePrototypes = protos.ToArray();

        var rng   = new System.Random(2025);
        var trees = new List<TreeInstance>(2000);
        int target = 1600;

        for (int attempt = 0; attempt < 50000 && trees.Count < target; attempt++)
        {
            float nx   = (float)rng.NextDouble();
            float nz   = (float)rng.NextDouble();
            float dx   = nx - CX, dz = nz - CZ;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);

            if (dist > 0.48f) continue;             // outside terrain
            if (dist < 0.035f) continue;            // too deep in crater

            float slope = td.GetSteepness(nx, nz);
            if (slope > 42f) continue;              // bare rock faces

            float minD; int nearI;
            ClosestOnLoop(new Vector2(nx, nz), path, out minD, out nearI);
            if (minD < 0.011f) continue;            // keep trail clear

            // ── Zone-based species and density ──
            string species;
            float  density, scaleMin, scaleMax;

            if (dist < 0.07f)
            {
                // Inner crater: sparse manuka + small ferns
                density = 0.25f; scaleMin = 0.35f; scaleMax = 0.60f;
                species = Pick(rng, ("Fern_C",45),("Heather_A",35),("Plant_D",20));
            }
            else if (dist < 0.13f)
            {
                // Crater rim scrub: ferns + manuka (NZ upper-slope feel)
                density = 0.55f; scaleMin = 0.45f; scaleMax = 0.75f;
                species = Pick(rng, ("Fern_A",30),("Fern_B",25),("Heather_A",20),
                                    ("Heather_B",15),("Plant_A",10));
            }
            else if (dist < 0.20f)
            {
                // Mid-slope dense NZ bush: trees + ponga ferns (most lush zone)
                density = 0.82f; scaleMin = 0.55f; scaleMax = 1.10f;
                species = Pick(rng, ("Bush_A",22),("Bush_B",18),
                                    ("Fern_A",20),("Fern_B",15),("Fern_C",10),
                                    ("Plant_A",8),("Plant_B",7));
            }
            else if (dist < 0.34f)
            {
                // Lower slopes: tall pohutukawa canopy + undergrowth
                density = 0.72f; scaleMin = 0.65f; scaleMax = 1.30f;
                species = Pick(rng, ("Bush_A",28),("Bush_B",22),
                                    ("Fern_A",15),("Plant_A",12),("Plant_B",10),
                                    ("Heather_A",8),("Plant_C",5));
            }
            else
            {
                // Outer lowland foothills: open with tussock and scattered bush
                density = 0.42f; scaleMin = 0.50f; scaleMax = 0.90f;
                species = Pick(rng, ("Bush_A",20),("Heather_A",20),("Heather_B",15),
                                    ("GrassDry_A",20),("GrassDry_B",15),("Plant_D",10));
            }

            if (rng.NextDouble() > density) continue;
            if (!idxMap.TryGetValue(species, out int proto)) continue;

            float scale = scaleMin + (float)rng.NextDouble() * (scaleMax - scaleMin);
            trees.Add(new TreeInstance
            {
                position       = new Vector3(nx, 0f, nz),
                widthScale     = scale,
                heightScale    = scale,
                rotation       = (float)(rng.NextDouble() * Mathf.PI * 2f),
                color          = Color.white,
                lightmapColor  = Color.white,
                prototypeIndex = proto,
            });
        }

        td.SetTreeInstances(trees.ToArray(), true);
        Debug.Log($"Placed {trees.Count} NZ native flora instances.");
    }

    // Weighted random species picker — pass (name, weight) tuples
    static string Pick(System.Random rng, params (string name, int weight)[] choices)
    {
        int total = 0;
        foreach (var c in choices) total += c.weight;
        int roll = (int)(rng.NextDouble() * total);
        int cum  = 0;
        foreach (var c in choices)
        {
            cum += c.weight;
            if (roll < cum) return c.name;
        }
        return choices[0].name;
    }

    // ════════════════════════════════════════════════════════════════════════
    // CRATER LAKE
    // ════════════════════════════════════════════════════════════════════════
    static void CreateCraterLake(Terrain terrain)
    {
        // Remove old lake if regenerating
        var old = GameObject.Find("CraterLake");
        if (old != null) Undo.DestroyObjectImmediate(old);

        Vector3 tPos  = terrain.transform.position;
        Vector3 tSize = terrain.terrainData.size;
        float   cx    = tPos.x + tSize.x * CX;
        float   cz    = tPos.z + tSize.z * CZ;

        // Sample actual crater floor height
        float floorY = terrain.SampleHeight(new Vector3(cx, 0, cz)) + tPos.y;
        float waterY = floorY + 14f;  // lake fills lower portion of bowl

        var lake = GameObject.CreatePrimitive(PrimitiveType.Plane);
        lake.name = "CraterLake";
        Object.DestroyImmediate(lake.GetComponent<MeshCollider>());

        // Unity Plane = 10×10 units; scale 4 → 40×40 m lake in a ~120 m diameter crater
        lake.transform.position   = new Vector3(cx, waterY, cz);
        lake.transform.localScale = new Vector3(4f, 1f, 4f);

        // Create / reuse water material
        const string MAT_PATH = "Assets/CraterLake_Water.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MAT_PATH);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.name = "CraterLake_Water";
            AssetDatabase.CreateAsset(mat, MAT_PATH);
        }
        // Deep teal-blue, highly reflective
        mat.SetColor("_BaseColor",   new Color(0.03f, 0.18f, 0.38f, 0.80f));
        mat.SetFloat("_Smoothness",  0.96f);
        mat.SetFloat("_Metallic",    0f);
        mat.SetFloat("_Surface",     1f);   // Transparent
        mat.SetFloat("_Blend",       0f);   // Alpha
        mat.SetInt("_SrcBlend",      (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend",      (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite",        0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        EditorUtility.SetDirty(mat);

        lake.GetComponent<MeshRenderer>().sharedMaterial = mat;
        Undo.RegisterCreatedObjectUndo(lake, "Create Crater Lake");
        Debug.Log($"Crater lake placed at Y={waterY:F1} m (floor={floorY:F1} m).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PLAYER SPAWN  — start of trail, facing the volcanic cone
    // ════════════════════════════════════════════════════════════════════════
    static void PositionPlayer(Terrain terrain, Vector2[] path)
    {
        var rig = Object.FindAnyObjectByType<OVRCameraRig>();
        if (rig == null) return;

        Vector2 startNorm = path[0];
        Vector3 tSize     = terrain.terrainData.size;
        Vector3 tPos      = terrain.transform.position;

        float wx = tPos.x + startNorm.x * tSize.x;
        float wz = tPos.z + startNorm.y * tSize.z;
        float wy = terrain.SampleHeight(new Vector3(wx, 0, wz)) + tPos.y;

        Undo.RecordObject(rig.transform, "Position player on trail");
        rig.transform.position = new Vector3(wx, wy + 1.7f, wz);   // eye height

        // Face inward toward the volcanic cone peak
        Vector3 coneTop = new Vector3(tPos.x + tSize.x * CX, wy + 80f, tPos.z + tSize.z * CZ);
        rig.transform.rotation = Quaternion.LookRotation(
            Vector3.ProjectOnPlane(coneTop - rig.transform.position, Vector3.up));
    }

    // ════════════════════════════════════════════════════════════════════════
    // LIGHTING  — warm late-afternoon Auckland sun
    // ════════════════════════════════════════════════════════════════════════
    static void SetLighting()
    {
        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
        foreach (var l in lights)
        {
            if (l.type != LightType.Directional) continue;
            Undo.RecordObject(l.transform, "Set sun");
            Undo.RecordObject(l, "Set sun");
            l.transform.rotation = Quaternion.Euler(38f, 215f, 0f); // sun from SW, 38° above horizon
            l.color              = new Color(1.00f, 0.93f, 0.78f);  // golden afternoon
            l.intensity          = 1.20f;
            EditorUtility.SetDirty(l.gameObject);
            break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SHARED HELPERS
    // ════════════════════════════════════════════════════════════════════════
    static TerrainLayer LoadLayer(string name) =>
        AssetDatabase.LoadAssetAtPath<TerrainLayer>(
            $"Assets/TerrainSampleAssets/TerrainLayers/{name}.terrainlayer");

    static void ClosestOnLoop(Vector2 pos, Vector2[] pts, out float minDist, out int nearIdx)
    {
        float ang = Mathf.Atan2(pos.y - CZ, pos.x - CX);
        if (ang < 0) ang += Mathf.PI * 2f;
        int est   = (int)(ang / (Mathf.PI * 2f) * PATH_SAMPLES) % PATH_SAMPLES;
        int range = PATH_SAMPLES / 5;
        minDist = float.MaxValue; nearIdx = est;
        for (int k = est - range; k <= est + range; k++)
        {
            int idx = ((k % PATH_SAMPLES) + PATH_SAMPLES) % PATH_SAMPLES;
            float d = (pos - pts[idx]).sqrMagnitude;
            if (d < minDist) { minDist = d; nearIdx = idx; }
        }
        minDist = Mathf.Sqrt(minDist);
    }

    static float[] SamplePathHeights(float[,] h, Vector2[] pts, int res)
    {
        var raw = new float[PATH_SAMPLES];
        for (int i = 0; i < PATH_SAMPLES; i++)
        {
            int xi = Mathf.Clamp(Mathf.RoundToInt(pts[i].x * (res - 1)), 0, res - 1);
            int zi = Mathf.Clamp(Mathf.RoundToInt(pts[i].y * (res - 1)), 0, res - 1);
            raw[i] = h[zi, xi];
        }
        return raw;
    }

    static float[] SmoothCircular(float[] a, int r)
    {
        int n = a.Length;
        var o = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0f; int c = 0;
            for (int j = -r; j <= r; j++)
            { int idx = ((i + j) % n + n) % n; s += a[idx]; c++; }
            o[i] = s / c;
        }
        return o;
    }
}
