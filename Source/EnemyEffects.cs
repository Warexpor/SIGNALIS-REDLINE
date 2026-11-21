using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

namespace Redline;

internal sealed partial class EnemyEffects
{
    private static readonly System.Random cutRandom = new();

    private readonly Rig rig;
    private readonly bool standardReplika;
    private readonly EffectsPhysics physics;
    private readonly SkinMap map;
    private readonly Action<string> log;
    private readonly List<SkinCut> skins = new();
    private readonly List<(Renderer Renderer, Part Part)> hidden = new();
    private readonly List<(SkinCut Skin, bool Owned)> corpseSkins = new();
    private readonly List<Renderer> hiddenVariants = new();
    private readonly List<(MeshRenderer Source, Transform Copy)> corpseAccessories = new();
    private GameObject corpseRoot;
    private Vector3 corpseCenter;
    private readonly List<Wound> wounds = new(5);
    private readonly float[] partDamage = new float[6];
    private float unlocatedDamage;
    private int lastUnlocatedRoll;
    private float nextPhysicsLog;
    private bool corpseBakedAtRest;
    private float corpseLift;
    private float corpseSupportFloor;
    private Vector3 corpseFacing;
    private BoxCollider fittedDownedBox;
    private Vector3 originalBoxCenter;
    private Vector3 originalBoxSize;
    private Quaternion originalBoxRotation;
    private bool pendingBoxFit;
    private float nextBoxFit;
    private readonly List<(Transform Transform, Vector3 RestPosition, bool Interaction)> downedAnchors = new(4);
    private readonly Animator animator;
    private readonly AnimatorCullingMode originalCulling;
    private bool forcedAnimation;
    private bool animationHeld;
    private float previousAnimatorSpeed;
    private bool previousAnimationEvents;
    private int missing;
    private int impacts;
    private bool downed;
    private bool skinScanned;
    private bool wasCrawling;
    private bool enteredCrawl;
    private bool crawlKilled;
    private Rig.Snapshot crawlDeathPose;
    private Vector3 crawlDeathRoot;
    private Transform crawlAim;
    private bool crawlAimSearched;
    private Vector3 crawlAimRest;
    private bool ownsCrawlPrompt;
    private Vector3 crawlPromptRest;
    private Vector3 crawlCenter;
    private int weaponArms = -1;
    private Rig.Snapshot visiblePose;
    private Part swipeArm;
    private float swipeStarted;
    private float nextSwipe;
    private bool swipeLanded;
    private Vector3 swipeFacing;
    private Vector3 previousHand;
    private bool hasHandSample;
    private float phase;
    private float blend;
    private float floor;
    private Vector3 floorPosition;
    private Vector3 previous;
    private float poolScale;
    private float poolReadyAt;
    private Vector3? stompPoolPosition;
    private BodyRagdoll ragdoll;
    private WaistSplit waistSplit;
    private MajorCut fatalCut;
    private float nextSquibReplacement;
    private int lastStompFrame = -1;
    private readonly ElsterBlood elsterBlood;
    private readonly RedlineSettings settings;

    public EnemyEffects(EnemyController target, EffectsPhysics physics, SkinMap map, ElsterBlood elsterBlood,
        RedlineSettings settings, Action<string> log)
    {
        Target = target;
        standardReplika = IsStandardReplika(target);
        this.physics = physics;
        this.map = map;
        this.elsterBlood = elsterBlood;
        this.settings = settings;
        int id = target.GetInstanceID();
        string name = target.name;
        this.log = message => log($"enemy={id} name={name} {message}");
        animator = target.animator;
        rig = new Rig(animator);
        if (animator) originalCulling = animator.cullingMode;
        previous = target.transform.position;
        floor = FindFloor();
        this.log($"tracked type={target.Preset.Type} rig={rig.CanRagdoll} crawlRig={rig.CanCrawl}");
    }

    public EnemyController Target { get; }
    public bool CanRagdoll => rig.CanRagdoll;
    public Vector3 ShotCenter => waistSplit?.StompPoint ?? (ragdoll != null ? corpseCenter :
        HasGroundAimPose ? crawlCenter : Target.transform.position + Battlefield.Up * 4f);
    private bool NativeDowned => Target.staggerType is EnemyController.hurtState.critical or EnemyController.hurtState.fire;
    private bool CanCrawlWithInjury => standardReplika && (missing & InjuryRules.Legs) != 0 && rig.CanCrawl;
    public bool IsCrawling => CanCrawlWithInjury && !downed && !NativeDowned && !holdingKnockdownCrawl;
    public bool HasCrawlPose => wasCrawling && IsCrawling;
    public bool HasGroundAimPose => HasCrawlPose || holdingKnockdownCrawl || revivalStage != RevivalStage.None;
    public bool EnteredCrawl => enteredCrawl;
    // Kolibri's shorter torso needs a higher cut to clear the leg seams.
    private float WaistFraction => standardReplika ? 0.34f : 0.81f;
    public static bool IsStandardReplika(EnemyController target) => target.Preset &&
        target.Preset.Type is AnEnemy.AnEnemyType.EULR or AnEnemy.AnEnemyType.STAR
            or AnEnemy.AnEnemyType.ARAR or AnEnemy.AnEnemyType.STCR;

    public static bool IsSupported(EnemyController target)
    {
        if (IsStandardReplika(target))
            return true;
        if (!target.Preset || target.Preset.Type != AnEnemy.AnEnemyType.KLBR)
            return false;
        var encounter = target.GetComponentInParent<KolibriManager>();
        return encounter && encounter.Real == target;
    }
    public Rig.Snapshot CapturePose() => visiblePose == null ? rig.Capture()
        : visiblePose with { Bones = (Rig.Pose[])visiblePose.Bones.Clone() };

    public void AddShotSurfaces(ShotHitboxes.Geometry body)
    {
        if (waistSplit != null)
        {
            waistSplit.AddShotSurfaces(body);
            return;
        }
        if (ragdoll != null)
        {
            foreach (var (skin, _) in corpseSkins)
                skin.AddShotSurface(body);
            return;
        }
        ShotHitboxes.AddNativeSurfaces(Target, body, source =>
            skins.Find(skin => skin.Source == source)?.AddShotSurface(body) == true);
    }
    private bool Missing(Part part) => (missing & (1 << (int)part)) != 0;

    private int WeaponArms
    {
        get
        {
            if (weaponArms >= 0)
                return weaponArms;
            weaponArms = 0;
            Detect(Target.WeaponHurtbox);
            Detect(Target.WeaponHurtboxAlt);
            return weaponArms;

            void Detect(Hurtbox weapon)
            {
                if (!weapon)
                    return;
                if (rig.LeftArm && weapon.transform.IsChildOf(rig.LeftArm))
                    weaponArms |= 1 << (int)Part.LeftArm;
                if (rig.RightArm && weapon.transform.IsChildOf(rig.RightArm))
                    weaponArms |= 1 << (int)Part.RightArm;
            }
        }
    }

    public bool UsesInjuredAttack(bool mayCrawl) =>
        standardReplika && (missing & (InjuryRules.Arms | InjuryRules.Legs)) != 0 && rig.CanCrawl &&
        InjuryRules.UsesSwipe(missing, WeaponArms, mayCrawl && (missing & InjuryRules.Legs) != 0);

    public float SwipeRange(bool mayCrawl) => IsCrawling && mayCrawl ? 4.2f : 5.0f;

