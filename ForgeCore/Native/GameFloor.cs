using System.Collections.Generic;
using UnityEngine;

namespace ForgeCore;

/// <summary>Walks a floor's transform tree.</summary>
public static class GameFloor
{
    /// <summary>Every component under <paramref name="root"/>, depth first, with a null yielded before each node so a
    /// caller can spread the walk over frames.</summary>
    public static IEnumerable<Component?> Components(Transform root)
    {
        var pending = new Stack<(Transform Transform, int Child)>();
        pending.Push((root, -1));
        while (pending.Count > 0)
        {
            yield return null;
            var (node, child) = pending.Pop();
            if (node == null) continue;
            if (child == -1)
            {
                foreach (var component in node.gameObject.GetComponents<Component>()) yield return component;
                child = 0;
            }
            if (child >= node.childCount) continue;
            pending.Push((node, child + 1));
            pending.Push((node.GetChild(child), -1));
        }
    }
}
