using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// A trail torch the player lights to drop the fog wall across the next stretch
/// of the loop.
///
/// A torch does not light on demand: it asks for PlantCollection.Required plant
/// samples gathered from the sector behind it first (see PlantHarvest for the
/// workout that gathers one). Until then the bowl fills toward cyan as a
/// progress readout and refuses the touch with a red pulse; TorchPrompt spells
/// out the count in words above it.
///
/// Built entirely in code (post, bowl, flame, light, audio) like every other
/// object in this project — see MovementTarget.Create and VRPointer.Create for
/// the same pattern. Registration into the static `All` list mirrors
/// NatureInteractable / VRButton so the manager never needs scene wiring.
///
/// INTERACTION — this project has no XR Interaction Toolkit (the manifest ships
/// Meta XR SDK + OpenXR only, and VRButton documents the deliberate choice to
/// avoid Unity's EventSystem stack), so there is no XRSimpleInteractable to
/// derive from. Instead the torch reuses the two interaction idioms the codebase
/// already established:
///   • reach out and touch the bowl with either hand — a hand-anchor distance
///     test, exactly how MovementTarget detects a punch;
///   • or squeeze the index trigger while standing near it — how NatureInfoUI
///     opens a plant card.
/// Face buttons are deliberately NOT bound: NatureInfoUI already consumes those
/// and a plant growing beside a torch would otherwise trigger both at once.
[DisallowMultipleComponent]
public class Torch : MonoBehaviour
{
    /// Every torch currently in the scene, in gate order. Read by the manager
    /// and by DesktopHikeSimulator.
    public static readonly List<Torch> All = new List<Torch>();

    // ── Tuning ───────────────────────────────────────────────────────────────
    const float PostHeight    = 1.45f;   // bowl sits at roughly chest/shoulder height
    const float ReachRadius   = 0.38f;   // hand-to-bowl distance that counts as a touch
    const float InteractRange = 3.0f;    // trigger-press range, matches NatureInfoUI
    const float LightRange    = 14f;
    const float LightIntensity = 3.1f;

    static readonly Color UnlitColor  = new Color(0.13f, 0.13f, 0.15f);
    static readonly Color LitColor    = new Color(1.00f, 0.62f, 0.22f);
    static readonly Color FlameWarm   = new Color(1.00f, 0.55f, 0.12f);
    /// Flashed when the torch is touched before its quota is met.
    static readonly Color DeniedColor = new Color(0.80f, 0.20f, 0.16f);

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    /// Beyond this, the bowl's flicker/pulse animation is not resolvable and
    /// its point light (range 14) cannot be lighting anything visible — so a
    /// settled (not mid-fade) torch this far from the head skips its per-frame
    /// material write entirely rather than repainting a colour nobody can see.
    const float UpdateRange    = 40f;
    const float UpdateRangeSqr = UpdateRange * UpdateRange;

    // ── Identity ─────────────────────────────────────────────────────────────
    /// Position in the progression order; torch 0 opens the first gate.
    public int   Index        { get; private set; }
    /// Unwrapped angle along the loop (deg from spawn) that this torch unlocks.
    public float GateAngleDeg { get; private set; }
    public bool  IsLit        { get; private set; }

    /// World position of the bowl — what the player actually reaches for.
    public Vector3 BowlPosition => bowl != null ? bowl.position : transform.position;

    Transform       bowl;
    Material        bowlMat;
    Light           flameLight;
    ParticleSystem  flame;
    AudioSource     igniteSource, loopSource;
    Transform       head;
    OVRCameraRig    rig;
    float           flickerSeed;
    float           litBlend;        // 0 → 1 ramp so ignition eases in
    float           denyFlash;       // 1 → 0 after a refused touch
    float           denyCooldown;    // stops a resting hand strobing the bowl

    // ── Construction ─────────────────────────────────────────────────────────
    public static Torch Create(int index, float gateAngleDeg, Vector3 basePosition,
                               Transform head, OVRCameraRig rig)
    {
        var go = new GameObject($"Torch_{index + 1:00}");
        go.transform.position = basePosition;

        var t = go.AddComponent<Torch>();
        t.Index        = index;
        t.GateAngleDeg = gateAngleDeg;
        t.head         = head;
        t.rig          = rig;
        t.flickerSeed  = index * 13.7f;

        t.BuildPost();
        t.BuildBowl();
        t.BuildFlame();
        t.BuildAudio();
        t.ApplyVisualState(instant: true);

        // OnEnable ran during AddComponent, before Index existed, so the
        // registry is only sortable once construction is finished.
        All.Sort((a, b) => a.Index.CompareTo(b.Index));
        return t;
    }

