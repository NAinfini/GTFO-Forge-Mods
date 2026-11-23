using System;
using System.Runtime.InteropServices;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// One terminal's own log table, read for the `forge.trigger.interaction.terminal_log_read` row. The game tells a
/// machine that a log was read either by the name the player typed (`ReadLog`) or by the file's content id, which
/// is all the replication packet carries (`pLogRead.ID` == `TerminalLogFileData.FileContent.Id`,
/// `ReadLog` RVA 0x128C080 / `OnReceiveLogRead` RVA 0x1289FE0, both frozen in evidence/door-terminal-hooks.json).
///
/// The table is the terminal's own authored state: `m_localLogs` is keyed by `FileName.ToUpper()` and every
/// record carries the authored `FileName` and that same content id, so a read is answered with the name the level
/// author wrote and never with the spelling the player typed. It is read as the terminal's own field rather than
/// through the content action's readback, because the action layer's own evidence classifies that readback as part
/// of the write path it carries; this reader only reads. An id of zero names nothing — the game uses it for a log
/// with no localization entry — and an id that matches no record, or more than one, is refused with the reason
/// instead of being published under a guessed name.
/// </summary>
internal static class TerminalLogReading
{
    /// <summary>The refusals this reader answers with, one cause each, so a report names what happened rather
    /// than which lookup failed.</summary>
    internal const string NoId = "the identity packet carries content id 0, which names no log file";
    internal const string NoTable = "the terminal reports no local log table";
    internal const string UnknownId = "no log file of this terminal carries that content id";
    internal const string UnknownName = "this terminal holds no log file of that name";
    internal const string Unreadable = "the terminal is not readable any more";

    /// <summary>The content id a log this mod adds at run time carries. The packet names a file by that id alone, so
    /// a log with no Text block of its own gets one derived from its authored name: the 32-bit FNV-1a of the
    /// upper-cased name's UTF-16 bytes (the spelling the terminal's own table keys by) with the top bit set. The top bit keeps it
    /// out of the range of the game's own Text ids, the value never depends on the language, the machine, the order
    /// logs were added in or whether the log arrived by command, restore or migration, and it is never 0.</summary>
    internal static uint IdFor(string name)
        => ForgeCore.Fnv.Hash32(MemoryMarshal.AsBytes(name.ToUpperInvariant().AsSpan())) | 0x80000000u;

    /// <summary>Whether the id lies in the range <see cref="IdFor"/> derives from. A derived id names a log of this
    /// terminal only; it is not a Text block id and is never turned into a text reference.</summary>
    internal static bool IsDerived(uint id) => (id & 0x80000000u) != 0;

    /// <summary>Whether another log of the terminal already carries <paramref name="name"/>'s derived id, which
    /// would make a packet's id name two files. The caller refuses the write instead of publishing under a guess.</summary>
    internal static bool Collides(LG_ComputerTerminal terminal, string name)
    {
        uint id = IdFor(name);
        if (Table(terminal, out var logs, out _)) return false;
        var enumerator = logs!.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var data = enumerator.Current.Value;
            if (data != null && IdOf(data) == id
                && !string.Equals(data.FileName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The file name one `READLOG` argument names. The game matches the argument against the table's
    /// own upper-cased keys, and this reader matches it against the authored names the same way — case
    /// insensitively, so a culture's casing rules cannot make a readable log unreadable — and then answers the
    /// name through the same id lookup every other read uses, so both entries this row patches agree on the file
    /// they report.</summary>
    internal static (string? Name, string? Refusal) ByCommandArgument(LG_ComputerTerminal? terminal, string? argument)
    {
        if (Table(terminal, out var logs, out var refusal)) return (null, refusal);
        if (string.IsNullOrEmpty(argument)) return (null, UnknownName);
        uint id = 0;
        bool found = false;
        var enumerator = logs!.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var entry = enumerator.Current;
            var data = entry.Value;
            if (data == null || !string.Equals(data.FileName, argument, StringComparison.OrdinalIgnoreCase)) continue;
            id = IdOf(data);
            found = true;
            break;
        }
        if (!found) return (null, UnknownName);
        return ById(terminal, id);
    }

    /// <summary>The file name one content id names, read out of the same table. Two records carrying one id
    /// would make the name ambiguous, which is refused rather than resolved to one of them.</summary>
    internal static (string? Name, string? Refusal) ById(LG_ComputerTerminal? terminal, uint id)
    {
        if (id == 0) return (null, NoId);
        if (Table(terminal, out var logs, out var refusal)) return (null, refusal);
        string? name = null;
        var enumerator = logs!.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var data = enumerator.Current.Value;
            if (data == null || IdOf(data) != id) continue;
            if (name != null && !string.Equals(name, data.FileName, StringComparison.Ordinal)) return (null, UnknownId);
            name = data.FileName;
        }
        return string.IsNullOrEmpty(name) ? (null, UnknownId) : (name, null);
    }

    /// <summary>The content id one record carries. A record with no localized content carries none, which is the
    /// same reading as the id of zero the packet's own field uses for a log with nothing to show.</summary>
    private static uint IdOf(TerminalLogFileData data)
        => data.FileContent is { } content ? content.Id : 0;

    /// <summary>The player the terminal's own synced interaction source names, or null when the terminal
    /// reports none. The read callbacks carry no player, and the terminal is where the command was typed, so
    /// this is the one native member that names the reader — the same reading the terminal state row does.</summary>
    internal static object? Actor(LG_ComputerTerminal? terminal)
        => terminal == null || terminal.WasCollected || !terminal.m_hasInteractingPlayer
            ? null : terminal.m_syncedInteractionSource;

    /// <summary>The terminal's own log table, or the reason it cannot be read. One place decides what an
    /// unreadable table is, so both lookups refuse for the same cause.</summary>
    private static bool Table(LG_ComputerTerminal? terminal,
        out Il2CppSystem.Collections.Generic.Dictionary<string, TerminalLogFileData>? logs, out string? refusal)
    {
        logs = null;
        refusal = null;
        if (terminal == null || terminal.WasCollected) { refusal = Unreadable; return true; }
        if (terminal.m_localLogs is not { } table || table.WasCollected) { refusal = NoTable; return true; }
        logs = table;
        return false;
    }
}
