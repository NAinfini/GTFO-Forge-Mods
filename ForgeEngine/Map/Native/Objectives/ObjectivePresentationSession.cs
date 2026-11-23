using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Il2CppInterop.Runtime;
using LevelGeneration;
using Localization;

namespace ForgeMap.Native;

/// <summary>Only author display receipts live here. Progress, QUERY items and scheduling remain native/Runtime.</summary>
internal sealed class ObjectivePresentationSession : IDisposable
{
    private sealed record Entry(string Identity, int Layer, int Priority, bool Independent,
        TextResourceContract.TextValue Header, TextResourceContract.TextValue Body);
    private sealed class Row
    {
        internal Entry Value;
        internal readonly int Id;
        internal IntPtr NativeIdentity;
        internal int Priority;
        internal string? LastHeader, LastBody;
        internal Il2CppSystem.Func<string> Header = null!, Body = null!;
        internal Row(Entry value, int id, ObjectivePresentationSession session)
        {
            Value = value; Id = id;
            session.ResolveNativeText(Value.Header, ref LastHeader);
            session.ResolveNativeText(Value.Body, ref LastBody);
            BindGetters();
        }
        internal void BindGetters()
        {
            Header = NativeGetter(() => LastHeader ?? string.Empty);
            Body = NativeGetter(() => LastBody ?? string.Empty);
        }
    }
    private sealed class Panel
    {
        internal PUI_GameObjectives Native;
        internal readonly IntPtr Identity;
        internal readonly Il2CppSystem.Func<string>?[] Originals = new Il2CppSystem.Func<string>?[3];
        internal readonly List<Row> Rows = new();
        internal int NextId = int.MinValue;
        internal int MainLayer = -1;
        internal bool Active = true;
        internal readonly Il2CppSystem.Func<string>?[] MainGetters = new Il2CppSystem.Func<string>?[3];
        internal readonly string?[] LastMainTexts = new string?[3];
        /// <summary>The text last handed to the native panel by a draw; a draw rewrites the main objective only when this differs.</summary>
        internal readonly string?[] DrawnMainTexts = new string?[3];
        internal Panel(PUI_GameObjectives native) { Native = native; Identity = native.Pointer; }
    }

    private readonly IForgeEngine _kernel;
    private readonly Func<string?> _level;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly DomainStateChannel.NativeStateSource _state;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly List<Panel> _panels = new();
    private readonly OnceReporter _once = new();
    private readonly HashSet<string> _endingPendingReasons = new(StringComparer.Ordinal);
    private TextResourceContract.TextValue?[] _main = new TextResourceContract.TextValue?[3];
    private Entry[] _entries = Array.Empty<Entry>();
    private string? _ownerLevel;
    private JsonElement _snapshot;
    private Language _textLanguage;
    private readonly MapTextLanguage _language;
    private bool _textLanguageKnown, _usesPlayerLanguage;
    private bool _drawing, _disposed;
    private long _worldEpoch;
    private long? _endingWorldEpoch;
    private readonly OnceReporter _pendingReportedReasons = new();
    internal bool Pending { get; private set; }
    internal string? PendingReason { get; private set; }

    internal ObjectivePresentationSession(IForgeEngine kernel, RuntimeModuleHandle registration,
        Func<string?> level, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel; _level = level; _canExecute = canExecute; _report = report;
        _language = new();
        _worldEpoch = kernel.WorldEpoch;
        _snapshot = Save(null, _main, _entries);
        _state = DomainStateChannel.RegisterNativeState(kernel, "forge.map.objective-text", () => _snapshot, Restore);
        _lifecycle = registration.ObserveLifecycle(value =>
        {
            // A late WorldChanged must not wipe what the new world already restored or drew; the epoch check makes it idempotent.
            if (value.Current.StartupState is RuntimeStartupState.Stopped or RuntimeStartupState.Failed) ClearWorld();
            else if (value.Kind == RuntimeLifecycleKind.WorldChanged) { if (!value.IsMigration) EnsureWorld(); }
            else if (value.Kind == RuntimeLifecycleKind.CheckpointRestored) Pending = true;
        }, replayCurrent: false);
    }

