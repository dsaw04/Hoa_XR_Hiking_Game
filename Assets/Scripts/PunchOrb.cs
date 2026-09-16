using System.Collections;
using UnityEngine;

/// Shared look-and-feel for the things the player punches.
///
/// Locomotion orbs (MovementTarget) and harvest notes (HarvestNote) are
/// deliberately separate classes — their lifetimes, hit rules and motion have
/// almost nothing in common, and folding them together would put every harvest
/// change inside the locomotion hot path. What they *should* share is how they
/// look, so a note reads as the same family of object as the orbs the player
/// has been punching all hike.
///
/// NOTE: MovementTarget still carries its own copy of this construction code.
/// That is intentional — the agreed plan was that the locomotion path is not
/// touched at all, so nothing about the working punch loop can regress from
/// this change. New punchable objects should build themselves from here, and
/// MovementTarget can be folded in later under its own test pass.
public static class PunchOrb
{
    // Cached once rather than looked up by string every call: a harvest spawns
    // roughly two notes a second, each building two materials, and a
    // Shader.Find is a string hash into the shader table every time.
    static Shader s_unlitShader;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    /// URP-first with a built-in fallback, matching MovementTarget.Create.
    public static Material UnlitMaterial(Color color)
    {
        if (s_unlitShader == null)
            s_unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");

        var mat = new Material(s_unlitShader);
        SetColor(mat, color);
        return mat;
    }

    /// Colours a material built by UnlitMaterial. URP exposes _BaseColor;
    /// the built-in unlit shader only has .color.
    public static void SetColor(Material mat, Color color)
    {
        if (mat == null) return;
        if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, color);
        else                              mat.color = color;
    }

    // The mesh GameObject.CreatePrimitive(Sphere) would hand back, pulled out
    // once. CreatePrimitive also adds a SphereCollider that this call would
    // immediately destroy again — at roughly two notes a second that is a
    // PhysX add-and-remove for a collider nothing ever queries, on top of the
    // mesh being rebuilt from scratch every time.
    static Mesh s_sphereMesh;
    static Mesh SphereMesh()
    {
        if (s_sphereMesh != null) return s_sphereMesh;

        s_sphereMesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
        if (s_sphereMesh != null) return s_sphereMesh;

        // Fallback if that builtin resource name ever changes underneath us:
        // harvest the mesh from one throwaway primitive instead of one per orb.
        var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s_sphereMesh = temp.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(temp);
        return s_sphereMesh;
    }

    /// A renderable sphere with no collider. Punch detection everywhere in this
    /// project is distance-based against a hand anchor, so a collider would do
    /// nothing except block the CharacterController.
    public static GameObject Sphere(string name, Material mat)
    {
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = SphereMesh();
        go.AddComponent<MeshRenderer>().material = mat;
        return go;
    }

    /// Shrink-to-nothing burst. The caller owns the coroutine so it can decide
    /// what happens to the GameObject afterwards.
    public static IEnumerator PopOut(Transform t, float duration = 0.15f, float overshoot = 1.35f)
    {
        if (t == null) yield break;

        Vector3 from = t.localScale * overshoot;
        for (float e = 0f; e < duration; e += Time.deltaTime)
        {
            if (t == null) yield break;
            t.localScale = Vector3.Lerp(from, Vector3.zero, e / duration);
            yield return null;
        }
        if (t != null) t.localScale = Vector3.zero;
    }

    /// Flat arrow, seven vertices, pointing along local +Y in the local XY
    /// plane. Built as a mesh rather than rasterised to a texture (the way
    /// UIShapes works for canvas sprites) because at seven vertices a mesh is
    /// less code than a texture plus a quad plus UVs plus a transparent
    /// material — and it stays sharp at every distance with no alpha sorting
    /// against the note body behind it.
    ///
    /// Built once and shared: every hook/uppercut note assigns this same mesh
    /// via sharedMesh, so nothing is ever regenerated or leaked per note.
    /// Before this cache, every note's Mesh outlived Destroy(gameObject) — a
    /// MeshFilter's sharedMesh doesn't take the GameObject's Mesh down with
    /// it — so a harvest interval spent leaking one small mesh per directional
    /// note for the rest of the session.
    static Mesh s_arrowMesh;
    public static Mesh ArrowMesh()
    {
        if (s_arrowMesh != null) return s_arrowMesh;

        var mesh = new Mesh { name = "PunchArrow" };

        mesh.vertices = new[]
        {
            // stem quad
            new Vector3(-0.16f, -0.55f, 0f),
            new Vector3( 0.16f, -0.55f, 0f),
            new Vector3( 0.16f,  0.12f, 0f),
            new Vector3(-0.16f,  0.12f, 0f),
            // head triangle
            new Vector3(-0.42f,  0.08f, 0f),
            new Vector3( 0.42f,  0.08f, 0f),
            new Vector3( 0f,     0.72f, 0f),
        };

        mesh.triangles = new[]
        {
            0, 2, 1,  0, 3, 2,   // stem
            4, 6, 5,             // head
        };

        // Wound clockwise as seen from +Z, which is the side turned toward the
        // player once the arrow is billboarded, so the front face is the visible
        // one under the default backface culling.
        var normals = new Vector3[7];
        for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.forward;
        mesh.normals = normals;

        mesh.RecalculateBounds();
        s_arrowMesh = mesh;
        return s_arrowMesh;
    }
}
