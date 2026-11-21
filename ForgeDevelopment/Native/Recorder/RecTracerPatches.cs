using System;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using System.Reflection;

namespace ForgeDevelopment.Native;

/// <summary>
/// The one generic prefix/postfix pair every trace patch uses. Harmony binds them per target: <c>__instance</c>,
/// <c>__args</c>, <c>__originalMethod</c> and <c>__result</c> are resolved against the patched method, so a single
/// pair covers every signature the profiles match. Every decision — whether the call is over its rate, whether it is
/// a change, what is written — belongs to <see cref="RecPatch"/>; this file only carries the call into it.
/// </summary>
internal static class RecTracerPatches
{
    internal static MethodInfo ObservationPrefixMethod { get; } = Shared(nameof(ObservationPrefix));
    internal static MethodInfo ObservationReturnedMethod { get; } = Shared(nameof(ObservationReturned));
    internal static MethodInfo ObservationFinalizerMethod { get; } = Shared(nameof(ObservationFinalizer));
    internal static MethodInfo ObservationManagedPrefixMethod { get; } = Shared(nameof(ObservationManagedPrefix));
    internal static MethodInfo ObservationManagedReturnedMethod { get; } = Shared(nameof(ObservationManagedReturned));

    internal static void ObservationPrefix(MethodBase __originalMethod, out RecTraceInvocation? __state)
        => __state = RecTracerRuntime.BeginObservation(__originalMethod);

    // This native-return bridge binds no receiver, arguments, result, native context or legacy trace frame.
    internal static void ObservationReturned(RecTraceInvocation? __state, bool __runOriginal)
    {
        if (__runOriginal && __state != null) RecTracerRuntime.ReturnObservation(__state);
    }

    internal sealed class ObservationCall
    {
        internal ObservationCall(RecTraceInvocation invocation) { Invocation = invocation; }
        internal RecTraceInvocation Invocation { get; }
        internal bool Returned;
    }

    internal static void ObservationManagedPrefix(MethodBase __originalMethod, out ObservationCall? __state)
    {
        var invocation = RecTracerRuntime.BeginObservation(__originalMethod);
        __state = invocation == null ? null : new ObservationCall(invocation);
    }

    internal static void ObservationManagedReturned(ObservationCall? __state, bool __runOriginal)
    {
        if (__state != null) __state.Returned = __runOriginal;
    }

    // Shipped HarmonyX cannot bind __runOriginal in finalizers. Postfix marks a normal managed return first.
    internal static void ObservationFinalizer(ObservationCall? __state, Exception? __exception)
    {
        if (__exception == null && __state is { Returned: true })
            RecTracerRuntime.ReturnObservation(__state.Invocation);
    }

    /// <summary>The method the tracer installs. It is resolved once here, so a HarmonyX that cannot bind the pair is
    /// a startup failure the tracer reports rather than a patch that silently does nothing.</summary>
    internal static MethodInfo PrefixMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static MethodInfo PostfixMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static MethodInfo PostfixVoidMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(PostfixVoid), BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static MethodInfo FinalizerMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(Finalizer), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static MethodInfo PrefixCountMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(PrefixCount), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static MethodInfo PostfixCountMethod { get; } = typeof(RecTracerPatches).GetMethod(nameof(PostfixCount), BindingFlags.Static | BindingFlags.NonPublic)!;