    public float MovementScale(bool mayCrawl)
    {
        float scale = mayCrawl && IsCrawling ? InjuryRules.CrawlSpeed(missing) : 1f;
        return swipeArm == Part.Body ? scale : scale * 0.18f;
    }

    public void TryStartSwipe(bool mayCrawl)
    {
        if (swipeArm != Part.Body || Time.time < nextSwipe || !CanSwipe())
            return;
        Part arm = InjuryRules.SwipeArm(missing, WeaponArms);
        if (arm == Part.Body || !TrySwipeTarget(SwipeRange(mayCrawl), 0.72f, out _, out var facing))
            return;
        swipeArm = arm;
        swipeFacing = facing;
        swipeStarted = Time.time;
        nextSwipe = Time.time + Mathf.Max(1.45f, Target.attackCooldown);
        Target.attackCool = Mathf.Max(Target.attackCool, nextSwipe - Time.time);
        swipeLanded = hasHandSample = false;
        log($"injured attack started arm={arm} crawling={IsCrawling && mayCrawl} weaponMask=0x{WeaponArms:X}");
    }

    private bool CanSwipe() => !downed && knockdownPose == null && !HoldRevivalAi && Target.hitbox && Target.hitbox.HP > 0
        && Target.state is EnemyController.enemystate.pursuit or EnemyController.enemystate.attack
        && Target.staggerType == EnemyController.hurtState.none
        && !PlayerState.paused && !PlayerState.menu && !PlayerState.eventScreen
        && !PlayerState.cutscene && !PlayerState.gameOver && !PlayerState.cloaked;

    private bool TrySwipeTarget(float range, float facingDot, out Vector3 playerPoint, out Vector3 facing)
    {
        playerPoint = facing = Vector3.zero;
        var player = PlayerState.player;
        if (!player)
            return false;
        Vector3 origin = Target.transform.position;
        playerPoint = player.transform.position;
        var delta = Battlefield.OnPlane(playerPoint - origin);
        facing = Battlefield.OnPlane(Target.transform.forward).normalized;
        return delta.sqrMagnitude <= range * range && Mathf.Abs(playerPoint.z - origin.z) <= 2.3f
            && Vector3.Dot(delta, facing) >= facingDot * delta.magnitude && Target.LOS();
    }

    private void TickSwipe(bool crawling)
    {
        if (swipeArm == Part.Body)
            return;
        const float windup = 0.31f;
        const float strike = 0.18f;
        const float recover = 0.29f;
        float time = Time.time - swipeStarted;
        if (!CanSwipe() || Missing(swipeArm) || time >= windup + strike + recover)
        {
            swipeArm = Part.Body;
            return;
        }

        float ready = Mathf.SmoothStep(0, 1, time / windup);
        float sweep = Mathf.SmoothStep(0, 1, (time - windup) / strike);
        float weight = ready * (1f - Mathf.SmoothStep(0, 1, (time - windup - strike) / recover));
        Vector3 hand = rig.Swipe(swipeArm, swipeFacing, sweep, weight, crawling, floor);
        if (!swipeLanded && time >= windup && time - Time.deltaTime <= windup + strike
            && TrySwipeTarget(SwipeRange(crawling) + 0.25f, 0.58f, out var playerPoint, out _))
        {
            // A swept hand test covers low frame rates. Only the forward stroke can land.
            Vector3 start = Battlefield.OnPlane((hasHandSample ? previousHand : hand) - playerPoint);
            Vector3 travel = Battlefield.OnPlane(hand - playerPoint) - start;
            float nearest = Mathf.Clamp01(-Vector3.Dot(start, travel) / Mathf.Max(0.001f, travel.sqrMagnitude));
            Vector3 contact = Vector3.Lerp(hasHandSample ? previousHand : hand, hand, nearest);
            float height = Battlefield.Height(contact - playerPoint);
            if ((start + travel * nearest).sqrMagnitude <= 1.45f * 1.45f && height >= -0.15f && height <= 6.8f
                && !Physics.Linecast(contact, new Vector3(playerPoint.x, playerPoint.y, contact.z),
                    WallMask, QueryTriggerInteraction.Ignore))
            {
                swipeLanded = true;
                var direction = Battlefield.OnPlane(playerPoint - Target.transform.position).normalized;
                int damage = Mathf.Max(1, Mathf.RoundToInt(Target.Preset.damage * (crawling ? 0.40f : 0.52f)));
                PlayerState.HurtElster(damage, new Vector2(direction.x, direction.y));
                log($"injured attack contact arm={swipeArm} damage={damage} crawling={crawling}");
            }
        }
        previousHand = hand;
        hasHandSample = true;
    }

    private void HoldNativeAnimation()
    {
        if (!animator)
            return;
        // The enemy controller keeps its timers and interactions while the corpse is posed by physics.
        if (!animationHeld)
        {
            previousAnimatorSpeed = animator.speed;
            previousAnimationEvents = animator.fireEvents;
            animationHeld = true;
            log($"native downed animation paused speed={previousAnimatorSpeed:F2}");
        }
        if (animator.speed != 0)
            animator.speed = 0;
        if (animator.fireEvents)
            animator.fireEvents = false;
    }

    public void ReleaseUnusedAnimation()
    {
        if (!animationHeld || ragdoll != null || waistSplit != null || crawlDeathPose != null
            || revivalStage == RevivalStage.Reassemble)
            return;
        animationHeld = false;
        if (animator)
        {
            if (Target && Target.state != EnemyController.enemystate.dead)
                animator.ResetTrigger(Target.anim_Fall);
            animator.speed = previousAnimatorSpeed;
            animator.fireEvents = previousAnimationEvents;
        }
        log("native animator restored");
    }

    public void EmitReplacementSquib(BloodEffects blood, float amount)
    {
        if (Time.time < nextSquibReplacement)
            return;
        nextSquibReplacement = Time.time + 0.12f;
        var point = waistSplit?.StompPoint ?? (ragdoll != null ? corpseCenter :
            rig.Chest ? rig.Chest.position : Target.transform.position + Battlefield.Up * 2f);
        var surface = Battlefield.FloorBelow(point, floor);
        var player = PlayerAttack.instance;
        var direction = player ? Battlefield.OnPlane(point - player.transform.position).normalized : Vector3.zero;
        blood.Spurt(point, direction, surface, WallMask, amount, downed ? 40 : 24, downed ? 1.10f : 0.86f);
    }

