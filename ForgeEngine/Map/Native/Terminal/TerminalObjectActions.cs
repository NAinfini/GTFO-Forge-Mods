using System;
using ForgeEngine.Framework;
using LevelGeneration;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>The execute half of the two terminal action rows: `forge.action.map.terminal_command` and
/// `forge.action.map.terminal_output`. Both act on the terminals a plan named in the one `gtfo.map_object`
/// namespace, through the native entry points the door and terminal observation already read — no second
/// terminal table, no plan object, no handle.
///
/// What the native side can carry is narrow and this half refuses everything else instead of imitating it:
///
/// - The command row runs one whitelisted command through the terminal's own interpreter and the terminal
///   manager's own command entry, which is the game's authoritative action and the path the game's own servers
///   replicate. The engine parses the text and this half only decides whether the parsed command is one of the
///   commands a request may run, so a line the terminal does not know can never become a command.
/// - The actor a request may name is not passed on. The native command payload is a terminal id, a command and
///   three strings — there is no player in it — so resolving an actor here would hand the game an argument it
///   has no slot for.
/// - The printed-line row adds a line to the terminal's own line buffer. The replicated terminal state carries
///   used and removed commands, not lines, so that write is local to this machine; the row records the line in
///   the content ledger, which carries it to every peer, and reports that record.
/// - The command row can carry one argument string, because the entry point takes two parameter strings and the
///   row composes them into the input line the interpreter parses. A command that needs more is a command this
///   row does not offer.
///
/// Every refusal is a code, never a silent success: a terminal that is not this kind, a reference the kernel no
/// longer answers for, an instance that no longer reads as the address it was resolved through, a command the
/// terminal's own parser refused and a command outside the whitelist are five different rows.</summary>
internal sealed class TerminalObjectActions
{
    private const string Prefix = EntityKinds.MapObject + ":";
    /// <summary>The one category of that kind these rows act on. A door is a map object too, and a plan that
    /// wired one into `terminals` is refused rather than read through the terminal reader.</summary>
    private const string TerminalCategory = "terminal";

    /// <summary>The native instance behind one recipient reference, or null when this world cannot resolve it.
    /// It is a delegate so a registration hands over the session's own lookup and a test can hand over a table;
    /// the caller still verifies the answer against the kernel before anything is written.</summary>
    internal delegate LG_ComputerTerminal? Resolver(EntityReference reference);

    /// <summary>The command request, read once before the first terminal.</summary>
    private struct CommandRequest
    {
        internal TerminalObjectActions Self;
        internal string? Command, Arguments;
    }

    /// <summary>The printed-line request, read once before the first terminal: the rendered line and the
    /// reference the ledger keeps.</summary>
    private struct LineRequest
    {
        internal TerminalObjectActions Self;
        internal TerminalLineSeverity Severity;
        internal string Text, Reference;
    }

    private static readonly TargetStep<CommandRequest> CommandStep = CommandOne;
    private static readonly TargetStep<LineRequest> LineStep = LineOne;

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly Resolver _resolve;
    private readonly Action<string> _report;
    private readonly Func<TerminalContentLedger?> _ledger;

