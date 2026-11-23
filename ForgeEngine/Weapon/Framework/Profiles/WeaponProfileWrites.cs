using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>One request to put a named profile on one equipment instance for a bounded time: the instance, the
/// profile reference's own id, and the tick the change ends at. The writer identity travels with it because the
/// ledger merges several writers on one instance by `(plan, node, sequence)`, and that sequence is the profile
/// half's own per-application counter — not the dispatch tick, which two applications of one node inside one tick
/// would share — the same spelling the named rows use.
///
/// The three end-of-state switches travel with the request because they are decided at the swap: whether the
/// instance's in-progress burst, charge-up and firing are ended when the new profile takes over. They are the
/// card's own structural parameters and not a second kind of port, so a plan that set none of them swaps the
/// profile and leaves the instance's running state alone, exactly as it did before the ports existed.</summary>
internal sealed record WeaponProfileRequest(EntityReference Equipment, string ProfileId, long DurationTicks,
    string PlanId, string NodeId, long Tick, bool EndBurst = false, bool EndCharge = false, bool EndFiring = false);

/// <summary>What one profile write did. `Refused` is a request nothing was written for because the request itself
/// was not acceptable; `Failed` is a write that failed before any native value changed, so nothing was written
/// either but the cause is a failure; `Applied` is a change that is on the instance and named by a handle;
/// `Unconfirmed` is a write the native half attempted and could not read back, whose outcome nobody may claim —
/// not a refusal, which promises the instance was left alone, and not a success, which promises the values are on
/// it. The native half answers `Failed` and `Unconfirmed` only after it has given up the handle the request
/// minted, and `Unconfirmed` only after it has rolled the instance back to the state it last confirmed or kept a
/// reachable recovery entry, so the row can report `failed` without leaving a live name or an unreachable clone
/// behind.</summary>
internal enum WeaponProfileOutcome
{
    Refused,
    Failed,
    Applied,
    Unconfirmed
}

/// <summary>The one native half behind <see cref="WeaponProfileContract"/>, kept behind an interface so the row's
/// body holds no game type and a process without this package's native half can still declare the row and refuse
/// it by code.
///
/// The source owns the whole write because everything it needs is native: the profile block is a `GameData`
/// block, the values are its own members, and the handle that names the change is cast through the registration
/// the row was declared in. What the source is handed is therefore a decided request — the body has already
/// checked the ports, the parameter set and the duration — and what it answers is the handle the change is named
/// by, or the code of a failure or a refusal, or the code of a write whose outcome it could not read.</summary>
internal interface IWeaponProfileSource
{
    /// <summary>Puts one profile on one instance and answers what that write did: the handle that names the
    /// change, the code of a refusal, the code of a failure that wrote nothing, or the code of a write whose
    /// commit could not be observed. A refusal and a pre-write failure leave nothing written and no handle live;
    /// an unconfirmed write leaves no handle live either, and the instance back on the state this package last
    /// confirmed wherever the native half could put it.</summary>
    WeaponProfileOutcome TryApply(WeaponProfileRequest request, out RuntimeHandle handle, out string code);
}

/// <summary>The `property_profile` row's body. It is the same shape the debug-switch and gate rows of this
/// package use: a source the native half attaches for as long as its session is the live one, a thread the
/// source is only ever reached from, and a refusal by name whenever an answer would otherwise be invented.
///
/// What this half decides is everything that needs no game: which ports are present, whether the `profile`
/// parameter carries a reference of the kind the row declares, which of the three optional structural parameters
/// was set — each of them has no native destination on this build — and whether the duration is a whole,
/// non-negative number of ticks. A request that names no profile at all is refused here rather than resolved to
/// some default profile, because a profile this package picked would be a value the plan never asked for.</summary>
internal sealed class WeaponProfileWrites
{
    private readonly IWeaponProfileSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private WeaponProfileWrites(IWeaponProfileSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static WeaponProfileWrites? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the writes a session detaches with.</summary>
    public static WeaponProfileWrites Attach(IWeaponProfileSource source)
        => Installed = new WeaponProfileWrites(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the writes one attach returned, so a disposal cannot detach a machine that
    /// replaced it.</summary>
    public static void Detach(WeaponProfileWrites writes)
    {
        if (ReferenceEquals(Installed, writes)) Installed = null;
    }

    /// <summary>The handler table the registration carries: one name, the one the contract declares.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [WeaponProfileContract.HandlerName] = PropertyProfileRow
    };

    /// <summary>`forge.action.weapon.property_profile`: one profile, one instance, one handle. Every refusal that
    /// can be decided without the game is decided before the source is asked, so a rejected step never leaves a
    /// weapon half written. A write that failed before touching the instance is answered `failed` with no commit;
    /// one the native half performed and could not read back is answered `failed` with an unknown commit. Both
    /// carry the source's own code and no handle: the row may not claim the instance was left alone unless the
    /// failure proves it, and it may never claim the values are on it. The row carries the four fixed columns and
    /// nothing else — it is about one instance, so there is no count and no per-field echo to report.</summary>
    private static CommandResult PropertyProfileRow(CommandContext context)
    {
        if (Installed is not { } writes) return Outcome.Rejected(ResultCode.WeaponProfileSourceUnavailable);
        if (Environment.CurrentManagedThreadId != writes._threadId)
            return Outcome.Rejected(ResultCode.WeaponProfileSourceUnavailable).WithDetail("The weapon profile source is written off its own thread.");
        if (!context.TryEntity(WeaponProfileContract.EquipmentSlot, out var equipment))
            return Outcome.Rejected(ResultCode.MissingField).WithDetail("equipment");
        if (!context.TryResource(WeaponProfileContract.ProfileSlot, out var profile)
            || profile.ResourceKind != WeaponProfileContract.ProfileResourceKind || profile.ResourceId.Length == 0)
            return Outcome.Rejected(ResultCode.ProfileResourceUnsupported).WithDetail("profile");
        if (context.Integer(WeaponProfileContract.DurationSlot) is not { } duration || duration < 0)
            return Outcome.Rejected(ResultCode.ProfileDurationInvalid).WithDetail("duration");
        var request = new WeaponProfileRequest(equipment, profile.ResourceId, duration,
            context.PlanId ?? "", context.NodeId ?? "", context.SimulationTick,
            context.Boolean(WeaponProfileContract.EndBurstSlot) == true,
            context.Boolean(WeaponProfileContract.EndChargeSlot) == true,
            context.Boolean(WeaponProfileContract.EndFiringSlot) == true);
        switch (writes._source.TryApply(request, out var handle, out var code))
        {
            case WeaponProfileOutcome.Refused:
                return Outcome.Rejected(code);
            case WeaponProfileOutcome.Failed:
                context.Rows.Row(0, Status.Failed, Commit.None, code);
                return Outcome.Failed(code);
            case WeaponProfileOutcome.Unconfirmed:
                context.Rows.Row(0, Status.Failed, Commit.Unknown, code);
                return Outcome.FailedUnknown(code);
        }
        // The row and the handle are the step's two declared outputs, written side by side.
        context.Rows.Row(0, Status.Succeeded, Commit.Confirmed, ResultCode.ProfileApplied);
        context.Outputs.Handle(Blocks.ActionWeaponPropertyProfile.OutProfileHandle, handle);
        return Outcome.Succeeded();
    }
}
