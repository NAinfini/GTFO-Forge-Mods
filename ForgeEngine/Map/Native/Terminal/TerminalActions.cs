using System;
using ForgeEngine.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The severity a printed line carries. The terminal's own line vocabulary has exactly these three
/// readings, so a warning and an error are the game's own line kinds rather than a colour this layer invents.
/// </summary>
internal enum TerminalLineSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// The terminal half of the native execution layer: one method per terminal action, each taking the terminal the
/// address layer resolved and the address it was resolved through, and each writing through the terminal's own
/// member. As with the door half, nothing here is a plan, a handle or a result frame.
///
/// The command path deliberately goes through the game's own two members instead of a table of its own: the
/// terminal's own interpreter parses the line a player would type, so a line the terminal does not know can
/// never become a command, and the terminal manager's own command entry sends the parsed command with the
/// parameters the interpreter produced. The whitelist below is therefore the inner gate, not the only one.
///
/// The output path writes the terminal's own line buffer on this machine; the buffer is per machine (the
/// replicated terminal state carries used and removed commands, not lines), so the caller also records the line
/// in the <see cref="TerminalContentLedger"/>, which is what carries it to the peers.
/// </summary>
internal static class TerminalActions
{
    /// <summary>The line reached the terminal's own buffer on this machine. It never reaches a result: the output
    /// row reports the ledger record that follows, so this is this layer's own word and not a result code.</summary>
    internal const string LineAdded = "terminal-line-added";

    /// <summary>Runs one whitelisted terminal command. The command and its arguments are the row's own two
    /// inputs and are composed into the input line the terminal's interpreter expects, exactly as a player would
    /// have typed it; the interpreter then names the command and both parameters, and the manager's entry is
    /// what sends it. The actor a row may resolve is not passed on: the native command entry carries no player
    /// (the replicated command payload is a terminal id, a command and three strings), so naming one here would
    /// be an actor the game never receives.</summary>
    internal static MapActionOutcome Command(LG_ComputerTerminal terminal, MapObjectReference address,
        string? command, string? arguments)
    {
        if (Unusable(terminal, address) is { } refused) return refused;
        if (string.IsNullOrWhiteSpace(command)) return MapActionOutcome.Refused(ResultCode.TerminalCommandEmpty);
        var manager = LG_ComputerTerminalManager.Current;
        if (manager == null || manager.WasCollected) return MapActionOutcome.Refused(ResultCode.TerminalManagerUnavailable);
        var interpreter = terminal.m_command;
        if (interpreter == null || interpreter.WasCollected) return MapActionOutcome.Refused(ResultCode.TerminalInterpreterUnavailable);
        string line = string.IsNullOrWhiteSpace(arguments) ? command : command + " " + arguments;
        if (!interpreter.TryGetCommand(line, out var parsed, out var param1, out var param2))
            return MapActionOutcome.Refused(ResultCode.TerminalCommandUnknown);
        if (!Allowed(parsed)) return MapActionOutcome.Refused(ResultCode.TerminalCommandNotAllowed);
        LG_ComputerTerminalManager.WantToSendTerminalCommand(terminal.SyncID, parsed, line, param1, param2);
        return MapActionOutcome.Issued(ResultCode.TerminalCommandSent);
    }

    /// <summary>Adds one line to the terminal's own local line buffer, in the line kind the requested severity
    /// names. The native member's third argument is passed as its own zero, because the evidence for this build
    /// does not establish what that argument means; nothing is derived from a tick count here.</summary>
    internal static MapActionOutcome Print(LG_ComputerTerminal terminal, MapObjectReference address,
        TerminalLineSeverity severity, string? text)
    {
        if (Unusable(terminal, address) is { } refused) return refused;
        if (!Enum.IsDefined(typeof(TerminalLineSeverity), severity)) return MapActionOutcome.Refused(ResultCode.TerminalSeverityUnknown);
        if (string.IsNullOrEmpty(text)) return MapActionOutcome.Refused(ResultCode.TerminalTextEmpty);
        terminal.AddLine(LineType(severity), text, 0f);
        return MapActionOutcome.Issued(LineAdded);
    }

    /// <summary>The line kind a severity prints as, in the terminal's own vocabulary: a plain line, its warning
    /// line and its failing line.</summary>
    internal static TerminalLineType LineType(TerminalLineSeverity severity) => severity switch
    {
        TerminalLineSeverity.Warning => TerminalLineType.Warning,
        TerminalLineSeverity.Error => TerminalLineType.Fail,
        _ => TerminalLineType.Normal
    };

    /// <summary>The commands this provider runs. The terminal's own parser already refuses anything a player
    /// could not type, so this list is the inner gate: the commands that act on the level or answer about it.
    /// The terminal's own session and screen commands are not in it — `help`, `commands`, `cls` and `exit`
    /// address the sitting player's screen, and the interpreter's own markers (`none`, `empty_line`,
    /// `invalid_command`, `used_command` and the array-size sentinel) are not commands a request may name.</summary>
    private static bool Allowed(TERM_Command command) => command switch
    {
        TERM_Command.Open or TERM_Command.Close or TERM_Command.Activate or TERM_Command.Deactivate
            or TERM_Command.DownloadData or TERM_Command.ViewSecurityLog or TERM_Command.Override
            or TERM_Command.DisableAlarm or TERM_Command.Locate or TERM_Command.ActivateBeacon
            or TERM_Command.Find or TERM_Command.ShowList or TERM_Command.Query or TERM_Command.Ping
            or TERM_Command.ReactorStartup or TERM_Command.ReactorVerify or TERM_Command.ReactorShutdown
            or TERM_Command.WardenObjectiveSpecialCommand or TERM_Command.TerminalUplinkConnect
            or TERM_Command.TerminalUplinkVerify or TERM_Command.TerminalUplinkConfirm
            or TERM_Command.ListLogs or TERM_Command.ReadLog or TERM_Command.Start
            or TERM_Command.TryUnlockingTerminal or TERM_Command.WardenObjectiveGatherCommand
            or TERM_Command.TerminalCorruptedUplinkConnect or TERM_Command.TerminalCorruptedUplinkVerify
            or TERM_Command.TimedConnectionSend or TERM_Command.TimedConnectionVerify
            or TERM_Command.UniqueCommand1 or TERM_Command.UniqueCommand2 or TERM_Command.UniqueCommand3
            or TERM_Command.UniqueCommand4 or TERM_Command.UniqueCommand5 or TERM_Command.Info => true,
        _ => false
    };

    /// <summary>The gate both terminal actions pass: the instance must read, and it must still be the terminal
    /// the address was resolved to. A terminal that was spawned into another zone or moved to another position
    /// in its zone's list is refused rather than written to.</summary>
    private static MapActionOutcome? Unusable(LG_ComputerTerminal terminal, MapObjectReference address)
        => terminal == null || terminal.WasCollected ? MapActionOutcome.Refused(ResultCode.TerminalUnavailable)
            : TerminalObservation.IsCurrentAddress(terminal, address) ? null
            : MapActionOutcome.Refused(ResultCode.StaleEntity);
}
