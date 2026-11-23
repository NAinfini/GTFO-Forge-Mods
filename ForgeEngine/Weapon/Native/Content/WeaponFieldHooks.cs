using System;
using System.Collections.Generic;
using System.Linq;
using AIGraph;
using GameData;
using Gear;
using HarmonyLib;
using Player;
using ForgeEngine.Framework;

namespace ForgeWeapon.Native;

/// <summary>The authored-field hooks: one postfix per native generation point, each writing the document's own
/// values on the instance the game just built. They are declared here rather than in `WeaponNativeHooks` because
/// every one of them names a game type the offline suites' doubles do not carry, and this file is compiled only
/// into the Native project; `WeaponNativeHooks.NativeFieldHookTypes` hands the list in where it exists.
///
/// Every body reads the gear's own offline identity (consumables use their own item row) and writes through
/// the one applier the session built. Nothing is observed here and no
/// fact is published: these are the presentation and local-state members the research marked `identical`, so each
/// peer writes the identical document value.</summary>
internal static partial class WeaponFieldHooks
{
    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]
    {
        typeof(ItemEquippableSetup), typeof(EnemyScannerSetup), typeof(MineDeployerFirstPersonSetup),
        typeof(MineDeployerInstanceSetup), typeof(SentryGunFirstPersonSetup), typeof(SentryGunInstanceSetup),
        typeof(LockMelterFirstPersonSetup), typeof(GlueGunFieldSetup),
        typeof(EquipmentWielded), typeof(EquipmentFieldsDestroyed), typeof(SentryArchetypeSelected),
        typeof(SentryDetectionSetupScope), typeof(SentryFiringSetupScope),
        typeof(FogRepellerInstanceStart), typeof(InteractTimedSelected)
    });

    /// <summary>The enemy scanner's four per-frame wrappers as one unit, put on by <see cref="WeaponLazyHooks"/> the
    /// first time an authored scanner block exists: the scanner's own update (which inlines the node refresh), the
    /// node refresh and the tag progress for direct calls, and the bot's scan state. Without such a block the
    /// scanner runs the game's own bodies untouched.</summary>
    internal static IReadOnlyList<Type> ScannerUnit { get; } = Array.AsReadOnly(new[]
    {
        typeof(EnemyScannerCallFields), typeof(EnemyScannerTagFields), typeof(EnemyScannerBotCallFields)
    });

    internal static void Contribute(List<Type> types) => types.AddRange(Types);

    /// <summary>The unit goes on when the published applier carries a scanner block.</summary>
    internal static void EnsureScanner(WeaponFieldSet fields)
    {
        if (fields.HasScannerBlock) WeaponLazyHooks.Ensure(WeaponLazyHooks.Unit.Scanner);
    }

    /// <summary>One instance finished its own `Setup`. The session guard is the same readiness gate every other
    /// hook uses: nothing is written before the session is wired or after it faulted.</summary>
    internal static void Apply(WeaponFeature feature, ItemEquippable? item, ItemDataBlock? data)
        => WeaponNativeSession.Offer(feature)?.Guard(static (_, call) => Applier?.ApplyItem(call.Item1, call.Item2), (item, data == null ? 0u : data.persistentID));

    internal static void Apply(WeaponFeature feature, ItemEquippable item) => Apply(feature, item, item.ItemDataBlock);

    internal static ArchetypeDataBlock? SentryArchetype(SentryGunInstance sentry)
    {
        if (Applier == null || !Applier.TryEntry(sentry, sentry.ItemDataBlock?.persistentID ?? 0, out var entry)
            || !entry.TryBlock("forge.etc.Sentry", out var block)) return null;
        var text = block.Values.Text("ArchetypeID");
        if (!uint.TryParse(text, out var id)) return null;
        return ArchetypeDataBlock.GetBlock(id)
            ?? throw new InvalidOperationException("Sentry archetype is missing: " + id);
    }

    /// <summary>A thrown fog repeller started; it is an `Item`, not an equipment instance, so it has its own entry.</summary>
    internal static void Apply(WeaponFeature feature, FogRepellerInstance? repeller)
        => WeaponNativeSession.Offer(feature)?.Guard(static (_, fog) => Applier?.ApplyFogRepeller(fog), repeller);

    /// <summary>A timed interaction's selection changed.</summary>
    internal static void Apply(WeaponFeature feature, Interact_Timed? interaction, bool selected)
    {
        WeaponNativeSession.Offer(feature)?.Guard(static (_, call) => Applier?.ApplyInteraction(call.Item1, call.Item2), (interaction, selected));
    }
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.Setup))]
internal static class GlueGunFieldSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GlueGun __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance, data);
}

