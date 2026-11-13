using BepInEx;
using BepInEx.Unity.IL2CPP;
using Gear;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using System.Runtime.InteropServices;

namespace ForgeWeaponEnergyLab.Native;

[BepInPlugin(Id, "Forge Weapon Energy Lab", Version)]
[BepInDependency("dev.gtfomodding.gtfo-api", ">=0.6.1")]
[BepInDependency("NAinfini.ForgeCore", ">=1.0.0")]
// MTFO marks the game as modded (achievements and public matchmaking off), so the guns stay out of official play.
[BepInDependency("com.dak.MTFO")]
internal sealed class Plugin : BasePlugin
{
    internal const string Id = EnergyLab.PluginId;
    internal const string Version = EnergyLab.Version;
    /// <summary>Errors only; `ErrorOnce` writes each exact text once.</summary>
    internal static readonly ForgeCore.ModLog Logger = ForgeCore.Log.For(Id);

    public override void Load()
    {
        EnergyLab.Initialize(System.IO.Path.Combine(Paths.ConfigPath, "ForgeWeapon_EnergyLab"));
        // Queue this package's own sounds before any definition pack: ForgeAudio decodes one library at a time, so a
        // third-party pack's slow or stuck decode must never be in front of the sounds every energy gun needs.
        Presentation.EnergyAudio.Load();
        LoadDefinitionFiles();
        var harmony = new Harmony(Id);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            EnergyGameHooks.Subscribe();
            EnergyMuzzleFlash.Register();
            EnergyGearRegistry.Register();
            EnergyNetwork.Register(EnergyController.Start(Id));
        }
        catch (System.Exception error)
        {
            Logger.ErrorOnce("Energy Lab initialization failed: " + error);
            harmony.UnpatchSelf();
            throw;
        }
    }

    public override bool Unload() => false;

    // Every ForgeEnergyLab folder under the plugins tree (this package's own, content packs and loose files) and the active rundown's
    // CustomPath/ForgeEnergyLab (where Settings.forge, the rundown's rules, is read from). A problem skips one entry and is logged; the other guns still load.
    private static void LoadDefinitionFiles()
    {
        var errors = new System.Collections.Generic.List<string>();
        var folders = new System.Collections.Generic.SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ForgeCore.ContentFolders.Find(EnergyContent.FolderName, errors)) folders.Add(folder);
        string? rundownFolder = null;
        if (MTFO.API.MTFOPathAPI.HasCustomPath)
        {
            var rundown = System.IO.Path.GetFullPath(System.IO.Path.Combine(MTFO.API.MTFOPathAPI.CustomPath, EnergyContent.FolderName));
            if (System.IO.Directory.Exists(rundown)) { folders.Add(rundown); rundownFolder = rundown; }
        }
        foreach (var (definition, folder) in EnergyContent.Load(folders, errors, rundownFolder))
        {
            EnergyText.Bind(definition.Id, folder);
            // A gun's own sounds: the audio folder beside its definition file, when it has a cues.forge.
            Presentation.EnergyAudio.BindPack(definition.Id, folder);
        }
        // The rundown's own rules (Settings.forge): the guns players may choose and the guns' numbers, before registration closes.
        EnergyRules.Read(rundownFolder, errors);
        foreach (var error in errors) Logger.ErrorOnce(error);
    }
}

// Hooks that serve only energy guns go on the first time one is in a hand and stay: the game keeps a detour for a patched method after
// an unpatch, so taking them off would not make the method any cheaper. A session without energy guns never pays for them.
internal static class EnergyLazyHooks
{
    internal static readonly ForgeCore.LazyPatches Hooks = ForgeCore.Patches.Lazy(Plugin.Id, harmony =>
    {
        // The local player's first-person holder: the sight after the game's LateUpdate placed the gun (ForgeVisual's GunMount puts the hand pose in before it).
        ForgeCore.GameHooks.FirstPersonHolderLateUpdate.After(HolderPose.After);
        ForgeCore.GameHooks.MagazineVisibility.After((call, _) => Presentation.WeaponModels.NoteMagazineVisible(call.Weapon, call.Visible));
        NativeEnergyBotShotReady.Install(harmony);
        NativeEnergyShotReady.Install();
    });
}

// The game's offline gear list must see the new rows on its first pass even if another loader changes
// GameData initialization order. Install is idempotent for a second GTFO-API callback.
[HarmonyPatch(typeof(GearManager), nameof(GearManager.LoadOfflineGearDatas))]
internal static class RegisterBeforeOfflineGearLoad
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix() => EnergyGearRegistry.Install();
}

