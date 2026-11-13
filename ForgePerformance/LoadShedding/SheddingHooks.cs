using System;
using Enemies;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IRF;
using CullingSystem;
using FX_EffectSystem;
using UnityEngine;

using AirParticles = AirParticleSystem.AirParticleSystem;

namespace ForgePerformance.LoadShedding;

internal sealed partial class SheddingHooks : IDisposable
{
    private readonly Harmony _harmony = new("NAinfini.ForgePerformance.LoadShedding");
    internal void Install(SheddingHookPlan plan)
    {
        // Only this lane's explicitly listed patches; never PatchAll on the
        // collector assembly or another package's patch set.
        plan.Install(id => _harmony.CreateClassProcessor(PatchType(id)).Patch());
    }
    internal static Type PatchType(SheddingHookId id) => id switch
    {
        SheddingHookId.CorpseSetup => typeof(CorpseSetup),
        SheddingHookId.DeadEnter => typeof(DeadEnter),
        SheddingHookId.CorpseMove => typeof(CorpseMove),
        SheddingHookId.RagdollToggle => typeof(RagdollToggle),
        SheddingHookId.DecayHidden => typeof(DecayHidden),
        SheddingHookId.AmbientStart => typeof(AmbientStart),
        SheddingHookId.AirStart => typeof(AirStart),
        SheddingHookId.AmbientSystemUpdate => typeof(AmbientSystemUpdate),
        SheddingHookId.AmbientSystemReset => typeof(AmbientSystemReset),
        SheddingHookId.ShellMove => typeof(ShellMove),
        SheddingHookId.GardenEnable => typeof(GardenEnable),
        SheddingHookId.GardenDisable => typeof(GardenDisable),
        SheddingHookId.GardenShadow => typeof(GardenShadow),
        SheddingHookId.VisibilityPreCull => typeof(VisibilityPreCull),
        SheddingHookId.DecorationShadowsUpdate => typeof(DecorationShadowsUpdate),
        SheddingHookId.ImpactEffect => typeof(ImpactEffect),
        SheddingHookId.DecalPlay => typeof(DecalPlay),
        SheddingHookId.DecalReturn => typeof(DecalReturn),
        SheddingHookId.LightCollectionScan => typeof(LightCollectionScan),
        SheddingHookId.AirCollectCommands => typeof(AirCollectCommands),
        SheddingHookId.AirPrewarm => typeof(AirPrewarm),
        SheddingHookId.VisorSetup => typeof(VisorSetup),
        SheddingHookId.VisorCollectCommands => typeof(VisorCollectCommands),
        SheddingHookId.DecayInit => typeof(DecayInit),
        SheddingHookId.DebrisRun => typeof(DebrisRun),
        SheddingHookId.DebrisRunAligned => typeof(DebrisRunAligned),
        SheddingHookId.LeakAfterLevel => typeof(LeakAfterLevel),
        SheddingHookId.LeakEffectLights => typeof(LeakEffectLights),
        SheddingHookId.LeakInstancedRender => typeof(LeakInstancedRender),
        SheddingHookId.LeakEnemy => typeof(LeakEnemy),
        SheddingHookId.SpitterAwake => typeof(SpitterAwake),
        SheddingHookId.GlueSound => typeof(GlueSound),
        SheddingHookId.GuiMirror => typeof(GuiMirror),
        SheddingHookId.GuiMirrorOne => typeof(GuiMirrorOne),
        SheddingHookId.LeakTerminal => typeof(LeakTerminal),
        SheddingHookId.LeakScrollHeaders => typeof(LeakScrollHeaders),
        SheddingHookId.MeleePacket => typeof(MeleePacket),
        SheddingHookId.IrfDraw => typeof(IrfDraw),
        SheddingHookId.IrfLateUpdate => typeof(IrfLateUpdate),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
    public void Dispose() => _harmony.UnpatchSelf();
    internal static void VerifyLayout(SheddingSettings settings)
    {
        // Offsets that code reads by raw pointer are checked only when the switch that reads them is on.
        if (settings.IrfOffscreenSkip)
        {
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeature", "ForceStatic", IrfCulling.FeatureForceStatic);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeature", "m_dynamicTransforms", IrfCulling.FeatureDynamicTransforms);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeature", "m_renderer", IrfCulling.FeatureRenderer);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeature", "m_enemyRoot", IrfCulling.FeatureEnemyRoot);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeatureRenderer", "m_staticBounds", IrfCulling.RendererStaticBounds);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeatureRenderer", "m_updatedTransformsOnce", IrfCulling.RendererUpdatedOnce);
            Field("Modules-ASM.dll", "IRF", "InstancedRenderFeatureRenderer", "m_gameObjectIsStatic", IrfCulling.RendererGameObjectIsStatic);
        }
        if (settings.MeleePacketReuse)
        {
            Field("SNet_ASM.dll", "SNetwork", "SNet_Packet", "<Replicator>k__BackingField", MeleePacketReuse.PacketReplicator);
            Field("SNet_ASM.dll", "SNetwork", "SNet_Packet", "<Index>k__BackingField", MeleePacketReuse.PacketIndex);
        }
        if (settings.ClosedDoorDepthLimit)
        {
            Field("Modules-ASM.dll", "CullingSystem", "C_Portal", "<IsOpen>k__BackingField", ClosedDoorDepth.PortalIsOpen);
            Field("Modules-ASM.dll", "CullingSystem", "C_Camera", "m_cullAgent", ClosedDoorDepth.CameraCullAgent);
            Field("Modules-ASM.dll", "CullingSystem", "C_MovingCuller", "CurrentNode", ClosedDoorDepth.CullerCurrentNode);
        }
        var dead = IL2CPP.GetIl2CppClass("Modules-ASM.dll", "", "ES_Dead");
        var corpse = IL2CPP.GetIl2CppNestedType(dead, "<DecayDelayed>d__20");
        Offset(corpse, "<>1__state", 0x10); Offset(corpse, "delay", 0x20); Offset(corpse, "duration", 0x30);
        Offset(corpse, "<>4__this", 0x28);
        Field("Modules-ASM.dll", "", "ES_DeadBase", "m_didHide", 0x48);
        var shellClass = IL2CPP.GetIl2CppClass("Modules-ASM.dll", "", "WeaponShellManager");
        Offset(IL2CPP.GetIl2CppNestedType(shellClass, "<EjectShellInternal>d__11"), "<>1__state", 0x10);
    }
    private static void Field(string assembly, string ns, string type, string name, uint expected)
        => Offset(IL2CPP.GetIl2CppClass(assembly, ns, type), name, expected);
    private static void Offset(IntPtr type, string name, uint expected)
    {
        var field = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.GetIl2CppField(type, name);
        if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_offset(field) != expected)
            throw new InvalidOperationException("Load shedding native layout mismatch: " + name);
    }

    [HarmonyPatch(typeof(EnemyAgent), nameof(EnemyAgent.Setup))]
    private static class CorpseSetup
    {
        [HarmonyPrefix]
        private static void Prefix(EnemyAgent __instance)
        {
            if (LoadSheddingController.Current is not { } current) return;
            current.RestoreStealthBodies(__instance);
            current.RestoreCorpseShadows(__instance);
            if (!current.IsRefused(SheddingCut.CorpseTimer)) current.Corpses.BeforeEnemySetup(__instance);
        }
    }
    [HarmonyPatch(typeof(ES_Dead), nameof(ES_Dead.CommonEnter))]
    private static class DeadEnter
    {
        [HarmonyPostfix]
        private static void Postfix(ES_Dead __instance)
        {
            var current = LoadSheddingController.Current;
            if (current == null) return;
            current.HideStealthBody(__instance.m_enemyAgent);
            current.EnemyVisuals(__instance.m_enemyAgent, true);
        }
    }
    [HarmonyPatch(typeof(ES_Dead._DecayDelayed_d__20), nameof(ES_Dead._DecayDelayed_d__20.MoveNext))]
    private static class CorpseMove
    {
        [HarmonyPrefix] private static void Prefix(ES_Dead._DecayDelayed_d__20 __instance) => LoadSheddingController.Current?.CorpseTiming(__instance);
        [HarmonyPostfix]
        private static void Postfix(ES_Dead._DecayDelayed_d__20 __instance, bool __result)
        { if (!__result) LoadSheddingController.Current?.CorpseComplete(__instance); }
    }
    [HarmonyPatch(typeof(ES_Dead), nameof(ES_Dead.SetRagdollEnabled))]
    private static class RagdollToggle
    {
        // Whether the body was a ragdoll before the call: only a ragdoll that stops is refreshed, never a setup call.
        [HarmonyPrefix] private static void Prefix(ES_Dead __instance, out bool __state) => __state = __instance.m_isRagdoll;
        [HarmonyPostfix]
        private static void Postfix(ES_Dead __instance, bool enabled, bool __state)
            => LoadSheddingController.Current?.Ragdoll(__instance, enabled, __state);
    }
    [HarmonyPatch(typeof(ES_Dead), nameof(ES_Dead.OnDecayRendererHidden))]
    private static class DecayHidden
    {
        // One patch for the two switches that read it, each part only with its own switch on: the corpse timer first, then the ragdoll cap.
        [HarmonyPostfix]
        private static void Postfix(ES_Dead __instance)
        {
            if (LoadSheddingController.Current is not { } current) return;
            if (current.Settings.FastCorpseHide) current.Corpses.NativeHidden(__instance);
            if (current.Settings.RagdollCap) current.RagdollHidden(__instance);
        }
    }
    [HarmonyPatch(typeof(AmbientParticles), nameof(AmbientParticles.Start))]
    private static class AmbientStart
    { [HarmonyPostfix] private static void Postfix(AmbientParticles __instance) => LoadSheddingController.Current?.Ambient(__instance); }
    [HarmonyPatch(typeof(AmbientParticles), nameof(AmbientParticles.UpdateSystem))]
    private static class AmbientSystemUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix(AmbientParticles __instance, ref float deltaTime)
            => LoadSheddingController.Current?.AmbientUpdate(__instance, ref deltaTime) ?? true;
    }
    [HarmonyPatch(typeof(AmbientParticles), nameof(AmbientParticles.ResetSystem))]
    private static class AmbientSystemReset
    {
        [HarmonyPrefix]
        private static bool Prefix(AmbientParticles __instance)
            => LoadSheddingController.Current?.AmbientReset(__instance) ?? true;
    }
    [HarmonyPatch(typeof(AirParticles), nameof(AirParticles.Start))]
    private static class AirStart
    { [HarmonyPostfix] private static void Postfix(AirParticles __instance) => LoadSheddingController.Current?.Air(__instance); }
    [HarmonyPatch(typeof(AirParticles), nameof(AirParticles.CollectCommands), new[] { typeof(UnityEngine.Rendering.CommandBuffer), typeof(UnityEngine.Rendering.CommandBuffer) })]
    private static class AirCollectCommands
    {
        [HarmonyPrefix]
        private static bool Prefix()
            => LoadSheddingController.Current is not { Active: true } current || current.IsRefused(SheddingCut.Dust) || current.Dust.CollectAir();
    }
    [HarmonyPatch(typeof(AirParticles), nameof(AirParticles.InitializeAndPrewarmBuffers))]
    private static class AirPrewarm
    {
        [HarmonyPrefix]
        private static bool Prefix()
            => LoadSheddingController.Current is not { Active: true } current || current.IsRefused(SheddingCut.Dust) || current.Dust.PrewarmAir();
    }
    [HarmonyPatch(typeof(GlassLiquidSystem), nameof(GlassLiquidSystem.Setup), new[] { typeof(int), typeof(float) })]
    private static class VisorSetup
    {
        [HarmonyPrefix]
        private static void Prefix(GlassLiquidSystem __instance)
        { if (LoadSheddingController.Current is { Active: true } current) current.Visor.BeforeSetup(__instance); }
        [HarmonyPostfix]
        private static void Postfix(GlassLiquidSystem __instance)
        { if (LoadSheddingController.Current is { Active: true } current) current.Visor.Suppress(__instance); }
    }
    [HarmonyPatch(typeof(GlassLiquidSystem), nameof(GlassLiquidSystem.CollectCommands), new[] { typeof(UnityEngine.Rendering.CommandBuffer) })]
    private static class VisorCollectCommands
    {
        [HarmonyPrefix]
        private static bool Prefix()
            => LoadSheddingController.Current is not { Active: true } current || current.Visor.Collect();
    }
    [HarmonyPatch(typeof(WeaponShellManager._EjectShellInternal_d__11), nameof(WeaponShellManager._EjectShellInternal_d__11.MoveNext))]
    private static class ShellMove
    {
        [HarmonyPrefix]
        private static bool Prefix(WeaponShellManager._EjectShellInternal_d__11 __instance, ref bool __result)
        {
            if (LoadSheddingController.Current?.AllowShell(__instance) != false) return true;
            __result = false; return false;
        }
        [HarmonyPostfix]
        private static void Postfix(WeaponShellManager._EjectShellInternal_d__11 __instance, bool __result)
        { if (!__result) LoadSheddingController.Current?.ShellComplete(__instance); }
    }
    [HarmonyPatch(typeof(InstancedRenderFeature), nameof(InstancedRenderFeature.OnEnable))]
    private static class GardenEnable
    { [HarmonyPostfix] private static void Postfix(InstancedRenderFeature __instance) => LoadSheddingController.Current?.GardenFeatureEnabled(__instance); }
    [HarmonyPatch(typeof(InstancedRenderFeature), nameof(InstancedRenderFeature.OnDisable))]
    private static class GardenDisable
    { [HarmonyPostfix] private static void Postfix(InstancedRenderFeature __instance) => LoadSheddingController.Current?.GardenFeatureDisabled(__instance); }
    [HarmonyPatch(typeof(InstancedRenderFeatureRenderer), nameof(InstancedRenderFeatureRenderer.InjectShadowDrawCommand))]
    private static class GardenShadow
    { [HarmonyPrefix] private static bool Prefix(InstancedRenderFeatureRenderer __instance) => LoadSheddingController.Current?.DrawGardenShadow(__instance) ?? true; }

    // One patch for the three switches that stand on the visibility pass, each part only with its own switch on. Before the pass:
    // decoration culling, then the closed-door portals, which the finalizer opens again when the call ends, even by exception.
    // After it: decoration culling, then the decoration light list.
    [HarmonyPatch(typeof(C_Camera), nameof(C_Camera.RunVisibilityOnPreCull))]
    private static class VisibilityPreCull
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (LoadSheddingController.Current is not { } current) return;
            if (current.Settings.DecorationCulling) current.Decorations.CameraBefore();
            if (current.Settings.ClosedDoorDepthLimit && current.Active) current.Doors.BeforeVisibility();
        }
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (LoadSheddingController.Current is not { } current) return;
            if (current.Settings.DecorationCulling) current.Decorations.CameraAfter();
            if (current.Settings.DecorationLights) current.Decorations.LightsAfter();
        }
        [HarmonyFinalizer]
        private static Exception? Finalizer(Exception? __exception)
        {
            if (LoadSheddingController.Current is { } current && current.Settings.ClosedDoorDepthLimit) current.Doors.AfterVisibility();
            return __exception;
        }
    }
    [HarmonyPatch(typeof(CL_ShadowGenerator), nameof(CL_ShadowGenerator.InjectCommands))]
    private static class DecorationShadowsUpdate
    {
        [HarmonyPrefix]
        private static void Prefix(CL_ShadowGenerator __instance, out DecorationShedding.ShadowScope? __state)
            => __state = LoadSheddingController.Current?.Decorations.Shadows(__instance);
        [HarmonyFinalizer]
        private static Exception? Finalizer(DecorationShedding.ShadowScope? __state, Exception? __exception)
            => LoadSheddingController.Current?.Decorations.ShadowRestore(__state, __exception) ?? __exception;
    }
    [HarmonyPatch(typeof(FX_Manager), nameof(FX_Manager.PlayEffect), new[] {
        typeof(bool), typeof(FX_GroupName), typeof(Transform), typeof(Vector3), typeof(Quaternion), typeof(bool) })]
    private static class ImpactEffect
    {
        [HarmonyPrefix]
        private static void Prefix(FX_GroupName effectGroup, out int __state)
            => __state = LoadSheddingController.Current?.Decorations.ImpactBegin(effectGroup) ?? 0;
        [HarmonyFinalizer]
        private static Exception? Finalizer(int __state, Exception? __exception)
        { LoadSheddingController.Current?.Decorations.ImpactEnd(__state); return __exception; }
    }
    [HarmonyPatch(typeof(FX_Decal), nameof(FX_Decal.SetPlayStart))]
    private static class DecalPlay
    {
        [HarmonyPrefix] private static void Prefix(FX_Decal __instance) => LoadSheddingController.Current?.Decorations.DecalPlay(__instance);
        [HarmonyFinalizer]
        private static Exception? Finalizer(FX_Decal __instance, Exception? __exception)
        {
            if (__exception != null) LoadSheddingController.Current?.Decorations.DecalReturn(__instance);
            return __exception;
        }
    }
    // The game hides a decal (Decal.Shown(false)) only from FX_Decal.ReturnToPool, so this prefix is the one exit.
    [HarmonyPatch(typeof(FX_Decal), nameof(FX_Decal.ReturnToPool))]
    private static class DecalReturn
    { [HarmonyPrefix] private static void Prefix(FX_Decal __instance) => LoadSheddingController.Current?.Decorations.DecalReturn(__instance); }
    [HarmonyPatch(typeof(LevelGeneration.LG_LightCollection), nameof(LevelGeneration.LG_LightCollection.ScanForLights))]
    private static class LightCollectionScan
    { [HarmonyPostfix] private static void Postfix(LevelGeneration.LG_LightCollection __instance) => LoadSheddingController.Current?.Decorations.ControlledLights(__instance); }
}
