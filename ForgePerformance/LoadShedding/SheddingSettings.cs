namespace ForgePerformance.LoadShedding;

// Defaults: the owner set CorpseLifetime, CorpseRecycleSeconds, ParticleFactor, ParticleHz, WindLimit, DecalLimit, DecalLifetime and CullDistance (2026-11-09);
// RagdollLimit, ShellLimit, LightLimit and ClosedDoorDepth are chosen middle values. None is measured in game, and every one can be loosened or tightened in the config.
// The values live with their config entries in SheddingConfiguration.
internal sealed record SheddingSettings
{
    internal bool CorpseShadows { get; init; }
    internal bool FastCorpseHide { get; init; }
    internal float CorpseLifetime { get; init; }
    internal bool RagdollCap { get; init; }
    internal int RagdollLimit { get; init; }
    internal bool WindCapacityProtection { get; init; }
    internal bool WindNearest { get; init; }
    internal int WindLimit { get; init; }
    internal bool GardenShadows { get; init; }
    internal bool AmbientParticles { get; init; }
    internal bool AmbientDustOff { get; init; }
    internal bool VisorLiquidOff { get; init; }
    internal float ParticleFactor { get; init; }
    internal int ParticleHz { get; init; }
    internal bool ShellEjection { get; init; }
    internal int ShellLimit { get; init; }
    internal bool DroppedMagazineCleanup { get; init; }
    internal float DroppedMagazineSeconds { get; init; }
    internal bool SpentFogRepellerCleanup { get; init; }
    internal bool DecorationShadows { get; init; }
    internal bool CombatDecals { get; init; }
    internal int DecalLimit { get; init; }
    internal float DecalLifetime { get; init; }
    internal bool DecorationCulling { get; init; }
    internal float CullDistance { get; init; }
    internal bool DecorationLights { get; init; }
    internal int LightLimit { get; init; }
    internal bool DecorationAnimation { get; init; }
    internal bool ShadowDistance { get; init; }
    internal float ShadowDistanceFactor { get; init; }
    internal bool SoftParticlesOff { get; init; }
    internal bool LeakCleanup { get; init; }
    internal bool SummaryLog { get; init; }
    internal bool DecayCleanup { get; init; }
    internal bool DebrisFreeze { get; init; }
    internal bool GlueSoundRecycle { get; init; }
    internal bool StackTraceTrim { get; init; }
    internal bool GuiMirrorOff { get; init; }
    internal bool LowQualitySpitter { get; init; }
    internal bool CorpseRecycle { get; init; }
    internal float CorpseRecycleSeconds { get; init; }
    internal bool MeleePacketReuse { get; init; }
    internal bool IrfWorldCameraOnly { get; init; }
    internal bool IrfOffscreenSkip { get; init; }
    internal bool IrfPhysicsHalfRate { get; init; }
    internal bool TentacleLimitErrorFold { get; init; }
    internal bool RagdollNodeRefresh { get; init; }
    internal bool ClosedDoorDepthLimit { get; init; }
    internal int ClosedDoorDepth { get; init; }
    internal bool TextureStreamingOff { get; init; }
    internal bool SetupRendererLookup { get; init; }
    internal bool SpawnFrameDefer { get; init; }
    internal bool FarAnimatorThrottle { get; init; }
    internal float FarAnimatorDistance { get; init; }
    internal int FarAnimatorInterval { get; init; }
    internal bool FarSkinQuality { get; init; }
    internal float FarSkinDistance { get; init; }
    internal bool ClientHiddenSyncThrottle { get; init; }
    internal float HiddenSyncDistance { get; init; }
    internal int HiddenSyncInterval { get; init; }
    internal bool UpdateBudgets { get; init; }
    internal string BudgetBehaviour { get; init; } = "";
    internal string BudgetDetection { get; init; } = "";
    internal string BudgetNetwork { get; init; } = "";
    internal string BudgetLocomotion { get; init; } = "";
    internal string BudgetFixedLocomotion { get; init; } = "";
    internal string BudgetNodes { get; init; } = "";
    internal bool AvoidanceQuality { get; init; }
    internal int AvoidanceType { get; init; }
    internal bool PathfindingIterations { get; init; }
    internal int PathfindingIterationsPerFrame { get; init; }
    internal bool HitReactKeepsAgent { get; init; }
    internal bool Probe { get; init; }
    internal float ProbeSeconds { get; init; }
    // The enemy-crowd options and the probe: they need the enemy setup and death hooks and the per-frame controller.
    internal bool CrowdHooks => Probe || SpawnFrameDefer || FarAnimatorThrottle || FarSkinQuality
        || ClientHiddenSyncThrottle || UpdateBudgets || AvoidanceQuality || PathfindingIterations || HitReactKeepsAgent;
}