[HarmonyPatch(typeof(GearManager), nameof(GearManager.OnGearLoadingDone))]
internal static class AddEnergyGunsToLoadout
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(GearManager __instance) => EnergyGearRegistry.EnsureLoadout(__instance);
}

// Saved loadouts are parsed from JSON; an energy gun saved by an earlier build takes the current components.
[HarmonyPatch(typeof(GearIDRange), nameof(GearIDRange.TryApplyCompJSON))]
internal static class CurrentSavedEnergyPacket
{
    [HarmonyPostfix]
    private static void Postfix(GearIDRange __instance, bool __result)
    {
        if (!__result) return;
        try { EnergyGearRegistry.UseCurrentPacket(__instance); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Saved energy gun packet update failed: " + error); }
    }
}

// Gear assembly sets the shared base-item ItemFPSSettings; energy guns then take their private copy.
[HarmonyPatch(typeof(GearBuilder), nameof(GearBuilder.AssembleGearAsync))]
internal static class EnergyItemFpsSettings
{
    // Every assembly, including a packet that never passed through JSON, builds the current energy gun.
    [HarmonyPrefix]
    private static void Prefix(GearIDRange gearIDRange)
    {
        try { EnergyGearRegistry.UseCurrentPacket(gearIDRange); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy gun packet update failed: " + error); }
    }

    [HarmonyPostfix]
    private static void Postfix(GearIDRange gearIDRange, ItemEquippable __result)
    {
        try { EnergyItemFps.Apply(__result, gearIDRange); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy first-person settings failed: " + error); }
    }
}


// Auto.Update keeps native input, empty-clip and reload handling. Orbs fire only on our release input;
// a busy projectile slot is an execution budget, never a second cadence or ammunition gate.
//
// BWA_Auto.PreFireCheck is not hooked at its code: the game's build gives it the same native body as unrelated bool
// getters (`return this+0x51`, shared with TwoAxisInputControl.get_State / get_IsPressed and others read every frame),
// so a detour there would run for every input read with an instance of another type. Only BWA_Auto's own dispatch
// entries are redirected: its vtable slot (BulletWeaponArchetype.Update calls PreFireCheck through it) and its
// MethodInfo (reflection and delegates). The shared body and every other method folded into it stay untouched.
internal static unsafe class NativeEnergyShotReady
{
    private static Il2CppMethodInfo* _method;
    private static VirtualInvokeData* _slot;
    private static System.IntPtr _original;
    // The most recent archetype the trigger check ran for, with its gun and that gun's energy definition (null for a stock gun).
    // A destroyed gun reads as null, which forces a new lookup.
    private static System.IntPtr _self;
    private static BulletWeapon? _weapon;
    private static EnergyGear? _gear;

    internal static void Install()
    {
        if (_method != null) return;
        var klass = Il2CppClassPointerStore<BWA_Auto>.NativeClassPtr;
        // The runtime fills a class's vtable when the class is initialized, which plugin Load precedes. Allocating an
        // instance always initializes the class first (the object is garbage afterwards; no constructor runs).
        IL2CPP.il2cpp_runtime_class_init(klass);
        IL2CPP.il2cpp_object_new(klass);
        var method = (Il2CppMethodInfo*)IL2CPP.GetIl2CppMethod(klass, false, "PreFireCheck", "System.Boolean");
        if (method == null) throw new System.MissingMethodException("BWA_Auto.PreFireCheck has no MethodInfo.");
        var info = UnityVersionHandler.Wrap(method);
        var type = UnityVersionHandler.Wrap((Il2CppClass*)klass);
        if (info.Slot >= type.VtableCount)
            throw new System.InvalidOperationException($"BWA_Auto vtable has {type.VtableCount} slots, none at {info.Slot}.");
        var slot = (VirtualInvokeData*)type.VTable + info.Slot;
        // The slot must dispatch to this very method at its unmodified address (an unfilled slot cannot); anything else
        // is refused, never overwritten. This content check is the readiness check: the class struct's initialized
        // bit is not consulted.
        if (slot->method != method || slot->methodPtr != info.MethodPointer)
            throw new System.InvalidOperationException(
                $"BWA_Auto vtable slot {info.Slot} does not dispatch to PreFireCheck: slot method 0x{(long)slot->method:X} ptr 0x{(long)slot->methodPtr:X}, method 0x{(long)method:X} ptr 0x{(long)info.MethodPointer:X}, vtable initialized {type.IsVtableInitialized}.");
        _original = info.MethodPointer;
        var hook = (System.IntPtr)(delegate* unmanaged<System.IntPtr, Il2CppMethodInfo*, byte>)&PreFireCheck;
        slot->methodPtr = hook;
        info.MethodPointer = hook;
        _method = method;
        _slot = slot;
    }

    [UnmanagedCallersOnly]
    private static byte PreFireCheck(System.IntPtr self, Il2CppMethodInfo* method)
    {
        // The game's own answer (the held trigger). A released trigger needs no energy check and no managed wrapper.
        var held = ((delegate* unmanaged<System.IntPtr, Il2CppMethodInfo*, byte>)_original)(self, method);
        if (held == 0) return 0;
        try
        {
            // The gun this archetype belongs to is looked up once per archetype, not wrapped again on every held-trigger frame.
            if (self != _self || _weapon == null)
            {
                var weapon = new BWA_Auto(self).m_weapon;
                _self = self; _weapon = weapon; _gear = EnergyGearRegistry.DefinitionOf(weapon);
            }
            if (_gear is not { } gear) return held;
            return gear.Mode is not (EnergyMode.PlasmaBlast or EnergyMode.PlasmaArc) && EnergyController.CanLaunch(_weapon!, gear) ? held : (byte)0;
        }
        catch (System.Exception error)
        {
            Plugin.Logger.ErrorOnce("Energy shot gate failed: " + error);
            return held;
        }
    }
}

// Installed by EnergyLazyHooks.
internal static class HolderPose
{
    internal static void After(FirstPersonItemHolder holder, bool ran)
    {
        try { Presentation.WeaponModels.TickSight(holder); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy sight update failed: " + error); }
    }
}

// The game methods the energy guns read that ForgeCore patches once for every package (`ForgeCore.GameHooks`). Subscribed at
// plugin load; each handler only acts for an energy gun.
internal static class EnergyGameHooks
{
    internal static void Subscribe()
    {
        ForgeCore.GameHooks.GearPartsSpawned.After(MountModel);
        ForgeCore.GameHooks.BulletWeaponWielded.After(BindWieldModel);
        ForgeCore.GameHooks.BulletWeaponUnwielded.After(NoteUnwield);
        ForgeCore.GameHooks.ReloadTriggered.Before(BindReloadModel);
        // After every other patch of the gear-spawn completion.
        ForgeCore.GameHooks.GearSpawnCompleted.After(BindInventoryModel);
        NativeEnergyHit.Subscribe();
        NativeEnergyFireEntry.Subscribe();
        NativeEnergyRay.Subscribe();
    }

    private static void MountModel(GearPartHolder holder, bool ran)
    {
        try { Presentation.WeaponModels.Ensure(holder); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy model mount failed: " + error); }
    }

    private static void BindWieldModel(BulletWeapon weapon, bool ran)
    {
        EnergyController.NoteWield(weapon, true);
        if (EnergyGearRegistry.DefinitionOf(weapon) == null) return;
        Presentation.WeaponModels.NoteHeld(weapon, true);
        EnergyLazyHooks.Hooks.Ensure();
        try { Presentation.WeaponModels.BindWeapon(weapon); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy wield model binding failed: " + error); }
    }

    private static void BindInventoryModel(ItemEquippable item, bool ran)
    {
        EnergyMuzzleFlash.Queue(item);
        // The model bind owns its registration snapshot and rollback, including deferred asset mounts.
        try { Presentation.WeaponModels.BindWeapon(item); }
        catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy native socket binding failed: " + error); }
    }

    // The held gun leaving the hand ends the controller's armed state without a per-frame look at the hand.
    private static void NoteUnwield(BulletWeapon weapon, bool ran)
    {
        EnergyController.NoteWield(weapon, false);
        Presentation.WeaponModels.NoteHeld(weapon, false);
    }

    private static void BindReloadModel(ItemEquippable item)
    {
        if (item.GearCategoryData == null || EnergyGears.ModeForCategory(item.GearCategoryData.persistentID) == EnergyMode.Off) return;
        if (item.IsReloading) return;
        if (item.IsFirstPerson)
        {
            try { Presentation.WeaponModels.BindWeapon(item); }
            catch (System.Exception error) { Plugin.Logger.ErrorOnce("Energy reload model binding failed: " + error); }
        }
    }
}
