using UnityEngine;

namespace Redline;

// Bakes scene fog into blood vertex colors. The blood meshes use an unlit
// sprite shader with no fog support, so without this they keep full
// saturation in fogged-out rooms while everything else goes grey.
internal static class BloodFog
{
    public static float Factor(Vector3 world)
    {
        if (!RenderSettings.fog)
            return 0f;
        var camera = PlayerState.cameraActive;
        if (!camera) camera = Camera.main;
        if (!camera)
            return 0f;
        float distance = Vector3.Distance(camera.transform.position, world);
        return RenderSettings.fogMode switch
        {
            FogMode.Exponential => 1f - Mathf.Exp(-RenderSettings.fogDensity * distance),
            FogMode.ExponentialSquared => 1f - Mathf.Exp(-RenderSettings.fogDensity * RenderSettings.fogDensity
                * distance * distance),
            _ => Mathf.Clamp01((distance - RenderSettings.fogStartDistance)
                / Mathf.Max(0.001f, RenderSettings.fogEndDistance - RenderSettings.fogStartDistance)),
        };
    }

    public static Color32 Apply(Color32 color, float fog)
    {
        if (fog <= 0f)
            return color;
        float keep = 1f - Mathf.Clamp01(fog);
        var grey = RenderSettings.fogColor;
        return new Color32(
            (byte)(color.r * keep + grey.r * 255f * (1f - keep)),
            (byte)(color.g * keep + grey.g * 255f * (1f - keep)),
            (byte)(color.b * keep + grey.b * 255f * (1f - keep)),
            color.a);
    }
}
