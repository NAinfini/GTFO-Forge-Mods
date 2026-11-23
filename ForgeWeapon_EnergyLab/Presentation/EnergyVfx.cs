using System;
using System.Collections;
using System.IO;
using ForgeVisual;
using ForgeWeaponEnergyLab.Native;
using Gear;
using GTFO.API;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeWeaponEnergyLab.Presentation;

/// <summary>
/// The energy weapons' effects and the ricochet disc's model. Every effect is a slot of the effect library, read once when a level that carries an
/// energy gun is built (<see cref="LevelBuilt"/>; a gun assembled in a level that had none reads it on the next frame) and played with ForgeVisual's
/// <see cref="Fx"/>; until then the slots are empty and draw nothing. The disc's bundle is embedded in this DLL and registered with ForgeVisual as the pack <c>EnergyLabDisc</c> at load. A slot that is missing, or a disc that cannot be prepared, logs its error and is not drawn: nothing
/// substitutes another effect, and the guns fire either way.
/// </summary>
internal static class EnergyVfx
{
    private const string DiscPack = "EnergyLabDisc";

    /// <summary>The effect library's slots for this mod (<see cref="FxSlots.Empty"/> before it is read and without the bundle) and the handles the per-frame effects hold, found once when it is read.</summary>
    internal static FxSlots Library = FxSlots.Empty;
    internal static FxHandle? BeamLine, ArcLine, FlameLine, DiscTrail, OrbCharge, OrbTrail, HoleShell, OrbBody, ShockOrb, HoleField, FlameHit, PlasmaBlast, SparksHit,
        BlastMuzzle, OrbMuzzle, Discharge, DiscHit, DiscLaunch, HoleLaunch, BeamMuzzle, BeamHit, ArcHit, ShockBurst,
        OrbCharged, HoleOpen, HoleCollapse;

    /// <summary>Plays a slot's effect; a slot the table lacked (reported then), or one not read yet, draws nothing.</summary>
    internal static void Play(FxHandle? fx, UnityEngine.Vector3 at, UnityEngine.Vector3 direction)
    {
        if (fx != null) Fx.Play(fx, at, direction);
    }

    /// <summary>The ricochet disc's model; null when it could not be prepared.</summary>
    internal static ModelHandle? Disc;

    internal static IEnumerator Preload()
    {
        try
        {
            Register(DiscPack, "forge-energy-disc.bundle");
            var disc = Resolve(DiscPack, "forge_ricochet_disc");
            UseNativeDiscShaders(disc.Prefab);
            Disc = disc;
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("energylab-disc-prepare-failed", "the ricochet disc could not be prepared and is not drawn; the gun still fires: " + error); }
        yield break;
    }

    /// <summary>Whether the effect library has been read (successfully or not); before that a gun's <c>Look.Fx</c> is not looked up.</summary>
    internal static bool LibraryRead { get; private set; }
    private static bool _levelBuilt, _wanted;

    internal static void Register()
    {
        LevelAPI.OnBuildDone += LevelBuilt;
        ForgeCore.GameEvents.LevelEnded += () => _levelBuilt = false;
        // The blast gun's explosion is drawn under ForgeCore's shared name from this package's own copy (HeavyBlast/).
        HeavyBlast.Register(Plugin.Id, "HeavyBlast/forge-visual-fx.bundle", "HeavyBlast/fx-slots.forge");
    }

    /// <summary>The level is built: the library is read now when a player's weapon slot holds an energy gun.</summary>
    private static void LevelBuilt()
    {
        _levelBuilt = true;
        if (!LibraryRead && LevelCarriesEnergyGun()) ReadLibrary();
    }

    /// <summary>An energy gun was assembled. In a built level whose players had none (a late joiner, a bot), the library is read on the next frame
    /// (<see cref="Tick"/>); the guns assembled for the loadout icons before any level do not read it.</summary>
    internal static void GunAssembled() => _wanted |= _levelBuilt;

    internal static void Tick()
    {
        if (_wanted && !LibraryRead) ReadLibrary();
    }

    private static bool LevelCarriesEnergyGun()
    {
        for (var i = 0; i < SNet.Slots.PlayerSlots.Length; i++)
            if (SNet.Slots.GetPlayerInSlot(i) is { } player && PlayerBackpackManager.TryGetBackpack(player, out var backpack) && backpack != null)
                foreach (var slot in WeaponSlots)
                    if (backpack.TryGetBackpackItem(slot, out var item) && item?.GearIDRange is { } range
                        && EnergyGears.ForCategory(range.GetCompID(eGearComponent.Category)) != null) return true;
        return false;
    }

