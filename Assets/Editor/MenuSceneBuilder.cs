using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// Generates the standalone Menu Scene (Tools ▸ Build Menu Scene).
///
/// Same idiom as WalkingTrackGenerator: the scene is a build artefact of a
/// script, not something hand-authored in the editor, so it can be regenerated
/// and reviewed as code. Running it twice overwrites the scene.
///
/// What it makes:
///   - warm golden-hour procedural skybox + matching sun, fog and ambient
///   - a ground plane using the existing Grass_Moss terrain layer's texture
///   - a ring of the existing NZ flora prefabs, so turning your head shows a
///     real place with depth rather than a flat backdrop
///   - the OVRCameraRig, standing still: no CharacterController, no
///     HikingPlayerController, so the player can look around but not move
///   - a MenuSceneBootstrap object, which builds the panels and pointer at
///     runtime
/// then registers MenuScene first and SampleScene second in Build Settings, so
/// launching the app lands on the menu.
public static class MenuSceneBuilder
{
    const string ScenePath   = "Assets/Scenes/MenuScene.unity";
    const string SkyPath     = "Assets/Settings/MenuScene_Sky.mat";
    const string GroundPath  = "Assets/Settings/MenuScene_Ground.mat";
    const string PostFxPath  = "Assets/Settings/MenuScene_PostFX.asset";
    const string GameplayPath = "Assets/Scenes/SampleScene.unity";

    // OVRCameraRig, as referenced by SampleScene — resolved by GUID so a Meta
    // SDK version bump (which changes the package cache folder name) doesn't
    // break this.
    const string RigGuid = "126d619cf4daa52469682f85c1378b4a";

    const float EyeHeight = 1.7f;

