using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// One-shot conversion of the 1,340 individually-rendered NZFlora prefab
/// instances baked into the scene into native terrain TreeInstance data.
///
/// This is the perf audit's #1 finding: every plant is its own MeshRenderer,
/// with no static batching, no LOD, and its own shadow-map submission. As
/// terrain trees the same instances get Unity's native GPU-instanced batching,
/// frustum/distance culling and billboard crossfade for free.
///
/// What this deliberately does NOT touch: the plant-harvest game logic reads
/// nothing from these GameObjects except their Transform, discovered through
/// NatureInfoUI.Start() walking the live "NZFlora" hierarchy and attaching a
/// NatureInteractable to whatever it finds there. So this only strips the
/// MeshRenderer/MeshFilter — the part that was costing a draw call — off each
/// child; the GameObject, its Transform and its position in the hierarchy
/// survive untouched. The terrain now draws what that renderer used to, at
/// the exact same spot, and every identify/harvest/torch-quota path keeps
/// working exactly as before because it was never looking at the renderer.
///
/// Run this from the Editor (Tools ▸ Optimization ▸ Convert NZFlora To
/// Terrain Trees), then look at the result in Scene view before saving — it
/// deliberately does not save the scene itself. Undo (Ctrl+Z) reverts it in
/// one step if the trees don't look right.
public static class ConvertFloraToTerrainTrees
{
    const string FloraRootName = "NZFlora";
    const float  BendFactor    = 0.35f;   // matches WalkingTrackGenerator.PlaceVegetation

    [MenuItem("Tools/Optimization/Convert NZFlora To Terrain Trees")]
    public static void Convert()
    {
        var terrain = Terrain.activeTerrain;
        if (terrain == null)
        {
            Debug.LogError("[ConvertFloraToTerrainTrees] No active Terrain in the scene.");
            return;
        }

        var floraRoot = GameObject.Find(FloraRootName);
        if (floraRoot == null)
        {
            Debug.LogError($"[ConvertFloraToTerrainTrees] No '{FloraRootName}' GameObject found.");
            return;
        }

        var td = terrain.terrainData;
        Undo.RecordObject(td, "Convert Flora To Terrain Trees");

        Vector3 terrainPos  = terrain.transform.position;
        Vector3 terrainSize = td.size;

        var protoIndex = new Dictionary<string, int>();
        var protos     = new List<TreePrototype>();
        var instances  = new List<TreeInstance>(floraRoot.transform.childCount);

        int converted = 0, skippedNoRenderer = 0, skippedNoPrefab = 0;

        // Snapshot the children first: this loop destroys components (not the
        // children themselves) as it goes, but walking a live Transform while
        // mutating siblings under it is not worth the risk for what it saves.
        var children = new Transform[floraRoot.transform.childCount];
        for (int i = 0; i < children.Length; i++) children[i] = floraRoot.transform.GetChild(i);

        foreach (var child in children)
        {
            var mf = child.GetComponent<MeshFilter>();
            var mr = child.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) { skippedNoRenderer++; continue; }

            string species = SpeciesName(child.name);

            if (!protoIndex.TryGetValue(species, out int index))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"Assets/TerrainSampleAssets/Prefabs/{species}.prefab");
                if (prefab == null) { skippedNoPrefab++; continue; }

                index = protos.Count;
                protoIndex[species] = index;
                protos.Add(new TreePrototype { prefab = prefab, bendFactor = BendFactor });
            }

            Vector3 world = child.position;
            float nx = (world.x - terrainPos.x) / terrainSize.x;
            float nz = (world.z - terrainPos.z) / terrainSize.z;

            // TreeInstance.position.y is normalized like x/z, but Unity's
            // terrain tree renderer always re-samples the heightmap at x/z to
            // place a tree vertically and ignores this component — the same
            // reason WalkingTrackGenerator.PlaceVegetation leaves it at 0.
            Vector3 scale      = child.localScale;
            float widthScale   = (scale.x + scale.z) * 0.5f;
            float heightScale  = scale.y;

            instances.Add(new TreeInstance
            {
                position       = new Vector3(nx, 0f, nz),
                widthScale     = widthScale,
                heightScale    = heightScale,
                rotation       = child.eulerAngles.y * Mathf.Deg2Rad,
                color          = Color.white,
                lightmapColor  = Color.white,
                prototypeIndex = index,
            });

            // Strip only the rendering surface. The GameObject, its Transform
            // and (once NatureInfoUI.Start runs again) its NatureInteractable
            // stay — the plant is still exactly where the player identifies
            // and harvests it, it just isn't the thing drawing it any more.
            Undo.DestroyObjectImmediate(mr);
            Undo.DestroyObjectImmediate(mf);
            converted++;
        }

        td.treePrototypes = protos.ToArray();
        td.SetTreeInstances(instances.ToArray(), true);

        EditorUtility.SetDirty(td);
        EditorUtility.SetDirty(floraRoot);
        EditorSceneManager.MarkSceneDirty(floraRoot.scene);

        Debug.Log($"[ConvertFloraToTerrainTrees] Converted {converted} renderers into " +
                  $"{instances.Count} terrain tree instances across {protos.Count} species " +
                  $"({skippedNoRenderer} had no renderer, {skippedNoPrefab} had no matching prefab). " +
                  "Scene is dirty but NOT saved — check Scene view, then File ▸ Save if it looks right.");
    }

    /// Scene names are exactly the prefab name ("Bush_A", "Fern_C", ...); this
    /// only guards against a Unity-appended disambiguation suffix like
    /// " (1)" if two siblings ever collided.
    static string SpeciesName(string goName)
    {
        int paren = goName.IndexOf('(');
        return paren < 0 ? goName : goName.Substring(0, paren).TrimEnd();
    }
}