    private static readonly InventorySlot[] WeaponSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial };

    private static void ReadLibrary()
    {
        LibraryRead = true;
        UseLibrary();
        // The guns' own Look.Fx name library slots: looked up again now that the slots exist (the sounds were looked up at load).
        if (PresentationPreload.Ready) EnergyLook.ResolveAll();
    }

    /// <summary>The effect library's prefabs: the bundle <c>forge-energylab-fx.bundle</c> and its slot table <c>fx-slots.forge</c> next to the
    /// plugin, made by <c>Tools/Vfx/FxLibrary/fx_slots.py</c> from the owner's selection.</summary>
    private static void UseLibrary()
    {
        try
        {
            var set = Library = FxSlots.Load(Plugin.Id, "EnergyLabFx", "forge-energylab-fx.bundle", "fx-slots.forge");
            HeavyBlast.Load(); // the blast gun's explosion: this package's copy, registered at plugin load (Register)
            FlameHit = set.Find("energy.flame.hit");
            PlasmaBlast = set.Find("energy.plasma.blast"); SparksHit = set.Find("energy.sparks");
            // The beam and arc are line effects (authored along +Z, stretched from muzzle to end); the flame jet is held at the muzzle, its size the reach.
            BeamLine = set.Find("energy.beam"); BeamMuzzle = set.Find("energy.beam.muzzle"); BeamHit = set.Find("energy.beam.hit");
            ArcLine = set.Find("energy.arc"); ArcHit = set.Find("energy.arc.hit"); FlameLine = set.Find("energy.flame");
            DiscTrail = set.Find("energy.disc"); OrbCharge = set.Find("energy.orb.charge"); OrbTrail = set.Find("energy.orb.trail");
            HoleShell = set.Find("energy.hole.shell");
            OrbBody = set.Find("energy.orb"); ShockOrb = set.Find("energy.shock.orb"); ShockBurst = set.Find("energy.shock.burst"); HoleField = set.Find("energy.hole");
            BlastMuzzle = set.Find("energy.blast.muzzle"); OrbMuzzle = set.Find("energy.orb.muzzle"); Discharge = set.Find("energy.discharge");
            DiscHit = set.Find("energy.disc.hit"); DiscLaunch = set.Find("energy.disc.launch"); HoleLaunch = set.Find("energy.hole.launch");
            // Light-only moments: the slot is a light and no prefab.
            OrbCharged = set.Find("energy.orb.charged"); HoleOpen = set.Find("energy.hole.open"); HoleCollapse = set.Find("energy.hole.collapse");
        }
        catch (Exception error) { Plugin.Logger.ErrorOnce("energylab-effects-prepare-failed", "library effects could not be prepared and are not drawn; the guns still fire: " + error); }
    }

    private static void Register(string pack, string file)
    {
        var resource = "ForgeWeaponEnergyLab.Vfx." + file;
        var bytes = ForgeCore.Assets.Embedded(typeof(EnergyVfx).Assembly, resource);
        if (bytes.Length == 0) throw new InvalidDataException("Missing " + resource);
        if (ForgeVisual.Bundles.RegisterBytes(Plugin.Id, pack, file, bytes) == null)
            throw new InvalidDataException("ForgeVisual refused the effect bundle " + file + " (see its error in the log).");
    }

    private static ModelHandle Resolve(string pack, string member)
        => Models.Get(pack + ":" + member) ?? throw new InvalidDataException("Missing VFX " + member);

    // Baked material parameters use the game's shaders, just like native disc construction.
    private static void UseNativeDiscShaders(GameObject prefab)
    {
        var renderer = prefab.GetComponentInChildren<MeshRenderer>();
        var mesh = prefab.GetComponentInChildren<MeshFilter>()?.sharedMesh;
        if (renderer == null || mesh == null || mesh.subMeshCount != 4 || renderer.sharedMaterials.Length != 4)
            throw new InvalidDataException("Disc prefab requires four surfaces.");
        for (var i = 0; i < 4; i++)
        {
            var material = renderer.sharedMaterials[i] ?? throw new InvalidDataException("Missing disc material.");
            material.shader = ForgeVisual.Shaders.Find(i == 3 ? "Sprites/Default" : "Standard")
                ?? throw new InvalidOperationException("Native disc shader is unavailable.");
            if (i < 3) material.EnableKeyword("_EMISSION");
        }
    }
}
