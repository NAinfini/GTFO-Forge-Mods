using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using GameData;
using Gear;
using Player;
using SNetwork;
using ForgeCore;

namespace ForgeWeapon.Native;

/// <summary>
/// The game half of `forge.action.inventory.equip_gear`: every call that touches a game type.
///
/// The request itself is the game's own <c>PlayerBackpackManager.EquipLocalGear</c> for this machine's own player. It derives
/// the slot from the gear's base item and assembles the gear in the backpack of the calling machine's local player. A bot is
/// driven by the host, so on the host the same request for a bot is the game's own <c>PlayerBackpackManager.EquipBotGear</c>
/// for that bot, and the readback is the bot's own backpack. Any other recipient is refused instead of reaching into another
/// player's backpack. Nothing here decides how gear is built, how a
/// slot's old gear is torn down or how ammunition is filled; the game does, and the port only reads the result back.
/// </summary>
internal sealed class GearEquipPort : IGearEquipPort
{
    private readonly IForgeEngine _kernel;
    private readonly IPlayerDirectory _players;
    private readonly Func<bool> _canExecuteOwner;

    // The gear each (player, slot) held before this action first replaced it, kept for the world it was taken in.
    private readonly Dictionary<(string Session, GearSlot Slot), Departure> _departures = new();

    private readonly record struct Departure(long World, GearIDRange Gear, long? GearId);

