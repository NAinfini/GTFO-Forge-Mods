using System;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;

namespace ForgeWeapon.Native;

/// <summary>
/// The holder provider's registration and its two handlers: the executable half of
/// <see cref="WeaponHolderChannelContract"/>.
///
/// The provider is its own module rather than a set of rows on <see cref="ModuleDefinition.ProviderId"/>, because
/// the two facts that make an `owner` row executable — the execution tier and the owner-session resolver — are
/// registration facts of the module that owns the binding. One provider, one resolver table, one tier: a step
/// whose holder cannot be named is refused by the tier with <c>owner-holder</c>, and nothing is sent anywhere.
///
/// A handler here runs on the holder's machine and nowhere else. The kernel invokes it through its own owner
/// entry point, which is the same invocation path a host-tier handler goes through with one difference:
/// <see cref="CommandContext.IsHost"/> is false, because the advance that decided the timing ran on another
/// machine. Both handlers refuse a command that arrives while this machine is not the addressed holder, which is
/// the check that keeps a reload from ever landing on the wrong inventory.
/// </summary>
internal sealed class WeaponHolderModule : IDisposable
{
    private readonly IForgeEngine _kernel;
    /// <summary>ForgePlayer's player directory: which session a holder is on, and whether it is this machine's.</summary>
    private readonly IPlayerDirectory _players;
    private readonly Action<string> _report;
    /// <summary>The info sink. A fire the game's own Update declines (cooldown, held input) is the designed answer of the row,
    /// returned to its plan as a rejected result with the code; the line here is a note, never a console error.</summary>
    private readonly Action<string> _note;
    private readonly RuntimeModuleHandle _registration;
    private bool _disposed;

    private WeaponHolderModule(IForgeEngine kernel, RuntimeLogLevel logLevel, IPlayerDirectory players, Action<string> report,
        Action<string> note)
    {
        _kernel = kernel;
        _players = players;
        _report = report;
        _note = note;
        _registration = kernel.RegisterModule(Build(), logLevel);
    }

    /// <summary>Registers the holder provider. The caller owns the returned module and disposes it at unload.</summary>
    internal static WeaponHolderModule Register(IForgeEngine kernel, RuntimeLogLevel logLevel, IPlayerDirectory players,
        Action<string> report, Action<string> note)
        => new(kernel ?? throw new ArgumentNullException(nameof(kernel)), logLevel,
            players ?? throw new ArgumentNullException(nameof(players)),
            report ?? throw new ArgumentNullException(nameof(report)),
            note ?? throw new ArgumentNullException(nameof(note)));

    /// <summary>The registration itself, built by the contract from the two handler bodies and the one resolver
    /// this machine can answer: the rows, their bindings, their shapes and their owner-session table all come from
    /// the same place, so a declared row can never lack the resolver its tier needs.</summary>
    private RuntimeModule Build()
        => WeaponHolderActionsContract.Module(HolderSession, Reload, ClipSet, AutoFire);

    /// <summary>The owner tier's resolver: the session of the player this package says holds the instance, in
    /// ForgePlayer's one spelling. Null — nobody this machine can name — is refused by the tier.</summary>
    private string? HolderSession(EntityReference equipment) => _players.SessionOf(WeaponHolderChannel.HolderOf(equipment));

    /// <summary>Whether a request is this machine's to execute: the holder's session is this machine's own local
    /// player's. A request for a client's weapon arriving on the host — or the reverse — is refused instead of
    /// executed against the wrong inventory.</summary>
    private bool Addressed(EntityReference equipment) => _players.IsLocal(HolderSession(equipment));

    /// <summary>`forge.action.weapon.reload`. Refusals, in the order they are checked: a command that is not
    /// addressed here, an equipment instance this machine cannot see, an equipment that is not the held item, and
    /// the game's own reload check. The result's `clip` is the magazine read right after the game opened the
    /// reload, before the refill lands at the reload's end, so it is what the weapon held when the reload began, not
    /// the refilled count.</summary>
    private CommandResult Reload(CommandContext context)
    {
        if (!Read(context, WeaponHolderActionsContract.ReloadEquipmentSlot, out var equipment, out var failure))
            return failure!;
        if (!Addressed(equipment))
            return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        // `speed_multiplier` is the reload row's own modifier: absent means the reload takes the weapon's own
        // time, and a written multiplier is bounded by the row's `cap` before the instance's block is scaled.
        var multiplier = SpeedMultiplier(context);
        if (!WeaponHolderChannel.TryReload(_players, equipment, multiplier.HasValue, multiplier ?? 1d, out var clip, out var code))
        {
            // Runtime records the command's native refusal. It is not a package startup failure.
            if (code != ResultCode.NativeCommitException) return Outcome.Rejected(code);
            // The game's reload entry threw part-way: whether a reload opened is unknown.
            context.Rows.Begin(0).Integer(Blocks.ActionWeaponReload.RowClip, clip).End(Status.Failed, Commit.Unknown, code);
            return Outcome.FailedUnknown(code);
        }
        return Committed(context, Blocks.ActionWeaponReload.RowClip, clip);
    }

