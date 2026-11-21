using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ForgeDevelopment.Native;

internal static class NativePlayerLoop
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetLoop();
    private static Il2CppSystem.Object? _currentRoot, _defaultRoot;
    private static readonly (TimingPoint Point, string Type)[] Phases =
    {
        (TimingPoint.Physics, "UnityEngine.PlayerLoop.FixedUpdate+PhysicsFixedUpdate"),
        (TimingPoint.AnimationBegin, "UnityEngine.PlayerLoop.PreLateUpdate+DirectorUpdateAnimationBegin"),
        (TimingPoint.AnimationEnd, "UnityEngine.PlayerLoop.PreLateUpdate+DirectorUpdateAnimationEnd"),
        (TimingPoint.BehaviourUpdate, "UnityEngine.PlayerLoop.Update+ScriptRunBehaviourUpdate"),
        (TimingPoint.BehaviourFixed, "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate"),
        (TimingPoint.BehaviourLate, "UnityEngine.PlayerLoop.PreLateUpdate+ScriptRunBehaviourLateUpdate"),
        (TimingPoint.Renderers, "UnityEngine.PlayerLoop.PostLateUpdate+UpdateAllRenderers"),
        (TimingPoint.RenderSubmission, "UnityEngine.PlayerLoop.PostLateUpdate+FinishFrameRendering"),
        (TimingPoint.Navigation, "UnityEngine.PlayerLoop.PreUpdate+AIUpdate")
    };
    private static readonly (TimingPoint Point, string Type)[] Groups =
    {
        (TimingPoint.Initialization, "UnityEngine.PlayerLoop.Initialization"),
        (TimingPoint.EarlyUpdate, "UnityEngine.PlayerLoop.EarlyUpdate"),
        (TimingPoint.PreUpdateOther, "UnityEngine.PlayerLoop.PreUpdate"),
        (TimingPoint.PostLateUpdateOther, "UnityEngine.PlayerLoop.PostLateUpdate")
    };
    internal readonly record struct LoopEntry(string Group, IntPtr Function, bool Leaf);
    // Group the leaf work left outside existing timing points. Canvas, particles,
    // audio, loading, input and end-of-frame callbacks stay observable. This is
    // remaining work, not a claim that every included call is CPU-only.
    internal static bool InGroup(string type, LoopEntry entry, string group) => entry.Leaf && entry.Group == group
        && type != "UnityEngine.PlayerLoop.PreUpdate+AIUpdate"
        && type != "UnityEngine.PlayerLoop.PostLateUpdate+UpdateAllRenderers"
        && type != "UnityEngine.PlayerLoop.PostLateUpdate+FinishFrameRendering";

    internal static void Install(PerformanceTimings timings, List<IDisposable> hooks)
    {
        timings.Points[(int)TimingPoint.PhysicsLate].Unavailable("no_3D_PhysicsLateUpdate_type_in_native_dump");
        try
        {
            var module = NativeModuleFingerprint.Verified("UnityPlayer.dll", NativePresentWait.PlayerSha256, out var reason);
            if (module == IntPtr.Zero) { Missing(timings, reason); return; }
            // A 40-byte inline value, not the reconstructed interop reference
            // array. The export at 0x909DB0 explicitly copies five fields with
            // stride 0x28. The executor at 0x78DA00 dereferences the function
            // slot then calls it without arguments or a return value.
            var klass = IL2CPP.GetIl2CppClass("UnityEngine.CoreModule.dll", "UnityEngine.LowLevel", "PlayerLoopSystemInternal");
            uint alignment = 0;
            if (klass == IntPtr.Zero || IL2CPP.il2cpp_class_value_size(klass, ref alignment) != 40)
            { Missing(timings, "PlayerLoopSystemInternal_value_layout_unverified"); return; }
            var names = new[] { "type", "updateDelegate", "updateFunction", "loopConditionFunction", "numSubSystems" };
            for (var i = 0; i < names.Length; i++)
                if (PerformanceSceneReader.Offset(klass, names[i]) != 16 + i * 8)
                { Missing(timings, "PlayerLoopSystemInternal_field_layout_unverified"); return; }
            var current = IL2CPP.ResolveICall<GetLoop>("UnityEngine.LowLevel.PlayerLoop::GetCurrentPlayerLoopInternal")();
            var defaults = IL2CPP.ResolveICall<GetLoop>("UnityEngine.LowLevel.PlayerLoop::GetDefaultPlayerLoopInternal")();
            if (current == IntPtr.Zero || defaults == IntPtr.Zero) { Missing(timings, "native_player_loop_array_unavailable"); return; }
            // GC handles are created only at startup, keeping both native arrays
            // alive during discovery. Never invoke reconstructed array indexing.
            _currentRoot = new Il2CppSystem.Object(current); _defaultRoot = new Il2CppSystem.Object(defaults);
            var actual = Read(current); var stock = Read(defaults);
            var uses = new Dictionary<IntPtr, int>();
            foreach (var entry in actual.Values)
                if (entry.Function != IntPtr.Zero) uses[entry.Function] = uses.TryGetValue(entry.Function, out var count) ? count + 1 : 1;
            var targets = new HashSet<IntPtr>();
            foreach (var phase in Phases)
            {
                var counter = timings.Points[(int)phase.Point];
                var failure = InstallEntry(phase.Type, module, actual, stock, uses, targets, timings, counter, hooks, out var installed);
                if (failure != "none") counter.Unavailable(failure);
                else counter.Installed(installed);
            }
            foreach (var group in Groups)
            {
                var counter = timings.Points[(int)group.Point];
                var installed = new List<string>(); var missing = new List<string>();
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                // Union also exposes missing stock leaves and custom current
                // leaves. Never label a partial stage as fully covered.
                foreach (var item in stock) if (InGroup(item.Key, item.Value, group.Type)) candidates.Add(item.Key);
                foreach (var item in actual) if (InGroup(item.Key, item.Value, group.Type)) candidates.Add(item.Key);
                foreach (var name in candidates)
                {
                    var failure = InstallEntry(name, module, actual, stock, uses, targets, timings, counter, hooks, out var entry);
                    if (failure == "none") installed.Add(entry); else missing.Add(name + ":" + failure);
                }
                if (installed.Count == 0) counter.Unavailable("no_verified_native_leaf_in_stage");
                else counter.Installed(string.Join(";", installed));
                counter.AggregateCoverage(missing.Count == 0 && installed.Count != 0 ? "none"
                    : missing.Count == 0 ? "stage_has_no_native_leaf_entries" : string.Join(";", missing));
            }
        }
        catch (AggregateException) { throw; }
        catch (Exception error) { Missing(timings, "native_PlayerLoop_discovery_failed:" + error.GetBaseException().Message); }
        finally { _currentRoot = _defaultRoot = null; }
    }

    private static string InstallEntry(string name, IntPtr module, Dictionary<string, LoopEntry> actual,
        Dictionary<string, LoopEntry> stock, Dictionary<IntPtr, int> uses, HashSet<IntPtr> targets,
        PerformanceTimings timings, TimingCounter counter, List<IDisposable> hooks, out string installed)
    {
        installed = "unavailable";
        if (!actual.TryGetValue(name, out var current) || !stock.TryGetValue(name, out var expected)) return "phase_not_in_native_PlayerLoop";
        var target = current.Function;
        if (!current.Leaf || !expected.Leaf || current.Group != expected.Group) return "phase_hierarchy_changed";
        if (target == IntPtr.Zero || target != expected.Function) return "phase_has_no_native_function_or_managed_delegate_or_changed_function";
        if (!NativeModuleFingerprint.OriginalCode(module, target)) return "phase_entry_outside_verified_text_or_already_patched";
        if (uses[target] != 1) return "native_entry_shared_by_multiple_PlayerLoop_phases";
        if (!targets.Add(target)) return "phase_shares_native_entry_with_another_phase";
        try { hooks.Add(NativeTimingRegistration.Observe("UnityPlayer.dll", NativePresentWait.PlayerSha256, target, timings, counter, 0)); }
        catch (Exception error)
        {
            targets.Remove(target);
            return "phase_install_failed:" + error.GetBaseException().Message;
        }
        installed = name + "@UnityPlayer_rva=0x" + (target.ToInt64() - module.ToInt64()).ToString("X");
        return "none";
    }
    private static Dictionary<string, LoopEntry> Read(IntPtr array)
    {
        var count = IL2CPP.il2cpp_array_length(array);
        if (count < 1 || count > 1024) throw new InvalidOperationException("invalid_player_loop_length");
        var entries = new (string Type, IntPtr Function, int Descendants)[count];
        for (var i = 0; i < count; i++)
        {
            var item = IntPtr.Add(array, 32 + i * 40);
            var type = Marshal.ReadIntPtr(item);
            var name = type == IntPtr.Zero ? "unnamed_leaf_" + i : new Il2CppSystem.Type(type).FullName;
            var slot = Marshal.ReadIntPtr(item, 16);
            var function = slot == IntPtr.Zero || Marshal.ReadIntPtr(item, 8) != IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
            entries[i] = (name, function, Marshal.ReadInt32(item, 32));
        }
        return GroupEntries(entries);
    }
    // Unity 2019.4 PlayerLoop.bindings.cs stores the total descendant count,
    // not the direct child count. Walk each encoded subtree's exclusive end.
    internal static Dictionary<string, LoopEntry> GroupEntries((string Type, IntPtr Function, int Descendants)[] entries)
    {
        var count = entries.Length;
        if (count < 1 || count > 1024) throw new InvalidOperationException("invalid_player_loop_length");
        var result = new Dictionary<string, LoopEntry>(StringComparer.Ordinal);
        var index = 0;
        void Subtree(string group)
        {
            if (index >= count) throw new InvalidOperationException("truncated_player_loop_tree");
            var item = entries[index++];
            var name = item.Type;
            foreach (var known in Groups) if (known.Type == name) group = name;
            if (item.Descendants < 0 || item.Descendants > count - index) throw new InvalidOperationException("invalid_player_loop_descendant_count");
            var end = index + item.Descendants;
            var value = new LoopEntry(group, item.Function, item.Descendants == 0);
            if (!result.TryAdd(name, value)) result[name] = value with { Function = IntPtr.Zero };
            while (index < end) Subtree(group);
            if (index != end) throw new InvalidOperationException("player_loop_subtree_exceeds_parent_range");
        }
        Subtree("");
        if (index != count) throw new InvalidOperationException("player_loop_tree_has_unconsumed_entries");
        return result;
    }
    private static void Missing(PerformanceTimings timings, string reason)
    {
        foreach (var phase in Phases)
            if (!timings.Points[(int)phase.Point].Available) timings.Points[(int)phase.Point].Unavailable(reason);
        foreach (var group in Groups)
            if (!timings.Points[(int)group.Point].Available)
            { timings.Points[(int)group.Point].Unavailable(reason); timings.Points[(int)group.Point].AggregateCoverage(reason); }
    }
}
