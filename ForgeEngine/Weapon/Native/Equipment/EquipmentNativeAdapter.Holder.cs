using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeEngine.Framework;
using ForgeEngine.GameBindings;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

internal sealed partial class EquipmentNativeAdapter
{
    // Block lookup and binding snapshots change only at load/native readback, never at dispatch.
    private readonly Dictionary<uint, HolderMount> _holderMounts = new();

    private sealed class HolderMount
    {
        internal HolderBinding?[] Bindings = Array.Empty<HolderBinding?>();
        internal EquipmentHolderMatch? Match(EquipmentHolderSubject player)
        {
            var index = player.PlayerNumber;
            if (index <= 0 || index >= Bindings.Length) return null;
            var binding = Bindings[(int)index];
            return binding != null && binding.Match.Holder.WorldEpoch == player.Player.WorldEpoch
                && binding.Match.Holder.LifeEpoch == player.Player.LifeEpoch && binding.Held() ? binding.Match : null;
        }
    }

    internal sealed class HolderBinding
    {
        internal readonly uint Block;
        internal readonly int PlayerNumber;
        internal readonly Handle Handle;
        internal readonly EquipmentHolderMatch Match;
        private readonly EquipmentNativeAdapter adapter;
        private readonly IntPtr playerPointer, inventoryPointer;
        private readonly uint resource;
        private readonly bool gear;
        private readonly IForgeEngine kernel;

        internal HolderBinding(EquipmentNativeAdapter adapter, IForgeEngine kernel, uint block, int playerNumber, Handle handle, BackpackItem item, SNet_Player player,
            PlayerInventoryBase? inventory, Func<bool> holderCurrent)
        {
            this.adapter = adapter; this.kernel = kernel; Block = block; PlayerNumber = playerNumber; Handle = handle;
            playerPointer = player.Pointer; inventoryPointer = inventory?.Pointer ?? IntPtr.Zero;
            gear = item.GearIDRange != null;
            resource = gear ? item.GearIDRange!.GetChecksum() : item.ItemID;
            Match = new EquipmentHolderMatch(handle.Entity, handle.Owner, Current, holderCurrent);
        }

        private bool Current()
        {
            if (!Handle.Life.Active || Handle.Entity.WorldEpoch != kernel.WorldEpoch
                || !adapter.TryCurrentBackpack(Handle, out var backpack)) return false;
            var slots = backpack.Slots;
            if (slots == null || Handle.SlotIndex >= slots.Length) return false;
            var item = slots[Handle.SlotIndex];
            return item is not null && !item.WasCollected && item.Pointer == Handle.ItemPointer && item.Instance is { WasCollected: false }
                && item.Instance.Pointer == Handle.InstancePointer && (gear
                    ? item.GearIDRange != null && item.GearIDRange.GetChecksum() == resource
                    : item.GearIDRange == null && item.ItemID == resource);
        }

        internal bool Held()
        {
            if (!Current() || Handle.Backpack.IsDeployed((InventorySlot)Handle.SlotIndex)) return false;
            var owner = Handle.Backpack.Owner;
            var currentInventory = Inventory(owner);
            return owner is not null && !owner.WasCollected && owner.Pointer == playerPointer
                && currentInventory is { WasCollected: false } && currentInventory.Pointer == inventoryPointer && currentInventory.Owner != null
                && currentInventory.Owner.Owner != null && currentInventory.Owner.Owner.Pointer == playerPointer
                && Wielded(currentInventory, Handle.Backpack.Slots![Handle.SlotIndex]!);
        }
    }

    internal EquipmentHolderMatcher ResolveHolderMount(string reference)
    {
        if (!IsPlainDecimalId(reference)) throw new RuntimeContractException(RuntimeAbiCodes.AttachmentPort, reference);
        var block = uint.Parse(reference, NumberStyles.None, CultureInfo.InvariantCulture);
        if (!_holderMounts.TryGetValue(block, out var mount))
        {
            mount = new HolderMount();
            _holderMounts.Add(block, mount);
            // Loading after observation binds the already recorded native lives, with no inventory search.
            foreach (var handle in _byInstance.Values)
            {
                var item = handle.Backpack.Slots?[handle.SlotIndex];
                if (item != null) TrackHolder(handle, item, Inventory(handle.Backpack.Owner));
            }
        }
        return mount.Match;
    }

    private void TrackHolder(Handle handle, BackpackItem item, PlayerInventoryBase? inventory)
    {
        var text = MountBlock(item, handle.SlotIndex);
        if (text == null || !uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var block)
            || !_holderMounts.TryGetValue(block, out var mount)) return;
        var binding = handle.Life.Holder;
        if (binding == null || binding.Block != block || binding.Match.Holder != handle.Owner)
        {
            UntrackHolder(handle);
            var player = handle.Backpack.Owner;
            if (player == null) return;
            if (ForgePlayer.Native.PlayerHost.Session?.Module is not { } players)
                throw new RuntimeContractException(PlayerDirectoryService.MissingCode, "The Player domain is not running.");
            var holderCurrent = players.BindCurrent(handle.Owner);
            var number = new EquipmentHolderSubject(handle.Owner).PlayerNumber;
            if (holderCurrent == null || number <= 0) throw new RuntimeContractException("stale-entity", handle.Owner.Id);
            if (number >= int.MaxValue) throw new RuntimeContractException("equipment.active-budget", handle.Owner.Id);
            var index = (int)number;
            binding = new HolderBinding(this, _kernel, block, index, handle, item, player, inventory, holderCurrent);
            handle.Life.Holder = binding;
            if (index >= mount.Bindings.Length) Array.Resize(ref mount.Bindings, index + 1);
        }
        // A player has one wielded item. Other slots never overwrite its currently held equipment.
        if (binding.Held()) mount.Bindings[binding.PlayerNumber] = binding;
        else if (ReferenceEquals(mount.Bindings[binding.PlayerNumber], binding)) mount.Bindings[binding.PlayerNumber] = null;
    }

    private void UntrackHolder(Handle handle)
    {
        if (handle.Life.Holder is not { } binding) return;
        if (_holderMounts.TryGetValue(binding.Block, out var mount) && binding.PlayerNumber < mount.Bindings.Length
            && ReferenceEquals(mount.Bindings[binding.PlayerNumber], binding)) mount.Bindings[binding.PlayerNumber] = null;
        handle.Life.Holder = null;
    }

}