    // Shared-native observers are read-only callbacks. Their returned snapshots
    // run in the backend's normal-return finalizer lane, after native postfixes.
    internal static MethodInfo SharedReturnedMethod { get; } = Shared(nameof(SharedReturned));
    internal static MethodInfo SharedStaticPrefixMethod { get; } = Shared(nameof(SharedStaticPrefix));
    internal static MethodInfo SharedStaticCountPrefixMethod { get; } = Shared(nameof(SharedStaticCountPrefix));
    internal static MethodInfo SharedStaticReturnedMethod { get; } = Shared(nameof(SharedStaticReturned));
    internal static MethodInfo SharedStaticVoidMethod { get; } = Shared(nameof(SharedStaticVoid));
    internal static MethodInfo SharedStaticCountReturnedMethod { get; } = Shared(nameof(SharedStaticCountReturned));
    internal static MethodInfo SharedNoisePrefixMethod { get; } = Shared(nameof(SharedNoisePrefix));
    internal static MethodInfo SharedNoiseReturnedMethod { get; } = Shared(nameof(SharedNoiseReturned));
    internal static MethodInfo SharedMakeNoisePrefixMethod { get; } = Shared(nameof(SharedMakeNoisePrefix));
    internal static MethodInfo SharedMakeNoiseReturnedMethod { get; } = Shared(nameof(SharedMakeNoiseReturned));
    internal static MethodInfo SharedProcessNodePrefixMethod { get; } = Shared(nameof(SharedProcessNodePrefix));
    internal static MethodInfo SharedProcessNodeReturnedMethod { get; } = Shared(nameof(SharedProcessNodeReturned));
    private static MethodInfo Shared(string name) => typeof(RecTracerPatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void SharedReturned(object __instance, object?[] __args, MethodBase __originalMethod,
        object? __result, Frame __state, bool __runOriginal)
    { var result = __result; Complete(__instance, __args, __originalMethod, ref result, __state, __runOriginal, null); }
    private static void SharedStaticPrefix(object?[] __args, MethodBase __originalMethod, out Frame __state)
        => Begin(null!, __args, __originalMethod, out __state, null);
    private static void SharedStaticCountPrefix(MethodBase __originalMethod, out Frame __state)
        => Begin(null!, Array.Empty<object?>(), __originalMethod, out __state, null);
    private static void SharedStaticReturned(object?[] __args, MethodBase __originalMethod,
        object? __result, Frame __state, bool __runOriginal)
    { var result = __result; Complete(null!, __args, __originalMethod, ref result, __state, __runOriginal, null); }
    private static void SharedStaticVoid(object?[] __args, MethodBase __originalMethod, Frame __state, bool __runOriginal)
    { object? result = null; Complete(null!, __args, __originalMethod, ref result, __state, __runOriginal, null); }
    private static void SharedStaticCountReturned(MethodBase __originalMethod, Frame __state, bool __runOriginal)
    { object? result = null; Complete(null!, Array.Empty<object?>(), __originalMethod, ref result, __state, __runOriginal, null); }
    private static void SharedNoisePrefix(object __instance, NM_NoiseData __0, float __1,
        MethodBase __originalMethod, out Frame __state)
        => Begin(__instance, Array.Empty<object?>(), __originalMethod, out __state, NoiseArguments.Noise(__0, __1, __originalMethod));
    private static void SharedNoiseReturned(object __instance, NM_NoiseData __0, float __1,
        MethodBase __originalMethod, Frame __state, bool __runOriginal)
        => EndVoid(__instance, __originalMethod, __state, __runOriginal, NoiseArguments.Noise(__0, __1, __originalMethod));
    private static void SharedMakeNoisePrefix(NM_NoiseData __0, MethodBase __originalMethod, out Frame __state)
        => Begin(null!, Array.Empty<object?>(), __originalMethod, out __state, NoiseArguments.MakeNoise(__0, __originalMethod));
    private static void SharedMakeNoiseReturned(NM_NoiseData __0, MethodBase __originalMethod, Frame __state, bool __runOriginal)
        => EndVoid(null!, __originalMethod, __state, __runOriginal, NoiseArguments.MakeNoise(__0, __originalMethod));

    // The physical bridge supplies fresh ref values by value to read-only consumers.
    private static void SharedProcessNodePrefix(AIGraph.AIG_CourseNode __0, NM_NoiseData __1, bool __2, int __3,
        MethodBase __originalMethod, out Frame __state)
        => Begin(null!, Array.Empty<object?>(), __originalMethod, out __state,
            NoiseArguments.ProcessNode(__0, __1, __2, __3, __originalMethod));
    private static void SharedProcessNodeReturned(AIGraph.AIG_CourseNode __0, NM_NoiseData __1, bool __2, int __3,
        MethodBase __originalMethod, Frame __state, bool __runOriginal)
        => EndVoid(null!, __originalMethod, __state, __runOriginal,
            NoiseArguments.ProcessNode(__0, __1, __2, __3, __originalMethod));

    /// <summary>How a hook reads its arguments once the rate gate admits the call. The noise hooks pass their raw
    /// parameters in a struct so a call the gate refuses allocates nothing (a capturing lambda allocated a closure and a
    /// delegate before the gate decided).</summary>
    internal interface IArgumentReader { bool Present { get; } object?[] Read(); }
    private readonly struct NoReader : IArgumentReader
    {
        public bool Present => false;
        public object?[] Read() => Array.Empty<object?>();
    }
    private readonly struct FuncReader : IArgumentReader
    {
        private readonly Func<object?[]>? _read;
        internal FuncReader(Func<object?[]>? read) { _read = read; }
        public bool Present => _read != null;
        public object?[] Read() => _read!();
    }
    private readonly struct NoiseArguments : IArgumentReader
    {
        private readonly byte _kind;
        private readonly NM_NoiseData? _noise;
        private readonly AIGraph.AIG_CourseNode? _node;
        private readonly float _amount;
        private readonly bool _flag;
        private readonly int _number;
        private readonly MethodBase _method;
        private NoiseArguments(byte kind, NM_NoiseData? noise, AIGraph.AIG_CourseNode? node, float amount, bool flag, int number, MethodBase method)
        { _kind = kind; _noise = noise; _node = node; _amount = amount; _flag = flag; _number = number; _method = method; }
        internal static NoiseArguments Noise(NM_NoiseData noise, float amount, MethodBase method) => new(0, noise, null, amount, false, 0, method);
        internal static NoiseArguments MakeNoise(NM_NoiseData noise, MethodBase method) => new(1, noise, null, 0f, false, 0, method);
        internal static NoiseArguments ProcessNode(AIGraph.AIG_CourseNode node, NM_NoiseData noise, bool flag, int number, MethodBase method)
            => new(2, noise, node, 0f, flag, number, method);
        public bool Present => true;
        public object?[] Read() => _kind switch
        {
            0 => RecNoiseTrace.Arguments(_noise!, _amount, _method),
            1 => RecNoiseTrace.MakeNoiseArguments(_noise!, _method),
            _ => RecNoiseTrace.ProcessNodeArguments(_node!, _noise!, _flag, _number, _method),
        };
    }

    // Count observations retain receiver identity, but never ask Harmony to box
    // native arguments or return values that the profile does not read.
    private static void PrefixCount(object __instance, MethodBase __originalMethod, out Frame __state)
        => Begin(__instance, Array.Empty<object?>(), __originalMethod, out __state, null);
    private static void PostfixCount(object __instance, MethodBase __originalMethod, Frame __state, bool __runOriginal)
    {
        object? result = null;
        Complete(__instance, Array.Empty<object?>(), __originalMethod, ref result, __state, __runOriginal, null);
    }

    // Harmony owns one state value per invocation. Observer callbacks cannot pop a game's
    // outer frame, and legitimate game nesting has no fixed 32-frame limit.
    internal struct Frame
    {
        internal RecPatch Patch;
        internal object? Instance;
        internal object?[]? Arguments;
        internal bool Admitted;
        internal bool FirstCall;
        internal long Stamp;
        internal long CallId;
        internal long Installation;
        internal RecAcousticObservation.Scope? Acoustic;
        internal RecStaminaTrace.Scope? Stamina;
        internal RecMineFoamTrace.Scope? MineFoam;
    }

    internal static void Prefix(object __instance, object?[] __args, MethodBase __originalMethod, out Frame __state)
        => Begin(__instance, __args, __originalMethod, out __state, null);

    internal static void Begin(object __instance, object?[] __args, MethodBase __originalMethod, out Frame __state,
        Func<object?[]>? readArguments)
        => Begin(__instance, __args, __originalMethod, out __state, new FuncReader(readArguments));

    internal static void Begin<TReader>(object __instance, object?[] __args, MethodBase __originalMethod, out Frame __state,
        TReader readArguments, RecPatch? bound = null) where TReader : struct, IArgumentReader
    {
#if FORGE_DEVELOPMENT_TIMING
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentTraceBegin);
        using var slow = DevelopmentSlowOperation.Measure("trace.begin", __originalMethod);
#endif
        __state = default;
        if (!RecObservation.TryEnter()) return;
        var cost = default(RecTraceBudget.Scope);
        try
        {
            var patch = bound == null ? RecTracerRuntime.Get(__originalMethod) : RecTracerRuntime.IsActive(bound) ? bound : null;
            if (patch == null) return;
            ref var frame = ref __state;
            frame.Patch = patch;
            frame.Installation = patch.Installation;
            frame.Instance = __instance;
            frame.Stamp = Environment.TickCount64;
            frame.FirstCall = patch.Entry.FirstStack && patch.Calls == 0;
            var admitted = patch.Gate.Admit(frame.Stamp);
            if (admitted && !RecTraceBudget.TryAdmit())
            { System.Threading.Interlocked.Increment(ref patch.BudgetDropped); admitted = false; }
            if (admitted) cost = RecTraceBudget.Measure();
            if (patch.Entry.RecordsPair)
            {
                if (admitted) frame.CallId = patch.BeginPair();
                // A denied invocation masks its parent; children cannot borrow an older call.
                frame.Stamina = RecStaminaTrace.Enter(patch, __instance, __args, frame.CallId, admitted);
                frame.MineFoam = RecMineFoamTrace.Enter(patch, __instance, __args, frame.CallId, admitted);
            }
            if (!admitted) return;
            frame.Admitted = true;
            // Arguments are kept only once the call is admitted: a rate-limited call does not make the boxed
            // array reachable from the postfix it will never write.
            frame.Arguments = __args;
            if (readArguments.Present) frame.Arguments = readArguments.Read();
            if (frame.Stamina != null) frame.Arguments = frame.Stamina.Arguments;
            if (patch.Entry.RecordsPair)
            {
                if (patch.Entry.Acoustic != null)
                    frame.Acoustic = BeginAcoustic(patch, __instance, frame.Arguments, frame.Installation, frame.CallId);
                patch.RecordPhase(__instance, frame.Arguments, null, false, frame.CallId, "before", "entered", acousticScope: frame.Acoustic, staminaScope: frame.Stamina, mineFoamScope: frame.MineFoam);
            }
        }
        catch (Exception error)
        {
            // The prefix runs inside the game's own call: it may record its own failure, and nothing else.
            RecAcousticObservation.Exit(__state.Acoustic);
            RecStaminaTrace.Exit(__state.Stamina);
            RecMineFoamTrace.Exit(__state.MineFoam);
            __state.Patch?.Fail(error);
            __state = default;
            Safe(error);
        }
        finally { cost.Dispose(); RecObservation.Exit(); }
    }

    // Keep the lease callback's closure out of Begin: C# otherwise allocates it even on a refused/count-only call.
    private static RecAcousticObservation.Scope BeginAcoustic(RecPatch patch, object? instance, object?[]? arguments,
        long installation, long callId)
        => RecAcousticObservation.Enter(patch.Entry.Acoustic!, instance, arguments, patch.Id, installation, callId,
            () => patch.Installation == installation && RecTracerRuntime.IsActive(patch));

