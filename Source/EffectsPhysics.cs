using System;
using System.Collections.Generic;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed class EffectsPhysics(RedlineSettings settings, Action<string> log)
{
    // Enemy rigs are roughly ten units tall. At about six units/metre this is one g.
    public const float Gravity = 58f;
    public const float Step = 1f / 120f;
    private const float MaxFrameTime = 0.10f;

    private Scene scene;
    private PhysicsScene physics;
    private int collisionLayer;
    private PhysicMaterial surface;
    private float time;
    private readonly List<(Rigidbody Body, Collider Shape)> bodies = new();
    private readonly List<BoxCollider> floors = new();
    private readonly Dictionary<string, List<DetachedLimb>> levelLimbs = new();
    private readonly List<DetachedLimb> movingLimbs = new();
    private string level = "";
    private bool sampledPlayer;
    private Vector3 previousPlayer;
    private Vector3 sampledFloorAt;
    private float playerFloor;

    public float SimulatedTimeThisFrame { get; private set; }
    private Footstep PlayerStep { get; set; }
    public RagdollTuning LaunchTuning => new(settings.ExtremeRagdoll.Value, RedlineSettings.Factor(settings.ThrowSpeed),
        RedlineSettings.Factor(settings.Lift), RedlineSettings.Factor(settings.Spin));

    public GameObject Create(string name, Vector3 position)
    {
        if (!scene.IsValid())
        {
            // SIGNALIS disables Default-to-Default collision. Use an unnamed colliding layer
            // in this private physics scene and leave the game's collision matrix alone.
            collisionLayer = Array.Find(new[] { 3, 6, 7 }, layer => !Physics.GetIgnoreLayerCollision(layer, layer));
            if (collisionLayer == 0)
                throw new InvalidOperationException("No colliding layer is available for REDLINE physics");
            scene = SceneManager.CreateScene("REDLINE Effects", new CreateSceneParameters { localPhysicsMode = LocalPhysicsMode.Physics3D });
            physics = PhysicsSceneExtensions.GetPhysicsScene(scene);
            log($"physics scene layer={collisionLayer} selfCollision=true gravity={Gravity:F1} step={Step:F4}");
        }
        var go = new GameObject(name) { layer = collisionLayer };
        go.transform.position = position;
        SceneManager.MoveGameObjectToScene(go, scene);
        return go;
    }

    public BoxCollider Floor(Vector3 position, float floor)
    {
        var go = Create("REDLINE Physics Ground", Battlefield.AtHeight(position, floor - 0.2f));
        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(32, 32, 0.4f);
        SetSurface(box);
        foreach (var body in bodies)
            if (body.Shape) Physics.IgnoreCollision(box, body.Shape);
        floors.Add(box);
        return box;
    }

    public Rigidbody Register(Collider shape, BoxCollider floor, float mass, float drag, float angularDrag)
    {
        SetSurface(shape);
        var rigid = shape.gameObject.AddComponent<Rigidbody>();
        rigid.useGravity = false;
        rigid.mass = mass;
        rigid.drag = drag;
        rigid.angularDrag = angularDrag;
        rigid.solverIterations = 16;
        rigid.solverVelocityIterations = 8;
        rigid.maxDepenetrationVelocity = 3f;
        // Gravity is applied manually here. A proxy that sleeps in the air would stop falling.
        rigid.sleepThreshold = 0f;
        rigid.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        foreach (var otherFloor in floors)
            if (otherFloor && otherFloor != floor)
                Physics.IgnoreCollision(shape, otherFloor);
        bodies.Add((rigid, shape));
        return rigid;
    }

    public void Remove(Rigidbody rigid)
    {
        for (int i = bodies.Count - 1; i >= 0; i--)
        {
            if (bodies[i].Body != rigid)
                continue;
            bodies.RemoveAt(i);
            break;
        }
        if (rigid)
        {
            rigid.gameObject.SetActive(false);
            Object.Destroy(rigid.gameObject);
        }
    }

    public void RemoveFloor(BoxCollider floor)
    {
        floors.Remove(floor);
        if (!floor)
            return;
        floor.enabled = false;
        Object.Destroy(floor.gameObject);
    }

    public bool HasSupportBelow(Vector3 point, float reach) =>
        physics.Raycast(point, Battlefield.Down, out _, reach, 1 << collisionLayer, QueryTriggerInteraction.Ignore);

    private void SetSurface(Collider shape)
    {
        if (!surface)
        {
            surface = new PhysicMaterial
            {
                name = "REDLINE Floor Contact",
                staticFriction = 0.9f,
                dynamicFriction = 0.68f,
                bounciness = 0.015f,
                frictionCombine = PhysicMaterialCombine.Average,
                bounceCombine = PhysicMaterialCombine.Minimum
            };
        }
        shape.sharedMaterial = surface;
        shape.contactOffset = 0.025f;
    }

    public DetachedLimb Limb(Vector3 cut, Vector3 tip, float floor, float width)
    {
        var limb = CreatePiece("REDLINE Detached Limb", cut, tip, floor, width, retain: true);
        limb.RetainedLevel = level;
        if (!levelLimbs.TryGetValue(level, out var limbs))
            levelLimbs.Add(level, limbs = new List<DetachedLimb>());
        limbs.Add(limb);
        movingLimbs.Add(limb);
        return limb;
    }

    public bool CanRecall(DetachedLimb limb) =>
        limb.RetainedLevel == level && limb.Visual && limb.Visual.activeInHierarchy && limb.Meshes.Count > 0;

    public IEnumerable<SeveredPart> FindRetainedInjuries(string owner)
    {
        if (string.IsNullOrEmpty(owner) || !levelLimbs.TryGetValue(level, out var limbs))
            yield break;
        // The latest cut wins when a checkpoint causes the same limb to be severed again.
        for (int i = limbs.Count - 1; i >= 0; i--)
        {
            var limb = limbs[i];
            if (limb.Injury is SeveredPart injury && limb.RecoveryOwner == owner && CanRecall(limb))
                yield return injury;
        }
    }

    public void HoldForReturn(DetachedLimb limb)
    {
        movingLimbs.Remove(limb);
        limb.HoldForReturn();
    }

    public void ConsumeLimb(DetachedLimb limb)
    {
        if (!levelLimbs.TryGetValue(limb.RetainedLevel, out var limbs) || !limbs.Remove(limb))
            return;
        movingLimbs.Remove(limb);
        limb.Dispose();
    }

    public DetachedLimb CreatePiece(string name, Vector3 cut, Vector3 tip, float floor, float width, bool retain = false)
    {
        var body = Create(name, cut);
        Vector3 axis = tip - cut;
        if (axis.sqrMagnitude < 0.0001f)
            axis = Battlefield.Up * width * 2;
        body.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
        var visual = body;
        if (retain)
        {
            visual = new GameObject(name + " Visual");
            visual.transform.SetPositionAndRotation(cut, body.transform.rotation);
            // The private physics scene can be unloaded during a native scene transition.
            Object.DontDestroyOnLoad(visual);
        }
        return new DetachedLimb(this, body, visual, Floor(cut, floor), floor, axis.magnitude, width);
    }

    public void Tick()
    {
        SamplePlayerStep();
        SimulatedTimeThisFrame = 0;
        if (scene.IsValid() && physics.IsValid() && bodies.Count != 0)
        {
            if (PlayerStep.Moving)
                foreach (var limb in movingLimbs)
                    limb.Nudge(PlayerStep);
            time += Mathf.Min(Time.deltaTime, MaxFrameTime);
            while (time >= Step)
            {
                foreach (var (body, _) in bodies)
                    if (body && !body.isKinematic && !body.IsSleeping())
                        body.AddForce(Battlefield.Down * Gravity, ForceMode.Acceleration);
                physics.Simulate(Step);
                time -= Step;
                SimulatedTimeThisFrame += Step;
            }
        }
        else time = 0;
        for (int i = movingLimbs.Count - 1; i >= 0; i--)
        {
            var limb = movingLimbs[i];
            limb.Tick(SimulatedTimeThisFrame);
            if (!limb.ReadyToFreeze)
                continue;
            limb.Freeze();
            movingLimbs.RemoveAt(i);
        }
        if (levelLimbs.TryGetValue(level, out var limbs))
            foreach (var limb in limbs)
                limb.SlideOnFloor(PlayerStep);
    }

    private void SamplePlayerStep()
    {
        PlayerStep = default;
        if (!settings.DebrisPush.Value)
        {
            sampledPlayer = false;
            return;
        }
        var controller = PlayerState.pcs;
        var player = PlayerState.player;
        if (!player || !player.activeInHierarchy || PlayerState.noplayer || PlayerState.cutscene || PlayerState.menu
            || PlayerState.animating || PlayerState.grappled)
        {
            sampledPlayer = false;
            return;
        }
        // Sample the live locomotion transform, including movement between rigidbody steps.
        var position = controller ? controller.transform.position : player.transform.position;
        var start = previousPlayer;
        previousPlayer = position;
        if (!sampledPlayer)
        {
            sampledPlayer = true;
            sampledFloorAt = position;
            playerFloor = Battlefield.FloorBelow(position, Battlefield.Height(position));
            return;
        }
        var move = Battlefield.OnPlane(position - start);
        float distance = move.magnitude;
        float speed = distance / Mathf.Max(0.004f, Time.deltaTime);
        if (distance > 2.8f || Mathf.Abs(position.z - start.z) > 0.85f)
            return;
        if ((position - sampledFloorAt).sqrMagnitude > 0.81f || Mathf.Abs(position.z - sampledFloorAt.z) > 0.25f)
        {
            sampledFloorAt = position;
            playerFloor = Battlefield.FloorBelow(position, Battlefield.Height(position));
        }
        if (speed >= 0.75f)
        {
            PlayerStep = new Footstep(start, position, move / distance, speed, playerFloor);
            return;
        }
        if (!controller || !controller.character || controller.traversing || PlayerState.suspendInput
            || controller.input.sqrMagnitude < 0.04f)
            return;
        var intent = Battlefield.OnPlane(controller.character.inputAxis);
        if (intent.sqrMagnitude > 0.04f)
            PlayerStep = new Footstep(position, position, intent.normalized, 4.8f, playerFloor);
    }

    public void SetLevel(string label)
    {
        sampledPlayer = false;
        PlayerStep = default;
        if (label is "StartupPC" or "MainMenu")
            ClearLimbs();
        if (label == level)
            return;

        foreach (var limb in movingLimbs)
            limb.Freeze();
        movingLimbs.Clear();
        SetVisibility(level, false);
        level = label;
        SetVisibility(level, true);
    }

    private void SetVisibility(string label, bool visible)
    {
        if (!levelLimbs.TryGetValue(label, out var limbs))
            return;
        foreach (var limb in limbs)
            if (limb.Visual)
            {
                limb.StopSlide();
                limb.Visual.SetActive(visible);
            }
        log($"retained limbs level={label} visible={visible} count={limbs.Count}");
    }

    public void ClearLimbs()
    {
        foreach (var limbs in levelLimbs.Values)
            foreach (var limb in limbs)
                limb.Dispose();
        movingLimbs.Clear();
        levelLimbs.Clear();
    }
}

