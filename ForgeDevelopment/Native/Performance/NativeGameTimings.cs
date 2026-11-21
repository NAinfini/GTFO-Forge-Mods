using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;

namespace ForgeDevelopment.Native;

internal static class NativeGameTimings
{
    // Each package supplies its own registration path; sampling never installs patches.
    private static Func<MethodInfo, MethodInfo, IDisposable>? _subscribeMarker;
    private static readonly Dictionary<MethodInfo, IDisposable> MarkerLeases = new();
    internal static void ConfigureMarkers(Func<MethodInfo, MethodInfo, IDisposable> subscribe)
    {
        if (subscribe == null) throw new ArgumentNullException(nameof(subscribe));
        if (MarkerLeases.Count != 0)
            throw new InvalidOperationException("Cannot replace an active performance marker registration path.");
        _subscribeMarker = subscribe;
    }
    private static PerformanceTimings? _markerTimings;
    private static Func<object, IntPtr>? _pointer;
    private static Func<object, bool>? _active;
    internal static string MarkerEntries { get; private set; } = "unavailable";
    internal static string MarkerUnavailable { get; private set; } = "none";
    internal static string MarkerCoverage => "local_StartWave_StopEvent_calls;host_and_client_RegisterGroup_calls;per_wave_client_boundaries_unavailable;alarm_replicated_OnStateChange_host_and_client_isActive_transitions;recall_excluded";
    private const string MarkerObservation = ";declared_postfix";
    // All are non-generic, non-static void() instance methods in the current
    // dump. Raw timing avoids per-call interop object wrappers. SurvivalWave.SpawnGroup
    // is not timed: ForgeEngine patches that entry, so a raw timing on it could never install
    // (entry_already_patched); group registrations are counted by the RegisterGroup marker.
    // Both native arguments (this, MethodInfo*) are forwarded untouched.
    internal static readonly (int Rva, TimingPoint? Point, string Name, PerformanceMark Mark)[] Entries =
    {
        (0x1582240, TimingPoint.EnemyUpdate, "Enemies.EnemyUpdateManager.GamestateUpdate", PerformanceMark.None),
        (0x15821D0, TimingPoint.EnemyFixed, "Enemies.EnemyUpdateManager.GamestateUpdateFixed", PerformanceMark.None),
        (0x1582420, TimingPoint.EnemyMaster, "Enemies.EnemyUpdateManager.MasterMindUpdate", PerformanceMark.None),
        (0x15823B0, TimingPoint.EnemyMasterFixed, "Enemies.EnemyUpdateManager.MasterMindUpdateFixed", PerformanceMark.None),
        (0x13F5A10, null, "SurvivalWave.StartWave", PerformanceMark.WaveStart),
        (0x13F5BE0, null, "SurvivalWave.StopEvent", PerformanceMark.WaveStop)
    };
    internal static string Install(PerformanceTimings timings, List<IDisposable> hooks, bool alarmFieldsVerified)
    {
        string reason;
        IntPtr module;
        try { module = NativeModuleFingerprint.Verified("GameAssembly.dll", NativeModuleFingerprint.GameSha256, out reason); }
        catch (Exception error) { module = IntPtr.Zero; reason = "GameAssembly_verification_failed:" + error.Message; }
        if (module == IntPtr.Zero)
        {
            for (var i = (int)TimingPoint.EnemyUpdate; i < (int)TimingPoint.Count; i++) timings.Points[i].Unavailable(reason);
            return reason;
        }
        var failures = new List<string>();
        _markerTimings = timings;
        var installedMarkers = new List<string>();
        var unavailableMarkers = new List<string>();
        foreach (var entry in Entries)
        {
            // Markers use the supplied observation declaration, never raw timings.
            // Alarm transitions use the replicated OnStateChange below, on
            // both host and client, rather than host-only Master* calls.
            var counter = entry.Point is { } point ? timings.Points[(int)point] : null;
            try
            {
                var target = IntPtr.Add(module, entry.Rva);
                if (!InstanceVoidWithoutParameters(entry.Name)) throw new InvalidOperationException("native_method_signature_mismatch");
                if (entry.Point == null)
                {
                    InstallMarker(entry.Name, entry.Mark == PerformanceMark.WaveStart ? nameof(WaveStarted) : nameof(WaveStopped), 0);
                    installedMarkers.Add(entry.Name + MarkerObservation + ";existing_patches_retained");
                    continue;
                }
                if (!NativeModuleFingerprint.OriginalCode(module, target)) throw new InvalidOperationException("entry_already_patched_or_outside_text");
                hooks.Add(NativeTimingRegistration.Observe("GameAssembly.dll", NativeModuleFingerprint.GameSha256, target, timings, counter, 1)); counter?.Installed(entry.Name + ";GameAssembly_rva=0x" + entry.Rva.ToString("X"));
            }
            catch (Exception error) { counter?.Unavailable(error.GetBaseException().Message); failures.Add(entry.Name + ":" + error.GetBaseException().Message); if (entry.Point == null) unavailableMarkers.Add(entry.Name + ":" + error.GetBaseException().Message); }
        }
        try
        {
            InstallMarker("SurvivalWave.RegisterGroup", nameof(GroupRegistered), 2);
            installedMarkers.Add("SurvivalWave.RegisterGroup" + MarkerObservation + ";host_and_client;recall_excluded;registration_calls_not_spawn_success");
        }
        catch (Exception error) { unavailableMarkers.Add("SurvivalWave.RegisterGroup:" + error.GetBaseException().Message); failures.Add("SurvivalWave.RegisterGroup:" + error.GetBaseException().Message); }
        try
        {
            if (!alarmFieldsVerified) throw new InvalidOperationException("alarm_field_layout_unverified");
            InstallMarker("ChainedPuzzles.ChainedPuzzleInstance.OnStateChange", nameof(AlarmStateChanged), 3);
            installedMarkers.Add("ChainedPuzzles.ChainedPuzzleInstance.OnStateChange" + MarkerObservation + ";host_and_client;recall_excluded;isActive_transition_only");
        }
        catch (Exception error) { unavailableMarkers.Add("ChainedPuzzleInstance.OnStateChange:" + error.GetBaseException().Message); failures.Add("ChainedPuzzleInstance.OnStateChange:" + error.GetBaseException().Message); }
        MarkerEntries = installedMarkers.Count == 0 ? "unavailable" : string.Join("|", installedMarkers);
        MarkerUnavailable = unavailableMarkers.Count == 0 ? "none" : string.Join("|", unavailableMarkers);
        try
        {
            // il2cpp_gc_collect(int) forwards to 0xECBE0; engine-internal calls
            // to that implementation are included. Automatic allocation-driven
            // incremental GC remains explicitly unmeasured.
            var target = IntPtr.Add(module, 0xECBE0);
            if (!NativeModuleFingerprint.OriginalCode(module, target)) throw new InvalidOperationException("gc_entry_already_patched");
            var counter = timings.Points[(int)TimingPoint.ExplicitGc];
            hooks.Add(NativeTimingRegistration.Observe("GameAssembly.dll", NativeModuleFingerprint.GameSha256, target, timings, counter, 2)); counter.Installed("il2cpp_gc_collect(int);GameAssembly_rva=0xECBE0;explicit_collections_only");
        }
        catch (Exception error) { timings.Points[(int)TimingPoint.ExplicitGc].Unavailable(error.GetBaseException().Message); failures.Add("explicit_gc:" + error.GetBaseException().Message); }
        return failures.Count == 0 ? "none" : string.Join(";", failures);
    }

