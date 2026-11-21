using Il2CppInterop.Runtime.Attributes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Gear;
using Globals;
using Player;
using SNetwork;

namespace ForgeDevelopment.Native;

/// <summary>The lobby facts the unattended drivers wait on before they press ready and drop (<see cref="MenuDrive"/> and the
/// driver's lobby leave): the expedition selection took effect, no human is in the lobby, the declared bot slots hold joined bots with their
/// declared gear, and the game's start gate is open. Read-only except that an undeclared slot is closed.</summary>
internal static class LobbyGate
{
    internal static bool NativeEveryoneReady()
    {
        try { return GameStateManager.IsEveryoneReady(false); }
        catch (Exception) { return false; } // Il2Cpp read guard: not ready keeps the lobby wait going, and the stall watch names a wait that never ends.
    }

    internal static bool NativeStartReady()
    {
        // IsBotActionAllowed is the native auto-spawn cooldown; a slotted bot with a PlayerAgent has already passed it.
        if (!ForgeCore.Session.IsHost || !Global.HasNetwork || RundownManager.ActiveExpedition == null
            || GameStateManager.CurrentStateName != eGameStateName.Lobby || PlayerManager.Current == null) return false;
        var slots = SNet.Slots;
        var slotCount = slots.PlayerSlots.Length;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var player = slots.GetPlayerInSlot(slot);
            if (player?.IsBot == true && !player.HasPlayerAgent) return false;
        }
        return true;
    }

    internal static bool LobbyGearReady(PlayerBackpack backpack, InventorySlot inventory, uint offlineId)
    {
        // Bot spawn restores the registered favorite through ToJSON -> new GearIDRange(json).
        // That packet preserves components, not PlayfabItemInstanceId. Resolve the declared catalogue
        // entry by its ID, then use the native live-item/component readback, as the lobby program does.
        var key = "OfflineGear_ID_" + offlineId.ToString(CultureInfo.InvariantCulture);
        GearIDRange? expected = null;
        foreach (var candidate in GearManager.GetAllGearForSlot(inventory))
        {
            if (candidate == null || candidate.PlayfabItemInstanceId != key) continue;
            if (expected != null) throw new InvalidOperationException("ambiguous saved bot gear: " + key);
            expected = candidate;
        }
        if (expected == null) throw new InvalidOperationException("missing saved bot gear: " + key);
        return backpack.HasGear(expected, inventory);
    }

    /// <summary>One membership check for setup and the entire run, including humans not yet slotted. Bots are allowed.</summary>
    internal static bool HasOtherHuman()
    {
        // Each of these properties is an interop call that wraps a native object: read them once, not once per iteration.
        var slots = SNet.Slots;
        var slotCount = slots != null ? slots.PlayerSlots.Length : 0;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var player = slots!.GetPlayerInSlot(slot);
            if (player != null && !player.IsLocal && !player.IsBot) return true;
        }
        var lobby = SNet.LobbyPlayers;
        if (lobby != null)
        {
            var lobbyCount = lobby.Count;
            for (var index = 0; index < lobbyCount; index++)
            {
                var player = lobby[index];
                if (player != null && !player.IsLocal && !player.IsBot) return true;
            }
        }
        return false;
    }

    /// <summary>Slot permissions outlive the hub (build 20403457: a hub created after a level whose roster opened a bot
    /// slot spawns that bot before the host is slotted, kicks it as Kick_GameFull and then refuses the drop). The roster
    /// that opened a slot closes it again before the host leaves, so the next level starts from the closed default.</summary>
    [HideFromIl2Cpp]
    internal static void CloseBotSlotsBeforeLeaving()
    {
        var slots = SNet.Slots;
        if (slots == null || !ForgeCore.Session.IsHost) return;
        for (var slot = 0; slot < slots.PlayerSlots.Length; slot++)
        {
            if (slots.GetPlayerInSlot(slot)?.IsLocal == true) continue;
            if (slots.GetSlotPermission(slot) != SNet_PlayerSlotManager.SlotPermission.Forbidden)
                slots.SetSlotPermission(slot, SNet_PlayerSlotManager.SlotPermission.Forbidden);
        }
    }

    [HideFromIl2Cpp]
    internal static Dictionary<int, JsonObject[]> GroupBotSlots(JsonArray? slots)
        => (slots ?? new JsonArray()).OfType<JsonObject>().Where(row => (int?)row["slot"] is not null)
            .GroupBy(row => (int)row["slot"]!).ToDictionary(group => group.Key, group => group.ToArray());

    /// <summary>Selection is replicated asynchronously: the expedition the loadout page exposes is the target only
    /// once the native selection data says so.</summary>
    internal static bool SelectionApplied(uint rundown, int tier, int ordinal)
    {
        var selected = RundownManager.GetActiveExpeditionData();
        return Global.RundownIdToLoad == rundown && selected.rundownType == eRundownKey.Local
            && selected.rundownKey.data == RundownManager.GetLocalRundownKey(rundown)
            && (int)selected.tier == tier && selected.expeditionIndex == ordinal;
    }

    /// <summary>The roster gates of a lobby drive: the declared slots hold bots that finished joining, every other non-local slot is
    /// closed and empty, and no spawned bot is still joining. Returns the first unmet gate with its native readback, or null when the
    /// roster is ready.</summary>
    [HideFromIl2Cpp]
    internal static string? LobbyRosterPending(Dictionary<int, JsonObject[]> declaredBySlot)
    {
        var slots = SNet.Slots;
        for (var slot = 0; slot < slots.PlayerSlots.Length; slot++)
        {
            var player = slots.GetPlayerInSlot(slot);
            if (player?.IsLocal == true) continue;
            declaredBySlot.TryGetValue(slot, out var declared);
            if (declared?.Length > 1) throw new InvalidOperationException("duplicate saved bot slot: " + slot);
            var row = declared?[0];
            var bot = (bool?)row?["bot"] == true;
            // Declared slots belong to ForgeMap's lobby program. Only unused slots are closed by this runner.
            if (row == null && slots.GetSlotPermission(slot) != SNet_PlayerSlotManager.SlotPermission.Forbidden)
                slots.SetSlotPermission(slot, SNet_PlayerSlotManager.SlotPermission.Forbidden);
            var permission = bot ? SNet_PlayerSlotManager.SlotPermission.Bot : SNet_PlayerSlotManager.SlotPermission.Forbidden;
            var actual = slots.GetSlotPermission(slot);
            if (actual != permission) return "slot" + slot + " permission=" + actual + " expected=" + permission;
            if (!bot)
            {
                if (player != null) return "slot" + slot + " is closed but occupied " + SlotFlags(player);
                continue;
            }
            if (player == null) return "slot" + slot + " declared bot has not joined";
            if (!player.IsInSessionHub || !player.IsInSlot || !player.HasCharacterSlot
                || player.CharacterSlot == null || player.Session.characterSlotIndex != player.CharacterSlot.index)
                return "slot" + slot + " bot is still joining " + SlotFlags(player);
            if (row?["character"] is JsonValue character && character.TryGetValue<int>(out var desired)
                && (player.CharacterIndex != desired || player.Session.characterSlotIndex != desired))
                return "slot" + slot + " bot character=" + player.CharacterIndex + " expected=" + desired;
            var backpack = PlayerBackpackManager.GetBackpack(player);
            if (backpack == null) return "slot" + slot + " bot has no backpack";
            foreach (var gear in row?["loadout"] as JsonObject ?? new JsonObject())
            {
                var inventory = Enum.Parse<InventorySlot>(gear.Key);
                if (!LobbyGearReady(backpack, inventory, gear.Value!.GetValue<uint>()))
                    return "slot" + slot + " bot gear " + gear.Key + " is not the declared " + gear.Value;
            }
        }
        // A spawned but not yet slotted bot must finish joining (or leaving) before the ready/drop UI is used.
        var lobby = SNet.LobbyPlayers;
        if (lobby == null) return "lobby player list unavailable";
        for (var i = 0; i < lobby.Count; i++)
            if (lobby[i] != null && !lobby[i].IsLocal && (!lobby[i].IsBot || !lobby[i].IsInSlot))
                return "lobby player " + i + " is not a slotted bot " + SlotFlags(lobby[i]);
        return null;
    }

    internal static string SlotFlags(SNet_Player? player) => player == null ? "empty" : string.Join(',',
        player.IsLocal ? "local" : "remote", player.IsBot ? "bot" : "human",
        player.IsInSessionHub ? "hub" : "no-hub", player.IsInSlot ? "slot" : "no-slot",
        player.HasCharacterSlot ? "character" : "no-character", player.HasPlayerAgent ? "agent" : "no-agent");

    internal static string SelectionText()
    {
        var selected = RundownManager.GetActiveExpeditionData();
        return "selection=" + Global.RundownIdToLoad + "/" + selected.rundownType + "/" + selected.rundownKey.data
            + "/" + (int)selected.tier + "/" + selected.expeditionIndex;
    }
}