internal readonly record struct RagdollTuning(bool Extreme, float Throw, float Lift, float Spin)
{
    public float Push(float speed) => Mathf.Min(Extreme ? 15f : 10.5f, Throw * speed * (Extreme ? 1.42f : 1f));
    public float VerticalSpeed(float speed) => Mathf.Clamp(speed * Lift, -1.7f, Extreme ? 2.4f : 1.3f);
    public Vector3 AngularSpeed(Vector3 velocity) => Vector3.ClampMagnitude(velocity * Spin, Extreme ? 3.1f : 1.8f);
}

internal sealed class DetachedLimb(EffectsPhysics physics, GameObject body, GameObject visual, BoxCollider floor,
    float floorHeight, float length, float width)
{
    public GameObject Body { get; } = body;
    public GameObject Visual { get; } = visual;
    public readonly List<Mesh> Meshes = new();
    private readonly List<Vector3> vertices = new();
    private readonly List<int> triangles = new();
    private Rigidbody rigid;
    private Collider shape;
    private Mesh collisionMesh;
    private float quietTime;
    private float contactTime;
    private float visualDepth;
    private bool parked;
    private bool returning;
    private Vector3 parkedPosition;
    private Quaternion parkedRotation;
    private bool trackLaunchWalls;
    private Vector3 previousLaunchPosition;
    private float parkedFloor;
    private DebrisContact parkedContact;
    private GroundSlide slide;
    private Vector3 proxyCenter;
    public string Diagnostics { get; private set; }
    public string RetainedLevel { get; set; }
    public float SupportFloor => parked ? parkedFloor : floorHeight;
    public string RecoveryOwner { get; set; }
    public SeveredPart? Injury { get; set; }
    public bool ReadyToFreeze => !rigid || contactTime > 0 && rigid.IsSleeping();

    public void AddSurface(Il2CppStructArray<Vector3> positions, Il2CppStructArray<int>[] sections)
    {
        Vector3[] local = null;
        MeshArrays.Read(positions, ref local);
        int[] indices = null;
        var remapped = new Dictionary<int, int>();
        foreach (var section in sections)
        {
            MeshArrays.Read(section, ref indices);
            foreach (int index in indices)
            {
                if (!remapped.TryGetValue(index, out int compact))
                {
                    compact = vertices.Count;
                    remapped.Add(index, compact);
                    vertices.Add(local[index]);
                }
                triangles.Add(compact);
            }
        }
    }

    public void Launch(Vector3 direction, bool fling)
    {
        var scatter = Random.insideUnitCircle * (fling ? 0.85f : 0.28f);
        float speed = fling ? Random.Range(4.8f, 7.8f) : Random.Range(0.7f, 1.65f);
        float lift = fling ? Random.Range(0.48f, 1.10f) : Random.Range(-0.36f, 0.24f);
        var velocity = direction * speed + new Vector3(scatter.x, scatter.y, 0)
            + Battlefield.Up * lift;
        Launch(velocity, Random.onUnitSphere * (fling ? Random.Range(3.5f, 6.5f) : Random.Range(1.4f, 3.2f)), 1.3f);
    }

    public void Launch(Vector3 velocity, Vector3 spin, float mass, bool trackWalls = false)
    {
        shape = ContactShape();
        rigid = physics.Register(shape, floor, mass, 0.10f, 0.34f);
        proxyCenter = rigid.position;
        float overlap = shape.bounds.max.z + floorHeight;
        if (overlap > 0)
            rigid.position += Battlefield.Up * (overlap + 0.02f);
        trackLaunchWalls = trackWalls;
        previousLaunchPosition = rigid.position;
        rigid.velocity = velocity;
        if (trackWalls && spin.sqrMagnitude > 49f)
            rigid.maxAngularVelocity = spin.magnitude;
        rigid.angularVelocity = spin;
        UpdateVisual(Mathf.Min(0, overlap));
        Diagnostics = $"detached collider={shape.GetIl2CppType().Name} vertices={vertices.Count} speed={rigid.velocity.magnitude:F2} floor={floorHeight:F2}";
        vertices.Clear();
        triangles.Clear();
    }

    public Vector3 PointVelocity(Vector3 point) => rigid ? rigid.GetPointVelocity(point) : Vector3.zero;

    public void SetKinematic(bool enabled)
    {
        if (rigid)
            rigid.isKinematic = enabled;
    }

    public void Kick(Vector3 velocity, Vector3 point)
    {
        if (!rigid)
            return;
        rigid.WakeUp();
        rigid.AddForceAtPosition(velocity, point, ForceMode.VelocityChange);
    }

    public void IgnoreContact(DetachedLimb other) => Physics.IgnoreCollision(shape, other.shape);

    public void Nudge(Footstep foot)
    {
        if (!foot.Moving || !rigid || !shape || Mathf.Abs(floorHeight - foot.Floor) > 1.1f)
            return;
        var bounds = shape.bounds;
        if (bounds.max.z < -foot.Floor - 0.95f)
            return;
        var contact = Footprint(bounds.center, rigid.rotation);
        var drive = foot.Push(contact);
        if (drive.sqrMagnitude < 0.01f || DebrisContact.LimitTravel(contact.Center, drive * 0.05f, floorHeight, contact.Radius) == Vector3.zero)
            return;
        contactTime = quietTime = 0;
        DebrisContact.PushRigid(rigid, drive);
    }

    public void SlideOnFloor(Footstep foot)
    {
        if (!parked || returning || !Visual || (!foot.Moving && !slide.Moving))
            return;
        var drive = foot.Push(parkedContact);
        var move = slide.Advance(drive, 19f);
        if (move == Vector3.zero)
            return;
        var allowed = DebrisContact.LimitTravel(parkedContact.Start, move, parkedFloor, parkedContact.Radius);
        allowed = DebrisContact.LimitTravel(parkedContact.End, allowed, parkedFloor, parkedContact.Radius);
        if (allowed == Vector3.zero)
        {
            slide.Stop();
            return;
        }
        float floor = Battlefield.FloorBelow(parkedContact.Center + allowed, parkedFloor);
        allowed += Battlefield.Up * (floor - parkedFloor);
        parkedFloor = floor;
        Visual.transform.position += allowed;
        parkedContact = parkedContact.Translate(allowed);
    }

    public void StopSlide() => slide.Stop();

    public void HoldForReturn()
    {
        Freeze();
        slide.Stop();
        parkedPosition = Visual.transform.position;
        parkedRotation = Visual.transform.rotation;
        returning = true;
    }

    public void CancelReturn(bool settleInPlace)
    {
        if (!returning)
            return;
        returning = false;
        if (!Visual)
            return;
        if (settleInPlace)
        {
            var travel = Battlefield.OnPlane(Visual.transform.position - parkedPosition);
            float surface = Battlefield.FloorBelow(parkedContact.Center + travel, parkedFloor);
            travel += Battlefield.Up * (surface - parkedFloor);
            parkedFloor = surface;
            parkedContact = parkedContact.Translate(travel);
            parkedPosition += travel;
        }
        Visual.transform.SetPositionAndRotation(parkedPosition, parkedRotation);
    }

    private DebrisContact Footprint(Vector3 center, Quaternion rotation)
    {
        float radius = Mathf.Clamp(width, 0.22f, 1.35f);
        var span = rotation * Vector3.up * Mathf.Max(0, length * 0.47f - radius * 0.55f);
        return DebrisContact.Around(center, span, radius);
    }

    public void Tick(float dt)
    {
        if (!rigid || rigid.IsSleeping() || dt <= 0)
            return;
        var position = rigid.position;
        if (trackLaunchWalls)
        {
            var correction = DebrisContact.LaunchWallCorrection(previousLaunchPosition, position, floorHeight, 0.72f, out var normal);
            if (correction != Vector3.zero)
            {
                position += correction;
                rigid.position = position;
                DebrisContact.BounceOffLaunchWall(rigid, normal);
            }
        }
        if (floor && Battlefield.OnPlane(position - proxyCenter).sqrMagnitude > 7.5f * 7.5f)
        {
            proxyCenter = position;
            floorHeight = Battlefield.FloorBelow(position, floorHeight);
            floor.transform.position = Battlefield.AtHeight(position, floorHeight - 0.2f);
        }
        float depth = shape.bounds.max.z + floorHeight;
        if (depth > 0.28f)
        {
            rigid.position += Battlefield.Up * (depth + 0.025f);
            float down = Vector3.Dot(rigid.velocity, Battlefield.Down);
            if (down > 0) rigid.velocity -= Battlefield.Down * down;
            depth = -0.025f;
        }
        UpdateVisual(depth);
        bool contactsFloor = depth > -0.12f;
        contactTime = contactsFloor ? contactTime + dt : 0;
        float restSpeed = contactTime > 1.35f ? 0.56f : 0.35f;
        float restSpin = contactTime > 1.35f ? 0.70f : 0.40f;
        bool resting = contactsFloor && rigid.velocity.sqrMagnitude < restSpeed * restSpeed
            && rigid.angularVelocity.sqrMagnitude < restSpin * restSpin;
        quietTime = resting ? quietTime + dt : 0;
        if (quietTime >= 0.25f)
        {
            rigid.Sleep();
            quietTime = 0;
            trackLaunchWalls = false;
        }
        previousLaunchPosition = rigid.position;
    }

    public void Freeze()
    {
        if (!Visual || Visual == Body || parked)
            return;
        Vector3 center = Visual.transform.position;
        if (rigid)
        {
            var bounds = shape.bounds;
            UpdateVisual(bounds.max.z + floorHeight);
            center = bounds.center;
        }
        parkedFloor = rigid ? Battlefield.FloorBelow(center, floorHeight) : floorHeight;
        var snap = Battlefield.Up * (visualDepth + parkedFloor - floorHeight + 0.008f);
        Visual.transform.position += snap;
        parkedContact = Footprint(center + snap, Visual.transform.rotation);
        parked = true;
        if (rigid is not null) physics.Remove(rigid);
        else if (Body) Object.Destroy(Body);
        physics.RemoveFloor(floor);
        rigid = null;
        shape = null;
        if (collisionMesh) Object.Destroy(collisionMesh);
        collisionMesh = null;
    }

    private void UpdateVisual(float depth)
    {
        if (Visual == Body)
            return;
        Visual.transform.SetPositionAndRotation(rigid.position, rigid.rotation);
        visualDepth = depth;
    }

    private Collider ContactShape()
    {
        if (vertices.Count >= 4)
        {
            collisionMesh = new Mesh { name = "REDLINE Limb Collision" };
            collisionMesh.vertices = vertices.ToArray();
            collisionMesh.triangles = triangles.ToArray();
            var convex = Body.AddComponent<MeshCollider>();
            convex.convex = true;
            convex.sharedMesh = collisionMesh;
            if (convex.bounds.size.sqrMagnitude > 0.0001f)
                return convex;
            convex.enabled = false;
            convex.sharedMesh = null;
            Object.Destroy(convex);
            Object.Destroy(collisionMesh);
            collisionMesh = null;
        }

        var bounds = new Bounds(Vector3.up * length * 0.5f, new Vector3(width * 2, length, width * 2));
        if (vertices.Count != 0)
        {
            bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var vertex in vertices)
                bounds.Encapsulate(vertex);
        }
        var box = Body.AddComponent<BoxCollider>();
        box.center = bounds.center;
        box.size = Vector3.Max(bounds.size, Vector3.one * 0.06f);
        return box;
    }

    public void Dispose()
    {
        if (rigid is not null) physics.Remove(rigid);
        else if (Body) Object.Destroy(Body);
        if (Visual && Visual != Body)
        {
            Visual.SetActive(false);
            Object.Destroy(Visual);
        }
        physics.RemoveFloor(floor);
        if (collisionMesh) Object.Destroy(collisionMesh);
        foreach (var mesh in Meshes)
            if (mesh) Object.Destroy(mesh);
        Meshes.Clear();
    }
}