    /// <summary>`forge.action.weapon.clip_set`. The amount is read from the frame only when the structural policy
    /// asks for one; `fill` reads the weapon's own capacity instead. `overflow_policy` decides what the capacity
    /// bounds, and `source_pool` decides whether the gain comes from the holder's own reserve; both are the row's
    /// structural members and an absent one keeps the behaviour this row had before the ports existed.</summary>
    private CommandResult ClipSet(CommandContext context)
    {
        if (!Read(context, WeaponHolderActionsContract.ClipSetEquipmentSlot, out var equipment, out var failure))
            return failure!;
        if (!Addressed(equipment))
            return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        var policy = context.Text(WeaponHolderActionsContract.ClipSetPolicySlot);
        var overflow = context.Text(WeaponHolderActionsContract.ClipSetOverflowPolicySlot) ?? "";
        var source = context.Text(WeaponHolderActionsContract.ClipSetSourcePoolSlot) ?? "";
        var declaredAmount = context.Integer(WeaponHolderActionsContract.ClipSetAmountSlot);
        var hasAmount = declaredAmount is >= int.MinValue and <= int.MaxValue;
        var amount = hasAmount ? checked((int)declaredAmount!.Value) : 0;
        if (!WeaponHolderChannel.TrySetClip(_players, equipment, policy ?? "", hasAmount, amount, overflow, source,
                out var clip, out var code, out var written))
        {
            _report("weapon.holder-clip-refused equipment=" + equipment.Id + " code=" + code);
            if (!WeaponHolderChannel.IsCommitUnknown(code, written)) return Outcome.Rejected(code);
            context.Rows.Begin(0).Integer(Blocks.ActionWeaponClipSet.RowClip, clip).End(Status.Failed, Commit.Unknown, code);
            return Outcome.FailedUnknown(code);
        }
        return Committed(context, Blocks.ActionWeaponClipSet.RowClip, clip);
    }

