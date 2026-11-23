using System.Globalization;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ForgeWeapon.Tests.NativeLayout;

// Compiled-assembly metadata only: proves boundaries, plugin identity and hook shape, not native call timing.
// Inputs come from the environment (see ConfiguredInputs); the suite is excluded by the Native trait by default.
[Trait("Category", "Native")]
public sealed partial class NativeLayoutTests
{
    private static readonly string BepInEx = Environment.GetEnvironmentVariable("FORGE_WEAPON_BEPINEX")
        ?? Environment.GetEnvironmentVariable("GTFO_BEPINEX_PATH") ?? "";
    private static readonly string Artifacts = Environment.GetEnvironmentVariable("FORGE_ARTIFACTS") ?? "";
    private static readonly string Spec = Environment.GetEnvironmentVariable("FORGE_WEAPON_HOOK_SPEC")
        ?? Path.Combine("ForgeEngine", "Weapon", "evidence", "w1-native-hooks.json");
    /// <summary>The loadout policy's own hook record, written for a later batch than the w1 file: its two targets
    /// are verified by the same checks, and the dispatch kind and priority each one declares are verified too, which
    /// the w1 record's postfixes do not carry.</summary>
    private static readonly string LoadoutSpec = Environment.GetEnvironmentVariable("FORGE_WEAPON_LOADOUT_SPEC")
        ?? Path.Combine("ForgeEngine", "Weapon", "evidence", "w7-loadout-policy.json");

    // The 31 non-metadata fields in native-schema.types.MeleeAnimationSet, also present in
    // native-runtime.types.MeleeAnimationSetDataBlock (GameData). CloneMelee preserves them on
    // a newly constructed, unregistered per-item block; nested animation objects are only copied
    // by reference, never mutated. MaxDamageChargeTime alone is overridden afterwards.
    internal static readonly string[] MeleeCopyWrites =
    {
        "GameData.MeleeAnimationSetDataBlock::set_AutoAttackTime",
        "GameData.MeleeAnimationSetDataBlock::set_AutoAttackWarningTime",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpHitLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpHitRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpReleaseLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpReleaseRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackHitLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackHitRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackMissLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackMissRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPAttackPush",
        "GameData.MeleeAnimationSetDataBlock::set_FPChargeCancelAnimLeft",
        "GameData.MeleeAnimationSetDataBlock::set_FPChargeCancelAnimRight",
        "GameData.MeleeAnimationSetDataBlock::set_FPIdleAnim",
        "GameData.MeleeAnimationSetDataBlock::set_FPJumpAnim",
        "GameData.MeleeAnimationSetDataBlock::set_FPLandAnim",
        "GameData.MeleeAnimationSetDataBlock::set_FPRunAnim",
        "GameData.MeleeAnimationSetDataBlock::set_FPSettleAnim",
        "GameData.MeleeAnimationSetDataBlock::set_FPWalkAnim",
        "GameData.MeleeAnimationSetDataBlock::set_HoldToChargeTime",
        "GameData.MeleeAnimationSetDataBlock::set_MaxDamageChargeTime",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeft",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftCharge",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftChargeCrouch",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftCrouch",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftRelease",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftReleaseCrouch",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashIdle",
        "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashIdleCrouch",
    };

    // This only narrows the exact write whitelist below: no new member is admitted by its type/name.
    // The timing read sites are pinned in evidence/primitive-time-properties.json. Instance pointers
    // belong to ItemEquippable; the active charge caches belong to Gear.MWS_ChargeUp.
    internal static bool InstanceTimeWriteIsScoped(string callerType, string caller, string member)
    {
        if (callerType != "ForgeWeapon.Native.WeaponOverrideApplier") return false;
        if (member == "ItemEquippable::set_ArchetypeData") return caller == "Install";
        if (member == "ItemEquippable::set_MeleeAnimationData") return caller == "ApplyMelee";
        if (member is "Gear.MWS_ChargeUp::set_m_maxDamageTime" or "Gear.MWS_ChargeUp::set_m_chargeUpDone")
            return caller is "ApplyMelee" or "WithdrawMelee";
        if (member == "GameData.MeleeAnimationSetDataBlock::set_MaxDamageChargeTime")
            return caller is "CloneMelee" or "ApplyMelee" or "WithdrawMelee";
        return caller == "CloneMelee" && MeleeCopyWrites.Contains(member, StringComparer.Ordinal);
    }

    internal static bool MeleeCopyMembersAreExact(IEnumerable<string> members)
    {
        var calls = members.ToArray();
        var reads = MeleeCopyWrites.Select(m => m.Replace("::set_", "::get_", StringComparison.Ordinal)).ToArray();
        return calls.Where(m => MeleeCopyWrites.Contains(m, StringComparer.Ordinal))
                .OrderBy(m => m, StringComparer.Ordinal).SequenceEqual(MeleeCopyWrites, StringComparer.Ordinal)
            && calls.Where(m => reads.Contains(m, StringComparer.Ordinal))
                .OrderBy(m => m, StringComparer.Ordinal).SequenceEqual(reads, StringComparer.Ordinal);
    }

    // Exact IL call sites reviewed against w8-forge-field-apply.json, flashlight-settings.json,
    // item-use-failures.json and inventory-actions.json; native-calls.json records this local build.
    // Pin the enclosing type, source method, lambda and multiplicity. Compiler-assigned closure
    // class ordinals change when unrelated methods are inserted and do not identify a write site.
    internal static readonly string[] ReviewedFieldCallSites =
    {
        "Agents.pAgent::Set <- ForgeWeapon.Native.EquipmentNativeAdapter::KeyOf",
        "FogRepeller_Sphere::set_Range <- ForgeWeapon.Native.WeaponFieldApplier::ApplyFogRepeller",
        "Gear.EnemyScanner::set_m_maxScanWorldRadius <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyScanner>b__1",
        "Gear.EnemyScanner::set_m_pulseDuration <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyScanner>b__3",
        "Gear.EnemyScanner::set_m_rechargeDuration <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyScanner>b__4",
        "Gear.EnemyScanner::set_m_scanConeDotMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyScanner>b__0",
        "Gear.EnemyScanner::set_m_scanDelay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyScanner>b__2",
        "Gear.EnemyScanner::set_s_maxCourseNodeDistance <- ForgeWeapon.Native.WeaponScannerCall::Apply",
        "Gear.EnemyScanner::set_s_maxCourseNodeDistance <- ForgeWeapon.Native.WeaponScannerCall::Restore",
        "Gear.EnemyScanner::set_s_tagDuration <- ForgeWeapon.Native.WeaponScannerCall::Apply",
        "Gear.EnemyScanner::set_s_tagDuration <- ForgeWeapon.Native.WeaponScannerCall::Restore",
        "Gear.GearPartFlashlight::set_m_settingsID <- ForgeWeapon.Native.GearPartFlashlightApplier::Apply",
        "GlueGun::set_m_burstShotDelay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__11",
        "GlueGun::set_m_burstShotsMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__10",
        "GlueGun::set_m_burstShotsMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__9",
        "GlueGun::set_m_fireDelayMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__4",
        "GlueGun::set_m_fireDelayMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__3",
        "GlueGun::set_m_forceSizeMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__6",
        "GlueGun::set_m_forceSizeMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__5",
        "GlueGun::set_m_pressureProgressToFire <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__2",
        "GlueGun::set_m_spreadMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__8",
        "GlueGun::set_m_spreadMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__7",
        "GlueGun::set_m_timeToDepleatePressure <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__1",
        "GlueGun::set_m_timeToMaxpressure <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyGlue>b__0",
        "InputMapper/DelGetButton::Invoke <- ForgeWeapon.Native.UseFailureReads::Requested",

        "Interact_Timed::set_InteractDuration <- ForgeWeapon.Native.WeaponFieldApplier::ApplyInteraction",
        "Interact_Timed::set_InteractDuration <- ForgeWeapon.Native.WeaponFieldApplier::RestoreInteraction",
        "LockMelterFirstPerson::set_m_interactionDuration <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyItem>b__0",
        "MineDeployerFirstPerson::set_m_deployPickupInteractionDuration <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInteraction>b__0",
        "MineDeployerFirstPerson::set_m_timeBetweenPlacements <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInteraction>b__1",
        "MineDeployerInstance_Detect_Laser::set_m_maxLineDistance <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__10",
        "MineDeployerInstance_Detonate_Explosive::set_m_damageMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__5",
        "MineDeployerInstance_Detonate_Explosive::set_m_damageMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__4",
        "MineDeployerInstance_Detonate_Explosive::set_m_delay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__0",
        "MineDeployerInstance_Detonate_Explosive::set_m_distanceMax <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__3",
        "MineDeployerInstance_Detonate_Explosive::set_m_distanceMin <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__2",
        "MineDeployerInstance_Detonate_Explosive::set_m_explosionForce <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__6",
        "MineDeployerInstance_Detonate_Explosive::set_m_radius <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__1",
        "MineDeployerInstance_Detonate_Glue::set_m_delay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__7",
        "MineDeployerInstance_Detonate_Glue::set_m_explosionDelay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__8",
        "MineDeployerInstance_Detonate_Glue::set_m_projAngMinMax <- ForgeWeapon.Native.WeaponFieldApplier::ApplyMineInstance",
        "MineDeployerInstance_Detonate_Glue::set_m_projCount <- ForgeWeapon.Native.WeaponFieldApplier::<ApplyMineInstance>b__9",
        "MineDeployerInstance_Detonate_Glue::set_m_projVelMinMax <- ForgeWeapon.Native.WeaponFieldApplier::ApplyMineInstance",
        "Player.PlayerBackpackManager::MasterRemoveItem <- ForgeWeapon.Native.InventoryActionAdapter::ConsumeSlotted",
        "SentryGunFirstPerson::set_m_deployPickupInteractionDuration <- ForgeWeapon.Native.WeaponFieldApplier::<ApplySentryInteraction>b__0",
        "SentryGunInstance::set_m_initialScanDelay <- ForgeWeapon.Native.WeaponFieldApplier::<ApplySentry>b__0",
    };

    internal static bool ReviewedFieldCallSitesAreExact(IEnumerable<string> sites)
        => sites.Select(site => System.Text.RegularExpressions.Regex.Replace(site, @"/<>c__DisplayClass\d+_\d+(?=::)", ""))
            .OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(ReviewedFieldCallSites, StringComparer.Ordinal);

    // Content admission prepares private gear indexes before publishing them (Publish), and restores the original
    // pointers on failure or when the content generation does not stand (Restore); a generation's withdrawn gear leaves through
    // copies too (Withdraw). Fixed-slot replacement mutates only its newly constructed GearIDRange. The new slot-pool array is
    // filled through ForgeCore's barriered Il2CppArrays.Store, which is listed here so no other caller may store into a game array
    // either. After a content generation the lobby's loadouts are equipped again: the game rebuilds its favourites (RescanFavorites)
    // and records a withdrawn gear's fallback as last equipped; PutBack restores both when the generation does not stand.
    // No other caller may gain these writes, including observation and loadout filtering.
    internal static readonly string[] ReviewedContentWriteSites =
    {
        "ForgeCore.Il2CppArrays::Store <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "ForgeCore.Il2CppArrays::Store <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "ForgeCore.Il2CppArrays::Store <- ForgeWeapon.Native.WeaponGearCatalogue::Withdraw",
        "Gear.GearFavoritesData::set_LastEquipped_Class <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Gear.GearFavoritesData::set_LastEquipped_Melee <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Gear.GearFavoritesData::set_LastEquipped_Special <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Gear.GearFavoritesData::set_LastEquipped_Standard <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Gear.GearIDRange::SetCompID <- ForgeWeapon.Native.HackingToolSlot::Select",
        "Gear.GearIDRange::set_OfflineGearType <- ForgeWeapon.Native.GearEquipPort::ResolveBlock",
        "Gear.GearIDRange::set_OfflineGearType <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Gear.GearIDRange::set_PlayfabItemId <- ForgeWeapon.Native.GearEquipPort::ResolveBlock",
        "Gear.GearIDRange::set_PlayfabItemId <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Gear.GearIDRange::set_PlayfabItemInstanceId <- ForgeWeapon.Native.GearEquipPort::ResolveBlock",
        "Gear.GearIDRange::set_PlayfabItemInstanceId <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Gear.GearIDRange::set_PublicGearName <- ForgeWeapon.Native.HackingToolSlot::Select",
        "Gear.GearManager::RegisterGearInSlotAsEquipped <- ForgeWeapon.Native.WeaponGearCatalogue::Reinstall",
        "Gear.GearManager::RescanFavorites <- ForgeWeapon.Native.WeaponGearCatalogue::Reinstall",
        "Gear.GearManager::set_m_allGearPerInstanceKey <- ForgeWeapon.Native.WeaponGearCatalogue::Publish",
        "Gear.GearManager::set_m_allGearPerInstanceKey <- ForgeWeapon.Native.WeaponGearCatalogue::Restore",
        "Gear.GearManager::set_m_favoriteGearPerSlot <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Gear.GearManager::set_m_gearPerSlot <- ForgeWeapon.Native.WeaponGearCatalogue::Publish",
        "Gear.GearManager::set_m_gearPerSlot <- ForgeWeapon.Native.WeaponGearCatalogue::Restore",
        "Gear.GearManager::set_m_lastEquippedGearPerSlot <- ForgeWeapon.Native.WeaponGearCatalogue::PutBack",
        "Il2CppSystem.Collections.Generic.Dictionary`2::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.Dictionary`2::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.Dictionary`2::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Withdraw",
        "Il2CppSystem.Collections.Generic.List`1::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.List`1::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.List`1::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.List`1::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.List`1::Add <- ForgeWeapon.Native.WeaponGearCatalogue::Withdraw"
    };

    internal static bool ReviewedContentWriteSitesAreExact(IEnumerable<string> sites)
        => sites.OrderBy(site => site, StringComparer.Ordinal)
            .SequenceEqual(ReviewedContentWriteSites.OrderBy(site => site, StringComparer.Ordinal), StringComparer.Ordinal);

    // Private, unregistered block copies retain identity, localized text and animation list references.
    // These are clone writes, never permission to mutate a shared definition or a live observation.
    internal static readonly string[] ReviewedPrivateCopyWriteSites =
    {
        "GameData.ArchetypeDataBlock::set_AimSequence <- ForgeWeapon.Native.WeaponOverrideApplier::CloneBlock",
        "GameData.ArchetypeDataBlock::set_Description <- ForgeWeapon.Native.WeaponOverrideApplier::CloneBlock",
        "GameData.ArchetypeDataBlock::set_EquipSequence <- ForgeWeapon.Native.WeaponOverrideApplier::CloneBlock",
        "GameData.ArchetypeDataBlock::set_PublicName <- ForgeWeapon.Native.WeaponOverrideApplier::CloneBlock",
        "GameData.GameDataBlockBase`1::set_persistentID <- ForgeWeapon.Native.WeaponOverrideApplier::CloneBlock",
        "GameData.GameDataBlockBase`1::set_persistentID <- ForgeWeapon.Native.WeaponOverrideApplier::CloneRecoil",
    };

    internal static bool ReviewedPrivateCopyWriteSitesAreExact(IEnumerable<string> sites)
        => sites.OrderBy(site => site, StringComparer.Ordinal)
            .SequenceEqual(ReviewedPrivateCopyWriteSites.OrderBy(site => site, StringComparer.Ordinal), StringComparer.Ordinal);

    // H10 pin evidence (h10-sentry-bodies-20403457.json): Unload owns the one decrement.
    // ReleasePin checks the exact receipt/generation, live clone retirement and count > 1 before this call.
    // Dictionary reads include the three shard tables and the content catalogue collision check.
    // Catalogue copy writes are checked independently by ReviewedContentWriteSites.
    // The shard tables are read, and the native load, pin and unload are called, only by ForgeCore.GameAssets; these are Weapon's
    // call sites into it.
    internal static readonly string[] ReviewedPinCallSites =
    {
        "ForgeCore.GameAssets::IncreaseShardDependency <- ForgeWeapon.Native.GearPartExtraAssets::AcquireLoadedPin",
        "ForgeCore.GameAssets::IncreaseShardDependency <- ForgeWeapon.Native.GearPartExtraAssets::ConfirmStartedLoad",
        "ForgeCore.GameAssets::LoadShard <- ForgeWeapon.Native.GearPartExtraAssets::BeginLoad",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::AcquireLoadedPin",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::BeginLoad",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::ConfirmStartedLoad",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::ConfirmStartedLoad",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::Place",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::ReleasePin",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::Resolve",
        "ForgeCore.GameAssets::ReadShard <- ForgeWeapon.Native.GearPartExtraAssets::SettleForeign",
        "ForgeCore.GameAssets::UnloadShard <- ForgeWeapon.Native.GearPartExtraAssets::ReleasePin",
        "Il2CppSystem.Collections.Generic.Dictionary`2::ContainsKey <- ForgeWeapon.Native.WeaponContentReadback/<>c__DisplayClass0_0::<Read>b__3",
        "Il2CppSystem.Collections.Generic.Dictionary`2::ContainsKey <- ForgeWeapon.Native.WeaponGearCatalogue::Register",
        "Il2CppSystem.Collections.Generic.Dictionary`2::ContainsKey <- ForgeWeapon.Native.WeaponGearCatalogue::State"
    };

    internal static bool ReviewedPinCallSitesAreExact(IEnumerable<string> sites)
        => sites.OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(ReviewedPinCallSites, StringComparer.Ordinal);

    // The destruction capture (the before of the sentry-lifecycle subscription) reads the native holder for Forge-owned resource retirement, and the thermal sight reads it to find the sight of the gun just wielded.
    // Pin ownership still comes from GearPartExtraAssets, never from the holder getter itself.
    internal static readonly string[] ReviewedSentryRetirementCallSites =
    {
        "ItemEquippable::get_GearPartHolder <- ForgeWeapon.Native.SentryResourceLifetime/<>c__DisplayClass2_0::<CaptureHolder>b__0",
        "ItemEquippable::get_GearPartHolder <- ForgeWeapon.Native.ThermalSightApplier::Wielded"
    };

    internal static bool ReviewedSentryRetirementCallSitesAreExact(IEnumerable<string> sites)
        => sites.SequenceEqual(ReviewedSentryRetirementCallSites, StringComparer.Ordinal);

