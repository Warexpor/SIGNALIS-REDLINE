using UnityEngine;

namespace Redline;

internal readonly record struct Footstep(Vector3 Start, Vector3 End, Vector3 Direction, float Speed, float Floor)
{
    private const float ElsterCapsuleRadius = 1.8f;
    public bool Moving => Speed > 0;

    public Vector3 Push(DebrisContact contact)
    {
        if (!Moving || contact.Bottom < -Floor - 1.1f || contact.Bottom > -Floor + 1.35f)
            return Vector3.zero;

        // Test the swept foot path against the grounded section, including contact from the side.
        var a = Battlefield.OnPlane(contact.Start - Start);
        var axis = Battlefield.OnPlane(contact.End - contact.Start);
        var travel = Battlefield.OnPlane(End - Start);
        float reach = ElsterCapsuleRadius + 0.14f + contact.Radius;
        float broadReach = axis.magnitude + travel.magnitude + reach;
        if (a.sqrMagnitude > broadReach * broadReach)
            return Vector3.zero;
        var center = ClosestOnSegment(travel * 0.5f, a, axis);
        var foot = ClosestOnSegment(center, Vector3.zero, travel);
        center = ClosestOnSegment(foot, a, axis);
        if ((center - foot).sqrMagnitude > reach * reach || Vector3.Dot(center, Direction) < -0.12f)
            return Vector3.zero;
        if (Physics.Linecast(Battlefield.AtHeight(Start + foot, Floor + 0.32f),
            Battlefield.AtHeight(Start + center, Floor + 0.32f), Battlefield.RoomSolids, QueryTriggerInteraction.Ignore))
            return Vector3.zero;
        return Direction * Mathf.Min(7.5f, Speed * 0.68f);
    }

    private static Vector3 ClosestOnSegment(Vector3 point, Vector3 origin, Vector3 segment) =>
        origin + segment * Mathf.Clamp01(Vector3.Dot(point - origin, segment) / Mathf.Max(0.0001f, segment.sqrMagnitude));
}

internal readonly record struct DebrisContact(Vector3 Start, Vector3 End, float Radius)
{
    public Vector3 Center => (Start + End) * 0.5f;
    public float Bottom => Mathf.Max(Start.z, End.z) + Radius;

    public static DebrisContact Around(Vector3 center, Vector3 span, float radius) =>
        new(center - span, center + span, radius);

    public DebrisContact Translate(Vector3 move) => new(Start + move, End + move, Radius);

    public static float FloorCorrection(float bodyBottom, float floor, bool landed, bool settled)
    {
        float correction = bodyBottom + floor + 0.025f;
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

    public static void PushRigid(Rigidbody body, Vector3 drive)
    {
        float speed = drive.magnitude;
        if (speed < 0.01f)
            return;
        var direction = drive / speed;
        float needed = speed - Vector3.Dot(body.velocity, direction);
        if (needed <= 0.01f)
            return;
        body.WakeUp();
        body.AddForce(direction * Mathf.Min(needed, 94f * Time.deltaTime), ForceMode.VelocityChange);
    }
}

internal struct GroundSlide
{
    private Vector3 velocity;
    public bool Moving => velocity.sqrMagnitude > 0.01f;

    public Vector3 Advance(Vector3 drive, float friction)
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        velocity = drive.sqrMagnitude > 0.01f ? drive : Vector3.MoveTowards(velocity, Vector3.zero, friction * dt);
        return Moving ? velocity * dt : Vector3.zero;
    }

    public void Stop() => velocity = Vector3.zero;
}
