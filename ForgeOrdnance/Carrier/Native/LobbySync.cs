using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeUI;
using SNetwork;

namespace ForgeOrdnance;

/// <summary>
/// Shows every lobby player's call-in picks on their lobby bar. Each player publishes only their own picks, under the host's
/// rules, as this mod's ForgeCore lobby value (<see cref="Messages.LobbyPicks"/>); ForgeCore delivers it to every player, again to
/// players who join later, and forgets the picks of a player who left. Display only; the host still checks the loadout submitted on
/// entering a level. ForgeUI draws the row (<see cref="LobbyStrip"/>).
/// </summary>
internal static class LobbySync
{
    private static readonly uint[] LocalKeys = new uint[Messages.MaxLoadout];
    private static readonly uint[] Scratch = new uint[Messages.MaxLoadout];
    private static readonly Dictionary<ulong, Shown> Cache = new();
    private static NetLobbyValue? _picks;
    private static bool _dirty = true;
    private static DefinitionSet? _cacheDefinitions;

    private sealed class Shown
    {
        public int Version;
        public readonly List<LobbyStripItem> Items = new(Messages.MaxLoadout);
    }

    internal static void Register()
    {
        if (_picks != null) return;
        _picks = OrdnanceNetwork.Channel.Lobby(Messages.LobbyPicks);
        _picks.Changed += Received;
        LoadoutStore.Changed += MarkDirty;
        LobbyRules.Changed += MarkDirty;
        LobbyRules.Changed += Cache.Clear;
        LobbyRules.Changed += OrdnanceApi.RaisePickedParametersChanged;
        UIKit.Mod(Plugin.Id).AddLobbyStrip("Loadouts", ItemsOf, Messages.MaxLoadout);
    }

    private static void MarkDirty() => _dirty = true;

    /// <summary>Runs from the plugin's main-thread tick; publishes the local picks again only when they changed.</summary>
    internal static void Tick()
    {
        if (_picks == null || !_dirty) return;
        _dirty = false;
        try
        {
            if (!LobbyRules.TrySubmissionKeys(out var keys)) { _picks.Set(null); return; }
            var count = Math.Min(keys.Count, Messages.MaxLoadout);
            for (var i = 0; i < count; i++) LocalKeys[i] = keys[i];
            _picks.Set(LobbyPicks.Encode(LocalKeys.AsSpan(0, count)));
            OrdnanceApi.RaisePickedParametersChanged();
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("Lobby loadouts could not be published: " + error); }
    }

    private static void Received(ulong player)
    {
        Cache.Remove(player);
        OrdnanceApi.RaisePickedParametersChanged();
        if (_picks!.TryGet(player, out var bytes))
        {
            var count = LobbyPicks.Decode(bytes, Scratch);
            if (count < 0) Plugin.Logger.ErrorOnce("Malformed lobby picks from player " + player + " ignored.");
        }
    }

    /// <summary>The parameters of every picked call-in of <paramref name="type"/> across the lobby's players, each once
    /// (<see cref="OrdnanceApi.PickedParameters"/>).</summary>
    internal static void PickedParameters(string type, List<object> into)
    {
        into.Clear();
        var definitions = OrdnanceContent.Set;
        // Definitions with Callable false exist only to be placed by a level; no pick or drop list names them reliably, so they are always in.
        foreach (var placed in definitions.Definitions)
            if (!placed.Callable && placed.PayloadType == type && !into.Contains(placed.Parameters)) into.Add(placed.Parameters);
        var known = LobbyRules.TryCurrent(out var rules, out var level);
        if (known)
        {
            var authored = OrdnanceContent.Settings.For(level);
            foreach (var drop in authored.Drops)
                if (definitions.TryGetById(drop.Ordnance, out var dropped) && dropped.PayloadType == type && !into.Contains(dropped.Parameters)) into.Add(dropped.Parameters);
            // The author's own loadouts stand in for lobby picks while the lobby choice is off.
            if (!rules.LobbyPicker && rules.CallMenu)
                foreach (var ids in authored.Loadouts.Values)
                    foreach (var id in ids)
                        if (definitions.TryGetById(id, out var given) && given.PayloadType == type && !into.Contains(given.Parameters)) into.Add(given.Parameters);
        }
        if (_picks == null || known && (!rules.CallMenu || !rules.LobbyPicker)) return;
        AddPicked(_picks.Local, definitions, type, into);
        var players = SNet.LobbyPlayers;
        if (players == null) return;
        foreach (var player in players)
            if (player != null && !player.IsLocal && _picks.TryGet(player.Lookup, out var bytes)) AddPicked(bytes, definitions, type, into);
    }

    private static void AddPicked(byte[]? bytes, DefinitionSet definitions, string type, List<object> into)
    {
        var count = bytes == null ? -1 : LobbyPicks.Decode(bytes, Scratch);
        for (var i = 0; i < count; i++)
            if (definitions.TryGet(Scratch[i], out var definition) && definition.PayloadType == type && !into.Contains(definition.Parameters)) into.Add(definition.Parameters);
    }

    /// <summary>ForgeUI asks once per lobby bar per frame; the list is rebuilt only when that player's picks or the level's rules
    /// change. One cell per loadout slot the rules give: the picks in slot order, then free slots.</summary>
    private static IReadOnlyList<LobbyStripItem>? ItemsOf(SNet_Player player)
    {
        if (_picks == null || player == null || OrdnanceLoadout.PickerOff) return null;
        var lookup = player.Lookup;
        var version = player.IsLocal ? _picks.LocalVersion : _picks.VersionOf(lookup);
        if (version == 0) return null;
        var definitions = OrdnanceContent.Set;
        if (!ReferenceEquals(definitions, _cacheDefinitions))
        {
            Cache.Clear();
            _cacheDefinitions = definitions;
        }
        if (Cache.TryGetValue(lookup, out var shown) && shown.Version == version) return shown.Items;
        var bytes = player.IsLocal ? _picks.Local : _picks.TryGet(lookup, out var received) ? received : null;
        var count = bytes == null ? -1 : LobbyPicks.Decode(bytes, Scratch);
        if (count < 0) return null;
        if (shown == null) Cache[lookup] = shown = new Shown();
        shown.Version = version;
        shown.Items.Clear();
        for (var i = 0; i < count; i++)
        {
            if (!definitions.TryGet(Scratch[i], out var definition)) continue;
            var icon = OrdnanceContent.TryGetIconPath(definition, out var path) ? UIKit.LoadIcon(path) : null;
            shown.Items.Add(new LobbyStripItem(icon, icon == null ? OrdnanceApi.DisplayName(definition).ToUpperInvariant() : ""));
        }
        var slots = LobbyRules.TryCurrent(out var rules, out _) ? Math.Min(rules.LoadoutSize, Messages.MaxLoadout) : 0;
        while (shown.Items.Count < slots) shown.Items.Add(LobbyStripItem.Empty);
        return shown.Items;
    }
}
