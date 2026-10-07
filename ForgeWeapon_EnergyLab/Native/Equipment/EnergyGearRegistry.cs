using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GameData;
using Gear;
using GTFO.API;
using Localization;
using Player;
using UnityEngine;
using Il2CppGearList = Il2CppSystem.Collections.Generic.List<Gear.GearIDRange>;

namespace ForgeWeaponEnergyLabExperimental.Native;

// Add-only GTFO datablocks. No MTFO, ForgeRuntime or ForgeWeapon installation is needed.
internal static class EnergyGearRegistry
{
    // Shipped R8 templates: a semi-auto main weapon and a semi-auto special weapon.
    private const uint MainTemplate = 5, SpecialTemplate = 43;
    private const uint MainCategory = 4, SpecialCategory = 3;
    private const uint MainArchetype = 25, SpecialArchetype = 21;
    internal static bool Ready { get; private set; }
    private static bool _iconChecksumsRegistered;
    private static readonly Dictionary<uint, (EnergyGear Gear, GearIDRange.GearComponentCollection Comps)> CurrentPackets = new();

    internal static void Register()
    {
        GameDataAPI.OnGameDataInitialized += Install;
    }

    internal static EnergyGear? DefinitionOf(BulletWeapon? weapon)
        => weapon?.GearCategoryData is { } category ? EnergyGears.ForCategory(category.persistentID) : null;
    internal static EnergyMode ModeOf(BulletWeapon? weapon) => DefinitionOf(weapon)?.Mode ?? EnergyMode.Off;
    internal static bool Matches(BulletWeapon? weapon, uint categoryId, int mode)
        => EnergyGears.Match(categoryId, mode) is { } gear && DefinitionOf(weapon) == gear;