    internal static bool SentryResourceContributionIsExact(IEnumerable<string> instructions)
        => instructions.SequenceEqual(new[]
        {
            "ldarg.0", "ldtoken ForgeWeapon.Native.ShardDependencyResetObserved",
            "call System.Type::GetTypeFromHandle", "callvirt System.Collections.Generic.List`1<System.Type>::Add", "ret"
        }, StringComparer.Ordinal);

    // The destruction capture is not a Harmony class: it is one Around subscription to ForgeCore's sentry-lifecycle bracket, whose
    // before captures the holder, whose after and faulted run the one-shot retirement, and which has no way to skip the native body
    // or replace its exception. The audit pins every link: the entry the package subscribes through, the single Around call, the three
    // delegates in the order before/after/faulted, and the capture reading the step before it reads anything else.
    internal static bool SentrySubscriptionIsExact(IEnumerable<string> entryCalls, IEnumerable<string> bracketCalls,
        IEnumerable<string> delegateTargets, IEnumerable<string> captureSignature, IEnumerable<string> completionSignatures)
        => entryCalls.SequenceEqual(new[] { "ForgeWeapon.Native.SentryResourceLifetime::Subscribe" }, StringComparer.Ordinal)
            && bracketCalls.SequenceEqual(new[] { "ForgeCore.Bracket`1<ForgeCore.SentryCall>::Around" }, StringComparer.Ordinal)
            && delegateTargets.SequenceEqual(new[]
            {
                "ForgeWeapon.Native.SentryResourceLifetime::Capture", "ForgeWeapon.Native.SentryResourceLifetime::Retire",
                "ForgeWeapon.Native.SentryResourceLifetime::RetireAfterFault"
            }, StringComparer.Ordinal)
            && captureSignature.SequenceEqual(new[]
            {
                "System.Action(ForgeCore.SentryCall)", "first-call=ForgeCore.SentryCall::get_Step", "first-constant=4"
            }, StringComparer.Ordinal)
            && completionSignatures.SequenceEqual(new[]
            {
                "System.Void(ForgeCore.SentryCall,System.Action,System.Boolean)",
                "System.Void(ForgeCore.SentryCall,System.Action,System.Exception)"
            }, StringComparer.Ordinal);

    internal static bool ShardResetObservationSignaturesAreExact(
        IEnumerable<string> prefix, IEnumerable<string> postfix)
        => prefix.SequenceEqual(new[] { "System.String shardName" }, StringComparer.Ordinal)
            && postfix.SequenceEqual(new[] { "System.String shardName", "System.Boolean __runOriginal" }, StringComparer.Ordinal);

    internal static bool ThrowObservationPriorityIsExact(int? prefix, int? postfix)
        => prefix == null && postfix == 0; // Native request snapshot uses Harmony's default; observation is Last.

    private static string Artifact(string relative) => Path.Combine(Artifacts, relative);

    private static void RequireInputs()
    {
        Assert.False(string.IsNullOrWhiteSpace(BepInEx) || string.IsNullOrWhiteSpace(Artifacts),
            "Set FORGE_WEAPON_BEPINEX (or GTFO_BEPINEX_PATH) and FORGE_ARTIFACTS to an existing build output.");
    }

    [Fact]
    public void fire_noise_allocates_its_native_box_before_filling_the_request()
    {
        RequireInputs();
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"));
        var noise = game.MainModule.GetType("NM_NoiseData");
        var allocate = noise.Methods.Single(method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        Assert.Contains(allocate.Body.Instructions, instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == "Il2CppInterop.Runtime.IL2CPP" && call.Name == "il2cpp_value_box");
        using var native = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeEngine/release/ForgeEngine.dll"));
        var emit = native.MainModule.GetType("ForgeWeapon.Native.WeaponFireNoise").Methods.Single(method => method.Name == "Emit");
        // The weapon submits the request through ForgeCore's one noise entry and builds no native value itself.
        var emitted = emit.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
        Assert.Single(emitted, call => call.DeclaringType.FullName == "ForgeCore.Combat" && call.Name == "Noise" && call.Parameters.Count == 9);
        Assert.DoesNotContain(emitted, call => call.DeclaringType.FullName is "NM_NoiseData" or "NoiseManager");
        // The gun's noise keeps the sentry shot's shape: no noise maker (the maker argument, the last one pushed, is null).
        var submit = emit.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call
            && call.DeclaringType.FullName == "ForgeCore.Combat" && call.Name == "Noise");
        Assert.Equal(OpCodes.Ldnull, submit.Previous.OpCode);
        // ForgeCore's entry is where the native box is allocated (parameterless constructor) before its nine fields are filled.
        using var core = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeCore/release/ForgeCore.dll"));
        var entry = core.MainModule.GetType("ForgeCore.Combat").Methods.Single(method => method.Name == "Noise" && method.Parameters.Count == 9);
        Assert.Equal("Player.PlayerAgent", entry.Parameters[8].ParameterType.FullName);
        var calls = entry.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>().ToList();
        var constructor = Assert.Single(calls, call => call.DeclaringType.FullName == "NM_NoiseData" && call.Name == ".ctor");
        Assert.Empty(constructor.Parameters);
        var setters = calls.Where(call => call.DeclaringType.FullName == "NM_NoiseData" && call.Name.StartsWith("set_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(9, setters.Length);
        Assert.True(setters.All(setter => calls.IndexOf(setter) > calls.IndexOf(constructor)), "a field is written before the native box exists");
        // The maker field is written once, as the game's interface type wrapping the player's own native object (or null).
        Assert.Single(setters, setter => setter.Name == "set_noiseMaker");
        var wrap = Assert.Single(calls, call => call.DeclaringType.FullName == "INM_NoiseMaker" && call.Name == ".ctor");
        Assert.Equal("System.IntPtr", Assert.Single(wrap.Parameters).ParameterType.FullName);
        Assert.Single(calls, call => call.DeclaringType.FullName == "NoiseManager" && call.Name == "MakeNoise");
    }

    [Fact]
    public void weapon_native_layout_checks_all_pass()
    {
        RequireInputs();
        var checks = Audit();
        string failed = string.Join(" | ", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail));
        Assert.True(checks.Any(c => c.Id == "native.no-update-loop"), "The audit did not run to completion: " + failed);
        Assert.True(checks.Count(c => !c.Passed) == 0, failed);
    }

    [Fact]
    public void weapon_reads_both_host_publication_and_scoped_owner_gates()
    {
        RequireInputs();
        var checks = Audit();
        Assert.True(checks.Single(row => row.Id == "weapon.gameplay-gate-from-host").Passed);
        Assert.True(checks.Single(row => row.Id == "weapon.owner-gate-from-runtime").Passed);
    }

    [Fact]
    public void loadout_uses_only_current_offers_and_the_original_equip_entry()
    {
        RequireInputs();
        var checks = Audit();
        var ids = new[] { "native.loadout-no-shared-write", "native.loadout-no-loader-takeover",
            "native.loadout-transfer-contribution-exact" };
        foreach (var id in ids)
        {
            var row = Assert.Single(checks, row => row.Id == id);
            Assert.True(row.Passed, row.Id + ": " + row.Detail);
        }
    }

    private static List<CheckRow> Audit()
    {
        var checks = new List<CheckRow>();
        void Check(string id, bool passed, string detail = "") => checks.Add(new(id, passed, detail));
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        // The package assemblies reference the loader's Harmony and the interop game assemblies, and Cecil resolves
        // a reference out of the directory of the assembly it is reading unless it is told where else to look. The
        // one place those live is the BepInEx installation this test is already given, so it is handed to it.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(BepInEx, "core"));
        resolver.AddSearchDirectory(Path.Combine(BepInEx, "interop"));
        var reader = new ReaderParameters { AssemblyResolver = resolver };
        using var sdk = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeEngine.Framework/release/ForgeEngine.Framework.dll"), reader);
        using var host = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeEngine/release/ForgeEngine.dll"), reader);
        using var modules = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Modules-ASM.dll"), reader);
        using var shards = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "Shards-ASM.dll"), reader);
        using var snet = AssemblyDefinition.ReadAssembly(Path.Combine(BepInEx, "interop", "SNet_ASM.dll"), reader);
        using var specDocument = JsonDocument.Parse(File.ReadAllText(Spec));
        var specHooks = specDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        using var loadoutDocument = JsonDocument.Parse(File.ReadAllText(LoadoutSpec));
        var loadoutHooks = loadoutDocument.RootElement.GetProperty("hooks").EnumerateArray().ToArray();
        var packageTypes = ForgeTestInputs.DomainTypes.Native(host, sdk, "Weapon");
        var nativeTypes = packageTypes;
        var gameTypes = modules.MainModule.Types.Concat(shards.MainModule.Types).Concat(snet.MainModule.Types).SelectMany(Walk).ToArray();
        bool GameAssembly(string name) => name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("BepInEx", StringComparison.Ordinal)
            || name.Contains("Harmony", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal) || name.EndsWith("-ASM", StringComparison.Ordinal) || name.EndsWith("_ASM", StringComparison.Ordinal);
        var instructions = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        var calls = instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        string Scope(MethodReference m) => m.DeclaringType.Scope is AssemblyNameReference scope ? scope.Name : "";
        string Member(MethodReference m) => (m.DeclaringType is GenericInstanceType g ? g.ElementType.FullName : m.DeclaringType.FullName) + "::" + m.Name;

