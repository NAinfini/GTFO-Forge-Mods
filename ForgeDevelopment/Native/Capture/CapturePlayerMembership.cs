using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using Player;
using SNetwork;

namespace ForgeDevelopment.Native;

/// <summary>
/// Who is in PlayerManager.PlayerAgentsInLevel at each step of a player's spawn and despawn: before and after the
/// replication call, the manager's spawn and despawn hooks, and the list's own register and unregister. The record
/// sequence is the runtime call order. Each record carries the list as slot:human-or-bot, so whether an agent is
/// already listed when OnSpawn returns can be read straight off the records. No account id is written. Whether a
/// slot's account is stable across respawns is compared in memory, and only the verdict leaves the process
/// (weapon.no-steamid-in-logs).
/// </summary>
internal static class CapturePlayerMembership
{
    internal const string Kind = "player_membership";
    private static readonly Dictionary<int, ulong> LookupBySlot = new();
    private static long _sequence;
    private static IDisposable[]? _observation;

    /// <summary>Take the membership leases for this capture. A refused lease leaves the capture running without
    /// membership records and says so in the session, never silently.</summary>
    internal static void Start()
    {
        Stop();
        LookupBySlot.Clear(); // the first/same/changed verdict is per capture
        try
        {
            _observation = CapturePlayerMembershipObservation.Start();
        }
        catch (Exception error)
        {
            CaptureRegistry.Log("membership:subscribe", error);
            RecSession.Note("player_membership_unavailable", json => json.WriteString("reason", error.GetType().Name + ": " + error.Message));
        }
    }

    internal static void Stop()
    {
        var observation = _observation; _observation = null;
        if (observation != null) foreach (var lease in observation) lease.Dispose();
    }

    internal static void Record(string hook, string phase, PlayerAgent? agent, SNet_Player? player, string? characterIndex, bool compareLookup)
    {
        if (!RecSession.Active) return;
        try
        {
            var members = Members(out var count);
            var slot = Safe(() => agent != null ? agent.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)
                : player != null ? player.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture) : "none");
            var bot = Safe(() => agent != null ? (agent.Owner == null ? "unknown" : agent.Owner.IsBot.ToString())
                : player != null ? player.IsBot.ToString() : "none");
            var inList = agent == null ? "n/a" : Safe(() => Contains(agent).ToString());
            var lookup = compareLookup ? Safe(() => CompareLookup(agent)) : null;
            var sequence = ++_sequence;
            RecSession.Write(CaptureStateWriter.Channel, Kind, json =>
            {
                json.WriteString("hook", hook);
                json.WriteString("phase", phase);
                json.WriteNumber("seq", sequence);
                json.WriteNumber("count", count);
                json.WriteString("members", members);
                json.WriteString("subjectSlot", slot);
                json.WriteString("subjectBot", bot);
                json.WriteString("subjectInList", inList);
                if (characterIndex != null) json.WriteString("characterIndex", characterIndex);
                if (lookup != null) json.WriteString("lookupSinceLastRegister", lookup);
            });
        }
        catch (Exception error) { CaptureRegistry.Log("membership:" + hook + ":" + phase, error); }
    }

    /// <summary>The spawn-data variant. It reads only the character index; the spawn data's player member is the
    /// Steam account and is never touched.</summary>
    internal static void Spawn(string hook, string phase, pPlayerSpawnData spawnData)
    {
        string? characterIndex;
        try { characterIndex = spawnData.characterIndex.ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { characterIndex = "unreadable"; } // Il2Cpp read guard: the recorded value is the explicit text "unreadable".
        Record(hook, phase, null, null, characterIndex, false);
    }

    /// <summary>The list in its own order as slot:h or slot:b. A null entry stays in place, because its position
    /// is part of the answer.</summary>
    private static string Members(out int count)
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null)
        {
            count = -1;
            return "unavailable";
        }
        count = agents.Count;
        var text = new StringBuilder();
        for (var i = 0; i != agents.Count; i++)
        {
            if (i != 0) text.Append(',');
            var agent = agents[i];
            if (agent == null || agent.WasCollected)
            {
                text.Append("null");
                continue;
            }
            text.Append(agent.PlayerSlotIndex.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(agent.Owner == null ? "?" : agent.Owner.IsBot ? "b" : "h");
        }
        return text.ToString();
    }

    private static bool Contains(PlayerAgent agent)
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        if (agents == null) return false;
        for (var i = 0; i != agents.Count; i++)
        {
            var listed = agents[i];
            if (listed != null && listed.Pointer == agent.Pointer) return true;
        }
        return false;
    }

    /// <summary>Whether the account behind a slot is the one registered there last time. The id stays in this
    /// dictionary; the record carries only first, same or changed.</summary>
    private static string CompareLookup(PlayerAgent? agent)
    {
        if (agent == null || agent.Owner == null) return "no-owner";
        var slot = agent.PlayerSlotIndex;
        var lookup = agent.Owner.Lookup;
        var verdict = !LookupBySlot.TryGetValue(slot, out var previous) ? "first" : previous == lookup ? "same" : "changed";
        LookupBySlot[slot] = lookup;
        return verdict;
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception error) { return "error:" + error.GetType().Name; }
    }
}