    void BuildPost()
    {
        var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        post.name = "Post";
        // No collider: MovementTarget strips its own for the same reason — the
        // player drives a CharacterController and a thin obstacle at the trail
        // edge is far more likely to snag them than to read as scenery.
        Destroy(post.GetComponent<Collider>());

        post.transform.SetParent(transform, false);
        // Unity's cylinder primitive is 2 units tall, hence the halved Y scale.
        post.transform.localScale    = new Vector3(0.085f, PostHeight * 0.5f, 0.085f);
        post.transform.localPosition = new Vector3(0f, PostHeight * 0.5f, 0f);

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat = new Material(shader) { name = "TorchPost" };
        SetColorProperty(mat, new Color(0.19f, 0.14f, 0.10f));   // charred timber
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.12f);
        post.GetComponent<MeshRenderer>().material = mat;
    }

    void BuildBowl()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "Bowl";
        Destroy(go.GetComponent<Collider>());

        go.transform.SetParent(transform, false);
        go.transform.localScale    = new Vector3(0.24f, 0.19f, 0.24f);
        go.transform.localPosition = new Vector3(0f, PostHeight + 0.05f, 0f);
        bowl = go.transform;

        // Unlit so the bowl stays legible as a status light regardless of where
        // the golden-hour sun happens to be — same choice MovementTarget makes.
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        bowlMat = new Material(shader) { name = "TorchBowl" };
        go.GetComponent<MeshRenderer>().material = bowlMat;

        var lightGo = new GameObject("FlameLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = new Vector3(0f, PostHeight + 0.18f, 0f);
        flameLight = lightGo.AddComponent<Light>();
        flameLight.type      = LightType.Point;
        flameLight.color     = LitColor;
        flameLight.range     = LightRange;
        flameLight.intensity = 0f;
        // Realtime point-light shadows are one of the most expensive things you
        // can put in a Quest 3 scene, and five of them would be unaffordable.
        flameLight.shadows   = LightShadows.None;
    }

    void BuildFlame()
    {
        var go = new GameObject("Flame");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, PostHeight + 0.10f, 0f);

        flame = go.AddComponent<ParticleSystem>();
        flame.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = flame.main;
        main.loop             = true;
        main.duration         = 2f;
        main.startLifetime    = new ParticleSystem.MinMaxCurve(0.50f, 0.95f);
        main.startSpeed       = new ParticleSystem.MinMaxCurve(0.45f, 1.00f);
        main.startSize        = new ParticleSystem.MinMaxCurve(0.14f, 0.28f);
        main.startColor       = Color.white;
        main.gravityModifier  = -0.06f;                       // buoyant, not falling
        main.simulationSpace  = ParticleSystemSimulationSpace.Local;
        main.maxParticles     = 26;                           // deliberately tiny
        main.playOnAwake      = false;

        var emission = flame.emission;
        emission.rateOverTime = 20f;

        var shape = flame.shape;
        shape.enabled        = true;
        shape.shapeType      = ParticleSystemShapeType.Cone;
        shape.angle          = 13f;
        shape.radius         = 0.05f;
        shape.radiusThickness = 1f;

        var col = flame.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1.00f, 0.95f, 0.72f), 0.00f),  // white-hot core
                new GradientColorKey(FlameWarm,                      0.42f),
                new GradientColorKey(new Color(0.62f, 0.14f, 0.03f), 1.00f),  // cooling tip
            },
            new[]
            {
                new GradientAlphaKey(0.00f, 0.00f),
                new GradientAlphaKey(0.90f, 0.18f),
                new GradientAlphaKey(0.00f, 1.00f),
            });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        var size = flame.sizeOverLifetime;
        size.enabled = true;
        var curve = new AnimationCurve(
            new Keyframe(0f, 0.55f), new Keyframe(0.30f, 1f), new Keyframe(1f, 0.12f));
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.renderMode          = ParticleSystemRenderMode.Billboard;
        r.material            = BuildFlameMaterial();
        r.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows      = false;
        r.sortingFudge        = -2f;
    }

    // Additive soft-disc material. Additive blending is what makes overlapping
    // billboards read as a single bright flame instead of a stack of discs.
    static Material BuildFlameMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                  ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { name = "TorchFlame" };

        mat.mainTexture = SoftDisc();
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", SoftDisc());

        // Additive: Src=One (or SrcAlpha), Dst=One, no depth write.
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);     // transparent
        if (mat.HasProperty("_Blend"))   mat.SetFloat("_Blend",   1f);     // additive
        if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        if (mat.HasProperty("_ZWrite"))   mat.SetInt("_ZWrite", 0);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return mat;
    }

    static Texture2D s_softDisc;

    /// Radial falloff sprite for the flame billboards. Squared falloff keeps a
    /// bright centre with a wide soft skirt, which is what stops additive
    /// particles showing their circular silhouette.
    static Texture2D SoftDisc()
    {
        if (s_softDisc != null) return s_softDisc;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            name     = "TorchFlameDisc",
        };

        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f - half) / half;
            float dy = (y + 0.5f - half) / half;
            float d  = Mathf.Sqrt(dx * dx + dy * dy);
            float a  = Mathf.Clamp01(1f - d);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
        }
        tex.Apply();
        s_softDisc = tex;
        return tex;
    }

    void BuildAudio()
    {
        igniteSource = gameObject.AddComponent<AudioSource>();
        ConfigureSource(igniteSource, maxDistance: 26f, volume: 0.85f, loop: false);

        var loopGo = new GameObject("FlameLoop");
        loopGo.transform.SetParent(transform, false);
        loopGo.transform.localPosition = new Vector3(0f, PostHeight, 0f);
        loopSource = loopGo.AddComponent<AudioSource>();
        ConfigureSource(loopSource, maxDistance: 15f, volume: 0.0f, loop: true);
        loopSource.clip = TorchAudio.Crackle();
    }

    static void ConfigureSource(AudioSource src, float maxDistance, float volume, bool loop)
    {
        src.playOnAwake  = false;
        src.loop         = loop;
        src.volume       = volume;
        src.spatialBlend = 1f;                          // fully 3D
        src.rolloffMode  = AudioRolloffMode.Linear;     // predictable falloff outdoors
        src.minDistance  = 1.5f;
        src.maxDistance  = maxDistance;
        src.dopplerLevel = 0f;                          // no pitch shift as you walk past
    }

    void OnEnable()  { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() => All.Remove(this);

    // ── Lighting the torch ───────────────────────────────────────────────────

    /// True when this is the next torch in the sequence. Order is enforced here
    /// as well as physically by the fog walls, so a player who somehow gets
    /// off-trail still cannot light torch 4 before torch 2.
    public bool IsNext
    {
        get
        {
            var m = TorchProgressionManager.Instance;
            return !IsLit && m != null && m.NextTorchIndex == Index;
        }
    }

    /// True when this torch is next AND the plant-sample quota for its gate has
    /// been met. Checked here as well as in the manager so the bowl can show how
    /// far along the quota is rather than only refusing at the end of it.
    public bool CanLight
    {
        get
        {
            if (!IsNext) return false;
            var pc = PlantCollection.Instance;
            return pc == null || pc.QuotaMet;
        }
    }

    /// Attempts to light this torch. Routed through the manager so it stays the
    /// single authority on progression state.
    public bool TryLight(OVRInput.Controller source = OVRInput.Controller.None)
    {
        var m = TorchProgressionManager.Instance;
        if (m == null || !m.TryLight(this)) return false;

        if (source != OVRInput.Controller.None)
            StartCoroutine(HapticPulse(source, 0.6f, 0.12f));
        return true;
    }

    /// Applies lit state. Called by the manager (both for player interaction and
    /// for a future RestoreState pass, where `instant` skips the whoosh).
    internal void SetLit(bool lit, bool instant = false)
    {
        IsLit = lit;
        if (instant) litBlend = lit ? 1f : 0f;
        ApplyVisualState(instant);

        if (lit)
        {
            if (!instant && igniteSource != null) igniteSource.PlayOneShot(TorchAudio.Ignite());
            if (loopSource != null && !loopSource.isPlaying)
            {
                loopSource.time = Random.Range(0f, loopSource.clip.length);  // desync the five loops
                loopSource.Play();
            }
        }
        else if (loopSource != null)
        {
            loopSource.Stop();
        }
    }

    void ApplyVisualState(bool instant)
    {
        if (flame == null) return;

        var emission = flame.emission;
        emission.enabled = IsLit;
        if (IsLit && !flame.isPlaying)      flame.Play();
        else if (!IsLit && flame.isPlaying) flame.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (instant) UpdateBowlAndLight();
    }

    void Update()
    {
        // Ease the flame in over ~0.4 s rather than snapping to full brightness.
        float target = IsLit ? 1f : 0f;
        litBlend = Mathf.MoveTowards(litBlend, target, Time.deltaTime * 2.5f);

        denyFlash    = Mathf.Max(0f, denyFlash    - Time.deltaTime * 1.6f);
        denyCooldown = Mathf.Max(0f, denyCooldown - Time.deltaTime);

        // Mid-fade always updates so the ignite/extinguish ramp never stalls;
        // otherwise only bother animating the bowl material when it is close
        // enough to matter.
        bool fading   = litBlend > 0f && litBlend < 1f;
        bool nearHead = head == null || (head.position - BowlPosition).sqrMagnitude <= UpdateRangeSqr;
        if (fading || nearHead) UpdateBowlAndLight();

        if (IsLit) return;
        TryPlayerInteraction();
    }

    void UpdateBowlAndLight()
    {
        if (bowlMat == null) return;

        if (litBlend > 0.001f)
        {
            // Perlin flicker — smoother and less strobe-like than random noise,
            // which matters a lot in a headset.
            float n = Mathf.PerlinNoise(flickerSeed, Time.time * 5.5f);
            float flicker = 0.82f + n * 0.34f;

            SetColorProperty(bowlMat, Color.Lerp(UnlitColor, LitColor * flicker, litBlend));
            if (flameLight != null)
            {
                flameLight.intensity = LightIntensity * litBlend * flicker;
                flameLight.enabled   = true;
            }
            if (loopSource != null) loopSource.volume = 0.30f * litBlend;
            return;
        }

        if (flameLight != null) flameLight.enabled = false;

        if (!IsNext)
        {
            SetColorProperty(bowlMat, UnlitColor);
            return;
        }

        // This torch's turn. The bowl doubles as the quota readout: it fills
        // from inert toward the project's "you can act on this" cyan (VRPanel
        // .Accent — same colour as the punch orbs and the menu pointer) as
        // samples come in, then breathes once it can actually be lit. So the
        // torch says how far along you are from further off than the sign does.
        Color c;
        if (CanLight)
        {
            float pulse = 0.55f + Mathf.Sin(Time.time * 3.2f) * 0.45f;
            c = Color.Lerp(UnlitColor, VRPanel.Accent, pulse);
        }
        else
        {
            var pc = PlantCollection.Instance;
            float quota01 = pc != null ? pc.Progress01 : 0f;
            c = Color.Lerp(UnlitColor, VRPanel.Accent, 0.12f + 0.68f * quota01);
        }

        if (denyFlash > 0.001f) c = Color.Lerp(c, DeniedColor, denyFlash);
        SetColorProperty(bowlMat, c);
    }

    /// Resolves whether the player is trying to light this torch, then either
    /// lights it or refuses. The attempt is detected first and judged second —
    /// reaching for a torch you have not earned has to *do* something, or the
    /// quota is invisible at exactly the moment the player is asking about it.
    void TryPlayerInteraction()
    {
        if (!IsNext) return;

        bool attempted = false;
        var  source    = OVRInput.Controller.None;

        // 1. Reach out and touch the bowl (mirrors MovementTarget's hit test).
        if (rig != null)
        {
            if (HandTouching(rig.leftHandAnchor))
            { attempted = true; source = OVRInput.Controller.LTouch; }
            else if (HandTouching(rig.rightHandAnchor))
            { attempted = true; source = OVRInput.Controller.RTouch; }
        }

        // 2. Or squeeze a trigger from within a few metres.
        if (!attempted && head != null &&
            Vector3.Distance(head.position, BowlPosition) <= InteractRange)
        {
            if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
            { attempted = true; source = OVRInput.Controller.LTouch; }
            else if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
            { attempted = true; source = OVRInput.Controller.RTouch; }
#if UNITY_EDITOR
            // Headset-free testing. F rather than E/Space, both of which
            // NatureInfoUI and DesktopHikeSimulator have already claimed.
            else if (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
            { attempted = true; }
#endif
        }

        if (!attempted) return;
        if (CanLight) { TryLight(source); return; }

        Deny(source);
    }

    /// Red pulse and a weak buzz for a touch the quota refuses. Rate-limited
    /// because a hand left resting on the bowl would otherwise strobe it.
    void Deny(OVRInput.Controller source)
    {
        if (denyCooldown > 0f) return;
        denyCooldown = 0.6f;
        denyFlash    = 1f;

        if (source != OVRInput.Controller.None)
            StartCoroutine(HapticPulse(source, 0.25f, 0.08f));
    }

    bool HandTouching(Transform hand)
    {
        if (hand == null) return false;
        return (hand.position - BowlPosition).sqrMagnitude <= ReachRadius * ReachRadius;
    }

    System.Collections.IEnumerator HapticPulse(OVRInput.Controller ctrl,
                                               float amplitude, float seconds)
    {
        OVRInput.SetControllerVibration(0.4f, amplitude, ctrl);
        yield return new WaitForSecondsRealtime(seconds);
        OVRInput.SetControllerVibration(0f, 0f, ctrl);
    }

    static void SetColorProperty(Material mat, Color c)
    {
        if (mat.HasProperty(BaseColorId)) mat.SetColor(BaseColorId, c);
        else                              mat.color = c;
    }

    void OnDestroy()
    {
        if (bowlMat != null) Destroy(bowlMat);
    }
}