internal sealed class BodyRagdoll
{
    private const float QuietTime = 0.28f;
    private const float RelaxedRestTime = 1.8f;
    private const bool CorpseTwitchesEnabled = false;
    private const float TwitchDuration = 0.22f;
    private const float TwitchGap = 0.26f;
    private static int running;
    private readonly EffectsPhysics physics;
    private readonly BoxCollider ground;
    private float floorHeight;
    private Vector3 groundCenter;
    private Vector3 previousLaunchPosition;
    private readonly Rig.Snapshot startPose;
    private readonly Action<string> log;
    private readonly (Transform Bone, float Degrees) firstTwitch;
    private readonly (Transform Bone, float Degrees) secondTwitch;
    private readonly float twitchStart;
    private bool reportedFloorCorrection;
    private readonly List<Link> links = new(16);
    private RigidPose[] settled;
    private RigidPose[] restSample;
    private float quietTime;
    private float stillTime;
    private float groundedTime;
    private int relaxationPasses;
    private bool torsoOnFloor;
    private bool simulating = true;

    public static BodyRagdoll Start(Rig rig, EffectsPhysics physics, Vector3 hit, Vector3? point, Vector3 facing,
        float ground, int missing, float strength, bool rifle, bool stomp, Action<string> log, bool collapse = false)
        => running >= 8 || !rig.CanRagdoll ? null
            : new BodyRagdoll(rig, physics, hit, point, facing, ground, missing, strength, rifle, stomp, log, collapse);

