using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeEngine.Framework;

/// <summary>The command-level conclusion of a run of recipient rows, by the one rule every multi-target action
/// follows: <see cref="Conclude"/> for the host and owner tiers, <see cref="Presented"/> for the presentation tier.
/// A command that committed something answers with the shared word (`committed`, `partial`); every other
/// conclusion carries the code of the first row that decided it, so a one-recipient command reports the native
/// refusal by name.</summary>
internal static class ResultAggregation
{
    /// <summary>The conclusion of a host or owner action's <paramref name="rows"/>, written into
    /// <paramref name="outputs"/>: no row is `no-targets`; every row done is `succeeded` / `committed`; some row
    /// committed is `partial`, with an unknown commit state when any row's is unknown; nothing committed and nothing
    /// unknown is a rejection with the first row's code; nothing committed and something unknown is
    /// `failed + unknown` with the first unknown row's code. <paramref name="succeeded"/> says whether a row is
    /// done; without it a row is done when its commit state is confirmed, so an action whose rows can be
    /// `partial + confirmed` (a heal that fell short, a backpack that took part of the amount) passes it.</summary>
    public static CommandResult Conclude<T>(IReadOnlyList<T> rows, Func<T, string> commitState, Func<T, string> code,
        JsonElement outputs, Func<T, bool>? succeeded = null)
    {
        if (rows.Count == 0) return CommandResult.NoTargets();
        int done = 0, committed = 0;
        string? unknown = null;
        foreach (var row in rows)
        {
            var state = commitState(row);
            if (state == CommitStates.Confirmed) committed++;
            else if (state == CommitStates.Unknown) unknown ??= code(row);
            if (succeeded == null ? state == CommitStates.Confirmed : succeeded(row)) done++;
        }
        if (done == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown == null ? CommitStates.Confirmed : CommitStates.Unknown);
        return unknown == null
            ? CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code(rows[0]), "", outputs)
            : CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, unknown, "", outputs);
    }

    /// <summary>The conclusion of a presentation action's <paramref name="rows"/> by <see cref="PresentedTally"/>, the
    /// one presentation rule: the rows one machine wrote, or the host's gathering of every recipient machine's answer
    /// (each machine's answer is then one row). <paramref name="commitState"/> reads a row's commit where it can be
    /// unknown (a machine whose answer was lost after it may have run); without it every row's is none.</summary>
    public static CommandResult Presented<T>(IReadOnlyList<T> rows, Func<T, string> status, Func<T, string> code,
        JsonElement outputs, Func<T, string>? commitState = null)
    {
        var tally = new PresentedTally();
        foreach (var row in rows)
            tally.Add((Status)Array.IndexOf(Outcomes.StatusNames, status(row)),
                commitState?.Invoke(row) == CommitStates.Unknown ? Commit.Unknown : Commit.None, code(row), elsewhere: false);
        var concluded = tally.Conclude();
        return concluded.Code == ResultCode.NoTargets ? CommandResult.NoTargets()
            : CommandResult.Create(Outcomes.StatusNames[(int)concluded.Status], Outcomes.CommitNames[(int)concluded.Commit],
                concluded.Code, "", outputs);
    }
}

/// <summary>
/// The presentation rule, counted as rows arrive so a conclusion reads no row back. A row is issued when its status is
/// `partial` (shown, not confirmed) or `succeeded` (known to have landed); every other row is refused. A row written
/// for a recipient another machine presents (<see cref="RowWriter.EndElsewhere"/>) is that machine's to answer and
/// decides nothing here. Over the rows that decide: none at all is `no-targets` when no row was written, and the first
/// elsewhere row's own answer when every row was elsewhere; a row whose commit is unknown makes it `failed + unknown`
/// with that row's code; every row succeeded is `succeeded`; any row issued is `partial`, with the first refused row's
/// code when some row was refused (a real refusal is never hidden behind what landed) and otherwise the first issued
/// row's; every row refused alike is that status with the first row's code; anything else is a failure with the first
/// row that was neither issued nor rejected. The commit state is otherwise none.
/// </summary>
internal struct PresentedTally
{
    private int counted, succeeded, elsewhere;
    private bool alike;
    private Status firstStatus, elsewhereStatus;
    private string? firstCode, issuedCode, refusedCode, failedCode, unknownCode, elsewhereCode;

    internal void Add(Status status, Commit commit, string code, bool elsewhere)
    {
        if (elsewhere)
        {
            if (this.elsewhere++ == 0) { elsewhereStatus = status; elsewhereCode = code; }
            return;
        }
        if (counted++ == 0) { firstStatus = status; firstCode = code; alike = true; }
        else alike &= status == firstStatus;
        if (commit == Commit.Unknown) unknownCode ??= code;
        if (status == Status.Succeeded) succeeded++;
        if (status is Status.Succeeded or Status.Partial) { issuedCode ??= code; return; }
        refusedCode ??= code;
        if (status != Status.Rejected) failedCode ??= code;
    }

    internal readonly Outcome Conclude()
    {
        if (counted == 0) return elsewhere == 0 ? Outcome.NoTargets() : new Outcome(elsewhereStatus, Commit.None, elsewhereCode!);
        if (unknownCode != null) return Outcome.FailedUnknown(unknownCode);
        if (succeeded == counted) return new Outcome(Status.Succeeded, Commit.None, issuedCode!);
        if (issuedCode != null) return new Outcome(Status.Partial, Commit.None, refusedCode ?? issuedCode);
        return alike ? new Outcome(firstStatus, Commit.None, firstCode!) : Outcome.Failed(failedCode!);
    }
}
