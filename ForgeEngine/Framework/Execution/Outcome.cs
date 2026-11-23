using System;
using System.Threading;

namespace ForgeEngine.Framework;

/// <summary>What became of a command or of one recipient's row, in the order the `execution_outcome` set and
/// <see cref="CommandStatuses"/> declare the same six words.</summary>
public enum Status : byte { Succeeded, Partial, Rejected, Failed, Cancelled, Expired }

/// <summary>Whether a command or a row wrote world state, in the order the `commit_state` set and
/// <see cref="CommitStates"/> declare the same three words.</summary>
public enum Commit : byte { None, Confirmed, Unknown }

/// <summary>
/// A command's conclusion as a value: the status, the commit state and the reason code, with no document behind it.
/// It is what a handler that wrote its rows through <see cref="CommandContext.Rows"/> returns, and it converts to the
/// <see cref="CommandResult"/> the handler delegate still returns without allocating: the result for one
/// (status, commit, code) is made once and handed out again.
/// </summary>
public readonly struct Outcome
{
    private readonly string? detail;

    public Outcome(Status status, Commit commit, string code)
    {
        Status = status; Commit = commit; Code = code ?? throw new ArgumentNullException(nameof(code)); detail = null;
    }

    private Outcome(Outcome outcome, string detail)
    {
        Status = outcome.Status; Commit = outcome.Commit; Code = outcome.Code; this.detail = detail;
    }

    public Status Status { get; }
    public Commit Commit { get; }
    /// <summary>The reason code: one of the <see cref="ResultCode"/> constants or a registered kernel code.</summary>
    public string Code { get; }
    /// <summary>The diagnostic detail the result carries beside its code; empty where there is none.</summary>
    public string Detail => detail ?? "";

    /// <summary>The same conclusion carrying a diagnostic detail (at most <see cref="CommandResult.MaximumDetailBytes"/>
    /// of UTF-8). A detail that is the same string on every call — a constant, or one built once per kind — converts
    /// to the one result made for it; a detail composed per call makes a result per call.</summary>
    public Outcome WithDetail(string detail) => new(this, detail ?? throw new ArgumentNullException(nameof(detail)));

    public static Outcome Succeeded(string code = ResultCode.Committed) => new(Status.Succeeded, Commit.Confirmed, code);
    public static Outcome Partial(Commit commit = Commit.Confirmed, string code = ResultCode.Partial) => new(Status.Partial, commit, code);
    public static Outcome Rejected(string code) => new(Status.Rejected, Commit.None, code);
    public static Outcome Failed(string code) => new(Status.Failed, Commit.None, code);
    public static Outcome FailedUnknown(string code) => new(Status.Failed, Commit.Unknown, code);
    public static Outcome Cancelled(string code) => new(Status.Cancelled, Commit.None, code);
    public static Outcome Expired(string code) => new(Status.Expired, Commit.None, code);
    /// <summary>The answer to a recipient set that named nobody: nothing was needed, so nothing failed.</summary>
    public static Outcome NoTargets() => new(Status.Succeeded, Commit.Confirmed, ResultCode.NoTargets);

    public static implicit operator CommandResult(Outcome outcome) => Outcomes.Result(outcome);
}

/// <summary>The words the two enums stand for and the one result each conclusion converts to.</summary>
internal static class Outcomes
{
    internal static readonly string[] StatusNames = { CommandStatuses.Succeeded, CommandStatuses.Partial,
        CommandStatuses.Rejected, CommandStatuses.Failed, CommandStatuses.Cancelled, CommandStatuses.Expired };
    internal static readonly string[] CommitNames = { CommitStates.None, CommitStates.Confirmed, CommitStates.Unknown };

    /// <summary>The row and command pairing rule (<see cref="ResultRowAbi.CanonicalPairing(string, string)"/>) on the
    /// two enums.</summary>
    internal static bool CanonicalPairing(Status status, Commit commit) => status switch
    {
        Status.Succeeded => commit == Commit.Confirmed,
        Status.Partial => commit is Commit.Confirmed or Commit.Unknown or Commit.None,
        Status.Failed => commit is Commit.None or Commit.Unknown,
        Status.Rejected or Status.Cancelled or Status.Expired => commit == Commit.None,
        _ => false,
    };

    /// <summary>The results already made, by status and commit: each list is replaced as a whole when it grows, so a
    /// reader on any thread sees a complete one. Codes are compile-time constants and a detail is usually one string
    /// kept by its handler, so the same pair arrives as the same strings and is found by reference; a list stops
    /// growing at <see cref="MaximumCached"/> entries, past which a conclusion is simply made each time.</summary>
    private static readonly CommandResult[]?[] Made = new CommandResult[]?[18];
    private const int MaximumCached = 512;
    /// <summary>A longer detail is a composed one: its result is made each time and never kept.</summary>
    private const int MaximumCachedDetail = 512;

    internal static CommandResult Result(Outcome outcome)
    {
        if (outcome.Code == null) throw new RuntimeContractException("invalid-handler-result", "An outcome carries no code.");
        var slot = (int)outcome.Status * 3 + (int)outcome.Commit;
        if ((uint)slot >= (uint)Made.Length) throw new RuntimeContractException("invalid-handler-result", "An outcome carries an unknown status or commit state.");
        var detail = outcome.Detail;
        var made = Volatile.Read(ref Made[slot]);
        if (made != null)
        {
            for (var index = 0; index < made.Length; index++)
                if (ReferenceEquals(made[index].Code, outcome.Code) && ReferenceEquals(made[index].Detail, detail)) return made[index];
            for (var index = 0; index < made.Length; index++)
                if (made[index].Code == outcome.Code && made[index].Detail == detail) return made[index];
        }
        var result = CommandResult.Create(StatusNames[(int)outcome.Status], CommitNames[(int)outcome.Commit], outcome.Code, detail,
            RuntimeJson.EmptyObject);
        if ((made?.Length ?? 0) >= MaximumCached || detail.Length > MaximumCachedDetail) return result;
        var grown = new CommandResult[(made?.Length ?? 0) + 1];
        made?.CopyTo(grown, 0);
        // The cached result is found by the caller's own string, so that is the instance kept as its code.
        grown[^1] = result;
        Volatile.Write(ref Made[slot], grown);
        return result;
    }
}