    private BodyRagdoll(Rig rig, EffectsPhysics physics, Vector3 hit, Vector3? point, Vector3 facing,
        float floor, int missing, float strength, bool rifle, bool stomp, Action<string> log, bool collapse)
    {
        this.physics = physics;
        this.log = log;
        floorHeight = floor;
        var tuning = physics.LaunchTuning;
        if (stomp || collapse)
            tuning = tuning with { Extreme = false };
        startPose = rig.Capture();
        groundCenter = startPose.HipsPosition;
        ground = physics.Floor(groundCenter, floor);
        Vector3 forward = Battlefield.OnPlane(facing).normalized;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.up;
        var twitchLimbs = CorpseTwitchesEnabled && (missing & (1 << (int)Part.Head)) == 0
            ? new List<(Transform Bone, float Degrees)>(4) : null;
        running++;
        try
        {
            var hips = Add(rig.Hips, rig.Chest, null, 5.3f, 0.78f, JointKind.Torso);
            var chest = Add(rig.Chest, rig.Neck ? rig.Neck : rig.Head, hips, 4.8f, 0.82f, JointKind.Torso);
            Vector3 neck = rig.Neck ? rig.Neck.position : rig.Chest.position;
            Add(rig.Head, null, chest, 1.7f, 0.59f, JointKind.End, Part.Head,
                (rig.Head.position - neck).normalized * 0.88f);

            var leftArm = Add(rig.LeftArm, rig.LeftForearm, chest, 1.3f, 0.36f, JointKind.Shoulder, Part.LeftArm);
            var rightArm = Add(rig.RightArm, rig.RightForearm, chest, 1.3f, 0.36f, JointKind.Shoulder, Part.RightArm);
            if (leftArm != null) AddForearm(leftArm, rig.LeftForearm, rig.LeftHand, rig.LeftFinger);
            if (rightArm != null) AddForearm(rightArm, rig.RightForearm, rig.RightHand, rig.RightFinger);

            var leftLeg = Add(rig.LeftThigh, rig.LeftShin, hips, 2.3f, 0.50f, JointKind.Hip);
            var rightLeg = Add(rig.RightThigh, rig.RightShin, hips, 2.3f, 0.50f, JointKind.Hip);
            if (leftLeg != null) AddShin(leftLeg, rig.LeftShin, rig.LeftFoot, rig.LeftToe, Part.LeftLeg);
            if (rightLeg != null) AddShin(rightLeg, rig.RightShin, rig.RightFoot, rig.RightToe, Part.RightLeg);

            if (twitchLimbs is { Count: > 0 })
            {
                uint variant = unchecked((uint)(rig.Hips.GetInstanceID() + Time.frameCount) * 2654435761u);
                int primary = (int)((variant >> 16) % (uint)twitchLimbs.Count);
                int secondary = (primary + 1) % twitchLimbs.Count;
                float side = (variant & 1) == 0 ? 1 : -1;
                firstTwitch = (twitchLimbs[primary].Bone, twitchLimbs[primary].Degrees * side);
                secondTwitch = (twitchLimbs[secondary].Bone, twitchLimbs[secondary].Degrees * -side * 0.55f);
                twitchStart = 0.10f + ((variant >> 4) & 3) * 0.015f;
            }

            // Resolve sole overlap as a translation so contacts cannot kick the new rig back upward.
            float overlap = 0;
            foreach (var link in links)
                overlap = Mathf.Max(overlap, link.Volume.bounds.max.z + floor);
            if (overlap > 0)
                foreach (var link in links)
                    link.Body.position += Battlefield.Up * (overlap + 0.02f);

            float mass = 0;
            Vector3 center = Vector3.zero;
            foreach (var link in links)
            {
                mass += link.Body.mass;
                center += link.Body.worldCenterOfMass * link.Body.mass;
            }
            center /= mass;
            float height = Mathf.Max(0, Battlefield.Height(hips.Body.worldCenterOfMass) - floor);
            float upright = Mathf.Clamp01((height - 1.4f) / 2.8f);
            Vector3 direction = hit.sqrMagnitude > 0.01f ? hit.normalized : -forward;
            float energy = Mathf.Clamp01((strength - 0.54f) / 0.86f);
            float push = tuning.Push(collapse ? 0.60f : stomp ? 1.05f : InjuryRules.LaunchPush(strength, upright, rifle));
            float lift = collapse ? -0.45f : stomp ? -0.84f : Mathf.Lerp(0.07f, 0.21f + energy * 0.24f, upright);
            var lever = Vector3.ClampMagnitude((point ?? rig.Chest.position) - center, 2.1f);
            var tumble = Vector3.Cross(Battlefield.Up, direction);
            var spin = Vector3.Cross(lever, direction) * 0.20f + tumble * (stomp || collapse ? 0 : upright * (0.60f + energy * 0.28f));
            spin = Vector3.ClampMagnitude(spin, collapse ? 0.20f : stomp ? 0.32f : Mathf.Lerp(0.45f, 1.25f, upright));
            if (tuning.Extreme)
            {
                lift = lift * 2.3f + upright * 0.45f;
                spin = spin * 1.42f + tumble * (0.40f * upright);
            }
            lift = tuning.VerticalSpeed(lift);
            spin = tuning.AngularSpeed(spin);
            Vector3 shove = direction * push + Battlefield.Up * lift;
            foreach (var link in links)
            {
                link.Body.velocity = shove + Vector3.ClampMagnitude(
                    Vector3.Cross(spin, link.Body.worldCenterOfMass - center), tuning.Extreme ? 3.8f : 2.1f);
                link.Body.angularVelocity = spin;
            }
            previousLaunchPosition = hips.Body.position;
            log($"ragdoll launch push={push:F2} lift={lift:F2} spin={spin.magnitude:F2} strength={strength:F2} upright={upright:F2} direction={Battlefield.Format(direction)} stomp={stomp} collapse={collapse} extreme={tuning.Extreme}");
        }
        catch
        {
            Dispose();
            throw;
        }

        void AddForearm(Link arm, Transform elbow, Transform hand, Transform finger)
        {
            var forearm = Add(elbow, hand, arm, 0.90f, 0.32f, JointKind.Elbow);
            if (forearm != null)
            {
                Add(hand, finger, forearm, 0.40f, 0.24f, JointKind.End, extension: forearm.Axis * 0.52f);
                twitchLimbs?.Add((elbow, 5.5f));
            }
        }

        void AddShin(Link thigh, Transform knee, Transform foot, Transform toe, Part part)
        {
            var shin = Add(knee, foot, thigh, 1.50f, 0.39f, JointKind.Knee, part);
            if (shin != null)
            {
                Add(foot, toe, shin, 0.75f, 0.35f, JointKind.End, extension: forward * 0.92f);
                twitchLimbs?.Add((knee, 3.5f));
            }
        }

        Link Add(Transform bone, Transform tip, Link parent, float mass, float radius,
            JointKind kind, Part part = Part.Body, Vector3 extension = default)
        {
            if (!bone || (missing & (1 << (int)part)) != 0)
                return null;
            Vector3 axis = tip ? tip.position - bone.position : extension;
            if (axis.sqrMagnitude < 0.001f)
                axis = parent == null ? Battlefield.Up * radius : parent.Axis * radius;
            var go = physics.Create("REDLINE Ragdoll Proxy", bone.position);
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
            var volume = go.AddComponent<CapsuleCollider>();
            volume.center = Vector3.up * axis.magnitude * 0.5f;
            volume.radius = radius;
            volume.height = Mathf.Max(radius * 2, axis.magnitude + radius * (kind == JointKind.End ? 1.45f : 0.85f));
            var rigid = physics.Register(volume, ground, mass, 0.27f, 0.52f);
            var link = new Link(bone, rigid, volume, Quaternion.Inverse(go.transform.rotation) * bone.rotation, axis.normalized,
                axis.magnitude * 0.5f, Mathf.Max(0, volume.height * 0.5f - radius), radius,
                kind is JointKind.Shoulder or JointKind.Elbow or JointKind.Hip or JointKind.Knee);
            foreach (var other in links)
            {
                if (other == parent || Physics.ComputePenetration(volume, go.transform.position, go.transform.rotation,
                    other.Volume, other.Body.position, other.Body.rotation, out _, out _))
                    Physics.IgnoreCollision(volume, other.Volume);
            }
            links.Add(link);
            if (parent != null)
                Connect(link, parent, kind, forward);
            return link;
        }
    }

