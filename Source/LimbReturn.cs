using System;
using UnityEngine;

namespace Redline;

internal readonly record struct SeveredPart(Part Part, DetachedLimb Limb, Vector3 SocketOffset, Quaternion SocketRotation)
{
    public static SeveredPart Capture(Part part, DetachedLimb limb, Transform bone)
    {
        var visual = limb.Visual.transform;
        return new SeveredPart(part, limb, bone.InverseTransformPoint(visual.position),
            Quaternion.Inverse(bone.rotation) * visual.rotation);
    }

    public Vector3 Socket(Transform bone) => bone.TransformPoint(SocketOffset);
}

internal sealed class LimbReturn
{
    private const float DragEnd = 0.70f;
    private const float DragDistance = 0.78f;
    private readonly Transform bone;
    private readonly Vector3 origin;
    private readonly Quaternion rotation;
    private readonly float start;
    private readonly float duration;
    private readonly float originFloor;

    public LimbReturn(SeveredPart severed, Transform bone, float floor, float start)
    {
        Severed = severed;
        this.bone = bone;
        this.start = start;
        var visual = severed.Limb.Visual.transform;
        origin = visual.position;
        rotation = visual.rotation;
        originFloor = Battlefield.FloorBelow(origin, floor);
        float distance = Battlefield.OnPlane(severed.Socket(bone) - origin).magnitude;
        duration = Mathf.Clamp(0.52f + distance * 0.05f, 0.62f, 0.95f);
    }

    public SeveredPart Severed { get; }
    public Vector3 Socket => Severed.Socket(bone);

    public static bool HasPath(Vector3 from, Vector3 to, float floor)
    {
        var travel = Battlefield.OnPlane(to - from);
        float length = travel.magnitude;
        if (length < 0.06f)
            return true;
        var fromFloor = Battlefield.AtHeight(from, floor + 0.35f);
        return !Physics.SphereCast(fromFloor, 0.19f, travel / length, out _, length,
            Battlefield.RoomSolids, QueryTriggerInteraction.Ignore);
    }

    public ReturnPhase Advance(float now, float floor)
    {
        var limb = Severed.Limb.Visual;
        if (!limb || !limb.activeInHierarchy || !bone)
            return ReturnPhase.Blocked;
        float progress = Mathf.Clamp01((now - start) / duration);
        if (progress <= 0f)
            return ReturnPhase.Pulling;

        var socket = Socket;
        var visual = limb.transform;
        var travel = Battlefield.OnPlane(socket - origin);
        float drag = Mathf.InverseLerp(0.12f, DragEnd, progress);
        float lift = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(DragEnd, 1f, progress));
        // A short jerk, then a fast drag and a separate lift into the joint.
        float reach = DragDistance * Mathf.SmoothStep(0f, 1f, drag) + (1f - DragDistance) * lift;
        var position = origin + travel * reach;
        float surface = Battlefield.FloorBelow(position, originFloor);
        float restingHeight = Battlefield.Height(origin) + surface - originFloor;
        float twitch = Mathf.Sin((float)Math.PI * Mathf.InverseLerp(0f, 0.20f, progress));
        float height = Mathf.Lerp(restingHeight + 0.095f * twitch, Battlefield.Height(socket), lift);
        position = Battlefield.AtHeight(position, height);
        if (!HasPath(visual.position, position, floor))
            return ReturnPhase.Blocked;

        var kick = Quaternion.AngleAxis(9f * twitch, Battlefield.Up) * rotation;
        float turn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.40f, 1f, progress));
        visual.SetPositionAndRotation(position, Quaternion.Slerp(kick, bone.rotation * Severed.SocketRotation, turn));
        return progress >= 1f ? ReturnPhase.Joined : ReturnPhase.Pulling;
    }
}

internal enum ReturnPhase { Pulling, Joined, Blocked }
