using System;
using System.Collections.Generic;
using ForgeWeaponEnergyLabExperimental.Presentation;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

/// <summary>
/// First-person energy weapons and the model each one shows. The donor keeps its own first-person settings;
/// aim zoom comes from the gear's sight part (EnergyGearJson) and the aim height from the model's SightLook socket.
/// </summary>
internal static class WeaponBindings
{
    internal sealed class Reticle
    {
        internal Transform Surface = null!;
        internal Vector3 RestPosition;
        internal float PlaneDepth;
        // Optic-local mark centre and half extent at rest, for the lens test.
        internal Vector2 Center, HalfSize;
    }

    internal sealed class Binding
    {
        internal ItemEquippable Weapon = null!;
        internal Transform Model = null!;
        internal Transform Sight = null!;
        internal WeaponPresentation Presentation = null!;
        // Reticles show only while aiming (resolved once, not per frame).
        internal Reticle[] Reticles = Array.Empty<Reticle>();
        internal float AimWeight;
        // Optic-local ellipse of the glass; a mark is drawn only inside it, as a real optic shows it only through the lens.
        internal Vector2 LensCenter, LensRadius;

        internal bool InLens(Reticle reticle, Vector2 offset)
        {
            var radius = LensRadius - reticle.HalfSize;
            if (radius.x <= 0f || radius.y <= 0f) return false;
            var position = reticle.Center + offset - LensCenter;
            return position.x * position.x / (radius.x * radius.x) + position.y * position.y / (radius.y * radius.y) <= 1f;
        }
    }
    private static readonly Dictionary<IntPtr, Binding> Entries = new();
    private static readonly List<IntPtr> Departed = new();

    // A mount owns this registration and rolls it back when binding fails.
    internal sealed class Snapshot
    {
        internal IntPtr WeaponPointer;
        internal Binding? Entry;
    }

    internal static Snapshot Capture(ItemEquippable weapon)
    {
        Entries.TryGetValue(weapon.Pointer, out var entry);
        return new Snapshot { WeaponPointer = weapon.Pointer, Entry = entry };
    }

    internal static void Restore(Snapshot snapshot)
    {
        if (snapshot.Entry != null) Entries[snapshot.WeaponPointer] = snapshot.Entry;
        else Entries.Remove(snapshot.WeaponPointer);
    }

    internal static bool TryGet(ItemEquippable? weapon, out Binding binding)
    {
        binding = null!;
        return weapon != null && weapon.IsFirstPerson && Entries.TryGetValue(weapon.Pointer, out binding!) &&
            binding.Weapon != null && binding.Model != null;
    }

    internal static void Register(ItemEquippable weapon, Snapshot snapshot)
    {
        if (weapon.Pointer != snapshot.WeaponPointer) throw new InvalidOperationException("Registration belongs to another weapon.");
        if (!weapon.IsFirstPerson || weapon.GearCategoryData == null || weapon.GearPartHolder == null) return;
        var mode = EnergyGears.ModeForCategory(weapon.GearCategoryData.persistentID);
        if (mode == EnergyMode.Off) return;
        var model = weapon.GearPartHolder.transform.Find(WeaponModels.ModelName);
        if (model == null || !WeaponModels.TryDescription(weapon.GearCategoryData.persistentID, out var presentation)) return;
        if (Entries.TryGetValue(weapon.Pointer, out var existing) && existing.Model == model && existing.Presentation == presentation) return;
        Prune();
        if (Entries.Count >= 128) throw new InvalidOperationException("Viewmodel instance budget exhausted.");
        var sight = ModelSockets.Require(model, "SightLook");
        var reticles = new List<Reticle>();
        Vector2 lensMin = new(float.PositiveInfinity, float.PositiveInfinity), lensMax = new(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var part in presentation.Parts)
        {
            if (part.Surface == "glass" && model.Find(part.Name) is { } glass)
                Extent(sight, glass, glass.localPosition, ref lensMin, ref lensMax);
            if (part.Surface == "reticle" && model.Find(part.Name) is { } surface)
            {
                var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
                // Rebinding a reused model must not treat last frame's parallax offset as its authored rest.
                var rest = new Vector3(part.Pivot.X, part.Pivot.Y, part.Pivot.Z);
                var center = model.TransformPoint(rest) + surface.TransformVector(mesh.bounds.center);
                Vector2 markMin = new(float.PositiveInfinity, float.PositiveInfinity), markMax = new(float.NegativeInfinity, float.NegativeInfinity);
                Extent(sight, surface, rest, ref markMin, ref markMax);
                reticles.Add(new Reticle { Surface = surface, RestPosition = rest,
                    PlaneDepth = sight.InverseTransformPoint(center).z, Center = (markMin + markMax) * .5f, HalfSize = (markMax - markMin) * .5f });
            }
        }
        if (reticles.Count != 0 && !float.IsFinite(lensMin.x))
            throw new InvalidOperationException("An optic with reticle marks needs a glass surface: " + presentation.Mode);
        Entries[weapon.Pointer] = new Binding { Weapon = weapon, Model = model, Sight = sight, Presentation = presentation,
            Reticles = reticles.ToArray(), LensCenter = (lensMin + lensMax) * .5f, LensRadius = (lensMax - lensMin) * .5f };
    }

    // Grows an optic-local XY box by a surface's mesh bounds, with the surface placed at the given local position.
    private static void Extent(Transform sight, Transform surface, Vector3 localPosition, ref Vector2 min, ref Vector2 max)
    {
        var bounds = surface.GetComponent<MeshFilter>().sharedMesh.bounds;
        var parent = surface.parent;
        for (var corner = 0; corner < 8; corner++)
        {
            var local = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
            var world = parent.TransformPoint(localPosition + surface.localRotation * Vector3.Scale(surface.localScale, local));
            var point = sight.InverseTransformPoint(world);
            min = Vector2.Min(min, new Vector2(point.x, point.y));
            max = Vector2.Max(max, new Vector2(point.x, point.y));
        }
    }

    internal static void Prune()
    {
        foreach (var pair in Entries) if (pair.Value.Weapon == null || pair.Value.Model == null) Departed.Add(pair.Key);
        foreach (var key in Departed) Entries.Remove(key);
        Departed.Clear();
    }

    internal static void Clear() { Entries.Clear(); Departed.Clear(); }
}