    private static void Connect(Link child, Link parent, JointKind kind, Vector3 forward)
    {
        var go = child.Body.gameObject;
        if (kind is JointKind.Elbow or JointKind.Knee)
        {
            bool knee = kind == JointKind.Knee;
            Vector3 axis = Vector3.Cross(parent.Axis, knee ? -forward : child.Axis).normalized;
            if (axis.sqrMagnitude < 0.01f)
                axis = Vector3.Cross(parent.Axis, knee ? child.Axis : forward).normalized;
            if (axis.sqrMagnitude < 0.01f)
                axis = go.transform.right;
            float angle = Vector3.SignedAngle(parent.Axis, child.Axis, axis);
            var hinge = go.AddComponent<HingeJoint>();
            hinge.connectedBody = parent.Body;
            hinge.axis = go.transform.InverseTransformDirection(axis);
            hinge.limits = new JointLimits
            {
                min = Mathf.Max(-175, Mathf.Min(-3, -8 - angle)),
                max = Mathf.Min(175, Mathf.Max(3, (knee ? 128 : 143) - angle))
            };
            hinge.useLimits = true;
            return;
        }

        var joint = go.AddComponent<CharacterJoint>();
        joint.connectedBody = parent.Body;
        joint.axis = Vector3.up;
        joint.swingAxis = Vector3.right;
        joint.enableProjection = true;
        joint.projectionDistance = 0.16f;
        joint.projectionAngle = 12;
        var (twist, swing1, swing2) = kind switch
        {
            JointKind.Shoulder => (38, 82, 67),
            JointKind.Hip => (27, 66, 49),
            JointKind.End => (22, 37, 29),
            _ => (16, 27, 20)
        };
        joint.lowTwistLimit = new SoftJointLimit { limit = -twist };
        joint.highTwistLimit = new SoftJointLimit { limit = twist };
        joint.swing1Limit = new SoftJointLimit { limit = swing1 };
        joint.swing2Limit = new SoftJointLimit { limit = swing2 };
    }