    /// <summary>
    /// `forge.action.weapon.auto_fire`. The same order of checks as the magazine rows — addressed here, the
    /// equipment is this machine's held instance — and then the one this row adds: the weapon's own state has to
    /// be the one the request named. The shot is the native `Fire` body, so the magazine, the fire rate and the
    /// replicated shot count are the game's; the result carries the fixed columns alone, because one `Fire` body
    /// is one shot and a count here would always read one.
    /// </summary>
    private CommandResult AutoFire(CommandContext context)
    {
        if (!Read(context, WeaponHolderActionsContract.AutoFireEquipmentSlot, out var equipment, out var failure))
            return failure!;
        if (!Addressed(equipment))
            return Outcome.Rejected(ResultCode.OwnerNotAddressed);
        var requiredState = context.Text(WeaponHolderActionsContract.AutoFireRequiredStateSlot)
                            ?? WeaponHolderActionsContract.StateNone;
        if (!WeaponHolderChannel.TryFire(_players, equipment, requiredState, out var code, FireEnded))
        {
            // A charge-up gun spends its first native update charging: the shot comes in a later update, so the row answers
            // when the channel has settled the request instead of reporting the wait as a failure.
            if (code == ResultCode.FireRequestPending && WeaponHolderChannel.PendingFire is { } pending
                && DeferFire(context, equipment, pending) is { } deferred)
                return deferred;
            return FireFailure(context.Rows, equipment, requiredState, code);
        }
        return FireRow(context.Rows, equipment, Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
    }

    /// <summary>The fire row's answer when the request did not shoot: a pending, unconfirmed or thrown update is
    /// `failed + unknown`; anything else is the game declining the request, a rejection.</summary>
    private Outcome FireFailure(RowWriter rows, EntityReference equipment, string requiredState, string code)
    {
        if (code is ResultCode.FireRequestPending or ResultCode.ReadbackUnconfirmed or ResultCode.FireNativeUpdateThrew)
            return FireRow(rows, equipment, Status.Failed, Commit.Unknown, code);
        _note("weapon.holder-fire-refused equipment=" + equipment.Id + " state=" + requiredState + " code=" + code);
        return FireRow(rows, equipment, Status.Rejected, Commit.None, code);
    }

    /// <summary>How long a deferred fire waits for its shot: the longest charge a weapon has is well inside this.</summary>
    private const double FireWaitSeconds = 10;

    /// <summary>The deferred answer of a fire request that is still charging, or null when this context cannot answer later
    /// (the caller then reports the wait as unknown, as before). The kernel asks once per advance; at the deadline the
    /// request is stopped, which releases the synthetic input, and the row ends unknown.</summary>
    private CommandResult? DeferFire(CommandContext context, EntityReference equipment, WeaponHolderChannel.FireRequest request)
    {
        try
        {
            var ticks = Math.Clamp((int)Math.Ceiling(context.TicksPerSecond * FireWaitSeconds), 1, RuntimeKernel.MaximumOwnerDeferralTicks);
            return context.DeferOwnerResult(ticks, new PendingFire(this, equipment, request), SettleFire);
        }
        catch (RuntimeContractException) { return null; }
    }

    private readonly record struct PendingFire(WeaponHolderModule Module, EntityReference Equipment, WeaponHolderChannel.FireRequest Request);

    private static readonly OwnerSettlement<PendingFire> SettleFire = static (ref PendingFire pending, bool expired, RowWriter rows) =>
    {
        if (ReferenceEquals(WeaponHolderChannel.PendingFire, pending.Request))
        {
            if (!expired) return null;
            WeaponHolderChannel.StopFire(ResultCode.FireRequestPending);
        }
        var code = pending.Request.Code;
        return code.Length == 0 ? FireRow(rows, pending.Equipment, Status.Succeeded, Commit.Confirmed, ResultCode.Committed)
            : pending.Module.FireFailure(rows, pending.Equipment, WeaponHolderActionsContract.StateNone, code);
    };

    /// <summary>The channel's end-of-request line: the game declining the fire is the row's designed rejection (a note); any other
    /// ending (readback, thrown update, lifetime) is a problem the console shows.</summary>
    private void FireEnded(string message)
    {
        if (message.EndsWith(" code=" + ResultCode.FireRefused, StringComparison.Ordinal)) _note(message);
        else _report(message);
    }

    /// <summary>The equipment port every handler reads, plus the refusal that belongs to reading it. A frame with no
    /// equipment, or with one this machine has no live instance for, is refused before any write is attempted.</summary>
    private bool Read(CommandContext context, HandlerSlot equipmentSlot, out EntityReference equipment, out CommandResult? failure)
    {
        failure = null;
        if (context.TryEntity(equipmentSlot, out equipment) && _kernel.IsEntityCurrent(equipment)) return true;
        failure = Outcome.Rejected(ResultCode.StaleEntity);
        return false;
    }

    /// <summary>A committed magazine write: one result row with the four fixed columns and this row's own `clip`,
    /// which is the magazine as it stands after the write (<paramref name="column"/> is that block's own column).</summary>
    private static CommandResult Committed(CommandContext context, int column, int clip)
    {
        context.Rows.Begin(0).Integer(column, clip).End(Status.Succeeded, Commit.Confirmed, ResultCode.Committed);
        return Outcome.Succeeded();
    }

    /// <summary>One result row for the fire row: the four fixed columns every action result carries. A shot that
    /// the native body really produced is the fact of this row having run at all, so there is no fifth column.</summary>
    private static Outcome FireRow(RowWriter rows, EntityReference equipment, Status status, Commit commit, string code)
    {
        rows.Begin(0, equipment).End(status, commit, code);
        return new Outcome(status, commit, code);
    }

    /// <summary>The reload row's `speed_multiplier`, or null when the plan set none. A written value is bounded
    /// by the row's own `cap` under the multiplicative operation, which is the same rule every modifier row of
    /// this family uses.</summary>
    private static double? SpeedMultiplier(CommandContext context)
    {
        var raw = context.Number(WeaponHolderActionsContract.ReloadSpeedMultiplierSlot);
        if (!raw.HasValue) return null;
        // A written value that is not a number is carried on as one, so the channel refuses it by name instead of
        // the reload running at its own speed and reporting success.
        if (!double.IsFinite(raw.Value)) return double.NaN;
        return WeaponActionRuntime.WithCap(context.Number(WeaponHolderActionsContract.ReloadCapSlot),
            WeaponOverrideOperation.Multiply, raw.Value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _registration.Dispose();
        _disposed = true;
    }
}
