using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ForgeCore;
using GameData;
using Gear;
using GTFO.API;
using Localization;
using Player;
using UnityEngine;
using Il2CppGearList = Il2CppSystem.Collections.Generic.List<Gear.GearIDRange>;

namespace ForgeWeaponEnergyLab.Native;

// Add-only GTFO datablocks, registered by this plugin itself; no MTFO rundown files and no Forge base package are used.
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
        ForgeUI.UIKit.LanguageChanged += Relabel;
    }

    /// <summary>Writes the names and descriptions the lobby and inventory show again in the new language (ForgeUI translates the screens
    /// already drawn). The gear packet's own public name stays the stable name every peer agrees on, so the packet does not depend on
    /// a player's language.</summary>
    private static void Relabel()
    {
        if (!Ready) return;
        foreach (var gear in EnergyGears.All)
        {
            var name = Label(EnergyText.Name(gear));
            var description = Label(EnergyText.Description(gear));
            var archetype = GameDataBlockBase<ArchetypeDataBlock>.GetBlock(gear.ArchetypeId);
            var category = GameDataBlockBase<GearCategoryDataBlock>.GetBlock(gear.CategoryId);
            if (archetype != null) { archetype.PublicName = name; archetype.Description = description; }
            if (category != null) { category.PublicName = name; category.Description = description; }
        }
    }

    internal static EnergyGear? DefinitionOf(BulletWeapon? weapon)
        => weapon?.GearCategoryData is { } category ? EnergyGears.ForCategory(category.persistentID) : null;
    internal static EnergyMode ModeOf(BulletWeapon? weapon) => DefinitionOf(weapon)?.Mode ?? EnergyMode.Off;
    internal static bool Matches(BulletWeapon? weapon, uint categoryId, int mode)
        => EnergyGears.Match(categoryId, mode) is { } gear && DefinitionOf(weapon) == gear;

    internal static void Install()
    {
        // Every offline gear load calls this; once installed and the blocks still stand, there is nothing to clone again.
        if (Ready && EnergyGears.All.All(gear => GameDataBlockBase<PlayerOfflineGearDataBlock>.GetBlock(gear.GearId) != null)) return;
        Ready = false;
        try
        {
            var problems = new List<string>();
            EnergyGears.Freeze(problems);
            foreach (var problem in problems) Plugin.Logger.ErrorOnce(problem);
            Presentation.EnergyIcons.Supply();
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
                var audio = DataBlocks.Clone(Require<WeaponAudioDataBlock>(source.GetCompID(eGearComponent.AudioSetting)), gear.NativeName, gear.CategoryId);
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
                var casing = DataBlocks.Clone(Require<WeaponShellCasingDataBlock>(source.GetCompID(eGearComponent.ShellCasing)), gear.NativeName, gear.CategoryId);
                casing.ShellCasingType = ShellTypes.Shell_None;
                var recoil = CloneRecoil(Require<RecoilDataBlock>(sourceArchetype.RecoilDataID), gear);
                ValidateOwned(audio); ValidateOwned(casing); ValidateOwned(recoil);
                // Nested donor data is shared read-only; only the cloned blocks are changed.
                var archetype = DataBlocks.Clone(sourceArchetype, gear.NativeName, gear.ArchetypeId);
                archetype.RecoilDataID = recoil.persistentID;
                archetype.PublicName = Label(EnergyText.Name(gear));
                archetype.Description = Label(EnergyText.Description(gear));
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
                var category = DataBlocks.Clone(sourceCategory, gear.NativeName, gear.CategoryId);
                category.PublicName = Label(EnergyText.Name(gear));
                category.Description = Label(EnergyText.Description(gear));
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
            // One transaction: every collision is checked before the first native write and a failed write is removed again.
            // A re-install skips this package's blocks that are already registered.
            EnergyNetwork.SealDefinitions();
            var all = new List<object>();
            foreach (var block in staged)
                all.AddRange(new object[] { block.Audio, block.Casing, block.Recoil, block.Archetype, block.Category, block.Gear });
            var added = DataBlocks.AddAll(Plugin.Id, all);
            if (added is not (DataBlockResult.Added or DataBlockResult.AlreadyAdded))
                throw new InvalidOperationException($"Energy blocks registration failed ({added}).");
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
            EnergyText.Register();
            EnergyRules.Apply();
            Ready = true;
        }
        catch (Exception error)
        {
            Plugin.Logger.ErrorOnce("Energy gear registration failed; all energy firing remains disabled: " + error);
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
            Plugin.Logger.ErrorOnce("Forge Weapon Energy Lab weapons are not confirmed in the loadout: " + error);
        }
    }

    // Saved bot loadouts keep whole gear packets. A packet saved by an earlier build would assemble the gun with
    // outdated components (stock fire sound, shell casings) under an icon checksum this build never registered,
    // so every energy gun packet takes this build's components and its icon file is cleared once per run.
    internal static void UseCurrentPacket(GearIDRange? range)
    {
        if (!Ready || range == null || !CurrentPackets.TryGetValue(range.GetCompID(eGearComponent.Category), out var current)) return;
        range.ApplyCompIDs(current.Comps);
        ForgeUI.GearIcons.ClearCached(range);
    }

    // The game draws each energy gun's icon from its own model; the file it keeps per checksum is cleared once per run.
    private static void RegisterPreviewChecksums()
    {
        if (_iconChecksumsRegistered) return;
        foreach (var gear in EnergyGears.All)
            ForgeUI.GearIcons.ClearCached(new GearIDRange(Require<PlayerOfflineGearDataBlock>(gear.GearId).GearJSON));
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
        var continuous = gear.Mode is EnergyMode.Beam or EnergyMode.Flame;
        // Strength (view kick, crosshair pop, weapon kick, shake) is ForgeCore's one scaling; only the direction pattern and the
        // shake timing are this mod's: a continuous gun keeps no direction or shake data.
        var recoil = ForgeCore.Gear.ScaledRecoil(DataBlocks.Clone(source, gear.NativeName, gear.CategoryId), continuous ? 0f : 1f / 3f);
        var directionScale = continuous ? 0f : 1f;
        recoil.horizontalScale = new() { Min = source.horizontalScale.Min * directionScale, Max = source.horizontalScale.Max * directionScale };
        recoil.verticalScale = new() { Min = source.verticalScale.Min * directionScale, Max = source.verticalScale.Max * directionScale };
        recoil.concussionFrequency = continuous ? 0f : source.concussionFrequency;
        recoil.concussionDuration = continuous ? 0f : source.concussionDuration;
        return recoil;
    }

    // ForgeCore's DataBlocks adds only: an ID the game or another mod holds is refused, and a block this plugin added before is kept.
    private static void ValidateOwned<T>(T staged) where T : GameDataBlockBase<T>
    {
        if (DataBlocks.Check(Plugin.Id, staged) == DataBlockResult.Conflict)
            throw new InvalidOperationException($"Energy gear ID collision: {typeof(T).Name}/{staged.persistentID}.");
    }

    private static void AddMissing<T>(T block) where T : GameDataBlockBase<T>
    {
        var result = DataBlocks.Add(Plugin.Id, block);
        if (result is not (DataBlockResult.Added or DataBlockResult.AlreadyAdded))
            throw new InvalidOperationException($"Energy block registration failed ({result}): {typeof(T).Name}/{block.persistentID}.");
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