    public bool Landed { get; private set; }
    public bool IsSimulating => simulating;
    public Rig.Snapshot Snapshot => startPose;
    private Vector3 HipsPosition => simulating ? links[0].Body.position : settled[0].Position;
    public Vector3 PlanarTravel => Battlefield.OnPlane(HipsPosition - startPose.HipsPosition);
    public string Diagnostics => $"links={links.Count} floor={floorHeight:F3} hips={Battlefield.Format(HipsPosition)} landed={Landed} quiet={quietTime:F3}";

    public void Tick()
    {
        float dt = physics.SimulatedTimeThisFrame;
        if (!simulating || dt <= 0)
            return;
        TrackLaunchTravel();
        float penetration = 0;
        foreach (var link in links)
            penetration = Mathf.Max(penetration, link.Volume.bounds.max.z + floorHeight);
        if (penetration > 0.35f)
        {
            foreach (var link in links)
            {
                link.Body.position += Battlefield.Up * (penetration + 0.025f);
                float down = Vector3.Dot(link.Body.velocity, Battlefield.Down);
                if (down > 0) link.Body.velocity -= Battlefield.Down * down;
            }
            if (!reportedFloorCorrection)
            {
                log($"ragdoll floor recovery depth={penetration:F3} {Diagnostics}");
                reportedFloorCorrection = true;
            }
        }

        // Feet can be touching from frame one. The pool follows torso contact.
        float torsoDepth = Mathf.Max(links[0].Volume.bounds.max.z, links[1].Volume.bounds.max.z) + floorHeight;
        torsoOnFloor = torsoDepth > -0.18f;
        if (!Landed && torsoOnFloor)
        {
            Landed = true;
            log($"ragdoll torso contacted floor {Diagnostics}");
            if (firstTwitch.Bone)
                log($"ragdoll twitch bones={firstTwitch.Bone.name},{secondTwitch.Bone.name} start={twitchStart:F2}s");
        }
        bool quiet = true;
        bool restingOnFloor = torsoOnFloor;
        foreach (var link in links)
        {
            restingOnFloor |= TouchesFloor(link.Volume);
            if (link.Body.velocity.sqrMagnitude > 0.42f * 0.42f || link.Body.angularVelocity.sqrMagnitude > 0.5f * 0.5f)
                quiet = false;
        }
        groundedTime = restingOnFloor ? groundedTime + dt : 0f;
        quietTime = quiet && restingOnFloor ? quietTime + dt : 0;
        bool relaxed = groundedTime >= RelaxedRestTime;
        SampleRest(dt, restingOnFloor, relaxed);
        bool twitchPending = firstTwitch.Bone && torsoOnFloor && groundedTime < twitchStart + TwitchGap + TwitchDuration;
        bool bodyAtRest = quietTime >= (torsoDepth > -0.60f ? QuietTime : 0.85f)
            || stillTime >= (torsoOnFloor ? 0.36f : 0.70f);
        if (!bodyAtRest || twitchPending || RelaxRaisedLimbs())
            return;

        Landed = true;
        Settle();
    }