    internal static void Install()
    {
        Ready = false;
        try
        {
            EnergyGears.Freeze();
            var mainGear = Require<PlayerOfflineGearDataBlock>(MainTemplate);
            var specialGear = Require<PlayerOfflineGearDataBlock>(SpecialTemplate);
            var mainCategory = Require<GearCategoryDataBlock>(MainCategory);
            var specialCategory = Require<GearCategoryDataBlock>(SpecialCategory);
            var mainArchetype = Require<ArchetypeDataBlock>(MainArchetype);
            var specialArchetype = Require<ArchetypeDataBlock>(SpecialArchetype);
            Require<GearSightPartDataBlock>(EnergyGearJson.ZoomSightPart);
            var staged = new List<(ArchetypeDataBlock Archetype, GearCategoryDataBlock Category, PlayerOfflineGearDataBlock Gear,
                WeaponAudioDataBlock Audio, WeaponShellCasingDataBlock Casing, RecoilDataBlock Recoil)>();
            var gearIds = new HashSet<uint>(); var categoryIds = new HashSet<uint>(); var archetypeIds = new HashSet<uint>();
            foreach (var gear in EnergyGears.All)
            {
                if (!gearIds.Add(gear.GearId) || !categoryIds.Add(gear.CategoryId) || !archetypeIds.Add(gear.ArchetypeId) ||
                    gear.Clip <= 0 || !float.IsFinite(gear.AmmoPerRound) || gear.AmmoPerRound <= 0f)
                    throw new InvalidOperationException("Invalid energy gear definition: " + gear.Name);
                var sourceGear = gear.Main ? mainGear : specialGear;
                var sourceCategory = gear.Main ? mainCategory : specialCategory;
                var sourceArchetype = gear.Main ? mainArchetype : specialArchetype;
                var source = new GearIDRange(sourceGear.GearJSON);
                var audio = Require<WeaponAudioDataBlock>(source.GetCompID(eGearComponent.AudioSetting)).MemberwiseClone().Cast<WeaponAudioDataBlock>();
                audio.name = gear.NativeName; audio.persistentID = gear.CategoryId; audio.internalEnabled = true;
                audio.eventOnSemiFire2D = new(); audio.eventOnSemiFire3D = new();
                audio.eventOnBurstFire2D = new(); audio.eventOnBurstFire3D = new();
                audio.eventOnBurstFireOneShot2D = new();
                audio.eventOnAutoFireStart2D = new(); audio.eventOnAutoFireStart3D = new();
                audio.eventOnAutoFireEnd2D = new(); audio.eventOnAutoFireEnd3D = new();
                audio.eventOnChargeup2D = new(); audio.eventOnChargeup3D = new();
                audio.eventOnChargeupEnd2D = string.Empty; audio.eventOnChargeupEnd3D = string.Empty;
                audio.eventOnCooldown2D = new(); audio.eventOnCooldown3D = new();
                audio.eventOnCooldownEnd2D = string.Empty; audio.eventOnCooldownEnd3D = string.Empty;
                audio.eventOnSyncedBurstFirePerShot3D = new(); audio.eventOnSyncedAutoFirePerShot3D = new();
                var casing = Require<WeaponShellCasingDataBlock>(source.GetCompID(eGearComponent.ShellCasing)).MemberwiseClone().Cast<WeaponShellCasingDataBlock>();
                casing.name = gear.NativeName; casing.persistentID = gear.CategoryId; casing.internalEnabled = true;
                casing.ShellCasingType = ShellTypes.Shell_None;
                var recoil = CloneRecoil(Require<RecoilDataBlock>(sourceArchetype.RecoilDataID), gear);
                ValidateOwned(audio); ValidateOwned(casing); ValidateOwned(recoil);
                // Nested donor data is shared read-only; only the cloned blocks are changed.
                var archetype = sourceArchetype.MemberwiseClone().Cast<ArchetypeDataBlock>();
                archetype.name = gear.NativeName;
                archetype.persistentID = gear.ArchetypeId;
                archetype.internalEnabled = true;
                archetype.RecoilDataID = recoil.persistentID;
                archetype.PublicName = Label(gear.Name);
                archetype.Description = Label(gear.Definition.Description ?? "Experimental Forge energy weapon");
                archetype.Damage = gear.Numbers.Shot.Damage;
                archetype.DamageFalloff = new Vector2(gear.Numbers.Shot.Range, gear.Numbers.Shot.Range);
                archetype.StaggerDamageMulti = gear.Numbers.Shot.Stagger;
                archetype.PrecisionDamageMulti = gear.Numbers.Shot.Precision;
                archetype.DefaultClipSize = gear.Clip;
                archetype.DefaultReloadTime = gear.Definition.Tuning.ReloadTime;
                archetype.CostOfBullet = gear.AmmoPerRound;
                archetype.ShotDelay = (float)gear.Numbers.Shot.Interval;
                archetype.FireMode = eWeaponFireMode.Auto;
                archetype.BurstDelay = 0f;
                archetype.SpecialChargetupTime = 0f;
                archetype.SpecialCooldownTime = 0f;
                archetype.PiercingBullets = false;
                archetype.PiercingDamageCountLimit = 1;
                archetype.ShotgunBulletCount = 1;
                archetype.ShotgunConeSize = archetype.ShotgunBulletSpread = 0;
                archetype.HipFireSpread = archetype.AimSpread = 0f;
                archetype.EquipTransitionTime = gear.Definition.Tuning.EquipTime;
                archetype.AimTransitionTime = gear.Definition.Tuning.AimTime;
                var category = sourceCategory.MemberwiseClone().Cast<GearCategoryDataBlock>();
                category.name = gear.NativeName;
                category.persistentID = gear.CategoryId;
                category.internalEnabled = true;
                category.PublicName = Label(gear.Name);
                category.Description = Label(gear.Definition.Description ?? "Experimental Forge energy weapon");
                category.SemiArchetype = category.BurstArchetype = category.AutoArchetype = category.SemiBurstArchetype = gear.ArchetypeId;
                var block = new PlayerOfflineGearDataBlock
                {
                    name = gear.NativeName,
                    persistentID = gear.GearId,
                    internalEnabled = true,
                    Type = sourceGear.Type,
                    GearJSON = EnergyGearJson.Build(sourceGear.GearJSON, gear)
                };
                var packet = new GearIDRange(block.GearJSON);
                if (packet.GetCompID(eGearComponent.Category) != gear.CategoryId ||
                    packet.GetCompID(eGearComponent.BaseItem) == 0 || packet.GetCompID((eGearComponent)EnergyGearJson.SightComponent) != EnergyGearJson.ZoomSightPart ||
                    (packet.GetCompID(eGearComponent.AudioSetting) != gear.CategoryId ||
                        packet.GetCompID(eGearComponent.ShellCasing) != gear.CategoryId))
                    throw new InvalidOperationException("Invalid staged energy gear packet: " + gear.Name);
                ValidateOwned(archetype); ValidateOwned(category); ValidateOwned(block);
                staged.Add((archetype, category, block, audio, casing, recoil));
            }
            // All JSON, templates and collisions are checked before the first native write.
            // A native AddBlock failure is recoverable: a re-install skips this package's
            // already registered blocks individually, and completes only the missing suffix.
            EnergyNetwork.SealDefinitions();
            foreach (var block in staged)
            {
                AddMissing(block.Audio);
                AddMissing(block.Casing);
                AddMissing(block.Recoil);
                AddMissing(block.Archetype); AddMissing(block.Category); AddMissing(block.Gear);
            }
            foreach (var gear in EnergyGears.All)
                if (GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(gear.GearId) == null ||
                    GameDataBlockBase<GearCategoryDataBlock>.GetBlock(gear.CategoryId) == null ||
                    GameDataBlockBase<ArchetypeDataBlock>.GetBlock(gear.ArchetypeId) == null ||
                    GameDataBlockBase<ArchetypeDataBlock>.GetBlock(gear.ArchetypeId).RecoilDataID != gear.CategoryId ||
                    GameDataBlockBase<RecoilDataBlock>.GetBlock(gear.CategoryId) == null ||
                    (GameDataBlockBase<WeaponAudioDataBlock>.GetBlock(gear.CategoryId) == null ||
                        GameDataBlockBase<WeaponShellCasingDataBlock>.GetBlock(gear.CategoryId) == null))
                    throw new InvalidOperationException("Energy gear did not register: " + gear.Name);
            foreach (var gear in EnergyGears.All)
                CurrentPackets[gear.CategoryId] = (gear, new GearIDRange(Require<PlayerOfflineGearDataBlock>(gear.GearId).GearJSON).GetPacketStruct().Comps);
            Ready = true;
        }
        catch (Exception error)
        {
            Plugin.Error("Energy gear registration failed; all energy firing remains disabled: " + error);
        }
    }

