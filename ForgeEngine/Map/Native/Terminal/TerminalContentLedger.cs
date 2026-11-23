using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using LevelGeneration;
using Localization;

namespace ForgeMap.Native;

/// <summary>
/// The one replicated record of what Forge wrote into terminals: command registrations, log files and printed
/// lines. The game replicates only a terminal's used and removed command arrays; the command table, its help
/// text, the local log table and the line buffer are per machine, so a plain native write would exist on the
/// host alone. The host therefore records every confirmed write here, and the record travels the Runtime's own
/// native-state path (`DomainStateChannel.RegisterNativeState`), which is what carries state to a late joiner, a
/// reconnecting client, a checkpoint restore and a host migration. Every machine — the host included — applies
/// the record to its own terminals, idempotently, from the in-level frame until each entry has landed.
///
/// There is no second transport, scheduler or state service: this class owns a list, a frame callback and the
/// registration of one state source, and releases all three on dispose. The record is bounded; a write that would
/// exceed the bound is refused before it reaches the native terminal, never truncated afterwards.
///
/// Command visibility (show / hide / remove) is not recorded: the game replicates it itself through the
/// terminal's synchronized hide and show entries.
/// </summary>
internal sealed class TerminalContentLedger : IDisposable
{
    internal const string StateKey = "forge.map.terminal-content";
    internal const int MaximumEntries = 256;
    internal const string PendingCode = "terminal-ledger-terminal-unresolved";

    internal sealed record CommandEntry(string Terminal, int Slot, string Name, string Text, string Rule);
    internal sealed record LogEntry(string Terminal, string Name, string Text, int Sound, bool Visible, bool Removed);
    internal sealed record LineEntry(string Terminal, int Sequence, TerminalLineSeverity Severity, string Text);

    // Frames an unresolved entry waits before the next attempt (the zone-light ledger's pace); a new write resets it.
    private const int RetryFrames = 15;
    private int _retryIn;
    // Command and log entries that landed on this machine's terminals in the current language. A replaced entry is a
    // new record and so is not in here; a language change empties it so every text is rendered again.
    private readonly HashSet<object> _applied = new(ReferenceEqualityComparer.Instance);

    private readonly MapTextLanguage _language;
    private readonly Func<string, LG_ComputerTerminal?> _resolve;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly List<CommandEntry> _commands = new();
    private readonly List<LogEntry> _logs = new();
    private readonly List<LineEntry> _lines = new();
    // Lines this machine already printed, by sequence. A restore never prints a line twice.
    private readonly HashSet<int> _printed = new();
    private int _nextSequence = 1;
    private bool _disposed;
    private bool _unresolvedReported;
    private bool _hasLocalizedContent;
    private bool _languageKnown;
    private Language _lastLanguage;

    internal TerminalContentLedger(IForgeEngine kernel, MapTextLanguage language, Func<string, LG_ComputerTerminal?> resolve, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _language = language ?? throw new ArgumentNullException(nameof(language));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _state = DomainStateChannel.RegisterNativeState(kernel, StateKey, Capture, Restore);
    }

    /// <summary>The terminal a recorded address names on this machine, or null while the level has not built it.</summary>
    internal static LG_ComputerTerminal? ResolveByAddress(string text)
        => MapObjectTerminalAddress.TryParse(text) is { } address ? TerminalObservation.ByAddress(address) : null;

    /// <summary>Entries not yet applied to this machine's terminals.</summary>
    internal bool Pending { get; private set; }

    internal int EntryCount => _commands.Count + _logs.Count + _lines.Count;
    internal IReadOnlyList<CommandEntry> Commands => _commands;
    internal IReadOnlyList<LogEntry> Logs => _logs;
    internal IReadOnlyList<LineEntry> Lines => _lines;

    /// <summary>Whether a write of this kind still fits; asked before the native terminal is touched.</summary>
    internal bool HasRoomFor(bool replacesExisting) => replacesExisting || EntryCount < MaximumEntries;

