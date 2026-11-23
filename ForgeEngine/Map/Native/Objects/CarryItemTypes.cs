using System;
using System.Collections.Generic;
using ForgeCore;
using ForgeEngine.Framework;
using Gear;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ForgeMap.Native;

// dump.cs:565768, the one entry every client and every mode (pickup, first person, third person) builds an Item through.
// SpawnItem instantiates the base's functional shell and attaches the vanilla look; the postfix replaces only that look
// when the Item id is a custom mission item of the current level. The shell, interaction, sync, terminal registration and
// slot behaviour stay the vanilla carry body's. ForgeCore patches the entry once (GameHooks.ItemSpawned); this subscription
// changes no argument and no result.
internal static class CarryItemSpawned
{
    internal static void Spawned(ForgeCore.ItemSpawn call)
    {
        if (call.Item is not { } item || !CarryItemAssembly.AnyActive) return;
        try { CarryItemAppearance.Apply(item, call.Id); }
        catch (Exception error) { MapLog.Contained("carry-item.appearance", error); }
    }
}

/// <summary>Replaces the look of one spawned carry body: the vanilla renderers are hidden, the model assembly is placed below a
/// body node that fits it into the vanilla rendered bounds, the author's pose is applied on top, and the vanilla interaction
/// and ping boxes grow to cover the model. Colliders, lights, grip anchors and scripts are never removed or moved, except the
/// hand anchors the author gave. A refusal leaves the vanilla look untouched and is written to the log.</summary>
internal static class CarryItemAppearance
{
    internal const string BodyName = "ForgeCarryItemBody";
    private const string FitName = "Fit";
    private const double ColliderMargin = 0.04;

    internal static void Apply(Item item, uint itemId)
    {
        var assembly = CarryItemAssembly.Find(itemId, LevelIdentity.Read());
        if (assembly == null) return;
        var root = item.transform;
        if (root.Find(BodyName) != null) return;

        var vanilla = new List<Renderer>();
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.TryCast<MeshRenderer>() == null && renderer.TryCast<SkinnedMeshRenderer>() == null) continue;
            if (renderer.GetComponent<TMP_Text>() != null) continue; // the serial number panel is part of the carry body
            vanilla.Add(renderer);
        }
        if (RootBounds(vanilla, root) is not { } target)
        { Refuse(assembly, itemId, "carry-item-base-without-body"); return; }

        var body = new GameObject(BodyName);
        body.transform.SetParent(root, false);
        var fit = new GameObject(FitName);
        fit.transform.SetParent(body.transform, false);
        int layer = vanilla[0].gameObject.layer;
        body.layer = layer; fit.layer = layer;
        var refusal = assembly.Place(fit.transform, itemId + ":" + root.gameObject.GetInstanceID());
        if (refusal != null)
        {
            UnityEngine.Object.Destroy(body);
            Refuse(assembly, itemId, refusal);
            return;
        }

        var model = new List<Renderer>();
        foreach (var renderer in fit.GetComponentsInChildren<Renderer>(true)) if (renderer != null) model.Add(renderer);
        if (assembly.Item.AutoFit)
        {
            if (RootBounds(model, root) is not { } natural
                || !CarryItemFitting.TryAutoFit(target, natural, out var scale, out var x, out var y, out var z))
            {
                UnityEngine.Object.Destroy(body);
                Refuse(assembly, itemId, "carry-item-model-without-extent");
                return;
            }
            body.transform.localScale = new Vector3((float)scale, (float)scale, (float)scale);
            body.transform.localPosition = new Vector3((float)x, (float)y, (float)z);
        }
        var pose = assembly.Item.Fit;
        fit.transform.localPosition = new Vector3((float)pose.Position[0], (float)pose.Position[1], (float)pose.Position[2]);
        fit.transform.localRotation = new Quaternion((float)pose.Rotation[0], (float)pose.Rotation[1], (float)pose.Rotation[2], (float)pose.Rotation[3]);
        fit.transform.localScale = new Vector3((float)pose.Scale[0], (float)pose.Scale[1], (float)pose.Scale[2]);

        ForgeVisual.Models.HideRenderers(root, renderer => !vanilla.Exists(own => own == renderer), new List<Renderer>());
        if (RootBounds(model, root) is { } final)
        {
            GrowBoxes(root, body.transform, final);
            ApplyGrip(root, body.transform, assembly.Item.Grip);
        }
    }

    private static void Refuse(CarryItemAssembly assembly, uint itemId, string reason)
        => MapLog.Record(RuntimeLogLevel.Error, MapLogCodes.NativeReport,
            "carry-item-appearance-refused: item=" + itemId + " id=" + assembly.Item.Id + " reason=" + reason + "; the vanilla look is kept.");

    /// <summary>The renderers' mesh bounds as one box in the root's local frame. Mesh bounds, not renderer bounds, so an item that is
    /// still inactive while it is being built reads the same as an active one.</summary>
    private static FitBox? RootBounds(IReadOnlyList<Renderer> renderers, Transform root)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (var renderer in renderers)
        {
            var mesh = renderer.TryCast<SkinnedMeshRenderer>() is { } skinned ? skinned.sharedMesh
                : renderer.GetComponent<MeshFilter>() is { } filter ? filter.sharedMesh : null;
            if (mesh == null) continue;
            var bounds = mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var local = bounds.center + new Vector3((corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x,
                    (corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y, (corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z);
                var point = root.InverseTransformPoint(renderer.transform.TransformPoint(local));
                minX = Math.Min(minX, point.x); minY = Math.Min(minY, point.y); minZ = Math.Min(minZ, point.z);
                maxX = Math.Max(maxX, point.x); maxY = Math.Max(maxY, point.y); maxZ = Math.Max(maxZ, point.z);
            }
        }
        var box = new FitBox(minX, minY, minZ, maxX, maxY, maxZ);
        return box.IsFinite ? box : null;
    }

    /// <summary>Every box collider outside the model (the interaction trigger, the ping target, the shadow box) grows to cover the model.</summary>
    private static void GrowBoxes(Transform root, Transform body, FitBox model)
    {
        foreach (var collider in root.GetComponentsInChildren<BoxCollider>(true))
        {
            if (collider == null || collider.transform.IsChildOf(body)) continue;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                var world = root.TransformPoint(new Vector3((float)((corner & 1) == 0 ? model.MinX : model.MaxX),
                    (float)((corner & 2) == 0 ? model.MinY : model.MaxY), (float)((corner & 4) == 0 ? model.MinZ : model.MaxZ)));
                var local = collider.transform.InverseTransformPoint(world);
                minX = Math.Min(minX, local.x); minY = Math.Min(minY, local.y); minZ = Math.Min(minZ, local.z);
                maxX = Math.Max(maxX, local.x); maxY = Math.Max(maxY, local.y); maxZ = Math.Max(maxZ, local.z);
            }
            var center = collider.center; var size = collider.size;
            var existing = new FitBox(center.x - size.x * .5, center.y - size.y * .5, center.z - size.z * .5,
                center.x + size.x * .5, center.y + size.y * .5, center.z + size.z * .5);
            var grown = CarryItemFitting.Enlarge(existing, new FitBox(minX, minY, minZ, maxX, maxY, maxZ), ColliderMargin);
            collider.center = new Vector3((float)grown.CenterX, (float)grown.CenterY, (float)grown.CenterZ);
            collider.size = new Vector3((float)grown.SizeX, (float)grown.SizeY, (float)grown.SizeZ);
        }
    }

    private static void ApplyGrip(Transform root, Transform body, CarryItemGrip? grip)
    {
        if (grip == null) return;
        foreach (var node in root.GetComponentsInChildren<Transform>(true))
        {
            if (node == null || node.IsChildOf(body)) continue;
            var anchor = node.name == "LeftHand" ? grip.Left : node.name == "RightHand" ? grip.Right : null;
            if (anchor != null) node.position = root.TransformPoint(new Vector3((float)anchor[0], (float)anchor[1], (float)anchor[2]));
        }
    }
}