    // Both offline loading and PlayFab inventory loading end here. The lobby reads this
    // pool, not the datablock list, so the new guns must be present in the live pool.
    internal static void EnsureLoadout(GearManager manager)
    {
        if (!Ready) Install();
        if (!Ready) return;
        try
        {
            var pool = manager.m_gearPerSlot ?? throw new InvalidOperationException("Gear loadout pool is missing.");
            foreach (var gear in EnergyGears.All)
            {
                var slot = (int)(gear.Main ? InventorySlot.GearStandard : InventorySlot.GearSpecial);
                if (slot >= pool.Length || pool[slot] == null)
                    throw new InvalidOperationException("Gear loadout slot is missing: " + slot);
                if (Contains(pool[slot], gear.CategoryId)) continue;

                var block = Require<PlayerOfflineGearDataBlock>(gear.GearId);
                var candidate = new GearIDRange(block.GearJSON);
                if (candidate.GetCompID(eGearComponent.Category) != gear.CategoryId ||
                    candidate.GetCompID(eGearComponent.BaseItem) == 0)
                    throw new InvalidOperationException("Gear packet has an invalid category or base item: " + gear.Name);
                candidate.PlayfabItemId = block.name;
                candidate.PlayfabItemInstanceId = "OfflineGear_ID_" + gear.GearId.ToString(CultureInfo.InvariantCulture);
                candidate.OfflineGearType = block.Type;
                pool[slot].Add(candidate);
                if (pool[slot] == null || !Contains(pool[slot], gear.CategoryId))
                    throw new InvalidOperationException("Gear was not added to its loadout slot: " + gear.Name);
            }
            RegisterPreviewChecksums();
        }
        catch (Exception error)
        {
            Plugin.Error("Forge Weapon Energy Lab experimental weapons are not confirmed in the loadout: " + error);
        }
    }

    // Saved bot loadouts keep whole gear packets. A packet saved by an earlier build would assemble the gun with
    // outdated components (stock fire sound, shell casings) under an icon checksum this build never registered,
    // so every energy gun packet takes this build's components and its checksum maps to the gun's icon.
    internal static void UseCurrentPacket(GearIDRange? range)
    {
        if (!Ready || range == null || !CurrentPackets.TryGetValue(range.GetCompID(eGearComponent.Category), out var current)) return;
        range.ApplyCompIDs(current.Comps);
        Presentation.EnergyIcons.Register(range, current.Gear);
    }

    // Baked previews keep a separate in-memory cache; native PNGs remain untouched.
    private static void RegisterPreviewChecksums()
    {
        if (_iconChecksumsRegistered) return;
        foreach (var gear in EnergyGears.All)
            Presentation.EnergyIcons.Register(new GearIDRange(Require<PlayerOfflineGearDataBlock>(gear.GearId).GearJSON), gear);
        _iconChecksumsRegistered = true;
    }