    internal bool HoldsCommand(string terminal, int slot) => _commands.Any(entry => entry.Terminal == terminal && entry.Slot == slot);
    internal bool TryCommand(string terminal, int slot, out CommandEntry? entry)
    {
        entry = _commands.FirstOrDefault(item => item.Terminal == terminal && item.Slot == slot);
        return entry != null;
    }
    internal bool HoldsLog(string terminal, string name)
        => _logs.Any(entry => entry.Terminal == terminal && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    internal bool TryLog(string terminal, string name, out LogEntry? entry)
    {
        entry = _logs.FirstOrDefault(item => item.Terminal == terminal
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return entry != null;
    }
    internal bool HasRoomForLine() => EntryCount < MaximumEntries;

    internal void RecordCommand(string terminal, int slot, string name, string text, string rule)
    {
        var entry = new CommandEntry(terminal, slot, name, text, rule);
        int index = _commands.FindIndex(item => item.Terminal == terminal && item.Slot == slot);
        if (index >= 0) { _applied.Remove(_commands[index]); _commands[index] = entry; } else _commands.Add(entry);
        Changed();
    }

    internal void RecordLog(string terminal, string name, string text, int sound, bool visible, bool removed)
    {
        var entry = new LogEntry(terminal, name, text, sound, visible, removed);
        int index = _logs.FindIndex(item => item.Terminal == terminal
            && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) { _applied.Remove(_logs[index]); _logs[index] = entry; } else _logs.Add(entry);
        Changed();
    }

    /// <summary>Records a line the host has just printed; the host does not print it a second time.</summary>
    internal void RecordLine(string terminal, TerminalLineSeverity severity, string text)
    {
        int sequence = _nextSequence++;
        _lines.Add(new LineEntry(terminal, sequence, severity, text));
        _printed.Add(sequence);
        Changed();
    }

    private void Changed()
    {
        RecomputeLocalizedContent();
        Pending = true;
        _retryIn = 0;
        _unresolvedReported = false;
        _state.Changed();
    }

    /// <summary>Updates the frame-path flag only when the bounded ledger changes. Stable frames never scan
    /// entries or parse text references.</summary>
    private void RecomputeLocalizedContent()
        => _hasLocalizedContent = _commands.Any(entry => StationTextValue.RequiresLanguage(entry.Text))
            || _logs.Any(entry => !entry.Removed && StationTextValue.RequiresLanguage(entry.Text))
            || _lines.Any(entry => StationTextValue.RequiresLanguage(entry.Text));

    internal bool TryRenderCurrent(string reference, out string rendered, out string code)
    {
        if (!StationTextValue.RequiresLanguage(reference))
        {
            rendered = reference;
            code = "";
            return true;
        }
        if (!_language.TryRead(out var language, out code, out _))
        {
            rendered = "";
            return false;
        }
        _lastLanguage = language;
        _languageKnown = true;
        return TryRender(reference, language, out rendered, out code);
    }


    private static bool TryRender(string reference, Language language, out string rendered, out string code)
    {
        if (StationTextValue.TryRender(reference, language, out rendered))
        {
            code = "";
            return true;
        }
        rendered = "";
        code = ResultCode.TextResourceMissing;
        return false;
    }

    internal JsonElement Capture() => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = FormatVersion.Current,
        next = _nextSequence,
        commands = _commands.Select(entry => new { entry.Terminal, entry.Slot, entry.Name, entry.Text, entry.Rule }).ToArray(),
        logs = _logs.Select(entry => new { entry.Terminal, entry.Name, entry.Text, entry.Sound, entry.Visible, entry.Removed }).ToArray(),
        lines = _lines.Select(entry => new { entry.Terminal, entry.Sequence, Severity = entry.Severity.ToString(), entry.Text }).ToArray()
    });