    internal static void PostfixVoid(object __instance, object?[] __args, MethodBase __originalMethod, Frame __state, bool __runOriginal)
    {
        object? result = null;
        Postfix(__instance, __args, __originalMethod, ref result, __state, __runOriginal);
    }
    private static void Postfix(object __instance, object?[] __args, MethodBase __originalMethod, ref object? __result, Frame __state, bool __runOriginal)
        => Complete(__instance, __args, __originalMethod, ref __result, __state, __runOriginal, null);

    internal static void EndVoid(object instance, MethodBase method, Frame state, bool runOriginal, Func<object?[]> readArguments)
        => EndVoid(instance, method, state, runOriginal, new FuncReader(readArguments));

    private static void EndVoid<TReader>(object instance, MethodBase method, Frame state, bool runOriginal, TReader readArguments)
        where TReader : struct, IArgumentReader
    {
        object? result = null;
        Complete(instance, Array.Empty<object?>(), method, ref result, state, runOriginal, readArguments);
    }

    private static void Complete(object __instance, object?[] __args, MethodBase __originalMethod, ref object? __result,
        Frame __state, bool __runOriginal, Func<object?[]>? readArguments)
        => Complete(__instance, __args, __originalMethod, ref __result, __state, __runOriginal, new FuncReader(readArguments));

    private static void Complete<TReader>(object __instance, object?[] __args, MethodBase __originalMethod, ref object? __result,
        Frame __state, bool __runOriginal, TReader readArguments) where TReader : struct, IArgumentReader
    {
#if FORGE_DEVELOPMENT_TIMING
        using var frameCost = ForgeCore.HookCounters.Measure(ForgeCore.HookSection.DevelopmentTraceComplete);
        using var slow = DevelopmentSlowOperation.Measure("trace.complete", __originalMethod);
#endif
        try { CompleteCore(__instance, __args, __originalMethod, ref __result, __state, __runOriginal, readArguments); }
        finally { RecAcousticObservation.Exit(__state.Acoustic); RecStaminaTrace.Exit(__state.Stamina); RecMineFoamTrace.Exit(__state.MineFoam); }
    }

    private static void CompleteCore<TReader>(object __instance, object?[] __args, MethodBase __originalMethod, ref object? __result,
        Frame __state, bool __runOriginal, TReader readArguments) where TReader : struct, IArgumentReader
    {
        if (__state.Patch == null || !RecObservation.TryEnter()) return;
        var frame = __state;
        var cost = default(RecTraceBudget.Scope);
        try
        {
            var patch = frame.Patch;
            // An invocation belongs to the installation that admitted it. Re-enable must not let an older return
            // read state or satisfy a wait in the new run, including after-only profiles.
            if (!RecTracerRuntime.IsActive(patch) || frame.Installation != patch.Installation) return;
            if (!frame.Admitted)
            {
                patch.ReportOverflow(false);
                return;
            }
            cost = RecTraceBudget.Measure();
            if (!__runOriginal && !patch.Entry.RecordsPair)
            {
                RecSession.Write("tracer", "original_skipped", json =>
                {
                    json.WriteString("profile", patch.Entry.Profile);
                    json.WriteString("type", patch.Entry.Type);
                    json.WriteString("method", __originalMethod.Name);
                    json.WriteString("signature", __originalMethod.ToString());
                    json.WriteNumber("patch", patch.Id);
                    json.WriteString("completion", "original-skipped");
                    json.WriteString("observationScope", patch.ObservationScope);
                    json.WriteString("observationMoment", patch.ObservationMoment);
                    json.WriteBoolean("observationComplete", false);
                });
                patch.ReportOverflow(false);
                return;
            }
            if (readArguments.Present) __args = readArguments.Read();
            if (frame.Stamina != null) __args = frame.Stamina.Arguments;
            if (patch.Entry.RecordsChanges)
            {
                var key = patch.ChangeKey(__instance, __args);
                if (key == null || !patch.Changes.Changed(key)) { patch.ReportOverflow(false); return; }
            }
            // The experiments module waits on "this method was hit"; one branch on a volatile flag costs
            // nothing outside a run and keeps the observation inside the patch that already fired.
            if (__runOriginal && ExperimentTrace.Capturing && __originalMethod is MethodInfo traced)
                ExperimentTrace.Observe(patch.Entry.Type, traced.Name);
            var hasResult = patch.Entry.RecordsResult && __originalMethod is MethodInfo typed && typed.ReturnType != typeof(void);
            if (patch.Entry.RecordsPair)
            {
                patch.RecordPhase(__instance, __args, __result, hasResult && __runOriginal, frame.CallId,
                    "after", __runOriginal ? "returned" : "original-skipped", acousticScope: frame.Acoustic, staminaScope: frame.Stamina, mineFoamScope: frame.MineFoam);
            }
            else patch.Record(__instance, frame.Arguments, __result, hasResult);
            if (frame.FirstCall) WriteStack(patch, __instance);
            patch.ReportOverflow(false);
        }
        catch (Exception error)
        {
            if (frame.Patch != null) frame.Patch.Fail(error);
            Safe(error);
        }
        finally { cost.Dispose(); RecObservation.Exit(); }
    }

    // A void finalizer observes but cannot replace, suppress or rethrow the game's exception.
    // Crashes or missing hooks leave only the before record, which remains incomplete evidence.
    internal static void Finalizer(Exception? __exception, Frame __state)
    {
        try { FinalizeCore(__exception, __state); }
        finally { RecAcousticObservation.Exit(__state.Acoustic); RecStaminaTrace.Exit(__state.Stamina); RecMineFoamTrace.Exit(__state.MineFoam); }
    }

    private static void FinalizeCore(Exception? __exception, Frame __state)
    {
        if (__exception == null || __state.Patch == null || !__state.Admitted || __state.CallId == 0
            || !RecObservation.TryEnter()) return;
        using var cost = RecTraceBudget.Measure();
        try
        {
            if (!RecTracerRuntime.IsActive(__state.Patch)
                || __state.Installation != __state.Patch.Installation) return;
            __state.Patch.RecordPhase(__state.Instance, __state.Arguments, null, false,
                __state.CallId, "exception", "threw", __exception, __state.Acoustic, __state.Stamina, __state.MineFoam);
        }
        catch (Exception error) { Safe(error); }
        finally { RecObservation.Exit(); }
    }

    /// <summary>The managed stack of the first call of a method, which is what a profile asks for when it cannot tell
    /// from the arguments who called it.</summary>
    private static void WriteStack(RecPatch patch, object? instance)
    {
        var stack = new StackTrace(3, false).ToString();
        RecSession.Write("tracer", "first_stack", json =>
        {
            json.WriteString("profile", patch.Entry.Profile);
            json.WriteString("type", patch.Entry.Type);
            json.WriteString("method", patch.Method.Name);
            json.WriteNumber("patch", patch.Id);
            json.WriteString("stack", RecReflect.Truncate(stack, 8192));
        });
    }

    private static void Safe(Exception error)
    {
        try { RecSession.Write("tracer", "patch_error", json => json.WriteString("error", error.GetType().Name + ": " + RecReflect.Truncate(error.Message, 200))); }
        catch (Exception) { /* A recorder that cannot record its own failure must not replace the game's call. */ }
    }
}

