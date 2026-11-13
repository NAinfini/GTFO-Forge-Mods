using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using SNetwork;

namespace ForgeOrdnance;

/// <summary>The selected expedition's effective rules as the lobby host sets them, before loadout editing or submission. The host
/// publishes its rules as its ForgeCore lobby value (<see cref="Messages.LobbyRules"/>), which ForgeCore delivers to every player and
/// to players who join later; a client reads the host's value.</summary>
internal static class LobbyRules
{
    private static readonly List<uint> NoKeys = new();
    private static NetLobbyValue? _rules;
    private static bool _wasMaster, _contentReady, _reread, _dirty = true;
    private static ulong _master, _setHash;
    private static int _readVersion;
    private static float _retryAt;
    private const float RetrySeconds = 0.5f;
    private static string _localSelection = "";
    private static HostRulesSnapshot? _current;
    private static OrdnanceLevelRules? _currentSettings, _hostRulesSource;
    private static byte[]? _currentCanonical;

    internal static event Action? Changed;

    internal static void Register()
    {
        if (_rules != null) return;
        _rules = OrdnanceNetwork.Channel.Lobby(Messages.LobbyRules);
        _rules.Changed += player => { if (player == _master) { _reread = true; _dirty = true; } };
        GameEvents.MasterChanged += Invalidate;
        GameEvents.ExpeditionSelected += (_, _, _) => Invalidate();
    }

    /// <summary>The host, the loaded definitions or the received rules changed: the next read works the rules out again.</summary>
    internal static void Invalidate() => _dirty = true;

    /// <summary>Runs from the plugin's main-thread tick in menus and levels; the rules are worked out again only when something
    /// invalidated them (the host, the loaded definitions, the received rules or the selected expedition).</summary>
    internal static void Tick()
    {
        if (_rules == null) return;
        // Rules still missing (no host or local player yet, definitions not loaded, host value or selection not arrived): the
        // events that would end the wait may have fired before the context was complete, so look again twice a second.
        // ForgeCore's driver stamps the frame's unscaled time before it runs the frame callbacks this is called from.
        if (!_dirty && _current == null && Frame.UnscaledTime >= _retryAt) _dirty = true;
        if (!_dirty) return;
        _retryAt = Frame.UnscaledTime + RetrySeconds;
        try { RefreshContext(); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Host level rules could not be synchronized: " + error); }
    }

    internal static bool TryCurrent(out OrdnanceLevelRules rules, out string levelKey)
    {
        if (_dirty)
        {
            try { RefreshContext(); }
            catch (Exception error)
            {
                Plugin.Logger.ErrorOnce("Host level rules could not be read: " + error);
            }
        }
        if (_current != null && _currentSettings != null && _current.DefinitionHash == OrdnanceContent.Set.Hash
            && _current.LevelKey == _localSelection)
        {
            rules = _currentSettings;
            levelKey = _current.LevelKey;
            return true;
        }
        rules = null!;
        levelKey = "";
        return false;
    }

    /// <summary>Uses the host's slot count and allow-list without changing saved picks outside that view. False while the level's
    /// <c>LobbyPicker</c> is off: the player's picks are not used then (the host gives the level's own <c>Loadouts</c>).</summary>
    internal static bool TrySubmissionKeys(out List<uint> keys)
    {
        if (TryCurrent(out var rules, out _) && rules.LobbyPicker)
        {
            keys = LoadoutStore.KeysFor(rules);
            return true;
        }
        keys = NoKeys;
        return false;
    }

    private static void RefreshContext()
    {
        if (_rules == null) return;
        _dirty = false;
        var local = SNet.LocalPlayer?.Lookup ?? 0UL;
        var master = SNet.Master?.Lookup ?? (Session.IsHost ? local : 0UL);
        var selection = ForgeCore.Level.ActiveKey ?? "";
        var hash = OrdnanceContent.Set.Hash;
        var isMaster = Session.IsHost;
        var ready = OrdnanceApi.DefinitionsLoaded;
        var hostRules = isMaster && ready ? OrdnanceContent.Settings.For(selection) : null;
        if (master != _master || selection != _localSelection || hash != _setHash || isMaster != _wasMaster
            || ready != _contentReady || isMaster && !ReferenceEquals(hostRules, _hostRulesSource))
        {
            _master = master;
            _localSelection = selection;
            _setHash = hash;
            _wasMaster = isMaster;
            _contentReady = ready;
            _hostRulesSource = hostRules;
            _reread = true;
            SetCurrent(null, null, null);
            if (!isMaster) _rules.Set(null);
        }
        if (master == 0 || local == 0 || !ready) return;
        if (isMaster)
        {
            if (_current != null) return;
            var snapshot = HostRulesSnapshot.From(selection, OrdnanceContent.Set, hostRules!);
            var canonical = HostRulesWire.Encode(snapshot);
            SetCurrent(snapshot, canonical, hostRules);
            _rules.Set(canonical);
            return;
        }
        var version = _rules.VersionOf(master);
        if (!_reread && version == _readVersion) return;
        _reread = false;
        _readVersion = version;
        if (!_rules.TryGet(master, out var bytes)) { SetCurrent(null, null, null); return; }
        if (!HostRulesWire.TryDecode(bytes, out var received) || received == null)
        {
            SetCurrent(null, null, null);
            Plugin.Logger.ErrorOnce("The host sent malformed level rules; they were ignored.");
            return;
        }
        // The host publishes again when its selection changes; until the lobby's selection reaches this player the rules wait.
        if (received.LevelKey != _localSelection) { SetCurrent(null, null, null); return; }
        if (received.AllowedKeys != null && received.AllowedKeys.Any(key => !OrdnanceContent.Set.TryGet(key, out _)))
        {
            SetCurrent(null, null, null);
            Plugin.Logger.ErrorOnce("The host sent level rules containing an unknown call-in; the rules were ignored.");
            return;
        }
        SetCurrent(received, bytes, received.ToSettings(OrdnanceContent.Set));
    }

    private static void SetCurrent(HostRulesSnapshot? snapshot, byte[]? canonical, OrdnanceLevelRules? settings)
    {
        if (_currentCanonical == null && canonical == null) return;
        if (_currentCanonical != null && canonical != null && _currentCanonical.SequenceEqual(canonical)) return;
        _current = snapshot;
        _currentSettings = settings;
        _currentCanonical = canonical;
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList())
        {
            try { handler(); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("Applying synchronized host level rules failed: " + error); }
        }
    }
}
