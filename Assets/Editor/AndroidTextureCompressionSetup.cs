using UnityEditor;
using UnityEngine;

/// One-shot Android import overrides for the Terrain Sample Asset Pack
/// textures. The pack ships Standalone/PS4 platform overrides only; every
/// texture falls through to DefaultTexturePlatform on Android, which means
/// desktop-scale, desktop-format textures on a standalone Quest 3 build —
/// 31 textures at 4096+, 325 at 2048, and no guarantee of ASTC at all.
///
/// Scoped to the two folders that actually ship: Details (vegetation) and
/// Terrain (ground layers), both referenced by materials/TerrainLayers that
/// are in the built scene. Textures/Heightmaps is editor-only terrain-brush
/// stamp art — nothing in this project samples it at runtime, Unity already
/// strips it from the build regardless of import settings, and touching its
/// compression would be effort spent on assets that never ship.
public static class AndroidTextureCompressionSetup
{
    static readonly string[] Roots =
    {
        "Assets/TerrainSampleAssets/Textures/Details",
        "Assets/TerrainSampleAssets/Textures/Terrain",
    };

    [MenuItem("Tools/Optimization/Set Android Texture Compression")]
    public static void Apply()
    {
        int changed = 0, skipped = 0;

        foreach (var root in Roots)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                {
                    skipped++;
                    continue;
                }

                // Normal maps and mask/thickness maps need more precision per
                // texel than a base colour does at the same visual size — ASTC
                // 5x5 over 6x6 for those, and mask/thickness content (mostly
                // HDRP subsurface data this URP build never samples) can run
                // smaller again.
                bool isNormal = path.IndexOf("_Normal", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMask   = path.IndexOf("MaskMap",    System.StringComparison.OrdinalIgnoreCase) >= 0
                             || path.IndexOf("_Mask",      System.StringComparison.OrdinalIgnoreCase) >= 0
                             || path.IndexOf("Thickness",  System.StringComparison.OrdinalIgnoreCase) >= 0;

                var settings = importer.GetPlatformTextureSettings("Android");
                settings.overridden          = true;
                settings.format              = isNormal || isMask
                    ? TextureImporterFormat.ASTC_5x5
                    : TextureImporterFormat.ASTC_6x6;
                settings.maxTextureSize      = isMask ? 512 : 1024;
                settings.resizeAlgorithm     = TextureResizeAlgorithm.Mitchell;
                settings.compressionQuality  = (int)TextureCompressionQuality.Normal;
                settings.crunchedCompression = false;

                importer.SetPlatformTextureSettings(settings);
                importer.SaveAndReimport();
                changed++;
            }
        }

        Debug.Log($"[AndroidTextureCompressionSetup] Applied Android overrides to {changed} textures " +
                  $"({skipped} skipped — no TextureImporter at path).");
    }
}
