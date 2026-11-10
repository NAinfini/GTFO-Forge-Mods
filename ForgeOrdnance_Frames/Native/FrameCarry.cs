using System;
using Player;
using UnityEngine;

namespace ForgeOrdnanceFrames;

/// <summary>The in-level carry object (a cell, any heavy item) a suit's pilot carries, drawn with the object's own game model on every
/// other peer that sees the pilot. Whether it is in the hands or stowed is the game's own state (the wielded slot, which replicates; the
/// special-ability key only asks the game to wield another slot), so every peer derives the same picture. Wielded, for every suit: the
/// game's third-person item (which the game keeps on the pilot's hidden body) is shown between the suit's hands. Stowed, a suit that
/// carries on its back (<c>CarryHeavy</c>) hangs the object on its chest bone behind the shell (<see cref="CarrySocket"/>), so it follows
/// crouching, running and the opening shell. The pilot's own screen is the game's. The object is only ever moved, never reparented,
/// copied or removed: <see cref="Release"/> puts it back exactly where the game keeps it, whatever ended the carrying (dropped,
/// delivered, the pilot climbing out, the suit wrecked, the pilot grabbed away, the level ending).</summary>
internal sealed class FrameCarry
{
    private ItemEquippable? _item;
    private bool _displaced, _woken;
    private Renderer[] _renderers = Array.Empty<Renderer>();
    private IntPtr _parent;
    private Vector3 _localPosition;
    private Quaternion _localRotation;
    // Stowed: the object's centre and turn in the chest bone's frame, taken when it went on the back.
    private bool _hung;
    private Vector3 _hungAt;
    private Quaternion _hungTurn;

    /// <summary>Every frame the suit is worn, after the game placed the pilot's items this frame. <paramref name="stows"/>: the suit
    /// hangs the object on its back when it is not in the hands.</summary>
    internal void Show(PlayerAgent pilot, Transform suit, float backZ, Transform? chest, Vector3 hands, bool stows)
    {
        var item = Carried(pilot, stows);
        if (item != null && item.WasCollected) item = null;
        if (_item != null && (item == null || item.Pointer != _item.Pointer)) Release();
        if (item == null) return;
        if (_item == null) Take(item);
        else if (_woken && pilot.Inventory!.WieldedSlot == InventorySlot.InLevelCarry) _woken = false;  // back in the hand: the game switches it on itself
        var wielded = pilot.Inventory!.WieldedSlot == InventorySlot.InLevelCarry;
        if (wielded) _hung = false;
        else if (float.IsNaN(backZ)) return;
        Place(suit, backZ, chest, wielded ? hands : null);
    }

    /// <summary>The object the pilot carries: the one in hand, else (a suit that stows it on its back) the one in the carry slot.</summary>
    private ItemEquippable? Carried(PlayerAgent pilot, bool stows)
    {
        var inventory = pilot.Inventory;
        if (inventory == null) return null;
        if (inventory.WieldedSlot == InventorySlot.InLevelCarry) return inventory.WieldedItem;
        if (!stows || pilot.Owner == null || !PlayerBackpackManager.TryGetBackpack(pilot.Owner, out var backpack) || backpack == null
            || !backpack.TryGetBackpackItem(InventorySlot.InLevelCarry, out var carried) || carried == null || !carried.IsLoaded) return null;
        var instance = carried.Instance;
        if (instance == null) return null;
        return _item != null && instance.Pointer == _item.Pointer ? _item : instance.TryCast<ItemEquippable>();
    }

    /// <summary>True for the renderers of an object shown on the suit, which hiding the pilot's model must leave alone.</summary>
    internal bool Shows(Renderer renderer)
    {
        if (_item == null) return false;
        for (var i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null && _renderers[i].Pointer == renderer.Pointer) return true;
        return false;
    }

    /// <summary>The object leaves the suit (nothing happens when there is none): back to where the game holds it.</summary>
    internal void Release()
    {
        var item = _item;
        var displaced = _displaced;
        if (_woken && item != null && !item.WasCollected) item.gameObject.SetActive(false);
        _woken = false;
        _item = null;
        _hung = false;
        _displaced = false;
        _renderers = Array.Empty<Renderer>();
        if (item == null || !displaced || item.WasCollected || (item.transform.parent?.Pointer ?? IntPtr.Zero) != _parent) return;
        item.transform.localPosition = _localPosition;
        item.transform.localRotation = _localRotation;
    }

    private void Take(ItemEquippable item)
    {
        _item = item;
        _parent = item.transform.parent?.Pointer ?? IntPtr.Zero;
        _localPosition = item.transform.localPosition;
        _localRotation = item.transform.localRotation;
        // Away from the hand the game keeps the object switched off; it is shown for the time it hangs on the back.
        if (!item.gameObject.activeSelf)
        {
            item.gameObject.SetActive(true);
            _woken = true;
        }
        _renderers = item.GetComponentsInChildren<Renderer>(true);
    }

    /// <summary>A remote pilot's object: the game's third-person item, between the suit's hands while wielded, else hung behind the shell
    /// on the chest bone, turned with it.</summary>
    private void Place(Transform suit, float backZ, Transform? chest, Vector3? hands)
    {
        var t = _item!.transform;
        for (var i = 0; i < _renderers.Length; i++)
        {
            var renderer = _renderers[i];
            if (renderer == null || renderer.WasCollected) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
        }
        var bone = hands == null && chest != null && _hung;
        t.rotation = bone ? chest!.rotation * _hungTurn : suit.rotation;
        var centre = Vector3.zero;
        var depth = 0f;
        if (Extent(out var bounds))
        {
            centre = bounds.center - t.position;
            var forward = suit.forward;
            depth = Mathf.Abs(forward.x) * bounds.size.x + Mathf.Abs(forward.y) * bounds.size.y + Mathf.Abs(forward.z) * bounds.size.z;
        }
        Vector3 at;
        if (hands is { } held) at = held;
        else if (bone) at = chest!.TransformPoint(_hungAt);
        else
        {
            // Taken once, when it went on the back: where behind the shell the object sits, kept in the chest bone's frame.
            var chestY = chest != null ? suit.InverseTransformPoint(chest.position).y : 1.6f;
            var (x, y, z) = CarrySocket.Centre(backZ, chestY, depth);
            at = suit.TransformPoint(new Vector3(x, y, z));
            if (chest != null)
            {
                _hungAt = chest.InverseTransformPoint(at);
                _hungTurn = Quaternion.Inverse(chest.rotation) * suit.rotation;
                _hung = true;
            }
        }
        t.position = at - centre;
        _displaced = true;
    }

    private bool Extent(out Bounds bounds) => ForgeVisual.Models.WorldBounds(_renderers, out bounds) && bounds.size.sqrMagnitude > 1e-6f;
}
