using System;
using System.Collections.Generic;
using LevelGeneration;
using UnityEngine;

namespace ForgeReplay;

/// <summary>The level's objects of one kind, the ones the game has switched off included: this build's interop exposes no
/// inactive-including scene search, but the built objects are children of the floor and <c>GetComponentsInChildren(true)</c> reaches a
/// terminal or a pickup in a closed container. Objects made while the level runs (an item the recording spawned) are not under the
/// floor, so the active scene's own answer is added; what the floor holds is the same object and is kept once.</summary>
internal static class LevelFind
{
    internal static T[] All<T>() where T : Component
    {
        var active = UnityEngine.Object.FindObjectsOfType<T>();
        var builder = LG_LevelBuilder.Current;
        var floor = builder == null || builder.WasCollected ? null : builder.m_currentFloor;
        if (floor == null || floor.WasCollected) return active;
        var built = floor.transform.GetComponentsInChildren<T>(true);
        if (built.Length == 0) return active;
        var all = new List<T>(built.Length + active.Length);
        var seen = new HashSet<IntPtr>(built.Length + active.Length);
        foreach (var item in built) if (item != null && !item.WasCollected && seen.Add(item.Pointer)) all.Add(item);
        foreach (var item in active) if (item != null && !item.WasCollected && seen.Add(item.Pointer)) all.Add(item);
        return all.ToArray();
    }
}