// ForgeCore patches the equipment's gear-spawn completion once (`GameHooks.GearSpawnCompleted`); the fields are
// written after every other patch of it.
internal static class EquipmentFieldsReady
{
    internal static void Subscribe() => ForgeCore.GameHooks.GearSpawnCompleted.After(Ready);

    private static void Ready(ItemEquippable item, bool ran) => WeaponFieldHooks.Apply(WeaponFeature.Equipment, item);
}

/// <summary>The package's one patch on the wield body, shared by its two readers in the order they ran when they were
/// two classes: the reload flag the base body left (ReloadState, only when the body ran), then the authored fields
/// (Equipment). Each reader keeps its own feature gate, so a withdrawn feature leaves the other one running.</summary>
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnWield))]
internal static class EquipmentWielded
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance, bool __runOriginal)
    {
        if (__runOriginal)
            ReloadInventoryHooks.Guard(WeaponFeature.ReloadState, static (reload, item) => ReloadInventoryHooks.FlagRead(reload, item), __instance);
        WeaponFieldHooks.Apply(WeaponFeature.Equipment, __instance);
    }
}

/// <summary>The native equipment object is the scanner-cache lifetime. Remove its pointer before the game tears
/// the object down so a later life cannot inherit the previous work's scanner inputs if the address is reused.</summary>
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.OnDestroy))]
internal static class EquipmentFieldsDestroyed
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(ItemEquippable __instance)
        => WeaponNativeSession.Offer(WeaponFeature.Equipment)?.Guard(static (_, item) =>
            WeaponFieldHooks.Applier?.ForgetScanner(item), __instance);
}

// Native detection/firing Setup each select and cache their row before the host receives it.
// Replace only those scoped query results; never repair caches after native derived values were built.
internal static class SentryInitializationScope
{
    internal sealed class Spawn(SentryGunInstance item, ArchetypeDataBlock? selected, Func<bool> current)
    {
        internal readonly SentryGunInstance Item = item;
        internal readonly ArchetypeDataBlock? Selected = selected;
        internal readonly Func<bool> Current = current;
    }

    internal sealed class SetupCall(Spawn spawn, eWeaponFireMode fireMode)
    {
        internal readonly Spawn Spawn = spawn;
        internal readonly eWeaponFireMode FireMode = fireMode;
    }

    [ThreadStatic] internal static Spawn? Current;
    [ThreadStatic] internal static SetupCall? Active;

    internal static void Enter(iSentrygunInstanceCore core, IntPtr component, eWeaponFireMode fireMode, bool firing)
    {
        // Even a non-authored nested Setup must mask its caller's selection until its finalizer.
        Active = null;
        var spawn = Current;
        if (spawn == null || spawn.Selected == null || !spawn.Current()) return;
        var item = core.TryCast<SentryGunInstance>();
        if (item == null || item.Pointer != spawn.Item.Pointer) return;
        var expected = firing ? spawn.Item.m_firing?.Pointer : spawn.Item.m_detection?.Pointer;
        if (expected == null || expected.Value != component) return;
        Active = new SetupCall(spawn, fireMode);
    }

    internal static void Select(eWeaponFireMode fireMode, ref ArchetypeDataBlock result)
    {
        var active = Active;
        if (active != null && ReferenceEquals(Current, active.Spawn)
            && active.FireMode == fireMode && active.Spawn.Selected is { } selected
            && active.Spawn.Current()) result = selected;
    }
}

