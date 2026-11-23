using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeEngine.GameBindings;
using GameData;
using Gear;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgePlayer.Native;

/// <summary>One pre-drop slot preparation through the vanilla permission, character, replication and backpack
/// paths. PlayerManager.UpdateSpawnBots remains the only code that creates or removes a bot.</summary>
internal sealed class NativeBotSlotRequest : IRuntimeBotSlotRequest
{
    private readonly int _slot;
    private readonly bool _enabled;
    private readonly int? _character;
    private readonly IReadOnlyList<RuntimeBotGear> _loadout;
    private readonly int _session;
    private readonly IntPtr _slots;
    private readonly SNet_PlayerSlotManager.SlotPermission _permission;
    private SNet_Player? _bot;
    private ulong _botLookup;
    private IntPtr _botPointer;
    private bool _submitted;
    private int _reasserts;
    private const int ReassertLimit = 8;

    private NativeBotSlotRequest(int slot, bool enabled, int? character,
        IReadOnlyList<RuntimeBotGear> loadout)
    {
        _slot = slot; _enabled = enabled; _character = character; _loadout = loadout;
        _session = SNet.SessionHub!.SessionID; _slots = SNet.Slots!.Pointer;
        _permission = enabled ? SNet_PlayerSlotManager.SlotPermission.Bot : SNet_PlayerSlotManager.SlotPermission.Forbidden;
        var bot = SNet.Slots.GetPlayerInSlot(slot);
        if (bot != null) BindBot(bot);
    }

    internal int Slot => _slot;
    internal bool Enabled => _enabled;
    internal int? Character => _character;

    // Lobby state is visible before the host receives its player/character assignments. Spawning a bot during
    // that window lets vanilla claim the host's pending slot and then reject the bot as GameFull.
    internal static bool HostReady
    {
        get
        {
            var host = SNet.LocalPlayer;
            var slots = SNet.Slots;
            if (host == null || slots == null || !host.IsInSessionHub || !host.IsInSlot
                || !host.HasCharacterSlot || host.CharacterSlot == null) return false;
            int slot = host.PlayerSlotIndex(), character = host.Session.characterSlotIndex;
            return slot >= 0 && slot < slots.PlayerSlots.Length && character >= 0 && character < slots.CharacterSlots.Length
                && host.CharacterSlot.index == character
                && slots.GetPlayerInSlot(slot)?.Pointer == host.Pointer
                && slots.CharacterSlots[character].player?.Pointer == host.Pointer;
        }
    }

    internal static NativeBotSlotRequest Begin(int slot, bool enabled, int? character,
        IReadOnlyList<RuntimeBotGear> loadout)
    {
        Lobby();
        if (slot < 0 || slot >= SNet.Slots!.PlayerSlots.Length) throw Refuse("slot-invalid");
        if (!enabled && (character != null || loadout.Count != 0)) throw Refuse("request-invalid");
        if (enabled && RundownManager.ActiveExpedition?.IsSinglePlayer == true) throw Refuse("single-player-expedition");
        if (character is { } index && (index < 0 || index >= SNet.Slots!.CharacterSlots.Length))
            throw Refuse("character-invalid");
        var seen = new HashSet<InventorySlot>();
        foreach (var row in loadout)
        {
            if (!seen.Add(row.Slot) || row.Slot is not (InventorySlot.GearStandard or InventorySlot.GearSpecial
                    or InventorySlot.GearClass or InventorySlot.GearMelee) || row.Gear == null)
                throw Refuse("loadout-invalid");
            var item = ItemDataBlock.GetBlock(row.Gear.GetCompID(eGearComponent.BaseItem));
            if (item == null || item.inventorySlot != row.Slot) throw Refuse("gear-slot-mismatch");
        }
        var request = new NativeBotSlotRequest(slot, enabled, character, loadout);
        request.CheckIdentity();
        request.CheckCharacter();
        // The return value is a human-availability change, not a commit result (build 20403457 RVA 606F40).
        SNet.Slots!.SetSlotPermission(slot, request._permission);
        if (SNet.Slots.GetSlotPermission(slot) != request._permission) throw Refuse("permission-readback-mismatch");
        return request;
    }