    public void Impact(int shotId, Vector3 direction, Vector3 shotDirection, Vector3? point, ImpactCuts.Ray[] rays, float damage, bool knockdown,
        bool critical, bool finalDeath, bool heavy, bool spread, bool rifle, bool preview, Rig.Snapshot pose, bool mayCut, bool mayRagdoll, bool mayCrawl,
        float waistChance, BloodEffects blood, float amount, CutSounds sounds, bool proneHit = false, bool pairedLegs = false)
    {
        if (waistSplit != null)
        {
            if (proneHit && point.HasValue)
            {
                blood.Spurt(point.Value, shotDirection, floor, WallMask, amount, heavy ? 42 : 27, 0.83f, ballistic: true);
                sounds.PlayImpact(point.Value, heavy);
            }
            return;
        }
        impacts++;
        bool fall = knockdown || critical;
        if (!downed)
        {
            var travel = Target.transform.position - floorPosition;
            if (Mathf.Abs(travel.z) > 0.35f || travel.sqrMagnitude > 4f)
                floor = FindFloor();
        }
        int walls = WallMask;
        float strength = InjuryRules.Strength(damage, Target.Preset.HP);
        bool humanoid = IsSupported(Target);
        float cutStrength = Mathf.Min(1.4f, strength * RedlineSettings.Factor(settings.CutSensitivity));
        int unavailable = settings.Decapitation.Value ? missing : missing | (1 << (int)Part.Head);
        bool launch = !proneHit && !crawlKilled && mayRagdoll && humanoid && finalDeath && pose != null
            && InjuryRules.CanLaunch(heavy, damage, Target.Preset.HP);
        if (!downed && pose != null && (launch || mayCut && shotId > 0))
            rig.Apply(pose);
        Vector3 center = point ?? (rig.Chest ? rig.Chest.position : Target.transform.position + Battlefield.Up * 4.8f);
        bool ballistic = shotId > 0 && !preview;
        float impactFloor = ragdoll != null ? corpseSupportFloor : floor;
        blood.Spurt(center, ballistic ? shotDirection : direction, impactFloor, walls,
            amount, fall ? 54 : heavy ? 42 : 27, fall || heavy ? 1.10f : 0.83f, ballistic);
        log($"impact n={impacts} hp={Target.hitbox.HP} damage={damage:F1} strength={strength:F2} rays={rays.Length} point={Battlefield.Format(center)} nativeState={Target.state} fall={fall} finalDeath={finalDeath} launch={launch} critical={critical} heavy={heavy} preHitPose={pose != null} preview={preview} dir={Battlefield.Format(direction)} floor={floor:F3} wallMask=0x{walls:X}");

        if (ragdoll != null && (!proneHit || !mayCut))
        {
            if (ballistic)
                sounds.PlayImpact(center, heavy);
            return;
        }

        Vector3 corpseOffset = Vector3.zero;
        if (ragdoll != null)
        {
            rig.Apply(ragdoll.Snapshot);
            ragdoll.Pose();
            corpseOffset = corpseRoot.transform.position;
            var offset = Rig.ToPoint(corpseOffset);
            rays = Array.ConvertAll(rays, ray => ray with { Point = ray.Point - offset });
        }
        var zones = mayCut && humanoid && rays.Length != 0 ? rig.HitZones(missing) : null;
        var ranked = zones != null ? ImpactCuts.Rank(rays, zones) : null;
        bool legContact = pairedLegs && ranked != null && ImpactCuts.HasLegCutContact(ranked, missing);
        bool limbContact = legContact
            || ranked != null && ImpactCuts.HasLimbContact(ranked, heavy, spread, finalDeath, unavailable);
        if (!limbContact && spread && rays.Length > 1 && zones != null)
            limbContact = Array.Exists(rays, ray =>
                ImpactCuts.HasLimbContact(ImpactCuts.Rank(new[] { ray }, zones), heavy, spread, finalDeath, unavailable));
        var major = !proneHit && !legContact && mayCut && humanoid && !downed && ballistic && heavy && finalDeath && cutStrength >= 0.48f
            ? ImpactCuts.RollMajorCut(cutStrength, mayRagdoll ? waistChance : 0f, HitsWaist(rays),
                limbContact && ranked[0].Part == Part.Head, !limbContact,
                (unavailable & (1 << (int)Part.Head)) == 0 && rig.Head, Random.value, Random.value)
            : MajorCut.None;
        bool cutLimb = false;
        if (major == MajorCut.Waist)
        {
            cutLimb = TryCompanion(false, "waist", playSound: false);
            try { waistSplit = WaistSplit.TryStart(Target, rig, map, skins, physics, direction, floor, rifle, WaistFraction, log); }
            catch (Exception error) { log($"waist split fallback: {error}"); }
            if (waistSplit != null)
            {
                ConfirmFatalCut(MajorCut.Waist);
                ReleaseCrawlInteraction();
                downed = true;
                QueuePool(amount * 1.85f);
                HoldNativeAnimation();
                CaptureDownedAnchors();
                blood.Spurt(waistSplit.SpillPoint, ballistic ? shotDirection : direction, floor, walls, amount, 74, 1.25f, ballistic);
                sounds.PlayHeavyCut(waistSplit.SpillPoint);
                return;
            }
            if (cutLimb)
                sounds.Play(center, false);
        }
        unavailable |= missing;
        if (mayCut && humanoid)
        {
            var rolled = major != MajorCut.Head && !preview && !limbContact && shotId > 0 && cutStrength > 0
                ? RollUnlocatedCut(shotId, direction, cutStrength, heavy, finalDeath, unavailable) : default;
            Part[] selected = preview ? new[] { Missing(Part.LeftLeg) ? Part.RightLeg : Part.LeftLeg }
                : major == MajorCut.Head ? new[] { Part.Head }
                : legContact && cutStrength > 0 ? ImpactCuts.ChooseLegs(ranked, partDamage, cutStrength, heavy, missing)
                : limbContact && cutStrength > 0 ? ImpactCuts.Choose(ranked, partDamage, cutStrength, heavy, spread, finalDeath, unavailable)
                : rolled.Part != Part.Body ? new[] { rolled.Part } : Array.Empty<Part>();
            if (ranked != null)
                log($"cut contact shot={shotId} region={ranked[0].Part} gap={ranked[0].Gap:F2} localized={limbContact} selected={string.Join(",", selected)}");
            bool headCut = false;
            bool companionCut = false;
            foreach (var cut in selected)
            {
                if (!ApplyCut(cut))
                    continue;
                cutLimb = true;
                headCut |= cut == Part.Head;
                companionCut |= cut != Part.Head;
                if (rolled.Part == cut)
                    unlocatedDamage = Mathf.Max(0, unlocatedDamage - rolled.Cost);
            }
            if (headCut && !companionCut)
                TryCompanion(true, "head");
            else if (!cutLimb && !proneHit && ballistic && ImpactCuts.CanFallbackToLimb(finalDeath, heavy, cutStrength))
                cutLimb = TryCompanion(Battlefield.Height(center) > floor + 3.7f, "heavy finish");
        }

        if (ballistic && !cutLimb)
            sounds.PlayImpact(center, heavy);

        if (cutLimb && !preview && standardReplika && (missing & InjuryRules.Legs) == InjuryRules.Legs && !crawlKilled)
            PrepareCrawlDeath(ragdoll == null ? rig.Capture() : null);

        if (crawlKilled && !downed)
        {
            FinishCrawlDeath(direction, point, mayRagdoll);
            QueuePool(amount * 1.35f);
            return;
        }

        if (cutLimb)
        {
            CancelSwipe();
            if (!proneHit && UsesInjuredAttack(mayCrawl))
            {
                Target.EndHit();
                if (animator) animator.ResetTrigger(Target.anim_Attack);
                log($"injured attack mode weaponMask=0x{WeaponArms:X} missing=0x{missing:X}");
            }
        }

        if (fall && !downed)
        {
            CancelSwipe();
            ReleaseCrawlInteraction();
            downed = knockdown;
            QueuePool(amount * (launch ? 1.8f : critical ? 1.05f : 1.35f));
            if (launch)
                BeginRagdoll(direction, point, strength, rifle);
            else
                log($"native fall animation retained critical={critical} finalDeath={finalDeath}");
        }

        bool ApplyCut(Part part, bool playSound = true)
        {
            if (Missing(part) || !Sever(part, direction, heavy, launch, out var wound, out var outward, corpseOffset))
                return false;
            var spray = ballistic ? (shotDirection + outward * 0.18f).normalized
                : (direction * 0.8f + outward * 0.65f).normalized;
            blood.Spurt(wound, spray, impactFloor, walls, amount, heavy ? 68 : 50, heavy ? 1.28f : 1.06f, ballistic);
            if (!preview && playSound) sounds.Play(wound, false);
            return true;
        }

        bool TryCompanion(bool upper, string primary, bool playSound = true)
        {
            float side = Vector3.Dot(Target.transform.right, -direction);
            bool right = Mathf.Abs(side) > 0.14f ? side > 0 : Random.value < 0.5f;
            foreach (var part in ImpactCuts.CompanionOrder(ranked, missing, upper, right))
            {
                if (!ApplyCut(part, playSound))
                    continue;
                log($"fatal cut companion shot={shotId} primary={primary} limb={part}");
                return true;
            }
            return false;
        }
    }