// Same-call melee observations live beside the generic invocation bridge so its standalone
// pairing/serialization suites compile the actual observer, without a second hook or writer.
internal static class RecStaminaTrace
{
    private static long _epoch;
    [ThreadStatic] private static Scope? _top;
    private static readonly string[] CostFields = { "baseStaminaCostInCombat", "baseStaminaCostOutOfCombat",
        "resetRestingTimerInCombat", "resetRestingTimerOutOfCombat" };
    internal sealed class Fields
    {
        internal readonly Dictionary<string, object?> Values = new();
        internal readonly Dictionary<string, string> Reads = new();
        internal bool Complete = true;
        private readonly Func<bool>? _valid;
        internal Fields(Func<bool>? valid = null) { _valid = valid; }
        internal void Read(object? root, string key, string path)
        {
            var read = RecCallbackReadback.Read(root, path, _valid ?? (() => false));
            var value = read.Value;
            var finite = !(value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d));
            var scalar = value != null && (value is IntPtr || RecReflect.IsScalar(value));
            var available = read.Available && finite && scalar;
            Reads[key] = available ? "observed" : !finite ? "non-finite" : read.Available ? "scalar-unavailable" : read.Outcome;
            if (available) Values[key] = value is IntPtr pointer ? "0x" + pointer.ToInt64().ToString("x") : value;
            Complete &= available;
        }
        internal object Frozen => new Dictionary<string, object?> { ["values"] = Values, ["reads"] = Reads };
    }
    internal sealed class Scope
    {
        internal RecPatch Patch = null!;
        internal Scope? Parent;
        internal long Epoch, Installation, CallId;
        internal (string Session, long WorldEpoch) Context;
        internal int ThreadId, Closed, CostCalls, GetterCalls;
        internal bool Admitted, ArgumentsComplete, GetterReturned;
        internal bool? ActionState;
        internal long GetterCallId;
        internal string GetterScope = "unavailable";
        internal string? Identity;
        internal Fields Before = new(), Cost = new();
        internal Dictionary<string, object?>? CostCall;
        internal object?[] Arguments = Array.Empty<object?>();
    }
    internal static bool SupportsStatic(MethodInfo method) => method.DeclaringType?.FullName == "DramaManager"
        && method.Name == "get_InActionState" && method.IsStatic && method.ReturnType == typeof(bool)
        && method.GetParameters().Length == 0;
    private static bool IsCost(MethodBase method) => method.Name == "UseStamina";
    private static bool IsOuter(MethodBase method) => method.Name is "UseWeaponLightSwingStamina"
        or "UseWeaponChargedSwingStamina" or "UsePushStamina";
    internal static bool IsTarget(RecTraceEntry entry, MethodBase target)
    {
        if (entry.Profile != "cov4-stamina" || !entry.RecordsPair || target is not MethodInfo method
            || entry.Type != method.DeclaringType?.FullName || !entry.MatchesMethod(method.Name)) return false;
        if (SupportsStatic(method)) return true;
        if (method.IsStatic || method.DeclaringType?.FullName != "PlayerStamina" || method.ReturnType != typeof(void)) return false;
        var p = method.GetParameters();
        if (IsCost(method)) return p.Length == 2 && p[0].ParameterType.FullName == "PlayerStamina+ActionCost"
            && p[1].ParameterType == typeof(float);
        return IsOuter(method) && p.Length == (method.Name == "UseWeaponChargedSwingStamina" ? 2 : 1)
            && p[0].ParameterType.FullName == "Gear.MeleeWeaponFirstPerson"
            && (p.Length == 1 || p[1].ParameterType == typeof(float));
    }
    private static bool Current(Scope? scope) => scope != null && scope.Epoch == Volatile.Read(ref _epoch)
        && scope.ThreadId == Environment.CurrentManagedThreadId && scope.Closed == 0
        && scope.Context == RecSession.ObservationIdentity
        && scope.Patch.Installed && scope.Installation == scope.Patch.Installation
        && ReferenceEquals(RecTracerRuntime.Get(scope.Patch.Method), scope.Patch);
    private static Fields Snapshot(Scope scope, object? instance)
    {
        var fields = new Fields(() => Current(scope) && ReferenceEquals(_top, scope) && scope.Admitted);
        fields.Read(instance, "pointer", "Pointer");
        fields.Read(instance, "stamina", "m_currentStamina");
        fields.Read(instance, "ownerPointer", "m_owner.Pointer");
        fields.Read(instance, "ownerSlot", "m_owner.PlayerSlotIndex");
        fields.Read(instance, "playerDataID", "m_owner.PlayerData.persistentID");
        fields.Read(instance, "affectedByDrama", "m_owner.PlayerData.StaminaUsageAffectedByDrama");
        fields.Read(instance, "minimumOutOfCombat", "m_owner.PlayerData.StaminaMinimumCapWhenNotInCombat");
        return fields;
    }
    private static string? Identity(Fields fields) => fields.Values.TryGetValue("pointer", out var value)
        && value is string pointer && pointer != "0x0" ? pointer : null;
    private static bool SameIdentity(Fields before, Fields after)
    {
        foreach (var key in new[] { "pointer", "ownerPointer", "ownerSlot", "playerDataID" })
            if (!before.Values.TryGetValue(key, out var a) || !after.Values.TryGetValue(key, out var b) || !Equals(a, b)) return false;
        return true;
    }
    internal static Scope? Enter(RecPatch patch, object? instance, object?[]? args, long callId, bool admitted)
    {
        if (!IsTarget(patch.Entry, patch.Method)) return null;
        var parent = Current(_top) ? _top : null;
        var scope = new Scope
        {
            Patch = patch,
            Installation = patch.Installation,
            Epoch = Volatile.Read(ref _epoch),
            ThreadId = Environment.CurrentManagedThreadId,
            Context = RecSession.ObservationIdentity,
            CallId = callId,
            Admitted = admitted,
            Parent = parent
        };
        _top = scope;
        var getter = SupportsStatic((MethodInfo)patch.Method);
        if (getter)
        {
            if (parent != null && IsCost(parent.Patch.Method)) parent.GetterCalls++;
            else scope.Parent = null;
            scope.ArgumentsComplete = args is { Length: 0 };
            return scope;
        }
        if (parent != null && IsCost(patch.Method) && IsOuter(parent.Patch.Method)) parent.CostCalls++;
        if (!admitted) return scope;
        scope.Before = Snapshot(scope, instance); scope.Identity = Identity(scope.Before);
        if (parent == null || scope.Identity == null || scope.Identity != parent.Identity || !SameIdentity(scope.Before, parent.Before)) scope.Parent = null;
        if (IsCost(patch.Method))
        {
            scope.Cost = new Fields(() => Current(scope) && ReferenceEquals(_top, scope) && scope.Admitted);
            foreach (var field in CostFields) scope.Cost.Read(args is { Length: 2 } ? args[0] : null, field, field);
            scope.ArgumentsComplete = scope.Cost.Complete && args is { Length: 2 } && args[1] is float dt && float.IsFinite(dt);
            scope.Arguments = new object?[] { scope.Cost.Frozen, args is { Length: 2 } ? args[1] : null };
        }
        else
        {
            var weapon = new Fields(() => Current(scope) && ReferenceEquals(_top, scope) && scope.Admitted);
            weapon.Read(args is { Length: > 0 } ? args[0] : null, "pointer", "Pointer");
            weapon.Read(args is { Length: > 0 } ? args[0] : null, "syncID", "SyncID");
            scope.ArgumentsComplete = weapon.Complete && Identity(weapon) != null
                && args?.Length == patch.Method.GetParameters().Length;
            if (patch.Method.Name == "UseWeaponChargedSwingStamina")
            {
                scope.ArgumentsComplete &= args is { Length: 2 } && args[1] is float charge && float.IsFinite(charge);
                scope.Arguments = new object?[] { weapon.Frozen, args is { Length: 2 } ? args[1] : null };
            }
            else scope.Arguments = new[] { weapon.Frozen };
        }
        return scope;
    }
    internal static Dictionary<string, object?>? Capture(Scope? scope, object? instance,
        object? result, bool hasResult, string phase, string completion)
    {
        if (scope == null) return null;
        var live = Current(scope) && ReferenceEquals(_top, scope) && scope.Admitted;
        var returned = live && phase == "after" && completion == "returned";
        var getter = SupportsStatic((MethodInfo)scope.Patch.Method);
        var sample = getter ? new Fields() : phase == "before" ? scope.Before : Snapshot(scope, instance);
        var complete = live && completion is "entered" or "returned" && scope.ArgumentsComplete
            && (getter || sample.Complete && Identity(sample) == scope.Identity && scope.Identity != null
                && SameIdentity(scope.Before, sample));
        var output = new Dictionary<string, object?>
        {
            ["schema"] = "forge.melee-stamina-call.v1",
            ["threadId"] = scope.ThreadId,
            ["parentCallId"] = Current(scope.Parent) && scope.Parent!.Admitted ? scope.Parent.CallId : 0,
            ["staminaIdentity"] = scope.Identity,
            ["sourceWorldEpoch"] = scope.Context.WorldEpoch,
            ["snapshot"] = sample.Frozen
        };
        if (getter)
        {
            complete &= phase == "before" || returned && hasResult && result is bool;
            if (phase != "before" && Current(scope.Parent) && IsCost(scope.Parent!.Patch.Method))
            {
                scope.Parent.GetterReturned = returned && hasResult && result is bool;
                scope.Parent.ActionState = result is bool action ? action : null;
                scope.Parent.GetterCallId = scope.CallId;
                scope.Parent.GetterScope = scope.Patch.ObservationScope;
            }
        }
        else if (IsCost(scope.Patch.Method) && phase != "before")
        {
            var branch = Branch(scope, sample, returned);
            output["branch"] = branch;
            complete &= branch["status"] is "observed";
            if (Current(scope.Parent) && IsOuter(scope.Parent!.Patch.Method))
                scope.Parent.CostCall = new Dictionary<string, object?>
                {
                    ["callId"] = scope.CallId,
                    ["before"] = scope.Before.Frozen,
                    ["after"] = sample.Frozen,
                    ["arguments"] = scope.Arguments,
                    ["branch"] = branch,
                    ["observationComplete"] = complete
                };
        }
        else if (!getter && phase != "before")
        {
            output["costCalls"] = scope.CostCalls;
            output["costCall"] = scope.CostCall;
            complete &= returned && scope.CostCalls == 1 && scope.CostCall != null
                && scope.CostCall["observationComplete"] is true;
        }
        output["observationComplete"] = complete;
        return output;
    }
    private static Dictionary<string, object?> Branch(Scope scope, Fields after, bool returned)
    {
        var branch = new Dictionary<string, object?>
        {
            ["status"] = "selection-unavailable",
            ["actionStateReads"] = scope.GetterCalls,
            ["actionStateCallId"] = scope.GetterCallId,
            ["actionStateScope"] = scope.GetterScope,
            // Reviewed native selector: build 20403457, UseStamina RVA 0x12CD950. The getter
            // result below is its actual nested return, never an independently sampled Drama state.
            ["selectionRule"] = "out-of-combat iff affectedByDrama && !InActionState; native-build-20403457"
        };
        if (!returned || !SameIdentity(scope.Before, after) || !scope.Before.Values.TryGetValue("affectedByDrama", out var before) || before is not bool affected
            || !after.Values.TryGetValue("affectedByDrama", out var now) || !Equals(before, now)) return branch;
        bool outOfCombat;
        if (!affected)
        {
            if (scope.GetterCalls != 0) return branch;
            outOfCombat = false;
            branch["selectionEvidence"] = "drama-disabled-at-call-boundaries";
        }
        else
        {
            if (scope.GetterCalls != 1 || !scope.GetterReturned || !scope.ActionState.HasValue) return branch;
            outOfCombat = !scope.ActionState.Value;
            branch["inActionState"] = scope.ActionState.Value;
            branch["selectionEvidence"] = "same-call-getter-return";
        }
        var cost = outOfCombat ? "baseStaminaCostOutOfCombat" : "baseStaminaCostInCombat";
        var reset = outOfCombat ? "resetRestingTimerOutOfCombat" : "resetRestingTimerInCombat";
        if (!scope.Cost.Complete || !scope.Cost.Values.ContainsKey(cost) || !scope.Cost.Values.ContainsKey(reset)) return branch;
        branch["status"] = "observed";
        branch["selectedBranch"] = outOfCombat ? "out-of-combat" : "in-combat";
        branch["selectedBaseCost"] = scope.Cost.Values[cost];
        branch["selectedResetRestingTimer"] = scope.Cost.Values[reset];
        return branch;
    }
    internal static void Exit(Scope? scope)
    {
        if (scope == null || Interlocked.Exchange(ref scope.Closed, 1) != 0) return;
        if (ReferenceEquals(_top, scope)) _top = scope.Parent;
    }
    internal static void Reset() { Interlocked.Increment(ref _epoch); _top = null; }
}