/// <summary>The sentry's gear-spawn completion, the <c>GearSpawnComplete</c> step of ForgeCore's <c>GameHooks.SentryLifecycle</c>
/// (the other steps pass through untouched): the spawn the selected archetype belongs to is current for the whole body, the fields
/// are written once it returned, and the spawn that was current before the call is current again however the body ended.</summary>
internal static class SentryFieldsReady
{
    internal static void Subscribe() => ForgeCore.GameHooks.SentryLifecycle.Around(Enter, Done, Faulted);

    private static (bool Spawn, SentryInitializationScope.Spawn? Previous) Enter(ForgeCore.SentryCall call)
    {
        if (call.Step != ForgeCore.SentryStep.GearSpawnComplete) return (false, null);
        var sentry = call.Sentry;
        var previous = SentryInitializationScope.Current;
        SentryInitializationScope.Current = null;
        WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(_ =>
        {
            var current = WeaponNativeSession.CaptureSentryInitialization();
            var selected = WeaponFieldHooks.SentryArchetype(sentry);
            if (current != null && current())
                SentryInitializationScope.Current = new(sentry, selected, current);
        });
        return (true, previous);
    }

    private static void Done(ForgeCore.SentryCall call, (bool Spawn, SentryInitializationScope.Spawn? Previous) state, bool ran)
    {
        if (!state.Spawn) return;
        var sentry = call.Sentry;
        try
        {
            WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(_ =>
            {
                var scope = SentryInitializationScope.Current;
                if (scope != null && scope.Item.Pointer == sentry.Pointer && scope.Current())
                    WeaponFieldHooks.Apply(WeaponFeature.Sentry, sentry);
            });
        }
        finally { SentryInitializationScope.Current = state.Previous; }
    }

    private static void Faulted(ForgeCore.SentryCall call, (bool Spawn, SentryInitializationScope.Spawn? Previous) state, Exception error)
    {
        if (state.Spawn) SentryInitializationScope.Current = state.Previous;
    }
}

[HarmonyPatch(typeof(SentryGunInstance_Detection), nameof(SentryGunInstance_Detection.Setup),
    new[] { typeof(iSentrygunInstanceCore), typeof(eWeaponFireMode), typeof(AIG_CourseNode) })]
internal static class SentryDetectionSetupScope
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(SentryGunInstance_Detection __instance, iSentrygunInstanceCore core,
        eWeaponFireMode fireMode, out SentryInitializationScope.SetupCall? __state)
    {
        __state = SentryInitializationScope.Active;
        SentryInitializationScope.Active = null;
        WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(_ =>
            SentryInitializationScope.Enter(core, __instance.Pointer, fireMode, firing: false));
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer(SentryInitializationScope.SetupCall? __state)
        => SentryInitializationScope.Active = __state;
}

[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.Setup),
    new[] { typeof(iSentrygunInstanceCore), typeof(eWeaponFireMode), typeof(WeaponAudioDataBlock) })]
internal static class SentryFiringSetupScope
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(SentryGunInstance_Firing_Bullets __instance, iSentrygunInstanceCore core,
        eWeaponFireMode fireMode, out SentryInitializationScope.SetupCall? __state)
    {
        __state = SentryInitializationScope.Active;
        SentryInitializationScope.Active = null;
        WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(_ =>
            SentryInitializationScope.Enter(core, __instance.Pointer, fireMode, firing: true));
    }

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static void Finalizer(SentryInitializationScope.SetupCall? __state)
        => SentryInitializationScope.Active = __state;
}

[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.GetArchetypeDataForFireMode))]
internal static class SentryArchetypeSelected
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(eWeaponFireMode fireMode, ref ArchetypeDataBlock __result)
    {
        var candidate = __result;
        bool completed = false;
        WeaponNativeSession.Offer(WeaponFeature.Sentry)?.Guard(_ =>
        {
            SentryInitializationScope.Select(fireMode, ref candidate);
            completed = true;
        });
        if (completed) __result = candidate;
    }
}

internal static partial class WeaponNativeHooks
{
    static partial void NativeFieldHookTypes(List<Type> types) => WeaponFieldHooks.Contribute(types);
    static partial void NativeFieldSubscriptions()
    {
        SentryFieldsReady.Subscribe();
        EquipmentFieldsReady.Subscribe();
    }
}

