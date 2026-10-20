using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// The FitXR-style home screen for the Menu Scene.
///
/// Three world-space panels wrapped around the player plus a bottom nav bar,
/// all built procedurally on top of VRPanel (no prefabs, no scene-authored UI):
///
///   Left    browsable list of session types (the three MovementModes)
///   Center  featured "Today's Plan" card, the session setup the check-in used
///           to own (intensity + duration), a "new this week" row, and Start
///   Right   compact activity widget — streak, weekly goal ring, headline stats
///   Bottom  For You / Programs / Collections tabs, current one highlighted
///
/// This replaces the old in-gameplay BodyCheckInUI: the Activity / Intensity /
/// Duration selections and the mock "Today's Plan" handoff card live here now,
/// restyled onto rounded cards with drop shadows and driven by the ray pointer
/// instead of thumbstick row-stepping.
///
/// The panel rig is world-locked once placed — it does NOT follow the head.
/// A menu you point a laser at has to stay still, or the target moves with the
/// hand aiming at it.
[DefaultExecutionOrder(50)]
public class MenuSceneUI : MonoBehaviour
{
    // ── Panel geometry ───────────────────────────────────────────────────────
    // Canvas sizes in px (1 px = 1.5 mm at VRPanel.Scale) and panel placement
    // in metres, local to a root sitting Distance ahead of the player. Side
    // panels are toed in so all three face the player rather than sitting flat.
    const float CW = 900f, CH = 780f;    // center
    const float LW = 480f, LH = 780f;    // left
    const float RW = 440f, RH = 780f;    // right
    const float NW = 900f, NH = 150f;    // bottom nav

    const float Distance     = 1.55f;    // metres ahead of the head
    const float HeightOffset = -0.06f;   // rig centre just below eye level

    static readonly Vector3 CenterPos = new Vector3( 0f,    0f,   0f);
    static readonly Vector3 LeftPos   = new Vector3(-1.05f, 0.03f, -0.16f);
    static readonly Vector3 RightPos  = new Vector3( 1.02f, 0.03f, -0.15f);
    static readonly Vector3 NavPos    = new Vector3( 0f,   -0.72f,  0.03f);

    static readonly Vector3 LeftRot  = new Vector3(0f, -26f, 0f);
    static readonly Vector3 RightRot = new Vector3(0f,  25f, 0f);
    static readonly Vector3 NavRot   = new Vector3(13f,  0f, 0f);   // tilts up toward the eyes

    // Corner radii, in canvas px.
    const int PanelRadius = 34, CardRadius = 22, PillRadius = 26;

    // ── Mock "Today's Plan" (placeholder for the future mobile-app handoff) ──
    const MovementMode      PlanMode      = MovementMode.Gentle;
    const MovementIntensity PlanIntensity = MovementIntensity.Low;
    const int               PlanMinutes   = 10;

    enum Tab { ForYou, Programs, Collections }

    Transform           head;
    MovementModeManager mm;
    Action              onStartWorkout;

    Tab           tab = Tab.ForYou;
    RectTransform tabRoot;              // center-panel content, rebuilt per tab
    bool          placed;

    int modeIndex, intensityIndex, durationIndex;

    readonly VRButton[] modeButtons      = new VRButton[3];
    readonly VRButton[] tabButtons       = new VRButton[3];
    VRButton[] intensityButtons;         // null while a non-ForYou tab is shown
    VRButton[] durationButtons;
    TextMeshProUGUI startLabel;

    public static MenuSceneUI Create(Transform head, MovementModeManager mm, Action onStartWorkout)
    {
        var go = new GameObject("MenuSceneUI");
        var ui = go.AddComponent<MenuSceneUI>();
        ui.head           = head;
        ui.mm             = mm;
        ui.onStartWorkout = onStartWorkout;
        return ui;
    }

    void Start()
    {
        // Pre-fill from the last saved choice, exactly as the old check-in did.
        modeIndex      = (int)mm.CurrentMode;
        intensityIndex = (int)mm.Intensity;
        durationIndex  = Array.IndexOf(MovementModeManager.DurationOptions, mm.SessionMinutes);
        if (durationIndex < 0) durationIndex = 1;

        Place();
        BuildLeftPanel();
        BuildCenterPanel();
        BuildRightPanel();
        BuildNavBar();

        BuildTabContent();
        RefreshSelection();
    }

