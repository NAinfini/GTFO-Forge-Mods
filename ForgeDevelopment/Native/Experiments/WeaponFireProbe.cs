using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Gear;
using Player;
using UnityEngine;

namespace ForgeDevelopment.Native;

/// <summary>Development-only readbacks for a gun fired the way a player fires it. The trigger itself is the game's own Fire input
/// (<c>GameInputDrive.Hold Fire</c>, answered through HeldInputLease); nothing here fires. This class selects a gun (the game's own
/// offline-gear equip and wield-slot entries), counts what the game then does (<c>BulletWeapon.Fire</c>, <c>BulletHit</c>, a melee
/// hit, through ForgeCore's shared hooks), and reads the clip and a target's health before and after:
/// <c>drive_cli.py call static:ForgeDevelopment.Native.WeaponFireProbe Report</c>.
/// Order per gun: Wield, Fill, Ready, Arm, one <c>drive_cli.py step</c> command holding Hold Fire / wait / Let Fire / LetAll, Report. The Hold and
/// the Let must be steps of the same command: the lease ends with the command that took it, so a Hold in one call and a Let in the next never
/// reaches the game. <see cref="FireDirect"/> calls <c>BulletWeapon.Fire</c> itself and is the control run when the input run shows nothing.</summary>
internal static class WeaponFireProbe
{
    private static readonly List<IDisposable> Subscriptions = new();
    private static int _fires, _shotgunFires, _hits, _melee, _clipBefore = -1;

    private static PlayerAgent Local => ForgeCore.Players.Local ?? throw new InvalidOperationException("no local player: enter a level first");

    private static string Describe(Item? item)
    {
        if (item == null || item.WasCollected) return "none";
        var gun = item.TryCast<BulletWeapon>();
        var name = gun?.GearCategoryData?.name ?? item.ItemDataBlock?.publicName ?? item.name;
        return gun == null ? name + " (" + item.GetType().Name + ")"
            : name + " (" + gun.GetType().Name + ") clip " + gun.GetCurrentClip() + "/" + gun.GetMaxClip();
    }

