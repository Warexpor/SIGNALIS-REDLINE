using System;
using System.Collections.Generic;
using UnityEngine;

namespace Redline;

internal enum Part : byte { Body, LeftLeg, RightLeg, LeftArm, RightArm, Head }

internal sealed class Rig
{
    private readonly Dictionary<string, Transform> bones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pose> overridden = new(18);
    private readonly HashSet<int> remembered = new();
    private Transform[] ragdollBones;
    public readonly Transform Hips;
    public readonly Transform Chest;
    public readonly Transform Head;
    public readonly Transform LeftThigh;
    public readonly Transform RightThigh;
    public readonly Transform LeftShin;
    public readonly Transform RightShin;
    public readonly Transform LeftFoot;
    public readonly Transform RightFoot;
    public readonly Transform LeftArm;
    public readonly Transform RightArm;
    public readonly Transform LeftForearm;
    public readonly Transform RightForearm;
    public readonly Transform LeftHand;
    public readonly Transform RightHand;

    public Rig(Animator animator)
    {
        if (!animator)
            return;
        foreach (Transform bone in animator.GetComponentsInChildren<Transform>(true))
            if (!bones.ContainsKey(bone.name))
                bones.Add(bone.name, bone);

        Hips = Get("hips", HumanBodyBones.Hips);
        Chest = Get("chest", HumanBodyBones.Chest);
        Head = Get("head", HumanBodyBones.Head);
        LeftThigh = Get("thigh_L", HumanBodyBones.LeftUpperLeg);
        RightThigh = Get("thigh_R", HumanBodyBones.RightUpperLeg);
        LeftShin = Get("shin_L", HumanBodyBones.LeftLowerLeg);
        RightShin = Get("shin_R", HumanBodyBones.RightLowerLeg);
        LeftFoot = Get("foot_L", HumanBodyBones.LeftFoot);
        RightFoot = Get("foot_R", HumanBodyBones.RightFoot);
        LeftArm = Get("upper_arm_L", HumanBodyBones.LeftUpperArm);
        RightArm = Get("upper_arm_R", HumanBodyBones.RightUpperArm);
        LeftForearm = Get("forearm_L", HumanBodyBones.LeftLowerArm);
        RightForearm = Get("forearm_R", HumanBodyBones.RightLowerArm);
        LeftHand = Get("hand_L", HumanBodyBones.LeftHand);
        RightHand = Get("hand_R", HumanBodyBones.RightHand);

        Transform Get(string name, HumanBodyBones human) => bones.TryGetValue(name, out var bone)
            ? bone : animator.isHuman && animator.avatar && animator.avatar.isValid ? animator.GetBoneTransform(human) : null;
    }

    public bool CanCrawl => Hips && Chest && LeftArm && RightArm && LeftForearm && RightForearm && LeftHand && RightHand;
    public bool CanRagdoll => Hips && Chest && Head && (LeftArm || RightArm) && (LeftThigh || RightThigh);

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

    public Snapshot Capture()
    {
        if (!CanRagdoll)
            return null;
        if (ragdollBones == null)
            ragdollBones = Hips.GetComponentsInChildren<Transform>(true);
        var bones = new Pose[ragdollBones.Length];
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

    public void Crawl(Transform actor, float floor, float phase, float movement, float amount,
        bool leftLegMissing, bool rightLegMissing, bool leftArmMissing, bool rightArmMissing)
    {
        if (!CanCrawl)
            return;
        Vector3 front = Battlefield.OnPlane(actor.forward).normalized;
        if (front.sqrMagnitude < 0.01f)
            front = Battlefield.OnPlane(Chest.forward).normalized;
        Vector3 right = Vector3.Cross(Battlefield.Up, front);
        float height = Mathf.Clamp(Vector3.Distance(LeftArm.position, RightArm.position) * 3.3f, 6f, 12f);
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
        if (!rightArmMissing) Reach(RightArm, RightForearm, RightHand, 1, phase + (float)Math.PI);
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
