using ForgeEngine.Framework;

namespace ForgeMap;

/// <summary>The cards of round-based objectives: three facts the native uplink and timed-terminal machines decide, and
/// two requests. `reactor_verify` asks the reactor's own verification entry; `timed_round_time` moves the native round's
/// own start stamp, so the native timer, HUD and failure stay the only clock.</summary>
internal static class ObjectiveRoundContract
{
    public const string RoundCompletedFact = "objective.round_completed";
    public const string TimedRoundStartedFact = "objective.timed_round_started";
    public const string TimedRoundFailedFact = "objective.timed_round_failed";

    public const string ReactorVerifyHandler = "gtfo.map.reactor_verify";
    public const string TimedRoundTimeHandler = "gtfo.map.timed_round_time";

    public static readonly HandlerShape ReactorVerifyShape = new HandlerShape()
        .Inputs("objectives").Outputs("result").Parameters("chain");
    public static readonly HandlerSlot ReactorVerifyObjectives = ReactorVerifyShape.InputSlot("objectives");
    public static readonly HandlerSlot ReactorVerifyChain = ReactorVerifyShape.ParameterSlot("chain");

    public static readonly HandlerShape TimedRoundTimeShape = new HandlerShape()
        .Inputs("objectives", "seconds", "minimum_seconds").Outputs("result").Parameters("chain");
    public static readonly HandlerSlot TimedRoundTimeObjectives = TimedRoundTimeShape.InputSlot("objectives");
    public static readonly HandlerSlot TimedRoundTimeSeconds = TimedRoundTimeShape.InputSlot("seconds");
    public static readonly HandlerSlot TimedRoundTimeMinimum = TimedRoundTimeShape.InputSlot("minimum_seconds");
    public static readonly HandlerSlot TimedRoundTimeChain = TimedRoundTimeShape.ParameterSlot("chain");

    public static string Binding(string capabilityId) => LevelEventContract.Binding(capabilityId);

    public static ProviderBinding[] DeclareBindings() => new[]
    {
        Observe(Blocks.TriggerObjectiveRoundCompleted.Id), Observe(Blocks.TriggerObjectiveTimedRoundStarted.Id),
        Observe(Blocks.TriggerObjectiveTimedRoundFailed.Id),
        new ProviderBinding(Binding(Blocks.ActionMapReactorVerify.Id), Blocks.ActionMapReactorVerify.Id, ReactorVerifyHandler, "execute",
            new[] { ObjectiveActionContract.ObjectiveWritePermission }, ReactorVerifyShape),
        new ProviderBinding(Binding(Blocks.ActionMapTimedRoundTime.Id), Blocks.ActionMapTimedRoundTime.Id, TimedRoundTimeHandler, "execute",
            new[] { LevelEventContract.ObjectiveWritePermission }, TimedRoundTimeShape)
    };

    private static ProviderBinding Observe(string capability)
        => new(Binding(capability), capability, LevelEventContract.HandlerName(capability), "observe", new[] { LevelEventContract.ObjectiveReadPermission });
}
