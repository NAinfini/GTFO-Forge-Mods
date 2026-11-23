using LevelGeneration;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace ForgeMap.Native;

/// <summary>
/// The hit path's half of this provider's instance identity. A bullet is resolved against a `Collider`, and the
/// collider a door or a terminal is hit through is not the map object itself: the object's own components are
/// the door's blades, its frame, its button, the terminal's screen. The address a map object is named by is read
/// from the object's own members, so a hit object has to reach the map object above it before anything can be
/// addressed at all.
///
/// This is the same entry the address-resolving instance lookup already answers on — the kind's one owner is
/// asked with the native object a caller holds, and this provider is the owner — so the climb lives here and
/// nowhere else. Nothing is inferred from the hit: a collider with no door and no terminal above it, world
/// geometry, an enemy limb, a deployable, answers null and the kind stays unresolved rather than being given an
/// address its own members do not read. The two categories are searched in one fixed order, so an object that
/// could be read as either is answered the same way every time instead of by which reader ran first.
/// </summary>
internal static class MapObjectHit
{
    /// <summary>The map object a hit object belongs to, or null when it belongs to none of this provider's
    /// categories. The instance is the category's own native type, so the caller classifies and addresses it
    /// exactly as it would an instance handed over directly.</summary>
    internal static object? Instance(object hitObject)
    {
        if (hitObject is not Component component || component.WasCollected) return null;
        if (PlacedModelHitRegistry.Resolve(component) is { } model) return model;
        var door = component.GetComponentInParent(Il2CppType.Of<LG_SecurityDoor>())?.TryCast<LG_SecurityDoor>();
        if (door != null && !door.WasCollected) return door;
        var weak = component.GetComponentInParent(Il2CppType.Of<LG_WeakDoor>())?.TryCast<LG_WeakDoor>();
        if (weak != null && !weak.WasCollected) return weak;
        var terminal = component.GetComponentInParent(Il2CppType.Of<LG_ComputerTerminal>())?.TryCast<LG_ComputerTerminal>();
        return terminal != null && !terminal.WasCollected ? terminal : null;
    }
}
