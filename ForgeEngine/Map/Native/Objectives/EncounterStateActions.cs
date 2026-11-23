using System;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the two reads of an encounter that is already running: whether a raised alarm is still active
/// and whether a started survival wave is still being spawned.
///
/// Both name their subject by an effect handle of this provider, which is how the Map family already names a
/// running thing: <see cref="AlarmWaveActions"/> mints one per wave it starts and <see cref="AlarmActions"/> mints
/// one per alarm it raises. The kernel's own pool is what tells a live handle from a released one or from one of an
/// ended world.
/// </summary>
internal static class EncounterStateActions
{
    // ---- result vocabulary ---------------------------------------------------------------------------

    /// <summary>The refusals the two read rows answer with. A read is a `query` step, so it refuses by throwing
    /// the runtime's own contract exception rather than by writing a result row.</summary>
    internal const string MissingInputCode = "missing-field";
    internal const string AlarmNotLiveCode = "alarm-handle-not-live";
    /// <summary>The handle is this provider's own and still live, but the puzzle it named is no longer in the
    /// level — the door or gate the alarm was raised on has been removed. It is its own code because it is a
    /// different problem from an unwired or released handle: the plan is right and the world moved on.</summary>
    internal const string AlarmInstanceGoneCode = "alarm-instance-gone";
    internal const string WaveNotLiveCode = "wave-handle-not-live";
    internal const string ReadUnavailableCode = "encounter-read-unavailable";

    /// <summary>The registration the two rows resolve handles through.</summary>
    private static RuntimeModuleHandle? _registration;

    /// <summary>Attaches the registration after `RegisterModule` returned, because the handles resolve through it.</summary>
    internal static void Attach(RuntimeModuleHandle registration) => _registration = registration ?? throw new ArgumentNullException(nameof(registration));

    /// <summary>Drops the registration, so a session that is going away cannot leave a released one for a later session.</summary>
    internal static void Detach() => _registration = null;

    // ---- the two read rows ---------------------------------------------------------------------------

    /// <summary>`forge.query.map.alarm_state`: whether the alarm the handle names is still active, read from the
    /// puzzle's own replicated state. `ChainedPuzzleInstance.get_IsActive` (RVA 0x1736950) loads the instance's
    /// `m_stateReplicator` and returns the byte at its +0x44, which is the first byte of the `pChainedPuzzleState`
    /// that replicator carries — `isActive`; `get_IsSolved` returns the byte beside it at +0x45, which is
    /// `isSolved`. So the game's own accessor *is* the replicated-state read, and the frame here names no second
    /// alarm ledger: the instance the handle resolves to is the one the write side started, and its `IsActive` is
    /// `pChainedPuzzleState.isActive`. Reading the struct through `GetStateReplicator()` instead is not possible
    /// from this build's interop: that entry answers the non-generic `iSNet_StateReplicator`, whose only member is
    /// the replicator itself. The two failure answers are kept apart — a handle the row cannot resolve at all, and
    /// a live handle whose instance the level no longer holds.</summary>
    internal static void AlarmState(EvaluationContext context, EvaluationOutputs outputs) => outputs.Boolean(0, AlarmActive(context));

    internal static bool AlarmActive(EvaluationContext context)
    {
        var handle = Input(context, "alarm");
        if (_registration == null)
            throw new RuntimeContractException(ReadUnavailableCode, "No Map registration is attached.");
        switch (AlarmActions.TryPuzzle(handle, out var instance))
        {
            case AlarmActions.Binding.Unknown:
                throw new RuntimeContractException(AlarmNotLiveCode, "The handle names no live alarm of this provider.");
            case AlarmActions.Binding.Gone:
                throw new RuntimeContractException(AlarmInstanceGoneCode,
                    "The instance this alarm handle named is no longer in the level.");
        }
        return instance!.IsActive;
    }

    /// <summary>`forge.query.map.wave_state`: whether the Mastermind still holds the event the handle names. The
    /// master's own event register is the one place a running survival wave lives, so a wave that finished, was
    /// stopped or never registered answers false through the same read.</summary>
    internal static void WaveState(EvaluationContext context, EvaluationOutputs outputs) => outputs.Boolean(0, WaveActive(context));

    internal static bool WaveActive(EvaluationContext context)
    {
        var handle = Input(context, "wave");
        if (_registration is not { } registration)
            throw new RuntimeContractException(ReadUnavailableCode, "No Map registration is attached.");
        var native = registration.TryNative(handle, out var found) ? found : null;
        if (native is not NativeWaveLifetime binding)
        {
            // The kernel gives a wave's handle back when the wave ends, so a handle of this world that resolves to nothing names
            // a wave that is no longer running: that is the answer to this read. A handle of another world stays refused.
            if (native == null && handle.WorldEpoch == context.Query.WorldEpoch) return false;
            throw new RuntimeContractException(WaveNotLiveCode, "The handle names no wave event.");
        }
        var master = Mastermind.Current;
        if (master == null || master.WasCollected)
            throw new RuntimeContractException(ReadUnavailableCode, "No Mastermind is loaded.");
        // A reused EventID can already name a new allocation at the same pointer before the next sweep.
        // Validate the pinned run before reading any field through the old native wrapper. A run is
        // active while any of its member events is.
        return binding.IsLive();
    }

    /// <summary>One required handle input as its value, refused by name when the plan left it out: an absent handle
    /// answered as "not active" would read exactly like an alarm that really is down. The query context hands its
    /// inputs over as the port document, so the wire form is read into the value here, once, at that boundary.</summary>
    private static RuntimeHandle Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && RuntimeHandle.TryRead(value, out var handle)
            ? handle
            : throw new RuntimeContractException(MissingInputCode, port);
}
