using System.Collections.Generic;
using UnityEngine;

/// Procedurally generated sprites for the world-space VR panels.
///
/// The project keeps no imported art (see VRPanel's runtime-built TMP font), but
/// rounded corners, drop shadows and radial progress rings all need a Sprite —
/// a bare Image with no sprite can only draw a hard-edged rectangle. So these
/// are rasterised once at runtime from a signed-distance field and cached.
///
/// Rounded/soft sprites are returned with 9-slice borders, so a single small
/// texture stretches to any card size without distorting the corner radius.
/// Sprites are authored at 100 pixels-per-unit to match VRPanel's canvas
/// referencePixelsPerUnit, which makes 1 texture pixel == 1 canvas pixel: a
/// radius of 24 here is a 24 px corner on the panel.
public static class UIShapes
{
    const float PixelsPerUnit = 100f;

    static readonly Dictionary<string, Sprite> s_cache = new Dictionary<string, Sprite>();

    /// Solid rounded rectangle. Use with Image.type = Sliced.
    public static Sprite RoundedRect(int radius = 24)
    {
        string key = "round" + radius;
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        int size = radius * 2 + 4;          // +4 keeps a flat 4 px middle to stretch
        var tex  = NewTexture(size);
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = RoundedBoxSdf(x, y, half, radius);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
        }
        tex.Apply();

        return Cache(key, tex, size, radius);
    }

    /// Blurred rounded rectangle used as a drop shadow behind cards. Sits
    /// slightly larger and lower than the card it backs, at low alpha.
    public static Sprite SoftShadow(int radius = 24, int blur = 18)
    {
        string key = $"soft{radius}_{blur}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        int border = radius + blur;
        int size   = border * 2 + 4;
        var tex    = NewTexture(size);
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // Distance outside the inner rounded box, faded over `blur` px.
            float d = RoundedBoxSdf(x, y, half - blur, radius);
            float a = Mathf.Clamp01(-d / blur);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));   // squared = softer falloff
        }
        tex.Apply();

        return Cache(key, tex, size, border);
    }

    /// Annulus for the activity ring. Drawn with Image.type = Filled /
    /// fillMethod = Radial360 so a fillAmount sweeps it like a progress dial.
    public static Sprite Ring(int diameter = 256, int thickness = 26)
    {
        string key = $"ring{diameter}_{thickness}";
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        var tex    = NewTexture(diameter);
        float c    = diameter * 0.5f;
        float outer = c - 1f;
        float inner = outer - thickness;

        for (int y = 0; y < diameter; y++)
        for (int x = 0; x < diameter; x++)
        {
            float r = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
            // 1 px antialiasing on both edges of the band.
            float a = Mathf.Clamp01(outer - r) * Mathf.Clamp01(r - inner);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();

        return Cache(key, tex, diameter, 0);
    }

    /// Filled circle — pointer reticle, streak badge, small dots.
    public static Sprite Disc(int diameter = 64)
    {
        string key = "disc" + diameter;
        if (s_cache.TryGetValue(key, out var cached)) return cached;

        var tex = NewTexture(diameter);
        float c = diameter * 0.5f;

        for (int y = 0; y < diameter; y++)
        for (int x = 0; x < diameter; x++)
        {
            float r = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(c - 1f - r)));
        }
        tex.Apply();

        return Cache(key, tex, diameter, 0);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// Signed distance to a rounded box centred in a `half`-sized square:
    /// negative inside, positive outside, zero on the edge.
    static float RoundedBoxSdf(int x, int y, float half, float radius)
    {
        float px = Mathf.Abs(x + 0.5f - half) - (half - radius);
        float py = Mathf.Abs(y + 0.5f - half) - (half - radius);
        float ox = Mathf.Max(px, 0f), oy = Mathf.Max(py, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(px, py), 0f) - radius;
    }

    static Texture2D NewTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp,
            // Generated assets have no file on disk; don't let them leak into
            // the scene or survive a domain reload as orphans.
            hideFlags  = HideFlags.HideAndDontSave,
        };
        return tex;
    }

    static Sprite Cache(string key, Texture2D tex, int size, int border)
    {
        var sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f),
            PixelsPerUnit, 0, SpriteMeshType.FullRect,
            new Vector4(border, border, border, border));
        sprite.name      = key;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        s_cache[key] = sprite;
        return sprite;
    }
}
