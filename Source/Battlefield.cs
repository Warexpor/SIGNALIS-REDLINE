using UnityEngine;

namespace Redline;

internal static class Battlefield
{
    // SIGNALIS plays on XY. Its serialized PhysicsManager uses gravity (0, 0, 10).
    public static readonly Vector3 Up = Vector3.back;
    public static readonly Vector3 Down = Vector3.forward;
    public const int WalkableSurface = 1 << 9;

    public static float Height(Vector3 position) => -position.z;
    public static Vector3 OnPlane(Vector3 direction) => new(direction.x, direction.y, 0);
    public static Vector3 AtHeight(Vector3 position, float height) => new(position.x, position.y, -height);

    public static float FloorBelow(Vector3 position, float referenceHeight)
    {
        var probe = AtHeight(position, referenceHeight + 1.6f);
        if (Physics.Raycast(probe, Down, out var hit, 5f, WalkableSurface, QueryTriggerInteraction.Ignore)
            && Vector3.Dot(hit.normal, Up) > 0.55f)
            return Height(hit.point);
        return referenceHeight;
    }
}
