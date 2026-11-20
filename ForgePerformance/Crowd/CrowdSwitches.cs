using System;
using System.Text;
using ForgePerformance.LoadShedding;

namespace ForgePerformance.Crowd;

/// <summary>The current state of the enemy-crowd options. They start from the config; with the probe on, every hook they need is
/// installed so <see cref="PerformanceControl"/> can switch them during one session for a before/after comparison.</summary>
internal static class CrowdSwitches
{
    internal static bool SetupRendererLookup, SpawnFrameDefer, FarAnimatorThrottle, FarSkinQuality,
        ClientHiddenSyncThrottle, UpdateBudgets, AvoidanceQuality, PathfindingIterations, HitReactKeepsAgent;
    internal static int HiddenSyncInterval = 4, AvoidanceType = 1, PathfindingIterationsPerFrame = 100;
    private static SheddingSettings? _settings;
    private static bool _crowd;

    internal static void Configure(SheddingSettings settings, bool crowdInstalled)
    {
        _settings = settings; _crowd = crowdInstalled;
        SetupRendererLookup = settings.SetupRendererLookup;
        SpawnFrameDefer = settings.SpawnFrameDefer;
        FarAnimatorThrottle = settings.FarAnimatorThrottle;
        FarSkinQuality = settings.FarSkinQuality;
        ClientHiddenSyncThrottle = settings.ClientHiddenSyncThrottle;
        UpdateBudgets = settings.UpdateBudgets;
        AvoidanceQuality = settings.AvoidanceQuality;
        PathfindingIterations = settings.PathfindingIterations;
        HitReactKeepsAgent = settings.HitReactKeepsAgent;
        HiddenSyncInterval = settings.HiddenSyncInterval;
        AvoidanceType = settings.AvoidanceType;
        PathfindingIterationsPerFrame = settings.PathfindingIterationsPerFrame;
    }

    internal static string Describe()
    {
        var text = new StringBuilder(160);
        void Add(string name, bool on) { if (on) text.Append(text.Length == 0 ? "" : ",").Append(name); }
        Add("SetupRendererLookup", SetupRendererLookup); Add("SpawnFrameDefer", SpawnFrameDefer);
        Add("FarAnimatorThrottle", FarAnimatorThrottle);
        Add("FarSkinQuality", FarSkinQuality); Add("ClientHiddenSyncThrottle", ClientHiddenSyncThrottle);
        Add("UpdateBudgets", UpdateBudgets); Add("AvoidanceQuality=" + AvoidanceType, AvoidanceQuality);
        Add("PathfindingIterations=" + PathfindingIterationsPerFrame, PathfindingIterations); Add("HitReactKeepsAgent", HitReactKeepsAgent);
        Add("IrfPhysicsHalfRate", IrfCulling.PhysicsHalfRate); Add("TentacleLimitErrorFold", TentacleErrorFold.Enabled);
        return text.Length == 0 ? "none" : text.ToString();
    }

