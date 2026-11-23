using System;
using System.Collections.Generic;
using Agents;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    // Only changed refused reads are retained, and a recovery or world boundary releases them.
    private readonly Dictionary<ushort, (IntPtr Pointer, string Reason)> _identityRefusals = new();

    private void LogIdentityResolution(Enemies.EnemyAgent enemy, Entry? entry, string reason)
    {
        if (!Log.Logs(RuntimeLogLevel.Info)) return;
        var id = enemy.GlobalID;
        if (reason == "resolved")
        {
            if (_identityRefusals.Count == 0 || !_identityRefusals.Remove(id)) return;
        }
        else
        {
            var failure = (enemy.Pointer, reason);
            if (_identityRefusals.TryGetValue(id, out var previous) && previous == failure) return;
            _identityRefusals[id] = failure;
        }
        Log.Write(RuntimeLogLevel.Info, "enemy.identity-resolution", new RuntimeLogFacts
        {
            Entity = entry?.Reference.Id,
            Values = new[]
            {
                RuntimeLogValue.Quote("phase", reason == "resolved" ? "resolved" : "refused"),
                RuntimeLogValue.Quote("reason", reason), RuntimeLogValue.Of("nativeId", (long)id),
                RuntimeLogValue.Quote("nativePointer", enemy.Pointer.ToString("x")),
                RuntimeLogValue.Quote("trackedPointer", entry?.EnemyPointer.ToString("x") ?? ""),
                RuntimeLogValue.Of("trackedWorld", entry?.Reference.WorldEpoch ?? -1),
                RuntimeLogValue.Of("trackedLife", entry?.Reference.LifeEpoch ?? -1),
                RuntimeLogValue.Of("currentWorld", _kernel.WorldEpoch),
                RuntimeLogValue.Quote("startup", _kernel.StartupState.ToString()),
                RuntimeLogValue.Of("registered", IsRegistered), RuntimeLogValue.Of("tracked", (long)_entities.Count)
            }
        });
    }

    private static readonly RuntimeContractException MissingPlayerDirectory = new(PlayerDirectoryService.MissingCode,
        "The Player domain is not running.");
    private bool _playerDirectoryMissingNoted;

    /// <summary>Resolve the role the request actually names, through the owner of that exact life.</summary>
    internal Agent? NativeAgentOf(EntityReference reference)
    {
        if (EntityKinds.Is(reference, EntityKinds.Enemy)) return Resolve(reference)?.Enemy;
        if (!EntityKinds.Is(reference, EntityKinds.Player)) return null;
        if (ForgePlayer.Native.PlayerHost.Players is not { } players) throw MissingPlayerDirectory;
        var agent = players.AgentOf(reference);
        return agent != null && players.ReferenceOf(agent) == reference ? agent : null;
    }

    private static IntPtr NativeAgentPointer(Agent agent)
        => agent.TryCast<Enemies.EnemyAgent>() is { } enemy ? enemy.Pointer : agent.Pointer;
    /// <summary>One native agent through the two owners that can name it: the enemy kind through the kernel's
    /// instance resolver — which this module registers itself, so the kind always has one and no per-call probe for
    /// a missing resolver is needed — and a player through Runtime's registered player resolver. No kind is ever guessed
    /// from a pointer. An instance neither owner claims stays unresolved.</summary>
    private EntityReference? ResolveAgent(Agent? agent)
    {
        if (agent == null) return null;
        return _kernel.ResolveEntityInstance(EntityKinds.Enemy, agent) ?? ResolvePlayer(agent);
    }

    /// <summary>A native agent through the player kind's own resolver only: the ports that name a player.</summary>
    private EntityReference? ResolvePlayer(Agent? agent)
    {
        global::Player.PlayerAgent? player;
        try { player = agent?.TryCast<global::Player.PlayerAgent>(); }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
        return ResolvePlayer(player);
    }

    /// <summary>A player agent through the Runtime route to the one owner of player identity: the life
    /// the agent is recorded under right now, or null for an agent that is not a current player life.</summary>
    private EntityReference? ResolvePlayer(global::Player.PlayerAgent? player)
    {
        if (player == null) return null;
        if (ForgePlayer.Native.PlayerHost.Players is not { } players)
        {
            if (!_playerDirectoryMissingNoted)
            {
                _playerDirectoryMissingNoted = true;
                NativeFault.Note("enemy.player-directory-missing", MissingPlayerDirectory);
            }
            return null;
        }
        _playerDirectoryMissingNoted = false;
        return players.ReferenceOf(player);
    }

    /// <summary>The inflictor the game registered for the hit that just landed, resolved to the entity of
    /// whichever registered kind owns that agent. Read twice and compared by native pointer: each interop read
    /// of the field mints a fresh wrapper for the same agent, so the identity that has to hold is the pointer
    /// underneath, and a field another callback in this same chain is still writing is not reported. The health
    /// window of the same call reads it through this one member, so a fact of either window names the attacker the
    /// receiver itself registered instead of each half reading the field its own way.</summary>
    private EntityReference? ReadInflictor(Entry entry)
    {
        Agent? first, second;
        try { first = entry.Enemy.LastDamageInflictor; second = entry.Enemy.LastDamageInflictor; }
        catch (Exception caught) { EnemyLog.Contained(caught); return null; }
        if (first == null || second == null || first.Pointer != second.Pointer) return null;
        return ResolveAgent(first);
    }

}
