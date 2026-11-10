using System;
using BepInEx.Logging;
using GTFO.API;
using HarmonyLib;
using WindVolume;


namespace ForgePerformance.LoadShedding;

// Independently configured safety and visual views share only the native boundary.
internal sealed class WindCapacityProtection : IDisposable
{
    private static WindCapacityProtection? _current;
    private readonly Harmony _harmony = new("NAinfini.ForgePerformance.WindCapacity");
    private readonly WindCapacityGuard? _guard;
    private readonly WindCapacityGuard? _trim;
    private readonly ManualLogSource _log;
    private readonly SheddingSettings _settings;
    internal WindCapacityProtection(ManualLogSource log, SheddingSettings settings)
    {
        _log = log; _settings = settings;
        if (settings.WindCapacityProtection) _guard = new(settings with { WindNearest = false });
        if (settings.WindNearest && (settings.WindLimit < SheddingPolicy.WindCapacity || _guard == null)) _trim = new(settings);
    }

    internal void Install()
    {
        if (_guard == null && _trim == null) return;
        if (NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out var reason) == IntPtr.Zero)
            throw new InvalidOperationException("WindVolume refuses an unverified game assembly: " + reason);
        if (IntPtr.Size != 8 || WindVolumeCamera.MAX_AFFECTOR_COUNT != SheddingPolicy.WindCapacity)
            throw new InvalidOperationException("The WindVolume fixed capacity does not match the checked version.");
        _current = this;
        try
        {
            _harmony.CreateClassProcessor(typeof(WindEnable)).Patch();
            _harmony.CreateClassProcessor(typeof(WindDisable)).Patch();
            _harmony.CreateClassProcessor(typeof(WindUpdate)).Patch();
            EventAPI.OnGameStateChanged += OnStateChanged; // GTFO-API's: the game's own delegate added from a mod never fires
        }
        catch { Dispose(); throw; }
    }
    private void OnStateChanged(eGameStateName oldState, eGameStateName newState)
    {
        if (oldState == eGameStateName.InLevel && newState != eGameStateName.InLevel) Finish();
    }
    internal void Finish()
    {
        if (_guard != null) Write("wind-capacity", _guard);
        if (_trim != null) Write("wind-nearest", _trim);
        _guard?.Clear(); _trim?.Clear();
    }
    private void Write(string item, WindCapacityGuard view)
    {
        var line = "type=load_shedding\titem=" + item + "\tlimit=" + view.Limit + "\tguard_calls=" + view.Calls
            + "\tsnapshot_copies=" + view.SnapshotCopies + "\tdistance_reads=" + view.DistanceReads
            + "\tselection_writes=" + view.SelectionWrites + "\trefused_updates=" + view.Refused
            + "\trestored_calls=" + view.Restored + "\tpeak=" + view.Peak
            + "\trefresh_budget=" + WindCapacityGuard.RefreshBudget;
        if (_settings.SummaryLog) _log.LogInfo(line);
        view.Calls = view.SnapshotCopies = view.DistanceReads = view.SelectionWrites = view.Refused = view.Restored = 0;
        view.Peak = 0;
    }
    // Optional visual work runs first. The independent safety guard only builds
    // a view if vanilla would exceed its fixed capacity of 64.
    internal bool Before(WindVolumeCamera camera, out WindState state)
    {
        state = default;
        if (_trim != null && !_trim.Prefix(camera, out state.Trim)) return false;
        return _guard?.Prefix(camera, out state.Capacity) ?? true;
    }
    internal Exception? After(WindState state, Exception? error)
    {
        if (_guard != null) error = _guard.Finalizer(state.Capacity, error);
        return _trim?.Finalizer(state.Trim, error) ?? error;
    }
    internal struct WindState
    {
        internal WindCapacityGuard.Scope? Capacity, Trim;
    }
    public void Dispose()
    {
        EventAPI.OnGameStateChanged -= OnStateChanged;
        _guard?.Clear(); _trim?.Clear(); _harmony.UnpatchSelf(); _current = null;
    }

    private static void Register(WindVolumeAffector affector)
    { _current?._guard?.Register(affector); _current?._trim?.Register(affector); }
    private static void Unregister(WindVolumeAffector affector)
    { _current?._guard?.Unregister(affector); _current?._trim?.Unregister(affector); }

    [HarmonyPatch(typeof(WindVolumeAffector), nameof(WindVolumeAffector.OnEnable))]
    private static class WindEnable
    { [HarmonyPostfix] private static void Postfix(WindVolumeAffector __instance) => Register(__instance); }
    [HarmonyPatch(typeof(WindVolumeAffector), nameof(WindVolumeAffector.OnDisable))]
    private static class WindDisable
    { [HarmonyPostfix] private static void Postfix(WindVolumeAffector __instance) => Unregister(__instance); }
    [HarmonyPatch(typeof(WindVolumeCamera), nameof(WindVolumeCamera.UpdateVolume))]
    private static class WindUpdate
    {
        [HarmonyPrefix]
        private static bool Prefix(WindVolumeCamera __instance, out WindState __state)
        { __state = default; return _current?.Before(__instance, out __state) ?? true; }
        [HarmonyFinalizer]
        private static Exception? Finalizer(WindState __state, Exception? __exception)
            => _current?.After(__state, __exception) ?? __exception;
    }
}