    private ImpactCuts.UnlocatedCut RollUnlocatedCut(int shotId, Vector3 direction, float strength, bool heavy, bool terminal,
        int unavailable)
    {
        unlocatedDamage = Mathf.Min(1.4f, unlocatedDamage + strength);
        if (lastUnlocatedRoll == shotId)
            return default;
        int available = 0;
        for (int i = (int)Part.LeftLeg; i <= (int)Part.Head; i++)
            if (rig.CutBone((Part)i))
                available |= 1 << i;
        Vector3 right = Battlefield.OnPlane(Target.transform.right).normalized;
        float side = Mathf.Clamp(Vector3.Dot(right, -direction), -1, 1);
        float roll = (float)cutRandom.NextDouble();
        var decision = ImpactCuts.RollUnlocated(partDamage, unlocatedDamage, strength, heavy, terminal, unavailable,
            available, side, roll, (float)cutRandom.NextDouble());
        if (decision.Chance > 0)
            lastUnlocatedRoll = shotId;
        log($"cut unlocated shot={shotId} reserve={unlocatedDamage:F2} chance={decision.Chance:F2} roll={roll:F2} selected={decision.Part}");
        return decision;
    }

    public void Stomp(Rig.Snapshot pose, bool mayCut, bool mayRagdoll, BloodEffects blood, float amount, CutSounds sounds,
        Vector3? crawlContact = null)
    {
        if (lastStompFrame == Time.frameCount)
            return;
        lastStompFrame = Time.frameCount;
        var point = crawlContact ?? (Target.stompInter ? Target.stompInter.transform.position : Target.transform.position);
        var player = PlayerAttack.instance;
        var direction = Battlefield.OnPlane(player ? point - player.transform.position : -Target.transform.forward).normalized;
        if (direction.sqrMagnitude < 0.01f)
            direction = Battlefield.OnPlane(player ? player.transform.forward : -Target.transform.forward).normalized;
        if (waistSplit != null)
        {
            waistSplit.Stomp(direction, point);
            blood.Spurt(waistSplit.SpillPoint, direction, floor, WallMask, amount, 82, 1.3f);
            sounds.Play(point, true);
            log("stomp displaced split body");
            return;
        }

        if (ragdoll != null)
        {
            blood.Spurt(point, direction, floor, WallMask, amount, 66, 1.34f);
            sounds.Play(point, true);
            return;
        }
        if (pose != null)
            rig.Apply(pose);
        floor = Battlefield.FloorBelow(point, floor);
        var contact = Battlefield.AtHeight(point, floor + 0.75f);
        Part part = Part.Body;
        if (mayCut)
        {
            var ray = new ImpactCuts.Ray(Rig.ToPoint(contact + Battlefield.Up * 2.5f), Rig.ToPoint(Battlefield.Down));
            foreach (var hit in ImpactCuts.Rank(new[] { ray }, rig.HitZones(missing)))
            {
                if (hit.Part == Part.Body || Missing(hit.Part) || !Sever(hit.Part, direction, true, false, out var wound, out var outward))
                    continue;
                part = hit.Part;
                blood.Spurt(wound, (direction + outward * 0.5f).normalized, floor, WallMask, amount, 62, 1.22f);
                break;
            }
        }

        blood.Spurt(contact, direction, floor, WallMask, amount, 66, 1.34f);
        sounds.Play(contact, true);
        CancelSwipe();
        downed = true;
        QueuePool(amount * 1.95f, contact);
        if (mayRagdoll && pose != null && rig.CanRagdoll && Target.hitbox && Target.hitbox.HP <= 0)
        {
            try
            {
                // Critical and Fire can finish their stomp branch with zero HP before committing death.
                if (Target.state != EnemyController.enemystate.dead)
                {
                    Target.EndHit();
                    Target.KillSilent();
                }
                PreventRespawn(Target);
                BeginRagdoll(direction, contact, 0f, false, stomp: true);
                if (ragdoll != null && animator)
                    animator.ResetTrigger(Target.anim_Fall);
            }
            catch (Exception error) { log($"stomp ragdoll fallback: {error}"); }
        }
        log($"stomp processed part={part} point={Battlefield.Format(contact)} crawl={crawlContact.HasValue} ragdoll={ragdoll != null}");
        if (Target.state == EnemyController.enemystate.dead && Target.hitbox && Target.hitbox.HP <= 0)
            elsterBlood.Queue(contact, true, true, amount);
    }

    private bool HitsWaist(ImpactCuts.Ray[] rays)
    {
        if (!rig.Hips || !rig.Chest)
            return false;
        var center = Rig.ToPoint(Vector3.Lerp(rig.Hips.position, rig.Chest.position, WaistFraction));
        return Array.Exists(rays, ray => ray.DistanceTo(center) <= 1.63f);
    }

    public void RestorePose()
    {
        rig.RestorePose();
        UpdateGroundAim();
    }

    private void QueuePool(float scale, Vector3? stompPosition = null)
    {
        poolScale = scale;
        poolReadyAt = Time.time + 0.23f;
        stompPoolPosition = stompPosition;
    }

    public void PrepareCrawlDeath(Rig.Snapshot pose)
    {
        if (crawlKilled)
            return;
        crawlKilled = true;
        if (ragdoll == null && waistSplit == null)
        {
            crawlDeathPose = pose ?? CapturePose();
            crawlDeathRoot = Target.transform.position;
        }
        CancelSwipe();
        ReleaseCrawlInteraction();
        KeepFatalCutDead();
        if (animator)
        {
            animator.ResetTrigger(Target.anim_Fall);
            animator.SetBool(Target.anim_Critical, false);
        }
        HoldNativeAnimation();
        log($"crawl death permanent missing=0x{missing:X} crawled={enteredCrawl}");
    }

    private void FinishCrawlDeath(Vector3 direction, Vector3? point, bool mayRagdoll)
    {
        downed = true;
        if (crawlDeathPose == null)
            return;
        ApplyCrawlDeathPose();
        if (mayRagdoll)
        {
            try { BeginRagdoll(direction, point, 0f, false, collapse: true); }
            catch (Exception ex) { log($"crawler ragdoll fallback: {ex}"); }
        }
        if (ragdoll != null)
            crawlDeathPose = null;
        else if (!enteredCrawl)
        {
            rig.Crawl(Target.transform, floor, phase, 0f, 1f, Missing(Part.LeftLeg), Missing(Part.RightLeg),
                Missing(Part.LeftArm), Missing(Part.RightArm));
            crawlDeathPose = rig.Capture();
        }
    }

