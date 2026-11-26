using System;
using System.Collections.Generic;
using UnityEngine;
using Point = System.Numerics.Vector3;

namespace Redline;

internal sealed class Rig
{
    private readonly Dictionary<string, Transform> bones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pose> overridden = new(18);
    private readonly HashSet<int> remembered = new();
    private Transform[] ragdollBones;
    public readonly Transform Hips;
    public readonly Transform Chest;
    public readonly Transform Neck;
    public readonly Transform Head;
    public readonly Transform LeftThigh;
    public readonly Transform RightThigh;
    public readonly Transform LeftShin;
    public readonly Transform RightShin;
    public readonly Transform LeftFoot;
    public readonly Transform RightFoot;
    public readonly Transform LeftToe;
    public readonly Transform RightToe;
    public readonly Transform LeftArm;
    public readonly Transform RightArm;
    public readonly Transform LeftForearm;
    public readonly Transform RightForearm;
    public readonly Transform LeftHand;
    public readonly Transform RightHand;
    public readonly Transform LeftFinger;
    public readonly Transform RightFinger;

    public Rig(Animator animator)
    {
        if (!animator)
            return;
        foreach (Transform bone in animator.GetComponentsInChildren<Transform>(true))
            if (!bones.ContainsKey(bone.name))
                bones.Add(bone.name, bone);

        Hips = Get("hips", HumanBodyBones.Hips);
        Chest = Get("chest", HumanBodyBones.Chest);
        Neck = Get("neck", HumanBodyBones.Neck);
        Head = Get("head", HumanBodyBones.Head);
        LeftThigh = Get("thigh_L", HumanBodyBones.LeftUpperLeg);
        RightThigh = Get("thigh_R", HumanBodyBones.RightUpperLeg);
        LeftShin = Get("shin_L", HumanBodyBones.LeftLowerLeg);
        RightShin = Get("shin_R", HumanBodyBones.RightLowerLeg);
        LeftFoot = Get("foot_L", HumanBodyBones.LeftFoot);
        RightFoot = Get("foot_R", HumanBodyBones.RightFoot);
        LeftToe = Get("toe_L", HumanBodyBones.LeftToes);
        RightToe = Get("toe_R", HumanBodyBones.RightToes);
        LeftArm = Get("upper_arm_L", HumanBodyBones.LeftUpperArm);
        RightArm = Get("upper_arm_R", HumanBodyBones.RightUpperArm);
        LeftForearm = Get("forearm_L", HumanBodyBones.LeftLowerArm);
        RightForearm = Get("forearm_R", HumanBodyBones.RightLowerArm);
        LeftHand = Get("hand_L", HumanBodyBones.LeftHand);
        RightHand = Get("hand_R", HumanBodyBones.RightHand);
        LeftFinger = Get("f_middle_01_L", HumanBodyBones.LeftMiddleProximal);
        RightFinger = Get("f_middle_01_R", HumanBodyBones.RightMiddleProximal);

        Transform Get(string name, HumanBodyBones human) => bones.TryGetValue(name, out var bone)
            ? bone : animator.isHuman && animator.avatar && animator.avatar.isValid ? animator.GetBoneTransform(human) : null;
    }

    public bool CanCrawl => Hips && Chest && LeftArm && RightArm && LeftForearm && RightForearm && LeftHand && RightHand;
    public bool CanRagdoll => Hips && Chest && Head && (LeftArm || RightArm) && (LeftThigh || RightThigh);
    // Measured hip height in a healthy stance. Tall units like STCR need their
    // own scale instead of the arm-span guess used before any measurement.
    public float ModelHeight { get; set; }

    public List<ImpactCuts.Zone> HitZones(int missing)
    {
        var zones = new List<ImpactCuts.Zone>(12);
        Add(Part.Body, Hips, Chest, 0.90f);
        Add(Part.Body, Chest, Neck, 0.76f);
        Add(Part.Head, Head, null, 0.85f);
        Add(Part.LeftLeg, LeftThigh, LeftShin, 0.52f);
        Add(Part.LeftLeg, LeftShin, LeftFoot, 0.44f);
        Add(Part.RightLeg, RightThigh, RightShin, 0.52f);
        Add(Part.RightLeg, RightShin, RightFoot, 0.44f);
        Add(Part.LeftArm, LeftArm, LeftForearm, 0.43f);
        Add(Part.LeftArm, LeftForearm, LeftHand, 0.36f);
        Add(Part.RightArm, RightArm, RightForearm, 0.43f);
        Add(Part.RightArm, RightForearm, RightHand, 0.36f);
        return zones;

        void Add(Part part, Transform from, Transform to, float radius)
        {
            if (!from || (missing & (1 << (int)part)) != 0)
                return;
            var end = to ? to.position : from.position;
            zones.Add(new ImpactCuts.Zone(part, ToPoint(from.position), ToPoint(end), radius));
        }
    }