        Check("native.single-session-over-weapon-identity", nativeTypes.Count(t => t.Name == "WeaponNativeSession") == 1
            && !nativeTypes.Any(t => t.Name is "EquipmentIdentitySession" or "EquipmentIdentityIndex"));
        Check("native.no-update-loop", !nativeTypes.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "FixedUpdate" or "LateUpdate"));

        // Owners come only from Runtime's shared player-directory route: no kernel lookup of the player kind by its
        // string, no injected or cached player identity, no account key. A method that both loads the player kind's
        // string and calls the kernel's instance lookup is the string-keyed path this package no longer has.
        var stringPlayerLookups = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody
                && m.Body.Instructions.Any(i => i.Operand is MethodReference r && Member(r) is
                    "ForgeEngine.Framework.IForgeEngine::ResolveEntityInstance" or "ForgeEngine.Framework.RuntimeKernel::ResolveEntityInstance")
                && m.Body.Instructions.Any(i => i.Operand is string s && s == "gtfo.player"))
            .Select(m => m.DeclaringType.Name + "::" + m.Name).ToArray();
        Check("native.owner-through-player-directory", calls.Any(m => Member(m) == "ForgePlayer.Native.PlayerDirectory::ReferenceOf")
            && calls.Any(m => Member(m) == "ForgeEngine.Framework.IForgeEngine::IsEntityCurrent")
            && stringPlayerLookups.Length == 0
            && !nativeTypes.Any(t => t.Name == "WeaponPlayerReferences")
            && !calls.Any(m => m.Name is "set_EntityResolvers" or "set_EntityInstanceResolvers" or "set_EntityObservers"),
            string.Join(", ", stringPlayerLookups));
        // Session addresses come from IPlayerDirectory; the owner channel's transport key is ForgePlayer's to read, so no native lookup key or slot index is read here.
        var lookups = calls.Where(m => m.Name is "get_Lookup" or "PlayerSlotIndex").Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.session-only-lookup", lookups.Length == 0, string.Join(", ", lookups));

        // Native state is read, never written, outside the exact reviewed write scopes: every other game member
        // is a getter or named pure query. Presentation, owner commands, instance state, private copies, content
        // admission and authoring tests each have an exact member or call-site check below; unused entries fail.
        var gameCallReferences = calls.Where(m => GameAssembly(Scope(m)) && !Scope(m).StartsWith("BepInEx", StringComparison.Ordinal)
                && !Scope(m).Contains("Harmony", StringComparison.Ordinal)).ToArray();
        var gameCalls = gameCallReferences.Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // `GetBaseAgent` and `GetComponentInParent` are the two named pure queries the hit path adds: the first is
        // the game's own damage-limb owner accessor, the second is Unity's hierarchy lookup, and neither writes.
        // `Find` is Unity's own child lookup and `Vector3::.ctor` builds the value the pose is written as; a value
        // type's constructor changes nothing that outlives the call, so neither is a write. `GetBlock` and
        // `get_ItemDataBlock` are the supply catalog resolving an ammo item's block by id; `CountPocketItem`,
        // `GetAmmoMaxCap` and `GetBulletsInPack` are the backpack readbacks a supply or give command is decided on.
        // `GetCurrentClip`, `GetMaxClip`, `GetInventorySlotAmmo`,
        // `pAgent::TryGet` and the reload gate's own `CanReloadCurrent` are the reload, device and holder families'
        // named pure queries: each answers a value and writes nothing.
        string[] queries = { "Linecast", "TryGetBackpack", "IsDeployed", "GetChecksum", "GetCompID", "TryCast", "GetBaseAgent", "GetComponentInParent",
            "Find", ".ctor", "op_Equality", "PlayerCanInteract", "GetComponentInChildren", "op_Inequality", "op_Implicit", "GetBlock", "GetAllGearForSlot", "CountPocketItem", "GetAmmoMaxCap",
            "GetBulletsInPack", "GetAmmoInPack", "GetCurrentClip", "GetMaxClip", "GetInventorySlotAmmo", "GetEquippedHackingTool", "TryGet", "CanReloadCurrent",
            // Whether a data block row exists, asked before a flashlight part is pointed at a row. It writes nothing.
            "HasBlock",
            // The limb's damage-modifier rule asked without running it: the side-effect-free twin of the one a shot runs,
            // which returns the damage a shot would leave and writes nothing. The derived aim readback asks it per limb.
            "TestDamageModifiers",
            // The ranged archetype's own charge-up delay: the question the combat fact asks about how long a full
            // charge takes, next to the two fields it reads the elapsed and current state from. It writes nothing.
            "ChargeupDelay",
            // The added-part spawner's lookups: a part model already loaded, the shard name its block names, the
            // part data block's entry list walked for a tool part's type, and the copy's own children and
            // components walked while it is stripped. Each answers a value and writes nothing.
            "GetLoadedAsset", "GetShardName", "GetEnumerator", "MoveNext", "GetComponentsInChildren", "GetChild",
            // The hitscan row's own ray cast: it fills the by-value hit data the caller built and writes nothing
            // that outlives the call, which is the same shape as the value-type constructor above.
            "CastWeaponRay",
            // The shot rule's and the weapon projectile's own questions: the course portal's other side, the
            // component a spawned body carries, and the value-type vector and rotation arithmetic. Each answers a
            // value and writes nothing the game reads.
            "GetOppositeNode", "CalcThrust", "GetComponent", "LookRotation", "Reflect", "RotateTowards", "Cross",
            "op_Addition", "op_Division", "op_Multiply", "op_Subtraction",
            // Item appearance resolves a root-relative visual pose and skips nested mesh branches.
            "GetColumn", "TRS", "Euler", "IsChildOf",
            // Gear-copy serialization, interaction identity and the current expedition address are reads.
            "ToJSON", "GetInstanceID", "GetActiveExpeditionData",
            // The equip port enumerates the offline gear blocks, and the interop's `Cast` views the returned
            // replicator as its interface. Capture diagnostics read the replicator's spawn snapshot. Each answers a value.
            "GetAllBlocks", "Cast", "GetSpawnData",
            // The picked_up stimulus reads before it writes: the item data the game keeps for a held item, whether the item has a pickup prefab,
            // the world copy of a dropped item and its sync component. Each answers a value and writes nothing.
            "Get_pItemData", "ItemHasMode", "TryGetItemInLevelFromItemData", "GetSyncComponent", "GetCustomData" };
        // The one presentation write whitelist: a gear part's absolute local pose and whether that part is shown, and
        // the added parts hung beside them — a part model's asset shard loaded and pinned, a copy instantiated under
        // an inactive staging object, stripped to its renderers, named, layered, parented, given the gear's materials
        // and replaced on the next spawn. Nothing outside the whitelists in this file may write native state, and an
        // unexpected setter still fails below. The pose writes are exercised by tests/NativeAdapter, which asserts
        // the written values; the added-part writes are native-only and verified by this metadata audit alone.
        // The thermal sight also writes the material and gradient made for the local first-person gun (presentation, below); the lens copy
        // itself and its template's shard load are ForgeCore SightLenses. These calls of its gradient only compute (Native/Thermal/ThermalSight.cs).
        string[] thermalPureCalls = { "UnityEngine.Color::Lerp", "UnityEngine.ColorUtility::ToHtmlStringRGB", "UnityEngine.Mathf::Clamp01", "UnityEngine.Mathf::InverseLerp", "UnityEngine.Mathf::SmoothStep", "UnityEngine.Shader::PropertyToID" };
        string[] presentationWrites =
        {
            "Gear.GearPartSpawner::UpdateMaterials",
            // The authored Forge block fields that are pure local presentation: the mine's beam and light and the
            // sentry's scanner plane colours (scanning and target), written on the instance the game just built
            // from the document (evidence/w8-forge-field-apply.json).
            "SentryGunInstance_ScannerVisuals_Plane::set_m_hasTargetColor",
            "SentryGunInstance_ScannerVisuals_Plane::set_m_scanningColor",
            "UnityEngine.GameObject::SetActive",
            "UnityEngine.Light::set_color",
            "UnityEngine.Light::set_intensity",
            "UnityEngine.LineRenderer::set_endColor",
            "UnityEngine.LineRenderer::set_endWidth",
            "UnityEngine.LineRenderer::set_startColor",
            "UnityEngine.LineRenderer::set_startWidth",
            "UnityEngine.Material::SetTexture",
            "UnityEngine.Object::DestroyImmediate",
            "UnityEngine.Object::DontDestroyOnLoad",
            "UnityEngine.Object::Instantiate",
            "UnityEngine.Object::set_hideFlags",
            "UnityEngine.Object::set_name",
            "UnityEngine.Renderer::set_enabled",
            "UnityEngine.Texture2D::Apply",
            "UnityEngine.Texture2D::SetPixel",
            "UnityEngine.Texture::set_filterMode",
            "UnityEngine.Texture::set_wrapMode",
            "UnityEngine.Transform::SetParent",
            "UnityEngine.Transform::set_localEulerAngles",
            "UnityEngine.Transform::set_localPosition",
            "UnityEngine.Transform::set_localRotation",
            "UnityEngine.Transform::set_localScale"
        };
        // The authored Forge block fields whose native member is local state rather than presentation: what the
        // weapon, mine, sentry and tool instance does on the machine that owns it — the scanner's cone, ranges
        // and delays, the mine's payload numbers, the sentry's scan delay and the pickup timing, the thrown fog
        // repeller's sphere radius, the lock melter's burn time and the hack hold
        // time on a lock's hack interaction when this machine's player selects it. Every one is written on the
        // instance the game just built (or just selected), from the same document on every peer, and none of them
        // has a replication channel (evidence/w8-forge-field-apply.json); the fields the research marked
        // `host-replicated` are not in any applier's vocabulary.
        string[] localStateWrites =
        {
            // Fills a fresh handle the client report reads an agent's replicated key from (EquipmentNativeAdapter.KeyOf).
            "Agents.pAgent::Set",
            "FogRepeller_Sphere::set_Range",
            "Gear.EnemyScanner::set_m_maxScanWorldRadius",
            "Gear.EnemyScanner::set_m_pulseDuration",
            "Gear.EnemyScanner::set_m_rechargeDuration",
            "Gear.EnemyScanner::set_m_scanConeDotMin",
            "Gear.EnemyScanner::set_m_scanDelay",
            "Gear.EnemyScanner::set_s_maxCourseNodeDistance",
            "Gear.EnemyScanner::set_s_tagDuration",
            // The flashlight row one gear's own spawned flashlight part reads on wield: the part prefab's serialized
            // reference, pointed at the row the gear's document names (evidence/flashlight-settings.json).
            "Gear.GearPartFlashlight::set_m_settingsID",
            "GlueGun::set_m_burstShotDelay",
            "GlueGun::set_m_burstShotsMax",
            "GlueGun::set_m_burstShotsMin",
            "GlueGun::set_m_fireDelayMax",
            "GlueGun::set_m_fireDelayMin",
            "GlueGun::set_m_forceSizeMax",
            "GlueGun::set_m_forceSizeMin",
            "GlueGun::set_m_pressureProgressToFire",
            "GlueGun::set_m_spreadMax",
            "GlueGun::set_m_spreadMin",
            "GlueGun::set_m_timeToDepleatePressure",
            "GlueGun::set_m_timeToMaxpressure",
            "Interact_Timed::set_InteractDuration",
            "LockMelterFirstPerson::set_m_interactionDuration",
            "MineDeployerFirstPerson::set_m_deployPickupInteractionDuration",
            "MineDeployerFirstPerson::set_m_timeBetweenPlacements",
            "MineDeployerInstance_Detect_Laser::set_m_maxLineDistance",
            "MineDeployerInstance_Detonate_Explosive::set_m_damageMax",
            "MineDeployerInstance_Detonate_Explosive::set_m_damageMin",
            "MineDeployerInstance_Detonate_Explosive::set_m_delay",
            "MineDeployerInstance_Detonate_Explosive::set_m_distanceMax",
            "MineDeployerInstance_Detonate_Explosive::set_m_distanceMin",
            "MineDeployerInstance_Detonate_Explosive::set_m_explosionForce",
            "MineDeployerInstance_Detonate_Explosive::set_m_radius",
            "MineDeployerInstance_Detonate_Glue::set_m_delay",
            "MineDeployerInstance_Detonate_Glue::set_m_explosionDelay",
            "MineDeployerInstance_Detonate_Glue::set_m_projAngMinMax",
            "MineDeployerInstance_Detonate_Glue::set_m_projCount",
            "MineDeployerInstance_Detonate_Glue::set_m_projVelMinMax",
            "SentryGunFirstPerson::set_m_deployPickupInteractionDuration",
            "SentryGunInstance::set_m_initialScanDelay",
        };
        // The commanded writes: the executed half of an owner-tier request, each submitted through the game's own
        // entry point. A reload the holder's own machine starts through the very body the player's reload key
        // reaches, and the magazine the clip-set action writes (`tests/WeaponFacts`, `tests/ReloadInventoryFacts`);
        // the bullets the supply action moves in the recipient's own pack, and the item the give and consume
        // actions add and remove through the game's pickup/removal entries, reading the slot back before a commit
        // is reported (`tests/SupplyFacts`, `tests/InventoryActions`). The tag command left this package with the
        // enemy domain that now owns it.
        string[] commandWrites =
        {
            // The whole-profile row's three end-of-state transitions: the burst counters the burst archetypes
            // advance, and the archetype's own two end-of-run bodies. Each is reached only after the profile swap
            // was confirmed, and only when the plan's own structural switch asked for it.
            "Gear.BWA_Burst::set_m_burstCurrentCount",
            "Gear.BWA_SemiBurst::ResetBurstCount",
            // The hitscan row's own submission: the game's static hit body, entered with the by-value hit data the
            // row built. It is the same entry every weapon `Fire` body calls, and it is what turns one ray into a
            // hit — damage, limbs and cooldowns are the game's own from there on.
            "Gear.BulletWeapon::BulletHit",
            "Gear.BulletWeapon::set_m_burstCurrentCount",
            "Gear.BulletWeaponArchetype::OnStopChargeup",
            "Gear.BulletWeaponArchetype::OnStopFiring",
            // The complete native input request, including charge/rate/ammunition gates, runs the original
            // archetype Update. No caller bypasses it by directly invoking the low-level Fire body.
            "Gear.BulletWeaponArchetype::Update",
            "ItemEquippable::SetCurrentClip",
            // inventory-actions.json: every give creates one native pickup through the replication manager, then
            // submits that exact pickup through the game's complete interaction entry. A spawn whose sync component
            // cannot be read is despawned (through ForgeCore) before any pickup request is submitted.
            "LevelGeneration.LG_PickupItem_Sync::AttemptPickupInteraction",
            "LevelGeneration.LG_PickupItem_Sync::SetCustomData",
            // The detonation row's own write: the placed mine's item action (Trigger) for its placer, requested only
            // after the device resolved to a current placement of this provider. The readback that decides the commit
            // is the payload's own state, which is a read and not a write.
            "MineDeployerInstance::WantItemAction",
            "Player.PlayerAmmoStorage::UpdateBulletsInPack",
            "Player.PlayerAmmoStorage::UpdateSlotAmmoUI",
            // equip_gear: the game's own complete equip request (the host's bot variant for a bot recipient; this machine's own player goes through ForgeCore.Gear.EquipLocal); it derives the slot from the gear's base item and assembles
            // the gear itself, so no slot, ammunition or part is written here (evidence/w7-loadout-policy.json).
            "Player.PlayerBackpackManager::EquipBotGear",
            "Player.PlayerBackpackManager::GiveAmmoToPlayer",
            // inventory-actions.json: ConsumeSlotted removes the addressed instance through the host entry.
            "Player.PlayerBackpackManager::MasterRemoveItem",
            // ammo_add on the pool's owner: the gift's own receive half (evidence/weapon-supply.json ownerSettle).
            "Player.PlayerBackpackManager::ReceiveAmmoGive",
            "Player.PlayerBackpackManager::TryMasterRemovePocketItemWithID",
            "PlayerInventoryBase::TriggerReload",
            // The launch row's own submission: one game projectile requested through the manager's complete static entry, on the
            // host only, from the typed spawner both launch paths share.
            "ProjectileManager::WantToFireTargeting",
            // The addressee of that gift, filled by the game's own pPlayer setter on the by-value gift.
            "SNetwork.SNetStructs/pPlayer::SetPlayer",
            // The extended flight owns only a pure presentation copy. Its pose and destruction never modify a
            // native projectile's behaviour or collider. The remaining behaviour switch belongs to the bio-tracker:
            // the local scanner's own `enabled`,
            // which the game's `OnWield`/`OnUnWield` themselves write (RVA 0x1467A20 / 0x1467970).
            "UnityEngine.Behaviour::set_enabled",
            "UnityEngine.Object::Destroy",
            // The shot rule's pierced hit: the by-value ray hit of the cast it answers, its distance set to the
            // length the line really travelled, written back onto the caller's own by-reference hit data.
            "UnityEngine.RaycastHit::set_distance",
            "UnityEngine.Transform::set_position",
            "UnityEngine.Transform::set_rotation",
            // The hitscan row builds one by-value hit data per ray. These setters write only that local value —
            // nothing that outlives the call — and the fields are the game's own, so the shot is described to the
            // native hit body in the vocabulary that body already reads.
            "Weapon/WeaponHitData::set_angOffsetX",
            "Weapon/WeaponHitData::set_angOffsetY",
            "Weapon/WeaponHitData::set_damage",
            "Weapon/WeaponHitData::set_damageFalloff",
            "Weapon/WeaponHitData::set_fireAtPos",
            "Weapon/WeaponHitData::set_fireDir",
            "Weapon/WeaponHitData::set_hasFireDir",
            "Weapon/WeaponHitData::set_maxRayDist",
            "Weapon/WeaponHitData::set_owner",
            "Weapon/WeaponHitData::set_randomSpread",
            "Weapon/WeaponHitData::set_rayHit",
            // The debug-switch row's own three statics, written only when this machine is the authority the
            // switch belongs to and read back afterwards as the write's only proof.
            "Weapon::set_InfiniteAmmo",
            "Weapon::set_RecoilOn",
            "Weapon::set_SuperWeapons",
            // The sentry recall row's write: the replicated pickup for the placer.
            "iSentrygunInstance_Sync::WantItemAction",
        };
        // The override applier's own copy, and the only place this package writes a game data block: the interop
        // helpers below look up the block's constructor, allocate one and invoke it, then the reviewed fields are
        // filled in one at a time, and the archetype setters swap that private copy onto the weapon the player is
        // holding. Nothing else may be written; `tests/WeaponOverride` asserts every applied value and the restore.
        string[] overrideWrites = new[]
        {
            // The reload sequence bound: trailing items of a reload sequence are held at the sequence's own end marker.
            "GameData.WeaponAnimSequenceItem::set_TriggerTime",
            "GameData.ArchetypeDataBlock::set_AimSpread",
            "GameData.ArchetypeDataBlock::set_AimTransitionTime",
            "GameData.ArchetypeDataBlock::set_BurstDelay",
            "GameData.ArchetypeDataBlock::set_BurstShotCount",
            "GameData.ArchetypeDataBlock::set_CostOfBullet",
            "GameData.ArchetypeDataBlock::set_Damage",
            "GameData.ArchetypeDataBlock::set_DamageBoosterEffect",
            "GameData.ArchetypeDataBlock::set_DamageFalloff",
            "GameData.ArchetypeDataBlock::set_DefaultClipSize",
            "GameData.ArchetypeDataBlock::set_DefaultReloadTime",
            "GameData.ArchetypeDataBlock::set_EquipTransitionTime",
            "GameData.ArchetypeDataBlock::set_FireMode",
            "GameData.ArchetypeDataBlock::set_HipFireSpread",
            "GameData.ArchetypeDataBlock::set_PiercingBullets",
            "GameData.ArchetypeDataBlock::set_PiercingDamageCountLimit",
            "GameData.ArchetypeDataBlock::set_PrecisionDamageMulti",
            "GameData.ArchetypeDataBlock::set_RecoilDataID",
            "GameData.ArchetypeDataBlock::set_Sentry_CostOfBulletTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_DamageTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_DetectionMaxAngle",
            "GameData.ArchetypeDataBlock::set_Sentry_DetectionMaxRange",
            "GameData.ArchetypeDataBlock::set_Sentry_FireTagOnly",
            "GameData.ArchetypeDataBlock::set_Sentry_FireTowardsTargetInsteadOfForward",
            "GameData.ArchetypeDataBlock::set_Sentry_ForceAimTowardsBody",
            "GameData.ArchetypeDataBlock::set_Sentry_LegacyEnemyDetection",
            "GameData.ArchetypeDataBlock::set_Sentry_LongRangeThreshold",
            "GameData.ArchetypeDataBlock::set_Sentry_PrioTag",
            "GameData.ArchetypeDataBlock::set_Sentry_RotationSpeed",
            "GameData.ArchetypeDataBlock::set_Sentry_RotationSpeedTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_ShortRangeThreshold",
            "GameData.ArchetypeDataBlock::set_Sentry_ShotDelayTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_StaggerDamageTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_StartFireDelay",
            "GameData.ArchetypeDataBlock::set_Sentry_StartFireDelayTagMulti",
            "GameData.ArchetypeDataBlock::set_ShellCasingSize",
            "GameData.ArchetypeDataBlock::set_ShellCasingSpeedRange",
            "GameData.ArchetypeDataBlock::set_ShotDelay",
            "GameData.ArchetypeDataBlock::set_ShotgunBulletCount",
            "GameData.ArchetypeDataBlock::set_ShotgunBulletSpread",
            "GameData.ArchetypeDataBlock::set_ShotgunConeSize",
            "GameData.ArchetypeDataBlock::set_SpecialChargetupTime",
            "GameData.ArchetypeDataBlock::set_SpecialCooldownTime",
            "GameData.ArchetypeDataBlock::set_SpecialSemiBurstCountTimeout",
            "GameData.ArchetypeDataBlock::set_StaggerDamageMulti",
            "GameData.MinMaxValue::set_Max",
            "GameData.MinMaxValue::set_Min",
            "GameData.RecoilDataBlock::set_concussionDuration",
            "GameData.RecoilDataBlock::set_concussionFrequency",
            "GameData.RecoilDataBlock::set_concussionIntensity",
            "GameData.RecoilDataBlock::set_dampening",
            "GameData.RecoilDataBlock::set_directionalSimilarity",
            "GameData.RecoilDataBlock::set_hipFireCrosshairRecoilPop",
            "GameData.RecoilDataBlock::set_hipFireCrosshairSizeDefault",
            "GameData.RecoilDataBlock::set_hipFireCrosshairSizeMax",
            "GameData.RecoilDataBlock::set_horizontalScale",
            "GameData.RecoilDataBlock::set_power",
            "GameData.RecoilDataBlock::set_recoilAimingWeight",
            "GameData.RecoilDataBlock::set_recoilCameraPosWeight",
            "GameData.RecoilDataBlock::set_recoilCameraRotWeight",
            "GameData.RecoilDataBlock::set_recoilPosDamping",
            "GameData.RecoilDataBlock::set_recoilPosImpulse",
            "GameData.RecoilDataBlock::set_recoilPosImpulseWeight",
            "GameData.RecoilDataBlock::set_recoilPosShift",
            "GameData.RecoilDataBlock::set_recoilPosShiftWeight",
            "GameData.RecoilDataBlock::set_recoilPosStiffness",
            "GameData.RecoilDataBlock::set_recoilRotDamping",
            "GameData.RecoilDataBlock::set_recoilRotImpulse",
            "GameData.RecoilDataBlock::set_recoilRotImpulseWeight",
            "GameData.RecoilDataBlock::set_recoilRotStiffness",
            "GameData.RecoilDataBlock::set_spring",
            "GameData.RecoilDataBlock::set_verticalScale",
            "GameData.RecoilDataBlock::set_worldToViewSpaceBlendHorizontal",
            "GameData.RecoilDataBlock::set_worldToViewSpaceBlendVertical",
            "Gear.BWA_Burst::set_m_burstMax",
            "Gear.BWA_SemiBurst::set_m_burstMax",
            "Gear.BulletWeapon::set_m_burstMax",
            "Gear.BulletWeaponArchetype::set_m_archetypeData",
            "Gear.BulletWeaponArchetype::set_m_recoilData",
            // Only the addressed weapon's active charge state; ApplyMelee writes and WithdrawMelee
            // conditionally restores these caches using the elapsed time and melee speed modifier.
            "Gear.MWS_ChargeUp::set_m_chargeUpDone",
            "Gear.MWS_ChargeUp::set_m_maxDamageTime",
            "Il2CppInterop.Runtime.IL2CPP::GetIl2CppMethod",
            "Il2CppInterop.Runtime.IL2CPP::il2cpp_object_new",
            "Il2CppInterop.Runtime.IL2CPP::il2cpp_runtime_invoke",
            // Install mounts the private reload block; ApplyMelee mounts the private animation block.
            // Both accessors are declared on ItemEquippable, not the derived weapon classes.
            "ItemEquippable::set_ArchetypeData",
            "ItemEquippable::set_MeleeAnimationData"
        }.Concat(MeleeCopyWrites).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var contentWriteMembers = ReviewedContentWriteSites.Select(site => site.Split(" <- ", StringSplitOptions.None)[0])
            .ToHashSet(StringComparer.Ordinal);
        var copyWriteMembers = ReviewedPrivateCopyWriteSites.Select(site => site.Split(" <- ", StringSplitOptions.None)[0])
            .ToHashSet(StringComparer.Ordinal);
        var writes = gameCallReferences.Select(Member)
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
            .Where(c =>
            {
                var name = c[(c.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
                return !name.StartsWith("get_", StringComparison.Ordinal) && !queries.Contains(name)
                    // The producer callback's real item accessor (native-runtime ISyncedItem.GetItem / dump.cs 582839).
                    && c != "ISyncedItem::GetItem"
                    // item-use-failures.json: a read of the local fresh-press state, not an input injection.
                    // The game's own class name of an item, read only to name an unsupported consumable family.
                    && c != "Il2CppSystem.Object::GetIl2CppType"
                    && c != "InputMapper/DelGetButton::Invoke"
                    && c != "Il2CppSystem.Collections.Generic.Dictionary`2::ContainsKey"
                    && !thermalPureCalls.Contains(c, StringComparer.Ordinal)
                    && !presentationWrites.Contains(c, StringComparer.Ordinal) && !localStateWrites.Contains(c, StringComparer.Ordinal)
                    && !commandWrites.Contains(c, StringComparer.Ordinal)
                    && !contentWriteMembers.Contains(c)
                    && !copyWriteMembers.Contains(c)
                    && !overrideWrites.Contains(c, StringComparer.Ordinal);
            }).ToArray();
        Check("native.read-only-game-access", gameCalls.Length > 0 && writes.Length == 0, writes.Length == 0 ? string.Join(", ", gameCalls) : string.Join(", ", writes));
        var contentWriteSites = nativeTypes.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                .Where(reference => contentWriteMembers.Contains(Member(reference)))
                .Select(reference => Member(reference) + " <- " + method.DeclaringType.FullName + "::" + method.Name)).ToArray();
        Check("native.content-writes-scoped", ReviewedContentWriteSitesAreExact(contentWriteSites), string.Join(", ", contentWriteSites));
        var copyWriteSites = nativeTypes.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                .Where(reference => copyWriteMembers.Contains(Member(reference)))
                .Select(reference => Member(reference) + " <- " + method.DeclaringType.FullName + "::" + method.Name)).ToArray();
        Check("native.private-copy-writes-scoped", ReviewedPrivateCopyWriteSitesAreExact(copyWriteSites), string.Join(", ", copyWriteSites));
        bool ProductionPickup(MethodDefinition caller, MethodReference call) =>
            caller.DeclaringType.FullName == "ForgeWeapon.Native.InventoryActionAdapter"
            && caller.Name == "TryPickup"
            && Member(call) is "LevelGeneration.LG_PickupItem_Sync::AttemptPickupInteraction" or "LevelGeneration.LG_PickupItem_Sync::SetCustomData";
        // Every whitelisted write has to be used: a member nobody writes is a loosened whitelist, not a feature.
        var usedWrites = gameCalls.Where(c => presentationWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.presentation-writes-exact", usedWrites.SequenceEqual(presentationWrites, StringComparer.Ordinal), string.Join(", ", usedWrites));
        var usedLocalState = gameCalls.Where(c => localStateWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.local-state-writes-exact", usedLocalState.SequenceEqual(localStateWrites, StringComparer.Ordinal), string.Join(", ", usedLocalState));
        var reviewedMembers = localStateWrites.Concat(new[]
            { "InputMapper/DelGetButton::Invoke", "Player.PlayerBackpackManager::MasterRemoveItem" }).ToHashSet(StringComparer.Ordinal);
        var reviewedSites = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => reviewedMembers.Contains(Member(c)))
                .Select(c => Member(c) + " <- " + m.DeclaringType.FullName + "::" + m.Name)).ToArray();
        Check("native.field-and-inventory-call-sites-exact", ReviewedFieldCallSitesAreExact(reviewedSites),
            string.Join(", ", reviewedSites));
        var productionPickupSites = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => ProductionPickup(m, c))
                .Select(c => Member(c) + " <- " + m.DeclaringType.FullName + "::" + m.Name)).ToArray();
        Check("native.inventory-pickup-command-site-exact", productionPickupSites.SequenceEqual(new[]
        {
            "LevelGeneration.LG_PickupItem_Sync::SetCustomData <- ForgeWeapon.Native.InventoryActionAdapter::TryPickup",
            "LevelGeneration.LG_PickupItem_Sync::AttemptPickupInteraction <- ForgeWeapon.Native.InventoryActionAdapter::TryPickup"
        }, StringComparer.Ordinal), string.Join(", ", productionPickupSites));
        var pinMembers = ReviewedPinCallSites.Select(site => site.Split(" <- ", StringSplitOptions.None)[0]).ToHashSet(StringComparer.Ordinal);
        var pinSites = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => pinMembers.Contains(Member(c)))
                .Select(c => Member(c) + " <- " + m.DeclaringType.FullName + "::" + m.Name)).ToArray();
        Check("native.pin-and-table-call-sites-exact", ReviewedPinCallSitesAreExact(pinSites), string.Join(", ", pinSites));
        var retirementSites = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => Member(c) == "ItemEquippable::get_GearPartHolder")
                .Select(c => Member(c) + " <- " + m.DeclaringType.FullName + "::" + m.Name)).ToArray();
        Check("native.sentry-retirement-holder-read-exact", ReviewedSentryRetirementCallSitesAreExact(retirementSites),
            string.Join(", ", retirementSites));
        // The native unload is one call inside ForgeCore's shard entry; Weapon reaches it only from ReleasePin (the pin sites above).
        using var shardCore = AssemblyDefinition.ReadAssembly(Artifact("bin/ForgeCore/release/ForgeCore.dll"));
        var unloadSignatures = shardCore.MainModule.GetType("ForgeCore.GameAssets").Methods.Single(method => method.Name == "UnloadShard")
            .Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
            .Where(c => Member(c) == "AssetShards.AssetShardManager::UnloadShardAsync").Select(c => c.FullName).ToArray();
        Check("native.pin-unload-signature-exact", unloadSignatures.SequenceEqual(new[]
        { "System.Void AssetShards.AssetShardManager::UnloadShardAsync(System.String,Il2CppSystem.Action`1<UnityEngine.AsyncOperation>,System.String)" },
            StringComparer.Ordinal), string.Join(", ", unloadSignatures));
        var invocations = calls.Where(c => Member(c) is "InputMapper/DelGetButton::Invoke" or "Player.PlayerBackpackManager::MasterRemoveItem")
            .Select(c => c.FullName).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Check("native.input-and-removal-signatures-exact", invocations.SequenceEqual(new[]
        {
            "System.Boolean InputMapper/DelGetButton::Invoke(InputAction,eFocusState)",
            "System.Void Player.PlayerBackpackManager::MasterRemoveItem(Item,SNetwork.SNet_Player)"
        }, StringComparer.Ordinal), string.Join(", ", invocations));
        var usedCommands = gameCalls.Where(c => commandWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.command-writes-exact", usedCommands.SequenceEqual(commandWrites, StringComparer.Ordinal), string.Join(", ", usedCommands));
        var loadoutTypes = nativeTypes.Where(t => t.Name is "GearLoadoutSession" or "GearLoadoutFilter" or "GearOffersForSlot");
        var loadoutCalls = loadoutTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>().ToArray();
        Check("native.loadout-no-shared-write", !loadoutCalls.Any(m => m.Name is "get_m_gearPerSlot"
            or "SaveFavoritesData" or "RegisterGearInSlotAsEquipped" || m.Name.StartsWith("set_", StringComparison.Ordinal)
            || Scope(m).StartsWith("Il2Cpp", StringComparison.Ordinal) && m.Name is "Add" or "Clear"));
        Check("native.loadout-no-loader-takeover", !nativeTypes.Any(t => t.Name is "GearPoolLoading" or "Il2CppGearPoolSlot" or "GearLoadoutSlotSnapshot`1"));
        var usedOverrides = gameCalls.Where(c => overrideWrites.Contains(c, StringComparer.Ordinal)).ToArray();
        Check("native.override-writes-exact", usedOverrides.SequenceEqual(overrideWrites, StringComparer.Ordinal), string.Join(", ", usedOverrides));
        var meleeClone = nativeTypes.Single(t => t.FullName == "ForgeWeapon.Native.WeaponOverrideApplier")
            .Methods.Single(m => m.Name == "CloneMelee");
        var cloneMembers = meleeClone.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
            .Select(Member).ToArray();
        Check("native.melee-copy-members-exact", MeleeCopyMembersAreExact(cloneMembers),
            string.Join(", ", cloneMembers));
        // Admitting a necessary copy setter does not permit unrelated code to mutate game data.
        var instanceTimeWrites = MeleeCopyWrites.Concat(new[]
        {
            "Gear.MWS_ChargeUp::set_m_chargeUpDone", "Gear.MWS_ChargeUp::set_m_maxDamageTime",
            "ItemEquippable::set_ArchetypeData", "ItemEquippable::set_MeleeAnimationData"
        }).ToHashSet(StringComparer.Ordinal);
        var unreviewedTimeWriters = nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Where(c => instanceTimeWrites.Contains(Member(c))
                    && !InstanceTimeWriteIsScoped(m.DeclaringType.FullName, m.Name, Member(c)))
                .Select(c => m.FullName + " -> " + Member(c))).ToArray();
        Check("native.instance-time-writers-scoped", unreviewedTimeWriters.Length == 0,
            string.Join(", ", unreviewedTimeWriters));
        // Pinned from the reviewed build: any new native member, read or written, must be reviewed here first.
        var expectedReads = new[]
        {
            // The shot rule's reachability walk: a course node's own id and portals, a portal's traversability,
            // its door and the node on its other side (dump.cs 698843/698849, 699000/699017/699051).
            "AIGraph.AIG_CourseNode::get_NodeID",
            "AIGraph.AIG_CourseNode::get_m_portals",
            "AIGraph.AIG_CoursePortal::GetOppositeNode",
            "AIGraph.AIG_CoursePortal::get_IsTraversable",
            "AIGraph.AIG_CoursePortal::get_m_door",
            // A give's replicated pickup (see commandWrites) and the equip port's offline-gear block enumeration:
            // the node, manager and returned item the native request is built from and read back through.
            "GameData.GameDataBlockBase`1::GetAllBlocks",
            "Gear.GearPartHolder::get_MenuMode",
            "Gear.GearPartHolder::get_SightData",
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::Cast",
            "ItemReplicator::get_Item",
            "SentryGunInstance::get_m_sync",
            "UnityEngine.Color::Lerp",
            "UnityEngine.Color::get_black",
            "UnityEngine.ColorUtility::ToHtmlStringRGB",
            "UnityEngine.Material::.ctor",
            "UnityEngine.Material::SetTexture",
            "UnityEngine.Material::get_shader",
            "UnityEngine.Mathf::Clamp01",
            "UnityEngine.Mathf::InverseLerp",
            "UnityEngine.Mathf::SmoothStep",
            "iSentrygunInstance_Sync::WantItemAction",
            "UnityEngine.Quaternion::get_identity",
            // The shot rule's aim point and the course node the walk starts and ends on, and the projectile's
            // homing target: the agent's own aim targets, life, node and position (dump.cs 680574-680583).
            "Agents.Agent::get_AimTarget",
            "Agents.Agent::get_AimTargetBody",
            "Agents.Agent::get_Alive",
            "Agents.Agent::get_CourseNode",
            // AimFacts compares the receiver's eye with the player's camera ray.
            "Agents.Agent::get_EyePosition",
            // The melee-hit failure trace names the unresolved enemy with its native ushort id. QA Modules-ASM
            // exposes Agents.Agent.GlobalID as a public ushort getter; dump.cs 680571/680628 pins RVA 0x192f140,
            // and catalog/native/native-runtime.json records the same Agents.Agent field.
            "Agents.Agent::get_GlobalID",
            "Agents.Agent::get_Position",
            "Agents.pAgent::Set", // a fresh local handle, see localStateWrites
            "Agents.pAgent::TryGet",
            // The hit diagnostics count the receiver's colliders by layer and say whether its object is active
            // (AimFacts), so a ray that passes through an enemy and finds nothing can be told from a body that is off.
            // The syringe's application coroutine: its first resume (state 0) is the use starting, and its owner is the syringe.
            "SyringeFirstPerson/_ApplySyringe_d__18::get___1__state",
            "SyringeFirstPerson/_ApplySyringe_d__18::get___4__this",
            // Read-only native cooldown facts distinguish cooldown fire refusals; preparation shares this clock for its timeout.
            "Clock::get_Time",
            // The limbs an `aim_part` chooses from: each limb's aim point, its weak-spot kind and whether it is
            // already destroyed (dump.cs 544193, 544436/544464/544466).
            "Dam_EnemyDamageBase::get_DamageLimbs",
            "Dam_EnemyDamageBase::get_Owner",
            "Dam_EnemyDamageLimb::GetBaseAgent",
            "Dam_EnemyDamageLimb::get_DamageTargetPos",
            "Dam_EnemyDamageLimb::get_IsDestroyed",
            "Dam_EnemyDamageLimb::get_m_limbID",
            "Dam_EnemyDamageLimb::get_m_type",
            // Trace-only hit-limb-state readback in the derived test adapter: limb health and multipliers, never written.
            "Dam_PlayerDamageLimb::GetBaseAgent",
            "Dam_SyncedDamageBase::get_Health",
            "Enemies.EnemyAgent::get_Damage",
            // The holder half the shot, aim and wield readbacks ride: the owner and the item behind the holder,
            // and the sight trigger the aiming state is read from.
            "FirstPersonItemHolder::get_ItemAimTrigger",
            "FirstPersonItemHolder::get_WieldedItem",
            "FirstPersonItemHolder::get_m_owner",
            "FogRepellerInstance::get_m_repellerSphere", // authored field applier read (evidence/w8-forge-field-apply.json)
            "FogRepeller_Sphere::set_Range", // local state (evidence/w8-forge-field-apply.json)
            // The override applier reads the published archetype and recoil blocks one field at a time, as the
            // vanilla values every override is measured against, before writing the reviewed result into its own
            // copy. `GetBlock` and the item block accessors are the supply catalog resolving an ammo item by id.
            "GameData.ArchetypeDataBlock::get_AimSpread",
            "GameData.ArchetypeDataBlock::get_AimTransitionTime",
            "GameData.ArchetypeDataBlock::get_BurstDelay",
            "GameData.ArchetypeDataBlock::get_BurstShotCount",
            "GameData.ArchetypeDataBlock::get_CostOfBullet",
            "GameData.ArchetypeDataBlock::get_Damage",
            "GameData.ArchetypeDataBlock::get_DamageBoosterEffect",
            "GameData.ArchetypeDataBlock::get_DamageFalloff",
            "GameData.ArchetypeDataBlock::get_DefaultClipSize",
            "GameData.ArchetypeDataBlock::get_DefaultReloadTime",
            "GameData.ArchetypeDataBlock::get_EquipTransitionTime",
            "GameData.ArchetypeDataBlock::get_FireMode",
            "GameData.ArchetypeDataBlock::get_HipFireSpread",
            "GameData.ArchetypeDataBlock::get_PiercingBullets",
            "GameData.ArchetypeDataBlock::get_PiercingDamageCountLimit",
            "GameData.ArchetypeDataBlock::get_PrecisionDamageMulti",
            "GameData.ArchetypeDataBlock::get_RecoilDataID",
            "GameData.ArchetypeDataBlock::get_Sentry_CostOfBulletTagMulti",
            "GameData.ArchetypeDataBlock::get_Sentry_DamageTagMulti",
            "GameData.ArchetypeDataBlock::get_Sentry_DetectionMaxAngle",
            "GameData.ArchetypeDataBlock::get_Sentry_DetectionMaxRange",
            "GameData.ArchetypeDataBlock::get_Sentry_FireTagOnly",
            "GameData.ArchetypeDataBlock::get_Sentry_FireTowardsTargetInsteadOfForward",
            "GameData.ArchetypeDataBlock::get_Sentry_ForceAimTowardsBody",
            "GameData.ArchetypeDataBlock::get_Sentry_LegacyEnemyDetection",
            "GameData.ArchetypeDataBlock::get_Sentry_LongRangeThreshold",
            "GameData.ArchetypeDataBlock::get_Sentry_PrioTag",
            "GameData.ArchetypeDataBlock::get_Sentry_RotationSpeed",
            "GameData.ArchetypeDataBlock::get_Sentry_RotationSpeedTagMulti",
            "GameData.ArchetypeDataBlock::get_Sentry_ShortRangeThreshold",
            "GameData.ArchetypeDataBlock::get_Sentry_ShotDelayTagMulti",
            "GameData.ArchetypeDataBlock::get_Sentry_StaggerDamageTagMulti",
            "GameData.ArchetypeDataBlock::get_Sentry_StartFireDelay",
            "GameData.ArchetypeDataBlock::get_Sentry_StartFireDelayTagMulti",
            "GameData.ArchetypeDataBlock::get_ShellCasingSize",
            "GameData.ArchetypeDataBlock::get_ShellCasingSpeedRange",
            "GameData.ArchetypeDataBlock::get_ShotDelay",
            "GameData.ArchetypeDataBlock::get_ShotgunBulletCount",
            "GameData.ArchetypeDataBlock::get_ShotgunBulletSpread",
            "GameData.ArchetypeDataBlock::get_ShotgunConeSize",
            "GameData.ArchetypeDataBlock::get_SpecialChargetupTime",
            "GameData.ArchetypeDataBlock::get_SpecialCooldownTime",
            "GameData.ArchetypeDataBlock::get_SpecialSemiBurstCountTimeout",
            "GameData.ArchetypeDataBlock::get_StaggerDamageMulti",
            "GameData.ArchetypeDataBlock::set_AimSpread",
            "GameData.ArchetypeDataBlock::set_AimTransitionTime",
            "GameData.ArchetypeDataBlock::set_BurstDelay",
            "GameData.ArchetypeDataBlock::set_BurstShotCount",
            "GameData.ArchetypeDataBlock::set_CostOfBullet",
            "GameData.ArchetypeDataBlock::set_Damage",
            "GameData.ArchetypeDataBlock::set_DamageBoosterEffect",
            "GameData.ArchetypeDataBlock::set_DamageFalloff",
            "GameData.ArchetypeDataBlock::set_DefaultClipSize",
            "GameData.ArchetypeDataBlock::set_DefaultReloadTime",
            "GameData.ArchetypeDataBlock::set_EquipTransitionTime",
            "GameData.ArchetypeDataBlock::set_FireMode",
            "GameData.ArchetypeDataBlock::set_HipFireSpread",
            "GameData.ArchetypeDataBlock::set_PiercingBullets",
            "GameData.ArchetypeDataBlock::set_PiercingDamageCountLimit",
            "GameData.ArchetypeDataBlock::set_PrecisionDamageMulti",
            "GameData.ArchetypeDataBlock::set_RecoilDataID",
            "GameData.ArchetypeDataBlock::set_Sentry_CostOfBulletTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_DamageTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_DetectionMaxAngle",
            "GameData.ArchetypeDataBlock::set_Sentry_DetectionMaxRange",
            "GameData.ArchetypeDataBlock::set_Sentry_FireTagOnly",
            "GameData.ArchetypeDataBlock::set_Sentry_FireTowardsTargetInsteadOfForward",
            "GameData.ArchetypeDataBlock::set_Sentry_ForceAimTowardsBody",
            "GameData.ArchetypeDataBlock::set_Sentry_LegacyEnemyDetection",
            "GameData.ArchetypeDataBlock::set_Sentry_LongRangeThreshold",
            "GameData.ArchetypeDataBlock::set_Sentry_PrioTag",
            "GameData.ArchetypeDataBlock::set_Sentry_RotationSpeed",
            "GameData.ArchetypeDataBlock::set_Sentry_RotationSpeedTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_ShortRangeThreshold",
            "GameData.ArchetypeDataBlock::set_Sentry_ShotDelayTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_StaggerDamageTagMulti",
            "GameData.ArchetypeDataBlock::set_Sentry_StartFireDelay",
            "GameData.ArchetypeDataBlock::set_Sentry_StartFireDelayTagMulti",
            "GameData.ArchetypeDataBlock::set_ShellCasingSize",
            "GameData.ArchetypeDataBlock::set_ShellCasingSpeedRange",
            "GameData.ArchetypeDataBlock::set_ShotDelay",
            "GameData.ArchetypeDataBlock::set_ShotgunBulletCount",
            "GameData.ArchetypeDataBlock::set_ShotgunBulletSpread",
            "GameData.ArchetypeDataBlock::set_ShotgunConeSize",
            "GameData.ArchetypeDataBlock::set_SpecialChargetupTime",
            "GameData.ArchetypeDataBlock::set_SpecialCooldownTime",
            "GameData.ArchetypeDataBlock::set_SpecialSemiBurstCountTimeout",
            "GameData.ArchetypeDataBlock::set_StaggerDamageMulti",
            "GameData.PlayerDataBlock::get_AmmoStandardResourcePackMaxCap",
            "GameData.PlayerDataBlock::get_AmmoSpecialResourcePackMaxCap",
            "GameData.PlayerDataBlock::get_AmmoClassResourcePackMaxCap",
            "GameData.GameDataBlockBase`1::GetBlock",
            "GameData.GameDataBlockBase`1::HasBlock", // the flashlight row must exist before a part is pointed at it
            "GameData.GameDataBlockBase`1::get_name",
            "GameData.GameDataBlockBase`1::get_persistentID",
            "GameData.GearFlashlightPartDataBlock::get_General",
            "GameData.GearFrontPartDataBlock::get_General",
            "GameData.GearMagPartDataBlock::get_General",
            "GameData.GearMeleeHandlePartDataBlock::get_General",
            "GameData.GearMeleeHeadPartDataBlock::get_General",
            "GameData.GearMeleeNeckPartDataBlock::get_General",
            "GameData.GearMeleePommelPartDataBlock::get_General",
            "GameData.GearPartGeneralData::get_AssetBundle",
            "GameData.GearPartGeneralData::get_BundleShard",
            "GameData.GearPartGeneralData::get_Model",
            "GameData.GearReceiverPartDataBlock::get_General",
            "GameData.GearSightPartDataBlock::get_General",
            "GameData.GearStockPartDataBlock::get_General",
            "GameData.GearToolDeliveryPartDataBlock/TypeAndGeneralData::get_Data",
            "GameData.GearToolDeliveryPartDataBlock/TypeAndGeneralData::get_Type",
            "GameData.GearToolDeliveryPartDataBlock::get_GeneralDatas",
            "GameData.GearToolGripPartDataBlock::get_General",
            "GameData.GearToolMainPartDataBlock::get_General",
            "GameData.GearToolPayloadPartDataBlock/TypeAndGeneralData::get_Data",
            "GameData.GearToolPayloadPartDataBlock/TypeAndGeneralData::get_Type",
            "GameData.GearToolPayloadPartDataBlock::get_GeneralDatas",
            "GameData.GearToolScreenPartDataBlock::get_General",
            "GameData.GearToolTargetingPartDataBlock/TypeAndGeneralData::get_Data",
            "GameData.GearToolTargetingPartDataBlock/TypeAndGeneralData::get_Type",
            "GameData.GearToolTargetingPartDataBlock::get_GeneralDatas",
            "GameData.ItemDataBlock::get_inventorySlot",
            // CloneMelee preserves the complete reviewed animation block on a private instance.
            // This independent exact call list also rejects a missing or additional copy member.
            "GameData.MeleeAnimationSetDataBlock::get_AutoAttackTime",
            "GameData.MeleeAnimationSetDataBlock::get_AutoAttackWarningTime",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpHitLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpHitRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpReleaseLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpReleaseRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackChargeUpRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackHitLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackHitRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackMissLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackMissRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPAttackPush",
            "GameData.MeleeAnimationSetDataBlock::get_FPChargeCancelAnimLeft",
            "GameData.MeleeAnimationSetDataBlock::get_FPChargeCancelAnimRight",
            "GameData.MeleeAnimationSetDataBlock::get_FPIdleAnim",
            "GameData.MeleeAnimationSetDataBlock::get_FPJumpAnim",
            "GameData.MeleeAnimationSetDataBlock::get_FPLandAnim",
            "GameData.MeleeAnimationSetDataBlock::get_FPRunAnim",
            "GameData.MeleeAnimationSetDataBlock::get_FPSettleAnim",
            "GameData.MeleeAnimationSetDataBlock::get_FPWalkAnim",
            "GameData.MeleeAnimationSetDataBlock::get_HoldToChargeTime",
            "GameData.MeleeAnimationSetDataBlock::get_MaxDamageChargeTime",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeft",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeftCharge",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeftChargeCrouch",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeftCrouch",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeftRelease",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashAttackLeftReleaseCrouch",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashIdle",
            "GameData.MeleeAnimationSetDataBlock::get_TPAnimHashIdleCrouch",
            "GameData.MeleeAnimationSetDataBlock::set_AutoAttackTime",
            "GameData.MeleeAnimationSetDataBlock::set_AutoAttackWarningTime",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpHitLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpHitRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpReleaseLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpReleaseRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackChargeUpRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackHitLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackHitRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackMissLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackMissRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPAttackPush",
            "GameData.MeleeAnimationSetDataBlock::set_FPChargeCancelAnimLeft",
            "GameData.MeleeAnimationSetDataBlock::set_FPChargeCancelAnimRight",
            "GameData.MeleeAnimationSetDataBlock::set_FPIdleAnim",
            "GameData.MeleeAnimationSetDataBlock::set_FPJumpAnim",
            "GameData.MeleeAnimationSetDataBlock::set_FPLandAnim",
            "GameData.MeleeAnimationSetDataBlock::set_FPRunAnim",
            "GameData.MeleeAnimationSetDataBlock::set_FPSettleAnim",
            "GameData.MeleeAnimationSetDataBlock::set_FPWalkAnim",
            "GameData.MeleeAnimationSetDataBlock::set_HoldToChargeTime",
            "GameData.MeleeAnimationSetDataBlock::set_MaxDamageChargeTime",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeft",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftCharge",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftChargeCrouch",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftCrouch",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftRelease",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashAttackLeftReleaseCrouch",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashIdle",
            "GameData.MeleeAnimationSetDataBlock::set_TPAnimHashIdleCrouch",
            "GameData.MinMaxValue::get_Max",
            "GameData.MinMaxValue::get_Min",
            "GameData.MinMaxValue::set_Max",
            "GameData.MinMaxValue::set_Min",
            // The authored gear's record text, read to build the GearIDRange the equip fixture hands the game's own equip call.
            "GameData.PlayerOfflineGearDataBlock::get_GearJSON",
            "GameData.RecoilDataBlock::get_concussionDuration",
            "GameData.RecoilDataBlock::get_concussionFrequency",
            "GameData.RecoilDataBlock::get_concussionIntensity",
            "GameData.RecoilDataBlock::get_dampening",
            "GameData.RecoilDataBlock::get_directionalSimilarity",
            "GameData.RecoilDataBlock::get_hipFireCrosshairRecoilPop",
            "GameData.RecoilDataBlock::get_hipFireCrosshairSizeDefault",
            "GameData.RecoilDataBlock::get_hipFireCrosshairSizeMax",
            "GameData.RecoilDataBlock::get_horizontalScale",
            "GameData.RecoilDataBlock::get_power",
            "GameData.RecoilDataBlock::get_recoilAimingWeight",
            "GameData.RecoilDataBlock::get_recoilCameraPosWeight",
            "GameData.RecoilDataBlock::get_recoilCameraRotWeight",
            "GameData.RecoilDataBlock::get_recoilPosDamping",
            "GameData.RecoilDataBlock::get_recoilPosImpulse",
            "GameData.RecoilDataBlock::get_recoilPosImpulseWeight",
            "GameData.RecoilDataBlock::get_recoilPosShift",
            "GameData.RecoilDataBlock::get_recoilPosShiftWeight",
            "GameData.RecoilDataBlock::get_recoilPosStiffness",
            "GameData.RecoilDataBlock::get_recoilRotDamping",
            "GameData.RecoilDataBlock::get_recoilRotImpulse",
            "GameData.RecoilDataBlock::get_recoilRotImpulseWeight",
            "GameData.RecoilDataBlock::get_recoilRotStiffness",
            "GameData.RecoilDataBlock::get_spring",
            "GameData.RecoilDataBlock::get_verticalScale",
            "GameData.RecoilDataBlock::get_worldToViewSpaceBlendHorizontal",
            "GameData.RecoilDataBlock::get_worldToViewSpaceBlendVertical",
            "GameData.RecoilDataBlock::set_concussionDuration",
            "GameData.RecoilDataBlock::set_concussionFrequency",
            "GameData.RecoilDataBlock::set_concussionIntensity",
            "GameData.RecoilDataBlock::set_dampening",
            "GameData.RecoilDataBlock::set_directionalSimilarity",
            "GameData.RecoilDataBlock::set_hipFireCrosshairRecoilPop",
            "GameData.RecoilDataBlock::set_hipFireCrosshairSizeDefault",
            "GameData.RecoilDataBlock::set_hipFireCrosshairSizeMax",
            "GameData.RecoilDataBlock::set_horizontalScale",
            "GameData.RecoilDataBlock::set_power",
            "GameData.RecoilDataBlock::set_recoilAimingWeight",
            "GameData.RecoilDataBlock::set_recoilCameraPosWeight",
            "GameData.RecoilDataBlock::set_recoilCameraRotWeight",
            "GameData.RecoilDataBlock::set_recoilPosDamping",
            "GameData.RecoilDataBlock::set_recoilPosImpulse",
            "GameData.RecoilDataBlock::set_recoilPosImpulseWeight",
            "GameData.RecoilDataBlock::set_recoilPosShift",
            "GameData.RecoilDataBlock::set_recoilPosShiftWeight",
            "GameData.RecoilDataBlock::set_recoilPosStiffness",
            "GameData.RecoilDataBlock::set_recoilRotDamping",
            "GameData.RecoilDataBlock::set_recoilRotImpulse",
            "GameData.RecoilDataBlock::set_recoilRotImpulseWeight",
            "GameData.RecoilDataBlock::set_recoilRotStiffness",
            "GameData.RecoilDataBlock::set_spring",
            "GameData.RecoilDataBlock::set_verticalScale",
            "GameData.RecoilDataBlock::set_worldToViewSpaceBlendHorizontal",
            "GameData.RecoilDataBlock::set_worldToViewSpaceBlendVertical",
            // The expedition gate the projection reads: no gear can be chosen inside a level.
            "GameStateManager::get_IsInExpedition",
            // The burst archetype's own copy of the burst length, read back after the applier writes it: the
            // pointer pair and the two burst caches are one transaction, so the write is only reported when both
            // caches — this field and the weapon's limit below — answer with the value the candidate requires.
            "Gear.BWA_Burst::get_m_burstMax",
            "Gear.BWA_Burst::set_m_burstCurrentCount",
            "Gear.BWA_Burst::set_m_burstMax",
            "Gear.BWA_SemiBurst::ResetBurstCount",
            "Gear.BWA_SemiBurst::get_m_burstMax",
            "Gear.BWA_SemiBurst::set_m_burstMax",
            // The commanded fire-input update, declared in commandWrites above and called by the holder channel.
            // The hitscan row's own submission is the static hit body beside it.
            "Gear.BulletWeapon::BulletHit",
            "Gear.BulletWeaponArchetype::Update",
            "Gear.BulletWeapon::get_m_archeType",
            "Gear.BulletWeapon::get_m_burstMax",
            // The falloff floor the hit-context read derives the coefficient against, beside the range the hit
            // data carries, and the burst counter the profile row's `end_burst` clears.
            "Gear.BulletWeapon::get_s_falloffMin",
            "Gear.BulletWeapon::set_m_burstCurrentCount",
            "Gear.BulletWeapon::set_m_burstMax",
            // The ranged archetype's charge-up: the fact publishes the weapon's own elapsed charge-up timer over
            // the view's full charge-up delay, and the flag is the charge window the transition is read from.
            "Gear.BulletWeaponArchetype::ChargeupDelay",
            "Gear.BulletWeaponArchetype::OnStopChargeup",
            "Gear.BulletWeaponArchetype::OnStopFiring",
            // A burst start is published only after the archetype confirms its native firing state.
            "Gear.BulletWeaponArchetype::get_Firing",
            "Gear.BulletWeaponArchetype::get_m_archetypeData",
            "Gear.BulletWeaponArchetype::get_m_chargeupTimer",
            "Gear.BulletWeaponArchetype::get_m_inChargeup",
            "Gear.BulletWeaponArchetype::get_m_nextBurstTimer",
            "Gear.BulletWeaponArchetype::get_m_nextShotTimer",
            "Gear.BulletWeaponArchetype::get_m_recoilData",
            "Gear.BulletWeaponArchetype::get_m_weapon",
            "Gear.BulletWeaponArchetype::set_m_archetypeData",
            "Gear.BulletWeaponArchetype::set_m_recoilData",
            // The bio-tracker device writer: the hologram root it hides and shows (evidence/device-switch.json in ForgePlayer).
            // The use-refusal stimulus: the pack's own press latch and kind, and the player's health (item-use-failures.json).
            "Gear.EnemyScanner::get_m_scannerObjRoot",
            "Gear.EnemyScanner::get_s_maxCourseNodeDistance",
            "Gear.EnemyScanner::get_s_tagDuration",
            "Gear.EnemyScanner::set_m_maxScanWorldRadius", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_m_pulseDuration", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_m_rechargeDuration", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_m_scanConeDotMin", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_m_scanDelay", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_s_maxCourseNodeDistance", // local state (evidence/w8-forge-field-apply.json)
            "Gear.EnemyScanner::set_s_tagDuration", // local state (evidence/w8-forge-field-apply.json)
            // The derived-test equip fixture builds the authored gear's range from its record text.
            "Gear.GearIDRange::.ctor",
            "Gear.GearIDRange::GetChecksum",
            "Gear.GearIDRange::GetCompID",
            "Gear.GearIDRange::get_PlayfabItemInstanceId",
            // The current offer is read for local admission, with no access to shared pool storage.
            "Gear.GearManager::GetAllGearForSlot",
            "Gear.GearPartFlashlight::get_m_settingsID", // local state (evidence/flashlight-settings.json)
            "Gear.GearPartFlashlight::get_m_lightAlign", // the light mount the pose is written to (evidence/flashlight-settings.json: m_lightAlign)
            "Gear.GearPartFlashlight::set_m_settingsID", // local state (evidence/flashlight-settings.json)
            "Gear.GearPartHolder::get_FlashlightPart",
            "Gear.GearPartHolder::get_FrontPart",
            "Gear.GearPartHolder::get_FrontPartAttachmentA",
            "Gear.GearPartHolder::get_FrontPartAttachmentB",
            "Gear.GearPartHolder::get_GearIDRange",
            "Gear.GearPartHolder::get_MagPart",
            "Gear.GearPartHolder::get_MeleeHandlePart",
            "Gear.GearPartHolder::get_MeleeHeadPart",
            "Gear.GearPartHolder::get_MeleeNeckPart",
            "Gear.GearPartHolder::get_MeleePommelPart",
            "Gear.GearPartHolder::get_ReceiverPart",
            "Gear.GearPartHolder::get_ReceiverPartAttachment",
            "Gear.GearPartHolder::get_SightPart",
            "Gear.GearPartHolder::get_StockPart",
            "Gear.GearPartHolder::get_ToolDeliveryPart",
            "Gear.GearPartHolder::get_ToolDeliveryPartAttachment",
            "Gear.GearPartHolder::get_ToolGripPart",
            "Gear.GearPartHolder::get_ToolMainPart",
            "Gear.GearPartHolder::get_ToolMainPartAttachment",
            "Gear.GearPartHolder::get_ToolPayloadPart",
            "Gear.GearPartHolder::get_ToolScreenPart",
            "Gear.GearPartHolder::get_ToolTargetingPart",
            "Gear.GearPartHolder::get_m_gearPartSpawner",
            "Gear.GearPartSpawner::UpdateMaterials",
            // The melee state machines: the weapon link they share, and the charge-up machine's own elapsed and
            // full damage timers the melee charge fact is measured against.
            "Gear.MWS_Base::get_m_weapon",
            "Gear.MWS_ChargeUp::get_m_chargeUpDone",
            "Gear.MWS_ChargeUp::get_m_elapsed",
            "Gear.MWS_ChargeUp::get_m_maxDamageTime",
            "Gear.MWS_ChargeUp::set_m_chargeUpDone",
            "Gear.MWS_ChargeUp::set_m_maxDamageTime",
            "Gear.MeleeWeaponDamageData::get_damageGO",
            "Gear.MeleeWeaponFirstPerson::get_CurrentState",
            "Gear.MeleeWeaponFirstPerson::get_CurrentStateName",
            "Gear.MeleeWeaponFirstPerson::get_MeleeSpeedModifier",
            "Gear.MeleeWeaponFirstPerson::get_m_damageToDeal",
            // The interop's own constructor lookup, allocation and invoke, used only to build the private block
            // `overrideWrites` fills in; they touch no shared game state.
            "GlueGun::set_m_burstShotDelay",
            "GlueGun::set_m_burstShotsMax",
            "GlueGun::set_m_burstShotsMin",
            "GlueGun::set_m_fireDelayMax",
            "GlueGun::set_m_fireDelayMin",
            "GlueGun::set_m_forceSizeMax",
            "GlueGun::set_m_forceSizeMin",
            "GlueGun::set_m_pressureProgressToFire",
            "GlueGun::set_m_spreadMax",
            "GlueGun::set_m_spreadMin",
            "GlueGun::set_m_timeToDepleatePressure",
            "GlueGun::set_m_timeToMaxpressure",
            "ISyncedItem::GetItem",
            "Il2CppInterop.Runtime.IL2CPP::GetIl2CppMethod",
            "Il2CppInterop.Runtime.IL2CPP::il2cpp_object_new",
            "Il2CppInterop.Runtime.IL2CPP::il2cpp_runtime_invoke",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::GetEnumerator",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Item",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase`1::get_Length",
            // The projectile's one sweep buffer, allocated when the row is attached and reused by every cast.
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::TryCast",
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::get_Pointer",
            "Il2CppSystem.Collections.Generic.Dictionary`2::ContainsKey",
            "Il2CppSystem.Collections.Generic.Dictionary`2::get_Count",
            "Il2CppSystem.Collections.Generic.List`1/Enumerator::MoveNext",
            "Il2CppSystem.Collections.Generic.List`1/Enumerator::get_Current",
            "Il2CppSystem.Collections.Generic.List`1::GetEnumerator",
            "Il2CppSystem.Collections.Generic.List`1::get_Count",
            "Il2CppSystem.Collections.Generic.List`1::get_Item",
            "InputMapper/DelGetButton::Invoke",
            "InputMapper::get_GetButtonDown",
            "Interact_Hack::GetEquippedHackingTool", // selected hack's own equipped tool (catalog/native/native-runtime.json)
            "Interact_Timed::set_InteractDuration", // local state (evidence/w8-forge-field-apply.json)
            "Item::get_ItemDataBlock",
            "Item::get_Owner",
            "ItemEquippable::GetCurrentClip",
            "ItemEquippable::GetMaxClip",
            "ItemEquippable::SetCurrentClip",
            "ItemEquippable::get_AimButtonHeld",
            "ItemEquippable::get_AimingAllowed",
            "ItemEquippable::get_AllowPlayerInteraction",
            "ItemEquippable::get_AmmoType",
            // The native reload reader uses the item's block, distinct from m_archetypeData.
            "ItemEquippable::get_ArchetypeData",
            "ItemEquippable::get_CanReload",
            // The gear a firing gun was assembled from, whose offline record names its shot-noise block.
            "ItemEquippable::get_CanWield",
            "ItemEquippable::get_FireButtonPressed",
            "ItemEquippable::get_GearCategoryData", // a client's settled damage names a category, held against the item it holds
            "ItemEquippable::get_GearIDRange",
            "ItemEquippable::get_GearPartHolder", // exact destructor prefix read site audited above
            "ItemEquippable::get_IsReloading",
            "ItemEquippable::get_MeleeAnimationData",
            "ItemEquippable::get_RunAllowed",
            "ItemEquippable::get_WeaponSwitchAllowed",
            "ItemEquippable::get_m_animationSequenceCoroutine",
            "ItemEquippable::get_m_isWielded",
            "ItemEquippable::set_ArchetypeData",
            "ItemEquippable::set_MeleeAnimationData",
            // The game's own layer masks the two rows cast with: the bullet ray's and the damageable-enemy one
            // (dump.cs 583217 / 583234), and the door state an open-doors pierce is decided by (dump.cs 688722).
            "LayerManager::get_MASK_BULLETWEAPON_RAY",
            "LayerManager::get_MASK_ENEMY_DAMAGABLE",
            "LayerManager::get_MASK_WORLD", // the host's line of sight on a client's reported shot
            "LevelGeneration.iLG_Door_Core::get_LastStatus",
            "LockMelterFirstPerson::set_m_interactionDuration", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerFirstPerson::set_m_deployPickupInteractionDuration", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerFirstPerson::set_m_timeBetweenPlacements", // local state (evidence/w8-forge-field-apply.json)
            // The detonation row's own write, declared in `commandWrites` above and read here as the call the
            // detonate body makes.
            "MineDeployerInstance::WantItemAction",
            "MineDeployerInstance::get_m_detection", // authored field applier read (evidence/w8-forge-field-apply.json)
            // The detonation row's own two reads: the trigger component a placed mine was set up with, and the
            // explosive payload's in-progress state, which is what refuses a mine that is already going off. The
            // payload's core is the detonated fact's own read.
            "MineDeployerInstance::get_m_detonation",
            "MineDeployerInstance::get_m_light", // authored field applier read (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detect_Laser::get_m_lineRenderer", // authored field applier read (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detect_Laser::set_m_maxLineDistance", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::get_m_core",
            "MineDeployerInstance_Detonate_Explosive::get_m_inDetonationProcess",
            "MineDeployerInstance_Detonate_Explosive::set_m_damageMax", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_damageMin", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_delay", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_distanceMax", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_distanceMin", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_explosionForce", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Explosive::set_m_radius", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Glue::get_m_projVelMinMax", // authored field applier read (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Glue::set_m_delay", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Glue::set_m_explosionDelay", // authored MineFoam.BatchDelay, WeaponFieldApplier.ApplyMine
            "MineDeployerInstance_Detonate_Glue::set_m_projAngMinMax", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Glue::set_m_projCount", // local state (evidence/w8-forge-field-apply.json)
            "MineDeployerInstance_Detonate_Glue::set_m_projVelMinMax", // local state (evidence/w8-forge-field-apply.json)
            "Player.BackpackItem::get_GearIDRange",
            "Player.BackpackItem::get_Instance",
            "Player.BackpackItem::get_IsLoaded",
            "Player.BackpackItem::get_ItemID",
            "Player.InventorySlotAmmo::get_AmmoMaxCap",
            "Player.InventorySlotAmmo::get_BulletsInPack",
            // The pool slot's own ammunition cost, which a `unit=raw` add amount is converted through.
            "Player.InventorySlotAmmo::get_CostOfBullet",
            // The agent's own first-person holder, entered before the holder reads above.
            "Player.PlayerAgent::get_FPItemHolder",
            "Player.PlayerAgent::get_FPSCamera",
            "Player.PlayerAgent::get_InputFilter",
            "Player.PlayerAgent::get_Inventory",
            "Player.PlayerAgent::get_Owner",
            "Player.PlayerAgent::get_Sync",
            "Player.PlayerAmmoStorage::GetInventorySlotAmmo",
            // The supply, give and consume commands read a recipient's own ammo and backpack state: the pack
            // count, the caps and the slot they read back after a submission, so a refused or partial change is
            // reported rather than assumed.
            "Player.PlayerAmmoStorage::UpdateBulletsInPack",
            "Player.PlayerAmmoStorage::UpdateSlotAmmoUI",
            "Player.PlayerAmmoStorage::get_m_playerBackpack",
            "Player.PlayerBackpack::CountPocketItem",
            "Player.PlayerBackpack::IsDeployed",
            "Player.PlayerBackpack::get_AmmoStorage",
            "Player.PlayerBackpack::get_Owner",
            "Player.PlayerBackpack::get_Slots",
            // The derived-test equip fixture's one write: the game's own local equip call, scoped in DerivedTestWriteScopes.
            "Player.PlayerBackpackManager::EquipBotGear",
            "Player.PlayerBackpackManager::GetAmmoMaxCap",
            "Player.PlayerBackpackManager::GetBulletsInPack",
            "Player.PlayerBackpackManager::GiveAmmoToPlayer",
            "Player.PlayerBackpackManager::MasterRemoveItem",
            // ammo_add on the pool's owner: the manager the gift's receive half runs on. Whether this machine
            // settles the gift is ForgeCore's Players.SimulatedHere (its own player, or a bot on the host).
            "Player.PlayerBackpackManager::ReceiveAmmoGive",
            "Player.PlayerBackpackManager::get_Current",
            "SNetwork.SNetStructs/pPlayer::SetPlayer",
            "Player.PlayerBackpackManager::TryGetBackpack",
            "Player.PlayerBackpackManager::TryMasterRemovePocketItemWithID",
            // The local player's own backpack, where the bio-tracker writer finds the scanner it holds off.
            "Player.PlayerBackpackManager::get_LocalBackpack",
            "Player.PlayerBotActionUseEnemyScanner::get_m_tool", // Scoped bot scanner inputs.
            "PlayerInventoryBase::CanReloadCurrent",
            "PlayerInventoryBase::TriggerReload",
            "PlayerInventoryBase::get_Owner",
            "PlayerInventoryBase::get_WieldedItem",
            "PlayerInventoryBase::get_WieldedSlot",
            "PlayerInventoryBase::get_m_wieldedItem",
            // Only ordinary native launch calls WantToFireTargeting and reads the projectile list back. Extended flight
            // draws through ForgeVisual's ProjectileLooks, which reads the prefab table and the prefab's speed.
            "ProjectileManager::WantToFireTargeting",
            "ProjectileManager::get_Current",
            "ProjectileManager::get_Projectiles",
            // Equipment identity reads the native player that owns the instance; capture diagnostics read the
            // replicated spawn snapshot without consulting the global player table.
            "SNetwork.SNet_Player::get_HasPlayerAgent",
            "SNetwork.SNet_Player::get_IsBot",
            "SNetwork.SNet_Player::get_IsLocal",
            "SNetwork.SNet_Player::get_PlayerAgent",
            "SentryGunFirstPerson::set_m_deployPickupInteractionDuration", // local state (evidence/w8-forge-field-apply.json)
            "SentryGunInstance::get_m_detection",
            "SentryGunInstance::get_m_firing",
            "SentryGunInstance::get_m_visuals", // authored field applier read (evidence/w8-forge-field-apply.json)
            "SentryGunInstance::set_m_initialScanDelay", // local state (evidence/w8-forge-field-apply.json)
            "SentryGunInstance_Firing_Bullets::get_m_core",
            "SentryGunInstance_ScannerVisuals_Plane::set_m_hasTargetColor", // presentation (evidence/w8-forge-field-apply.json)
            "SentryGunInstance_ScannerVisuals_Plane::set_m_scanningColor", // presentation (evidence/w8-forge-field-apply.json)
            "ThrowingWeapon::get_m_inThrowCharge",
            "ThrowingWeapon::get_m_inThrowRelease",
            "ThrowingWeapon::get_m_ready",
            "ThrowingWeapon::get_m_waitingForReady",
            "UnityEngine.Behaviour::set_enabled",
            "UnityEngine.Color::.ctor", // authored field applier read (evidence/w8-forge-field-apply.json)
            "UnityEngine.Component::GetComponentInParent",
            "UnityEngine.Component::GetComponentsInChildren",
            "UnityEngine.Component::get_gameObject",
            "UnityEngine.Component::get_transform",
            "UnityEngine.GameObject::.ctor",
            "UnityEngine.GameObject::GetComponent",
            "UnityEngine.GameObject::GetComponentInParent",
            "UnityEngine.GameObject::GetComponentsInChildren",
            "UnityEngine.GameObject::SetActive",
            // Whether the bio tracker's hologram was shown when the hold began, which the restore puts back.
            "UnityEngine.GameObject::get_activeSelf",
            "UnityEngine.GameObject::get_layer",
            "UnityEngine.GameObject::get_transform",
            "UnityEngine.Light::set_color", // presentation (evidence/w8-forge-field-apply.json)
            "UnityEngine.Light::set_intensity", // presentation (evidence/w8-forge-field-apply.json)
            "UnityEngine.LineRenderer::set_endColor", // presentation (evidence/w8-forge-field-apply.json)
            "UnityEngine.LineRenderer::set_endWidth", // presentation (evidence/w8-forge-field-apply.json)
            "UnityEngine.LineRenderer::set_startColor", // presentation (evidence/w8-forge-field-apply.json)
            "UnityEngine.LineRenderer::set_startWidth", // presentation (evidence/w8-forge-field-apply.json)
            // Root-relative visual matrices; no Item, collision or interaction root is written.
            "UnityEngine.Matrix4x4::GetColumn",
            "UnityEngine.Matrix4x4::TRS",
            "UnityEngine.Matrix4x4::get_lossyScale",
            "UnityEngine.Matrix4x4::get_rotation",
            "UnityEngine.Matrix4x4::op_Multiply",
            "UnityEngine.Object::Destroy",
            "UnityEngine.Object::DestroyImmediate",
            "UnityEngine.Object::DontDestroyOnLoad",
            "UnityEngine.Object::Instantiate",
            "UnityEngine.Object::get_name",
            "UnityEngine.Object::op_Equality",
            "UnityEngine.Object::op_Inequality",
            "UnityEngine.Object::set_hideFlags",
            "UnityEngine.Object::set_name",
            "UnityEngine.Physics::Linecast", // the host's line of sight on a client's reported shot
            "UnityEngine.Quaternion::Euler",
            "UnityEngine.Quaternion::LookRotation",
            "UnityEngine.RaycastHit::get_collider",
            // The ray's own travelled length, which the hit-context read reports as `distance`.
            "UnityEngine.RaycastHit::get_distance",
            // The hit normal a melee or bullet hit fact carries beside the point.
            "UnityEngine.RaycastHit::get_normal",
            "UnityEngine.RaycastHit::get_point",
            "UnityEngine.RaycastHit::set_distance",
            "UnityEngine.Renderer::get_material",
            "UnityEngine.Renderer::get_sharedMaterial",
            "UnityEngine.Renderer::set_enabled",
            "UnityEngine.Shader::PropertyToID",
            "UnityEngine.Texture2D::.ctor",
            "UnityEngine.Texture2D::Apply",
            "UnityEngine.Texture2D::SetPixel",
            "UnityEngine.Texture2D::get_blackTexture",
            "UnityEngine.Texture::set_filterMode",
            "UnityEngine.Texture::set_wrapMode",
            // The frame length a projectile flight is advanced by, read once per step.
            "UnityEngine.Time::get_deltaTime",
            "UnityEngine.Time::get_frameCount",
            "UnityEngine.Transform::Find",
            "UnityEngine.Transform::IsChildOf",
            "UnityEngine.Transform::SetParent",
            // The facing of the body a hit reached, which the hit-context read measures a from-behind relation
            // against.
            "UnityEngine.Transform::get_forward",
            "UnityEngine.Transform::get_localToWorldMatrix",
            "UnityEngine.Transform::get_parent",
            "UnityEngine.Transform::get_position",
            "UnityEngine.Transform::get_root",
            "UnityEngine.Transform::get_worldToLocalMatrix",
            "UnityEngine.Transform::set_localEulerAngles",
            "UnityEngine.Transform::set_localPosition",
            "UnityEngine.Transform::set_localRotation",
            "UnityEngine.Transform::set_localScale",
            "UnityEngine.Transform::set_position",
            "UnityEngine.Transform::set_rotation",
            "UnityEngine.Vector2::.ctor",
            "UnityEngine.Vector3::.ctor",
            "UnityEngine.Vector3::Cross",
            "UnityEngine.Vector3::Reflect",
            "UnityEngine.Vector3::RotateTowards",
            "UnityEngine.Vector3::get_one",
            "UnityEngine.Vector3::get_sqrMagnitude", // WeaponBulletProjectiles.cs:450: homing proximity check.
            "UnityEngine.Vector3::get_zero",
            "UnityEngine.Vector3::op_Addition",
            // Length and normalization use double scalars (WeaponBulletProjectiles.cs:462,713,715;
            // WeaponHitscanActions.cs:316,318), so neither magnitude nor Vector3 division is referenced.
            "UnityEngine.Vector3::op_Multiply",
            "UnityEngine.Vector3::op_Subtraction",
            "UnityEngine.Vector4::op_Implicit",
            // The shot's own origin, direction and direction flag: the three readings the hit-context row derives
            // the incidence angle and the from-behind relation from. The hitscan row also builds the value itself
            // and reads back the range it wrote, so the constructor and that getter travel here.
            "Weapon/WeaponHitData::.ctor",
            // The spread and pellet offsets a pierced re-cast saves, zeroes and restores, so the re-cast follows
            // the line the game's own ray already took.
            "Weapon/WeaponHitData::get_angOffsetX",
            "Weapon/WeaponHitData::get_angOffsetY",
            "Weapon/WeaponHitData::get_damageFalloff",
            "Weapon/WeaponHitData::get_fireAtPos",
            "Weapon/WeaponHitData::get_fireDir",
            "Weapon/WeaponHitData::get_hasFireDir",
            "Weapon/WeaponHitData::get_maxRayDist",
            "Weapon/WeaponHitData::get_owner",
            "Weapon/WeaponHitData::get_randomSpread",
            "Weapon/WeaponHitData::get_rayHit",
            // The by-value hit data the hitscan row builds, field by field, before it hands it to the hit body.
            "Weapon/WeaponHitData::set_angOffsetX",
            "Weapon/WeaponHitData::set_angOffsetY",
            "Weapon/WeaponHitData::set_damage",
            "Weapon/WeaponHitData::set_damageFalloff",
            "Weapon/WeaponHitData::set_fireAtPos",
            "Weapon/WeaponHitData::set_fireDir",
            "Weapon/WeaponHitData::set_hasFireDir",
            "Weapon/WeaponHitData::set_maxRayDist",
            "Weapon/WeaponHitData::set_owner",
            "Weapon/WeaponHitData::set_randomSpread",
            "Weapon/WeaponHitData::set_rayHit",
            // The hitscan row's own cast, beside the weapon's own `MaxRayDist` it defaults the range from.
            "Weapon::CastWeaponRay",
            "Weapon::get_InfiniteAmmo",
            "Weapon::get_MaxRayDist",
            "Weapon::get_RecoilOn",
            "Weapon::get_SuperWeapons",
            "Weapon::set_InfiniteAmmo",
            "Weapon::set_RecoilOn",
            "Weapon::set_SuperWeapons",
            "iSentrygunInstanceCore::get_Ammo",
            // Content registration, fixed-slot copies and reversible hacking interactions.
            "GameData.ItemDataBlock::get_publicName",
            "GameData.PlayerOfflineGearDataBlock::get_Type",
            "Gear.GearIDRange::SetCompID",
            "Gear.GearIDRange::ToJSON",
            "Gear.GearIDRange::set_OfflineGearType",
            "Gear.GearIDRange::set_PlayfabItemId",
            "Gear.GearIDRange::set_PlayfabItemInstanceId",
            "Gear.GearIDRange::set_PublicGearName",
            "Gear.GearManager::get_Current",
            "Gear.GearManager::get_m_allGearPerInstanceKey",
            "Gear.GearManager::get_m_gearPerSlot",
            "Gear.GearManager::set_m_allGearPerInstanceKey",
            "Gear.GearManager::set_m_gearPerSlot",
            "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1::.ctor",
            "Il2CppSystem.Collections.Generic.Dictionary`2/Enumerator::MoveNext",
            "Il2CppSystem.Collections.Generic.Dictionary`2/Enumerator::get_Current",
            "Il2CppSystem.Collections.Generic.Dictionary`2::.ctor",
            "Il2CppSystem.Collections.Generic.Dictionary`2::Add",
            "Il2CppSystem.Collections.Generic.Dictionary`2::GetEnumerator",
            "Il2CppSystem.Collections.Generic.KeyValuePair`2::get_Key",
            "Il2CppSystem.Collections.Generic.KeyValuePair`2::get_Value",
            "Il2CppSystem.Collections.Generic.List`1::.ctor",
            "Il2CppSystem.Collections.Generic.List`1::Add",
            "Interact_Timed::get_InteractDuration",
            "UnityEngine.Object::GetInstanceID",
            // Empty ItemThrower effects cannot be indexed by BulletWeapon.SetupEffects; gun preflight reads the actual FPS path.
            "GameData.WeaponMuzzleFlashDataBlock::get_muzzleFlashFPS",
            // Private-copy metadata and the exact authoring-test native request/readback surface.
            "FirstPersonItemHolder::get_ItemIsBusy",
            "GameData.ArchetypeDataBlock::get_AimSequence",
            "GameData.ArchetypeDataBlock::get_Description",
            "GameData.ArchetypeDataBlock::get_EquipSequence",
            "GameData.ArchetypeDataBlock::get_PublicName",
            "GameData.ArchetypeDataBlock::set_AimSequence",
            "GameData.ArchetypeDataBlock::set_Description",
            "GameData.ArchetypeDataBlock::set_EquipSequence",
            "GameData.ArchetypeDataBlock::set_PublicName",
            "GameData.GameDataBlockBase`1::set_persistentID",
            "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase::get_WasCollected",
            // An unsupported consumable family is named by the game's own class of the item.
            "LevelGeneration.ItemInLevel::GetSyncComponent",
            "LevelGeneration.LG_PickupItem_Sync::AttemptPickupInteraction",
            "LevelGeneration.LG_PickupItem_Sync::GetCustomData",
            "LevelGeneration.LG_PickupItem_Sync::SetCustomData",
            // The loadout fixture's initial reserve of an equipped gun: the player's data block names the amount, its cap and the
            // resource-pack cap the game's gift multiplies the relative amount by.
            "Player.InventorySlotAmmo::get_AmmoInPack",
            // Test-only pool measurements written to the ammo_add and consumable-grant logs.
            "Player.PlayerSync::get_m_fireCountSyncRef",
            "Item::get_pItemData",
            // The camera ray the thrown weapon aims along.
            "FPSCamera::get_CameraRayDir",
            "ThrowingWeapon::get_m_hasThrown", "ThrowingWeapon::CalcThrust",
            // The sentry placement aim: the ground at the feet, found with the game's own sentry camera-ray mask.
            // The reload sequence bound reads each item's marker type and time (ReloadSpeedHooks.cs).
            "GameData.WeaponAnimSequenceItem::get_TriggerTime",
            "GameData.WeaponAnimSequenceItem::get_Type",
            "GameData.WeaponAnimSequenceItem::set_TriggerTime",
            // The bullet_projectile reader finds the flight's scene body by its name (WeaponBehaviorEffects.Observed.cs).
            // A content generation equips the lobby's loadouts again (WeaponGearCatalogue.Reinstall): this machine's player and,
            // on the host, its bots from the slotted players; the favourites and last-equipped state it saves and puts back when
            // the generation does not stand, the game's own favourites rescan and last-equipped record, and the replaced gear read
            // from the index under its record.
            "Gear.GearFavoritesData::get_LastEquipped_Class", "Gear.GearFavoritesData::get_LastEquipped_Melee",
            "Gear.GearFavoritesData::get_LastEquipped_Special", "Gear.GearFavoritesData::get_LastEquipped_Standard",
            "Gear.GearFavoritesData::set_LastEquipped_Class", "Gear.GearFavoritesData::set_LastEquipped_Melee",
            "Gear.GearFavoritesData::set_LastEquipped_Special", "Gear.GearFavoritesData::set_LastEquipped_Standard",
            "Gear.GearManager::RegisterGearInSlotAsEquipped", "Gear.GearManager::RescanFavorites", "Gear.GearManager::get_FavoritesData",
            "Gear.GearManager::get_m_favoriteGearPerSlot", "Gear.GearManager::get_m_lastEquippedGearPerSlot",
            "Gear.GearManager::set_m_favoriteGearPerSlot", "Gear.GearManager::set_m_lastEquippedGearPerSlot",
            "Il2CppSystem.Collections.Generic.Dictionary`2::get_Item",
            "SNetwork.SNet::get_IsMaster", "SNetwork.SNet::get_Slots", "SNetwork.SNet_PlayerSlotManager::get_SlottedPlayers",
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var missingReads = expectedReads.Except(gameCalls, StringComparer.Ordinal).ToArray();
        var extraReads = gameCalls.Except(expectedReads, StringComparer.Ordinal).ToArray();
        Check("native.exact-game-reads", gameCalls.SequenceEqual(expectedReads, StringComparer.Ordinal),
            "missing=[" + string.Join(", ", missingReads) + "] extra=[" + string.Join(", ", extraReads)
            + "] actual=[" + string.Join(", ", gameCalls) + "]");
        // The gear-block mount is answered from the record the game's own offline gear loader writes onto a gear,
        // so both halves of that read are pinned: the accessor and the literal prefix it is measured against.
        Check("native.gear-block-reads-the-offline-record",
            calls.Any(m => Member(m) == "Gear.GearIDRange::get_PlayfabItemInstanceId")
            && nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand! == "OfflineGear_ID_"),
            "The gear-block matcher does not read the offline gear record.");
        // The session layer is read through getters alone. Session addresses come from `IPlayerDirectory`; the
        // capture snapshot is a pure query and neither path reaches an account key or cached player table.
        var snetReads = gameCalls.Where(c => c.StartsWith("SNetwork.", StringComparison.Ordinal)).ToArray();
        Check("native.snet-read-only", snetReads.Length > 0 && snetReads.All(c => c.Contains("::get_", StringComparison.Ordinal)
            // The by-value replicator reference used by the diagnostic, its pure spawn snapshot query, and the
            // replication manager's own return slot cleared before the single spawn call, and the by-value addressee of
            // the ammunition gift the pool's owner settles.
            || c is "SNetwork.SNetStructs/pReplicator::SetID" or "SNetwork.SNet_DynamicReplicator`1::GetSpawnData"
                or "SNetwork.SNetStructs/pPlayer::SetPlayer"), string.Join(", ", snetReads));
        var harmonyCalls = calls.Where(m => Scope(m).Contains("Harmony", StringComparison.Ordinal)).Select(Member).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.no-harmony-installation", harmonyCalls.All(c => c is not ("HarmonyLib.Harmony::.ctor" or "HarmonyLib.Harmony::CreateClassProcessor"
            or "HarmonyLib.PatchClassProcessor::Patch" or "HarmonyLib.Harmony::UnpatchSelf")), string.Join(", ", harmonyCalls));

        bool Patch(TypeDefinition t) => t.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        var hooks = packageTypes.Where(Patch).ToArray();
        // The evidence file is the frozen witness for the hooks it was written for; the combat and deployable
        // hooks added later are not in it, so their declared target is checked against the same game assemblies
        // instead, and their shape by the same checks. Every hook is still pinned by exactly one source.
        // Two hooks are named by both files: the loadout policy pins them with its own readback, and the hook table lists the same
        // classes after a regeneration. Each hook keeps one witness, the loadout row, and the two files must name the same target.
        var evidenced = specHooks.Where(h => !loadoutHooks.Any(l => l.GetProperty("hook").GetString() == h.GetProperty("hook").GetString())).Concat(loadoutHooks).ToArray();
        Check("loadout.witness-agrees-with-hook-table", loadoutHooks.All(l => specHooks.All(s => s.GetProperty("hook").GetString() != l.GetProperty("hook").GetString()
            || (s.GetProperty("type").GetString() == l.GetProperty("type").GetString() && s.GetProperty("name").GetString() == l.GetProperty("name").GetString()))),
            "The loadout policy and the hook table must name the same declared target for every hook both pin.");
        var specNames = evidenced.Select(h => h.GetProperty("hook").GetString()!).ToArray();
        var extraNames = hooks.Select(t => t.Name).Where(n => !specNames.Contains(n, StringComparer.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.exact-hook-set", hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(specNames.Concat(extraNames).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(", ", hooks.Select(t => t.Name)));
        Check("native.spec-hook-set-partitioned", specNames.Distinct(StringComparer.Ordinal).Count() == specNames.Length,
            "Two hook records claim the same hook name.");
        var resourceContribution = nativeTypes.Single(t => t.FullName == "ForgeWeapon.Native.WeaponNativeHooks")
            .Methods.SingleOrDefault(m => m.Name == "NativeResourceLifetimeHookTypes");
        // This native-only partial appends the exact destruction and reset-observer hooks. Do not discover arbitrary helper methods or
        // treat an uncalled type token as proof that a hook is installed.
        string[] ContributionInstructions(MethodDefinition? contribution) => contribution?.HasBody == true
            ? contribution.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).Select(i =>
                i.OpCode.Name + (i.Operand is TypeReference type ? " " + type.FullName
                    : i.Operand is MethodReference method ? " " + method.DeclaringType.FullName + "::" + method.Name
                    : i.Operand == null ? "" : " " + i.Operand)).ToArray()
            : Array.Empty<string>();
        var resourceInstructions = ContributionInstructions(resourceContribution);
        bool resourceContributionExact = resourceContribution is { IsStatic: true }
            && resourceContribution.ReturnType.FullName == "System.Void"
            && resourceContribution.Parameters.Count == 1
            && resourceContribution.Parameters[0].ParameterType.FullName == "System.Collections.Generic.List`1<System.Type>"
            && SentryResourceContributionIsExact(resourceInstructions);
        Check("native.sentry-resource-contribution-exact", resourceContributionExact, string.Join(", ", resourceInstructions));
        // The destruction hook is a ForgeCore subscription: no Harmony class of its own, one Around call over three delegates.
        var lifetimeType = nativeTypes.Single(t => t.FullName == "ForgeWeapon.Native.SentryResourceLifetime");
        var lifetimeMethods = lifetimeType.Methods.Concat(lifetimeType.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody).ToArray();
        string Qualified(MethodReference m) => m.DeclaringType.FullName + "::" + m.Name;
        var subscribeBody = lifetimeType.Methods.Single(m => m.Name == "Subscribe").Body.Instructions;
        var captureBody = lifetimeType.Methods.Single(m => m.Name == "Capture").Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        var firstConstant = captureBody.FirstOrDefault(i => i.OpCode.Code is Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_S or Code.Ldc_I4);
        int? firstConstantValue = firstConstant == null ? null : firstConstant.OpCode.Code switch
        {
            Code.Ldc_I4_0 => 0,
            Code.Ldc_I4_1 => 1,
            Code.Ldc_I4_2 => 2,
            Code.Ldc_I4_3 => 3,
            Code.Ldc_I4_4 => 4,
            Code.Ldc_I4_5 => 5,
            Code.Ldc_I4_6 => 6,
            Code.Ldc_I4_7 => 7,
            _ => Convert.ToInt32(firstConstant.Operand)
        };
        string Signature(MethodDefinition m) => m.ReturnType.FullName + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        var entry = packageTypes.Single(type => type.FullName == "ForgeWeapon.Native.WeaponNativeHooks").Methods.Single(m => m.Name == "NativeResourceLifetimeSubscriptions");
        var subscribesFromHost = packageTypes.Single(type => type.FullName == "ForgeWeapon.Native.WeaponNativeHooks").Methods.Single(m => m.Name == "Subscribe")
            .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Count(m => m.Name == "NativeResourceLifetimeSubscriptions") == 1;
        var entryCalls = entry.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(Qualified).ToArray();
        var bracketCalls = lifetimeMethods.SelectMany(m => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>())
            .Where(m => m.DeclaringType.FullName.StartsWith("ForgeCore.Bracket`1", StringComparison.Ordinal)).Select(Qualified).ToArray();
        var delegateTargets = subscribeBody.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => Qualified((MethodReference)i.Operand)).ToArray();
        bool harmonyFree = lifetimeType.CustomAttributes.All(a => a.AttributeType.Namespace != "HarmonyLib")
            && lifetimeType.Methods.All(m => m.CustomAttributes.All(a => a.AttributeType.Namespace != "HarmonyLib"));
        var lifetimeMembers = lifetimeType.Methods.ToDictionary(m => m.Name);
        Check("native.sentry-destruction-subscription-exact", subscribesFromHost && harmonyFree && SentrySubscriptionIsExact(entryCalls, bracketCalls, delegateTargets,
            new[] { Signature(lifetimeMembers["Capture"]), "first-call=" + Qualified(captureBody.Select(i => i.Operand).OfType<MethodReference>().First()), "first-constant=" + firstConstantValue },
            new[] { Signature(lifetimeMembers["Retire"]), Signature(lifetimeMembers["RetireAfterFault"]) }),
            "host=" + subscribesFromHost + " harmonyFree=" + harmonyFree + " entry=" + string.Join(",", entryCalls) + " bracket=" + string.Join(",", bracketCalls)
            + " delegates=" + string.Join(",", delegateTargets) + " capture=" + Signature(lifetimeMembers["Capture"])); var fixedToolContribution = nativeTypes.Single(type => type.FullName == "ForgeWeapon.Native.WeaponNativeHooks")
            .Methods.SingleOrDefault(method => method.Name == "NativeFixedToolHookTypes");
        var fixedToolInstructions = ContributionInstructions(fixedToolContribution);
        bool fixedToolContributionExact = fixedToolContribution is { IsStatic: true }
            && fixedToolContribution.ReturnType.FullName == "System.Void"
            && fixedToolContribution.Parameters.Count == 1
            && fixedToolContribution.Parameters[0].ParameterType.FullName == "System.Collections.Generic.List`1<System.Type>"
            && fixedToolInstructions.SequenceEqual(new[]
            {
                "ldarg.0", "ldtoken ForgeWeapon.Native.HackingToolSlotSpawn", "call System.Type::GetTypeFromHandle",
                "callvirt System.Collections.Generic.List`1<System.Type>::Add", "ret"
            }, StringComparer.Ordinal);
        Check("native.fixed-tool-contribution-exact", fixedToolContributionExact, string.Join(", ", fixedToolInstructions));
        // The package declares no loadout clip-transfer contribution.
        bool loadoutContributionExact = packageTypes.Single(type => type.FullName == "ForgeWeapon.Native.WeaponNativeHooks")
                .Methods.All(method => method.Name != "NativeLoadoutHookTypes")
            && packageTypes.All(type => type.Name != "LoadoutClipTransfer");
        Check("native.loadout-transfer-contribution-exact", loadoutContributionExact);
        var resetObserver = nativeTypes.Single(t => t.FullName == "ForgeWeapon.Native.ShardDependencyResetObserved");
        var resetPrefix = resetObserver.Methods.Single(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix"));
        var resetPostfix = resetObserver.Methods.Single(m => m.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix"));
        int? ResetPriority(MethodDefinition method) => method.CustomAttributes
            .SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority")?.ConstructorArguments[0].Value as int?;
        string Parameter(ParameterDefinition parameter) => parameter.ParameterType.FullName + " " + parameter.Name;
        var resetBody = resetPostfix.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        bool resetExecutionProof = resetBody.Length >= 3 && resetBody[0].OpCode == OpCodes.Ldarg_1
            && resetBody[1].OpCode.FlowControl == FlowControl.Cond_Branch && resetBody[2].OpCode == OpCodes.Ret;
        Check("native.shard-reset-observer-signature-exact",
            resetPrefix.IsStatic && resetPostfix.IsStatic
            && resetPrefix.ReturnType.FullName == "System.Void" && resetPostfix.ReturnType.FullName == "System.Void"
            && ShardResetObservationSignaturesAreExact(
                resetPrefix.Parameters.Select(Parameter), resetPostfix.Parameters.Select(Parameter))
            && ResetPriority(resetPrefix) == 800 && ResetPriority(resetPostfix) == 0
            && resetExecutionProof,
            "prefix=(" + string.Join(", ", resetPrefix.Parameters.Select(Parameter))
            + ") postfix=(" + string.Join(", ", resetPostfix.Parameters.Select(Parameter))
            + ") gate=" + string.Join(" ", resetBody.Take(3).Select(i => i.OpCode.Name)));
        // Every hook class the package declares is named by one of the lists its own installation loop reads: the
        // package's own list, the family that declares the authored-field hooks beside the applier it writes
        // through, and the three families that declare theirs beside the table or observer they read. A family list
        // nobody installs is the exact failure that left the reload rules compiled but never patched, so the
        // declaration and the installation are pinned against each other rather than each against itself.
        string[] ListedHookNames(string owner)
        {
            var type = nativeTypes.Single(t => t.Name == owner);
            var methods = new List<MethodDefinition>();
            // A static property's initializer runs in the type's own static constructor; the getter is read too so
            // a list written in either place is found.
            if (type.Methods.FirstOrDefault(m => m.Name == ".cctor") is { } initializer) methods.Add(initializer);
            if (type.Properties.FirstOrDefault(p => p.Name == "Types")?.GetMethod is { } getter) methods.Add(getter);
            if (owner == "WeaponNativeHooks" && resourceContributionExact) methods.Add(resourceContribution!);
            if (owner == "WeaponNativeHooks" && fixedToolContributionExact) methods.Add(fixedToolContribution!);
            return methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Where(i => i.OpCode == OpCodes.Ldtoken && i.Operand is TypeReference)
                .Select(i => ((TypeReference)i.Operand!).Name).ToArray();
        }
        string[] hookLists = { "WeaponNativeHooks", "WeaponFieldHooks", "ReloadInventoryHooks", "WeaponPlacementHooks", "ReloadSpeedHooks",
            "BiotrackerDevice", "WeaponLazyHooks" };
        // WeaponDamagePacketEquipment declares no list: the damage receivers are observed through Runtime's shared entries.
        // WeaponShotRules declares none either: its only patch class was the hit entry, which is now a subscription to ForgeCore's one patch.
        var listed = hookLists.SelectMany(ListedHookNames).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("native.declared-hooks-are-listed", listed.SequenceEqual(hooks.Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal),
            string.Join(", ", listed));
        // And the one installation path reads all three lists: the loop that patches hook classes follows each of
        // them, so being listed and being installed are the same thing.
        // The walk follows the package's own call graph. A reference to one of this assembly's own types is
        // resolved by the module that declares it rather than by an assembly reference, so membership is asked of
        // the type table rather than of the operand's scope. A static property's initializer is not in its own
        // getter but in the type's constructor, so every own type a body mentions is entered from that side too:
        // a list written as `{ get; } = ...` is otherwise invisible to a walk that arrives through a getter.
        var ownTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var type in nativeTypes) ownTypes.TryAdd(type.FullName, type);
        var ownMethods = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        foreach (var method in nativeTypes.SelectMany(t => t.Methods)) ownMethods.TryAdd(method.FullName, method);
        var reachedLists = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<MethodDefinition>(nativeTypes.SelectMany(t => t.Methods).Where(m => m.HasBody
            && m.Body.Instructions.Any(i => i.Operand is MethodReference reference
                && reference.DeclaringType.FullName == "ForgeEngine.GameBindings.NativeHookRegistration" && reference.Name == "Declare")));
        Check("native.hooks-declared-through-runtime", pending.Count > 0, "Every callback class must be handed to Runtime by NativeHookRegistration.Declare.");
        var walked = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var caller = pending.Dequeue();
            if (!walked.Add(caller.FullName) || !caller.HasBody) continue;
            foreach (var reference in caller.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>())
            {
                if (!ownTypes.TryGetValue(reference.DeclaringType.FullName, out var declaring)) continue;
                if (reference.Name == "get_Types") reachedLists.Add(declaring.Name);
                if (ownMethods.TryGetValue(reference.FullName, out var target)) pending.Enqueue(target);
                if (declaring.Methods.FirstOrDefault(m => m.Name == ".cctor") is { } initializer) pending.Enqueue(initializer);
            }
        }
        Check("native.hook-lists-are-installed", hookLists.Where(list => list != "WeaponLazyHooks").All(reachedLists.Contains),
            string.Join(", ", reachedLists.OrderBy(x => x, StringComparer.Ordinal)));
        // The per-frame units are not in the startup walk: they go on through Runtime the first time a request needs
        // them. Their installer is Runtime's own Declare, and the one Ensure body reads the unit table it installs.
        var lazy = nativeTypes.Single(t => t.Name == "WeaponLazyHooks");
        var lazyInit = lazy.Methods.Single(m => m.Name == ".cctor");
        var lazyEnsure = lazy.Methods.Single(m => m.Name == "Ensure");
        Check("native.lazy-hook-units-install-through-runtime",
            lazyInit.Body.Instructions.Any(i => i.Operand is MethodReference reference
                && reference.DeclaringType.FullName == "ForgeEngine.GameBindings.NativeHookRegistration" && reference.Name == "Declare")
            && lazyEnsure.Body.Instructions.Any(i => i.Operand is FieldReference field && field.Name == "Units")
            && lazyEnsure.Body.Instructions.Any(i => i.Operand is MethodReference reference && reference.Name == "get_Installer"),
            "WeaponLazyHooks.Ensure must install its own unit table through NativeHookRegistration.Declare.");
        Check("native.sentry-resource-contribution-is-installed", resourceContribution != null
            && walked.Contains(resourceContribution.FullName), "The installation call graph must reach the exact native-only contribution.");
        Check("native.fixed-tool-contribution-is-installed", fixedToolContribution != null
            && walked.Contains(fixedToolContribution.FullName), "The installation call graph must reach the fixed-tool contribution.");
        foreach (var name in specNames.Concat(extraNames))
        {
            JsonElement? spec = evidenced.Where(h => h.GetProperty("hook").GetString() == name)
                .Select(h => (JsonElement?)h).SingleOrDefault();
            var hook = hooks.SingleOrDefault(t => t.Name == name);
            if (hook == null) { Check("hook." + name + ".present", false); continue; }
            var patch = hook.CustomAttributes.Single(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
            var type = patch.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().Single();
            var method = patch.ConstructorArguments.Select(a => a.Value).OfType<string>().Single();
            Check("hook." + name + ".declared-target",
                spec == null || (type.FullName == spec.Value.GetProperty("type").GetString() && method == spec.Value.GetProperty("name").GetString()),
                type.FullName + "::" + method);
            // A setter patch names the property and asks for its accessor, so the member to find is the one the
            // metadata spells `set_<name>`; a getter patch is the same rule with `get_`, and everything else
            // patches the member the declaration names.
            var accessor = patch.ConstructorArguments.Where(a => a.Type.Name == "MethodType")
                .Select(a => Convert.ToInt32(a.Value, CultureInfo.InvariantCulture)).SingleOrDefault();
            var targetName = accessor == 1 ? "get_" + method : accessor == 2 ? "set_" + method : method;
            // A patch that names its argument types brackets exactly one of several bodies of the same name, so
            // the declaration's own argument list is the filter; without one the name alone must be unique. A
            // declaration that also names how each argument is passed marks a `ref` or `out` argument (Harmony's
            // `ArgumentType.Ref` = 1, `Out` = 2), which the game's own metadata spells as a by-reference type.
            var argumentTypes = patch.ConstructorArguments
                .Where(a => a.Value is CustomAttributeArgument[] list && list.All(item => item.Value is TypeReference))
                .SelectMany(a => (CustomAttributeArgument[])a.Value!)
                .Select(a => ((TypeReference)a.Value!).FullName).ToArray();
            var passing = patch.ConstructorArguments.Where(a => a.Type is ArrayType { ElementType.Name: "ArgumentType" })
                .SelectMany(a => (CustomAttributeArgument[])a.Value!).Select(a => Convert.ToInt32(a.Value, CultureInfo.InvariantCulture)).ToArray();
            if (passing.Length == argumentTypes.Length)
                argumentTypes = argumentTypes.Select((argument, index) => passing[index] is 1 or 2 ? argument + "&" : argument).ToArray();
            else if (passing.Length != 0) argumentTypes = new[] { "<argument kinds do not match the argument types>" };
            var named = gameTypes.Where(t => t.FullName == type.FullName).SelectMany(t => t.Methods.Where(m => m.Name == targetName));
            var targets = argumentTypes.Length == 0 ? named.ToArray()
                : named.Where(m => m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(argumentTypes, StringComparer.Ordinal)).ToArray();
            // The evidenced hooks are pinned to their recorded signature; a hook added after that evidence was
            // frozen is pinned to the one game method its patch declaration names, which is the same guarantee.
            Check("hook." + name + ".unique-game-method", targets.Length == 1 && (spec == null
                    ? targets[0].Name == targetName && type.Name == targets[0].DeclaringType.Name
                    : targets[0].FullName == spec.Value.GetProperty("signature").GetString()
                        && targets[0].IsVirtual == spec.Value.GetProperty("isVirtual").GetBoolean()),
                string.Join(" | ", targets.Select(m => m.FullName)));
            // A record that carries the build's own sharing count for its target body is pinned on it: 1 is a unique
            // entry, which is what makes the RVA a safe patch target. The w1 record predates that field.
            if (spec != null && spec.Value.TryGetProperty("rvaSharing", out var sharing))
                Check("hook." + name + ".unique-rva", sharing.GetInt32() == 1, spec.Value.GetProperty("rva").GetString() ?? "");
            var patches = hook.Methods.Where(m => m.CustomAttributes.Any(a => a.AttributeType.Namespace == "HarmonyLib"
                && a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler" or "HarmonyFinalizer")).ToArray();
            // A hook class carries one dispatched method — except where one native body is bracketed, which is the
            // receiver the remote-melee half patches: then the pair is the whole hook, one prefix and one postfix
            // on the same target, and nothing else is in the class.
            bool Prefix(MethodDefinition p) => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPrefix");
            bool Postfix(MethodDefinition p) => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyPostfix");
            bool Finalizer(MethodDefinition p) => p.CustomAttributes.Any(a => a.AttributeType.Name == "HarmonyFinalizer");
            var pair = patches.Length == 2 && patches.Count(Prefix) == 1 && patches.Count(Postfix) == 1;
            // Attribution and sentry selection must unwind even if the native body throws.
            var scope = patches.Length is 2 or 3 && patches.Count(Prefix) == 1
                && patches.Count(Finalizer) == 1 && patches.Count(Postfix) == patches.Length - 2;
            // A record that names its dispatch is the record for a hook whose body may be a prefix or a postfix.
            // A hook the frozen records do not carry declares its own dispatch and its own priority: the checks
            // below then hold it to the attributes it actually carries — exactly one dispatch, and a priority that
            // is one of the two this package uses — rather than to a record it does not have.
            var declared = spec != null && spec.Value.TryGetProperty("dispatch", out var dispatch)
                ? dispatch.GetProperty("kind").GetString()
                : spec == null ? null : "Postfix";
            var declaredPriority = spec != null && spec.Value.TryGetProperty("dispatch", out var priorityRecord)
                ? priorityRecord.GetProperty("priority").GetString()
                : spec == null ? null : "Last";
            // The methods the hook's own dispatch declares: one, or the prefix and the postfix of a bracketing pair.
            var dispatched = pair || scope || declared == null
                ? patches
                : patches.Where(p => p.CustomAttributes.Any(a => a.AttributeType.Name == "Harmony" + declared)).ToArray();
            var expectedPriority = declaredPriority == "First" ? 800 : declaredPriority == "Last" ? 0 : (int?)null;
            Check("hook." + name + ".dispatch", (patches.Length == 1 || pair || scope) && patches.All(p => p.IsStatic) && dispatched.Length > 0,
                string.Join(", ", patches.Select(p => p.Name)));
            // A body patch binds nothing it does not name: `__instance` is the target's own instance, so where a body
            // takes it, it has to be typed as the target. A folded body is one body shared by every type its record
            // names, so there `__instance` may be typed only as a base class all of those types derive from. Which
            // parameters a patched body declares beyond that is its own business — the loadout hooks read the session
            // and the patched array instead of the target.
            var foldedNames = spec != null && spec.Value.TryGetProperty("foldedWith", out var foldRecords)
                ? foldRecords.EnumerateArray().Select(foldRecord => foldRecord.GetProperty("type").GetString()!).ToArray()
                : Array.Empty<string>();
            bool DerivesFrom(string derived, string ancestor)
            {
                for (var current = gameTypes.SingleOrDefault(candidate => candidate.FullName == derived)?.BaseType; current != null;
                     current = gameTypes.SingleOrDefault(candidate => candidate.FullName == current.FullName)?.BaseType)
                    if (current.FullName == ancestor) return true;
                return false;
            }
            Check("hook." + name + ".instance-type", dispatched.All(p =>
            {
                var names = p.Parameters.Select(parameter => parameter.Name).ToArray();
                if (names.Length == 0 || names[0] != "__instance") return true;
                var instanceType = p.Parameters[0].ParameterType.FullName;
                return instanceType == type.FullName
                    || foldedNames.Length > 0 && new[] { type.FullName }.Concat(foldedNames)
                        .All(member => member == instanceType || DerivesFrom(member, instanceType));
            }), string.Join(" | ", dispatched.Select(p => p.Parameters.Select(parameter => parameter.Name + " " + parameter.ParameterType.FullName).Aggregate("", (all, one) => all + one + " "))));
            int? PriorityOf(MethodDefinition? method) => method?.CustomAttributes.SingleOrDefault(a => a.AttributeType.Name == "HarmonyPriority")
                is { } attribute && attribute.ConstructorArguments[0].Value is int value ? value : null;
            Check("hook." + name + ".priority", scope
                ? PriorityOf(patches.Single(Prefix)) == 800 && PriorityOf(patches.Single(Finalizer)) == 0
                    && patches.Where(Postfix).All(p => PriorityOf(p) == 0)
                : pair
                ? name == "ThrowUseChecked"
                    ? ThrowObservationPriorityIsExact(PriorityOf(patches.Single(Prefix)), PriorityOf(patches.Single(Postfix)))
                    : PriorityOf(patches.Single(Prefix)) == 800 && PriorityOf(patches.Single(Postfix)) == 0
                : expectedPriority == null
                    ? PriorityOf(dispatched.Length == 1 ? dispatched[0] : null) is 0 or 800
                    : PriorityOf(dispatched.Length == 1 ? dispatched[0] : null) == expectedPriority,
                string.Join(", ", dispatched.Select(p => p.Name + "=" + (PriorityOf(p)?.ToString() ?? "<none>"))));
            var body = dispatched.SelectMany(p => p.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()).ToArray();
            // Either the session is read as a property and asked once, or its own hook body is named; a body that
            // does neither would run native work with nothing to answer to.
            Check("hook." + name + ".guarded-by-session",
                body.Any(m => m.DeclaringType.Name == "WeaponNativeSession"
                    && m.Name is "get_Current" or "Guard" or "Offer")
                // The reload and inventory hooks reach the session through their own family's guard, which asks
                // the same session and applies its guard; either front door is the session being asked once. The
                // authored-field hooks do the same through `WeaponFieldHooks.Apply`, which is the one entry of that
                // family and the only place the session is asked for it.
                || body.Any(m => m.DeclaringType.Name == "ReloadInventoryHooks" && m.Name is "Guard" or "GuardInventory")
                || body.Any(m => m.DeclaringType.Name == "ReloadInventoryHooks" && m.Name == "ObservePoolHook")
                    && nativeTypes.Single(t => t.Name == "ReloadInventoryHooks").Methods.Single(m => m.Name == "ObservePoolHook")
                        .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                        .Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "Guard")
                || body.Any(m => m.DeclaringType.Name == "WeaponFieldHooks" && m.Name == "Apply")
                || body.Any(m => m.DeclaringType.Name == "EnemyScannerCallFields" && m.Name == "Enter")
                    && nativeTypes.Single(t => t.Name == "EnemyScannerCallFields").Methods.Single(m => m.Name == "Enter")
                        .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                        .Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "Guard")
                || body.Any(m => m.DeclaringType.Name == "WeaponDamagePacketEquipment" && m.Name == "Enter")
                // The reload sequence edit only acts inside the window the scope hook opens, and that hook opens it only
                // when the session offers the reload-speed feature.
                || body.Any(m => m.DeclaringType.Name == "ReloadSequenceBound" && m.Name == "Apply")
                    && nativeTypes.Single(t => t.Name == "ReloadSequenceScope").Methods.Single(m => m.Name == "Enter")
                        .Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                        .Any(m => m.DeclaringType.Name == "WeaponNativeSession" && m.Name == "Offer"),
                string.Join(", ", body.Where(m => m.DeclaringType.Name is "WeaponNativeSession" or "ReloadInventoryHooks" or "WeaponFieldHooks").Select(m => m.Name)));
        }

        // One bracket per Slot 151 body, and ForgeCore alone patches them. The build declares exactly four Fire bodies
        // and none of them reaches another (evidence/w4-player-fire.json): the package patches none of the four itself
        // (a second patch would count a single shot twice) and subscribes to each of ForgeCore's four brackets, so a
        // family whose body is not listened to would never produce a shot.
        var fireBodies = new[] { "Gear.BulletWeapon", "Gear.Shotgun", "Gear.BulletWeaponSynced", "Gear.ShotgunSynced" };
        var ownFirePatches = hooks.Where(hook => hook.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch"
            && a.ConstructorArguments.Select(x => x.Value).OfType<TypeReference>().Any(type => fireBodies.Contains(type.FullName))
            && a.ConstructorArguments.Select(x => x.Value).OfType<string>().Any(name => name == "Fire"))).Select(hook => hook.Name).ToArray();
        var subscribedFireBrackets = packageTypes.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Select(i => i.Operand).OfType<FieldReference>()
            .Where(field => field.DeclaringType.FullName == "ForgeCore.GameHooks/Fire").Select(field => field.Name).Distinct(StringComparer.Ordinal).ToArray();
        Check("native.fire-bodies-patched-by-core-alone", ownFirePatches.Length == 0
            && subscribedFireBrackets.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "BulletWeapon", "BulletWeaponSynced", "Shotgun", "ShotgunSynced" }),
            "own patches: " + string.Join(", ", ownFirePatches) + "; subscribed: " + string.Join(", ", subscribedFireBrackets));

        // A part pose names an `eGearComponent` member, and the number that reaches `GearIDRange.GetCompID` is the
        // production enum's own value: every slot the package can name must carry the build's number, and the two
        // enums must agree on the members, so a pose can never address a different slot than it names.
        var gameComponents = gameTypes.Where(t => t.FullName == "Gear.eGearComponent").SelectMany(t => t.Fields)
            .Where(f => f.HasConstant).ToDictionary(f => f.Name, f => Convert.ToInt32(f.Constant), StringComparer.Ordinal);
        var packageSlots = nativeTypes.Where(t => t.Name == "GearPartSlot").SelectMany(t => t.Fields)
            .Where(f => f.HasConstant).ToDictionary(f => f.Name, f => Convert.ToInt32(f.Constant), StringComparer.Ordinal);
        var slotMismatch = packageSlots.Where(s => !gameComponents.TryGetValue(s.Key, out var value) || value != s.Value)
            .Select(s => s.Key + "=" + s.Value + " vs " + (gameComponents.TryGetValue(s.Key, out var v) ? v.ToString() : "<absent>")).ToArray();
        Check("native.gear-part-slots-exact", packageSlots.Count == 22 && slotMismatch.Length == 0,
            slotMismatch.Length == 0 ? packageSlots.Count + " slots" : string.Join(", ", slotMismatch));

        // The domain reads both gates the engine publishes: the host's gameplay phase and the kernel's scoped owner callback.
        Check("weapon.gameplay-gate-from-host", calls.Any(m => Member(m) == "ForgeEngine.Plugin::get_CanExecuteGameplay"));
        Check("weapon.owner-gate-from-runtime", calls.Any(m => Member(m) == "ForgeEngine.Plugin::get_CanExecuteOwnerGameplay")); return checks;
    }

    private sealed record CheckRow(string Id, bool Passed, string Detail);
}
