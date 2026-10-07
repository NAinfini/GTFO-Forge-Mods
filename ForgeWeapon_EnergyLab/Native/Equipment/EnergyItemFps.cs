using System;
using System.Collections.Generic;
using GameData;
using Gear;
using UnityEngine;

namespace ForgeWeaponEnergyLabExperimental.Native;

/// <summary>
/// The game gives an assembled gun its base item's ItemFPSSettings block. That block is shared with vanilla guns,
/// so each energy category gets its own settings and recoil animation with a wider hip item-camera FOV.
/// Aim FOVs and firing animations are copied unchanged.
/// </summary>
internal static class EnergyItemFps
{
    internal const int HipItemFov = 45;
    /// <summary>The energy guns carry more bulk behind the grip than their donors, so the hip and relaxed holds sit
    /// further forward and lower than the donor's (vanilla holds range z 0.31–0.70, y -0.06 to -0.50). Aim stays native.</summary>
    internal static readonly UnityEngine.Vector3 HoldOffset = new(0.02f, -0.05f, 0.14f);

    private sealed class Entry
    {
        internal ItemFPSSettingsDataBlock Donor = null!;
        internal ItemFPSSettingsDataBlock Own = null!;
    }
    private static readonly Dictionary<uint, Entry> ByCategory = new();

    internal static void Apply(ItemEquippable? item, GearIDRange? gear)
    {
        if (item == null || gear == null) return;
        var category = gear.GetCompID(eGearComponent.Category);
        if (EnergyGears.ForCategory(category) is not { } definition) return;
        var donor = item.m_itemFPSSettingsForGear ?? item.m_itemFPSSettingsForItem
            ?? throw new InvalidOperationException($"Energy category={category} has no native first-person settings.");
        ByCategory.TryGetValue(category, out var entry);
        if (entry == null || donor.Pointer != entry.Donor.Pointer && donor.Pointer != entry.Own.Pointer)
        {
            var source = GameDataBlockBase<ItemMovementAnimationDataBlock>.GetBlock(donor.RecoilAnimation)
                ?? throw new InvalidOperationException($"Missing native recoil animation {donor.RecoilAnimation}: {definition.Name}.");
            var recoil = EnergyGearRegistry.RegisterRecoilAnimation(CloneRecoilAnimation(source, definition));
            var own = donor.MemberwiseClone().Cast<ItemFPSSettingsDataBlock>();
            own.RecoilAnimation = recoil.persistentID;
            own.ItemCameraFOVDefault = HipItemFov;
            own.localPosHip = donor.localPosHip + HoldOffset;
            own.localPosRelaxed = donor.localPosRelaxed + HoldOffset;
            ByCategory[category] = entry = new Entry { Donor = donor, Own = own };
        }
        // The native setter resolves RecoilAnimation again; changing the settings ID alone leaves the cached block stale.
        item.SetItemFPSSettingsForGear(entry.Own);
        if (item.RecoilAnimation == null || item.RecoilAnimation.Pointer !=
            GameDataBlockBase<ItemMovementAnimationDataBlock>.GetBlock(category)?.Pointer)
            throw new InvalidOperationException("Energy first-person recoil animation was not applied: " + definition.Name);
    }

    internal static ItemMovementAnimationDataBlock CloneRecoilAnimation(ItemMovementAnimationDataBlock source, EnergyGear gear)
    {
        var recoil = source.MemberwiseClone().Cast<ItemMovementAnimationDataBlock>();
        recoil.name = gear.NativeName; recoil.persistentID = gear.CategoryId; recoil.internalEnabled = true;
        var scale = gear.Mode is EnergyMode.Beam or EnergyMode.Flame ? 0f : 1f / 3f;
        recoil.ItemPosition = CloneCurves(source.ItemPosition, scale);
        recoil.ItemRotation = CloneCurves(source.ItemRotation, scale);
        recoil.CameraPosition = CloneCurves(source.CameraPosition, scale);
        recoil.CameraRotation = CloneCurves(source.CameraRotation, scale);
        return recoil;
    }

    private static Vector3AnimationCurve CloneCurves(Vector3AnimationCurve source, float scale)
    {
        var curves = source.MemberwiseClone().Cast<Vector3AnimationCurve>();
        curves.X = CloneCurve(source.X, scale);
        curves.Y = CloneCurve(source.Y, scale);
        curves.Z = CloneCurve(source.Z, scale);
        // Evaluate multiplies each axis by its variance range. Keep that multiplier so strength is scaled only once.
        return curves;
    }

    private static AnimationCurve CloneCurve(AnimationCurve source, float scale)
    {
        var keys = source.keys;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            key.value = scale == 0f ? 0f : key.value * scale;
            // Step keys can have infinite tangents; infinity * zero would introduce NaN.
            key.inTangent = scale == 0f ? 0f : key.inTangent * scale;
            key.outTangent = scale == 0f ? 0f : key.outTangent * scale;
            keys[i] = key;
        }
        return new AnimationCurve(keys) { preWrapMode = source.preWrapMode, postWrapMode = source.postWrapMode };
    }
}
