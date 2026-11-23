using System.Collections.Generic;
using UnityEngine;

namespace ForgeWeapon.Native;

internal sealed partial class GearPartTransformApplier
{
    // Immutable configuration gate. The spawn hook checks it before touching a native object.
    internal bool HasItemAppearance => _snapshot.HasItems;

    /// <summary>Called once per SpawnItem, which always returns a new item, so an item is never posed twice.</summary>
    internal void ApplyItem(Item? item, uint itemId)
    {
        if (!_snapshot.HasItems || !_snapshot.TryItem(itemId, out var pose) || item == null) return;
        // SpawnItem selected this ItemDataBlock by persistentID. With doSetup=false the instance's
        // ItemDataBlock property is still unset; its requested native identity is already authoritative.
        var root = item.transform;
        var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
        var branches = new List<Transform>();
        // Work only on complete, purely visual subtrees. Never move colliders, lights, grips or scripts.
        foreach (var renderer in renderers)
        {
            var visual = renderer.gameObject;
            if (visual == null)
            { Once("item-missing-visual:" + itemId, "weapon.item-appearance-refused: item=" + itemId + " visual object is missing."); return; }
            if (GameplayMarker(visual.name)) continue;
            var node = renderer.transform;
            if (node == root || !VisualOnly(node))
            { Once("item-unsafe:" + itemId, "weapon.item-appearance-refused: item=" + itemId + " mesh shares a gameplay subtree."); return; }
            bool nested = false;
            foreach (var other in renderers)
                if (other != renderer && node.IsChildOf(other.transform)) { nested = true; break; }
            if (!nested) branches.Add(node);
        }
        if (branches.Count == 0)
        { Once("item-empty:" + itemId, "weapon.item-appearance-refused: item=" + itemId + " instance has no static visual mesh."); return; }

        var offset = Matrix4x4.TRS(pose.Position ?? Vector3.zero, Quaternion.Euler(pose.EulerAngles ?? Vector3.zero), pose.Scale ?? Vector3.one);
        var worldOffset = root.localToWorldMatrix * offset * root.worldToLocalMatrix;
        // Resolve all matrices before writing: nested mesh branches and parent frames must stay original.
        var matrices = new Matrix4x4[branches.Count];
        for (int i = 0; i < branches.Count; i++)
        {
            var node = branches[i];
            var parent = node.parent;
            if (parent == null)
            { Once("item-parent:" + itemId, "weapon.item-appearance-refused: item=" + itemId + " visual parent is missing."); return; }
            matrices[i] = parent.worldToLocalMatrix * worldOffset * node.localToWorldMatrix;
        }
        for (int i = 0; i < branches.Count; i++)
        {
            var matrix = matrices[i];
            branches[i].localPosition = matrix.GetColumn(3);
            branches[i].localRotation = matrix.rotation;
            branches[i].localScale = matrix.lossyScale;
        }
        if (pose.Enabled is bool enabled)
            foreach (var renderer in renderers)
                if (!GameplayMarker(renderer.gameObject!.name)) renderer.enabled = enabled;
    }

    private static bool VisualOnly(Transform node)
    {
        foreach (var component in node.GetComponentsInChildren<Component>(true))
            if (component.gameObject is not { } visual || GameplayMarker(visual.name)
                || component.TryCast<Transform>() == null && component.TryCast<MeshFilter>() == null
                && component.TryCast<MeshRenderer>() == null && component.TryCast<LODGroup>() == null) return false;
        return true;
    }

    // These static hand placeholders occur in the vanilla tripmine models (including pickup and LOD1).
    // They are absent from the Low item preview and do not belong to the authored item body.
    private static bool GameplayMarker(string name) => name is "LeftHand001" or "RightHand001";
}

// dump.cs:565768, RVA 0x12A2000. SpawnItem assembles the mode's prefabs and runs setup before returning; all clients use this entry.
// ForgeCore patches it once (GameHooks.ItemSpawned); this subscription runs after the body and every other postfix.
internal static class ItemAppearanceSpawned
{
    // Content entries arrive on Runtime's first tick, after the active weapon session is up.
    // Subscribed for the process lifetime; the handler stays inert until an accepted snapshot has Item poses.
    internal static void Subscribe() => ForgeCore.GameHooks.ItemSpawned.After(static (call, _) => Spawned(call.Item, call.Id));

    private static void Spawned(Item? item, uint persistentID)
    {
        var session = WeaponNativeSession.Offer(WeaponFeature.Appearance);
        if (session?.GearParts?.HasItemAppearance != true) return;
        session.Guard(static (current, state) => current.GearParts.ApplyItem(state.Item, state.Id),
            (Item: item, Id: persistentID));
    }
}
