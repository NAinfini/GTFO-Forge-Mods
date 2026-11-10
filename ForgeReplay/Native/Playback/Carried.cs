using ForgeCore;
using System;
using System.Collections.Generic;
using System.Linq;
using LevelGeneration;
using Player;
using UnityEngine;

namespace ForgeReplay;

/// <summary>A big item a player carries in the hands (a cell, a turbine, a neonate): the recording has the pickup's state with its holder
/// (27 ItemState's holder slot) and the holder's wielded slot. The game hides the level's item when it is picked up and the carried copy lives
/// in the holder's backpack, which a replay does not touch, so the level's own item is shown at the puppet's right hand for as long as the recording has it
/// held in the hands. Display only: its state, interaction and objective are the setter's and the game's, and its colliders are off while it is carried. What the
/// level has no object for in the hands (a resource pack, a consumable, a carried item whose pickup was not matched) is a copy of the item in the recorded kit
/// (PlayerKit), one per player and item, shown for as long as the recorded wield has it. This is for the bodies only: in the eyes view the hands are the observer's
/// own first-person arms, which hold the item through the observer's backpack like its gear (<see cref="FirstPerson"/>). Main thread only.</summary>
internal static class Carried
{
    private const ulong PickedUp = (ulong)ePickupItemStatus.PickedUp;

    private sealed class Shown
    {
        internal LG_PickupItem_Sync Item = null!;
        internal GameObject Root = null!;
        internal readonly List<Collider> Off = new();
        internal bool Seen;
    }

