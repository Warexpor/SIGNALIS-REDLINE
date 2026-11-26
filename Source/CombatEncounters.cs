using System;
using System.Collections.Generic;
using Pathfinding;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Redline;

internal sealed class CombatEncounters(RedlineSettings settings, Action<string> log)
{
    private const string ReinforcementId = "redline.reinforcement.";
    private readonly Dictionary<int, Room> evaluated = new();
    private readonly List<(EnemyController Actor, EnemyManager Manager)> extras = new();
    private readonly List<EnemyController> stock = new();
    private bool networkedLogged;
    private bool idleLogged;
    private Room room;
    private float readyAt;

    public void SceneChanged()
    {
        idleLogged = false;
        extras.RemoveAll(extra => !extra.Actor);
        foreach (int id in new List<int>(evaluated.Keys))
            if (!evaluated[id]) evaluated.Remove(id);
        room = null;
    }

    public void Tick()
    {
        float rate = RedlineSettings.Read(settings.ReinforcementRate, 1f);
        if (rate <= 0f || !CombatWorld.Playing || !AstarPath.active)
        {
            if (!idleLogged)
            {
                idleLogged = true;
                log($"reinforcements idle rate={rate} astar={AstarPath.active != null} {CombatWorld.DescribePlaying()}");
            }
            return;
        }
        if (CombatWorld.Networked)
        {
            if (!networkedLogged)
            {
                networkedLogged = true;
                log($"reinforcements standing down for multiplayer: {CombatWorld.Describe()}");
            }
            if (extras.Count != 0)
            {
                Dispose();
                evaluated.Clear();
            }
            return;
        }
        var current = PlayerState.currentRoom;
        if (!current || !current.chunk || !current.chunk.activeInHierarchy || evaluated.ContainsKey(current.GetInstanceID()))
            return;
        if (room != current)
        {
            room = current;
            readyAt = Time.time + 0.45f;
            return;
        }
        if (Time.time < readyAt)
            return;
        evaluated.Add(current.GetInstanceID(), current);

        var player = PlayerState.player.transform.position;
        stock.Clear();
        int candidates = 0;
        foreach (var target in current.GetComponentsInChildren<EnemyController>(false))
        {
            if (!target || !target.isActiveAndEnabled || !EnemyEffects.IsStandardReplika(target) || !target.agent
                || !target.hitbox || target.customSleep)
                continue;
            candidates++;
            if (string.IsNullOrEmpty(StockId(target)))
            {
                log($"reinforcement source without save id type={target.Preset.Type}");
                continue;
            }
            stock.Add(target);
        }
        if (stock.Count == 0)
        {
            log($"reinforcements room={current.roomName} native=0/{candidates} extra=0");
            return;
        }
        stock.Sort((a, b) => string.CompareOrdinal(StockId(a), StockId(b)));
        int budget = Mathf.Min(3, Mathf.CeilToInt(stock.Count * rate));
        int spawned = 0;
        bool occupied = stock.Exists(target => target.hitbox.HP > 0 &&
            target.state != EnemyController.enemystate.dead && !target.queuedForRespawn && !target.permadeath);
        foreach (var source in stock)
        {
            if (spawned == budget)
                break;
            var manager = source.GetComponentInParent<EnemyManager>();
            if (!manager || !manager.inOperation || manager.Kolibri || manager.enemies == null
                || !manager.enemies.Contains(source))
            {
                log($"reinforcement skipped type={source.Preset.Type} room={current.roomName} manager={!manager} inOperation={manager?.inOperation} kolibri={manager?.Kolibri} listed={manager?.enemies?.Contains(source)}");
                continue;
            }
            string id = ReinforcementId + StockId(source);
            bool saved = SProgress.GetInt("enemy " + id + " hp", -1) >= 0;
            if (!saved && (!occupied || source.hitbox.HP <= 0 || source.queuedForRespawn || source.permadeath))
                continue;
            Vector3 position = SProgress.GetVector("enemy " + id + " pos", source.transform.position);
            if (!saved && !TryPosition(source, player, out position))
                continue;
            Spawn(source, manager, position, id);
            spawned++;
        }
        log($"reinforcements room={current.roomName} native={stock.Count}/{candidates} extra={spawned}");
    }

    private static string StockId(EnemyController target)
    {
        string id = EnemyEffects.RecoveryOwner(target);
        return id != null && !id.StartsWith(ReinforcementId, StringComparison.Ordinal) ? id : null;
    }