    internal bool Ready
    {
        get
        {
            CheckIdentity();
            var permission = SNet.Slots!.GetSlotPermission(_slot);
            if (permission != _permission)
            {
                // The loadout page re-applies the player's saved slot permissions (CM_PageLoadout.ApplyPlayerSlotPermissionsFromSettings,
                // on page activation) and so overwrites this request's write whenever the page opens after it, which is
                // the normal order after a selection. The request is the roster's owner for its slot, so it writes
                // its permission again; only a write that does not hold within the budget is a refusal.
                if (_reasserts++ >= ReassertLimit) throw new PermissionChanged(_permission, permission, _submitted, _session, _botPointer != IntPtr.Zero);
                SNet.Slots.SetSlotPermission(_slot, _permission);
                var again = SNet.Slots.GetSlotPermission(_slot);
                if (again != _permission) throw new PermissionChanged(_permission, again, _submitted, _session, _botPointer != IntPtr.Zero);
            }
            var bot = SNet.Slots.GetPlayerInSlot(_slot);
            if (!_enabled) return bot == null;
            if (bot == null) return false;
            if (_bot == null) BindBot(bot);
            return bot.IsInSessionHub && bot.IsInSlot && bot.HasCharacterSlot && bot.CharacterSlot != null
                && bot.Session.characterSlotIndex == bot.CharacterSlot.index;
        }
    }

