using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

/// Proximity prompt + identification card for the flora, and the entry point
/// into the plant-gathering workout.
///
/// The flow is: stand near a plant → press → do the mini workout (PlantHarvest
/// spawns a ring of punch orbs) → the sample is banked and the card opens as the
/// reward. The card deliberately does NOT open at the start of the workout: it
/// hangs 1.2 m in front of the face and the orbs sit out at the plant, so the
/// player would be punching through it.
///
/// Once the quota for the next torch is met (or the plant has already been
/// gathered) pressing just opens the card, with no workout — the hike stays a
/// place you can wander and read about plants, not only a place you grind them.
[DefaultExecutionOrder(100)]
public class NatureInfoUI : MonoBehaviour
{
    [Header("Detection")]
    [SerializeField] float showDistance = 3f;
    [SerializeField] float hideDistance = 4.5f;

    [Header("Panel")]
    [SerializeField] float readingDistance = VRPanel.InfoDistance;
    [SerializeField] float heightOffset    = -0.1f;

    [Header("Auto-Setup")]
    [SerializeField] string floraRootName = "NZFlora";

    Canvas canvas, promptCanvas;
    TextMeshProUGUI nameText, sciText, descText, factText, typeText, progressText;
    TextMeshProUGUI promptText;
    Transform cam;
    OVRCameraRig rig;
    NatureInteractable showing;

    void Start()
    {
        rig = FindAnyObjectByType<OVRCameraRig>();
        cam = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;

        BuildInfoPanel();
        BuildPromptPanel();

        var floraRoot = GameObject.Find(floraRootName);
        if (floraRoot != null)
            foreach (Transform child in floraRoot.transform)
                if (child.GetComponent<NatureInteractable>() == null)
                    child.gameObject.AddComponent<NatureInteractable>();

        canvas.gameObject.SetActive(false);
        promptCanvas.gameObject.SetActive(false);
    }