    // Head tracking can take a frame or two to report a usable forward; keep
    // trying until it does, then never move again.
    void Update()
    {
        if (!placed) Place();
    }

    void Place()
    {
        if (head == null) return;
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) return;
        fwd.Normalize();

        transform.position = head.position + fwd * Distance + Vector3.up * HeightOffset;
        transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        placed = true;
    }

    // ════════════════════════════════════════════════════════════════════════
    // LEFT — session types
    // ════════════════════════════════════════════════════════════════════════
    void BuildLeftPanel()
    {
        var panel = MakePanel("LeftPanel", LeftPos, LeftRot, new Vector2(LW, LH));

        Section("Header", panel, new Vector2(0f, 340f), LW - 60f, "SESSIONS", TextAnchor.MiddleLeft);
        Label("Sub", panel, 15, FontStyle.Normal, new Vector2(0f, 302f), new Vector2(LW - 60f, 40f),
            VRPanel.TextMuted, TextAnchor.UpperLeft)
            .text = "Pick how your body wants to move today.";

        float[] y = { 190f, -10f, -210f };
        for (int i = 0; i < 3; i++)
            modeButtons[i] = BuildModeCard(panel, (MovementMode)i, y[i]);

        Label("Footer", panel, 13, FontStyle.Normal, new Vector2(0f, -350f), new Vector2(LW - 60f, 24f),
            VRPanel.TextMuted, TextAnchor.MiddleLeft)
            .text = "3 of 8 trails unlocked";
    }

    VRButton BuildModeCard(Transform parent, MovementMode mode, float y)
    {
        int i = (int)mode;
        var size = new Vector2(420f, 190f);

        DropShadow(parent, new Vector2(0f, y), size);
        var border = Rounded("CardBorder" + i, parent, new Vector2(0f, y), size + new Vector2(8f, 8f),
            VRPanel.Clear, CardRadius + 4);
        var card   = Rounded("Card" + i, border.transform, Vector2.zero, size, VRPanel.Card, CardRadius);

        // Silhouette occupies the left ~120 px of the card; the text column
        // starts clear of it so the figure never sits under the title.
        BuildSilhouette(card.transform, mode, new Vector2(-148f, 0f));

        var title = Label("Title" + i, card.transform, 21, FontStyle.Bold,
            new Vector2(62f, 48f), new Vector2(280f, 30f), VRPanel.TextPrimary, TextAnchor.MiddleLeft);
        title.text = MovementModeManager.Title(mode);

        Label("Blurb" + i, card.transform, 14, FontStyle.Normal,
            new Vector2(62f, -18f), new Vector2(280f, 76f), VRPanel.TextSecondary, TextAnchor.UpperLeft)
            .text = MovementModeManager.Blurb(mode);

        return VRButton.Attach(card, () => SelectMode(i),
            VRPanel.Card, VRPanel.CardHover, VRPanel.CardSel,
            title, VRPanel.TextPrimary, VRPanel.TextPrimary, border);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CENTER — featured session + setup + start
    // ════════════════════════════════════════════════════════════════════════
    void BuildCenterPanel()
    {
        var panel = MakePanel("CenterPanel", CenterPos, Vector3.zero, new Vector2(CW, CH));

        Label("Heading", panel, 36, FontStyle.Bold, new Vector2(0f, 346f), new Vector2(CW - 60f, 48f),
            VRPanel.TextPrimary, TextAnchor.MiddleLeft)
            .text = "Made for you";
        Label("Greeting", panel, 16, FontStyle.Normal, new Vector2(0f, 310f), new Vector2(CW - 60f, 26f),
            VRPanel.TextSecondary, TextAnchor.MiddleLeft)
            .text = "Stay within your limits — you can change any of this before you start.";

        // Tab content is torn down and rebuilt on every tab switch; the Start
        // button and headings are chrome and persist, so Start is always
        // reachable no matter which tab you're browsing.
        tabRoot = MakeRect("TabContent", panel, Vector2.zero, new Vector2(CW - 60f, 620f));

        DropShadow(panel, new Vector2(0f, -305f), new Vector2(460f, 80f));
        var startBtn = Rounded("StartBtn", panel, new Vector2(0f, -305f), new Vector2(460f, 80f),
            VRPanel.Accent, PillRadius);
        startLabel = Label("StartLabel", startBtn.transform, 24, FontStyle.Bold,
            Vector2.zero, new Vector2(440f, 60f), VRPanel.Ink, TextAnchor.MiddleCenter);

        VRButton.Attach(startBtn, StartWorkout,
            VRPanel.Accent, Lighten(VRPanel.Accent, 0.12f), VRPanel.Accent,
            startLabel, VRPanel.Ink, VRPanel.Ink);

        Label("Hint", panel, 13, FontStyle.Normal, new Vector2(0f, -358f), new Vector2(CW - 80f, 22f),
            VRPanel.TextMuted, TextAnchor.MiddleCenter)
            .text = "Your choice is saved and carried into the hike.";
    }

    void BuildTabContent()
    {
        // SetActive(false) before Destroy so the old VRButtons deregister this
        // frame — Destroy is deferred, and a stale button would still be
        // pickable by the pointer until the end of the frame.
        foreach (Transform child in tabRoot)
        {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }
        intensityButtons = null;
        durationButtons  = null;

        switch (tab)
        {
            case Tab.ForYou:      BuildForYouTab();      break;
            case Tab.Programs:    BuildProgramsTab();    break;
            case Tab.Collections: BuildCollectionsTab(); break;
        }
        RefreshSelection();
    }

    void BuildForYouTab()
    {
        BuildFeaturedCard();
        BuildSetupRows();
        BuildNewThisWeek();
    }

    // The mock handoff card, carried over from the old check-in: hardcoded
    // placeholder data standing in for a plan pushed from a future companion
    // mobile app. No real backend behind it.
    void BuildFeaturedCard()
    {
        var size = new Vector2(840f, 230f);
        DropShadow(tabRoot, new Vector2(0f, 175f), size);
        var card = Rounded("Featured", tabRoot, new Vector2(0f, 175f), size,
            Lighten(VRPanel.Card, 0.03f), CardRadius);

        // Accent spine down the left edge, same signature the old card used.
        Rounded("Spine", card.transform, new Vector2(-417f, 0f), new Vector2(6f, 230f),
            VRPanel.Accent, 3);

        var kicker = Section("Kicker", card.transform, new Vector2(-152f, 72f), 460f,
            "TODAY'S PLAN · FROM HOA", TextAnchor.MiddleLeft);
        kicker.color = VRPanel.Accent;

        Label("PlanTitle", card.transform, 30, FontStyle.Bold, new Vector2(-152f, 28f),
            new Vector2(460f, 40f), VRPanel.TextPrimary, TextAnchor.MiddleLeft)
            .text = MovementModeManager.Title(PlanMode);
        Label("PlanBody", card.transform, 17, FontStyle.Normal, new Vector2(-152f, -12f),
            new Vector2(460f, 28f), VRPanel.TextSecondary, TextAnchor.MiddleLeft)
            .text = $"{PlanMinutes} min · low intensity · fits a small space";
        Label("PlanFine", card.transform, 14, FontStyle.Normal, new Vector2(-152f, -46f),
            new Vector2(460f, 24f), VRPanel.TextMuted, TextAnchor.MiddleLeft)
            .text = "Chosen to match how you moved this week.";

        var pill = Rounded("PlanBtn", card.transform, new Vector2(285f, 0f), new Vector2(220f, 62f),
            VRPanel.AccentSoft, PillRadius);
        var pillText = Label("PlanBtnT", pill.transform, 16, FontStyle.Bold, Vector2.zero,
            new Vector2(210f, 50f), VRPanel.Accent, TextAnchor.MiddleCenter);
        pillText.text = "Use this plan";

        // Ghost button: hover deepens the accent wash rather than filling it, so
        // the cyan label keeps its contrast (a solid accent fill would hide it).
        VRButton.Attach(pill, ApplyPlan,
            VRPanel.AccentSoft, Alpha(VRPanel.Accent, 0.34f), VRPanel.AccentSoft,
            pillText, VRPanel.Accent, VRPanel.Accent);
    }

    // Intensity + duration, the two secondary settings the check-in owned,
    // side by side as pill rows.
    void BuildSetupRows()
    {
        Section("IntLabel", tabRoot, new Vector2(-220f, 10f), 400f, "INTENSITY", TextAnchor.MiddleCenter);
        Section("DurLabel", tabRoot, new Vector2( 220f, 10f), 400f, "DURATION",  TextAnchor.MiddleCenter);

        string[] levels = { "Low", "Medium", "High" };
        float[]  offset = { -136f, 0f, 136f };

        intensityButtons = new VRButton[3];
        durationButtons  = new VRButton[3];

        for (int i = 0; i < 3; i++)
        {
            int index = i;
            intensityButtons[i] = BuildPill("Int" + i, new Vector2(-220f + offset[i], -40f),
                levels[i], () => SelectIntensity(index));
            durationButtons[i]  = BuildPill("Dur" + i, new Vector2( 220f + offset[i], -40f),
                MovementModeManager.DurationOptions[i] + " min", () => SelectDuration(index));
        }
    }

    VRButton BuildPill(string name, Vector2 pos, string text, Action onClick)
    {
        var pill = Rounded(name, tabRoot, pos, new Vector2(128f, 58f), VRPanel.Card, PillRadius);
        var t    = Label(name + "T", pill.transform, 17, FontStyle.Bold, Vector2.zero,
            new Vector2(124f, 50f), VRPanel.TextSecondary, TextAnchor.MiddleCenter);
        t.text = text;

        return VRButton.Attach(pill, onClick,
            VRPanel.Card, VRPanel.CardHover, VRPanel.Accent,
            t, VRPanel.TextSecondary, VRPanel.Ink);
    }

    // Mock "what's new" row — the small-card shelf FitXR runs under its hero.
    void BuildNewThisWeek()
    {
        Section("NewLabel", tabRoot, new Vector2(0f, -100f), CW - 60f, "NEW THIS WEEK", TextAnchor.MiddleLeft);

        string[] titles = { "Crater Rim Loop", "Dawn Chorus Walk", "Fern Gully" };
        string[] subs   = { "Steady · rolling grade", "Gentle · birdsong audio", "Energy Boost · steep" };
        string[] tags   = { "18 min", "12 min", "25 min" };
        float[]  x      = { -286f, 0f, 286f };

        for (int i = 0; i < 3; i++)
        {
            var size = new Vector2(268f, 130f);
            DropShadow(tabRoot, new Vector2(x[i], -180f), size);
            var card = Rounded("New" + i, tabRoot, new Vector2(x[i], -180f), size, VRPanel.Card, CardRadius);

            var title = Label("NewT" + i, card.transform, 17, FontStyle.Bold, new Vector2(0f, 34f),
                new Vector2(232f, 26f), VRPanel.TextPrimary, TextAnchor.MiddleLeft);
            title.text = titles[i];
            Label("NewS" + i, card.transform, 13, FontStyle.Normal, new Vector2(0f, 4f),
                new Vector2(232f, 24f), VRPanel.TextMuted, TextAnchor.MiddleLeft)
                .text = subs[i];

            var tag = Rounded("NewTag" + i, card.transform, new Vector2(-78f, -38f), new Vector2(76f, 32f),
                VRPanel.AccentSoft, 16);
            Label("NewTagT" + i, tag.transform, 13, FontStyle.Bold, Vector2.zero,
                new Vector2(72f, 28f), VRPanel.Accent, TextAnchor.MiddleCenter)
                .text = tags[i];
        }
    }

    void BuildProgramsTab()
    {
        Section("ProgLabel", tabRoot, new Vector2(0f, 292f), CW - 60f,
            "PROGRAMS · MULTI-WEEK", TextAnchor.MiddleLeft);

        string[] titles = { "Back to Walking", "Gentle Restart", "Hill Confidence",
                            "Morning Habit",   "Long Trails",    "Recovery Weeks" };
        string[] subs   = { "4 weeks · 3×/wk", "2 weeks · 4×/wk", "6 weeks · 3×/wk",
                            "3 weeks · 5×/wk", "8 weeks · 2×/wk", "2 weeks · 3×/wk" };
        float[]  x = { -286f, 0f, 286f };
        float[]  y = { 185f, -15f };

        for (int i = 0; i < 6; i++)
        {
            var size = new Vector2(268f, 180f);
            Vector2 pos = new Vector2(x[i % 3], y[i / 3]);
            DropShadow(tabRoot, pos, size);
            var card = Rounded("Prog" + i, tabRoot, pos, size, VRPanel.Card, CardRadius);

            Label("ProgT" + i, card.transform, 18, FontStyle.Bold, new Vector2(0f, 34f),
                new Vector2(230f, 52f), VRPanel.TextPrimary, TextAnchor.UpperLeft)
                .text = titles[i];
            Label("ProgS" + i, card.transform, 13, FontStyle.Normal, new Vector2(0f, -34f),
                new Vector2(230f, 24f), VRPanel.TextMuted, TextAnchor.MiddleLeft)
                .text = subs[i];
        }

        Label("ProgNote", tabRoot, 14, FontStyle.Normal, new Vector2(0f, -160f),
            new Vector2(CW - 60f, 24f), VRPanel.TextMuted, TextAnchor.MiddleCenter)
            .text = "Mock content — programs aren't wired to a backend yet.";
    }

    void BuildCollectionsTab()
    {
        Section("ColLabel", tabRoot, new Vector2(0f, 292f), CW - 60f,
            "COLLECTIONS", TextAnchor.MiddleLeft);

        string[] titles = { "Auckland Volcanoes", "Native Bush", "Sunrise Walks", "Short & Sweet" };
        string[] subs   = { "6 trails", "9 trails", "4 trails", "12 trails" };
        float[]  x = { -215f, 215f };
        float[]  y = { 175f, -45f };

        for (int i = 0; i < 4; i++)
        {
            var size = new Vector2(410f, 200f);
            Vector2 pos = new Vector2(x[i % 2], y[i / 2]);
            DropShadow(tabRoot, pos, size);
            var card = Rounded("Col" + i, tabRoot, pos, size, VRPanel.Card, CardRadius);

            Label("ColT" + i, card.transform, 20, FontStyle.Bold, new Vector2(0f, 30f),
                new Vector2(360f, 30f), VRPanel.TextPrimary, TextAnchor.MiddleLeft)
                .text = titles[i];
            Label("ColS" + i, card.transform, 14, FontStyle.Normal, new Vector2(0f, -6f),
                new Vector2(360f, 24f), VRPanel.TextMuted, TextAnchor.MiddleLeft)
                .text = subs[i];
        }

        Label("ColNote", tabRoot, 14, FontStyle.Normal, new Vector2(0f, -170f),
            new Vector2(CW - 60f, 24f), VRPanel.TextMuted, TextAnchor.MiddleCenter)
            .text = "Mock content — one trail is built today (Mt Eden loop).";
    }

    // ════════════════════════════════════════════════════════════════════════
    // RIGHT — activity widget
    // ════════════════════════════════════════════════════════════════════════
    void BuildRightPanel()
    {
        var panel = MakePanel("RightPanel", RightPos, RightRot, new Vector2(RW, RH));

        Section("ActHeader", panel, new Vector2(0f, 340f), RW - 60f, "YOUR ACTIVITY", TextAnchor.MiddleLeft);

        BuildStreakCard(panel);
        BuildGoalRing(panel);

        string[] statLabels = { "Time moved", "Distance", "Best streak" };
        string[] statValues = { "64 min", "1.8 km", "6 days" };
        float[]  statY      = { -140f, -204f, -268f };

        for (int i = 0; i < 3; i++)
        {
            var row = Rounded("Stat" + i, panel, new Vector2(0f, statY[i]), new Vector2(380f, 54f),
                VRPanel.Card, 16);
            Label("StatL" + i, row.transform, 15, FontStyle.Normal, new Vector2(-70f, 0f),
                new Vector2(220f, 26f), VRPanel.TextSecondary, TextAnchor.MiddleLeft)
                .text = statLabels[i];
            Label("StatV" + i, row.transform, 17, FontStyle.Bold, new Vector2(120f, 0f),
                new Vector2(120f, 26f), VRPanel.Accent, TextAnchor.MiddleRight)
                .text = statValues[i];
        }

        Label("ActNote", panel, 12, FontStyle.Normal, new Vector2(0f, -340f), new Vector2(RW - 60f, 22f),
            VRPanel.TextMuted, TextAnchor.MiddleCenter)
            .text = "Mock data — syncs from HOA in a later build.";
    }

    void BuildStreakCard(Transform panel)
    {
        DropShadow(panel, new Vector2(0f, 262f), new Vector2(380f, 104f));
        var card = Rounded("StreakCard", panel, new Vector2(0f, 262f), new Vector2(380f, 104f),
            VRPanel.Card, CardRadius);

        var disc = Rounded("StreakDisc", card.transform, new Vector2(-130f, 0f), new Vector2(68f, 68f),
            VRPanel.Accent, 0);
        disc.sprite = UIShapes.Disc(128);
        disc.type   = Image.Type.Simple;
        Label("StreakN", disc.transform, 28, FontStyle.Bold, Vector2.zero, new Vector2(68f, 40f),
            VRPanel.Ink, TextAnchor.MiddleCenter).text = "4";

        Section("StreakL", card.transform, new Vector2(30f, 18f), 220f, "DAY STREAK", TextAnchor.MiddleLeft);
        Label("StreakS", card.transform, 14, FontStyle.Normal, new Vector2(30f, -16f),
            new Vector2(220f, 24f), VRPanel.TextMuted, TextAnchor.MiddleLeft)
            .text = "Keep it going today";
    }

    void BuildGoalRing(Transform panel)
    {
        Section("GoalL", panel, new Vector2(0f, 185f), RW - 60f, "WEEKLY GOAL", TextAnchor.MiddleLeft);

        var holder = MakeRect("Ring", panel, new Vector2(0f, 55f), new Vector2(220f, 220f));

        // Full ring as the unfilled track, a radially-filled copy on top.
        var track = MakeImage("Track", holder, Vector2.zero, new Vector2(220f, 220f), VRPanel.AccentSoft);
        track.sprite = UIShapes.Ring(512, 60);

        var fill = MakeImage("Fill", holder, Vector2.zero, new Vector2(220f, 220f), VRPanel.Accent);
        fill.sprite        = UIShapes.Ring(512, 60);
        fill.type          = Image.Type.Filled;
        fill.fillMethod    = Image.FillMethod.Radial360;
        fill.fillOrigin    = (int)Image.Origin360.Top;
        fill.fillClockwise = true;
        fill.fillAmount    = 2f / 3f;    // 2 of 3 sessions this week

        Label("RingN", holder, 40, FontStyle.Bold, new Vector2(0f, 14f), new Vector2(180f, 50f),
            VRPanel.TextPrimary, TextAnchor.MiddleCenter).text = "2/3";
        Label("RingS", holder, 14, FontStyle.Normal, new Vector2(0f, -26f), new Vector2(180f, 24f),
            VRPanel.TextMuted, TextAnchor.MiddleCenter).text = "sessions";
    }

    // ════════════════════════════════════════════════════════════════════════
    // BOTTOM — nav bar
    // ════════════════════════════════════════════════════════════════════════
    void BuildNavBar()
    {
        var panel = MakePanel("NavPanel", NavPos, NavRot, new Vector2(NW, NH));

        string[] names = { "FOR YOU", "PROGRAMS", "COLLECTIONS" };
        float[]  x     = { -282f, 0f, 282f };

        for (int i = 0; i < 3; i++)
        {
            int index = i;
            var pill = Rounded("Tab" + i, panel, new Vector2(x[i], 18f), new Vector2(262f, 66f),
                VRPanel.Card, PillRadius);
            var t = Label("TabT" + i, pill.transform, 17, FontStyle.Bold, Vector2.zero,
                new Vector2(250f, 50f), VRPanel.TextSecondary, TextAnchor.MiddleCenter);
            t.characterSpacing = 3f;
            t.text = names[i];

            tabButtons[i] = VRButton.Attach(pill, () => SelectTab((Tab)index),
                VRPanel.Card, VRPanel.CardHover, VRPanel.Accent,
                t, VRPanel.TextSecondary, VRPanel.Ink);
        }

        Label("NavHint", panel, 13, FontStyle.Normal, new Vector2(0f, -44f), new Vector2(NW - 60f, 22f),
            VRPanel.TextMuted, TextAnchor.MiddleCenter)
            .text = "Point with either controller · trigger to select        (editor: mouse + left click)";
    }

    // ════════════════════════════════════════════════════════════════════════
    // SELECTION
    // ════════════════════════════════════════════════════════════════════════
    void SelectMode(int i)      { modeIndex      = i; mm.SetMode((MovementMode)i);           RefreshSelection(); }
    void SelectIntensity(int i) { intensityIndex = i; mm.SetIntensity((MovementIntensity)i); RefreshSelection(); }
    void SelectDuration(int i)
    {
        durationIndex = i;
        mm.SetDuration(MovementModeManager.DurationOptions[i]);
        RefreshSelection();
    }

    void SelectTab(Tab next)
    {
        if (tab == next) return;
        tab = next;
        BuildTabContent();
    }

    // Pre-fills every selection from the mock plan. The old check-in started the
    // session outright on this press; with a pointer the plan just fills the
    // form in, and Start stays a separate deliberate click.
    void ApplyPlan()
    {
        modeIndex      = (int)PlanMode;
        intensityIndex = (int)PlanIntensity;
        int d = Array.IndexOf(MovementModeManager.DurationOptions, PlanMinutes);
        durationIndex  = d < 0 ? durationIndex : d;

        mm.SetMode(PlanMode);
        mm.SetIntensity(PlanIntensity);
        mm.SetDuration(MovementModeManager.DurationOptions[durationIndex]);
        RefreshSelection();
    }

    // Null-safe throughout: the intensity/duration rows only exist on the
    // For You tab, but mode and tab selection are always live.
    void RefreshSelection()
    {
        for (int i = 0; i < 3; i++)
        {
            if (modeButtons[i]      != null) modeButtons[i].SetSelected(i == modeIndex);
            if (tabButtons[i]       != null) tabButtons[i].SetSelected(i == (int)tab);
            if (intensityButtons    != null) intensityButtons[i].SetSelected(i == intensityIndex);
            if (durationButtons     != null) durationButtons[i].SetSelected(i == durationIndex);
        }

        if (startLabel != null)
            startLabel.text = $"Start {MovementModeManager.Title((MovementMode)modeIndex)}" +
                              $" · {MovementModeManager.DurationOptions[durationIndex]} min";
    }

    void StartWorkout()
    {
        mm.SetMode((MovementMode)modeIndex);
        mm.SetIntensity((MovementIntensity)intensityIndex);
        mm.SetDuration(MovementModeManager.DurationOptions[durationIndex]);
        onStartWorkout?.Invoke();
    }

    // ════════════════════════════════════════════════════════════════════════
    // BUILD HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// One world-space canvas panel: rounded glass slab with a drop shadow,
    /// parented to the (world-locked) menu root at a local pose.
    RectTransform MakePanel(string name, Vector3 localPos, Vector3 localEuler, Vector2 sizePx)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);

        // Adding a Canvas swaps the Transform for a RectTransform, so pose the
        // panel afterwards — and after Configure, which sets the panel scale.
        var canvas = go.AddComponent<Canvas>();
        VRPanel.Configure(canvas, sizePx);

        var root = canvas.GetComponent<RectTransform>();
        root.localPosition = localPos;
        root.localRotation = Quaternion.Euler(localEuler);

        DropShadow(root, Vector2.zero, sizePx, PanelRadius);
        Rounded("BG", root, Vector2.zero, sizePx, VRPanel.Panel, PanelRadius);
        return root;
    }

    // A blurred rounded slab behind a card. Sized a little larger and dropped a
    // few px so cards read as lifted off the panel rather than painted on it.
    static Image DropShadow(Transform parent, Vector2 pos, Vector2 size, int radius = CardRadius)
    {
        var img = MakeImage("Shadow", parent, pos + new Vector2(0f, -8f), size + new Vector2(26f, 26f),
            VRPanel.Shadow);
        // Sprite radii are texture pixels mapped 1:1 onto canvas units, so they
        // supersample alongside everything else or the corners go soft.
        img.sprite = UIShapes.SoftShadow(VRPanel.PxInt(radius), VRPanel.PxInt(20));
        img.type   = Image.Type.Sliced;
        img.raycastTarget = false;
        return img;
    }

    static Image Rounded(string name, Transform parent, Vector2 pos, Vector2 size, Color color, int radius)
    {
        var img = MakeImage(name, parent, pos, size, color);
        if (radius > 0)
        {
            img.sprite = UIShapes.RoundedRect(VRPanel.PxInt(radius));
            img.type   = Image.Type.Sliced;
        }
        return img;
    }

    // Every authored coordinate enters the canvas here, so this is the single
    // place the design-pixel → canvas-unit supersample is applied.
    static RectTransform MakeRect(string name, Transform parent, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = VRPanel.Px(pos);
        rt.sizeDelta = VRPanel.Px(size);
        return rt;
    }

    static Image MakeImage(string name, Transform parent, Vector2 pos, Vector2 size, Color color)
    {
        var img = MakeRect(name, parent, pos, size).gameObject.AddComponent<Image>();
        img.color = color;
        return img;
    }

    static TextMeshProUGUI Label(string name, Transform parent, int size, FontStyle style,
        Vector2 pos, Vector2 dim, Color color, TextAnchor align)
    {
        var t = MakeRect(name, parent, pos, dim).gameObject.AddComponent<TextMeshProUGUI>();
        VRPanel.ApplyFont(t);
        // Laid out at Supersample× the design size, then minified with the rest
        // of the canvas — this is what keeps glyph edges tight.
        t.fontSize  = VRPanel.Px(size);
        t.fontStyle = VRPanel.ToStyle(style);
        t.color     = color;
        t.alignment = VRPanel.ToAlign(align);
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode     = TextOverflowModes.Overflow;
        t.raycastTarget    = false;
        return t;
    }

    /// Small letterspaced uppercase header — the quiet tier of the hierarchy.
    static TextMeshProUGUI Section(string name, Transform parent, Vector2 pos, float width,
        string text, TextAnchor align)
    {
        var t = Label(name, parent, 14, FontStyle.Bold, pos, new Vector2(width, 24f),
            VRPanel.TextMuted, align);
        t.characterSpacing = 5f;
        t.text = text;
        return t;
    }

    static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

    static Color Lighten(Color c, float amount) =>
        new Color(c.r + amount, c.g + amount, c.b + amount, c.a);

    // A minimal stick-figure silhouette per mode so a card reads at a glance
    // without paragraphs. Assembled from tinted rects — no sprites needed.
    // Carried over unchanged from the old BodyCheckInUI cards.
    static void BuildSilhouette(Transform card, MovementMode mode, Vector2 pos)
    {
        var box = MakeRect("Silhouette", card, pos, new Vector2(120f, 120f));
        Color c = new Color(0.85f, 0.92f, 0.95f);

        MakeImage("head",  box, new Vector2(0f, 40f), new Vector2(24f, 24f), c);
        MakeImage("torso", box, new Vector2(0f, 4f),  new Vector2(20f, 52f), c);

        switch (mode)
        {
            case MovementMode.Gentle:       // arms low and tucked, compact stance
                Part(box, new Vector2(-15f,  8f),  new Vector2(30f, 10f),  22f, c);
                Part(box, new Vector2( 15f,  8f),  new Vector2(30f, 10f), -22f, c);
                Part(box, new Vector2( -6f, -36f), new Vector2(10f, 34f),   0f, c);
                Part(box, new Vector2(  6f, -36f), new Vector2(10f, 34f),   0f, c);
                break;

            case MovementMode.Steady:       // arms raised, feet together
                Part(box, new Vector2(-20f, 26f),  new Vector2(40f, 11f),  55f, c);
                Part(box, new Vector2( 20f, 26f),  new Vector2(40f, 11f), -55f, c);
                Part(box, new Vector2( -8f, -38f), new Vector2(11f, 38f),   0f, c);
                Part(box, new Vector2(  8f, -38f), new Vector2(11f, 38f),   0f, c);
                break;

            case MovementMode.EnergyBoost:  // dynamic: one arm high, wide stride
                Part(box, new Vector2(-22f,  28f), new Vector2(46f, 12f),  50f, c);
                Part(box, new Vector2( 22f,  14f), new Vector2(46f, 12f),  25f, c);
                Part(box, new Vector2(-14f, -36f), new Vector2(11f, 40f),  18f, c);
                Part(box, new Vector2( 14f, -36f), new Vector2(11f, 40f), -18f, c);
                break;
        }
    }

    static void Part(Transform parent, Vector2 pos, Vector2 size, float angle, Color c)
    {
        var img = MakeImage("part", parent, pos, size, c);
        img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}
