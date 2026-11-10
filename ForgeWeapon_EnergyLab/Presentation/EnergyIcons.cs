using ForgeUI;
using Gear;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>
/// Weapon icons are drawn by the game's own icon renderer; ForgeUI's <see cref="GearIcons"/> takes over the job of every energy gun and
/// waits for the model, and this supplies that model: the gun's own model (the bundle's prefab with its vanilla-gear-shader materials),
/// loaded in the background by <see cref="WeaponModels"/>. A model that cannot be had completes the job with an empty picture and one
/// logged error, never the donor gun.
/// </summary>
internal static class EnergyIcons
{
    private static readonly GearIconModel Model = IconModel;

    /// <summary>Every energy gun's category takes its icon job from ForgeUI; called once the energy guns are fixed.</summary>
    internal static void Supply()
    {
        foreach (var gear in EnergyGears.All) GearIcons.Supply(gear.CategoryId, Model);
    }

    private static GameObject? IconModel(GearIDRange range, out string? failure)
    {
        failure = null;
        if (EnergyGears.ForCategory(range.GetCompID(eGearComponent.Category)) is not { } gear)
        {
            failure = "the gear is not an energy gun";
            return null;
        }
        if (PresentationPreload.Failed)
        {
            failure = "the energy presentation of " + gear.Definition.Id + " failed to preload";
            return null;
        }
        var model = WeaponModels.IconModel(gear, out var failed);
        if (failed) failure = "the model of " + gear.Definition.Id + " failed to load";
        return model;
    }
}