    public static Point ToPoint(Vector3 value) => new(value.x, value.y, value.z);

    public Transform CutBone(Part part) => part switch
    {
        Part.LeftLeg => LeftShin,
        Part.RightLeg => RightShin,
        Part.LeftArm => LeftArm,
        Part.RightArm => RightArm,
        Part.Head => Head,
        _ => null
    };

    public Transform Tip(Part part) => part switch
    {
        Part.LeftLeg => LeftFoot,
        Part.RightLeg => RightFoot,
        Part.LeftArm => LeftHand,
        Part.RightArm => RightHand,
        _ => null
    };

    public void Remember(Transform bone)
    {
        if (bone && remembered.Add(bone.GetInstanceID()))
            overridden.Add(new Pose(bone, bone.localPosition, bone.localRotation));
    }

    public void RestorePose()
    {
        for (int i = overridden.Count - 1; i >= 0; i--)
        {
            var pose = overridden[i];
            if (!pose.Bone)
                continue;
            pose.Bone.localPosition = pose.Position;
            pose.Bone.localRotation = pose.Rotation;
        }
        overridden.Clear();
        remembered.Clear();
    }

    public Snapshot Capture(Snapshot reuse = null)
    {
        if (!CanRagdoll)
            return null;
        if (ragdollBones == null)
            ragdollBones = Hips.GetComponentsInChildren<Transform>(true);
        var bones = reuse?.Bones ?? new Pose[ragdollBones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = ragdollBones[i];
            if (bone)
                bones[i] = new Pose(bone, bone.localPosition, bone.localRotation);
        }
        return new Snapshot(bones, Hips.position, Hips.rotation);
    }

    public void Apply(Snapshot snapshot)
    {
        foreach (var pose in snapshot.Bones)
        {
            if (!pose.Bone)
                continue;
            Remember(pose.Bone);
            pose.Bone.localPosition = pose.Position;
            pose.Bone.localRotation = pose.Rotation;
        }
        Hips.SetPositionAndRotation(snapshot.HipsPosition, snapshot.HipsRotation);
    }

    public void BlendFrom(Snapshot snapshot, float weight)
    {
        var hipsPosition = Vector3.Lerp(snapshot.HipsPosition, Hips.position, weight);
        var hipsRotation = Quaternion.Slerp(snapshot.HipsRotation, Hips.rotation, weight);
        foreach (var pose in snapshot.Bones)
        {
            var bone = pose.Bone;
            if (!bone || bone == Hips)
                continue;
            Remember(bone);
            bone.localPosition = Vector3.Lerp(pose.Position, bone.localPosition, weight);
            bone.localRotation = Quaternion.Slerp(pose.Rotation, bone.localRotation, weight);
        }
        Remember(Hips);
        Hips.SetPositionAndRotation(hipsPosition, hipsRotation);
    }

