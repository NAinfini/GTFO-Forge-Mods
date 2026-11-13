using System;
using System.Collections.Generic;

namespace ForgePerformance.LoadShedding;

internal enum SheddingHookId
{
    CorpseSetup,
    DeadEnter,
    CorpseMove,
    RagdollToggle,
    DecayHidden,
    AmbientStart,
    AirStart,
    AmbientSystemUpdate,
    AmbientSystemReset,
    AirCollectCommands,
    AirPrewarm,
    VisorSetup,
    VisorCollectCommands,
    ShellMove,
    GardenEnable,
    GardenDisable,
    GardenShadow,
    VisibilityPreCull,
    DecorationShadowsUpdate,
    ImpactEffect,
    DecalPlay,
    DecalReturn,
    LightCollectionScan,
    DecayInit,
    DebrisRun,
    DebrisRunAligned,
    LeakAfterLevel,
    LeakEffectLights,
    LeakInstancedRender,
    LeakEnemy,
    SpitterAwake,
    GlueSound,
    GuiMirror,
    GuiMirrorOne,
    LeakTerminal,
    LeakScrollHeaders,
    MeleePacket,
    IrfDraw,
    IrfLateUpdate
}

internal sealed class SheddingHookPlan
{
    private readonly SheddingHookId[] _hooks;

    private SheddingHookPlan(SheddingSettings settings, SheddingHookId[] hooks)
    {
        Settings = settings;
        _hooks = hooks;
        RequiresDecorationAnimationScan = settings.DecorationAnimation;
        RequiresExtras = settings.AnyRenderCut || settings.ShadowDistance || settings.SoftParticlesOff || settings.StackTraceTrim
            || settings.GuiMirrorOff || settings.LowQualitySpitter || settings.LeakCleanup || settings.DecayCleanup
            || settings.DebrisFreeze || settings.GlueSoundRecycle;
    }

    internal SheddingSettings Settings { get; }
    internal IReadOnlyList<SheddingHookId> Hooks => _hooks;
    internal bool RequiresDecorationAnimationScan { get; }
    internal bool RequiresExtras { get; }
    internal bool HasShedding => _hooks.Length != 0 || RequiresDecorationAnimationScan || RequiresExtras;

    internal void Install(Action<SheddingHookId> install)
    {
        if (install is null) throw new ArgumentNullException(nameof(install));
        foreach (var hook in _hooks) install(hook);
    }

    internal static SheddingHookPlan Create(SheddingSettings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));
        var hooks = new List<SheddingHookId>(30);
        void Add(bool enabled, SheddingHookId hook) { if (enabled) hooks.Add(hook); }

        Add(settings.FastCorpseHide || settings.CorpseShadows, SheddingHookId.CorpseSetup);
        Add(settings.FastCorpseHide || settings.CorpseShadows, SheddingHookId.DeadEnter);
        Add(settings.FastCorpseHide || settings.CorpseRecycle, SheddingHookId.CorpseMove);
        Add(settings.RagdollCap || settings.RagdollNodeRefresh, SheddingHookId.RagdollToggle);
        Add(settings.FastCorpseHide || settings.RagdollCap, SheddingHookId.DecayHidden);
        Add(settings.AmbientParticles || settings.AmbientDustOff, SheddingHookId.AmbientStart);
        Add(settings.AmbientParticles || settings.AmbientDustOff, SheddingHookId.AirStart);
        Add(settings.AmbientParticles || settings.AmbientDustOff, SheddingHookId.AmbientSystemUpdate);
        Add(settings.AmbientParticles || settings.AmbientDustOff, SheddingHookId.AmbientSystemReset);
        Add(settings.AmbientDustOff, SheddingHookId.AirCollectCommands);
        Add(settings.AmbientDustOff, SheddingHookId.AirPrewarm);
        Add(settings.VisorLiquidOff, SheddingHookId.VisorSetup);
        Add(settings.VisorLiquidOff, SheddingHookId.VisorCollectCommands);
        Add(settings.ShellEjection, SheddingHookId.ShellMove);
        Add(settings.GardenShadows, SheddingHookId.GardenEnable);
        Add(settings.GardenShadows, SheddingHookId.GardenDisable);
        Add(settings.GardenShadows, SheddingHookId.GardenShadow);
        Add(settings.DecorationCulling || settings.DecorationLights || settings.ClosedDoorDepthLimit, SheddingHookId.VisibilityPreCull);
        Add(settings.DecorationShadows, SheddingHookId.DecorationShadowsUpdate);
        Add(settings.CombatDecals, SheddingHookId.ImpactEffect);
        Add(settings.CombatDecals, SheddingHookId.DecalPlay);
        Add(settings.CombatDecals, SheddingHookId.DecalReturn);
        Add(settings.DecorationLights, SheddingHookId.LightCollectionScan);
        Add(settings.DecayCleanup, SheddingHookId.DecayInit);
        Add(settings.DebrisFreeze, SheddingHookId.DebrisRun);
        Add(settings.DebrisFreeze, SheddingHookId.DebrisRunAligned);
        Add(settings.LeakCleanup, SheddingHookId.LeakAfterLevel);
        Add(settings.LeakCleanup, SheddingHookId.LeakEffectLights);
        Add(settings.LeakCleanup, SheddingHookId.LeakInstancedRender);
        Add(settings.LeakCleanup, SheddingHookId.LeakEnemy);
        Add(settings.LowQualitySpitter, SheddingHookId.SpitterAwake);
        Add(settings.GlueSoundRecycle, SheddingHookId.GlueSound);
        Add(settings.GuiMirrorOff, SheddingHookId.GuiMirror);
        Add(settings.GuiMirrorOff, SheddingHookId.GuiMirrorOne);
        Add(settings.LeakCleanup, SheddingHookId.LeakTerminal);
        Add(settings.LeakCleanup, SheddingHookId.LeakScrollHeaders);
        Add(settings.MeleePacketReuse, SheddingHookId.MeleePacket);
        Add(settings.IrfWorldCameraOnly, SheddingHookId.IrfDraw);
        Add(settings.IrfOffscreenSkip, SheddingHookId.IrfLateUpdate);
        return new SheddingHookPlan(settings, hooks.ToArray());
    }
}
