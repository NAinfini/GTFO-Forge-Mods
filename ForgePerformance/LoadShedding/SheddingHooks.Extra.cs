using CellMenu;
using Enemies;
using Gear;
using HarmonyLib;
using IRF;
using LevelGeneration;
using UnityEngine;
using FX_EffectSystem;

namespace ForgePerformance.LoadShedding;

// Rare-event hooks of the extra cuts. Every body is one null-checked call into ExtraCuts, which catches
// and reports its own failures, so a failed cut can never change the original call. The two IRF hooks
// (IrfDraw, IrfLateUpdate) run once per instanced-render feature per frame: each reads one static flag first
// and does nothing else while that flag is off.
internal sealed partial class SheddingHooks
{
    [HarmonyPatch(typeof(LG_ComputerTerminal), nameof(LG_ComputerTerminal.OnDestroy))]
    private static class LeakTerminal
    { [HarmonyPostfix] private static void Postfix(LG_ComputerTerminal __instance) => LoadSheddingController.Current?.Extras?.LeakTerminal(__instance); }

    [HarmonyPatch(typeof(CM_ScrollWindow), nameof(CM_ScrollWindow.ResetHeaders))]
    private static class LeakScrollHeaders
    { [HarmonyPostfix] private static void Postfix(CM_ScrollWindow __instance) => LoadSheddingController.Current?.Extras?.LeakScrollHeaders(__instance); }

