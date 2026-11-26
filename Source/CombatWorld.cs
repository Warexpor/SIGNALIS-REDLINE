using System;
using System.Collections.Generic;
using UnityEngine;

namespace Redline;

internal static class CombatWorld
{
    private static System.Reflection.PropertyInfo networkInstance, networkRole;
    private static bool checkedNetwork;
    private static int networkFrame = -1;
    private static bool networked;
    private static readonly List<AraNest> bossNests = new(8);

    public static void RefreshBossNests()
    {
        bossNests.Clear();
        foreach (var boss in UnityEngine.Object.FindObjectsOfType<MED_MynahBoss>(true))
        {
            AddNest(boss.leftNest);
            AddNest(boss.rightNest);
        }
        foreach (var boss in UnityEngine.Object.FindObjectsOfType<END_Boss>(true))
        {
            AddNest(boss.MedMinionA);
            AddNest(boss.MedMinionB);
            AddNest(boss.LabMinionA);
            AddNest(boss.LabMinionB);
            AddNest(boss.LabMinionC);
            AddNest(boss.LabMinionD);
        }

        void AddNest(AraNest nest)
        {
            if (nest && !bossNests.Contains(nest))
                bossNests.Add(nest);
        }
    }

    public static bool IsBossMinion(EnemyController target)
    {
        foreach (var nest in bossNests)
            if (nest && nest.AraLogic == target)
                return true;
        return false;
    }

    public static bool Playing => PlayerState.player && PlayerState.gameState == PlayerState.gameStates.play
        // The menu flag can stay set after gameplay resumes, so it must not gate live systems.
        && !PlayerState.noplayer && !PlayerState.paused && !PlayerState.pauseMenu
        && !PlayerState.cutscene && !PlayerState.eventScreen && !PlayerState.gameOver;

    public static bool Networked
    {
        get
        {
            if (networkFrame == Time.frameCount)
                return networked;
            if (!checkedNetwork)
            {
                var type = Type.GetType("SyncRADation.Networking.LanNetworkManager, SyncRADation");
                networkInstance = type?.GetProperty("Instance");
                networkRole = type?.GetProperty("Role");
                checkedNetwork = true;
            }
            var network = networkInstance?.GetValue(null);
            networked = network != null && networkRole?.GetValue(network)?.ToString() is "Host" or "Client";
            networkFrame = Time.frameCount;
            return networked;
        }
    }

    public static string Describe()
    {
        bool active = Networked;
        object instance = null;
        object role = null;
        try
        {
            instance = networkInstance?.GetValue(null);
            if (instance != null)
                role = networkRole?.GetValue(instance);
        }
        catch { }
        return $"instance={instance != null} role={role} networked={active}";
    }

    public static string DescribePlaying()
    {
        var state = PlayerState.gameState;
        return $"player={PlayerState.player != null} state={state} noplayer={PlayerState.noplayer} paused={PlayerState.paused} pauseMenu={PlayerState.pauseMenu} menu={PlayerState.menu} cutscene={PlayerState.cutscene} eventScreen={PlayerState.eventScreen} gameOver={PlayerState.gameOver}";
    }
}
