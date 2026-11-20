using System;
using System.Collections.Generic;
using UnityEngine;
using Point = System.Numerics.Vector3;

namespace Redline;

internal sealed class BloodLighting
{
    public static BloodLighting Room { get; } = new();

    private readonly List<Source> lights = new();
    private readonly Dictionary<(int X, int Y, int Z, int Normal), Color32> surfaces = new();
    private float nextSample;
    private Point ambient;

    public void Refresh(float now)
    {
        if (now < nextSample)
            return;
        nextSample = now + 0.7f;
        lights.Clear();
        surfaces.Clear();
        var roomAmbient = RenderSettings.ambientLight;
        ambient = new Point(0.025f) + Rgb(roomAmbient) * (0.85f * Mathf.Clamp01(RenderSettings.ambientIntensity));
        foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
        {
            if (!light || !light.isActiveAndEnabled || (light.cullingMask & 1) == 0 || light.intensity <= 0)
                continue;
            bool directional = light.type == LightType.Directional;
            bool spot = light.type == LightType.Spot;
            if ((!directional && light.range <= 0.01f) || (!directional && !spot && light.type != LightType.Point))
                continue;
            var transform = light.transform;
            float cone = spot ? Mathf.Cos(light.spotAngle * ((float)Math.PI / 360f)) : -1;
            var output = Rgb(light.color) * Mathf.Sqrt(light.intensity);
            float shadow = light.shadows == LightShadows.None ? 0 : Mathf.Clamp01(light.shadowStrength);
            lights.Add(new Source(Rig.ToPoint(transform.position), Rig.ToPoint(transform.forward),
                light.range * light.range, output, cone, directional, spot, shadow));
        }
    }

    public Color32 SurfaceTint(Vector3 position, Vector3 normal)
    {
        int plane = normal.z < -0.65f ? -3 : normal.z > 0.65f ? 3
            : normal.x < -0.65f ? -1 : normal.x > 0.65f ? 1 : normal.y < 0 ? -2 : 2;
        var cell = (Mathf.FloorToInt(position.x * 1.45f), Mathf.FloorToInt(position.y * 1.45f),
            Mathf.FloorToInt(position.z * 1.45f), plane);
        if (!surfaces.TryGetValue(cell, out var tint))
            surfaces[cell] = tint = Tint(position, normal);
        return tint;
    }

    private Color32 Tint(Vector3 position, Vector3 normal)
    {
        var point = Rig.ToPoint(position);
        var surface = Rig.ToPoint(normal);
        var energy = ambient;
        Shadow key = default;
        Shadow fill = default;
        foreach (var light in lights)
        {
            Point contribution;
            Point sourcePosition = light.Position;
            if (light.Directional)
            {
                contribution = light.Output * Math.Max(0, Point.Dot(surface, -light.Direction));
                sourcePosition = point - light.Direction * 40f;
            }
            else
            {
                var delta = light.Position - point;
                float distanceSquared = delta.LengthSquared();
                if (distanceSquared >= light.RangeSquared || distanceSquared < 0.0001f)
                    continue;
                var direction = delta / (float)Math.Sqrt(distanceSquared);
                float direct = Math.Max(0, Point.Dot(surface, direction));
                float rangeFraction = distanceSquared / light.RangeSquared;
                float falloff = 1 - rangeFraction;
                float cone = 1f;
                if (light.Spot)
                {
                    float angle = Point.Dot(light.Direction, -direction);
                    cone = Math.Min(1, Math.Max(0, (angle - light.Cone) * 10f));
                }
                contribution = light.Output * (direct * falloff * falloff * cone * cone / (1 + 1.6f * rangeFraction));
            }
            energy += contribution;
            if (light.ShadowStrength <= 0)
                continue;
            var blocked = contribution * light.ShadowStrength;
            float brightness = Luminance(blocked);
            if (brightness > key.Brightness)
            {
                fill = key;
                key = new Shadow(sourcePosition, blocked, brightness);
            }
            else if (brightness > fill.Brightness)
                fill = new Shadow(sourcePosition, blocked, brightness);
        }
        var start = position + normal * 0.07f;
        return Exposure(Point.Max(Point.Zero, energy - Occlusion(start, key) - Occlusion(start, fill)));
    }

    private static Point Occlusion(Vector3 start, Shadow light) => light.Brightness > 0.045f && Physics.Linecast(start,
        new Vector3(light.Position.X, light.Position.Y, light.Position.Z), Battlefield.RoomSolids,
        QueryTriggerInteraction.Ignore) ? light.Blocked : Point.Zero;

    private static Color32 Exposure(Point energy)
    {
        float intensity = Luminance(energy);
        float lightLevel = Math.Min(1f, intensity / 0.78f);
        float dimmer = 0.35f + 0.65f * lightLevel * lightLevel * (3f - 2f * lightLevel);
        float exposure = (0.27f + 0.69f * intensity / (intensity + 0.82f)) * dimmer;
        return new Color32(Channel(energy.X), Channel(energy.Y), Channel(energy.Z), 255);

        byte Channel(float value)
        {
            float ratio = Math.Min(1.07f, Math.Max(0.70f, (value + 0.32f) / (intensity + 0.32f)));
            return (byte)(255f * Math.Min(0.97f, exposure * ratio));
        }
    }

    public void Clear()
    {
        lights.Clear();
        surfaces.Clear();
        nextSample = 0;
    }

    private static Point Rgb(Color color) => new(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b));
    private static float Luminance(Point color) => Point.Dot(color, new Point(0.2126f, 0.7152f, 0.0722f));

    private readonly record struct Source(Point Position, Point Direction, float RangeSquared,
        Point Output, float Cone, bool Directional, bool Spot, float ShadowStrength);
    private readonly record struct Shadow(Point Position, Point Blocked, float Brightness);
}