    [HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.Setup))]
    private static class MeleePacket
    { [HarmonyPostfix] private static void Postfix(MeleeWeaponFirstPerson __instance) => LoadSheddingController.Current?.Extras?.MeleeSetup(__instance); }

    [HarmonyPatch(typeof(InstancedRenderFeatureRenderer), nameof(InstancedRenderFeatureRenderer.Draw))]
    private static class IrfDraw
    {
        [HarmonyPrefix]
        private static void Prefix(ref Camera camera)
        {
            var started = ForgePerformance.Crowd.Probe.Begin();
            if (IrfCulling.WorldCameraOnly && LoadSheddingController.Current is { Active: true }) IrfCulling.RedirectDraw(ref camera);
            ForgePerformance.Crowd.Probe.End(ForgePerformance.Crowd.ProbePoint.OwnIrfDraw, started);
        }
    }

    [HarmonyPatch(typeof(InstancedRenderFeature), nameof(InstancedRenderFeature.LateUpdate))]
    private static class IrfLateUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix(InstancedRenderFeature __instance)
        {
            var started = ForgePerformance.Crowd.Probe.Begin();
            var run = !(IrfCulling.OffscreenSkip && LoadSheddingController.Current is { Active: true } && IrfCulling.ShouldSkip(__instance.Pointer));
            ForgePerformance.Crowd.Probe.End(ForgePerformance.Crowd.ProbePoint.OwnIrfLateUpdate, started);
            return run;
        }
    }

    // Runs once per instanced-render feature per frame; reads one static flag first and does nothing else while it is off.
    [HarmonyPatch(typeof(InstancedRenderFeatureRenderer), nameof(InstancedRenderFeatureRenderer.Update))]
    private static class IrfPhysics
    {
        [HarmonyPrefix]
        private static bool Prefix(InstancedRenderFeatureRenderer __instance, ref float deltaTime, out long __state)
        {
            __state = ForgePerformance.Crowd.Probe.Begin();
            return !IrfCulling.PhysicsHalfRate || LoadSheddingController.Current is not { Active: true } || IrfCulling.PhysicsUpdate(__instance.Pointer, ref deltaTime);
        }
        [HarmonyPostfix]
        private static void Postfix(long __state) => ForgePerformance.Crowd.Probe.End(ForgePerformance.Crowd.ProbePoint.IrfRendererUpdate, __state);
    }

    // Every Debug.LogError(object) passes here; only the game's tentacle-limit message is held back (TentacleLimitErrorFold).
    [HarmonyPatch(typeof(Debug), nameof(Debug.LogError), new[] { typeof(Il2CppSystem.Object) })]
    private static class TentacleError
    {
        [HarmonyPrefix]
        private static bool Prefix(Il2CppSystem.Object message) => !TentacleErrorFold.Hold(message);
    }

    [HarmonyPatch(typeof(Decay), nameof(Decay.Initialize), new[] { typeof(SkinnedMeshRenderer), typeof(Il2CppSystem.Collections.Generic.List<InstancedRenderFeature>) })]
    private static class DecayInit
    { [HarmonyPostfix] private static void Postfix(Decay __instance) => LoadSheddingController.Current?.Extras?.DecayInitialized(__instance); }

    [HarmonyPatch(typeof(DebrisSpawner), nameof(DebrisSpawner.RunSequence), new[] { typeof(Transform) })]
    private static class DebrisRunAligned
    { [HarmonyPrefix] private static void Prefix(DebrisSpawner __instance) { if (LoadSheddingController.Current is { Active: true } c) c.Extras?.DebrisStarting(__instance); } }

    [HarmonyPatch(typeof(DebrisSpawner), nameof(DebrisSpawner.RunSequence), new System.Type[0])]
    private static class DebrisRun
    { [HarmonyPrefix] private static void Prefix(DebrisSpawner __instance) { if (LoadSheddingController.Current is { Active: true } c) c.Extras?.DebrisStarting(__instance); } }

    [HarmonyPatch(typeof(GS_AfterLevel), nameof(GS_AfterLevel.CleanupAfterExpedition))]
    private static class LeakAfterLevel
    { [HarmonyPriority(Priority.Low)][HarmonyPostfix] private static void Postfix() => LoadSheddingController.Current?.Extras?.LeakAfterLevel(); }

    [HarmonyPatch(typeof(FX_Manager), nameof(FX_Manager.ResetManager))]
    private static class LeakEffectLights
    { [HarmonyPrefix] private static void Prefix() => LoadSheddingController.Current?.Extras?.LeakEffectLights(); }

    [HarmonyPatch(typeof(InstancedRenderFeature), nameof(InstancedRenderFeature.OnDestroy))]
    private static class LeakInstancedRender
    { [HarmonyPrefix] private static void Prefix(InstancedRenderFeature __instance) => LoadSheddingController.Current?.Extras?.LeakInstancedRender(__instance); }

    [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.OnDestroy))]
    private static class LeakEnemy
    {
        // The probe counts destroyed enemies per frame here (only when LeakCleanup installs this patch).
        [HarmonyPrefix]
        private static void Prefix(EnemyAgent __instance)
        {
            ForgePerformance.Crowd.Probe.Count(ForgePerformance.Crowd.ProbePoint.EnemyDestroyed);
            LoadSheddingController.Current?.Extras?.LeakEnemy(__instance);
        }
    }

    [HarmonyPatch(typeof(InfectionSpitter), nameof(InfectionSpitter.Awake))]
    private static class SpitterAwake
    { [HarmonyPostfix] private static void Postfix(InfectionSpitter __instance) { if (LoadSheddingController.Current is { Active: true } c) c.Extras?.SpitterAwake(__instance); } }

    [HarmonyPatch(typeof(GlueGunProjectile), nameof(GlueGunProjectile.SyncDestroy))]
    private static class GlueSound
    { [HarmonyPrefix] private static void Prefix(GlueGunProjectile __instance) => LoadSheddingController.Current?.Extras?.GlueDestroying(__instance); }

    // The game's one caller of this overload is BulletWeapon.FixedUpdate dropping a reloaded gun's magazine from the gun's own pool.
    [HarmonyPatch(typeof(ObjectPooler.Pool), nameof(ObjectPooler.Pool.Get), new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) })]
    private static class MagazineDrop
    {
        [HarmonyPostfix]
        private static void Postfix(ObjectPooler.Pool __instance, GameObject __result)
        { if (LoadSheddingController.Current is { Active: true } c) c.Extras?.MagazineDropped(__instance, __result); }
    }

    // Every repeller sphere's state changes pass here, a few times in its life; only the change into Disabled is handed on.
    [HarmonyPatch(typeof(FogRepeller_Sphere), nameof(FogRepeller_Sphere.ChangeState))]
    private static class RepellerState
    {
        [HarmonyPrefix] private static void Prefix(FogRepeller_Sphere __instance, out eFogRepellerSphereState __state) => __state = __instance.CurrentState;
        [HarmonyPostfix]
        private static void Postfix(FogRepeller_Sphere __instance, eFogRepellerSphereState state, eFogRepellerSphereState __state)
        {
            if (state == eFogRepellerSphereState.Disabled && __state != eFogRepellerSphereState.Disabled && LoadSheddingController.Current is { Active: true } c)
                c.Extras?.RepellerSpent(__instance);
        }
    }

    [HarmonyPatch(typeof(UI_Apply), nameof(UI_Apply.ApplySetttings), new System.Type[0])]
    private static class GuiMirror
    { [HarmonyPrefix] private static void Prefix() => LoadSheddingController.Current?.Extras?.MirrorBefore(); }

    [HarmonyPatch(typeof(UI_Apply), nameof(UI_Apply.ApplySetttings), new[] { typeof(UI_Apply) })]
    private static class GuiMirrorOne
    { [HarmonyPrefix] private static void Prefix() => LoadSheddingController.Current?.Extras?.MirrorBefore(); }
}