    /// <summary>Switches one option; refuses one whose hooks are not installed in this session.</summary>
    internal static string Set(string name, bool on)
    {
        // The render options switch only where their hook was installed at start (configured on).
        switch (name)
        {
            case nameof(SheddingSettings.IrfPhysicsHalfRate):
                if (_settings?.IrfPhysicsHalfRate != true) return "refused: IrfPhysicsHalfRate was off at start";
                IrfCulling.PhysicsHalfRate = on; return Switched(name, on);
            case nameof(SheddingSettings.IrfOffscreenSkip):
                if (_settings?.IrfOffscreenSkip != true) return "refused: IrfOffscreenSkip was off at start";
                IrfCulling.OffscreenSkip = on; return Switched(name, on);
            case nameof(SheddingSettings.TentacleLimitErrorFold):
                if (_settings?.TentacleLimitErrorFold != true) return "refused: TentacleLimitErrorFold was off at start";
                TentacleErrorFold.Enabled = on; return Switched(name, on);
        }
        if (_settings == null || !_crowd) return "refused: crowd options are not installed in this session";
        if (on && name == nameof(ClientHiddenSyncThrottle) && !CrowdPatches.SyncHooked) return "refused: EnemyTransformSync.Update is not hooked";
        if (on && name == nameof(HitReactKeepsAgent) && !CrowdPatches.HitreactHooked) return "refused: SetNavMeshAgent or ES_Hitreact.Enter is not hooked";
        if (on && name == nameof(SetupRendererLookup) && !(_settings.FastCorpseHide || _settings.CorpseShadows))
            return "refused: the corpse options that use the lookup are off";
        Probe.Cut();
        switch (name)
        {
            case nameof(SetupRendererLookup): SetupRendererLookup = on; break;
            case nameof(SpawnFrameDefer): SpawnFrameDefer = on; break;
            case nameof(FarAnimatorThrottle): FarAnimatorThrottle = on; break;
            case nameof(FarSkinQuality): FarSkinQuality = on; break;
            case nameof(ClientHiddenSyncThrottle): ClientHiddenSyncThrottle = on; break;
            case nameof(UpdateBudgets): UpdateBudgets = on; break;
            case nameof(AvoidanceQuality): AvoidanceQuality = on; break;
            case nameof(PathfindingIterations): PathfindingIterations = on; break;
            case nameof(HitReactKeepsAgent): HitReactKeepsAgent = on; break;
            default: return "refused: unknown option " + name;
        }
        return Switched(name, on);
    }
    private static string Switched(string name, bool on)
    {
        Probe.Line("type=perf_probe_switch\toption=" + name + "\tvalue=" + (on ? "true" : "false"));
        return name + "=" + (on ? "true" : "false");
    }
    /// <summary>Sets the value AvoidanceQuality or PathfindingIterations applies; an option that is on applies the new value at
    /// the next crowd tick.</summary>
    internal static string Value(string name, int value)
    {
        if (_settings == null || !_crowd) return "refused: crowd options are not installed in this session";
        switch (name)
        {
            case nameof(AvoidanceType) when value is >= 0 and <= 4: AvoidanceType = value; break;
            case nameof(PathfindingIterationsPerFrame) when value >= 1: PathfindingIterationsPerFrame = value; break;
            case nameof(AvoidanceType) or nameof(PathfindingIterationsPerFrame): return "refused: " + name + " cannot be " + value;
            default: return "refused: unknown value " + name;
        }
        Probe.Cut();
        Probe.Line("type=perf_probe_switch\toption=" + name + "\tvalue=" + value);
        return name + "=" + value;
    }
}

/// <summary>Developer control from the ForgeDevelopment console:
/// <c>call static:ForgePerformance.Crowd.PerformanceControl Set "FarSkinQuality" false</c>, <c>Value "AvoidanceType" 0</c>,
/// <c>Mark "wave-off"</c>, <c>Read</c>.</summary>
internal static class PerformanceControl
{
    internal static string Set(string option, bool on) => CrowdSwitches.Set(option, on);
    internal static string Value(string option, int value) => CrowdSwitches.Value(option, value);
    internal static string Mark(string label) { Probe.Mark(label); return "label=" + label; }
    internal static string Limits() => Probe.FrameLimits().Replace('\t', ';');
    /// <summary>FarAnimatorThrottle's hit-box offset: returns the statistics so far, then starts (true, from zero) or stops measuring.</summary>
    internal static string Lag(bool on) => CrowdLoad.Lag(on);
    internal static string Animators() => CrowdLoad.Animators();
    internal static string Physics() => ProbePhysics.Read();
    /// <summary>Starts a chase measurement; poll <see cref="ChaseState"/> (it answers "done…" when <paramref name="expected"/>
    /// enemies are within <paramref name="radius"/> metres, "done-timeout…" after 45 s).</summary>
    internal static string Chase(int expected, float radius) => CrowdLoad.Chase(expected, radius);
    internal static string ChaseState() => CrowdLoad.ChaseState();
    /// <summary>GPU timeline of the next frames, and its state; <see cref="Finish"/> ends the stack sampling and writes the names (once, last).</summary>
    internal static string Capture(int frames) => ProbeNative.Capture(frames);
    internal static string CaptureState() => ProbeNative.CaptureState();
    internal static string Finish() => ProbeNative.Finish();
    internal static string Foreground() => Probe.Foreground();
    internal static string Background() => Probe.Background();
    internal static string Read()
        => "switches=" + CrowdSwitches.Describe() + ";probe=" + (Probe.On ? "on" : "off") + ";enemies=" + Probe.Enemies
            + ";far_skins=" + Probe.FarSkins + ";throttled_animators=" + Probe.ThrottledAnimators + ";throttled_syncs=" + Probe.ThrottledSyncs
            + ";" + (LoadSheddingController.Current?.CrowdWork?.ReadGlobals() ?? "crowd=not-installed");
}
