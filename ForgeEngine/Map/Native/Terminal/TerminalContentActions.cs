using System;
using ForgeEngine.Framework;
using GameData;
using LevelGeneration;
using Localization;
using ForgeCore;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the terminal-content row (`forge.action.map.terminal_content`): one action that adds,
/// removes, shows or hides a command or a log on a terminal the level already built.
///
/// The command is added to the terminal's own interpreter, not to a table of this package's: the interpreter is
/// the same object a player's typed line is parsed by, so a command this row adds is typed, completed and answered
/// through the same native parser. Registration and help text are local; the native synchronized state does
/// not carry either command table. Removing and
/// hiding a command use the terminal's own synchronized hide entry and showing uses its show entry
/// (`TrySyncSetCommandHidden` / `TrySyncSetCommandShow`), the game's own way of withdrawing a command from the
/// list and offering it again without rebuilding the command table.
///
/// A terminal that already knows the command is not written twice: the row reports that the state it was asked
/// for already holds. That check is also what makes a second event harmless.
///
/// The log half writes the terminal's own local log table — `AddLocalLog` takes the `TerminalLogFileData` entry,
/// `RemoveLocalLog` takes it back out, and `SetLogVisible` only takes one off the list a player reads. Those members
/// are not replicated by the native terminal state, so every confirmed command help or log write is recorded in
/// the one <see cref="TerminalContentLedger"/>. Each machine rebuilds its native table from that record and renders
/// authored text with its own player-text language.</summary>
internal sealed class TerminalContentActions
{
    private const string EntityKind = EntityKinds.MapObject;

    private readonly IForgeEngine _kernel;
    private readonly Func<bool> _ready;
    private readonly Action<string> _report;
    private readonly Func<TerminalContentLedger?> _ledger;

    /// <summary>`ledger` is the session's replicated record, read when a request runs: every confirmed command
    /// registration and log write is recorded there so peers, late joiners and checkpoint restores receive it.</summary>
    internal TerminalContentActions(IForgeEngine kernel, Func<bool> ready, Action<string> report,
        Func<TerminalContentLedger?> ledger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    private static string? Key(EntityReference target) => TerminalObjectActions.Address(target)?.ToString();

    internal CommandResult Handle(CommandContext context)
        => Apply(context);

    /// <summary>The request is read once for the whole command, before any terminal is touched: a request the
    /// native path cannot carry in full must not half-apply. Every terminal is then written through the one
    /// multi-target skeleton, one row each.</summary>
    internal CommandResult Apply(CommandContext context)
    {
        if (!_ready() || !Session.IsHost
            || _kernel.Lifecycle.StartupState != RuntimeStartupState.Ready
            || _kernel.Lifecycle.IsHost != true) return Outcome.Rejected(ResultCode.AuthorityOrPhase);
        if (!TryReadRequest(context, out var request, out var code)) return Outcome.Rejected(code);
        var walk = new ContentWalk { Self = this, Request = request };
        return context.EachTarget(ref walk, ContentStep, EntityKinds.MapObject, CommandResult.MaximumFacts,
            countColumn: Blocks.ActionMapTerminalContent.RowTargetCount);
    }

    /// <summary>What one terminal of <see cref="Apply(CommandContext)"/> needs, handed by reference.</summary>
    private struct ContentWalk
    {
        internal TerminalContentActions Self;
        internal TerminalRequest Request;
    }

    private static readonly TargetStep<ContentWalk> ContentStep = ContentOne;

    private static void ContentOne(ref ContentWalk walk, EntityReference target, int index, RowWriter rows)
    {
        var end = walk.Self.ApplyOne(target, walk.Request);
        rows.End(end.Status, end.Commit, end.Code);
    }

    private RowEnd ApplyOne(EntityReference target, TerminalRequest request)
    {
        if (!Resolve(target, out var terminal, out var resolveCode)) return Refused(resolveCode);
        if (request.Kind == "log") return Log(target, terminal, request);
        var interpreter = terminal.m_command;
        if (interpreter == null || interpreter.WasCollected) return Refused(ResultCode.TerminalInterpreterUnavailable);
        if (request.Operation != "add")
        {
            try
            {
                if (!interpreter.TryGetCommand(request.Name, out var parsed, out var first, out var second)
                    || parsed != (TERM_Command)request.Number || !string.IsNullOrEmpty(first)
                    || !string.IsNullOrEmpty(second)
                    || !interpreter.TryGetCommandName(parsed, out var name)
                    || !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase))
                    return Refused(ResultCode.TerminalContentCommandConflict);
            }
            catch (Exception error)
            {
                ReportRead(error);
                return Refused(ResultCode.TerminalInterpreterUnavailable);
            }
        }
        return request.Operation switch
        {
            "add" => Add(target, terminal, request),
            "remove" => Remove(target, terminal, request),
            _ => SetShown(target, terminal, request, request.Operation == "show")
        };
    }