    private static bool Contains(Il2CppGearList list, uint categoryId)
    {
        for (var index = 0; index < list.Count; index++)
            if (list[index]?.GetCompID(eGearComponent.Category) == categoryId) return true;
        return false;
    }

    private static T Require<T>(uint id) where T : GameDataBlockBase<T>
        => GameDataBlockBase<T>.GetBlock(id) ?? throw new InvalidOperationException($"Missing native {typeof(T).Name} template {id}.");

    internal static ItemMovementAnimationDataBlock RegisterRecoilAnimation(ItemMovementAnimationDataBlock animation)
    {
        AddMissing(animation);
        var registered = Require<ItemMovementAnimationDataBlock>(animation.persistentID);
        if (registered.Pointer != animation.Pointer)
        {
            // Reassembly can select different donor settings; refresh only this category's owned block.
            registered.ItemPosition = animation.ItemPosition; registered.ItemRotation = animation.ItemRotation;
            registered.CameraPosition = animation.CameraPosition; registered.CameraRotation = animation.CameraRotation;
            registered.m_cachedDuration = animation.m_cachedDuration;
        }
        return registered;
    }

    internal static RecoilDataBlock CloneRecoil(RecoilDataBlock source, EnergyGear gear)
    {
        var recoil = source.MemberwiseClone().Cast<RecoilDataBlock>();
        recoil.name = gear.NativeName; recoil.persistentID = gear.CategoryId; recoil.internalEnabled = true;
        var continuous = gear.Mode is EnergyMode.Beam or EnergyMode.Flame;
        var scale = continuous ? 0f : 1f / 3f;
        var directionScale = continuous ? 0f : 1f;
        // The two axis ranges define the normalized direction pattern, not its strength.
        // All ranges are reference types and must remain independent of the donor.
        recoil.power = new() { Min = source.power.Min * scale, Max = source.power.Max * scale };
        recoil.horizontalScale = new() { Min = source.horizontalScale.Min * directionScale, Max = source.horizontalScale.Max * directionScale };
        recoil.verticalScale = new() { Min = source.verticalScale.Min * directionScale, Max = source.verticalScale.Max * directionScale };
        recoil.hipFireCrosshairRecoilPop = source.hipFireCrosshairRecoilPop * scale;
        recoil.recoilPosImpulse = source.recoilPosImpulse * scale;
        recoil.recoilPosShift = source.recoilPosShift * scale;
        recoil.recoilRotImpulse = source.recoilRotImpulse * scale;
        // Weights multiply the magnitudes above; they keep donor values so each kick is scaled once, not squared.
        recoil.concussionIntensity = source.concussionIntensity * scale;
        recoil.concussionFrequency = continuous ? 0f : source.concussionFrequency;
        recoil.concussionDuration = continuous ? 0f : source.concussionDuration;
        // Continuous guns retain zero direction/shake data. Other timing/blending and animations keep their donor values.
        return recoil;
    }

    private static class Owned<T> { internal static readonly HashSet<uint> Ids = new(); }

    private static void ValidateOwned<T>(T staged) where T : GameDataBlockBase<T>
    {
        var existing = GameDataBlockBase<T>.GetBlock(staged.persistentID);
        if (existing != null && (!Owned<T>.Ids.Contains(staged.persistentID) || existing.name != staged.name))
            throw new InvalidOperationException($"Energy gear ID collision: {typeof(T).Name}/{staged.persistentID}.");
    }

    private static void AddMissing<T>(T block) where T : GameDataBlockBase<T>
    {
        ValidateOwned(block);
        if (GameDataBlockBase<T>.GetBlock(block.persistentID) == null)
        {
            // A native call can throw after insertion. Retain ownership for a safe retry.
            try { GameDataBlockBase<T>.AddBlock(block); }
            finally
            {
                if (GameDataBlockBase<T>.GetBlock(block.persistentID)?.name == block.name) Owned<T>.Ids.Add(block.persistentID);
            }
        }
        if (GameDataBlockBase<T>.GetBlock(block.persistentID)?.name != block.name)
            throw new InvalidOperationException($"Energy block registration readback failed: {typeof(T).Name}/{block.persistentID}.");
    }

    // LocalizedText is an IL2CPP value type. Its managed string constructor does not
    // allocate a boxed instance; the game's uint conversion does.
    private static LocalizedText Label(string text)
    {
        LocalizedText value = 0u;
        value.UntranslatedText = text;
        return value;
    }
}
