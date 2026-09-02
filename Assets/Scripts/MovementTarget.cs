using System.Collections;
using UnityEngine;

/// A floating orb the player punches.
///
/// Two of them ride in front of the head and drive locomotion (created by
/// HikingPlayerController); short-lived rings of them also make up the
/// plant-gathering workout (see PlantHarvest). Hit detection is distance-based
/// against the hand anchor plus a minimum controller speed, so no rigidbodies or
/// colliders are needed on the hands.
public class MovementTarget : MonoBehaviour
{
    [SerializeField] float hitRadius   = 0.18f;
    [SerializeField] float minHitSpeed = 1.1f;
    [SerializeField] float cooldown    = 0.30f;

    static readonly Color ReadyColor = new Color(0.15f, 0.85f, 1f);
    static readonly Color HitColor   = new Color(1f, 0.85f, 0.15f);

    /// (target, hitSpeed) — fired once per punch.
    public System.Action<MovementTarget, float> OnHit;
    public bool IsLeft { get; private set; }

    Transform hand;
    OVRInput.Controller controller;
    Material mat;
    Vector3  baseScale;
    float    cooldownLeft;
    float    flash;          // 1 → 0 after a hit, drives color/scale pop
    float    bobPhase;
    bool     popping;

    public static MovementTarget Create(Transform handAnchor, bool isLeft)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = isLeft ? "MoveTarget_L" : "MoveTarget_R";
        Destroy(go.GetComponent<Collider>());   // distance check only; must not block the CharacterController

        var t = go.AddComponent<MovementTarget>();
        t.hand       = handAnchor;
        t.IsLeft     = isLeft;
        t.controller = isLeft ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        t.bobPhase   = isLeft ? 0f : Mathf.PI;  // targets bob out of phase

        go.transform.localScale = t.baseScale = Vector3.one * 0.22f;

        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        t.mat = new Material(shader);
        t.SetColor(ReadyColor);
        go.GetComponent<MeshRenderer>().material = t.mat;
        return t;
    }

    void Update()
    {
        if (popping) return;

        cooldownLeft = Mathf.Max(0f, cooldownLeft - Time.deltaTime);
        flash        = Mathf.Max(0f, flash - Time.deltaTime * 4f);
        bobPhase    += Time.deltaTime * 2f;

        // Pop + yellow flash on hit, gentle idle pulse otherwise.
        float idlePulse = 1f + Mathf.Sin(bobPhase) * 0.04f;
        transform.localScale = baseScale * (idlePulse + flash * 0.45f);
        SetColor(Color.Lerp(ReadyColor, HitColor, flash));

        if (cooldownLeft > 0f || hand == null) return;

        float speed = OVRInput.GetLocalControllerVelocity(controller).magnitude;
        if (speed < minHitSpeed) return;

        if ((hand.position - transform.position).sqrMagnitude <= hitRadius * hitRadius)
            SimulateHit(speed);
    }

    /// Registers a hit as if the player punched the target at the given
    /// controller speed (m/s). Also used by DesktopHikeSimulator for
    /// headset-free testing in the editor.
    public void SimulateHit(float speed)
    {
        if (popping || cooldownLeft > 0f) return;
        cooldownLeft = cooldown;
        flash        = 1f;
        OnHit?.Invoke(this, speed);
    }

    /// Overrides the minimum controller speed (m/s) needed to register a hit.
    /// Used by the Body-Adaptive movement system to make punches easier
    /// (Gentle) or harder (Energy Boost).
    public void SetRequiredSpeed(float speed) => minHitSpeed = Mathf.Max(0.1f, speed);

    /// Overrides the orb's diameter (m). The idle pulse scales from this.
    public void SetSize(float diameter)
    {
        baseScale = Vector3.one * Mathf.Max(0.02f, diameter);
        transform.localScale = baseScale;
    }

    /// Overrides how close a hand has to get (m) to count as a punch. Harvest
    /// orbs sit still and are reached for rather than swung past, so they can
    /// afford a more forgiving radius than the locomotion pair.
    public void SetHitRadius(float radius) => hitRadius = Mathf.Max(0.05f, radius);

    /// Bursts the orb and destroys it. Further hits are ignored from the moment
    /// this is called, so a punch landing during the burst cannot double-count.
    public void Pop()
    {
        if (popping) return;
        popping = true;
        OnHit   = null;
        SetColor(HitColor);
        StartCoroutine(PopOut());
    }

    IEnumerator PopOut()
    {
        const float duration = 0.15f;
        Vector3 from = baseScale * 1.35f;

        for (float t = 0f; t < duration; t += Time.deltaTime)
        {
            transform.localScale = Vector3.Lerp(from, Vector3.zero, t / duration);
            yield return null;
        }
        Destroy(gameObject);
    }

    void SetColor(Color c)
    {
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        else                               mat.color = c;
    }

    void OnDestroy()
    {
        if (mat != null) Destroy(mat);
    }
}