    private void ApplyCrawlDeathPose()
    {
        if (crawlDeathPose != null)
            rig.Apply(crawlDeathPose with { HipsPosition = crawlDeathPose.HipsPosition + Target.transform.position - crawlDeathRoot });
    }

    public void Tick(bool mayCrawl, bool mayCut, bool mayRagdoll, BloodEffects blood, float amount, CutSounds sounds)
    {
        var position = Target.transform.position;
        Vector3 travel = Battlefield.OnPlane(position - previous);
        previous = position;
        bool killed = Target.state == EnemyController.enemystate.dead;
        bool alive = !killed && Target.hitbox && Target.hitbox.HP > 0;
        if (alive && downed && KeepFatalCutDead())
        {
            killed = true;
            alive = false;
        }
        if (revivalStage != RevivalStage.None && (!alive || NativeDowned))
            EndBodyRevival(interrupted: true);
        if (alive && downed)
        {
            log($"revival nativeState={Target.state} hp={Target.hitbox.HP}");
            if (!awaitingCorpseRevival || !BeginBodyRevival())
                EndRagdoll();
            Array.Clear(partDamage, 0, partDamage.Length);
            unlocatedDamage = 0;
            downed = false;
            poolScale = 0;
        }
        PrepareKnockdownCrawl(alive, mayCrawl, position);
        if (!PrepareRevivalFrame(mayCrawl))
            return;
        ObserveRevival(alive);
        if (!alive)
            ApplyCrawlDeathPose();

        if (ragdoll != null && downed)
        {
            HoldNativeAnimation();
            if (corpseRoot && !corpseRoot.activeSelf)
                corpseRoot.SetActive(true);
            ragdoll.Tick();
            foreach (var renderer in hiddenVariants)
                if (renderer && !renderer.forceRenderingOff) renderer.forceRenderingOff = true;
            if (!corpseBakedAtRest && (physics.SimulatedTimeThisFrame > 0 || !ragdoll.IsSimulating))
                BakeCorpse();
            if (Time.time >= nextPhysicsLog && ragdoll.IsSimulating)
            {
                nextPhysicsLog = Time.time + 0.25f;
                log($"ragdoll step {ragdoll.Diagnostics} visualLift={corpseLift:F3}");
            }
            FollowDownedBody(ragdoll.PlanarTravel, corpseCenter);
        }
        if (waistSplit != null)
        {
            HoldNativeAnimation();
            waistSplit.Tick(physics.SimulatedTimeThisFrame, blood, amount, floor, WallMask);
            FollowDownedBody(waistSplit.Travel, waistSplit.StompPoint);
        }
        TryStartPool(blood);

        bool crawling = alive && IsCrawling && mayCrawl && revivalStage != RevivalStage.Reassemble;
        enteredCrawl |= crawling;
        if (crawling != wasCrawling)
        {
            wasCrawling = crawling;
            log($"crawl active={crawling} missing=0x{missing:X} position={Battlefield.Format(position)}");
        }
        blend = NativeDowned || holdingKnockdownCrawl || revivalStage == RevivalStage.Reassemble ? 0f
            : crawling && (revivalStage == RevivalStage.Align || knockdownPose != null) ? 1f
            : Mathf.MoveTowards(blend, crawling ? 1 : 0, Time.deltaTime * 4.0f);
        if (alive && !downed && blend > 0)
        {
            float movement = Mathf.Clamp01(travel.magnitude / Mathf.Max(0.001f, Time.deltaTime) * 0.30f);
            if (travel.sqrMagnitude < 25)
                phase = Mathf.Repeat(phase + travel.magnitude * 1.2f + Time.deltaTime * 0.35f, (float)Math.PI * 2);
            if (Mathf.Abs(Battlefield.Height(position) - floor) > 1.3f)
                floor = FindFloor();
            rig.Crawl(Target.transform, floor, phase, movement, blend, Missing(Part.LeftLeg), Missing(Part.RightLeg),
                Missing(Part.LeftArm), Missing(Part.RightArm));
            if (crawling && knockdownPose != null)
                BlendKnockdownCrawl(position);
        }

        bool revivalOverride = revivalStage is RevivalStage.Reassemble or RevivalStage.Align;
        if (revivalStage != RevivalStage.None)
            AdvanceBodyRevival();
        if (alive && revivalStage == RevivalStage.None && Target.state == EnemyController.enemystate.pursuit
            && !Target.tracking && UsesInjuredAttack(mayCrawl))
            TryStartSwipe(mayCrawl);
        TickSwipe(crawling);
        AdvanceLimbReturns(sounds);
        if (ragdoll == null && waistSplit == null)
            RefreshSkins(crawlDeathPose != null || holdingKnockdownCrawl || alive && !NativeDowned && (blend > 0 || revivalOverride));
        visiblePose = alive && (blend > 0 || swipeArm != Part.Body || revivalOverride || holdingKnockdownCrawl)
            ? rig.Capture(visiblePose) : null;
        UpdateCrawlInteraction(crawling, mayCut, mayRagdoll, blood, amount, sounds);
        TickWounds(killed, blood, amount);
    }

    private void UpdateCrawlInteraction(bool crawling, bool mayCut, bool mayRagdoll, BloodEffects blood, float amount, CutSounds sounds)
    {
        if (crawling || holdingKnockdownCrawl || revivalStage != RevivalStage.None)
        {
            crawlCenter = Vector3.Lerp(rig.Hips.position, rig.Chest.position, 0.72f);
            UpdateGroundAim();
        }
        else
            ReleaseGroundAim();

        if (!crawling || revivalStage != RevivalStage.None)
        {
            ReleaseCrawlPrompt();
            return;
        }

        var prompt = Target.stompInter;
        if (!prompt)
            return;
        var contact = Battlefield.AtHeight(crawlCenter, floor);
        if (!ownsCrawlPrompt)
        {
            crawlPromptRest = prompt.transform.localPosition;
            prompt.triggered = false;
            ownsCrawlPrompt = true;
        }
        prompt.transform.position = contact;
        if (!prompt.gameObject.activeSelf)
            prompt.gameObject.SetActive(true);
        if (!prompt.triggered)
            return;
        prompt.triggered = false;

        var pose = CapturePose();
        Target.EndHit();
        Target.Kill();
        if (Target.state != EnemyController.enemystate.dead || Target.hitbox.HP > 0)
            return;
        Target.revives = 0;
        ReleaseCrawlInteraction();
        PlayerState.Stomp();
        log($"crawler stomp confirmed hp={Target.hitbox.HP} point={Battlefield.Format(contact)}");
        try { Stomp(pose, mayCut, mayRagdoll, blood, amount, sounds, contact); }
        finally { RestorePose(); }
    }

    public void UpdateGroundAim()
    {
        if (!Target || !HasGroundAimPose || Target.state == EnemyController.enemystate.dead)
            return;
        if (!crawlAimSearched)
        {
            crawlAimSearched = true;
            var aim = Target.GetComponent<CharacterAimPoint>();
            var point = aim && aim.TargetPoint ? aim.TargetPoint : Target.AimPoint;
            if (point && point != Target.transform && point.IsChildOf(Target.transform))
            {
                crawlAim = point;
                crawlAimRest = point.localPosition;
                log($"crawl aim anchor={point.name} center={Battlefield.Format(crawlCenter)}");
            }
        }
        if (crawlAim)
            crawlAim.position = crawlCenter + (Target.transform.position - previous);
    }