    private static void InstallMarker(string fullName, string callback, int parameters)
    {
        var split = fullName.LastIndexOf('.');
        var typeName = fullName.Substring(0, split); var name = fullName.Substring(split + 1);
        var type = Assembly.Load("Modules-ASM").GetType(typeName, true)!;
        var original = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (original == null || original.IsStatic || original.ReturnType != typeof(void) || original.GetParameters().Length != parameters)
            throw new InvalidOperationException("marker_interop_signature_unverified");
        if (parameters == 2 && (original.GetParameters()[0].ParameterType.Name != "EnemyGroup" || original.GetParameters()[1].ParameterType != typeof(bool)))
            throw new InvalidOperationException("group_registration_signature_unverified");
        if (parameters == 3)
        {
            var args = original.GetParameters();
            if (args[0].ParameterType.FullName != "ChainedPuzzles.pChainedPuzzleState" || args[1].ParameterType != args[0].ParameterType
                || args[2].ParameterType != typeof(bool)) throw new InvalidOperationException("alarm_state_signature_unverified");
            var state = IL2CPP.GetIl2CppClass("Modules-ASM.dll", "ChainedPuzzles", "pChainedPuzzleState");
            uint alignment = 0;
            if (state == IntPtr.Zero || IL2CPP.il2cpp_class_value_size(state, ref alignment) != 6
                || PerformanceSceneReader.Offset(state, "isActive") != 0x14)
                throw new InvalidOperationException("alarm_replicated_state_layout_unverified");
            var value = Expression.Parameter(typeof(object));
            _active = Expression.Lambda<Func<object, bool>>(Expression.Field(Expression.Convert(value, args[0].ParameterType), "isActive"), value).Compile();
            var baseType = typeof(IL2CPP).Assembly.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", true)!;
            _pointer = Expression.Lambda<Func<object, IntPtr>>(Expression.Property(Expression.Convert(value, baseType), "Pointer"), value).Compile();
        }
        PatchMarker(original, typeof(NativeGameTimings).GetMethod(callback, BindingFlags.Static | BindingFlags.NonPublic)!);
    }
    internal static void PatchMarker(MethodInfo original, MethodInfo callback)
    {
        if (MarkerLeases.ContainsKey(original)) throw new InvalidOperationException("Duplicate performance marker target: " + original);
        var subscribe = _subscribeMarker ?? throw new InvalidOperationException("performance_marker_registration_unavailable");
        MarkerLeases.Add(original, subscribe(original, callback)
            ?? throw new InvalidOperationException("Performance marker registration returned no lease."));
    }
    private static void WaveStarted() => WaveMark(PerformanceMark.WaveStart);
    private static void WaveStopped() => WaveMark(PerformanceMark.WaveStop);
    private static void GroupRegistered(bool isRecall) { if (!isRecall) WaveMark(PerformanceMark.SpawnGroup); }
    private static void WaveMark(PerformanceMark mark)
    {
        var timings = _markerTimings;
        if (timings != null && ThreadCpuClock.CurrentThreadId() == timings.MainThread) timings.Mark(mark);
    }
    private static void AlarmStateChanged(object __instance, object oldState, object newState, bool isRecall)
    {
        var timings = _markerTimings;
        if (timings == null || ThreadCpuClock.CurrentThreadId() != timings.MainThread || isRecall) return;
        var mark = AlarmTransition(_pointer!(__instance), _active!(oldState), _active(newState), isRecall);
        if (mark != PerformanceMark.None) timings.Mark(mark);
    }
    internal static PerformanceMark AlarmTransition(IntPtr puzzle, bool oldActive, bool newActive, bool isRecall)
        => isRecall || oldActive == newActive || !PerformanceSceneReader.IsAlarmPuzzle(puzzle) ? PerformanceMark.None
            : newActive ? PerformanceMark.AlarmStart : PerformanceMark.AlarmStop;
    internal static void StopMarkers()
    {
        _markerTimings = null; // Stop observations even when a physical removal fails.
        var failures = new List<Exception>();
        foreach (var pair in MarkerLeases.ToArray())
        {
            try { pair.Value.Dispose(); MarkerLeases.Remove(pair.Key); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Performance marker cleanup remains pending.", failures);
    }
    private static bool InstanceVoidWithoutParameters(string fullName)
    {
        var split = fullName.LastIndexOf('.'); var type = fullName.Substring(0, split); var method = fullName.Substring(split + 1);
        var namespaceEnd = type.LastIndexOf('.');
        var ns = namespaceEnd < 0 ? "" : type.Substring(0, namespaceEnd);
        var name = type.Substring(namespaceEnd + 1);
        var klass = IL2CPP.GetIl2CppClass("Modules-ASM.dll", ns, name);
        if (klass == IntPtr.Zero) return false;
        var info = IL2CPP.il2cpp_class_get_method_from_name(klass, method, 0);
        if (info == IntPtr.Zero || IL2CPP.il2cpp_method_get_param_count(info) != 0) return false;
        uint implementationFlags = 0;
        return (IL2CPP.il2cpp_method_get_flags(info, ref implementationFlags) & 0x10) == 0
            && IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_method_get_return_type(info)) == 1; // IL2CPP_TYPE_VOID
    }
}
