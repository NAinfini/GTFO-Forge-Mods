using System;
using System.Collections.Generic;
using GameData;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the three action rows: the one native entry each of them reaches, and the
/// `WardenObjectiveEventData` struct that entry takes. Every action here is a level event, so the struct's own
/// `Type` member selects the effect and the game's own executor carries it to every client — the same call the
/// vanilla mount points use. Nothing writes the state the event owns, which is what keeps a client's world
/// consistent: the executor is the only writer, exactly as it is for a level's own event list.
///
/// The event types below are `eWardenObjectiveEventType` members read from the build (20403457):
/// `DimensionFlashTeam` 7, `AddToTimer` 24, `ResetTimer` 25, `WinOnDeath` 26,
/// `ForceInstantWin` 27, `ClearDimension` 30.
///
/// Every check a request can be refused by lives in the game-independent half, so the refusal a plan sees is
/// decided in one place; the native call only happens once a request is known to be legal. A call that throws is
/// reported as an unknown commit — the executor may already have written the event's replicated state before it
/// threw, and claiming nothing happened is a claim this layer cannot make.</summary>
internal static class LevelEventActions
{
    /// <summary>`GS_InLevel.FAIL_COMMAND_CODE`, build 20403457.</summary>
    private const int FailCommandCode = 1;

    /// <summary>The countdown row: `add` carries the ticks the plan supplied — the module converts them to the
    /// native `Duration`'s seconds at the rate the running world reported — and `reset` carries none. Both go
    /// through the world event executor rather than the objective manager's own setter, because the executor is
    /// the entry the game's own data uses and it treats an authored event and this one the same way.
    ///
    /// The layer comes from the row's own recipient — the `objectives` reference the plan wired, which the
    /// game-independent half has already read and checked — and is written into the event's `Layer` member. The
    /// executor moves the layer the event names, so the row's `target` is the objective this call really
    /// addressed instead of a second, independent choice the write would have ignored.</summary>
    internal static CommandResult Timer(CommandContext context)
    {
        var module = MapHost.Session?.LevelEvents;
        if (module == null) return Unavailable();
        return module.ExecuteTimer(context,
            (layer, seconds) => Execute(Event(eWardenObjectiveEventType.AddToTimer, data =>
            {
                data.Layer = (LG_LayerType)layer;
                data.Duration = seconds;
            })),
            layer => Execute(Event(eWardenObjectiveEventType.ResetTimer, data => data.Layer = (LG_LayerType)layer)));
    }

    /// <summary>The dimension row: native `flash` moves the complete named team and `clear` empties a dimension. The
    /// dimension is the `DimensionIndex` field, and the mode alone selects the event type: `ClearDimension` is the
    /// event that empties a dimension, so the row's own `clear` flag is deleted and read nowhere.</summary>
    internal static CommandResult Dimension(CommandContext context)
    {
        var session = MapHost.Session;
        var module = session?.LevelEvents;
        if (session == null || module == null) return Unavailable();
        return module.ExecuteDimension(context, (mode, dimension) => Execute(Event(
            mode == "clear" ? eWardenObjectiveEventType.ClearDimension : eWardenObjectiveEventType.DimensionFlashTeam,
            data => data.DimensionIndex = (eDimensionIndex)dimension)),
            mode => ValidateDimensionPlayers(session.Players, context, mode));
    }

    /// <summary>Native flash addresses the complete team; clear addresses the explicit dimension.
    /// Do not turn a subset or a stale life into a team request.</summary>
    internal static string? ValidateDimensionPlayers(IPlayerDirectory players, CommandContext context, string mode)
    {
        try { return ValidateDimensionPlayersCore(players, context, mode); }
        catch (RuntimeContractException error) { return error.Code; }
    }

    private static string? ValidateDimensionPlayersCore(IPlayerDirectory players, CommandContext context, string mode)
    {
        var targets = new List<EntityReference>();
        int count = context.Count(LevelEventContract.DimensionPlayers);
        if (count < 0) return ResultCode.MissingField;
        for (int index = 0; index < count; index++)
        {
            if (!context.TryEntity(LevelEventContract.DimensionPlayers, index, out var reference))
                return ResultCode.DimensionPlayerInvalid;
            if (reference.WorldEpoch != context.WorldEpoch || players.AgentOf(reference) == null)
                return ResultCode.StaleEntity;
            if (targets.Contains(reference)) return ResultCode.DimensionPlayerInvalid;
            targets.Add(reference);
        }
        if (mode == "flash")
        {
            var addressed = players.SessionsOf(targets);
            var all = players.EverySession();
            if (addressed == null || all == null) return ResultCode.DimensionPlayerInvalid;
            var sessions = new HashSet<string>(addressed, StringComparer.Ordinal);
            if (!sessions.SetEquals(all)) return ResultCode.DimensionTeamRequired;
        }
        return null;
    }

    /// <summary>The expedition-end row: `instant_win` ends the expedition now, `win_on_death` makes the next wipe
    /// the win the objective's own completion check looks for. `fail` has no level event: it is the session command
    /// `GS_InLevel` itself sends when `CheckExpeditionFailed` answers true (`TryEndPlaying`, `FAIL_COMMAND_CODE` 1), so the
    /// game's own failure path runs on every machine; a command the session refuses is a failed commit.</summary>
    internal static CommandResult ExpeditionEnd(CommandContext context)
    {
        var module = MapHost.Session?.LevelEvents;
        if (module == null) return Unavailable();
        return module.ExecuteExpeditionEnd(context, ending =>
        {
            if (ending == "fail")
            {
                if (!SNet.Sync.SessionCommand(eSessionCommandType.TryEndPlaying, FailCommandCode))
                    throw new LevelEventCommitException(new InvalidOperationException("session refused the failure command"));
                return;
            }
            Execute(Event(ending == "win_on_death" ? eWardenObjectiveEventType.WinOnDeath : eWardenObjectiveEventType.ForceInstantWin));
        });
    }

    /// <summary>The one entry every action above reaches: the game's own level-event executor, with the delay the
    /// plan's own step already applied as zero and the struct passed by value, which is what the interop signature
    /// takes. A call that throws is reported as a failed commit through the game-independent half, because the
    /// executor may already have written the event's replicated state and claiming nothing happened is a claim
    /// this layer cannot make.</summary>
    private static void Execute(WardenObjectiveEventData data)
    {
        try { WorldEventManager.ExecuteEvent(data, 0f); }
        catch (Exception error) { throw new LevelEventCommitException(error); }
    }

    private static WardenObjectiveEventData Event(eWardenObjectiveEventType type) => new() { Type = type };

    private static WardenObjectiveEventData Event(eWardenObjectiveEventType type, Action<WardenObjectiveEventData> setup)
    {
        var data = new WardenObjectiveEventData { Type = type };
        setup(data);
        return data;
    }

    private static CommandResult Unavailable() => LevelEventModule.Refused(ResultCode.MapSessionUnavailable);

    /// <summary>The one failure the native call can produce: the executor was reached and did not return
    /// normally. It is its own type so the game-independent half can tell "the world refused to write" from a
    /// mistake in argument handling, which would be a programming error rather than a commit outcome.</summary>
    private sealed class LevelEventCommitException : Exception
    {
        internal LevelEventCommitException(Exception inner) : base("level event commit failed", inner) { }
    }
}
