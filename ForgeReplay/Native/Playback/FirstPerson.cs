using ForgeCore;
using System;
using System.Collections.Generic;
using Gear;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The first-person view of a recorded player (PLAN 5.4): the observer's own first-person arms and gun, which the game attaches to
/// the local player's <c>FirstPersonItemHolder</c>, are moved to the eye the replay camera shows after the game has placed them, so the
/// observer's body never moves. The weapon in the hands follows the recorded wielded slot (the viewed player's gear is equipped from what the
/// recording has the player wearing at that moment); recorded shots, swings, reloads and tool use play the game's own first-person animations of the item in the hands, and
/// the recorded stance and speed drive its idle, walk and run sway and the jump and land motion. Animation only: nothing is fired and
/// no swing deals damage. The view is the screen camera's eyes mode (<see cref="ReplayCamera"/> places the camera and calls
/// <see cref="Frame"/>). Main thread only.</summary>
internal static class FirstPerson
{
    private const float WalkSpeed = 0.3f, RunSpeed = 3.2f;

    private enum Move { None, Idle, Walk, Run }

    // The four gear lines of a loadout (standard, special, melee, tool) and the slots they fill.
    private static readonly InventorySlot[] EquipSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearMelee, InventorySlot.GearClass };
    // The level's items a player holds in the hands (resource pack, consumable, a big carried item) are not gear: the game puts one in a backpack slot when it is picked up
    // and brings up its first-person model from there. The replay does the same for the observer's backpack for as long as the recording holds that slot: the game's own
    // store (PlayerBackpack.SpawnAndEquipItem, the call its pickup path ends in) with no pickup, no packet and no pickup prompt, and the game's own clear when the slot is let go.
    private static readonly InventorySlot[] ItemSlots = { InventorySlot.ResourcePack, InventorySlot.Consumable, InventorySlot.InLevelCarry };
    private static readonly uint[] _placed = new uint[ItemSlots.Length];
    private static readonly float[] _outAt = new float[ItemSlots.Length];
    private static readonly HashSet<(int Slot, uint Item)> _unplaceable = new();
    private const float ItemLetGoSeconds = 0.5f;

    private static bool _hooked, _have, _lamp, _aiming;
    private static Vector3 _eye;
    private static Quaternion _rotation;
    private static int _wanted = -1, _lastWield = -1, _equipFor = -1, _lastLocomotion = -1, _swing;
    internal const int GearLines = 4;
    private const float GiveUpSeconds = 20f;
    private static string[] _loadout = Array.Empty<string>();
    private static string _keptFrom = "";
    private static bool[] _on = Array.Empty<bool>();
    private static float[] _asked = Array.Empty<float>();
    private static float _nextEquip, _nextWield, _speed;
    private static Move _move;

    /// <summary>True while the screen camera shows the viewed player's eyes.</summary>
    internal static bool Active => Replayer.Active && ReplayCamera.Eyes;

    /// <summary>True while the viewed player aims down sights (the recorded state; only the recorder's own player has one).</summary>
    internal static bool Aiming => Active && _aiming;

    // The game's equip, the holder's draw and its transitions run on game time, which a paused replay has stopped (time scale 0): a gun or pack asked for while paused stayed at
    // the start of its draw, low in the frame or not there. For a short while after each ask the replay lets the game's clock run (Replayer.Late), so the arms get to the raised pose.
    private const float SettleSeconds = 0.8f;
    private static float _settleUntil, _switchFrom;
    private static int _switchNotes;
    internal static bool Settling => Active && ForgeCore.Frame.UnscaledTime < _settleUntil;

    private static void Settle()
    {
        var now = ForgeCore.Frame.UnscaledTime;
        if (now >= _settleUntil) _switchFrom = now;
        _settleUntil = now + SettleSeconds;
    }

    internal static void Install()
    {
        if (_hooked) return;
        _hooked = true;
        // The holder reads the force at the top of its own LateUpdate (FirstPersonItemHolder.UpdateAimInput), before the after-hook can set
        // it: the before-hook puts the recorded state where that read looks, and Pin writes it again after the holder has finished, so the
        // next frame's Update sees the recording's trigger and not the observer's released aim.
        ForgeCore.GameHooks.FirstPersonHolderLateUpdate.Before(Force);
        ForgeCore.GameHooks.FirstPersonHolderLateUpdate.After((holder, _) => Pin(holder));
        // The swing of the observer's weapon is a picture of the recorded one: it deals no damage to what stands in front of the camera.
        ForgeCore.GameHooks.MeleeAttackDamage.Skip(_ => Active);
    }

    // Before the game's LateUpdate: the force the holder's own UpdateAimInput reads and turns into its trigger this frame. Only the local
    // player's holder is driven; another holder's aim is the game's own.
    private static void Force(FirstPersonItemHolder holder)
    {
        if (!Active || holder == null || holder.WasCollected) return;
        var owner = holder.m_owner;
        if (owner == null || !owner.IsLocallyOwned) return;
        holder.ForceAimTrigger = _aiming;
    }

    // After the game placed the holder: put it at the eye, so the arms and the gun are in front of the camera.
    private static void Pin(FirstPersonItemHolder holder)
    {
        if (!Active || !_have || holder == null || holder.WasCollected) return;
        var owner = holder.m_owner;
        if (owner == null || !owner.IsLocallyOwned) return;
        // This frame's eye: the camera may be placed only after this update (a frame behind, the arms and what follows them trail every turn).
        if (ReplayCamera.EyePose(out var eye, out var look)) holder.transform.SetPositionAndRotation(eye, look);
        else holder.transform.SetPositionAndRotation(_eye, _rotation);
        // The force, not only the trigger: the holder rewrites its own trigger from the observer's input in UpdateAimInput, so the force is
        // put there again after the holder has run. ItemAimTrigger is written as well because the state machine (FPIS_Hip.Update /
        // FPIS_Aim.Update) reads that in the next Update, before the next LateUpdate: without it the first read after this update still
        // saw the observer's released aim and the weapon dropped out of the sight for a frame.
        holder.ForceAimTrigger = _aiming;
        if (!_aiming) holder.ItemAimTrigger = false;
        else if (holder.WieldedItem is { } item && item.AimingAllowed) holder.ItemAimTrigger = true;
        Inertia(holder);
        if (owner.FPSCamera != null) owner.FPSCamera.m_lookInputMouse = _lookInput;
    }

    /// <summary>The arms hold the slot the recording says and the item in that slot is the viewed player's, not the observer's own or a piece
    /// still being built. The other three pieces do not hold the picture back.</summary>
    internal static bool HoldsRecorded(PlayerAgent local)
    {
        if ((int)local.Inventory.WieldedSlot != _wanted) return false;
        var line = Array.IndexOf(EquipSlots, (InventorySlot)_wanted);
        if (line >= 0) return line < _on.Length && _on[line];
        var item = Array.IndexOf(ItemSlots, (InventorySlot)_wanted);
        return item >= 0 && _placed[item] != 0;
    }

    /// <summary>Leaving the eyes mode: forget what was driven, so a later entry wields and moves again from the start. The gear stays on.</summary>
    internal static void Reset()
    {
        _settleUntil = 0f; _have = false; _wanted = -1; _lastWield = -1; _lastLocomotion = -1; _move = Move.None; _speed = 0f; _swing = 0; _charge = Charging.None; _cancelAtMs = -1; LetGo(); ClearItems();
        _walkInertia = 0f; _runInertia = 0f; _lookKnown = false; _lookInput = Vector2.zero;
        var rest = Players.Local?.TryCast<PlayerAgent>()?.FPItemHolder;
        if (rest != null && !rest.WasCollected) Inertia(rest);
        if (_aiming)
        {
            _aiming = false;
            var holder = Players.Local?.TryCast<PlayerAgent>()?.FPItemHolder;
            if (holder != null && !holder.WasCollected) { holder.ForceAimTrigger = false; holder.ItemAimTrigger = false; }
        }
        if (!_lamp) return;
        _lamp = false;
        try { Players.Local?.TryCast<PlayerAgent>()?.Inventory?.SetFlashlightEnabled(false, true, false); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person flashlight could not be put out: " + error.Message); }
    }

    /// <summary>The replay ended: the gear is forgotten too.</summary>
    internal static void Forget()
    {
        Reset();
        _fire?.Dispose(); _fire = null;
        _equipFor = -1; _keptFrom = ""; _loadout = Array.Empty<string>(); _on = Array.Empty<bool>(); _unplaceable.Clear();
    }

    /// <summary>Where a shot of the viewed player starts while the eyes are the view: the muzzle of the first-person weapon in the hands.</summary>
    internal static bool Muzzle(int slot, out Vector3 muzzle)
    {
        muzzle = default;
        if (!Active || (slot & 3) != (Replayer.Follow & 3)) return false;
        var local = Players.Local?.TryCast<PlayerAgent>();
        if (local == null || local.WasCollected || !HoldsRecorded(local)) return false;
        var weapon = local.Inventory?.WieldedItem?.TryCast<Weapon>();
        if (weapon == null || weapon.MuzzleAlign == null) return false;
        muzzle = weapon.MuzzleAlign.position;
        return true;
    }

    /// <summary>How many of the followed player's gear lines are on the observer, of <see cref="GearLines"/>.</summary>
    internal static int GearDone
    {
        get
        {
            var done = 0;
            if (_equipFor >= 0) foreach (var on in _on) if (on) done++;
            return done;
        }
    }

    /// <summary>The followed player's gear is on the observer, or as near as it will get.</summary>
    internal static bool GearOn
    {
        get
        {
            if (_equipFor < 0) return false;
            foreach (var on in _on) if (!on) return false;
            return true;
        }
    }

    /// <summary>Every frame in the eyes mode: the arms at <paramref name="eye"/>, the wielded slot, the gear and the movement of the viewed player.</summary>
    internal static void Frame(Vector3 eye, Quaternion look, WorldState state, int follow, uint nowMs, IReadOnlyList<PlayerEntry> players, float seconds)
    {
        var local = Players.Local?.TryCast<PlayerAgent>();
        if (local == null || local.WasCollected) return;
        _eye = eye; _rotation = look; _have = true;
        LookInput(local, look);
        var status = state.PlayerStatus[follow & 3];
        var locomotion = PlayerLocomotion.PLOC_State.Stand;
        var wield = -1;
        if (status != null) { var read = PayloadReader.Over(status); read.U(); locomotion = (PlayerLocomotion.PLOC_State)read.U(); wield = (int)read.U(); }
        _wanted = wield;
        // The game brings each new piece up when it is built (PlayerBackpackManager.EquipLocalGear's callback), and the slot may not be allowed
        // yet: ask again for the recorded slot until the arms hold it.
        Items(local, state.PlayerKits[follow & 3], wield);
        if (wield > 0 && (int)local.Inventory.WieldedSlot != wield && (Array.IndexOf(EquipSlots, (InventorySlot)wield) >= 0 || ItemIn(wield)) && ForgeCore.Frame.UnscaledTime >= _nextWield)
        { _nextWield = ForgeCore.Frame.UnscaledTime + 0.1f; _lastWield = -1; }
        if (wield >= 0 && wield != _lastWield && !ItemMissing(wield))
        {
            _lastWield = wield; _move = Move.None; _swing = 0;
            Settle();
            try { local.Sync.WantsToWieldSlot((InventorySlot)wield, false); }
            catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person view could not wield slot " + wield + ": " + error.Message); }
        }
        SwitchNote(local, wield);
        // The lamp the viewed player had on: in these levels most of what a player saw is what it lit. It goes with the view (CameraLease.Look).
        if (state.PlayerKits[follow & 3] is { Known: true } kit && local.Inventory.FlashlightEnabled != kit.Flashlight)
        {
            try { local.Inventory.SetFlashlightEnabled(kit.Flashlight, true, false); _lamp = kit.Flashlight; }
            catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person flashlight could not be set: " + error.Message); }
        }
        Sway(local, state, follow, nowMs, locomotion, seconds);
        try { Charge(local, state, follow, nowMs); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person melee charge could not be shown: " + error.Message); }
    }

    // Diagnostic for the slow switch into the eyes (a one-time check, at most 6 lines a replay): when the arms hold the recorded slot, seconds after the ask.
    private static void SwitchNote(PlayerAgent local, int wield)
    {
        if (_settleUntil == 0f || _switchNotes >= 6 || wield <= 0 || _switchFrom == 0f) return;
        var now = ForgeCore.Frame.UnscaledTime;
        if ((int)local.Inventory.WieldedSlot != wield || local.Inventory.WieldedItem is not { } item || item.WasCollected) { if (now - _switchFrom > 6f) { _switchNotes++; _switchFrom = 0f; Plugin.Diagnostic.LogDebug("First-person switch: after 6 s the arms still do not hold slot " + wield + " (hold " + local.Inventory.WieldedSlot + ", placed " + string.Join("/", _placed) + ", time scale " + Time.timeScale + ")."); } return; }
        _switchNotes++;
        Plugin.Diagnostic.LogDebug("First-person switch: slot " + wield + " (" + item.name + ") in the arms " + (now - _switchFrom).ToString("0.00") + " s after the ask, time scale " + Time.timeScale + ", settling " + (now < _settleUntil) + ".");
        _switchFrom = 0f;
    }

    // The look sway: the holder pushes the arms and gun by the camera's last look input (UpdateLookSway reads LookCameraController.LastLookInput,
    // the mouse part being the yaw and pitch it turned this frame, in degrees). The observer's own mouse is blocked, so the viewed player's
    // turn between two frames is that input, written each frame and again after the holder's update (Pin). A jump in time is no turn.
    private const float LookJumpDegrees = 45f;
    private static bool _lookKnown;
    private static float _lastYaw, _lastPitch;
    private static Vector2 _lookInput;

    private static void LookInput(PlayerAgent local, Quaternion look)
    {
        var forward = look * Vec.Forward;
        var yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        var pitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        var turn = _lookKnown ? new Vector2(Mathf.DeltaAngle(_lastYaw, yaw), -(pitch - _lastPitch)) : Vector2.zero;
        _lookInput = Mathf.Abs(turn.x) > LookJumpDegrees || Mathf.Abs(turn.y) > LookJumpDegrees ? Vector2.zero : turn;
        _lastYaw = yaw; _lastPitch = pitch; _lookKnown = true;
        if (local.FPSCamera != null) local.FPSCamera.m_lookInputMouse = _lookInput;
    }

    private const float WalkFullSpeed = 2f;
    private static float _walkInertia, _runInertia;

    private static void Inertia(FirstPersonItemHolder holder)
    {
        holder.WalkInertia = _walkInertia;
        holder.RunInertia = _runInertia;
        holder.IsRunning = _runInertia > 0f;
    }

    // The sway of the arms and gun: the game starts it from the local player's own locomotion, which does not move here, so the viewed
    // player's recorded stance and speed (smoothed like the puppet's) start the same idle, walk and run, and a jump or a landing.
    private static void Sway(PlayerAgent local, WorldState state, int follow, uint nowMs, PlayerLocomotion.PLOC_State locomotion, float seconds)
    {
        // Only the recorder's own player has this; everyone else's aiming is not known to any machine but theirs. The transition is written when
        // it happens and held here; an older recording has only PlayerGun's 10 Hz sample.
        var aiming = ReplayExtras.Aim(state, follow & 3, out var recordedAim) ? recordedAim : ReplayExtras.Gun(state, follow & 3, out var gun) && gun.Aiming;
        if (aiming != _aiming) _aiming = aiming;
        var holder = local.FPItemHolder;
        if (holder == null || holder.WasCollected) return;
        _speed = Mathf.Lerp(_speed, Puppeteer.Velocity(state, follow & 3, nowMs).magnitude, 1f - Mathf.Exp(-seconds / 0.12f));
        var move = locomotion == PlayerLocomotion.PLOC_State.Run || _speed >= RunSpeed ? Move.Run : _speed >= WalkSpeed ? Move.Walk : Move.Idle;
        if (locomotion is PlayerLocomotion.PLOC_State.Jump or PlayerLocomotion.PLOC_State.Fall or PlayerLocomotion.PLOC_State.Land or PlayerLocomotion.PLOC_State.Downed) move = Move.Idle;
        // The walk and run bob: the holder's update eases its walk and run weights towards these two targets (0..1, UpdateCommonWeights), which
        // the observer's own locomotion leaves at rest; the viewed player's speed sets them, every frame and again after the holder's update (Pin).
        _walkInertia = move == Move.Walk ? Mathf.Clamp01(_speed / WalkFullSpeed) : 0f;
        _runInertia = move == Move.Run ? 1f : 0f;
        Inertia(holder);
        try
        {
            if (move != _move)
            {
                _move = move;
                switch (move) { case Move.Run: holder.OnRunStart(); break; case Move.Walk: holder.OnWalkStart(); break; default: holder.OnIdleStart(); break; }
            }
            if ((int)locomotion != _lastLocomotion)
            {
                if (locomotion == PlayerLocomotion.PLOC_State.Jump) holder.PlayJumpAnimation();
                else if (locomotion == PlayerLocomotion.PLOC_State.Land) holder.PlayLandAnimation();
                _lastLocomotion = (int)locomotion;
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person sway could not be driven: " + error.Message); }
    }

    // The item the recording has in the hands, in the observer's backpack slot for it: put in when the recorded wield is that slot (the kit says which item), taken out once the
    // recording holds something else (the arms are asked to leave it first) and when the eyes mode ends. A piece the game cannot make in first person is said once and left out.
    private static void Items(PlayerAgent local, PlayerKit kit, int wield)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        if (backpack == null) return;
        var clock = ForgeCore.Frame.UnscaledTime;
        for (var i = 0; i < ItemSlots.Length; i++)
        {
            var slot = ItemSlots[i];
            var want = wield != (int)slot || !kit.Known ? 0u : i == 0 ? kit.PackItem : i == 1 ? kit.ConsumableItem : kit.CarryItem;
            if (_placed[i] == want) { _outAt[i] = 0f; continue; }
            if (_placed[i] != 0)
            {
                if (_outAt[i] == 0f) _outAt[i] = clock;
                if (want == 0 && local.Inventory.WieldedSlot == slot && clock - _outAt[i] < ItemLetGoSeconds) continue;
                ClearItem(i);
                if (want == 0) continue;
            }
            if (!_unplaceable.Contains((i, want))) PlaceItem(local, backpack, i, want);
        }
    }

    // Once per change of the held item: the game's own store into the slot (an item it cannot make in first person is said once and left out).
    private static void PlaceItem(PlayerAgent local, PlayerBackpack backpack, int i, uint item)
    {
        var slot = ItemSlots[i];
        try
        {
            if (!ItemSpawnManager.ItemHasMode(item, ItemMode.FirstPerson)) { _unplaceable.Add((i, item)); Skipped.Note("an item in the viewed player's hands has no first-person form", "item " + item); return; }
            // Whatever the observer's own backpack holds there goes: the replay's level is the observer's alone.
            if (backpack.TryGetBackpackItem(slot, out var own) && own?.Instance != null) PlayerBackpackManager.ClearLocalSlot(slot, false, false);
            var made = backpack.SpawnAndEquipItem(item, slot, new pItemData { itemID_gearCRC = item, slot = slot });
            if (made == null || made.Instance == null) { _unplaceable.Add((i, item)); Skipped.Note("an item in the viewed player's hands could not be put in the first-person view", "item " + item); return; }
            // The game's own pickup path wields what it just stored (`PlayerSync.SyncWieldBPItem`, the call its `AddItem` makes after
            // `SpawnAndEquipItem`). Asking for the slot in the next frame, as a gear line is asked for, left a resource pack at the backpack's
            // root instead of in the first-person hands: the item is wielded here, the moment it is made, as the game does.
            local.Sync.SyncWieldBPItem(made);
            _placed[i] = item; _lastWield = -1; Settle();
        }
        catch (Exception error) { _unplaceable.Add((i, item)); Plugin.Logger.ErrorOnce("The first-person view could not hold item " + item + ": " + error.Message); }
    }

    // The slot is one of the level's items and the replay holds none in it (yet, or the game would not make it): there is nothing to bring up.
    private static bool ItemMissing(int wield)
    {
        var item = Array.IndexOf(ItemSlots, (InventorySlot)wield);
        return item >= 0 && _placed[item] == 0;
    }

    private static bool ItemIn(int wield)
    {
        var item = Array.IndexOf(ItemSlots, (InventorySlot)wield);
        return item >= 0 && _placed[item] != 0;
    }

    private static void ClearItem(int i)
    {
        _placed[i] = 0; _outAt[i] = 0f;
        try { PlayerBackpackManager.ClearLocalSlot(ItemSlots[i], false, false); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person view could not let go of its item: " + error.Message); }
    }

    private static void ClearItems()
    {
        for (var i = 0; i < _placed.Length; i++) if (_placed[i] != 0) ClearItem(i);
    }

    /// <summary>Every frame in the eyes, and while the replay is made ready: keeps the followed player's four gear lines (standard, special,
    /// melee, tool) on the observer. A piece is asked for once its assets are in (one at a time, the one the recording has in the hands
    /// first) and counts as on when the backpack's item in that slot is that gear, built. The game builds a piece when its parts have
    /// loaded, which takes as long as it takes: a piece that never shows up stops counting after a while.</summary>
    internal static void Keep(WorldState state, int follow, IReadOnlyList<PlayerEntry> players)
    {
        // What the player wears now (a tool changed in the level, a player who joined late); before the first puppet update the file header is all there is.
        var wearing = Puppeteer.LoadoutOf(follow);
        if (wearing.Length == 0)
            foreach (var player in players)
                if ((player.Slot & 3) == (follow & 3)) wearing = player.Loadout;
        if (_equipFor != follow || _keptFrom != wearing)
        {
            var samePlayer = _equipFor == follow;
            if (!samePlayer)
            {
                _lastWield = -1; _asked = new float[GearLines]; _on = new bool[GearLines];
                _loadout = new string[GearLines];
            }
            _equipFor = follow; _keptFrom = wearing;
            var lines = wearing.Split('\n');
            for (var i = 0; i < GearLines; i++)
            {
                var line = i < lines.Length ? Canonical(lines[i]) : "";
                if (samePlayer && line == _loadout[i]) continue;
                _loadout[i] = line; _on[i] = line.Length == 0; _asked[i] = 0f;
            }
        }
        // Every missing line is checked each frame, so the one in the hands is wielded the frame it is built (the 11-06 replays waited a frame per line).
        var clock = ForgeCore.Frame.UnscaledTime;
        for (var check = 0; check < GearLines; check++)
        {
            if (_on[check]) continue;
            var worn = Worn(check);
            if (!worn && _asked[check] > 0f && clock - _asked[check] > GiveUpSeconds)
            {
                Skipped.Note("the first-person view's gear did not arrive in " + GiveUpSeconds + " s", "slot " + EquipSlots[check]);
                worn = true;
            }
            if (worn) { _on[check] = true; Puppeteer.RecheckGear(); if (_lastWield == (int)EquipSlots[check]) _lastWield = -1; }
        }
        if (clock < _nextEquip) return;
        var first = -1;
        var status = state.PlayerStatus[follow & 3];
        if (status != null) { var read = PayloadReader.Over(status); read.U(); read.U(); first = Array.IndexOf(EquipSlots, (InventorySlot)read.U()); }
        // Every line whose parts are loaded is asked for at once (the one in the hands first): the game builds each slot on its own.
        for (var i = -1; i < GearLines; i++)
        {
            var line = i < 0 ? first : i;
            if (line < 0 || _on[line] || _asked[line] > 0f) continue;
            _nextEquip = clock + 0.1f;
            try
            {
                var reading = ForgeCore.Gear.EquipLocal(new GearIDRange(_loadout[line]));
                if (reading.Status == ForgeCore.GearAssetStatus.Ready) _asked[line] = clock;
                else if (reading.Status != ForgeCore.GearAssetStatus.Loading) { Skipped.Note("the first-person view could not equip gear", "slot " + EquipSlots[line] + " of player " + follow + ": " + reading.Detail); _on[line] = true; }
            }
            catch (Exception error) { Plugin.Logger.ErrorOnce("The first-person view could not read a loadout line: " + error.Message); _on[line] = true; }
        }
    }

    private static string Canonical(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return "";
        try { return new GearIDRange(line).ToJSON().Replace("\n", " "); }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A loadout line could not be read: " + error.Message); return ""; }
    }

    private static bool Worn(int line)
    {
        var backpack = PlayerBackpackManager.LocalBackpack;
        return backpack != null && backpack.TryGetBackpackItem(EquipSlots[line], out var item) && item != null && item.Instance != null && item.GearIDRange != null
            && item.GearIDRange.ToJSON().Replace("\n", " ") == _loadout[line];
    }

    /// <summary>A recorded event of the viewed player plays the game's first-person animation of the item in the hands.</summary>
    internal static void Event(Record record, int follow)
    {
        if (!Active) return;
        if (record.EventType is not (EventKind.Shot or EventKind.Melee or EventKind.Reload or EventKind.Interact)) return;
        var read = new PayloadReader(record.Payload!);
        if (((int)read.U() & 3) != (follow & 3)) return;
        try
        {
            var local = Players.Local?.TryCast<PlayerAgent>();
            var item = local?.Inventory?.WieldedItem;
            // Not what the recording holds yet: its animation would play on a hidden item and still be running when the right one comes up.
            if (item == null || !HoldsRecorded(local!)) return;
            switch (record.EventType)
            {
                case EventKind.Shot:
                    // The pellets of one shotgun shot are Shot events of the same millisecond: one fire animation, as the presenter plays one sound.
                    if (record.TimeMs == _shotMs && item == _shotItem) break;
                    _shotMs = record.TimeMs; _shotItem = item;
                    item.TriggerFireAnimationSequence();
                    // The game's own first-person shot recoil of the weapon in the hands: its RecoilData and its spring offsets kick the gun and the
                    // camera. It runs on the recorded shot, and the replay camera still puts the recorded look on every frame, so the recorded look is
                    // what the view settles back to.
                    item.TryCast<Weapon>()?.ApplyRecoil();
                    item.TryCast<Weapon>()?.m_muzzleFlash?.Play();
                    break;
                case EventKind.Melee:
                    Swing(item, (int)read.U());
                    break;
                case EventKind.Reload:
                    Reload(item);
                    break;
                case EventKind.Interact:
                    switch ((pGenericInteractAnimation.TypeEnum)read.U())
                    {
                        case pGenericInteractAnimation.TypeEnum.Reload: Reload(item); break;
                        case pGenericInteractAnimation.TypeEnum.MeleeShove: Swing(item, 2); break;
                        case pGenericInteractAnimation.TypeEnum.PlaceTripmine: item.TriggerFireAnimationSequence(); break;
                    }
                    break;
            }
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A first-person animation could not play: " + error.Message); }
    }

    // A local player's reload is recorded twice in the same millisecond, as the Reload event and as the Interact the game sends for it (read in
    // the 11-05 recording): a second sequence started on top of the first ran both on the same animator.
    private static void Reload(ItemEquippable item)
    {
        if (!item.IsReloading) item.TryTriggerReloadAnimationSequence();
    }

    // A swing of the melee weapon in the hands: the game's own state of the swing (light alternates left and right, 2 is the push), heavy the
    // release of the charge state (Charge). The recording writes a light swing once it was decided, and a charge is a press of its own.
    private static void Swing(ItemEquippable item, int kind)
    {
        var melee = item.TryCast<MeleeWeaponFirstPerson>();
        if (melee == null) return;
        if (kind == 0) _left = (_swing++ & 1) == 0;
        if (kind == 1)
        {
            _charge = Charging.Done; LetGo();
            // A charge held to its end is let go by the game's own charge state: that release is already playing.
            if (melee.CurrentState?.TryCast<MWS_AttackHeavy>() != null) return;
        }
        melee.ChangeState(kind switch
        {
            2 => eMeleeWeaponState.Push,
            1 => _left ? eMeleeWeaponState.AttackChargeReleaseLeft : eMeleeWeaponState.AttackChargeReleaseRight,
            _ => _left ? eMeleeWeaponState.AttackMissLeft : eMeleeWeaponState.AttackMissRight,
        });
    }

    private static uint _shotMs = uint.MaxValue;
    private static ItemEquippable? _shotItem;

    private enum Charging { None, Up, Done }
    private static Charging _charge;
    private static bool _left, _fireHeld;
    private static long _cancelAtMs = -1;
    private static ForgeCore.InputLease? _fire;
    private const long CancelGraceMs = 150;

    // The recorded charge (MeleeCharge) of the weapon in the hands: the game's charge state, with the fire button held for it, since the charge
    // state lets go by itself the moment the button is up. Its release is the heavy swing; a charge that ends without one goes back to idle.
    private static void Charge(PlayerAgent local, WorldState state, int follow, uint nowMs)
    {
        var charging = ReplayExtras.MeleeCharging(state, follow & 3);
        var melee = HoldsRecorded(local) ? local.Inventory.WieldedItem?.TryCast<MeleeWeaponFirstPerson>() : null;
        if (melee == null) { _charge = Charging.None; _cancelAtMs = -1; LetGo(); return; }
        if (charging && _charge == Charging.None)
        {
            _charge = Charging.Up; _cancelAtMs = -1;
            // A charge has no light swing before it: it is a press of its own and takes the next side.
            _left = (_swing++ & 1) == 0;
            (_fire ??= ForgeCore.InputLease.Take(Plugin.Id)).Press(InputAction.Fire);
            _fireHeld = true;
            melee.ChangeState(_left ? eMeleeWeaponState.AttackChargeUpLeft : eMeleeWeaponState.AttackChargeUpRight);
        }
        else if (!charging && _charge == Charging.Done) _charge = Charging.None;
        else if (!charging && _charge == Charging.Up)
        {
            // The heavy swing and the end of the charge are written apart (the charge is read once a frame), so the swing gets a moment to come.
            if (_cancelAtMs < 0) _cancelAtMs = nowMs + CancelGraceMs;
            else if (nowMs >= _cancelAtMs || nowMs + CancelGraceMs < _cancelAtMs)
            {
                _charge = Charging.None; _cancelAtMs = -1; LetGo();
                melee.ChangeState(_left ? eMeleeWeaponState.CancelChargeToIdleLeft : eMeleeWeaponState.CancelChargeToIdleRight);
            }
        }
    }

    private static void LetGo()
    {
        if (!_fireHeld) return;
        _fireHeld = false;
        _fire?.Release(InputAction.Fire, requireConsumed: false);
    }
}