/// <summary>What a custom mission item forbids while it is in hand. `CarryItemEquippableFirstPerson` only exists on the local player's
/// own machine and drives its insert and drop holds itself (`m_interactInsertItem`, `m_interactDropItem`), so the insert check is its own
/// field and the world interactions are the three static switches of `PlayerInteraction`; dropping the item never goes through either.
/// An <see cref="ForgeCore.InputLease"/> holds the switches off, so what else changed one meanwhile is not undone when the item leaves the hand.</summary>
internal static class CarryItemRestrictions
{
    private static InputLease? _lease;

    internal static void Wielded(CarryItemEquippableFirstPerson shell)
    {
        if (!CarryItemAssembly.AnyActive || shell.ItemDataBlock is not { } block) return;
        var item = CarryItemAssembly.Find(block.persistentID, LevelIdentity.Read())?.Item;
        if (item == null) return;
        if (item.NoInsert) shell.m_insertCheckType = eCarryItemInsertTargetType.None;
        if (!item.NoInteract || _lease is { IsHeld: true }) return;
        _lease = InputLease.Take(MapHost.ModId);
        _lease.BlockInteractions(InteractionSwitch.CameraRay, InteractionSwitch.SphereCheck, InteractionSwitch.Ladder);
    }

    /// <summary>The item left the hand (put away, dropped, inserted, the player went down) or the level ended.</summary>
    internal static void Released()
    {
        _lease?.Dispose();
        _lease = null;
    }
}

[HarmonyPatch(typeof(CarryItemEquippableFirstPerson), nameof(CarryItemEquippableFirstPerson.OnWield))]
internal static class CarryItemWielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(CarryItemEquippableFirstPerson __instance)
    {
        try { CarryItemRestrictions.Wielded(__instance); }
        catch (Exception error) { MapLog.Contained("carry-item.wield", error); }
    }
}

[HarmonyPatch(typeof(CarryItemEquippableFirstPerson), nameof(CarryItemEquippableFirstPerson.OnUnWield))]
internal static class CarryItemUnwielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        try { CarryItemRestrictions.Released(); }
        catch (Exception error) { MapLog.Contained("carry-item.unwield", error); }
    }
}