    internal TerminalObjectActions(IForgeEngine kernel, Func<bool> ready, Resolver resolve, Action<string> report,
        Func<TerminalContentLedger?> ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal int Submitted { get; private set; }
    internal int Refused { get; private set; }
    internal int Unknown { get; private set; }

    /// <summary>One terminal behind one recipient reference, checked the same way for both rows. The kernel is
    /// the authority on whether the reference is still for this world; the address text is read with the
    /// terminal category's own grammar, and the instance the address names has to be the instance the kernel
    /// itself resolves the reference back to — a lookup that answers with a different terminal is refused
    /// rather than written to. A reference that does not name a terminal is `unsupported-recipient`; one the
    /// kernel no longer answers for, one the resolver cannot find, or an instance that no longer reads as its
    /// address is `stale-entity`; an instance the kernel resolves back to another reference is
    /// `terminal-identity-mismatch`.</summary>
    internal LG_ComputerTerminal? Resolve(EntityReference? reference, out string refusal)
    {
        refusal = "";
        if (reference == null || !IsTerminal(reference)) { refusal = ResultCode.UnsupportedRecipient; return null; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = ResultCode.StaleEntity; return null; }
        if (Address(reference) is not { } address) { refusal = ResultCode.UnsupportedRecipient; return null; }
        LG_ComputerTerminal? terminal;
        try { terminal = _resolve(reference); }
        catch (Exception error)
        {
            _report("map.terminal-resolve-exception: " + error.GetType().Name);
            refusal = ResultCode.StaleEntity;
            return null;
        }
        if (terminal == null) { refusal = ResultCode.StaleEntity; return null; }
        if (_kernel.ResolveEntityInstance(EntityKinds.MapObject, terminal) is not { } verified || verified != reference)
        { refusal = ResultCode.TerminalIdentityMismatch; return null; }
        // The instance still has to read as the address the reference was built from: a terminal that moved to
        // another placement index in its zone, or into another zone, is no longer the terminal the plan named.
        if (TerminalObservation.Address(terminal) != address) { refusal = ResultCode.StaleEntity; return null; }
        return terminal;
    }

    /// <summary>The same check, in the form the two loops use: the instance and the address it was resolved
    /// through, or the one code that refused it.</summary>
    private bool TryResolve(EntityReference target, out LG_ComputerTerminal terminal, out MapObjectReference address,
        out string refusal)
    {
        terminal = null!;
        address = null!;
        if (Resolve(target, out refusal) is not { } resolved) return false;
        if (Address(target) is not { } parsed) { refusal = ResultCode.UnsupportedRecipient; return false; }
        terminal = resolved;
        address = parsed;
        return true;
    }

    /// <summary>Runs one whitelisted command on one terminal. The native half composes the input line from the
    /// command and its argument string, asks the terminal's own interpreter to parse it, and refuses anything
    /// the interpreter did not recognise or the whitelist does not run. A command the terminal's interpreter
    /// received inside the send is confirmed; one that was only sent is an unknown commit.</summary>
    private void Run(LG_ComputerTerminal terminal, MapObjectReference address, string? command, string? arguments,
        RowWriter rows)
    {
        MapActionOutcome outcome;
        long received = TerminalCommandReadbackFixed.Count;
        try { outcome = TerminalActions.Command(terminal, address, command, arguments); }
        catch (Exception error)
        {
            // The call entered the terminal's own path; whether the manager's action had already sent its
            // packet is not observable from here, so the commit stays unknown and the terminal is not retried.
            Unknown++;
            _report("map.terminal-command-exception: " + error.GetType().Name);
            rows.End(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
            return;
        }
        if (outcome.Commit == MapActionCommit.Issued)
        {
            Submitted++;
            // The terminal's interpreter received the command inside the send (the host runs it at once): that
            // receipt is the commit.
            if (TerminalCommandReadbackFixed.Count == received + 1 && TerminalCommandReadbackFixed.LastTerminal == terminal.SyncID)
            {
                rows.End(Status.Succeeded, Commit.Confirmed, outcome.Code);
                return;
            }
            // Otherwise WantToSendTerminalCommand returned before ReceiveCommand proved that the manager accepted the
            // request, and the synchronous Runtime command result cannot be completed by that later hook.
            Unknown++;
            rows.End(Status.Failed, Commit.Unknown, outcome.Code);
            return;
        }
        Refused++;
        rows.End(Status.Rejected, Commit.None, outcome.Code);
    }

    /// <summary>Adds one line to the terminal's own buffer and records it. The buffer is local to this machine by
    /// the game's own design and the game's replicated state carries no lines, so what the row commits is the
    /// record in the <see cref="TerminalContentLedger"/>, from which every peer prints the line.</summary>
    private void Print(LG_ComputerTerminal terminal, MapObjectReference address, TerminalLineSeverity severity,
        string text, string source, RowWriter rows)
    {
        // The record is checked before the native buffer is touched: a line it could not hold would be printed on
        // this machine alone.
        if (_ledger() is not { } ledger) { Refused++; rows.End(Status.Rejected, Commit.None, ResultCode.TerminalLedgerUnavailable); return; }
        if (!ledger.HasRoomForLine()) { Refused++; rows.End(Status.Rejected, Commit.None, ResultCode.TerminalLedgerFull); return; }
        MapActionOutcome outcome;
        try { outcome = TerminalActions.Print(terminal, address, severity, text); }
        catch (Exception error)
        {
            Unknown++;
            _report("map.terminal-print-exception: " + error.GetType().Name);
            rows.End(Status.Failed, Commit.Unknown, ResultCode.NativeCommitException);
            return;
        }
        if (outcome.Commit == MapActionCommit.Issued)
        {
            // AddLine only queues local output and has no native receipt, so the local print is not what this row
            // confirms. The line is written into this package's own replicated ledger, which holds it once
            // RecordLine returns; every peer prints it from that record exactly once, late joiners and checkpoint
            // restores included. That record is the commit, so the row is succeeded and confirmed.
            ledger.RecordLine(address.ToString(), severity, source);
            Submitted++;
            rows.End(Status.Succeeded, Commit.Confirmed, ResultCode.TerminalLinePrintedRecorded);
            return;
        }
        Refused++;
        rows.End(Status.Rejected, Commit.None, outcome.Code);
    }

    /// <summary>Whether a reference names a terminal of the one map-object namespace. Only the address's own
    /// first segment is read here; the full grammar is the category's own parser's job. The segment is compared
    /// as text as well, so a category a plan spelled with different case is refused rather than accepted and
    /// then failed by the parser with a different code.</summary>
    internal static bool IsTerminal(EntityReference reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int slash = id.IndexOf('/', Prefix.Length);
        int length = slash - Prefix.Length;
        return (length == TerminalCategory.Length && string.CompareOrdinal(id, Prefix.Length, TerminalCategory, 0, length) == 0)
            || (length == MapObjectCategories.Uplink.Length && string.CompareOrdinal(id, Prefix.Length, MapObjectCategories.Uplink, 0, length) == 0);
    }

    /// <summary>The address a terminal reference carries, read back with the terminal category's own grammar.
    /// A five-segment address of that category is this provider's; every other string — another category,
    /// another shape, a coordinate that is not a plain decimal, a key the category does not use — is not.</summary>
    internal static MapObjectReference? Address(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return MapObjectTerminalAddress.TryParse(id[Prefix.Length..]);
    }

    /// <summary>The terminal a reference addresses, through the two reads a terminal lookup is made of: the
    /// category's own address grammar and the observation table that address resolves in. A reference of another
    /// category, or one whose address no observed terminal carries, answers null.</summary>
    internal static LG_ComputerTerminal? ResolveByAddress(EntityReference? reference)
        => Address(reference) is { } address ? TerminalObservation.ByAddress(address) : null;

    /// <summary>One classification of the catalog's `severity` parameter onto the terminal's own line kinds.
    /// The parameter is the plan's structural choice, so it arrives in the node's constant bag; a value outside
    /// the catalog's three members — including an absent one, which is not a severity a line may be printed
    /// with — is refused rather than defaulted to the plain line kind.</summary>
    internal static TerminalLineSeverity? Severity(string? value) => value switch
    {
        "info" => TerminalLineSeverity.Info,
        "warning" => TerminalLineSeverity.Warning,
        "error" => TerminalLineSeverity.Error,
        _ => null
    };

    /// <summary>The terminal command handler. The whole request is checked before the first terminal, because a
    /// command that cannot be carried out as asked must not half-apply: the argument string has to be one the
    /// native entry can carry. The kernel answers an empty collection before this handler runs; a port that
    /// carries no collection at all is a missing input. Every terminal is asked independently; a command its
    /// terminal received during the send is `succeeded + confirmed`, one only sent is `failed + unknown`.
    ///
    /// The command row runs a command and nothing else: whether a command appears on the terminal at all is
    /// `forge.action.map.terminal_content`'s show and hide, which is a different author node because it asks for a
    /// different thing.</summary>
    internal CommandResult ExecuteCommand(CommandContext context)
    {
        if (!_ready() || !Session.IsHost || _kernel.Lifecycle.StartupState != RuntimeStartupState.Ready
            || _kernel.Lifecycle.IsHost != true) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        string? command = context.Text(TerminalObjectContract.CommandText);
        string? arguments2 = context.Text(TerminalObjectContract.CommandArguments);
        if (command == null && arguments2 != null) return Outcome.Rejected(ResultCode.TerminalCommandEmpty);
        var request = new CommandRequest { Self = this, Command = command, Arguments = arguments2 };
        return context.EachTarget(ref request, CommandStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapTerminalCommand.RowTargetCount);
    }

    private static void CommandOne(ref CommandRequest request, EntityReference target, int index, RowWriter rows)
    {
        var self = request.Self;
        if (!self.TryResolve(target, out var terminal, out var address, out var refusal))
        { rows.End(Status.Rejected, Commit.None, refusal); return; }
        self.Run(terminal, address, request.Command, request.Arguments, rows);
    }

    /// <summary>The printed-line handler. A printed line stays on the terminal: the native member's time argument
    /// takes the terminal's own zero, so the row declares no lifetime.</summary>
    internal CommandResult ExecuteOutput(CommandContext context)
    {
        if (!_ready() || !Session.IsHost || _kernel.Lifecycle.StartupState != RuntimeStartupState.Ready
            || _kernel.Lifecycle.IsHost != true) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        // The severity is the row's required structural parameter, so it is checked before the recipients: a
        // request whose parameter bag carries none is not a request this row can print.
        if (Severity(context.Text(TerminalObjectContract.OutputSeverity)) is not { } severity)
            return Outcome.Rejected(ResultCode.TerminalSeverityUnknown);
        // Preserve the author value in the content ledger. The terminal's native `AddLine` accepts only a plain
        // string, so this client renders the value through the shared player-text-language service immediately
        // before printing it. The ledger keeps the original reference for late peers and checkpoint restores.
        if (!TextResourceContract.TryText(context, TerminalObjectContract.OutputText,
                TerminalObjectContract.OutputTextId, out var cardText, out string? missing))
            return Outcome.Rejected(missing!);
        string reference = TextCardValue.Reference(cardText);
        if (_ledger() is not { } ledger)
            return Outcome.Rejected(ResultCode.TerminalLedgerUnavailable);
        if (!ledger.TryRenderCurrent(reference, out string text, out string renderCode))
            return Outcome.Rejected(renderCode);
        var request = new LineRequest { Self = this, Severity = severity, Text = text, Reference = reference };
        return context.EachTarget(ref request, LineStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapTerminalOutput.RowTargetCount);
    }

    /// <summary>One terminal's printed line.</summary>
    private static void LineOne(ref LineRequest request, EntityReference target, int index, RowWriter rows)
    {
        var self = request.Self;
        if (!self.TryResolve(target, out var terminal, out var address, out var refusal))
        { rows.End(Status.Rejected, Commit.None, refusal); return; }
        self.Print(terminal, address, request.Severity, request.Text, request.Reference, rows);
    }

    internal CommandResult HandleCommand(CommandContext context)
        => ExecuteCommand(context);

    internal CommandResult HandleOutput(CommandContext context)
        => ExecuteOutput(context);
}