    /// <summary>Takes a complete record from the state path: the whole record is read and checked first, so a
    /// malformed record leaves this machine's ledger as it was.</summary>
    internal void Restore(JsonElement data)
    {
        if (_disposed) return;
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("terminal-ledger-object");
        string[] fields = { "schemaVersion", "next", "commands", "logs", "lines" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name))
                throw new FormatException("terminal-ledger-field");
        if (seen.Count != fields.Length) throw new FormatException("terminal-ledger-field");
        if (data.GetProperty("schemaVersion").GetString() != FormatVersion.Current) throw new FormatException("terminal-ledger-version");
        int next = data.GetProperty("next").GetInt32();
        var commands = new List<CommandEntry>();
        var logs = new List<LogEntry>();
        var lines = new List<LineEntry>();
        foreach (var item in Items(data, "commands"))
            commands.Add(new(Str(item, "Terminal"), item.GetProperty("Slot").GetInt32(), Str(item, "Name"), Str(item, "Text"), Str(item, "Rule")));
        foreach (var item in Items(data, "logs"))
            logs.Add(new(Str(item, "Terminal"), Str(item, "Name"), Str(item, "Text"), item.GetProperty("Sound").GetInt32(),
                item.GetProperty("Visible").GetBoolean(), item.GetProperty("Removed").GetBoolean()));
        foreach (var item in Items(data, "lines"))
        {
            if (!Enum.TryParse<TerminalLineSeverity>(Str(item, "Severity"), out var severity)) throw new FormatException("terminal-ledger-severity");
            lines.Add(new(Str(item, "Terminal"), item.GetProperty("Sequence").GetInt32(), severity, Str(item, "Text")));
        }
        if (commands.Count + logs.Count + lines.Count > MaximumEntries || next < 1) throw new FormatException("terminal-ledger-size");
        _commands.Clear(); _commands.AddRange(commands);
        _logs.Clear(); _logs.AddRange(logs);
        _lines.Clear(); _lines.AddRange(lines);
        _nextSequence = Math.Max(_nextSequence, next);
        RecomputeLocalizedContent();
        _applied.Clear();
        _retryIn = 0;
        _unresolvedReported = false;
        Pending = true;
        Frame();
    }

    private static IEnumerable<JsonElement> Items(JsonElement data, string name)
    {
        var array = data.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException("terminal-ledger-array");
        return array.EnumerateArray().ToArray();
    }

    private static string Str(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.GetProperty(name).ValueKind == JsonValueKind.String
            ? item.GetProperty(name).GetString()! : throw new FormatException("terminal-ledger-text");

    /// <summary>Applies every entry that has not landed on this machine yet. An entry whose terminal this
    /// machine cannot resolve yet stays pending and is retried on the next frame; it is reported once.</summary>
    internal void Frame()
    {
        if (_disposed) return;
        Language language = default;
        bool languageChanged = false;
        if (_hasLocalizedContent)
        {
            if (!_language.TryRead(out language, out var languageCode, out _))
            {
                Pending = true;
                if (!_unresolvedReported) { _unresolvedReported = true; Report(languageCode); }
                return;
            }
            languageChanged = !_languageKnown || _lastLanguage != language;
            _lastLanguage = language;
            _languageKnown = true;
        }
        if (!Pending && !languageChanged) return;
        if (languageChanged) { _applied.Clear(); _retryIn = 0; }
        else if (_retryIn > 0) { _retryIn--; return; }
        bool complete = true;
        for (int index = 0; index < _commands.Count; index++)
        {
            var entry = _commands[index];
            if (_applied.Contains(entry)) continue;
            if (Apply(entry, language)) _applied.Add(entry); else complete = false;
        }
        for (int index = 0; index < _logs.Count; index++)
        {
            var entry = _logs[index];
            if (_applied.Contains(entry)) continue;
            if (Apply(entry, language)) _applied.Add(entry); else complete = false;
        }
        for (int index = 0; index < _lines.Count; index++) complete &= Apply(_lines[index], language);
        Pending = !complete;
        if (!complete) _retryIn = RetryFrames;
        if (!complete && !_unresolvedReported)
        {
            _unresolvedReported = true;
            Report(PendingCode);
        }
        else if (complete) _unresolvedReported = false;
    }

    private bool Apply(CommandEntry entry, Language language)
    {
        try
        {
            if (_resolve(entry.Terminal) is not { } terminal) return false;
            var interpreter = terminal.m_command;
            if (interpreter == null || interpreter.WasCollected) return false;
            if (!TryRender(entry.Text, language, out var rendered, out _)) return false;
            var command = (TERM_Command)entry.Slot;
            if (interpreter.TryGetCommand(entry.Name, out var parsed, out var first, out var second))
            {
                if (parsed != command || !string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(second)
                    || terminal.GetCommandRule(command) != TerminalContentActions.Rule(entry.Rule)) return false;
            }
            else
            {
                if (TerminalContentActions.SlotOccupied(interpreter, command)) return false;
                interpreter.AddCommand(command, entry.Name, TextCardValue.FromRendered(rendered), TerminalContentActions.Rule(entry.Rule),
                    new Il2CppSystem.Collections.Generic.List<WardenObjectiveEventData>());
            }
            if (!interpreter.TryGetCommand(entry.Name, out parsed, out first, out second) || parsed != command
                || !string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(second)
                || !interpreter.TryGetCommandName(command, out var name)
                || !string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)
                || terminal.GetCommandRule(command) != TerminalContentActions.Rule(entry.Rule)) return false;
            var help = interpreter.m_commandHelpStrings;
            if (help == null || help.WasCollected) return false;
            help[command] = TextCardValue.FromRendered(rendered);
            return help.ContainsKey(command) && string.Equals((string)help[command], rendered, StringComparison.Ordinal);
        }
        catch (Exception error) { Report("terminal-ledger-command-exception", error.GetType().Name); return false; }
    }

    private bool Apply(LogEntry entry, Language language)
    {
        try
        {
            if (_resolve(entry.Terminal) is not { } terminal) return false;
            var existing = TerminalContentActions.FindLog(terminal, entry.Name);
            if (entry.Removed)
            {
                if (existing != null) terminal.RemoveLocalLog(entry.Name);
                return TerminalContentActions.FindLog(terminal, entry.Name) == null;
            }
            if (!TryRender(entry.Text, language, out var rendered, out _)) return false;
            if (existing == null)
            {
                if (TerminalLogReading.Collides(terminal, entry.Name)) return false;
                var data = new TerminalLogFileData
                {
                    FileName = entry.Name,
                    FileContent = TextCardValue.FromRendered(rendered, TerminalLogReading.IdFor(entry.Name))
                };
                if (entry.Sound >= 0) data.AttachedAudioFile = (uint)entry.Sound;
                terminal.AddLocalLog(data, true);
            }
            var current = TerminalContentActions.FindLog(terminal, entry.Name);
            if (current == null) return false;
            current.FileContent = TextCardValue.FromRendered(rendered, TerminalLogReading.IdFor(entry.Name));
            current.AttachedAudioFile = entry.Sound >= 0 ? (uint)entry.Sound : 0u;
            if (terminal.IsLogVisible(entry.Name) != entry.Visible) terminal.SetLogVisible(entry.Name, entry.Visible);
            current = TerminalContentActions.FindLog(terminal, entry.Name);
            return current != null && string.Equals((string?)current.FileContent, rendered, StringComparison.Ordinal)
                && current.AttachedAudioFile == (entry.Sound >= 0 ? (uint)entry.Sound : 0u)
                && terminal.IsLogVisible(entry.Name) == entry.Visible;
        }
        catch (Exception error) { Report("terminal-ledger-log-exception", error.GetType().Name); return false; }
    }

    private bool Apply(LineEntry entry, Language language)
    {
        if (_printed.Contains(entry.Sequence)) return true;
        try
        {
            if (_resolve(entry.Terminal) is not { } terminal) return false;
            if (!TryRender(entry.Text, language, out var rendered, out _)) return false;
            terminal.AddLine(TerminalActions.LineType(entry.Severity), rendered, 0f);
            _printed.Add(entry.Sequence);
            return true;
        }
        catch (Exception error) { Report("terminal-ledger-line-exception", error.GetType().Name); return false; }
    }

    /// <summary>A world change or a stopped runtime ends the record: the terminals it names are gone.</summary>
    internal void Clear()
    {
        _commands.Clear(); _logs.Clear(); _lines.Clear(); _printed.Clear(); _applied.Clear(); _retryIn = 0;
        _nextSequence = 1; Pending = false; _unresolvedReported = false; _hasLocalizedContent = false; _languageKnown = false;
    }

    private void Report(string code, string? exceptionType = null)
    {
        try { _report(exceptionType == null ? "map." + code : "map." + code + ": " + exceptionType); }
        catch (Exception reportError) { MapLog.Contained("terminal-content.report", reportError); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _state.Dispose();
    }
}
