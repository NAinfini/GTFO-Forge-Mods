using System;
using System.Collections.Generic;
using HarmonyLib;
using LevelGeneration;
using UnityEngine;

namespace ForgeOrdnance;

/// <summary>
/// Keeps the supplies in a drop pod shut away until its doors open, on every peer. The host spawns them just after touchdown
/// (<see cref="PodTimeline.StockDelay"/>), so they exist, in their bays, well before the doors move; until the doors start to
/// open they cannot be taken (the pickup interaction is switched off) and are not drawn, so nothing shows through or past the
/// closed doors. When the pod opens they are switched back on in the same frame the doors start to swing.
/// </summary>
internal static class SupplyHold
{
    private sealed class Held
    {
        internal GameObject Root = null!;
        internal ulong Thrower;
        internal uint ThrowId;
        internal Interact_Timed[] Interacts = Array.Empty<Interact_Timed>();
        internal Renderer[] Renderers = Array.Empty<Renderer>();
        internal Light[] Lights = Array.Empty<Light>();
    }

    private static readonly List<Held> All = new();

    /// <summary>Shuts a placed pickup away until the doors of pod (<paramref name="thrower"/>, <paramref name="throwId"/>) open (or
    /// the pod is gone). The interaction is held shut by ForgeCore's <see cref="ForgeCore.HoldPoint"/>; the pod's own events release it.</summary>
    internal static void Hold(GameObject root, ulong thrower, uint throwId)
    {
        foreach (var known in All)
            if (known.Root == root) return;
        var pod = Deliveries.FindPod(thrower, throwId);
        if (pod == null || pod.IsOpen) return;
        var held = new Held { Root = root, Thrower = thrower, ThrowId = throwId };
        var interacts = new List<Interact_Timed>();
        foreach (var interact in root.GetComponentsInChildren<Interact_Timed>(true)) interacts.Add(interact);
        held.Interacts = interacts.ToArray();
        var renderers = new List<Renderer>();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) if (renderer.enabled) renderers.Add(renderer);
        held.Renderers = renderers.ToArray();
        var lights = new List<Light>();
        foreach (var light in root.GetComponentsInChildren<Light>(true)) if (light.enabled) lights.Add(light);
        held.Lights = lights.ToArray();
        All.Add(held);
        foreach (var interact in held.Interacts) ForgeCore.HoldPoint.Disable(interact);
        ShutVisuals(held);
        pod.Opened += () => Release(held);
        pod.Left += () => Release(held);
    }

    /// <summary>True while a pickup is shut away, which is the only time <see cref="Tick"/> has work.</summary>
    internal static bool Any => All.Count != 0;

    /// <summary>The game can switch a renderer or a light on again (placing, culling) and has no event for it, so each frame a
    /// held pickup is found drawn it is switched off again. The interaction needs no such frame work.</summary>
    internal static void Tick()
    {
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var held = All[i];
            if (held.Root == null || held.Root.WasCollected) { All.RemoveAt(i); continue; }
            ShutVisuals(held);
        }
    }

    internal static void Clear() => All.Clear();

    private static void ShutVisuals(Held held)
    {
        foreach (var renderer in held.Renderers) if (renderer != null && !renderer.WasCollected && renderer.enabled) renderer.enabled = false;
        foreach (var light in held.Lights) if (light != null && !light.WasCollected && light.enabled) light.enabled = false;
    }

    private static void Release(Held held)
    {
        if (!All.Remove(held)) return;
        foreach (var renderer in held.Renderers) if (renderer != null && !renderer.WasCollected) renderer.enabled = true;
        foreach (var light in held.Lights) if (light != null && !light.WasCollected) light.enabled = true;
        foreach (var interact in held.Interacts)
            if (interact != null && !interact.WasCollected) ForgeCore.HoldPoint.Enable(interact);
    }
}
/// <summary>A pickup the game places in the level on a peer: when it lies in a bay of a pod whose doors are still shut, it is held.</summary>
internal static class SupplyHoldPatch
{
    internal static void Subscribe() => ForgeCore.GameHooks.PickupItemState.After((call, _) => Changed(call.Item, call.New, call.Recall));

    internal static void Changed(LG_PickupItem_Sync item, pPickupItemState newState, bool isRecall)
    {
        try
        {
            if (isRecall || newState.status != ePickupItemStatus.PlacedInLevel) return;
            if (!Deliveries.TryShutPodAt(newState.placement.position, out var thrower, out var throwId)) return;
            SupplyHold.Hold(item.gameObject, thrower, throwId);
        }
        catch (Exception error) { Plugin.Error("Holding a supply pickup until its pod opens failed: " + error); }
    }
}