    private void ReleaseCrawlInteraction()
    {
        ReleaseGroundAim();
        ReleaseCrawlPrompt();
    }

    private void ReleaseGroundAim()
    {
        if (crawlAim)
            crawlAim.localPosition = crawlAimRest;
        crawlAim = null;
        crawlAimSearched = false;
    }

    private void ReleaseCrawlPrompt()
    {
        if (!ownsCrawlPrompt)
            return;
        ownsCrawlPrompt = false;
        if (!Target || !Target.stompInter || NativeDowned)
            return;
        var prompt = Target.stompInter;
        prompt.triggered = false;
        prompt.transform.localPosition = crawlPromptRest;
        if (prompt.gameObject.activeSelf)
            prompt.gameObject.SetActive(false);
    }

    private void TryStartPool(BloodEffects blood)
    {
        if (poolScale <= 0 || Time.time < poolReadyAt || ragdoll?.IsSimulating == true)
            return;
        if (ragdoll == null && waistSplit == null && stompPoolPosition == null && !downed && !NativeDowned)
        {
            poolScale = 0;
            return;
        }
        Vector3 spill = waistSplit?.SpillPoint ?? (ragdoll != null ? corpseCenter :
            stompPoolPosition ?? (rig.Chest ? rig.Chest.position : Target.transform.position));
        if (ragdoll == null && stompPoolPosition == null && Battlefield.Height(spill) > floor + 2.35f)
            return;
        if (ragdoll != null)
        {
            float lowest = float.PositiveInfinity;
            foreach (var wound in wounds)
            {
                if (!wound.Sample(true, out var point, out _))
                    continue;
                float height = Battlefield.Height(point);
                if (height >= lowest)
                    continue;
                lowest = height;
                spill = point;
            }
        }
        log($"blood pool at={Battlefield.Format(spill)} amount={poolScale:F2} growth=9s");
        blood.Pool(spill, Battlefield.FloorBelow(spill, floor), poolScale);
        poolScale = 0;
    }

    private void TickWounds(bool killed, BloodEffects blood, float amount)
    {
        float now = Time.time;
        for (int i = wounds.Count - 1; i >= 0; i--)
        {
            var wound = wounds[i];
            if (!Missing(wound.Part) || wound.Flow.Finished(now))
            {
                log($"wound stopped limb={wound.Part} emissions={wound.Emissions}");
                wounds.RemoveAt(i);
                continue;
            }
            if (!wound.Sample(ragdoll != null, out var point, out var outward))
                continue;
            wound.Track(point, now);
            float movement = Mathf.Clamp01(Battlefield.OnPlane(wound.Velocity).magnitude * 0.14f);
            float pressure = wound.Flow.Pulse(now, killed, movement);
            if (pressure <= 0 || amount <= 0)
                continue;
            float beneath = Battlefield.FloorBelow(point, floor);
            if (blood.Drip(point, outward, wound.Velocity, beneath, WallMask, amount, pressure, ref wound.Flow.PlayedSound))
                wound.Emissions++;
        }
    }