    /// <summary>A native read of terminal content threw. The caller's row says only that the interpreter is unavailable,
    /// so the report names the member that failed (caller) and carries the exception text, whose native frames show which
    /// read threw.</summary>
    private void ReportRead(Exception error, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var text = error.Message ?? "";
        _report("map.terminal-content-read-exception: caller=" + caller + " " + error.GetType().Name + ": "
            + (text.Length > 600 ? text[..600] : text));
    }
    private RowEnd Add(EntityReference target, LG_ComputerTerminal terminal,
        TerminalRequest request)
    {
        var interpreter = terminal.m_command;
        if (interpreter == null || interpreter.WasCollected)
            return Refused(ResultCode.TerminalInterpreterUnavailable);
        if (_ledger() is not { } ledger || Key(target) is not { } key)
            return Refused(ResultCode.TerminalLedgerUnavailable);
        if (!ledger.TryRenderCurrent(request.Text, out var rendered, out var textCode))
            return Refused(textCode);
        // The record is bounded and is checked before the native terminal is touched: a write the record cannot
        // hold would exist on this machine alone, so it is refused instead.
        if (!ledger.HasRoomFor(ledger.HoldsCommand(key, request.Number)))
            return Refused(ResultCode.TerminalLedgerFull);
        try
        {
            if (interpreter.TryGetCommand(request.Name, out var parsed, out var first, out var second))
            {
                var command = (TERM_Command)request.Number;
                if (parsed != command || !string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(second)
                    || !interpreter.TryGetCommandName(command, out var currentName)
                    || !string.Equals(currentName, request.Name, StringComparison.OrdinalIgnoreCase)
                    || terminal.GetCommandRule(command) != Rule(request.Rule) || terminal.CommandIsHidden(command))
                    return Refused(ResultCode.TerminalContentCommandConflict);
                if (ledger.TryCommand(key, request.Number, out var owned))
                {
                    if (owned == null || owned.Name != request.Name || owned.Text != request.Text || owned.Rule != request.Rule
                        || interpreter.m_commandHelpStrings is not { } help || help.WasCollected)
                        return Refused(ResultCode.TerminalContentCommandConflict);
                    help[command] = TextCardValue.FromRendered(rendered);
                }
                if (!CommandMatches(terminal, request, rendered)) return Refused(ResultCode.TerminalContentCommandConflict);
                ledger.RecordCommand(key, request.Number, request.Name, request.Text, request.Rule);
                return Issued(ResultCode.TerminalContentCommandPresentRecorded);
            }
            if (SlotOccupied(interpreter, (TERM_Command)request.Number))
                return Refused(ResultCode.TerminalContentCommandConflict);
        }
        catch (Exception error)
        {
            ReportRead(error);
            return Refused(ResultCode.TerminalInterpreterUnavailable);
        }
        try
        {
            interpreter.AddCommand((TERM_Command)request.Number, request.Name, TextCardValue.FromRendered(rendered),
                Rule(request.Rule), new Il2CppSystem.Collections.Generic.List<WardenObjectiveEventData>());
        }
        catch (Exception error)
        {
            _report("map.terminal-content-commit-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
        try
        {
            if (!CommandMatches(terminal, request, rendered)) return Unknown(ResultCode.TerminalContentWriteUnconfirmed);
            ledger.RecordCommand(key, request.Number, request.Name, request.Text, request.Rule);
            return Issued(ResultCode.TerminalContentCommandAddedRecorded);
        }
        catch (Exception error)
        {
            ReportRead(error);
            return Unknown(ResultCode.TerminalContentWriteUnconfirmed);
        }
    }

    private static bool CommandMatches(LG_ComputerTerminal terminal, TerminalRequest request, string rendered)
    {
        var interpreter = terminal.m_command;
        if (interpreter == null || interpreter.WasCollected) return false;
        var command = (TERM_Command)request.Number;
        if (!interpreter.TryGetCommand(request.Name, out var parsed, out var first, out var second)
            || parsed != command || !string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(second)
            || !interpreter.TryGetCommandName(command, out var name)
            || !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase)) return false;
        var help = interpreter.m_commandHelpStrings;
        return help != null && !help.WasCollected && help.ContainsKey(command)
            && string.Equals((string)help[command], rendered, StringComparison.Ordinal)
            && terminal.GetCommandRule(command) == Rule(request.Rule) && !terminal.CommandIsHidden(command);
    }

    /// <summary>Removal is the terminal's own synchronized hide entry, which is the member the game itself uses to
    /// withdraw a command: it writes `pComputerTerminalState.RemovedCommands`, the array the terminal reads back
    /// through `CommandIsHidden`, so the effect every peer sees is the game's own replication of the terminal's
    /// state and not a Forge broadcast. The readback is that same `CommandIsHidden`.</summary>
    private RowEnd Remove(EntityReference target, LG_ComputerTerminal terminal, TerminalRequest request)
    {
        var command = (TERM_Command)request.Number;
        bool hidden;
        try { hidden = terminal.CommandIsHidden(command); }
        catch (Exception error)
        {
            ReportRead(error);
            return Refused(ResultCode.TerminalInterpreterUnavailable);
        }
        if (hidden) return Issued(ResultCode.TerminalContentCommandAbsent);
        try { terminal.TrySyncSetCommandHidden(command); }
        catch (Exception error)
        {
            _report("map.terminal-content-commit-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
        try
        {
            return terminal.CommandIsHidden(command) ? Issued(ResultCode.TerminalContentCommandRemoved)
                : Unknown(ResultCode.TerminalContentVisibilityUnconfirmed);
        }
        catch (Exception error)
        {
            ReportRead(error);
            return Unknown(ResultCode.TerminalContentVisibilityUnconfirmed);
        }
    }

    /// <summary>Shows or hides one command through the terminal's own synchronized setters — `TrySyncSetCommandShow`
    /// and `TrySyncSetCommandHidden`. Both write `pComputerTerminalState.RemovedCommands`, the array the same
    /// component reads back through `CommandIsHidden`, so the writer and the reader are one component and the
    /// effect every peer sees is the game's own replication. The state asked for is read first, so a command
    /// already in it is reported as that state rather than re-written; the reading is repeated after the setter,
    /// and a setter whose effect the terminal's own reading does not confirm is an unknown commit.</summary>
    private RowEnd SetShown(EntityReference target, LG_ComputerTerminal terminal, TerminalRequest request,
        bool shown)
    {
        var command = (TERM_Command)request.Number;
        bool hidden;
        try { hidden = terminal.CommandIsHidden(command); }
        catch (Exception error)
        {
            ReportRead(error);
            return Refused(ResultCode.TerminalInterpreterUnavailable);
        }
        if (hidden != shown) return Issued(shown ? ResultCode.TerminalContentCommandAlreadyShown : ResultCode.TerminalContentCommandAlreadyHidden);
        try
        {
            if (shown) terminal.TrySyncSetCommandShow(command);
            else terminal.TrySyncSetCommandHidden(command);
        }
        catch (Exception error)
        {
            _report("map.terminal-content-commit-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
        bool applied;
        try { applied = terminal.CommandIsHidden(command) != shown; }
        catch (Exception error)
        {
            ReportRead(error);
            return Unknown(ResultCode.NativeCommitException);
        }
        if (!applied)
            return Unknown(ResultCode.TerminalContentVisibilityUnconfirmed);
        return Issued(shown ? ResultCode.TerminalContentCommandShown : ResultCode.TerminalContentCommandHidden);
    }

    /// <summary>One terminal request, already checked against the row's own vocabulary. `text` is the command's
    /// help line or the log file's body, and `sound` is the log's own audio file id — `TerminalLogFileData.
    /// AttachedAudioFile` — with a negative value meaning the request named none.</summary>
    private readonly record struct TerminalRequest(string Kind, string Operation, int Number, string Name, string Rule,
        string Text, int Sound);

    /// <summary>The request half's own reading. A command needs its code and its name — the interpreter looks a
    /// command up by name and stores it under its enum member — while a log needs its name and, when it is being
    /// added, the body a player reads. The help text and the body are the row's own `text` input; `sound` is only
    /// read for a log, because a command's interpreter carries no audio file.</summary>
    private static bool TryReadRequest(CommandContext context, out TerminalRequest request,
        out string code)
    {
        request = default;
        code = "";
        var kind = context.Text(TerminalContentContract.ContentKind);
        if (kind == null || Array.IndexOf(TerminalContentContract.Kinds, kind) < 0) { code = ResultCode.TerminalContentKindUnknown; return false; }
        var operation = context.Text(TerminalContentContract.Operation);
        if (operation == null || Array.IndexOf(TerminalContentContract.Operations, operation) < 0)
        {
            code = ResultCode.TerminalContentOperationUnknown;
            return false;
        }
        var name = context.Text(TerminalContentContract.Name);
        if (string.IsNullOrEmpty(name) || name!.Length > NameLimit) { code = ResultCode.TerminalContentNameRequired; return false; }
        if (kind == "command")
            foreach (char character in name)
                if (char.IsControl(character) || char.IsWhiteSpace(character)) { code = ResultCode.TerminalContentCommandNameInvalid; return false; }
        var text = context.Text(TerminalContentContract.ContentText) ?? "";
        if (text.Length > TextLimit) { code = ResultCode.TerminalContentLogBodyRequired; return false; }
        if (kind == "log")
        {
            if (operation == "add" && text.Length == 0) { code = ResultCode.TerminalContentLogBodyRequired; return false; }
            var soundValue = context.Integer(TerminalContentContract.Sound);
            int sound = soundValue is >= int.MinValue and <= int.MaxValue ? (int)soundValue.Value : -1;
            request = new TerminalRequest(kind, operation, 0, name, "normal", text, sound);
            return true;
        }
        var rule = context.Text(TerminalContentContract.Rule) ?? "normal";
        if (Array.IndexOf(TerminalContentContract.Rules, rule) < 0) { code = ResultCode.TerminalContentRuleUnknown; return false; }
        if (context.Integer(TerminalContentContract.Slot) is not { } slotValue
            || slotValue < 0 || slotValue > int.MaxValue) { code = ResultCode.TerminalContentNumberRequired; return false; }
        int slot = (int)slotValue;
        var command = (TERM_Command)slot;
        if (Convert.ToInt32(command) != slot || !Enum.IsDefined(typeof(TERM_Command), command))
        { code = ResultCode.TerminalContentNumberRequired; return false; }
        request = new TerminalRequest(kind, operation, slot, name, rule, text, -1);
        return true;
    }

    /// <summary>The interpreter's own three rules.</summary>
    /// <summary>The native name lookup raises a NullReferenceException for a slot the interpreter never registered. That
    /// is a free slot, the very state an add needs, so it is read as "not occupied"; any other failure escapes.</summary>
    internal static bool SlotOccupied(LG_ComputerTerminalCommandInterpreter interpreter, TERM_Command command)
    {
        try { return interpreter.TryGetCommandName(command, out _); }
        catch (Exception error) when (IsNativeNullReference(error)) { return false; }
    }

    internal static bool IsNativeNullReference(Exception error)
        => error.GetType().Name == "Il2CppException" && error.Message.Contains("NullReferenceException", StringComparison.Ordinal);

    internal static TERM_CommandRule Rule(string rule) => rule switch
    {
        "only_once" => TERM_CommandRule.OnlyOnce,
        "only_once_delete" => TERM_CommandRule.OnlyOnceDelete,
        _ => TERM_CommandRule.Normal
    };

    /// <summary>The longest terminal command name this row accepts, matching the buffer a typed line is read at.
    /// The interpreter's own help argument carries the row's `text` input, which is bounded the same way.</summary>
    internal const int NameLimit = 64;

    /// <summary>The longest `text` this row accepts, for a command's help line or a log file's body. A log is a
    /// page of text rather than a line, so the bound is the payload slot one command request carries.</summary>
    internal const int TextLimit = 4096;

    /// <summary>One log request's own write. `add` builds the entry the terminal holds (`TerminalLogFileData` with
    /// this row's name and body), `remove` takes the entry out of the table, and `show`/`hide` only move it on and
    /// off the list a player reads. The readback is the same member the game itself reads — `GetLocalLogs` for the
    /// entry, `IsLogVisible` for the list — so the row reports the state that holds afterwards and not the one it
    /// asked for.</summary>
    private RowEnd Log(EntityReference target, LG_ComputerTerminal terminal, TerminalRequest request)
    {
        try
        {
            var logs = terminal.GetLocalLogs();
            if (logs == null || logs.WasCollected) return Refused(ResultCode.TerminalContentLogTableUnavailable);
            if (_ledger() is not { } ledger || Key(target) is not { } key)
                return Refused(ResultCode.TerminalLedgerUnavailable);
            if (request.Operation == "add" && !ledger.HasRoomFor(ledger.HoldsLog(key, request.Name)))
                return Refused(ResultCode.TerminalLedgerFull);
            switch (request.Operation)
            {
                case "add":
                    {
                        if (!ledger.TryRenderCurrent(request.Text, out var rendered, out var textCode))
                            return Refused(textCode);
                        if (FindLog(terminal, request.Name) is { } existing)
                        {
                            if (ledger.TryLog(key, request.Name, out var owned))
                            {
                                if (owned == null || owned.Removed || owned.Text != request.Text || owned.Sound != request.Sound
                                    || existing.AttachedAudioFile != (request.Sound >= 0 ? (uint)request.Sound : 0u)
                                    || !terminal.IsLogVisible(request.Name))
                                    return Refused(ResultCode.TerminalContentLogConflict);
                                existing.FileContent = TextCardValue.FromRendered(rendered, TerminalLogReading.IdFor(request.Name));
                            }
                            if (!LogMatches(existing, rendered, request.Sound) || !terminal.IsLogVisible(request.Name))
                                return Refused(ResultCode.TerminalContentLogConflict);
                            ledger.RecordLog(key, request.Name, request.Text, request.Sound, true, false);
                            return Issued(ResultCode.TerminalContentLogPresentRecorded);
                        }
                        if (TerminalLogReading.Collides(terminal, request.Name)) return Refused(ResultCode.TerminalContentLogConflict);
                        var data = new TerminalLogFileData
                        {
                            FileName = request.Name,
                            FileContent = TextCardValue.FromRendered(rendered, TerminalLogReading.IdFor(request.Name))
                        };
                        if (request.Sound >= 0) data.AttachedAudioFile = (uint)request.Sound;
                        terminal.AddLocalLog(data, true);
                        if (FindLog(terminal, request.Name) is not { } added || !LogMatches(added, rendered, request.Sound)
                            || !terminal.IsLogVisible(request.Name))
                            return Unknown(ResultCode.TerminalContentWriteUnconfirmed);
                        ledger.RecordLog(key, request.Name, request.Text, request.Sound, true, false);
                        return Issued(ResultCode.TerminalContentLogAddedRecorded);
                    }
                case "remove":
                    {
                        if (FindLog(terminal, request.Name) is not { } doomed) return Issued(ResultCode.TerminalContentLogAbsentRecorded);
                        // The body and sound are kept in the record so a later add of the same name is judged against them.
                        string body = ledger.TryLog(key, request.Name, out var owned) && owned != null
                            ? owned.Text : NativeTextReference(doomed.FileContent);
                        int sound = doomed.AttachedAudioFile == 0u ? -1 : (int)doomed.AttachedAudioFile;
                        if (!ledger.HasRoomFor(ledger.HoldsLog(key, request.Name)))
                            return Refused(ResultCode.TerminalLedgerFull);
                        terminal.RemoveLocalLog(request.Name);
                        if (Holds(terminal, request.Name)) return Unknown(ResultCode.TerminalContentWriteUnconfirmed);
                        ledger.RecordLog(key, request.Name, body, sound, false, true);
                        return Issued(ResultCode.TerminalContentLogRemovedRecorded);
                    }
                case "show":
                case "hide":
                    {
                        var visible = request.Operation == "show";
                        if (FindLog(terminal, request.Name) is not { } current) return Refused(ResultCode.TerminalContentLogAbsentRecorded);
                        string content = ledger.TryLog(key, request.Name, out var owned) && owned != null
                            ? owned.Text : NativeTextReference(current.FileContent);
                        int audio = current.AttachedAudioFile == 0u ? -1 : (int)current.AttachedAudioFile;
                        if (!ledger.HasRoomFor(ledger.HoldsLog(key, request.Name)))
                            return Refused(ResultCode.TerminalLedgerFull);
                        if (terminal.IsLogVisible(request.Name) == visible)
                        {
                            ledger.RecordLog(key, request.Name, content, audio, visible, false);
                            return Issued(ResultCode.TerminalContentLogPresentRecorded);
                        }
                        terminal.SetLogVisible(request.Name, visible);
                        if (terminal.IsLogVisible(request.Name) != visible)
                            return Unknown(ResultCode.TerminalContentVisibilityUnconfirmed);
                        ledger.RecordLog(key, request.Name, content, audio, visible, false);
                        return Issued(visible ? ResultCode.TerminalContentLogShownRecorded : ResultCode.TerminalContentLogHiddenRecorded);
                    }
                default:
                    return Refused(ResultCode.TerminalContentOperationUnknown);
            }
        }
        catch (Exception error)
        {
            _report("map.terminal-content-log-exception: " + error.GetType().Name);
            return Unknown(ResultCode.NativeCommitException);
        }
    }

    /// <summary>The content read of `forge.query.map.terminal_content`: whether the terminal already holds the
    /// named command or log, and whether that content sits on the list a player reads. The `kind` member chooses
    /// the table — the interpreter's own command table for `command`, the terminal's own local log table for
    /// `log` — and each answer is that table's own reading: `TryGetCommand` plus `CommandIsHidden` for a command,
    /// `GetLocalLogs` plus `IsLogVisible` for a log. A terminal this world cannot resolve, or one whose own
    /// interpreter is gone, answers null, which the row refuses rather than reporting absent content.</summary>
    internal TerminalContentContract.TerminalContentReading? ReadContent(EntityReference reference, string kind, string text)
    {
        if (!Resolve(reference, out var terminal, out _)) return null;
        try
        {
            if (kind == "log")
            {
                bool present = Holds(terminal, text);
                return new TerminalContentContract.TerminalContentReading(present, present && terminal.IsLogVisible(text));
            }
            var interpreter = terminal.m_command;
            if (interpreter == null || interpreter.WasCollected) return null;
            if (!interpreter.TryGetCommand(text, out var command, out var first, out var second)
                || !string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(second))
                return new TerminalContentContract.TerminalContentReading(false, false);
            return new TerminalContentContract.TerminalContentReading(true, !terminal.CommandIsHidden(command));
        }
        catch (Exception error)
        {
            ReportRead(error);
            return null;
        }
    }

    /// <summary>Whether the terminal already holds a log file of this name.</summary>
    private static bool Holds(LG_ComputerTerminal terminal, string name)
        => FindLog(terminal, name) != null;

    internal static TerminalLogFileData? FindLog(LG_ComputerTerminal terminal, string name)
    {
        var logs = terminal.GetLocalLogs();
        if (logs == null || logs.WasCollected) throw new InvalidOperationException(ResultCode.TerminalContentLogTableUnavailable);
        var entries = logs.GetEnumerator();
        while (entries.MoveNext())
        {
            var data = entries.Current.Value;
            if (data != null && string.Equals(data.FileName, name, StringComparison.OrdinalIgnoreCase)) return data;
        }
        return null;
    }

    private static bool LogMatches(TerminalLogFileData data, string rendered, int sound)
        => string.Equals((string?)data.FileContent, rendered, StringComparison.Ordinal)
            && data.AttachedAudioFile == (sound >= 0 ? (uint)sound : 0u);

    private static string NativeTextReference(LocalizedText? text)
    {
        if (text is not { } value) return "";
        uint id = value.Id != 0 ? value.Id : value.OldId;
        return id != 0 && !TerminalLogReading.IsDerived(id) ? TextCardValue.Reference(TextResourceContract.TextValue.OfBlock(id))
            : value.UntranslatedText ?? "";
    }

    /// <summary>One terminal behind one recipient reference, checked the way the terminal family checks its own:
    /// the kernel still has to answer for the reference, the address has to read with the terminal category's
    /// grammar, and the instance the address resolves to has to still read as that address.</summary>
    private bool Resolve(EntityReference? reference, out LG_ComputerTerminal terminal, out string refusal)
    {
        terminal = null!;
        if (reference == null || TerminalObjectActions.Address(reference) is not { } parsed)
        {
            refusal = ResultCode.TerminalUnavailable;
            return false;
        }
        if (!_kernel.IsEntityCurrent(reference))
        {
            refusal = ResultCode.StaleEntity;
            return false;
        }
        LG_ComputerTerminal? resolved;
        try { resolved = TerminalObservation.ByAddress(parsed); }
        catch (Exception error)
        {
            _report("map.terminal-content-resolve-exception: " + error.GetType().Name);
            refusal = ResultCode.StaleEntity;
            return false;
        }
        if (resolved == null) { refusal = ResultCode.StaleEntity; return false; }
        if (_kernel.ResolveEntityInstance(EntityKind, resolved) is not { } verified || verified != reference)
        {
            refusal = ResultCode.TerminalIdentityMismatch;
            return false;
        }
        if (!TerminalObservation.IsCurrentAddress(resolved, parsed))
        {
            refusal = ResultCode.StaleEntity;
            return false;
        }
        terminal = resolved;
        refusal = "";
        return true;
    }

    /// <summary>How one recipient's row ends: its status, commit state and code.</summary>
    private readonly record struct RowEnd(Status Status, Commit Commit, string Code);

    private static RowEnd Issued(string code) => new(Status.Succeeded, Commit.Confirmed, code);

    private static RowEnd Refused(string code) => new(Status.Rejected, Commit.None, code);

    private static RowEnd Unknown(string code) => new(Status.Failed, Commit.Unknown, code);
}