    private static readonly Dictionary<ulong, Shown> Held = new();
    private static readonly List<ulong> Gone = new();
    private static readonly bool[] LevelItemHeld = new bool[4];
    private static readonly Dictionary<(int Slot, uint Item), Shown> Copies = new();
    private static readonly Dictionary<uint, bool> NoCopy = new();
    private static readonly Shown?[] CopyShown = new Shown?[4];
    // The gear a puppet or body copy still has in its hands behind a pack or a carried item (the game keeps the last gear up on a body that
    // cannot hold the pack), and the renderers this hid. Hidden gear comes back only when its own item is the one in the hands again, so a
    // switch does not put a stowed item on show beside the new one.
    private static readonly List<Renderer>[] HiddenGear = { new(), new(), new(), new() };
    private static readonly Dictionary<int, Renderer[]> GearRenderers = new();
    private static readonly bool[] GearBlocked = new bool[4];
    private static readonly InventorySlot[] GearSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearMelee, InventorySlot.GearClass };
    private static readonly Dictionary<(int Slot, uint Item), ItemEquippable> ThirdPerson = new();
    // The (player, item) pairs the game would not make or hold in third person: those two only, so a puppet that was not ready for it does not cost every player the item.
    private static readonly HashSet<(int Slot, uint Item)> NoThirdPerson = new();
    private static readonly uint[] HoldingNow = new uint[4];

    /// <summary>Per frame, from the state alone (a jump needs nothing else).</summary>
    internal static void Frame(WorldState state, int follow, bool eyes)
    {
        foreach (var shown in Held.Values) shown.Seen = false;
        Array.Clear(LevelItemHeld);
        if (EventPresenter.Setter is { } setter)
            foreach (var ((kind, key), payload) in state.Objects)
            {
                if (kind != EventKind.ItemState) continue;
                var read = PayloadReader.Over(payload);
                read.U();
                if (read.U() != PickedUp) continue;
                read.U(); read.Pos();
                var holder = read.U();
                if (holder == 0) continue;
                var slot = (int)(holder - 1) & 3;
                if (!InHands(state, slot) || setter.Known(key) is not { } item) continue;
                if (eyes && slot == (follow & 3) || !Place(slot, out var at, out var rotation)) continue;
                if (!Held.TryGetValue(key, out var shown))
                {
                    if (RootOf(item) is not { } root) continue;
                    Held[key] = shown = new Shown { Item = item, Root = root };
                }
                shown.Seen = true;
                LevelItemHeld[slot] = true;
                Show(shown, at, rotation);
                HideGear(slot);
            }
        Gone.Clear();
        foreach (var (key, shown) in Held) if (!shown.Seen) { Put(shown); Gone.Add(key); }
        foreach (var key in Gone) Held.Remove(key);
        for (var slot = 0; slot < 4; slot++) ShowCopy(state, slot, follow, eyes);
    }

    // The item the recorded wield has in the hands with no level object to show for it.
    private static void ShowCopy(WorldState state, int slot, int follow, bool eyes)
    {
        var wanted = WieldedItem(state, slot);
        var held = HoldVanilla(slot, wanted);
        Shown? copy = null;
        // The game's own third-person item covers the bodies; the eyes view holds its item in the observer's own hands.
        if (wanted != 0 && !held && !(eyes && slot == (follow & 3)) && Place(slot, out var at, out var rotation))
        {
            if (!Copies.TryGetValue((slot, wanted), out copy) && !NoCopy.ContainsKey(wanted) && Made(wanted, at, rotation) is { } made) Copies[(slot, wanted)] = copy = made;
            if (copy != null) Show(copy, at, rotation);
        }
        // A pack in the hands, the game's own third-person one or a copy, leaves no gear there: the last gear the game still holds is hidden.
        // Once nothing is in the hands, the gear comes back where the game put it (the current item only: a stowed one stays hidden).
        if (LevelItemHeld[slot] || wanted != 0 && (held || copy != null)) HideGear(slot);
        else { GearBlocked[slot] = false; RestoreGear(slot); }
        if (CopyShown[slot] is { } was && was != copy) was.Root.SetActive(false);
        CopyShown[slot] = copy;
    }

    // The third-person form of the item on the puppet's own hands, as the game holds it for a remote player. False when there is no puppet or the item has no such form.
    private static bool HoldVanilla(int slot, uint wanted)
    {
        var puppet = Puppeteer.PlayerOf(slot);
        if (puppet == null) return false;
        if (wanted == 0 || NoThirdPerson.Contains((slot, wanted)))
        {
            if (HoldingNow[slot] != 0)
            {
                var before = HoldingNow[slot];
                puppet.HoldItem(null);
                // The game's switch back to the last gear and this item's own hiding can be a frame apart: a pack switched away from must not
                // stay beside the item that replaces it.
                if (ThirdPerson.TryGetValue((slot, before), out var old) && old != null && !old.WasCollected) old.gameObject.SetActive(false);
                HoldingNow[slot] = 0;
            }
            return false;
        }
        if (HoldingNow[slot] == wanted) return true;
        // A switch from one pack to another: the old one is switched off here, since the game's equip entry may show the new one before it
        // hides the old (and a consumable brought up as a gun is not this path's item at all).
        if (HoldingNow[slot] != 0 && ThirdPerson.TryGetValue((slot, HoldingNow[slot]), out var previous) && previous != null && !previous.WasCollected)
            previous.gameObject.SetActive(false);
        if (!ThirdPerson.TryGetValue((slot, wanted), out var item))
        {
            // A puppet just made or moved has no course node yet: tried again next frame, not given up on.
            if (puppet.Agent.CourseNode == null) return false;
            item = MadeThirdPerson(wanted, puppet.Agent);
            if (item == null) { NoThirdPerson.Add((slot, wanted)); return false; }
            ThirdPerson[(slot, wanted)] = item;
        }
        // An item this held before was switched off; the game's equip entry does not switch an item it already counts as wielded on again.
        if (item.gameObject != null && !item.gameObject.activeSelf) item.gameObject.SetActive(true);
        if (!puppet.HoldItem(item)) { NoThirdPerson.Add((slot, wanted)); return false; }
        HoldingNow[slot] = wanted;
        return true;
    }

    private static ItemEquippable? MadeThirdPerson(uint itemId, PlayerAgent agent)
    {
        var block = GameData.ItemDataBlock.GetBlock(itemId);
        if (block == null) return null;
        return ForgeCore.Items.Spawn(itemId, block.inventorySlot, null, ItemMode.ThirdPerson, agent.Position, agent.Rotation, agent.CourseNode, out _)?.Item?.TryCast<ItemEquippable>();
    }

    private static uint WieldedItem(WorldState state, int slot)
    {
        if (state.PlayerStatus[slot] is not { } status) return 0;
        var read = PayloadReader.Over(status);
        read.U(); read.U();
        var wield = (InventorySlot)read.U();
        if (wield is not (InventorySlot.ResourcePack or InventorySlot.Consumable or InventorySlot.InLevelCarry)) return 0;
        if (wield == InventorySlot.InLevelCarry && LevelItemHeld[slot]) return 0;
        ref readonly var has = ref state.PlayerKits[slot];
        return wield == InventorySlot.ResourcePack ? has.PackItem : wield == InventorySlot.Consumable ? has.ConsumableItem : has.CarryItem;
    }

    private static Shown? Made(uint itemId, Vector3 at, Quaternion rotation)
    {
        var block = GameData.ItemDataBlock.GetBlock(itemId);
        if (block == null || !Dimension.TryGetCourseNodeFromPos(at, out var node) || node == null) return null;
        var replicator = ForgeCore.Items.Spawn(itemId, block.inventorySlot, null, ItemMode.Pickup, at, rotation, node, out var refusal);
        var item = replicator?.Item;
        if (item == null)
        {
            if (refusal == ItemSpawnRefusal.ModeMissing) NoCopy[itemId] = true;
            Skipped.Note("an item in a player's hands could not be shown", "item " + itemId + ": " + refusal);
            return null;
        }
        var pickup = item.GetComponentInChildren<LG_PickupItem_Sync>();
        if (pickup != null) EventPresenter.Setter?.NotRecorded(pickup);
        return new Shown { Item = pickup!, Root = item.gameObject };
    }

    /// <summary>What each player's hands show, for ReplayControl.Inspect.</summary>
    internal static string Inspect() => string.Join("; ", new[] { 0, 1, 2, 3 }.Select(s => s + ": third=" + HoldingNow[s] + " copy=" + (CopyShown[s]?.Root.name ?? "-"))) + " level items=" + Held.Count;

    /// <summary>The replay ends: every carried item goes back to the state the game has it in.</summary>
    internal static void Clear()
    {
        foreach (var shown in Held.Values) Put(shown);
        Held.Clear();
        Array.Clear(CopyShown);
        for (var slot = 0; slot < 4; slot++)
        {
            GearBlocked[slot] = false;
            foreach (var renderer in HiddenGear[slot]) if (renderer != null && !renderer.WasCollected) renderer.forceRenderingOff = false;
            HiddenGear[slot].Clear();
        }
        GearRenderers.Clear();
        Copies.Clear(); NoCopy.Clear(); ThirdPerson.Clear(); NoThirdPerson.Clear(); Array.Clear(HoldingNow);
    }

    // The carried item's own object, the one the game hides on a pickup (CarryItemPickup_Core); null for any other pickup.
    private static GameObject? RootOf(LG_PickupItem_Sync item)
    {
        var core = item.GetComponent<CarryItemPickup_Core>();
        if (core == null) { var up = item.GetComponentsInParent<CarryItemPickup_Core>(true); if (up.Length > 0) core = up[0]; }
        return core != null ? core.gameObject : null;
    }

    private static bool InHands(WorldState state, int slot)
    {
        var status = state.PlayerStatus[slot];
        if (status == null) return false;
        var read = PayloadReader.Over(status);
        read.U(); read.U();
        return (InventorySlot)read.U() == InventorySlot.InLevelCarry;
    }

    // At the body's right hand, turned with its body.
    private static bool Place(int slot, out Vector3 at, out Quaternion rotation)
    {
        at = default; rotation = Vec.Identity;
        var body = Puppeteer.AnimatorOf(slot);
        var hand = body != null ? body.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (body == null || hand == null) return false;
        at = hand.position;
        rotation = Quaternion.Euler(0f, body.transform.eulerAngles.y, 0f);
        return true;
    }

    // The bot's hands still hold its last gear (it has no carried item to bring up), and the fourth player's copy holds the last gear it
    // was given: that gear is hidden for as long as the item is held. The gear behind a pack is the one the recording last had up, not the
    // item the game's asynchronous wield happens to name this frame, so both are hidden.
    private static void HideGear(int slot)
    {
        GearBlocked[slot] = true;
        var puppet = Puppeteer.PlayerOf(slot);
        if (puppet != null)
        {
            var inventory = puppet.Agent.Inventory;
            if (inventory != null && inventory.WieldedSlot is InventorySlot.GearStandard or InventorySlot.GearSpecial or InventorySlot.GearMelee or InventorySlot.GearClass
                && inventory.WieldedItem is { } held)
                Hide(held.gameObject, slot);
            // Every gear line the body still carries goes with the pack: the game keeps the last one in the hands, and the rest are placed on
            // the body by the same equip pass, so after a switch one of them could stay beside the pack.
            if (puppet.Agent.Owner is { } owner && PlayerBackpackManager.TryGetBackpack(owner, out var backpack) && backpack != null)
                foreach (var gearSlot in GearSlots)
                    if (backpack.TryGetBackpackItem(gearSlot, out var item) && item?.Instance != null)
                        Hide(item.Instance.gameObject, slot);
        }
        if (Puppeteer.CosmeticOf(slot)?.HeldWeapon is { } copy) Hide(copy, slot);
    }

    private static void Hide(GameObject root, int slot)
    {
        var id = root.GetInstanceID();
        if (!GearRenderers.TryGetValue(id, out var renderers)) { renderers = root.GetComponentsInChildren<Renderer>(false); GearRenderers[id] = renderers; }
        foreach (var renderer in renderers)
            if (renderer != null && !renderer.WasCollected && !renderer.forceRenderingOff) { renderer.forceRenderingOff = true; HiddenGear[slot].Add(renderer); }
    }

    // The gear is back only when the item that owns the renderer is the one in the hands: giving every hidden renderer back on a switch put a
    // fog repeller or a gun the game had already stowed beside the item that replaced it. Cheap: a handful of renderers, no allocation.
    private static void RestoreGear(int slot)
    {
        var hidden = HiddenGear[slot];
        if (hidden.Count == 0 || GearBlocked[slot]) return;
        var current = Puppeteer.PlayerOf(slot)?.Agent.Inventory?.WieldedItem;
        var copy = Puppeteer.CosmeticOf(slot)?.HeldWeapon;
        for (var i = hidden.Count - 1; i >= 0; i--)
        {
            var renderer = hidden[i];
            if (renderer == null || renderer.WasCollected) { hidden.RemoveAt(i); continue; }
            var root = renderer.transform;
            if (current == null || !root.IsChildOf(current.transform)) { if (copy == null || !root.IsChildOf(copy.transform)) continue; }
            renderer.forceRenderingOff = false;
            hidden.RemoveAt(i);
        }
    }

    private static void Show(Shown shown, Vector3 at, Quaternion rotation)
    {
        try
        {
            var item = shown.Root;
            if (!item.activeSelf) item.SetActive(true);
            if (shown.Off.Count == 0)
                foreach (var collider in item.GetComponentsInChildren<Collider>(false))
                    if (collider != null && collider.enabled) { collider.enabled = false; shown.Off.Add(collider); }
            item.transform.SetPositionAndRotation(at, rotation);
        }
        catch (Exception error) { Skipped.Note("a carried item could not be shown in the hands", error.Message); }
    }

    // Back to how the game has it: put down (the setter placed it), or still picked up and only out of the hands (hidden, as the game hides it).
    private static void Put(Shown shown)
    {
        try
        {
            if (shown.Item == null || shown.Item.WasCollected) return;
            foreach (var collider in shown.Off) if (collider != null && !collider.WasCollected) collider.enabled = true;
            shown.Off.Clear();
            if (shown.Item.GetCurrentState().status == ePickupItemStatus.PickedUp && shown.Root != null) shown.Root.SetActive(false);
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("A carried item could not be put back: " + error.Message); }
    }
}