    [MenuItem("Tools/Build Menu Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var sun = CreateSun();
        ConfigureEnvironment(sun);
        CreateGround();
        CreateVignette();
        CreatePostFx();

        if (!CreateCameraRig()) return;

        new GameObject("MenuSceneBootstrap").AddComponent<MenuSceneBootstrap>();

        EditorSceneManager.SaveScene(scene, ScenePath);
        RegisterBuildSettings();

        Debug.Log($"Menu scene built at {ScenePath}. " +
                  "Build Settings now start on MenuScene and fall through to SampleScene.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // LIGHTING  — the same late-afternoon Auckland sun the trail generator sets
    // ════════════════════════════════════════════════════════════════════════
    static Light CreateSun()
    {
        var go = new GameObject("Sun");
        go.transform.rotation = Quaternion.Euler(38f, 215f, 0f);

        var light = go.AddComponent<Light>();
        light.type      = LightType.Directional;
        light.color     = new Color(1.00f, 0.93f, 0.78f);
        light.intensity = 1.20f;
        light.shadows   = LightShadows.Soft;
        return light;
    }

    static void ConfigureEnvironment(Light sun)
    {
        RenderSettings.skybox = CreateSkybox();
        RenderSettings.sun    = sun;

        // Trilight rather than Skybox ambient: this scene is never baked, and
        // Trilight gives a predictable warm bounce with no GI pass.
        RenderSettings.ambientMode         = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.60f, 0.64f, 0.70f);
        RenderSettings.ambientEquatorColor = new Color(0.46f, 0.43f, 0.38f);
        RenderSettings.ambientGroundColor  = new Color(0.20f, 0.18f, 0.15f);

        // Haze hides the edge of the ground plane, so the vignette reads as
        // open country rather than a disc floating in a void.
        RenderSettings.fog                 = true;
        RenderSettings.fogMode             = FogMode.Linear;
        RenderSettings.fogColor            = new Color(0.80f, 0.74f, 0.63f);
        RenderSettings.fogStartDistance    = 35f;
        RenderSettings.fogEndDistance      = 190f;
    }

    static Material CreateSkybox()
    {
        var mat = new Material(Shader.Find("Skybox/Procedural"));
        mat.SetFloat("_SunSize",            0.055f);
        mat.SetFloat("_AtmosphereThickness", 1.55f);   // >1 = thicker, warmer, low-sun look
        mat.SetColor("_SkyTint",            new Color(0.62f, 0.55f, 0.48f));
        mat.SetColor("_GroundColor",        new Color(0.29f, 0.26f, 0.22f));
        mat.SetFloat("_Exposure",           1.15f);
        return SaveAsset(mat, SkyPath);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GROUND + VIGNETTE
    // ════════════════════════════════════════════════════════════════════════
    static void CreateGround()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Plane);
        go.name = "Ground";
        go.transform.localScale = new Vector3(40f, 1f, 40f);   // 400 m across

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetColor("_BaseColor", new Color(0.55f, 0.56f, 0.44f));
        mat.SetFloat("_Smoothness", 0.05f);

        // Reuse the terrain layer art rather than importing anything new.
        var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(
            "Assets/TerrainSampleAssets/TerrainLayers/Grass_Moss_TerrainLayer.terrainlayer");
        if (layer != null && layer.diffuseTexture != null)
        {
            mat.SetTexture("_BaseMap", layer.diffuseTexture);
            mat.SetTextureScale("_BaseMap", new Vector2(80f, 80f));
            mat.SetColor("_BaseColor", Color.white);
            if (layer.normalMapTexture != null)
            {
                mat.SetTexture("_BumpMap", layer.normalMapTexture);
                mat.EnableKeyword("_NORMALMAP");
            }
        }

        go.GetComponent<MeshRenderer>().sharedMaterial = SaveAsset(mat, GroundPath);
        Object.DestroyImmediate(go.GetComponent<MeshCollider>());   // nothing moves here
    }

    /// A ring of existing NZ flora around the player. Seeded, so rebuilding the
    /// scene reproduces the same arrangement.
    static void CreateVignette()
    {
        var root = new GameObject("MenuVignette").transform;
        Random.InitState(20260802);

        // (prefab names, count, inner radius, outer radius, scale range)
        // Trees start beyond the panel rig (~1.6 m) and well outside it, so
        // nothing crowds the menu; the near bands fill the middle distance.
        Scatter(root, new[] { "Bush_A", "Bush_B" },                        70, 12f, 60f, 1.0f, 1.8f);
        Scatter(root, new[] { "Fern_A", "Fern_B", "Fern_C" },              45,  6f, 24f, 0.9f, 1.5f);
        Scatter(root, new[] { "Plant_A", "Plant_B", "Plant_C", "Plant_D" },40,  5f, 20f, 0.9f, 1.4f);
        Scatter(root, new[] { "Heather_A", "Heather_B" },                  35,  5f, 28f, 1.0f, 1.6f);
        Scatter(root, new[] { "GrassDry_A", "GrassDry_B" },                55,  4f, 22f, 1.0f, 1.7f);
    }

    static void Scatter(Transform root, string[] names, int count,
        float innerRadius, float outerRadius, float minScale, float maxScale)
    {
        var prefabs = new List<GameObject>();
        foreach (var n in names)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(
                $"Assets/TerrainSampleAssets/Prefabs/{n}.prefab");
            if (p != null) prefabs.Add(p);
        }
        if (prefabs.Count == 0) return;

        for (int i = 0; i < count; i++)
        {
            float angle  = Random.Range(0f, Mathf.PI * 2f);
            // sqrt keeps the scatter area-uniform instead of clumping inward.
            float radius = Mathf.Lerp(innerRadius, outerRadius, Mathf.Sqrt(Random.value));

            var go = (GameObject)PrefabUtility.InstantiatePrefab(
                prefabs[Random.Range(0, prefabs.Count)], root);
            go.transform.position = new Vector3(Mathf.Cos(angle) * radius, 0f,
                                                Mathf.Sin(angle) * radius);
            go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            go.transform.localScale = Vector3.one * Random.Range(minScale, maxScale);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // POST-PROCESSING  — subtle; carries the "glass panel" look
    // ════════════════════════════════════════════════════════════════════════
    static void CreatePostFx()
    {
        // The profile must exist on disk before its overrides are added: each
        // override is a separate ScriptableObject that has to be nested inside
        // the profile asset, or it evaporates on the next domain reload.
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        DeleteIfExists(PostFxPath);
        AssetDatabase.CreateAsset(profile, PostFxPath);

        var bloom = profile.Add<Bloom>(true);
        bloom.intensity.value = 0.55f;
        bloom.threshold.value = 1.05f;      // only the accent highlights bloom
        bloom.scatter.value   = 0.62f;

        var vignette = profile.Add<Vignette>(true);
        vignette.intensity.value  = 0.26f;
        vignette.smoothness.value = 0.5f;

        var grade = profile.Add<ColorAdjustments>(true);
        grade.postExposure.value = 0.10f;
        grade.contrast.value     = 6f;
        grade.saturation.value   = 4f;

        foreach (var component in profile.components)
        {
            component.name = component.GetType().Name;
            AssetDatabase.AddObjectToAsset(component, profile);
        }
        AssetDatabase.SaveAssets();

        var volume = new GameObject("Global Volume").AddComponent<Volume>();
        volume.isGlobal = true;
        volume.profile  = profile;
    }

    // ════════════════════════════════════════════════════════════════════════
    // CAMERA RIG
    // ════════════════════════════════════════════════════════════════════════
    static bool CreateCameraRig()
    {
        string path = AssetDatabase.GUIDToAssetPath(RigGuid);
        var prefab = string.IsNullOrEmpty(path)
            ? null
            : AssetDatabase.LoadAssetAtPath<GameObject>(path);

        if (prefab == null)
        {
            Debug.LogError("MenuSceneBuilder: could not find the OVRCameraRig prefab " +
                           $"(GUID {RigGuid}). Is the Meta XR core SDK still installed? " +
                           "Menu scene not saved.");
            return false;
        }

        var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        rig.name = "OVRCameraRig";
        // Tracking origin is EyeLevel (set by MenuSceneBootstrap), so the rig
        // root sits at eye height above the ground plane.
        rig.transform.position = new Vector3(0f, EyeHeight, 0f);

        // Post-processing is off by default on URP cameras; the volume above
        // does nothing without this.
        foreach (var cam in rig.GetComponentsInChildren<Camera>(true))
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;

        return true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // BUILD SETTINGS
    // ════════════════════════════════════════════════════════════════════════
    static void RegisterBuildSettings()
    {
        // Menu first so a launched build opens on it; gameplay second so
        // SceneManager.LoadScene("SampleScene") resolves.
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath,    true),
            new EditorBuildSettingsScene(GameplayPath, true),
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Rebuild rather than patch: these assets are only ever referenced by the
    // menu scene, which is itself regenerated on the same run, so deleting and
    // recreating can't leave a dangling reference anywhere.
    static T SaveAsset<T>(T asset, string path) where T : Object
    {
        DeleteIfExists(path);
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        return asset;
    }

    static void DeleteIfExists(string path)
    {
        if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            AssetDatabase.DeleteAsset(path);
    }
}