/// <summary>The three replication boundaries are ForgeCore's shared membership brackets (several packages read them), so
/// Development is a read-only subscriber: it never patches them. Before records see the list before the native body; After
/// records see it as the body left it.</summary>
internal static class CapturePlayerMembershipObservation
{
    internal static IDisposable[] Start()
    {
        const string spawn = "PlayerReplicationManager.OnSpawn", despawn = "PlayerReplicationManager.OnDeSpawn",
            spawned = "PlayerManager.OnPlayerSpawned";
        return new[]
        {
            ForgeCore.GameHooks.PlayerReplicationSpawned.Before(data => CapturePlayerMembership.Spawn(spawn, "before", data)),
            ForgeCore.GameHooks.PlayerReplicationSpawned.After((data, _) => CapturePlayerMembership.Spawn(spawn, "after", data)),
            ForgeCore.GameHooks.PlayerReplicationDespawned.Before(_ => CapturePlayerMembership.Record(despawn, "before", null, null, null, false)),
            ForgeCore.GameHooks.PlayerReplicationDespawned.After((_, _) => CapturePlayerMembership.Record(despawn, "after", null, null, null, false)),
            ForgeCore.GameHooks.PlayerManagerSpawned.Before(data => CapturePlayerMembership.Spawn(spawned, "before", data)),
            ForgeCore.GameHooks.PlayerManagerSpawned.After((data, _) => CapturePlayerMembership.Spawn(spawned, "after", data))
        };
    }
}
/// <summary>The player-list boundary of <c>PlayerManager.OnPlayerDespawned</c>, subscribed by <see cref="DevelopmentGameHooks"/> to ForgeCore's
/// one patch of it: <c>Before</c> sees the list before every other patch, <c>After</c> after every other patch.</summary>
internal static class MembershipPlayerDespawned
{
    internal static void Before(SNet_Player snetPlayer) => CapturePlayerMembership.Record("PlayerManager.OnPlayerDespawned", "before", null, snetPlayer, null, false);

    internal static void After(SNet_Player snetPlayer) => CapturePlayerMembership.Record("PlayerManager.OnPlayerDespawned", "after", null, snetPlayer, null, false);
}

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.RegisterPlayerAgent))]
internal static class MembershipRegistered
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerAgent agent) => CapturePlayerMembership.Record("PlayerManager.RegisterPlayerAgent", "before", agent, null, null, false);

    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void Postfix(PlayerAgent agent) => CapturePlayerMembership.Record("PlayerManager.RegisterPlayerAgent", "after", agent, null, null, true);
}

[HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.UnregisterPlayerAgent))]
internal static class MembershipUnregistered
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerAgent agent) => CapturePlayerMembership.Record("PlayerManager.UnregisterPlayerAgent", "before", agent, null, null, false);

    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    private static void Postfix(PlayerAgent agent) => CapturePlayerMembership.Record("PlayerManager.UnregisterPlayerAgent", "after", agent, null, null, false);
}
