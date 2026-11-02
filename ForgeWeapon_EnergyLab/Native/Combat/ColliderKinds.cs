using System.Collections.Generic;
using Enemies;
using Player;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Native;

/// <summary>What a collider belongs to. Projectiles ask this for every collider of every sweep, so the answer is kept per collider
/// (a collider never changes owner) instead of walking its parents each time.</summary>
internal enum ColliderKind : byte { None, Agent, Enemy, Player }

internal static class ColliderKinds
{
    private const int Capacity = 4096;
    private static readonly Dictionary<int, ColliderKind> Known = new();

    static ColliderKinds() => ForgeCore.GameEvents.LevelEnded += Known.Clear;

    internal static ColliderKind Of(Collider collider)
    {
        var id = collider.GetInstanceID();
        if (Known.TryGetValue(id, out var kind)) return kind;
        kind = Classify(collider);
        if (Known.Count >= Capacity) Known.Clear();
        Known[id] = kind;
        return kind;
    }

    /// <summary>An enemy or a player (its body, its limbs).</summary>
    internal static bool IsActor(Collider collider) => Of(collider) is ColliderKind.Enemy or ColliderKind.Player;

    private static ColliderKind Classify(Collider collider)
    {
        var agent = collider.GetComponentInParent<IDamageable>()?.GetBaseAgent();
        if (agent?.TryCast<PlayerAgent>() != null) return ColliderKind.Player;
        if (agent?.TryCast<EnemyAgent>() != null) return ColliderKind.Enemy;
        if (collider.GetComponentInParent<PlayerAgent>() != null) return ColliderKind.Player;
        return agent != null ? ColliderKind.Agent : ColliderKind.None;
    }
}