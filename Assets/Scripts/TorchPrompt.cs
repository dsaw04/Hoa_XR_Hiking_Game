using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// The sign floating above the torch the player is currently working toward.
///
/// Without it the quota is invisible: the player arrives at a torch, touches it,
/// and nothing happens. The bowl's colour carries the same information (see
/// Torch.UpdateBowlAndLight) but only as a rough fill, and only once you are
/// close enough to judge the shade.
///
/// One canvas is created for the whole hike and moved to whichever torch is
/// active, rather than one per torch — five world-space canvases would be five
/// permanent draw calls for text nobody is reading. It scales with distance so
/// it stays legible from down the trail: VRPanel's design-pixel sizes assume a
/// 1.2 m reading distance and a torch is usually several metres off.
public class TorchPrompt : MonoBehaviour
{
    const float ShowDistance = 16f;
    const float HeightAbove  = 0.55f;   // above the bowl
    const float MaxScale     = 9f;      // stop growing past ~11 m

    const float PanelW = 380f, PanelH = 118f;
    const float BarW   = 300f, BarH   = 10f;

    Canvas canvas;
    TextMeshProUGUI headline, detail;
    RectTransform barTrack, barFill;
    Transform head;

    string lastHeadline = null, lastDetail = null;
    float  lastFill     = -1f;

    public static TorchPrompt Create(Transform head)
    {
        var go = new GameObject("TorchPrompt");
        var p  = go.AddComponent<TorchPrompt>();
        p.head = head;
        p.Build();
        p.Hide();
        return p;
    }

    void Build()
    {
        canvas = gameObject.AddComponent<Canvas>();
        VRPanel.Configure(canvas, new Vector2(PanelW, PanelH));

        var bg = MakeRect("BG", transform, new Vector2(PanelW, PanelH));
        var bgImage = bg.gameObject.AddComponent<Image>();
        bgImage.sprite = UIShapes.RoundedRect(18);
        bgImage.type   = Image.Type.Sliced;
        bgImage.color  = VRPanel.Panel;

        headline = MakeLabel("Headline", bg, 27, FontStyle.Bold,
            new Vector2(0f, -16f), new Vector2(360f, 38f),
            VRPanel.TextPrimary, TextAnchor.MiddleCenter);

        barTrack = MakeRect("BarTrack", bg, new Vector2(BarW, BarH));
        barTrack.anchorMin = barTrack.anchorMax = barTrack.pivot = new Vector2(0.5f, 1f);
        barTrack.anchoredPosition = VRPanel.Px(new Vector2(0f, -58f));
        var track = barTrack.gameObject.AddComponent<Image>();
        track.sprite = UIShapes.RoundedRect(5);
        track.type   = Image.Type.Sliced;
        track.color  = VRPanel.AccentSoft;

        // Left-pivoted so setting the width grows the fill rightwards.
        barFill = MakeRect("BarFill", barTrack, new Vector2(0f, BarH));
        barFill.anchorMin = barFill.anchorMax = barFill.pivot = new Vector2(0f, 0.5f);
        barFill.anchoredPosition = Vector2.zero;
        var fill = barFill.gameObject.AddComponent<Image>();
        fill.sprite = UIShapes.RoundedRect(5);
        fill.type   = Image.Type.Sliced;
        fill.color  = VRPanel.Accent;

        detail = MakeLabel("Detail", bg, 17, FontStyle.Normal,
            new Vector2(0f, -74f), new Vector2(360f, 30f),
            VRPanel.TextSecondary, TextAnchor.MiddleCenter);
    }

    void Update()
    {
        var m = TorchProgressionManager.Instance;
        if (m == null || head == null || !m.Ready) { Hide(); return; }

        int index = m.NextTorchIndex;
        if (index >= m.TorchCount) { Hide(); return; }   // loop complete

        var torch = m.Torches[index];
        if (torch == null) { Hide(); return; }

        Vector3 pos  = torch.BowlPosition + Vector3.up * HeightAbove;
        float   dist = Vector3.Distance(head.position, pos);
        if (dist > ShowDistance) { Hide(); return; }

        Show();

        transform.position    = pos;
        transform.rotation    = Quaternion.LookRotation(pos - head.position);
        // Constant apparent size: VRPanel.Scale is authored for 1.2 m, so scale
        // linearly with distance and cap it so a torch across the crater doesn't
        // become a billboard.
        transform.localScale  = Vector3.one * VRPanel.Scale
                              * Mathf.Clamp(dist / VRPanel.InfoDistance, 1f, MaxScale);

        Refresh(torch);
    }

    void Refresh(Torch torch)
    {
        var pc = PlantCollection.Instance;
        bool ready = torch.CanLight;

        string h, d;
        float  fill01;

        if (ready)
        {
            h = "Light the torch";
            d = "Reach out and touch the bowl";
            fill01 = 1f;
        }
        else if (pc != null)
        {
            int left = Mathf.Max(0, pc.Required - pc.Count);
            h = $"{pc.Count} / {pc.Required} samples gathered";
            d = left == 1 ? "One more plant along this stretch"
                          : $"{left} more plants along this stretch";
            fill01 = pc.Progress01;
        }
        else
        {
            h = "Light the torch";
            d = "Reach out and touch the bowl";
            fill01 = 1f;
        }

        barTrack.gameObject.SetActive(!ready);

        // TMP rebuilds its mesh on every assignment, so only write on change.
        if (h != lastHeadline) { headline.text = h; lastHeadline = h; }
        if (d != lastDetail)   { detail.text   = d; lastDetail   = d; }

        if (!Mathf.Approximately(fill01, lastFill))
        {
            barFill.sizeDelta = new Vector2(VRPanel.Px(BarW) * fill01, VRPanel.Px(BarH));
            lastFill = fill01;
        }

        headline.color = ready ? VRPanel.Accent : VRPanel.TextPrimary;
    }

    void Show() { if (!canvas.enabled) canvas.enabled = true; }
    void Hide() { if (canvas.enabled)  canvas.enabled = false; }

    // ── Helpers (mirrors NatureInfoUI; authored sizes are design pixels) ─────
    static RectTransform MakeRect(string n, Transform parent, Vector2 sizePx)
    {
        var go = new GameObject(n);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = VRPanel.Px(sizePx);
        return rt;
    }

    static TextMeshProUGUI MakeLabel(string n, Transform parent, int size, FontStyle style,
        Vector2 pos, Vector2 dim, Color color, TextAnchor align)
    {
        var rt = MakeRect(n, parent, dim);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = VRPanel.Px(pos);

        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        VRPanel.ApplyFont(t);
        t.fontSize         = VRPanel.Px(size);
        t.fontStyle        = VRPanel.ToStyle(style);
        t.color            = color;
        t.alignment        = VRPanel.ToAlign(align);
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode     = TextOverflowModes.Overflow;
        return t;
    }
}
