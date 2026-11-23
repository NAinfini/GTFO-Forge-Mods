using System;
using System.Collections.Generic;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>Two placement rules every fixed object may carry: which items its receptacle takes, and whether its own look is
/// drawn. Which items a receptacle takes is an admission rule like a door's key, so it is data on the placed object, not a
/// behavior; what an insertion then does stays in behaviors, on the game's own item facts. A refused insertion leaves the
/// item in the player's hands, the same as the game refusing any other insertion. Hiding the look keeps the object's
/// interaction, collision and prompt; the author places their own model beside it.</summary>
internal static class FixedItemRules
{
    private static readonly Dictionary<IntPtr, IReadOnlyList<uint>> Accepts = new();

    internal static void Clear() => Accepts.Clear();

    internal static void Apply(Transform root, FixedItem row)
    {
        if (row.AcceptItems != null) Accepts[root.Pointer] = row.AcceptItems;
        if (row.HideBody) HideBody(root, null);
    }

    /// <summary>Stops drawing every renderer under <paramref name="root"/> that <paramref name="keep"/> does not name, and
    /// disables it as well. The game's level culling gathers only enabled renderers of active objects
    /// (<c>C_CullBucket.GetAllCullableChildren</c>), and every time it shows a gathered renderer it writes <c>enabled = true</c>
    /// and <c>forceRenderingOff = false</c> (<c>RendererCullingExtensions.CullShow</c>, build 20403457). A body hidden before the
    /// native culling collection is therefore left out of it and stays hidden; the draw switch alone would be cleared by the
    /// first CullShow.</summary>
    internal static void HideBody(Transform root, Func<Renderer, bool>? keep)
    {
        var hidden = new List<Renderer>();
        ForgeVisual.Models.HideRenderers(root, keep, hidden);
        foreach (var renderer in hidden) renderer.enabled = false;
    }

    /// <summary>Whether a rule on the receptacle's placed object refuses this item. Anything unreadable is left to the game.</summary>
    internal static bool Refuses(iCarryItemInteractionTarget? target, pItemData itemData)
    {
        if (Accepts.Count == 0) return false;
        IReadOnlyList<uint>? accepts = null;
        for (var at = target?.TryCast<Component>()?.transform; at != null && accepts == null; at = at.parent)
            Accepts.TryGetValue(at.Pointer, out accepts);
        if (accepts == null) return false;
        if (!PlayerBackpackManager.TryGetItemInLevelFromItemData(itemData, out var item) || item?.ItemDataBlock is not { } block) return false;
        foreach (var id in accepts) if (id == block.persistentID) return false;
        return true;
    }
}
