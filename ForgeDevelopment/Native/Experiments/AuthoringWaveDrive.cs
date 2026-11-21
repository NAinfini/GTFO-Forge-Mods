using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;

namespace ForgeDevelopment.Native;

// Drive-session player protection and wave-member clearing. Protection skips every player's damage receive entry through ForgeCore's
// one player receive bracket (bullet, melee, explosion, tentacle, shooter projectile, fall; bots and remote players included) and
// refuses the game's own infection writes through ForgeCore's infection gate. It is held until released and counts what it blocked; clearing is a
// real kill (a lethal request through the game's damage entry) on every member of Mastermind's active groups, so the game itself
// retires the groups and the wave tracker's own "no group remains" fact sees the same path a player's kill takes.
internal static class AuthoringWaveDrive
{
    private const string Handle = "forge.development.drive-protection";
    private static IDisposable? _skip;
    private static int _blocked;
    private static double _blockedAmount;

    internal static string Protect()
    {
        if (_skip == null)
        {
            _blocked = 0; _blockedAmount = 0;
            _skip = ForgeCore.GameHooks.PlayerReceived.Skip(call => { _blocked++; _blockedAmount += call.Amount; return true; });
            ForgeCore.PlayerHits.GateInfection(Handle, (_, _) => ForgeCore.PlayerHits.AuthoredInfection);
        }
        return "drive-players-protected;" + Snapshot();
    }

    internal static string Unprotect() => _skip != null ? Release() : "drive-player-protection-absent";

    private static string Snapshot() => JsonSerializer.Serialize(new { blocked = _blocked, blockedAmount = _blockedAmount });

    private static string Release()
    {
        var snapshot = Snapshot();
        _skip?.Dispose(); _skip = null;
        ForgeCore.PlayerHits.GateInfection(Handle, null);
        return "drive-player-protection-released;" + snapshot;
    }
    internal static string ClearGroups()
    {
        var mastermind = Mastermind.Current ?? throw new InvalidOperationException("drive.mastermind-unavailable");
        var groups = mastermind.m_activeGroups ?? throw new InvalidOperationException("drive.active-groups-unavailable");
        var members = new List<EnemyAgent>();
        for (var i = 0; i < groups.Count; i++)
        {
            var list = groups[i]?.Members;
            for (var j = 0; list != null && j < list.Count; j++)
                if (list[j] != null && !list[j].WasCollected) members.Add(list[j]);
        }
        var player = ForgeCore.Players.Local;
        if (player == null || player.WasCollected || !player.Alive) throw new InvalidOperationException("drive.local-player-unavailable-or-stale");
        var killed = 0;
        foreach (var enemy in members)
        {
            if (!enemy.Alive) continue;
            AuthoringEnemyProbe.Kill(enemy, player);
            if (!enemy.WasCollected && !enemy.Alive) killed++;
        }
        return JsonSerializer.Serialize(new { groups = groups.Count, members = members.Count, killed });
    }
}

