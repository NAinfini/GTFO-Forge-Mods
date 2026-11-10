using System;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeOrdnanceArsenal;

/// <summary>What the standing devices besides the dispenser (<see cref="Dispenser"/>) do, all through the game's own entries. Scanner:
/// <c>ToolSyncManager.WantToTagEnemy</c> (the game's own tag, which every player sees). Floodlight: a light from ForgeVisual's pool on every
/// peer, and the game's own fog repeller item (<c>ItemMode.Instance</c>, item 117) stood by the host, its sphere sized on every peer.</summary>
internal static class DeployableActions
{
    private const uint FogRepellerItem = 117;

    // ---- host: the work -------------------------------------------------------------------------------------------

    internal static void HostWork(Deployable u, float now)
    {
        if (!u.Started)
        {
            u.Started = true;
            u.Rng = new System.Random(u.Seed);
            u.NextAt = now;
            if (u.Spec.Kind == DeployableKind.Floodlight) StandFog(u);
        }
        switch (u.Spec.Kind)
        {
            case DeployableKind.Dispenser: Dispenser.HostWork(u, now); break;
            case DeployableKind.Scanner: Scan(u, now); break;
        }
    }

    internal static void HostEnd(Deployable u)
    {
        Dispenser.HostEnd(u);
        DespawnFog(u);
    }

    /// <summary>Host: takes the fog repeller the floodlight stood back out of the level, unless the game already collected it. A host that took over
    /// never stood it, so it takes the instance this peer matched to the floodlight (<see cref="ApplyFog"/>) by that instance's own replicator.</summary>
    internal static void DespawnFog(Deployable u)
    {
        if (!ForgeCore.Session.IsHost) return;
        IReplicator? target = null;
        var replicator = u.FogReplicator;
        u.FogReplicator = null;
        if (replicator != null) { if (!replicator.WasCollected) target = replicator.Cast<IReplicator>(); }
        else if (u.FogItem is { WasCollected: false } fog && fog.ReplicationWrapper is { WasCollected: false } wrapper) target = wrapper.Replicator;
        u.FogItem = null;
        if (target != null) ForgeCore.Items.Despawn(target);
    }

    internal static PlayerAgent Owner(Deployable u)
        => u.Source != null && !u.Source.WasCollected ? u.Source : ForgeCore.Players.Local ?? throw new InvalidOperationException("No player agent owns the munition.");

    private static void Scan(Deployable u, float now)
    {
        if (now < u.NextAt) return;
        u.NextAt = now + u.Spec.Scan;
        foreach (var enemy in ForgeCore.Enemies.Near(u.Position, u.Spec.Range))
            if (enemy != null && !enemy.IsTagged && ForgeCore.Enemies.Alert(enemy) is ForgeCore.EnemyAlert.Awake or ForgeCore.EnemyAlert.Hunting) ToolSyncManager.WantToTagEnemy(enemy);
    }

    private static void StandFog(Deployable u)
    {
        if (u.Node == null) throw new InvalidOperationException("The landing course node is gone.");
        var replicator = ForgeCore.Items.Spawn(FogRepellerItem, InventorySlot.Consumable, new pItemData_Custom { ammo = 1f }, ItemMode.Instance,
            u.Position + Vector3.up * 0.5f, Quaternion.identity, u.Node, out var refusal);
        if (replicator == null) Plugin.Logger.ErrorOnce("The floodlight could not stand the game's fog repeller (" + refusal + "); it lights but does not clear fog.");
        u.FogReplicator = replicator;
    }

    // ---- every peer: the floodlight's fog -------------------------------------------------------------------------

    /// <summary>Fog repellers that spawned here before any floodlight claimed them (a late joiner gets the item before the device record);
    /// <see cref="ApplyFog"/> matches them once the device exists.</summary>
    private static readonly System.Collections.Generic.List<FogRepellerInstance> Unclaimed = new();

    /// <summary>The fog repeller the host stood has spawned here: finds the floodlight it belongs to by place.</summary>
    internal static void OnItemSpawned(pItemSpawnData data, ItemReplicator replicator)
    {
        var fog = replicator?.Item?.TryCast<FogRepellerInstance>();
        if (fog == null) return;
        if (!Claim(fog)) Unclaimed.Add(fog);
    }

    private static bool Claim(FogRepellerInstance fog)
    {
        var at = fog.transform.position;
        foreach (var u in Deployables.All)
            if (u.Spec.Kind == DeployableKind.Floodlight && u.FogItem == null && (u.Position - at).sqrMagnitude < 4f) { u.FogItem = fog; return true; }
        return false;
    }

    /// <summary>Sizes the sphere of the floodlight's repeller (its own, on this peer) once it exists: the reach and the time left.</summary>
    internal static void ApplyFog(Deployable u, float now)
    {
        if (u.FogItem == null && Unclaimed.Count > 0)
        {
            Unclaimed.RemoveAll(fog => fog == null || fog.WasCollected);
            for (var i = 0; i < Unclaimed.Count; i++)
                if ((u.Position - Unclaimed[i].transform.position).sqrMagnitude < 4f) { u.FogItem = Unclaimed[i]; Unclaimed.RemoveAt(i); break; }
        }
        var fog = u.FogItem;
        if (fog == null || u.FogApplied) return;
        if (fog.WasCollected) { u.FogItem = null; return; }
        var sphere = fog.m_repellerSphere;
        if (sphere == null) return;
        sphere.Range = u.Spec.FogRadius;
        sphere.LifeDuration = Mathf.Max(5f, u.EndsAt - now);
        u.FogApplied = true;
    }

    internal static void Clear()
    {
        Unclaimed.Clear();
        Dispenser.Clear();
    }
}
