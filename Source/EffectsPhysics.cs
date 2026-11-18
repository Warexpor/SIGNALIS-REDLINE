using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed class EffectsPhysics
{
    private Scene scene;
    private PhysicsScene physics;
    private float time;
    private readonly Queue<DetachedLimb> limbs = new();

    public GameObject Create(string name, Vector3 position)
    {
        if (!scene.IsValid())
        {
            scene = SceneManager.CreateScene("REDLINE Effects", new CreateSceneParameters { localPhysicsMode = LocalPhysicsMode.Physics3D });
            physics = PhysicsSceneExtensions.GetPhysicsScene(scene);
        }
        var go = new GameObject(name);
        go.transform.position = position;
        SceneManager.MoveGameObjectToScene(go, scene);
        return go;
    }

    public GameObject Floor(Vector3 position, float floor)
    {
        var go = Create("REDLINE Physics Ground", Battlefield.AtHeight(position, floor - 0.2f));
        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(26, 26, 0.4f);
        return go;
    }

    public DetachedLimb Limb(Vector3 cut, Vector3 tip, float floor, Vector3 push, float width)
    {
        Trim();
        if (limbs.Count >= 18)
            limbs.Dequeue().Dispose();
        var body = Create("REDLINE Detached Limb", cut);
        Vector3 axis = tip - cut;
        body.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
        var rb = body.AddComponent<Rigidbody>();
        var box = body.AddComponent<BoxCollider>();
        box.center = Vector3.up * axis.magnitude * 0.5f;
        box.size = new Vector3(width * 2, Mathf.Max(width * 2, axis.magnitude), width * 2);
        rb.mass = 1.3f;
        rb.drag = 0.31f;
        rb.angularDrag = 1.25f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.velocity = push * Random.Range(3.5f, 6.3f) + Battlefield.Up * Random.Range(1.7f, 3.1f);
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.angularVelocity = Random.onUnitSphere * Random.Range(4.1f, 8.5f);
        var limb = new DetachedLimb(body, Floor(cut, floor), Time.time + 25);
        limbs.Enqueue(limb);
        return limb;
    }

    public void Tick()
    {
        Trim();
        if (!scene.IsValid() || !physics.IsValid())
            return;
        time += Mathf.Min(Time.deltaTime, 0.05f);
        const float step = 1f / 60;
        while (time >= step)
        {
            physics.Simulate(step);
            time -= step;
        }
    }

    private void Trim()
    {
        while (limbs.Count != 0 && (limbs.Peek().Expires <= Time.time || !limbs.Peek().Body))
            limbs.Dequeue().Dispose();
    }

    public void ClearLimbs()
    {
        while (limbs.Count != 0)
            limbs.Dequeue().Dispose();
    }
}

internal sealed class DetachedLimb(GameObject body, GameObject floor, float expires)
{
    public GameObject Body { get; } = body;
    public float Expires { get; } = expires;
    public readonly List<Mesh> Meshes = new();

    public void Dispose()
    {
        if (Body) Object.Destroy(Body);
        if (floor) Object.Destroy(floor);
        foreach (var mesh in Meshes)
            if (mesh) Object.Destroy(mesh);
        Meshes.Clear();
    }
}

internal sealed class BodyRagdoll
{
    private static int running;
    private readonly List<Link> links = new(12);
    private readonly GameObject ground;
    private readonly Rig.Snapshot startPose;
    private readonly float started = Time.time;
    private Vector3 settledHips;
    private Quaternion[] settled;
    private bool simulating = true;

    public static BodyRagdoll Start(Rig rig, EffectsPhysics physics, Vector3 hit, float ground, int missing)
        => running >= 8 || !rig.CanRagdoll ? null : new BodyRagdoll(rig, physics, hit, ground, missing);