/// <summary>The Native project's half of the session: the documents the session read become this build's writer.
/// Where this file is not compiled the call compiles away and the package reads the documents without writing.</summary>
internal static partial class WeaponLazyHooks
{
    static partial void NativeUnits(List<Type[]> units) => units.Add(WeaponFieldHooks.ScannerUnit.ToArray());
}

internal sealed partial class WeaponNativeSession
{
    static partial void NativeFieldApplierReady(WeaponFieldSet fields, WeaponLog log)
        => WeaponFieldHooks.Ready(fields, log);

    static partial void NativeScannerHooks(WeaponFieldSet fields) => WeaponFieldHooks.EnsureScanner(fields);

    static partial void NativeInteractionLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.WorldChanged or RuntimeLifecycleKind.CheckpointRestored
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            WeaponFieldHooks.Applier?.RestoreInteractions();
    }

    internal static Func<bool>? CaptureSentryInitialization()
    {
        var session = Current;
        if (session == null || session.Inert) return null;
        var world = session._kernel.WorldEpoch;
        var generation = session._contentGeneration;
        return () => ReferenceEquals(Current, session) && !session.Inert
            && session._kernel.WorldEpoch == world && session._contentGeneration == generation;
    }
}

/// <summary>The base point every equipment instance passes through. A concrete kind that overrides `Setup` and
/// does not call this body reaches its own patch below instead, and every write is an absolute `set`, so a body
/// that is entered twice lands on the same numbers.</summary>
[HarmonyPatch(typeof(ItemEquippable), nameof(ItemEquippable.Setup))]
internal static class ItemEquippableSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(ItemEquippable __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Equipment, __instance, data);
}

/// <summary>The scanner's own setup, after the members the document writes exist.</summary>
[HarmonyPatch(typeof(EnemyScanner), nameof(EnemyScanner.Setup))]
internal static class EnemyScannerSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(EnemyScanner __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance, data);
}

/// <summary>The mine's first-person half: the placement and pickup timing and the placement cooldown.</summary>
[HarmonyPatch(typeof(MineDeployerFirstPerson), nameof(MineDeployerFirstPerson.Setup))]
internal static class MineDeployerFirstPersonSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerFirstPerson __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance, data);
}

/// <summary>The placed mine itself: its detonation payload, its laser beam and its light. This body runs after
/// the instance assembled those components, which is why they can be reached from here.</summary>
[HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.Setup))]
internal static class MineDeployerInstanceSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance, data);
}

/// <summary>The sentry's first-person half: the placement and pickup timing.</summary>
[HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.Setup))]
internal static class SentryGunFirstPersonSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunFirstPerson __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Sentry, __instance, data);
}

/// <summary>The deployed sentry: its scan delay and its scanner plane's colour.</summary>
[HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.Setup))]
internal static class SentryGunInstanceSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Sentry, __instance, data);
}

/// <summary>The lock melter's own setup: the burn time its own hold timer counts to.</summary>
[HarmonyPatch(typeof(LockMelterFirstPerson), nameof(LockMelterFirstPerson.Setup))]
internal static class LockMelterFirstPersonSetup
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LockMelterFirstPerson __instance, ItemDataBlock data) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance, data);
}

/// <summary>A thrown fog repeller's `Start`: the repeller sphere exists and has not begun to grow.</summary>
[HarmonyPatch(typeof(FogRepellerInstance), nameof(FogRepellerInstance.Start))]
internal static class FogRepellerInstanceStart
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(FogRepellerInstance __instance) => WeaponFieldHooks.Apply(WeaponFeature.Devices, __instance);
}

/// <summary>A timed interaction was selected by a player on this machine — the point before the hold starts. Only a
/// lock's hack interaction is written; every other interaction passes through untouched.</summary>
[HarmonyPatch(typeof(Interact_Timed), nameof(Interact_Timed.OnSelectedChange))]
internal static class InteractTimedSelected
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(Interact_Timed __instance, bool selected) => WeaponFieldHooks.Apply(WeaponFeature.Equipment, __instance, selected);
}


