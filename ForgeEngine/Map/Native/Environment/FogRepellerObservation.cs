using System;
using System.Collections.Generic;
using ForgeEngine.Framework;

namespace ForgeMap.Native;

/// <summary>The placed pickup's native global state owns its sphere. The saved key is the
/// fixed placement's author token; moving it or recreating Unity ids does not change that key.</summary>
internal static class FogRepellerObservation
{
    private static readonly Dictionary<string, HeavyFogRepellerGlobalState> Placed = new(StringComparer.Ordinal);
    private static long _world = long.MinValue;
    internal static void Clear() { Placed.Clear(); _world = long.MinValue; }

    internal static void Register(long world, string itemKey, HeavyFogRepellerGlobalState state)
    {
        if (world != _world) { _world = world; Placed.Clear(); }
        if (LevelEndpointAddress.RepellerOfItem(itemKey) is { } key && state != null && !state.WasCollected)
            Placed.Add(key, state);
    }

    internal static HeavyFogRepellerGlobalState? ResolveState(string key)
    {
        if (MapHost.Session is { } session && session.WorldEpoch != _world) return null;
        return Placed.TryGetValue(key, out var state) && state != null && !state.WasCollected ? state : null;
    }

    internal static bool IsCurrent(EntityReference reference)
        => reference.WorldEpoch == _world && reference.LifeEpoch == 1
            && (MapHost.Session is not { } session || session.WorldEpoch == _world);

    internal static FogRepeller_Sphere? Resolve(string key)
        => ResolveState(key)?.m_repellerSphere is { } sphere && !sphere.WasCollected ? sphere : null;

    internal static EntityReference? Reference(string address, long world)
        => world == _world && LevelEndpointAddress.Parse(address) is { Category: LevelEndpointAddress.FogRepeller } parsed
            && Resolve(address) != null ? LevelEndpointAddress.Reference(parsed, world) : null;
}