    private BodyRagdoll(Rig rig, EffectsPhysics physics, Vector3 hit, float floor, int missing)
    {
        startPose = rig.Capture();
        ground = physics.Floor(startPose.HipsPosition, floor);
        running++;
        try
        {
            var hips = Add(rig.Hips, rig.Chest, null, 5.3f, 0.78f);
            var chest = Add(rig.Chest, rig.Head, hips, 4.8f, 0.82f);
            Add(rig.Head, null, chest, 1.7f, 0.54f, Part.Head);
            var leftArm = Add(rig.LeftArm, rig.LeftForearm, chest, 1.3f, 0.36f, Part.LeftArm);
            var rightArm = Add(rig.RightArm, rig.RightForearm, chest, 1.3f, 0.36f, Part.RightArm);
            if (leftArm != null) Add(rig.LeftForearm, rig.LeftHand, leftArm, 0.9f, 0.31f);
            if (rightArm != null) Add(rig.RightForearm, rig.RightHand, rightArm, 0.9f, 0.31f);
            var leftLeg = Add(rig.LeftThigh, rig.LeftShin, hips, 2.3f, 0.50f);
            var rightLeg = Add(rig.RightThigh, rig.RightShin, hips, 2.3f, 0.50f);
            Add(rig.LeftShin, rig.LeftFoot, leftLeg, 1.5f, 0.38f, Part.LeftLeg);
            Add(rig.RightShin, rig.RightFoot, rightLeg, 1.5f, 0.38f, Part.RightLeg);

            hips?.Body.AddForce(hit * 1.8f, ForceMode.VelocityChange);
            chest?.Body.AddForce(hit * 4.25f + Battlefield.Up * 0.2f, ForceMode.VelocityChange);
            chest?.Body.AddTorque(Vector3.Cross(Battlefield.Up, hit) * 3.2f, ForceMode.VelocityChange);
        }
        catch
        {
            Dispose();
            throw;
        }

        Link Add(Transform bone, Transform tip, Link parent, float mass, float radius, Part part = Part.Body)
        {
            if (!bone || (missing & (1 << (int)part)) != 0)
                return null;
            Vector3 axis = tip ? tip.position - bone.position : Battlefield.Up * 0.16f;
            if (axis.sqrMagnitude < 0.0001f)
                axis = Battlefield.Up * 0.16f;
            var go = physics.Create("REDLINE Ragdoll Proxy", bone.position);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            var rigid = go.AddComponent<Rigidbody>();
            rigid.mass = mass;
            rigid.drag = 0.65f;
            rigid.angularDrag = 2.1f;
            rigid.solverIterations = 10;
            rigid.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var volume = go.AddComponent<CapsuleCollider>();
            volume.center = tip ? Vector3.up * axis.magnitude * 0.5f : Vector3.zero;
            volume.height = Mathf.Max(radius * 2, axis.magnitude + radius);
            volume.radius = radius;
            foreach (var other in links)
                Physics.IgnoreCollision(volume, other.Volume);
            if (parent != null)
            {
                var joint = go.AddComponent<CharacterJoint>();
                joint.connectedBody = parent.Body;
                joint.axis = Vector3.up;
                joint.swingAxis = Vector3.right;
                joint.enableProjection = true;
                joint.enablePreprocessing = false;
                joint.projectionDistance = 0.23f;
                joint.projectionAngle = 15;
                joint.lowTwistLimit = new SoftJointLimit { limit = -22 };
                joint.highTwistLimit = new SoftJointLimit { limit = 22 };
                joint.swing1Limit = new SoftJointLimit { limit = 50 };
                joint.swing2Limit = new SoftJointLimit { limit = 37 };
            }
            var link = new Link(bone, rigid, volume, Quaternion.Inverse(go.transform.rotation) * bone.rotation);
            links.Add(link);
            return link;
        }
    }

    public void Pose(Rig rig)
    {
        if (simulating && Time.time - started >= 2.6f)
            Settle();
        rig.Apply(startPose);
        Vector3 position = simulating ? links[0].Body.position : settledHips;
        var travel = Vector3.ClampMagnitude(Battlefield.OnPlane(position - startPose.HipsPosition), 2.35f);
        rig.Hips.position = Battlefield.AtHeight(startPose.HipsPosition + travel, Battlefield.Height(position));
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link.Bone)
                link.Bone.rotation = simulating ? link.Body.rotation * link.Offset : settled[i];
        }
    }

    public bool Landed => !simulating || Time.time - started >= 1.2f;

    public void Settle()
    {
        if (!simulating)
            return;
        settledHips = links[0].Body.position;
        settled = new Quaternion[links.Count];
        for (int i = 0; i < settled.Length; i++)
            settled[i] = links[i].Body.rotation * links[i].Offset;
        EndSimulation();
    }

    public void Dispose() => EndSimulation();

    private void EndSimulation()
    {
        if (!simulating)
            return;
        simulating = false;
        running--;
        foreach (var link in links)
            if (link.Body) Object.Destroy(link.Body.gameObject);
        if (ground) Object.Destroy(ground);
    }

    private sealed record Link(Transform Bone, Rigidbody Body, Collider Volume, Quaternion Offset);
}