    private bool TryPosition(EnemyController source, Vector3 player, out Vector3 position)
    {
        position = default;
        var origin = source.transform.position;
        var heading = Battlefield.OnPlane(origin - player).normalized;
        if (heading.sqrMagnitude < 0.1f)
            return false;
        var side = new Vector3(-heading.y, heading.x, 0f);
        var graph = AstarPath.active;
        var anchor = graph.GetNearest(origin);
        if (anchor.node == null || !anchor.node.Walkable)
            return false;

        int mask = Battlefield.RoomSolids | (source.character ? source.character.WallMask.value : 0);
        mask &= ~((1 << 11) | (1 << 12) | (1 << 13) | (1 << 17) | (1 << 18));
        for (int i = 0; i < 8; i++)
        {
            float angle = i * (float)Math.PI * 0.25f;
            var candidate = origin + (Mathf.Cos(angle) * heading + Mathf.Sin(angle) * side) * (i % 2 == 0 ? 5f : 6.6f);
            var nearest = graph.GetNearest(candidate);
            if (nearest.node == null || !nearest.node.Walkable || nearest.node.Area != anchor.node.Area
                || Battlefield.OnPlane(nearest.position - candidate).sqrMagnitude > 1.8f * 1.8f)
                continue;
            candidate = Battlefield.AtHeight(nearest.position, Battlefield.Height(origin));
            var probe = candidate + Battlefield.Up * 2.5f;
            if (!Physics.Raycast(probe, Battlefield.Down, out var floor, 3.2f, Battlefield.WalkableSurface,
                    QueryTriggerInteraction.Ignore) || Mathf.Abs(floor.point.z - origin.z) > 0.55f
                || Vector3.Dot(floor.normal, Battlefield.Up) < 0.7f
                || Physics.Linecast(origin + Battlefield.Up * 1.25f, candidate + Battlefield.Up * 1.25f,
                    mask, QueryTriggerInteraction.Ignore)
                || Physics.CheckCapsule(candidate + Battlefield.Up * 0.95f, probe, 0.65f, mask, QueryTriggerInteraction.Ignore)
                || (candidate - player).sqrMagnitude < 10f * 10f || Occupied(candidate))
                continue;
            position = candidate;
            return true;
        }
        log($"reinforcement no footing type={source.Preset.Type} room={room?.roomName}");
        return false;
    }

    private bool Occupied(Vector3 candidate)
    {
        const float clearanceSquared = 3.5f * 3.5f;
        return stock.Exists(actor => actor && (actor.transform.position - candidate).sqrMagnitude < clearanceSquared)
            || extras.Exists(extra => extra.Actor && (extra.Actor.transform.position - candidate).sqrMagnitude < clearanceSquared);
    }

    private void Spawn(EnemyController source, EnemyManager manager, Vector3 position, string id)
    {
        // Assign the new save identity while Unity has the whole copy inactive.
        var staging = new GameObject("REDLINE Reinforcement Setup");
        staging.SetActive(false);
        GameObject copy = null;
        EnemyController actor = null;
        try
        {
            copy = Object.Instantiate(source.gameObject, staging.transform);
            copy.SetActive(false);
            copy.name = "REDLINE " + source.Preset.Type;
            copy.GetComponent<UniqueId>().id = id;
            actor = copy.GetComponent<EnemyController>();
            actor.state = EnemyController.enemystate.sleep;
            actor.woken = false;
            actor.queuedForRespawn = false;
            actor.permadeath = false;
            actor.waypoints = null;
            actor.behaviour = EnemyController.idleBehaviour.idle;
            copy.transform.SetParent(source.transform.parent, true);
            copy.transform.SetPositionAndRotation(position, source.transform.rotation);
            copy.SetActive(true);
            if (manager.initPos != null)
                manager.initPos.Add(actor.transform, position);
            if (manager.initRot != null)
                manager.initRot.Add(actor.transform, actor.transform.localEulerAngles.y);
            manager.enemies.Add(actor);
            extras.Add((actor, manager));
            log($"reinforcement id={id} type={source.Preset.Type} position={Battlefield.Format(position)}");
        }
        catch
        {
            if (copy)
            {
                RemoveFromManager(actor, manager);
                copy.SetActive(false);
                Object.Destroy(copy);
            }
            throw;
        }
        finally
        {
            Object.Destroy(staging);
        }
    }

    public void Dispose()
    {
        foreach (var (actor, manager) in extras)
        {
            RemoveFromManager(actor, manager);
            if (actor) Object.Destroy(actor.gameObject);
        }
        extras.Clear();
    }

    private static void RemoveFromManager(EnemyController actor, EnemyManager manager)
    {
        if (!actor || !manager)
            return;
        manager.enemies?.Remove(actor);
        manager.dead?.Remove(actor);
        manager.initPos?.Remove(actor.transform);
        manager.initRot?.Remove(actor.transform);
    }
}