    /// <summary>The offline gear blocks whose name starts with <paramref name="prefix"/>: what <see cref="Equip"/> takes.</summary>
    internal static string Gears(string prefix)
    {
        var names = GameData.PlayerOfflineGearDataBlock.GetAllBlocks().ToArray()
            .Where(block => block.name.StartsWith(prefix, StringComparison.Ordinal)).Select(block => block.name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        return names.Length + " gears: " + string.Join(",", names);
    }

    internal static string Slots()
    {
        var player = Local;
        if (!PlayerBackpackManager.TryGetBackpack(player.Owner, out var pack) || pack?.Slots == null) return "no backpack";
        var rows = new StringBuilder("wielded " + Describe(player.Inventory?.WieldedItem));
        for (var i = 0; i < pack.Slots.Length; i++)
            if (pack.Slots[i] is { } item) rows.Append("; slot ").Append(i).Append(' ').Append((InventorySlot)i).Append(' ').Append(Describe(item.Instance));
        return rows.ToString();
    }

    /// <summary>Equips an offline gear block by name through ForgeCore (the game's own local equip).</summary>
    internal static string Equip(string name)
    {
        var block = GameData.PlayerOfflineGearDataBlock.GetBlock(name) ?? throw new ArgumentException("no offline gear " + name);
        var reading = ForgeCore.Gear.EquipLocal(new GearIDRange(block.GearJSON));
        return "equip " + name + ": " + reading.Status;
    }

    /// <summary>The game's own wield request for a backpack slot (<c>GearStandard</c>, <c>GearSpecial</c>, <c>GearMelee</c>).</summary>
    internal static string Wield(string slot)
    {
        if (!Enum.TryParse<InventorySlot>(slot, false, out var parsed)) throw new ArgumentException("unknown slot " + slot);
        Local.Sync.WantsToWieldSlot(parsed, false);
        return "wield requested " + parsed;
    }

    /// <summary>The readback of a <see cref="Wield"/>: the slot the game wields now against the one asked for. Something that holds the
    /// player's hands (an exosuit's gun, a carried item) can keep the request from taking; this says so instead of waiting.</summary>
    internal static string WieldTaken(string slot)
    {
        if (!Enum.TryParse<InventorySlot>(slot, false, out var parsed)) throw new ArgumentException("unknown slot " + slot);
        var inventory = Local.Inventory;
        var now = inventory?.WieldedSlot;
        return "taken=" + (now == parsed).ToString().ToLowerInvariant() + "; requested=" + parsed + "; wieldedSlot=" + now + "; wielded=" + Describe(inventory?.WieldedItem);
    }

    private static BulletWeapon Wielded() => Local.Inventory?.WieldedItem?.TryCast<BulletWeapon>() ?? throw new InvalidOperationException("wielded item is not a bullet weapon");

    /// <summary>Fills the wielded gun's magazine and its slot's reserve through the game's own ammo write (<c>Gear.WriteLocalAmmo</c>).</summary>
    internal static string Fill()
    {
        var gun = Wielded();
        PlayerBackpackManager.TryGetBackpack(Local.Owner, out var pack);
        var type = AmmoType.Standard;
        for (var i = 0; pack?.Slots != null && i < pack.Slots.Length; i++)
            if (pack.Slots[i]?.Instance is { } item && item.Pointer == gun.Pointer) type = (InventorySlot)i == InventorySlot.GearSpecial ? AmmoType.Special : AmmoType.Standard;
        var ok = ForgeCore.Gear.WriteLocalAmmo(type, gun, 500f, gun.GetMaxClip());
        return "filled " + type + " reserve 500 clip " + gun.GetMaxClip() + ": " + (ok ? "read back" : "NOT read back") + "; " + Describe(gun);
    }

    /// <summary>Everything that has to hold before a fire input can shoot: focus, the game's focus state, the wielded gun, its clip, the
    /// gun's own fire-button flags (the game's reading of the input) and the lease's query frames.</summary>
    internal static string Ready()
    {
        var player = Local;
        var gun = player.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        return "Application.isFocused " + Application.isFocused + "; FocusState " + FocusStateManager.CurrentState + "; " + Describe(player.Inventory?.WieldedItem)
            + (gun == null ? "" : "; FireButton " + gun.FireButton + " FireButtonPressed " + gun.FireButtonPressed) + "; lease " + System.Text.Json.JsonSerializer.Serialize(HeldInputLease.Describe());
    }

    /// <summary>One call of the wielded gun's native <c>BulletWeapon.Fire</c> per count. It covers the shot, ammo and hit resolution from that entry down;
    /// it does NOT cover the input layer or the archetype's own fire gating, so a pass here with a failed input run puts the fault above Fire.</summary>
    internal static string FireDirect(string count)
    {
        var gun = Wielded();
        for (var i = int.Parse(count); i > 0; i--) gun.Fire();
        return "called BulletWeapon.Fire x" + count + "; " + Describe(gun);
    }

    /// <summary>Starts counting. Run it after the gun is wielded and before the Fire input is held.</summary>
    internal static string Arm()
    {
        foreach (var subscription in Subscriptions) subscription.Dispose();
        Subscriptions.Clear();
        _fires = _shotgunFires = _hits = _melee = 0;
        Subscriptions.Add(ForgeCore.GameHooks.Fire.BulletWeapon.After((_, ran) => { if (ran) _fires++; }));
        Subscriptions.Add(ForgeCore.GameHooks.Fire.Shotgun.After((_, ran) => { if (ran) _shotgunFires++; }));
        Subscriptions.Add(ForgeCore.GameHooks.BulletHit.Before(_ => _hits++));
        Subscriptions.Add(ForgeCore.GameHooks.MeleeAttackDamage.Before(_ => _melee++));
        var held = Local.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        _clipBefore = held?.GetCurrentClip() ?? -1;
        return "armed; wielded " + Describe(Local.Inventory?.WieldedItem);
    }

    internal static string Report()
    {
        var held = Local.Inventory?.WieldedItem;
        var clip = held?.TryCast<BulletWeapon>()?.GetCurrentClip() ?? -1;
        var text = "wielded " + Describe(held) + "; clip before " + _clipBefore + " after " + clip + "; BulletWeapon.Fire " + _fires
            + " Shotgun.Fire " + _shotgunFires + " BulletHit " + _hits + " melee hits " + _melee;
        return text;
    }

    /// <summary>Lends an offline gear (by persistent id, the id an Arsenal heavy weapon payload names) into the special slot through ForgeCore's
    /// <c>LoanedGun</c>, the entry every borrowed gun takes. This is the plain game gun of that id: Arsenal's payload numbers (damage, grenade) are
    /// applied by its rack interaction and are not part of this loan.</summary>
    internal static string Loan(string gearId)
    {
        var block = GameData.PlayerOfflineGearDataBlock.GetBlock(uint.Parse(gearId)) ?? throw new ArgumentException("no offline gear " + gearId);
        ForgeCore.LoanedGun.Equip(new GearIDRange(block.GearJSON));
        return "loaned " + block.name + " (" + gearId + ")";
    }
    /// <summary>What ForgeVisual's first-person arms read from the local player's third-person model: each arm part, its skinned meshes,
    /// and whether the mesh sits under the body animator (the three things <c>FirstPersonArms</c> needs before it can copy the arms).</summary>
    internal static string ArmsInfo()
    {
        var agent = Local;
        var model = agent.PlayerSyncModel;
        if (model == null) return "PlayerSyncModel null";
        var animator = agent.AnimatorBody;
        var text = new StringBuilder("animator " + (animator == null || animator.WasCollected ? "none" : animator.name) + "; Ready " + ForgeVisual.Models.FirstPersonArmsReady(agent));
        var parts = model.m_gfxArms;
        text.Append("; m_gfxArms ").Append(parts == null ? "null" : parts.Length.ToString());
        if (parts != null)
            foreach (var part in parts)
            {
                if (part == null) { text.Append("; part null"); continue; }
                text.Append("; part ").Append(part.name);
                foreach (var skin in part.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var under = false;
                    if (animator != null) for (var up = skin.transform.parent; up != null; up = up.parent) if (up.Pointer == animator.transform.Pointer) { under = true; break; }
                    text.Append(" [smr ").Append(skin.name).Append(" mesh ").Append(skin.sharedMesh != null).Append(" underAnimator ").Append(under).Append(']');
                }
            }
        return text.ToString();
    }

    /// <summary>The public bind ForgeEmote calls, run once: returns what it gave back (the failure reason is in the log).</summary>
    internal static string BindArms() => ForgeVisual.Models.BindFirstPersonArms(Local) is { } arms ? "bound; alive " + arms.Alive : "bind returned null";
}