    internal NativeBotSlotRequest Apply()
    {
        if (!Ready) throw Refuse("slot-not-ready");
        if (_submitted) throw Refuse("already-submitted");
        if (!_enabled) { _submitted = true; return this; }
        CheckCharacter();
        var bot = _bot!;
        var backpack = PlayerBackpackManager.GetBackpack(bot);
        if (backpack == null) throw Refuse("backpack-missing");
        if (_character is { } desired && bot.CharacterIndex != desired)
        {
            var previous = bot.CharacterIndex;
            try
            {
                if (!SNet.Slots!.ManageSlot(bot, SNet_SlotType.CharacterSlot, SNet_SlotHandleType.Remove, previous)
                    || !SNet.Slots.ManageSlot(bot, SNet_SlotType.CharacterSlot, SNet_SlotHandleType.Set, desired))
                    throw Refuse("character-assignment-refused");
                SNet.Sync.MasterBotCommand(bot, eMasterCommandType.PickAvatar, desired);
                if (bot.CharacterIndex != desired || bot.Session.characterSlotIndex != desired)
                    throw Refuse("character-readback-mismatch");
            }
            catch (Exception error)
            {
                try
                {
                    SNet.Slots!.ManageSlot(bot, SNet_SlotType.CharacterSlot, SNet_SlotHandleType.Remove, desired);
                    if (!SNet.Slots.ManageSlot(bot, SNet_SlotType.CharacterSlot, SNet_SlotHandleType.Set, previous))
                        throw Refuse("character-restore-refused");
                    SNet.Sync.MasterBotCommand(bot, eMasterCommandType.PickAvatar, previous);
                    if (bot.CharacterIndex != previous || bot.Session.characterSlotIndex != previous)
                        throw Refuse("character-restore-mismatch");
                }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        _submitted = true;
        foreach (var row in _loadout)
        {
            if (!backpack.HasGear(row.Gear, row.Slot))
            {
                if (backpack.AmmoStorage == null) throw Refuse("ammo-storage-missing");
                backpack.AmmoStorage.Reset();
                PlayerBackpackManager.EquipBotGear(bot, row.Gear);
            }
            // CM_InventorySlotItem.PickWeapon passes the lobby player slot, not the character index.
            GearManager.RegisterBotGearInSlotAsEquipped(row.Gear, row.Slot, _slot);
        }
        return this;
    }

    internal bool Applied
    {
        get
        {
            if (!Ready || !_submitted) return false;
            if (!_enabled) return true;
            var bot = _bot!;
            if (_character is { } desired && (bot.CharacterIndex != desired || bot.Session.characterSlotIndex != desired))
                throw Refuse("character-changed");
            var backpack = PlayerBackpackManager.GetBackpack(bot);
            if (backpack == null) throw Refuse("backpack-missing");
            foreach (var row in _loadout) if (!backpack.HasGear(row.Gear, row.Slot)) return false;
            return true;
        }
    }

    internal string Readback
    {
        get
        {
            CheckIdentity();
            var bot = SNet.Slots!.GetPlayerInSlot(_slot);
            var gear = new Dictionary<string, object>();
            if (bot != null)
            {
                var backpack = PlayerBackpackManager.GetBackpack(bot);
                foreach (var slot in new[] { InventorySlot.GearStandard, InventorySlot.GearSpecial,
                             InventorySlot.GearClass, InventorySlot.GearMelee })
                    if (backpack != null && backpack.TryGetBackpackItem(slot, out var item) && item?.GearIDRange != null)
                        gear[slot.ToString()] = new
                        {
                            catalogId = item.GearIDRange.PlayfabItemId,
                            instanceId = item.GearIDRange.PlayfabItemInstanceId,
                            composition = item.GearIDRange.ToJSON()
                        };
            }
            return JsonSerializer.Serialize(new
            {
                slot = _slot,
                permission = SNet.Slots.GetSlotPermission(_slot).ToString(),
                bot = bot?.IsBot,
                lookup = bot?.Lookup,
                character = bot?.CharacterIndex,
                sessionCharacter = bot?.Session.characterSlotIndex,
                loadout = gear,
                submitted = _submitted,
                applied = Applied
            });
        }
    }

    private void CheckIdentity()
    {
        Lobby();
        if (SNet.SessionHub!.SessionID != _session || SNet.Slots!.Pointer != _slots) throw Refuse("session-changed");
        var bot = SNet.Slots.GetPlayerInSlot(_slot);
        if (bot != null && !bot.IsBot) throw Refuse("human-slot");
        if (_bot != null)
        {
            // UpdateSpawnBots removing the captured bot is the successful transition for a disabled slot.
            bool replaced = bot == null ? _enabled : bot.Lookup != _botLookup || bot.Pointer != _botPointer;
            // Before the loadout is submitted, the bot captured at construction is the slot's previous occupant, and vanilla
            // re-creates a slot's bot when its permission or character changes: that is the normal path, so Ready binds the
            // new occupant. Only a bot replaced after it was given its loadout has lost that loadout.
            if (replaced && _submitted) throw Refuse("bot-replaced");
            if (replaced) _bot = null;
        }
    }

    private void CheckCharacter()
    {
        if (_character is not { } desired) return;
        var occupant = SNet.Slots!.CharacterSlots[desired].player;
        if (occupant != null && (_bot == null || occupant.Pointer != _botPointer)) throw Refuse("character-occupied");
        if (_bot?.HasPlayerAgent == true && _bot.CharacterIndex != desired) throw Refuse("character-agent-already-spawned");
    }

    private void BindBot(SNet_Player bot)
    { _bot = bot; _botLookup = bot.Lookup; _botPointer = bot.Pointer; }

    bool IRuntimeBotSlotRequest.Ready => Ready;
    bool IRuntimeBotSlotRequest.Applied => Applied;
    string IRuntimeBotSlotRequest.Readback => Readback;
    IRuntimeBotSlotRequest IRuntimeBotSlotRequest.Apply() => Apply();

    private static void Lobby()
    {
        if (!Session.IsHost) throw Refuse("host-required");
        if (GameStateManager.CurrentStateName != eGameStateName.Lobby || !SNet.IsInLobby
            || SNet.Slots == null || SNet.SessionHub == null || !HostReady) throw Refuse("lobby-required");
    }

    private static InvalidOperationException Refuse(string code) => new(ForgeEngine.Logging.DynamicDiagnosticCodes.Of("native-bot.{code}", code));
    private const string PermissionChangedReason = "permission-changed";

    internal sealed class PermissionChanged(SNet_PlayerSlotManager.SlotPermission expected,
        SNet_PlayerSlotManager.SlotPermission actual, bool submitted, int session, bool botBound)
        : InvalidOperationException("native-bot." + PermissionChangedReason)
    {
        internal string ExpectedPermission => expected.ToString();
        internal string ActualPermission => actual.ToString();
        internal string Phase => submitted ? "applied" : "waiting-membership";
        internal int Session => session;
        internal bool BotBound => botBound;
    }
}
