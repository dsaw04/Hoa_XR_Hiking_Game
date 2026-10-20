using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Shared configuration + placement for the project's world-space VR panels, so
/// they all sit at a comfortable reading distance and legible size instead of
/// each panel hard-coding its own scale/distance/billboard maths.
/// Used by MenuSceneUI and NatureInfoUI.
///
/// Convention: panels are authored in "design pixels" (RectTransform sizeDelta,
/// positions and font sizes). One design pixel is 1.5 mm on the panel, so a
/// 30 px font is 4.5 cm tall → ~2.1° of arc at 1.2 m, comfortably readable in a
/// headset.
///
/// Supersampling: a design pixel is NOT a canvas unit. Everything authored is
/// multiplied by Supersample on the way into the canvas, and the canvas is
/// scaled down by the same factor — so the panel ends up exactly the same
/// physical size, but TMP lays out a 30 px font as a 120 pt one and the result
/// is minified rather than magnified. Authoring small and magnifying is what
/// made the text look soft: the SDF edge falloff is a fixed fraction of the
/// point size, so at 13 pt the soft band was a visible slice of each glyph.
/// Route every authored dimension through Px() and this stays invisible.
public static class VRPanel
{
    /// Canvas units per design pixel. Raise for crisper text at the cost of
    /// larger glyph geometry; 4 is the point of diminishing returns against the
    /// 86 pt SDF atlas TMP ships.
    public const float Supersample = 4f;

    /// World size of one *canvas unit*, in metres. Design pixels are 1.5 mm;
    /// this is that divided by Supersample so the two cancel out.
    public const float Scale = 0.0015f / Supersample;

    /// CanvasScaler dynamic pixel density — higher = crisper dynamic elements.
    public const float PixelDensity = 10f;

    /// SDF edge sharpening applied to the shared text material. TMP's default
    /// of 0 leaves small text mushy in a headset; a little goes a long way and
    /// too much starts to alias.
    const float TextSharpness = 0.3f;

    /// Design pixels → canvas units. Every authored size, position, corner
    /// radius and font size must go through this.
    public static float Px(float designPixels) => designPixels * Supersample;

    public static Vector2 Px(Vector2 designPixels) => designPixels * Supersample;

    /// Design pixels → canvas units, rounded, for the integer sprite radii.
    public static int PxInt(float designPixels) => Mathf.RoundToInt(designPixels * Supersample);

    /// Comfortable reading distances (m) in front of the head, per panel type.
    /// Kept in the 1–1.5 m band recommended for VR menu panels.
    public const float MenuDistance   = 1.2f;
    public const float InfoDistance   = 1.2f;
    public const float PromptDistance = 1.2f;

    // ── Shared palette ───────────────────────────────────────────────────────
    // One accent (matching the cyan MovementTarget orbs) over a dark neutral
    // glass, so the menu panels, the pointer laser and the in-world panels all
    // read as one system. Lives here rather than in each panel so a colour
    // change is a single edit.

    /// Interactive / selected colour. Anything the player can act on is cyan.
    public static readonly Color Accent    = new Color(0.15f, 0.85f, 1.00f);
    /// Accent at low alpha — hover washes, tinted card fills, ring tracks.
    public static readonly Color AccentSoft = new Color(0.15f, 0.85f, 1.00f, 0.16f);
    /// Text/icon colour drawn *on top of* Accent (near-black, not white).
    public static readonly Color Ink       = new Color(0.03f, 0.09f, 0.11f);

    /// Panel backdrop — dark, slightly translucent "glass". Kept near-opaque:
    /// the side panels are angled toward a bright sky, and any more
    /// transparency washes the body text out against it.
    public static readonly Color Panel     = new Color(0.06f, 0.09f, 0.11f, 0.97f);
    /// A card sitting on Panel.
    public static readonly Color Card      = new Color(0.11f, 0.15f, 0.18f, 0.98f);
    /// A card under the pointer.
    public static readonly Color CardHover = new Color(0.15f, 0.20f, 0.24f, 0.99f);
    /// A selected card.
    public static readonly Color CardSel   = new Color(0.13f, 0.24f, 0.28f, 0.99f);
    /// Drop-shadow tint (alpha carries the strength).
    public static readonly Color Shadow    = new Color(0f, 0f, 0f, 0.55f);

    public static readonly Color TextPrimary   = new Color(0.97f, 0.98f, 0.99f);
    public static readonly Color TextSecondary = new Color(0.72f, 0.80f, 0.84f);
    public static readonly Color TextMuted     = new Color(0.48f, 0.58f, 0.62f);
    /// Fully transparent — hidden borders, spacer graphics.
    public static readonly Color Clear         = new Color(0f, 0f, 0f, 0f);

