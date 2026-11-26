using UnityEngine;

namespace Redline;

internal readonly record struct DebrisContact(Vector3 Start, Vector3 End, float Radius)
{
    public Vector3 Center => (Start + End) * 0.5f;
    public float Bottom => Mathf.Max(Start.z, End.z) + Radius;

    public static DebrisContact Around(Vector3 center, Vector3 span, float radius) =>
        new(center - span, center + span, radius);

    public DebrisContact Translate(Vector3 move) => new(Start + move, End + move, Radius);

    public static float FloorCorrection(float bodyBottom, float floor, bool landed, bool settled)
    {
        float correction = bodyBottom + floor + 0.01f;
        return settled ? correction : Mathf.Max(landed ? -0.22f : 0f, correction);
    }

    public static Vector3 LimitTravel(Vector3 center, Vector3 move, float floor, float radius)
    {
        float length = move.magnitude;
        if (length < 0.0001f)
            return Vector3.zero;
        radius = Mathf.Clamp(radius, 0.18f, 0.72f);
        var origin = Battlefield.AtHeight(center, floor + radius + 0.14f);
        if (!Physics.SphereCast(origin, radius, move / length, out var hit, length + 0.06f,
            Battlefield.RoomSolids, QueryTriggerInteraction.Ignore))
            return move;
        return move * Mathf.Clamp01((hit.distance - 0.06f) / length);
    }

    public static Vector3 LaunchWallCorrection(Vector3 from, Vector3 to, float floor, float radius, out Vector3 normal)
    {
        normal = Vector3.zero;
        var move = Battlefield.OnPlane(to - from);
        float distance = move.magnitude;
        if (distance < 0.001f)
            return Vector3.zero;
        var direction = move / distance;
        var origin = Battlefield.AtHeight(from, Mathf.Max(Battlefield.Height(from), floor + radius + 0.14f));
        if (!Physics.SphereCast(origin, radius, direction, out var hit, distance + 0.06f,
            Battlefield.RoomSolids, QueryTriggerInteraction.Ignore))
            return Vector3.zero;
        var wall = Battlefield.OnPlane(hit.normal);
        if (wall.sqrMagnitude < 0.45f || Vector3.Dot(wall, direction) > -0.05f)
            return Vector3.zero;
        normal = wall.normalized;
        return direction * (Mathf.Max(0, hit.distance - 0.06f) - distance);
    }

    public static void BounceOffLaunchWall(Rigidbody body, Vector3 normal)
    {
        float speed = Vector3.Dot(body.velocity, normal);
        if (speed < 0)
            body.velocity -= normal * (speed * 1.18f);
    }
}
