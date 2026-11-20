using UnityEngine;

namespace Redline;

internal static class Battlefield
{
    // SIGNALIS plays on XY. Its serialized PhysicsManager uses gravity (0, 0, 10).
    public static readonly Vector3 Up = Vector3.back;
    public static readonly Vector3 Down = Vector3.forward;
    public const int WalkableSurface = 1 << 9;
    public const int RoomSolids = (1 << 8) | (1 << 22);

    public static float Height(Vector3 position) => -position.z;
    public static Vector3 OnPlane(Vector3 direction) => new(direction.x, direction.y, 0);
    public static Vector3 AtHeight(Vector3 position, float height) => new(position.x, position.y, -height);

    public static string Format(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

    public static float FloorBelow(Vector3 position, float referenceHeight) =>
        FloorBelow(position, referenceHeight, out _);

    public static float FloorBelow(Vector3 position, float referenceHeight, out string surface)
    {
        // Use the room floor near the actor. A lower floor through stairs and openings is a different level.
        var walkableProbe = AtHeight(position, referenceHeight + 2.0f);
        if (Physics.Raycast(walkableProbe, Down, out var hit, 2.48f, WalkableSurface, QueryTriggerInteraction.Ignore)
            && Vector3.Dot(hit.normal, Up) > 0.70f)
        {
            surface = hit.collider ? $"walkable:{hit.collider.name}" : "walkable";
            return Height(hit.point);
        }

        var floorProbe = AtHeight(position, referenceHeight + 0.42f);
        if (Physics.Raycast(floorProbe, Down, out hit, 0.84f, RoomSolids, QueryTriggerInteraction.Ignore)
            && Vector3.Dot(hit.normal, Up) > 0.82f)
        {
            surface = hit.collider ? $"room-solid:{hit.collider.name}" : "room-solid";
            return Height(hit.point);
        }
        surface = "actor-plane";
        return referenceHeight;
    }
}