    internal CommandResult Main(CommandContext context)
    {
        if (!ObjectivePresentationContract.TryMain(context, out var request, out var code)) return ObjectivePresentationContract.Refused(context, false, code!);
        if (!Ready(context, out code)) return ObjectivePresentationContract.Refused(context, false, code!);
        if (!TryText(request.Text, out var rendered, out code))
            return ObjectivePresentationContract.Refused(context, false, code!);
        if (string.IsNullOrWhiteSpace(rendered)) return ObjectivePresentationContract.Refused(context, false, ResultCode.ObjectiveTextRequired);
        var main = (TextResourceContract.TextValue?[])_main.Clone(); main[request.Layer] = request.Text;
        Record(main, _entries);
        return ObjectivePresentationContract.MainWritten(context, request, rendered);
    }

    internal CommandResult List(CommandContext context)
    {
        if (!ObjectivePresentationContract.TryList(context, out var request, out var code)) return ObjectivePresentationContract.Refused(context, true, code!);
        if (!Ready(context, out code)) return ObjectivePresentationContract.Refused(context, true, code!);
        if (!request.Visible)
        {
            // Withdrawal goes through the same state as a write: the draw removes the native row
            // (RemoveProgressionObjective), clients and checkpoints receive the snapshot without it.
            if (Array.Exists(_entries, entry => entry.Identity == request.Entry))
                Record(_main, Array.FindAll(_entries, entry => entry.Identity != request.Entry));
            return ObjectivePresentationContract.ListWritten(context, request, "", "");
        }
        if (!TryText(request.Header, out var header, out code)
            || !TryText(request.Body, out var body, out code))
            return ObjectivePresentationContract.Refused(context, true, code!);
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(body))
            return ObjectivePresentationContract.Refused(context, true, ResultCode.ObjectiveTextRequired);
        var value = new Entry(request.Entry, request.Layer, request.Priority, request.LayerIndependent, request.Header, request.Body);
        int index = Array.FindIndex(_entries, entry => entry.Identity == request.Entry);
        var entries = index < 0 ? _entries.Append(value).ToArray() : (Entry[])_entries.Clone();
        if (index >= 0) entries[index] = value;
        Array.Sort(entries, Compare);
        Record(_main, entries);
        return ObjectivePresentationContract.ListWritten(context, request, header, body);
    }

    private bool Ready(CommandContext context, out string? code)
    {
        EnsureWorld();
        code = null;
        if (_disposed || !_canExecute() || !Session.IsHost) { code = ResultCode.AuthorityOrPhase; return false; }
        if (context.WorldEpoch != _kernel.WorldEpoch) { code = ResultCode.ObjectiveWorldStale; return false; }
        var current = _level();
        if (current == null || _ownerLevel != null && current != _ownerLevel)
        { code = ResultCode.ObjectiveLevelUnavailable; return false; }
        return true;
    }
    private void Record(TextResourceContract.TextValue?[] main, Entry[] entries)
    {
        var snapshot = Save(_level(), main, entries);
        _state.Changed();
        _ownerLevel = _level(); SetTexts(main, entries); _snapshot = snapshot;
        ObjectivePresentationLog.State("written", _ownerLevel, entries.Length);
        RequestRefresh(); Refresh();
    }
    private static int Compare(Entry left, Entry right)
    {
        int order = left.Priority.CompareTo(right.Priority);
        return order != 0 ? order : string.CompareOrdinal(left.Identity, right.Identity);
    }

    private static object SaveText(TextResourceContract.TextValue value)
        => new { stored = value.Stored, id = value.Stored ? value.Id : (uint?)null, literal = value.Literal };
    private static JsonElement Save(string? level, TextResourceContract.TextValue?[] main, Entry[] entries)
        => JsonSerializer.SerializeToElement(new
        {
            schemaVersion = FormatVersion.Current,
            level,
            main = main.Select((text, layer) => new { text, layer }).Where(value => value.text.HasValue)
                .Select(value => new { value.layer, text = SaveText(value.text!.Value) }).ToArray(),
            entries = entries.Select(entry => new
            {
                entry = entry.Identity,
                layer = entry.Layer,
                priority = entry.Priority,
                layerIndependent = entry.Independent,
                header = SaveText(entry.Header),
                body = SaveText(entry.Body)
            }).ToArray()
        });
    private static void Shape(JsonElement data, params string[] fields)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException("objective-text-state-object");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (Array.IndexOf(fields, property.Name) < 0 || !found.Add(property.Name)) throw new FormatException("objective-text-state-field");
        if (found.Count != fields.Length) throw new FormatException("objective-text-state-field");
    }
    private static TextResourceContract.TextValue ReadText(JsonElement data)
    {
        Shape(data, "stored", "id", "literal");
        bool stored = data.GetProperty("stored").GetBoolean();
        if (stored)
        {
            if (data.GetProperty("literal").ValueKind != JsonValueKind.Null) throw new FormatException("objective-text-state");
            return TextResourceContract.TextValue.OfBlock(data.GetProperty("id").GetUInt32());
        }
        string? literal = data.GetProperty("literal").GetString();
        if (data.GetProperty("id").ValueKind != JsonValueKind.Null || string.IsNullOrWhiteSpace(literal))
            throw new FormatException("objective-text-state");
        return TextResourceContract.TextValue.OfLiteral(literal);
    }
    private void Restore(JsonElement data)
    {
        EnsureWorld();
        Shape(data, "schemaVersion", "level", "main", "entries");
        if (!FormatVersion.Matches(data))
            throw new FormatException("objective-text-state-version");
        string? level = data.GetProperty("level").GetString();
        var current = _level();
        if (level != null && (string.IsNullOrWhiteSpace(level) || current != null && level != current))
            throw new FormatException("objective-text-state-level");
        var main = new TextResourceContract.TextValue?[3];
        foreach (var row in data.GetProperty("main").EnumerateArray())
        {
            Shape(row, "layer", "text");
            int layer = row.GetProperty("layer").GetInt32();
            if (layer < 0 || layer > 2 || main[layer].HasValue) throw new FormatException("objective-text-state-layer");
            main[layer] = ReadText(row.GetProperty("text"));
        }
        var entries = new List<Entry>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in data.GetProperty("entries").EnumerateArray())
        {
            Shape(row, "entry", "layer", "priority", "layerIndependent", "header", "body");
            string? identity = row.GetProperty("entry").GetString(); int layer = row.GetProperty("layer").GetInt32();
            if (!ObjectivePresentationContract.ValidIdentity(identity) || !identities.Add(identity!)
                || layer < 0 || layer > 2)
                throw new FormatException("objective-text-state-entry");
            entries.Add(new(identity!, layer, row.GetProperty("priority").GetInt32(),
                row.GetProperty("layerIndependent").GetBoolean(), ReadText(row.GetProperty("header")), ReadText(row.GetProperty("body"))));
        }
        if (level == null && (entries.Count != 0 || main.Any(text => text.HasValue)))
            throw new FormatException("objective-text-state-level");
        entries.Sort(Compare);
        _ownerLevel = level; SetTexts(main, entries.ToArray()); _snapshot = Save(level, main, _entries);
        ObjectivePresentationLog.State("restored", _ownerLevel, _entries.Length);
        // Full replacement also removes author entries created after the saved checkpoint. No actions are replayed.
        RequestRefresh();
    }

    /// <summary>The authored texts as the checkpoint state holds them: what a check puts back through <see cref="Replace"/>.</summary>
    internal JsonElement Snapshot => _snapshot;

    /// <summary>Replaces the authored texts with a snapshot and draws it at once, the way a checkpoint restore does.</summary>
    internal void Replace(JsonElement data)
    {
        Restore(data);
        _state.Changed();
        Refresh();
    }

    internal void RequestRefresh()
    {
        if (_disposed || _drawing) return;
        Pending = true;
    }
    /// <summary>The existing Map frame notices a local effective-language change and wakes the same presentation
    /// clock that already owns objective redraws. Rows and the main objective remain the same native objects.</summary>
    internal void TickLanguage()
    {
        if (_disposed || !UsesPlayerLanguage())
        {
            _textLanguageKnown = false;
            return;
        }
        if (!TryLanguage(out var language)) return;
        if (_textLanguageKnown && language == _textLanguage) return;
        _textLanguage = language;
        _textLanguageKnown = true;
        if (_ownerLevel != null) RequestRefresh();
    }
    internal void Refresh()
    {
        EnsureWorld();
        if (_disposed || _drawing || !Pending) return;
        if (UsesPlayerLanguage())
        {
            if (!TryLanguage(out var language)) return;
            _textLanguage = language;
            _textLanguageKnown = true;
        }
        else _textLanguageKnown = false;
        if (_ownerLevel == null && !Drawn()) { ClearPending(); return; }
        var current = _level();
        if (_ownerLevel != null && current != _ownerLevel) return;
        _drawing = true;
        try
        {
            var gui = GuiManager.Current;
            var player = Live(gui) ? gui!.m_playerLayer : null;
            var hud = Live(player) ? player!.m_wardenObjective : null;
            bool complete = Live(hud);
            if (complete) FindPanel(hud!);
            else ReportPending(!Live(gui) ? "objective-gui-unavailable"
                : !Live(player) ? "objective-player-panel-unavailable" : "objective-panel-unavailable");
            if (_panels.Count == 0) return; // Existing Map clock retries after GUI initialization.
            for (int i = _panels.Count - 1; i >= 0; i--)
            {
                var panel = _panels[i];
                if (!Live(panel.Native)) { _panels.RemoveAt(i); continue; }
                try { complete &= Draw(panel); }
                catch (Exception error) { complete = false; Report("objective-text-native-failed", RuntimeDiagnostics.Describe(error)); ReportPending("objective-text-native-failed"); }
            }
            if (!complete) ReportPending(PendingReason ?? "objective-panel-unavailable");
            else
            {
                ObjectivePresentationLog.Presented(_ownerLevel, _entries.Length, _panels.Count);
                ClearPending(); _once.Reset();
            }
        }
        catch (Exception error)
        {
            Report("objective-text-native-failed", RuntimeDiagnostics.Describe(error));
            ReportPending("objective-text-native-failed");
        }
        finally { _drawing = false; }
    }

    /// <summary>The one writer of the texts: whether any of them follows the player's language is decided here,
    /// when they change, so the per-tick language check reads a flag instead of building each reference again.</summary>
    private void SetTexts(TextResourceContract.TextValue?[] main, Entry[] entries)
    {
        _main = main; _entries = entries;
        _usesPlayerLanguage = false;
        foreach (var value in main)
            if (value is { } text && UsesPlayerLanguage(text)) _usesPlayerLanguage = true;
        foreach (var entry in entries)
            if (UsesPlayerLanguage(entry.Header) || UsesPlayerLanguage(entry.Body)) _usesPlayerLanguage = true;
    }

    private bool UsesPlayerLanguage() => _usesPlayerLanguage;

    /// <summary>Whether any panel still shows an authored row or main text that a draw would have to withdraw.</summary>
    private bool Drawn()
    {
        foreach (var panel in _panels)
            if (panel.Rows.Count != 0 || panel.MainLayer >= 0) return true;
        return false;
    }

    private static bool UsesPlayerLanguage(TextResourceContract.TextValue text)
        => StationTextValue.RequiresLanguage(TextCardValue.Reference(text));

    private Panel FindPanel(PUI_GameObjectives native)
    {
        foreach (var panel in _panels)
            if (panel.Identity == native.Pointer)
            {
                // Hooks and the current HUD supply the current wrapper; a cached wrapper may have expired.
                panel.Native = native;
                return panel;
            }
        var added = new Panel(native);
        int layer = (int)native.m_currentLayer;
        if (layer >= 0 && layer < 3 && Live(native.m_mainObjective)) added.Originals[layer] = native.m_mainObjective;
        _panels.Add(added); return added;
    }
    // Unity equality itself reads the IL2CPP pointer: WasCollected must precede it.
    private static bool Live(UnityEngine.Object? value) => value is not null && !value.WasCollected && value != null;
    private static bool Live(PlayerGuiLayer? value) => value is not null && !value.WasCollected;
    private static bool Live(Il2CppSystem.Func<string>? value) => value is not null && !value.WasCollected;

    // Runs for HUD and Tab panels. Native updates remain the source of the unmodified main text.
    internal void BeforeMain(PUI_GameObjectives native, LG_LayerType layer, ref Il2CppSystem.Func<string> getter)
    {
        EnsureWorld();
        if (_disposed || _drawing || (int)layer < 0 || (int)layer > 2) return;
        if (!Live(native)) { ReportPending("objective-panel-unavailable"); return; }
        if (!Live(getter)) { ReportPending("objective-main-not-ready", "before-main"); return; }
        var panel = FindPanel(native); int index = (int)layer;
        // A panel the draw skipped for want of a native baseline gets the authored text here, in the native write itself.
        bool baseline = panel.Originals[index] is null;
        if (!Live(panel.MainGetters[index]) || getter.Pointer != panel.MainGetters[index]!.Pointer)
            panel.Originals[index] = getter;
        if (!_main[index].HasValue) { panel.MainLayer = -1; return; }
        if (MainGetter(panel, index) is { } authored)
        {
            panel.MainLayer = index; getter = authored;
            if (baseline && _pendingReportedReasons.ShouldReport("installed@" + Site(panel, index, "installed")))
                ObjectivePresentationLog.Pending(_ownerLevel, _entries.Length, "objective-main-baseline-arrived",
                    Site(panel, index, "installed"), _worldEpoch, "installed");
        }
    }
    internal void PanelChanged(PUI_GameObjectives native)
    {
        EnsureWorld();
        if (_disposed || _drawing) return;
        if (!Live(native)) { ReportPending("objective-panel-unavailable"); return; }
        FindPanel(native); if (_ownerLevel != null) Pending = true;
    }
    /// <summary>The panel's per-frame text refresh. It rewrites text from the same native maps and getters the
    /// last draw filled, so it neither changes what the panel holds nor needs a redraw; only a panel this session
    /// has not seen yet does. Structural changes (entry set/removed, main set, clear) still pend through
    /// <see cref="PanelChanged"/>.</summary>
    internal void PanelTextRefreshed(PUI_GameObjectives native)
    {
        EnsureWorld();
        if (_disposed || _drawing) return;
        if (!Live(native)) { ReportPending("objective-panel-unavailable"); return; }
        int known = _panels.Count;
        FindPanel(native);
        if (_panels.Count != known && _ownerLevel != null) Pending = true;
    }
    internal void PanelCleaned(PUI_GameObjectives native)
    {
        EnsureWorld();
        _panels.RemoveAll(panel =>
        {
            if (!ReferenceEquals(panel.Native, native) && (!Live(native) || panel.Identity != native.Pointer)) return false;
            panel.Active = false; return true;
        });
        if (_ownerLevel != null) RequestRefresh(); else ClearPending();
    }
    // QA DelegateSupport tests the already-native return type against string, so Func<string>'s
    // implicit conversion returns a CLR string address. Return the IL2CPP string pointer explicitly.
    private static Il2CppSystem.Func<string> NativeGetter(Func<string> read)
        => DelegateSupport.ConvertDelegate<Il2CppSystem.Func<string>>(
            new Func<IntPtr>(() => IL2CPP.ManagedStringToIl2Cpp(read())))!;

    private Il2CppSystem.Func<string>? MainGetter(Panel panel, int layer)
    {
        if (!Live(panel.MainGetters[layer]))
        {
            long world = _worldEpoch;
            // Seed the native getter before installation. Later reads return only the value resolved by refresh.
            if (_main[layer] is { } initial) ResolveNativeText(initial, ref panel.LastMainTexts[layer]);
            else if (Live(panel.Originals[layer])) panel.LastMainTexts[layer] = panel.Originals[layer]!.Invoke();
            if (panel.LastMainTexts[layer] is null) return null;
            panel.MainGetters[layer] = NativeGetter(() =>
            {
                if (!panel.Active || _disposed || _kernel.WorldEpoch != world) return string.Empty;
                return panel.LastMainTexts[layer] ?? string.Empty;
            });
        }
        return panel.MainGetters[layer]!;
    }
    private string ResolveNativeText(TextResourceContract.TextValue text, ref string? last)
    {
        var language = UsesPlayerLanguage(text) && _textLanguageKnown ? _textLanguage : (Language?)null;
        if (TryText(text, out var resolved, out var code, reportContractFailure: true, language)) return last = resolved;
        ReportPending(code!);
        // A failed refresh keeps the last confirmed display while the same Map clock retries.
        return last!;
    }
    private bool TryText(TextResourceContract.TextValue text, out string rendered, out string? code,
        bool reportContractFailure = false, Language? language = null)
    {
        rendered = "";
        code = ResultCode.TextResourceMissing;
        try
        {
            var reference = TextCardValue.Reference(text);
            return language.HasValue
                ? StationTextValue.TryRender(reference, language.Value, out rendered)
                : StationTextValue.TryRender(reference, _kernel, out rendered);
        }
        catch (RuntimeContractException error)
        {
            code = error.Code;
            // An action's admission refusal is its result; an installed native getter has no result channel.
            if (reportContractFailure) Report(code, error.Message);
            return false;
        }
        catch (Exception error)
        {
            code = ResultCode.ObjectiveTextNativeUnavailable;
            Report(code, RuntimeDiagnostics.Describe(error));
            return false;
        }
    }
    private bool TryLanguage(out Language language)
    {
        try
        {
            if (_language.TryRead(out language, out var code, out var detail)) return true;
            Report(code, detail); ReportPending(code);
            return false;
        }
        catch (Exception error)
        {
            language = default;
            Report(ResultCode.ObjectiveTextNativeUnavailable, RuntimeDiagnostics.Describe(error));
            return Unavailable(ResultCode.ObjectiveTextNativeUnavailable);
        }
    }
    private bool Draw(Panel panel, bool withdraw = false)
    {
        var native = panel.Native; int layer = (int)native.m_currentLayer;
        if (layer < 0 || layer > 2) { Report(ResultCode.ObjectiveLayerUnknown, layer.ToString()); return Unavailable(ResultCode.ObjectiveLayerUnknown); }
        var objectives = native.m_progressionObjectiveMap;
        var priorities = native.m_progressionObjectivePriorityMap;
        if (objectives is null || objectives.WasCollected || priorities is null || priorities.WasCollected)
            return Unavailable("objective-entry-map-unavailable");
        bool complete = true;
        if (!withdraw && _main[layer] is { } text)
        {
            // A panel that has never received a native main objective has no baseline to override or restore.
            // Its list and the other panels can finish; BeforeMain wakes projection when its baseline arrives.
            if (panel.Originals[layer] is null)
                ReportPending("objective-main-not-ready", Site(panel, layer, "no-original"), retry: false);
            else if (!Live(panel.Originals[layer]))
                complete = Unavailable("objective-main-not-ready", Site(panel, layer, "no-original"));
            else
            {
                ResolveNativeText(text, ref panel.LastMainTexts[layer]);
                if (panel.LastMainTexts[layer] is null) return false;
                if (MainGetter(panel, layer) is not { } authored) return false;
                panel.MainLayer = layer;
                // A draw that stays pending for another panel runs every frame. The native write restarts the panel's
                // own text update, so repeat it only when the getter or the text it returns actually changed.
                bool installed = Live(native.m_mainObjective) && native.m_mainObjective!.Pointer == authored.Pointer;
                if (!installed || !string.Equals(panel.DrawnMainTexts[layer], panel.LastMainTexts[layer], StringComparison.Ordinal))
                {
                    native.SetMainObjective((LG_LayerType)layer, authored, true);
                    panel.DrawnMainTexts[layer] = panel.LastMainTexts[layer];
                }
            }
        }
        else if (panel.MainLayer == layer)
        {
            if (Live(panel.Originals[layer]))
            {
                native.SetMainObjective((LG_LayerType)layer, panel.Originals[layer]!, true);
                panel.MainLayer = -1; panel.DrawnMainTexts[layer] = null;
            }
            else complete = Unavailable("objective-main-not-ready", Site(panel, layer, "withdraw-no-original"));
        }
        var visible = withdraw ? Array.Empty<Entry>() : _entries.Where(entry => entry.Independent || entry.Layer == layer).ToArray();
        foreach (var row in panel.Rows.ToArray())
        {
            bool exists = objectives.ContainsKey(row.Id);
            var actual = exists ? objectives[row.Id] : null;
            if (exists && !Live(actual))
            {
                complete = Unavailable("objective-entry-unavailable");
                if (visible.Length != 0) return false;
                continue; // Empty-state withdrawal still cleans the other live rows.
            }
            bool owned = exists && row.NativeIdentity != IntPtr.Zero && actual!.Pointer == row.NativeIdentity;
            if (exists && !owned) Report("objective-entry-ownership-lost", row.Value.Identity);
            if (!owned || !visible.Any(entry => entry.Identity == row.Value.Identity))
            {
                if (owned) native.RemoveProgressionObjective(row.Id, true);
                panel.Rows.Remove(row);
            }
        }
        for (int order = 0; order < visible.Length; order++)
        {
            var value = visible[order]; var row = panel.Rows.FirstOrDefault(item => item.Value.Identity == value.Identity);
            // Unique native ranks retain the author's relative sort values and give equal values a stable name tie.
            // Native priority direction and actual layout capacity still require the S8 game observation.
            int priority = checked(PUI_GameObjectives.PROGRESSION_OBJECTIVE_PRIORITY + order);
            if (row == null)
            {
                if (!Live(native.m_progressionObjectivePrefab)) return Unavailable("objective-entry-prefab-unavailable");
                while (panel.NextId == PUI_GameObjectives.MAIN_OBJECTIVE_ID
                    || panel.NextId == PUI_GameObjectives.CUSTOM_SUB_OBJECTIVE_ID
                    || native.m_progressionObjectiveMap.ContainsKey(panel.NextId))
                {
                    if (panel.NextId == int.MaxValue) throw new InvalidOperationException("objective-native-id-exhausted");
                    panel.NextId++;
                }
                row = new Row(value, panel.NextId, this);
                if (row.LastHeader is null || row.LastBody is null) return false;
                panel.NextId++; panel.Rows.Add(row);
                try { native.SetProgressionObjective(row.Id, row.Header, row.Body, priority, true, true, ""); }
                finally
                {
                    // This interop's generic TryGetValue out wrapper requires the nonexistent
                    // System.__Canon(IntPtr) constructor. ContainsKey plus the indexer has no out marshalling.
                    var created = objectives.ContainsKey(row.Id) ? objectives[row.Id] : null;
                    if (Live(created)) row.NativeIdentity = created!.Pointer;
                }
                if (row.NativeIdentity == IntPtr.Zero) return Unavailable("objective-entry-unavailable");
                row.Priority = priority;
            }
            else
            {
                row.Value = value;
                ResolveNativeText(value.Header, ref row.LastHeader);
                ResolveNativeText(value.Body, ref row.LastBody);
                if (row.LastHeader is null || row.LastBody is null) return false;
                var current = objectives.ContainsKey(row.Id) ? objectives[row.Id] : null;
                if (current is null || !Live(current)
                    || !Live(current.m_header) || !Live(current.m_text))
                    return Unavailable("objective-entry-unavailable");
                if (!Live(row.Header) || !Live(row.Body))
                {
                    row.BindGetters();
                    native.SetProgressionObjective(row.Id, row.Header, row.Body, priority, true, true, "");
                    var rebound = objectives.ContainsKey(row.Id) ? objectives[row.Id] : null;
                    if (rebound is null || !Live(rebound))
                        return Unavailable("objective-entry-unavailable");
                    current = rebound; row.NativeIdentity = rebound.Pointer;
                }
                else current.UpdateText();
                if (row.Priority != priority) native.m_progressionObjectivePriorityMap[current] = priority;
                row.Priority = priority;
            }
        }
        native.UpdateObjectivesLayout(true, true);
        return complete;
    }
    private void Report(string code, string detail)
    {
        if (!_once.ShouldReport(code)) return;
        ObjectivePresentationLog.Refused(_ownerLevel, code, detail);
        _report(code + ": " + detail);
    }
    private bool Unavailable(string reason, string? site = null) { ReportPending(reason, site); return false; }
    private string Site(Panel panel, int layer, string cause)
        => SurfaceOf(panel, _panels.IndexOf(panel)) + ":" + ObjectivePresentationContract.Layers[layer] + ":" + cause;
    private string SurfaceOf(Panel panel, int index)
    {
        var hud = GuiManager.Current?.m_playerLayer?.m_wardenObjective;
        return Live(hud) && hud!.Pointer == panel.Identity ? "hud" : "panel-" + index;
    }
    private void ReportPending(string reason, string? site = null, bool retry = true)
    {
        if (_ownerLevel == null
            && _panels.All(panel => panel.Rows.Count == 0 && panel.MainLayer < 0)) return;
        if (_endingWorldEpoch.HasValue)
        {
            if (!_endingPendingReasons.Add(reason)) return;
        }
        else
        {
            if (retry) { Pending = true; PendingReason = reason; }
            string key = reason + "@" + site;
            if (!_pendingReportedReasons.ShouldReport(key)) return;
        }
        ObjectivePresentationLog.Pending(_ownerLevel, _entries.Length, reason, site,
            _endingWorldEpoch ?? _worldEpoch, _endingWorldEpoch.HasValue ? "ending" : retry ? "pending" : "skipped");
    }
    private void ClearPending() { Pending = false; PendingReason = null; }
    private void EnsureWorld() { if (_worldEpoch != _kernel.WorldEpoch) ClearWorld(); }
    /// <summary>A host migration keeps the level and its objective panels: they stay under the new world number.</summary>
    internal void FollowWorld(long from, long to) { if (_worldEpoch == from) _worldEpoch = to; }
    private void ClearWorld()
    {
        _endingWorldEpoch = _worldEpoch; _worldEpoch = _kernel.WorldEpoch;
        // HUD/Tab panels may survive a world boundary or a runtime stop. Withdraw owned rows before forgetting them.
        bool drawing = _drawing; _drawing = true;
        try
        {
            foreach (var panel in _panels) panel.Active = false;
            foreach (var panel in _panels)
            {
                try { if (Live(panel.Native)) Draw(panel, withdraw: true); }
                catch (Exception error) { Report("objective-text-native-failed", RuntimeDiagnostics.Describe(error)); ReportPending("objective-text-native-failed"); }
            }
        }
        finally { _panels.Clear(); Clear(); _endingPendingReasons.Clear(); _endingWorldEpoch = null; _drawing = drawing; }
    }
    private void Clear()
    {
        SetTexts(new TextResourceContract.TextValue?[3], Array.Empty<Entry>()); _ownerLevel = null;
        _snapshot = Save(null, _main, _entries); ClearPending(); _pendingReportedReasons.Reset(); _once.Reset();
    }
    public void Dispose()
    {
        if (_disposed) return;
        ClearWorld(); _disposed = true; _state.Dispose(); _lifecycle.Dispose();
    }
}