    internal GearEquipPort(IForgeEngine kernel, IPlayerDirectory players, Func<bool> canExecuteOwner)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _canExecuteOwner = canExecuteOwner ?? throw new ArgumentNullException(nameof(canExecuteOwner));
    }

    public bool CanWrite => _canExecuteOwner();

    public GearRecipientState Locate(EntityReference recipient)
    {
        if (!TryPlayer(recipient, out _, out _, out var state)) return state;
        return GearRecipientState.Local;
    }

    /// <summary>The two resource kinds this action's gear inputs name, owned here because only the game's offline gear
    /// table knows which ids exist and which workshop a gear belongs to. A plan that compiled a gear id this install
    /// does not have is refused at load as `stale-resource`; a kind with no owner would refuse every id, real or not.</summary>
    internal static IReadOnlyDictionary<string, RuntimeResourceProvider> ResourceProviders() => new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
    {
        [GearEquipContract.WeaponKind] = RuntimeResourceProvider.Of(() => EnumerateGear(GearEquipContract.WeaponKind), id => ResolveGear(GearEquipContract.WeaponKind, id)),
        [GearEquipContract.ToolKind] = RuntimeResourceProvider.Of(() => EnumerateGear(GearEquipContract.ToolKind), id => ResolveGear(GearEquipContract.ToolKind, id))
    };

    private static IReadOnlyList<ResourceRef> EnumerateGear(string kind)
    {
        var found = new List<ResourceRef>();
        foreach (var block in GameDataBlockBase<PlayerOfflineGearDataBlock>.GetAllBlocks())
        {
            var reference = ResolveGear(kind, block.persistentID.ToString(CultureInfo.InvariantCulture));
            if (reference != null) found.Add(reference);
        }
        return found;
    }

    private static ResourceRef? ResolveGear(string kind, string id)
        => ResolveBlock(kind, id, out _) == GearResolution.Found ? new ResourceRef(kind, id) : null;

    public GearResolution Resolve(string resourceKind, string resourceId, out GearTarget target)
        => ResolveBlock(resourceKind, resourceId, out target);

    private static GearResolution ResolveBlock(string resourceKind, string resourceId, out GearTarget target)
    {
        target = default;
        if (!uint.TryParse(resourceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) return GearResolution.Unresolved;
        try
        {
            var block = PlayerOfflineGearDataBlock.GetBlock(id);
            if (block == null || string.IsNullOrEmpty(block.GearJSON)) return GearResolution.Unresolved;
            // GearJSON holds the components only; the offline record text is what the backpack and the equipment identity reader use.
            var gear = new GearIDRange(block.GearJSON)
            {
                PlayfabItemId = block.name,
                PlayfabItemInstanceId = EquipmentNativeAdapter.GearBlockPrefix + id.ToString(CultureInfo.InvariantCulture),
                OfflineGearType = block.Type
            };
            var item = ItemDataBlock.GetBlock(gear.GetCompID(eGearComponent.BaseItem));
            if (item == null) return GearResolution.Unresolved;
            if (SlotOf(item.inventorySlot) is not { } slot) return GearResolution.SlotUnsupported;
            // A weapon resource names a weapon slot and a tool resource the tool slot; a resource of the wrong workshop is not this gear.
            var isTool = slot == GearSlot.Tool;
            if (isTool != (resourceKind == GearEquipContract.ToolKind)) return GearResolution.SlotUnsupported;
            target = new GearTarget(slot, id, gear);
            return GearResolution.Found;
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.ResolveBlock", failure); return GearResolution.Unresolved; }
    }

    public bool TryDeparture(EntityReference recipient, GearSlot slot, out GearTarget target)
    {
        target = default;
        if (!TryPlayer(recipient, out _, out var session, out _)) return false;
        if (!_departures.TryGetValue((session, slot), out var kept) || kept.World != _kernel.WorldEpoch) return false;
        target = new GearTarget(slot, kept.GearId, kept.Gear);
        return true;
    }

    public GearSlotReading Read(EntityReference recipient, GearSlot slot)
    {
        if (!TryPlayer(recipient, out var backpack, out _, out _)) return default;
        try
        {
            var inventory = InventoryOf(slot);
            var item = backpack!.Slots[(int)inventory];
            if (item == null || item.GearIDRange == null) return new GearSlotReading(true, null, null, null);
            long? gear = long.TryParse(EquipmentNativeAdapter.GearBlock(item), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            int? clip = null;
            try { clip = item.Instance?.TryCast<BulletWeapon>()?.GetCurrentClip(); }
            catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.Read", failure); clip = null; }
            double? reserve = null;
            var pool = backpack.AmmoStorage?.GetInventorySlotAmmo(inventory);
            if (pool != null && float.IsFinite(pool.AmmoInPack)) reserve = pool.AmmoInPack;
            return new GearSlotReading(true, gear, clip, reserve);
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.Read#2", failure); return default; }
    }

    public bool TryHeld(EntityReference recipient, GearSlot slot, out GearTarget target)
    {
        target = default;
        if (!TryPlayer(recipient, out var backpack, out _, out _)) return false;
        try
        {
            var item = backpack!.Slots[(int)InventoryOf(slot)];
            if (item?.GearIDRange == null) return false;
            if (!long.TryParse(EquipmentNativeAdapter.GearBlock(item), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
            target = new GearTarget(slot, id, item.GearIDRange);
            return true;
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.TryHeld", failure); return false; }
    }

    public EntityReference? HeldEquipment(EntityReference recipient, GearSlot slot)
    {
        if (!TryPlayer(recipient, out var backpack, out _, out _)) return null;
        try
        {
            var instance = backpack!.Slots[(int)InventoryOf(slot)]?.Instance;
            return instance == null || instance.WasCollected ? null : _kernel.ResolveEntityInstance(EntityKinds.Equipment, instance);
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.HeldEquipment", failure); return null; }
    }

    public GearAssets PrepareAssets(GearTarget target)
    {
        if (target.Gear is not GearIDRange gear) return new(GearAssetState.Failed, "The equip target carries no native gear.");
        return GearAssetPreparation.Of(gear);
    }

    public void KeepDeparture(EntityReference recipient, GearSlot slot)
    {
        if (!TryPlayer(recipient, out var backpack, out var session, out _)) return;
        var world = _kernel.WorldEpoch;
        if (_departures.TryGetValue((session, slot), out var kept) && kept.World == world) return;
        try
        {
            var item = backpack!.Slots[(int)InventoryOf(slot)];
            if (item?.GearIDRange == null) return;
            long? gear = long.TryParse(EquipmentNativeAdapter.GearBlock(item), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            // The row is confirmed by the slot showing this gear's id, so a gear with no readable id could never be
            // confirmed and is not kept as a departure gear.
            if (gear == null) return;
            _departures[(session, slot)] = new Departure(world, item.GearIDRange, gear);
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.KeepDeparture", failure); /* a slot whose gear cannot be read keeps no departure gear; the departure source then refuses by name */ }
    }

    public bool SlotLeased(EntityReference recipient, GearSlot slot)
        => slot == GearSlot.Special && Locate(recipient) == GearRecipientState.Local && !IsBotRecipient(recipient)
           && ForgeCore.Gear.SpecialSlot.Taken;

    private bool IsBotRecipient(EntityReference recipient)
    {
        var player = _players.PlayerOf(recipient);
        return player != null && IsBotOfThisHost(player);
    }

    public void Equip(EntityReference recipient, GearTarget target)
    {
        if (target.Gear is not GearIDRange gear) throw new InvalidOperationException("The equip target carries no native gear.");
        var player = _players.PlayerOf(recipient);
        if (player != null && IsBotOfThisHost(player)) PlayerBackpackManager.EquipBotGear(player, gear);
        else if (ForgeCore.Gear.EquipLocal(gear) is { Status: not ForgeCore.GearAssetStatus.Ready } reading)
            throw new InvalidOperationException("The gear's assets were not ready at equip time: " + reading.Status + " " + reading.Detail);
    }

    private static bool IsBotOfThisHost(SNet_Player player)
    {
        try { return player.IsBot && Session.IsHost; }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.IsBotOfThisHost", failure); return false; }
    }

    private bool TryPlayer(EntityReference recipient, out PlayerBackpack? backpack, out string session, out GearRecipientState state)
    {
        backpack = null; session = "";
        if (recipient == null || !EntityKinds.Is(recipient, EntityKinds.Player))
        { state = GearRecipientState.NotPlayer; return false; }
        if (!_kernel.IsEntityCurrent(recipient)) { state = GearRecipientState.Stale; return false; }
        try
        {
            var player = _players.PlayerOf(recipient);
            if (player == null) { state = GearRecipientState.Stale; return false; }
            var address = _players.SessionOf(player);
            if (address == null) { state = GearRecipientState.Stale; return false; }
            // A bot belongs to the host: its backpack is built by the host and replicated, so only the host addresses it.
            if (!_players.IsLocal(address) && !IsBotOfThisHost(player)) { state = GearRecipientState.NotAddressedHere; return false; }
            if (!PlayerBackpackManager.TryGetBackpack(player, out var found) || found == null)
            { state = GearRecipientState.NoBackpack; return false; }
            backpack = found; session = address; state = GearRecipientState.Local;
            return true;
        }
        catch (Exception failure) { NativeCallFailure.Note("GearEquipPort.TryPlayer", failure); state = GearRecipientState.Stale; return false; }
    }

    private static GearSlot? SlotOf(InventorySlot slot) => slot switch
    {
        InventorySlot.GearStandard => GearSlot.Standard,
        InventorySlot.GearSpecial => GearSlot.Special,
        InventorySlot.GearMelee => GearSlot.Melee,
        InventorySlot.GearClass => GearSlot.Tool,
        _ => null
    };

    private static InventorySlot InventoryOf(GearSlot slot) => slot switch
    {
        GearSlot.Standard => InventorySlot.GearStandard,
        GearSlot.Special => InventorySlot.GearSpecial,
        GearSlot.Melee => InventorySlot.GearMelee,
        _ => InventorySlot.GearClass
    };
}