    static TMP_FontAsset s_font;
    static Material      s_fontMaterial;

    /// Shared TextMeshPro font asset.
    ///
    /// Prefers the LiberationSans SDF asset TMP ships with the project: it's a
    /// pre-baked 1024² static atlas at 86 pt with 9 px padding, which is a
    /// better SDF than anything built at runtime. Building one from the builtin
    /// LegacyRuntime.ttf was the old first choice, but it fails on this
    /// editor/player combination ("Unable to load font face") and fell through
    /// to this same asset anyway — after logging a warning and burning an atlas
    /// build attempt every session. It stays as the fallback for the case where
    /// TMP Essentials aren't imported.
    public static TMP_FontAsset Font
    {
        get
        {
            if (s_font == null)
            {
                s_font = TMP_Settings.defaultFontAsset;

                if (s_font == null)
                {
                    var ttf = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (ttf != null) s_font = TMP_FontAsset.CreateFontAsset(ttf);
                    if (s_font != null) s_font.name = "LegacyRuntime SDF (runtime)";
                }
            }
            return s_font;
        }
    }

    /// Shared text material — a copy of the font's own, with SDF sharpening
    /// dialled up. TMP ships _Sharpness at 0, which reads as a soft halo on
    /// small text in a headset. Copied rather than edited in place so the
    /// project's TMP asset isn't mutated.
    public static Material FontMaterial
    {
        get
        {
            if (s_fontMaterial == null && Font != null && Font.material != null)
            {
                s_fontMaterial = new Material(Font.material)
                {
                    name      = "VRPanel Text (sharpened)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                int sharpness = Shader.PropertyToID("_Sharpness");
                if (s_fontMaterial.HasProperty(sharpness))
                    s_fontMaterial.SetFloat(sharpness, TextSharpness);
            }
            return s_fontMaterial;
        }
    }

    /// Applies the shared font + sharpened material to a label. Call after the
    /// label is created and before its text is set.
    public static void ApplyFont(TMP_Text label)
    {
        label.font = Font;
        var mat = FontMaterial;
        if (mat != null) label.fontSharedMaterial = mat;
    }

    /// Maps a legacy UI FontStyle to the TMP FontStyles flags.
    public static FontStyles ToStyle(FontStyle s) => s switch
    {
        FontStyle.Bold          => FontStyles.Bold,
        FontStyle.Italic        => FontStyles.Italic,
        FontStyle.BoldAndItalic => FontStyles.Bold | FontStyles.Italic,
        _                       => FontStyles.Normal,
    };

    /// Maps a legacy UI TextAnchor to the TMP TextAlignmentOptions.
    public static TextAlignmentOptions ToAlign(TextAnchor a) => a switch
    {
        TextAnchor.UpperLeft   => TextAlignmentOptions.TopLeft,
        TextAnchor.UpperCenter => TextAlignmentOptions.Top,
        TextAnchor.UpperRight  => TextAlignmentOptions.TopRight,
        TextAnchor.MiddleLeft  => TextAlignmentOptions.Left,
        TextAnchor.MiddleRight => TextAlignmentOptions.Right,
        TextAnchor.LowerLeft   => TextAlignmentOptions.BottomLeft,
        TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
        TextAnchor.LowerRight  => TextAlignmentOptions.BottomRight,
        _                      => TextAlignmentOptions.Center,   // MiddleCenter
    };

    /// Sets a Canvas to world-space at the shared scale + pixel density.
    /// `sizePx` is in design pixels; the supersample is applied here.
    public static void Configure(Canvas canvas, Vector2 sizePx, float scale = Scale)
    {
        canvas.renderMode = RenderMode.WorldSpace;

        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler == null) scaler = canvas.gameObject.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit   = PixelDensity;
        scaler.referencePixelsPerUnit = 100f;

        canvas.GetComponent<RectTransform>().sizeDelta = Px(sizePx);
        canvas.transform.localScale = Vector3.one * scale;
    }

    /// Positions a panel a fixed distance in front of the head (yaw-only, so head
    /// pitch doesn't drive it into the ground) and billboards it to face the
    /// player. lerp >= 100 snaps instantly; smaller values ease toward the spot.
    /// Returns false if the head has no valid forward yet (placement skipped).
    public static bool FacePlace(Transform panel, Transform cam,
        float distance, float heightOffset, float lerp)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) return false;
        fwd.Normalize();

        Vector3 target = cam.position + fwd * distance + Vector3.up * heightOffset;
        panel.position = lerp >= 100f
            ? target
            : Vector3.Lerp(panel.position, target, Time.deltaTime * lerp);
        panel.rotation = Quaternion.LookRotation(panel.position - cam.position);
        return true;
    }
}