// Fixed paths in the two reviewed Y4 observers may copy live callback values. This is
// deliberately separate from the generic probe: no config path, native-class fallback,
// retained object lease or global relaxation of RecObservation.NativeReadUnavailable.
internal static class RecCallbackReadback
{
    internal static VanillaValueReading.ReadResult Read(object? root, string path, Func<bool> valid)
    {
        if (!RecObservation.InHook || !valid()) return new("callback-scope-unavailable", null);
        object? current = root;
        try
        {
            foreach (var name in RecReflect.PathSteps(path))
            {
                if (!valid()) return new("callback-scope-changed", null);
                if (current == null) return new("null-before-leaf", null);
                var getter = RecReflect.ResolveManagedMemberCached(current as Type ?? current.GetType(), name, current is Type);
                if (getter == null) return new("missing-member", null, Error: path);
                current = getter(current);
            }
            return valid() ? new(current == null ? "explicit-null" : "value", current)
                : new("callback-scope-changed", null);
        }
        catch (Exception error) { return new("read-error", null, Error: error.GetType().Name); }
    }
}


// Append outside RecTracerPatches, in its existing namespace. No game type references.
internal static class RecMineFoamTrace
{
    internal const string Profile = "cov4-mine-foam";
    private const string Iterator = "MineDeployerInstance_Detonate_Glue+_ExplosionSequence_d__25";
    private const int BurstBudget = 256, SyncBudget = 8192;
    private static readonly object Gate = new();
    private static readonly System.Collections.Generic.Dictionary<(long, long), Burst> Bursts = new();
    private static readonly System.Collections.Generic.Dictionary<uint, Link> Links = new();
    [ThreadStatic] private static Scope? _top;
    private static long _epoch, _nextBurst;
    private static bool _budgetLost;
    private static string _session = "";
    private static long _worldEpoch;
    internal sealed class Burst
    {
        internal long Id, Epoch, WorldEpoch, IteratorPointer, MinePointer, RootCall;
        internal string Session = "";
        internal int Thread, Generated, Requests;
        internal bool Finished;
        internal readonly System.Collections.Generic.HashSet<string> Issues = new();
        internal readonly System.Collections.Generic.List<(RecPatch Patch, long Installation)> Hooks = new();
    }
    internal sealed class Link
    {
        internal Burst Burst = null!;
        internal long RequestCall, SyncCall, Pointer, Manager;
        internal bool Ambiguous, Observed;
    }
    internal sealed class Scope
    {
        internal RecPatch Patch = null!;
        internal long Epoch, WorldEpoch, Installation, Call, ParentCall;
        internal string Session = "";
        internal int Thread, SyncResults;
        internal string Kind = "", Reason = "";
        internal Scope? Parent;
        internal Burst? Burst;
        internal Link? Link;
        internal uint? Sync;
        internal bool? MergeToNothing;
        internal Registration? Before;
        internal bool Blocked, Closed, Completed;
    }
    internal sealed record Registration(string State, long Pointer = 0, uint? SyncID = null,
        object? Radius = null, object? Expansion = null, object? Landed = null)
    {
        internal bool Available => State is "absent" or "present";
        internal object Scalars() => new System.Collections.Generic.Dictionary<string, object?>
        {
            ["state"] = State,
            ["pointer"] = Pointer == 0 ? null : Pointer.ToString("X"),
            ["syncID"] = SyncID,
            ["radius"] = Radius,
            ["expansion"] = Expansion,
            ["landed"] = Landed
        };
    }
    private static bool Shape(MethodInfo m, string owner, string name, bool stat, string returns, params string[] parameters)
    {
        if (m.DeclaringType?.FullName != owner || m.DeclaringType.Assembly.GetName().Name != "Modules-ASM" || m.Name != name
            || m.IsStatic != stat || m.IsGenericMethod || m.ContainsGenericParameters || m.ReturnType.FullName != returns) return false;
        var p = m.GetParameters(); if (p.Length != parameters.Length) return false;
        for (var i = 0; i < p.Length; i++) if (p[i].IsOut || p[i].ParameterType.IsByRef || p[i].ParameterType.FullName != parameters[i]) return false;
        return true;
    }
    private static string Kind(MethodBase target)
    {
        if (target is not MethodInfo m) return "";
        if (Shape(m, Iterator, "MoveNext", false, "System.Boolean")) return "burst";
        if (Shape(m, "ProjectileManager", "WantToFireGlue", true, "System.Void", "Player.PlayerAgent", "UnityEngine.Vector3", "UnityEngine.Vector3", "System.Single", "System.Boolean")) return "request";
        if (Shape(m, "ProjectileManager", "GetNextSyncID", true, "System.UInt32")) return "sync";
        if (Shape(m, "ProjectileManager", "SpawnGlueGunProjectileIfNeeded", false, "GlueGunProjectile", "System.UInt32", "UnityEngine.Vector3", "System.Boolean")) return "spawn";
        if (Shape(m, "ProjectileManager", "DoFireGlue", false, "System.Void", "ProjectileManager+pFireGlue")) return "fire";
        if (Shape(m, "ProjectileManager", "DoMergeGlue", false, "System.Void", "ProjectileManager+pMergeGlue")) return "merge";
        if (Shape(m, "ProjectileManager", "DoDestroyGlue", false, "System.Void", "ProjectileManager+pDestroyGlue")) return "destroy";
        return "";
    }
    internal static bool IsTarget(RecTraceEntry entry, MethodBase method) => entry.Profile == Profile && entry.RecordsPair && entry.Type == method.DeclaringType?.FullName && entry.MatchesMethod(method.Name) && Kind(method).Length != 0;
    internal static bool SupportsStatic(MethodInfo method) => method.IsStatic && (Kind(method) is "request" or "sync");
    internal static object?[] Arguments(MethodBase method, object?[]? args)
    {
        if (args == null) return new object?[] { Unknown("arguments-unavailable") };
        var kind = Kind(method);
        if (kind is "fire" or "merge" or "destroy")
        {
            var names = kind == "merge" ? new[] { "mergeTargetSyncID", "inVolume", "inExpandVolume", "mergeToNothing" }
                : kind == "fire" ? new[] { "syncID", "expandVolume", "glueStrengthMultiplier", "allowSplat" } : new[] { "syncID" };
            var copy = new System.Collections.Generic.Dictionary<string, object?>();
            foreach (var name in names) copy[name] = Scalar(args.Length > 0 ? args[0] : null, name);
            return new object?[] { copy };
        }
        var values = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (kind == "request" && i == 0) values[i] = new System.Collections.Generic.Dictionary<string, object?> { ["pointer"] = Pointer(value).ToString("X"), ["lifetime"] = "unverified" };
            else if (kind == "request" && (i == 1 || i == 2) || kind == "spawn" && i == 1)
                values[i] = new System.Collections.Generic.Dictionary<string, object?> { ["x"] = Scalar(value, "x"), ["y"] = Scalar(value, "y"), ["z"] = Scalar(value, "z") };
            else values[i] = value is uint or float or bool or int ? value : Unknown("scalar-unavailable");
        }
        return values;
    }
    internal static Scope? Enter(RecPatch patch, object? instance, object?[]? args, long callId, bool admitted)
    {
        if (!IsTarget(patch.Entry, patch.Method)) return null;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            var identity = RecSession.ObservationIdentity;
            if (_session != identity.Session || _worldEpoch != identity.WorldEpoch)
            { Reset(); _session = identity.Session; _worldEpoch = identity.WorldEpoch; }
            var parent = _top;
            var s = new Scope
            {
                Patch = patch,
                Epoch = _epoch,
                Session = identity.Session,
                WorldEpoch = identity.WorldEpoch,
                Installation = patch.Installation,
                Call = callId,
                Thread = Environment.CurrentManagedThreadId,
                Parent = parent,
                ParentCall = parent?.Call ?? 0,
                Kind = Kind(patch.Method)
            };
            _top = s;
            s.Blocked = !admitted || parent is { Blocked: true } || parent is { Closed: true } || parent != null && parent.Epoch != _epoch;
            if (s.Blocked)
            {
                s.Reason = admitted ? "blocked-parent" : "rate-denied";
                // A denied burst may be detached from its earlier yielded frame. Conservatively invalidate
                // retained evidence rather than treating an unobserved slice as zero native output.
                foreach (var b in Bursts.Values) b.Issues.Add(s.Reason);
                return s;
            }
            if (s.Kind == "burst") BeginBurst(s, instance);
            else if (s.Kind == "request")
            {
                if (parent?.Kind == "burst" && parent.Burst != null && !parent.Burst.Finished) { s.Burst = parent.Burst; s.Burst.Requests++; }
                else s.Reason = "burst-parent-unavailable";
            }
            else if (s.Kind == "sync")
            {
                if (parent?.Kind == "request") s.Burst = parent.Burst; else s.Reason = "request-parent-unavailable";
            }
            else
            {
                s.Sync = SyncArgument(s.Kind, args);
                if (s.Kind == "merge" && args is { Length: > 0 } && Member(args[0], "mergeToNothing") is bool remove) s.MergeToNothing = remove;
                if (s.Sync.HasValue && Links.TryGetValue(s.Sync.Value, out var link))
                {
                    s.Link = link; s.Burst = link.Burst;
                    if (link.Ambiguous) s.Reason = "sync-id-ambiguous";
                    else if (link.Burst.Thread != s.Thread) s.Reason = "thread-mismatch";
                    else if (s.Kind == "spawn" && parent?.Kind == "fire" && parent.Sync != s.Sync) s.Reason = "fire-sync-mismatch";
                    else if (parent?.Burst != null && !ReferenceEquals(parent.Burst, link.Burst)) s.Reason = "parent-burst-mismatch";
                    if (s.Reason.Length != 0) link.Burst.Issues.Add(s.Reason);
                }
                else s.Reason = "sync-id-unassociated";
                if (s.Sync.HasValue) s.Before = ReadRegistration(instance, s.Sync.Value);
            }
            if (_budgetLost) s.Burst?.Issues.Add("observation-budget-exceeded");
            return s;
        }
    }
    private static void BeginBurst(Scope s, object? instance)
    {
        var iterator = Pointer(instance); var mine = Pointer(Member(instance, "__4__this")); var state = Member(instance, "__1__state");
        if (iterator == 0 || mine == 0 || state is not int initial) { s.Reason = "burst-identity-unavailable"; return; }
        var key = (iterator, mine);
        if (!Bursts.TryGetValue(key, out var b))
        {
            if (Bursts.Count >= BurstBudget) { LoseBudget(); s.Reason = "burst-budget-exceeded"; return; }
            b = new Burst { Id = ++_nextBurst, Epoch = _epoch, Session = s.Session, WorldEpoch = s.WorldEpoch, IteratorPointer = iterator, MinePointer = mine, RootCall = s.Call, Thread = s.Thread };
            Bursts.Add(key, b); if (initial != 0) b.Issues.Add("initial-move-next-unobserved");
            var assembly = s.Patch.Method.DeclaringType!.Assembly;
            foreach (var owner in new[] { Iterator, "ProjectileManager" })
            {
                var type = assembly.GetType(owner);
                if (type == null) { b.Issues.Add("hook-type-unavailable"); continue; }
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (Kind(m).Length == 0) continue;
                    var patch = RecTracerRuntime.Get(m);
                    if (patch == null || !patch.Installed || !IsTarget(patch.Entry, m)) b.Issues.Add("hook-not-installed:" + m.Name);
                    else b.Hooks.Add((patch, patch.Installation));
                }
            }
            if (b.Hooks.Count != 7) b.Issues.Add("required-hook-set-incomplete");
        }
        else if (b.Finished) b.Issues.Add("closed-iterator-reused");
        if (b.Thread != s.Thread) b.Issues.Add("thread-mismatch");
        for (var parent = s.Parent; parent != null; parent = parent.Parent)
            if (!parent.Closed && parent.Kind == "burst" && ReferenceEquals(parent.Burst, b)) b.Issues.Add("iterator-reentrant");
        s.Burst = b;
    }
    internal static System.Collections.Generic.Dictionary<string, object?>? Capture(Scope? s, object? instance, object?[]? args,
        object? result, bool hasResult, string phase, string completion)
    {
        if (s == null) return null;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            var valid = Validate(s); var b = s.Burst;
            if (phase != "before")
            {
                if (s.Completed) { s.Reason = "duplicate-completion"; b?.Issues.Add(s.Reason); valid = false; }
                s.Completed = true;
                if (completion != "returned") { s.Reason = completion; b?.Issues.Add(completion); valid = false; }
                if (valid && s.Kind == "sync") RegisterSync(s, result, hasResult);
                if (valid && s.Kind == "request" && s.SyncResults != 1) b?.Issues.Add("request-sync-result-not-unique");
                if (valid && s.Kind == "burst")
                { if (hasResult && result is bool more) b!.Finished = !more; else b?.Issues.Add("move-next-result-unavailable"); }
            }
            Registration? after = null; var generated = false;
            if (valid && phase == "after" && s.Sync.HasValue && (s.Kind is "spawn" or "merge" or "destroy" or "fire"))
            {
                if (SyncArgument(s.Kind, args) != s.Sync) { s.Reason = "argument-sync-changed"; b?.Issues.Add(s.Reason); valid = false; }
                after = ReadRegistration(instance, s.Sync.Value);
                if (!after.Available || s.Before is not { Available: true }) b?.Issues.Add("registration-read-unavailable");
                if (s.Kind == "spawn" && s.Link != null)
                {
                    var ptr = Pointer(result); var sync = Member(result, "SyncID"); var manager = Pointer(instance);
                    if (!hasResult || ptr == 0 || sync is not uint id || id != s.Sync || manager == 0) b?.Issues.Add("spawn-return-unverified");
                    else if (after.State != "present" || after.Pointer != ptr || after.SyncID != id) b?.Issues.Add("spawn-registration-mismatch");
                    else if (s.Link.Manager != 0 && s.Link.Manager != manager) b?.Issues.Add("manager-identity-changed");
                    else if (s.Link.Observed && s.Link.Pointer != ptr) b?.Issues.Add("sync-pointer-reused");
                    else if (valid && s.Before is { Available: true } && !s.Link.Ambiguous)
                    {
                        if (!s.Link.Observed && s.Before.State == "absent") { b!.Generated++; generated = true; }
                        s.Link.Observed = true; s.Link.Pointer = ptr; s.Link.Manager = manager;
                    }
                }
                if ((s.Kind is "merge" or "destroy") && s.Link != null)
                {
                    if (!s.Link.Observed || s.Before?.State != "present" || s.Before.Pointer != s.Link.Pointer || s.Link.Manager != Pointer(instance)) b?.Issues.Add("lifecycle-identity-unverified");
                    if (s.Kind == "merge" && after.State == "present" && (after.Pointer != s.Link.Pointer || after.Radius is not float || after.Expansion is not float || after.Landed is not bool)) b?.Issues.Add("merge-post-state-unavailable");
                    if (s.Kind == "merge")
                    {
                        var toNothing = args is { Length: > 0 } ? Member(args[0], "mergeToNothing") : null;
                        if (!s.MergeToNothing.HasValue || toNothing is not bool remove) b?.Issues.Add("merge-payload-unavailable");
                        else if (remove != s.MergeToNothing.Value) b?.Issues.Add("merge-payload-changed");
                        else if (remove && after.State != "absent") b?.Issues.Add("merge-to-nothing-not-removed");
                        else if (!remove && after.State != "present") b?.Issues.Add("merge-target-registration-lost");
                    }
                    if (s.Kind == "destroy" && after.State != "absent") b?.Issues.Add("destroy-registration-still-present");
                }
            }
            valid = Validate(s) && valid;
            var pending = Pending(b);
            var complete = valid && (completion is "entered" or "returned") && s.Reason.Length == 0 && b != null && b.Issues.Count == 0;
            var settled = complete && b!.Finished && pending == 0;
            return new System.Collections.Generic.Dictionary<string, object?>
            {
                ["reader"] = "native-mine-foam-same-burst",
                ["epoch"] = s.Epoch,
                ["session"] = s.Session,
                ["worldEpoch"] = s.WorldEpoch,
                ["callId"] = s.Call,
                ["parentCallId"] = s.ParentCall,
                ["threadId"] = s.Thread,
                ["patchInstallation"] = s.Installation,
                ["kind"] = s.Kind,
                ["burstId"] = b?.Id,
                ["burstRootCallId"] = b?.RootCall,
                ["iteratorPointer"] = b?.IteratorPointer.ToString("X"),
                ["minePointer"] = b?.MinePointer.ToString("X"),
                ["syncID"] = s.Sync,
                ["requestCallId"] = s.Link?.RequestCall,
                ["syncCallId"] = s.Link?.SyncCall,
                ["registrationBefore"] = s.Before?.Scalars(),
                ["registrationAfter"] = after?.Scalars(),
                ["newRegistration"] = generated,
                ["observedNewRegistrations"] = b?.Generated,
                ["pendingSyncIDs"] = pending,
                ["sequenceFinished"] = b?.Finished,
                ["bubbleCount"] = settled ? b!.Generated : null,
                ["burstComplete"] = settled,
                ["observationComplete"] = complete,
                ["reason"] = s.Reason.Length != 0 ? s.Reason : b == null ? "burst-unassociated" : "",
                ["incompleteReasons"] = b == null ? System.Array.Empty<string>() : new System.Collections.Generic.List<string>(b.Issues).ToArray(),
                ["scope"] = "installed-native-callbacks-only; unobserved-lifecycle-remains-uncovered"
            };
        }
    }
    private static bool Validate(Scope s)
    {
        var identity = RecSession.ObservationIdentity;
        if (s.Session != identity.Session || s.WorldEpoch != identity.WorldEpoch) s.Reason = "session-or-world-changed";
        else if (s.Epoch != _epoch) s.Reason = "session-reset";
        else if (s.Closed) s.Reason = "scope-closed";
        else if (s.Thread != Environment.CurrentManagedThreadId) s.Reason = "thread-mismatch";
        else if (!ReferenceEquals(_top, s)) s.Reason = "scope-out-of-order";
        else if (!Current(s.Patch, s.Installation)) s.Reason = "patch-installation-changed";
        if (s.Burst is { } b)
        { foreach (var hook in b.Hooks) if (!Current(hook.Patch, hook.Installation)) b.Issues.Add("required-hook-installation-changed"); if (s.Reason.Length != 0) b.Issues.Add(s.Reason); }
        return !s.Blocked && s.Reason.Length == 0 && s.Epoch == _epoch;
    }
    private static bool Current(RecPatch patch, long installation) => patch.Installed && patch.Installation == installation && ReferenceEquals(RecTracerRuntime.Get(patch.Method), patch);
    private static void RegisterSync(Scope s, object? result, bool hasResult)
    {
        if (s.Burst == null || !hasResult || result is not uint id) { s.Reason = "sync-result-unavailable"; s.Burst?.Issues.Add(s.Reason); return; }
        s.Parent!.SyncResults++; s.Sync = id;
        if (Links.TryGetValue(id, out var previous))
        { previous.Ambiguous = true; previous.Burst.Issues.Add("duplicate-sync-id"); s.Burst.Issues.Add("duplicate-sync-id"); s.Reason = "duplicate-sync-id"; return; }
        if (Links.Count >= SyncBudget) { LoseBudget(); s.Reason = "sync-budget-exceeded"; return; }
        var link = new Link { Burst = s.Burst, RequestCall = s.Parent.Call, SyncCall = s.Call }; Links.Add(id, link); s.Link = link;
    }
    private static int? Pending(Burst? b)
    { if (b == null) return null; var n = 0; foreach (var link in Links.Values) if (ReferenceEquals(link.Burst, b) && !link.Observed) n++; return n; }
    private static void LoseBudget() { _budgetLost = true; foreach (var b in Bursts.Values) b.Issues.Add("observation-budget-exceeded"); }
    internal static void Exit(Scope? s)
    {
        if (s == null) return;
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            if (s.Closed) return;
            if (!s.Completed && !s.Blocked) s.Burst?.Issues.Add("completion-unobserved"); s.Closed = true;
            if (ReferenceEquals(_top, s)) { _top = s.Parent; while (_top != null && (_top.Closed || _top.Epoch != _epoch)) _top = _top.Parent; }
            else s.Burst?.Issues.Add("scope-out-of-order");
        }
    }
    internal static void Reset()
    {
        using (RecGate.Enter(Gate, "recorder.lock-timeout"))
        {
            foreach (var b in Bursts.Values)
                if (!b.Finished || Pending(b) != 0 || b.Issues.Count != 0)
                    RecSession.Write("tracer", "mine_foam_incomplete", json =>
                    {
                        json.WriteNumber("burstId", b.Id); json.WriteNumber("epoch", b.Epoch); json.WriteString("session", b.Session); json.WriteNumber("worldEpoch", b.WorldEpoch); json.WriteString("reason", "session-or-profile-reset");
                        json.WriteBoolean("observationComplete", false); json.WriteNumber("observedNewRegistrations", b.Generated); json.WriteNumber("pendingSyncIDs", Pending(b) ?? 0);
                        json.WriteStartArray("incompleteReasons"); foreach (var issue in b.Issues) json.WriteStringValue(issue); json.WriteEndArray();
                    });
            _epoch++; Bursts.Clear(); Links.Clear(); _top = null; _budgetLost = false;
        }
    }
    private static uint? SyncArgument(string kind, object?[]? args)
    { if (args == null || args.Length == 0) return null; var value = kind == "spawn" ? args[0] : Member(args[0], kind == "merge" ? "mergeTargetSyncID" : "syncID"); return value is uint id ? id : null; }
    private static bool CanRead()
    {
        var s = _top;
        var identity = RecSession.ObservationIdentity;
        return RecObservation.InHook && s != null && s.Session == identity.Session && s.WorldEpoch == identity.WorldEpoch && !s.Blocked && !s.Closed && s.Epoch == _epoch
            && s.Thread == Environment.CurrentManagedThreadId && IsTarget(s.Patch.Entry, s.Patch.Method) && Current(s.Patch, s.Installation);
    }
    internal static object? Result(MethodBase method, object? value)
        => Kind(method) == "spawn" ? new System.Collections.Generic.Dictionary<string, object?>
        {
            ["pointer"] = Pointer(value) is var pointer && pointer != 0 ? pointer.ToString("X") : null,
            ["syncID"] = Scalar(value, "SyncID")
        } : value;
    private static object? Member(object? value, string path)
    { if (value == null) return null; var read = RecCallbackReadback.Read(value, path, CanRead); return read.Available ? read.Value : null; }
    private static object Unknown(string reason) => new System.Collections.Generic.Dictionary<string, object?> { ["$nativeRead"] = reason };
    private static object? Scalar(object? value, string path)
    { var read = value == null ? new VanillaValueReading.ReadResult("argument-unavailable", null) : RecCallbackReadback.Read(value, path, CanRead); return read.Available && read.Value is uint or int or float or bool ? read.Value : Unknown(read.Available ? "scalar-unavailable" : read.Outcome); }
    private static long Pointer(object? value)
    { try { return CanRead() && value != null && RecReflect.Probe.TryInstanceId(value, out var ptr) ? ptr : 0; } catch { return 0; } }
    private static Registration ReadRegistration(object? manager, uint id)
    {
        try
        {
            var dictionary = Member(manager, "m_glueGunProjectiles");
            if (dictionary == null || !CanRead()) return new("dictionary-unavailable");
            MethodInfo? lookup = null;
            foreach (var m in dictionary.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                var p = m.GetParameters();
                if (m.Name == "TryGetValue" && !m.IsGenericMethod && m.ReturnType == typeof(bool) && p.Length == 2 && p[0].ParameterType == typeof(uint)
                    && p[1].ParameterType.IsByRef && p[1].ParameterType.GetElementType()?.FullName == "GlueGunProjectile") { lookup = m; break; }
            }
            if (lookup == null) return new("lookup-unavailable");
            object?[] values = { id, null }; var observed = lookup.Invoke(dictionary, values);
            if (!CanRead()) return new("callback-scope-changed");
            if (observed is not bool present) return new("lookup-result-unavailable");
            if (!present) return new("absent");
            var projectile = values[1]; var ptr = Pointer(projectile); var sync = Member(projectile, "SyncID");
            if (ptr == 0 || sync is not uint syncID) return new("registered-object-unavailable");
            return new("present", ptr, syncID, Member(projectile, "m_radius"), Member(projectile, "m_expandPotential"), Member(projectile, "m_landed"));
        }
        catch (Exception error) { return new("lookup-error:" + error.GetType().Name); }
    }
}