    void Update()
    {
        if (cam == null) return;

        // Find nearest interactable
        NatureInteractable nearest = null;
        float nearDist = float.MaxValue;
        foreach (var ni in NatureInteractable.All)
        {
            float d = Vector3.Distance(cam.position, ni.transform.position);
            if (d < nearDist) { nearDist = d; nearest = ni; }
        }

        bool inRange = nearest != null && nearDist <= showDistance;

        // OVR face buttons + index triggers + keyboard fallback for Meta XR
        // Simulator (Simulator maps right-A → E key, right-B → R key, trigger →
        // right mouse button). Space is deliberately NOT bound: DesktopHikeSimulator
        // uses it to throw an alternating punch, so binding it here meant one
        // press both walked the player and opened a card.
        bool pressed = OVRInput.GetDown(OVRInput.Button.One)
                    || OVRInput.GetDown(OVRInput.Button.Two)
                    || OVRInput.GetDown(OVRInput.Button.Three)
                    || OVRInput.GetDown(OVRInput.Button.Four)
                    || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger)
                    || OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger)
                    || (Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame);

        if (pressed) HandlePress(nearest, inRange);

        // Auto-hide info panel when walking away from the plant it's showing
        if (showing != null && Vector3.Distance(cam.position, showing.transform.position) > hideDistance)
        {
            canvas.gameObject.SetActive(false);
            showing = null;
        }

        // Prompt shows only when near a plant, with no card open and no workout
        // in progress — during a harvest the orbs are the instruction.
        bool showPrompt = inRange
                       && !canvas.gameObject.activeSelf
                       && PlantHarvest.Active == null;

        promptCanvas.gameObject.SetActive(showPrompt);
        if (showPrompt) UpdatePromptText(nearest);

        if (canvas.gameObject.activeSelf)       TrackCanvas(canvas);
        if (promptCanvas.gameObject.activeSelf) TrackCanvas(promptCanvas);
    }

    // ── Interaction ──────────────────────────────────────────────────────────
    void HandlePress(NatureInteractable nearest, bool inRange)
    {
        // A press during the workout abandons it — the same button that started
        // it backs out of it, so the player is never committed to a set.
        if (PlantHarvest.Active != null)
        {
            PlantHarvest.Active.Cancel();
            return;
        }

        if (canvas.gameObject.activeSelf)
        {
            canvas.gameObject.SetActive(false);
            showing = null;
            return;
        }

        if (!inRange) return;

        var pc = PlantCollection.Instance;
        if (pc != null && pc.Evaluate(nearest) == PlantState.Collectable &&
            PlantHarvest.Begin(nearest, cam, rig, OnHarvested) != null)
            return;

        // Nothing to gather here (or no rig to punch with) — just identify it.
        ShowCard(nearest);
    }

    void OnHarvested(NatureInteractable plant)
    {
        PlantCollection.Instance?.TryGather(plant);
        ShowCard(plant);
    }

    void ShowCard(NatureInteractable ni)
    {
        if (ni == null) return;
        showing = ni;
        Populate(ni);
        canvas.gameObject.SetActive(true);
        promptCanvas.gameObject.SetActive(false);
    }

    void Populate(NatureInteractable ni)
    {
        typeText.text = ni.category.ToUpper();
        nameText.text = ni.displayName;
        sciText.text  = ni.scientificName;
        descText.text = ni.description;
        factText.text = string.IsNullOrEmpty(ni.funFact) ? "" : $"Did you know? {ni.funFact}";

        var pc = PlantCollection.Instance;
        if (pc == null)
        {
            progressText.text = "";
            return;
        }

        progressText.text = pc.QuotaMet
            ? $"Samples ready — {pc.Count} / {pc.Required}. Light the next torch."
            : $"Sample gathered — {pc.Count} / {pc.Required} for the next torch.";
        progressText.color = pc.QuotaMet ? VRPanel.Accent : new Color(0.55f, 0.95f, 0.6f);
    }

    void UpdatePromptText(NatureInteractable ni)
    {
        var pc = PlantCollection.Instance;
        string text;

        if (pc == null)
        {
            text = "[ E ]  Identify";
        }
        else
        {
            switch (pc.Evaluate(ni))
            {
                case PlantState.Collectable:
                    text = $"[ E ]  Investigate   ·   {pc.Count} / {pc.Required}";
                    break;
                case PlantState.AlreadyGathered:
                    text = "[ E ]  Identify   ·   sampled";
                    break;
                default:
                    text = "[ E ]  Identify";
                    break;
            }
        }

        if (promptText.text != text) promptText.text = text;
    }

    void TrackCanvas(Canvas c)
    {
        VRPanel.FacePlace(c.transform, cam, readingDistance, heightOffset, 6f);
    }

    // ── Info Panel ───────────────────────────────────────────────────────────
    void BuildInfoPanel()
    {
        var go = new GameObject("NatureInfoPanel");
        canvas = go.AddComponent<Canvas>();
        go.AddComponent<GraphicRaycaster>();
        VRPanel.Configure(canvas, new Vector2(550f, 380f));

        var bg = MakeRect("BG", go.transform);
        SetRect(bg, new Vector2(550f, 380f));
        bg.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.09f, 0.04f, 0.93f);

        var bar = MakeRect("TopBar", bg.transform);
        bar.anchorMin = new Vector2(0f, 1f); bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 1f); SetRect(bar, new Vector2(0f, 8f));
        bar.gameObject.AddComponent<Image>().color = new Color(0.3f, 0.85f, 0.4f);

        typeText = MakeLabel("Type", bg.transform, 18, FontStyle.Normal,
            new Vector2(0f, -22f),  new Vector2(550f, 28f),  new Color(0.35f, 1f, 0.45f),   TextAnchor.MiddleCenter);
        nameText = MakeLabel("Name", bg.transform, 34, FontStyle.Bold,
            new Vector2(0f, -60f),  new Vector2(530f, 48f),  Color.white,                    TextAnchor.MiddleCenter);
        sciText  = MakeLabel("Sci",  bg.transform, 19, FontStyle.Italic,
            new Vector2(0f, -108f), new Vector2(530f, 28f),  new Color(0.75f, 0.9f, 0.75f), TextAnchor.MiddleCenter);

        var div = MakeRect("Divider", bg.transform);
        div.anchorMin = new Vector2(0.05f, 1f); div.anchorMax = new Vector2(0.95f, 1f);
        div.pivot = new Vector2(0.5f, 1f); SetRect(div, new Vector2(0f, 2f));
        div.anchoredPosition = VRPanel.Px(new Vector2(0f, -140f));
        div.gameObject.AddComponent<Image>().color = new Color(0.3f, 0.6f, 0.3f, 0.5f);

        descText = MakeLabel("Desc", bg.transform, 17, FontStyle.Normal,
            new Vector2(0f, -180f), new Vector2(520f, 100f), new Color(0.9f, 0.95f, 0.9f),  TextAnchor.UpperCenter);
        factText = MakeLabel("Fact", bg.transform, 14, FontStyle.Italic,
            new Vector2(0f, -292f), new Vector2(520f, 52f),  new Color(1f, 0.88f, 0.4f),    TextAnchor.UpperCenter);

        progressText = MakeLabel("Progress", bg.transform, 16, FontStyle.Bold,
            new Vector2(0f, -344f), new Vector2(520f, 26f),  new Color(0.55f, 0.95f, 0.6f), TextAnchor.MiddleCenter);

        var hint = MakeLabel("Hint", bg.transform, 13, FontStyle.Normal,
            new Vector2(0f, 14f), new Vector2(550f, 22f), new Color(0.5f, 0.65f, 0.5f), TextAnchor.MiddleCenter);
        hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0f);
        hint.rectTransform.pivot = new Vector2(0.5f, 0f);
        hint.rectTransform.anchoredPosition = VRPanel.Px(new Vector2(0f, 14f));
        hint.text = "Press E to close";
    }

    // ── Proximity Prompt ─────────────────────────────────────────────────────
    void BuildPromptPanel()
    {
        var go = new GameObject("NaturePromptPanel");
        promptCanvas = go.AddComponent<Canvas>();
        go.AddComponent<GraphicRaycaster>();
        VRPanel.Configure(promptCanvas, new Vector2(320f, 48f));

        var bg = MakeRect("BG", go.transform);
        SetRect(bg, new Vector2(320f, 48f));
        bg.gameObject.AddComponent<Image>().color = new Color(0.05f, 0.12f, 0.05f, 0.88f);

        promptText = MakeLabel("Prompt", bg.transform, 22, FontStyle.Normal,
            Vector2.zero, new Vector2(310f, 40f), new Color(0.5f, 1f, 0.55f), TextAnchor.MiddleCenter);
        promptText.rectTransform.anchorMin = promptText.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        promptText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        promptText.rectTransform.anchoredPosition = Vector2.zero;
        promptText.text = "[ E ]  Investigate";
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    static RectTransform MakeRect(string n, Transform parent)
    {
        var go = new GameObject(n);
        go.transform.SetParent(parent, false);
        return go.AddComponent<RectTransform>();
    }

    // Authored sizes/offsets are design pixels; the canvas works in supersampled
    // units (see VRPanel), so everything set directly on a RectTransform here
    // has to go through Px the same way MakeLabel does.
    static void SetRect(RectTransform rt, Vector2 sizePx)
        => rt.sizeDelta = VRPanel.Px(sizePx);

    static TextMeshProUGUI MakeLabel(string n, Transform parent, int size, FontStyle style,
        Vector2 pos, Vector2 dim, Color color, TextAnchor align)
    {
        var rt = MakeRect(n, parent);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = VRPanel.Px(pos);
        rt.sizeDelta = VRPanel.Px(dim);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        VRPanel.ApplyFont(t);
        t.fontSize = VRPanel.Px(size);
        t.fontStyle = VRPanel.ToStyle(style);
        t.color = color;
        t.alignment = VRPanel.ToAlign(align);
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Overflow;
        return t;
    }
}
