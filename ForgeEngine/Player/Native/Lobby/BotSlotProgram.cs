using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using ForgeCore;
using GameData;
using Gear;
using Player;
using SNetwork;

namespace ForgePlayer.Native;

/// <summary>Drives the saved roster in the host lobby. Each step advances only after the native membership or
/// backpack readback changes; it never creates, removes or substitutes an agent.</summary>
internal sealed class BotSlotProgram : IDisposable
{
    private readonly RegistrationBotSlotLog _log;
    private bool _active, _disposed;
    private readonly IReadOnlyDictionary<LevelAddress, IReadOnlyList<BotSlotSetting>> _levels;
    private LevelAddress? _level;
    private int _session;
    private IntPtr _slots;
    private LevelAddress? _confirmedLevel;
    private int _confirmedSession;
    private IntPtr _confirmedSlots;
    private LevelAddress? _pendingSelection;
    private IReadOnlyList<Resolved>? _rows;
    private int _next;
    private NativeBotSlotRequest? _request;
    private readonly List<NativeBotSlotRequest> _completed = new();
    private bool _submitted, _failed, _inLobby;

    internal BotSlotProgram(IReadOnlyList<BotSlotLevel> levels, RegistrationBotSlotLog log)
    {
        _levels = levels.ToDictionary(level => level.Level, level => level.Slots);
        _log = log;
        // The roster only moves in the lobby, so the frame works from the lobby state's own transition events
        // and is a single bool outside it.
        GameEvents.LobbyChanged += LobbyChanged;
        GameEvents.ExpeditionSelected += ConfirmSelection;
        _active = GameEvents.InLobby;
    }