    private bool RelaxRaisedLimbs()
    {
        if (relaxationPasses >= 3)
            return false;
        bool moved = false;
        foreach (var link in links)
        {
            if (!link.CanRelax || TouchesFloor(link.Volume))
                continue;
            var axis = link.Body.rotation * Vector3.up;
            var tip = link.Body.position + axis * (link.Center + link.Span);
            if (Battlefield.Height(tip - link.Body.position) < 0.42f
                || Battlefield.Height(tip) - floorHeight < link.Radius + 0.65f
                || physics.HasSupportBelow(tip, link.Radius + 0.32f))
                continue;

            // A joint can balance against its limit with its tip pointed up. Lean it away
            // from the torso so gravity can fold it down without moving the whole corpse.
            var outward = Battlefield.OnPlane(tip - HipsPosition).normalized;
            if (outward == Vector3.zero)
                outward = Battlefield.OnPlane(link.Body.rotation * Vector3.right).normalized;
            link.Body.AddForceAtPosition(Battlefield.Down * 0.95f + outward * (0.66f + relaxationPasses * 0.24f),
                tip, ForceMode.VelocityChange);
            moved = true;
        }
        if (moved)
        {
            relaxationPasses++;
            quietTime = stillTime = 0f;
            log($"ragdoll relaxed raised limbs pass={relaxationPasses}");
        }
        return moved;
    }

