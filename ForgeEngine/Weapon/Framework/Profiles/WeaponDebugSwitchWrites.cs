using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeWeapon;

/// <summary>The native write behind <see cref="WeaponDebugSwitchContract"/> and its readback, kept behind an
/// interface so the row's body holds no game type and a process without this package's native half can still
/// declare the row and refuse it by code. The three switches are the game's own process-wide statics, so both are
/// asked by the member's declared index and never by a weapon.</summary>
internal interface IWeaponDebugSwitchSource
{
    /// <summary>Writes one switch, or false with the code the write refused by: a machine that may not write right
    /// now, or a native property this process could not set.</summary>
    bool TrySet(int switchIndex, bool enabled, out string code);

    /// <summary>Reads one switch back. The native setter returns nothing, so the readback is the only proof a
    /// write landed; a read that cannot be answered keeps its own code rather than confirming a write.</summary>
    bool TryRead(int switchIndex, out bool value, out string code);
}

/// <summary>The debug-switch row's body. It is the same shape the read rows of this package use: a source the
/// native half attaches for as long as its session is the live one, a thread the source is only ever reached
/// from, and a refusal by name whenever an answer would otherwise be invented.
///
/// One command is one native write. The switches are static, so a plan naming three subjects does not write three
/// times: the write happens once and each named subject is reported with the outcome that one write had, which is
/// what the row's own `subject` port means — the session the switch was set for, not a filter over weapons. The
/// static is read back after the write, which is the only proof the write landed.</summary>
internal sealed class WeaponDebugSwitchWrites
{
    private readonly IWeaponDebugSwitchSource _source;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private WeaponDebugSwitchWrites(IWeaponDebugSwitchSource source) => _source = source;

    /// <summary>The source of the host this package is loaded into, or null while none is attached.</summary>
    public static WeaponDebugSwitchWrites? Installed { get; private set; }

    /// <summary>Attaches one host's source and answers the writes a session detaches with.</summary>
    public static WeaponDebugSwitchWrites Attach(IWeaponDebugSwitchSource source)
        => Installed = new WeaponDebugSwitchWrites(source ?? throw new ArgumentNullException(nameof(source)));

    /// <summary>Detaches exactly the writes one attach returned, so a disposal cannot detach a machine that
    /// replaced it.</summary>
    public static void Detach(WeaponDebugSwitchWrites writes)
    {
        if (ReferenceEquals(Installed, writes)) Installed = null;
    }

    /// <summary>The handler table the registration carries: one name, the one the contract declares.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [WeaponDebugSwitchContract.HandlerName] = DebugSwitchRow
    };

    /// <summary>
    /// The `switch` parameter as the member index of <see cref="WeaponDebugSwitchContract.Switches"/>.
    ///
    /// The kernel hands a handler the resolved member of an enum parameter
    /// (<c>RuntimeJson.ResolveEnumParameters</c> turns the compiled index into the member the row declares), so the
    /// only form a dispatched command carries is the declared name — `super_weapons`, `infinite_ammo` or `recoil`.
    /// The name is resolved through that one list, so the handler and the row read the same vocabulary; anything
    /// else — a compiled index, an unknown name, a non-string — is the row's own refusal.
    /// </summary>
    private static bool TrySwitch(string? name, out int index)
    {
        index = -1;
        for (var candidate = 0; candidate < WeaponDebugSwitchContract.Switches.Count; candidate++)
            if (string.Equals(WeaponDebugSwitchContract.Switches[candidate], name, StringComparison.Ordinal))
            {
                index = candidate;
                return true;
            }
        return false;
    }

    /// <summary>`forge.action.weapon.debug_switch`: one write of one static, reported under every subject the step
    /// named. Every refusal that can be decided before the native call is decided before it, so a rejected step
    /// never leaves a switch half written. The rows carry the four fixed columns and nothing else — the switch is a
    /// static, so there is no per-subject count to report. One write answers every row, so the rows are written
    /// directly rather than through the per-target skeleton.</summary>
    private static CommandResult DebugSwitchRow(CommandContext context)
    {
        if (Installed is not { } writes) return Outcome.Rejected(ResultCode.WeaponDebugSourceUnavailable);
        if (Environment.CurrentManagedThreadId != writes._threadId)
            return Outcome.Rejected(ResultCode.WeaponDebugSourceUnavailable).WithDetail("The weapon debug source is written off its own thread.");
        if (!TrySwitch(context.Text(WeaponDebugSwitchContract.SwitchSlot), out int index))
            return Outcome.Rejected(ResultCode.SwitchUnsupported);
        if (context.Boolean(WeaponDebugSwitchContract.EnabledSlot) is not { } enabled)
            return Outcome.Rejected(ResultCode.MissingField).WithDetail("enabled");
        var rows = context.Rows;
        if (rows.Count > CommandResult.MaximumFacts) return Outcome.Rejected(ResultCode.TooManyTargets);

        // One write for the whole step. A refusal here is a refusal for every subject, because there is one static
        // and it was not written.
        if (!writes._source.TrySet(index, enabled, out var code))
        {
            if (code != ResultCode.DebugSwitchWriteFailed) return Outcome.Rejected(code);
            // The native setter threw part-way: the static may or may not have changed.
            for (var subject = 0; subject < rows.Count; subject++) rows.Row(subject, Status.Failed, Commit.Unknown, code);
            return rows.Conclude();
        }
        // The native setter answers nothing, so the switch is read back: a readback that disagrees, and one that
        // cannot be taken at all, both leave the commit unknown rather than reporting a write that may not stand.
        bool read = writes._source.TryRead(index, out bool actual, out var readCode);
        bool confirmed = read && actual == enabled;
        string rowCode = confirmed ? ResultCode.Committed : read ? ResultCode.DebugSwitchReadback : readCode;
        for (var subject = 0; subject < rows.Count; subject++)
            rows.Row(subject, confirmed ? Status.Succeeded : Status.Failed, confirmed ? Commit.Confirmed : Commit.Unknown, rowCode);
        return rows.Conclude();
    }
}