    private void BeginRagdoll(Vector3 direction, Vector3? point, float strength, bool rifle, bool stomp = false, bool collapse = false)
    {
        visiblePose = null;
        try
        {
            ragdoll = BodyRagdoll.Start(rig, physics, direction, point, Target.transform.forward, floor, missing,
                strength, rifle, stomp, log, collapse);
            if (ragdoll == null)
            {
                log($"ragdoll declined rig={rig.CanRagdoll}");
                return;
            }

            corpseRoot = new GameObject("REDLINE Physics Corpse");
            SceneManager.MoveGameObjectToScene(corpseRoot, Target.gameObject.scene);
            corpseCenter = rig.Chest.position;
            ScanSkins();
            foreach (var renderer in Target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer || !renderer.sharedMesh || renderer.bones.Length == 0)
                    continue;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                {
                    HideVariant(renderer);
                    continue;
                }
                var existing = skins.Find(skin => skin.Source == renderer && skin.CanDisplay);
                if (existing == null && renderer.forceRenderingOff)
                    continue;

                bool owned = existing == null;
                var skin = existing ?? new SkinCut(renderer, null);
                corpseSkins.Add((skin, owned));
                skin.BeginCorpse(corpseRoot.transform);
            }
            if (corpseSkins.Count == 0)
            {
                log("ragdoll fallback: no visible skinned body surfaces");
                EndRagdoll();
                return;
            }
            foreach (var renderer in animator.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!renderer || !renderer.enabled || renderer.forceRenderingOff
                    || renderer.name.StartsWith("REDLINE", StringComparison.Ordinal)
                    || SkinCut.IsStockGlitchQuad(renderer) || !renderer.transform.IsChildOf(rig.Hips))
                    continue;
                var copy = SkinCut.CopyAccessory(renderer, corpseRoot.transform);
                if (!copy)
                    continue;
                corpseAccessories.Add((renderer, copy));
                HideVariant(renderer);
            }
            ForceAnimation();
            HoldNativeAnimation();
            corpseBakedAtRest = false;
            nextPhysicsLog = 0;
            CaptureDownedAnchors();
            BakeCorpse();
            foreach (var (skin, owned) in corpseSkins)
                log($"corpse surface mesh={skin.Source.sharedMesh.name} cut={!owned} {skin.DrawStatus}");
            log($"corpse renderer took over meshes={corpseSkins.Count} hiddenVariants={hiddenVariants.Count} missing=0x{missing:X} {ragdoll.Diagnostics}");
        }
        catch
        {
            EndRagdoll();
            throw;
        }
    }

    private void CancelSwipe() => swipeArm = Part.Body;

    private void HideVariant(Renderer renderer)
    {
        if (renderer.forceRenderingOff)
            return;
        renderer.forceRenderingOff = true;
        hiddenVariants.Add(renderer);
    }

    private void BakeCorpse()
    {
        corpseRoot.transform.localPosition = Vector3.zero;
        rig.Apply(ragdoll.Snapshot);
        try
        {
            ragdoll.Pose();
            foreach (var (source, copy) in corpseAccessories)
                if (source && copy)
                    copy.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            corpseCenter = Vector3.Lerp(rig.Hips.position, rig.Chest.position, 0.48f);
            corpseFacing = Battlefield.OnPlane(rig.Chest.position - rig.Hips.position);
            float depth = float.NegativeInfinity;
            float bodyDepth = float.NegativeInfinity;
            var lowest = corpseCenter;
            var lowestBody = corpseCenter;
            foreach (var (skin, _) in corpseSkins)
            {
                skin.Refresh();
                float bottom = skin.LowestPoint(out var contact);
                if (bottom > depth)
                {
                    depth = bottom;
                    lowest = contact;
                }
                if (skin.SupportsFloor && bottom > bodyDepth)
                {
                    bodyDepth = bottom;
                    lowestBody = contact;
                }
            }
            float allSurfaces = depth;
            if (!float.IsNegativeInfinity(bodyDepth))
            {
                depth = bodyDepth;
                lowest = lowestBody;
            }
            if (float.IsNegativeInfinity(depth) || float.IsNaN(depth) || float.IsPositiveInfinity(depth))
                throw new InvalidOperationException("Ragdoll could not produce a complete visible body");

            corpseBakedAtRest = !ragdoll.IsSimulating;
            corpseSupportFloor = corpseBakedAtRest
                ? Battlefield.FloorBelow(lowest, Battlefield.Height(lowest))
                : Battlefield.FloorBelow(corpseCenter, floor);
            corpseLift = DebrisContact.FloorCorrection(depth, corpseSupportFloor, ragdoll.Landed, corpseBakedAtRest);
            corpseRoot.transform.position += Battlefield.Up * corpseLift;
            corpseCenter += Battlefield.Up * corpseLift;
            pendingBoxFit = true;
            foreach (var wound in wounds)
                wound.CaptureCorpse(Battlefield.Up * corpseLift);
            if (corpseBakedAtRest)
                log($"corpse settled floor={corpseSupportFloor:F3} bodyMaxZ={depth:F3} allMaxZ={allSurfaces:F3} correction={corpseLift:F3} center={Battlefield.Format(corpseCenter)}");
        }
        finally
        {
            rig.RestorePose();
        }
    }

    private void CaptureDownedAnchors()
    {
        if (Target.stompInter)
        {
            var anchor = Target.stompInter.transform;
            downedAnchors.Add((anchor, anchor.localPosition, true));
        }
        if (Target.downedHitbox)
        {
            var anchor = Target.downedHitbox.transform;
            downedAnchors.Add((anchor, anchor.localPosition, true));
            fittedDownedBox = Target.downedHitbox.GetComponent<BoxCollider>();
            if (fittedDownedBox)
            {
                originalBoxCenter = fittedDownedBox.center;
                originalBoxSize = fittedDownedBox.size;
                originalBoxRotation = anchor.localRotation;
            }
        }
        foreach (var renderer in Target.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!renderer || !SkinCut.IsStockGlitchQuad(renderer))
                continue;
            var anchor = renderer.transform;
            downedAnchors.Add((anchor, anchor.localPosition, false));
        }
    }

    private void FollowDownedBody(Vector3 travel, Vector3 center)
    {
        foreach (var (anchor, rest, interaction) in downedAnchors)
        {
            if (!anchor)
                continue;
            var parent = anchor.parent;
            var world = parent ? parent.TransformPoint(rest) : rest;
            anchor.position = fittedDownedBox && anchor == fittedDownedBox.transform && ragdoll != null ? center :
                interaction ? new Vector3(center.x, center.y, world.z) : world + travel;
        }
        if (pendingBoxFit && fittedDownedBox && fittedDownedBox.gameObject.activeInHierarchy
            && (corpseBakedAtRest || Time.time >= nextBoxFit))
        {
            FitDownedBox();
            nextBoxFit = Time.time + 0.12f;
        }
    }

    private void FitDownedBox()
    {
        var frame = fittedDownedBox.transform;
        if (corpseFacing.sqrMagnitude > 0.05f)
            frame.rotation = Quaternion.LookRotation(corpseFacing, Battlefield.Up);
        var boxSpace = frame.worldToLocalMatrix;
        var min = new System.Numerics.Vector3(float.PositiveInfinity);
        var max = new System.Numerics.Vector3(float.NegativeInfinity);
        foreach (var (skin, _) in corpseSkins)
            skin.AddHitboxBounds(boxSpace, ref min, ref max);
        if (float.IsInfinity(min.X))
            return;
        var center = (min + max) * 0.5f;
        var size = max - min;
        fittedDownedBox.center = new Vector3(center.X, center.Y, center.Z);
        fittedDownedBox.size = Vector3.Max(new Vector3(size.X, size.Y, size.Z) + Vector3.one * 0.04f, Vector3.one * 0.08f);
        pendingBoxFit = false;
    }

    private void RefreshSkins(bool procedural)
    {
        foreach (var skin in skins)
        {
            skin.SetProceduralPose(procedural);
            skin.Refresh();
        }
    }

    private int WallMask
    {
        get
        {
            const int actors = (1 << 11) | (1 << 12) | (1 << 13) | (1 << 17) | (1 << 18);
            int mask = Target.character ? Target.character.WallMask.value : 0;
            return (mask | Target.stoppingMask.value) & ~actors;
        }
    }

    private float FindFloor()
    {
        Vector3 position = Target.transform.position;
        floorPosition = position;
        float height = Battlefield.Height(position);
        float measured = Battlefield.FloorBelow(position, height, out var surface);
        log($"floor root={Battlefield.Format(position)} rootHeight={height:F3} result={measured:F3} source={surface}");
        return measured;
    }

    private bool Sever(Part part, Vector3 direction, bool heavy, bool fling, out Vector3 wound, out Vector3 outward,
        Vector3 corpseOffset = default)
    {
        wound = outward = default;
        if (!IsSupported(Target) || part == Part.Head && (!settings.Decapitation.Value || !Target.hitbox || Target.hitbox.HP > 0))
            return false;
        Transform bone = rig.CutBone(part);
        if (!bone || !animator)
            return false;
        ScanSkins();
        if (!skins.Exists(skin => skin.HasPart(part) && skin.CanSever && !SkinCut.IsEffectsPass(skin.Source)))
            return false;

        Transform tip = rig.Tip(part);
        wound = bone.position + corpseOffset;
        Vector3 end = tip ? tip.position + corpseOffset : wound + Battlefield.Up * 0.92f;
        ForceAnimation();
        float width = part == Part.Head ? 0.54f : Mathf.Clamp(Vector3.Distance(wound, end) * 0.16f, 0.27f, 0.52f);
        var loose = physics.Limb(wound, end, ragdoll != null ? corpseSupportFloor : floor, width);
        var severed = SeveredPart.Capture(part, loose, bone);
        missing |= 1 << (int)part;
        SkinCut.RimAnchor rim = null;
        outward = (end - wound).normalized;
        if (outward.sqrMagnitude < 0.01f)
            outward = Battlefield.Up;
        foreach (var skin in skins)
        {
            if (!skin.HasPart(part))
                continue;
            skin.Sever(part, missing, loose, corpseOffset);
            var candidate = skin.FindWoundRim(part, wound, outward);
            if (candidate != null && (rim == null || candidate.Perimeter > rim.Perimeter))
                rim = candidate;
            log($"cut surface mesh={skin.Source.sharedMesh.name} rimLoops={skin.RimCount} active={skin.CanDisplay} {skin.DrawStatus}");
        }
        if (part == Part.Head)
            ConfirmFatalCut(MajorCut.Head);
        var bleeding = new Wound(part, bone, rim, outward, Time.time, heavy);
        wounds.Add(bleeding);
        if (ragdoll != null)
        {
            bleeding.CaptureCorpse(corpseOffset);
            ragdoll.RemoveLimb(bone);
            pendingBoxFit = true;
        }
        bleeding.Sample(ragdoll != null, out wound, out outward);
        log($"wound started limb={part} rim={rim != null} point={Battlefield.Format(wound)}");

        HideAccessories(part, bone, loose.Visual.transform);
        loose.Launch(direction, fling);
        RememberSeveredPart(severed);

        log($"cut limb={part} wound={Battlefield.Format(wound)} endpoint={Battlefield.Format(end)} floor={floor:F3} radius={width:F3} {loose.Diagnostics}");
        return true;
    }

    private void ScanSkins()
    {
        if (skinScanned)
            return;
        skinScanned = true;
        foreach (var renderer in Target.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (!renderer || !renderer.sharedMesh || renderer.bones.Length == 0)
                continue;
            var labels = map.Find(renderer, rig);
            if (labels != null)
                skins.Add(new SkinCut(renderer, labels));
            else
                log($"No cut binding for {renderer.sharedMesh.name} ({renderer.sharedMesh.vertexCount}, {renderer.sharedMesh.subMeshCount})");
        }
    }

    private void HideAccessories(Part part, Transform bone, Transform fragment = null)
    {
        for (int i = corpseAccessories.Count - 1; i >= 0; i--)
        {
            var (source, copy) = corpseAccessories[i];
            if (!source || !source.transform.IsChildOf(bone))
                continue;
            if (copy && fragment)
                copy.SetParent(fragment, true);
            else if (copy)
                UnityEngine.Object.Destroy(copy.gameObject);
            corpseAccessories.RemoveAt(i);
            hiddenVariants.Remove(source);
            hidden.Add((source, part));
        }
        // Copy only the visible mesh. Hurtboxes and other gameplay components stay with the actor.
        foreach (var accessory in bone.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (!accessory || accessory.name.StartsWith("REDLINE", StringComparison.Ordinal))
                continue;
            if (accessory.forceRenderingOff)
                continue;
            if (fragment && accessory.enabled && accessory.gameObject.activeInHierarchy)
                SkinCut.CopyAccessory(accessory, fragment);
            accessory.forceRenderingOff = true;
            hidden.Add((accessory, part));
        }
    }

    private void ConfirmFatalCut(MajorCut cut)
    {
        fatalCut = cut;
        KeepFatalCutDead();
        log($"permanent death confirmed cut={cut} queued={Target.queuedForRespawn} revives={Target.revives}");
    }

    public bool KeepFatalCutDead()
    {
        if (fatalCut == MajorCut.None && !crawlKilled)
            return false;
        // A stomp can empty HP before the native death state is applied.
        if (Target.state != EnemyController.enemystate.dead || Target.hitbox && Target.hitbox.HP > 0)
        {
            Target.EndHit();
            Target.KillSilent();
            if (animationHeld && animator)
                animator.ResetTrigger(Target.anim_Fall);
        }
        PreventRespawn(Target);
        return true;
    }

    public static void PreventRespawn(EnemyController target)
    {
        target.permadeath = true;
        target.queuedForRespawn = false;
        target.revives = 0;
    }

    private void RestorePart(Part part)
    {
        int stillMissing = missing & ~(1 << (int)part);
        foreach (var skin in skins)
            skin.RestoreParts(stillMissing);
        missing = stillMissing;
        partDamage[(int)part] = 0;
        wounds.RemoveAll(wound => wound.Part == part);
        RestoreAccessories(missing);
        for (int i = severedParts.Count - 1; i >= 0; i--)
        {
            var severed = severedParts[i];
            if (severed.Part != part)
                continue;
            physics.ConsumeLimb(severed.Limb);
            severedParts.RemoveAt(i);
        }
    }

    private void RestoreAccessories(int stillMissing)
    {
        for (int i = hidden.Count - 1; i >= 0; i--)
        {
            var (renderer, part) = hidden[i];
            if ((stillMissing & (1 << (int)part)) != 0)
                continue;
            if (renderer) renderer.forceRenderingOff = false;
            hidden.RemoveAt(i);
        }
    }

    public void Suspend()
    {
        CancelLimbReturns();
        EndBodyRevival(interrupted: true);
        knockdownPose = null;
        holdingKnockdownCrawl = false;
        ReleaseCrawlInteraction();
        RestorePose();
        CancelSwipe();
        visiblePose = null;
        if (corpseRoot && !Target.gameObject.activeInHierarchy)
            corpseRoot.SetActive(false);
        ragdoll?.Tick();
    }

    private void ForceAnimation()
    {
        if (forcedAnimation || !animator)
            return;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        forcedAnimation = true;
    }

    private void EndRagdoll()
    {
        ClearWaistSplit();
        if (ragdoll != null) log($"corpse renderer released surfaces={corpseSkins.Count}");
        ragdoll?.Dispose();
        ragdoll = null;
        foreach (var (skin, owned) in corpseSkins)
        {
            if (owned) skin.Dispose();
            else skin.EndCorpse();
        }
        corpseSkins.Clear();
        corpseAccessories.Clear();
        foreach (var renderer in hiddenVariants)
            if (renderer) renderer.forceRenderingOff = false;
        hiddenVariants.Clear();
        if (corpseRoot)
        {
            corpseRoot.SetActive(false);
            UnityEngine.Object.Destroy(corpseRoot);
            corpseRoot = null;
        }
        RestoreDownedAnchors();
        ReleaseUnusedAnimation();
    }

    public void ClearWaistSplit()
    {
        if (waistSplit == null)
            return;
        log(waistSplit.Diagnostics);
        waistSplit.Dispose();
        waistSplit = null;
        RestoreDownedAnchors();
        ReleaseUnusedAnimation();
    }

    private void RestoreDownedAnchors()
    {
        if (fittedDownedBox)
        {
            fittedDownedBox.center = originalBoxCenter;
            fittedDownedBox.size = originalBoxSize;
            fittedDownedBox.transform.localRotation = originalBoxRotation;
        }
        fittedDownedBox = null;
        pendingBoxFit = false;
        foreach (var (anchor, rest, _) in downedAnchors)
            if (anchor) anchor.localPosition = rest;
        downedAnchors.Clear();
    }

    public void Dispose()
    {
        CancelLimbReturns();
        EndBodyRevival(interrupted: true);
        ReleaseCrawlInteraction();
        RestorePose();
        crawlDeathPose = null;
        EndRagdoll();
        foreach (var skin in skins) skin.Dispose();
        RestoreAccessories(0);
        if (animator && forcedAnimation) animator.cullingMode = originalCulling;
    }

    private sealed class Wound(Part part, Transform bone, SkinCut.RimAnchor rim, Vector3 outward, float now, bool heavy)
    {
        private readonly Vector3 localDirection = bone.InverseTransformDirection(outward);
        private Vector3 corpsePoint;
        private Vector3 corpseDirection;
        private Vector3 previous;
        private float lastSample = float.NegativeInfinity;
        public Part Part { get; } = part;
        public StumpBleed Flow { get; } = new(now, heavy);
        public Vector3 Velocity { get; private set; }
        public int Emissions { get; set; }

        public bool Sample(bool corpse, out Vector3 point, out Vector3 normal)
        {
            if (rim != null && rim.TrySample(out point, out normal))
                return true;
            if (corpse)
            {
                point = corpsePoint;
                normal = corpseDirection;
                return true;
            }
            point = normal = Vector3.zero;
            if (!bone)
                return false;
            point = bone.position;
            normal = bone.TransformDirection(localDirection);
            return true;
        }

        public void CaptureCorpse(Vector3 lift)
        {
            if (!bone)
                return;
            corpsePoint = bone.position + lift;
            corpseDirection = bone.TransformDirection(localDirection);
        }

        public void Track(Vector3 point, float time)
        {
            float delta = time - lastSample;
            Vector3 travel = point - previous;
            Velocity = delta > 0.001f && delta < 0.18f && travel.sqrMagnitude < 16
                ? Vector3.Lerp(Velocity, Vector3.ClampMagnitude(travel / delta, 16f), 0.32f)
                : Vector3.zero;
            previous = point;
            lastSample = time;
        }
    }

}