    private void LobbyChanged(bool inLobby)
    {
        if (_disposed) return;
        if (inLobby) _active = true;
        else if (_active) { _active = false; EndLobby(report: true); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GameEvents.LobbyChanged -= LobbyChanged;
        GameEvents.ExpeditionSelected -= ConfirmSelection;
    }

    // A native selection belongs to one host Hub and its current slot manager. The active expedition alone
    // may still describe the previous Hub while the next lobby is being created.
    internal void ConfirmSelection(uint? rundownId, int tier, int index)
    {
        var selected = rundownId is { } id ? LevelAddress.FromNative(id, tier, index) : null;
        if (GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby
            || SNet.Slots == null || SNet.SessionHub == null)
        {
            if (_inLobby) EndLobby(report: true);
            _pendingSelection = selected;
            return;
        }
        if (!Session.IsHost) return;
        if (_inLobby && (_session != SNet.SessionHub.SessionID || _slots != SNet.Slots.Pointer))
        {
            if (!_failed && HasLifecycle()) Reject("native-bot.session-changed", CurrentSlot());
            _inLobby = false;
        }
        _pendingSelection = null;
        _confirmedLevel = selected;
        _confirmedSession = SNet.SessionHub.SessionID;
        _confirmedSlots = SNet.Slots.Pointer;
    }

    internal void Frame()
    {
        if (!_active) return;
        // A lobby that leaves for a recording's playback (ForgeCore Puppets.Playback) seats no saved bots: the
        // preparation is withdrawn, not refused.
        if (ForgeCore.Puppets.Playback || !Session.IsHost || GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby
            || SNet.Slots == null || SNet.SessionHub == null)
        {
            EndLobby(report: !ForgeCore.Puppets.Playback);
            return;
        }
        var level = ActiveLevel();
        if (!_inLobby)
        {
            _inLobby = true;
            if (_pendingSelection is { } pending)
            {
                _confirmedLevel = pending;
                _confirmedSession = SNet.SessionHub.SessionID;
                _confirmedSlots = SNet.Slots.Pointer;
                _pendingSelection = null;
            }
            Select(Confirmed(level) ? level : null);
        }
        else if (_session != SNet.SessionHub.SessionID || _slots != SNet.Slots.Pointer)
        {
            if (_failed) return;
            if (HasLifecycle()) Reject("native-bot.session-changed", CurrentSlot());
            else Select(null);
            return;
        }
        else if (!Confirmed(level)) return;
        else if (level != _level) Select(level);
        if (_failed || _rows == null) return;

        int? observedSlot = CurrentSlot();
        try
        {
            foreach (var completed in _completed)
            {
                observedSlot = completed.Slot;
                if (!completed.Applied) throw Refuse("applied-readback-changed");
            }
            if (_next >= _rows.Count) return;
            var row = _rows[_next];
            observedSlot = row.Setting.Slot;
            if (_request == null)
            {
                if (!NativeBotSlotRequest.HostReady) return;
                _request = NativeBotSlotRequest.Begin(row.Setting.Slot, row.Setting.Bot, row.Setting.Character, row.Loadout);
                if (_log.Logs(RuntimeLogLevel.Info))
                    _log.Record(RuntimeLogLevel.Info, BotSlotLogCodes.Requested,
                        "The host submitted one saved lobby slot permission and is waiting for native membership.",
                        RuntimeLogValue.Of("level", _level!.Value.ToString()), RuntimeLogValue.Of("slot", row.Setting.Slot),
                        RuntimeLogValue.Of("bot", row.Setting.Bot));
            }
            if (!_request.Ready) return;
            if (!_submitted) { _request.Apply(); _submitted = true; }
            if (!_request.Applied) return;
            string readback = Compact(_request.Readback);
            if (_log.Logs(RuntimeLogLevel.Info))
                _log.Record(RuntimeLogLevel.Info, BotSlotLogCodes.Applied, readback,
                    RuntimeLogValue.Of("level", _level!.Value.ToString()), RuntimeLogValue.Of("slot", row.Setting.Slot),
                    RuntimeLogValue.Of("bot", row.Setting.Bot));
            _completed.Add(_request);
            _next++; _request = null; _submitted = false;
        }
        catch (Exception error)
        {
            Reject(Result(error), observedSlot, error as NativeBotSlotRequest.PermissionChanged, Unexpected(error));
        }
    }

    /// <summary>The active expedition's identity, or null when the game cannot name one: between levels, with no rundown loaded or
    /// with a tier this vocabulary cannot spell. A null level is never read as a match.</summary>
    private static LevelAddress? ActiveLevel()
        => global::ForgeCore.Level.TryActiveExpedition(out var rundownId, out var tier, out var index)
            ? LevelAddress.FromNative(rundownId, tier, index) : null;

    private bool Confirmed(LevelAddress? level)
        => level is not null && level == _confirmedLevel
            && _confirmedSession == SNet.SessionHub!.SessionID && _confirmedSlots == SNet.Slots!.Pointer;

    private void Select(LevelAddress? level)
    {
        _level = level; _session = SNet.SessionHub!.SessionID; _slots = SNet.Slots!.Pointer;
        _rows = null; _next = 0; _request = null; _submitted = false; _failed = false;
        _completed.Clear();
        if (level is not { } selected || !_levels.TryGetValue(selected, out var settings)) return;
        int? resolvingSlot = null;
        try
        {
            // Resolve every saved gear id before the first permission write, so an illegal loadout cannot leave a
            // partially configured roster. Disabled slots run first and let UpdateSpawnBots free their characters.
            var resolved = new List<Resolved>(settings.Count);
            foreach (var setting in settings)
            {
                resolvingSlot = setting.Slot;
                resolved.Add(new Resolved(setting, Resolve(setting)));
            }
            _rows = Array.AsReadOnly(resolved.OrderBy(row => row.Setting.Bot)
                .ThenBy(row => row.Setting.Slot).ToArray());
        }
        catch (Exception error)
        {
            Reject(Result(error), resolvingSlot, null, Unexpected(error));
        }
    }

    private void EndLobby(bool report)
    {
        if (!_inLobby) return;
        if (report && !_failed && _rows != null && _next < _rows.Count)
            Reject("native-bot.lobby-required", CurrentSlot());
        _inLobby = false;
        _level = null; _rows = null; _next = 0; _request = null; _submitted = false; _failed = false;
        _confirmedLevel = null;
        _completed.Clear();
    }

    private int? CurrentSlot()
    {
        if (_request != null) return _request.Slot;
        if (_rows != null && _next < _rows.Count) return _rows[_next].Setting.Slot;
        return _completed.Count == 0 ? null : _completed[^1].Slot;
    }

    private bool HasLifecycle()
        => _request != null || _completed.Count != 0 || (_rows != null && _next < _rows.Count);

    private void Reject(string result, int? slot, NativeBotSlotRequest.PermissionChanged? diagnostic = null, string? exception = null)
    {
        if (_failed) return;
        _failed = true;
        string level = _level?.ToString() ?? "";
        var values = new List<RuntimeLogValue>
        {
            RuntimeLogValue.Of("level", level), RuntimeLogValue.Quote("result", result)
        };
        if (slot is { } index) values.Add(RuntimeLogValue.Of("slot", index));
        if (exception != null) values.Add(RuntimeLogValue.Quote("exception", exception));
        if (diagnostic != null)
        {
            values.Add(RuntimeLogValue.Of("expectedPermission", diagnostic.ExpectedPermission));
            values.Add(RuntimeLogValue.Of("actualPermission", diagnostic.ActualPermission));
            values.Add(RuntimeLogValue.Of("phase", diagnostic.Phase));
            values.Add(RuntimeLogValue.Of("session", diagnostic.Session));
            values.Add(RuntimeLogValue.Of("botBound", diagnostic.BotBound));
        }
        _log.Record(RuntimeLogLevel.Error, BotSlotLogCodes.Refused, result, values.ToArray());
    }

    private static IReadOnlyList<RuntimeBotGear> Resolve(BotSlotSetting setting)
    {
        var resolved = new List<RuntimeBotGear>();
        foreach (var pair in setting.Loadout)
        {
            var slot = Enum.Parse<InventorySlot>(pair.Key);
            var block = GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(pair.Value);
            if (block == null || string.IsNullOrEmpty(block.GearJSON)) throw Refuse("gear-missing");
            string key = "OfflineGear_ID_" + pair.Value.ToString(CultureInfo.InvariantCulture);
            GearIDRange? found = null;
            foreach (var candidate in GearManager.GetAllGearForSlot(slot))
            {
                if (candidate == null || !string.Equals(candidate.PlayfabItemInstanceId, key, StringComparison.Ordinal)) continue;
                if (found != null) throw Refuse("gear-ambiguous");
                found = candidate;
            }
            if (found == null) throw Refuse("gear-missing");
            var item = ItemDataBlock.GetBlock(found.GetCompID(eGearComponent.BaseItem));
            if (item == null || item.inventorySlot != slot) throw Refuse("gear-slot-mismatch");
            resolved.Add(new(slot, found));
        }
        return Array.AsReadOnly(resolved.ToArray());
    }

    private static string Compact(string readback)
    {
        using var document = JsonDocument.Parse(readback);
        var root = document.RootElement;
        var loadout = root.GetProperty("loadout").EnumerateObject()
            .Select(row => row.Name + "=" + (row.Value.TryGetProperty("instanceId", out var id) ? id.GetString() : ""));
        return "permission=" + root.GetProperty("permission").GetString()
            + " bot=" + root.GetProperty("bot")
            + " lookup=" + root.GetProperty("lookup")
            + " character=" + root.GetProperty("character")
            + " sessionCharacter=" + root.GetProperty("sessionCharacter")
            + " loadout=[" + string.Join(",", loadout) + "]";
    }

    private static string Result(Exception error)
    {
        if (error is AggregateException aggregate)
            return string.Join("|", aggregate.Flatten().InnerExceptions.Select(Result));
        return IsRefusal(error) ? error.Message : "native-bot.native-error";
    }

    /// <summary>The exception types behind a <c>native-bot.native-error</c> result: the code stays fixed, the types go into the detail.</summary>
    private static string? Unexpected(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            var types = aggregate.Flatten().InnerExceptions.Select(Unexpected).Where(type => type != null);
            return types.Any() ? string.Join("|", types) : null;
        }
        return IsRefusal(error) ? null : error.GetType().Name;
    }

    private static bool IsRefusal(Exception error) => error.Message.StartsWith("native-bot.", StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string code) => new(ForgeEngine.Logging.DynamicDiagnosticCodes.Of("native-bot.{code}", code));
    private sealed record Resolved(BotSlotSetting Setting, IReadOnlyList<RuntimeBotGear> Loadout);
}

internal static class BotSlotLogCodes
{
    /// <summary>The host submitted one saved lobby slot permission and is waiting for native membership.</summary>
    internal const string Requested = "player.bot-slot-requested";
    /// <summary>Native slot permission, identity, character and backpack readback confirmed.</summary>
    internal const string Applied = "player.bot-slot-applied";
    /// <summary>A saved bot-slot preparation was refused; the result names why.</summary>
    internal const string Refused = "player.bot-slot-refused";
}
