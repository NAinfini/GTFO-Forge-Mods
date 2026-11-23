using System;
using System.Collections.Generic;
using System.Linq;
using ForgeCore;
using ForgeEngine.Framework;
using ForgeUI;
using Player;
using UnityEngine;

namespace ForgePlayer.Native;

/// <summary>A level's carry-on-back rules at run time. The moves are <see cref="CarrySlot"/>; this says which object may, on which key and
/// at what speed. Everything is read again from the game's replicated slot and wield each frame and the rules are a file every peer has, so
/// host, clients, a late joiner and a checkpoint reload need no state of their own. A player who leaves is the game's own case: its leaving
/// capture lists the whole backpack and drops the object whatever is in hand. The hooks go on only when a package ships a rule; one the
/// game refuses is reported once and every heavy object keeps the game's behaviour.</summary>
internal sealed class CarryOnBack
{
    internal const string RefusedCode = "player.carry-on-back-refused";
    internal const string UnavailableCode = "player.carry-on-back-unavailable";
    private static readonly PublishedTextCodes Codes = new("player-carry-on-back-path", "player-carry-on-back-unreadable",
        "player-carry-on-back-file-size", "player-carry-on-back-json", "player-carry-on-back-utf8");
    private const float LevelEvery = 1f, OthersEvery = 0.5f;

    private readonly Dictionary<LevelAddress, Dictionary<uint, CarryOnBackRule>> _levels;
    private readonly Dictionary<KeyCode, HudKeyHint?> _hints = new();
    private readonly List<IDisposable> _subscriptions = new();
    private Dictionary<uint, CarryOnBackRule>? _rules;
    private CarryOnBackRule? _rule;
    private HudKeyHint? _shown;
    private float _speed = 1f, _levelAt, _othersAt;

    private readonly Action<string> _unavailable;

    private CarryOnBack(IEnumerable<CarryOnBackLevel> levels, Action<string> unavailable)
    {
        _levels = levels.ToDictionary(row => row.Level, row => row.Items.ToDictionary(item => item.ItemId));
        _unavailable = unavailable;
    }

    /// <summary>Null when no package ships a rule, or the game refused a hook (reported through <paramref name="unavailable"/>).</summary>
    internal static CarryOnBack? Start(IReadOnlyList<CarryOnBackLevel> levels, Action<string> unavailable)
    {
        if (levels.Count == 0) return null;
        var created = new CarryOnBack(levels, unavailable);
        try
        {
            created._subscriptions.Add(GameHooks.WieldedSlotBlocked.Release(created.Releases));
            created._subscriptions.Add(GameHooks.LocalItemHeld.Hide(created.Hides));
            created._subscriptions.Add(PlayerStats.Walk(created.Walked));
            created._subscriptions.Add(GameHooks.ItemSpawned.After((call, _) => created.Spawned(call)));
            return created;
        }
        catch (Exception error)
        {
            created.Stop();
            unavailable("Carrying on the back is off and heavy objects stay in both hands: " + error.Message);
            return null;
        }
    }

    /// <summary>Ends every subscription this instance made and empties the rules, so heavy objects go back to the game's
    /// behaviour. A session that is torn down, a start that failed half way and a frame that failed all stop here.</summary>
    internal void Stop()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _levels.Clear();
        _rules = null;
        Clear();
    }

    internal static IReadOnlyList<CarryOnBackLevel> Read(string bepInExRoot, Action<string> refused)
        => LevelDocuments.Read(bepInExRoot, CarryOnBackDocument.FileName, Codes, CarryOnBackDocument.Parse, row => row.Level, refused);

    /// <summary>A failure turns the rules off for the session and is reported: heavy objects go back to the game's behaviour, the rest of Player keeps running.</summary>
    internal void Frame(float now)
    {
        if (_levels.Count == 0) return;
        try { Step(now); }
        catch (Exception error)
        {
            Stop();
            _unavailable("Carrying on the back stopped and heavy objects stay in both hands: " + error.Message);
        }
    }

    private void Step(float now)
    {
        if (now >= _levelAt) { _levelAt = now + LevelEvery; _rules = LevelRules(); }
        var agent = Players.Local;
        if (_rules == null || agent == null) { Clear(); return; }
        _rule = _rules.GetValueOrDefault(CarrySlot.ItemId(agent));
        if (_rule == null) Clear();
        else
        {
            CarrySlot.Track(agent);
            if (!CarrySlot.DropIfFallen(agent, now) && agent.Alive && Input.GetKeyDown(_rule.Key) && PlayerKeyFacts.Focused()
                && Players.Motion(agent) != PlayerMotion.StandStill)
                CarrySlot.Toggle(agent, now);
            _speed = CarrySlot.InHands == false ? _rule.MoveSpeed : 1f;
            ShowHint(_rule.Key, CarrySlot.InHands == true);
        }
        if (now < _othersAt) return;
        _othersAt = now + OthersEvery;
        for (int i = 0; i < Players.Count; i++)
            if (Players.At(i) is { } other && !other.IsLocallyOwned && _rules.ContainsKey(CarrySlot.ItemId(other))) CarrySlot.ShowOnBack(other);
    }

    private bool Releases(PlayerInventoryBase inventory)
        => _rule != null && CarrySlot.IsLocal(inventory);

    private bool Hides(InventorySlot slot) => slot == InventorySlot.InLevelCarry && _rule != null && CarrySlot.OnBack(Players.Local);

    private Vector3 Walked(PlayerAgent owner, Vector3 velocity) => _speed == 1f ? velocity : velocity * _speed;

    private void Spawned(ItemSpawn call)
    {
        if (call.Item == null || LevelRules() is not { } rules || !rules.TryGetValue(call.Id, out var rule) || rule.HoldSeconds is not { } seconds) return;
        CarrySlot.SetPickupHold(call.Item, seconds);
    }

    private Dictionary<uint, CarryOnBackRule>? LevelRules()
        => Level.TryActiveExpedition(out var rundownId, out var tier, out var index)
           && LevelAddress.FromNative(rundownId, tier, index) is { } level ? _levels.GetValueOrDefault(level) : null;

    private void Clear()
    {
        _rule = null;
        _speed = 1f;
        if (_shown != null) { _shown.Visible = false; _shown = null; }
    }

    private HudKeyHint? _labelHint;
    private bool _labelInHands;

    private void ShowHint(KeyCode key, bool inHands)
    {
        if (!_hints.TryGetValue(key, out var hint))
            _hints[key] = hint = UIKit.Mod(ForgeEngine.Plugin.PluginGuid).AddKeyHint("CarryOnBack." + key, key, "");
        if (hint == null) return;
        if (_shown != hint && _shown != null) _shown.Visible = false;
        _shown = hint;
        if (_labelHint != hint || _labelInHands != inHands)
        {
            hint.Label = UIText.Key(inHands ? "carry.sling" : "carry.take");
            _labelHint = hint; _labelInHands = inHands;
        }
        hint.Visible = true;
    }
}