internal static partial class WeaponFieldHooks
{
    internal static WeaponFieldApplier? Applier { get; private set; }
    private static WeaponNativeSession? _owner;

    internal static void Ready(WeaponFieldSet fields, WeaponLog log)
    {
        // Before Bind, hooks and Current are not installed and this pending applier cannot write natives.
        // An abandoned, unbound preparation may be replaced. A bound owner must explicitly detach.
        if (_owner != null)
            throw new InvalidOperationException("The previous weapon field owner has not detached.");
        Applier = new WeaponFieldApplier(fields, log);
    }

    internal static void Bind(WeaponNativeSession owner)
    {
        if (Applier == null || _owner != null && !ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("The pending weapon field writer has another session owner.");
        _owner = owner;
    }

    internal static void RequireOwner(WeaponNativeSession owner)
    {
        if (!ReferenceEquals(_owner, owner))
            throw new InvalidOperationException("The weapon session no longer owns its field writer.");
    }

    /// <summary>Cold preparation for a content candidate. Live exact-instance bindings are re-resolved before any
    /// native table is committed; failure leaves the published applier and all of its bindings untouched.</summary>
    internal static void PrepareScannerBindings(WeaponFieldApplier candidate)
        => candidate.PrepareScannerBindings(Applier);

    // The caller checked ownership before native content publication.
    // Candidate preparation already rebuilt its live instance cache. This commit remains an assignment; no cleanup,
    // allocation or new rejection follows native publication.
    internal static void Use(WeaponFieldApplier applier) => Applier = applier;

    internal static void Detach(WeaponNativeSession owner)
    {
        if (!ReferenceEquals(_owner, owner)) return;
        try { Applier?.RestoreInteractions(); }
        finally { Applier = null; _owner = null; }
    }
}

// Update contains an inlined RefreshNodeEnemyList in this build, which has no caller of
// its own, so Update carries the refresh; UpdateTagProgress is patched for its direct calls. Finalizers run even when a prefix or the native body throws.
[HarmonyPatch(typeof(EnemyScanner), "Update")]
internal static class EnemyScannerCallFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(EnemyScanner __instance, out WeaponScannerCall __state)
        => Enter(__instance, out __state);

    internal static void Enter(ItemEquippable? item, out WeaponScannerCall state)
    {
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.WeaponScanner);
        // WHY: P5 — a snapshot with no scanner block and no open call has nothing to write or mask, so the string
        // reads and lookups of PrepareScannerCall are skipped; an open call still reaches it for the nested mask.
        if (WeaponFieldHooks.Applier is { ScannerCallIdle: true }) { state = default; return; }
        state = WeaponNativeSession.Offer(WeaponFeature.Devices)?.Guard(static (_, scanner) =>
            ApplierCall(scanner), item, default(WeaponScannerCall)) ?? default;
        state.Apply();
    }

    private static WeaponScannerCall ApplierCall(ItemEquippable? item)
        => WeaponFieldHooks.Applier?.PrepareScannerCall(item) ?? default;

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static Exception? Finalizer(Exception? __exception, WeaponScannerCall __state)
        => __state.Restore(__exception);
}

[HarmonyPatch(typeof(EnemyScanner), "UpdateTagProgress")]
internal static class EnemyScannerTagFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(EnemyScanner __instance, out WeaponScannerCall __state)
        => EnemyScannerCallFields.Enter(__instance, out __state);
    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Exception? __exception, WeaponScannerCall __state)
        => __state.Restore(__exception);
}

// BotGetScanResult/BotTag are static and receive no equipment identity. Their real
// caller owns m_tool, and also reads s_tagDuration inline. Scope that whole native
// call so both the query and the bot's tag timer use the same authored tool.
[HarmonyPatch(typeof(PlayerBotActionUseEnemyScanner), "UpdateStateScan")]
internal static class EnemyScannerBotCallFields
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    internal static void Prefix(PlayerBotActionUseEnemyScanner __instance, out WeaponScannerCall __state)
        => EnemyScannerCallFields.Enter(__instance.m_tool, out __state);

    [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
    internal static Exception? Finalizer(Exception? __exception, WeaponScannerCall __state)
        => __state.Restore(__exception);
}

