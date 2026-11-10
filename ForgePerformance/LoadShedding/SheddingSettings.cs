namespace ForgePerformance.LoadShedding;

internal sealed record SheddingSettings
{
    internal const int MinimumRagdollLimit = 1;
    internal const int MaximumRagdollLimit = 12;
    internal const int MinimumWindLimit = 1;
    internal const int MaximumWindLimit = 64;
    internal const float MinimumParticleFactor = 0.01f;
    internal const float MaximumParticleFactor = 1f;
    internal const int MinimumParticleHz = 1;
    internal const int MaximumParticleHz = 15;
    internal const int MinimumShellLimit = 1;
    internal const int MaximumShellLimit = 128;
    internal const int MinimumDecalLimit = 1;
    internal const int MaximumDecalLimit = 4096;
    internal const float MinimumDecalLifetime = 0.01f;
    internal const float MaximumDecalLifetime = 60f;
    internal const float MinimumCullDistance = 1f;
    internal const float MaximumCullDistance = 1000f;
    internal const int MinimumLightLimit = 1;
    internal const int MaximumLightLimit = 32;
    internal const float MinimumCorpseLifetime = 0.01f;
    internal const float MaximumCorpseLifetime = 60f;
    internal const float MinimumShadowDistanceFactor = 0.3f;
    internal const float MaximumShadowDistanceFactor = 1f;
    internal const float MinimumRecycleSeconds = 0.1f;
    internal const float MaximumRecycleSeconds = 60f;
    internal const int MinimumClosedDoorDepth = 1;
    internal const int MaximumClosedDoorDepth = 4;

    // Defaults: the owner set CorpseLifetime, CorpseRecycleSeconds, ParticleFactor, ParticleHz, WindLimit, DecalLimit, DecalLifetime and CullDistance (2026-11-09);
    // RagdollLimit, ShellLimit, LightLimit and ClosedDoorDepth are chosen middle values. None is measured in game.
    // They have not been measured in game; every one can be loosened or tightened in the config.
    internal const float DefaultCorpseLifetime = 0.01f;
    internal const int DefaultRagdollLimit = 4;
    internal const int DefaultWindLimit = 1;
    internal const float DefaultParticleFactor = 0.01f;
    internal const int DefaultParticleHz = 1;
    internal const int DefaultShellLimit = 6;
    internal const int DefaultDecalLimit = 1;
    internal const float DefaultDecalLifetime = 0.01f;
    internal const float DefaultCullDistance = 10f;
    internal const int DefaultLightLimit = 12;
    internal const float DefaultShadowDistanceFactor = 0.65f;
    internal const float DefaultRecycleSeconds = 1f;
    internal const int DefaultClosedDoorDepth = 2;

    internal bool CorpseShadows { get; init; } = true;
    internal bool FastCorpseHide { get; init; } = true;
    internal float CorpseLifetime { get; init; } = DefaultCorpseLifetime;
    internal bool RagdollCap { get; init; } = true;
    internal int RagdollLimit { get; init; } = DefaultRagdollLimit;
    internal bool WindCapacityProtection { get; init; } = true;
    internal bool WindNearest { get; init; } = true;
    internal int WindLimit { get; init; } = DefaultWindLimit;
    internal bool GardenShadows { get; init; } = true;
    internal bool AmbientParticles { get; init; } = true;
    internal bool AmbientDustOff { get; init; } = true;
    internal bool VisorLiquidOff { get; init; } = true;
    internal float ParticleFactor { get; init; } = DefaultParticleFactor;
    internal int ParticleHz { get; init; } = DefaultParticleHz;
    internal bool ShellEjection { get; init; } = true;
    internal int ShellLimit { get; init; } = DefaultShellLimit;
    internal bool DecorationShadows { get; init; } = true;
    internal bool CombatDecals { get; init; } = true;
    internal int DecalLimit { get; init; } = DefaultDecalLimit;
    internal float DecalLifetime { get; init; } = DefaultDecalLifetime;
    internal bool DecorationCulling { get; init; } = true;
    internal float CullDistance { get; init; } = DefaultCullDistance;
    internal bool DecorationLights { get; init; } = true;
    internal int LightLimit { get; init; } = DefaultLightLimit;
    internal bool DecorationAnimation { get; init; } = true;
    internal bool ShadowDistance { get; init; } = true;
    internal float ShadowDistanceFactor { get; init; } = DefaultShadowDistanceFactor;
    internal bool SoftParticlesOff { get; init; } = true;
    internal bool LeakCleanup { get; init; } = true;
    internal bool SummaryLog { get; init; }
    internal bool DecayCleanup { get; init; } = true;
    internal bool DebrisFreeze { get; init; } = true;
    internal bool GlueSoundRecycle { get; init; } = true;
    internal bool StackTraceTrim { get; init; } = true;
    internal bool GuiMirrorOff { get; init; } = true;
    internal bool LowQualitySpitter { get; init; } = true;
    internal bool CorpseRecycle { get; init; } = true;
    internal float CorpseRecycleSeconds { get; init; } = DefaultRecycleSeconds;
    internal bool MeleePacketReuse { get; init; }
    internal bool IrfWorldCameraOnly { get; init; } = true;
    internal bool IrfOffscreenSkip { get; init; } = true;
    internal bool RagdollNodeRefresh { get; init; } = true;
    internal bool ClosedDoorDepthLimit { get; init; } = true;
    internal int ClosedDoorDepth { get; init; } = DefaultClosedDoorDepth;
    internal bool TextureStreamingOff { get; init; } = true;
    internal bool AnyRenderCut => TextureStreamingOff;
}