    public Vector3 Swipe(Part arm, Vector3 forward, float sweep, float weight, bool crawling, float floor)
    {
        bool left = arm == Part.LeftArm;
        float side = left ? -1f : 1f;
        var upper = left ? LeftArm : RightArm;
        var elbow = left ? LeftForearm : RightForearm;
        var hand = left ? LeftHand : RightHand;
        var right = Vector3.Cross(Battlefield.Up, forward);
        float torsoTurn = Mathf.Lerp(24f, -30f, sweep) * side * weight;
        float arc = Mathf.Sin((float)Math.PI * sweep);
        float drive = arc * weight;
        Remember(Chest);
        Chest.rotation = Quaternion.AngleAxis(torsoTurn, Battlefield.Up) * Chest.rotation;
        Chest.position += forward * (drive * (crawling ? 0.3f : 0.52f));

        float extension = Mathf.Lerp(0.46f, 0.98f, arc);
        float yaw = Mathf.Lerp(77f, -37f, sweep) * side;
        Vector3 swing = Quaternion.AngleAxis(yaw, Battlefield.Up) * forward;
        float upperLength = Vector3.Distance(upper.position, elbow.position);
        float forearmLength = Vector3.Distance(elbow.position, hand.position);
        float height = floor + (crawling ? 1.55f : 4.7f) - Battlefield.Height(upper.position);
        Vector3 aim = swing * ((upperLength + forearmLength) * extension)
            + Battlefield.Up * Mathf.Clamp(height, -1.65f, 0.85f);
        float reach = Mathf.Clamp(aim.magnitude, Mathf.Abs(upperLength - forearmLength) + 0.01f,
            Mathf.Max(0.02f, upperLength + forearmLength - 0.01f));
        var axis = aim.normalized;
        float along = (reach * reach + upperLength * upperLength - forearmLength * forearmLength) / (2f * reach);
        float bend = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - along * along));
        var bias = Vector3.ProjectOnPlane(right * side + Battlefield.Down * 0.4f, axis).normalized;
        var endpoint = upper.position + axis * reach;
        AimBone(upper, elbow, axis * along + bias * bend, weight);
        AimBone(elbow, hand, endpoint - elbow.position, weight);
        return hand.position;
    }

    public void Crawl(Transform actor, float floor, float phase, float movement, float amount,
        bool leftLegMissing, bool rightLegMissing, bool leftArmMissing, bool rightArmMissing)
    {
        if (!CanCrawl)
            return;
        Vector3 front = Battlefield.OnPlane(actor.forward).normalized;
        if (front.sqrMagnitude < 0.01f)
            front = Battlefield.OnPlane(Chest.forward).normalized;
        Vector3 right = Vector3.Cross(Battlefield.Up, front);
        float height = ModelHeight > 0f ? ModelHeight
            : Mathf.Clamp(Vector3.Distance(LeftArm.position, RightArm.position) * 3.3f, 6f, 12f);
        float bob = Mathf.Sin(phase * 2) * 0.025f * movement;
        float pull = Mathf.Sin(phase) * 0.07f * movement;

        Remember(Hips);
        var hips = actor.position - front * (0.19f - pull) * height;
        hips = Battlefield.AtHeight(hips, floor + (0.19f + bob) * height);
        Hips.position = Vector3.Lerp(Hips.position, hips, amount);
        Hips.rotation = Quaternion.AngleAxis(amount * (69 + Mathf.Sin(phase) * 3 * movement), right) * Hips.rotation;

        if (Head)
        {
            Remember(Head);
            Head.rotation = Quaternion.AngleAxis(-29f * amount, right) * Head.rotation;
        }

        if (!leftArmMissing) Reach(LeftArm, LeftForearm, LeftHand, -1, phase);
        if (!rightArmMissing) Reach(RightArm, RightForearm, RightHand, 1, phase + (leftArmMissing ? 0 : (float)Math.PI));
        Drag(LeftThigh, LeftShin, -1, leftLegMissing);
        Drag(RightThigh, RightShin, 1, rightLegMissing);

        void Reach(Transform upper, Transform lower, Transform hand, float side, float cycle)
        {
            float reach = Mathf.Sin(cycle) * movement;
            float lift = Mathf.Max(0, Mathf.Cos(cycle)) * movement;
            var shoulder = upper.position;
            var handTarget = shoulder + front * ((0.13f + reach * 0.14f) * height) + right * (0.069f * side * height);
            handTarget = Battlefield.AtHeight(handTarget, floor + (0.045f + lift * 0.13f) * height);
            float upperLength = Vector3.Distance(shoulder, lower.position);
            float lowerLength = Vector3.Distance(lower.position, hand.position);
            Vector3 aim = handTarget - shoulder;
            float distance = Mathf.Clamp(aim.magnitude, 0.02f, Mathf.Max(0.021f, upperLength + lowerLength - 0.005f));
            var direction = aim.normalized;
            float length = (distance * distance + upperLength * upperLength - lowerLength * lowerLength) / (2 * distance);
            float bend = Mathf.Sqrt(Mathf.Max(0, upperLength * upperLength - length * length));
            Vector3 elbowBias = Vector3.ProjectOnPlane(right * side - front * 0.6f, direction).normalized;
            Vector3 elbow = shoulder + direction * length + elbowBias * bend;
            AimBone(upper, lower, elbow - shoulder, amount);
            AimBone(lower, hand, handTarget - lower.position, amount);
        }

        void Drag(Transform thigh, Transform shin, float side, bool missing)
        {
            if (!thigh || !shin)
                return;
            Vector3 direction = -front + right * (0.28f * side) + Battlefield.Down * (missing ? 0.20f : 0.53f);
            AimBone(thigh, shin, direction, amount);
            var foot = side < 0 ? LeftFoot : RightFoot;
            if (!missing && foot)
                AimBone(shin, foot, -front + right * (0.17f * side) + Battlefield.Up * 0.05f, amount);
        }
    }

    private void AimBone(Transform bone, Transform child, Vector3 direction, float amount)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude < 0.0001f || direction.sqrMagnitude < 0.0001f)
            return;
        Remember(bone);
        var rotation = Quaternion.FromToRotation(current, direction) * bone.rotation;
        bone.rotation = Quaternion.Slerp(bone.rotation, rotation, amount);
    }

    internal readonly record struct Pose(Transform Bone, Vector3 Position, Quaternion Rotation);
    internal sealed record Snapshot(Pose[] Bones, Vector3 HipsPosition, Quaternion HipsRotation);
}