    private void TrackLaunchTravel()
    {
        var position = HipsPosition;
        var correction = DebrisContact.LaunchWallCorrection(previousLaunchPosition, position, floorHeight, 0.78f, out var normal);
        if (correction != Vector3.zero)
        {
            foreach (var link in links)
            {
                link.Body.position += correction;
                DebrisContact.BounceOffLaunchWall(link.Body, normal);
            }
            position += correction;
        }
        previousLaunchPosition = position;
        // The original floor proxy only extends 16 units from the hit.
        if (Battlefield.OnPlane(position - groundCenter).sqrMagnitude > 7.5f * 7.5f)
        {
            groundCenter = position;
            floorHeight = Battlefield.FloorBelow(position, floorHeight);
            ground.transform.position = Battlefield.AtHeight(position, floorHeight - 0.2f);
        }
    }

    private void SampleRest(float dt, bool restingOnFloor, bool relaxed)
    {
        // Contacts can report velocity after the visible pose is already still.
        if (!restingOnFloor)
        {
            stillTime = 0;
            restSample = null;
            return;
        }
        float positionTolerance = relaxed ? 0.22f : 0.13f;
        float rotationDot = relaxed ? 0.996f : 0.998f;
        bool still = restSample != null;
        for (int i = 0; still && i < links.Count; i++)
        {
            var pose = ReadPose(links[i]);
            still = (pose.Position - restSample[i].Position).sqrMagnitude <= positionTolerance * positionTolerance
                && Mathf.Abs(Quaternion.Dot(pose.Rotation, restSample[i].Rotation)) >= rotationDot;
        }
        if (still)
        {
            stillTime += dt;
            return;
        }
        stillTime = 0;
        restSample ??= new RigidPose[links.Count];
        for (int i = 0; i < links.Count; i++)
            restSample[i] = ReadPose(links[i]);
    }

    private bool TouchesFloor(Collider shape) => shape.bounds.max.z + floorHeight > -0.18f;

    public void Pose()
    {
        for (int i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (!link.Bone)
                continue;
            var pose = simulating ? ReadPose(link) : settled[i];
            link.Bone.SetPositionAndRotation(pose.Position, pose.Rotation);
        }
        if (simulating && torsoOnFloor && firstTwitch.Bone)
        {
            // Rotate the visible limb in the floor plane without waking the physics rig or lifting its support points.
            PoseTwitch(firstTwitch, groundedTime - twitchStart);
            PoseTwitch(secondTwitch, groundedTime - twitchStart - TwitchGap);
        }
    }

    public void RemoveLimb(Transform root)
    {
        int kept = links.Count;
        for (int i = links.Count - 1; i >= 2; i--)
        {
            var link = links[i];
            if (!link.Bone || (link.Bone != root && !link.Bone.IsChildOf(root)))
                continue;
            if (simulating)
                physics.Remove(link.Body);
            else
                Array.Copy(settled, i + 1, settled, i, kept - i - 1);
            links.RemoveAt(i);
            kept--;
        }
        restSample = null;
    }

    private static void PoseTwitch((Transform Bone, float Degrees) limb, float time)
    {
        if (time <= 0 || time >= TwitchDuration || !limb.Bone)
            return;
        float curve = Mathf.Sin((float)Math.PI * time / TwitchDuration);
        limb.Bone.rotation = Quaternion.AngleAxis(limb.Degrees * curve * curve, Battlefield.Up) * limb.Bone.rotation;
    }

    private void Settle()
    {
        settled = new RigidPose[links.Count];
        for (int i = 0; i < settled.Length; i++)
            settled[i] = ReadPose(links[i]);
        log($"ragdoll physics settled {Diagnostics}");
        Dispose();
    }

    private static RigidPose ReadPose(Link link) => new(link.Body.position, link.Body.rotation * link.Offset);

    public void Dispose()
    {
        if (!simulating)
            return;
        simulating = false;
        running--;
        foreach (var link in links)
            physics.Remove(link.Body);
        physics.RemoveFloor(ground);
    }

    private enum JointKind { Torso, Shoulder, Elbow, Hip, Knee, End }
    private sealed record Link(Transform Bone, Rigidbody Body, Collider Volume, Quaternion Offset, Vector3 Axis,
        float Center, float Span, float Radius, bool CanRelax);
    private readonly record struct RigidPose(Vector3 Position, Quaternion Rotation);
}
