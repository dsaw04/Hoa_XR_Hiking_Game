using UnityEngine;

public class WindSystem : MonoBehaviour
{
    [Header("Base Settings")]
    [SerializeField] float baseSpeed     = 1.5f;
    [SerializeField] float turbulence    = 0.25f;
    [SerializeField] float gustMagnitude = 0.4f;
    [SerializeField] float gustFrequency = 0.3f;

    [Header("Natural Variation")]
    [SerializeField] float speedVariation = 0.6f;   // fraction of baseSpeed to vary by
    [SerializeField] float dirSwayDeg     = 20f;    // max heading deviation in degrees
    [SerializeField] float variationSpeed = 0.12f;  // Perlin time advance per second

    WindZone wz;
    float t;

    void Awake()
    {
        wz = GetComponent<WindZone>();
        if (wz == null) wz = gameObject.AddComponent<WindZone>();
        wz.mode               = WindZoneMode.Directional;
        wz.windTurbulence     = turbulence;
        wz.windPulseMagnitude = gustMagnitude;
        wz.windPulseFrequency = gustFrequency;
    }

    void Update()
    {
        t += Time.deltaTime * variationSpeed;

        float speedNoise = Mathf.PerlinNoise(t, 0f);
        wz.windMain = Mathf.Lerp(baseSpeed * (1f - speedVariation),
                                  baseSpeed * (1f + speedVariation), speedNoise);

        float sway = (Mathf.PerlinNoise(0f, t) * 2f - 1f) * dirSwayDeg;
        transform.rotation = Quaternion.Euler(0f, sway, 0f);
    }
}
