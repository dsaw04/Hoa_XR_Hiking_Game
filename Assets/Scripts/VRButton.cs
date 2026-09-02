using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// A pointer-clickable region on a world-space VRPanel.
///
/// Deliberately does not use Unity's EventSystem / GraphicRaycaster stack: the
/// project builds every panel in code and knows exactly which rects are
/// interactive, so a flat list of registered rects hit-tested against the
/// controller ray is both simpler and avoids dragging in an EventSystem, an
/// input module and a screen-space camera mapping just to click a card.
///
/// Registration is automatic via OnEnable/OnDisable, which means deactivating a
/// panel (or destroying it on a tab switch) removes its buttons from the pick
/// list on the same frame.
[DisallowMultipleComponent]
public class VRButton : MonoBehaviour
{
    /// Every currently-active button, in creation order. Read by VRPointer.
    public static readonly List<VRButton> All = new List<VRButton>();

    /// How much a button grows under the pointer — small, because at 1.5 m a
    /// big pop reads as the panel lurching toward you.
    const float HoverScale = 1.035f;

    RectTransform  rect;
    Image          bg;
    TextMeshProUGUI label;
    Graphic        border;          // optional accent frame, shown when selected

    Color bgNormal, bgHover, bgSelected;
    Color textNormal, textSelected;

    Action onClick;
    bool   hovered, selected;

    public bool Selected => selected;

    /// Wires an already-built card/pill up to the pointer. `border` is an
    /// optional graphic revealed in Accent while the button is selected.
    public static VRButton Attach(Image background, Action onClick,
        Color normal, Color hover, Color selected,
        TextMeshProUGUI label = null,
        Color textNormal = default, Color textSelected = default,
        Graphic border = null)
    {
        var b = background.gameObject.AddComponent<VRButton>();
        b.rect       = background.rectTransform;
        b.bg         = background;
        b.label      = label;
        b.border     = border;
        b.onClick    = onClick;
        b.bgNormal   = normal;
        b.bgHover    = hover;
        b.bgSelected = selected;
        b.textNormal   = textNormal   == default ? VRPanel.TextSecondary : textNormal;
        b.textSelected = textSelected == default ? VRPanel.Ink           : textSelected;
        b.Refresh();
        return b;
    }

    void OnEnable()  => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// Intersects `ray` with this button's rect. Purely geometric — the panel
    /// plane, then a rect containment test in the rect's own local space, which
    /// is exact regardless of how the panel is scaled or angled.
    public bool Raycast(Ray ray, out float distance)
    {
        distance = 0f;
        if (rect == null) return false;

        var plane = new Plane(rect.forward, rect.position);
        if (!plane.Raycast(ray, out float enter)) return false;

        Vector2 local = rect.InverseTransformPoint(ray.GetPoint(enter));
        if (!rect.rect.Contains(local)) return false;

        distance = enter;
        return true;
    }

    public void SetHover(bool value)
    {
        if (hovered == value) return;
        hovered = value;
        Refresh();
    }

    public void SetSelected(bool value)
    {
        if (selected == value) return;
        selected = value;
        Refresh();
    }

    public void Click() => onClick?.Invoke();

    void Refresh()
    {
        if (bg != null)
            bg.color = selected ? bgSelected : hovered ? bgHover : bgNormal;

        if (label != null)
            label.color = selected ? textSelected : textNormal;

        if (border != null)
            border.color = selected ? VRPanel.Accent
                         : hovered  ? new Color(VRPanel.Accent.r, VRPanel.Accent.g, VRPanel.Accent.b, 0.45f)
                         : VRPanel.Clear;

        // Buttons with an accent frame already show hover through the frame, and
        // growing them would push the card out past its own border — so only
        // frameless buttons (pills, Start) get the scale nudge.
        if (rect != null && border == null)
            rect.localScale = Vector3.one * (hovered && !selected ? HoverScale : 1f);
    }
}
